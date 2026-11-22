namespace Ahziael.Bots;

/// <summary>
/// The bots' killswitch (Niduus, 2026-11-22: "if its on (= true) then the bots all won't work. Even with the key"):
/// bots-killswitch.txt at the root of Niduus/Imlight on quality-assurance, one line "on = true" or "on = false", lines
/// starting with # are comments. The bots read it themselves every 5 minutes (logs\bots\killswitch.txt); the app reads the
/// same file on its own, as often, so it can show it and, once it can change things, refuse to switch bots on while it's on.
/// </summary>
internal static class Killswitch {

    internal const string URL = "https://raw.githubusercontent.com/Niduus/Imlight/quality-assurance/bots-killswitch.txt";

    private static readonly TimeSpan s_every = TimeSpan.FromMinutes(5);
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly Lock s_lock = new();
    private static DateTime s_readAt = DateTime.MinValue;
    private static bool? s_on;
    private static string s_text = "Not read yet.";

    /// <summary>Whether it is on (null: never read), and a line saying when and where it was read.</summary>
    internal static (bool? On, string Text) Read() {
        lock (s_lock) {
            if (DateTime.UtcNow - s_readAt < s_every) {
                return (s_on, s_text);
            }

            s_readAt = DateTime.UtcNow;
        }

        bool? on = null;
        string text;
        try {
            var body = s_http.GetStringAsync(URL).GetAwaiter().GetResult();
            on = Parse(body);
            text = on is null ? $"read {DateTime.Now:HH:mm}, but it has no \"on = true/false\" line" : $"read {DateTime.Now:HH:mm} from GitHub";
        }
        catch (Exception) {
            // Like the bots: keep the last verdict when GitHub can't be reached.
            lock (s_lock) {
                s_text = $"GitHub not reached at {DateTime.Now:HH:mm}; " + (s_on is null ? "never read yet" : $"last read said {(s_on.Value ? "on" : "off")}");
                return (s_on, s_text);
            }
        }

        lock (s_lock) {
            s_on = on ?? s_on;
            s_text = text;
            return (s_on, s_text);
        }
    }

    internal static bool? Parse(string body) {
        foreach (var raw in body.Split('\n')) {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim().Equals("on", StringComparison.OrdinalIgnoreCase)) {
                var value = line[(eq + 1)..].Trim();
                if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) {
                    return true;
                }

                if (value.Equals("false", StringComparison.OrdinalIgnoreCase)) {
                    return false;
                }
            }
        }

        return null;
    }
}
