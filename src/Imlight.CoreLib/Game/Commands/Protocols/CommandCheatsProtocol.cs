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
using System.Linq;
using System.Text;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.cheats` lists the player cheats you have and whether each is on (the same as `.cheat list`); `.cheat on <name>` and
/// `.cheat off <name>` switch one the server owner lets players switch (Niduus, 2026-10-28). Chat starting with a dot
/// never shows to anyone else.
/// </summary>
internal class CommandCheatsProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("cheats")]
    [Alias("mycheats")]
    private void CheatsCommand() => InformSenderClient(PlayerCheatChat.List(Context.Character));

}

/// <summary>The answers of `.cheats`, `.cheat on` and `.cheat off`.</summary>
internal static class PlayerCheatChat {

    internal static string List(Wizard wizard) {
        if (wizard is null) {
            return "Pick a wizard first.";
        }

        var cheats = PlayerCheats.Owned(wizard.CharId);
        if (cheats.Count == 0) {
            return "You have no cheats on this server.";
        }

        var text = new StringBuilder("Your cheats:");
        foreach (var cheat in cheats) {
            var label = PlayerCheatSwitches.LabelOf(cheat);
            var on = PlayerCheatSwitches.IsOn(wizard.CharId, cheat);
            text.Append('\n').Append(label).Append(": ").Append(on ? "on" : "off");
            text.Append(cheat.PlayersSwitch ? $" (type .cheat {(on ? "off" : "on")} {label})" : " (always on)");
        }

        return text.ToString();
    }

    internal static string Switch(Wizard wizard, string which, bool on) {
        if (wizard is null) {
            return "Pick a wizard first.";
        }

        which = (which ?? "").Trim();
        var word = on ? "on" : "off";
        if (which.Length == 0) {
            return $"Type .cheat {word} and a cheat's name, such as .cheat {word} Cheat 1, or .cheat {word} all. Type .cheats to see yours.";
        }

        var cheats = PlayerCheats.Owned(wizard.CharId);
        if (which.Equals("all", StringComparison.OrdinalIgnoreCase)) {
            var switchable = cheats.Where(c => c.PlayersSwitch).ToList();
            if (switchable.Count == 0) {
                return "None of your cheats can be switched by you.";
            }

            foreach (var cheat in switchable) {
                PlayerCheatSwitches.Set(wizard.CharId, cheat, on);
            }

            return $"All {switchable.Count} of your switchable cheats are {word}. It counts from your next move.";
        }

        // By name, else by number ("3" or "cheat 3").
        var number = which.StartsWith("cheat ", StringComparison.OrdinalIgnoreCase) ? which[6..].Trim() : which;
        var found = cheats.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Name) && c.Name.Trim().Equals(which, StringComparison.OrdinalIgnoreCase))
                    ?? (int.TryParse(number, out var n) ? cheats.FirstOrDefault(c => c.Number == n) : null);
        if (found is null) {
            return $"You have no cheat called \"{which}\". Type .cheats to see yours.";
        }

        var label = PlayerCheatSwitches.LabelOf(found);
        if (!found.PlayersSwitch) {
            return $"{label} is always on; only the server owner can turn it off.";
        }

        PlayerCheatSwitches.Set(wizard.CharId, found, on);

        return $"{label} is {word} for you. It counts from your next move.";
    }

}
