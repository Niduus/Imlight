using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace Ahziael.Bots;

/// <summary>
/// Ahziael Bots: the bots' own small app, which will take the bots out of Ahziael step by step over several updates (Niduus,
/// 2026-11-22). It runs a tiny web page on this PC only (127.0.0.1, a free port) and opens it in the browser.
/// <para>
/// AhziaelBots.exe                      opens the page
/// AhziaelBots.exe --server "D:\Server" a server folder other than %LOCALAPPDATA%\ImlightManager\Server
/// AhziaelBots.exe --once               prints the bots' state as JSON once and exits (no page)
/// AhziaelBots.exe --no-browser         serves the page without opening the browser
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

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint) listener.LocalEndpoint).Port;
        var url = $"http://127.0.0.1:{port}/";
        Console.WriteLine($"Ahziael Bots is running at {url}");
        Console.WriteLine($"Server folder: {files.Folder}");
        Console.WriteLine("Close this window to stop it.");

        if (openBrowser) {
            try {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception) {
                Console.WriteLine("Could not open the browser by itself: open the address above.");
            }
        }

        var page = ReadPage();
        while (true) {
            var client = listener.AcceptTcpClient();
            _ = Task.Run(() => Answer(client, files, page, port));
        }
    }

    private static byte[] ReadPage() {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("page.html")!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return memory.ToArray();
    }

    // One request per connection: GET / (the page) or GET /api/status (the bots, as JSON). Anything else is refused.
    private static void Answer(TcpClient client, ServerFiles files, byte[] page, int port) {
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
                else if (parts.Length < 2 || parts[0] != "GET") {
                    Send(stream, "405 Method Not Allowed", "text/plain", "Read only."u8.ToArray());
                }
                else if (parts[1] == "/") {
                    Send(stream, "200 OK", "text/html; charset=utf-8", page);
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
