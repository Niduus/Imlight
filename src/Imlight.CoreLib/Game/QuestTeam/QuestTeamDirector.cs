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

namespace Imlight.CoreLib.Game.QuestTeam;

/// <summary>
/// The Quest Team (Niduus, 2026-11-12): four wizards, Ice, Life, Death and Storm, who play the World and Story quests
/// together from level 1 and report which ones work. Always in that order: Ice leads and the others follow one behind the
/// other, they go into battles in that order, and they never split up unless an area takes one at a time. They walk
/// everywhere, talk to NPCs, fight, and train at their school trainers like players. What they do goes to the Quest
/// Team's log; a quest that can't be done, a team that is stuck, a fight lost again and again, or a quest taking much
/// longer than expected is an issue Ahziael announces, with the log of what happened.
/// </summary>
internal static class QuestTeamDirector {

    private static readonly System.Threading.Lock s_lock = new();
    private static IActorRef s_actor;
    private static DateTime s_retryAt = DateTime.MinValue;

    /// <summary>The team plays (its mini server answers): started once.</summary>
    internal static void EnsureRunning() {
        lock (s_lock) {
            if (s_actor is not null || DateTime.UtcNow < s_retryAt || QuestTeamHost.System is not { } system || QuestTeamHost.Server is null) {
                return;
            }

            s_actor = system.ActorOf(Props.Create(() => new QuestTeamDirectorActor()), $"quest-team-{Guid.NewGuid():N}");
        }
    }

    /// <summary>The team logs out (the switch is off or its mini server stopped).</summary>
    internal static void Stop(string why) {
        lock (s_lock) {
            if (s_actor is null) {
                return;
            }

            s_actor.Tell(new QuestTeamDirectorActor.Halt(why));
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

    // ---- the team's wizards, for the battles (CombatDuelComponent.QuestTeam.cs) ----

    private static volatile Dictionary<ulong, string> s_roles = [];
    private static volatile List<QuestTeamMember.Status> s_members = [];

    internal static void Remember(IEnumerable<QuestTeamMember.Status> members) {
        s_members = [.. members];
        s_roles = s_members.ToDictionary(m => m.GlobalId, m => m.Role);
    }

    internal static void Forget() {
        s_roles = [];
        s_members = [];
    }

    private static long s_formationBrokeAt;
    private static volatile string s_formationWhy = "";

    /// <summary>A battle saw the team's formation broken and the team fled (CombatDuelComponent.QuestTeam.cs).</summary>
    internal static void FormationBroke(string why) {
        s_formationWhy = why ?? "";
        System.Threading.Interlocked.Exchange(ref s_formationBrokeAt, DateTime.UtcNow.Ticks);
    }

    /// <summary>When the formation last broke (UTC), and why.</summary>
    internal static (DateTime At, string Why) LastFormationBreak
        => (new DateTime(System.Threading.Interlocked.Read(ref s_formationBrokeAt), DateTimeKind.Utc), s_formationWhy);

    /// <summary>The team's four wizards as they are now (for .questteam tp), empty while the team is not playing.</summary>
    internal static IReadOnlyList<QuestTeamMember.Status> Members => s_members;

    /// <summary>The role (Ice, Life, Death, Storm) of a Quest Team wizard by its object id, or null for anyone else.</summary>
    internal static string RoleOf(ulong globalId) => s_roles.GetValueOrDefault(globalId);

}

internal sealed partial class QuestTeamDirectorActor : ReceiveActor, IWithTimers {

    internal sealed record Halt(string Why);

    private sealed record Tick;

    private sealed record Loaded(List<QuestTeamRoster.Member> Members);

    public ITimerScheduler Timers { get; set; }

    private const double TICK_SECONDS = 0.5;

    // How close a follower keeps to the wizard before it, and when it is too far for the leader to go on.
    private const float FOLLOW_GAP = 90;
    private const float FOLLOW_FAR = 650;

    private readonly List<QuestTeamMember.Status> _team = [];
    private readonly List<IActorRef> _actors = [];
    private bool _starting;

    // What each wizard was last sent to (so it is not sent again every tick).
    private readonly Dictionary<int, (QuestTeamPlanner.Target Target, DateTime At, float X, float Y)> _sent = [];

    // The leader's target and the clicks already made for it.
    private QuestTeamPlanner.Target _target;
    private DateTime _targetSince = DateTime.UtcNow;
    private readonly Dictionary<string, (int Times, DateTime Last)> _clicks = [];
    private DateTime _nextClick = DateTime.MinValue;

    // Where the leader left an area, for the ones behind (Niduus: "they will never break this formation").
    private readonly List<(string Zone, float X, float Y, QuestTeamPlanner.Target How, DateTime At)> _trail = [];
    private string _leaderZone = "";
    private float _leaderLastX, _leaderLastY;
    private QuestTeamPlanner.Target _leaderLastTarget;

    // Progress, for the log and the alerts.
    private string _signature = "";
    private DateTime _lastProgress = DateTime.UtcNow;
    private readonly Dictionary<string, QuestRun> _runs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenGoals = [];
    private readonly Dictionary<string, int> _losses = [];
    private readonly Dictionary<string, DateTime> _reported = [];
    private readonly bool[] _wasInBattle = new bool[4];
    private string _currentQuest;
    private DateTime _nextStatus = DateTime.MinValue;
    private DateTime _nextGroupTry = DateTime.MinValue;
    private DateTime _apartSince = DateTime.MinValue;
    private DateTime _nextTraining = DateTime.MinValue;
    private string _doing = "starting";

    private sealed class QuestRun {
        public string Quest = "";
        public string Title = "";
        public string World = "";
        public DateTime Started = DateTime.UtcNow;
        public double Expected;
        public bool SlowTold;
    }

    public QuestTeamDirectorActor() {
        Receive<Halt>(m => {
            Note("Team", $"logs out: {m.Why}.");
            foreach (var actor in _actors) {
                actor.Tell(new QuestTeamMember.Disconnect(m.Why));
            }

            QuestTeamDirector.Forget();
            WriteStatus("off", m.Why);
            Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(2), Self, PoisonPill.Instance, Self);
        });
        Receive<Loaded>(OnLoaded);
        Receive<Tick>(_ => {
            try {
                OnTick();
            }
            catch (Exception e) {
                Logger.Error("Quest Team: {0}", Logger.Args(e));
            }
        });
    }

    protected override void PreStart() {
        StartTeam(restart: false);
        base.PreStart();
    }

    private void StartTeam(bool restart) {
        if (_starting) {
            return;
        }

        _starting = true;
        _doing = restart ? "making four new wizards" : "logging in";
        var self = Self;
        _ = System.Threading.Tasks.Task.Run(() => {
            var members = restart ? QuestTeamRoster.Restart() : QuestTeamRoster.Load();
            self.Tell(new Loaded(members));
        });
    }

    private void OnLoaded(Loaded loaded) {
        _starting = false;
        if (loaded.Members is null) {
            Issue("team", null, "The Quest Team's wizards could not be made or loaded. The server log says why.");
            QuestTeamDirector.Failed(Self);

            return;
        }

        QuestTeamRoster.Remember(loaded.Members);
        _team.Clear();
        _actors.Clear();
        foreach (var member in loaded.Members) {
            var status = new QuestTeamMember.Status { Role = member.Role, CharId = member.CharId, AccountId = member.AccountId, Name = member.Name };
            _team.Add(status);
            var actor = Context.ActorOf(QuestTeamMember.Props(member, status), $"member-{member.Role.ToLowerInvariant()}");
            _actors.Add(actor);
        }

        QuestTeamDirector.Remember(_team);
        Note("Team", $"is {string.Join(", ", _team.Select(m => $"{m.Name} ({m.Role})"))}. They log in one after the other.");

        // They log in in their order, a moment apart.
        for (var i = 0; i < _actors.Count; i++) {
            Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(1 + (i * 3)), _actors[i], new QuestTeamMember.Connect(), Self);
        }

        _lastProgress = DateTime.UtcNow;
        Timers.StartPeriodicTimer("tick", new Tick(), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(TICK_SECONDS));
    }

    // ---------------------------------------------------------------------
    // Each half second
    // ---------------------------------------------------------------------

    private void OnTick() {
        if (_team.Count != 4) {
            return;
        }

        // Ahziael's Restart: everyone logs out, four new wizards, in again.
        var restartFile = Path.Combine(QuestTeamWire.Folder, QuestTeamWire.RESTART_FILE);
        if (File.Exists(restartFile)) {
            try {
                File.Delete(restartFile);
            }
            catch (IOException) {
                return;
            }

            Note("Team", "Restart asked in Ahziael: the team logs out and four new level 1 wizards are made.");
            foreach (var actor in _actors) {
                actor.Tell(PoisonPill.Instance);
            }

            Timers.Cancel("tick");
            QuestTeamDirector.Forget();
            _runs.Clear();
            _seenGoals.Clear();
            _losses.Clear();
            _trail.Clear();
            _target = null;
            _currentQuest = null;
            StartTeam(restart: true);

            return;
        }

        TrackProgress();
        if (DateTime.UtcNow >= _nextStatus) {
            _nextStatus = DateTime.UtcNow.AddSeconds(4);
            WriteStatus("on", null);
        }

        if (_team.Any(m => m.State != "playing" || m.Wizard is null)) {
            _doing = "waiting for everyone to be in the game";

            return;
        }

        KeepGrouped();
        UsePotions();

        // The formation broke in a battle and they fled: they gather again before anything else.
        if (Regroup()) {
            return;
        }

        // A battle: the ones not in it join, in the team's order.
        if (HandleBattle()) {
            return;
        }

        // Anyone reading a dialog: the team waits for them.
        if (_team.Any(m => m.Talking || DateTime.UtcNow - m.LastDialog < TimeSpan.FromSeconds(1))) {
            _doing = "reading a dialog";

            return;
        }

        // Better gear on, and decks that miss new spells redone (QuestTeamDirector.Gear.cs).
        KeepGearAndDecks();

        // Members in another area than the leader catch up the way the leader went.
        var leader = _team[0];
        NoteLeaderZone(leader);
        var behind = _team.Skip(1).Where(m => m.ZoneActor is null || !m.ZoneActor.Equals(leader.ZoneActor)).ToList();
        if (behind.Count > 0) {
            if (_apartSince == DateTime.MinValue) {
                _apartSince = DateTime.UtcNow;
            }

            foreach (var member in behind) {
                CatchUp(member);
            }

            // The leader waits for them, unless the area takes one at a time (they stay apart a long while).
            if (DateTime.UtcNow - _apartSince < TimeSpan.FromSeconds(90)) {
                _doing = "waiting for the team to catch up";
                StopWalking(0);

                return;
            }
        }
        else {
            _apartSince = DateTime.MinValue;
        }

        // The ones in the leader's area follow in a line: Ice, Life, Death, Storm.
        var together = _team.Where(m => m.ZoneActor is not null && m.ZoneActor.Equals(leader.ZoneActor)).ToList();
        for (var i = 1; i < _team.Count; i++) {
            if (together.Contains(_team[i])) {
                Follow(i, together);
            }
        }

        // The leader waits while someone in its area has fallen behind.
        if (together.Skip(1).Any(m => Far(m, _team[_team.IndexOf(m) - 1]) > FOLLOW_FAR)) {
            _doing = "waiting for the line to close up";
            StopWalking(0);

            return;
        }

        Lead(together);
    }

    // ---------------------------------------------------------------------
    // The leader: what to do next
    // ---------------------------------------------------------------------

    private void Lead(List<QuestTeamMember.Status> group) {
        var leader = group[0];
        var target = PickTarget(group);
        if (target is null) {
            StopWalking(0);

            return;
        }

        if (!target.Same(_target)) {
            _target = target;
            _targetSince = DateTime.UtcNow;
            Note("Team", $"next: {Describe(target)}.");
            // A fight is next: each deck made ready for it.
            if (target.Kind == QuestTeamPlanner.FIGHT) {
                PrepareDecks(BotCards.SituationOf(target.Template));
            }
        }

        if (target.Problem.Length > 0) {
            _doing = $"stuck: {target.What}: {target.Problem}";
            StopWalking(0);
            Issue("quest", target.Quest, $"{target.What}: {target.Problem}.", key: target.Key + "|problem");

            return;
        }

        _doing = Describe(target);
        var reach = target.Kind switch {
            QuestTeamPlanner.TALK or QuestTeamPlanner.OFFER or QuestTeamPlanner.TRAIN or QuestTeamPlanner.SHOP => 160f,
            QuestTeamPlanner.DOOR or QuestTeamPlanner.SPIRAL => 140f,
            QuestTeamPlanner.FIGHT => 40f,
            _ => 25f,
        };

        var distance = QuestTeamPlanner.Far(leader.X, leader.Y, target.X, target.Y);
        if (distance > reach) {
            WalkTo(0, target);

            return;
        }

        // There: what is done here, one wizard at a time in the team's order.
        switch (target.Kind) {
            case QuestTeamPlanner.TALK:
                ClickEach(group, target, ["QuestPersonaGoalService", "QuestOfferService", "QuestUnderwayService"],
                    m => HoldsGoal(m, target.Quest, target.Goal));
                break;
            case QuestTeamPlanner.OFFER:
                ClickEach(group, target, ["QuestOfferService"], m => m.Wizard?.HasQuest(target.Quest) == false
                                                                     && m.Wizard?.HasCompletedQuest(target.Quest) == false);
                break;
            case QuestTeamPlanner.USE:
                UseHere(group, target);
                break;
            case QuestTeamPlanner.SPOT:
                // Each of them steps on the spot (an area a quest asks to walk into counts for whoever walks in).
                for (var i = 1; i < group.Count; i++) {
                    if (HoldsGoal(group[i], target.Quest, target.Goal) && QuestTeamPlanner.Far(group[i].X, group[i].Y, target.X, target.Y) > 25) {
                        WalkTo(_team.IndexOf(group[i]), target);
                    }
                }

                break;
            case QuestTeamPlanner.FIGHT:
                // Walking onto the creature starts the battle; nothing to click.
                WalkTo(0, target with { X = target.X + 1 });
                break;
            case QuestTeamPlanner.EXIT:
                // On into the exit: a step past its middle.
                WalkTo(0, target with { X = target.X + 5, Y = target.Y + 5 });
                break;
            case QuestTeamPlanner.DOOR:
                ClickOnce(0, target, ["Interact"], "opens the door " + target.What);
                break;
            case QuestTeamPlanner.SPIRAL:
                if (CanClick(target.Key + "|0")) {
                    Clicked(target.Key + "|0");
                    Note(leader.Role, $"{leader.Name} uses the Spiral Door to {QuestTeamText.Zone(target.To)}.");
                    _actors[0].Tell(new QuestTeamMember.WorldTeleport(target.GlobalId, target.To));
                    _leaderLastTarget = target;
                }

                break;
            case QuestTeamPlanner.TRAIN:
                TrainHere(group, target);
                break;
            case QuestTeamPlanner.SHOP:
                ShopHere(group, target);
                break;
        }
    }

    private QuestTeamPlanner.Target PickTarget(List<QuestTeamMember.Status> group) {
        var leader = group[0];

        // Training: when someone has spells waiting and the team is in Wizard City (or two spells wait), a trip to Ravenwood.
        if (TrainingTarget(group) is { } training) {
            return training;
        }

        // Shopping: better gear one of them can pay for, at a shop in this world.
        if (ShoppingTarget(group) is { } shopping) {
            return shopping;
        }

        _currentQuest = QuestTeamPlanner.PickQuest(_team);
        if (_currentQuest is not null) {
            var target = QuestTeamPlanner.ForQuest(group, _currentQuest);
            if (target is not null) {
                return target;
            }
        }

        // Nobody here has a story quest: the next one, from whoever gives it.
        if (QuestTeamPlanner.NextQuest(leader.Wizard) is var (quest, giver)) {
            return QuestTeamPlanner.ForGiver(leader, quest, giver);
        }

        if (_currentQuest is null) {
            _doing = "no World or Story quest is left to get";
            Issue("team", null, "There is no World or Story quest left for the team to get. If they have not reached the last world, " +
                                "the next quest's giver or its requirements are the problem.", key: "nothing-left|" + leader.Zone);
        }

        return null;
    }

    private QuestTeamPlanner.Target TrainingTarget(List<QuestTeamMember.Status> group) {
        if (DateTime.UtcNow < _nextTraining) {
            return _target?.Kind == QuestTeamPlanner.TRAIN ? _target : null;
        }

        var lessons = group.Select(m => (Member: m, Lessons: QuestTeamTraining.Learnable(m))).Where(x => x.Lessons.Count > 0).ToList();
        if (lessons.Count == 0) {
            _nextTraining = DateTime.UtcNow.AddSeconds(30);

            return null;
        }

        var leader = group[0];

        // Never out of a dungeon for it: the trip waits until they are out.
        if (group.Any(m => m.Wizard?.ZoneIsInstance == true)) {
            return null;
        }

        var inWizardCity = (leader.Zone ?? "").StartsWith("WizardCity/", StringComparison.OrdinalIgnoreCase);
        if (!inWizardCity && lessons.Sum(l => l.Lessons.Count) < 3) {
            _nextTraining = DateTime.UtcNow.AddSeconds(30);

            return null;
        }

        if (!OverlayQuestTracker.SameZone(QuestTeamTraining.TRAINER_ZONE, leader.Zone)) {
            return QuestTeamPlanner.Travel(leader, QuestTeamTraining.TRAINER_ZONE, "training");
        }

        // The first lesson in the team's order; its trainer's spot.
        var (member, list) = lessons[0];
        var lesson = list[0];
        var live = MinimapIndex.In(leader.ZoneActor);
        var trainer = live.FirstOrDefault(m => m.Template == lesson.TrainerTemplate);
        var (x, y) = trainer is null ? (lesson.X, lesson.Y) : (trainer.Object.m_location.X, trainer.Object.m_location.Y);

        return new QuestTeamPlanner.Target(QuestTeamPlanner.TRAIN, x, y, $"{member.Name} trains {lesson.Spell}", Template: lesson.TrainerTemplate,
            GlobalId: trainer?.Object.m_globalID.Full ?? 0, Key: $"train|{member.Role}|{lesson.SpellId}");
    }

    // ---------------------------------------------------------------------
    // Clicking things
    // ---------------------------------------------------------------------

    private bool CanClick(string key) {
        if (DateTime.UtcNow < _nextClick) {
            return false;
        }

        return !_clicks.TryGetValue(key, out var done) || DateTime.UtcNow - done.Last > TimeSpan.FromSeconds(20);
    }

    private void Clicked(string key) {
        _nextClick = DateTime.UtcNow.AddSeconds(2);
        _clicks[key] = (_clicks.TryGetValue(key, out var done) ? done.Times + 1 : 1, DateTime.UtcNow);
    }

    private int TimesClicked(string key) => _clicks.TryGetValue(key, out var done) ? done.Times : 0;

    // Each wizard who needs to, in the team's order, clicks the NPC's option once it is in range of it.
    private void ClickEach(List<QuestTeamMember.Status> group, QuestTeamPlanner.Target target, string[] services,
                           Func<QuestTeamMember.Status, bool> needs) {
        foreach (var member in group) {
            if (!needs(member)) {
                continue;
            }

            var index = _team.IndexOf(member);
            var key = $"{target.Key}|{index}";
            if (QuestTeamPlanner.Far(member.X, member.Y, target.X, target.Y) > 200) {
                WalkTo(index, target with { X = target.X - 40 });

                return;
            }

            if (!member.Options.TryGetValue(target.GlobalId, out var options)) {
                // Not sent its options yet: a step closer.
                if (QuestTeamPlanner.Far(member.X, member.Y, target.X, target.Y) > 80) {
                    WalkTo(index, target with { X = target.X - 30 });
                }
                else if (TimesClicked(key) == 0 && DateTime.UtcNow - _targetSince > TimeSpan.FromSeconds(30)) {
                    Issue("quest", target.Quest, $"{target.What}: {member.Name} stands next to the NPC, but it offers nothing to click.",
                        key: key + "|nooptions");
                }

                return;
            }

            var option = services.SelectMany(s => options.Where(o => string.Equals(o.Service, s, StringComparison.OrdinalIgnoreCase))).FirstOrDefault();
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
            _actors[index].Tell(new QuestTeamMember.Interact(target.GlobalId, option.Service, option.Index, target.What));

            return;
        }
    }

    private void ClickOnce(int index, QuestTeamPlanner.Target target, string[] services, string what) {
        var member = _team[index];
        var key = $"{target.Key}|{index}";
        if (!CanClick(key)) {
            return;
        }

        if (!member.Options.TryGetValue(target.GlobalId, out var options)) {
            WalkTo(index, target with { X = target.X - 20 });

            return;
        }

        var option = services.SelectMany(s => options.Where(o => string.Equals(o.Service, s, StringComparison.OrdinalIgnoreCase))).FirstOrDefault();
        if (option.Service is null) {
            option = options.FirstOrDefault();
        }

        Clicked(key);
        Note(member.Role, $"{member.Name} {what}.");
        _actors[index].Tell(new QuestTeamMember.Interact(target.GlobalId, option.Service ?? "Interact", option.Index, what));
        if (index == 0) {
            _leaderLastTarget = target;
        }
    }

    // A thing to use where the quest arrow points: each wizard who needs it clicks what offers "Interact" near the spot.
    private void UseHere(List<QuestTeamMember.Status> group, QuestTeamPlanner.Target target) {
        foreach (var member in group.Where(m => HoldsGoal(m, target.Quest, target.Goal))) {
            var index = _team.IndexOf(member);
            if (QuestTeamPlanner.Far(member.X, member.Y, target.X, target.Y) > 120) {
                WalkTo(index, target);

                return;
            }

            var usable = member.Options.Where(o => o.Value.Any(s => s.Service == "Interact")).Select(o => o.Key).ToList();
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
                Note(member.Role, $"{member.Name} uses something for {target.What}.");
                _actors[index].Tell(new QuestTeamMember.Interact(id, option.Service, option.Index, target.What));

                return;
            }

            if (DateTime.UtcNow - _targetSince > TimeSpan.FromSeconds(90)) {
                Issue("quest", target.Quest, $"{target.What}: {member.Name} used what is there and the step did not finish.", key: target.Key + "|usednothing");
            }

            return;
        }
    }

    private void TrainHere(List<QuestTeamMember.Status> group, QuestTeamPlanner.Target target) {
        var parts = target.Key.Split('|');
        var member = _team.FirstOrDefault(m => m.Role == parts[1]);
        if (member is null || !uint.TryParse(parts[2], out var spellId)) {
            return;
        }

        var index = _team.IndexOf(member);
        if (QuestTeamPlanner.Far(member.X, member.Y, target.X, target.Y) > 200) {
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

        var spot = QuestTeamTraining.IndexAt(member, target.GlobalId, spellId);
        if (spot < 0) {
            Note(member.Role, $"{member.Name} can't find {QuestTeamText.SpellName(spellId)} in this trainer's list.");
            _nextTraining = DateTime.UtcNow.AddMinutes(10);

            return;
        }

        Clicked(key);
        if (member.Options.TryGetValue(target.GlobalId, out var options) && options.FirstOrDefault(o => o.Service == "WizTrainingService") is { Service: not null } open) {
            _actors[index].Tell(new QuestTeamMember.Interact(target.GlobalId, open.Service, open.Index, "training"));
        }

        Note(member.Role, $"{member.Name} trains {QuestTeamText.SpellName(spellId)}.");
        Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(1.5), _actors[index],
            new QuestTeamMember.Train(target.GlobalId, spot, QuestTeamText.SpellName(spellId)), Self);
        _nextTraining = DateTime.UtcNow.AddSeconds(3);
    }

    private static bool HoldsGoal(QuestTeamMember.Status member, string questName, string goalName) {
        var held = member.Wizard?.QuestBehavior?.CurrentQuestInstances?.FirstOrDefault(q => q is not null
            && string.Equals(q.QuestName, questName, StringComparison.OrdinalIgnoreCase));

        return held is not null && (string.IsNullOrEmpty(goalName) || held.IsGoalActive(goalName));
    }

    // ---------------------------------------------------------------------
    // Walking in a line
    // ---------------------------------------------------------------------

    private void WalkTo(int index, QuestTeamPlanner.Target target) {
        var member = _team[index];
        if (_sent.TryGetValue(index, out var last) && last.Target is not null && last.Target.Same(target) && member.Walking
            && DateTime.UtcNow - last.At < TimeSpan.FromSeconds(15)) {
            return;
        }

        var points = OverlayQuestPath.Walk(member.CharId, member.Zone, member.X, member.Y, target.X, target.Y);
        _sent[index] = (target, DateTime.UtcNow, member.X, member.Y);
        _actors[index].Tell(new QuestTeamMember.WalkTo(points, target.What));
        if (index == 0) {
            _leaderLastTarget = target;
        }
    }

    private void StopWalking(int index) {
        if (_team[index].Walking) {
            _actors[index].Tell(new QuestTeamMember.StopWalking());
            _sent.Remove(index);
        }
    }

    // Each follows the one before it, a short gap behind.
    private void Follow(int index, List<QuestTeamMember.Status> together) {
        var member = _team[index];
        var ahead = _team[index - 1];
        if (!together.Contains(ahead)) {
            ahead = together[0];
        }

        var gap = Far(member, ahead);
        if (gap < FOLLOW_GAP * 1.6f) {
            if (gap < FOLLOW_GAP * 0.8f) {
                StopWalking(index);
            }

            return;
        }

        // A point behind the one ahead, on the line between them.
        var (dx, dy) = (member.X - ahead.X, member.Y - ahead.Y);
        var (x, y) = (ahead.X + (dx / gap * FOLLOW_GAP), ahead.Y + (dy / gap * FOLLOW_GAP));
        var follow = new QuestTeamPlanner.Target("follow", x, y, $"follows {ahead.Name}", Key: "follow");
        if (_sent.TryGetValue(index, out var last) && member.Walking && QuestTeamPlanner.Far(last.Target.X, last.Target.Y, x, y) < 80
            && DateTime.UtcNow - last.At < TimeSpan.FromSeconds(6)) {
            return;
        }

        var points = OverlayQuestPath.Walk(member.CharId, member.Zone, member.X, member.Y, x, y);
        _sent[index] = (follow, DateTime.UtcNow, member.X, member.Y);
        _actors[index].Tell(new QuestTeamMember.WalkTo(points, follow.What));
    }

    // The leader changed areas: where it left from and how, for the ones behind.
    private void NoteLeaderZone(QuestTeamMember.Status leader) {
        if (_leaderZone.Length > 0 && !string.Equals(_leaderZone, leader.Zone, StringComparison.OrdinalIgnoreCase)) {
            _trail.Add((_leaderZone, _leaderLastX, _leaderLastY, _leaderLastTarget, DateTime.UtcNow));
            if (_trail.Count > 20) {
                _trail.RemoveAt(0);
            }

            Note("Team", $"{leader.Name} is now in {QuestTeamText.Zone(leader.Zone)}; the others follow.");
        }

        _leaderZone = leader.Zone ?? "";
        _leaderLastX = leader.X;
        _leaderLastY = leader.Y;
    }

    // A wizard in another area than the leader goes where the leader left that area, and the same way.
    private void CatchUp(QuestTeamMember.Status member) {
        var index = _team.IndexOf(member);
        if (member.State != "playing") {
            return;
        }

        var step = _trail.LastOrDefault(t => string.Equals(t.Zone, member.Zone, StringComparison.OrdinalIgnoreCase));
        QuestTeamPlanner.Target how;
        if (step.Zone is not null && step.How is { } way && way.Kind is QuestTeamPlanner.DOOR or QuestTeamPlanner.SPIRAL or QuestTeamPlanner.EXIT) {
            how = way;
        }
        else if (step.Zone is not null) {
            how = new QuestTeamPlanner.Target(QuestTeamPlanner.EXIT, step.X, step.Y, $"where {_team[0].Name} left", Key: "trail|" + member.Zone);
        }
        else {
            how = QuestTeamPlanner.Travel(member, _team[0].Zone, $"to {_team[0].Name}");
        }

        if (how is null) {
            return;
        }

        var reach = how.Kind is QuestTeamPlanner.DOOR or QuestTeamPlanner.SPIRAL ? 140f : 25f;
        if (QuestTeamPlanner.Far(member.X, member.Y, how.X, how.Y) > reach) {
            WalkTo(index, how);

            return;
        }

        switch (how.Kind) {
            case QuestTeamPlanner.DOOR:
                ClickOnce(index, how, ["Interact"], "follows through the door");
                break;
            case QuestTeamPlanner.SPIRAL:
                if (CanClick(how.Key + "|" + index)) {
                    Clicked(how.Key + "|" + index);
                    Note(member.Role, $"{member.Name} follows through the Spiral Door.");
                    _actors[index].Tell(new QuestTeamMember.WorldTeleport(how.GlobalId, how.To));
                }

                break;
            default:
                WalkTo(index, how with { X = how.X + 5, Y = how.Y + 5 });
                break;
        }
    }

    // ---------------------------------------------------------------------
    // Battles
    // ---------------------------------------------------------------------

    private bool HandleBattle() {
        var fighting = _team.Where(m => CombatDuelComponent.IsInBattle(m.GlobalId)).ToList();
        for (var i = 0; i < _team.Count; i++) {
            var now = CombatDuelComponent.IsInBattle(_team[i].GlobalId);
            if (_wasInBattle[i] && !now) {
                BattleOver(i);
            }

            _wasInBattle[i] = now;
        }

        if (fighting.Count == 0) {
            return false;
        }

        _doing = $"in a battle ({fighting.Count} of 4 in it)";
        _lastProgress = DateTime.UtcNow;

        // The next one in the team's order who isn't in walks onto the battle (only one at a time, so they join in order).
        var battle = fighting[0];
        foreach (var member in _team) {
            if (CombatDuelComponent.IsInBattle(member.GlobalId)) {
                continue;
            }

            if (member.ZoneActor is null || !member.ZoneActor.Equals(battle.ZoneActor)) {
                continue;
            }

            var index = _team.IndexOf(member);
            WalkTo(index, new QuestTeamPlanner.Target("battle", battle.X, battle.Y, "joins the battle", Key: "battle"));

            break;
        }

        return true;
    }

    private DateTime _regroupSeen = DateTime.MinValue;
    private DateTime _regroupUntil = DateTime.MinValue;
    private DateTime _nextRecall = DateTime.MinValue;

    // After a broken formation (CombatDuelComponent.QuestTeam.cs): nobody stays in a battle, they gather by Ice, and a
    // dungeon left by fleeing is entered again with Dungeon Recall, Ice first, before they go back to their quest's fight.
    private bool Regroup() {
        var (at, why) = QuestTeamDirector.LastFormationBreak;
        if (at > _regroupSeen) {
            _regroupSeen = at;
            _regroupUntil = DateTime.UtcNow.AddSeconds(12);
            _target = null;
            Note("Team", $"regroups ({why}); they go back to their fight together, in their order.");
        }

        if (DateTime.UtcNow >= _regroupUntil) {
            return false;
        }

        _doing = "regrouping to keep the formation";
        _lastProgress = DateTime.UtcNow;

        // Still in the battle: they leave it (as a player clicks Flee).
        foreach (var member in _team.Where(m => CombatDuelComponent.IsInBattle(m.GlobalId))) {
            _actors[_team.IndexOf(member)].Tell(new QuestTeamMember.Flee());
        }

        // Thrown out of a dungeon by fleeing: Dungeon Recall, Ice first and the rest after it.
        if (DateTime.UtcNow >= _nextRecall) {
            for (var i = 0; i < _team.Count; i++) {
                if (_team[i].Wizard?.ZoneIsInstance != true && World.DungeonRecall.TryGet(_team[i].CharId, out _)) {
                    Note(_team[i].Role, $"{_team[i].Name} uses Dungeon Recall.");
                    _actors[i].Tell(new QuestTeamMember.UseDungeonRecall());
                    _nextRecall = DateTime.UtcNow.AddSeconds(4);
                    _regroupUntil = DateTime.UtcNow.AddSeconds(12);

                    return true;
                }
            }
        }

        // The rest gather by Ice.
        var ice = _team[0];
        for (var i = 1; i < _team.Count; i++) {
            if (_team[i].ZoneActor is not null && _team[i].ZoneActor.Equals(ice.ZoneActor) && Far(_team[i], ice) > 120) {
                WalkTo(i, new QuestTeamPlanner.Target("regroup", ice.X - (40 * i), ice.Y, "regroups by Ice", Key: "regroup"));
            }
        }

        return true;
    }

    private void BattleOver(int index) {
        var member = _team[index];
        var stats = member.Wizard?.GameStats;
        if (stats is null) {
            return;
        }

        var lost = stats.m_currentHitpoints <= 1;
        if (!lost || index != 0) {
            if (index == 0) {
                Note("Team", "won the battle.");
            }

            return;
        }

        var key = _target?.Key ?? member.Zone;
        _losses[key] = _losses.GetValueOrDefault(key) + 1;
        Note("Team", $"lost the battle ({_losses[key]} time(s) at this step).");
        if (_losses[key] >= QuestTeamWire.LossesBeforeIssue) {
            Issue("quest", _target?.Quest, $"The team lost the fight for \"{_target?.What ?? "this step"}\" {_losses[key]} times.", key: key + "|losses");
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
                _actors[i].Tell(new QuestTeamMember.UsePotion());
            }
        }
    }

    // The four of them in one group, so dungeons and their quests are shared (Ice invites the others).
    private void KeepGrouped() {
        if (DateTime.UtcNow < _nextGroupTry) {
            return;
        }

        _nextGroupTry = DateTime.UtcNow.AddSeconds(20);
        var leader = _team[0];
        foreach (var member in _team.Skip(1)) {
            if (!GroupRegistry.AreGrouped(leader.CharId, member.CharId)) {
                _actors[0].Tell(new QuestTeamMember.Invite(member.CharId, member.GlobalId));
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
                    if (_seenGoals.Add(quest.QuestName + "|" + done.GoalName)) {
                        QuestTeamLink.Send(new TeamRequest { Op = "quest", Event = "step", Quest = quest.QuestName, Title = QuestTeamText.Title(quest.QuestName),
                            Text = $"{member.Name} finished a step" });
                    }
                }
            }
        }

        // A quest none of them holds any more, that one of them finished: done.
        foreach (var run in _runs.Values.ToList()) {
            var holder = _team.Any(m => m.Wizard?.HasQuest(run.Quest) == true);
            var finished = _team.Any(m => m.Wizard?.HasCompletedQuest(run.Quest) == true);
            if (!holder && finished) {
                var seconds = (DateTime.UtcNow - run.Started).TotalSeconds;
                Note("Team", $"FINISHED {run.Title} in {Minutes(seconds)}.");
                QuestTeamLink.Send(new TeamRequest { Op = "quest", Event = "done", Quest = run.Quest, Title = run.Title, World = run.World, Seconds = seconds });
                _runs.Remove(run.Quest);
            }
            else if (!run.SlowTold && run.Expected > 0 && (DateTime.UtcNow - run.Started).TotalSeconds > run.Expected * QuestTeamWire.SlowFactor) {
                run.SlowTold = true;
                Issue("slow", run.Quest, $"{run.Title} is taking much longer than expected: {Minutes((DateTime.UtcNow - run.Started).TotalSeconds)} so far, " +
                                          $"about {Minutes(run.Expected)} expected. Now: {_doing}.", key: "slow|" + run.Quest);
            }
        }

        var signature = string.Join(';', parts);
        if (signature != _signature) {
            _signature = signature;
            _lastProgress = DateTime.UtcNow;
        }

        // Nothing has changed for a long while: stuck.
        if (DateTime.UtcNow - _lastProgress > TimeSpan.FromMinutes(QuestTeamWire.StuckMinutes)) {
            _lastProgress = DateTime.UtcNow;
            var kind = _target?.Problem.Length > 0 || _target?.Kind is QuestTeamPlanner.TALK or QuestTeamPlanner.USE ? "quest" : "team";
            Issue(kind, _target?.Quest ?? _currentQuest, $"No progress for {QuestTeamWire.StuckMinutes} minutes. The team is {_doing}.",
                key: "stuck|" + (_target?.Key ?? _doing));
        }
    }

    private void StartRun(string questName) {
        if (_runs.ContainsKey(questName) || QuestTemplateCollection.GetQuestByName(questName) is not { } quest) {
            return;
        }

        var kind = QuestStory.KindOf(quest);
        if (kind == QuestStory.SIDE) {
            return;
        }

        var run = new QuestRun { Quest = questName, Title = QuestTeamText.Title(questName), World = QuestStory.WorldOf(quest) };
        _runs[questName] = run;
        var steps = quest.m_goals?.Count(g => g is not null) ?? 1;
        var fights = quest.m_goals?.Count(g => g is Imcodec.ObjectProperty.TypeCache.BountyGoalTemplate) ?? 0;
        Note("Team", $"STARTED {run.Title} ({kind}, {steps} steps).");
        var self = Self;
        _ = System.Threading.Tasks.Task.Run(() => {
            var answer = QuestTeamLink.Ask(new TeamRequest { Op = "quest", Event = "start", Quest = questName, Title = run.Title, World = run.World,
                Steps = steps, Fights = fights }, quiet: true);
            if (answer?.Ok == true) {
                run.Expected = answer.Expected;
            }
        });
    }

    private void Issue(string kind, string quest, string text, string key = null) {
        key ??= kind + "|" + quest + "|" + text;
        if (_reported.TryGetValue(key, out var when) && DateTime.UtcNow - when < TimeSpan.FromMinutes(30)) {
            return;
        }

        _reported[key] = DateTime.UtcNow;
        var template = string.IsNullOrEmpty(quest) ? null : QuestTemplateCollection.GetQuestByName(quest);
        QuestTeamLink.Issue(kind, quest ?? "", string.IsNullOrEmpty(quest) ? "" : QuestTeamText.Title(quest),
            template is null ? "" : QuestStory.WorldOf(template), text);
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
                    ["zone"] = QuestTeamText.Zone(member.Zone),
                    ["health"] = stats is null ? "" : $"{stats.m_currentHitpoints}/{stats.m_baseHitpoints + stats.m_bonusHitpoints}",
                    ["gold"] = stats?.m_currentGold ?? 0,
                    ["training"] = wizard?.MagicSchoolBehavior?.TrainingPoints ?? 0,
                    ["battle"] = CombatDuelComponent.IsInBattle(member.GlobalId),
                    ["quests"] = wizard?.QuestBehavior?.CurrentQuestInstances?.Count ?? 0,
                    // What Ahziael's Quest Team page shows under each wizard: its quests, gear, deck and trained spells.
                    ["questList"] = new JsonArray([.. (wizard?.QuestBehavior?.CurrentQuestInstances?.ToList() ?? [])
                        .Where(q => q is not null).Select(q => (JsonNode) QuestTeamText.Title(q.QuestName))]),
                    ["gear"] = new JsonArray([.. BotShared.BotGear.Describe(wizard).Select(g => (JsonNode) g)]),
                    ["deck"] = wizard?.SpellbookBehavior?.SpellList is { } deck
                        ? BotShared.BotCards.Describe(deck.Where(d => d is not null).Select(d => ((uint) d.m_templateID, (int) d.m_quantity)))
                        : "",
                    ["deckFor"] = _deckFor.GetValueOrDefault(member.Role) ?? "",
                    ["spells"] = new JsonArray([.. (wizard?.SpellbookBehavior?.LearnedSpellTemplateIds?.ToList() ?? [])
                        .Select(id => QuestTeamText.SpellName(id)).Where(n => !string.IsNullOrEmpty(n)).Distinct().Order().Select(n => (JsonNode) n)]),
                });
            }

            var run = _currentQuest is null ? null : _runs.GetValueOrDefault(_currentQuest);
            var status = new JsonObject {
                ["state"] = state,
                ["why"] = why ?? "",
                ["doing"] = _doing,
                ["quest"] = _currentQuest is null ? "" : QuestTeamText.Title(_currentQuest),
                ["questCode"] = _currentQuest ?? "",
                ["questFor"] = run is null ? 0 : (int) (DateTime.UtcNow - run.Started).TotalSeconds,
                ["questExpected"] = run is null ? 0 : (int) run.Expected,
                ["lastProgress"] = _lastProgress.ToString("o"),
                ["at"] = DateTime.UtcNow.ToString("o"),
                ["members"] = members,
            };
            QuestTeamLink.Send(new TeamRequest { Op = "status", Json = status.ToJsonString() });
        }
        catch (Exception e) {
            Logger.Debug("Quest Team: could not write the status: {0}", Logger.Args(e.Message));
        }
    }

    private static void Note(string who, string text) => QuestTeamLink.Note(who, text);

    private static string Describe(QuestTeamPlanner.Target target) => target.Kind switch {
        QuestTeamPlanner.TALK => $"{target.What} ({QuestTeamText.Title(target.Quest)})",
        QuestTeamPlanner.OFFER => target.What,
        QuestTeamPlanner.FIGHT => $"{target.What} ({QuestTeamText.Title(target.Quest)})",
        QuestTeamPlanner.SPOT or QuestTeamPlanner.USE => $"{target.What} ({QuestTeamText.Title(target.Quest)})",
        _ => target.What,
    };

    private static float Far(QuestTeamMember.Status a, QuestTeamMember.Status b) => QuestTeamPlanner.Far(a.X, a.Y, b.X, b.Y);

    private static string Minutes(double seconds) => seconds < 90 ? $"{seconds:0} s" : $"{seconds / 60:0} min";

}
