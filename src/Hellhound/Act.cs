using System.Globalization;

namespace Hellhound;

/// <summary>
/// The few things Hellhound changes, each written to the same file Ahziael's Bots pages write, which the running server reads
/// within seconds (no restart): a bot family's on/off switch in Config\Imlight.ini, a seat's paused.txt, and bots\check-now.txt.
/// Nothing turns a bot on while the kill switch says the bots must stop.
/// </summary>
internal static class Act {

    /// <summary>Turns a bot family on or off ([Section] Enabled), and says what happened.</summary>
    internal static string Switch(ServerFiles files, string section, bool on) {
        var bot = ServerFiles.Bots.FirstOrDefault(b => b.Section.Equals(section, StringComparison.OrdinalIgnoreCase));
        if (bot.Name is null) {
            return "No such bot.";
        }

        if (on && Killswitch.Read(files.Folder) is { Stop: true } kill) {
            return $"{bot.Name} stays off: the kill switch says the bots must stop ({kill.Why}).";
        }

        if (!File.Exists(files.ConfigFile)) {
            return "The server's Imlight.ini isn't there. Install or start the server once first.";
        }

        var lines = File.ReadAllLines(files.ConfigFile).ToList();
        Ini.Set(lines, bot.Section, "Enabled", on ? "true" : "false");
        File.WriteAllLines(files.ConfigFile, lines);

        return on ? $"{bot.Name} is on. The server starts {(bot.Name.Contains("Family") || bot.Name == "TCO7" ? "them" : "it")} within a few seconds."
                  : $"{bot.Name} is off. The server logs {(bot.Name.Contains("Family") || bot.Name == "TCO7" ? "them" : "it")} out within a few seconds.";
    }

    /// <summary>Pauses a seat (writes its paused.txt) or resumes it (removes it), as Ahziael's Pause or resume button does.</summary>
    internal static string Pause(ServerFiles files, string id) {
        // Only a seat the page shows: never a path the request makes up.
        var seat = ServerFiles.Bots
            .SelectMany(b => ServerFiles.Seats(Path.Combine(files.Logs, b.Folder)))
            .FirstOrDefault(s => Path.GetRelativePath(files.Logs, s).Replace('\\', '/') == id);
        if (seat is null) {
            return "No such bot seat.";
        }

        var name = Path.GetFileName(seat);
        var file = Path.Combine(seat, "paused.txt");
        if (File.Exists(file)) {
            File.Delete(file);

            return $"Resumed {name}. It goes on with its quest.";
        }

        File.WriteAllText(file, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));

        return $"Paused {name}. It stands where it is and stays logged in (a battle it is in still finishes).";
    }

    /// <summary>Asks the server to look for a bot update now (bots\check-now.txt), as Ahziael's Bot Updates page does.</summary>
    internal static string CheckUpdates(ServerFiles files) {
        if (!files.ServerRunning()) {
            return "The server isn't running, so it can't look for a bot update now. It looks by itself when it starts.";
        }

        var folder = Path.Combine(files.Folder, "bots");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "check-now.txt"), DateTime.Now.ToString("o", CultureInfo.InvariantCulture));

        return "The server looks for a bot update within a few seconds. If there's a newer one, the bots log out and come back with it.";
    }
}
