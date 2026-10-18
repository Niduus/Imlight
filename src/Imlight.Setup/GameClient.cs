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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Imlight.Setup;

/// <summary>
/// A private server the person saved in the app, to play on with their own Wizard101.
/// </summary>
internal sealed record SavedServer(string Name, string Host, int Port);

/// <summary>
/// Starts the person's Wizard101 connected to a private server (this computer's, or one they saved),
/// never to the live game, and reads the game's own log.
/// </summary>
internal static class GameClient {

    private const string SERVERS_FILE = "servers.json";
    private const int LOG_TAIL_BYTES = 256 * 1024;
    private const int LOG_MAX_LINES = 1500;

    /// <summary>
    /// Domains of the live game. Connecting to any of them, or to an address they resolve to, is refused.
    /// </summary>
    private static readonly string[] LIVE_DOMAINS = ["wizard101.com", "kingsisle.com", "gameforge.com"];

    private static readonly string[] LIVE_HOSTS = [
        "login.us.wizard101.com", "patch.us.wizard101.com", "www.wizard101.com", "wizard101.com",
        "login.eu.wizard101.com", "patch.eu.wizard101.com",
    ];

    internal static string? ClientExe(string installFolder) {
        var game = InstallInfo.Load(installFolder).GameFolder;
        var client = game is null ? null : Path.Combine(game, "Bin", "WizardGraphicalClient.exe");

        return client is not null && File.Exists(client) ? client : null;
    }

    /// <summary>
    /// Starts Wizard101 connected to a private server. Throws a <see cref="PanelException"/> with the reason when it cannot.
    /// </summary>
    internal static async Task LaunchAsync(string installFolder, string host, int port) {
        var client = ClientExe(installFolder)
            ?? throw new PanelException("Wizard101 could not be found. Click \"Update Imlight\" on the Tools tab of the app once, then try again.");
        if (await IsLiveRealmAsync(host)) {
            throw new PanelException("That is the live Wizard101 game. This app only plays on private servers.");
        }

        var startInfo = new ProcessStartInfo(client) {
            WorkingDirectory = Path.GetDirectoryName(client)!,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-L");
        startInfo.ArgumentList.Add(host);
        startInfo.ArgumentList.Add(port.ToString());
        Process.Start(startInfo)?.Dispose();
    }

    /// <summary>
    /// True when the host is the live game: one of its domains, or an address one of its servers uses.
    /// </summary>
    internal static async Task<bool> IsLiveRealmAsync(string host) {
        var name = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (LIVE_DOMAINS.Any(d => name == d || name.EndsWith("." + d, StringComparison.Ordinal))) {
            return true;
        }

        var addresses = await ResolveAsync(name);
        if (addresses.Length == 0 || addresses.All(IsPrivate)) {
            return false;
        }

        foreach (var live in LIVE_HOSTS) {
            if ((await ResolveAsync(live)).Any(addresses.Contains)) {
                return true;
            }
        }

        return false;
    }

    private static async Task<IPAddress[]> ResolveAsync(string host) {
        if (IPAddress.TryParse(host, out var address)) {
            return [address];
        }

        try {
            return await Dns.GetHostAddressesAsync(host).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or ArgumentException) {
            return [];
        }
    }

    private static bool IsPrivate(IPAddress address) {
        if (IPAddress.IsLoopback(address)) {
            return true;
        }

        if (address.IsIPv4MappedToIPv6) {
            address = address.MapToIPv4();
        }

        var b = address.GetAddressBytes();

        return address.AddressFamily == AddressFamily.InterNetwork
            ? b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] is >= 64 and <= 127)
            : address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
    }

    // ---------------------------------------------------------------------
    // Saved servers
    // ---------------------------------------------------------------------

    internal static List<SavedServer> LoadServers(string installFolder) {
        var path = Path.Combine(installFolder, SERVERS_FILE);
        try {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<SavedServer>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (JsonException) {
            return [];
        }
    }

    private static void SaveServers(string installFolder, List<SavedServer> servers)
        => File.WriteAllText(Path.Combine(installFolder, SERVERS_FILE),
            JsonSerializer.Serialize(servers, new JsonSerializerOptions { WriteIndented = true }));

    internal static async Task<SavedServer> AddServerAsync(string installFolder, string name, string host, int port) {
        name = name.Trim();
        host = host.Trim();
        if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) {
            host = new Uri(host).Host;
        }

        if (host.Length == 0 || host.Any(char.IsWhiteSpace) || Uri.CheckHostName(host) == UriHostNameType.Unknown) {
            throw new PanelException("Enter the server's address, such as play.example.com or 203.0.113.5.");
        }

        if (port is < 1 or > 65535) {
            throw new PanelException("Enter the server's login port, a number from 1 to 65535.");
        }

        if (await IsLiveRealmAsync(host)) {
            throw new PanelException("That is the live Wizard101 game. This app only plays on private servers.");
        }

        if (await IsThisComputerAsync(host) && ReservedPorts(installFolder).TryGetValue(port, out var use)) {
            throw new PanelException($"Port {port} on this computer is {use}. Give the other server a different login port.");
        }

        var servers = LoadServers(installFolder);
        if (name.Length == 0) {
            name = $"{host}:{port}";
        }

        if (servers.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) {
            throw new PanelException($"There is already a server named {name}.");
        }

        var server = new SavedServer(name, host, port);
        servers.Add(server);
        SaveServers(installFolder, servers);

        return server;
    }

    internal static void RemoveServer(string installFolder, string name) {
        var servers = LoadServers(installFolder);
        if (servers.RemoveAll(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == 0) {
            throw new PanelException($"There is no saved server named {name}.");
        }

        SaveServers(installFolder, servers);
    }

    /// <summary>
    /// Ports on this computer that Imlight and its game files need, with what uses each.
    /// The patch files port is also the one Aurorium uses when it runs here.
    /// </summary>
    internal static Dictionary<int, string> ReservedPorts(string installFolder) {
        var config = InstalledCommands.ConfigFile(installFolder);
        var ports = new Dictionary<int, string>();

        void Add(string section, string key, int fallback, string use) {
            var port = int.TryParse(IniFile.Get(config, section, key), out var parsed) ? parsed : fallback;
            ports.TryAdd(port, use);
        }

        Add("Login Server", "LoginServerPort", 12000, "your own Imlight server's login port (use Play on my server for it)");
        Add("Game Server", "GameServerPort", 12333, "used by your Imlight game server");
        Add("Patch Server", "PatchServerPort", 12500, "used by your Imlight patch server");
        Add("Database", "EmbeddedDatabasePort", 8080, "used by your Imlight player database");
        var patchUrl = IniFile.Get(config, "Patch Server", "PatchServerInternalUrl");
        ports.TryAdd(Uri.TryCreate(patchUrl, UriKind.Absolute, out var uri) ? uri.Port : 12369, "used for game files (Imlight or Aurorium)");
        return ports;
    }

    private static async Task<bool> IsThisComputerAsync(string host) {
        var addresses = await ResolveAsync(host);
        if (addresses.Any(IPAddress.IsLoopback) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        var own = NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(i => i.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .ToHashSet();

        return addresses.Any(own.Contains);
    }

    // ---------------------------------------------------------------------
    // Client log
    // ---------------------------------------------------------------------

    /// <summary>
    /// The end of the newest log Wizard101 wrote, so it can be read and copied outside the game.
    /// </summary>
    internal sealed record ClientLog(string? File, string Text, string? Reason);

    internal static ClientLog ReadClientLog(string installFolder) {
        var game = InstallInfo.Load(installFolder).GameFolder;
        if (game is null || !Directory.Exists(game)) {
            return new ClientLog(null, "", "Wizard101 could not be found. Click \"Update Imlight\" on the Tools tab of the app once.");
        }

        var file = new[] { Path.Combine(game, "Bin", "logs"), Path.Combine(game, "Bin"), Path.Combine(game, "logs"), game }
            .Where(Directory.Exists)
            .SelectMany(folder => new DirectoryInfo(folder).EnumerateFiles())
            .Where(f => f.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase)
                || (f.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) && f.Name.Contains("log", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (file is null) {
            return new ClientLog(null, "", "Wizard101 has not written a log yet. Play once, then look again.");
        }

        string text;
        using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
            var start = Math.Max(0, stream.Length - LOG_TAIL_BYTES);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
            if (start > 0) {
                text = text[(text.IndexOf('\n') + 1)..];
            }
        }

        var lines = text.ReplaceLineEndings("\n").Split('\n');
        if (lines.Length > LOG_MAX_LINES) {
            text = string.Join("\n", lines[^LOG_MAX_LINES..]);
        }

        return new ClientLog(file.FullName, text, null);
    }

}
