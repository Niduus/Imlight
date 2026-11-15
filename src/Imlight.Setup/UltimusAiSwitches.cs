using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Imlight.Setup;

/// <summary>
/// Ultimus's AI switches that the overlay and Ahziael share (Niduus, 2026-11-15, "all"): pause the AI for an hour (or
/// resume it), and turn every AI off at once. They write the server's Imlight.ini on this PC, which the server reads
/// again by itself, so they count right away.
/// </summary>
internal static class UltimusAiSwitches {

    /// <summary>The time the AI is paused until (UTC), or null when it is not paused.</summary>
    internal static DateTime? PausedUntil(string configFile)
        => IniFile.Get(configFile, "Ultimus", "AIPausedUntil") is { Length: > 0 } value
           && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var until)
           && until > DateTime.UtcNow
            ? until
            : null;

    /// <summary>Pauses the AI for one hour, or resumes it when it is paused. Plain words back, and whether it worked.</summary>
    internal static (bool Ok, string Said) TogglePause(string configFile) {
        if (!File.Exists(configFile) || IniFile.Get(configFile, "Ultimus", "AIPausedUntil") is null) {
            return (false, "Only the PC that runs the server can pause Ultimus's AI, and its Ahziael must be updated first.");
        }

        if (PausedUntil(configFile) is not null) {
            Write(configFile, ("Ultimus", "AIPausedUntil", ""));
            return (true, "The AI is back on for Ultimus and the Quest Team.");
        }

        var until = DateTime.UtcNow.AddHours(1);
        Write(configFile, ("Ultimus", "AIPausedUntil", until.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
        return (true, $"The AI is paused until {until.ToLocalTime():HH:mm}. Ultimus and the Quest Team use their normal chat and own fixes meanwhile.");
    }

    /// <summary>
    /// Every AI off at once: Ultimus's AI chat, his stuck thinking and the Quest Team's AI answers. Each can be turned on
    /// again by its own switch.
    /// </summary>
    internal static (bool Ok, string Said) AllOff(string configFile) {
        if (!File.Exists(configFile)) {
            return (false, "The server's settings file isn't on this PC.");
        }

        var changes = new[] { ("Ultimus", "LocalAI", "false"), ("Ultimus", "StuckThinking", "false"), ("QuestTeam", "AIChat", "false") }
            .Where(c => IniFile.Get(configFile, c.Item1, c.Item2) is not null).ToArray();
        Write(configFile, changes);
        return (true, "All AI is off: Ultimus's AI chat, his stuck thinking and the Quest Team's AI answers. Each switch can turn its part on again.");
    }

    private static void Write(string configFile, params (string Section, string Key, string Value)[] changes) {
        var lines = File.ReadAllLines(configFile).ToList();
        foreach (var (section, key, value) in changes) {
            IniFile.Set(lines, section, key, value);
        }

        File.WriteAllLines(configFile, lines);
    }

}
