using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ahziael.Bots;

/// <summary>
/// Everything the app knows about the bots, read from the files beside the server, the same files Ahziael's Bots pages read:
/// Config\Imlight.ini (which bots are on), logs\&lt;bot&gt;\status.json and paused.txt (what each one is doing), bots\state.json
/// (which bot update runs), and the lock checks the bots write in logs\bots (home-guard.txt, hive-mind-check.txt,
/// ahziael-signal.txt, killswitch.txt). Read only: nothing here writes a file.
/// </summary>
internal sealed class ServerFiles(string serverFolder) {

    internal string Folder { get; } = Path.GetFullPath(serverFolder);

    private string Logs => Path.Combine(Folder, "logs");

    /// <summary>The server folder Ahziael installs to on this PC (%LOCALAPPDATA%\ImlightManager\Server).</summary>
    internal static string DefaultFolder
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImlightManager", "Server");

    // The bot families and their Imlight.ini sections, in the order Ahziael's side bar shows them.
    private static readonly (string Name, string Section, string Folder)[] s_bots = [
        ("Ultimus", "Ultimus", "ultimus"),
        ("TCO7", "TCO7", "tco7"),
        ("Kitharas Family", "Kitharas", "kitharas"),
        ("Zoom", "Zoom", "zoom"),
        ("True Form", "TrueForm", "trueform"),
        ("Quest Team", "QuestTeam", "quest-team"),
        ("Street Bot Players", "BotPlayers", "bot-players"),
    ];

    // The checks that decide whether the bots may play (written by the bots in logs\bots).
    private static readonly (string Name, string File, string Missing)[] s_locks = [
        ("Stays on this server", "home-guard.txt", "No check written yet."),
        ("Signed Hive Mind", "hive-mind-check.txt", "No check written yet."),
        ("Ahziael signal", "ahziael-signal.txt", "No check written yet."),
        ("Kill switch (what the bots last saw)", "killswitch.txt", "Not written yet (comes with the next bot update)."),
    ];

    /// <summary>The whole picture as JSON, for the page.</summary>
    internal string ToJson() {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream)) {
            json.WriteStartObject();
            json.WriteString("folder", Folder);
            json.WriteBoolean("folderFound", Directory.Exists(Folder));
            json.WriteBoolean("serverRunning", ServerRunning());
            json.WriteString("readAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            json.WriteStartArray("locks");
            foreach (var (name, file, missing) in s_locks) {
                var line = LastLine(Path.Combine(Logs, "bots", file));
                json.WriteStartObject();
                json.WriteString("name", name);
                json.WriteString("text", line ?? missing);
                json.WriteBoolean("written", line is not null);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            var (stop, why) = Killswitch.Read(Folder);
            json.WriteStartObject("killswitch");
            json.WriteBoolean("stop", stop);
            json.WriteString("text", why);
            json.WriteEndObject();

            var ini = Ini.Read(Path.Combine(Folder, "Config", "Imlight.ini"));
            json.WriteStartArray("bots");
            foreach (var (name, section, folder) in s_bots) {
                WriteBot(json, ini, name, section, Path.Combine(Logs, folder));
            }

            json.WriteEndArray();

            WriteUpdates(json, ini);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteBot(Utf8JsonWriter json, Ini ini, string name, string section, string folder) {
        json.WriteStartObject();
        json.WriteString("name", name);
        json.WriteString("on", ini.Get(section, "Enabled") is { } on ? (on.Equals("true", StringComparison.OrdinalIgnoreCase) ? "on" : "off") : "unknown");
        if (ini.Get(section, "Count") is { Length: > 0 } count) {
            json.WriteString("count", count);
        }

        // A family keeps one folder per member (logs\tco7\tulkar); a single bot keeps its files in its own folder.
        json.WriteStartArray("seats");
        if (Directory.Exists(folder)) {
            var seats = new List<string> { folder };
            try {
                seats.AddRange(Directory.GetDirectories(folder).Order(StringComparer.OrdinalIgnoreCase));
            }
            catch (IOException) {
            }
            catch (UnauthorizedAccessException) {
            }

            foreach (var seat in seats) {
                WriteSeat(json, seat, seat == folder ? name : Path.GetFileName(seat));
            }
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void WriteSeat(Utf8JsonWriter json, string folder, string name) {
        var statusFile = Path.Combine(folder, "status.json");
        var pauseFile = Path.Combine(folder, "paused.txt");
        if (!File.Exists(statusFile) && !File.Exists(pauseFile)) {
            return;
        }

        var status = ReadJson(statusFile);
        json.WriteStartObject();
        json.WriteString("name", name);
        json.WriteString("at", Text(status?["at"]));
        json.WriteString("quest", Text(status?["quest"]));
        json.WriteString("doing", Text(status?["doing"]) is { Length: > 0 } doing ? doing : Text(status?["progress"]));
        json.WriteString("paused", FirstLine(pauseFile) ?? "");

        json.WriteStartArray("members");
        if (status?["members"] is JsonArray members) {
            foreach (var member in members) {
                json.WriteStartObject();
                json.WriteString("name", Text(member?["name"]));
                json.WriteString("state", Text(member?["state"]));
                json.WriteString("zone", Text(member?["zone"]));
                json.WriteEndObject();
            }
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private void WriteUpdates(Utf8JsonWriter json, Ini ini) {
        var state = ReadJson(Path.Combine(Folder, "bots", "state.json"));
        json.WriteStartObject("updates");
        json.WriteString("liveUpdates", ini.Get("Bots", "LiveUpdates") ?? "true");
        json.WriteString("wanted", FirstLine(Path.Combine(Folder, "bots", "active.txt")) ?? "built in");
        json.WriteString("running", Text(state?["running"]));
        json.WriteString("lastCheck", Text(state?["lastCheck"]));
        json.WriteString("lastCheckResult", Text(state?["lastCheckResult"]));
        json.WriteString("problem", Text(state?["problem"]));
        json.WriteEndObject();
    }

    private bool ServerRunning() {
        var exe = Path.Combine(Folder, OperatingSystem.IsWindows() ? "Imlight.Director.exe" : "Imlight.Director");
        foreach (var process in Process.GetProcessesByName("Imlight.Director")) {
            try {
                if (string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }
            }
            catch (Exception) {
                // Another user's process: not this server.
            }
            finally {
                process.Dispose();
            }
        }

        return false;
    }

    private static string Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node?.ToJsonString() ?? "";

    private static JsonNode? ReadJson(string path) {
        try {
            return File.Exists(path) ? JsonNode.Parse(Shared(path)) : null;
        }
        catch (Exception) {
            // Being written by the server right now: the page asks again in a few seconds.
            return null;
        }
    }

    private static string? FirstLine(string path)
        => Lines(path)?.FirstOrDefault(l => l.Trim().Length > 0)?.Trim();

    private static string? LastLine(string path)
        => Lines(path)?.LastOrDefault(l => l.Trim().Length > 0)?.Trim();

    private static string[]? Lines(string path) {
        try {
            return File.Exists(path) ? Shared(path).Split('\n') : null;
        }
        catch (IOException) {
            return null;
        }
        catch (UnauthorizedAccessException) {
            return null;
        }
    }

    // Reads a file the server may be writing at the same moment, without blocking it.
    private static string Shared(string path) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}

/// <summary>The server's Imlight.ini, read the plain way ([Section], Key = Value, ; comments).</summary>
internal sealed class Ini {

    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    internal static Ini Read(string path) {
        var ini = new Ini();
        try {
            if (!File.Exists(path)) {
                return ini;
            }

            var section = "";
            foreach (var raw in File.ReadLines(path)) {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] is ';' or '#') {
                    continue;
                }

                if (line[0] == '[' && line.EndsWith(']')) {
                    section = line[1..^1].Trim();
                    continue;
                }

                var eq = line.IndexOf('=');
                if (eq > 0) {
                    ini._values[$"{section}\n{line[..eq].Trim()}"] = line[(eq + 1)..].Trim();
                }
            }
        }
        catch (IOException) {
        }
        catch (UnauthorizedAccessException) {
        }

        return ini;
    }

    internal string? Get(string section, string key) => _values.GetValueOrDefault($"{section}\n{key}");
}
