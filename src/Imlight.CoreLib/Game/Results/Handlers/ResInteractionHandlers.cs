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
 * Zone trigger and quest results that went unhandled (Niduus 2026-10-27: "make sure the server is accounting all
 * interactions, especially in puzzle quests").
 */

using System;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Results.Handlers;

/// <summary>
/// ResReInteract: the zone changed what its objects offer (a teleport stone switched on, a puzzle piece moved on a
/// step), so every NPC and object near the player sends its options again. Without it the old prompt stayed until
/// the player walked away and back (44 triggers: Celestia/Dragonspyre/Zafaria teleport stones, Azteca's cure, Avalon's
/// Innes switch).
/// </summary>
internal sealed class ResReInteractHandler : BaseResultHandler<ResReInteract> {

    public override bool Execute(IResultContext context) {
        var zoneActor = context.GetZoneActor();
        var playerActor = context.GetPlayerRef();
        if (zoneActor is null || playerActor is null) {
            return false;
        }

        zoneActor.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Messages = [new ZONE_102_PROTOCOL.MSG_REFRESHSERVICEOPTIONS { PlayerActor = playerActor }],
            Targets = ZoneBroadcastTarget.Objects,
        }, ActorRefs.NoSender);

        return true;
    }

}

/// <summary>
/// ResStateChange: puts a named zone object into a state, as ResModifyTriggerObject does (Marleybone's powered-down
/// golem, Empyrea's essence machine, Morganthe at Dido's mausoleum).
/// </summary>
internal sealed class ResStateChangeHandler : BaseResultHandler<ResStateChange> {

    public override bool Execute(IResultContext context) {
        var zoneActor = context.GetZoneActor();
        if (zoneActor is null || Result is null || string.IsNullOrEmpty(Result.m_triggerObjName)) {
            return false;
        }

        zoneActor.Tell(new ZONE_102_PROTOCOL.MSG_MODIFYTRIGGEROBJECT {
            ObjectName = Result.m_triggerObjName.ToString(),
            StateName = Result.m_triggerObjState.ToString(),
            PlayerActor = context.GetPlayerRef(),
            PlayerGameObject = context.GetPlayerObj(),
            PlayerSpawned = context.IsPlayerSpawn(),
        }, ActorRefs.NoSender);

        return true;
    }

}

/// <summary>
/// ResAddGold: a trigger's gold reward (Karanahn Palace's chest gives 500).
/// </summary>
internal sealed class ResAddGoldHandler : BaseResultHandler<ResAddGold> {

    private const float QUERY_WIZARD_TIMEOUT_SECONDS = 5.0f;

    public override bool Execute(IResultContext context) {
        var playerActor = context.GetPlayerRef();
        if (playerActor is null || Result is null || Result.m_gold <= 0) {
            return false;
        }

        var wizard = playerActor.Ask<CHARACTER_103_PROTOCOL.MSG_CHARACTER>(new CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD(),
            TimeSpan.FromSeconds(QUERY_WIZARD_TIMEOUT_SECONDS)).Result?.Wizard;
        if (wizard is not Wizard) {
            Logger.Error("ResAddGold could not get the wizard within {0} seconds.", Logger.Args(QUERY_WIZARD_TIMEOUT_SECONDS));

            return false;
        }

        wizard.AddGold(Result.m_gold);
        playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
            Gold = wizard.GameStats.m_currentGold,
            MaxGold = wizard.GameStats.m_baseGoldPouch,
        });

        return true;
    }

}

/// <summary>
/// ResMarkZoneNoWarn, ResDownloadPackage and ResDownloadElement only matter to the game itself (it fetches its own
/// files and warnings); the server has nothing to do, so they no longer show as unhandled in the log.
/// </summary>
internal sealed class ResMarkZoneNoWarnHandler : BaseResultHandler<ResMarkZoneNoWarn> {

    public override bool Execute(IResultContext context) => true;

}

/// <inheritdoc cref="ResMarkZoneNoWarnHandler"/>
internal sealed class ResDownloadPackageHandler : BaseResultHandler<ResDownloadPackage> {

    public override bool Execute(IResultContext context) => true;

}

/// <inheritdoc cref="ResMarkZoneNoWarnHandler"/>
internal sealed class ResDownloadElementHandler : BaseResultHandler<ResDownloadElement> {

    public override bool Execute(IResultContext context) => true;

}
