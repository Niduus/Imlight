/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Imlight.RemoteAdmin;

namespace Imlight.Setup;

/// <summary>A server Ahziael can manage from this PC (Server > Remote). The password is never saved.</summary>
internal sealed record RemoteServer(string Name, string Host, int Port, string Username, string? Fingerprint) {

    internal string Label => Name.Length > 0 ? Name : Host;

}

/// <summary>The remote servers this PC knows, in remote-servers.json in the install folder.</summary>
internal static class RemoteServers {

    private const string FILE = "remote-servers.json";

    internal static List<RemoteServer> Load(string installFolder) {
        try {
            var path = Path.Combine(installFolder, FILE);

            return File.Exists(path) ? JsonSerializer.Deserialize<List<RemoteServer>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            return [];
        }
    }

    internal static void Save(string installFolder, RemoteServer server) {
        var all = Load(installFolder);
        all.RemoveAll(s => SameServer(s, server));
        all.Insert(0, server);
        Write(installFolder, all);
    }

    internal static void Remove(string installFolder, RemoteServer server) {
        var all = Load(installFolder);
        all.RemoveAll(s => SameServer(s, server));
        Write(installFolder, all);
    }

    internal static bool SameServer(RemoteServer a, RemoteServer b)
        => a.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    private static void Write(string installFolder, List<RemoteServer> all)
        => File.WriteAllText(Path.Combine(installFolder, FILE), JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));

}

/// <summary>
/// Ahziael managing an Imlight server on another PC (Niduus, 2026-10-30), through that server's Remote Admin
/// (RemoteAdminServer). The server's data files are kept in step with a copy on this PC, so every page that reads and
/// writes the server's files works unchanged on that copy: what a page saves goes to the server within a couple of
/// seconds, and what the server writes comes back. Commands and announcements the pages write go to the server as
/// requests of their own, so commands run for this admin's own wizards there. While a session is active (Current),
/// InstalledCommands.ServerFolder points at the copy.
/// </summary>
internal sealed class RemoteSession : IDisposable {

    /// <summary>What the pages say about something only the host's PC can do.</summary>
    internal const string LOCAL_ONLY = "That only works on the PC the server runs on (local only). Click \"Back to my PC\" at the top to do it here.";

    private static readonly TimeSpan s_every = TimeSpan.FromSeconds(1.5);

    internal static RemoteSession? Current { get; private set; }

    internal RemoteServer Server { get; private set; }
    internal string Account { get; private set; } = "";
    internal string ServerName { get; private set; } = "";
    internal string Label => ServerName.Length > 0 ? ServerName : Server.Label;

    /// <summary>The copy of the server folder on this PC.</summary>
    internal string MirrorFolder { get; }

    internal bool Connected { get; private set; }
    internal int Players { get; private set; }

    /// <summary>A problem to show, such as a lost connection or a command no wizard ran; cleared when read.</summary>
    private readonly Queue<string> _notices = new();

    private readonly string _installFolder;
    private readonly string _password;
    private readonly string _statePath;
    private readonly Dictionary<string, string> _roots;
    private readonly Dictionary<string, SyncState> _state;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private TcpClient? _tcp;
    private SslStream? _tls;
    private string _epoch = "";
    private long _seq;
    private int _requestId;
    private readonly HashSet<string> _refetch = [];
    private bool _dirty;

    private sealed class SyncState {

        public string Hash { get; set; } = "";
        public long Size { get; set; }
        public long Ticks { get; set; }
        // How far into a log file the copy goes.
        public long End { get; set; }

    }

    private RemoteSession(string installFolder, RemoteServer server, string password) {
        _installFolder = installFolder;
        Server = server;
        _password = password;
        var folder = Path.Combine(installFolder, "Remote servers", SafeName($"{server.Host}-{server.Port}"));
        MirrorFolder = Path.Combine(folder, "Server");
        _statePath = Path.Combine(folder, "sync-state.json");
        _roots = new Dictionary<string, string> {
            [RemoteAdminPaths.SERVER] = MirrorFolder,
            [RemoteAdminPaths.EXTRA] = Path.Combine(MirrorFolder, "spiraldb-extra"),
            [RemoteAdminPaths.ID_LOOKUP] = Path.Combine(MirrorFolder, "id-lookup"),
            [RemoteAdminPaths.LOGS] = Path.Combine(MirrorFolder, "logs"),
        };
        Directory.CreateDirectory(Path.Combine(MirrorFolder, "Config"));
        _state = LoadState();
    }

    internal string ExtraFolder => _roots[RemoteAdminPaths.EXTRA];
    internal string IdLookupFolder => _roots[RemoteAdminPaths.ID_LOOKUP];
    internal string LogFolder => _roots[RemoteAdminPaths.LOGS];

    private static string SafeName(string text)
        => new([.. text.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_')]);

    // ---------------------------------------------------------------------
    // Connecting
    // ---------------------------------------------------------------------

    /// <summary>
    /// Connects and logs in, then brings the server's files over. A server seen for the first time shows its fingerprint
    /// to <paramref name="trust"/> before the password is sent; a server whose fingerprint changed is refused.
    /// </summary>
    internal static async Task<RemoteSession> ConnectAsync(string installFolder, RemoteServer server, string password,
                                                           Func<string, Task<bool>> trust, IProgress<string> progress) {
        if (Current is not null) {
            throw new PanelException("Click \"Back to my PC\" first, then connect to another server.");
        }

        var session = new RemoteSession(installFolder, server, password);
        try {
            progress.Report($"Connecting to {server.Label}…");
            var fingerprint = await session.OpenAsync();
            if (server.Fingerprint is null) {
                if (!await trust(RemoteAdminWire.ShortFingerprint(fingerprint))) {
                    throw new PanelException("Not connected. Check the fingerprint with the host first.");
                }

                session.Server = server with { Fingerprint = fingerprint };
            }

            await session.LoginAsync();
            RemoteServers.Save(installFolder, session.Server);
            progress.Report($"Logged in as {session.Account}. Bringing over the server's files…");
            await session.SyncAsync(progress, first: true);
            session.Connected = true;
            progress.Report($"Connected to {session.Label}.");

            return session;
        }
        catch {
            session.Dispose();
            throw;
        }
    }

    /// <summary>Makes this the server every page works on, and keeps its files in step from now on.</summary>
    internal void Activate() {
        Current = this;
        Watch();
        _ = Task.Run(LoopAsync);
    }

    /// <summary>Goes back to the server on this PC.</summary>
    internal static void Deactivate() {
        var current = Current;
        Current = null;
        current?.Dispose();
    }

    // Opens the encrypted connection and returns the server's fingerprint. A known server must show the same one.
    private async Task<string> OpenAsync() {
        Close();
        var tcp = new TcpClient { NoDelay = true };
        try {
            using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10))) {
                await tcp.ConnectAsync(Server.Host, Server.Port, wait.Token);
            }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) {
            tcp.Dispose();
            throw new PanelException($"Could not reach {Server.Host} on port {Server.Port}. Check the address, and ask the host to turn on " +
                "Remote Admin (Server > Remote) and let that port through their router.");
        }

        string? seen = null;
        var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) => {
            seen = certificate is null ? null : RemoteAdminWire.Fingerprint(certificate.GetRawCertData());

            // The server's certificate is its own, not from an authority: it is trusted by its fingerprint.
            return seen is not null && (Server.Fingerprint is null || seen == Server.Fingerprint);
        });
        try {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
                TargetHost = "Imlight Remote Admin",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, wait.Token);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException) {
            tls.Dispose();
            tcp.Dispose();
            if (seen is not null && Server.Fingerprint is not null && seen != Server.Fingerprint) {
                throw new PanelException($"{Server.Label} has a different fingerprint than last time ({RemoteAdminWire.ShortFingerprint(seen)}). " +
                    "Not connected, to keep your password safe. If the host made a new one, remove the server here and add it again.");
            }

            throw new PanelException($"{Server.Host} port {Server.Port} answered, but not as an Imlight server with Remote Admin.");
        }

        (_tcp, _tls) = (tcp, tls);

        return seen!;
    }

    private async Task LoginAsync() {
        var hello = await SendAsync(new JsonObject { ["op"] = "hello" }, null);
        if ((int?) hello.Header["protocol"] != RemoteAdminWire.PROTOCOL) {
            throw new PanelException("Ahziael and that server are different versions. Update both, then try again.");
        }

        var login = await SendAsync(new JsonObject {
            ["op"] = "login", ["username"] = Server.Username, ["password"] = _password, ["protocol"] = RemoteAdminWire.PROTOCOL,
        }, null);
        if (login.Header["ok"]?.GetValue<bool>() != true) {
            throw new PanelException((string?) login.Header["error"] ?? "The server did not let you in.");
        }

        Account = (string?) login.Header["account"] ?? Server.Username;
        ServerName = (string?) login.Header["name"] ?? "";
    }

    private void Close() {
        _tls?.Dispose();
        _tcp?.Dispose();
        (_tls, _tcp) = (null, null);
    }

    public void Dispose() {
        _stop.Cancel();
        _watcher?.Dispose();
        Connected = false;
        Close();
        SaveState();
    }

    // ---------------------------------------------------------------------
    // Requests
    // ---------------------------------------------------------------------

    // One request at a time on the one connection.
    private async Task<(JsonObject Header, byte[] Body)> SendAsync(JsonObject header, byte[]? body) {
        await _gate.WaitAsync();
        try {
            var tls = _tls ?? throw new IOException("Not connected.");
            header["id"] = ++_requestId;
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            wait.CancelAfter(TimeSpan.FromSeconds(90));
            await RemoteAdminWire.WriteAsync(tls, header, body, wait.Token);

            return await RemoteAdminWire.ReadAsync(tls, RemoteAdminWire.MAX_HEADER, RemoteAdminWire.MAX_BODY, wait.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or InvalidDataException) {
            throw new IOException("The connection to the server was lost.", ex);
        }
        finally {
            _gate.Release();
        }
    }

    // A request whose answer must be ok; a refusal becomes a PanelException with the server's words.
    private async Task<(JsonObject Header, byte[] Body)> AskAsync(JsonObject header, byte[]? body = null) {
        var reply = await SendAsync(header, body);
        if (reply.Header["ok"]?.GetValue<bool>() != true) {
            throw new PanelException((string?) reply.Header["error"] ?? "The server refused.");
        }

        return reply;
    }

    /// <summary>The next thing to tell the person, or null.</summary>
    internal string? TakeNotice() {
        lock (_notices) {
            return _notices.Count > 0 ? _notices.Dequeue() : null;
        }
    }

    private void Notice(string text) {
        lock (_notices) {
            if (_notices.Count < 10) {
                _notices.Enqueue(text);
            }
        }
    }

    /// <summary>
    /// Passes the Accounts tab's database requests through Remote Admin; the server only lets the account list, one
    /// account by name and saving an account through (RemoteAdminServer.DatabaseAsync).
    /// </summary>
    internal HttpMessageHandler DatabaseHandler() => new RemoteDatabaseHandler(this);

    private sealed class RemoteDatabaseHandler(RemoteSession session) : HttpMessageHandler {

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var header = new JsonObject { ["op"] = "db", ["body"] = body };
            if (request.Method == HttpMethod.Post && path.EndsWith("/queries", StringComparison.Ordinal)) {
                header["request"] = "query";
            }
            else if (request.Method == HttpMethod.Put && path.EndsWith("/docs", StringComparison.Ordinal)) {
                header["request"] = "put";
                header["docId"] = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["id"];
            }

            (JsonObject Header, byte[] Body) reply;
            try {
                reply = await session.SendAsync(header, null);
            }
            catch (IOException ex) {
                throw new HttpRequestException(ex.Message, ex);
            }

            if (reply.Header["ok"]?.GetValue<bool>() != true) {
                return new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden) {
                    Content = new StringContent((string?) reply.Header["error"] ?? "Refused."),
                };
            }

            return new HttpResponseMessage((System.Net.HttpStatusCode) ((int?) reply.Header["status"] ?? 500)) {
                Content = new StringContent((string?) reply.Header["body"] ?? "", Encoding.UTF8, "application/json"),
            };
        }

    }

    // ---------------------------------------------------------------------
    // Keeping the files in step
    // ---------------------------------------------------------------------

    private async Task LoopAsync() {
        while (!_stop.IsCancellationRequested) {
            try {
                await Task.Delay(s_every, _stop.Token);
                if (!Connected) {
                    await ReconnectAsync();
                    continue;
                }

                await SyncAsync(null, first: false);
                var status = await AskAsync(new JsonObject { ["op"] = "status" });
                Players = (int?) status.Header["players"] ?? 0;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) {
                return;
            }
            catch (IOException) {
                if (Connected) {
                    Connected = false;
                    Notice($"Lost the connection to {Label}. Trying again…");
                }
            }
            catch (PanelException ex) {
                Notice(ex.Message);
            }
            catch (Exception ex) {
                Notice($"Remote server: {ex.Message}");
            }
        }
    }

    private DateTime _nextTry;

    private async Task ReconnectAsync() {
        if (DateTime.UtcNow < _nextTry) {
            return;
        }

        _nextTry = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        try {
            await OpenAsync();
            await LoginAsync();
            Connected = true;
            Notice($"Connected to {Label} again.");
        }
        catch (PanelException ex) when (ex.Message.Contains("fingerprint") || ex.Message.Contains("Administrator") || ex.Message.Contains("Wrong")) {
            Notice(ex.Message);
            _nextTry = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        }
        catch (Exception ex) when (ex is PanelException or IOException) {
            // Still away; tried again in a few seconds.
        }
    }

    /// <summary>
    /// One round: the commands and announcements the pages wrote, then this PC's changed files to the server, then the
    /// server's changed files here. A file changed on both sides keeps this PC's version, as it was the person's edit.
    /// </summary>
    private async Task SyncAsync(IProgress<string>? progress, bool first) {
        await SendActionsAsync();
        if (!first) {
            await UploadAsync();
        }

        var changes = await AskAsync(new JsonObject { ["op"] = "changes", ["epoch"] = _epoch, ["since"] = _seq });
        var full = changes.Header["full"]?.GetValue<bool>() == true;
        var remote = new Dictionary<string, (string Hash, long Size, bool Gone)>(StringComparer.Ordinal);
        foreach (var node in changes.Header["files"] as JsonArray ?? []) {
            var path = (string?) node?["p"] ?? "";
            if (RemoteAdminPaths.IsShared(path)) {
                remote[path] = ((string?) node!["h"] ?? "", (long?) node["s"] ?? 0, node["gone"]?.GetValue<bool>() == true);
            }
        }

        // A full list leaves out what is gone: whatever this PC copied before and the list lacks was removed there.
        if (full) {
            foreach (var path in _state.Keys.Where(p => !remote.ContainsKey(p)).ToList()) {
                remote[path] = ("", 0, true);
            }
        }

        foreach (var path in _refetch) {
            if (!remote.ContainsKey(path)) {
                remote[path] = ("refetch", 0, false);
            }
        }

        _refetch.Clear();
        var wanted = new List<string>();
        foreach (var (path, (hash, size, gone)) in remote) {
            if (RemoteAdminPaths.OnDisk(path, _roots) is not { } file) {
                continue;
            }

            if (gone) {
                if (_state.Remove(path)) {
                    _dirty = true;
                    if (File.Exists(file)) {
                        TryDelete(file);
                    }
                }

                continue;
            }

            if (_state.TryGetValue(path, out var known) && known.Hash == hash && File.Exists(file)) {
                continue;
            }

            // The first time, files this PC already has the same (its own server, or an earlier visit) are not fetched.
            if (first && !RemoteAdminPaths.IsLog(path) && TryReuse(path, file, hash, size)) {
                continue;
            }

            wanted.Add(path);
        }

        await DownloadAsync(wanted, progress);
        _epoch = (string?) changes.Header["epoch"] ?? "";
        _seq = (long?) changes.Header["seq"] ?? 0;
        SaveState();
    }

    // A file already here with the right contents: in the copy itself, or in this PC's own server of the same version.
    private bool TryReuse(string path, string file, string hash, long size) {
        try {
            if (File.Exists(file) && new FileInfo(file).Length == size && RemoteAdminWire.HashOf(file) == hash) {
                Remember(path, file, hash);

                return true;
            }

            if (LocalTwin(path) is { } twin && File.Exists(twin) && new FileInfo(twin).Length == size && RemoteAdminWire.HashOf(twin) == hash) {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.Copy(twin, file, overwrite: true);
                Remember(path, file, hash);

                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        }

        return false;
    }

    private Dictionary<string, string>? _localRoots;

    // The same file in this PC's own server: the shipped additions folder is most of what a server has.
    private string? LocalTwin(string path) {
        if (!path.StartsWith(RemoteAdminPaths.EXTRA + "/", StringComparison.Ordinal)) {
            return null;
        }

        _localRoots ??= new Dictionary<string, string> { [RemoteAdminPaths.EXTRA] = LocalExtraFolder() };

        return RemoteAdminPaths.OnDisk(path, _localRoots);
    }

    private string LocalExtraFolder() {
        var server = ServerSource.Load(_installFolder).ServerFolder(_installFolder);
        var config = Path.Combine(server, "Config", "Imlight.ini");

        return Path.GetFullPath(Path.Combine(server, IniFile.Get(config, "Database", "SpiralDBExtraPath") ?? "./spiraldb-extra"));
    }

    private async Task DownloadAsync(List<string> wanted, IProgress<string>? progress) {
        var done = 0;
        while (wanted.Count > 0) {
            var batch = wanted.Take(2000).ToList();
            var from = new JsonObject();
            foreach (var path in batch.Where(RemoteAdminPaths.IsLog)) {
                if (_state.TryGetValue(path, out var known) && known.End > 0) {
                    from[path] = known.End;
                }
            }

            var reply = await AskAsync(new JsonObject {
                ["op"] = "get", ["paths"] = new JsonArray([.. batch.Select(p => (JsonNode) p)]), ["from"] = from,
            });
            var data = RemoteAdminWire.Unpack(reply.Body).ToDictionary(f => f.Path, f => f.Data, StringComparer.Ordinal);
            var answered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in reply.Header["files"] as JsonArray ?? []) {
                var path = (string?) node?["p"] ?? "";
                answered.Add(path);
                if (RemoteAdminPaths.OnDisk(path, _roots) is not { } file) {
                    continue;
                }

                if (node!["gone"]?.GetValue<bool>() == true || !data.TryGetValue(path, out var bytes)) {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                if (RemoteAdminPaths.IsLog(path)) {
                    var start = (long?) node["start"] ?? 0;
                    var appending = _state.TryGetValue(path, out var known) && known.End == start && start > 0 && File.Exists(file);
                    using (var stream = new FileStream(file, appending ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) {
                        stream.Write(bytes);
                    }

                    var info = new FileInfo(file);
                    _dirty = true;
                    _state[path] = new SyncState { Hash = $"{(long?) node["end"] ?? 0}", Size = info.Length, Ticks = info.LastWriteTimeUtc.Ticks, End = (long?) node["end"] ?? 0 };
                }
                else {
                    var temporary = file + ".ahziael-part";
                    File.WriteAllBytes(temporary, bytes);
                    File.Move(temporary, file, overwrite: true);
                    Remember(path, file, (string?) node["h"] ?? RemoteAdminWire.HashOf(bytes));
                }
            }

            done += answered.Count;
            wanted.RemoveAll(answered.Contains);
            if (answered.Count == 0) {
                break;
            }

            progress?.Report($"Bringing over the server's files: {done} so far, {wanted.Count} to go…");
        }
    }

    private void Remember(string path, string file, string hash) {
        _dirty = true;
        var info = new FileInfo(file);
        _state[path] = new SyncState { Hash = hash, Size = info.Length, Ticks = info.LastWriteTimeUtc.Ticks };
    }

    // What changed in the copy, told by Windows as it happens, so a round looks only at those files; every half
    // minute the whole copy is looked at too, in case a change was missed.
    private FileSystemWatcher? _watcher;
    private readonly HashSet<string> _touched = new(StringComparer.Ordinal);
    private DateTime _fullScanUtc;
    private static readonly TimeSpan s_fullScanEvery = TimeSpan.FromSeconds(30);

    private void Watch() {
        try {
            _watcher = new FileSystemWatcher(MirrorFolder) {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            FileSystemEventHandler touched = (_, e) => Touch(e.FullPath);
            _watcher.Changed += touched;
            _watcher.Created += touched;
            _watcher.Deleted += touched;
            _watcher.Renamed += (_, e) => {
                Touch(e.OldFullPath);
                Touch(e.FullPath);
            };
            _watcher.Error += (_, _) => _fullScanUtc = DateTime.MinValue;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException) {
            _watcher = null;
        }
    }

    private void Touch(string file) {
        lock (_touched) {
            _touched.Add(file);
        }
    }

    // The root path of a file in the copy: the folder deepest in wins, as the additions folder sits in the server's.
    private string? RootPathOf(string file) {
        foreach (var (root, folder) in _roots.OrderByDescending(r => r.Value.Length)) {
            var top = folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (file.StartsWith(top, StringComparison.Ordinal)) {
                return root + "/" + file[top.Length..].Replace('\\', '/');
            }
        }

        return null;
    }

    private async Task UploadAsync() {
        List<string> touched;
        lock (_touched) {
            touched = [.. _touched];
            _touched.Clear();
        }

        if (_watcher is null || DateTime.UtcNow - _fullScanUtc > s_fullScanEvery) {
            _fullScanUtc = DateTime.UtcNow;
            await UploadEverythingAsync();

            return;
        }

        foreach (var file in touched) {
            if (RootPathOf(file) is not { } path || RemoteAdminPaths.IsLog(path)) {
                continue;
            }

            if (File.Exists(file)) {
                if (RemoteAdminPaths.IsShared(path)) {
                    await UploadOneAsync(path, new FileInfo(file));
                }
            }
            else if (Directory.Exists(file)) {
                // A folder made or renamed: what is in it is new.
                foreach (var (inner, info) in RemoteAdminPaths.List(path.Split('/')[0], _roots[path.Split('/')[0]]).Where(f => f.File.FullName.StartsWith(file, StringComparison.Ordinal)).ToList()) {
                    await UploadOneAsync(inner, info);
                }
            }
            else {
                // A file or a whole folder removed.
                foreach (var gone in _state.Keys.Where(p => p == path || p.StartsWith(path + "/", StringComparison.Ordinal)).ToList()) {
                    await RemoveOneAsync(gone);
                }
            }
        }
    }

    private async Task UploadEverythingAsync() {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (root, folder) in _roots) {
            if (root == RemoteAdminPaths.LOGS) {
                continue;
            }

            foreach (var (path, info) in RemoteAdminPaths.List(root, folder).ToList()) {
                seen.Add(path);
                await UploadOneAsync(path, info);
            }
        }

        // Removed here: removed there too.
        foreach (var path in _state.Keys.Where(p => !RemoteAdminPaths.IsLog(p) && !seen.Contains(p)).ToList()) {
            await RemoveOneAsync(path);
        }
    }

    private async Task UploadOneAsync(string path, FileInfo info) {
        if (_state.TryGetValue(path, out var known) && known.Size == info.Length && known.Ticks == info.LastWriteTimeUtc.Ticks) {
            return;
        }

        byte[] bytes;
        try {
            bytes = await File.ReadAllBytesAsync(info.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Being written; sent on the next round.
            Touch(info.FullName);

            return;
        }

        var hash = RemoteAdminWire.HashOf(bytes);
        if (known is not null && known.Hash == hash) {
            Remember(path, info.FullName, hash);

            return;
        }

        if (!RemoteAdminPaths.MayWrite(path)) {
            return;
        }

        var reply = await SendAsync(new JsonObject { ["op"] = "put", ["p"] = path }, RemoteAdminWire.Gzip(bytes));
        Remember(path, info.FullName, hash);
        if (reply.Header["ok"]?.GetValue<bool>() != true) {
            Notice((string?) reply.Header["error"] ?? $"The server did not take {Path.GetFileName(path)}.");
            _refetch.Add(path);
        }
        else if (reply.Header["refused"] is JsonArray { Count: > 0 } refused) {
            Notice($"Only the host can change {string.Join(", ", refused.Select(r => (string?) r))}. Those settings were put back.");
            _refetch.Add(path);
        }
    }

    private async Task RemoveOneAsync(string path) {
        if (RemoteAdminPaths.OnDisk(path, _roots) is { } file && File.Exists(file)) {
            return;
        }

        if (!_state.Remove(path)) {
            return;
        }

        _dirty = true;
        if (RemoteAdminPaths.MayWrite(path)) {
            var reply = await SendAsync(new JsonObject { ["op"] = "delete", ["p"] = path }, null);
            if (reply.Header["ok"]?.GetValue<bool>() != true) {
                _refetch.Add(path);
            }
        }
    }

    // The files the pages write for the server on this PC to act on, sent as requests instead.
    private const string COMMANDS_FILE = "manager-commands.txt";
    private const string ANNOUNCEMENTS_FILE = "server-announcements.txt";

    private async Task SendActionsAsync() {
        foreach (var line in Take(COMMANDS_FILE)) {
            var (wizard, command) = (default(string), line.Trim());
            if (command.StartsWith('@') && command.IndexOf(' ') is var space and > 1) {
                (wizard, command) = (command[1..space], command[(space + 1)..].Trim());
            }

            if (command.Length == 0) {
                continue;
            }

            var reply = await SendAsync(new JsonObject { ["op"] = "command", ["text"] = command, ["wizard"] = wizard }, null);
            if (reply.Header["ok"]?.GetValue<bool>() != true) {
                Notice((string?) reply.Header["error"] ?? "The server did not run the command.");
            }
        }

        foreach (var line in Take(ANNOUNCEMENTS_FILE)) {
            var reply = await SendAsync(new JsonObject { ["op"] = "announce", ["text"] = line }, null);
            if (reply.Header["ok"]?.GetValue<bool>() != true) {
                Notice((string?) reply.Header["error"] ?? "The server did not send the announcement.");
            }
        }
    }

    private List<string> Take(string name) {
        var file = Path.Combine(MirrorFolder, name);
        try {
            if (!File.Exists(file)) {
                return [];
            }

            var taken = file + ".sending";
            File.Move(file, taken, overwrite: true);
            var lines = File.ReadAllLines(taken).Where(l => l.Trim().Length > 0).ToList();
            File.Delete(taken);

            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    private static void TryDelete(string file) {
        try {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        }
    }

    private Dictionary<string, SyncState> LoadState() {
        try {
            if (File.Exists(_statePath)) {
                return JsonSerializer.Deserialize<Dictionary<string, SyncState>>(File.ReadAllText(_statePath)) ?? new(StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
        }

        return new Dictionary<string, SyncState>(StringComparer.Ordinal);
    }

    // Written only when something changed: the list has a line for each of the server's files.
    private void SaveState() {
        if (!_dirty) {
            return;
        }

        try {
            File.WriteAllText(_statePath, JsonSerializer.Serialize(_state));
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) {
            // Kept for next time; at worst a file is compared again.
        }
    }

}
