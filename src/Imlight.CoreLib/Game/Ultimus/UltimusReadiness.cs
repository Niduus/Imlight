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
using System.Text;
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// The whole-spiral readiness check (Niduus, 2026-11-16: "he scans every main-world quest ahead for missing data or likely
/// stuck spots before he reaches them"). Every quest of the main story tree he has not done yet (UltimusQuestTree, read from
/// the game's quest files), in every main world, is looked over with what the server knows:
/// <list type="bullet">
/// <item>MISSING DATA: a quest with no steps read, a step the game names no area for, a "talk to" person the zone files
/// place in no story area, a step area whose people were never read;</item>
/// <item>LIKELY STUCK: a step area with no door in by the zone files, a step the server's quest check says may not work,
/// a puzzle not solved before, a quest he got stuck on before.</item>
/// </list>
/// Written to logs/ultimus/readiness.txt (Ahziael's Ultimus Issues page shows it), at the start once the books are built,
/// every 6 hours, and when Ahziael's "Check now" asks (readiness-request.txt). Nothing is skipped or changed by it: it only
/// says, ahead of time, where data is missing or he will likely need help.
/// </summary>
internal static class UltimusReadiness {

    internal const string FILE = "readiness.txt", REQUEST = "readiness-request.txt";

    internal sealed record Finding(string World, string Quest, string Title, bool Missing, string What);

    internal sealed record Result(int Quests, int Worlds, int Ready, List<Finding> Findings, Dictionary<string, int> Gates);

    /// <summary>Checks every quest ahead (those <paramref name="done"/> says are not done yet). Null while the books are not built.</summary>
    internal static Result Check(Func<string, bool> done) {
        if (!UltimusQuestTree.Ready || !UltimusAtlas.Ready) {
            return null;
        }

        Dictionary<string, List<string>> checkProblems = [];
        try {
            if (File.Exists(UltimusDirectorActor.QuestCheckPath)) {
                checkProblems = (UltimusDirectorActor.ReadAheadProblems(null) ?? [])
                    .GroupBy(p => p.Quest, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.SelectMany(p => p.Problems).ToList(), StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception) {
            // The quest check is one source among several.
        }

        var stuckBefore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in UltimusJournal.Recent(1000).Where(e => e.Kind == UltimusJournal.STUCK && e.About.Length > 0)) {
            stuckBefore.Add(entry.About);
        }

        var findings = new List<Finding>();
        var gates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var quests = 0;
        var worlds = 0;
        var ready = 0;
        foreach (var world in MainWorlds.Questable) {
            var ahead = UltimusQuestTree.World(world).Where(q => !done(q.Code)).ToList();
            if (ahead.Count == 0) {
                continue;
            }

            worlds++;
            quests += ahead.Count;
            var (gateCount, _) = BotGates.InWorld(world);
            if (gateCount > 0) {
                gates[world] = gateCount;
            }

            List<(string Quest, string Kind, bool Known)> puzzles = [];
            try {
                puzzles = BotPuzzle.Ahead(ahead.Select(q => QuestTemplateCollection.GetQuestByName(q.Code)).Where(t => t is not null));
            }
            catch (Exception) {
                // Puzzles are one source among several.
            }

            foreach (var quest in ahead) {
                var before = findings.Count;
                void Add(bool missing, string what) => findings.Add(new Finding(world, quest.Code, quest.Title, missing, what));

                var steps = UltimusAtlas.OfQuest(quest.Code);
                if (quest.Steps.Count == 0 && steps.Count == 0) {
                    Add(true, "no steps could be read from its quest file");
                }

                var template = QuestTemplateCollection.GetQuestByName(quest.Code);
                foreach (var step in steps) {
                    var zone = step.RealZone.Length > 0 ? step.RealZone : step.StepZone;
                    // No area in the quest file: the game's quest arrow, or the place the step names in words, is where he goes
                    // (the planner uses both), so only a step with none of them lacks an area.
                    var goal = template?.m_goals?.FirstOrDefault(g => g is not null && string.Equals((string) g.m_goalName, step.Goal, StringComparison.Ordinal));
                    if (zone.Length == 0 && goal is not null) {
                        zone = QuestArrowGoals.GetSpots(template, goal).Select(p => p.Zone).FirstOrDefault(z => !string.IsNullOrEmpty(z))
                               ?? Tools.OverlayQuestTracker.PlaceZone(goal);
                    }

                    if (zone.Length == 0 && step.PersonZones.Count == 0) {
                        Add(true, $"\"{step.Text}\": the game names no area for this step");
                    }

                    if (step.PersonTemplate != 0 && step.PersonZones.Count == 0) {
                        Add(true, $"\"{step.Text}\": {step.Person} is placed in no story area by the zone files");
                    }

                    // The area's own file was never read (it holds the people and things the step needs). A dungeon with
                    // no story person in it was flagged here before, though its file was read.
                    if (zone.Length > 0 && UltimusNpcBook.Ready && !UltimusNpcBook.Knows(zone) && !HasZoneFile(zone)) {
                        Add(true, $"\"{step.Text}\": the people of {UltimusText.Zone(zone)} were never read (its zone file may be missing)");
                    }

                    if (step.DoorsIn == 0) {
                        Add(false, $"\"{step.Text}\": {UltimusText.Zone(zone)} has no door in by the zone files (reached some other way, or needs a look)");
                    }
                }

                if (checkProblems.TryGetValue(quest.Code, out var problems) && problems.Count > 0) {
                    Add(false, $"the quest check says: {string.Join(" ", problems.Take(2))}");
                }

                foreach (var puzzle in puzzles.Where(p => string.Equals(p.Quest, quest.Code, StringComparison.OrdinalIgnoreCase) && !p.Known)) {
                    Add(false, $"a puzzle ({puzzle.Kind}) he has not solved before");
                }

                if (stuckBefore.Contains(quest.Code) || stuckBefore.Contains(quest.Title)) {
                    Add(false, "he got stuck on this quest before");
                }

                if (findings.Count == before) {
                    ready++;
                }
            }
        }

        return new Result(quests, worlds, ready, findings, gates);
    }

    private static HashSet<string> s_zoneFiles;

    /// <summary>Whether the server has the area's own file (its people, objects and doors).</summary>
    private static bool HasZoneFile(string zone) {
        try {
            s_zoneFiles ??= new HashSet<string>(SpiralDB.GetAllZoneData().Select(z => z?.ZoneName).Where(z => !string.IsNullOrWhiteSpace(z)),
                StringComparer.OrdinalIgnoreCase);
            return s_zoneFiles.Contains(zone.Trim());
        }
        catch (Exception) {
            return false;
        }
    }

    /// <summary>The result in plain words, world by world, for readiness.txt.</summary>
    internal static string Report(Result result, DateTime written) {
        var text = new StringBuilder();
        var missing = result.Findings.Where(f => f.Missing).Select(f => f.Quest).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var stuck = result.Findings.Where(f => !f.Missing).Select(f => f.Quest).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        text.AppendLine($"SPIRAL READINESS (checked {written:yyyy-MM-dd HH:mm})");
        text.AppendLine($"{result.Quests} main story quest(s) ahead of Ultimus in {result.Worlds} world(s). {result.Ready} look ready. " +
                        $"{missing} have missing data, {stuck} have a likely stuck spot.");
        text.AppendLine("MISSING DATA = something the server can't read yet. LIKELY STUCK = a spot where he will probably need a look. " +
                        "He still plays every quest as a player would; nothing is skipped.");
        foreach (var world in result.Findings.GroupBy(f => f.World, StringComparer.OrdinalIgnoreCase)) {
            text.AppendLine();
            text.AppendLine($"== {MainWorlds.Name(world.Key)} ==" + (result.Gates.TryGetValue(world.Key, out var g) ? $" ({g} locked way(s) that open with a quest step; see Locked ways ahead)" : ""));
            foreach (var quest in world.GroupBy(f => f.Quest, StringComparer.OrdinalIgnoreCase)) {
                text.AppendLine($"  \"{quest.First().Title}\" ({quest.Key})");
                foreach (var f in quest.OrderByDescending(f => f.Missing)) {
                    text.AppendLine($"    {(f.Missing ? "MISSING DATA" : "LIKELY STUCK")}: {f.What}");
                }
            }
        }

        if (result.Findings.Count == 0) {
            text.AppendLine();
            text.AppendLine("Nothing found: every quest ahead looks ready.");
        }

        return text.ToString();
    }

    internal static void Write(string report) {
        try {
            Directory.CreateDirectory(UltimusWire.SharedFolder);
            File.WriteAllText(Path.Combine(UltimusWire.SharedFolder, FILE + ".tmp"), report);
            File.Move(Path.Combine(UltimusWire.SharedFolder, FILE + ".tmp"), Path.Combine(UltimusWire.SharedFolder, FILE), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Ultimus: {0} could not be written: {1}", Logger.Args(FILE, ex.Message));
        }
    }

}

internal sealed partial class UltimusDirectorActor {

    private DateTime _readinessAt = DateTime.MinValue;
    private DateTime _readinessLook = DateTime.MinValue;

    /// <summary>Each tick: the readiness check at the start (once the books are built), every 6 hours, and when Ahziael asks.</summary>
    private void ReadinessTick() {
        // One spiral-wide check is enough: Ultimus makes it, the TCO7 read it.
        if (!_seat.IsUltimus || DateTime.UtcNow < _readinessLook) {
            return;
        }

        _readinessLook = DateTime.UtcNow.AddSeconds(15);
        var request = Path.Combine(UltimusWire.SharedFolder, UltimusReadiness.REQUEST);
        var asked = File.Exists(request);
        if (!asked && DateTime.UtcNow - _readinessAt < TimeSpan.FromHours(6)) {
            return;
        }

        var leader = _team.FirstOrDefault()?.Wizard;
        var result = UltimusReadiness.Check(q => leader?.HasCompletedQuest(q) == true);
        if (result is null) {
            return;
        }

        if (asked) {
            try {
                File.Delete(request);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // Asked again next look.
            }
        }

        _readinessAt = DateTime.UtcNow;
        UltimusReadiness.Write(UltimusReadiness.Report(result, DateTime.Now));
        var missing = result.Findings.Where(f => f.Missing).Select(f => f.Quest).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var stuck = result.Findings.Where(f => !f.Missing).Select(f => f.Quest).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Note("Ultimus", $"READINESS: {result.Quests} quest(s) ahead in {result.Worlds} world(s): {result.Ready} ready, {missing} with missing data, " +
                        $"{stuck} with a likely stuck spot ({UltimusReadiness.FILE}).", missing + stuck > 0 ? UltimusLink.NOTABLE : null);
        Think($"I looked over every main story quest still ahead of me ({result.Quests} in {result.Worlds} worlds): {result.Ready} look ready, " +
              $"{missing} are missing data and {stuck} have a spot where I'll likely need a look. I wrote it all down for Niduus, and I'll still " +
              "try every one as a player would.", "readiness|" + DateTime.Now.ToString("yyyyMMddHH", System.Globalization.CultureInfo.InvariantCulture));
    }

}
