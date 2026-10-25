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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Madlibs;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>
/// The files behind the Quests tab of ImlightManager (Niduus, 2026-10-24: "Include quest checks into manager, allowing
/// me to see which ones I need to do and button to execute commands to start the quest. Another to tp to the exact zone
/// it's given"). quest-guide.txt in the ID lookup folder lists every quest in the order it can be played, with the
/// quests it needs and the zone of the NPC who gives it; quest-progress/&lt;wizard id&gt;.txt says which quests each wizard
/// has done and has now. Nothing is added to the game: both only read what the server already has.
/// </summary>
internal static class QuestGuide {

    internal const string LIST_NAME = "quest-guide.txt";
    private const string PROGRESS_FOLDER = "quest-progress";
    private const string COMPLETE_SUFFIX = "_Complete";
    private const string AUTO_START_TAG = "ImlightAutoStart";

    // ---------------------------------------------------------------------
    // The quest list
    // ---------------------------------------------------------------------

    /// <summary>
    /// Writes the quest list. One quest per line, tab separated: code, title, level, "main" or "side", the codes of the
    /// quests it needs (comma separated), the giver's name, the giver's zone, and where that zone came from: "npc" (a
    /// step of some quest talks to the giver there), "step" (the giver's zone is not known, so its first step's zone)
    /// or "auto" (it starts by itself when the quest before it is done). Then, for the Quests tab's filters (Niduus,
    /// 2026-10-25: "add to manager quests ... searches"): how many steps it has, the kinds of its steps (defeat, collect,
    /// talk, go to...), the steps' words, the drop tables that reward it, its marks (repeatable, hidden, outdated, pet,
    /// crafting check) and its activity (Crafting, Fishing, Gardening, Pet, Spell or none).
    /// </summary>
    internal static void WriteList(string folder) {
        s_folder = folder;
        try {
            // Loads the zone list the teleport command uses, if nothing has yet.
            _ = AccessPassManager.Zones;
            // A quest turned off or skipped in the Manager's Quest Editor is left out: nobody gets it.
            var quests = QuestTemplateCollection.GetAllQuests()
                .Where(q => q is not null && !string.IsNullOrEmpty(q.m_questName) && !Quests.QuestOptions.IsOff(q.m_questName))
                .GroupBy(q => (string) q.m_questName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            var byName = quests.ToDictionary(q => (string) q.m_questName, StringComparer.OrdinalIgnoreCase);
            var needs = quests.ToDictionary(q => (string) q.m_questName,
                q => NeededQuests(q.m_requirements).Where(byName.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);
            var personaZones = PersonaZones(quests);

            var text = new StringBuilder();
            foreach (var quest in PlayOrder(quests, needs)) {
                string name = quest.m_questName;
                var (giver, zone, from) = GiverOf(quest, personaZones);
                text.Append(Clean(name)).Append('\t')
                    .Append(Clean(TitleOf(quest))).Append('\t')
                    .Append(quest.m_questLevel.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(IsMain(name) ? "main" : "side").Append('\t')
                    .Append(string.Join(',', needs[name].Select(Clean))).Append('\t')
                    .Append(Clean(giver)).Append('\t')
                    .Append(Clean(zone)).Append('\t')
                    .Append(from).Append('\t')
                    .Append(Details(quest)).Append('\n');
            }

            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, LIST_NAME);
            var temp = path + ".tmp";
            File.WriteAllText(temp, text.ToString());
            File.Move(temp, path, overwrite: true);
            Logger.Information("Quest guide: {0} quests listed for the Manager's Quests tab.", Logger.Args(quests.Count));

            if (!s_detailsWritten) {
                s_detailsWritten = true;
                WriteDetails(folder, personaZones);
            }
        }
        catch (Exception ex) {
            Logger.Warning("Could not write the quest list for the Manager. {Exception}", Logger.Args(ex));
        }
    }

    // Its steps, rewards, marks and activity, tab separated (the columns after "from").
    private static string Details(QuestTemplate quest) {
        try {
            var goals = (quest.m_goals ?? []).Where(g => g is not null).ToList();
            var kinds = goals.Select(g => GoalKind(g.m_goalType)).Where(k => k.Length > 0).Distinct();
            var words = goals.Select(g => (string) g.m_goalTitle).Where(k => !string.IsNullOrEmpty(k))
                .Select(k => Locale.GetEnglishName(k) is { Length: > 0 } text ? text : "").Where(t => t.Length > 0);
            var rewards = (quest.m_endResults?.m_results ?? []).OfType<ResDropTable>().Select(r => (string) r.m_tableName)
                .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct();
            var marks = new List<string>();
            if (quest.m_questRepeat > 0) {
                marks.Add("repeatable");
            }

            if (quest.m_isHidden) {
                marks.Add("hidden");
            }

            if (quest.m_outdated) {
                marks.Add("outdated");
            }

            if (quest.m_playAsYourPetNPC || goals.Any(g => g.m_petOnlyQuest)) {
                marks.Add("pet");
            }

            if (quest.m_checkInventoryForCrafting) {
                marks.Add("crafting");
            }

            var activity = quest.m_activityType.ToString().Replace("ACTIVITY_", "", StringComparison.Ordinal);

            return string.Join('\t', goals.Count.ToString(CultureInfo.InvariantCulture), string.Join(',', kinds),
                Clean(string.Join(" | ", words)), string.Join(',', rewards.Select(Clean)), string.Join(',', marks),
                activity == "NotActivity" ? "" : activity);
        }
        catch (Exception) {
            return "";
        }
    }

    private static string GoalKind(GOAL_TYPE type) => type switch {
        GOAL_TYPE.GOAL_TYPE_BOUNTY => "Defeat",
        GOAL_TYPE.GOAL_TYPE_BOUNTYCOLLECT or GOAL_TYPE.GOAL_TYPE_COLLECTAFTERBOUNTY => "Defeat and collect",
        GOAL_TYPE.GOAL_TYPE_SCAVENGE or GOAL_TYPE.GOAL_TYPE_SCAVENGEFAKE => "Collect",
        GOAL_TYPE.GOAL_TYPE_PERSONA => "Talk to",
        GOAL_TYPE.GOAL_TYPE_WAYPOINT or GOAL_TYPE.GOAL_TYPE_ENCOUNTER_WAYPOINT_FOREACH => "Go to",
        GOAL_TYPE.GOAL_TYPE_ACHIEVERANK => "Reach a rank",
        GOAL_TYPE.GOAL_TYPE_USAGE => "Use",
        GOAL_TYPE.GOAL_TYPE_COMPLETEQUEST => "Finish a quest",
        GOAL_TYPE.GOAL_TYPE_SOCIARANK or GOAL_TYPE.GOAL_TYPE_SOCIACURRENCY or GOAL_TYPE.GOAL_TYPE_SOCIAMINIGAME
            or GOAL_TYPE.GOAL_TYPE_SOCIAGIVEITEM or GOAL_TYPE.GOAL_TYPE_SOCIAGETITEM => "Other",
        _ => "",
    };

    private static string s_folder;
    private static bool s_detailsWritten;

    /// <summary>Writes the quest list again (after the Manager's Quest Editor changed quests), when it was written before.</summary>
    internal static void WriteListAgain() {
        if (s_folder is { } folder) {
            WriteList(folder);
        }
    }

    // ---------------------------------------------------------------------
    // Every quest's details, for the Quest Editor
    // ---------------------------------------------------------------------

    internal const string DETAILS_NAME = "quest-details.json";

    private sealed record GoalDetails(string Name, string Kind, string Text, int Count, ulong Npc, string NpcName, string Place);

    private sealed record RewardDetails(int Xp, int GoldMin, int GoldMax, int TrainingPoints, List<string> Items);

    private sealed record QuestDetails(string Code, string Title, int Level, bool Main, ulong Giver, string GiverName,
        bool StartsByItself, List<string> Needs, List<GoalDetails> Goals, RewardDetails Rewards);

    private sealed record NpcDetails(ulong Id, string Name, string Code, string Zone);

    private sealed record DetailsFile(List<QuestDetails> Quests, List<NpcDetails> Npcs);

    /// <summary>
    /// Writes quest-details.json beside the quest list: each game quest as its files have it (before any Quest Editor
    /// change), with its steps, rewards, giver and the quests it needs, and every NPC that gives a quest or that a step
    /// talks to. The Manager's Quest Editor shows these next to the changes.
    /// </summary>
    private static void WriteDetails(string folder, Dictionary<string, string> personaZones) {
        try {
            var npcs = new Dictionary<ulong, NpcDetails>();
            NpcDetails Npc(ulong id) {
                if (id == 0) {
                    return null;
                }

                if (npcs.TryGetValue(id, out var known)) {
                    return known;
                }

                if (CoreObjectFactory.GetCoreTemplate(id) is not GameObjectTemplate npc) {
                    return npcs[id] = null;
                }

                string objectName = npc.m_objectName;
                return npcs[id] = new NpcDetails(id, NameOf(npc), objectName ?? "",
                    personaZones.GetValueOrDefault(PersonaKey(objectName)) ?? "");
            }

            var quests = new List<QuestDetails>();
            foreach (var quest in WizardData.SpiralDB.OriginalQuestTemplates
                         .Where(q => q is not null && !string.IsNullOrEmpty(q.m_questName))
                         .GroupBy(q => (string) q.m_questName, StringComparer.OrdinalIgnoreCase)
                         .Select(g => g.First())) {
                var giver = Npc(QuestMadlibs.PrepSpeakerOf(quest));
                var goals = (quest.m_goals ?? []).Where(g => g is not null).Select(goal => {
                    var npc = goal is PersonaGoalTemplate persona ? Npc(QuestMadlibs.SpeakerTemplateOf(persona.m_personaName, 0)) : null;
                    var (kind, text) = DescribeGoal(goal, npc);
                    return new GoalDetails(goal.m_goalName, kind, text, GoalCount(goal), npc?.Id ?? 0, npc?.Name ?? "",
                        English(goal.m_locationName));
                }).ToList();

                quests.Add(new QuestDetails(quest.m_questName, TitleOf(quest), quest.m_questLevel, quest.m_mainline || IsMain(quest.m_questName),
                    giver?.Id ?? 0, giver?.Name ?? "", quest.m_clientTags?.Any(t => (string) t == AUTO_START_TAG) == true,
                    [.. NeededQuests(quest.m_requirements).Distinct(StringComparer.OrdinalIgnoreCase)], goals, RewardsOf(quest)));
            }

            var file = new DetailsFile(quests, [.. npcs.Values.Where(n => n is not null).OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)]);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, DETAILS_NAME);
            File.WriteAllText(path + ".tmp", System.Text.Json.JsonSerializer.Serialize(file));
            File.Move(path + ".tmp", path, overwrite: true);
            Logger.Information("Quest guide: {0} quests and {1} NPCs written for the Manager's Quest Editor.",
                Logger.Args(quests.Count, file.Npcs.Count));
        }
        catch (Exception ex) {
            Logger.Warning("Could not write the quest details for the Manager. {Exception}", Logger.Args(ex));
        }
    }

    private static string NameOf(GameObjectTemplate npc) {
        string displayKey = npc.m_displayName;
        var name = string.IsNullOrEmpty(displayKey) ? "" : Locale.GetEnglishName(displayKey);

        return string.IsNullOrEmpty(name) ? (string) npc.m_objectName ?? "" : name;
    }

    // A lang key's English words, or the text itself when it is no key (our own quests are written in English).
    private static string English(string keyOrText) {
        if (string.IsNullOrEmpty(keyOrText)) {
            return "";
        }

        var english = keyOrText.Contains(' ') ? null : Locale.GetEnglishName(keyOrText);
        var text = string.IsNullOrEmpty(english) ? keyOrText : english;

        return text.Replace('|', ',').Trim();
    }

    private static int GoalCount(GoalTemplate goal) => goal switch {
        BountyGoalTemplate { m_bountyTotal: > 0 } bounty => bounty.m_bountyTotal,
        ScavengeGoalTemplate { m_itemTotal: > 0 } scavenge => scavenge.m_itemTotal,
        _ => goal.m_tallyCounter?.m_count ?? 0,
    };

    private static (string Kind, string Text) DescribeGoal(GoalTemplate goal, NpcDetails npc) {
        var count = GoalCount(goal);
        var what = English(goal.m_tallyCounter?.m_descriptor);
        return goal switch {
            PersonaGoalTemplate persona => ("Talk", $"Talk to {(npc is null ? (string) persona.m_personaName : npc.Name)}"),
            BountyGoalTemplate => ("Defeat", $"Defeat {Math.Max(count, 1)} {(what.Length > 0 ? what : "creatures")}"),
            ScavengeGoalTemplate => ("Collect", $"Collect {Math.Max(count, 1)} {(what.Length > 0 ? what : "items")}"),
            WaypointGoalTemplate waypoint => ("Go", $"Go to {(English(goal.m_locationName) is { Length: > 0 } place ? place : (string) waypoint.m_zoneTag)}"),
            _ when goal.m_goalType == GOAL_TYPE.GOAL_TYPE_BOUNTYCOLLECT => ("Collect", $"Collect {Math.Max(count, 1)} {(what.Length > 0 ? what : "items")} from battles"),
            _ when goal.m_goalType == GOAL_TYPE.GOAL_TYPE_USAGE => ("Use", $"Use {(what.Length > 0 ? what : "an object")}{(count > 1 ? $" {count} times" : "")}"),
            _ => ("Other", English(goal.m_goalTitle) is { Length: > 0 } title ? title : goal.m_goalType.ToString()),
        };
    }

    private static RewardDetails RewardsOf(QuestTemplate quest) {
        int xp = 0, goldMin = 0, goldMax = 0, tp = 0;
        var items = new List<string>();
        foreach (var name in (quest.m_endResults?.m_results ?? []).OfType<ResDropTable>().Select(r => (string) r.m_tableName)) {
            if (string.IsNullOrEmpty(name) || DropTables.DropTableFiles.Get(name) is not { } table) {
                continue;
            }

            xp += table.ExperienceAmount;
            goldMin += table.MinGold;
            goldMax += table.MaxGold;
            tp += table.TrainingPoints;
            foreach (var item in table.Items ?? []) {
                if (ulong.TryParse(item.ItemId, out var id) && CoreObjectFactory.GetCoreTemplate(id) is GameObjectTemplate template) {
                    string key = template.m_displayName;
                    var itemName = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);
                    items.Add(string.IsNullOrEmpty(itemName) ? (string) template.m_objectName ?? item.ItemId : itemName);
                }
                else if (!string.IsNullOrEmpty(item.ItemName)) {
                    items.Add(item.ItemName);
                }
            }
        }

        return new RewardDetails(xp, goldMin, goldMax, tp, [.. items.Distinct()]);
    }

    private static string Clean(string text) => (text ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();

    private static bool IsMain(string name) => name.Contains("MAIN", StringComparison.OrdinalIgnoreCase);

    private static string TitleOf(QuestTemplate quest) {
        string key = quest.m_questTitle;
        var title = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);

        return string.IsNullOrEmpty(title) ? (string) quest.m_questName : title;
    }

    // The quests that must be done first: the "has this quest's entry" requirements that must be true, at any depth.
    private static IEnumerable<string> NeededQuests(Requirement requirement) {
        switch (requirement) {
            case ReqHasEntry entry when !entry.m_applyNOT && entry.m_isQuestRegistry && !string.IsNullOrEmpty(entry.m_questName):
                yield return entry.m_questName;
                break;
            case RequirementList list when !list.m_applyNOT:
                // "Any one of these" lists are left out: none of them alone is needed.
                var inners = list.m_requirements ?? [];
                if (inners.Take(inners.Count - 1).Any(r => r?.m_operator == Operator.ROP_OR)) {
                    break;
                }

                foreach (var inner in inners) {
                    foreach (var found in NeededQuests(inner)) {
                        yield return found;
                    }
                }
                break;
        }
    }

    // Each quest after the ones it needs; among quests that are free at the same time, by code, so a world's story keeps together.
    private static List<QuestTemplate> PlayOrder(List<QuestTemplate> quests, Dictionary<string, List<string>> needs) {
        var waiting = needs.ToDictionary(p => p.Key, p => p.Value.Count, StringComparer.OrdinalIgnoreCase);
        var unlocks = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, needed) in needs) {
            foreach (var earlier in needed) {
                if (!unlocks.TryGetValue(earlier, out var list)) {
                    unlocks[earlier] = list = [];
                }

                list.Add(name);
            }
        }

        var byName = quests.ToDictionary(q => (string) q.m_questName, StringComparer.OrdinalIgnoreCase);
        var ready = new SortedSet<string>(waiting.Where(p => p.Value == 0).Select(p => p.Key), StringComparer.OrdinalIgnoreCase);
        var order = new List<QuestTemplate>(quests.Count);
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (ready.Count > 0) {
            var next = ready.Min;
            ready.Remove(next);
            placed.Add(next);
            order.Add(byName[next]);
            foreach (var later in unlocks.GetValueOrDefault(next) ?? []) {
                if (--waiting[later] == 0) {
                    ready.Add(later);
                }
            }
        }

        // Quests that need each other in a loop go last, by code.
        order.AddRange(quests.Where(q => !placed.Contains(q.m_questName)).OrderBy(q => (string) q.m_questName, StringComparer.OrdinalIgnoreCase));

        return order;
    }

    private static string PersonaKey(string name) {
        name = (name ?? "").Trim();
        if (name.EndsWith("_Persona", StringComparison.OrdinalIgnoreCase)) {
            name = name[..^"_Persona".Length];
        }

        return name.ToLowerInvariant();
    }

    // Where each NPC stands, from the "talk to" steps of every quest: the zone named most often for them.
    private static Dictionary<string, string> PersonaZones(List<QuestTemplate> quests)
        => quests
            .SelectMany(q => q.m_goals ?? [])
            .OfType<PersonaGoalTemplate>()
            .Select(g => (Persona: PersonaKey(g.m_personaName), Zone: TeleportZone(g.m_destinationZone)))
            .Where(p => p.Persona.Length > 0 && p.Zone.Length > 0)
            .GroupBy(p => p.Persona)
            .ToDictionary(g => g.Key, g => g.GroupBy(p => p.Zone, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(z => z.Count()).ThenBy(z => z.Key, StringComparer.OrdinalIgnoreCase).First().Key);

    /// <summary>The zone as the teleport command takes it, or "" when the server can't teleport there.</summary>
    private static string TeleportZone(string zone) {
        zone = (zone ?? "").Trim();
        if (zone.Length == 0) {
            return "";
        }

        return AccessPassManager.GetContainedZoneName(zone) ?? "";
    }

    private static (string Giver, string Zone, string From) GiverOf(QuestTemplate quest, Dictionary<string, string> personaZones) {
        var templateId = QuestMadlibs.GiverTemplateOf(quest);
        var npc = templateId == 0 ? null : CoreObjectFactory.GetCoreTemplate(templateId) as GameObjectTemplate;

        var giver = "";
        if (npc is not null) {
            string displayKey = npc.m_displayName;
            giver = string.IsNullOrEmpty(displayKey) ? "" : Locale.GetEnglishName(displayKey);
            if (string.IsNullOrEmpty(giver)) {
                giver = npc.m_objectName;
            }

            if (personaZones.TryGetValue(PersonaKey(npc.m_objectName), out var zone)) {
                return (giver, zone, "npc");
            }
        }

        // No NPC is known to stand anywhere: where its first step happens.
        var firstGoal = quest.m_goals?.FirstOrDefault();
        var stepZone = TeleportZone(firstGoal is WaypointGoalTemplate waypoint && !string.IsNullOrEmpty(waypoint.m_zoneTag)
            ? waypoint.m_zoneTag
            : firstGoal?.m_destinationZone);
        var auto = quest.m_clientTags?.Any(t => (string) t == AUTO_START_TAG) == true;

        return (giver, stepZone, auto && npc is null ? "auto" : "step");
    }

    // ---------------------------------------------------------------------
    // Each wizard's quests
    // ---------------------------------------------------------------------

    private sealed record Progress(string Wizard, string Zone, bool OnThisPc, string[] Done, string[] Now);

    private static readonly ConcurrentDictionary<ulong, Progress> s_pending = new();
    private static Timer s_timer;

    /// <summary>
    /// Notes a wizard's quests for the Manager. Called on the wizard's own session when a zone loads and when a quest
    /// starts, ends or is removed; the file is written a moment later, off the game's thread.
    /// </summary>
    internal static void Update(Wizard wizard, bool onThisPc) {
        try {
            if (wizard?.QuestBehavior is not { } quests) {
                return;
            }

            var done = quests.Registry
                .Where(p => p.Value > 0 && p.Key.EndsWith(COMPLETE_SUFFIX, StringComparison.Ordinal))
                .Select(p => p.Key[..^COMPLETE_SUFFIX.Length])
                .ToArray();
            var now = quests.CurrentQuestInstances.Where(q => q is not null).Select(q => q.QuestName).ToArray();
            s_pending[wizard.CharId] = new Progress(wizard.PlayerNameBehavior?.GetWizardName() ?? "", wizard.Zone ?? "", onThisPc, done, now);
            if (s_timer is null) {
                Interlocked.CompareExchange(ref s_timer, new Timer(_ => WritePending(), null, 1000, 1000), null);
            }
        }
        catch (Exception ex) {
            Logger.Debug("Could not note quest progress for the Manager: {0}", Logger.Args(ex.Message));
        }
    }

    private static void WritePending() {
        foreach (var charId in s_pending.Keys) {
            if (!s_pending.TryRemove(charId, out var progress)) {
                continue;
            }

            try {
                // Key and value per line: name, zone, pc (local or remote), updated, then a done line per finished quest and
                // a now line per quest held.
                var text = new StringBuilder()
                    .Append("name\t").Append(Clean(progress.Wizard)).Append('\n')
                    .Append("zone\t").Append(Clean(progress.Zone)).Append('\n')
                    .Append("pc\t").Append(progress.OnThisPc ? "local" : "remote").Append('\n')
                    .Append("updated\t").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
                foreach (var quest in progress.Done) {
                    text.Append("done\t").Append(Clean(quest)).Append('\n');
                }

                foreach (var quest in progress.Now) {
                    text.Append("now\t").Append(Clean(quest)).Append('\n');
                }

                var folder = Path.Combine(Directory.GetCurrentDirectory(), PROGRESS_FOLDER);
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, charId.ToString(CultureInfo.InvariantCulture) + ".txt");
                var temp = path + ".tmp";
                File.WriteAllText(temp, text.ToString());
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) {
                Logger.Debug("Could not write quest progress for the Manager: {0}", Logger.Args(ex.Message));
            }
        }
    }

}
