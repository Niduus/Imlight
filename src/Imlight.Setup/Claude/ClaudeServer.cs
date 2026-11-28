#if !HELLHOUND
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Imlight.Setup;

/// <summary>
/// Ahziael's Claude connector (MCP, the Model Context Protocol; Niduus, 2026-11-24, built like Blood Moon's): Claude talks
/// to it on this PC only (127.0.0.1), over MCP's "Streamable HTTP" (one JSON-RPC message POSTed to /mcp, one JSON answer
/// back). Claude desktop reaches it through "Imlight.exe --mcp" (<see cref="ClaudeBridge"/>); Claude Code can use the
/// address directly. Web pages are turned away (a browser always says which page it comes from), so only programs on this
/// PC get in. Off until Server > Ahziael > Claude turns it on.
/// </summary>
internal sealed class ClaudeServer : IDisposable {

        private const int MOST_BODY = 4 * 1024 * 1024;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<string, JsonObject, Task<JsonObject>> _call;
    private readonly Action<string> _said;

    public int Port { get; }
    public string Address => $"http://127.0.0.1:{Port}/mcp";

    /// <param name="call">Runs a tool by name with its arguments; returns the MCP tool result.</param>
    /// <param name="said">Hears what Claude did, in words, for the card on the Ahziael tab (kept in memory only).</param>
    public ClaudeServer(int port, Func<string, JsonObject, Task<JsonObject>> call, Action<string> said) {
        Port = port;
        _call = call;
        _said = said;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop() {
        while (!_stop.IsCancellationRequested) {
            TcpClient client;
            try {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            } catch (Exception) {
                return;
            }

            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client) {
        using var _ = client;
        try {
            client.ReceiveTimeout = 30000;
            var stream = client.GetStream();
            while (await ReadRequest(stream) is { } req) {
                var (status, body, type) = await Answer(req);
                await WriteResponse(stream, status, body, type);
                if (!req.KeepAlive) {
                    break;
                }
            }
        } catch (Exception) {
            // The caller went away.
        }
    }

    private sealed record Request(string Method, string Path, Dictionary<string, string> Headers, byte[] Body) {
        public bool KeepAlive => !Headers.TryGetValue("connection", out var c) || !c.Equals("close", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<Request?> ReadRequest(NetworkStream stream) {
        var head = new List<byte>();
        var one = new byte[1];
        while (true) {
            if (await stream.ReadAsync(one) == 0) {
                return null;
            }

            head.Add(one[0]);
            if (head.Count >= 4 && head[^1] == '\n' && head[^2] == '\r' && head[^3] == '\n' && head[^4] == '\r') {
                break;
            }

            if (head.Count > 32768) {
                return null;
            }
        }

        var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1)) {
            var colon = line.IndexOf(':');
            if (colon > 0) {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        var length = headers.TryGetValue("content-length", out var l) && int.TryParse(l, out var n) ? n : 0;
        if (length < 0 || length > MOST_BODY) {
            return null;
        }

        var body = new byte[length];
        var got = 0;
        while (got < length) {
            var r = await stream.ReadAsync(body.AsMemory(got));
            if (r == 0) {
                return null;
            }

            got += r;
        }

        return new Request(first[0].ToUpperInvariant(), first[1].Split('?')[0], headers, body);
    }

    private static async Task WriteResponse(NetworkStream stream, int status, string body, string type) {
        var bytes = Encoding.UTF8.GetBytes(body);
        var reason = status switch { 200 => "OK", 202 => "Accepted", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Error" };
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\n"
                   + (status == 405 ? "Allow: POST\r\n" : "") + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    /// <summary>Only programs on this PC: a request from a web page carries its Origin, and only a local one is let in.</summary>
    private static bool LocalOrigin(Request req) {
        if (!req.Headers.TryGetValue("origin", out var origin) || origin.Length == 0 || origin == "null") {
            return !req.Headers.ContainsKey("origin");
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var u) && (u.Host is "127.0.0.1" or "localhost" or "[::1]");
    }

    private async Task<(int Status, string Body, string Type)> Answer(Request req) {
        const string JSON = "application/json";
        if (!LocalOrigin(req)) {
            _said("Turned away a web page that tried to use the connector.");
            return (403, "{\"error\":\"Only programs on this PC may use Ahziael.\"}", JSON);
        }

        if (req.Path is not ("/mcp" or "/")) {
            return (404, "{\"error\":\"Ahziael's MCP address is /mcp\"}", JSON);
        }

        if (req.Method != "POST") {
            // No server-sent stream: every answer comes back on its own POST.
            return (405, "{\"error\":\"POST JSON-RPC messages to /mcp\"}", JSON);
        }

        JsonNode? message;
        try {
            message = JsonNode.Parse(req.Body);
        } catch (JsonException) {
            return (400, ClaudeTools.Fail(null, -32700, "Not JSON").ToJsonString(), JSON);
        }

        if (message is JsonArray batch) {
            var answers = new JsonArray();
            foreach (var m in batch) {
                if (await Handle(m) is { } a) {
                    answers.Add(a);
                }
            }

            return answers.Count == 0 ? (202, "", JSON) : (200, answers.ToJsonString(), JSON);
        }

        return await Handle(message) is { } answer ? (200, answer.ToJsonString(), JSON) : (202, "", JSON);
    }

    /// <summary>One JSON-RPC message; null for a notification (nothing to answer).</summary>
    public async Task<JsonObject?> Handle(JsonNode? message) {
        var id = message?["id"];
        var method = message?["method"]?.GetValue<string>() ?? "";
        if (id is null) {
            return null;
        }

        try {
            switch (method) {
                case "initialize":
                    _said($"Claude connected ({AppLogs.Scrub(message?["params"]?["clientInfo"]?["name"]?.GetValue<string>() ?? "a program")}).");
                    return ClaudeTools.Reply(id, ClaudeTools.InitializeResult(message));
                case "ping":
                    return ClaudeTools.Reply(id, new JsonObject());
                case "tools/list":
                    return ClaudeTools.Reply(id, ClaudeTools.ListResult());
                case "tools/call": {
                    var name = message?["params"]?["name"]?.GetValue<string>() ?? "";
                    var args = message?["params"]?["arguments"] as JsonObject ?? new JsonObject();
                    if (ClaudeTools.All.All(t => t.Name != name)) {
                        return ClaudeTools.Fail(id, -32602, "No tool named " + name);
                    }

                    _said(AppLogs.Scrub($"Claude used {name} {args.ToJsonString()}"));
                    JsonObject result;
                    try {
                        result = await _call(name, (JsonObject) args.DeepClone());
                    } catch (Exception ex) {
                        AppLogs.Error("Claude's " + name + " failed", ex);
                        result = ClaudeTools.Text($"{name} failed: {ex.Message}", error: true);
                    }

                    return ClaudeTools.Reply(id, result);
                }
                default:
                    return ClaudeTools.Fail(id, -32601, "Ahziael doesn't know " + method);
            }
        } catch (Exception ex) {
            return ClaudeTools.Fail(id, -32603, ex.Message);
        }
    }

    public void Dispose() {
        _stop.Cancel();
        try {
            _listener.Stop();
        } catch (Exception) {
            // Already stopped.
        }

    }
}
#endif
