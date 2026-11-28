#if !HELLHOUND
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace Imlight.Setup;

/// <summary>
/// "Imlight.exe --mcp": the way Claude desktop starts Ahziael's connector. It opens no window; it passes each MCP message
/// Claude writes (one JSON line on standard input) to the Ahziael that is open on this PC, and writes the answer back on
/// standard output. While Ahziael is closed (or its Claude connector is off) Claude still sees the tools, and each one
/// answers how to turn it on. It writes nothing to disk.
/// </summary>
internal static class ClaudeBridge {

    internal static int Run(string installFolder) {
        var port = ClaudeConnectorSettings.Load(installFolder).UsablePort;
        var address = $"http://127.0.0.1:{port}/mcp";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        while (input.ReadLine() is { } line) {
            if (line.Trim().Length == 0) {
                continue;
            }

            JsonNode? message;
            try {
                message = JsonNode.Parse(line);
            } catch (Exception) {
                output.WriteLine(ClaudeTools.Fail(null, -32700, "Not JSON").ToJsonString());
                continue;
            }

            string? answer;
            try {
                using var content = new StringContent(line, Encoding.UTF8, "application/json");
                using var request = new HttpRequestMessage(HttpMethod.Post, address) { Content = content };
                request.Headers.Accept.ParseAdd("application/json");
                request.Headers.Accept.ParseAdd("text/event-stream");
                using var response = http.Send(request);
                answer = (int) response.StatusCode == 202 ? null : response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            } catch (Exception) {
                answer = Closed(message)?.ToJsonString();
            }

            if (!string.IsNullOrWhiteSpace(answer)) {
                output.WriteLine(answer.Replace("\r", "").Replace("\n", ""));
            }
        }

        return 0;
    }

    /// <summary>What Claude hears while Ahziael isn't listening.</summary>
    private static JsonObject? Closed(JsonNode? message) {
        var id = message?["id"];
        if (id is null) {
            return null;
        }

        return message?["method"]?.GetValue<string>() switch {
            "initialize" => ClaudeTools.Reply(id, ClaudeTools.InitializeResult(message)),
            "ping" => ClaudeTools.Reply(id, new JsonObject()),
            "tools/list" => ClaudeTools.Reply(id, ClaudeTools.ListResult()),
            "tools/call" => ClaudeTools.Reply(id, ClaudeTools.Text(
                "Ahziael isn't open, or its Claude connector is off. Open Ahziael, then turn on Server > Ahziael > Claude > \"Let Claude use Ahziael\", and try again.",
                error: true)),
            var other => ClaudeTools.Fail(id, -32601, "Ahziael doesn't know " + other),
        };
    }
}
#endif
