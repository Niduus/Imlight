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
 * A portal (Portals.json) also has its own prompt (name line and second line, read each time the prompt is sent), its
 * own reach, and can send whoever walks into it instead (like a house teleporter). A player who arrives inside a
 * walk-in portal has to step out of it before it sends them.
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/25/2026
 */

using System;
using System.Collections.Concurrent;
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

    // How long the teleport effect plays before the player is moved, as the game's own teleports do.
    private const int EFFECT_DELAY_IN_MS = 1500;

    // How high above or below a walk-in portal a player can be and still be in it.
    private const float WALK_HEIGHT = 150f;

    // A player who was just sent by a teleporter of the Customs tab: a walk-in portal they arrive in does not send them
    // on until they have stepped out of it.
    private static readonly ConcurrentDictionary<ulong, DateTime> s_sentAt = new();
    private static readonly TimeSpan s_arrivalGrace = TimeSpan.FromSeconds(6);

    // The players standing in this walk-in portal (sent, or waiting to step out), by player object id.
    private readonly HashSet<ulong> _inside = [];

    private CustomTeleporters.Stone Stone => CustomTeleporters.For(Entity.Info?.m_zoneTag);

    public string ServiceName     => "Interact";
    public string NpcIcon         => (Entity.Template as GameObjectTemplate)?.m_sIcon;
    public string NpcNameKey      => null;
    public string NpcTextKey      => Stone?.TextKey ?? CustomTeleporters.TELEPORT_TEXT_KEY;
    public WizBangs WizBang       => WizBangs.None;
    public string StateName       => null;
    public string InteractWizBang => null;
    public string DisplayKey      => null;

    // A portal's own reach; a hub stone keeps the game's usual one.
    public float DEFAULT_INTERACTION_RADIUS => Stone is { Reach: > 0 } stone ? stone.Reach : 300f;

    // Only ZoneEntity adds it, by zone tag.
    public static bool ShouldAttachToEntity(CoreTemplate template) => false;

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter) {
        // A walk-in portal has nothing to press: walking in sends.
        if (Stone is not { } stone || string.IsNullOrEmpty(stone.ToZone) || stone.WalkIn) {
            return [];
        }

        return [new InteractableOption { m_serviceName = ServiceName }];
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        var stone = Stone;
        if (stone is null || string.IsNullOrEmpty(stone.ToZone) || playerActor is null || stone.WalkIn) {
            return;
        }

        Send(stone, playerActor, playerCharacter, playerObject, "uses");
    }

    public override void OnPlayerJoin(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        // Coming into the zone inside a walk-in portal (sent here, or logged in on it) does not send them on.
        if (playerObj is not null && Stone is { WalkIn: true } stone && IsIn(stone, playerObj)) {
            _inside.Add(playerObj.m_globalID.Full);
        }
    }

    public override void OnPlayerLeave(IActorRef playerActor, ulong id) => _inside.Remove(id);

    public override void OnPlayerMove(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (playerObj is null || Stone is not { WalkIn: true } stone) {
            _inside.Clear();

            return;
        }

        var id = playerObj.m_globalID.Full;
        if (!IsIn(stone, playerObj)) {
            _inside.Remove(id);

            return;
        }

        if (!_inside.Add(id)) {
            return;
        }

        // Just sent here by a teleporter: they have to step out first.
        if (s_sentAt.TryGetValue(id, out var sentAt) && DateTime.UtcNow - sentAt < s_arrivalGrace) {
            return;
        }

        if (string.IsNullOrEmpty(stone.ToZone) || playerActor is null) {
            return;
        }

        Send(stone, playerActor, playerWizard, playerObj, "walks into");
    }

    private bool IsIn(CustomTeleporters.Stone stone, CoreObject playerObj)
        => IsInHorizontalRadius(playerObj, stone.Reach > 0 ? stone.Reach : 120f)
           && Math.Abs(playerObj.m_location.Z - Entity.ActiveGameObject.m_location.Z) <= WALK_HEIGHT;

    private void Send(CustomTeleporters.Stone stone, IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, string how) {
        var spot = string.Join(",", new[] { stone.ToX, stone.ToY, stone.ToZ, stone.ToYaw }.Select(v => v.ToString(CultureInfo.InvariantCulture)));
        var sameZone = string.Equals(stone.ToZone, Entity.Zone?.ZonePath, StringComparison.OrdinalIgnoreCase);
        Logger.Information("Teleporter hubs: {0} {5} {1} ({2}) and goes to {3} {4}.",
            Logger.Args(playerCharacter?.CharId ?? 0, CustomTeleporters.NameOf(stone), stone.Tag, stone.ToZone, spot, how));

        if (playerObject is not null) {
            // Marked from the moment it starts, so the effect's wait never lets a walk-in portal at the far end send
            // them on as they land.
            s_sentAt[playerObject.m_globalID.Full] = DateTime.UtcNow.AddMilliseconds(EFFECT_DELAY_IN_MS);
        }

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
