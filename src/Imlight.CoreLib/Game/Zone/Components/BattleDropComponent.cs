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
using Akka.Actor;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// A wisp dropped after a battle disappears if nobody picks it up in time.
/// </summary>
internal sealed class BattleDropComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IComponentFactory, IWithTimers {

    private const string EXPIRE_TIMER_KEY = "BattleDropExpire";
    private static readonly TimeSpan s_lifetime = TimeSpan.FromMinutes(2);

    public ITimerScheduler Timers { get; set; }

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate goTemplate && goTemplate.m_objectName.ToString().Contains("Wisp");

    public override void OnStart() {
        if (Entity.ActiveGameObject?.m_zoneTagID == BattleDrops.DropTagId) {
            Timers.StartSingleTimer(EXPIRE_TIMER_KEY, new ZONE_102_PROTOCOL.MSG_BATTLEDROPEXPIRED(), s_lifetime);
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_BATTLEDROPEXPIRED))]
    private void ReceiveExpired(ZONE_102_PROTOCOL.MSG_BATTLEDROPEXPIRED message)
        => Entity.DeleteObject("WispDespawn");

}
