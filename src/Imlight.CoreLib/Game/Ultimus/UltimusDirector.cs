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
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Groups;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Ultimus, the Solo Quester (Niduus, 2026-11-12: "Can we create a Solo Quester called Ultimus? ... It's primary goal will
/// be to complete the Main Story quests above all else. This will basically be a fork of current QT"). One Balance wizard
/// named Ultimus who plays the Main Story alone from level 1, the way a player does: it walks everywhere, talks to NPCs,
/// fights, trains and shops. Unlike the Quest Team it welcomes players into its battles and joins a player's battle when
/// the creature its quest needs is in it; it never joins the Quest Team, and the Quest Team keeps out of its way. What it
/// does goes to its own log (logs/ultimus); a quest that can't be done, Ultimus stuck, a fight lost again and again, or a
/// quest taking too long is an issue Ahziael announces, with a debug report (debug/issue-NNNN.txt) detailed enough to fix
/// the quest from. Something odd it notices is logged in pink (ABNORMAL), and what it learns from its mistakes goes to
/// learned.jsonl. QT learns to play as a team, Ultimus learns to play solo; both are the body of the bots to come.
/// </summary>
internal static class UltimusDirector {

    private static readonly System.Threading.Lock s_lock = new();
    private static IActorRef s_actor;
    private static DateTime s_retryAt = DateTime.MinValue;

    /// <summary>The team plays (its mini server answers): started once.</summary>
    internal static void EnsureRunning() {
        lock (s_lock) {
            if (s_actor is not null || DateTime.UtcNow < s_retryAt || QuestTeam.QuestTeamHost.System is not { } system || QuestTeam.QuestTeamHost.Server is null) {
                return;
            }

            s_actor = system.ActorOf(Props.Create(() => new UltimusDirectorActor()), $"ultimus-{Guid.NewGuid():N}");
        }
    }

    /// <summary>The team logs out (the switch is off or its mini server stopped).</summary>
    internal static void Stop(string why) {
        lock (s_lock) {
            if (s_actor is null) {
                return;
            }

            s_actor.Tell(new UltimusDirectorActor.Halt(why));
            s_actor = null;
        }
    }

    /// <summary>The team could not start (its wizards could not be made): tried again in a minute.</summary>
    internal static void Failed(IActorRef actor) {
        lock (s_lock) {
            if (s_actor is not null && s_actor.Equals(actor)) {
                s_actor = null;
                s_retryAt = DateTime.UtcNow.AddMinutes(1);
            }
        }

        actor.Tell(PoisonPill.Instance);
    }

    // ---- the team's wizards, for the battles (CombatDuelComponent.Ultimus.cs) ----

    private static volatile Dictionary<ulong, string> s_roles = [];
    private static volatile List<UltimusMember.Status> s_members = [];

    internal static void Remember(IEnumerable<UltimusMember.Status> members) {
        s_members = [.. members];
        s_roles = s_members.ToDictionary(m => m.GlobalId, m => m.Role);
    }

    internal static void Forget() {
        s_roles = [];
        s_members = [];
    }

    private static volatile Dictionary<string, IActorRef> s_actors = [];

    /// <summary>The actor (pretend game) of a Ultimus wizard by role, or null.</summary>
    internal static IActorRef ActorOf(string role) => role is null ? null : s_actors.GetValueOrDefault(role);

    internal static void RememberActors(Dictionary<string, IActorRef> actors) => s_actors = actors;

    // Where Ultimus is busy: the creature it walks to or fights, in which area, until when.
    private static volatile Busy s_busy;

    private sealed record Busy(IActorRef Zone, float X, float Y, DateTime Until);

    /// <summary>Ultimus is going for a creature at (x, y), or fighting there (the Director says so each half second).</summary>
    internal static void SetBusy(IActorRef zone, float x, float y) => s_busy = zone is null ? null : new Busy(zone, x, y, DateTime.UtcNow.AddSeconds(5));

    /// <summary>
    /// Whether Ultimus is going for, or fighting, a creature near (x, y) in this area (Niduus, 2026-11-12: the Quest Team
    /// "will always attempt to avoid colliding with Ultimus"): the Quest Team picks another creature.
    /// </summary>
    public static bool IsBusyNear(IActorRef zone, float x, float y, float range = 700) {
        if (zone is null || s_busy is not { } busy || busy.Until < DateTime.UtcNow || !zone.Equals(busy.Zone)) {
            return false;
        }

        return UltimusPlanner.Far(x, y, busy.X, busy.Y) < range;
    }

    /// <summary>Whether a wizard (by object id) is an Ultimus wizard.</summary>
    public static bool IsUltimus(ulong globalId) => s_roles.ContainsKey(globalId);

    /// <summary>Ultimus's wizards as they are now (for .ultimus tp), empty while it is not playing.</summary>
    internal static IReadOnlyList<UltimusMember.Status> Members => s_members;

    /// <summary>The seat (Balance) of an Ultimus wizard by its object id, or null for anyone else.</summary>
    internal static string RoleOf(ulong globalId) => s_roles.GetValueOrDefault(globalId);

}

internal sealed partial class UltimusDirectorActor : ReceiveActor, IWithTimers {

    internal sealed record Halt(string Why);

    private sealed record Tick;

    private sealed record Loaded(List<UltimusRoster.Member> Members);

    public ITimerScheduler Timers { get; set; }

    private const double TICK_SECONDS = 0.5;

    private readonly List<UltimusMember.Status> _team = [];
    private readonly List<IActorRef> _actors = [];
    private bool _starting;

    // What each wizard was last sent to (so it is not sent again every tick).
    private readonly Dictionary<int, (UltimusPlanner.Target Target, DateTime At, float X, float Y)> _sent = [];

    // The leader's target and the clicks already made for it.
    private UltimusPlanner.Target _target;
    private DateTime _targetSince = DateTime.UtcNow;
    private readonly Dictionary<string, (int Times, DateTime Last)> _clicks = [];
    private DateTime _nextClick = DateTime.MinValue;

    private string _leaderZone = "";
    private float _leaderLastX, _leaderLastY;
    private UltimusPlanner.Target _leaderLastTarget;

    // Progress, for the log and the alerts.
    private string _signature = "";
    private DateTime _lastProgress = DateTime.UtcNow;
    private readonly Dictionary<string, QuestRun> _runs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenGoals = [];
    private readonly Dictionary<string, int> _losses = [];
    private readonly Dictionary<string, DateTime> _reported = [];
    private readonly bool[] _wasInBattle = new bool[UltimusRoster.Roles.Length];
    private string _currentQuest;
    private DateTime _nextStatus = DateTime.MinValue;
    private DateTime _nextTraining = DateTime.MinValue;
    private string _doing = "starting";

    private sealed class QuestRun {
        public string Quest = "";
        public string Title = "";
        public string World = "";
        public DateTime Started = DateTime.UtcNow;
        public double Expected;
        public bool SlowTold;
        public bool LimitTold;

        /// <summary>Time spent fighting for XP to catch up with the quest's level (coach review 2026-11-14: "Payback" was flagged
        /// slow while he levelled from 12 toward 14 for it): not counted against the quest.</summary>
        public double GrindSeconds;
        public bool BudgetTold;

        internal double Seconds => Math.Max(0, (DateTime.UtcNow - Started).TotalSeconds - GrindSeconds);
    }

    private DateTime _runsCheckedAt = DateTime.UtcNow;

    public UltimusDirectorActor() {
        Receive<Halt>(m => {
            Note("Ultimus", $"logs out: {m.Why}.");
            foreach (var actor in _actors) {
                actor.Tell(new UltimusMember.Disconnect(m.Why));
            }

            try {
                MaybeReview(stopping: true);
                // The session's end report (UltimusDirector.Sense.cs).
                WriteSession(_team.Count > 0 ? _team[0] : null, final: true);
            }
            catch (Exception e) {
                Logger.Debug("Ultimus: the review at stopping failed: {0}", Logger.Args(e.Message));
            }

            UltimusDirector.Forget();
            WriteStatus("off", m.Why);
            Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(2), Self, PoisonPill.Instance, Self);
        });
        Receive<Loaded>(OnLoaded);
        Receive<Tick>(_ => {
            try {
                OnTick();
            }
            catch (Exception e) {
                Logger.Error("Ultimus: {0}", Logger.Args(e));
            }
        });
    }

    protected override void PreStart() {
        // His self-tuning limits written once, so they can be opened and set before his first daily review.
        _ = Limits();
        StartTeam(restart: false);
        base.PreStart();
    }

    private void StartTeam(bool restart) {
        if (_starting) {
            return;
        }

        _starting = true;
        _doing = restart ? "making a new Ultimus" : "logging in";
        var self = Self;
        _ = System.Threading.Tasks.Task.Run(() => {
            var members = restart ? UltimusRoster.Restart() : UltimusRoster.Load();
            self.Tell(new Loaded(members));
        });
    }

    private void OnLoaded(Loaded loaded) {
        _starting = false;
        if (loaded.Members is null) {
            Issue("team", null, "Ultimus could not be made or loaded. The server log says why.");
            UltimusDirector.Failed(Self);

            return;
        }

        UltimusRoster.Remember(loaded.Members);
        _usernames = loaded.Members.Select(m => m.Username).Where(u => !string.IsNullOrEmpty(u)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _team.Clear();
        _actors.Clear();
        foreach (var member in loaded.Members) {
            var status = new UltimusMember.Status { Role = member.Role, CharId = member.CharId, AccountId = member.AccountId, Name = member.Name };
            _team.Add(status);
            var actor = Context.ActorOf(UltimusMember.Props(member, status), $"member-{member.Role.ToLowerInvariant()}");
            _actors.Add(actor);
        }

        UltimusDirector.Remember(_team);
        UltimusDirector.RememberActors(_team.Select((m, i) => (m.Role, Actor: _actors[i])).ToDictionary(x => x.Role, x => x.Actor));
        Note("Ultimus", $"logs in: {string.Join(", ", _team.Select(m => $"{m.Name} ({m.Role})"))}.");

        // They log in in their order, a moment apart.
        for (var i = 0; i < _actors.Count; i++) {
            Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(1 + (i * 3)), _actors[i], new UltimusMember.Connect(), Self);
        }

        _lastProgress = DateTime.UtcNow;
        Timers.StartPeriodicTimer("tick", new Tick(), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(TICK_SECONDS));
    }

    // ---------------------------------------------------------------------
    // Each half second
    // ---------------------------------------------------------------------

    private void OnTick() {
        if (_team.Count != UltimusRoster.Roles.Length) {
            return;
        }

        // Ahziael's Restart: everyone logs out, four new wizards, in again.
        var restartFile = Path.Combine(UltimusWire.Folder, UltimusWire.RESTART_FILE);
        if (File.Exists(restartFile)) {
            try {
                File.Delete(restartFile);
            }
            catch (IOException) {
                return;
            }

            Note("Ultimus", "Restart asked in Ahziael: Ultimus logs out and a new level 1 Ultimus is made.");
            foreach (var actor in _actors) {
                actor.Tell(PoisonPill.Instance);
            }

            Timers.Cancel("tick");
            UltimusDirector.Forget();
            _runs.Clear();
            _seenGoals.Clear();
            _losses.Clear();
            _stuck.Clear();
            _target = null;
            _currentQuest = null;
            ForgetWorlds();
            StartTeam(restart: true);

            return;
        }

        TrackProgress();
        if (DateTime.UtcNow >= _nextStatus) {
            _nextStatus = DateTime.UtcNow.AddSeconds(4);
            WriteStatus("on", null);
        }

        if (_team.Any(m => m.State != "playing" || m.Wizard is null)) {
            _doing = "waiting to be in the game";

            return;
        }

        UsePotions();
        NoticeOddities();
        AnswerPlayers();
        // Ahziael's "Pause and explain", answered even while paused (UltimusDirector.Story.cs).
        MaybeExplain(_team[0]);
        // Ahziael's "Back to story", and the guardrails' watch (UltimusDirector.Guard.cs).
        MaybeBackToStory();
        GuardTick(_team[0]);
        // Dungeon sense and his session's goals (UltimusDirector.Sense.cs).
        DungeonTick(_team[0]);
        SessionTick(_team[0]);

        // Paused in Ahziael: they stand where they are, logged in (a battle still plays out by itself).
        if (DateTime.UtcNow >= _nextPauseLook) {
            _nextPauseLook = DateTime.UtcNow.AddSeconds(2);
            var paused = File.Exists(Path.Combine(UltimusWire.Folder, UltimusWire.PAUSE_FILE));
            if (paused != _paused) {
                _paused = paused;
                Note("Ultimus", paused ? "is paused in Ahziael: it stands still." : "is resumed in Ahziael.");
            }
        }

        if (_paused) {
            _doing = "paused (Ahziael's Pause button)";
            _lastProgress = DateTime.UtcNow;
            for (var i = 0; i < _team.Count; i++) {
                StopWalking(i);
            }

            return;
        }

        // A battle: it plays it out (CombatDuelComponent.Ultimus.cs picks its cards).
        if (HandleBattle()) {
            return;
        }

        // A cutscene: watched to the end (UltimusDirector.Sense.cs).
        if (WatchingCutscene()) {
            return;
        }

        // Anyone reading a dialog: the team waits for them.
        if (_team.Any(m => m.Talking || DateTime.UtcNow - m.LastDialog < TimeSpan.FromSeconds(1))) {
            _doing = "reading a dialog";
            if (_dialogSince == DateTime.MaxValue) {
                _dialogSince = DateTime.UtcNow;
            }

            // Dialogs read to the end (Niduus, 2026-11-12: "read dialogs to the end and flag dialogs that never finish"):
            // a minute of dialog after dialog with no end is a dialog that never finishes.
            if (DateTime.UtcNow - _dialogSince > TimeSpan.FromSeconds(60)) {
                var stuck = _team.Where(m => m.Talking || DateTime.UtcNow - m.LastDialog < TimeSpan.FromSeconds(1)).Select(m => m.Role).ToList();
                var quest = _currentQuest ?? _target?.Quest;
                Issue("quest", quest, $"A dialog never finishes for {string.Join(", ", stuck)}: it has gone on for over a minute, one dialog after another" +
                                      (_target is null ? "." : $", at \"{_target.What}\"."), key: "dialog|" + quest + "|" + _target?.Key, broken: false, level: UltimusLink.NOTABLE);
            }

            return;
        }

        _dialogSince = DateTime.MaxValue;

        // On a dungeon or tower sigil, counting down: he stands still on it until it takes him in, with whoever stepped on
        // with him (Niduus, 2026-11-14: "I hopped on his Sigil for a dungeon/tower and he got off"). Nothing else is planned,
        // walked or unstuck meanwhile, as a player waits on the pad.
        if (Services.ZoneService.IsCountingDown(_team[0].GlobalId)) {
            if (_doing != "on the sigil, waiting for the countdown") {
                Think("I am on the sigil and its countdown runs: I stay on it, with anyone who joined me, until it takes us in.", "sigil-wait");
            }

            _doing = "on the sigil, waiting for the countdown";
            _lastProgress = DateTime.UtcNow;
            return;
        }

        // Never stands in one place for ever (UltimusDirector.Stuck.cs).
        LookForStuck();

        // His mood, and once a day what he learned (UltimusDirector.Plans.cs).
        ThinkMood();
        MaybeReview();

        // Better gear on, and decks that miss new spells redone (UltimusDirector.Gear.cs).
        KeepGearAndDecks();

        var leader = _team[0];
        NoteLeaderZone(leader);
        // Solo tag, the watchdog and his progress file (UltimusDirector.Push.cs).
        PushTick(leader);
        // On the walkable ground, where he loses time, the nightly coach folder (UltimusDirector.Bounds.cs).
        BoundsTick(leader);
        WasteTick(leader);
        MaybeAutoCoach();
        RollDay();
        MaybeWeeklyReport();
        Lead([leader]);
    }

    // ---------------------------------------------------------------------
    // The leader: what to do next
    // ---------------------------------------------------------------------

    private DateTime _dialogSince = DateTime.MaxValue;
    private readonly Dictionary<ulong, DateTime> _stonesUsed = [];
    private readonly List<(DateTime At, string Zone)> _zoneChanges = [];

    // Alone, it needs more in the tank than a team member before a fight: its playbook's Quest.ReadyHealth and ReadyMana.

    private const float SAFE_FROM_MOBS = 900f;

    /// <summary>
    /// Where the leader should stand while not fighting: null when it already stands on a path well away from the
    /// creatures; otherwise the closest walkable path point that is. A street with no walk grid gives null.
    /// </summary>
    private static UltimusPlanner.Target SafeSpot(UltimusMember.Status leader) {
        if (leader.ZoneActor is not { } zone || string.IsNullOrEmpty(leader.Zone)) {
            return null;
        }

        var mobs = MinimapIndex.In(zone).Where(m => m.Kind == MinimapIndex.MOB)
            .Select(m => (m.Object.m_location.X, m.Object.m_location.Y)).ToList();
        if (mobs.Count == 0) {
            return null;
        }

        float Nearest(float x, float y) => mobs.Min(m => UltimusPlanner.Far(x, y, m.X, m.Y));
        var here = new System.Numerics.Vector3(leader.X, leader.Y, leader.Z);
        if (Nearest(leader.X, leader.Y) >= SAFE_FROM_MOBS && Imlight.CoreLib.Game.BotPlayers.BotGround.GoodSpot(leader.Zone, here)) {
            return null;
        }

        var best = Imlight.CoreLib.Game.Zone.Core.ZoneNav.Points(leader.Zone)
            .Where(p => MathF.Abs(p.Z - leader.Z) < 400 && System.Numerics.Vector3.Distance(p, here) < 3000
                        && Nearest(p.X, p.Y) >= SAFE_FROM_MOBS && Imlight.CoreLib.Game.BotPlayers.BotGround.GoodSpot(leader.Zone, p))
            .OrderBy(p => System.Numerics.Vector3.Distance(p, here))
            .Select(p => (System.Numerics.Vector3?) p)
            .FirstOrDefault();

        return best is { } spot
            ? new UltimusPlanner.Target(UltimusPlanner.SPOT, spot.X, spot.Y, "a safe spot off the road, away from the creatures", Key: "safe")
            : null;
    }

    private DateTime _healingAt = DateTime.MinValue;
    private string _buttonFromZone;

    // A Recall or Commons press sent in the last 12 seconds that has not moved him yet.
    private bool ButtonMovePending(UltimusMember.Status leader) {
        var sent = _recallSentAt > _hubPressedAt ? _recallSentAt : _hubPressedAt;
        if (DateTime.UtcNow - sent > TimeSpan.FromSeconds(12)) {
            _buttonFromZone = null;
            return false;
        }

        _buttonFromZone ??= leader.Zone;
        if (!string.Equals(_buttonFromZone, leader.Zone, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        _doing = sent == _recallSentAt ? "recalling to his marked spot" : "using the Commons button";

        return true;
    }

    private void Lead(List<UltimusMember.Status> group) {
        var leader = group[0];
        // A step just done: a moment, as a player takes (UltimusDirector.Life.cs).
        if (Pausing()) {
            StopWalking(0);
            return;
        }

        // One intention at a time (approved 2026-11-14): a Recall or a Commons button press just sent is let finish (the
        // loading and the landing) before anything new is planned or walked; it was cancelled by new walks before.
        if (ButtonMovePending(leader)) {
            StopWalking(0);
            return;
        }

        // Every new move checked against his mission, with its "because" (UltimusDirector.Guard.cs).
        var target = MissionCheck(leader, PickTarget(group));
        // Healing first, above all before a fight (UltimusDirector.Health.cs).
        if (HealFirst(group, target)) {
            _healingAt = DateTime.UtcNow;
            return;
        }

        // A fight lost again and again, the "patience" remedy: a few minutes by it first (UltimusDirector.Losses.cs).
        if (WaitingForHelp(leader, target)) {
            if (SafeSpot(leader) is { } calm) {
                WalkTo(0, calm);
            } else {
                StopWalking(0);
            }

            return;
        }

        if (target is null) {
            // Nothing to do this moment: off the road, not standing among the creatures.
            if (SafeSpot(leader) is { } safe) {
                WalkTo(0, safe);
            } else {
                StopWalking(0);
            }

            return;
        }

        if (!target.Same(_target)) {
            // A creature that walks about moves the target, but it is the same step: said once (it was said every second).
            var sameStep = _target is not null && _target.Kind == target.Kind && _target.Key == target.Key;
            _target = target;
            if (!sameStep) {
                _targetSince = DateTime.UtcNow;
                Note("Ultimus", $"next: {Describe(target)}.");
                // His goals, how much he trusts this plan and what he expects (UltimusDirector.Plans.cs).
                var (confidence, reasons) = Confidence(leader, target);
                ThinkGoals(leader, target, confidence);
                ThinkConfidence(leader, target, confidence, reasons);
                RiskBudget(target, confidence);
                Predict(leader, target);
                // His level and what went wrong here before (UltimusDirector.Push.cs).
                PreCheck(leader, target);
                // A fight is next: each deck made ready for it.
                if (target.Kind == UltimusPlanner.FIGHT) {
                    // Its deck read first, as a player who knows the creature (UltimusScout): shields for what it hits with.
                    UltimusScout.Expect(target.Template);
                    Note("Ultimus", $"knows what it is about to fight: {UltimusScout.OfCreature(target.Template).Describe()}.");
                    ThinkAboutFight(target);
                    ThinkAboutRival(target);
                    PrepareDecks(UltimusScout.SituationOf(target.Template));
                }
            }
        }

        if (target.Problem.Length > 0) {
            // A person does not give up on a step at once (Niduus, 2026-11-13: steps were skipped within seconds): it
            // looks about the area first, and only a step that stays unworkable for a while is written up (flagged, never skipped).
            if (!_problemSince.TryGetValue(target.Key, out var since)) {
                _problemSince[target.Key] = since = DateTime.UtcNow;
                Note("Ultimus", $"can't see how to do \"{target.What}\" yet ({target.Problem}): looks around the area before giving up.");
            }

            if (DateTime.UtcNow - since < s_problemPatience) {
                _doing = $"looking around for: {target.What}";
                _lastProgress = DateTime.UtcNow;
                LookAround(leader, target);

                return;
            }

            _doing = $"stuck: {target.What}: {target.Problem}";
            StopWalking(0);
            Issue("quest", target.Quest, $"{target.What}: {target.Problem}.", key: target.Key + "|problem");

            return;
        }

        _problemSince.Remove(target.Key);

        _doing = Describe(target);
        if (target.Kind == UltimusPlanner.WAIT) {
            if (SafeSpot(leader) is { } aside) {
                WalkTo(0, aside);
            } else {
                StopWalking(0);
            }

            return;
        }

        if (target.Kind == UltimusPlanner.FIGHT) {
            UltimusDirector.SetBusy(leader.ZoneActor, target.X, target.Y);
        }

        var reach = target.Kind switch {
            UltimusPlanner.TALK or UltimusPlanner.OFFER or UltimusPlanner.TRAIN or UltimusPlanner.SHOP => 160f,
            UltimusPlanner.DOOR or UltimusPlanner.SPIRAL or UltimusPlanner.STONE => 140f,
            UltimusPlanner.FIGHT => 40f,
            _ => 25f,
        };

        var distance = UltimusPlanner.Far(leader.X, leader.Y, target.X, target.Y);
        if (distance > reach) {
            WalkTo(0, target);

            return;
        }

        // There: the walk that got it here is written down as a proven way (RouteMemory).
        if (RouteMemory.Arrived(leader.CharId, leader.Zone, leader.X, leader.Y)) {
            var (legs, stuck) = RouteMemory.Count();
            Note("Ultimus", $"{leader.Name} remembers the walk to \"{target.What}\" in {UltimusText.Zone(leader.Zone)} as a way that works " +
                            $"({legs} known ways, {stuck} stuck spots to walk round).");
        }

        // There: what is done here, one wizard at a time in the team's order.
        switch (target.Kind) {
            case UltimusPlanner.TALK:
                ClickEach(group, target, ["QuestPersonaGoalService", "QuestOfferService", "QuestUnderwayService"],
                    m => HoldsGoal(m, target.Quest, target.Goal));
                break;
            case UltimusPlanner.OFFER:
                ClickEach(group, target, ["QuestOfferService"], m => m.Wizard?.HasQuest(target.Quest) == false
                                                                     && m.Wizard?.HasCompletedQuest(target.Quest) == false);
                break;
            case UltimusPlanner.USE:
                UseHere(group, target);
                break;
            case UltimusPlanner.SPOT:
                // Each of them steps on the spot (an area a quest asks to walk into counts for whoever walks in).
                for (var i = 1; i < group.Count; i++) {
                    if (HoldsGoal(group[i], target.Quest, target.Goal) && UltimusPlanner.Far(group[i].X, group[i].Y, target.X, target.Y) > 25) {
                        WalkTo(_team.IndexOf(group[i]), target);
                    }
                }

                break;
            case UltimusPlanner.FIGHT:
                // Walking onto the creature starts the battle; nothing to click.
                WalkTo(0, target with { X = target.X + 1 });
                break;
            case UltimusPlanner.EXIT:
                IntoExit(leader, target);
                break;
            case UltimusPlanner.DOOR:
                if (leader.Options.ContainsKey(target.GlobalId)) {
                    _doorWait = null;
                    ClickOnce(0, target, ["Interact"], "opens the door " + target.What);
                }
                else {
                    DoorWithoutOptions(leader, target);
                }

                break;
            case UltimusPlanner.SPIRAL:
                if (CanClick(target.Key + "|0")) {
                    Clicked(target.Key + "|0");
                    Note(leader.Role, $"{leader.Name} uses the Spiral Door to {UltimusText.Zone(target.To)}.");
                    _actors[0].Tell(new UltimusMember.WorldTeleport(target.GlobalId, target.To));
                    _leaderLastTarget = target;
                }

                break;
            case UltimusPlanner.TRAIN:
                TrainHere(group, target);
                break;
            case UltimusPlanner.STONE:
                // All four at the stone, then each presses it, so they arrive together.
                if (group.Count < _team.Count || group.Any(m => UltimusPlanner.Far(m.X, m.Y, target.X, target.Y) > 260)) {
                    _doing = "waiting at the street teleporter for the team";
                    StopWalking(0);
                    break;
                }

                if (!_team.All(m => m.Options.ContainsKey(target.GlobalId))) {
                    for (var i = 0; i < _team.Count; i++) {
                        if (!_team[i].Options.ContainsKey(target.GlobalId)) {
                            WalkTo(i, target with { X = target.X - 20 });
                        }
                    }

                    break;
                }

                Note("Ultimus", $"uses {target.What}: it brings it closer to the story quest.");
                for (var i = 0; i < _team.Count; i++) {
                    var option = _team[i].Options[target.GlobalId].FirstOrDefault(o => o.Service == "Interact");
                    Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromMilliseconds(300 + (i * 700)), _actors[i],
                        new UltimusMember.Interact(target.GlobalId, option.Service ?? "Interact", option.Index, target.What), Self);
                }

                _stonesUsed[target.GlobalId] = DateTime.UtcNow;
                _nextClick = DateTime.UtcNow.AddSeconds(2.5);
                break;
            case UltimusPlanner.SHOP:
                ShopHere(group, target);
                break;
        }
    }

    private UltimusPlanner.Target PickTarget(List<UltimusMember.Status> group) {
        var leader = group[0];

        // Off the main story (an event, a gauntlet, a house, a side world his step does not name): leaving it comes first
        // (Niduus, 2026-11-14). Then a wrong area the way went to: back out the same way (UltimusDirector.Bounds.cs).
        if (LeaveOffStory(leader) is { } leave) {
            return leave;
        }

        if (BackOut(leader) is { } backOut) {
            return backOut;
        }

        // The wisps a won fight left, picked up (UltimusDirector.Smart.cs).
        if (WispAfterFight(leader) is { } wisp) {
            return wisp;
        }

        // Training: when someone has spells waiting and the team is in Wizard City (or two spells wait), a trip to Ravenwood.
        // He marks his spot first, and recalls back after (UltimusDirector.Push.cs).
        // Side trips each have a time budget, and close for a while after one runs over it or an off-track alarm
        // (UltimusDirector.Guard.cs).
        if (SideTripsOpen && SideTrip(TrainingTarget(group), "training") is { } training) {
            MarkBeforeTrip(leader, training);
            return training;
        }

        // Shopping: better gear one of them can pay for, at a shop in this world (in push mode, only when it is worth it).
        if (SideTripsOpen && ShopAllowed(leader) && SideTrip(NotMidStep(ShoppingTarget(group)), "shopping") is { } shopping) {
            MarkBeforeTrip(leader, shopping);
            return shopping;
        }

        // Little room in the backpack: he sells what he won't wear at a shop here (UltimusDirector.Life.cs).
        // The Bazaar pays far more than a shop: there first when it is on and he is in Wizard City anyway (UltimusDirector.Life.cs).
        if (SideTripsOpen && SideTrip(NotMidStep(BazaarSellTarget(group)), "selling") is { } bazaarSelling) {
            return bazaarSelling;
        }

        if (SideTripsOpen && SideTrip(SellTarget(group), "selling") is { } selling) {
            return selling;
        }

        // A clear upgrade at the Bazaar he can pay for past his reserve (in push mode, only when shopping is allowed).
        if (SideTripsOpen && ShopAllowed(leader) && SideTrip(NotMidStep(BazaarTarget(group)), "bazaar") is { } bazaar) {
            MarkBeforeTrip(leader, bazaar);
            return bazaar;
        }

        NoSideTrip();

        RecallAfterTrip(leader);

        _currentQuest = UltimusPlanner.PickQuest(_team);
        NoteWorld(_currentQuest);
        if (_currentQuest is not null) {
            var target = UltimusPlanner.ForQuest(group, _currentQuest);
            // A step he can't place: the area or person the quest's own words name (UltimusDirector.Sense.cs).
            if (HintFromText(leader, target) is { } hint) {
                return hint;
            }

            if (target is not null) {
                foreach (var old in _stonesUsed.Where(s => DateTime.UtcNow - s.Value > TimeSpan.FromSeconds(90)).Select(s => s.Key).ToList()) {
                    _stonesUsed.Remove(old);
                }

                // A quest fight lost again and again: a side quest of his level first (never farming creatures for XP).
                if (LevelFirst(leader, target) is { } xp) {
                    return xp;
                }

                // Another quest's step right by him first, and a wisp before a fight (UltimusDirector.Smart.cs).
                if (WhileHere(group, target) is { } here) {
                    return TopUpBeforeFight(leader, here) ?? here;
                }

                if (TopUpBeforeFight(leader, target) is { } topUp) {
                    return topUp;
                }

                return UltimusPlanner.Shortcut(leader, target, _stonesUsed.Keys) ?? target;
            }
        }

        // Nobody here has a story quest: the next one, from whoever gives it.
        if (UltimusPlanner.NextQuest(leader.Wizard) is var (quest, giver)) {
            return UltimusPlanner.ForGiver(leader, quest, giver);
        }

        // Under-leveled for the next story quest: he fights creatures for the XP, as a player does (Niduus, 2026-11-14: no XP
        // command; he quests as a human). UltimusDirector.Push.cs.
        if (_currentQuest is null && UltimusPlanner.LevelBlocked(leader.Wizard) is var (blocked, level)) {
            return XpTarget(leader, level, $"'{UltimusText.Title((string) blocked.m_questName)}' needs level {level}", gate: true);
        }

        // The whole story played, to the last world the data has: they stop (paused, so Ahziael's Resume or Restart decides).
        if (_currentQuest is null && UltimusPlanner.FinishedLastWorld(leader.Wizard, out var lastWorld)) {
            Note("Ultimus", $"MAIN STORY COMPLETE: the last world, {lastWorld}, is done ({DateTime.Now.ToString("dddd d MMMM yyyy, HH:mm", System.Globalization.CultureInfo.InvariantCulture)}). " +
                         "The team stops here.", UltimusLink.COMMAND);
            UltimusLink.Send(new UltimusRequest { Op = "quest", Event = "story done", Quest = "", Title = "Main Story", Text = $"finished with {lastWorld}; the team stopped" });
            try {
                File.WriteAllText(Path.Combine(UltimusWire.Folder, UltimusWire.PAUSE_FILE), "Main Story complete");
            }
            catch (IOException) {
                // Paused next look anyway: the note says it.
            }

            _paused = true;
            _doing = "finished the Main Story; stopped";
            Milestone("story", $"Ultimus finished the whole Main Story, to {lastWorld}.");

            return null;
        }

        if (_currentQuest is null) {
            _doing = "no World or Story quest is left to get";
            Issue("team", null, "There is no World or Story quest left for the team to get. If they have not reached the last world, " +
                                "the next quest's giver or its requirements are the problem.", key: "nothing-left|" + leader.Zone);
        }

        return null;
    }

    private string _storyWorld;

    // A new world's story begun: the last one is done, bookmarked in the quest log with the date and time (Niduus,
    // 2026-11-12: "Keep going, book marking where they got the world completed in logs with date and time").
    private void NoteWorld(string quest) {
        if (string.IsNullOrEmpty(quest) || QuestTemplateCollection.GetQuestByName(quest) is not { } template || !QuestStory.IsMain(template)) {
            return;
        }

        var world = QuestStory.WorldOf(template);
        if (string.IsNullOrEmpty(world) || string.Equals(world, _storyWorld, StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        if (_storyWorld is not null) {
            var at = DateTime.Now.ToString("dddd d MMMM yyyy, HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            Note("Ultimus", $"WORLD COMPLETE: {_storyWorld} on {at}; on to {world}.");
            Milestone("world", $"Ultimus finished {_storyWorld}'s story on {at} at level {_team[0].Wizard?.MagicSchoolBehavior?.Level ?? 0}; on to {world}.");
            UltimusJournal.Write(UltimusJournal.WORLD, $"Finished the story of {WorldName(_storyWorld)} at level {_team[0].Wizard?.MagicSchoolBehavior?.Level ?? 0}.",
                WorldName(_storyWorld), "", WorldName(_storyWorld), 1);
            Count("worlds completed");
            UltimusLink.Send(new UltimusRequest { Op = "quest", Event = "world done", Quest = _storyWorld, Title = $"World {_storyWorld}",
                Text = $"story finished on {at}; the team goes on to {world} (levels {string.Join(", ", _team.Select(m => $"{m.Role} {m.Wizard?.MagicSchoolBehavior?.Level ?? 0}"))})" });
        }

        UltimusJournal.Write(UltimusJournal.WORLD, $"Started {WorldName(world)}'s story at level {_team[0].Wizard?.MagicSchoolBehavior?.Level ?? 0}.",
            WorldName(world), "", WorldName(world));
        Snapshot(world);
        WorldStarted(world, _team[0].Wizard?.MagicSchoolBehavior?.Level ?? 0);
        NewWorldGearCheck(_team[0], world);
        _storyWorld = world;
    }

    private List<string> _usernames = [];

    // A copy of the team's accounts and wizards before each new world (Niduus, 2026-11-12: "a snapshot before each new
    // world"), saved as Account Transfer files in the Ultimus folder's Snapshots, so a broken world can be undone by
    // loading one in Ahziael's Account Transfer tab.
    private void Snapshot(string world) {
        var names = _usernames.ToList();
        var folder = Path.Combine(UltimusWire.Folder, "Snapshots");
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm", System.Globalization.CultureInfo.InvariantCulture);
        _ = System.Threading.Tasks.Task.Run(async () => {
            try {
                Directory.CreateDirectory(folder);
                foreach (var username in names) {
                    var file = await global::Imlight.RemoteAdmin.AccountTransfer.ExportAsync(Tools.RemoteAdmin.RemoteAdminServer.PlayerDatabaseHttp(), username, "Ultimus", null);
                    var path = Path.Combine(folder, $"{stamp} before {world} - {username}{(global::Imlight.RemoteAdmin.AccountTransfer.EXTENSION)}");
                    await File.WriteAllBytesAsync(path, global::Imlight.RemoteAdmin.AccountTransfer.ToBytes(file));
                    Note("Ultimus", $"SNAPSHOT before {world}: saved {Path.GetFileName(path)}.", UltimusLink.COMMAND);
                }
            }
            catch (Exception ex) {
                Note("Ultimus", $"could not save the snapshot before {world}: {ex.Message}", UltimusLink.NOTABLE);
            }
        });
    }

    private UltimusPlanner.Target TrainingTarget(List<UltimusMember.Status> group) {
        if (DateTime.UtcNow < _nextTraining) {
            return _target?.Kind == UltimusPlanner.TRAIN ? _target : null;
        }

        var lessons = group.Select(m => (Member: m, Lessons: UltimusTraining.Learnable(m))).Where(x => x.Lessons.Count > 0).ToList();
        if (lessons.Count == 0) {
            _nextTraining = DateTime.UtcNow.AddSeconds(30);

            return null;
        }

        var leader = group[0];

        // Never out of a dungeon for it: the trip waits until they are out.
        if (group.Any(m => m.Wizard?.ZoneIsInstance == true)) {
            return null;
        }

        // A trainer right here: trained on the way. Otherwise a trip to Ravenwood when in Wizard City or when three wait.
        var here = lessons.SelectMany(l => l.Lessons.Where(x => OverlayQuestTracker.SameZone(x.Zone, leader.Zone)).Select(x => (l.Member, Lesson: x))).FirstOrDefault();
        var inWizardCity = (leader.Zone ?? "").StartsWith("WizardCity/", StringComparison.OrdinalIgnoreCase);
        if (here.Lesson is null && !inWizardCity && lessons.Sum(l => l.Lessons.Count) < 3) {
            _nextTraining = DateTime.UtcNow.AddSeconds(30);

            return null;
        }

        if (here.Lesson is null && !OverlayQuestTracker.SameZone(UltimusTraining.TRAINER_ZONE, leader.Zone)) {
            return UltimusPlanner.Travel(leader, UltimusTraining.TRAINER_ZONE, "training");
        }

        // The first lesson in the team's order here; its trainer's spot.
        var (member, lesson) = here.Lesson is not null ? here
            : lessons.SelectMany(l => l.Lessons.Where(x => OverlayQuestTracker.SameZone(x.Zone, leader.Zone)).Select(x => (l.Member, Lesson: x))).FirstOrDefault();
        if (lesson is null) {
            _nextTraining = DateTime.UtcNow.AddSeconds(30);

            return null;
        }

        var live = MinimapIndex.In(leader.ZoneActor);
        var trainer = live.FirstOrDefault(m => m.Template == lesson.TrainerTemplate);
        var (x, y) = trainer is null ? (lesson.X, lesson.Y) : (trainer.Object.m_location.X, trainer.Object.m_location.Y);

        return new UltimusPlanner.Target(UltimusPlanner.TRAIN, x, y, $"{member.Name} trains {lesson.Spell}", Template: lesson.TrainerTemplate,
            GlobalId: trainer?.Object.m_globalID.Full ?? 0, Key: $"train|{member.Role}|{lesson.SpellId}");
    }

    // ---------------------------------------------------------------------
    // Clicking things
    // ---------------------------------------------------------------------

    private bool CanClick(string key) {
        if (DateTime.UtcNow < _nextClick) {
            return false;
        }

        return !_clicks.TryGetValue(key, out var done) || DateTime.UtcNow - done.Last > CLICK_AGAIN;
    }

    // Quick hands (Niduus, 2026-11-13: "Have Ultimus be more responsive"): a click a second at most, the same thing clicked
    // again after 12 seconds when it did nothing.
    private static readonly TimeSpan CLICK_AGAIN = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan s_resendWalk = TimeSpan.FromSeconds(8);

    private void Clicked(string key) {
        _nextClick = DateTime.UtcNow.AddSeconds(1);
        _clicks[key] = (_clicks.TryGetValue(key, out var done) ? done.Times + 1 : 1, DateTime.UtcNow);
    }

    private int TimesClicked(string key) => _clicks.TryGetValue(key, out var done) ? done.Times : 0;

    // Each wizard who needs to, in the team's order, clicks the NPC's option once it is in range of it.
    private void ClickEach(List<UltimusMember.Status> group, UltimusPlanner.Target target, string[] services,
                           Func<UltimusMember.Status, bool> needs) {
        foreach (var member in group) {
            if (!needs(member)) {
                continue;
            }

            var index = _team.IndexOf(member);
            var key = $"{target.Key}|{index}";
            if (UltimusPlanner.Far(member.X, member.Y, target.X, target.Y) > 200) {
                WalkTo(index, target with { X = target.X - 40 });

                return;
            }

            if (!member.Options.TryGetValue(target.GlobalId, out var options)) {
                // Not sent its options yet: a step closer.
                if (UltimusPlanner.Far(member.X, member.Y, target.X, target.Y) > 80) {
                    WalkTo(index, target with { X = target.X - 30 });
                }
                else if (TimesClicked(key) == 0 && DateTime.UtcNow - _targetSince > TimeSpan.FromSeconds(90)) {
                    Issue("quest", target.Quest, $"{target.What}: {member.Name} stands next to the NPC, but it offers nothing to click.",
                        key: key + "|nooptions");
                }

                return;
            }

            var matching = services.SelectMany(s => options.Where(o => string.Equals(o.Service, s, StringComparison.OrdinalIgnoreCase))).ToList();
            var option = BotOptions.Pick(matching, target.Quest, TimesClicked(key));
            if (option.Service is null) {
                if (DateTime.UtcNow - _targetSince > TimeSpan.FromSeconds(40)) {
                    Issue("quest", target.Quest, $"{target.What}: the NPC only offers {string.Join(", ", options.Select(o => o.Service))} to {member.Name}, " +
                                                 "not the quest.", key: key + "|wrongoptions");
                }

                continue;
            }

            if (!CanClick(key)) {
                if (TimesClicked(key) >= 3) {
                    Issue("quest", target.Quest, $"{target.What}: {member.Name} talked to the NPC {TimesClicked(key)} times and the step did not finish.",
                        key: key + "|nofinish");
                }

                return;
            }

            Clicked(key);
            Note(member.Role, $"{member.Name} talks to them ({option.Service}).");
            _actors[index].Tell(new UltimusMember.Interact(target.GlobalId, option.Service, option.Index, target.What));
            // Talking can take the leader somewhere (Merle Ambrose sends a new wizard on to Unicorn Way): the others, left
            // behind, then talk to the same NPC to follow (Niduus, 2026-11-12: "they are stuck facing Merle").
            if (index == 0) {
                _leaderLastTarget = target;
            }

            return;
        }
    }

    private void ClickOnce(int index, UltimusPlanner.Target target, string[] services, string what) {
        var member = _team[index];
        var key = $"{target.Key}|{index}";
        if (!CanClick(key)) {
            return;
        }

        if (!member.Options.TryGetValue(target.GlobalId, out var options)) {
            WalkTo(index, target with { X = target.X - 20 });

            return;
        }

        var option = BotOptions.Pick([.. services.SelectMany(s => options.Where(o => string.Equals(o.Service, s, StringComparison.OrdinalIgnoreCase)))],
            target.Quest, TimesClicked(key));
        if (option.Service is null) {
            option = options.FirstOrDefault();
        }

        Clicked(key);
        Note(member.Role, $"{member.Name} {what}.");
        _actors[index].Tell(new UltimusMember.Interact(target.GlobalId, option.Service ?? "Interact", option.Index, what));
        if (index == 0) {
            _leaderLastTarget = target;
        }
    }

    // A thing to use where the quest arrow points: each wizard who needs it clicks what offers "Interact" near the spot.
    private void UseHere(List<UltimusMember.Status> group, UltimusPlanner.Target target) {
        foreach (var member in group.Where(m => HoldsGoal(m, target.Quest, target.Goal))) {
            var index = _team.IndexOf(member);
            if (UltimusPlanner.Far(member.X, member.Y, target.X, target.Y) > 120) {
                WalkTo(index, target);

                return;
            }

            // Only what it hasn't used for the step yet; a spot with nothing left is done, and it goes on to the next.
            var everything = member.Options.Where(o => o.Value.Any(s => s.Service == "Interact")).Select(o => o.Key).ToList();
            var usable = everything.Where(id => !BotCollect.HasUsed(member.CharId, target.Quest, target.Goal, id)).ToList();
            if (usable.Count == 0 && everything.Count > 0) {
                BotCollect.SpotDone(member.CharId, target.Quest, target.Goal, target.X, target.Y);
                Note(member.Role, $"{member.Name} already used what is here for {target.What}: on to the next one.");

                return;
            }

            if (usable.Count == 0) {
                if (DateTime.UtcNow - _targetSince > TimeSpan.FromSeconds(45)) {
                    Issue("quest", target.Quest, $"{target.What}: at the quest arrow's spot there is nothing {member.Name} can use.",
                        key: target.Key + "|nothingtouse");
                }

                return;
            }

            foreach (var id in usable) {
                var key = $"{target.Key}|{index}|{id}";
                if (!CanClick(key)) {
                    continue;
                }

                var option = member.Options[id].First(s => s.Service == "Interact");
                Clicked(key);
                BotCollect.Used(member.CharId, target.Quest, target.Goal, id, target.X, target.Y);
                Note(member.Role, $"{member.Name} uses something for {target.What}.");
                _actors[index].Tell(new UltimusMember.Interact(id, option.Service, option.Index, target.What));

                return;
            }

            if (DateTime.UtcNow - _targetSince > TimeSpan.FromSeconds(90)) {
                Issue("quest", target.Quest, $"{target.What}: {member.Name} used what is there and the step did not finish.", key: target.Key + "|usednothing");
            }

            return;
        }
    }

    private void TrainHere(List<UltimusMember.Status> group, UltimusPlanner.Target target) {
        var parts = target.Key.Split('|');
        var member = _team.FirstOrDefault(m => m.Role == parts[1]);
        if (member is null || !uint.TryParse(parts[2], out var spellId)) {
            return;
        }

        var index = _team.IndexOf(member);
        if (UltimusPlanner.Far(member.X, member.Y, target.X, target.Y) > 200) {
            WalkTo(index, target with { X = target.X - 40 });

            return;
        }

        var key = $"{target.Key}|{index}";
        if (!CanClick(key)) {
            if (TimesClicked(key) >= 2) {
                // Could not learn it here; the next lessons first, back to it later.
                _nextTraining = DateTime.UtcNow.AddMinutes(10);
            }

            return;
        }

        var spot = UltimusTraining.IndexAt(member, target.GlobalId, spellId);
        if (spot < 0) {
            Note(member.Role, $"{member.Name} can't find {UltimusText.SpellName(spellId)} in this trainer's list.");
            _nextTraining = DateTime.UtcNow.AddMinutes(10);

            return;
        }

        Clicked(key);
        if (member.Options.TryGetValue(target.GlobalId, out var options) && options.FirstOrDefault(o => o.Service == "WizTrainingService") is { Service: not null } open) {
            _actors[index].Tell(new UltimusMember.Interact(target.GlobalId, open.Service, open.Index, "training"));
            HoldRecall(6);
        }

        Note(member.Role, $"{member.Name} trains {UltimusText.SpellName(spellId)}.");
        Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(1.5), _actors[index],
            new UltimusMember.Train(target.GlobalId, spot, UltimusText.SpellName(spellId)), Self);
        _nextTraining = DateTime.UtcNow.AddSeconds(3);
    }

    private static bool HoldsGoal(UltimusMember.Status member, string questName, string goalName) {
        var held = member.Wizard?.QuestBehavior?.CurrentQuestInstances?.FirstOrDefault(q => q is not null
            && string.Equals(q.QuestName, questName, StringComparison.OrdinalIgnoreCase));

        return held is not null && (string.IsNullOrEmpty(goalName) || held.IsGoalActive(goalName));
    }

    // ---------------------------------------------------------------------
    // Walking in a line
    // ---------------------------------------------------------------------

    private void WalkTo(int index, UltimusPlanner.Target target) {
        if (Unsticking(index)) {
            return;
        }

        var member = _team[index];

        // Creatures it is not here to fight, watched all the way, as a player watches them walk about (Niduus,
        // 2026-11-13: "I just saw him willingly put himself in battle. Have detections for mobs"): one walking up close
        // has it step back out of its reach, and one near the way has the way planned again round it.
        // Short of the level his story needs: creatures on the way are XP, not walked round (UltimusDirector.Smart.cs).
        var seek = index == 0 && target.Kind != UltimusPlanner.FIGHT && SeekFights(member);
        var avoid = seek ? null : CreaturesToAvoid(member, target);
        var near = avoid?.Select(c => (c.X, c.Y, Far: UltimusPlanner.Far(member.X, member.Y, c.X, c.Y))).OrderBy(c => c.Far).FirstOrDefault();
        var replanSoon = near is { Far: < MOB_WATCH } && DateTime.UtcNow - _sent.GetValueOrDefault(index).At > TimeSpan.FromSeconds(2);
        if (near is { Far: < MOB_TOO_CLOSE and > 1 } close && DateTime.UtcNow - _sent.GetValueOrDefault(index).At > TimeSpan.FromSeconds(1.5)) {
            var away = (X: member.X + ((member.X - close.X) / close.Far * MOB_STEP_BACK), Y: member.Y + ((member.Y - close.Y) / close.Far * MOB_STEP_BACK));
            var back = OverlayQuestPath.Walk(member.CharId, member.Zone, member.X, member.Y, away.X, away.Y, body: true, avoid: avoid);
            _sent[index] = (null, DateTime.UtcNow, member.X, member.Y);
            _actors[index].Tell(new UltimusMember.WalkTo(back, "steps back from a creature it does not need to fight"));
            if (_mobDodgeSaid.AddSeconds(30) < DateTime.UtcNow) {
                _mobDodgeSaid = DateTime.UtcNow;
                Note("Ultimus", $"{member.Name} steps back from a creature {close.Far:0} away that its step does not need it to fight.");
            }

            return;
        }

        if (!replanSoon && _sent.TryGetValue(index, out var last) && last.Target is not null && last.Target.Same(target) && member.Walking
            && DateTime.UtcNow - last.At < s_resendWalk) {
            return;
        }

        // Round the creatures it does not need to fight, not into a battle (Niduus, 2026-11-13: "avoid battles as much as
        // possible"), and by the area's teleporters when walls or water cut the step off (UltimusDirector.Routes.cs).
        var (toX, toY, what) = WayTo(member, target);
        // By a door or street teleporter stone of the way: pressed, as a player presses X (standing by it does nothing).
        if (PadToPress(member) is { } press && UltimusPlanner.Far(member.X, member.Y, press.X, press.Y) <= PRESS_REACH
            && member.Options.ContainsKey(press.Click)) {
            ClickOnce(index, new UltimusPlanner.Target(UltimusPlanner.STONE, press.X, press.Y, press.Name, GlobalId: press.Click, Key: "pad|" + press.Click),
                ["Interact"], $"presses {press.Name}, the teleporter toward \"{target.What}\"");

            return;
        }

        // The street's creature areas too, walked or not right now (MobSpawnAreas), except the step's own creatures.
        var walkAvoid = seek ? null : RouteMobs.Avoid(member.ZoneActor, toX, toY, target.Template == 0 ? null : [target.Template], avoid);
        var points = SightWalk(member, toX, toY, what, target.Kind == UltimusPlanner.FIGHT ? null : avoid) ?? OverlayQuestPath.Walk(member.CharId, member.Zone, member.X, member.Y, toX, toY, body: true, avoid: walkAvoid);
        // A walk that goes nowhere while the step is far: said once per step, with the spots, so the log shows where the
        // map gives no way (Niduus's log, 2026-11-13: it stood at The Oasis's start for an hour with nothing said).
        if (UltimusPlanner.Far(member.X, member.Y, toX, toY) > 300
            && (points.Count == 0 || UltimusPlanner.Far(member.X, member.Y, points[^1].X, points[^1].Y) < 60)
            && _noWaySaid.Add($"{member.Zone}|{(int) (toX / 200)}|{(int) (toY / 200)}")) {
            Note("Ultimus", $"{member.Name} finds no way on the map from ({member.X:0}, {member.Y:0}) to \"{what}\" at ({toX:0}, {toY:0}) in {UltimusText.Zone(member.Zone)}.",
                UltimusLink.ABNORMAL);
        }

        _sent[index] = (target, DateTime.UtcNow, member.X, member.Y);
        _actors[index].Tell(new UltimusMember.WalkTo(Wobble(Smooth(member.Zone, StopShort(points, target))), what));
        if (index == 0) {
            _leaderLastTarget = target;
        }
    }

    // A creature this near the way has the way planned again; this near Ultimus, it steps back (a creature starts a
    // battle within about 175).
    // How long a step that can't be worked out is looked into before it is written up (flagged, never skipped).
    private static readonly TimeSpan s_problemPatience = TimeSpan.FromMinutes(2);
    private readonly Dictionary<string, DateTime> _problemSince = new(StringComparer.Ordinal);
    private (float X, float Y, DateTime At)? _lookingAt;

    // Walks about the area, from one landmark to another (the step's person or creature may turn up, or a "go to"
    // spot may count on the way), as a player looks round a room for what the quest book means.
    private void LookAround(UltimusMember.Status leader, UltimusPlanner.Target target) {
        if (_lookingAt is { } at && DateTime.UtcNow - at.At < TimeSpan.FromSeconds(20)
                                 && UltimusPlanner.Far(leader.X, leader.Y, at.X, at.Y) > 60) {
            return;
        }

        var marks = MinimapIndex.In(leader.ZoneActor).Where(m => m.Kind is not (MinimapIndex.MOB or MinimapIndex.BOSS))
            .Select(m => (m.Object.m_location.X, m.Object.m_location.Y))
            .Where(m => UltimusPlanner.Far(leader.X, leader.Y, m.X, m.Y) is > 300 and < 4000).ToList();
        if (marks.Count == 0) {
            StopWalking(0);
            return;
        }

        var (x, y) = marks[Random.Shared.Next(marks.Count)];
        _lookingAt = (x, y, DateTime.UtcNow);
        WalkTo(0, new UltimusPlanner.Target(UltimusPlanner.SPOT, x, y, "looking around for " + target.What, target.Quest, target.Goal,
            Key: target.Key + "|look"));
    }

    private const float MOB_WATCH = 450f;
    private const float MOB_TOO_CLOSE = 240f;
    private const float MOB_STEP_BACK = 260f;
    private DateTime _mobDodgeSaid = DateTime.MinValue;

    private void StopWalking(int index) {
        if (_team[index].Walking && !Unsticking(index)) {
            _actors[index].Tell(new UltimusMember.StopWalking());
            _sent.Remove(index);
        }
    }

    // The leader changed areas: where it left from and how, for the ones behind.
    private void NoteLeaderZone(UltimusMember.Status leader) {
        if (_leaderZone.Length > 0 && !string.Equals(_leaderZone, leader.Zone, StringComparison.OrdinalIgnoreCase)) {
            Note("Ultimus", $"{leader.Name} is now in {UltimusText.Zone(leader.Zone)}.");
            // A Recall or a Commons button press of his own is a player move, never a wrong landing (audit A6: a Recall back to
            // his mark was taken as a wrong area and he walked out again).
            var buttonMove = DateTime.UtcNow - _recallSentAt < TimeSpan.FromSeconds(30) || DateTime.UtcNow - _hubPressedAt < TimeSpan.FromSeconds(30);
            var meant = !buttonMove && _prediction is { Kind: "travel" } travel ? travel.Zone : null;
            CheckArrival(leader);
            // The landing the zone transfer book says, and backing out of a wrong area (UltimusDirector.Bounds.cs).
            CheckLanding(_leaderZone, _leaderLastX, _leaderLastY, leader, meant);

            // Going in and out of areas again and again is a way that loops (Niduus's log, 2026-11-12: "to Savarstaad Pass"
            // while already in Savarstaad Pass, every few seconds): an issue (flagged for Niduus, never skipped).
            // Only coming back into the same area again and again counts: a quick trip through many areas (The Commons,
            // Ravenwood, the World Tree, the Spiral Door's landing and The Oasis) is no loop (Niduus's log, 2026-11-13: "A
            // Whole New World" was skipped as a loop on its way to Krokotopia).
            _zoneChanges.Add((DateTime.UtcNow, leader.Zone ?? ""));
            _zoneChanges.RemoveAll(t => DateTime.UtcNow - t.At > TimeSpan.FromSeconds(90));
            var again = _zoneChanges.Count(z => string.Equals(z.Zone, leader.Zone, StringComparison.OrdinalIgnoreCase));
            if (_zoneChanges.Count >= 8 && again >= 4) {
                _zoneChanges.Clear();
                var what = _target?.What ?? "the next step";
                Issue("quest", _target?.Quest ?? _currentQuest, $"The way to \"{what}\" goes round in a loop: {leader.Name} went in and out of " +
                                                               $"{UltimusText.Zone(leader.Zone)} 8 times in 90 seconds without reaching it.",
                    key: "loop|" + (_target?.Key ?? leader.Zone));
                StopWalking(0);
            }
        }

        _leaderZone = leader.Zone ?? "";
        _leaderLastX = leader.X;
        _leaderLastY = leader.Y;
    }

    // ---------------------------------------------------------------------
    // Battles
    // ---------------------------------------------------------------------

    private DateTime _battleSince = DateTime.MaxValue;
    private int _battlesWithPlayers;
    private bool _battleCompany;
    private int _companyCount;
    private List<(ulong CharId, string Name)> _battlePlayers = [];

    private bool HandleBattle() {
        var me = _team[0];
        var now = CombatDuelComponent.IsInBattle(me.GlobalId);
        if (_wasInBattle[0] && !now) {
            BattleOver(0);
        }

        if (!_wasInBattle[0] && now) {
            _battleSince = DateTime.UtcNow;
            NoteFightKit(me);
            FightStarted(me);
            var others = CombatDuelComponent.UltimusBattleCompany(me.GlobalId);
            _battleCompany = others.Count > 0;
            _battlePlayers = CombatDuelComponent.UltimusBattlePlayers(me.GlobalId);
            _companyCount = others.Count;
            if (others.Count > 0) {
                _battlesWithPlayers++;
                Note("Ultimus", $"is in a battle with {string.Join(", ", others)}; it welcomes them and plays with them.");
                // It says hello in Menu Chat: "I'll help" when it walked into theirs, a welcome when they walked into its.
                Speak(_target?.Key?.EndsWith("|join", StringComparison.Ordinal) == true ? "join" : "welcome", 0.6);
            }
        }

        _wasInBattle[0] = now;
        if (!now) {
            _battleSince = DateTime.MaxValue;

            return false;
        }

        // A player walked into its battle after it began: a welcome in Menu Chat.
        var company = CombatDuelComponent.UltimusBattleCompany(me.GlobalId).Count;
        if (company > _companyCount && _companyCount >= 0 && _battleSince < DateTime.UtcNow.AddSeconds(-3)) {
            _battleCompany = true;
            Note("Ultimus", "a player joined its battle; it welcomes them.");
            Speak("welcome", 0.7);
        }

        _companyCount = company;
        if (company > 0) {
            _battlePlayers = CombatDuelComponent.UltimusBattlePlayers(me.GlobalId);
        }

        _doing = "in a battle";
        _lastProgress = DateTime.UtcNow;
        UltimusDirector.SetBusy(me.ZoneActor, me.X, me.Y);

        // A battle that goes on and on is odd for one wizard at its level (pink, once per battle).
        if (DateTime.UtcNow - _battleSince > TimeSpan.FromMinutes(10)) {
            _battleSince = DateTime.MaxValue - TimeSpan.FromDays(1);
            UltimusLink.Abnormal($"has been in the same battle for over 10 minutes{(_target is null ? "" : $" (for \"{_target.What}\")")}; a solo player's battle at this level is usually a few minutes.");
        }

        return true;
    }

    private bool _paused;

    // Battles won for each fighting step (quest|goal), and how many is odd for one step.
    private readonly Dictionary<string, int> _fightsFor = [];
    private const int DROP_FIGHTS_ODD = 12;
    private DateTime _nextPauseLook = DateTime.MinValue;
    private void BattleOver(int index) {
        var member = _team[index];
        var stats = member.Wizard?.GameStats;
        if (stats is null) {
            return;
        }

        var lost = CombatDuelComponent.TakeLostBattle(member.GlobalId) || stats.m_currentHitpoints <= 1;
        if (index == 0) {
            FightEnded(member, !lost);
        }

        if (!lost || index != 0) {
            if (index == 0) {
                Note("Ultimus", "won the battle.");
                NoteFightSpot(member);
                if (_battleCompany) {
                    Speak("won", 0.5);
                }

                // Who he fought with is remembered ("We've fought 3 battles together now").
                foreach (var (charId, name) in _battlePlayers) {
                    BotSocial.FoughtWith(UltimusRoster.NAME, charId, name);
                }

                Count("battles won");
                // A won fight for a step or while levelling for the story is story progress (the guardrails' off-track alarm).
                if (_target?.Kind == UltimusPlanner.FIGHT) {
                    StoryProgress();
                }

                // Fights won for each step, to see how many kills a drop or a count takes (Niduus: "flag abnormal drops").
                if (_target is { Kind: UltimusPlanner.FIGHT } fought) {
                    _fightsFor[fought.Key] = _fightsFor.GetValueOrDefault(fought.Key) + 1;
                }
            }

            return;
        }

        var key = _target?.Key ?? member.Zone;
        _losses[key] = _losses.GetValueOrDefault(key) + 1;
        Note("Ultimus", $"lost the battle ({_losses[key]} time(s) at this step).", UltimusLink.MINOR);
        // Learning from it (Niduus, 2026-11-12: "Both documenting mistakes for future learning").
        var level = member.Wizard?.MagicSchoolBehavior?.Level ?? 0;
        UltimusLink.Learned("lost-fight", _target?.Quest ?? "", _target?.Goal ?? "", member.Zone ?? "",
            $"Lost the fight for \"{_target?.What ?? "this step"}\" alone at level {level} ({_losses[key]} time(s)).",
            _losses[key] == 1
                ? "Goes in at full health and mana with a deck made for these creatures, and fights fewer at once."
                : "Waits for a player to join or asks for help through an issue; the step may need a group.");
        Count("battles lost");
        // Two or more: he reasons out the best remedy himself (UltimusDirector.Losses.cs).
        PlanAfterLoss(member, key, _losses[key]);
        if (_losses[key] >= UltimusWire.LossesBeforeIssue) {
            // A fight he keeps losing is too hard for him now, not broken: he flags it and keeps going at it as a player would
            // (rested, careful), never skipping it (Niduus, 2026-11-14).
            Issue("quest", _target?.Quest, $"The team lost the fight for \"{_target?.What ?? "this step"}\" {_losses[key]} times. He rests and tries again; nothing is skipped.",
                key: key + "|losses" + (_losses[key] > UltimusWire.LossesBeforeIssue ? "|again" : ""), broken: false,
                level: _losses[key] > UltimusWire.LossesBeforeIssue ? UltimusLink.CRITICAL : null);
        }
    }

    private void UsePotions() {
        for (var i = 0; i < _team.Count; i++) {
            var stats = _team[i].Wizard?.GameStats;
            if (stats is null || CombatDuelComponent.IsInBattle(_team[i].GlobalId)) {
                continue;
            }

            var most = Math.Max(1, stats.m_baseHitpoints + stats.m_bonusHitpoints);
            var key = "potion|" + i;
            if (stats.m_currentHitpoints < most * 0.4 && stats.m_potionCharge >= 1 && CanClick(key)) {
                Clicked(key);
                Note(_team[i].Role, $"{_team[i].Name} drinks a potion ({stats.m_currentHitpoints} of {most} health).");
                _actors[i].Tell(new UltimusMember.UsePotion());
            }
        }
    }

    // ---------------------------------------------------------------------
    // Progress, the log and the alerts
    // ---------------------------------------------------------------------

    private void TrackProgress() {
        var parts = new List<string>();
        foreach (var member in _team) {
            var wizard = member.Wizard;
            if (wizard?.QuestBehavior is null) {
                continue;
            }

            parts.Add($"{member.Role}:{member.Zone}:{wizard.MagicSchoolBehavior?.Level}");
            foreach (var quest in wizard.QuestBehavior.CurrentQuestInstances?.ToList() ?? []) {
                if (quest is null) {
                    continue;
                }

                var active = (quest.GoalProgress ?? []).Where(g => g is not null && quest.IsGoalActive(g.GoalName)).Select(g => g.GoalName).ToList();
                parts.Add($"{quest.QuestName}={string.Join(',', active)}");
                StartRun(quest.QuestName);
                foreach (var done in (quest.GoalProgress ?? []).Where(g => g is not null && quest.IsGoalCompleted(g.GoalName))) {
                    if (_fightsFor.Remove(quest.QuestName + "|" + done.GoalName, out var fights) && _seenGoals.Add("fights|" + quest.QuestName + "|" + done.GoalName)) {
                        UltimusLink.Send(new UltimusRequest { Op = "quest", Event = "step", Quest = quest.QuestName, Title = UltimusText.Title(quest.QuestName),
                            Text = $"\"{done.GoalName}\" took {fights} battle(s) won" });
                        if (fights > DROP_FIGHTS_ODD) {
                            Issue("quest", quest.QuestName, $"The step \"{done.GoalName}\" took {fights} battles won to finish, far more than a player expects. " +
                                                            "The drop chance or the creatures that count for it may be wrong.", key: "drops|" + quest.QuestName + "|" + done.GoalName, broken: false, level: UltimusLink.MINOR);
                        }
                    }

                    if (_seenGoals.Add(quest.QuestName + "|" + done.GoalName)) {
                        StoryProgress();
                        PauseAfterStep();
                        UltimusLink.Send(new UltimusRequest { Op = "quest", Event = "step", Quest = quest.QuestName, Title = UltimusText.Title(quest.QuestName),
                            Text = $"{member.Name} finished a step" });
                    }
                }
            }
        }

        // Levelling up for a quest (XP fights, and healing between them) is not the quest being slow.
        var sinceCheck = Math.Min(30, (DateTime.UtcNow - _runsCheckedAt).TotalSeconds);
        _runsCheckedAt = DateTime.UtcNow;
        var grinding = (_target ?? _leaderLastTarget)?.What?.StartsWith("a fight for XP", StringComparison.Ordinal) == true;

        // A quest none of them holds any more, that one of them finished: done.
        foreach (var run in _runs.Values.ToList()) {
            if (grinding) {
                run.GrindSeconds += sinceCheck;
            }

            var holder = _team.Any(m => m.Wizard?.HasQuest(run.Quest) == true);
            var finished = _team.Any(m => m.Wizard?.HasCompletedQuest(run.Quest) == true);
            if (!holder && finished) {
                var seconds = (DateTime.UtcNow - run.Started).TotalSeconds;
                Note("Ultimus", $"FINISHED {run.Title} in {Minutes(seconds)}.");
                UltimusJournal.Write(UltimusJournal.QUEST, seconds > 30 * 60 ? $"Finished {run.Title}, but it took me {Minutes(seconds)}." : $"Finished {run.Title} in {Minutes(seconds)}.",
                    WorldOf(_team[0].Zone), UltimusText.Zone(_team[0].Zone), run.Title, seconds > 30 * 60 ? -1 : 1);
                StoryProgress();
                Count("quests finished");
                UltimusLink.Send(new UltimusRequest { Op = "quest", Event = "done", Quest = run.Quest, Title = run.Title, World = run.World, Seconds = seconds });
                _runs.Remove(run.Quest);
            }
            else if (!run.LimitTold && UltimusWire.QuestMinutes > 0 && run.Seconds / 60 > UltimusWire.QuestMinutes) {
                run.LimitTold = true;
                Issue("slow", run.Quest, $"{run.Title} went past the time limit of {UltimusWire.QuestMinutes} minutes ([Ultimus] QuestMinutes): " +
                                         $"{Minutes(run.Seconds)} so far (not counting levelling up for it).", key: run.Quest + "|limit");
            }
            else if (!run.BudgetTold && run.Seconds > Math.Max(30 * 60, run.Expected * UltimusPlaybook.Value("Guard.QuestFactor"))) {
                // The quest's time budget (the guardrails): plan again, another way.
                run.BudgetTold = true;
                Note("Ultimus", $"TIME BUDGET: {run.Title} went over its budget ({Minutes(run.Seconds)}, about {Minutes(run.Expected)} expected); plans again.",
                    UltimusLink.ABNORMAL);
                Think($"{run.Title} ran over its time budget ({UltimusPlaybook.Cite("Guard.QuestFactor")} its expected time, at least 30 minutes). " +
                      "I plan it again from where I stand, and try its steps another way.", "budget-quest|" + run.Quest);
                Ban((_target ?? _leaderLastTarget)?.Key, TimeSpan.FromMinutes(10));
                Replan();
            }
            else if (!run.SlowTold && run.Expected > 0 && run.Seconds > run.Expected * UltimusWire.SlowFactor) {
                run.SlowTold = true;
                Issue("slow", run.Quest, $"{run.Title} is taking much longer than expected: {Minutes(run.Seconds)} so far (not counting levelling up for it), " +
                                          $"about {Minutes(run.Expected)} expected. Now: {_doing}.", key: "slow|" + run.Quest);
            }
        }

        var signature = string.Join(';', parts);
        if (signature != _signature) {
            _signature = signature;
            _lastProgress = DateTime.UtcNow;
        }

        // Nothing has changed for a long while: stuck. A quarter sooner than the setting says (Niduus, 2026-11-13: "engage
        // with its restart timer 25% sooner if idle for too long"), and it does something about it: a fresh look at the way
        // (another way when there is one), and the second time running, back to where the area starts.
        var idle = TimeSpan.FromMinutes(UltimusWire.StuckMinutes * UltimusDirectorActor.SOONER);
        if (DateTime.UtcNow - _lastProgress > idle) {
            _lastProgress = DateTime.UtcNow;
            var minutes = Math.Round(idle.TotalMinutes, 1);
            var kind = _target?.Problem.Length > 0 || _target?.Kind is UltimusPlanner.TALK or UltimusPlanner.USE ? "quest" : "team";
            Issue(kind, _target?.Quest ?? _currentQuest, $"No progress for {minutes} minutes. The team is {_doing}.",
                key: "stuck|" + (_target?.Key ?? _doing));
            RestartIdle(minutes);
        }
        else if (_idleRestarts > 0 && DateTime.UtcNow - _lastProgress < TimeSpan.FromSeconds(30) && _lastProgress > _idleRestartAt) {
            _idleRestarts = 0;
        }
    }

    private readonly HashSet<string> _noWaySaid = [];
    // At a door that offers nothing to click (Niduus, 2026-11-13: Ultimus stood in the Pyramid's doorway in The Oasis,
    // "I need him more responsive and more active to correct mistakes"): a doorway is walked through, as a player walks in;
    // still nothing, the door is pressed straight away, as a player presses X; and it is written up when nothing works.
    private (string Key, DateTime Since, float FromX, float FromY)? _doorWait;

    private void DoorWithoutOptions(UltimusMember.Status leader, UltimusPlanner.Target target) {
        if (_doorWait is not { } wait || wait.Key != target.Key) {
            _doorWait = wait = (target.Key, DateTime.UtcNow, leader.X, leader.Y);
        }

        var waited = DateTime.UtcNow - wait.Since;
        if (waited < TimeSpan.FromSeconds(2)) {
            WalkTo(0, target with { X = target.X - 20 });

            return;
        }

        // Through the doorway: on past the door's middle, the way it came.
        var (dx, dy) = (target.X - wait.FromX, target.Y - wait.FromY);
        var length = MathF.Max(1, MathF.Sqrt((dx * dx) + (dy * dy)));
        var past = (X: target.X + (dx / length * 220), Y: target.Y + (dy / length * 220));
        if (CanClick(target.Key + "|through")) {
            Clicked(target.Key + "|through");
            Note("Ultimus", $"{leader.Name}: the door {target.What} offers nothing to click, so it walks on through the doorway.");
            var points = OverlayQuestPath.Walk(leader.CharId, leader.Zone, leader.X, leader.Y, past.X, past.Y, body: true);
            points.Add(past);
            _actors[0].Tell(new UltimusMember.WalkTo(points, "walks through the doorway " + target.What));
            _sent[0] = (target, DateTime.UtcNow, leader.X, leader.Y);

            return;
        }

        if (waited > TimeSpan.FromSeconds(8) && CanClick(target.Key + "|press")) {
            Clicked(target.Key + "|press");
            Note("Ultimus", $"{leader.Name} presses the door {target.What} straight away (it offered nothing to click).", UltimusLink.ABNORMAL);
            _actors[0].Tell(new UltimusMember.Interact(target.GlobalId, "Interact", 0, "opens the door " + target.What));
            _leaderLastTarget = target;
        }

        if (waited > TimeSpan.FromSeconds(45)) {
            Issue("quest", target.Quest, $"{target.What}: {leader.Name} stands at the door, walked through the doorway and pressed it, and it does not take it anywhere.",
                key: target.Key + "|door-dead", broken: false, level: UltimusLink.ABNORMAL);
        }
    }

    // At a walk-through exit that does not take it on: standing inside it already (arrived there, so the game never saw
    // it walk in), it steps back out and walks in again, as a player does (Niduus's log, 2026-11-13: stuck at The Oasis's
    // start, where it lands, on the way to Altar of Kings). Walking in at the one point never took it on in v0.10.851, so
    // now it wanders the exit's whole area as a player feeling for the trigger does (Niduus, 2026-11-14: "Built in a zone
    // transfer wandering feature around the areas like this for the zone transfer. Make it smarter"): each try walks out
    // and in again through another part of the exit (the middle, then rings further out, from all sides). When every part
    // was walked and the game still never took it on, Ahziael takes it through to the area the exit leads to, written up
    // pink (ABNORMAL), in his thoughts and in the mistakes book.
    private (string Key, DateTime Since)? _exitWait;
    private string _exitKey = "";
    private int _exitTries;
    private DateTime _exitFirst = DateTime.MinValue;

    // Where each try goes in, round the exit's middle: (how far out, which way round), from all sides.
    private static readonly (float Out, float Angle)[] s_exitSweep = [
        (0, 0), (60, 0), (60, 180), (60, 90), (60, 270), (120, 45), (120, 225), (120, 135), (120, 315), (200, 0), (200, 180),
        (200, 90), (200, 270),
    ];

    // Exits whose trigger never took him on after the whole sweep, kept in a file (approved 2026-11-14, "remember known
    // failures across restarts": the Oasis exit to Altar of Kings refused him 44 times over several runs). The next time
    // he tries it once the normal way and is then taken through at once, instead of the whole sweep again.
    private const string STUBBORN_EXITS_FILE = "stubborn-exits.txt";
    private static HashSet<string> s_stubbornExits;

    private static string ExitId(string zone, string to) => $"{zone}>{to}";

    private static HashSet<string> StubbornExits() {
        if (s_stubbornExits is not null) {
            return s_stubbornExits;
        }

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try {
            var path = Path.Combine(UltimusWire.Folder, STUBBORN_EXITS_FILE);
            if (File.Exists(path)) {
                foreach (var line in File.ReadAllLines(path)) {
                    if (line.Split(' ')[0] is { Length: > 0 } id) {
                        set.Add(id);
                    }
                }
            }
        }
        catch (Exception e) {
            Logger.Debug("Ultimus: could not read {0}: {1}", Logger.Args(STUBBORN_EXITS_FILE, e.Message));
        }

        return s_stubbornExits = set;
    }

    private static void RememberStubbornExit(string zone, string to, float x, float y) {
        if (string.IsNullOrEmpty(zone) || string.IsNullOrEmpty(to) || !StubbornExits().Add(ExitId(zone, to))) {
            return;
        }

        try {
            File.AppendAllLines(Path.Combine(UltimusWire.Folder, STUBBORN_EXITS_FILE), [$"{ExitId(zone, to)} at ({x:0}, {y:0}) {DateTime.Now:yyyy-MM-dd HH:mm}"]);
        }
        catch (Exception e) {
            Logger.Debug("Ultimus: could not write {0}: {1}", Logger.Args(STUBBORN_EXITS_FILE, e.Message));
        }
    }

    // Tries before Ahziael takes it through, and the longest it works an exit on its own.
    private const int EXIT_TRIES = 8;
    private static readonly TimeSpan EXIT_MOST = TimeSpan.FromSeconds(100);

    /// <summary>Whether it is working a walk-through exit now (standing still there is part of it, not being stuck).</summary>
    private bool WorkingExit => _exitWait is { } wait && DateTime.UtcNow - wait.Since < TimeSpan.FromSeconds(30);

    private void IntoExit(UltimusMember.Status leader, UltimusPlanner.Target target) {
        if (_exitKey != target.Key) {
            _exitKey = target.Key;
            _exitTries = 0;
            _exitFirst = DateTime.UtcNow;
        }

        if (UltimusPlanner.Far(leader.X, leader.Y, target.X, target.Y) > 260) {
            _exitWait = null;
            WalkTo(0, target with { X = target.X + 5, Y = target.Y + 5 });

            return;
        }

        if (_exitWait is not { } wait || wait.Key != target.Key) {
            _exitWait = wait = (target.Key, DateTime.UtcNow);
        }

        if (_exitTries == 0 && DateTime.UtcNow - wait.Since < TimeSpan.FromSeconds(6)) {
            WalkTo(0, target with { X = target.X + 5, Y = target.Y + 5 });

            return;
        }

        if (!CanClick(target.Key + "|sweep")) {
            return;
        }

        Clicked(target.Key + "|sweep");
        _exitWait = (target.Key, DateTime.UtcNow);
        _sent[0] = (target, DateTime.UtcNow, leader.X, leader.Y);
        _leaderLastTarget = target;

        var known = !string.IsNullOrEmpty(target.To) && StubbornExits().Contains(ExitId(leader.Zone, target.To));
        if (_exitTries >= EXIT_TRIES || DateTime.UtcNow - _exitFirst > EXIT_MOST || known) {
            if (known) {
                Think($"This exit {target.What} never takes me on (I wrote it down before), so I don't walk the whole sweep again.", "known-exit|" + target.Key);
            }

            ThroughExit(leader, target);

            return;
        }

        // The part of the exit this try walks in through, entered from the far side of the middle.
        var (far, angle) = s_exitSweep[_exitTries % s_exitSweep.Length];
        var turn = angle * MathF.PI / 180f;
        var spot = (X: target.X + (MathF.Cos(turn) * far), Y: target.Y + (MathF.Sin(turn) * far));
        var from = turn + MathF.PI + ((Random.Shared.NextSingle() - 0.5f) * 0.8f);
        var outside = (X: spot.X + (MathF.Cos(from) * 300), Y: spot.Y + (MathF.Sin(from) * 300));
        _exitTries++;

        var points = OverlayQuestPath.Walk(leader.CharId, leader.Zone, leader.X, leader.Y, outside.X, outside.Y, body: true);
        if (points.Count == 0) {
            points.Add(outside);
        }

        // Through the spot and a little past it, so the game sees it cross the trigger, not stop on its edge.
        points.Add(spot);
        points.Add((spot.X - (MathF.Cos(from) * 60), spot.Y - (MathF.Sin(from) * 60)));
        Note("Ultimus", $"{leader.Name} is at the exit {target.What} and it does not take it on: try {_exitTries} of {EXIT_TRIES}, " +
                        $"walks out and in again through another part of it ({far:0} from its middle).", UltimusLink.NOTABLE);
        if (_exitTries == 1) {
            Think($"I stand at the exit {target.What}, but the game does not take me on. I walk out and in again through every part " +
                  $"of it, the middle first and then further out, from all sides; after {EXIT_TRIES} tries Ahziael takes me through.",
                "exit|" + target.Key);
        }

        _actors[0].Tell(new UltimusMember.WalkTo(points, "walks out of the exit and in again through another part of it"));
    }

    // Every part of the exit was walked: Ahziael takes it through to the area the exit leads to.
    private void ThroughExit(UltimusMember.Status leader, UltimusPlanner.Target target) {
        var tries = _exitTries;
        var seconds = (DateTime.UtcNow - _exitFirst).TotalSeconds;
        _exitTries = 0;
        _exitFirst = DateTime.UtcNow;
        _exitWait = null;
        if (string.IsNullOrEmpty(target.To)) {
            return;
        }

        var where = $"{UltimusText.Zone(leader.Zone)} at ({target.X:0}, {target.Y:0})";
        Note("Ultimus", $"{leader.Name} walked through every part of the exit {target.What} in {where} ({tries} tries, {seconds:0} seconds) " +
                        $"and the game never took it on: Ahziael takes it through to {UltimusText.Zone(target.To)}.", UltimusLink.ABNORMAL);
        var thought = $"I walked through every part of the exit {target.What} ({tries} tries) and the game never took me on. " +
                      $"Ahziael takes me through to {UltimusText.Zone(target.To)}; this exit is written in the mistakes book so it can be looked at.";
        Think(thought, "through|" + target.Key);
        BotMistakes.Record("Ultimus", BotMistakes.STUCK, leader.Zone, target.X, target.Y, target.Quest,
            $"The walk-through exit {target.What} never took it on, though it walked through every part of it.", thought,
            "Ahziael takes a bot through when the exit's trigger does not fire; the exit's trigger in the zone file needs a look.",
            "walked through every part of the exit");
        BotMistakes.Fixed(BotMistakes.STUCK, leader.Zone, target.X, target.Y, "Ahziael takes it through the exit");
        RememberStubbornExit(leader.Zone, target.To, target.X, target.Y);
        _actors[0].Tell(new UltimusMember.ThroughExit(target.To));
        _unstickUntil[0] = DateTime.UtcNow + UNSTICK_FOR;
        OverlayQuestPath.Forget(leader.CharId);
    }

    private int _idleRestarts;
    private DateTime _idleRestartAt = DateTime.MinValue;

    // No progress for a long while: the way is looked at fresh (the old walks forgotten) and the next fix for the spot is made.
    private void RestartIdle(double minutes) {
        if (_team.Count == 0) {
            return;
        }

        var leader = _team[0];
        _idleRestarts++;
        _idleRestartAt = DateTime.UtcNow;
        _sent.Clear();
        _clicks.Clear();
        _target = null;
        foreach (var member in _team) {
            OverlayQuestPath.Forget(member.CharId);
        }

        if (leader.Wizard is null || CombatDuelComponent.IsInBattle(leader.GlobalId)) {
            return;
        }

        // The same flags and fixes as standing still (UltimusFixes): a spot where no progress was made gets the next fix not
        // tried there yet, never the same one again (Niduus's log, 2026-11-13: "goes back to where The Oasis starts" every
        // six minutes, for hours).
        Unstick(0, $"made no progress for {minutes} minutes");
    }

    private void StartRun(string questName) {
        if (_runs.ContainsKey(questName) || QuestTemplateCollection.GetQuestByName(questName) is not { } quest) {
            return;
        }

        var kind = QuestStory.KindOf(quest);
        if (kind == QuestStory.SIDE) {
            return;
        }

        var run = new QuestRun { Quest = questName, Title = UltimusText.Title(questName), World = QuestStory.WorldOf(quest) };
        _runs[questName] = run;
        var steps = quest.m_goals?.Count(g => g is not null) ?? 1;
        var fights = quest.m_goals?.Count(g => g is Imcodec.ObjectProperty.TypeCache.BountyGoalTemplate) ?? 0;
        Note("Ultimus", $"STARTED {run.Title} ({kind}, {steps} steps).");
        var self = Self;
        _ = System.Threading.Tasks.Task.Run(() => {
            var answer = UltimusLink.Ask(new UltimusRequest { Op = "quest", Event = "start", Quest = questName, Title = run.Title, World = run.World,
                Steps = steps, Fights = fights }, quiet: true);
            if (answer?.Ok == true) {
                run.Expected = answer.Expected;
            }
        });
    }

    private void Issue(string kind, string quest, string text, string key = null, bool broken = true, string level = null) {
        // A way or exit target carries no quest of its own: the issue is the quest Ultimus is on, so the debug report names it (a loop in and out of an area named no quest and never ended, 2026-11-13).
        if (kind == "quest" && string.IsNullOrEmpty(quest)) {
            quest = _currentQuest;
        }

        key ??= kind + "|" + quest + "|" + text;
        if (_reported.ContainsKey(key)) {
            return;
        }

        _reported[key] = DateTime.UtcNow;
        Count("issues raised");
        var template = string.IsNullOrEmpty(quest) ? null : QuestTemplateCollection.GetQuestByName(quest);

        // A broken step is flagged, never skipped (Niduus, 2026-11-14: "He is not allowed to use commands to JUMP to the next
        // main quest. HIS ENTIRE PURPOSE IS TO QUEST AS IF HE IS A HUMAN. His only exceptions ... to unstuck"). He keeps
        // trying it as a player would, with unstuck moves only, until Niduus fixes it; the debug report says what to fix.
        level ??= kind switch { "slow" => UltimusLink.MINOR, "team" => UltimusLink.CRITICAL, _ => UltimusLink.NOTABLE };
        var flagged = kind == "quest" && broken && !string.IsNullOrEmpty(quest);
        Note("Ultimus", $"ISSUE{(string.IsNullOrEmpty(quest) ? "" : $" in {UltimusText.Title(quest)}")}: {text}", flagged ? UltimusLink.CRITICAL : level);
        if (flagged) {
            text += " The step looks broken: he keeps trying it as a player would and skips nothing, so it needs a fix.";
            Note("Ultimus", $"BROKEN STEP in {UltimusText.Title(quest)}: flagged for Niduus. I keep trying it the way a player would; I never skip it.", UltimusLink.CRITICAL);
            UltimusJournal.Write(UltimusJournal.STUCK, $"Got stuck on a step of {UltimusText.Title(quest)} that would not work: {UltimusText.Short(text, 90)}",
                _team.Count > 0 ? WorldOf(_team[0].Zone) : "", _team.Count > 0 ? UltimusText.Zone(_team[0].Zone) : "", UltimusText.Title(quest), -1);
            Count("broken steps flagged");
            UltimusLink.Send(new UltimusRequest { Op = "quest", Event = "broken", Quest = quest, Title = UltimusText.Title(quest), Text = "a broken step, flagged; not skipped" });
        }

        // What most likely breaks it (Niduus, 2026-11-13: "detail character and quest detection to determine when/if a quest
        // can/cannot be completed ... to assume what is breaking it has its accuracy go up way higher"): checked against the
        // quest data, the wizard and the live server, and put first in the issue and its debug report.
        // Kept for good at this step, even when it happened once: read first the next time (UltimusDirector.Push.cs).
        if (kind == "quest") {
            RememberStep(quest, _target?.Quest == quest ? _target.Goal : "", text);
        }

        // The report is built in a guard: a fault in it never stops what he was doing (an unstick round broke off at the
        // "flag it" step on a tester's log, 2026-11-13, and the next round started with an empty list of tries).
        try {
            Report(kind, quest, text, template);
        }
        catch (Exception e) {
            Logger.Warning("Ultimus: the issue report could not be finished ({0}): {1}", Logger.Args(text, e));
        }
    }

    private void Report(string kind, string quest, string text, Imcodec.ObjectProperty.TypeCache.QuestTemplate template) {
        var diagnosis = Diagnose(quest);
        var facts = DebugFacts(quest, template) ?? [];
        if (diagnosis is not null) {
            text += " " + diagnosis.Summary;
            facts.Insert(0, $"Likely cause ({diagnosis.Confidence} confidence, {diagnosis.Category}): {diagnosis.LikelyCause}");
            facts.Insert(1, $"Can the step be done: {diagnosis.Completable}");
            facts.AddRange(diagnosis.Checks.Select(c => "Check " + c));
            Note("Ultimus", $"DIAGNOSIS: {diagnosis.LikelyCause} ({diagnosis.Confidence} confidence)",
                diagnosis.Confidence == "High" ? UltimusLink.ABNORMAL : UltimusLink.NOTABLE);
        }

        UltimusLink.Issue(kind, quest ?? "", string.IsNullOrEmpty(quest) ? "" : UltimusText.Title(quest),
            template is null ? "" : QuestStory.WorldOf(template), text, Expected(kind, _target), Seen(quest),
            _target?.Goal ?? "", facts);
    }

    /// <summary>Runs the shared quest diagnosis (BotShared.BotQuestDiagnosis) for Ultimus on <paramref name="quest"/>; null when it can't.</summary>
    private BotShared.BotQuestDiagnosis.Result Diagnose(string quest) {
        try {
            var me = _team.Count > 0 ? _team[0] : null;
            if (me is null) {
                return null;
            }

            var goal = _target is not null && (string.Equals(_target.Quest, quest, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(_target.Quest))
                ? _target.Goal : null;

            return BotShared.BotQuestDiagnosis.Run(me.Wizard, me.Zone, me.ZoneActor, me.X, me.Y, quest, goal, me.Options, me.LastServerText,
                me.Wizard is null ? 0 : me.GlobalId);
        }
        catch (Exception e) {
            Logger.Debug("Ultimus: the diagnosis could not run: {0}", Logger.Args(e.Message));

            return null;
        }
    }

    /// <summary>
    /// The facts a quest is debugged from, one "Name: value" each (Niduus, 2026-11-12: "incorporate you into him so you
    /// can also run quest debugs from that Quester"): the quest's and step's codes and data file, what the step asks, where
    /// the quest arrow points, what Ultimus was going for, and what the server last said to it.
    /// </summary>
    private List<string> DebugFacts(string quest, Imcodec.ObjectProperty.TypeCache.QuestTemplate template) {
        var facts = new List<string>();
        try {
            var me = _team.Count > 0 ? _team[0] : null;
            var wizard = me?.Wizard;
            if (template is not null) {
                facts.Add($"Quest code: {quest}");
                facts.Add($"Quest kind: {QuestStory.KindOf(template)} (world {QuestStory.WorldOf(template)})");
                facts.Add($"Quest data file: data/spiraldb/QuestTemplates/questtemplates_{quest}.json");
                facts.Add($"Quest giver area: {QuestGuide.GiverZone(template)}");
                var held = wizard?.QuestBehavior?.CurrentQuestInstances?.FirstOrDefault(q => q is not null
                    && string.Equals(q.QuestName, quest, StringComparison.OrdinalIgnoreCase));
                foreach (var goal in (template.m_goals ?? []).Where(g => g is not null)) {
                    string name = goal.m_goalName;
                    var state = held is null ? (wizard?.HasCompletedQuest(quest) == true ? "quest done" : "quest not held")
                        : held.IsGoalCompleted(name) ? "done" : held.IsGoalActive(name) ? "ACTIVE" : "not open yet";
                    var spots = Imlight.CoreLib.Game.Services.QuestArrowGoals.GetSpots(template, goal).Take(3).Select(s => $"{s.Zone} ({s.Spot.X:0}, {s.Spot.Y:0})").ToList();
                    facts.Add($"Step {name}: {goal.GetType().Name.Replace("Template", "")}, \"{OverlayQuestTracker.StepTitle(goal)}\", {state}, " +
                              $"area {(string.IsNullOrEmpty((string) goal.m_destinationZone) ? "(none named)" : (string) goal.m_destinationZone)}, " +
                              $"arrow {(spots.Count == 0 ? "(no spot)" : string.Join("; ", spots))}{GoalDetail(goal)}");
                }
            }

            if (_target is { } target) {
                facts.Add($"Going for: {target.Kind} \"{target.What}\" at ({target.X:0}, {target.Y:0}), template {target.Template}, object {target.GlobalId}" +
                          $"{(string.IsNullOrEmpty(target.To) ? "" : $", to {target.To}")}, key {target.Key}" +
                          $"{(target.Template == 0 ? "" : $" ({TemplateName(target.Template)})")}");
                if (target.Problem.Length > 0) {
                    facts.Add($"Planner says: {target.Problem}");
                }

                facts.Add($"On this step for: {Minutes((DateTime.UtcNow - _targetSince).TotalSeconds)}");
            }

            if (me is not null) {
                facts.Add($"Ultimus: level {wizard?.MagicSchoolBehavior?.Level ?? 0}, {me.Zone} at ({me.X:0}, {me.Y:0}, {me.Z:0}), {(wizard?.ZoneIsInstance == true ? "in an instance" : "in the open world")}, state {me.State}");
                facts.Add($"NPCs and objects it can click here: {(me.Options.IsEmpty ? "none" : string.Join("; ", me.Options.Take(6).Select(o => $"{o.Key}: {string.Join(", ", o.Value.Select(v => v.Service))}")))}");
                if (me.LastServerText.Length > 0) {
                    facts.Add($"Server last said: {UltimusText.Short(me.LastServerText, 200)}");
                }

                if (me.LastDialog > DateTime.MinValue) {
                    facts.Add($"Last dialog: {(DateTime.UtcNow - me.LastDialog).TotalSeconds:0} seconds ago");
                }

                facts.Add($"Clicks on this step: {string.Join(", ", _clicks.Where(c => _target is not null && c.Key.StartsWith(_target.Key, StringComparison.Ordinal)).Select(c => $"{c.Key} x{c.Value.Times}").DefaultIfEmpty("none"))}");
            }
        }
        catch (Exception e) {
            facts.Add($"(could not read everything: {e.Message})");
        }

        return facts;

        static string GoalDetail(Imcodec.ObjectProperty.TypeCache.GoalTemplate goal) => goal switch {
            Imcodec.ObjectProperty.TypeCache.PersonaGoalTemplate persona => $", talk to {(string) persona.m_personaName}",
            Imcodec.ObjectProperty.TypeCache.BountyGoalTemplate bounty => $", defeat {string.Join("/", (bounty.m_npcAdjectives ?? []).Select(a => (string) a))}",
            _ => "",
        };
    }

    private static string TemplateName(ulong template) {
        try {
            return Imlight.CoreLib.Shared.Resources.CoreObjectFactory.GetCoreTemplate((uint) template) is Imcodec.ObjectProperty.TypeCache.WizGameObjectTemplate found ? (string) found.m_objectName ?? "" : "";
        }
        catch (Exception) {
            return "";
        }
    }

    /// <summary>What a player would do and see at the step Ultimus is on, in plain words, for an issue.</summary>
    private static string Expected(string kind, UltimusPlanner.Target target) {
        if (kind == "slow") {
            return "A player finishes this quest in about the time it took before (the expected time above).";
        }

        if (target is null) {
            return kind == "team"
                ? "A player always has a story quest to follow: the quest arrow points to the next step, and a quest giver offers the next quest."
                : "A player can do every step of the quest from what is in the game.";
        }

        var step = $"\"{target.What}\"";
        return target.Kind switch {
            UltimusPlanner.TALK => $"A player walks to the NPC the quest arrow points at for {step}, clicks it, reads the dialog, and the step is done.",
            UltimusPlanner.OFFER => $"A player walks to the quest giver, clicks it and is offered the quest {UltimusText.Title(target.Quest)}.",
            UltimusPlanner.FIGHT => $"A player walks into the creature for {step}; a battle starts, and winning it counts for the step. " +
                                      "A player alone at the level the quest asks for wins it within a few tries.",
            UltimusPlanner.USE => $"A player walks to the quest arrow's spot for {step}, clicks the object there, and the step is done.",
            UltimusPlanner.SPOT => $"A player walks to the place the quest arrow points at for {step}, and the step is done on arrival.",
            UltimusPlanner.EXIT or UltimusPlanner.DOOR or UltimusPlanner.SPIRAL =>
                $"A player can walk from where they stand to {UltimusText.Zone(target.To)} through this way for {step}.",
            UltimusPlanner.TRAIN => "A player walks to the trainer, opens the training window and learns the spell with training points.",
            UltimusPlanner.SHOP => "A player walks to the shop, opens it and buys the item with gold.",
            _ => $"A player can do {step} from what is in the game.",
        };
    }

    /// <summary>Where each wizard was and what it held for the quest when the issue came up.</summary>
    private List<string> Seen(string quest) {
        var lines = new List<string>();
        foreach (var member in _team) {
            var held = string.IsNullOrEmpty(quest) ? null : member.Wizard?.QuestBehavior?.CurrentQuestInstances?
                .FirstOrDefault(q => q is not null && string.Equals(q.QuestName, quest, StringComparison.OrdinalIgnoreCase));
            var goals = held?.GoalProgress?.Where(g => g is not null && held.IsGoalActive(g.GoalName)).Select(g => (string) g.GoalName).ToList() ?? [];
            lines.Add($"{member.Role} {member.Name}: level {member.Wizard?.MagicSchoolBehavior?.Level ?? 0}, {member.State}, " +
                      $"{UltimusText.Zone(member.Zone)} at ({member.X:0}, {member.Y:0}){(CombatDuelComponent.IsInBattle(member.GlobalId) ? ", in a battle" : "")}" +
                      (string.IsNullOrEmpty(quest) ? "" : held is null ? ", does not hold the quest" : $", on step {(goals.Count == 0 ? "(none active)" : string.Join(", ", goals))}"));
        }

        return lines;
    }

    private void WriteStatus(string state, string why) {
        try {
            var members = new JsonArray();
            foreach (var member in _team) {
                var wizard = member.Wizard;
                var stats = wizard?.GameStats;
                members.Add(new JsonObject {
                    ["role"] = member.Role,
                    ["name"] = member.Name,
                    ["state"] = member.State,
                    ["level"] = wizard?.MagicSchoolBehavior?.Level ?? 0,
                    ["zone"] = UltimusText.Zone(member.Zone),
                    ["health"] = stats is null ? "" : $"{stats.m_currentHitpoints}/{stats.m_baseHitpoints + stats.m_bonusHitpoints}",
                    ["gold"] = stats?.m_currentGold ?? 0,
                    ["training"] = wizard?.MagicSchoolBehavior?.TrainingPoints ?? 0,
                    ["battle"] = CombatDuelComponent.IsInBattle(member.GlobalId),
                    ["quests"] = wizard?.QuestBehavior?.CurrentQuestInstances?.Count ?? 0,
                    // What Ahziael's Ultimus page shows under each wizard: its quests, gear, deck and trained spells.
                    ["questList"] = new JsonArray([.. (wizard?.QuestBehavior?.CurrentQuestInstances?.ToList() ?? [])
                        .Where(q => q is not null).Select(q => (JsonNode) UltimusText.Title(q.QuestName))]),
                    ["gear"] = new JsonArray([.. BotShared.BotGear.Describe(wizard).Select(g => (JsonNode) g)]),
                    ["deck"] = wizard?.SpellbookBehavior?.SpellList is { } deck
                        ? BotShared.BotCards.Describe(deck.Where(d => d is not null).Select(d => ((uint) d.m_templateID, (int) d.m_quantity)))
                        : "",
                    ["deckFor"] = _deckFor.GetValueOrDefault(member.Role) ?? "",
                    // The quest it plays now and the step it is on, for Ahziael's sheet.
                    ["questNow"] = _currentQuest is null ? "" : UltimusText.Title(_currentQuest),
                    ["stepNow"] = _target is null ? "" : Describe(_target),
                    ["spells"] = new JsonArray([.. (wizard?.SpellbookBehavior?.LearnedSpellTemplateIds?.ToList() ?? [])
                        .Select(id => UltimusText.SpellName(id)).Where(n => !string.IsNullOrEmpty(n)).Distinct().Order().Select(n => (JsonNode) n)]),
                });
                // Stats, deck cards and school, the same sheet as the Quest Team's (Niduus, 2026-11-13: "view their gear,
                // stats and their current deck and quest").
                BotShared.BotSheet.Fill((JsonObject) members[^1], wizard, member.Role);
            }

            var run = _currentQuest is null ? null : _runs.GetValueOrDefault(_currentQuest);
            var status = new JsonObject {
                ["state"] = state,
                ["why"] = why ?? "",
                ["doing"] = _team.Count > 0 && _team[0].Wizard is not null && CombatDuelComponent.IsInBattle(_team[0].GlobalId) ? "in a battle" : _doing,
                ["quest"] = _currentQuest is null ? "" : UltimusText.Title(_currentQuest),
                ["questCode"] = _currentQuest ?? "",
                ["questFor"] = run is null ? 0 : (int) (DateTime.UtcNow - run.Started).TotalSeconds,
                ["questExpected"] = run is null ? 0 : (int) run.Expected,
                ["lastProgress"] = _lastProgress.ToString("o"),
                ["at"] = DateTime.UtcNow.ToString("o"),
                ["today"] = Summary(),
                ["members"] = members,
            };
            UltimusLink.Send(new UltimusRequest { Op = "status", Json = status.ToJsonString() });
            // The overlay's Ultimus Spectator draws this quest's way on Ultimus's own minimap (read only).
            // Always what is true this moment (approved 2026-11-14, "an always-accurate Spectator status line"): the quest's
            // real step from his quest book, and what he is doing now (a battle, a heal, a Recall), not the last plan.
            var leaderNow = _team.Count > 0 ? _team[0] : null;
            var stepNow = leaderNow?.Wizard is null ? "" : SocialSelf(leaderNow).Step ?? "";
            var doingNow = leaderNow?.Wizard is not null && CombatDuelComponent.IsInBattle(leaderNow.GlobalId) ? "in a battle" : _doing;
            UltimusSpectate.NoteStatus(_currentQuest ?? _target?.Quest, stepNow.Length > 0 ? stepNow : _target is null ? "" : Describe(_target), doingNow,
                _leaderZone, _target?.X, _target?.Y);
        }
        catch (Exception e) {
            Logger.Debug("Ultimus: could not write the status: {0}", Logger.Args(e.Message));
        }
    }

    // Everything Ultimus does is said as "Ultimus: ..." (a line that starts with its name again drops it).
    private static void Note(string who, string text, string level = null)
        => UltimusLink.Note(UltimusRoster.NAME, text.StartsWith(UltimusRoster.NAME + " ", StringComparison.Ordinal) ? text[(UltimusRoster.NAME.Length + 1)..] : text, level);

    // ---------------------------------------------------------------------
    // Oddities it notices (pink in Ahziael)
    // ---------------------------------------------------------------------

    private int _lastLevel = -1;
    private DateTime _fightWalkSince = DateTime.MaxValue;
    private string _fightWalkKey = "";
    private DateTime _nextOddLook = DateTime.MinValue;
    private readonly HashSet<string> _oddTold = [];

    // Niduus, 2026-11-12: "Ultimus and QT are to report in PINK abnormal behavior they've notice". Things that are not a
    // broken step but not what a player would expect either; each told once.
    private void NoticeOddities() {
        if (DateTime.UtcNow < _nextOddLook || _team.Count == 0) {
            return;
        }

        _nextOddLook = DateTime.UtcNow.AddSeconds(2);
        var me = _team[0];
        var level = me.Wizard?.MagicSchoolBehavior?.Level ?? 0;

        // More than one level at once: one battle or quest gives a player one level at most.
        if (_lastLevel > 0 && level > _lastLevel + 1) {
            Odd($"level|{level}", $"went from level {_lastLevel} to {level} at once; one battle or quest gives a player one level at most.");
        }

        _lastLevel = level;

        // Standing on the creature it walked into for a while and no battle started.
        if (_target is { Kind: UltimusPlanner.FIGHT } fight && !CombatDuelComponent.IsInBattle(me.GlobalId)
            && UltimusPlanner.Far(me.X, me.Y, fight.X, fight.Y) < 60) {
            if (_fightWalkKey != fight.Key) {
                _fightWalkKey = fight.Key;
                _fightWalkSince = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - _fightWalkSince > TimeSpan.FromSeconds(20)) {
                Odd($"nobattle|{fight.Key}|{fight.GlobalId}", $"stood on the creature for \"{fight.What}\" ({TemplateName(fight.Template)}) for 20 seconds and no battle started; a player walking into it starts one.");
            }
        }
        else {
            _fightWalkKey = "";
        }

        // Health or mana over its most. Not just after a swap: taking off a piece with health lowers the most before the
        // game trims the health (v0.10.852: flagged pink right after he put on the Adept ring).
        if (DateTime.UtcNow - _gearChangedAt > TimeSpan.FromMinutes(1) && me.Wizard?.GameStats is { } stats && stats.m_currentHitpoints > (stats.m_baseHitpoints + stats.m_bonusHitpoints) * 1.05 + 1) {
            Odd($"hp|{level}", $"has {stats.m_currentHitpoints} health, more than its most ({stats.m_baseHitpoints + stats.m_bonusHitpoints}).");
        }
    }

    private void Odd(string key, string text) {
        if (_oddTold.Add(key)) {
            UltimusLink.Abnormal(text);
            Count("oddities noticed");
        }
    }

    // The day's tally, for Ahziael's Ultimus page and a summary at the end of each day (Niduus, 2026-11-12: "daily
    // summary").
    private readonly Dictionary<string, long> _today = new(StringComparer.Ordinal);
    private DateTime _todayDate = DateTime.Now.Date;

    private void Count(string what, long amount = 1) {
        _today[what] = _today.GetValueOrDefault(what) + amount;
        SessionCount(what, amount);
    }

    private string Summary() {
        var levels = string.Join(", ", _team.Select(m => $"{m.Role} {m.Wizard?.MagicSchoolBehavior?.Level ?? 0}"));
        var counts = _today.Count == 0 ? "nothing yet" : string.Join(", ", _today.Select(c => $"{c.Value:N0} {c.Key}"));

        return $"{counts}. Levels: {levels}.";
    }

    private void RollDay() {
        if (DateTime.Now.Date == _todayDate) {
            return;
        }

        var day = _todayDate.ToString("dddd d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        var summary = Summary();
        Note("Ultimus", $"DAILY SUMMARY for {day}: {summary}");
        UltimusLink.Send(new UltimusRequest { Op = "quest", Event = "daily summary", Quest = "", Title = $"Day {day}", Text = summary });
        _today.Clear();
        _todayDate = DateTime.Now.Date;
    }

    private static string Describe(UltimusPlanner.Target target) => target.Kind switch {
        UltimusPlanner.TALK => $"{target.What} ({UltimusText.Title(target.Quest)})",
        UltimusPlanner.OFFER => target.What,
        UltimusPlanner.FIGHT => $"{target.What} ({UltimusText.Title(target.Quest)})",
        UltimusPlanner.SPOT or UltimusPlanner.USE => $"{target.What} ({UltimusText.Title(target.Quest)})",
        _ => target.What,
    };

    private static float Far(UltimusMember.Status a, UltimusMember.Status b) => UltimusPlanner.Far(a.X, a.Y, b.X, b.Y);

    private static string Minutes(double seconds) => seconds < 90 ? $"{seconds:0} s" : $"{seconds / 60:0} min";

}
