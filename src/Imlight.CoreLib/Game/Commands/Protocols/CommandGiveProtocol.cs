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

using System.Linq;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// Test unlocks with no group: `.givemetpstyle`, `.intothedarkness` and `.givemeallbadges`.
/// </summary>
internal class CommandGiveProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("givemetpstyle")]
    [Alias("givemetpstyles")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void GiveAllTeleportEffectsCommand() {
        Context.Character.UnlockAllTeleportEffects();

        InformSenderClient("Unlocked every teleport effect. Relog to see them.");
    }

    [Command("givemeallbadges")]
    [Alias("givemebadges")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void GiveAllBadgesCommand() {
        var badges = WizardData.SpiralDB.Badges;
        if (badges.Count == 0) {
            InformSenderClient("The server has no badge list yet (SpiralDB's BadgeDefinitions folder is missing).");

            return;
        }

        var added = Context.Character.AddBadges(badges.Where(b => !b.Retired).Select(b => b.Id));
        InformSenderClient($"Gave you {added} new badges ({Context.Character.Badges.Count} of {badges.Count} in all). " +
                           "Quests and doors that need a badge now count them.");
    }

    [Command("intothedarkness")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void UnlockShadowMagicCommand() {
        Context.Character.UnlockShadowMagic();

        InformSenderClient("You now have shadow magic: shadow pip slots, the shadow gauge and shadow pips in battle. Relog to see it.");
    }

}
