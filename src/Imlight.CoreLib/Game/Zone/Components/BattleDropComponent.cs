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
using System.Linq;
using Akka.Actor;
using Imcodec.Math;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// A wisp dropped after a battle wanders around where it fell, and disappears if nobody picks it up in time.
/// </summary>
internal sealed class BattleDropComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IComponentFactory, IWithTimers {

    private const string EXPIRE_TIMER_KEY = "BattleDropExpire";
    private const string WANDER_TIMER_KEY = "BattleDropWander";
    private const string TICK_TIMER_KEY = "BattleDropTick";
    private static readonly TimeSpan s_lifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_tick = TimeSpan.FromMilliseconds(250);

    // How far from where it fell a wisp wanders, how fast when its template has no speed, and how long it rests.
    private const float WANDER_RADIUS = 150f;
    private const float DEFAULT_SPEED = 60f;
    private const int MIN_REST_MS = 500;
    private const int MAX_REST_MS = 2500;

    public ITimerScheduler Timers { get; set; }

    private Vector3 _home;
    private Vector3 _from;
    private Vector3 _to;
    private DateTime _setOff;
    private float _speed;

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate goTemplate && goTemplate.m_objectName.ToString().Contains("Wisp");

    public override void OnStart() {
        if (Entity.ActiveGameObject?.m_zoneTagID != BattleDrops.DropTagId) {
            return;
        }

        Timers.StartSingleTimer(EXPIRE_TIMER_KEY, new ZONE_102_PROTOCOL.MSG_BATTLEDROPEXPIRED(), s_lifetime);

        // Niduus, 2026-10-27: "have them wonder around". At the template's own path speed when it has one.
        var pathMovement = Entity.Template.m_behaviors?.OfType<PathMovementBehaviorTemplate>().FirstOrDefault();
        var speed = pathMovement is null ? 0f : pathMovement.m_movementSpeed * Math.Max(pathMovement.m_movementScale, 0.1f);
        _speed = speed > 0f ? speed : DEFAULT_SPEED;
        _home = _from = _to = Entity.ActiveGameObject.m_location;
        _setOff = DateTime.Now;
        Timers.StartSingleTimer(WANDER_TIMER_KEY, new ZONE_102_PROTOCOL.MSG_BATTLEDROPWANDER(),
            TimeSpan.FromMilliseconds(Random.Shared.Next(MIN_REST_MS, MAX_REST_MS)));
        Timers.StartPeriodicTimer(TICK_TIMER_KEY, new ZONE_102_PROTOCOL.MSG_BATTLEDROPTICK(), s_tick, s_tick);
    }

    public override void OnPlayerJoin(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (Entity.ActiveGameObject?.m_zoneTagID == BattleDrops.DropTagId && Vector3.Distance(_from, _to) > 0.001f) {
            playerActor.Tell(MoveMessage(_to));
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_BATTLEDROPEXPIRED))]
    private void ReceiveExpired(ZONE_102_PROTOCOL.MSG_BATTLEDROPEXPIRED message)
        => Entity.DeleteObject("WispDespawn");

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_BATTLEDROPWANDER))]
    private void ReceiveWander(ZONE_102_PROTOCOL.MSG_BATTLEDROPWANDER message) {
        // A new spot near where it fell, from where it is now.
        _from = Position();
        var angle = Random.Shared.NextSingle() * MathF.Tau;
        var distance = MathF.Sqrt(Random.Shared.NextSingle()) * WANDER_RADIUS;
        _to = new Vector3(_home.X + (MathF.Cos(angle) * distance), _home.Y + (MathF.Sin(angle) * distance), _home.Z);
        _setOff = DateTime.Now;
        Entity.ActiveGameObject.m_location = _from;

        Entity.ZoneRef.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = new GAME_5_PROTOCOL.MSG_MOVESTATE { GlobalID = Entity.ActiveGameObject.m_globalID, NewState = 0 },
            Targets = ZoneBroadcastTarget.Players,
        });
        Entity.ZoneRef.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = MoveMessage(_to),
            Targets = ZoneBroadcastTarget.Players,
        });

        var travelMs = (int) (Vector3.Distance(_from, _to) / _speed * 1000);
        Timers.StartSingleTimer(WANDER_TIMER_KEY, new ZONE_102_PROTOCOL.MSG_BATTLEDROPWANDER(),
            TimeSpan.FromMilliseconds(travelMs + Random.Shared.Next(MIN_REST_MS, MAX_REST_MS)));
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_BATTLEDROPTICK))]
    private void ReceiveTick(ZONE_102_PROTOCOL.MSG_BATTLEDROPTICK message)
        => Entity.ActiveGameObject.m_location = Position();

    // Where the wisp is now on its way from _from to _to, as the game draws it.
    private Vector3 Position() {
        var total = Vector3.Distance(_from, _to);
        if (total < 0.001f) {
            return _to;
        }

        var t = Math.Clamp((float) (DateTime.Now - _setOff).TotalSeconds * _speed / total, 0f, 1f);

        return new Vector3(_from.X + (t * (_to.X - _from.X)), _from.Y + (t * (_to.Y - _from.Y)), _from.Z + (t * (_to.Z - _from.Z)));
    }

    private GAME_5_PROTOCOL.MSG_SERVERMOVE MoveMessage(Vector3 to) {
        var yaw = MathF.Atan2(to.Y - _from.Y, to.X - _from.X);
        if (yaw < 0) {
            yaw += MathF.Tau;
        }

        return new() {
            Direction = (byte) (yaw / MathF.Tau * 250),
            LocationX = unchecked((ushort) (short) MathF.Round(to.X / 4.0f)),
            LocationY = unchecked((ushort) (short) MathF.Round(to.Y / 4.0f)),
            LocationZ = unchecked((ushort) (short) MathF.Round(to.Z / 4.0f)),
            MobileID = Entity.MobileID,
        };
    }

}
