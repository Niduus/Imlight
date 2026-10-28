/*
 * Catch-up rewards (Niduus, 2026-10-28: "if a task previously completed by a wizard is altered in a positive regard (More
 * gold, XP, new rewards) they are given that as well"). [Character] QuestCatchUpRewards, on by default, read live.
 *
 * What a quest gives for sure is its reward: the gold, experience and training points of its drop tables that always
 * roll, their items that always drop, the item a pick-one table picks, and the spells it teaches. A game update, the
 * Manager's Quest Editor (QuestOptions.json) or a drop table edit can change that.
 *   - quest-reward-history.json, next to the server, keeps each quest's reward as it was the first time the server saw
 *     it. A wizard who finished a quest got at least that.
 *   - Wizard.QuestRewardCredits keeps, for the quests where it is more than that, the best reward the wizard got.
 *   - At login, every finished quest whose reward is now better gives the difference, once: more gold, experience or
 *     training points, items and spells it did not give before. A reward that got smaller takes nothing away.
 *   - Finishing a quest counts its reward as given, also while the switch is off, so turning it on later never gives
 *     twice. Potion bottles are already caught up by QuestRewardRepair.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Quests;

internal static class QuestRewardCatchUp {

    internal const string FILE = "quest-reward-history.json";
    private const string COMPLETE_SUFFIX = "_Complete";
    private const string PICK_PREFIX = "pick:";
    private const int MOST_LINES = 5;

    private static bool s_enabled => ConfigurationManager.GetValue("Character.QuestCatchUpRewards", true);

    private sealed class HistoryFile {
        public Dictionary<string, QuestRewardCredit> Quests { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    // A quest's reward now, with the drop table items behind its item keys (for their requirements and names).
    private sealed record Reward(QuestRewardCredit Credit, Dictionary<string, List<DropItem>> Items, Dictionary<uint, ResLearnSpell> Spells);

    private static readonly object s_lock = new();
    private static Dictionary<string, QuestRewardCredit> s_first;
    private static bool s_started;

    /// <summary>
    /// Notes the first reward of every quest not noted yet, away from the caller. The game server calls it once it is
    /// up, and the Quest Editor's changes call it again (a new quest is noted before anyone can finish it).
    /// </summary>
    internal static void NoteAllQuests() {
        _ = Task.Run(() => {
            try {
                var added = 0;
                foreach (var quest in QuestTemplateCollection.GetAllQuests()) {
                    if (quest?.m_questName is { } name && !string.IsNullOrEmpty(name) && FirstOf(name, Now(quest).Credit, save: false).Added) {
                        added++;
                    }
                }

                if (added > 0) {
                    lock (s_lock) {
                        Save();
                    }

                    Logger.Information("Catch-up rewards: noted the rewards of {0} quests.", Logger.Args(added));
                }
            }
            catch (Exception ex) {
                Logger.Warning("Catch-up rewards: the quest rewards could not be noted: {0}", Logger.Args(ex.Message));
            }
        });
    }

    /// <summary>The game server's start. Calling it again does nothing.</summary>
    internal static void Start() {
        lock (s_lock) {
            if (s_started) {
                return;
            }

            s_started = true;
        }

        NoteAllQuests();
    }

    /// <summary>
    /// The wizard just finished the quest and got its reward as it is now: it counts as given.
    /// </summary>
    internal static void NoteFinished(Wizard wizard, string questName) {
        try {
            var quest = QuestTemplateCollection.GetQuestByName(questName);
            if (quest is null) {
                return;
            }

            var now = Now(quest).Credit;
            var first = FirstOf(questName, now, save: true).Credit;
            wizard.QuestRewardCredits ??= [];
            var had = wizard.QuestRewardCredits.TryGetValue(questName, out var kept) ? kept : first;
            if (Keep(wizard, questName, Best(had, now), first)) {
                WizardCollection.UpdateCharacterQuestRewardCredits(wizard);
            }
        }
        catch (Exception ex) {
            Logger.Warning("Catch-up rewards: quest {0} of wizard {1} could not be noted: {2}",
                Logger.Args(questName, wizard.CharId, ex.Message));
        }
    }

    /// <summary>
    /// Gives the wizard what each finished quest gives now and did not give then.
    /// </summary>
    /// <returns>The chat lines saying what was given.</returns>
    internal static List<string> GiveCatchUp(IActorRef playerActor, CoreObject playerObj, Wizard wizard) {
        var lines = new List<string>();
        if (!s_enabled || wizard is null) {
            return lines;
        }

        var given = new List<(string Title, List<string> What)>();
        var changed = false;
        wizard.QuestRewardCredits ??= [];
        foreach (var questName in FinishedQuests(wizard)) {
            try {
                var quest = QuestTemplateCollection.GetQuestByName(questName);
                if (quest is null) {
                    continue;
                }

                var now = Now(quest);
                var first = FirstOf(questName, now.Credit, save: true).Credit;
                var had = wizard.QuestRewardCredits.TryGetValue(questName, out var kept) ? kept : first;
                var what = Give(playerActor, playerObj, wizard, had, now);
                changed |= Keep(wizard, questName, Best(had, now.Credit), first);
                if (what.Count > 0) {
                    given.Add((TitleOf(quest), what));
                    Logger.Information("Catch-up rewards: wizard {0} got {1} for quest {2}.",
                        Logger.Args(wizard.CharId, string.Join(", ", what), questName));
                }
            }
            catch (Exception ex) {
                Logger.Warning("Catch-up rewards: quest {0} of wizard {1} could not be caught up: {2}",
                    Logger.Args(questName, wizard.CharId, ex.Message));
            }
        }

        if (changed) {
            WizardCollection.UpdateCharacterQuestRewardCredits(wizard);
        }

        foreach (var (title, what) in given.Take(MOST_LINES)) {
            lines.Add($"\"{title}\" gives more now, so you got: {string.Join(", ", what)}.");
        }

        if (given.Count > MOST_LINES) {
            lines.Add($"...and the better rewards of {given.Count - MOST_LINES} more quests you finished.");
        }

        return lines;
    }

    // ---------------------------------------------------------------------
    // Rewards
    // ---------------------------------------------------------------------

    private static IEnumerable<string> FinishedQuests(Wizard wizard) {
        foreach (var (key, value) in wizard.QuestBehavior?.Registry.ToArray() ?? Array.Empty<KeyValuePair<string, ulong>>()) {
            if (value != 0 && key.EndsWith(COMPLETE_SUFFIX, StringComparison.Ordinal)) {
                yield return key[..^COMPLETE_SUFFIX.Length];
            }
        }
    }

    private static Reward Now(QuestTemplate quest) {
        var credit = new QuestRewardCredit();
        var items = new Dictionary<string, List<DropItem>>(StringComparer.Ordinal);
        var spells = new Dictionary<uint, ResLearnSpell>();
        foreach (var result in quest.m_endResults?.m_results ?? []) {
            switch (result) {
                case ResDropTable { m_tableName: { } tableName }:
                    AddTable(DropTableFiles.Get(tableName), credit, items);
                    break;
                case ResLearnSpell { m_templateID: > 0 } spell:
                    spells.TryAdd(spell.m_templateID, spell);
                    break;
            }
        }

        credit.Spells = [.. spells.Keys.OrderBy(id => id)];

        return new Reward(credit, items, spells);
    }

    private static void AddTable(DropTable table, QuestRewardCredit credit, Dictionary<string, List<DropItem>> items) {
        // A table that may not roll is luck, not a reward.
        if (table is null || DropOdds.ChanceOf(table) < 0.999) {
            return;
        }

        credit.Gold += Math.Max(0, table.MinGold);
        credit.Xp += Math.Max(0, table.ExperienceAmount);
        credit.TrainingPoints += Math.Max(0, table.TrainingPoints);
        var listed = (table.Items ?? []).Where(i => !string.IsNullOrEmpty(i?.ItemId)).ToList();
        if (listed.Count == 0) {
            return;
        }

        if (table.EachItem) {
            foreach (var item in listed.Where(i => i.Guaranteed || (i.Chance ?? 100) >= 100)) {
                var count = Math.Max(1, item.MinCount ?? 1);
                credit.Items[item.ItemId] = credit.Items.GetValueOrDefault(item.ItemId) + count;
                (items.TryGetValue(item.ItemId, out var same) ? same : items[item.ItemId] = []).Add(item);
            }

            return;
        }

        // A pick-one table gives one of its items: the reward is the pick, whichever item it is.
        var key = listed.Count == 1 ? listed[0].ItemId : PICK_PREFIX + string.Join("|", listed.Select(i => i.ItemId).Distinct().OrderBy(id => id, StringComparer.Ordinal));
        credit.Items[key] = credit.Items.GetValueOrDefault(key) + 1;
        items[key] = listed;
    }

    // The best of both: what the wizard has had once is never given again.
    private static QuestRewardCredit Best(QuestRewardCredit had, QuestRewardCredit now) {
        var best = new QuestRewardCredit {
            Gold = Math.Max(had.Gold, now.Gold),
            Xp = Math.Max(had.Xp, now.Xp),
            TrainingPoints = Math.Max(had.TrainingPoints, now.TrainingPoints),
            Items = new Dictionary<string, int>(had.Items ?? [], StringComparer.Ordinal),
            Spells = [.. (had.Spells ?? []).Union(now.Spells ?? []).OrderBy(id => id)],
        };
        foreach (var (key, count) in now.Items ?? []) {
            best.Items[key] = Math.Max(best.Items.GetValueOrDefault(key), count);
        }

        return best;
    }

    // Kept on the wizard only where it is more than the quest's first reward. True when that changed.
    private static bool Keep(Wizard wizard, string questName, QuestRewardCredit best, QuestRewardCredit first) {
        var had = wizard.QuestRewardCredits.TryGetValue(questName, out var kept) ? kept : null;
        if (Same(best, first)) {
            return had is not null && wizard.QuestRewardCredits.Remove(questName);
        }

        if (had is not null && Same(had, best)) {
            return false;
        }

        wizard.QuestRewardCredits[questName] = best;

        return true;
    }

    private static bool Same(QuestRewardCredit a, QuestRewardCredit b)
        => a.Gold == b.Gold && a.Xp == b.Xp && a.TrainingPoints == b.TrainingPoints
            && (a.Spells ?? []).OrderBy(id => id).SequenceEqual((b.Spells ?? []).OrderBy(id => id))
            && (a.Items ?? []).Count == (b.Items ?? []).Count
            && (a.Items ?? []).All(p => (b.Items ?? []).TryGetValue(p.Key, out var count) && count == p.Value);

    private static List<string> Give(IActorRef playerActor, CoreObject playerObj, Wizard wizard, QuestRewardCredit had, Reward now) {
        var what = new List<string>();
        var loot = new DropTableResult {
            DropTableId = "QuestCatchUp",
            GoldAmount = Math.Max(0, now.Credit.Gold - had.Gold),
            ExperienceAmount = Math.Max(0, now.Credit.Xp - had.Xp),
            TrainingPoints = Math.Max(0, now.Credit.TrainingPoints - had.TrainingPoints),
            Items = [],
        };

        // An item the quest gave before in another way (its pick-one table now lists other items too, or the other way
        // round) is the same reward moved, not a new one.
        var hadIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in (had.Items ?? []).Keys) {
            hadIds.UnionWith(IdsOf(key));
        }

        var random = new Random();
        foreach (var (key, count) in now.Credit.Items) {
            var more = count - (had.Items?.GetValueOrDefault(key) ?? 0);
            if (more <= 0 || !now.Items.TryGetValue(key, out var choices)) {
                continue;
            }

            var isNew = !(had.Items?.ContainsKey(key) ?? false);
            if (isNew && IdsOf(key).Any(hadIds.Contains)) {
                continue;
            }

            for (var i = 0; i < more; i++) {
                var allowed = choices.Where(item => Meets(item.Requirements, playerActor, playerObj, wizard)).ToList();
                if (allowed.Count == 0) {
                    break;
                }

                var item = key.StartsWith(PICK_PREFIX, StringComparison.Ordinal) ? allowed[random.Next(allowed.Count)] : allowed[0];
                loot.Items.Add(new DropItemResult { ItemId = item.ItemId, ItemName = item.ItemName, Quantity = 1 });
            }
        }

        if (loot.GoldAmount > 0 || loot.ExperienceAmount > 0 || loot.TrainingPoints > 0 || loot.Items.Count > 0) {
            var granted = LootGranter.Grant(playerActor, wizard, loot);
            if (granted.GoldAmount > 0) {
                what.Add($"{granted.GoldAmount:N0} gold");
            }

            if (granted.ExperienceAmount > 0) {
                what.Add($"{granted.ExperienceAmount:N0} XP");
            }

            if (granted.TrainingPoints > 0) {
                what.Add(granted.TrainingPoints == 1 ? "1 training point" : $"{granted.TrainingPoints} training points");
            }

            foreach (var group in granted.Items.GroupBy(i => i.ItemId)) {
                var count = group.Sum(i => Math.Max(1, i.Quantity));
                var name = ItemNameOf(group.First());
                what.Add(count > 1 ? $"{count} x {name}" : name);
            }
        }

        foreach (var (id, spell) in now.Spells) {
            if (had.Spells?.Contains(id) == true || !Meets(spell.m_requirements, playerActor, playerObj, wizard)) {
                continue;
            }

            if (SpellTeacher.TryTeach(playerActor, wizard, id) == SpellTeachResult.Learned) {
                what.Add($"the spell {SpellNameOf(id)}");
            }
        }

        return what;
    }

    private static IEnumerable<string> IdsOf(string key)
        => key.StartsWith(PICK_PREFIX, StringComparison.Ordinal) ? key[PICK_PREFIX.Length..].Split('|') : [key];

    private static bool Meets(RequirementList requirements, IActorRef playerActor, CoreObject playerObj, Wizard wizard)
        => requirements?.m_requirements is not { Count: > 0 }
            || RequirementDispatcher.EvaluateRequirements(requirements,
                new GenericRequirementContext(requirements: requirements, playerRef: playerActor, playerObj: playerObj, wizard: wizard));

    private static string ItemNameOf(DropItemResult item) {
        if (ulong.TryParse(item.ItemId, out var id)) {
            if (CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate) {
                return $"{SpellNameOf((uint) id)} treasure card";
            }

            var name = ItemSafety.ItemHistory.NameOf(id);
            if (!name.StartsWith("item ", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(item.ItemName)) {
                return name;
            }
        }

        return string.IsNullOrWhiteSpace(item.ItemName) ? item.ItemId : item.ItemName;
    }

    private static string SpellNameOf(uint id) {
        if (CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate spell) {
            string key = spell.m_displayName;
            var shown = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);

            return string.IsNullOrWhiteSpace(shown) ? (string) spell.m_name ?? $"spell {id}" : shown;
        }

        return $"spell {id}";
    }

    private static string TitleOf(QuestTemplate quest) {
        string key = quest.m_questTitle;
        var title = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);

        return string.IsNullOrEmpty(title) ? (string) quest.m_questName ?? "" : title;
    }

    // ---------------------------------------------------------------------
    // The first rewards (quest-reward-history.json)
    // ---------------------------------------------------------------------

    // The quest's first reward; a quest seen for the first time starts with its reward now.
    private static (QuestRewardCredit Credit, bool Added) FirstOf(string questName, QuestRewardCredit now, bool save) {
        lock (s_lock) {
            s_first ??= Load();
            if (s_first.TryGetValue(questName, out var first)) {
                return (first, false);
            }

            s_first[questName] = now;
            if (save) {
                Save();
            }

            return (now, true);
        }
    }

    private static Dictionary<string, QuestRewardCredit> Load() {
        try {
            if (File.Exists(FILE)) {
                var quests = JsonSerializer.Deserialize<HistoryFile>(File.ReadAllText(FILE))?.Quests;
                if (quests is not null) {
                    return new Dictionary<string, QuestRewardCredit>(quests.Where(p => p.Value is not null), StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) {
            // Starting over only means rewards changed before now are not caught up; nothing is given twice.
            Logger.Warning("Catch-up rewards: {0} could not be read, the quest rewards are noted again: {1}", Logger.Args(FILE, ex.Message));
        }

        return new Dictionary<string, QuestRewardCredit>(StringComparer.OrdinalIgnoreCase);
    }

    // Called under s_lock.
    private static void Save() {
        try {
            var temp = FILE + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new HistoryFile {
                Quests = new Dictionary<string, QuestRewardCredit>(s_first.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
            }));
            File.Move(temp, FILE, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Warning("Catch-up rewards: {0} could not be saved: {1}", Logger.Args(FILE, ex.Message));
        }
    }

}
