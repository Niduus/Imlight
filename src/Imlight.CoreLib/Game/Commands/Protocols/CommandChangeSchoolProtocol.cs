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
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Auth;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.changeschool &lt;school&gt;` says what changing school would do; `.changeschool &lt;school&gt; yes` does it (see
/// QuestService.ChangeSchool.cs). Every player can, unless [Character] PlayersCanChangeSchool is off: then only staff.
/// </summary>
internal class CommandChangeSchoolProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    private static bool PlayersCanChange => ConfigurationManager.GetValue("Character.PlayersCanChangeSchool", true);

    [Command("changeschool")]
    [Alias("school")]
    private void ChangeSchoolCommand([Remainder] string argument = "") {
        var wizard = Context.Character;
        if (wizard is null) {
            return;
        }

        var words = (argument ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var confirmed = words.Count > 1 && words[^1].ToLowerInvariant() is "yes" or "y" or "confirm";
        if (confirmed) {
            words.RemoveAt(words.Count - 1);
        }

        var current = wizard.MagicSchoolBehavior.MagicSchool;
        var schools = string.Join(", ", QuestService.ChangeableSchools);
        if (words.Count == 0) {
            InformSenderClient($"You are a {current} wizard. Type .changeschool and a school ({schools}) to see what changing "
                + "to it does, such as .changeschool ice.");

            return;
        }

        if (QuestService.FindChangeableSchool(string.Join(' ', words)) is not { } to) {
            InformSenderClient($"Pick one of {schools}, such as .changeschool ice.");

            return;
        }

        if (to == current) {
            InformSenderClient($"You are already a {to} wizard.");

            return;
        }

        if (!PlayersCanChange && !AuthorityRequester.RequestAuthority(AuthLevel.QualityAssurance, Context.Account, "Command changeschool")) {
            InformSenderClient("Only staff can change school on this server.");

            return;
        }

        if (wizard.IsInDuel) {
            InformSenderClient("Finish your battle first, then type .changeschool again.");

            return;
        }

        var name = to.ToString().ToLowerInvariant();
        if (!confirmed) {
            var plan = QuestService.PlanSchoolChange(wizard, to);
            InformSenderClient($"Changing to {to} makes you a {to} wizard and takes your {plan.OldSpells.Count} {current} spells out of "
                + "your spellbook and decks (Spellement upgrades of them too; cantrips, fishing, gardening, dropped lore spells "
                + "and spells of other schools stay). Then you learn the free " + to + " trainer spells your level allows and the "
                + to + " spells from quests at your level"
                + (plan.PointsBack > 0 ? $", and get {plan.PointsBack} training points back." : ". It gives back no training points.")
                + $" Type .changeschool {name} yes to do it.");

            return;
        }

        InformSenderClient($"Changing you to {to}...");
        Context.SessionActor.Tell(new CHARACTER_103_PROTOCOL.MSG_QUESTCOMMAND {
            Action = "changeschool",
            QuestName = to.ToString(),
        });
    }

}
