using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hellhound;

/// <summary>
/// Everything the app knows about the bots, read from the files beside the server, the same files Ahziael's Bots pages read:
/// Config\Imlight.ini (which bots are on), logs\&lt;bot&gt;\status.json and paused.txt (what each one is doing), bots\state.json
/// (which bot update runs), and the lock checks the bots write in logs\bots (home-guard.txt, hive-mind-check.txt,
/// ahziael-signal.txt, killswitch.txt). The few things it changes are in <see cref="Act"/>.
/// </summary>
internal sealed class ServerFiles(string serverFolder) {

    internal string Folder { get; } = Path.GetFullPath(serverFolder);

    internal string Logs => Path.Combine(Folder, "logs");

    internal string ConfigFile => Path.Combine(Folder, "Config", "Imlight.ini");

    /// <summary>The server folder Ahziael installs to on this PC (%LOCALAPPDATA%\ImlightManager\Server).</summary>
    internal static string DefaultFolder
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImlightManager", "Server");

    // The bot families and their Imlight.ini sections, in the order Ahziael's side bar shows them.
    // On: what the server takes when Imlight.ini has no Enabled line for it (Savitar's isn't written in the file).
    internal static readonly (string Name, string Section, string Folder, bool On)[] Bots = [
        ("Ultimus", "Ultimus", "ultimus", false),
        ("TCO7", "TCO7", "tco7", false),
        ("Kitharas Family", "Kitharas", "kitharas", false),
        ("Zoom", "Zoom", "zoom", true),
        ("Savitar", "Savitar", "savitar", true),
        ("True Form", "TrueForm", "trueform", true),
        ("Quest Team", "QuestTeam", "quest-team", false),
        ("Street Bot Players", "BotPlayers", "bot-players", false),
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

            var ini = Ini.Read(ConfigFile);
            json.WriteStartArray("bots");
            foreach (var (name, section, folder, on) in Bots) {
                WriteBot(json, ini, name, section, on, Path.Combine(Logs, folder), Logs);
            }

            json.WriteEndArray();

            WriteUpdates(json, ini);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteBot(Utf8JsonWriter json, Ini ini, string name, string section, bool onByDefault, string folder, string logs) {
        json.WriteStartObject();
        json.WriteString("name", name);
        json.WriteString("section", section);
        json.WriteString("on", (ini.Get(section, "Enabled") is { Length: > 0 } on ? on.Equals("true", StringComparison.OrdinalIgnoreCase) : onByDefault) ? "on" : "off");
        if (ini.Get(section, "Count") is { Length: > 0 } count) {
            json.WriteString("count", count);
        }

        // A family keeps one folder per member (logs\tco7\tulkar); a single bot keeps its files in its own folder.
        json.WriteStartArray("seats");
        foreach (var seat in Seats(folder)) {
            WriteSeat(json, seat, seat == folder ? name : Path.GetFileName(seat), Path.GetRelativePath(logs, seat).Replace('\\', '/'));
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    /// <summary>A bot's seats: its own folder, then a family member's folders (logs\tco7\tulkar), each with a status or pause file.</summary>
    internal static List<string> Seats(string folder) {
        var seats = new List<string>();
        if (!Directory.Exists(folder)) {
            return seats;
        }

        var all = new List<string> { folder };
        try {
            all.AddRange(Directory.GetDirectories(folder).Order(StringComparer.OrdinalIgnoreCase));
        }
        catch (IOException) {
        }
        catch (UnauthorizedAccessException) {
        }

        seats.AddRange(all.Where(seat => File.Exists(Path.Combine(seat, "status.json")) || File.Exists(Path.Combine(seat, "paused.txt"))));

        return seats;
    }

    private static void WriteSeat(Utf8JsonWriter json, string folder, string name, string id) {
        var statusFile = Path.Combine(folder, "status.json");
        var pauseFile = Path.Combine(folder, "paused.txt");
        var status = ReadJson(statusFile);
        json.WriteStartObject();
        json.WriteString("id", id);
        json.WriteString("name", name);
        json.WriteString("at", Text(status?["at"]));
        json.WriteString("quest", Text(status?["quest"]));
        json.WriteString("doing", Text(status?["doing"]) is { Length: > 0 } doing ? doing : Text(status?["progress"]));
        json.WriteString("paused", FirstLine(pauseFile) ?? "");
        // Savitar parks once Zoom catches up with him (handoff.txt: when, and at which quest).
        json.WriteString("note", FirstLine(Path.Combine(folder, "handoff.txt")) is { } handoff ? "handed over to Zoom: " + handoff : "");

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

    internal bool ServerRunning() {
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

    /// <summary>Sets one key in the file's lines, keeping every other line and comment as it is (as Ahziael's IniFile.Set).</summary>
    internal static void Set(List<string> lines, string section, string key, string value) {
        var inSection = false;
        var sectionEnd = -1;
        for (var i = 0; i < lines.Count; i++) {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) {
                if (inSection) {
                    break;
                }

                inSection = trimmed[1..^1].Trim().Equals(section, StringComparison.OrdinalIgnoreCase);
                if (inSection) {
                    sectionEnd = i + 1;
                }

                continue;
            }

            if (!inSection) {
                continue;
            }

            if (trimmed.Length > 0) {
                sectionEnd = i + 1;
            }

            var equals = trimmed.IndexOf('=');
            if (!trimmed.StartsWith(';') && equals > 0 && trimmed[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) {
                lines[i] = $"{key} = {value}";
                return;
            }
        }

        if (sectionEnd < 0) {
            lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key} = {value}");
        }
        else {
            lines.Insert(sectionEnd, $"{key} = {value}");
        }
    }
}
