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
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Round 4 (Niduus, 2026-11-14, "All"): patience with cutscenes, dungeon sense, hints from the quest's own text, his rival
/// creatures, and goals for each session with a report at its end. (The friends page is in Ahziael.)
/// </summary>
internal sealed partial class UltimusDirectorActor {

    // ---------------------------------------------------------------------
    // Cutscenes: watched to the end
    // ---------------------------------------------------------------------

    private DateTime _cutsceneSince = DateTime.MaxValue;

    /// <summary>True while a cutscene plays for him: he stands and watches, as a player does (nothing counts as stuck).</summary>
    private bool WatchingCutscene() {
        if (!_team.Any(m => m.CutsceneUntil > DateTime.UtcNow)) {
            _cutsceneSince = DateTime.MaxValue;
            return false;
        }

        if (_cutsceneSince == DateTime.MaxValue) {
            _cutsceneSince = DateTime.UtcNow;
            Think("A cutscene is playing: I stand and watch it to the end before I go on, as a player does.", "cutscene|" + DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute);
        }

        _doing = "watching a cutscene";
        _lastProgress = DateTime.UtcNow;
        for (var i = 0; i < _team.Count; i++) {
            StopWalking(i);
        }

        // One that never ends is flagged, like a dialog that never finishes.
        if (DateTime.UtcNow - _cutsceneSince > TimeSpan.FromMinutes(2)) {
            var quest = _currentQuest ?? _target?.Quest;
            Issue("quest", quest, "A cutscene has played for over 2 minutes" + (_target is null ? "." : $", at \"{_target.What}\"."),
                key: "cutscene|" + quest + "|" + _target?.Key, broken: false, level: UltimusLink.NOTABLE);
            foreach (var member in _team) {
                member.CutsceneUntil = DateTime.MinValue;
            }
        }

        return true;
    }

    private bool InScene(UltimusMember.Status leader)
        => leader.Talking || leader.CutsceneUntil > DateTime.UtcNow || DateTime.UtcNow - leader.LastDialog < TimeSpan.FromSeconds(2);

    // ---------------------------------------------------------------------
    // Dungeon sense
    // ---------------------------------------------------------------------

    private string _dungeonIn;
    private DateTime _dungeonSince;
    private readonly HashSet<string> _dungeonRooms = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether this move walks him into a dungeon (an instance) from outside one.</summary>
    private static bool IntoDungeon(UltimusMember.Status leader, UltimusPlanner.Target target)
        => target?.Kind is UltimusPlanner.EXIT or UltimusPlanner.DOOR && leader.Wizard?.ZoneIsInstance != true
           && !string.IsNullOrEmpty(target.To) && OverlayQuestRoute.IsDungeon(target.To);

    /// <summary>Health and mana he wants before walking into a dungeon (he finishes a dungeon in one go).</summary>
    private bool DungeonPrep(UltimusMember.Status leader, UltimusPlanner.Target target, out bool lowHealth, out bool lowMana) {
        lowHealth = lowMana = false;
        if (!IntoDungeon(leader, target) || leader.Wizard?.GameStats is not { } stats) {
            return false;
        }

        lowHealth = stats.m_currentHitpoints < Math.Max(1, stats.m_baseHitpoints + stats.m_bonusHitpoints) * UltimusPlaybook.Value("Dungeon.PrepHealth");
        lowMana = stats.m_currentMana < Math.Max(1, stats.m_baseMana + stats.m_bonusMana) * UltimusPlaybook.Value("Dungeon.PrepMana");
        if (lowHealth || lowMana) {
            Think($"\"{target.What}\" takes me into a dungeon ({UltimusText.Zone(target.To)}). A dungeon is done in one go, so I go in ready: " +
                  $"health and mana first ({UltimusPlaybook.Cite("Dungeon.PrepHealth")}).", "dungeon-prep|" + target.To);
        }

        return lowHealth || lowMana;
    }

    /// <summary>In a dungeon: side trips stay closed, and he notes each new room (said once per dungeon and room).</summary>
    private void DungeonTick(UltimusMember.Status leader) {
        if (leader.Wizard?.ZoneIsInstance != true || string.IsNullOrEmpty(leader.Zone)) {
            if (_dungeonIn is not null) {
                Think($"I am out of the dungeon ({UltimusText.Zone(_dungeonIn)}) after {(DateTime.UtcNow - _dungeonSince).TotalMinutes:0} minutes and " +
                      $"{_dungeonRooms.Count} room(s).", "dungeon-out|" + _dungeonIn + "|" + _dungeonSince.Ticks);
                Count("dungeons left");
            }

            _dungeonIn = null;
            _dungeonRooms.Clear();

            return;
        }

        if (_dungeonIn is null) {
            _dungeonIn = leader.Zone;
            _dungeonSince = DateTime.UtcNow;
            Count("dungeons entered");
            Think($"I am in a dungeon ({UltimusText.Zone(leader.Zone)}). I finish it in one go: no side trips until I am out, every fight its " +
                  "steps need, and if I fall, Dungeon Recall takes me back to where I was.", "dungeon-in|" + leader.Zone + "|" + _dungeonSince.Ticks);
        }

        if (_dungeonRooms.Add(leader.Zone) && _dungeonRooms.Count > 1) {
            Think($"Room {_dungeonRooms.Count} of the dungeon: {UltimusText.Zone(leader.Zone)}.", "dungeon-room|" + leader.Zone + "|" + _dungeonSince.Ticks);
        }
    }

    // ---------------------------------------------------------------------
    // Hints from the quest's own words
    // ---------------------------------------------------------------------

    /// <summary>
    /// A step he can't place (no spot on the quest arrow, the person not here): the area or the person its words name, as a
    /// player reads the quest text ("Go to the Oasis", "Talk to Cyrus Drake"). Null when the words name nothing he can use.
    /// </summary>
    private UltimusPlanner.Target HintFromText(UltimusMember.Status leader, UltimusPlanner.Target target) {
        if (target is null || target.Problem.Length == 0 || string.IsNullOrEmpty(target.Quest)) {
            return null;
        }

        var quest = QuestTemplateCollection.GetQuestByName(target.Quest);
        var goal = quest?.m_goals?.FirstOrDefault(g => g is not null && g.m_goalName == target.Goal);
        var words = $"{(goal is null ? "" : QuestGuide.StepText(goal))} {target.What}";
        if (string.IsNullOrWhiteSpace(words)) {
            return null;
        }

        // A person the words name, from his NPC book, in another area.
        foreach (var (id, entry) in UltimusNpcBook.Named(words)) {
            var place = UltimusNpcBook.Of(id, leader.Zone).FirstOrDefault();
            if (place is not null && !OverlayQuestTracker.SameZone(place.Zone, leader.Zone) && OverlayQuestRoute.OnStory(place.Zone)
                && UltimusPlanner.Travel(leader, place.Zone, $"{entry.Name}, whom the quest names", "hint|" + target.Key) is { } toPerson) {
                Think($"I can't place \"{target.What}\" ({target.Problem}), but its words name {entry.Name}, who stands in " +
                      $"{UltimusText.Zone(place.Zone)}: I go there, as a player reading the quest would.", "hint|" + target.Key);

                return toPerson;
            }
        }

        // An area the words name (the longest name that fits, on the story's way).
        var area = ZoneNames.All.Where(z => z.Name.Length >= 5 && z.Type != "D" && OverlayQuestRoute.OnStory(z.Code)
                                            && !OverlayQuestTracker.SameZone(z.Code, leader.Zone) && MenuChatBook.Says(words, z.Name))
            .OrderByDescending(z => z.Name.Length).FirstOrDefault();
        if (area is not null && UltimusPlanner.Travel(leader, area.Code, $"{area.Name}, which the quest names", "hint|" + target.Key) is { } toArea) {
            Think($"I can't place \"{target.What}\" ({target.Problem}), but its words name {area.Name}: I go there and look, as a player " +
                  "reading the quest would.", "hint|" + target.Key);

            return toArea;
        }

        return null;
    }

    // ---------------------------------------------------------------------
    // Rivals
    // ---------------------------------------------------------------------

    /// <summary>A rival is next (a creature that beat him or takes most of his health): said, and met at full strength.</summary>
    private void ThinkAboutRival(UltimusPlanner.Target target) {
        if (target?.Kind != UltimusPlanner.FIGHT || UltimusFoes.Of((uint) Math.Min(target.Template, uint.MaxValue)) is not { } foe
            || !(foe.Lost > 0 || foe.Tough)) {
            return;
        }

        _toughFoeNext = true;
        Think($"{foe.Name} is one of my rivals ({foe.Describe()}). I meet it at full health and mana with a deck made for it" +
              (foe.Lost >= 2 ? ", a level stronger than when it beat me twice." : "."), "rival|" + foe.Template);
    }

    // ---------------------------------------------------------------------
    // Session goals and the end report
    // ---------------------------------------------------------------------

    private sealed class Session {
        public DateTime Started = DateTime.UtcNow;
        public int LevelIn;
        public string QuestIn = "";
        public string WorldIn = "";
        public int QuestsWanted = 3;
        public readonly Dictionary<string, long> Counts = new(StringComparer.Ordinal);
        public bool QuestInDone;
    }

    private Session _session;
    private DateTime _nextSessionWrite = DateTime.MinValue;

    /// <summary>His goals for this session, set when he logs in and has a quest to name.</summary>
    private void SessionStart(UltimusMember.Status leader) {
        if (_session is not null || leader.Wizard is null) {
            return;
        }

        _session = new Session {
            LevelIn = leader.Wizard.MagicSchoolBehavior?.Level ?? 1,
            QuestIn = _currentQuest ?? "",
            WorldIn = WorldOf(leader.Zone),
        };
        Think($"My goals for this session: (1) finish {(_session.QuestIn.Length > 0 ? UltimusText.Title(_session.QuestIn) : "my next story quest")}; " +
              $"(2) finish {_session.QuestsWanted} story quests; (3) reach level {_session.LevelIn + 1}; (4) no off-track alarms. " +
              "I write how it went when the session ends (session-report.txt).", "session-goals|" + _session.Started.Ticks);
        WriteSession(leader, final: false);
    }

    /// <summary>Counted for the session too (Count).</summary>
    private void SessionCount(string what, long amount) {
        if (_session is not null) {
            _session.Counts[what] = _session.Counts.GetValueOrDefault(what) + amount;
        }
    }

    /// <summary>Every 10 minutes the report so far; at the end (log out, Stop) the final one.</summary>
    private void SessionTick(UltimusMember.Status leader) {
        if (_session is null) {
            SessionStart(leader);
            return;
        }

        if (!_session.QuestInDone && _session.QuestIn.Length > 0 && leader.Wizard?.HasCompletedQuest(_session.QuestIn) == true) {
            _session.QuestInDone = true;
        }

        if (DateTime.UtcNow >= _nextSessionWrite) {
            WriteSession(leader, final: false);
        }
    }

    private void WriteSession(UltimusMember.Status leader, bool final) {
        if (_session is not { } s) {
            return;
        }

        _nextSessionWrite = DateTime.UtcNow.AddMinutes(10);
        long Got(string what) => s.Counts.GetValueOrDefault(what);
        var level = leader?.Wizard?.MagicSchoolBehavior?.Level ?? s.LevelIn;
        var quests = Got("quests finished");
        var alarms = Got("off-track alarms");
        var goals = new List<(string Goal, bool Met)> {
            ($"finish {(s.QuestIn.Length > 0 ? UltimusText.Title(s.QuestIn) : "my next story quest")}", s.QuestIn.Length > 0 ? s.QuestInDone : quests > 0),
            ($"finish {s.QuestsWanted} story quests ({quests} done)", quests >= s.QuestsWanted),
            ($"reach level {s.LevelIn + 1} (now {level})", level > s.LevelIn),
            ($"no off-track alarms ({alarms})", alarms == 0),
        };
        var met = goals.Count(g => g.Met);
        var text = new StringBuilder();
        text.AppendLine($"ULTIMUS SESSION REPORT{(final ? "" : " (so far)")}: {s.Started.ToLocalTime():yyyy-MM-dd HH:mm} to {DateTime.Now:HH:mm} " +
                        $"({(DateTime.UtcNow - s.Started).TotalHours:0.0} h). Goals met: {met} of {goals.Count}.");
        text.AppendLine($"Mission: {UltimusMission.STATEMENT}");
        text.AppendLine();
        text.AppendLine("GOALS");
        foreach (var (goal, ok) in goals) {
            text.AppendLine($"  [{(ok ? "x" : " ")}] {goal}");
        }

        text.AppendLine();
        text.AppendLine("WHAT HAPPENED");
        text.AppendLine($"  Started in {s.WorldIn} at level {s.LevelIn}; now {WorldOf(leader?.Zone)} at level {level}.");
        foreach (var (what, count) in s.Counts.OrderByDescending(c => c.Value)) {
            text.AppendLine($"  {count:N0} {what}");
        }

        var rivals = UltimusFoes.Rivals().Take(3).ToList();
        if (rivals.Count > 0) {
            text.AppendLine();
            text.AppendLine("RIVALS");
            foreach (var rival in rivals) {
                text.AppendLine("  " + rival.Describe());
            }
        }

        text.AppendLine();
        text.AppendLine("NEXT TIME");
        text.AppendLine(met == goals.Count ? "  Every goal met: I keep the same plan."
            : alarms > 0 ? "  I went off track: I keep side trips short and check my progress sooner."
            : quests == 0 ? "  No quest finished: the step I was on needs a look (see the issues)."
            : "  Some goals missed: I take the same goals again and push on.");
        try {
            Directory.CreateDirectory(UltimusWire.Folder);
            File.WriteAllText(Path.Combine(UltimusWire.Folder, "session-report.txt"), text.ToString());
            if (final) {
                File.AppendAllText(Path.Combine(UltimusWire.Folder, "session-reports.txt"), text + Environment.NewLine);
            }
        }
        catch (IOException) {
            // Written again in ten minutes, or at the next session's end.
        }

        if (final) {
            Note("Ultimus", $"SESSION REPORT: goals met {met} of {goals.Count} ({quests} quests, level {s.LevelIn} to {level}); session-report.txt.", UltimusLink.NOTABLE);
            Think($"This session is over: I met {met} of my {goals.Count} goals ({quests} story quests, level {s.LevelIn} to {level}). " +
                  "How it went is in session-report.txt.", "session-end|" + s.Started.Ticks);
            _session = null;
        }
    }

}
