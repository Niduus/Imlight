using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace Hellhound;

/// <summary>
/// Hellhound: the bots' own app, which takes the bots out of Ahziael step by step over several updates (Niduus, 2026-11-22:
/// "this one will just be called Hellhound. Everything bot related goes in this"). It runs a tiny web page on this PC only
/// (127.0.0.1, a free port) and opens it in a window of its own (<see cref="AppWindow"/>).
/// <para>
/// Hellhound.exe                      opens the page
/// Hellhound.exe --server "D:\Server" a server folder other than %LOCALAPPDATA%\ImlightManager\Server
/// Hellhound.exe --once               prints the bots' state as JSON once and exits (no page)
/// Hellhound.exe --no-browser         serves the page without opening its window
/// </para>
/// </summary>
internal static class Program {

    private static int Main(string[] args) {
        var folder = ServerFiles.DefaultFolder;
        var once = false;
        var openBrowser = true;
        for (var i = 0; i < args.Length; i++) {
            switch (args[i]) {
                case "--server" when i + 1 < args.Length:
                    folder = args[++i];
                    break;
                case "--once":
                    once = true;
                    break;
                case "--no-browser":
                    openBrowser = false;
                    break;
            }
        }

        var files = new ServerFiles(folder);
        if (once) {
            Console.WriteLine(files.ToJson());
            return 0;
        }

        // One Hellhound at a time: a second start just opens the window again.
        using var single = new Mutex(true, "Local\\Hellhound", out var first);
        var portFile = Path.Combine(AppWindow.DataFolder, "port.txt");
        if (!first) {
            if (File.Exists(portFile) && int.TryParse(File.ReadAllText(portFile).Trim(), out var running)) {
                AppWindow.Open($"http://127.0.0.1:{running}/")?.Dispose();
            }

            return 0;
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint) listener.LocalEndpoint).Port;
        var url = $"http://127.0.0.1:{port}/";
        AppWindow.Log($"Hellhound is running at {url} for the server in {files.Folder}.");
        try {
            Directory.CreateDirectory(AppWindow.DataFolder);
            File.WriteAllText(portFile, port.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception) {
        }

        if (openBrowser && AppWindow.Open(url) is { } window) {
            // Closing Hellhound's window closes Hellhound.
            // (When an old Hellhound window is still open, Edge hands the new one to it and its first process quits at
            // once: Hellhound then keeps running.)
            var opened = DateTime.UtcNow;
            _ = Task.Run(() => {
                window.WaitForExit();
                if (DateTime.UtcNow - opened > TimeSpan.FromSeconds(5)) {
                    Environment.Exit(0);
                }
            });
        }

        var page = ReadPage();
        var spectator = new Spectator(files);
        while (true) {
            var client = listener.AcceptTcpClient();
            _ = Task.Run(() => Answer(client, files, spectator, page, port));
        }
    }

    private static byte[] ReadPage() {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("page.html")!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return memory.ToArray();
    }

    // One request per connection: GET / (the page), GET /api/status (the bots, as JSON), or one of the POST actions below.
    // Anything else is refused.
    private static void Answer(TcpClient client, ServerFiles files, Spectator spectator, byte[] page, int port) {
        using (client) {
            try {
                client.ReceiveTimeout = 5000;
                using var stream = client.GetStream();
                var head = ReadHead(stream);
                var lines = head.Split("\r\n");
                var parts = lines[0].Split(' ');
                var host = lines.Skip(1).FirstOrDefault(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))?[5..].Trim();

                // Only pages opened at this exact address may ask (another website in the browser can't read the bots).
                if (host != $"127.0.0.1:{port}" && host != $"localhost:{port}") {
                    Send(stream, "403 Forbidden", "text/plain", "Not allowed."u8.ToArray());
                }
                else if (parts.Length >= 2 && parts[0] == "POST") {
                    // Only the page itself may change things: a browser sends this header only from the page's own address
                    // (another website would need permission this app never gives), and the Origin must be this address.
                    var origin = Header(lines, "Origin");
                    if (Header(lines, "X-Hellhound") != "1" || (origin is not null && origin != $"http://{host}")) {
                        Send(stream, "403 Forbidden", "text/plain", "Not allowed."u8.ToArray());
                    }
                    else {
                        Send(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(Do(files, parts[1])));
                    }
                }
                else if (parts.Length < 2 || parts[0] != "GET") {
                    Send(stream, "405 Method Not Allowed", "text/plain", "Not allowed."u8.ToArray());
                }
                else if (parts[1] == "/") {
                    Send(stream, "200 OK", "text/html; charset=utf-8", page);
                }
                else if (parts[1].StartsWith("/api/watch", StringComparison.Ordinal)) {
                    var query = Query(parts[1]);
                    var json = spectator.WatchAsync(query.GetValueOrDefault("who") ?? "", query.GetValueOrDefault("ground") ?? "").GetAwaiter().GetResult();
                    Send(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                }
                else if (parts[1] == "/api/status") {
                    Send(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(files.ToJson()));
                }
                else {
                    Send(stream, "404 Not Found", "text/plain", "Not found."u8.ToArray());
                }
            }
            catch (Exception) {
                // The browser went away mid-answer.
            }
        }
    }

    private static string? Header(string[] lines, string name)
        => lines.Skip(1).FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..].Trim();

    // POST /api/switch?bot=<Section>&on=true|false, /api/pause?seat=<seat id>, /api/check-updates.
    private static string Do(ServerFiles files, string target) {
        var q = target.IndexOf('?');
        var path = q < 0 ? target : target[..q];
        try {
            var query = Query(target);
            return path switch {
                "/api/switch" => Act.Switch(files, query.GetValueOrDefault("bot") ?? "", query.GetValueOrDefault("on") == "true"),
                "/api/pause" => Act.Pause(files, query.GetValueOrDefault("seat") ?? ""),
                "/api/check-updates" => Act.CheckUpdates(files),
                _ => "Unknown action.",
            };
        }
        catch (Exception e) {
            return "That didn't work: " + e.Message;
        }
    }

    private static Dictionary<string, string> Query(string target) {
        var q = target.IndexOf('?');
        var query = new Dictionary<string, string>();
        foreach (var pair in (q < 0 ? "" : target[(q + 1)..]).Split('&', StringSplitOptions.RemoveEmptyEntries)) {
            var kv = pair.Split('=', 2);
            query[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
        }

        return query;
    }

    private static string ReadHead(NetworkStream stream) {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 16 * 1024 && stream.Read(one, 0, 1) == 1) {
            bytes.Add(one[0]);
            if (bytes.Count >= 4 && bytes[^1] == '\n' && bytes[^2] == '\r' && bytes[^3] == '\n' && bytes[^4] == '\r') {
                break;
            }
        }

        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static void Send(NetworkStream stream, string status, string type, byte[] body) {
        var head = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\n" +
                   "X-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
        stream.Write(Encoding.ASCII.GetBytes(head));
        stream.Write(body);
    }
}
