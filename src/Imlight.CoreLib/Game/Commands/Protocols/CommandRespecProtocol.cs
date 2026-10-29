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

}
