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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Ultimus knows where he stands, and Niduus can see it (Niduus, 2026-11-14, "Yes" and "All approved"):
/// <list type="bullet">
/// <item>Story order: which world of the story he is in, the levels it is played at, and the next world.</item>
/// <item>Self-test after each update: when the server's files change, he checks his own parts once (playbook, quest
/// data, way finding, trainers, creatures, the Bazaar) and writes self-test.txt.</item>
/// <item>Pause and explain: Ahziael's button pauses him and he writes his whole plan to explain.txt.</item>
/// <item>Milestones (milestones.jsonl) for Ahziael's banner: a world finished, a level up, badly stuck, the story done.</item>
/// <item>Side quests only when they beat fighting for XP: a side quest of about his level given in this area.</item>
/// <item>A weekly pace report: how long each world took against a typical player (pace-report.txt).</item>
/// </list>
/// </summary>
internal sealed partial class UltimusDirectorActor {

    // ---------------------------------------------------------------------
    // Story order
    // ---------------------------------------------------------------------

    // The levels each world of the story is usually played at (a rough guide, as players and the game's guides give it),
    // and about how many hours a player takes for it.
    private static readonly Dictionary<string, (int From, int To, double Hours)> s_worldLevels = new(StringComparer.OrdinalIgnoreCase) {
        ["WizardCity"] = (1, 10, 8), ["Krokotopia"] = (10, 20, 10), ["Marleybone"] = (20, 25, 8), ["MooShu"] = (25, 30, 10),
        ["DragonSpire"] = (30, 40, 14), ["Celestia"] = (40, 50, 16), ["Zafaria"] = (50, 55, 14), ["Avalon"] = (55, 60, 16),
        ["Azteca"] = (60, 70, 18), ["Khrysalis"] = (70, 80, 24), ["Polaris"] = (80, 90, 14), ["Mirage"] = (90, 100, 16),
        ["Empyrea"] = (100, 110, 22), ["Karamelle"] = (110, 120, 16), ["Lemuria"] = (120, 130, 16), ["Novus"] = (130, 140, 18),
        ["Wallaru"] = (140, 150, 18), ["Selenopolis"] = (150, 160, 18), ["Ahmarra"] = (160, 170, 18),
    };

    /// <summary>The story world he is on: the zone folder of his main quest's step, else of where he stands.</summary>
    private string StoryFolder(UltimusMember.Status leader) {
        if (!string.IsNullOrEmpty(_currentQuest) && QuestTemplateCollection.GetQuestByName(_currentQuest) is { } quest) {
            var zone = (quest.m_goals ?? []).Where(g => g is not null).Select(g => (string) g.m_destinationZone).FirstOrDefault(z => !string.IsNullOrWhiteSpace(z));
            var folder = (zone ?? "").Split('/')[0];
            if (UltimusPlanner.Ladder.Contains(folder, StringComparer.OrdinalIgnoreCase)) {
                return folder;
            }
        }

        var here = (leader.Zone ?? "").Split('/')[0];

        return UltimusPlanner.Ladder.FirstOrDefault(w => string.Equals(w, here, StringComparison.OrdinalIgnoreCase)) ?? "";
    }

    /// <summary>Where he stands in the story, in words: "world 2 of 19, Krokotopia (levels 10 to 20); next Marleybone".</summary>
    private string StoryPlace(UltimusMember.Status leader) {
        var folder = StoryFolder(leader);
        var ladder = UltimusPlanner.Ladder.ToList();
        var index = ladder.FindIndex(w => string.Equals(w, folder, StringComparison.OrdinalIgnoreCase));
        if (index < 0) {
            return "not in a story world";
        }

        var levels = s_worldLevels.GetValueOrDefault(folder);
        var next = index + 1 < ladder.Count ? WorldOf(ladder[index + 1] + "/") : "the end of the story";

        return $"world {index + 1} of {ladder.Count}, {WorldOf(folder + "/")} (played at about levels {levels.From} to {levels.To}); next: {next}";
    }

    private string _storySaid = "";

    /// <summary>Said once per world: where he stands in the story and whether his level is ahead or behind.</summary>
    private void ThinkStory(UltimusMember.Status leader) {
        var folder = StoryFolder(leader);
        if (folder.Length == 0 || folder == _storySaid || !s_worldLevels.TryGetValue(folder, out var levels)) {
            return;
        }

        _storySaid = folder;
        var level = leader.Wizard?.MagicSchoolBehavior?.Level ?? 1;
        var feel = level < levels.From - 1 ? $"I am level {level}, behind it: I fight a little more on the way and play it carefully"
            : level > levels.To ? $"I am level {level}, ahead of it: it should go quickly"
            : $"I am level {level}, right for it";
        Think($"Story: {StoryPlace(leader)}. {feel}.", "story|" + folder);
    }

    // ---------------------------------------------------------------------
    // Milestones (Ahziael's banner)
    // ---------------------------------------------------------------------

    private const string MILESTONES_FILE = "milestones.jsonl";
    private int _milestoneLevel = -1;

    internal static void Milestone(string kind, string text) {
        try {
            var path = Path.Combine(UltimusWire.Folder, MILESTONES_FILE);
            var id = File.Exists(path) ? File.ReadLines(path).Count() + 1 : 1;
            var line = JsonSerializer.Serialize(new { id, at = DateTime.UtcNow, kind, text });
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (Exception e) {
            Logger.Debug("Ultimus: could not write a milestone: {0}", Logger.Args(e.Message));
        }

        Note("Ultimus", $"MILESTONE ({kind}): {text}", UltimusLink.NOTABLE);
    }

    private void LevelMilestone(UltimusMember.Status leader) {
        var level = leader.Wizard?.MagicSchoolBehavior?.Level ?? 0;
        if (level <= 0) {
            return;
        }

        if (_milestoneLevel > 0 && level > _milestoneLevel) {
            StoryProgress();
            Milestone("level", $"Ultimus reached level {level} in {UltimusText.Zone(leader.Zone)} ({StoryPlace(leader)}).");
            UltimusJournal.Write(UltimusJournal.LEVEL, $"Reached level {level} in {UltimusText.Zone(leader.Zone)}.", WorldOf(leader.Zone), UltimusText.Zone(leader.Zone), "", 1);
            Announce(SAY_MOMENT, $"Level {level}! Woo!", urgent: true);
        }

        _milestoneLevel = level;
    }

    // ---------------------------------------------------------------------
    // Pause and explain
    // ---------------------------------------------------------------------

    private DateTime _nextExplainLook = DateTime.MinValue;

    /// <summary>Ahziael's "Pause and explain": his whole plan, written to explain.txt.</summary>
    private void MaybeExplain(UltimusMember.Status leader) {
        if (DateTime.UtcNow < _nextExplainLook) {
            return;
        }

        _nextExplainLook = DateTime.UtcNow.AddSeconds(1);
        var request = Path.Combine(UltimusWire.Folder, UltimusWire.EXPLAIN_FILE);
        if (!File.Exists(request)) {
            return;
        }

        try {
            File.Delete(request);
        }
        catch (IOException) {
            return;
        }

        var text = new StringBuilder();
        var wizard = leader.Wizard;
        var stats = wizard?.GameStats;
        text.AppendLine($"ULTIMUS EXPLAINS HIMSELF ({DateTime.Now:dddd d MMMM yyyy, HH:mm:ss}). He is paused; press Pause or resume to let him go on.");
        text.AppendLine();
        text.AppendLine("MY MISSION");
        text.AppendLine($"  {UltimusMission.STATEMENT}");
        text.AppendLine("  Hard limits (only Niduus changes them): " + string.Join("; ", UltimusMission.HardLimits) + ".");
        text.AppendLine($"  Side trips: {(SideTripsOpen ? "open" : $"closed until {_sideTripsClosedUntil.ToLocalTime():HH:mm}")}. Last story progress: " +
                        $"{(DateTime.UtcNow - _storyProgressAt).TotalMinutes:0} minute(s) ago. Progress checks in a row with no: {_checkNoes}.");
        text.AppendLine();
        text.AppendLine("WHERE I AM");
        text.AppendLine($"- Level {wizard?.MagicSchoolBehavior?.Level ?? 0}, in {UltimusText.Zone(leader.Zone)} at ({leader.X:0}, {leader.Y:0}).");
        text.AppendLine($"- Story: {StoryPlace(leader)}.");
        if (stats is not null) {
            text.AppendLine($"- Health {stats.m_currentHitpoints}/{stats.m_baseHitpoints + stats.m_bonusHitpoints}, mana {stats.m_currentMana}/{stats.m_baseMana}, " +
                            $"gold {stats.m_currentGold:N0} (I keep {GoldReserve(leader):N0} back), potions {stats.m_potionCharge:0.#}, " +
                            $"backpack {wizard.BackpackFreeSlots} spaces free.");
        }

        text.AppendLine();
        text.AppendLine("MY GOALS");
        text.AppendLine($"- The story world: {WorldOf((StoryFolder(leader).Length > 0 ? StoryFolder(leader) : leader.Zone ?? "") + "/")}.");
        text.AppendLine($"- The quest: {(_currentQuest is null ? "none right now" : UltimusText.Title(_currentQuest))}.");
        text.AppendLine($"- What I do now: {_doing}.");
        if (_target is { } target) {
            var (confidence, reasons) = Confidence(leader, target);
            text.AppendLine($"- My next move: {Describe(target)} at ({target.X:0}, {target.Y:0}); I trust it {confidence}/100 ({string.Join("; ", reasons)}).");
            foreach (var before in RecallStep(target.Quest, target.Goal)) {
                text.AppendLine($"- Last time at this step: {before}");
            }
        }

        text.AppendLine();
        text.AppendLine("HOW I FEEL AND PLAY");
        text.AppendLine($"- Mood: {UltimusMood.Now}. {UltimusMood.Why()}.");
        text.AppendLine($"- Push mode: {(UltimusPlaybook.Value("Push.On") >= 0.5 ? "on" : "off")}. Plays: {BotKnowledge.PartyOf("Ultimus")}.");
        text.AppendLine($"- Last step done or XP gained: {Minutes((DateTime.UtcNow - _progressAt).TotalSeconds)} ago.");
        if (_markedZone is not null) {
            text.AppendLine($"- I marked my spot in {UltimusText.Zone(_markedZone)} and will recall there after this trip.");
        }

        if (_sideQuest is not null) {
            text.AppendLine($"- Side quest for XP: {UltimusText.Title(_sideQuest)}.");
        }

        text.AppendLine();
        text.AppendLine("WHAT I THOUGHT LAST (newest first)");
        foreach (var thought in _recentThoughts.AsEnumerable().Reverse().Take(15)) {
            text.AppendLine("- " + thought);
        }

        text.AppendLine();
        text.AppendLine("TODAY: " + Summary());
        try {
            File.WriteAllText(Path.Combine(UltimusWire.Folder, "explain.txt"), text.ToString());
            File.WriteAllText(Path.Combine(UltimusWire.Folder, UltimusWire.PAUSE_FILE), "Pause and explain");
        }
        catch (Exception e) {
            Logger.Debug("Ultimus: could not write the explanation: {0}", Logger.Args(e.Message));
        }

        Note("Ultimus", "is paused in Ahziael to explain himself: his whole plan is in explain.txt.");
    }

    // ---------------------------------------------------------------------
    // Self-test after each update
    // ---------------------------------------------------------------------

    private bool _selfTested;

    /// <summary>
    /// Once after the server's files change (an update): he checks each of his parts and writes self-test.txt, so a part
    /// an update broke shows at once, not hours later in a stuck log.
    /// </summary>
    private void MaybeSelfTest(UltimusMember.Status leader) {
        if (_selfTested) {
            return;
        }

        _selfTested = true;
        string stamp;
        try {
            var dll = typeof(UltimusDirectorActor).Assembly.Location;
            stamp = $"{typeof(UltimusDirectorActor).Assembly.GetName().Version} {File.GetLastWriteTimeUtc(dll):o}";
        }
        catch (Exception) {
            return;
        }

        var stampFile = Path.Combine(UltimusWire.Folder, "self-test-version.txt");
        try {
            if (File.Exists(stampFile) && File.ReadAllText(stampFile).Trim() == stamp) {
                return;
            }
        }
        catch (IOException) {
            return;
        }

        var results = new List<(string Part, bool Ok, string What)>();
        void Check(string part, Func<string> test) {
            try {
                var what = test();
                results.Add((part, what is null || !what.StartsWith("FAIL", StringComparison.Ordinal), what ?? "ok"));
            }
            catch (Exception e) {
                results.Add((part, false, "FAIL: " + e.Message));
            }
        }

        Check("Playbook", () => UltimusPlaybook.Value("Quest.ReadyHealth") > 0 ? "rules read" : "FAIL: no rules");
        // Every screenshot bug as a check, chat lines included (UltimusReplay, BotSocialChecks).
        Check("Replay pack", () => UltimusReplay.Run() is { Count: > 0 } failed
            ? $"FAIL: {failed.Count} of {UltimusReplay.Count}: {string.Join("; ", failed.Take(3))}"
            : $"all {UltimusReplay.Count} screenshot bugs stay fixed");
        Check("Quest data", () => {
            var quest = _currentQuest ?? UltimusPlanner.PickQuest(_team);
            if (quest is null) {
                return UltimusPlanner.NextQuest(leader.Wizard) is var (next, _) ? $"next quest found: {UltimusText.Title((string) next.m_questName)}" : "no quest held or offered";
            }

            var target = UltimusPlanner.ForQuest(_team, quest);
            return target is null ? $"FAIL: no step worked out for {UltimusText.Title(quest)}"
                : target.Problem.Length > 0 ? $"FAIL: {UltimusText.Title(quest)}: {target.Problem}" : $"{UltimusText.Title(quest)}: {Describe(target)}";
        });
        Check("Way finding", () => {
            var far = Imlight.CoreLib.Game.Zone.Core.ZoneNav.Points(leader.Zone)
                .OrderByDescending(p => UltimusPlanner.Far(leader.X, leader.Y, p.X, p.Y)).Take(1).ToList();
            if (far.Count == 0) {
                return "no walk points in this area";
            }

            var walk = OverlayQuestPath.Walk(leader.CharId, leader.Zone, leader.X, leader.Y, far[0].X, far[0].Y, body: true);
            return walk.Count > 0 ? $"a way across {UltimusText.Zone(leader.Zone)} in {walk.Count} turns" : "FAIL: no way across this area";
        });
        Check("Creatures and NPCs", () => $"{MinimapIndex.In(leader.ZoneActor).Count()} things seen here");
        Check("Trainers", () => $"{UltimusTraining.Learnable(leader).Count} spells he can learn now");
        // Read in the background on the first ask: still reading just after a start is not a failure (coach review 2026-11-14).
        Check("Gear list", () => BotGear.Catalog.Count > 0 ? $"{BotGear.Catalog.Count} pieces known"
            : BotGear.Reading ? "still being read in the background (normal right after a start)" : "FAIL: no gear known");
        // Only how it stands: asking would wake a sleeping Bazaar bot for nothing.
        Check("Bazaar", () => Imlight.CoreLib.Game.Bazaar.BazaarLink.Describe());

        var passed = results.Count(r => r.Ok);
        var text = new StringBuilder();
        text.AppendLine($"ULTIMUS SELF-TEST after an update ({DateTime.Now:yyyy-MM-dd HH:mm}), {stamp}: {passed} of {results.Count} passed.");
        foreach (var (part, ok, what) in results) {
            text.AppendLine($"{(ok ? "PASS" : "FAIL")}  {part}: {what}");
        }

        try {
            File.WriteAllText(Path.Combine(UltimusWire.Folder, "self-test.txt"), text.ToString());
            File.WriteAllText(stampFile, stamp);
        }
        catch (Exception e) {
            Logger.Debug("Ultimus: could not write the self-test: {0}", Logger.Args(e.Message));
        }

        var failed = results.Where(r => !r.Ok).Select(r => $"{r.Part} ({r.What})").ToList();
        Note("Ultimus", $"SELF-TEST after an update: {passed} of {results.Count} passed{(failed.Count == 0 ? "" : ". Failed: " + string.Join("; ", failed))}.",
            failed.Count == 0 ? UltimusLink.NOTABLE : UltimusLink.CRITICAL);
        Milestone("self-test", $"Self-test after an update: {passed} of {results.Count} passed{(failed.Count == 0 ? "." : ". Failed: " + string.Join("; ", failed))}");
    }

    // ---------------------------------------------------------------------
    // Side quests only when they beat fighting for XP
    // ---------------------------------------------------------------------

    private string _sideQuest;

    /// <summary>
    /// Under-leveled: a side quest of about his level whose giver is in this area pays XP, gold and gear on top of the
    /// fights it needs, so it beats fighting one creature at a time. Its next move, or null when none is worth it.
    /// </summary>
    private UltimusPlanner.Target SideQuestForXp(UltimusMember.Status leader) {
        if (UltimusPlaybook.Value("Side.On") < 0.5 || leader.Wizard is not { } wizard || wizard.ZoneIsInstance) {
            return null;
        }

        if (_sideQuest is not null) {
            if (wizard.HasQuest(_sideQuest) && UltimusPlanner.ForQuest(_team, _sideQuest) is { Problem.Length: 0 } step) {
                return step;
            }

            if (wizard.HasCompletedQuest(_sideQuest)) {
                Count("side quests done for XP");
            }

            _sideQuest = null;
        }

        var level = wizard.MagicSchoolBehavior?.Level ?? 1;
        var below = (int) UltimusPlaybook.Value("Side.LevelsBelow");
        var pick = QuestService.QuestFinderGivers()
            .Where(g => UltimusPlanner.IsMainStoryWorld(g.Quest) && !BotStory.Plays(g.Quest) && g.Quest.m_questLevel >= level - below
                        && g.Quest.m_questLevel <= level + 1 && OverlayQuestTracker.SameZone(QuestGuide.GiverZone(g.Quest), leader.Zone)
                        && !wizard.HasQuest(g.Quest.m_questName) && !wizard.HasCompletedQuest(g.Quest.m_questName)
                        && MapService.IsOfferedTo(g.Quest, wizard))
            .OrderByDescending(g => g.Quest.m_questLevel)
            .FirstOrDefault();
        if (pick.Quest is null) {
            return null;
        }

        _sideQuest = pick.Quest.m_questName;
        Think($"I need XP. The side quest {UltimusText.Title(_sideQuest)} (level {pick.Quest.m_questLevel}) is given right here: its reward comes on top of the " +
              "fights it needs, so it beats fighting one creature at a time.", "side|" + _sideQuest);

        return UltimusPlanner.ForGiver(leader, pick.Quest, pick.Giver);
    }

    // ---------------------------------------------------------------------
    // Weekly pace report
    // ---------------------------------------------------------------------

    private void MaybeWeeklyReport() {
        var path = Path.Combine(UltimusWire.Folder, "pace-report.txt");
        try {
            if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromDays(7)) {
                return;
            }
        }
        catch (IOException) {
            return;
        }

        var worlds = Worlds();
        if (worlds.Count == 0) {
            return;
        }

        var text = new StringBuilder();
        text.AppendLine($"ULTIMUS WEEKLY PACE REPORT ({DateTime.Now:dddd d MMMM yyyy})");
        text.AppendLine("How long each world took him, next to about how long a player takes (a rough guide, not exact).");
        text.AppendLine();
        foreach (var world in worlds) {
            var took = ((world.Finished ?? DateTime.UtcNow) - world.Started).TotalHours;
            var folder = UltimusPlanner.Ladder.FirstOrDefault(w => string.Equals(WorldOf(w + "/"), world.World, StringComparison.OrdinalIgnoreCase)
                                                                   || string.Equals(w, world.World, StringComparison.OrdinalIgnoreCase)) ?? "";
            var typical = s_worldLevels.TryGetValue(folder, out var guide) ? guide.Hours : 0;
            var pace = typical <= 0 ? "" : took <= typical ? $", faster than a player's ~{typical:0} h" : $", slower than a player's ~{typical:0} h ({took / typical:0.0}x)";
            text.AppendLine($"- {world.World}: {(world.Finished is null ? "playing for" : "took")} {took:0.0} h{pace}; level {world.LevelIn}{(world.Finished is null ? "" : $" to {world.LevelOut}")}.");
        }

        text.AppendLine();
        text.AppendLine("This week: " + Summary());
        try {
            File.WriteAllText(path, text.ToString());
        }
        catch (Exception e) {
            Logger.Debug("Ultimus: could not write the pace report: {0}", Logger.Args(e.Message));
        }

        Milestone("weekly", "The weekly pace report is ready (pace-report.txt).");
    }

}
