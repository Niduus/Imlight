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
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;
using NVector3 = System.Numerics.Vector3;

namespace Imlight.CoreLib.Game.BotPlayers;

/// <summary>
/// The body of one bot in the world: what the players' games see. It is a wizard object like a real player's (sent with
/// MSG_NEWOBJECT, moved with MSG_SERVERMOVE, taken away with MSG_REMOVEOBJECT), and to a battle it is a wizard on a
/// players' circle: the battle asks it for its wizard like it asks a player's session (MSG_QUERYACTIVEWIZARD), and every
/// message a battle sends a player's game ends here and is dropped. The brain (BotBrain, the mini server) decides how it
/// walks; the body does the walking. It leaves when no real player is left in its area, when its area closes, when it
/// loses a battle, when it is sent away, or when Bot Players is turned off.
/// </summary>
internal sealed class BotBody : ReceiveActor, IWithTimers {

    internal sealed record Info(int Id, string Name, Wizard Wizard, IActorRef Zone, string ZonePath, IActorRef Owner);

    private sealed record Step;
    private sealed record LookAround;
    private sealed record JoinNow(IActorRef Battle, string For);

    private const string STEP_TIMER = "step";
    private const string LOOK_TIMER = "look";
    private const string JOIN_TIMER = "join";

    // How far apart the points of a walk are snapped to the zone's walk grid, and how close to a wall a point may be.
    private const float NAV_SNAP = 140f;
    private const float NAV_HEIGHT = 90f;
    private const float WALL_CLEARANCE = 40f;

    public ITimerScheduler Timers { get; set; }

    private readonly Info _info;
    private readonly Wizard _wizard;
    private readonly CoreObjectSerializer _serializer = new(versionable: false, behaviors: SerializerFlags.None);
    private WizClientObject _object;
    private ushort _mobileId;
    private bool _gone;

    // The walk: its points, the next one, how fast and how long it stops at each.
    private readonly List<NVector3> _loop = [];
    private int _next;
    private float _speed = 260;
    private float _pause = 2;

    // In a battle (or on its way to one): it stands still until the battle no longer has it.
    private bool _inBattle;
    private DateTime _battleSince;

    public BotBody(Info info) {
        _info = info;
        _wizard = info.Wizard;

        Receive<CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD>(_ => Sender.Tell(new CHARACTER_103_PROTOCOL.MSG_CHARACTER {
            Wizard = _wizard,
            WizardGameObject = _object,
        }));
        Receive<BotShowTo>(m => ShowTo(m.Player));
        Receive<Step>(_ => WalkOn());
        Receive<LookAround>(_ => Look());
        Receive<BotGoJoin>(GoJoin);
        Receive<JoinNow>(m => JoinBattle(m.Battle, m.For));
        Receive<BotSeated>(Seated);
        Receive<BotNotSeated>(m => {
            _inBattle = false;
            BotLink.Note(_info.Id, _info.Name, "not seated", m.Why);
            Timers.StartSingleTimer(STEP_TIMER, new Step(), TimeSpan.FromSeconds(1));
        });
        Receive<BotLeave>(m => Leave(m.Why));
        // Out of its battle: kicked (it fled), defeated, or sent out. It steps off the circle at once.
        Receive<COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT>(m => OutOfBattle(m.DiedInDuel ? "defeated" : "left the battle"));
        Receive<Terminated>(_ => Leave("its area closed"));
        // Everything a battle or the zone sends a player's game (combat messages, health, victory, defeat...) ends here.
        ReceiveAny(_ => { });
    }

    protected override void PreStart() {
        try {
            var reply = _info.Zone.Ask<ZONE_102_PROTOCOL.MSG_GETRESERVEDMOBILEIDRSP>(
                new ZONE_102_PROTOCOL.MSG_GETRESERVEDMOBILEID(), TimeSpan.FromSeconds(3)).Result;
            _mobileId = reply.MobileID;
        }
        catch (Exception e) {
            Logger.Warning("Bot Players | bot #{0} got no place in its area: {1}", Logger.Args(_info.Id, e.GetBaseException().Message));
        }

        if (_mobileId == 0) {
            Leave("its area gave it no place");

            return;
        }

        _wizard.GameObject.m_nMobileID = _mobileId;
        _object = WizardObjectLoader.GetPlayerGameObject(_wizard);
        _object.m_nMobileID = _mobileId;

        Context.Watch(_info.Zone);

        if (NewObjectMessage() is { } spawn) {
            Broadcast(spawn);
        }

        PlanWalk();
        Timers.StartPeriodicTimer(LOOK_TIMER, new LookAround(), TimeSpan.FromSeconds(5));
        Timers.StartSingleTimer(STEP_TIMER, new Step(), TimeSpan.FromSeconds(2));
    }

    protected override void PostStop() {
        if (!_gone) {
            Leave("its body stopped", stop: false);
        }

        base.PostStop();
    }

    // ---------------------------------------------------------------------
    // Being seen
    // ---------------------------------------------------------------------

    private GAME_5_PROTOCOL.MSG_NEWOBJECT NewObjectMessage() {
        _object.m_location = _wizard.Location;
        _object.m_orientation = _wizard.Orientation;
        var flags = PropertyFlags.Prop_Public | PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;
        if (!_serializer.Serialize(_object, flags, out var data)) {
            Logger.Error("Bot Players | bot #{0} ({1}): its wizard object could not be written for the games.", Logger.Args(_info.Id, _info.Name));

            return null;
        }

        return new GAME_5_PROTOCOL.MSG_NEWOBJECT { Data = data };
    }

    private void ShowTo(IActorRef player) {
        if (_gone || NewObjectMessage() is not { } spawn) {
            return;
        }

        player.Tell(spawn);
    }

    private void Broadcast(Imcodec.MessageLayer.IMessage message) => _info.Zone.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
        Message = message,
        Selfless = false,
        Targets = ZoneBroadcastTarget.Players,
    });

    // ---------------------------------------------------------------------
    // Walking
    // ---------------------------------------------------------------------

    private void PlanWalk() {
        var plan = BotLink.Ask(new BotRequest { Op = "walk", Id = _info.Id, Name = _info.Name, Zone = _info.ZonePath })?.Walk
                   ?? new BotWalkPlan { Radius = 200, Points = 6, Speed = 260, PauseSeconds = 2 };
        _speed = Math.Clamp(plan.Speed, 80, 600);
        _pause = Math.Clamp(plan.PauseSeconds, 0, 30);

        var home = new NVector3(_wizard.Location.X, _wizard.Location.Y, _wizard.Location.Z);
        var start = Random.Shared.NextSingle() * MathF.Tau;
        var count = Math.Clamp(plan.Points, 3, 16);
        _loop.Clear();
        for (var i = 0; i < count; i++) {
            var angle = start + (MathF.Tau * i / count);
            var wanted = new NVector3(home.X + (MathF.Cos(angle) * plan.Radius), home.Y + (MathF.Sin(angle) * plan.Radius), home.Z);
            if (Walkable(wanted, home.Z) is { } point && !NearAvoided(point)) {
                _loop.Add(point);
            }
        }

        // No walk grid and nothing clear: it stays near where it came in.
        if (_loop.Count < 2) {
            _loop.Clear();
            _loop.Add(home);
            _loop.Add(new NVector3(home.X + 60, home.Y, home.Z));
        }

        _next = 0;
        _onLeg = false;
        BotLink.Note(_info.Id, _info.Name, "walking", $"{_loop.Count} points in {_info.ZonePath}");
    }

    // The point snapped onto the zone's walk grid (when the zone has one), and not inside or right next to a wall.
    private NVector3? Walkable(NVector3 wanted, float homeZ) {
        var point = ZoneNav.Points(_info.ZonePath).Length == 0
            ? wanted
            : ZoneNav.Nearest(_info.ZonePath, wanted.X, wanted.Y, homeZ, NAV_SNAP, NAV_HEIGHT);
        if (point is not { } p) {
            return null;
        }

        return ZoneCollision.Clearance(_info.ZonePath, p) < WALL_CLEARANCE ? null : p;
    }

    // A real player's game sends where it is a few times a second, and the other games draw the walk between those.
    // One move to a far point made the bot slide or jump there (Niduus, 2026-11-12: "has weird movement"), so it walks
    // in short steps the same way.
    private const float TICK_SECONDS = 0.3f;

    private NVector3 _legTo;
    private bool _onLeg;

    // A battle it was kicked out of (or lost): it keeps its walk clear of that place for a while.
    private NVector3? _avoid;
    private DateTime _avoidUntil;
    private const float AVOID_RADIUS = 420f;

    private bool NearAvoided(NVector3 point)
        => _avoid is { } spot && DateTime.UtcNow < _avoidUntil && NVector3.Distance(spot, point) < AVOID_RADIUS;

    private void WalkOn() {
        if (_gone || _inBattle || _loop.Count == 0) {
            return;
        }

        var from = Here;
        if (!_onLeg) {
            _legTo = _loop[_next];
            _next = (_next + 1) % _loop.Count;
            _onLeg = true;
        }

        var left = NVector3.Distance(from, _legTo);
        var stride = _speed * TICK_SECONDS;
        var arrived = left <= stride;
        var to = arrived ? _legTo : from + ((_legTo - from) / left * stride);
        MoveTo(from, _legTo, to);

        if (!arrived) {
            Timers.StartSingleTimer(STEP_TIMER, new Step(), TimeSpan.FromSeconds(TICK_SECONDS));

            return;
        }

        _onLeg = false;
        var pause = _pause * (0.5f + Random.Shared.NextSingle());
        Timers.StartSingleTimer(STEP_TIMER, new Step(), TimeSpan.FromSeconds(Math.Clamp(TICK_SECONDS + pause, TICK_SECONDS, 60)));
    }

    private NVector3 Here => new(_wizard.Location.X, _wizard.Location.Y, _wizard.Location.Z);

    // One step: faces where it is heading, and is drawn at the new place by every game in the area.
    private void MoveTo(NVector3 from, NVector3 heading, NVector3 to) {
        var yaw = NVector3.DistanceSquared(from, heading) < 1 ? _wizard.Orientation.Z : MathF.Atan2(heading.Y - from.Y, heading.X - from.X);
        if (yaw < 0) {
            yaw += MathF.Tau;
        }

        Broadcast(new GAME_5_PROTOCOL.MSG_SERVERMOVE {
            Direction = (byte) (yaw / MathF.Tau * 250),
            LocationX = Compress(to.X),
            LocationY = Compress(to.Y),
            LocationZ = Compress(to.Z),
            MobileID = _mobileId,
        });

        // Where it is for a player who comes in later (they are sent the object with this place).
        _wizard.Location = new Imcodec.Math.Vector3(to.X, to.Y, to.Z);
        _wizard.Orientation = new Imcodec.Math.Vector3(0, 0, yaw);
    }

    private static ushort Compress(float coordinate) => unchecked((ushort) (short) MathF.Round(coordinate / 4));

    // ---------------------------------------------------------------------
    // Battles
    // ---------------------------------------------------------------------

    private void GoJoin(BotGoJoin message) {
        if (_gone || _inBattle) {
            return;
        }

        _inBattle = true;
        _battleSince = DateTime.UtcNow;
        Timers.Cancel(STEP_TIMER);
        BotLink.Note(_info.Id, _info.Name, "joining", $"{message.For}'s battle in {message.Wait.TotalSeconds:0} s");
        Timers.StartSingleTimer(JOIN_TIMER, new JoinNow(message.Battle, message.For), message.Wait);
    }

    private void JoinBattle(IActorRef battle, string forWhom) {
        if (_gone) {
            return;
        }

        _battleSince = DateTime.UtcNow;
        battle.Tell(new BotJoinBattle(_object, Self, _info.Name));
    }

    private void Seated(BotSeated message) {
        _wizard.Location = message.Where;
        _wizard.Orientation = new Imcodec.Math.Vector3(0, 0, message.Yaw);
        BotLink.Note(_info.Id, _info.Name, "joined", "a battle; it passes every round (test bot)");
    }

    // Every few seconds: is a real player still here, and is its battle over?
    private void Look() {
        if (_gone) {
            return;
        }

        if (BotBodies.PlayersIn(_info.Zone) == 0) {
            Leave("no real player is in its area any more");

            return;
        }

        if (!_inBattle || DateTime.UtcNow - _battleSince < TimeSpan.FromSeconds(8)) {
            return;
        }

        if (CombatDuelComponent.IsInBattle(_wizard.GameObjectID)) {
            return;
        }

        // The battle is over (a won battle sends the bot nothing that says so).
        OutOfBattle("battle over");
    }

    // Leaves the battle behind: a defeated bot leaves the area; otherwise it walks off the circle, away from the battle,
    // and plans a new walk that keeps clear of it.
    private void OutOfBattle(string why) {
        if (_gone || !_inBattle) {
            return;
        }

        _inBattle = false;
        Timers.Cancel(JOIN_TIMER);
        if (why == "defeated" || _wizard.GameStats.m_currentHitpoints <= 0) {
            Leave("it was defeated");

            return;
        }

        var circle = Here;
        _avoid = circle;
        _avoidUntil = DateTime.UtcNow.AddMinutes(2);
        BotLink.Note(_info.Id, _info.Name, why, $"walks away from the battle with {_wizard.GameStats.m_currentHitpoints} health");

        // A first leg straight away from the battle, then a new loop around there.
        var angle = Random.Shared.NextSingle() * MathF.Tau;
        NVector3? away = null;
        for (var tries = 0; tries < 8 && away is null; tries++, angle += MathF.Tau / 8) {
            var wanted = new NVector3(circle.X + (MathF.Cos(angle) * AVOID_RADIUS * 1.2f), circle.Y + (MathF.Sin(angle) * AVOID_RADIUS * 1.2f), circle.Z);
            away = Walkable(wanted, circle.Z);
        }

        var target = away ?? new NVector3(circle.X + (AVOID_RADIUS * 1.2f), circle.Y, circle.Z);
        _wizard.Location = new Imcodec.Math.Vector3(circle.X, circle.Y, circle.Z);
        var home = _wizard.Location;
        _wizard.Location = new Imcodec.Math.Vector3(target.X, target.Y, target.Z);
        PlanWalk();
        _wizard.Location = home;
        _loop.Insert(0, target);
        _next = 0;
        _onLeg = false;
        Timers.StartSingleTimer(STEP_TIMER, new Step(), TimeSpan.FromSeconds(0.5));
    }

    // ---------------------------------------------------------------------
    // Leaving
    // ---------------------------------------------------------------------

    private void Leave(string why, bool stop = true) {
        if (_gone) {
            return;
        }

        _gone = true;
        BotBodies.Gone(_info.Id);
        if (_mobileId != 0) {
            Broadcast(new GAME_5_PROTOCOL.MSG_REMOVEOBJECT { GameObjectID = _wizard.GameObjectID });
            _info.Zone.Tell(new ZONE_102_PROTOCOL.MSG_RELEASEMOBILEID { MobileId = _mobileId });
        }

        BotLink.Note(_info.Id, _info.Name, "gone", why);
        if (stop) {
            Context.Stop(Self);
        }
    }

}
