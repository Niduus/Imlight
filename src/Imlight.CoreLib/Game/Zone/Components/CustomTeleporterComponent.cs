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
 *
 * ========================================================================
 * CUSTOM TELEPORTER COMPONENT
 * ========================================================================
 *
 * PURPOSE:
 * A hub stone or street teleporter of the Customs tab's teleporter hubs (CustomTeleporters.cs): pressing X at it plays
 * the teleport effect and sends the player to the stone it is linked to, in the same zone or another one.
 *
 * NOTE:
 * ZoneEntity adds it to an object whose zone tag is a custom teleporter's, in place of the template's own services.
 * Where it sends is read when it is pressed, so a street teleporter added or moved in the Manager counts at once. A
 * hub stone with no street teleporter yet offers nothing to press.
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/25/2026
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class CustomTeleporterComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    // "Press X to Teleport", the Colossus Boulevard stones' own line.
    private const string TELEPORT_TEXT_KEY = "GUI_00005578";

    // How long the teleport effect plays before the player is moved, as the game's own teleports do.
    private const int EFFECT_DELAY_IN_MS = 1500;

    public string ServiceName     => "Interact";
    public string NpcIcon         => (Entity.Template as GameObjectTemplate)?.m_sIcon;
    public string NpcNameKey      => null;
    public string NpcTextKey      => TELEPORT_TEXT_KEY;
    public WizBangs WizBang       => WizBangs.None;
    public string StateName       => null;
    public string InteractWizBang => null;
    public string DisplayKey      => null;

    // Only ZoneEntity adds it, by zone tag.
    public static bool ShouldAttachToEntity(CoreTemplate template) => false;

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter) {
        if (string.IsNullOrEmpty(CustomTeleporters.For(Entity.Info?.m_zoneTag)?.ToZone)) {
            return [];
        }

        return [new InteractableOption { m_serviceName = ServiceName }];
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        var stone = CustomTeleporters.For(Entity.Info?.m_zoneTag);
        if (stone is null || string.IsNullOrEmpty(stone.ToZone) || playerActor is null) {
            return;
        }

        var spot = string.Join(",", new[] { stone.ToX, stone.ToY, stone.ToZ, stone.ToYaw }.Select(v => v.ToString(CultureInfo.InvariantCulture)));
        var sameZone = string.Equals(stone.ToZone, Entity.Zone?.ZonePath, StringComparison.OrdinalIgnoreCase);
        Logger.Information("Teleporter hubs: {0} uses {1} ({2}) and goes to {3} {4}.",
            Logger.Args(playerCharacter?.CharId ?? 0, stone.Name, stone.Tag, stone.ToZone, spot));

        playerActor.Tell(new CHARACTER_103_PROTOCOL.MSG_DOTELEPORTEFFECTS(), ActorRefs.NoSender);
        IServerMessage move = sameZone
            ? new CHARACTER_103_PROTOCOL.MSG_MOVEINZONE { Location = spot }
            : new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
                DestinationZone = stone.ToZone,
                DestinationLocation = spot,
                SendToClient = true,
                OwnerCharId = playerCharacter?.CharId ?? 0,
            };
        Task.Delay(EFFECT_DELAY_IN_MS).ContinueWith(_ => playerActor.Tell(move, ActorRefs.NoSender));
    }

}
