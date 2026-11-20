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
using System.Globalization;
using System.IO;
using System.Linq;
using Imlight.Common;
using Imlight.CoreLib.Game.Ultimus;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// The Kitharas Family from the game's chat (Niduus, 2026-11-20: twenty more bots that work the same as the TCO7). Until
/// Ahziael has a page for them, this turns them on and off and sets how many play: ".kitharas" says how they are doing,
/// ".kitharas on" turns them on (5 at first, so the PC is not loaded with twenty at once), ".kitharas on 20" or
/// ".kitharas 10" sets how many, ".kitharas off" logs them all out, ".kitharas off Moon" / ".kitharas on Moon" switches one,
/// and ".kitharas squad Moon, Hart, Rock, Hydra" makes a squad (".kitharas squad off" ends it). It writes [Kitharas] in
/// Imlight.ini (adding it when the file has none yet), and the server reads the file again by itself, so a change works
/// within seconds, without a restart. Administrators only.
/// </summary>
internal class CommandKitharas : CommandProtocol {

    internal override string Group { get; set; } = "";

    private static readonly object s_writeLock = new();

    private static string IniPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "Imlight.ini");

    private const string SECTION = UltimusSeat.KITHARAS_TEAM;

    [Command("kitharas")]
    [Alias("kf", "kitharasfamily")]
    [AuthRequired(AuthLevel.Administrator)]
    private void KitharasCommand([Remainder] string words = "") {
        var text = (words ?? "").Trim();
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var first = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var rest = parts.Length > 1 ? string.Join(' ', parts[1..]) : "";
        int? count = int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n
            : int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : null;
        switch (first) {
            case "" or "list" or "where" or "status":
                Show();
                return;
            case "squad" or "team":
                Squad(rest);
                return;
            case "on" or "start" when rest.Length > 0 && count is null:
                SwitchOne(rest, true);
                return;
            case "off" or "stop" when rest.Length > 0:
                SwitchOne(rest, false);
                return;
            case "on" or "start":
                Save(on: true, count);
                return;
            case "off" or "stop":
                Save(on: false, null);
                return;
            default:
                if (count is not null) {
                    Save(null, count);
                    return;
                }

                InformSenderClient(".kitharas shows the Kitharas Family. .kitharas on turns them on (5 at first), .kitharas 20 sets how many play, " +
                                   ".kitharas off logs them all out, .kitharas off Moon / on Moon switches one, .kitharas squad Moon, Hart, Rock makes a squad.");
                return;
        }
    }

    private void Save(bool? on, int? count) {
        if (count is { } wanted && (wanted < 0 || wanted > UltimusSeat.Kitharas.Length)) {
            InformSenderClient($"Pick a number from 0 to {UltimusSeat.Kitharas.Length}.");
            return;
        }

        var changes = new List<(string Key, string Value)>();
        if (on is { } enabled) {
            changes.Add(("Enabled", enabled ? "true" : "false"));
        }

        if (count is { } number) {
            changes.Add(("Count", number.ToString(CultureInfo.InvariantCulture)));
        }

        if (!Write(changes)) {
            return;
        }

        var playing = on == false ? 0
            : count ?? Math.Clamp(ConfigurationManager.GetValue(SECTION + ".Count", UltimusSeat.KITHARAS_DEFAULT_COUNT), 0, UltimusSeat.Kitharas.Length);
        InformSenderClient(on == false
            ? "The Kitharas Family is off. They log out within a few seconds."
            : on == true || ConfigurationManager.GetValue(SECTION + ".Enabled", false)
                ? $"The Kitharas Family is on: {playing} of the 20 play. They start (or log out) within a few seconds; each brand new one starts at level 1 in Wizard City."
                : $"Saved: {playing} of the Kitharas Family play once they are on (.kitharas on).");
    }

    private void SwitchOne(string who, bool on) {
        if (UltimusSeat.Named(who) is not { IsKitharas: true } seat) {
            InformSenderClient($"Nobody in the Kitharas Family is called {who}. Use a name or its own word: {string.Join(", ", UltimusSeat.Kitharas.Select(s => s.CallName))}.");
            return;
        }

        if (!Write([(seat.Key, on ? "true" : "false")])) {
            return;
        }

        InformSenderClient(on
            ? $"{seat.Name} may play again{(ConfigurationManager.GetValue(SECTION + ".Enabled", false) ? "" : " once the family is on (.kitharas on)")}. Only the first [Kitharas] Count of those switched on play."
            : $"{seat.Name} is switched off and logs out within a few seconds; the next one in the family takes the seat if the count allows.");
    }

    private void Squad(string names) {
        names = names.Trim();
        if (names.Length == 0) {
            var squad = UltimusTco7.SquadOf(SECTION);
            InformSenderClient(squad.Count < 2
                ? "No Kitharas Family squad: each one quests at its own pace. .kitharas squad Moon, Hart, Rock, Hydra makes one."
                : $"Kitharas Family squad: {string.Join(", ", squad.Select(s => s.Name))}."
                  + (UltimusTco7.CaptainOf(SECTION) is { } captain ? $" {captain.Name} leads (furthest behind in the story); the others help it catch up." : " Fewer than two are playing, so it waits."));
            return;
        }

        if (names.Equals("off", StringComparison.OrdinalIgnoreCase) || names.Equals("none", StringComparison.OrdinalIgnoreCase)) {
            if (Write([("Squad", "")])) {
                UltimusTco7.SetSquadOverride(SECTION, null);
                InformSenderClient("The Kitharas Family squad is ended: each one quests at its own pace again.");
            }

            return;
        }

        var parts = names.Contains(',') ? names.Split(',', StringSplitOptions.RemoveEmptyEntries) : names.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var picked = parts.Select(p => UltimusSeat.Named(p.Trim())).Where(s => s is { IsKitharas: true }).Distinct().Take(4).ToList();
        if (picked.Count < 2) {
            InformSenderClient("A squad needs two to four of the Kitharas Family: .kitharas squad Moon, Hart, Rock, Hydra");
            return;
        }

        var value = string.Join(", ", picked.Select(s => s.CallName));
        if (Write([("Squad", value)])) {
            UltimusTco7.SetSquadOverride(SECTION, null);
            InformSenderClient($"Kitharas Family squad: {string.Join(", ", picked.Select(s => s.Name))}. The one furthest behind leads and the others help it catch up, then they quest on together.");
        }
    }

    private bool Write(List<(string Key, string Value)> changes) {
        if (changes.Count == 0) {
            return true;
        }

        try {
            lock (s_writeLock) {
                var lines = File.ReadAllLines(IniPath).ToList();
                foreach (var (key, value) in changes) {
                    Set(lines, key, value);
                }

                File.WriteAllLines(IniPath, lines);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            InformSenderClient($"Imlight.ini could not be changed: {e.Message}");
            return false;
        }
    }

    /// <summary>Sets one key of [Kitharas], adding the key, or the whole section with a note, when the file has none.</summary>
    internal static void Set(List<string> lines, string key, string value) {
        var header = lines.FindIndex(l => l.Trim().Equals("[" + SECTION + "]", StringComparison.OrdinalIgnoreCase));
        if (header < 0) {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) {
                lines.Add("");
            }

            lines.Add("[" + SECTION + "]");
            lines.Add("; The Kitharas Family: twenty bots that play the Main Story like the TCO7, each with Ultimus's brain. Enabled turns");
            lines.Add("; them on; Count is how many play at once (0-20, 5 unless set). Each one's own switch is its key (KitharasMoon = false");
            lines.Add("; keeps Kitharas Moon off). Talk, TeamUp and Squad work as the TCO7's. Live, no restart.");
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
        var on = ConfigurationManager.GetValue(SECTION + ".Enabled", false);
        var lines = new List<string> {
            on ? $"The Kitharas Family is on: {UltimusSeat.KitharasCount} of the 20 may play." : "The Kitharas Family is off. .kitharas on turns them on (5 at first).",
        };
        foreach (var seat in UltimusSeat.Kitharas) {
            var member = UltimusDirector.MembersOf(seat).FirstOrDefault();
            var level = member?.Wizard?.MagicSchoolBehavior?.Level ?? 0;
            var (doing, _, quest) = UltimusNow.Of(seat);
            lines.Add(!seat.Enabled
                ? $"{seat.Number}. {seat.Name} ({seat.Role}): not playing"
                : member is null
                    ? $"{seat.Number}. {seat.Name} ({seat.Role}): starting"
                    : $"{seat.Number}. {seat.Name} ({seat.Role}, level {level}) in {member.Zone}: {(string.IsNullOrEmpty(quest) ? doing : quest)}");
        }

        InformSenderClient(string.Join("\n", lines), true);
    }

}
