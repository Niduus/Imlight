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
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Imlight.Setup;

/// <summary>
/// A small patch server that hands Imlight the game files from the player's own Wizard101 install,
/// so no separate patch server (such as Aurorium) is needed on a home computer.
/// </summary>
/// <remarks>
/// Imlight asks for <c>{url}/V_{revision}/Data/GameData/{name}.wad</c> and <c>LatestFileList.xml</c>.
/// The wad paths match the game folder's own layout. The file list is made from the files on disk,
/// so Imlight refreshes its cache by itself whenever Wizard101 patches. Only loopback is served.
/// </remarks>
internal sealed class GameDataServer : IDisposable {

    private const int MAX_HEADER_BYTES = 16 * 1024;

    // Looked in from first to last: Aurorium's revisions (newest first) when the server takes its files, then Wizard101's.
    private readonly string[] _gameDataFolders;
    private readonly string _missingHint;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();

    private GameDataServer(string[] gameDataFolders, int port, string missingHint) {
        _gameDataFolders = [.. gameDataFolders.Select(Path.GetFullPath)];
        _missingHint = missingHint;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    /// <summary>
    /// Starts serving, or returns null when the port is taken (another patch server is already running).
    /// </summary>
    internal static GameDataServer? TryStart(string gameDataFolder, int port)
        => TryStart([gameDataFolder], port, "is not in your Wizard101 folder yet. Visit that area once in the real game so Wizard101 downloads it.");

    /// <inheritdoc cref="TryStart(string, int)"/>
    internal static GameDataServer? TryStart(string[] gameDataFolders, int port, string missingHint) {
        var server = new GameDataServer(gameDataFolders, port, missingHint);
        try {
            server._listener.Start();
        }
        catch (SocketException) {
            server.Dispose();

            return null;
        }

        _ = server.AcceptLoopAsync();

        return server;
    }

    private async Task AcceptLoopAsync() {
        while (!_stop.IsCancellationRequested) {
            TcpClient client;
            try {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception) {
                // Stopped.
                return;
            }

            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client) {
        using (client) {
            try {
                var stream = client.GetStream();
                var request = await ReadRequestLineAsync(stream);
                if (request is null) {
                    return;
                }

                var (method, target) = request.Value;
                var isHead = method == "HEAD";
                if (!isHead && method != "GET") {
                    await WriteEmptyAsync(stream, "405 Method Not Allowed");

                    return;
                }

                await RespondAsync(stream, target, isHead);
            }
            catch (IOException) {
                // Imlight closed the connection early; nothing to do.
            }
        }
    }

    private async Task RespondAsync(NetworkStream stream, string target, bool isHead) {
        var relative = ToRelativePath(target);
        if (relative.Equals("LatestFileList.xml", StringComparison.OrdinalIgnoreCase)) {
            var body = Encoding.UTF8.GetBytes(BuildLatestFileList());
            await WriteHeadAsync(stream, "200 OK", body.Length, "text/xml");
            if (!isHead) {
                await stream.WriteAsync(body);
            }

            return;
        }

        var file = Resolve(relative);
        if (file is null) {
            if (relative.EndsWith(".wad", StringComparison.OrdinalIgnoreCase)) {
                Write(ConsoleColor.Yellow, $"{Path.GetFileName(relative)} {_missingHint}");
            }

            await WriteEmptyAsync(stream, "404 Not Found");

            return;
        }

        await using var content = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await WriteHeadAsync(stream, "200 OK", content.Length, "application/octet-stream");
        if (!isHead) {
            Write(ConsoleColor.DarkGray, $"Sending {Path.GetFileName(file)} to the server.");
            await content.CopyToAsync(stream);
        }
    }

    /// <summary>
    /// Turns "/V_r803238.Wizard_1_610/Data/GameData/Root.wad?x" into "Data/GameData/Root.wad".
    /// </summary>
    internal static string ToRelativePath(string target) {
        var path = target.Split('?', '#')[0];
        path = Uri.UnescapeDataString(path).Replace('\\', '/');
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();

        // The first segment names the game revision; the files on disk are whatever the game has now.
        if (segments.Count > 0 && segments[0].StartsWith("V_", StringComparison.OrdinalIgnoreCase)) {
            segments.RemoveAt(0);
        }

        return string.Join('/', segments);
    }

    /// <summary>
    /// Finds a requested file in the game data folders, the first that has it. Only files directly inside them are served.
    /// </summary>
    internal string? Resolve(string relative) {
        const string PREFIX = "Data/GameData/";
        var name = relative.StartsWith(PREFIX, StringComparison.OrdinalIgnoreCase) ? relative[PREFIX.Length..] : relative;
        if (name.Length == 0 || name.Contains('/') || name.Contains("..", StringComparison.Ordinal)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) {
            return null;
        }

        foreach (var folder in _gameDataFolders) {
            var full = Path.GetFullPath(Path.Combine(folder, name));
            var isInside = string.Equals(Path.GetDirectoryName(full), folder.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
            if (isInside && File.Exists(full)) {
                return full;
            }
        }

        return null;
    }

    /// <summary>
    /// A LatestFileList listing every wad on disk with its size (the copy <see cref="Resolve"/> hands out, when several
    /// folders have one). Imlight compares the sizes to its cache.
    /// </summary>
    internal string BuildLatestFileList() {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<LatestFileList>\n<Base>\n");
        var files = _gameDataFolders.Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.wad"))
            .DistinctBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files) {
            var name = SecurityElement.Escape("Data/GameData/" + Path.GetFileName(file));
            builder.Append("<RECORD><SrcFileName>").Append(name).Append("</SrcFileName><TarFileName>").Append(name)
                .Append("</TarFileName><FileType>0</FileType><Size>").Append(new FileInfo(file).Length)
                .Append("</Size></RECORD>\n");
        }

        builder.Append("</Base>\n</LatestFileList>\n");

        return builder.ToString();
    }

    private static async Task<(string Method, string Target)?> ReadRequestLineAsync(NetworkStream stream) {
        var buffer = new byte[MAX_HEADER_BYTES];
        var length = 0;
        while (length < buffer.Length) {
            var read = await stream.ReadAsync(buffer.AsMemory(length));
            if (read == 0) {
                return null;
            }

            length += read;
            var text = Encoding.ASCII.GetString(buffer, 0, length);
            if (!text.Contains("\r\n\r\n", StringComparison.Ordinal)) {
                continue;
            }

            var parts = text[..text.IndexOf("\r\n", StringComparison.Ordinal)].Split(' ');

            return parts.Length >= 2 ? (parts[0].ToUpperInvariant(), parts[1]) : null;
        }

        return null;
    }

    private static async Task WriteHeadAsync(NetworkStream stream, string status, long length, string contentType) {
        var head = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
    }

    private static Task WriteEmptyAsync(NetworkStream stream, string status)
        => WriteHeadAsync(stream, status, 0, "text/plain");

    private static readonly Lock s_consoleLock = new();

    private static void Write(ConsoleColor color, string message) {
        lock (s_consoleLock) {
            Console.ForegroundColor = color;
            Console.WriteLine($"[Game files] {message}");
            Console.ResetColor();
        }
    }

    public void Dispose() {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }

}
