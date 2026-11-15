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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Madlibs;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// The Quest Atlas (Niduus, 2026-11-15, "all" of the robustness pitches, "only accurate data ... NO EXCEPTIONS"): every
/// step of every main story quest, worked out once from the game's own files before he gets there, instead of being found
/// out by getting stuck. For each step: the area the quest names, the area the step is really done in, the person of a
/// "talk to" step and every area the zone files place them, and how many doors lead into that area. A "talk to" step whose
/// person the zone files place in one other story area only is done there (his log, 2026-11-15: "Out of Control" names
/// The Ironworks while Ms. Marphely stands only in Chelsea Court, and he rode between The Ironworks and Digmoore Station for
/// over half an hour). Written to logs/ultimus/quest-atlas.json (the raw record later bots are built from) and
/// quest-atlas.txt (the steps the Atlas corrects or can't place, for Niduus). Read-only: nothing in the game's files changes.
/// Off switch: playbook Rules.Atlas (2 on, 1 watch only, 0 off).
/// </summary>
internal static class UltimusAtlas {

    internal const string RULE = "Atlas";
    private const string DATA = "quest-atlas.json", REPORT = "quest-atlas.txt";

    internal sealed record Step(string Quest, string Title, string Goal, string Text, string Kind, string World, string StepZone, string RealZone,
        string Person, ulong PersonTemplate, List<string> PersonZones, int DoorsIn, string Why);

    private static readonly ConcurrentDictionary<string, Step> s_steps = new(StringComparer.OrdinalIgnoreCase);
    private static int s_building;

    internal static bool Ready { get; private set; }

    internal static int Count => s_steps.Count;

    /// <summary>The Atlas's record of a step, or null (not built yet, or not a main story step).</summary>
    internal static Step Of(string quest, string goal) => s_steps.TryGetValue(quest + "|" + goal, out var step) ? step : null;

    /// <summary>
    /// Where the person of a "talk to" step really stands, when the zone files place them only in one other story area than
    /// the one the quest names, and the quest's own area was read and does not hold them; "" otherwise. Pure, so it can be checked.
    /// </summary>
    internal static (string Zone, string Why) RealZone(string stepZone, string person, IReadOnlyCollection<string> personZones, bool stepZoneRead = true) {
        // An area the zone files were not read for says nothing about who stands there: the quest's own area is kept.
        if (stepZone.Length > 0 && !stepZoneRead) {
            return ("", "");
        }

        var story = personZones.Where(z => !string.IsNullOrWhiteSpace(z)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (story.Count == 0) {
            return ("", "");
        }

        if (stepZone.Length > 0 && story.Any(z => Same(z, stepZone))) {
            return ("", "");
        }

        if (story.Count == 1) {
            return (story[0], stepZone.Length > 0
                ? $"the quest names {Shown(stepZone)}, but the game's zone files place {person} only in {Shown(story[0])}"
                : $"the quest names no area, and the game's zone files place {person} only in {Shown(story[0])}");
        }

        return ("", "");
    }

    /// <summary>Builds the Atlas once, in the background, after the zone transfer book and the NPC location book are ready.</summary>
    internal static void BuildInBackground() {
        if (UltimusRules.Mode(RULE) == UltimusRules.OFF || Interlocked.Exchange(ref s_building, 1) == 1) {
            return;
        }

        _ = Task.Run(async () => {
            try {
                // The two books it is made from (each built in the background at the start): up to 20 minutes.
                for (var i = 0; i < 240 && (!UltimusNpcBook.Ready || UltimusTransfers.Count().Areas == 0); i++) {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }

                var doorsIn = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var (_, list) in UltimusTransfers.All()) {
                    foreach (var to in list.Select(t => t.To).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase)) {
                        doorsIn[to] = doorsIn.GetValueOrDefault(to) + 1;
                    }
                }

                var quests = QuestTemplateCollection.GetAllQuests().Where(q => q is not null && BotShared.BotStory.Plays(q)).ToList();
                foreach (var quest in quests) {
                    foreach (var goal in quest.m_goals?.Where(g => g is not null) ?? []) {
                        if (Read(quest, goal, doorsIn) is { } step && BotShared.MainWorlds.QuestsIn(step.World)) {
                            s_steps[step.Quest + "|" + step.Goal] = step;
                        }
                    }

                    await Task.Yield();
                }

                Ready = true;
                Write();
                Logger.Information("Ultimus: the Quest Atlas has {0} story steps ({1}).", Logger.Args(s_steps.Count, DATA));
            }
            catch (Exception ex) {
                Logger.Debug("Ultimus: the Quest Atlas could not be built: {0}", Logger.Args(ex.Message));
                Interlocked.Exchange(ref s_building, 0);
            }
        });
    }

    private static Step Read(QuestTemplate quest, GoalTemplate goal, Dictionary<string, int> doorsIn) {
        string questName = quest.m_questName;
        string goalName = goal.m_goalName;
        var stepZone = OverlayQuestTracker.StepZone(goal) ?? "";
        var person = "";
        ulong template = 0;
        var personZones = new List<string>();
        if (goal is PersonaGoalTemplate talk) {
            person = talk.m_personaName;
            template = QuestMadlibs.SpeakerTemplateOf(talk.m_personaName, 0);
            personZones = [.. UltimusNpcBook.Of(template).Select(p => p.Zone).Where(OverlayQuestRoute.OnStory).Distinct(StringComparer.OrdinalIgnoreCase)];
            if (UltimusNpcBook.Find(person) is { } named && template != 0 && UltimusNpcBook.Of(template).Count == 0) {
                personZones = [.. named.Places.Select(p => p.Zone).Where(OverlayQuestRoute.OnStory).Distinct(StringComparer.OrdinalIgnoreCase)];
            }
        }

        var (real, why) = RealZone(stepZone, person, personZones, stepZone.Length == 0 || UltimusNpcBook.Knows(stepZone));
        var world = (real.Length > 0 ? real : stepZone.Length > 0 ? stepZone : QuestGuide.GiverZone(quest)).Split('/')[0];
        var target = real.Length > 0 ? real : stepZone;

        return new Step(questName, QuestGuide.QuestTitle(quest), goalName, OverlayQuestTracker.StepTitle(goal), goal.m_goalType.ToString(), world,
            stepZone, real, person, template, personZones, target.Length == 0 ? -1 : doorsIn.GetValueOrDefault(target), why);
    }

    private static void Write() {
        try {
            Directory.CreateDirectory(UltimusWire.Folder);
            var steps = s_steps.Values.OrderBy(s => s.World, StringComparer.Ordinal).ThenBy(s => s.Quest, StringComparer.Ordinal).ThenBy(s => s.Goal, StringComparer.Ordinal).ToList();
            File.WriteAllText(Path.Combine(UltimusWire.Folder, DATA), UltimusWire.Write(steps));
            File.WriteAllText(Path.Combine(UltimusWire.Folder, REPORT), Report(steps, DateTime.Now));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Ultimus: {0} could not be written: {1}", Logger.Args(REPORT, ex.Message));
        }
    }

    internal static string Report(IReadOnlyCollection<Step> steps, DateTime written) {
        var text = new StringBuilder();
        text.AppendLine($"QUEST ATLAS (written {written:yyyy-MM-dd HH:mm}): {steps.Count} main story steps, read from the game's own files.");
        var moved = steps.Where(s => s.RealZone.Length > 0).ToList();
        var noDoor = steps.Where(s => s.DoorsIn == 0).ToList();
        var unplaced = steps.Where(s => s.PersonTemplate != 0 && s.PersonZones.Count == 0).ToList();
        text.AppendLine($"{moved.Count} step(s) are done in another area than the quest names, {noDoor.Count} step area(s) have no door in by the zone files, " +
                        $"{unplaced.Count} \"talk to\" person(s) are not placed in any story area.");
        void List(string title, IEnumerable<Step> list, Func<Step, string> line) {
            text.AppendLine();
            text.AppendLine(title);
            foreach (var s in list) {
                text.AppendLine($"  {MainWorlds(s.World)}: \"{s.Title}\", \"{s.Text}\": {line(s)}");
            }
        }

        List("DONE IN ANOTHER AREA (he goes there at once)", moved, s => s.Why);
        List("NO DOOR IN BY THE ZONE FILES (reached some other way, or needs a look)", noDoor, s => UltimusText.Zone(s.RealZone.Length > 0 ? s.RealZone : s.StepZone));
        List("PERSON NOT PLACED IN ANY STORY AREA (needs a look)", unplaced, s => s.Person);

        return text.ToString();
    }

    // The same area: the same path, else one holds the other (the game's area list; not loaded in the replay checks).
    private static bool Same(string a, string b) {
        if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        try {
            return OverlayQuestTracker.SameZone(a, b) || OverlayQuestTracker.SameZone(b, a);
        }
        catch (Exception) {
            return false;
        }
    }

    // The area's shown name; its path when the names are not loaded (the replay checks run without the game's files).
    private static string Shown(string zone) {
        try {
            return UltimusText.Zone(zone);
        }
        catch (Exception) {
            return zone;
        }
    }

    private static string MainWorlds(string folder) => BotShared.MainWorlds.Name(folder);
}
