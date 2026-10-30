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

#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Imlight.RemoteAdmin;

/// <summary>
/// The words Ahziael and the server speak over Remote Admin (Niduus, 2026-10-30: "people are able to use Ahziael while on
/// non-locally hosted servers, so long as they have admin perms"). The same file builds into both, so they always agree.
/// Each message is a JSON header and an optional block of bytes: [u32 header length][header][u32 body length][body],
/// lengths big-endian, inside TLS.
/// </summary>
public static class RemoteAdminWire {

    public const int PROTOCOL = 1;

    public const int DEFAULT_PORT = 12410;

    /// <summary>Largest header and body a logged-in side accepts.</summary>
    public const int MAX_HEADER = 8 << 20;
    public const int MAX_BODY = 64 << 20;

    /// <summary>Before logging in, only small messages are read, so nobody can make the server hold big ones.</summary>
    public const int MAX_LOGIN_HEADER = 16 << 10;

    /// <summary>Largest file the two sides keep in step.</summary>
    public const long MAX_FILE = 48L << 20;

    /// <summary>How much of a log file a first look brings over.</summary>
    public const long LOG_TAIL = 1L << 20;

    public static async Task WriteAsync(Stream stream, JsonObject header, byte[]? body, CancellationToken ct) {
        var head = Encoding.UTF8.GetBytes(header.ToJsonString());
        body ??= [];
        var frame = new byte[8 + head.Length + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, head.Length);
        head.CopyTo(frame, 4);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4 + head.Length), body.Length);
        body.CopyTo(frame, 8 + head.Length);
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Reads one message, or throws <see cref="InvalidDataException"/> when it is bigger than allowed.</summary>
    public static async Task<(JsonObject Header, byte[] Body)> ReadAsync(Stream stream, int maxHeader, int maxBody, CancellationToken ct) {
        var length = new byte[4];
        await stream.ReadExactlyAsync(length, ct);
        var headLength = BinaryPrimitives.ReadInt32BigEndian(length);
        if (headLength <= 0 || headLength > maxHeader) {
            throw new InvalidDataException("The message is too big.");
        }

        var head = new byte[headLength];
        await stream.ReadExactlyAsync(head, ct);
        await stream.ReadExactlyAsync(length, ct);
        var bodyLength = BinaryPrimitives.ReadInt32BigEndian(length);
        if (bodyLength < 0 || bodyLength > maxBody) {
            throw new InvalidDataException("The message is too big.");
        }

        var body = new byte[bodyLength];
        await stream.ReadExactlyAsync(body, ct);

        return (JsonNode.Parse(head) as JsonObject ?? throw new InvalidDataException("The message is not JSON."), body);
    }

    /// <summary>The server certificate's SHA-256, as 64 hex digits. Ahziael remembers it to know the server again.</summary>
    public static string Fingerprint(byte[] certificate) => Convert.ToHexString(SHA256.HashData(certificate));

    /// <summary>The first half of a fingerprint in groups of four, short enough for two people to read to each other.</summary>
    public static string ShortFingerprint(string fingerprint)
        => string.Join(' ', fingerprint.Take(32).Chunk(4).Select(c => new string(c)));

    public static string HashOf(byte[] data) => Convert.ToHexString(SHA256.HashData(data))[..32];

    public static string HashOf(string path) {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        return Convert.ToHexString(SHA256.HashData(file))[..32];
    }

    public static byte[] Gzip(byte[] data) {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest)) {
            gzip.Write(data);
        }

        return output.ToArray();
    }

    public static byte[] Gunzip(byte[] data, long max = MAX_BODY) {
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer)) > 0) {
            if (output.Length + read > max) {
                throw new InvalidDataException("The file is too big.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    /// <summary>Several files in one block: [u16 path length][path][u32 length][bytes]..., gzipped.</summary>
    public static byte[] Pack(IEnumerable<(string Path, byte[] Data)> files) {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        using (var writer = new BinaryWriter(gzip)) {
            foreach (var (path, data) in files) {
                var name = Encoding.UTF8.GetBytes(path);
                writer.Write((ushort) name.Length);
                writer.Write(name);
                writer.Write(data.Length);
                writer.Write(data);
            }
        }

        return output.ToArray();
    }

    public static List<(string Path, byte[] Data)> Unpack(byte[] block) {
        var files = new List<(string, byte[])>();
        using var reader = new BinaryReader(new MemoryStream(Gunzip(block, MAX_BODY * 2L)));
        while (reader.BaseStream.Position < reader.BaseStream.Length) {
            var name = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadUInt16()));
            var length = reader.ReadInt32();
            if (length < 0 || length > MAX_FILE) {
                throw new InvalidDataException("A file in the block is too big.");
            }

            files.Add((name, reader.ReadBytes(length)));
        }

        return files;
    }

    public static JsonObject Error(string message) => new() { ["ok"] = false, ["error"] = message };

    public static JsonObject Ok() => new() { ["ok"] = true };

}

/// <summary>
/// Which files the two sides keep in step, by a path that starts with its root: "server/codes.json",
/// "server/Config/Imlight.ini", "extra/Customs/1.json", "idlookup/ids.csv", "logs/log20261030.txt". Only data files:
/// never a program, a library, the database or the server's own .NET settings, so a remote admin can change what the
/// server does but not run anything on its PC.
/// </summary>
public static class RemoteAdminPaths {

    public const string SERVER = "server";
    public const string EXTRA = "extra";
    public const string ID_LOOKUP = "idlookup";
    public const string LOGS = "logs";

    public static readonly string[] ROOTS = [SERVER, EXTRA, ID_LOOKUP, LOGS];

    public const string CONFIG = "Config/Imlight.ini";

    /// <summary>The folders inside the server folder whose files are data the app reads or writes.</summary>
    public static readonly string[] SERVER_SUBFOLDERS = ["quest-progress", "fast-forward"];

    private static readonly string[] s_dataExtensions = [".json", ".txt", ".csv"];

    // Files of the server folder that are not data: .NET's own settings, and the two the app's commands and
    // announcements go through, which Remote Admin sends as their own requests instead.
    private static readonly string[] s_neverNames = [
        "manager-commands.txt", "server-announcements.txt", "ahziael-version.txt",
    ];

    /// <summary>Whether the path is well formed: a known root, then plain names, no "..", no drive, no hidden names.</summary>
    public static bool IsSafe(string path) {
        if (string.IsNullOrEmpty(path) || path.Length > 400 || path.Contains('\\') || path.Contains(':') || path.Contains('\0')) {
            return false;
        }

        var parts = path.Split('/');
        if (parts.Length < 2 || !ROOTS.Contains(parts[0])) {
            return false;
        }

        return parts.Skip(1).All(p => p.Length > 0 && p != "." && p != ".." && !p.StartsWith('.')
            && p.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && p.Trim() == p && !p.EndsWith('.'));
    }

    /// <summary>Whether the file is kept in step at all.</summary>
    public static bool IsShared(string path) {
        if (!IsSafe(path)) {
            return false;
        }

        var parts = path.Split('/');
        var name = parts[^1];
        var extension = Path.GetExtension(name).ToLowerInvariant();
        switch (parts[0]) {
            case SERVER:
                if (string.Join('/', parts.Skip(1)).Equals(CONFIG, StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }

                if (parts.Length == 3 && !SERVER_SUBFOLDERS.Contains(parts[1], StringComparer.OrdinalIgnoreCase)) {
                    return false;
                }

                if (parts.Length > 3) {
                    return false;
                }

                var lower = name.ToLowerInvariant();

                return s_dataExtensions.Contains(extension)
                    && !s_neverNames.Contains(lower)
                    && !lower.EndsWith(".deps.json") && !lower.Contains(".runtimeconfig.")
                    && !lower.StartsWith("appsettings") && !lower.StartsWith("settings");
            case EXTRA:
            case ID_LOOKUP:
                return s_dataExtensions.Contains(extension);
            case LOGS:
                return parts.Length == 2 && extension is ".txt" or ".log";
            default:
                return false;
        }
    }

    /// <summary>Whether Ahziael may change or remove the file. Logs are only read.</summary>
    public static bool MayWrite(string path) => IsShared(path) && !path.StartsWith(LOGS + "/", StringComparison.Ordinal);

    /// <summary>Logs only grow, so they are compared by size and time and fetched from where Ahziael's copy ends.</summary>
    public static bool IsLog(string path) => path.StartsWith(LOGS + "/", StringComparison.Ordinal);

    /// <summary>Every shared file under a folder, as root paths. Missing folders give nothing.</summary>
    public static IEnumerable<(string Path, FileInfo File)> List(string root, string folder) {
        if (!Directory.Exists(folder)) {
            yield break;
        }

        var top = new DirectoryInfo(folder);
        IEnumerable<FileInfo> files;
        try {
            files = root switch {
                SERVER => top.EnumerateFiles()
                    .Concat(SERVER_SUBFOLDERS.Select(s => new DirectoryInfo(Path.Combine(folder, s))).Where(d => d.Exists).SelectMany(d => d.EnumerateFiles()))
                    .Concat(new[] { new FileInfo(Path.Combine(folder, "Config", "Imlight.ini")) }.Where(f => f.Exists)),
                LOGS => top.EnumerateFiles()
                    .Where(f => f.Extension is ".txt" or ".log")
                    .OrderByDescending(f => f.LastWriteTimeUtc).Take(4),
                _ => top.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint }),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            yield break;
        }

        foreach (var file in files) {
            var relative = Path.GetRelativePath(folder, file.FullName).Replace('\\', '/');
            var path = root + "/" + relative;
            if (file.Length <= RemoteAdminWire.MAX_FILE && IsShared(path)) {
                yield return (path, file);
            }
        }
    }

    /// <summary>Where a root path is on disk, or null when it would leave its folder.</summary>
    public static string? OnDisk(string path, IReadOnlyDictionary<string, string> roots) {
        if (!IsSafe(path)) {
            return null;
        }

        var slash = path.IndexOf('/');
        if (!roots.TryGetValue(path[..slash], out var folder)) {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(folder, path[(slash + 1)..]));
        var top = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return full.StartsWith(top, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? full : null;
    }

}

/// <summary>
/// Puts a remote admin's Imlight.ini into the server's own, keeping the settings a remote admin may not change: where
/// files and the database are, the ports and addresses the server listens on, and Remote Admin itself.
/// </summary>
public static class RemoteAdminIni {

    private static readonly string[] s_lockedSections = ["Database", "Agent Bridge", "Remote Admin", "Logging"];

    private static readonly string[] s_lockedEndings = [
        "Path", "Url", "Port", "IP", "Address", "Token", "Certificate", "Password", "Folder", "Directory", "Host", "Exe",
    ];

    public static bool IsLocked(string section, string key)
        => s_lockedSections.Contains(section, StringComparer.OrdinalIgnoreCase)
            || s_lockedEndings.Any(e => key.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>The section and key of a "Key = value" line, or null for comments and blank lines.</summary>
    private static (string Key, string Value)? Pair(string line) {
        var text = line.Trim();
        if (text.Length == 0 || text[0] is ';' or '#' or '[') {
            return null;
        }

        var equals = text.IndexOf('=');

        return equals > 0 ? (text[..equals].Trim(), text[(equals + 1)..].Trim()) : null;
    }

    private static string? Section(string line) {
        var text = line.Trim();

        return text.StartsWith('[') && text.EndsWith(']') ? text[1..^1].Trim() : null;
    }

    public static Dictionary<(string, string), string> Read(string text) {
        var values = new Dictionary<(string, string), string>(new KeyComparer());
        var section = "";
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n')) {
            if (Section(line) is { } s) {
                section = s;
            }
            else if (Pair(line) is { } pair) {
                values[(section, pair.Key)] = pair.Value;
            }
        }

        return values;
    }

    /// <summary>
    /// The server's file with the remote admin's values for every setting they may change; locked settings, comments and
    /// the order of the server's file stay. Settings only the remote file has are added at the end of their section.
    /// </summary>
    public static string Merge(string current, string remote, out List<string> refused) {
        refused = [];
        var wanted = Read(remote);
        var lines = current.ReplaceLineEndings("\n").Split('\n').ToList();
        var seen = new HashSet<(string, string)>(new KeyComparer());
        var section = "";
        for (var i = 0; i < lines.Count; i++) {
            if (Section(lines[i]) is { } s) {
                section = s;
                continue;
            }

            if (Pair(lines[i]) is not { } pair) {
                continue;
            }

            seen.Add((section, pair.Key));
            if (!wanted.TryGetValue((section, pair.Key), out var value) || value == pair.Value) {
                continue;
            }

            if (IsLocked(section, pair.Key)) {
                refused.Add($"{section}.{pair.Key}");
                continue;
            }

            var indent = lines[i][..(lines[i].Length - lines[i].TrimStart().Length)];
            lines[i] = $"{indent}{pair.Key} = {value}";
        }

        // Settings the server's file does not have yet (an older server copy, or one the app adds).
        foreach (var group in wanted.Keys.Where(k => !seen.Contains(k)).GroupBy(k => k.Item1, StringComparer.OrdinalIgnoreCase)) {
            var added = new List<string>();
            foreach (var (s, key) in group) {
                if (IsLocked(s, key)) {
                    refused.Add($"{s}.{key}");
                }
                else {
                    added.Add($"{key} = {wanted[(s, key)]}");
                }
            }

            if (added.Count == 0) {
                continue;
            }

            var at = SectionEnd(lines, group.Key);
            if (at < 0) {
                lines.Add("");
                lines.Add($"[{group.Key}]");
                lines.AddRange(added);
            }
            else {
                lines.InsertRange(at, added);
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    // Where a new line of a section goes: after its last setting, or -1 when the file has no such section.
    private static int SectionEnd(List<string> lines, string name) {
        var start = lines.FindIndex(l => Section(l) is { } s && s.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (start < 0) {
            return -1;
        }

        var end = start + 1;
        for (var i = start + 1; i < lines.Count && Section(lines[i]) is null; i++) {
            if (Pair(lines[i]) is not null) {
                end = i + 1;
            }
        }

        return end;
    }

    private sealed class KeyComparer : IEqualityComparer<(string, string)> {

        public bool Equals((string, string) a, (string, string) b)
            => string.Equals(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string, string) k)
            => HashCode.Combine(k.Item1.ToLowerInvariant(), k.Item2.ToLowerInvariant());

    }

}
