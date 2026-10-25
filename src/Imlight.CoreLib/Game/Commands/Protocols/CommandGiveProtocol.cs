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
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Shared.Packets;
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

    [Command("tpstyle")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void EquipTeleportEffectCommand(string number) {
        // The game takes the teleport effect item's template id, such as 1457191 (Shadowstride).
        if (!uint.TryParse(number, out var effect)) {
            InformSenderClient("Type a teleport effect item's id, such as .tpstyle 1457191 for Shadowstride (0 is the normal sparkle).");

            return;
        }

        Context.Character.EquipTeleportEffect(effect);

        // The same request the game makes, so everyone in the zone is told right away.
        Context.SessionActor.Tell(new WIZARD2_53_PROTOCOL.MSG_UPDATETELEPORTEFFECT(), ActorRefs.NoSender);

        InformSenderClient($"Teleport effect set to {effect} (0 is the normal sparkle). Teleport to see it.");
    }

    [Command("overdose")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void OverdoseCommand(string onOrOff) {
        var on = onOrOff?.Trim().ToLowerInvariant() switch {
            "on" or "true" or "yes" or "1" => true,
            "off" or "false" or "no" or "0" => (bool?) false,
            _ => null,
        };
        if (on is null) {
            InformSenderClient("Type .overdose on or .overdose off");

            return;
        }

        Context.Character.SetOverdose(on.Value);
        InformSenderClient(on.Value
            ? "Overdose on: your battles start with Quickening already active."
            : "Overdose off: your battles start normally.");
    }

    [Command("givemeallbadges")]
    [Alias("givemebadges")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void GiveAllBadgesCommand() {
        Context.SessionActor.Tell(new CHARACTER_103_PROTOCOL.MSG_BADGECOMMAND { Action = "grantall", Argument = string.Empty });
    }

    [Command("intothedarkness")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void UnlockShadowMagicCommand() {
        // Like the game: unlocking shadow magic gives one slot; .sp slots 2 gives the second.
        Context.Character.UnlockShadowMagic(1);

        InformSenderClient("You now have shadow magic with 1 shadow pip slot (.sp slots 2 for a second). Relog to see it.");
    }

}
