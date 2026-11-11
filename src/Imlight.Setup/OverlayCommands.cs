/*
 * Imlight
 * Copyright (C) 2026 Revive101
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
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Imlight.RemoteAdmin;

namespace Imlight.Setup;

/// <summary>
/// A player's own cheats and commands, sent from Ahziael (and its overlay buttons) to the server they play on
/// (Niduus, 2026-11-11: "clients that are not hosting a server, but connected to one could communicate their
/// cheats/commands to the hosting Ahziael instance"). It goes over the server's Remote Admin port, not game chat, so
/// there is no length limit and nothing shows in chat. The server is the one the overlay links to (the one the game
/// was last started on with Play), and the server checks everything again (PlayerCommands on the server).
///
/// The player signs in once with their game username and password (<see cref="SignInAsync"/>); the server gives back a
/// key (a name and a secret), kept in player-keys.json in the install folder. The password is never kept. Each command
/// is then signed with the secret (RemoteAdminWire.SignCommand) and carries a number that only goes up, so it can't be
/// faked, changed or sent twice. The server's certificate is trusted by its fingerprint, as with Account Transfer.
/// </summary>
internal static class OverlayCommands {

    /// <summary>One of the player's cheats, as the server lists it.</summary>
    internal sealed record CheatInfo(int Number, string Name, bool On, bool CanSwitch, bool CanUse);

    /// <summary>What the server answered: whether it worked, what the game said (or why not), and the cheats when asked.</summary>
    internal sealed record Result(bool Ok, string Text, IReadOnlyList<CheatInfo> Cheats);

    private sealed record SavedKey(string Host, int Port, string Account, string Key, string Secret, string Fingerprint);

    private const string FILE = "player-keys.json";
    private const string LINK_FILE = "overlay-link.json";

    private static readonly SemaphoreSlim s_gate = new(1, 1);
    private static long s_last;

    private static string Folder => OverlayHost.InstallFolder is { Length: > 0 } folder ? folder : AppContext.BaseDirectory;

    /// <summary>The server the overlay links to: the one the game was last started on, else this PC's own.</summary>
    internal static (string Host, int Port) Server {
        get {
            try {
                var path = Path.Combine(Folder, LINK_FILE);
                if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is { } json
                    && (string?) json["host"] is { Length: > 0 and < 256 } host) {
                    var port = (int?) json["port"] ?? RemoteAdminWire.DEFAULT_PORT;

                    return (host.Trim(), port is > 0 and < 65536 ? port : RemoteAdminWire.DEFAULT_PORT);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) {
            }

            return ("127.0.0.1", GameClient.OverlayPort(Folder, "127.0.0.1"));
        }
    }

    /// <summary>Whether the player has signed in to the server the overlay links to now.</summary>
    internal static bool SignedIn => Find(Server) is not null;

    /// <summary>The account signed in to the server the overlay links to now, or "".</summary>
    internal static string SignedInAs => Find(Server)?.Account ?? "";

    /// <summary>
    /// Runs one of the player's own commands on the server the overlay links to, as their wizard there:
    /// kind "command" (value: a chat command, the dot optional), "cheat" (value: "on NAME", "off NAME" or "use NAME"),
    /// or "cheats" (the list, in Result.Cheats). Never throws: a problem comes back as Ok false with words to show.
    /// </summary>
    internal static async Task<Result> SendAsync(string kind, string value, CancellationToken stop = default) {
        var server = Server;
        var key = Find(server);
        if (key is null) {
            return Fail("Sign in to this server first: in Ahziael, Play > Overlay > My cheats on this server.");
        }

        await s_gate.WaitAsync(stop);
        try {
            for (var attempt = 0; ; attempt++) {
                // A number that only goes up, even across Ahziael and the overlay on its own (both use the clock).
                var number = Math.Max(s_last + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                s_last = number;
                var at = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                value = (value ?? "").ReplaceLineEndings(" ").Trim();
                kind = (kind ?? "").Trim().ToLowerInvariant();
                var header = new JsonObject {
                    ["op"] = "pcmd", ["key"] = key.Key, ["n"] = number, ["at"] = at, ["kind"] = kind, ["value"] = value,
                    ["sig"] = RemoteAdminWire.SignCommand(Convert.FromBase64String(key.Secret), key.Key, number, at, kind, value),
                };
                JsonObject reply;
                try {
                    reply = await AskAsync(key.Host, key.Port, key.Fingerprint, header, stop);
                }
                catch (FingerprintException) {
                    Remove(key);

                    return Fail("This server's fingerprint changed since you signed in, so nothing was sent. Sign in again and check the fingerprint with the host.");
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or AuthenticationException
                                               or InvalidDataException or ObjectDisposedException or JsonException) {
                    return Fail($"Could not reach {key.Host} on port {key.Port}. Is the server running, and is that port open for you?");
                }

                if (reply["ok"]?.GetValue<bool>() == true) {
                    return new Result(true, (string?) reply["text"] ?? "", Cheats(reply["cheats"]));
                }

                var error = (string?) reply["error"] ?? "The server refused.";
                // Sent at the same moment as one from the overlay on its own: once more with a new number.
                if (attempt == 0 && error.Contains("already sent", StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                if (reply["signIn"]?.GetValue<bool>() == true) {
                    Remove(key);
                }

                return Fail(error);
            }
        }
        finally {
            s_gate.Release();
        }
    }

    /// <summary>
    /// Signs in to a server with the player's game username and password and keeps the key it gives. A server seen for
    /// the first time shows its fingerprint to <paramref name="trust"/> before the password is sent.
    /// </summary>
    internal static async Task<string> SignInAsync(string host, int port, string username, string password, Func<string, Task<bool>> trust) {
        host = host.Trim();
        var known = Load().FirstOrDefault(k => Same(k, host, port))?.Fingerprint;
        try {
            var (tcp, tls, fingerprint) = await OpenAsync(host, port, known);
            using var _ = tcp;
            await using var __ = tls;
            if (known is null && !await trust(RemoteAdminWire.ShortFingerprint(fingerprint))) {
                throw new PanelException("Not signed in. Check the fingerprint with the host first.");
            }

            var hello = await ExchangeAsync(tls, new JsonObject { ["op"] = "hello", ["id"] = 1 }, CancellationToken.None);
            if ((int?) hello["protocol"] != RemoteAdminWire.PROTOCOL) {
                throw new PanelException("Ahziael and that server are different versions. Update Ahziael, and ask the host to update theirs.");
            }

            var reply = await ExchangeAsync(tls, new JsonObject {
                ["op"] = "login", ["id"] = 2, ["username"] = username.Trim(), ["password"] = password,
                ["protocol"] = RemoteAdminWire.PROTOCOL, ["purpose"] = "commands",
            }, CancellationToken.None);
            if (reply["ok"]?.GetValue<bool>() != true) {
                throw new PanelException((string?) reply["error"] ?? "The server refused.");
            }

            if ((string?) reply["key"] is not { Length: > 0 } key || (string?) reply["secret"] is not { Length: > 0 } secret) {
                throw new PanelException("That server doesn't know about cheats from Ahziael yet. Ask the host to update their Ahziael.");
            }

            var account = (string?) reply["account"] ?? username.Trim();
            Save(new SavedKey(host, port, account, key, secret, fingerprint));

            return account;
        }
        catch (FingerprintException changed) {
            throw new PanelException($"{host} has a different fingerprint than last time ({changed.Message}). Nothing was sent. " +
                "If the host moved their server or installed it again, click \"Sign out\" here, then sign in again and check the fingerprint with them.");
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or AuthenticationException
                                       or InvalidDataException or ObjectDisposedException or JsonException) {
            throw new PanelException($"Could not reach {host} on port {port}. Check the address, and ask the host to update Ahziael and " +
                "click \"Let friends connect\" once more on their Play tab.");
        }
    }

    /// <summary>Forgets the sign-in for the server the overlay links to now.</summary>
    internal static void SignOut() {
        if (Find(Server) is { } key) {
            Remove(key);
        }
    }

    // ---------------------------------------------------------------------
    // The connection
    // ---------------------------------------------------------------------

    private sealed class FingerprintException(string seen) : Exception(seen);

    private static Result Fail(string text) => new(false, text, []);

    private static List<CheatInfo> Cheats(JsonNode? list) {
        var cheats = new List<CheatInfo>();
        if (list is not JsonArray array) {
            return cheats;
        }

        foreach (var item in array.OfType<JsonObject>()) {
            cheats.Add(new CheatInfo((int?) item["number"] ?? 0, (string?) item["name"] ?? "", item["on"]?.GetValue<bool>() == true,
                item["canSwitch"]?.GetValue<bool>() == true, item["canUse"]?.GetValue<bool>() == true));
        }

        return cheats;
    }

    private static async Task<JsonObject> AskAsync(string host, int port, string fingerprint, JsonObject header, CancellationToken stop) {
        var (tcp, tls, _) = await OpenAsync(host, port, fingerprint);
        using var _ = tcp;
        await using var __ = tls;
        header["id"] = 1;

        return await ExchangeAsync(tls, header, stop);
    }

    private static async Task<JsonObject> ExchangeAsync(SslStream tls, JsonObject header, CancellationToken stop) {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop);
        wait.CancelAfter(TimeSpan.FromSeconds(20));
        await RemoteAdminWire.WriteAsync(tls, header, null, wait.Token);
        var (reply, _) = await RemoteAdminWire.ReadAsync(tls, 1 << 20, 0, wait.Token);

        return reply;
    }

    // An encrypted connection to the server; a known server must show the same fingerprint.
    private static async Task<(TcpClient Tcp, SslStream Tls, string Fingerprint)> OpenAsync(string host, int port, string? known) {
        var tcp = new TcpClient { NoDelay = true };
        try {
            using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(8))) {
                await tcp.ConnectAsync(host, port, wait.Token);
            }

            string? seen = null;
            var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) => {
                seen = certificate is null ? null : RemoteAdminWire.Fingerprint(certificate.GetRawCertData());

                // The server's certificate is its own, not from an authority: it is trusted by its fingerprint.
                return seen is not null && (known is null || seen == known);
            });
            try {
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
                    TargetHost = "Imlight Remote Admin", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, wait.Token);
            }
            catch (AuthenticationException) when (seen is not null && known is not null && seen != known) {
                await tls.DisposeAsync();
                throw new FingerprintException(RemoteAdminWire.ShortFingerprint(seen));
            }

            return (tcp, tls, seen!);
        }
        catch {
            tcp.Dispose();
            throw;
        }
    }

    // ---------------------------------------------------------------------
    // The saved keys
    // ---------------------------------------------------------------------

    private static bool Same(SavedKey key, string host, int port) => key.Host.Equals(host.Trim(), StringComparison.OrdinalIgnoreCase) && key.Port == port;

    private static SavedKey? Find((string Host, int Port) server) => Load().FirstOrDefault(k => Same(k, server.Host, server.Port));

    private static List<SavedKey> Load() {
        try {
            var path = Path.Combine(Folder, FILE);

            return File.Exists(path) ? JsonSerializer.Deserialize<List<SavedKey>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            return [];
        }
    }

    private static void Save(SavedKey key) {
        var all = Load().Where(k => !Same(k, key.Host, key.Port)).ToList();
        all.Add(key);
        Write(all);
    }

    private static void Remove(SavedKey key) => Write([.. Load().Where(k => !Same(k, key.Host, key.Port))]);

    private static void Write(List<SavedKey> all) {
        try {
            var path = Path.Combine(Folder, FILE);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(all.TakeLast(20).ToList(), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        }
    }

}
