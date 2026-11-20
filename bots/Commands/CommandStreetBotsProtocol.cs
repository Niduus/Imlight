/*
 * Imlight
 * Copyright (C) 2026 Revive101
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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Imlight.Common;
using Imlight.CoreLib.Game.Ultimus;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// The questing street bots from the game's chat (Niduus, 2026-11-20: street bots that quest like the TCO7, to swarm the
/// server and collect more data). Until Ahziael has its own switch for them, this turns them on and off and sets how many:
/// ".streetbots" says how they are doing, ".streetbots on 6" turns six on, ".streetbots 10" sets ten, ".streetbots off"
/// logs them all out. It writes [StreetQuest] in Imlight.ini (adding it when the file has none yet), and the server reads
/// the file again by itself, so a change works within seconds, without a restart. Administrators only.
/// </summary>
internal class CommandStreetBots : CommandProtocol {

    internal override string Group { get; set; } = "";

    private static readonly object s_writeLock = new();

    private static string IniPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "Imlight.ini");

    [Command("streetbots")]
    [Alias("streetbot", "streetquest")]
    [AuthRequired(AuthLevel.Administrator)]
    private void StreetBotsCommand([Remainder] string words = "") {
        var parts = (words ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var first = parts.Length > 0 ? parts[0] : "";
        int? count = parts.Select(p => int.TryParse(p, out var n) ? n : (int?) null).FirstOrDefault(n => n is not null);
        switch (first) {
            case "" or "list" or "where" or "status":
                Show();
                return;
            case "on" or "start":
                Save(true, count);
                return;
            case "off" or "stop":
                Save(false, null);
                return;
            default:
                if (count is not null) {
                    Save(null, count);
                    return;
                }

                InformSenderClient(".streetbots shows the questing street bots. .streetbots on 6 turns six on, .streetbots 10 sets ten, .streetbots off logs them all out.");
                return;
        }
    }

    private void Save(bool? on, int? count) {
        if (count is { } n && (n < 0 || n > UltimusSeat.STREET_MOST)) {
            InformSenderClient($"Pick a number from 0 to {UltimusSeat.STREET_MOST}.");
            return;
        }

        try {
            lock (s_writeLock) {
                var lines = File.ReadAllLines(IniPath).ToList();
                if (on is { } enabled) {
                    Set(lines, "Enabled", enabled ? "true" : "false");
                }

                if (count is { } number) {
                    Set(lines, "Count", number.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                File.WriteAllLines(IniPath, lines);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            InformSenderClient($"Imlight.ini could not be changed: {e.Message}");
            return;
        }

        var playing = on == false ? 0 : count ?? Math.Clamp(ConfigurationManager.GetValue("StreetQuest.Count", 4), 0, UltimusSeat.STREET_MOST);
        InformSenderClient(on == false
            ? "Questing street bots are off. They log out within a few seconds."
            : on == true || ConfigurationManager.GetValue("StreetQuest.Enabled", false)
                ? $"Questing street bots are on: {playing} of them. They start (or log out) within a few seconds; each brand new one starts at level 1 in Wizard City."
                : $"Saved: {playing} questing street bots once they are on (.streetbots on).");
    }

    /// <summary>Sets one key of [StreetQuest], adding the key, or the whole section with a note, when the file has none.</summary>
    internal static void Set(List<string> lines, string key, string value) {
        var header = lines.FindIndex(l => l.Trim().Equals("[" + UltimusSeat.STREET_SECTION + "]", StringComparison.OrdinalIgnoreCase));
        if (header < 0) {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) {
                lines.Add("");
            }

            lines.Add("[" + UltimusSeat.STREET_SECTION + "]");
            lines.Add("; Questing street bots: street bots that play the Main Story like the TCO7, each with Ultimus's brain and a plain");
            lines.Add("; random name. Enabled turns them on; Count is how many play at once (0-" + UltimusSeat.STREET_MOST + "). Live, no restart.");
            header = lines.Count - 1;
        }

        var end = lines.FindIndex(header + 1, l => l.TrimStart().StartsWith('['));
        if (end < 0) {
            end = lines.Count;
        }

        for (var i = header + 1; i < end; i++) {
            var text = lines[i].Trim();
            if (text.Length == 0 || text[0] is ';' or '#') {
                continue;
            }

            var equals = text.IndexOf('=');
            if (equals > 0 && text[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) {
                lines[i] = $"{key} = {value}";
                return;
            }
        }

        // After the section's last line that is not blank.
        var at = end;
        while (at > header + 1 && lines[at - 1].Trim().Length == 0) {
            at--;
        }

        lines.Insert(at, $"{key} = {value}");
    }

    private void Show() {
        var on = ConfigurationManager.GetValue("StreetQuest.Enabled", false);
        var count = UltimusSeat.StreetCount;
        var lines = new List<string> {
            on ? $"Questing street bots are on: {count} may play." : "Questing street bots are off. .streetbots on 6 turns six on.",
        };
        foreach (var seat in UltimusSeat.Street.Where(s => s.Enabled)) {
            var member = UltimusDirector.MembersOf(seat).FirstOrDefault();
            var level = member?.Wizard?.MagicSchoolBehavior?.Level ?? 0;
            var (doing, _, quest) = UltimusNow.Of(seat);
            lines.Add(member is null
                ? $"{seat.Number}. {seat.Name} ({seat.Role}): starting"
                : $"{seat.Number}. {member.Name} ({seat.Role}, level {level}) in {member.Zone}: {(string.IsNullOrEmpty(quest) ? doing : quest)}");
        }

        InformSenderClient(string.Join("\n", lines), lines.Count > 2);
    }

}
