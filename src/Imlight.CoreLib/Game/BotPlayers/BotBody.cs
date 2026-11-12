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

    // How far apart the walk grid points of one hop of a walk are.
    private const float HOP_MIN = 80f;
    private const float HOP_MAX = 700f;

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

    // How far from where it came in it goes.
    private float _radius = 1500;

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
        var count = Math.Clamp(plan.Points, 3, 16);
        _radius = Math.Clamp(plan.Radius, 300, 8000);
        _loop.Clear();
        var grid = ZoneNav.Points(_info.ZonePath);
        if (grid.Length > 0) {
            // Places of the street's own walk grid within its range, spread apart, clear of walls.
            // Only places on its paths, away from water (Niduus, 2026-11-12: a bot stood in a pond), and among them the
            // open ones more often: a square, a shop front, a wide street.
            var near = grid.Where(p => NVector3.Distance(p, home) <= _radius && MathF.Abs(p.Z - home.Z) < 400).ToArray();
            for (var tries = 0; tries < count * 20 && _loop.Count < count && near.Length > 0; tries++) {
                var p = near[Random.Shared.Next(near.Length)];
                if (!NearAvoided(p) && _loop.All(q => NVector3.Distance(p, q) > _radius / 4)
                    && BotGround.GoodSpot(_info.ZonePath, p)
                    && (Random.Shared.Next(3) == 0 || BotGround.Openness(_info.ZonePath, p) >= 30)
                    && ZoneCollision.Clearance(_info.ZonePath, p) >= WALL_CLEARANCE) {
                    _loop.Add(p);
                }
            }
        }
        else {
            var start = Random.Shared.NextSingle() * MathF.Tau;
            for (var i = 0; i < count; i++) {
                var angle = start + (MathF.Tau * i / count);
                var reach = _radius * (0.4f + (Random.Shared.NextSingle() * 0.6f));
                var wanted = new NVector3(home.X + (MathF.Cos(angle) * reach), home.Y + (MathF.Sin(angle) * reach), home.Z);
                if (ZoneCollision.Clearance(_info.ZonePath, wanted) >= WALL_CLEARANCE && !NearAvoided(wanted)
                    && BotGround.CanStep(_info.ZonePath, wanted)) {
                    _loop.Add(wanted);
                }
            }
        }

        // Nothing clear: it stays near where it came in.
        if (_loop.Count < 2) {
            _loop.Clear();
            _loop.Add(home);
            _loop.Add(new NVector3(home.X + 150, home.Y, home.Z));
        }

        _next = 0;
        _onLeg = false;
        _route.Clear();
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

    // The way to where it is going now: a few points along a gentle curve, as a person rarely walks a ruler-straight line.
    private readonly Queue<NVector3> _route = new();
    private bool _onLeg;
    private float _legSpeed;
    private int _lookTurns;

    // After a battle the first leg goes straight away from it (OutOfBattle).
    private bool _nextIsFixed;

    // A battle it was kicked out of (or lost): it keeps its walk clear of that place for a while.
    private NVector3? _avoid;
    private DateTime _avoidUntil;
    private const float AVOID_RADIUS = 420f;

    private bool NearAvoided(NVector3 point)
        => _avoid is { } spot && DateTime.UtcNow < _avoidUntil && NVector3.Distance(spot, point) < AVOID_RADIUS;

    // Walks like a person (Niduus, 2026-11-12: "make the movements more human"): it wanders between the places of its
    // area in no set order, along a slight curve, at a pace that changes a little from walk to walk; at the end of a walk it
    // goes on at once, or stops a moment and looks around, or now and then stands a while.
    private void WalkOn() {
        if (_gone || _inBattle || _loop.Count == 0) {
            return;
        }

        var from = Here;
        if (_lookTurns > 0) {
            LookAroundOnce();

            return;
        }

        if (!_onLeg) {
            StartLeg(from);
        }

        if (_route.Count == 0) {
            _onLeg = false;
            Timers.StartSingleTimer(STEP_TIMER, new Step(), TimeSpan.FromSeconds(1));

            return;
        }

        var next = _route.Peek();
        var left = NVector3.Distance(from, next);
        var stride = _legSpeed * TICK_SECONDS;
        while (left <= stride && _route.Count > 1) {
            // Past a bend point: on to the next one in the same step.
            _route.Dequeue();
            next = _route.Peek();
            left = NVector3.Distance(from, next);
        }

        var arrived = left <= stride;
        var to = arrived ? next : from + ((next - from) / left * stride);
        MoveTo(from, next, to);
        if (arrived) {
            _route.Dequeue();
        }

        if (_route.Count > 0) {
            Timers.StartSingleTimer(STEP_TIMER, new Step(), TimeSpan.FromSeconds(TICK_SECONDS));

            return;
        }

        _onLeg = false;
        var roll = Random.Shared.NextDouble();
        double pause;
        if (roll < 0.5) {
            pause = 0.1 + (Random.Shared.NextDouble() * 1.2);           // walks on, almost at once
        }
        else if (roll < 0.85) {
            pause = 0.6 + (Random.Shared.NextDouble() * _pause);       // stops and looks around
            _lookTurns = Random.Shared.Next(1, 4);
        }
        else {
            pause = 5 + (Random.Shared.NextDouble() * 8);               // stands a while
            _lookTurns = Random.Shared.Next(0, 2);
        }

        Timers.StartSingleTimer(STEP_TIMER, new Step(), TimeSpan.FromSeconds(Math.Clamp(pause, TICK_SECONDS, 30)));
    }

    // The next place to go, and the curve there.
    private void StartLeg(NVector3 from) {
        NVector3 destination;
        if (_nextIsFixed) {
            destination = _loop[_next];
            _nextIsFixed = false;
        }
        else {
            // Anywhere in its area but where it stands.
            var choices = _loop.Where(p => NVector3.Distance(p, from) > 150).ToList();
            if (choices.Count == 0) {
                choices = [.. _loop];
            }

            destination = choices[Random.Shared.Next(choices.Count)];
        }

        _route.Clear();
        var grid = ZoneNav.Points(_info.ZonePath);
        if (grid.Length > 0) {
            // Along the street's walk grid, hop by hop toward the place, so it follows the street around corners and
            // buildings instead of a straight line through them.
            var at = from;
            for (var hop = 0; hop < 80 && NVector3.Distance(at, destination) > HOP_MAX; hop++) {
                var left = NVector3.Distance(at, destination);
                var here = at;
                var next = grid
                    .Where(p => MathF.Abs(p.Z - here.Z) < 120 && BotGround.CanStep(_info.ZonePath, p))
                    .Select(p => (Point: p, Step: NVector3.Distance(p, here), Left: NVector3.Distance(p, destination)))
                    .Where(x => x.Step is >= HOP_MIN and <= HOP_MAX && x.Left < left - 40
                                && ZoneCollision.Clearance(_info.ZonePath, (x.Point + here) / 2) >= 20
                                && BotGround.CanStep(_info.ZonePath, (x.Point + here) / 2))
                    .OrderBy(x => x.Left).Take(3).ToList();
                if (next.Count == 0) {
                    break;
                }

                at = next[Random.Shared.Next(next.Count)].Point;
                _route.Enqueue(at);
            }
        }
        else if (NVector3.Distance(from, destination) is var distance && distance > 120) {
            // No walk grid: a gentle curve there.
            var dir = (destination - from) / distance;
            var side = new NVector3(-dir.Y, dir.X, 0) * (distance * (0.08f + (Random.Shared.NextSingle() * 0.17f)) * (Random.Shared.Next(2) == 0 ? -1 : 1));
            var control = ((from + destination) / 2) + side;
            for (var i = 1; i <= 3; i++) {
                var t = i / 4f;
                var u = 1 - t;
                var point = (u * u * from) + (2 * u * t * control) + (t * t * destination);
                if (ZoneCollision.Clearance(_info.ZonePath, point) >= WALL_CLEARANCE && !NearAvoided(point)) {
                    _route.Enqueue(point);
                }
            }
        }

        _route.Enqueue(destination);
        _legSpeed = _speed * (0.85f + (Random.Shared.NextSingle() * 0.25f));
        _onLeg = true;
    }

    // Standing, it turns its head this way and that: a turn on the spot.
    private void LookAroundOnce() {
        _lookTurns--;
        var yaw = _wizard.Orientation.Z + ((Random.Shared.NextSingle() - 0.5f) * MathF.PI * 1.2f);
        yaw = ((yaw % MathF.Tau) + MathF.Tau) % MathF.Tau;
        var here = Here;
        Broadcast(new GAME_5_PROTOCOL.MSG_SERVERMOVE {
            Direction = (byte) (yaw / MathF.Tau * 250),
            LocationX = Compress(here.X),
            LocationY = Compress(here.Y),
            LocationZ = Compress(here.Z),
            MobileID = _mobileId,
        });
        _wizard.Orientation = new Imcodec.Math.Vector3(0, 0, yaw);
        Timers.StartSingleTimer(STEP_TIMER, new Step(), TimeSpan.FromSeconds(0.8 + (Random.Shared.NextDouble() * 1.6)));
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
        BotLink.Note(_info.Id, _info.Name, "joined", "a battle");
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

        // Low after a fight, it drinks a potion, as a player would before the next one (Niduus, 2026-11-12).
        var stats = _wizard.GameStats;
        if (stats.m_currentHitpoints < stats.m_baseHitpoints / 2 || stats.m_currentMana < stats.m_baseMana / 4) {
            stats.m_currentHitpoints = Math.Max(stats.m_currentHitpoints, stats.m_baseHitpoints);
            stats.m_currentMana = Math.Max(stats.m_currentMana, stats.m_baseMana);
            BotLink.Note(_info.Id, _info.Name, "potion", $"drinks a potion: back to {stats.m_currentHitpoints} health and {stats.m_currentMana} mana");
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
        _nextIsFixed = true;
        _onLeg = false;
        _route.Clear();
        _lookTurns = 0;
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
