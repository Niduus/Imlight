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
using Imlight.CoreLib.Game.Cantrips;
using Imlight.CoreLib.Game.Quests;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
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
/// Collect Spells (Niduus, 2026-10-29: "a 'Collect Spells' under Progress for that wizard to get all their quest, main,
/// side and progress spells"). The Manager writes fast-forward/&lt;wizard id&gt;-spells.txt (which kinds to collect) and
/// sends '.quest collectspells'. Spells already known are left alone, and each spell is only taught where the game
/// would teach it: a quest's spell for the wizard's school, a trainer's spell their level, school and earlier spells
/// allow and that costs no training points or gold (or, with "paid", the ones that cost training points while the wizard
/// has them, paid as at the trainer). What it taught goes to fast-forward/&lt;wizard id&gt;-spells-result.txt, with
/// "check": the spells in the book that no part of the real game gives, and the real game's spells still missing and why
/// (Niduus, 2026-10-30: "some non-vanilla trained spells are in my book and when collecting spells, doesn't give me all
/// the spells that I am missing from vanilla gameplay"). '.quest removespells' takes out the ones Ahziael lists in
/// fast-forward/&lt;wizard id&gt;-spells-remove.txt.
/// </summary>
internal partial class QuestService {

    private const string COMPLETE_SUFFIX = "_Complete";
    // Trainer spells that need other trainer spells first are taught in rounds.
    private const int COLLECT_TRAINER_ROUNDS = 12;

    private static string CollectSpellsPlanPath(ulong charId)
        => Path.Combine(FAST_FORWARD_FOLDER, charId.ToString(CultureInfo.InvariantCulture) + "-spells.txt");

    private static string CollectSpellsResultPath(ulong charId)
        => Path.Combine(FAST_FORWARD_FOLDER, charId.ToString(CultureInfo.InvariantCulture) + "-spells-result.txt");

    private static string RemoveSpellsPlanPath(ulong charId)
        => Path.Combine(FAST_FORWARD_FOLDER, charId.ToString(CultureInfo.InvariantCulture) + "-spells-remove.txt");

    private static string RemoveSpellsResultPath(ulong charId)
        => Path.Combine(FAST_FORWARD_FOLDER, charId.ToString(CultureInfo.InvariantCulture) + "-spells-remove-result.txt");

    // Spells the real game gives some way: its trainers, its quests, Spellement upgrades, fusion and cantrips.
    private static readonly Lazy<HashSet<uint>> s_realGameSpells = new(BuildRealGameSpells);

    // The schools whose missing trainer spells are listed besides the wizard's own (with their secondary school).
    private static readonly string[] s_alwaysListedSchools = ["Star", "Sun", "Moon", "Shadow"];

    private void CommandCollectSpells(Wizard wizard) {
        // Without a plan (the command typed in chat) it collects everything.
        var kinds = new HashSet<string>(["finished", "side", "trainers"], StringComparer.OrdinalIgnoreCase);
        try {
            if (File.Exists(CollectSpellsPlanPath(wizard.CharId))) {
                kinds = new HashSet<string>(File.ReadLines(CollectSpellsPlanPath(wizard.CharId)).Select(l => l.Trim()).Where(l => l.Length > 0),
                    StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        }

        // Without a plan everything is collected and checked, but nothing that costs training points is bought.
        if (!File.Exists(CollectSpellsPlanPath(wizard.CharId))) {
            kinds.Add("check");
        }

        CommandReply("Collect Spells: looking for the spells you should have...");
        Logger.Information("Collect Spells for {0}: {1}.", Logger.Args(wizard.CharId, string.Join(", ", kinds)));
        var taught = new List<(string Spell, string From)>();
        var extras = new List<(uint Id, string Spell)>();
        var missing = new List<(string Spell, string Why)>();
        var trainerSpells = 0;
        var points = 0;
        try {
            if (kinds.Contains("finished") || kinds.Contains("side")) {
                CollectQuestSpells(wizard, kinds.Contains("finished"), kinds.Contains("side"), taught);
            }

            if (kinds.Contains("trainers") || kinds.Contains("paid")) {
                var before = taught.Count;
                points = CollectTrainerSpells(wizard, kinds.Contains("trainers"), kinds.Contains("paid"), taught);
                trainerSpells = taught.Count - before;
            }

            if (kinds.Contains("check")) {
                extras = NotFromRealGame(wizard);
                missing = StillMissing(wizard);
            }
        }
        catch (Exception ex) {
            Logger.Error("Collect Spells for {0} stopped: {1}", Logger.Args(wizard.CharId, ex));
            CommandReply($"Collect Spells stopped: something went wrong on the server ({ex.Message}).");
        }

        WriteCollectSpellsResult(wizard.CharId, taught, extras, missing, kinds.Contains("check"), points);
        TryDelete(CollectSpellsPlanPath(wizard.CharId));
        Logger.Information("Collect Spells for {0}: {1} spells taught.", Logger.Args(wizard.CharId, taught.Count));
        CommandReply(taught.Count == 0
            ? "Collect Spells: no new spells to give."
            : $"Collect Spells: you learned {taught.Count} spells ({taught.Count - trainerSpells} from quests, {trainerSpells} from trainers" +
              (points > 0 ? $", {points} training points spent" : "") + ").");
        if (kinds.Contains("check")) {
            CommandReply($"Collect Spells: {extras.Count} spells in your book don't come from the real game, and {missing.Count} real game spells " +
                         "are still missing. Ahziael's Progress > Collect Spells page lists them.");
        }
    }

    // '.quest removespells': takes the spells Ahziael listed out of the spellbook and decks, and gives back the training
    // points they cost.
    private void CommandRemoveSpells(Wizard wizard) {
        var ids = new List<uint>();
        try {
            if (File.Exists(RemoveSpellsPlanPath(wizard.CharId))) {
                foreach (var line in File.ReadLines(RemoveSpellsPlanPath(wizard.CharId))) {
                    if (uint.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) {
                        ids.Add(id);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        }

        if (ids.Count == 0) {
            CommandReply("Remove spells: Ahziael's Collect Spells page picks which spells to take out.");
            return;
        }

        var taken = new List<uint>();
        foreach (var id in ids.Distinct()) {
            if (!wizard.SpellbookBehavior.HasSpell(id)) {
                continue;
            }

            try {
                TrainingReset.RemoveFromDecks(SessionActor.ActorRef, wizard, id);
                if (wizard.UnlearnSpell(id)) {
                    SendToSocket(new WIZARD_12_PROTOCOL.MSG_REMOVESPELLFROMBOOK { SpellID = (int) id });
                    taken.Add(id);
                }
            }
            catch (Exception ex) {
                Logger.Error("Remove spells | Could not take spell {0} from {1}: {2}", Logger.Args(id, wizard.CharId, ex.Message));
            }
        }

        var back = TrainingReset.Forget(wizard, taken).Values.Sum();
        if (back > 0) {
            var newPoints = wizard.MagicSchoolBehavior.TrainingPoints + back;
            wizard.UpdateTrainingPoints(newPoints);
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATETRAINING { TrainingPoints = newPoints });
        }

        try {
            var text = new StringBuilder()
                .Append("state\tdone\n")
                .Append("written\t").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n')
                .Append("points\t").Append(back).Append('\n');
            foreach (var id in taken) {
                text.Append("removed\t").Append(Clean(SpellNameOf(id))).Append('\t').Append(id).Append('\n');
            }

            var path = RemoveSpellsResultPath(wizard.CharId);
            File.WriteAllText(path + ".tmp", text.ToString());
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Remove spells result could not be written: {0}", Logger.Args(ex.Message));
        }

        TryDelete(RemoveSpellsPlanPath(wizard.CharId));
        Logger.Information("Remove spells | {0}: {1} spells taken, {2} training points back.", Logger.Args(wizard.CharId, taken.Count, back));
        CommandReply($"Removed {taken.Count} spells from your spellbook" + (back > 0 ? $" and gave back {back} training points." : "."));
    }

    // The spells of the wizard's finished quests, and of quests at or below their level they have not done, main story
    // ones too: a wizard levelled by command has done none (a quest with no level of its own is often a later step of a
    // higher chain, so it is left out).
    private void CollectQuestSpells(Wizard wizard, bool finished, bool side, List<(string Spell, string From)> taught) {
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in wizard.QuestBehavior?.Registry.ToArray() ?? []) {
            if (value != 0 && key.EndsWith(COMPLETE_SUFFIX, StringComparison.Ordinal)) {
                done.Add(key[..^COMPLETE_SUFFIX.Length]);
            }
        }

        var level = (int) wizard.MagicSchoolBehavior.Level;
        var school = wizard.MagicSchoolBehavior.MagicSchool.ToString();
        foreach (var quest in QuestTemplateCollection.GetAllQuests()) {
            if (quest?.m_questName is not { } nameValue || (string) nameValue is not { Length: > 0 } name || QuestOptions.IsOff(name)) {
                continue;
            }

            var isDone = done.Contains(name);
            if (!(isDone && finished || !isDone && side && quest.m_questLevel > 0 && quest.m_questLevel <= level && ForSchool(quest, school))) {
                continue;
            }

            foreach (var spell in SpellResultsOf(quest)) {
                if (spell.m_requirements?.m_requirements is { Count: > 0 } && !RequirementDispatcher.EvaluateRequirements(spell.m_requirements,
                        new GenericRequirementContext(spell.m_requirements, SessionActor.ActorRef, GetActiveGameObject(), wizard))) {
                    continue;
                }

                if (SpellTeacher.TryTeach(SessionActor.ActorRef, wizard, spell.m_templateID) == SpellTeachResult.Learned) {
                    taught.Add((SpellNameOf(spell.m_templateID), (isDone ? "quest " : "quest not done yet: ") + QuestTitleOf(quest)));
                }
            }
        }
    }

    // A quest made for one school (its own requirements name a school of focus) is only for wizards of that school.
    private static bool ForSchool(QuestTemplate quest, string school) {
        var schools = quest.m_requirements?.m_requirements?.OfType<ReqSchoolOfFocus>()
            .Where(r => !r.m_applyNOT).Select(r => (string) r.m_magicSchool).ToList() ?? [];

        return schools.Count == 0 || schools.Any(s => string.Equals(s, school, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<ResLearnSpell> SpellResultsOf(QuestTemplate quest) {
        var lists = new List<ResultList> { quest.m_startResults, quest.m_endResults };
        foreach (var goal in quest.m_goals ?? []) {
            if (goal is not null) {
                lists.Add(goal.m_activateResults);
                lists.Add(goal.m_completeResults);
            }
        }

        return lists.Where(l => l?.m_results is not null).SelectMany(l => l.m_results).OfType<ResLearnSpell>().Where(r => r.m_templateID > 0);
    }

    // The game trainers' spells the wizard may train now: free ones (their own school's, and any other free school
    // spell) and, with paid, the ones that cost training points while they have enough, paid as at the trainer.
    // Fishing, gardening and monster magic are left out. Returns the training points spent.
    private int CollectTrainerSpells(Wizard wizard, bool free, bool paid, List<(string Spell, string From)> taught) {
        var spells = SchoolTrainerSpells();
        var spent = 0;
        for (var round = 0; round < COLLECT_TRAINER_ROUNDS; round++) {
            var learned = 0;
            foreach (var (trainer, spell) in spells) {
                if (wizard.SpellbookBehavior.HasSpell(spell.Id) || CoreObjectFactory.GetCoreTemplate(spell.Id) is not SpellTemplate template) {
                    continue;
                }

                var verdict = VanillaTrainers.Judge(wizard, spell, template);
                if (!verdict.CanTrain || verdict.Gold > 0 || (verdict.Cost > 0 ? !paid : !free)) {
                    continue;
                }

                if (SpellTeacher.TryTeach(SessionActor.ActorRef, wizard, spell.Id, randomize: false) == SpellTeachResult.Learned) {
                    VanillaTrainers.Trained(wizard, spell);
                    if (verdict.Cost > 0) {
                        TrainingReset.Record(wizard, spell.Id, verdict.Cost);
                        wizard.UpdateTrainingPoints(wizard.MagicSchoolBehavior.TrainingPoints - verdict.Cost);
                        spent += verdict.Cost;
                    }

                    taught.Add((SpellNameOf(spell.Id), "trainer " + trainer.Name + (verdict.Cost > 0 ? $" ({verdict.Cost} training points)" : "")));
                    learned++;
                }
            }

            if (learned == 0) {
                break;
            }
        }

        if (spent > 0) {
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATETRAINING { TrainingPoints = wizard.MagicSchoolBehavior.TrainingPoints });
        }

        return spent;
    }

    private static List<(VanillaTrainers.Trainer Trainer, VanillaTrainers.Spell Spell)> SchoolTrainerSpells()
        => [
            .. VanillaTrainers.All
                .SelectMany(t => t.Spells.Where(s => s.Kind == "School").Select(s => (Trainer: t, Spell: s)))
                .GroupBy(x => x.Spell.Id).Select(g => g.First()),
        ];

    private static HashSet<uint> BuildRealGameSpells() {
        var ids = new HashSet<uint>();
        try {
            foreach (var spell in VanillaTrainers.All.SelectMany(t => t.Spells)) {
                ids.Add(spell.Id);
            }

            foreach (var inventory in SpiralDB.NpcSpellInventories.Values.Where(i => i?.Spells is not null)) {
                foreach (var entry in inventory.Spells) {
                    ids.Add((uint) entry.TemplateID);
                }
            }

            foreach (var quest in QuestTemplateCollection.GetAllQuests().Where(q => q is not null)) {
                foreach (var spell in SpellResultsOf(quest)) {
                    ids.Add(spell.m_templateID);
                }
            }

            foreach (var recipe in SpellFusions.Recipes) {
                ids.UnionWith(recipe.Outputs);
            }

            foreach (var (id, _) in CantripFactory.PlayerCantrips()) {
                ids.Add(id);
            }
        }
        catch (Exception ex) {
            Logger.Error("Collect Spells | Could not list the real game's spells: {0}", Logger.Args(ex.Message));
        }

        return ids;
    }

    // Spells in the book that no trainer, quest, fusion or cantrip of the real game gives. Spellement upgrades count as
    // real (they grow from a real spell on the upgrade page).
    private static List<(uint Id, string Spell)> NotFromRealGame(Wizard wizard) {
        var real = s_realGameSpells.Value;
        if (real.Count == 0) {
            return [];
        }

        return [
            .. (wizard.SpellbookBehavior.LearnedSpellTemplateIds ?? []).Distinct()
                .Where(id => !real.Contains(id) && CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate and not TieredSpellTemplate)
                .Select(id => (id, SpellNameOf(id)))
                .OrderBy(x => x.Item2, StringComparer.OrdinalIgnoreCase),
        ];
    }

    // The trainer spells of the wizard's school, secondary school, astral schools and shadow magic that they still lack,
    // with what stands in the way. Spells above their level are left out.
    private static List<(string Spell, string Why)> StillMissing(Wizard wizard) {
        var schools = new HashSet<string>(s_alwaysListedSchools, StringComparer.OrdinalIgnoreCase) {
            wizard.MagicSchoolBehavior.MagicSchool.ToString(),
        };
        if (VanillaTrainers.SecondarySchoolOf(wizard) is { } secondary) {
            schools.Add(secondary);
        }

        var level = (int) wizard.MagicSchoolBehavior.Level;
        var missing = new List<(string Spell, string Why)>();
        foreach (var (trainer, spell) in SchoolTrainerSpells()) {
            if (spell.Rank > level || wizard.SpellbookBehavior.HasSpell(spell.Id) || CoreObjectFactory.GetCoreTemplate(spell.Id) is not SpellTemplate template
                || !schools.Contains((string) template.m_sMagicSchoolName ?? spell.School ?? "")) {
                continue;
            }

            var verdict = VanillaTrainers.Judge(wizard, spell, template);
            var why = !verdict.CanTrain ? verdict.Why
                : verdict.Cost > 0 ? $"costs {verdict.Cost} training point{(verdict.Cost == 1 ? "" : "s")} at {trainer.Name}"
                : verdict.Gold > 0 ? $"costs {verdict.Gold} gold at {trainer.Name}"
                : $"free at {trainer.Name}";
            missing.Add((SpellNameOf(spell.Id), why));
        }

        return [.. missing.OrderBy(m => m.Spell, StringComparer.OrdinalIgnoreCase)];
    }

    private static string SpellNameOf(uint id) {
        if (CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate spell) {
            string key = spell.m_displayName;
            var shown = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);

            return string.IsNullOrWhiteSpace(shown) ? (string) spell.m_name ?? $"spell {id}" : shown;
        }

        return $"spell {id}";
    }

    private static string Clean(string text) => (text ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    private static void WriteCollectSpellsResult(ulong charId, List<(string Spell, string From)> taught, List<(uint Id, string Spell)> extras,
        List<(string Spell, string Why)> missing, bool checkedBook, int points) {
        try {
            var text = new StringBuilder()
                .Append("state\tdone\n")
                .Append("written\t").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n')
                .Append("points\t").Append(points).Append('\n')
                .Append("checked\t").Append(checkedBook ? 1 : 0).Append('\n');
            foreach (var (spell, from) in taught) {
                text.Append("spell\t").Append(Clean(spell)).Append('\t').Append(Clean(from)).Append('\n');
            }

            foreach (var (id, spell) in extras) {
                text.Append("extra\t").Append(Clean(spell)).Append('\t').Append(id).Append('\n');
            }

            foreach (var (spell, why) in missing) {
                text.Append("missing\t").Append(Clean(spell)).Append('\t').Append(Clean(why)).Append('\n');
            }

            Directory.CreateDirectory(FAST_FORWARD_FOLDER);
            var path = CollectSpellsResultPath(charId);
            File.WriteAllText(path + ".tmp", text.ToString());
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Collect Spells result could not be written: {0}", Logger.Args(ex.Message));
        }
    }

}
