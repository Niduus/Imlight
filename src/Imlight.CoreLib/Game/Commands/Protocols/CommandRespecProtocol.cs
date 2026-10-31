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
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Spells;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.respec` says how many training points a reset gives back and which spells it takes; `.respec yes` does it.
/// Every player can use it (see <see cref="TrainingReset"/>).
/// </summary>
internal class CommandRespecProtocol : CommandProtocol {

    private const int SHOWN_SPELLS = 12;

    internal override string Group { get; set; } = "";

    [Command("respec")]
    [Alias("resettp")]
    private void RespecCommand([Remainder] string argument = "") {
        var bought = TrainingReset.BoughtSpells(Context.Character);
        if (bought.Count == 0) {
            InformSenderClient("You have no training points to get back. Spells of your own school, free spells and spells from quests stay.");

            return;
        }

        if ((argument ?? "").Trim().ToLowerInvariant() is "yes" or "y" or "confirm") {
            var (spells, points) = TrainingReset.Reset(Context.SessionActor, Context.Character);
            InformSenderClient(TrainService.ResetText(spells, points));

            return;
        }

        var names = bought.Take(SHOWN_SPELLS)
            .Select(s => CoreObjectFactory.GetCoreTemplate(s.SpellId) is SpellTemplate t ? (string) t.m_name : s.SpellId.ToString());
        var more = bought.Count > SHOWN_SPELLS ? $" and {bought.Count - SHOWN_SPELLS} more" : "";
        InformSenderClient($"A reset gives back {bought.Sum(s => s.Cost)} training points and takes {bought.Count} spells: "
            + $"{string.Join(", ", names)}{more}. Type .respec yes to do it.");
    }

    // Ahziael's Spells page: takes the boss and dev spells out of the spellbook and decks, with their training points back.
    [Command("unlearnboss")]
    [Alias("unlearndev")]
    private void UnlearnBossCommand([Remainder] string argument = "") {
        var found = BossDevSpells.Of(Context.Character);
        if ((argument ?? "").Trim().ToLowerInvariant() is "yes" or "y" or "confirm") {
            var taken = BossDevSpells.Unlearn(Context.SessionActor, Context.Character);
            var points = taken.Sum(t => t.Points);
            InformSenderClient(taken.Count == 0
                ? "You have no boss or dev spells. Your spellbook is already legit."
                : $"Unlearned {taken.Count} boss and dev spells{(points > 0 ? $" and got {points} training points back" : "")}. "
                  + "Ahziael's Spells page lists them.");

            return;
        }

        if (found.Count == 0) {
            InformSenderClient("You have no boss or dev spells. Your spellbook is already legit.");

            return;
        }

        var names = found.Take(SHOWN_SPELLS).Select(s => s.Name);
        var more = found.Count > SHOWN_SPELLS ? $" and {found.Count - SHOWN_SPELLS} more" : "";
        InformSenderClient($"You know {found.Count} boss and dev spells: {string.Join(", ", names)}{more}. "
            + "Type .unlearnboss yes to take them out of your spellbook and decks.");
    }


    // Niduus, 2026-10-31: unlearn any spell. `.unlearn <spell>` says what it gives back, `.unlearn <spell> yes` does it.
    // Ahziael's Progress > Unlearn Spells sends `.unlearn list` and `.unlearn picked yes`.
    [Command("unlearn")]
    [Alias("unlearnspell")]
    private void UnlearnCommand([Remainder] string argument = "") {
        var wizard = Context.Character;
        var text = (argument ?? "").Trim();
        var confirmed = false;
        foreach (var word in new[] { " yes", " y", " confirm" }) {
            if (text.EndsWith(word, StringComparison.OrdinalIgnoreCase)) {
                text = text[..^word.Length].Trim();
                confirmed = true;
                break;
            }
        }

        if (text.Length == 0 || text.Equals("list", StringComparison.OrdinalIgnoreCase)) {
            SpellUnlearn.WriteBook(wizard);
            InformSenderClient($"You know {SpellUnlearn.Book(wizard).Count} spells. Type .unlearn and a spell's name to see what "
                + "unlearning it gives back, then .unlearn <spell name> yes to do it. Ahziael's Progress > Unlearn Spells page lists them all.");

            return;
        }

        if (text.Equals("picked", StringComparison.OrdinalIgnoreCase)) {
            var picked = SpellUnlearn.ReadPicked(wizard.CharId);
            if (!confirmed || picked.Count == 0) {
                InformSenderClient("Unlearn: pick the spells on Ahziael's Progress > Unlearn Spells page.");

                return;
            }

            var done = SpellUnlearn.Unlearn(Context.SessionActor, wizard, picked);
            SpellUnlearn.WriteResult(wizard.CharId, done);
            SpellUnlearn.WriteBook(wizard);
            InformSenderClient(UnlearnedText(done));

            return;
        }

        var found = SpellUnlearn.Find(wizard, text);
        if (found.Count == 0) {
            InformSenderClient($"You don't know a spell called {text}. Type .unlearn list to see how many you know; Ahziael's "
                + "Progress > Unlearn Spells page lists them all.");

            return;
        }

        // A part of a name that fits spells of different names: say which.
        var names = found.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count > 1) {
            var more = names.Count > SHOWN_SPELLS ? $" and {names.Count - SHOWN_SPELLS} more" : "";
            InformSenderClient($"{names.Count} of your spells fit \"{text}\": {string.Join(", ", names.Take(SHOWN_SPELLS))}{more}. "
                + "Type .unlearn and the full name.");

            return;
        }

        if (!confirmed) {
            var back = found.Sum(s => s.PointsBack);
            var copies = found.Count > 1 ? $" ({found.Count} versions)" : "";
            InformSenderClient($"Unlearning {names[0]}{copies} takes it out of your spellbook and decks"
                + (back > 0 ? $" and gives back {back} training points." : ". It gives back no training points (it was free).")
                + $" Type .unlearn {names[0]} yes to do it.");

            return;
        }

        var taken = SpellUnlearn.Unlearn(Context.SessionActor, wizard, found.Select(s => s.SpellId));
        SpellUnlearn.WriteBook(wizard);
        InformSenderClient(UnlearnedText(taken));
    }

    private static string UnlearnedText(List<SpellUnlearn.Known> taken) {
        if (taken.Count == 0) {
            return "Nothing was unlearned: you don't know those spells any more.";
        }

        var points = taken.Sum(t => t.PointsBack);
        var names = string.Join(", ", taken.Select(t => t.Name).Distinct().Take(SHOWN_SPELLS));

        return (taken.Count == 1 ? $"Unlearned {names}" : $"Unlearned {taken.Count} spells ({names}{(taken.Count > SHOWN_SPELLS ? ", ..." : "")})")
            + (points > 0 ? $" and got {points} training points back." : ".");
    }

}
