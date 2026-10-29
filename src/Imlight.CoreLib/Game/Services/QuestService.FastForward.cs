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

using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.Quests;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Game.Requirements.Handlers;
using Imlight.CoreLib.Game.Results;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// Fast Forward (Niduus, 2026-10-29: "if I want to be level 10, it does all the main quests for me to get to level 10. If
/// I want to complete wizard city, it will complete all wizard city quests for me (Choice of Side or main) with rewards
/// ... I don't want to bunker up XP"). The Manager's Fast Forward tab writes a plan, fast-forward/&lt;wizard id&gt;.txt
/// beside the server, and sends '.quest fastforward'. The quests of the plan are finished here in order, the way a
/// player would finish them: every step's and the quest's own results run (progress flags, spells, badges) and each
/// quest gives its own rewards, so the XP is only what those quests give. A level target stops before the first quest
/// once the wizard would have that level. Nothing is shown for each quest (no dialogs, no loot windows); a summary is
/// said in chat at the end and written to fast-forward/&lt;wizard id&gt;-result.txt for the Manager.
/// </summary>
internal partial class QuestService {

    private const string FAST_FORWARD_FOLDER = "fast-forward";
    // A whole story with side quests is about 3,000 quests; more than this is a broken plan.
    private const int FAST_FORWARD_MAX_QUESTS = 6000;
    // How deep the quests a planned quest needs are fetched in first.
    private const int FAST_FORWARD_NEEDS_DEPTH = 4;
    // Saved this often while it runs, so a crash keeps most of it.
    private const int FAST_FORWARD_SAVE_EVERY = 25;
    private const int FAST_FORWARD_SAY_EVERY = 100;

    // The results a quest gives the player that matter after it is done. The rest (dialogs, scenes, sounds, spawns,
    // teleports, battles) only matter while it is played.
    private static readonly HashSet<System.Type> s_fastForwardKept = [
        typeof(ResLearnSpell), typeof(ResAddSpell), typeof(ResGiveSpell), typeof(ResAddBadge), typeof(ResIncrementBadge),
        typeof(ResAddGold), typeof(ResDeleteItem),
    ];

    private sealed record FastForwardPlan(int Level, bool PassLevels, bool Go, List<(string Code, bool Main)> Quests, List<string> Next);

    private sealed class FastForwardRun {
        public int Xp;
        public int StartXp;
        public int StartLevel;
        public int Gold;
        public int TrainingPoints;
        public int Finished;
        public int AlreadyDone;
        public int ForNeeds;
        public readonly List<string> Done = [];
        public readonly List<(string Code, string Why)> Skipped = [];
        public readonly Dictionary<string, int> Items = new(StringComparer.OrdinalIgnoreCase);
        public string StoppedAt = "";
        public string StoppedWhy = "";
        public string Next = "";
        public bool ReachedLevel;

        public int Level => MagicLevelsConfig.GetPlayerLevelAtExperience(StartXp + Xp) is var level and > 0
            ? Math.Min(level, Math.Max(1, MagicLevelsConfig.MaxLevel)) : StartLevel;
    }

    private static string FastForwardPlanPath(ulong charId)
        => Path.Combine(FAST_FORWARD_FOLDER, charId.ToString(CultureInfo.InvariantCulture) + ".txt");

    private static string FastForwardResultPath(ulong charId)
        => Path.Combine(FAST_FORWARD_FOLDER, charId.ToString(CultureInfo.InvariantCulture) + "-result.txt");

    private void CommandFastForward(Wizard wizard) {
        var plan = ReadFastForwardPlan(wizard.CharId);
        if (plan is null) {
            CommandReply("No Fast Forward plan for this wizard. Pick what to reach on Ahziael's Progress page and press Fast forward.");
            return;
        }

        var run = new FastForwardRun {
            StartXp = wizard.MagicSchoolBehavior.ExperiencePoints,
            StartLevel = wizard.MagicSchoolBehavior.Level,
        };
        WriteFastForwardResult(wizard.CharId, run, "running", $"0/{plan.Quests.Count}");
        CommandReply($"Fast Forward: finishing up to {plan.Quests.Count} quests" +
            (plan.Level > 0 ? $" until level {plan.Level}" : "") + ". Please wait until it says it's done.");
        Logger.Information("Fast Forward for {0}: {1} quests planned, level {2}.", Logger.Args(wizard.CharId, plan.Quests.Count, plan.Level));

        var count = 0;
        var saved = 0;
        var said = 0;
        try {
            foreach (var (code, main) in plan.Quests) {
                if (plan.Level > 0 && run.Level >= plan.Level) {
                    run.ReachedLevel = true;
                    break;
                }

                count++;

                var template = QuestTemplateCollection.GetQuestByName(code);
                if (template is null || QuestOptions.IsOff(code)) {
                    run.Skipped.Add((code, "not on this server"));
                    continue;
                }

                if (wizard.HasCompletedQuest(template.m_questName) && !wizard.HasQuest(template.m_questName)) {
                    run.AlreadyDone++;
                    continue;
                }

                var why = FastForwardQuest(wizard, template, plan, run, 0);
                if (why is not null) {
                    if (main && why != FF_NOT_YOURS) {
                        // The story can't go on past a main quest that can't be done.
                        run.StoppedAt = template.m_questName;
                        run.StoppedWhy = why;
                        break;
                    }

                    run.Skipped.Add((template.m_questName, why));
                }

                if (run.Finished - saved >= FAST_FORWARD_SAVE_EVERY) {
                    saved = run.Finished;
                    WizardCollection.UpdateCharacterQuestBehavior(wizard);
                    WriteFastForwardResult(wizard.CharId, run, "running", $"{count}/{plan.Quests.Count}");
                }

                if (run.Finished - said >= FAST_FORWARD_SAY_EVERY) {
                    said = run.Finished;
                    CommandReply($"Fast Forward: {run.Finished} quests done so far...");
                }
            }

            if (plan.Level > 0 && run.Level >= plan.Level) {
                run.ReachedLevel = true;
            }
        }
        catch (Exception ex) {
            Logger.Error("Fast Forward for {0} stopped: {1}", Logger.Args(wizard.CharId, ex));
            run.StoppedWhy = $"something went wrong on the server ({ex.Message})";
        }

        WizardCollection.UpdateCharacterQuestBehavior(wizard);

        // The XP of every finished quest at once: the game shows one level up to the level they add up to.
        if (run.Xp > 0) {
            SessionActor.ActorRef.Tell(new CHARACTER_103_PROTOCOL.MSG_GAINXP { XP = run.Xp }, Self);
        }

        // The quest after the last one, so the wizard knows where to go: the plan's own, or after a level target the
        // main quests left in it (several, for the copies of one quest each school has).
        if (run.StoppedAt.Length == 0 && run.StoppedWhy.Length == 0) {
            var next = plan.Next.Count > 0 ? plan.Next
                : run.ReachedLevel ? [.. plan.Quests.Skip(count).Where(q => q.Main).Select(q => q.Code).Take(12)]
                : [];
            GiveFastForwardNext(wizard, next, plan.Go, run);
        }

        TryAutoStartQuests(wizard);
        NoteProgress(wizard);
        WriteFastForwardResult(wizard.CharId, run, "done", "");
        TryDelete(FastForwardPlanPath(wizard.CharId));

        Logger.Information("Fast Forward for {0}: {1} quests done, {2} XP, level {3} -> {4}, stopped at '{5}' {6}.",
            Logger.Args(wizard.CharId, run.Finished, run.Xp, run.StartLevel, run.Level, run.StoppedAt, run.StoppedWhy));
        CommandReply($"Fast Forward done: {run.Finished} quests finished" +
            (run.ForNeeds > 0 ? $" ({run.ForNeeds} of them needed by the others)" : "") +
            $", +{run.Xp:N0} XP, level {run.StartLevel} -> {run.Level}" +
            (run.Gold > 0 ? $", +{run.Gold:N0} gold" : "") +
            (run.Items.Count > 0 ? $", {run.Items.Values.Sum()} items" : "") + ".");
        if (run.StoppedAt.Length > 0) {
            CommandReply($"It stopped at '{FastForwardTitle(run.StoppedAt)}': {run.StoppedWhy}.");
        }
        else if (run.StoppedWhy.Length > 0) {
            CommandReply($"It stopped early: {run.StoppedWhy}.");
        }

        if (run.Next.Length > 0) {
            CommandReply($"Your next quest is in your quest book: '{FastForwardTitle(run.Next)}'.");
        }

        CommandReply("Ahziael's Progress page lists every quest it finished.");
    }

    // The name players see, or the code of a quest the server doesn't have.
    private static string FastForwardTitle(string code)
        => QuestTemplateCollection.GetQuestByName(code) is { } quest ? QuestTitleOf(quest) : code;

    private const string FF_NOT_YOURS = "for another school or wizard";

    /// <summary>
    /// Finishes one quest as if played, first the quests it needs that are not done yet. Returns null when it is done,
    /// or why it could not be.
    /// </summary>
    private string FastForwardQuest(Wizard wizard, QuestTemplate template, FastForwardPlan plan, FastForwardRun run, int depth) {
        string name = template.m_questName;
        var context = new GenericRequirementContext(template.m_requirements, SessionActor.ActorRef, GetActiveGameObject(), wizard);
        if (!FastForwardMeets(template.m_requirements, context, plan.PassLevels, run.Level)) {
            // Quests it needs come first, the way a player would have done them.
            if (depth < FAST_FORWARD_NEEDS_DEPTH) {
                foreach (var needed in RequiredEntries(template.m_requirements).Where(e => e.m_isQuestRegistry)
                             .Select(e => (string) e.m_questName).Where(n => !string.IsNullOrEmpty(n))
                             .Distinct(StringComparer.OrdinalIgnoreCase).ToList()) {
                    if (wizard.HasCompletedQuest(needed) || QuestTemplateCollection.GetQuestByName(needed) is not { } earlier
                        || QuestOptions.IsOff(needed)) {
                        continue;
                    }

                    if (FastForwardQuest(wizard, earlier, plan, run, depth + 1) is null) {
                        run.ForNeeds++;
                    }
                }
            }

            if (!FastForwardMeets(template.m_requirements, context, plan.PassLevels, run.Level)) {
                return WhyNotMet(template.m_requirements, run.Level, wizard);
            }
        }

        // A copy in the quest book goes; it is finished below.
        var held = wizard.QuestBehavior.CurrentQuestInstances
            .FirstOrDefault(q => q is not null && q.QuestName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (held is not null && wizard.RemoveQuest(held.QuestName)) {
            SendRemoveQuest(held.ID);
        }

        if (wizard.HasCompletedQuest(name)) {
            wizard.QuestBehavior.RemoveAllQuestRegistryEntries(name);
        }

        // The steps are walked on a copy that is never put in the quest book: only the finished quest is kept.
        var instance = new QuestInstance(template, wizard.CharId);
        FastForwardResults(wizard, template.m_startResults, name, null, run);
        foreach (var goal in (template.m_goals ?? []).Where(g => instance.IsGoalActive(g.m_goalName))) {
            FastForwardResults(wizard, goal.m_activateResults, name, goal.m_goalName, run);
        }

        for (var step = 0; step < 256; step++) {
            var goal = (template.m_goals ?? []).FirstOrDefault(g => instance.IsGoalActive(g.m_goalName));
            if (goal is null) {
                break;
            }

            instance.CompleteGoal(goal.m_goalName);
            FastForwardResults(wizard, goal.m_completeResults, name, goal.m_goalName, run);
            if (!DetermineNextGoals(template, instance, out var next)) {
                break;
            }

            foreach (var nextGoal in next) {
                if (instance.IsGoalCompleted(nextGoal.m_goalName) || instance.IsGoalActive(nextGoal.m_goalName)) {
                    continue;
                }

                // A step for another school is left out, as when played.
                if (nextGoal.m_goalRequirements?.m_requirements is { Count: > 0 }
                    && !RequirementDispatcher.EvaluateRequirements(nextGoal.m_goalRequirements,
                        new QuestRequirementContext(nextGoal.m_goalRequirements, SessionActor.ActorRef, GetActiveGameObject(), wizard,
                            name, nextGoal.m_goalName))) {
                    continue;
                }

                instance.StartGoal(nextGoal.m_goalName);
                FastForwardResults(wizard, nextGoal.m_activateResults, name, nextGoal.m_goalName, run);
            }
        }

        wizard.QuestBehavior.SetQuestRegistryValue(name, QUEST_COMPLETED_ENTRY, 1);
        if (string.Equals(name, Imlight.CoreLib.Game.Combat.CombatDuelSubCircle.SHADOW_MAGIC_QUEST, StringComparison.OrdinalIgnoreCase)) {
            wizard.MarkShadowQuestDone();
        }

        FastForwardResults(wizard, template.m_endResults, name, null, run);
        QuestRewardRepair.MarkDone(wizard, name);
        QuestRewardCatchUp.NoteFinished(wizard, name);

        run.Finished++;
        run.Done.Add(name);

        return null;
    }

    /// <summary>
    /// Runs the results of a step or quest that last: progress flags at once (later quests check them), rewards without a
    /// loot window (XP is added up and given at the end), spells and badges as usual.
    /// </summary>
    private void FastForwardResults(Wizard wizard, ResultList results, string questName, string goalName, FastForwardRun run) {
        if (results?.m_results is not { Count: > 0 } list) {
            return;
        }

        var later = new List<Result>();
        foreach (var result in list) {
            if (result is null) {
                continue;
            }

            if (result.m_requirements?.m_requirements is { Count: > 0 }
                && !RequirementDispatcher.EvaluateRequirements(result.m_requirements,
                    new GenericRequirementContext(result.m_requirements, SessionActor.ActorRef, GetActiveGameObject(), wizard,
                        questName: questName, goalName: goalName))) {
                continue;
            }

            switch (result) {
                case ResModifyEntry entry when !string.IsNullOrEmpty(entry.m_entryName):
                    if (!entry.m_isQuestRegistry) {
                        wizard.QuestBehavior.SetRegistryValue(entry.m_entryName, (ulong) entry.m_value);
                    }
                    else if (!string.IsNullOrEmpty(entry.m_questName)) {
                        wizard.QuestBehavior.SetQuestRegistryValue(entry.m_questName, entry.m_entryName, (ulong) entry.m_value);
                    }
                    break;
                case ResIncrementEntry entry when !string.IsNullOrEmpty(entry.m_entryName):
                    if (!entry.m_isQuestRegistry) {
                        wizard.QuestBehavior.IncrementRegistryValue(entry.m_entryName);
                    }
                    else if (!string.IsNullOrEmpty(entry.m_questName)) {
                        wizard.QuestBehavior.IncrementQuestRegistryValue(entry.m_questName, entry.m_entryName);
                    }
                    break;
                case ResRemoveEntry entry when !string.IsNullOrEmpty(entry.m_entryName):
                    if (!entry.m_isQuestRegistry) {
                        wizard.QuestBehavior.RemoveRegistryValue(entry.m_entryName);
                    }
                    else if (!string.IsNullOrEmpty(entry.m_questName)) {
                        wizard.QuestBehavior.RemoveQuestRegistryValue(entry.m_questName, entry.m_entryName);
                    }
                    break;
                case ResDropTable table when !string.IsNullOrWhiteSpace(table.m_tableName):
                    FastForwardReward(wizard, table.m_tableName, run);
                    break;
                default:
                    if (s_fastForwardKept.Contains(result.GetType())) {
                        later.Add(result);
                    }
                    break;
            }
        }

        if (later.Count > 0) {
            ResultDispatcher.ExecuteResults(
                actorContext: Context,
                results: new ResultList { m_results = later },
                playerRef: SessionActor.ActorRef,
                playerObj: GetActiveGameObject(),
                zoneActor: SessionActor.GetZoneActor(),
                questName: questName,
                goalName: goalName);
        }
    }

    // A reward table: gold, training points and items now, without the loot window; its XP is added up for the end.
    private void FastForwardReward(Wizard wizard, string tableName, FastForwardRun run) {
        var rolled = DropTableRoller.Roll([tableName], SessionActor.ActorRef, GetActiveGameObject(), wizard);
        if (rolled is null) {
            return;
        }

        run.Xp += Math.Max(0, rolled.ExperienceAmount);
        rolled.ExperienceAmount = 0;
        if (!rolled.HasRewards) {
            return;
        }

        var given = LootGranter.Grant(SessionActor.ActorRef, wizard, rolled);
        run.Gold += Math.Max(0, given.GoldAmount);
        run.TrainingPoints += Math.Max(0, given.TrainingPoints);
        foreach (var item in given.Items ?? []) {
            var itemName = string.IsNullOrWhiteSpace(item.ItemName) ? item.ItemId : item.ItemName;
            run.Items[itemName] = run.Items.GetValueOrDefault(itemName) + Math.Max(1, item.Quantity);
        }
    }

    /// <summary>
    /// The quest's requirements, folded left to right as RequirementDispatcher does, with the wizard's level taken as the
    /// level the quests finished so far add up to (their XP is only given at the end), or not checked at all.
    /// </summary>
    private static bool FastForwardMeets(RequirementList requirements, IRequirementContext context, bool passLevels, int level) {
        if (requirements?.m_requirements is not { Count: > 0 } list) {
            return true;
        }

        var result = true;
        var join = Operator.ROP_AND;
        foreach (var requirement in list) {
            if (requirement is null) {
                continue;
            }

            if (join == Operator.ROP_OR ? !result : result) {
                result = requirement switch {
                    ReqMagicLevel when passLevels => true,
                    ReqMagicLevel magic => NumericRequirement.Compare(magic, level) != magic.m_applyNOT,
                    RequirementList nested => FastForwardMeets(nested, context, passLevels, level) != nested.m_applyNOT,
                    _ => RequirementDispatcher.EvaluateIndividualRequirement(requirement, context) != requirement.m_applyNOT,
                };
            }

            join = requirement.m_operator;
        }

        return result;
    }

    // Why a quest's requirements are not met, in words for the chat and the Manager.
    private static string WhyNotMet(RequirementList requirements, int level, Wizard wizard) {
        var needs = new List<string>();
        void Walk(Requirement requirement, int depth) {
            if (requirement is null || depth > 6) {
                return;
            }

            switch (requirement) {
                case RequirementList list:
                    foreach (var inner in list.m_requirements ?? []) {
                        Walk(inner, depth + 1);
                    }
                    break;
                case ReqMagicLevel magic when NumericRequirement.Compare(magic, level) == magic.m_applyNOT:
                    needs.Add($"it needs level {magic.m_numericValue:0} (the quests so far make you level {level})");
                    break;
                case ReqHasEntry { m_isQuestRegistry: true, m_applyNOT: false } entry
                    when !string.IsNullOrEmpty(entry.m_questName) && !wizard.HasCompletedQuest(entry.m_questName):
                    needs.Add($"it needs '{FastForwardTitle(entry.m_questName)}' first");
                    break;
            }
        }

        Walk(requirements, 0);
        if (needs.Count == 0) {
            // Nothing a quest or level gives: a school's own copy, or something only another wizard has.
            return FF_NOT_YOURS;
        }

        return string.Join("; ", needs.Distinct().Take(3));
    }

    // The quest after the plan: the first of these this wizard can have.
    private void GiveFastForwardNext(Wizard wizard, List<string> codes, bool go, FastForwardRun run) {
        foreach (var code in codes) {
            if (QuestTemplateCollection.GetQuestByName(code) is not { } next || QuestOptions.IsOff(code)
                || wizard.HasCompletedQuest(next.m_questName)) {
                continue;
            }

            if (wizard.HasQuest(next.m_questName)) {
                run.Next = next.m_questName;
                break;
            }

            var context = new GenericRequirementContext(next.m_requirements, SessionActor.ActorRef, GetActiveGameObject(), wizard);
            if (!FastForwardMeets(next.m_requirements, context, passLevels: true, run.Level)) {
                continue;
            }

            StartQuest(next, wizard);
            run.Next = next.m_questName;
            if (go) {
                GoToFirstStep(wizard, next);
            }

            break;
        }
    }

    private static FastForwardPlan ReadFastForwardPlan(ulong charId) {
        try {
            var path = FastForwardPlanPath(charId);
            if (!File.Exists(path)) {
                return null;
            }

            int level = 0;
            bool pass = false, go = false;
            var quests = new List<(string, bool)>();
            var next = new List<string>();
            foreach (var line in File.ReadLines(path)) {
                var parts = line.Split('\t');
                switch (parts[0].Trim()) {
                    case "level" when parts.Length > 1:
                        int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out level);
                        break;
                    case "levels" when parts.Length > 1:
                        pass = parts[1].Trim() == "pass";
                        break;
                    case "go":
                        go = true;
                        break;
                    case "quest" when parts.Length > 1 && parts[1].Trim().Length > 0 && quests.Count < FAST_FORWARD_MAX_QUESTS:
                        quests.Add((parts[1].Trim(), parts.Length > 2 && parts[2].Trim() == "main"));
                        break;
                    case "next" when parts.Length > 1 && parts[1].Trim().Length > 0:
                        next.Add(parts[1].Trim());
                        break;
                }
            }

            return new FastForwardPlan(Math.Max(0, level), pass, go, quests, next);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Warning("Fast Forward plan of {0} could not be read: {1}", Logger.Args(charId, ex.Message));

            return null;
        }
    }

    // Key and value per line, like the quest progress files: state, progress, then the totals and every quest.
    private static void WriteFastForwardResult(ulong charId, FastForwardRun run, string state, string progress) {
        try {
            static string Clean(string text) => (text ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
            var text = new StringBuilder()
                .Append("state\t").Append(state).Append('\n')
                .Append("progress\t").Append(progress).Append('\n')
                .Append("written\t").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n')
                .Append("finished\t").Append(run.Finished.ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append("already\t").Append(run.AlreadyDone.ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append("needed\t").Append(run.ForNeeds.ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append("xp\t").Append(run.Xp.ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append("levels\t").Append(run.StartLevel.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(run.Level.ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append("gold\t").Append(run.Gold.ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append("tp\t").Append(run.TrainingPoints.ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append("reached\t").Append(run.ReachedLevel ? "yes" : "no").Append('\n');
            if (run.StoppedAt.Length > 0 || run.StoppedWhy.Length > 0) {
                text.Append("stopped\t").Append(Clean(run.StoppedAt)).Append('\t').Append(Clean(run.StoppedWhy)).Append('\n');
            }

            if (run.Next.Length > 0) {
                text.Append("next\t").Append(Clean(run.Next)).Append('\n');
            }

            foreach (var code in run.Done) {
                text.Append("done\t").Append(Clean(code)).Append('\n');
            }

            foreach (var (code, why) in run.Skipped) {
                text.Append("skipped\t").Append(Clean(code)).Append('\t').Append(Clean(why)).Append('\n');
            }

            foreach (var (item, count) in run.Items) {
                text.Append("item\t").Append(Clean(item)).Append('\t').Append(count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }

            Directory.CreateDirectory(FAST_FORWARD_FOLDER);
            var path = FastForwardResultPath(charId);
            File.WriteAllText(path + ".tmp", text.ToString());
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Fast Forward result could not be written: {0}", Logger.Args(ex.Message));
        }
    }

    private static void TryDelete(string path) {
        try {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        }
    }

}
