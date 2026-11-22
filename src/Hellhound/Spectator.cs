using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json.Nodes;

namespace Hellhound;

/// <summary>
/// The Watch page's feed (Niduus, 2026-11-22: "a build in Minimap along with battle hand for Hellhound so I am able to watch
/// them"). Hellhound links to the server the same way Ahziael's overlay does (TLS, op "overlay" on the game's port, the
/// Remote Admin wire: big-endian header length, JSON header, body length, body), and asks with op "ultimus", who
/// "anyone:&lt;character id&gt;": Spectate Anyone, which the server answers only on its own PC. The answer is the wizard's map
/// (with its area's ground shapes when the page doesn't have them yet), quests, and in a battle its hand, pips, foes and
/// casts. Read only: nothing here changes the game.
/// </summary>
internal sealed class Spectator(ServerFiles files) {

    private const int MAX_ANSWER = 8 << 20;
    private readonly SemaphoreSlim _one = new(1, 1);
    private TcpClient? _tcp;
    private SslStream? _tls;
    private DateTime _beatBy = DateTime.MinValue;

    /// <summary>One look at a wizard ("" the first one: Ultimus when he plays), as JSON for the page.</summary>
    internal async Task<string> WatchAsync(string who, string groundKey) {
        await _one.WaitAsync();
        try {
            for (var attempt = 0; attempt < 2; attempt++) {
                try {
                    var tls = await LinkAsync();
                    var ask = new JsonObject {
                        ["op"] = "ultimus", ["who"] = "anyone:" + who, ["have"] = "none", ["groundKey"] = groundKey, ["shapes"] = true, ["reach"] = 0,
                    };
                    var answer = await AskAsync(tls, ask);

                    return answer.ToJsonString();
                }
                catch (Exception e) when (e is IOException or SocketException or AuthenticationException or InvalidDataException or OperationCanceledException) {
                    Drop();
                    if (attempt == 1) {
                        return Error(files.ServerRunning() ? "Hellhound can't reach the server just now: " + e.Message : "The server isn't running.");
                    }
                }
            }

            return Error("Hellhound can't reach the server just now.");
        }
        finally {
            _one.Release();
        }
    }

    private static string Error(string text) => new JsonObject { ["ok"] = false, ["error"] = text }.ToJsonString();

    // The link, made when needed and kept; a beat before the server's wait (4 beats of 5 s) runs out.
    private async Task<SslStream> LinkAsync() {
        if (_tls is { } open) {
            if (DateTime.UtcNow >= _beatBy) {
                await AskAsync(open, new JsonObject { ["op"] = "beat", ["windows"] = 0, ["have"] = "none" });
            }

            return open;
        }

        Exception? last = null;
        foreach (var port in Ports()) {
            var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            try {
                using (var connect = new CancellationTokenSource(TimeSpan.FromSeconds(4))) {
                    await tcp.ConnectAsync(IPAddress.Loopback, port, connect.Token);
                }

                // The server makes its own certificate; nothing secret goes over this link, and it never leaves this PC.
                var tls = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
                using (var handshake = new CancellationTokenSource(TimeSpan.FromSeconds(8))) {
                    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
                        TargetHost = "Imlight Remote Admin", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    }, handshake.Token);
                }

                var hello = await AskAsync(tls, new JsonObject { ["op"] = "overlay", ["windows"] = 0, ["version"] = "Hellhound", ["have"] = "none" });
                if (hello["ok"]?.GetValue<bool>() != true) {
                    throw new IOException((string?) hello["error"] ?? "the server said no");
                }

                (_tcp, _tls) = (tcp, tls);

                return tls;
            }
            catch (Exception e) when (e is IOException or SocketException or AuthenticationException or InvalidDataException or OperationCanceledException) {
                last = e;
                tcp.Dispose();
            }
        }

        throw last ?? new IOException("no port to try");
    }

    // The game's own port first (where every overlay links), then the login port, then the Remote Admin port.
    private IEnumerable<int> Ports() {
        var ini = Ini.Read(files.ConfigFile);
        var ports = new List<int>();
        foreach (var (section, key, fallback) in new[] {
                     ("Game Server", "GameServerPort", 12333), ("Login Server", "LoginServerPort", 12000), ("Remote Admin", "Port", 12410) }) {
            var port = int.TryParse(ini.Get(section, key), out var set) && set is > 0 and < 65536 ? set : fallback;
            if (!ports.Contains(port)) {
                ports.Add(port);
            }
        }

        return ports;
    }

    private async Task<JsonObject> AskAsync(SslStream tls, JsonObject header) {
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var head = Encoding.UTF8.GetBytes(header.ToJsonString());
        var frame = new byte[8 + head.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, head.Length);
        head.CopyTo(frame, 4);
        await tls.WriteAsync(frame, wait.Token);
        await tls.FlushAsync(wait.Token);

        var length = new byte[4];
        await tls.ReadExactlyAsync(length, wait.Token);
        var headLength = BinaryPrimitives.ReadInt32BigEndian(length);
        if (headLength <= 0 || headLength > MAX_ANSWER) {
            throw new InvalidDataException("The answer is too big.");
        }

        var answer = new byte[headLength];
        await tls.ReadExactlyAsync(answer, wait.Token);
        await tls.ReadExactlyAsync(length, wait.Token);
        var bodyLength = BinaryPrimitives.ReadInt32BigEndian(length);
        if (bodyLength is < 0 or > MAX_ANSWER) {
            throw new InvalidDataException("The answer is too big.");
        }

        if (bodyLength > 0) {
            await tls.ReadExactlyAsync(new byte[bodyLength], wait.Token);
        }

        _beatBy = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        return JsonNode.Parse(answer) as JsonObject ?? throw new InvalidDataException("The answer is not JSON.");
    }

    private void Drop() {
        try {
            _tls?.Dispose();
            _tcp?.Dispose();
        }
        catch (Exception) {
        }

        (_tcp, _tls) = (null, null);
    }
}
