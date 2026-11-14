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

using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Results.Handlers;

/*
 * Steps of the Beastmoon arenas' scripts (Battlegrounds/BMH/BMH_Zone_02..05 triggers.xml) the engine did not know.
 * The other battleground steps the raids use are in ResRaidHandlers.cs.
 */

/// <summary>
/// ResUnknown5991CB8C (m_value): shows (1) or hides (0) the battleground compass. Read from where the arenas use it: with
/// m_value 1 on the base teleporters, just before a wizard goes out to the field (Trigger_Teleport_2_Red01), and with
/// the default 0 alone in Trigger_RemoveCompass_Red / _Blue, on the way back into a base. The game's message for it is
/// MSG_BATTLEGROUNDSHOWCOMPASS ("Zone server tells client to show the compass", ShowCompass:UBYT). The class name is not
/// in the client data, so this reading is inferred.
/// </summary>
internal sealed class ResUnknown5991CB8CHandler : BaseResultHandler<ResUnknown5991CB8C> {

    public override bool Execute(IResultContext context) {
        var playerRef = context.GetPlayerRef();
        playerRef?.Tell(new WIZARD2_53_PROTOCOL.MSG_BATTLEGROUNDSHOWCOMPASS {
            ShowCompass = (byte) (Result.m_value != 0 ? 1 : 0),
        }, ActorRefs.NoSender);

        return playerRef is not null;
    }

}

/// <summary>
/// ResWizPlayMusic (m_musicName, m_fadeTime): changes the zone's music (the arenas play AV_Glorious_LP in the bases and
/// AV_Tense_LP on the way out). The game has no message the server can send to play a music cue, so the zone keeps its
/// own music; the step is known and lets the steps after it run.
/// </summary>
internal sealed class ResWizPlayMusicHandler : BaseResultHandler<ResWizPlayMusic> {

    public override bool Execute(IResultContext context) => RaidResult.Skip($"ResWizPlayMusic {Result.m_musicName}");

}

/// <summary>
/// ResUnknown1DC65E8F (m_eventName, m_subEventName): posts an event, as ResPostEvent does. A PvE arena posts
/// EVENT_PVE_Complete with it when its last boss falls (Trigger_On_Boss_Death_03), which the match is won on.
/// </summary>
internal sealed class ResUnknown1DC65E8FHandler : BaseResultHandler<ResUnknown1DC65E8F> {

    public override bool Execute(IResultContext context) {
        var zoneActor = context.GetZoneActor();
        if (zoneActor is null || string.IsNullOrEmpty(Result.m_eventName)) {
            return false;
        }

        foreach (var name in new[] { Result.m_eventName, Result.m_subEventName }) {
            if (!string.IsNullOrEmpty(name)) {
                zoneActor.Tell(new ZONE_102_PROTOCOL.MSG_POSTEVENT {
                    EventName = name,
                    PlayerActor = context.GetPlayerRef(),
                    PlayerGameObject = context.GetPlayerObj(),
                }, ActorRefs.NoSender);
            }
        }

        return true;
    }

}

/// <summary>
/// ResUnknown0758F7C9 (m_classProjectName): progress in a class project ("TestBattleground", on the Beastmoon chests
/// and time clams). It names a test project, not the event on (Battlegrounds.PvEEvent); the event's own activities
/// count instead (monsters, rings taken, forms levelled: Game/Events/ClassProjects.cs), so this step is known and skipped.
/// </summary>
internal sealed class ResUnknown0758F7C9Handler : BaseResultHandler<ResUnknown0758F7C9> {

    public override bool Execute(IResultContext context) => RaidResult.Skip($"ResUnknown0758F7C9 {Result.m_classProjectName}");

}

/// <summary>
/// ResLoot (m_lootTable) in a Beastmoon arena: the Coin Clam (BG_Chest-003, "Coin Clam"; its badge counts
/// InteractCoinClam) opens with a ResLoot naming no loot table, then despawns. The game files hold no Beastmoon loot,
/// so the wizard who opened it gets [Beastmoon] CoinClamCoins Battle Coins (Game/Services/BattlegroundService.cs).
/// Elsewhere there is no handler for it yet, as before; it is skipped so the steps after it still run.
/// </summary>
internal sealed class ResLootHandler : BaseResultHandler<ResLoot> {

    public override bool Execute(IResultContext context) {
        var zone = context.GetZoneActor()?.Path.Name ?? "";
        if (!zone.StartsWith("Battlegrounds-", System.StringComparison.OrdinalIgnoreCase)) {
            return RaidResult.Skip("ResLoot outside a battleground");
        }

        context.GetPlayerRef()?.Tell(new ZONE_102_PROTOCOL.MSG_BATTLEGROUNDCONTROL { Kind = BattlegroundControlKind.CoinClam },
            ActorRefs.NoSender);

        return true;
    }

}
