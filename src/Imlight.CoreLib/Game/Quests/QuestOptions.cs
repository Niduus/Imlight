/*
 * Quest changes (Niduus, 2026-10-25: "add custom options to quests (In detail)"). Made in the Manager (Create page,
 * Quest Editor tab) and saved as spiraldb-extra/QuestOptions.json; read again within two seconds of a save, so a change
 * counts with no restart. The game's files are never touched: each changed quest is a copy of the server's own quest
 * with the changes put on it, and the server uses the copy in its place (SpiralDB.UseQuestTemplates).
 *
 * The file: { "Quests": [ change, ... ] }. A change:
 *   Quest           the quest's code (WC-MAIN-C01-011). With CopyOf it is the code of a NEW quest.
 *   CopyOf          a custom quest: a copy of this game quest under the new code, with the changes below.
 *   Off             never offered or started by itself (a wizard who has it keeps it).
 *   Skip            counts as done for every quest, door and NPC that waits for it, and is never offered.
 *   Repeatable      offered again after it is done.
 *   Level           the wizard's level it needs (and the level the quest book shows).
 *   Needs           quests that must be done first; NeedsOnly true: only these (the game's own requirements are dropped).
 *   Giver           template ID of the NPC who offers it; StartsByItself true: no NPC, it starts as soon as it may.
 *   Mainline        true/false: marked as main story or not.
 *   KeepGameRewards false: the game's gold, experience, training points and items are not given (spells still are).
 *   Xp, Gold, TrainingPoints, Items [ { "Id": template ID, "Count" } ]: given on top, when the quest is done.
 *   Goals [ { "Goal": goal name, "Count": how many to defeat or collect, "Npc": template ID of the NPC to talk to,
 *             "Skip": done as soon as it starts, "AnyCreature": any won battle counts for a defeat step } ].
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Madlibs;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Quests;

internal static class QuestOptions {

    internal sealed class QuestOptionFile {
        public List<QuestChange> Quests { get; set; } = [];
    }

    internal sealed class QuestChange {
        public string Quest { get; set; } = "";
        public string CopyOf { get; set; } = "";
        public bool Off { get; set; }
        public bool Skip { get; set; }
        public bool Repeatable { get; set; }
        public int? Level { get; set; }
        public List<string> Needs { get; set; } = [];
        public bool NeedsOnly { get; set; }
        public ulong Giver { get; set; }
        public bool StartsByItself { get; set; }
        public bool? Mainline { get; set; }
        public bool KeepGameRewards { get; set; } = true;
        public int Xp { get; set; }
        public int Gold { get; set; }
        public int TrainingPoints { get; set; }
        public List<RewardItem> Items { get; set; } = [];
        public List<GoalChange> Goals { get; set; } = [];

        internal bool HasOwnRewards => Xp > 0 || Gold > 0 || TrainingPoints > 0 || Items.Any(i => i.Id != 0 && i.Count > 0);
    }

    internal sealed class RewardItem {
        public ulong Id { get; set; }
        public int Count { get; set; } = 1;
    }

    internal sealed class GoalChange {
        public string Goal { get; set; } = "";
        public int? Count { get; set; }
        public ulong Npc { get; set; }
        public bool Skip { get; set; }
        public bool AnyCreature { get; set; }
    }

    internal const string FILE = "QuestOptions.json";
    internal const string REWARD_TABLE_PREFIX = "ImlightQuestReward_";
    private const string AUTO_START_TAG = "ImlightAutoStart";
    private const string PERSONA_SUFFIX = "_Persona";

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly object s_lock = new();
    private static Dictionary<string, QuestChange> s_changes = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime s_fileTime = DateTime.MinValue;
    private static Timer s_timer;
    private static int s_version;

    private static string FilePath => Path.Combine(SpiralDB.ExtraPath, FILE);

    /// <summary>Goes up each time the changes are put in use, so what keeps quests of its own knows to look again.</summary>
    internal static int Version => Volatile.Read(ref s_version);

    /// <summary>The quest changes now in use, by quest code.</summary>
    internal static IReadOnlyDictionary<string, QuestChange> Changes => s_changes;

    private static QuestChange ChangeOf(string questName)
        => questName is not null && s_changes.TryGetValue(questName, out var change) ? change : null;

    /// <summary>Never offered nor started by itself: turned off, or skipped (a skipped quest counts as done instead).</summary>
    internal static bool IsOff(string questName) => ChangeOf(questName) is { } c && (c.Off || c.Skip);

    /// <summary>Counts as done for everything that waits for it (ReqHasEntryHandler).</summary>
    internal static bool IsSkipped(string questName) => ChangeOf(questName)?.Skip == true;

    internal static bool IsRepeatable(string questName) => ChangeOf(questName)?.Repeatable == true;

    /// <summary>The NPC the Manager made the quest's giver, or 0.</summary>
    internal static ulong GiverOf(string questName) => ChangeOf(questName) is { StartsByItself: false, Giver: > 0 } c ? c.Giver : 0;

    internal static bool StartsByItself(string questName) => ChangeOf(questName)?.StartsByItself == true;

    private static GoalChange GoalChangeOf(string questName, string goalName)
        => ChangeOf(questName)?.Goals?.FirstOrDefault(g => string.Equals(g?.Goal, goalName, StringComparison.Ordinal));

    internal static bool SkipsGoal(string questName, string goalName) => GoalChangeOf(questName, goalName)?.Skip == true;

    internal static bool AnyCreatureCounts(string questName, string goalName) => GoalChangeOf(questName, goalName)?.AnyCreature == true;

    /// <summary>Every NPC (template ID) the changes make a giver or a step's NPC: they get the quest services in their zones.</summary>
    internal static bool NamesNpc(ulong templateId)
        => templateId != 0 && s_changes.Values.Any(c => c.Giver == templateId || c.Goals.Any(g => g.Npc == templateId));

    /// <summary>The drop table the quest's own rewards are given by (DropTableFiles), or null.</summary>
    internal static DropTable RewardTable(string tableName) {
        if (tableName is null || !tableName.StartsWith(REWARD_TABLE_PREFIX, StringComparison.Ordinal)
            || ChangeOf(tableName[REWARD_TABLE_PREFIX.Length..]) is not { } change) {
            return null;
        }

        return new DropTable {
            Id = tableName,
            Name = tableName,
            RollChance = 1.0,
            MinGold = Math.Max(0, change.Gold),
            MaxGold = Math.Max(0, change.Gold),
            ExperienceAmount = Math.Max(0, change.Xp),
            TrainingPoints = Math.Max(0, change.TrainingPoints),
            EachItem = true,
            Items = [.. change.Items.Where(i => i.Id != 0 && i.Count > 0).Select(i => new DropItem {
                ItemId = i.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Guaranteed = true,
                Chance = 100,
                MinCount = i.Count,
                MaxCount = i.Count,
            })],
        };
    }

    /// <summary>
    /// Reads the file and puts the changed quests in use. SpiralDB.Load calls it at boot; after that a timer looks at the
    /// file every two seconds and calls it again when the Manager saved it.
    /// </summary>
    internal static void Apply() {
        lock (s_lock) {
            s_fileTime = FileTime();
            ApplyFile(Read());
            s_timer ??= new Timer(_ => CheckFile(), null, 2000, 2000);
        }
    }

    private static DateTime FileTime() {
        try {
            return File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
        }
        catch (IOException) {
            return DateTime.MinValue;
        }
    }

    private static void CheckFile() {
        try {
            if (FileTime() == s_fileTime) {
                return;
            }

            lock (s_lock) {
                var time = FileTime();
                if (time == s_fileTime) {
                    return;
                }

                s_fileTime = time;
                ApplyFile(Read());
            }

            // The Manager's quest list shows the quests as they are now.
            Tools.QuestGuide.WriteListAgain();
        }
        catch (Exception ex) {
            Logger.Warning("Quest changes could not be put in use: {0}", Logger.Args(ex.Message));
        }
    }

    private static List<QuestChange> Read() {
        if (!File.Exists(FilePath)) {
            return [];
        }

        try {
            return (JsonSerializer.Deserialize<QuestOptionFile>(File.ReadAllText(FilePath), s_options)?.Quests ?? [])
                .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Quest))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) {
            // The changes in use stay (the Manager writes the file whole, so this is a file broken by hand or mid-save).
            Logger.Warning("Quest changes: {0} could not be read: {1}", Logger.Args(FILE, ex.Message));

            return [.. s_changes.Values];
        }
    }

    private static void ApplyFile(List<QuestChange> changes) {
        var originals = SpiralDB.OriginalQuestTemplates;
        var byName = new Dictionary<string, QuestTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var quest in originals) {
            if (quest?.m_questName is { } name) {
                byName.TryAdd(name, quest);
            }
        }

        var kept = new Dictionary<string, QuestChange>(StringComparer.OrdinalIgnoreCase);
        var made = new Dictionary<string, QuestTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes) {
            change.Quest = change.Quest.Trim();
            change.CopyOf = (change.CopyOf ?? "").Trim();
            change.Needs = [.. (change.Needs ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim())];
            change.Items = [.. (change.Items ?? []).Where(i => i is not null)];
            change.Goals = [.. (change.Goals ?? []).Where(g => g is not null && !string.IsNullOrEmpty(g.Goal))];

            var isCopy = change.CopyOf.Length > 0;
            if (isCopy && byName.ContainsKey(change.Quest)) {
                Logger.Warning("Quest changes: the new quest {0} has the code of a game quest; pick another code.", Logger.Args(change.Quest));

                continue;
            }

            if (!byName.TryGetValue(isCopy ? change.CopyOf : change.Quest, out var original)) {
                Logger.Warning("Quest changes: there is no quest {0}; skipped.", Logger.Args(isCopy ? change.CopyOf : change.Quest));

                continue;
            }

            if (kept.ContainsKey(change.Quest)) {
                Logger.Warning("Quest changes: {0} is listed twice; the first one counts.", Logger.Args(change.Quest));

                continue;
            }

            try {
                var quest = Clone(original);
                if (isCopy) {
                    quest.m_questName = change.Quest;
                    quest.m_questNameID = 0;
                }

                Change(quest, change);
                kept[change.Quest] = change;
                made[change.Quest] = quest;
            }
            catch (Exception ex) {
                Logger.Warning("Quest changes: {0} could not be changed: {1}", Logger.Args(change.Quest, ex.Message));
            }
        }

        var quests = new List<QuestTemplate>(originals.Count + made.Count);
        foreach (var quest in originals) {
            quests.Add(quest?.m_questName is { } name && made.TryGetValue(name, out var changed) ? changed : quest);
        }

        quests.AddRange(made.Where(p => !byName.ContainsKey(p.Key)).Select(p => p.Value));

        s_changes = kept;
        SpiralDB.UseQuestTemplates(quests);
        Interlocked.Increment(ref s_version);
        if (kept.Count > 0 || changes.Count > 0) {
            Logger.Information("Quest changes in use: {0} quests changed, {1} new.",
                Logger.Args(kept.Count(p => p.Value.CopyOf.Length == 0), kept.Count(p => p.Value.CopyOf.Length > 0)));
        }
    }

    private static QuestTemplate Clone(QuestTemplate quest)
        => JsonSerializer.Deserialize<QuestTemplate>(JsonSerializer.Serialize(quest, SpiralJsonOptions.Options), SpiralJsonOptions.Options);

    private static void Change(QuestTemplate quest, QuestChange change) {
        if (change.Level is > 0 and var level) {
            quest.m_questLevel = level;
        }

        if (change.Mainline is { } mainline) {
            quest.m_mainline = mainline;
        }

        ChangeRequirements(quest, change);
        ChangeGiver(quest, change);
        ChangeRewards(quest, change);

        foreach (var goalChange in change.Goals) {
            var goal = quest.m_goals?.FirstOrDefault(g => string.Equals(g.m_goalName, goalChange.Goal, StringComparison.Ordinal));
            if (goal is null) {
                Logger.Warning("Quest changes: quest {0} has no step {1}.", Logger.Args(change.Quest, goalChange.Goal));

                continue;
            }

            if (goalChange.Count is > 0 and var count) {
                if (goal.m_tallyCounter is not null) {
                    goal.m_tallyCounter.m_count = count;
                }

                switch (goal) {
                    case BountyGoalTemplate bounty:
                        bounty.m_bountyTotal = count;
                        break;
                    case ScavengeGoalTemplate scavenge:
                        scavenge.m_itemTotal = count;
                        break;
                }
            }

            if (goalChange.Npc != 0 && goal is PersonaGoalTemplate persona && NpcOf(goalChange.Npc) is { } npc) {
                var old = QuestMadlibs.SpeakerTemplateOf(persona.m_personaName, 0);
                persona.m_personaName = npc.m_objectName + PERSONA_SUFFIX;
                persona.m_displayImage1 = string.IsNullOrEmpty(npc.m_sIcon) ? persona.m_displayImage1 : npc.m_sIcon;
                Respeak(goal.m_dialogList, null, old, npc);
            }
        }
    }

    // A wizard's level, the quests that must be done first, or both, joined to (or in place of) the game's requirements.
    private static void ChangeRequirements(QuestTemplate quest, QuestChange change) {
        var added = new List<Requirement>();
        foreach (var needed in change.Needs) {
            added.Add(new ReqHasEntry {
                m_entryName = "Complete",
                m_displayName = "",
                m_isQuestRegistry = true,
                m_questName = needed,
                m_operator = Operator.ROP_AND,
            });
        }

        if (change.Level is > 0 and var level) {
            added.Add(new ReqMagicLevel {
                m_numericValue = level,
                m_operatorType = OPERATOR_TYPE.OPERATOR_GREATER_THAN_EQ,
                m_magicSchool = "",
                m_operator = Operator.ROP_AND,
            });
        }

        if (!change.NeedsOnly && added.Count == 0) {
            return;
        }

        var list = new List<Requirement>();
        if (!change.NeedsOnly && quest.m_requirements is { m_requirements.Count: > 0 } game) {
            game.m_operator = Operator.ROP_AND;
            list.Add(game);
        }

        list.AddRange(added);
        quest.m_requirements = new RequirementList { m_requirements = list, m_operator = Operator.ROP_AND };
    }

    private static void ChangeGiver(QuestTemplate quest, QuestChange change) {
        if (change.StartsByItself) {
            quest.m_clientTags ??= [];
            if (!quest.m_clientTags.Any(t => (string) t == AUTO_START_TAG)) {
                quest.m_clientTags.Add(AUTO_START_TAG);
            }

            return;
        }

        if (change.Giver == 0 || NpcOf(change.Giver) is not { } npc) {
            return;
        }

        // The offer's lines are said by the new giver: the old one's name and picture would point at the wrong NPC.
        var old = QuestMadlibs.PrepSpeakerOf(quest);
        Respeak(quest.m_dialogList, "Prep", old, npc);
        Respeak(quest.m_dialogList, "Underway", old, npc);
    }

    // The lines of a dialog (all, or the ones with this tag) that the old NPC says are said by the new one.
    private static void Respeak(ActorDialogListBase dialogs, string tag, ulong oldNpc, GameObjectTemplate npc) {
        if (dialogs is not ActorDialogList list) {
            return;
        }

        foreach (var dialog in list.m_dialogs ?? []) {
            if (dialog?.m_dialogEntries is null || (tag is not null && dialog.m_dialogTag != tag)) {
                continue;
            }

            var changed = false;
            foreach (var entry in dialog.m_dialogEntries.OfType<NPCDialogEntry>()) {
                var speaker = QuestMadlibs.SpeakerTemplateOf(entry.m_personaName, entry.m_actorTemplateID);
                if (oldNpc != 0 && speaker != oldNpc) {
                    continue;
                }

                entry.m_personaName = npc.m_objectName + PERSONA_SUFFIX;
                entry.m_actorTemplateID = (uint) npc.m_templateID;
                entry.m_picture = npc.m_sIcon;
                changed = true;
            }

            if (changed) {
                // Named again when the dialog is sent (QuestMadlibs.NameDialogSpeakers), the new speaker included.
                foreach (var entry in dialog.m_dialogEntries.OfType<NPCDialogEntry>()) {
                    entry.m_nameSTKey = "";
                }

                dialog.m_madlibs = null;
            }
        }
    }

    private static void ChangeRewards(QuestTemplate quest, QuestChange change) {
        if (change.KeepGameRewards && !change.HasOwnRewards) {
            return;
        }

        quest.m_endResults ??= new ResultList { m_results = [] };
        quest.m_endResults.m_results ??= [];
        if (!change.KeepGameRewards) {
            quest.m_endResults.m_results.RemoveAll(r => r is ResDropTable);
        }

        if (change.HasOwnRewards) {
            quest.m_endResults.m_results.Add(new ResDropTable { m_tableName = REWARD_TABLE_PREFIX + change.Quest });
        }
    }

    private static GameObjectTemplate NpcOf(ulong templateId) {
        if (CoreObjectFactory.GetCoreTemplate(templateId) is GameObjectTemplate npc && !string.IsNullOrEmpty(npc.m_objectName)) {
            return npc;
        }

        Logger.Warning("Quest changes: there is no NPC with template ID {0}.", Logger.Args(templateId));

        return null;
    }

}
