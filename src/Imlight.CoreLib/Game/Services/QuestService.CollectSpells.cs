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

using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Quests;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Resources;
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
/// allow and that costs no training points or gold. What it taught goes to fast-forward/&lt;wizard id&gt;-spells-result.txt.
/// </summary>
internal partial class QuestService {

    private const string COMPLETE_SUFFIX = "_Complete";
    // Trainer spells that need other trainer spells first are taught in rounds.
    private const int COLLECT_TRAINER_ROUNDS = 12;

    private static string CollectSpellsPlanPath(ulong charId)
        => Path.Combine(FAST_FORWARD_FOLDER, charId.ToString(CultureInfo.InvariantCulture) + "-spells.txt");

    private static string CollectSpellsResultPath(ulong charId)
        => Path.Combine(FAST_FORWARD_FOLDER, charId.ToString(CultureInfo.InvariantCulture) + "-spells-result.txt");

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

        CommandReply("Collect Spells: looking for the spells you should have...");
        var taught = new List<(string Spell, string From)>();
        var trainerSpells = 0;
        try {
            if (kinds.Contains("finished") || kinds.Contains("side")) {
                CollectQuestSpells(wizard, kinds.Contains("finished"), kinds.Contains("side"), taught);
            }

            if (kinds.Contains("trainers")) {
                var before = taught.Count;
                CollectTrainerSpells(wizard, taught);
                trainerSpells = taught.Count - before;
            }
        }
        catch (Exception ex) {
            Logger.Error("Collect Spells for {0} stopped: {1}", Logger.Args(wizard.CharId, ex));
            CommandReply($"Collect Spells stopped: something went wrong on the server ({ex.Message}).");
        }

        WriteCollectSpellsResult(wizard.CharId, taught);
        TryDelete(CollectSpellsPlanPath(wizard.CharId));
        Logger.Information("Collect Spells for {0}: {1} spells taught.", Logger.Args(wizard.CharId, taught.Count));
        CommandReply(taught.Count == 0
            ? "Collect Spells: you already have every spell you should have by now."
            : $"Collect Spells: you learned {taught.Count} spells ({taught.Count - trainerSpells} from quests, {trainerSpells} from trainers). " +
              "Ahziael's Progress page lists them.");
    }

    // The spells of the wizard's finished quests, and of side quests at or below their level they have not done (a side
    // quest with no level of its own is often a later step of a higher chain, so it is left out).
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
            var main = quest.m_mainline || name.Contains("MAIN", StringComparison.OrdinalIgnoreCase);
            if (!(isDone && finished || !isDone && side && !main && quest.m_questLevel > 0 && quest.m_questLevel <= level && ForSchool(quest, school))) {
                continue;
            }

            foreach (var spell in SpellResultsOf(quest)) {
                if (spell.m_requirements?.m_requirements is { Count: > 0 } && !RequirementDispatcher.EvaluateRequirements(spell.m_requirements,
                        new GenericRequirementContext(spell.m_requirements, SessionActor.ActorRef, GetActiveGameObject(), wizard))) {
                    continue;
                }

                if (SpellTeacher.TryTeach(SessionActor.ActorRef, wizard, spell.m_templateID) == SpellTeachResult.Learned) {
                    taught.Add((SpellNameOf(spell.m_templateID), (isDone ? "quest " : "side quest ") + QuestTitleOf(quest)));
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

    // The game trainers' spells the wizard may train now for free: their own school's (and any other free school
    // spell), by level, school, earlier spells, quests and badges. Fishing, gardening and monster magic are left out.
    private void CollectTrainerSpells(Wizard wizard, List<(string Spell, string From)> taught) {
        var spells = VanillaTrainers.All
            .SelectMany(t => t.Spells.Where(s => s.Kind == "School").Select(s => (Trainer: t, Spell: s)))
            .GroupBy(x => x.Spell.Id).Select(g => g.First()).ToList();
        for (var round = 0; round < COLLECT_TRAINER_ROUNDS; round++) {
            var learned = 0;
            foreach (var (trainer, spell) in spells) {
                if (wizard.SpellbookBehavior.HasSpell(spell.Id) || CoreObjectFactory.GetCoreTemplate(spell.Id) is not SpellTemplate template) {
                    continue;
                }

                var verdict = VanillaTrainers.Judge(wizard, spell, template);
                if (!verdict.CanTrain || verdict.Cost > 0 || verdict.Gold > 0) {
                    continue;
                }

                if (SpellTeacher.TryTeach(SessionActor.ActorRef, wizard, spell.Id, randomize: false) == SpellTeachResult.Learned) {
                    VanillaTrainers.Trained(wizard, spell);
                    taught.Add((SpellNameOf(spell.Id), "trainer " + trainer.Name));
                    learned++;
                }
            }

            if (learned == 0) {
                break;
            }
        }
    }

    private static string SpellNameOf(uint id) {
        if (CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate spell) {
            string key = spell.m_displayName;
            var shown = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);

            return string.IsNullOrWhiteSpace(shown) ? (string) spell.m_name ?? $"spell {id}" : shown;
        }

        return $"spell {id}";
    }

    private static void WriteCollectSpellsResult(ulong charId, List<(string Spell, string From)> taught) {
        try {
            static string Clean(string text) => (text ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
            var text = new StringBuilder()
                .Append("state\tdone\n")
                .Append("written\t").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
            foreach (var (spell, from) in taught) {
                text.Append("spell\t").Append(Clean(spell)).Append('\t').Append(Clean(from)).Append('\n');
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
