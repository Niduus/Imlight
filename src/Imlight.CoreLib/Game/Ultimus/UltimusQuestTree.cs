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
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// The whole main story quest tree, kept in Ultimus (Niduus, 2026-11-15: "make sure Ultimus has the entire quest tree
/// noted in him"): for each of the main worlds, in order, every quest he plays there (the story, its dungeons and the
/// side quests the story needs), in the order the game's requirements allow, with what each needs first, what it opens,
/// who gives it and where, its steps (from the Quest Atlas: area, person), which quests open the next world and which one
/// ends the world. Read only from the game's own quest files, nothing guessed; built once after the Quest Atlas.
/// Written to logs/ultimus/quest-tree.json (the full record) and quest-tree.txt (by world, for Niduus).
/// </summary>
internal static class UltimusQuestTree {

    private const string DATA = "quest-tree.json", REPORT = "quest-tree.txt";

    internal sealed record StepInfo(string Goal, string Text, string Kind, string Zone, string Person);

    internal sealed record Quest(string World, int Order, string Code, string Title, string Kind, int Level, string Giver, string GiverZone,
        List<string> Needs, List<string> NeedsElsewhere, List<string> Opens, int ChoiceOf, bool Counted, bool OpensNextWorld, bool WorldEnd,
        List<StepInfo> Steps);

    private static readonly ConcurrentDictionary<string, Quest> s_quests = new(StringComparer.OrdinalIgnoreCase);

    internal static bool Ready { get; private set; }

    internal static int Count => s_quests.Count;

    /// <summary>A quest's place in the tree, or null (not built yet, or not a quest he plays).</summary>
    internal static Quest Of(string code) => string.IsNullOrEmpty(code) ? null : s_quests.GetValueOrDefault(code);

    /// <summary>A world's quests in order (zone folder, as "Marleybone").</summary>
    internal static List<Quest> World(string world)
        => [.. s_quests.Values.Where(q => string.Equals(q.World, world, StringComparison.OrdinalIgnoreCase)).OrderBy(q => q.Order)];

    /// <summary>Builds the tree from the game's quests (called once the Quest Atlas is ready).</summary>
    internal static void Build() {
        try {
            var (plans, ends, storyWorld) = BotProgress.Tree();
            var ladder = MainWorlds.Folders.Where(plans.ContainsKey).ToList();
            var played = QuestTemplateCollection.GetAllQuests().Where(q => q is not null && !string.IsNullOrEmpty(q.m_questName) && BotStory.Plays(q)).ToList();
            var built = new List<Quest>();
            for (var w = 0; w < ladder.Count; w++) {
                var world = ladder[w];
                var next = w + 1 < ladder.Count ? ladder[w + 1] : "";
                var counted = plans[world].SelectMany(g => g).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var choiceOf = plans[world].SelectMany(g => g.Select(n => (n, g.Count))).ToDictionary(p => p.n, p => p.Count, StringComparer.OrdinalIgnoreCase);
                var names = new HashSet<string>(counted, StringComparer.OrdinalIgnoreCase);
                foreach (var q in played.Where(q => string.Equals(BotProgress.WorldOf(q), world, StringComparison.OrdinalIgnoreCase))) {
                    names.Add(q.m_questName);
                }

                // The next world's first story quests: what they need from this world opens it.
                var nextFirst = storyWorld.Where(p => string.Equals(p.Value, next, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key)
                    .Where(n => QuestTemplateCollection.GetQuestByName(n) is { } q
                                && !QuestStory.ChainQuests(q.m_requirements).Any(need => storyWorld.TryGetValue(need, out var nw) && string.Equals(nw, next, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                var opensNext = nextFirst.SelectMany(n => QuestTemplateCollection.GetQuestByName(n) is { } q ? QuestStory.ChainQuests(q.m_requirements) : [])
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // In the order the requirements allow: the depth among this world's quests, then the code.
                var depth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int Depth(string name) {
                    if (depth.TryGetValue(name, out var known)) {
                        return known;
                    }

                    if (!visiting.Add(name) || QuestTemplateCollection.GetQuestByName(name) is not { } quest) {
                        return 0;
                    }

                    var d = QuestStory.ChainQuests(quest.m_requirements).Where(names.Contains).Select(Depth).DefaultIfEmpty(0).Max() + 1;
                    visiting.Remove(name);
                    return depth[name] = d;
                }

                var order = 0;
                foreach (var name in names.OrderBy(Depth).ThenBy(n => n, StringComparer.OrdinalIgnoreCase)) {
                    if (QuestTemplateCollection.GetQuestByName(name) is not { } quest) {
                        continue;
                    }

                    var needs = QuestStory.ChainQuests(quest.m_requirements).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var kind = QuestStory.KindOf(quest) switch {
                        QuestStory.STORY => "story",
                        QuestStory.INSTANCE => "dungeon",
                        _ => "needed by the story",
                    };
                    var steps = (quest.m_goals ?? []).Where(g => g is not null).Select(g => UltimusAtlas.Of(name, g.m_goalName) is { } a
                            ? new StepInfo(a.Goal, a.Text, a.Kind, a.RealZone.Length > 0 ? a.RealZone : a.StepZone, a.Person)
                            : new StepInfo(g.m_goalName, OverlayQuestTracker.StepTitle(g), g.m_goalType.ToString(), OverlayQuestTracker.StepZone(g) ?? "", ""))
                        .ToList();
                    built.Add(new Quest(world, ++order, name, QuestGuide.QuestTitle(quest), kind, quest.m_questLevel, QuestGuide.GiverName(quest),
                        QuestGuide.GiverZone(quest), [.. needs.Where(names.Contains)], [.. needs.Where(n => !names.Contains(n))],
                        [.. QuestGuide.QuestsAfter(name).Select(q => (string) q.m_questName).Where(n => names.Contains(n) || storyWorld.ContainsKey(n)).Distinct(StringComparer.OrdinalIgnoreCase)],
                        choiceOf.GetValueOrDefault(name, 1), counted.Contains(name), opensNext.Contains(name),
                        string.Equals(ends.GetValueOrDefault(world), name, StringComparison.OrdinalIgnoreCase), steps));
                }
            }

            s_quests.Clear();
            foreach (var q in built) {
                s_quests[q.Code] = q;
            }

            Ready = true;
            Write(built);
            Logger.Information("Ultimus: the Quest Tree has {0} quests in {1} main worlds ({2}).", Logger.Args(built.Count, ladder.Count, DATA));
        }
        catch (Exception ex) {
            Logger.Debug("Ultimus: the Quest Tree could not be built: {0}", Logger.Args(ex.Message));
        }
    }

    private static void Write(List<Quest> quests) {
        try {
            Directory.CreateDirectory(UltimusWire.SharedFolder);
            File.WriteAllText(Path.Combine(UltimusWire.SharedFolder, DATA), UltimusWire.Write(quests));
            File.WriteAllText(Path.Combine(UltimusWire.SharedFolder, REPORT), Report(quests, DateTime.Now));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Ultimus: {0} could not be written: {1}", Logger.Args(REPORT, ex.Message));
        }
    }

    /// <summary>The tree in plain words, world by world, in order.</summary>
    internal static string Report(IReadOnlyCollection<Quest> quests, DateTime written) {
        var text = new StringBuilder();
        var worlds = quests.GroupBy(q => q.World, StringComparer.OrdinalIgnoreCase).ToList();
        text.AppendLine($"QUEST TREE (written {written:yyyy-MM-dd HH:mm}): {quests.Count} quests in {worlds.Count} main worlds, read from the game's own quest files.");
        text.AppendLine("Each line: order, title (code), kind, level, who gives it and where. \"needs\" = done first; \"opens\" = what it starts.");
        foreach (var world in worlds) {
            var list = world.OrderBy(q => q.Order).ToList();
            var end = list.FirstOrDefault(q => q.WorldEnd);
            text.AppendLine();
            text.AppendLine($"== {BotProgress.Shown(world.Key)} (world {MainWorlds.Number(world.Key)} of {MainWorlds.All.Length}): {list.Count} quests, " +
                            $"{list.Count(q => q.Counted)} in the story count" + (end is null ? "" : $", ends with \"{end.Title}\"") + " ==");
            foreach (var q in list) {
                var tags = new List<string> { q.Kind };
                if (q.ChoiceOf > 1) {
                    tags.Add($"one of {q.ChoiceOf} choices");
                }

                if (q.OpensNextWorld) {
                    tags.Add("opens the next world");
                }

                if (q.WorldEnd) {
                    tags.Add("LAST QUEST OF THE WORLD");
                }

                text.AppendLine($"{q.Order,3}. \"{q.Title}\" ({q.Code}), {string.Join(", ", tags)}, level {q.Level}" +
                                (q.Giver.Length > 0 ? $", from {q.Giver}" + (q.GiverZone.Length > 0 ? $" in {UltimusText.Zone(q.GiverZone)}" : "") : ""));
                if (q.Needs.Count + q.NeedsElsewhere.Count > 0) {
                    text.AppendLine($"       needs: {string.Join(", ", q.Needs.Concat(q.NeedsElsewhere))}");
                }

                if (q.Opens.Count > 0) {
                    text.AppendLine($"       opens: {string.Join(", ", q.Opens)}");
                }

                foreach (var s in q.Steps) {
                    text.AppendLine($"       - {s.Text}" + (s.Zone.Length > 0 ? $" ({UltimusText.Zone(s.Zone)})" : "") + (s.Person.Length > 0 ? $" [{s.Person}]" : ""));
                }
            }
        }

        return text.ToString();
    }
}
