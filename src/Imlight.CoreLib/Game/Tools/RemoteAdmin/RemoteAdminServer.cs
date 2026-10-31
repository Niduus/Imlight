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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Game.Commands;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.RemoteAdmin;

namespace Imlight.CoreLib.Game.Tools.RemoteAdmin;

/// <summary>
/// Lets Ahziael on another PC manage this server (Niduus, 2026-10-30), for accounts with the Administrator rank only.
/// Off until [Remote Admin] Enabled is true, and follows that switch and the port live. The connection is TLS with a
/// certificate made here once; Ahziael remembers its fingerprint. Logins are checked against the server's own accounts,
/// and an address with 5 wrong ones in 15 minutes is turned away until they age out. What a remote admin can change is
/// limited to data files (RemoteAdminPaths) and the settings RemoteAdminIni does not lock; nothing is sent anywhere else.
///
/// The same port lets any player download a copy of their own account to take to another server (Niduus, 2026-10-31:
/// "Allow for players using the Ahziael application whilst in another server, to copy their player data"), while
/// [Remote Admin] PlayerDownloads is on (the default). A player logs in with their own game username and password and
/// can do nothing else: no files, no settings, no other account (AccountTransfer).
/// </summary>
public static class RemoteAdminServer {

    private const int MAX_FAILURES = 5;
    private static readonly TimeSpan s_failureWindow = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan s_loginTime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_idleTime = TimeSpan.FromMinutes(5);
    private const int MAX_CONNECTIONS = 16;
    private const int MAX_WAITING_PER_ADDRESS = 3;
    private const long MAX_BATCH = 24L << 20;

    private static readonly object s_lock = new();
    private static TcpListener s_listener;
    private static CancellationTokenSource s_stop;
    private static (int Port, string Bind) s_listening;
    private static X509Certificate2 s_certificate;
    private static bool s_started;

    private static readonly string s_serverFolder = Directory.GetCurrentDirectory();
    private static readonly string s_epoch = Guid.NewGuid().ToString("N");
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<DateTime>> s_failures = new();
    private static readonly ConcurrentDictionary<string, int> s_waiting = new();
    private static int s_connections;
    private static readonly FileIndex s_index = new();
    private static readonly HttpClient s_database = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Where the certificate and its fingerprint are kept, next to Imlight.ini.</summary>
    public static string Folder => Path.Combine(s_serverFolder, "Config", "remote-admin");

    /// <summary>Starts following the [Remote Admin] settings. Calling it again does nothing.</summary>
    public static void Start() {
        lock (s_lock) {
            if (s_started) {
                return;
            }

            s_started = true;
        }

        ConfigurationManager.Reloaded += Apply;
        Apply();
    }

    private static void Apply() {
        try {
            var enabled = AdminsOn || PlayersOn;
            var port = ConfigurationManager.GetValue("Remote Admin.Port", RemoteAdminWire.DEFAULT_PORT);
            var bind = ConfigurationManager.GetValue("Remote Admin.ListenAddress", "");
            lock (s_lock) {
                if (!enabled) {
                    if (s_listener is not null) {
                        StopListening();
                        Logger.Information("Remote Admin and account downloads are off: Ahziael on other PCs can no longer connect.");
                    }

                    return;
                }

                if (s_listener is not null && s_listening == (port, bind)) {
                    return;
                }

                StopListening();
                s_certificate ??= LoadOrMakeCertificate();
                var listener = Listen(bind, port);
                s_listener = listener;
                s_listening = (port, bind);
                s_stop = new CancellationTokenSource();
                _ = AcceptAsync(listener, s_stop.Token);
                Logger.Information("Remote Admin is listening on port {0}: {1}. Certificate fingerprint: {2}", Logger.Args(port,
                    AdminsOn && PlayersOn ? "Administrator accounts can manage this server, and players can download their own account"
                        : AdminsOn ? "Administrator accounts can manage this server"
                        : "players can download their own account (Remote Admin itself is off)",
                    RemoteAdminWire.ShortFingerprint(RemoteAdminWire.Fingerprint(s_certificate.RawData))));
            }
        }
        catch (Exception ex) {
            Logger.Error("Remote Admin could not start: {0}", Logger.Args(ex.Message));
        }
    }

    private static bool AdminsOn => ConfigurationManager.GetValue("Remote Admin.Enabled", false);

    private static bool PlayersOn => ConfigurationManager.GetValue("Remote Admin.PlayerDownloads", true);

    // Every address of this PC (IPv4 and IPv6) unless ListenAddress names one.
    private static TcpListener Listen(string bind, int port) {
        if (!string.IsNullOrWhiteSpace(bind)) {
            var one = new TcpListener(IPAddress.Parse(bind.Trim()), port);
            one.Start();

            return one;
        }

        try {
            var both = new TcpListener(IPAddress.IPv6Any, port);
            both.Server.DualMode = true;
            both.Start();

            return both;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported) {
            var four = new TcpListener(IPAddress.Any, port);
            four.Start();

            return four;
        }
    }

    private static void StopListening() {
        s_stop?.Cancel();
        s_listener?.Stop();
        s_listener = null;
        s_stop = null;
    }

    /// <summary>
    /// The server's own certificate, made the first time: a key that never leaves this PC. Its fingerprint is written
    /// next to it so Ahziael on this PC can show it to the host, who tells it to their admins.
    /// </summary>
    private static X509Certificate2 LoadOrMakeCertificate() {
        Directory.CreateDirectory(Folder);
        var file = Path.Combine(Folder, "certificate.pfx");
        byte[] pfx;
        if (File.Exists(file)) {
            pfx = File.ReadAllBytes(file);
        }
        else {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Imlight Remote Admin", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
            pfx = made.Export(X509ContentType.Pkcs12);
            File.WriteAllBytes(file, pfx);
        }

        // Loaded from the file's bytes: Windows' TLS can't use a key that only lives in memory.
        var certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
        File.WriteAllText(Path.Combine(Folder, "fingerprint.txt"), RemoteAdminWire.Fingerprint(certificate.RawData));

        return certificate;
    }

    private static async Task AcceptAsync(TcpListener listener, CancellationToken stop) {
        while (!stop.IsCancellationRequested) {
            TcpClient client;
            try {
                client = await listener.AcceptTcpClientAsync(stop);
            }
            catch (Exception) when (stop.IsCancellationRequested) {
                return;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException) {
                return;
            }

            _ = Task.Run(() => ServeAsync(client, stop), CancellationToken.None);
        }
    }

    private static string AddressOf(TcpClient client) {
        var address = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        if (address?.IsIPv4MappedToIPv6 == true) {
            address = address.MapToIPv4();
        }

        return address?.ToString() ?? "?";
    }

    private sealed class Session {

        internal ulong AccountId;
        internal string Username;
        internal string Address;
        internal DateTime CheckedUtc;

        /// <summary>Logged in only to download their own account.</summary>
        internal bool PlayerOnly;

    }

    private static async Task ServeAsync(TcpClient client, CancellationToken stop) {
        var address = AddressOf(client);
        var waiting = true;
        if (Interlocked.Increment(ref s_connections) > MAX_CONNECTIONS
            || s_waiting.AddOrUpdate(address, 1, (_, n) => n + 1) > MAX_WAITING_PER_ADDRESS) {
            Interlocked.Decrement(ref s_connections);
            s_waiting.AddOrUpdate(address, 0, (_, n) => n - 1);
            client.Dispose();

            return;
        }

        Session session = null;
        try {
            using var _ = client;
            client.NoDelay = true;
            await using var tls = new SslStream(client.GetStream(), false);
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop)) {
                handshake.CancelAfter(TimeSpan.FromSeconds(10));
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                    ServerCertificate = s_certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, handshake.Token);
            }

            var loginBy = DateTime.UtcNow + s_loginTime;
            while (!stop.IsCancellationRequested) {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop);
                wait.CancelAfter(session is null ? loginBy - DateTime.UtcNow : s_idleTime);
                var (header, body) = await RemoteAdminWire.ReadAsync(tls,
                    session is null ? RemoteAdminWire.MAX_LOGIN_HEADER : RemoteAdminWire.MAX_HEADER,
                    session is null ? 0 : RemoteAdminWire.MAX_BODY, wait.Token);
                var op = (string) header["op"] ?? "";
                JsonObject reply;
                byte[] replyBody = null;
                if (session is null) {
                    if (op == "hello") {
                        reply = Hello();
                    }
                    else if (op == "login") {
                        (reply, session) = await LoginAsync(header, address);
                        if (session is not null && waiting) {
                            waiting = false;
                            s_waiting.AddOrUpdate(address, 0, (_, n) => n - 1);
                        }
                    }
                    else {
                        reply = RemoteAdminWire.Error("Log in first.");
                    }
                }
                else if (StillAllowed(session) is { } gone) {
                    await RemoteAdminWire.WriteAsync(tls, RemoteAdminWire.Error(gone), null, stop);

                    return;
                }
                else {
                    try {
                        (reply, replyBody) = await HandleAsync(session, op, header, body);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) {
                        reply = RemoteAdminWire.Error(ex.Message);
                    }
                }

                reply["id"] = header["id"]?.DeepClone();
                await RemoteAdminWire.WriteAsync(tls, reply, replyBody, stop);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or AuthenticationException
                                       or InvalidDataException or ObjectDisposedException or System.Text.Json.JsonException) {
            // Gone, too slow, or not speaking Remote Admin.
        }
        catch (Exception ex) {
            Logger.Error("Remote Admin: a connection from {0} failed: {1}", Logger.Args(address, ex.Message));
        }
        finally {
            Interlocked.Decrement(ref s_connections);
            if (waiting) {
                s_waiting.AddOrUpdate(address, 0, (_, n) => n - 1);
            }

            if (session is not null) {
                Logger.Information("Remote admin {0} ({1}) disconnected.", Logger.Args(session.Username, address));
            }
        }
    }

    private static JsonObject Hello() {
        var hello = RemoteAdminWire.Ok();
        hello["protocol"] = RemoteAdminWire.PROTOCOL;
        hello["name"] = ConfigurationManager.GetValue("Remote Admin.ServerName", "");

        return hello;
    }

    // ---------------------------------------------------------------------
    // Logging in
    // ---------------------------------------------------------------------

    private static bool TooManyFailures(string address, out TimeSpan wait) {
        wait = TimeSpan.Zero;
        if (!s_failures.TryGetValue(address, out var times)) {
            return false;
        }

        while (times.TryPeek(out var oldest) && DateTime.UtcNow - oldest > s_failureWindow) {
            times.TryDequeue(out _);
        }

        if (times.Count < MAX_FAILURES || !times.TryPeek(out var first)) {
            return false;
        }

        wait = s_failureWindow - (DateTime.UtcNow - first);

        return true;
    }

    private static async Task<(JsonObject, Session)> LoginAsync(JsonObject header, string address) {
        if (TooManyFailures(address, out var wait)) {
            Logger.Warning("Remote Admin: turned away {0}, which got the password wrong {1} times.", Logger.Args(address, MAX_FAILURES));

            return (RemoteAdminWire.Error($"Too many wrong passwords. Try again in {Math.Max(1, (int) Math.Ceiling(wait.TotalMinutes))} minutes."), null);
        }

        var username = ((string) header["username"] ?? "").Trim();
        var password = (string) header["password"] ?? "";
        if ((int?) header["protocol"] is { } protocol && protocol != RemoteAdminWire.PROTOCOL) {
            return (RemoteAdminWire.Error("Ahziael and this server are different versions. Update both, then try again."), null);
        }

        Account account = null;
        if (username.Length is > 0 and <= 64 && password.Length is > 0 and <= 256) {
            account = AccountCollection.GetAccountForCharacterList(username);
        }

        var hash = Encoding.UTF8.GetBytes(DatabaseUtilities.CreateHashedPassword(password));
        var right = account is not null && !account.PasswordNotSet && account.PasswordHash is { Length: > 0 } stored
            && CryptographicOperations.FixedTimeEquals(hash, Encoding.UTF8.GetBytes(stored));
        if (!right || account.IsLocked || account.InfractionHistory?.IsCurrentlyBanned == true) {
            s_failures.GetOrAdd(address, _ => new ConcurrentQueue<DateTime>()).Enqueue(DateTime.UtcNow);
            Logger.Warning("Remote Admin: a wrong username or password from {0}.", Logger.Args(address));
            // Slows down guessing even before the limit.
            await Task.Delay(1000);

            return (RemoteAdminWire.Error("Wrong username or password."), null);
        }

        var playerOnly = (string) header["purpose"] == "account";
        if (playerOnly ? !PlayersOn : !AdminsOn) {
            return (RemoteAdminWire.Error(playerOnly
                ? "The host has turned off account downloads on this server. Ask them to turn on \"Let players download their account\" on Ahziael's Account Transfer tab."
                : "Remote Admin is off on this server. Ask the host to turn it on (Server > Remote in their Ahziael)."), null);
        }

        if (!playerOnly && account.AuthLevel < AuthLevel.Administrator) {
            Logger.Warning("Remote Admin: {0} from {1} is not an Administrator, so it was not let in.", Logger.Args(account.Username, address));

            return (RemoteAdminWire.Error("That account is not an Administrator on this server, so it can't use Ahziael here. Ask the host to give it the Administrator rank."), null);
        }

        // Every server ships with the same first admin account and password, so that pair would let anyone in.
        if (password == "compsci" || username.Equals(ConfigurationManager.GetValue("Database.AdminAccountUsername", "admin"), StringComparison.OrdinalIgnoreCase)
            && password == ConfigurationManager.GetValue("Database.AdminAccountPassword", "compsci")) {
            Logger.Warning("Remote Admin: {0} still has the password every server starts with, so it was not let in from {1}.", Logger.Args(account.Username, address));

            return (RemoteAdminWire.Error("That account still has the password every Imlight server starts with, so Remote Admin won't take it. " +
                "Give it a new password first: on the host's Accounts tab, or with .setpassword in the game."), null);
        }

        s_failures.TryRemove(address, out _);
        Logger.Information(playerOnly ? "Account download: {0} logged in from {1}." : "Remote admin {0} logged in from {1}.", Logger.Args(account.Username, address));
        var reply = RemoteAdminWire.Ok();
        reply["account"] = account.Username;
        reply["accountId"] = account.AccountId.ToString();
        reply["name"] = ConfigurationManager.GetValue("Remote Admin.ServerName", "");

        return (reply, new Session {
            AccountId = account.AccountId, Username = account.Username, Address = address, CheckedUtc = DateTime.UtcNow, PlayerOnly = playerOnly,
        });
    }

    // A switch turned off ends the connection at once; a rank taken away or an account locked within a minute.
    // Null while the session may go on, else why it ends.
    private static string StillAllowed(Session session) {
        if (session.PlayerOnly ? !PlayersOn : !AdminsOn) {
            return session.PlayerOnly ? "The host turned off account downloads." : "The host turned Remote Admin off.";
        }

        if (DateTime.UtcNow - session.CheckedUtc < TimeSpan.FromMinutes(1)) {
            return null;
        }

        var account = AccountCollection.GetAccountForCharacterList(session.AccountId);
        if (account is null || account.IsLocked || (!session.PlayerOnly && account.AuthLevel < AuthLevel.Administrator)) {
            Logger.Warning("Remote Admin: {0} can no longer log in here and was disconnected.", Logger.Args(session.Username));

            return session.PlayerOnly ? "Your account can no longer log in to this server." : "Your account is no longer an Administrator on this server.";
        }

        session.CheckedUtc = DateTime.UtcNow;

        return null;
    }

    // ---------------------------------------------------------------------
    // Requests
    // ---------------------------------------------------------------------

    private static async Task<(JsonObject, byte[])> HandleAsync(Session session, string op, JsonObject header, byte[] body) {
        if (session.PlayerOnly) {
            return op == "export"
                ? await ExportAsync(session, header)
                : (RemoteAdminWire.Error("This login can only download your own account."), null);
        }

        switch (op) {
            case "status": {
                var reply = RemoteAdminWire.Ok();
                reply["players"] = OnlinePlayerCollection.GetOnlinePlayers().Count();

                return (reply, null);
            }
            case "changes":
                return (s_index.Changes(Roots(), (string) header["epoch"] ?? "", (long?) header["since"] ?? 0, s_epoch), null);
            case "get":
                return Get(header);
            case "put":
                return (Put(session, header, body), null);
            case "delete":
                return (Delete(session, header), null);
            case "command":
                return (await CommandAsync(session, header), null);
            case "announce":
                return (Announce(session, header), null);
            case "db":
                return (await DatabaseAsync(header), null);
            case "export":
                return await ExportAsync(session, header);
            case "import":
                return (await ImportAsync(session, header, body), null);
            default:
                return (RemoteAdminWire.Error($"This server does not know \"{op}\". Update it."), null);
        }
    }

    /// <summary>The folders kept in step, read again each time as the settings can change.</summary>
    private static Dictionary<string, string> Roots() {
        string Full(string path) => Path.GetFullPath(Path.Combine(s_serverFolder, path));

        return new Dictionary<string, string> {
            [RemoteAdminPaths.SERVER] = s_serverFolder,
            [RemoteAdminPaths.EXTRA] = Full(ConfigurationManager.GetValue("Database.SpiralDBExtraPath", "./spiraldb-extra")),
            [RemoteAdminPaths.ID_LOOKUP] = Full(ConfigurationManager.GetValue("ID Lookup.OutputPath", "./id-lookup")),
            [RemoteAdminPaths.LOGS] = Path.GetDirectoryName(Full(ConfigurationManager.GetValue("Logging.LogPath", "./logs/log.txt"))) ?? Full("logs"),
        };
    }

    private static (JsonObject, byte[]) Get(JsonObject header) {
        var roots = Roots();
        var from = header["from"] as JsonObject;
        var files = new List<(string, byte[])>();
        var listed = new JsonArray();
        long total = 0;
        var more = false;
        foreach (var node in header["paths"] as JsonArray ?? []) {
            var path = (string) node ?? "";
            if (!RemoteAdminPaths.IsShared(path) || RemoteAdminPaths.OnDisk(path, roots) is not { } file || !File.Exists(file)) {
                listed.Add(new JsonObject { ["p"] = path, ["gone"] = true });
                continue;
            }

            if (total > 0 && total >= MAX_BATCH) {
                more = true;
                break;
            }

            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            if (length > RemoteAdminWire.MAX_FILE) {
                listed.Add(new JsonObject { ["p"] = path, ["gone"] = true });
                continue;
            }

            long start = 0;
            if (RemoteAdminPaths.IsLog(path)) {
                start = (long?) from?[path] ?? 0;
                if (start <= 0 || start > length) {
                    start = Math.Max(0, length - RemoteAdminWire.LOG_TAIL);
                }
            }

            stream.Position = start;
            var data = new byte[length - start];
            stream.ReadExactly(data);
            total += data.Length;
            files.Add((path, data));
            listed.Add(new JsonObject {
                ["p"] = path, ["start"] = start, ["end"] = start + data.Length,
                ["h"] = RemoteAdminPaths.IsLog(path) ? null : RemoteAdminWire.HashOf(data),
            });
        }

        var reply = RemoteAdminWire.Ok();
        reply["files"] = listed;
        reply["more"] = more;

        return (reply, RemoteAdminWire.Pack(files));
    }

    private static JsonObject Put(Session session, JsonObject header, byte[] body) {
        var path = (string) header["p"] ?? "";
        if (!RemoteAdminPaths.MayWrite(path) || RemoteAdminPaths.OnDisk(path, Roots()) is not { } file) {
            return RemoteAdminWire.Error("Remote Admin can't change that file.");
        }

        var data = RemoteAdminWire.Gunzip(body, RemoteAdminWire.MAX_FILE);
        var reply = RemoteAdminWire.Ok();
        if (path == RemoteAdminPaths.SERVER + "/" + RemoteAdminPaths.CONFIG) {
            var current = File.Exists(file) ? File.ReadAllText(file) : "";
            var merged = RemoteAdminIni.Merge(current, Encoding.UTF8.GetString(data), out var refused);
            if (refused.Count > 0) {
                Logger.Warning("Remote admin {0} tried to change settings only the host can change: {1}", Logger.Args(session.Username, string.Join(", ", refused)));
                reply["refused"] = new JsonArray([.. refused.Select(r => (JsonNode) r)]);
            }

            if (merged == current) {
                return reply;
            }

            data = Encoding.UTF8.GetBytes(merged);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".remote-admin";
        File.WriteAllBytes(temporary, data);
        File.Move(temporary, file, overwrite: true);
        s_index.Forget(path);
        Logger.Information("Remote admin {0} changed {1}.", Logger.Args(session.Username, path));

        return reply;
    }

    private static JsonObject Delete(Session session, JsonObject header) {
        var path = (string) header["p"] ?? "";
        if (!RemoteAdminPaths.MayWrite(path) || path == RemoteAdminPaths.SERVER + "/" + RemoteAdminPaths.CONFIG
            || RemoteAdminPaths.OnDisk(path, Roots()) is not { } file) {
            return RemoteAdminWire.Error("Remote Admin can't remove that file.");
        }

        if (File.Exists(file)) {
            File.Delete(file);
            Logger.Information("Remote admin {0} removed {1}.", Logger.Args(session.Username, path));
        }

        s_index.Forget(path);

        return RemoteAdminWire.Ok();
    }

    /// <summary>Runs a command for the remote admin's own wizards in the game, never anyone else's.</summary>
    private static async Task<JsonObject> CommandAsync(Session session, JsonObject header) {
        var text = ((string) header["text"] ?? "").ReplaceLineEndings(" ").Trim().TrimStart('.').Trim();
        if (text.Length == 0) {
            return RemoteAdminWire.Error("Type a command first.");
        }

        ulong? wizard = ulong.TryParse((string) header["wizard"], out var id) ? id : null;
        var ran = new ConcurrentQueue<string>();
        var command = new RemoteAdminCommand(session.AccountId, wizard, text, ran.Enqueue);
        foreach (var service in AgentBridge.CommandServices) {
            service.Tell(command);
        }

        for (var i = 0; i < 10 && ran.IsEmpty; i++) {
            await Task.Delay(100);
        }

        if (ran.IsEmpty) {
            return RemoteAdminWire.Error("None of your wizards are in the game on that server. Log in to the game there first.");
        }

        var reply = RemoteAdminWire.Ok();
        reply["wizards"] = new JsonArray([.. ran.Distinct().Select(n => (JsonNode) n)]);

        return reply;
    }

    private static JsonObject Announce(Session session, JsonObject header) {
        var text = ((string) header["text"] ?? "").ReplaceLineEndings(" ").Trim();
        if (text.Length == 0) {
            return RemoteAdminWire.Error("Type what to announce first.");
        }

        Logger.Information("Remote admin {0} sent an announcement.", Logger.Args(session.Username));
        File.AppendAllText(Path.Combine(s_serverFolder, "server-announcements.txt"), text + Environment.NewLine);

        return RemoteAdminWire.Ok();
    }

    /// <summary>
    /// The Accounts tab's two requests, passed to the server's own database: the account list, one account by name,
    /// and saving one account. Nothing else in the database can be read or changed this way.
    /// </summary>
    // ---------------------------------------------------------------------
    // Moving accounts (AccountTransfer)
    // ---------------------------------------------------------------------

    private static RavenHttp PlayerDatabaseHttp() {
        if (!string.IsNullOrWhiteSpace(ConfigurationManager.GetValue("Database.PlayerDatabaseUrl", ""))) {
            throw new InvalidDataException("This server keeps its accounts in a database of its own, so accounts can't be moved from here.");
        }

        var port = ConfigurationManager.GetValue("Database.EmbeddedDatabasePort", 8080);

        return new RavenHttp(s_database, $"http://127.0.0.1:{port}", ConfigurationManager.GetValue("Database.PlayerDatabaseName", "Playerdata"));
    }

    // A player gets their own account; an admin may name any.
    private static async Task<(JsonObject, byte[])> ExportAsync(Session session, JsonObject header) {
        var username = !session.PlayerOnly && ((string) header["username"])?.Trim() is { Length: > 0 } named ? named : session.Username;
        var account = AccountCollection.GetAccountForCharacterList(username);
        if (account is null) {
            return (RemoteAdminWire.Error($"There is no account named {username} on this server."), null);
        }

        JsonObject file;
        try {
            file = await AccountTransfer.ExportAsync(PlayerDatabaseHttp(), account.Username, ConfigurationManager.GetValue("Remote Admin.ServerName", ""),
                CharactersOf(account.AccountId));
        }
        catch (AccountTransferException ex) {
            return (RemoteAdminWire.Error(ex.Message), null);
        }

        var online = OnlinePlayerCollection.GetOnlinePlayers().Any(p => p.AccountId == account.AccountId);
        var counts = (file["Counts"] as JsonObject ?? []).ToDictionary(c => c.Key, c => (int?) c.Value ?? 0);
        Logger.Information("Account download: {0} downloaded {1} from {2} ({3}).",
            Logger.Args(session.Username, account.Username, session.Address, AccountTransfer.Describe(counts)));
        var reply = RemoteAdminWire.Ok();
        reply["account"] = account.Username;
        reply["summary"] = AccountTransfer.Describe(counts);
        reply["online"] = online;

        return (reply, RemoteAdminWire.Gzip(AccountTransfer.ToBytes(file)));
    }

    // The wizards' names, schools and levels, for the person importing the file.
    private static JsonArray CharactersOf(ulong accountId) {
        var list = new JsonArray();
        try {
            using var session = Imlight.CoreLib.WizardData.Databases.PlayerDatabase.Instance.Store.OpenSession();
            foreach (var wizard in session.Query<Wizard>(collectionName: WizardCollection.CollectionName)
                         .Customize(q => q.WaitForNonStaleResults())
                         .Where(w => w.AccountId == accountId).ToList().Where(w => w is not null)) {
                string name;
                try {
                    name = wizard.PlayerNameBehavior?.GetWizardName() ?? "";
                }
                catch (Exception) {
                    name = "";
                }

                list.Add(new JsonObject {
                    ["Name"] = name,
                    ["School"] = wizard.MagicSchoolBehavior?.MagicSchool.ToString() ?? "",
                    ["Level"] = wizard.MagicSchoolBehavior?.Level ?? 0,
                });
            }
        }
        catch (Exception ex) {
            Logger.Warning("Account download: could not name the wizards: {0}", Logger.Args(ex.Message));
        }

        return list;
    }

    // An admin brings an account file into this server, as the host can on their own PC.
    private static async Task<JsonObject> ImportAsync(Session session, JsonObject header, byte[] body) {
        try {
            var file = AccountTransfer.Read(RemoteAdminWire.Gunzip(body));
            var username = ((string) header["username"])?.Trim() is { Length: > 0 } named ? named : file.Username;
            var password = (string) header["password"];
            await AccountTransfer.ImportAsync(PlayerDatabaseHttp(), file, username,
                string.IsNullOrEmpty(password) ? null : DatabaseUtilities.CreateHashedPassword(password));
            Logger.Information("Remote admin {0} imported the account {1} ({2}).", Logger.Args(session.Username, username, AccountTransfer.Describe(file.Counts)));
            var reply = RemoteAdminWire.Ok();
            reply["account"] = username;

            return reply;
        }
        catch (AccountTransferException ex) {
            return RemoteAdminWire.Error(ex.Message);
        }
    }

    private static async Task<JsonObject> DatabaseAsync(JsonObject header) {
        if (!string.IsNullOrWhiteSpace(ConfigurationManager.GetValue("Database.PlayerDatabaseUrl", ""))) {
            return RemoteAdminWire.Error("This server keeps its accounts in a database of its own. Manage them there.");
        }

        var port = ConfigurationManager.GetValue("Database.EmbeddedDatabasePort", 8080);
        var name = Uri.EscapeDataString(ConfigurationManager.GetValue("Database.PlayerDatabaseName", "Playerdata"));
        var request = (string) header["request"];
        var text = (string) header["body"] ?? "";
        HttpRequestMessage message;
        if (request == "query") {
            var query = JsonNode.Parse(text) as JsonObject;
            var rql = ((string) query?["Query"] ?? "").Trim();
            if (rql is not ("from Accounts" or "from Accounts where Username = $username")) {
                return RemoteAdminWire.Error("Remote Admin only reads accounts.");
            }

            message = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/databases/{name}/queries") {
                Content = new StringContent(new JsonObject { ["Query"] = rql, ["QueryParameters"] = query["QueryParameters"]?.DeepClone() }.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }
        else if (request == "put") {
            var id = (string) header["docId"] ?? "";
            var document = JsonNode.Parse(text) as JsonObject;
            if (!id.StartsWith("accounts/", StringComparison.OrdinalIgnoreCase)
                || (string) document?["@metadata"]?["@collection"] != "Accounts") {
                return RemoteAdminWire.Error("Remote Admin only changes accounts.");
            }

            message = new HttpRequestMessage(HttpMethod.Put, $"http://127.0.0.1:{port}/databases/{name}/docs?id={Uri.EscapeDataString(id)}") {
                Content = new StringContent(document.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }
        else {
            return RemoteAdminWire.Error("Remote Admin only reads and saves accounts.");
        }

        using (message) {
            using var response = await s_database.SendAsync(message);
            var reply = RemoteAdminWire.Ok();
            reply["status"] = (int) response.StatusCode;
            reply["body"] = await response.Content.ReadAsStringAsync();

            return reply;
        }
    }

    // ---------------------------------------------------------------------
    // What changed
    // ---------------------------------------------------------------------

    /// <summary>
    /// Every shared file with its size, time and hash, each change numbered, so Ahziael asks only for what changed since
    /// it last looked. The folders are looked at again at most every second and a half, only while someone asks.
    /// </summary>
    private sealed class FileIndex {

        private sealed class Entry {

            internal long Size;
            internal long Ticks;
            internal string Hash;
            internal long Seq;
            internal bool Gone;

        }

        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _scannedUtc = [];
        private long _seq;

        internal void Forget(string path) {
            lock (_entries) {
                _scannedUtc.Remove(path.Split('/')[0]);
                if (_entries.TryGetValue(path, out var entry)) {
                    entry.Ticks = -1;
                }
            }
        }

        internal JsonObject Changes(Dictionary<string, string> roots, string epoch, long since, string current) {
            lock (_entries) {
                Scan(roots);
                var full = epoch != current || since <= 0 || since > _seq;
                var files = new JsonArray();
                foreach (var (path, entry) in _entries) {
                    if (full ? entry.Gone : entry.Seq <= since) {
                        continue;
                    }

                    files.Add(entry.Gone
                        ? new JsonObject { ["p"] = path, ["gone"] = true }
                        : new JsonObject { ["p"] = path, ["h"] = entry.Hash, ["s"] = entry.Size });
                }

                var reply = RemoteAdminWire.Ok();
                reply["epoch"] = current;
                reply["seq"] = _seq;
                reply["full"] = full;
                reply["files"] = files;

                return reply;
            }
        }

        // The additions folder holds most of the files and changes least, so it is looked at less often.
        private static TimeSpan Every(string root) => TimeSpan.FromSeconds(root == RemoteAdminPaths.EXTRA ? 6 : 1.5);

        private void Scan(Dictionary<string, string> roots) {
            foreach (var (root, folder) in roots) {
                if (_scannedUtc.TryGetValue(root, out var last) && DateTime.UtcNow - last < Every(root)) {
                    continue;
                }

                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (path, file) in RemoteAdminPaths.List(root, folder)) {
                    if (!seen.Add(path)) {
                        continue;
                    }

                    try {
                        var size = file.Length;
                        var ticks = file.LastWriteTimeUtc.Ticks;
                        if (_entries.TryGetValue(path, out var entry) && !entry.Gone && entry.Size == size && entry.Ticks == ticks) {
                            continue;
                        }

                        var hash = RemoteAdminPaths.IsLog(path) ? $"{size}-{ticks}" : RemoteAdminWire.HashOf(file.FullName);
                        if (entry is null) {
                            entry = new Entry();
                            _entries[path] = entry;
                        }
                        else if (!entry.Gone && entry.Hash == hash) {
                            (entry.Size, entry.Ticks) = (size, ticks);
                            continue;
                        }

                        (entry.Size, entry.Ticks, entry.Hash, entry.Gone, entry.Seq) = (size, ticks, hash, false, ++_seq);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                        // Being written; looked at again next time.
                    }
                }

                foreach (var (path, entry) in _entries) {
                    if (!entry.Gone && path.StartsWith(root + "/", StringComparison.Ordinal) && !seen.Contains(path)) {
                        (entry.Gone, entry.Seq) = (true, ++_seq);
                    }
                }

                _scannedUtc[root] = DateTime.UtcNow;
            }
        }

    }

}
