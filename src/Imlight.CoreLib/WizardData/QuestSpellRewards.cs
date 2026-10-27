/*
 * Many quests of the real game teach a spell when they are done: the school spells of Grim Tales or Walpurgisnacht,
 * Ra for Balance wizards, Plow and Revive in Growing Pains. Our quests rebuilt from Wizard404 (and some captured
 * ones) lack those results, so nobody learned the spell. spiraldb-extra/QuestSpellRewards.json lists them; at boot
 * each one is added to that quest's completion results (Niduus 2026-10-27).
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;

namespace Imlight.CoreLib.WizardData;

public static class QuestSpellRewards {

    internal const string FILE_NAME = "QuestSpellRewards.json";

    private sealed record Entry(string Quest, string School, uint Spell, string Name);

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Adds the listed spells to their quests' completion results. Runs once after SpiralDB loads.</summary>
    public static void ApplyAll(IReadOnlyList<QuestTemplate> quests, string extraPath) {
        try {
            var path = Path.Combine(extraPath, FILE_NAME);
            if (!File.Exists(path)) {
                return;
            }

            var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path), s_options) ?? [];
            var byName = quests.Where(q => q?.m_questName is not null)
                .GroupBy(q => (string) q.m_questName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var added = 0;
            foreach (var entry in entries) {
                if (entry?.Quest is null || entry.Spell == 0 || !byName.TryGetValue(entry.Quest, out var quest)) {
                    continue;
                }

                var results = quest.m_endResults ??= new ResultList();
                results.m_results ??= [];
                var school = string.IsNullOrWhiteSpace(entry.School) ? null : entry.School.Trim();
                if (results.m_results.OfType<ResLearnSpell>().Any(r => r.m_templateID == entry.Spell && SchoolOf(r) == school)) {
                    continue;
                }

                results.m_results.Add(new ResLearnSpell {
                    m_templateID = entry.Spell,
                    m_requirements = school is null ? null : new RequirementList {
                        m_requirements = [new ReqSchoolOfFocus { m_magicSchool = school, m_operator = Operator.ROP_AND }],
                        m_operator = Operator.ROP_AND,
                    },
                });
                added++;
            }

            Logger.Information("Quest spells: added {0} spell rewards to quests.", Logger.Args(added));
        } catch (Exception ex) {
            Logger.Warning("Quest spells could not be added: {0}", Logger.Args(ex.Message));
        }
    }

    private static string SchoolOf(ResLearnSpell result)
        => result.m_requirements?.m_requirements?.OfType<ReqSchoolOfFocus>().Select(r => (string) r.m_magicSchool).FirstOrDefault();

}
