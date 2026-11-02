/*
 * Many quests of the real game teach a spell when they are done: the school spells of Grim Tales or Walpurgisnacht,
 * Ra for Balance wizards, Plow and Revive in Growing Pains. Our quests rebuilt from Wizard404 (and some captured
 * ones) lack those results, so nobody learned the spell. spiraldb-extra/QuestSpellRewards.json lists them; at boot
 * each one is added to that quest's completion results (Niduus 2026-10-27).
 *
 * Some quests teach the spell part way through: King Artorius after talking to the King in Letters of Light, Scion
 * after the Root Guardian in Finally Growing Into It. "goal" names that step; the spell is taught when the step is
 * done and is also kept on the completion, so wizards who already finished the quest get it from the catch-up
 * rewards (learning a known spell does nothing). "badge" instead of "spell" gives a badge at the end, for quests
 * whose badge (Initiate Shadowmancer, Scion of Bartleby, Order of the Fang) no result handed out (Niduus 2026-11-02).
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

    private sealed record Entry(string Quest, string School, uint Spell, string Name, string Goal = null, string Badge = null);

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

            // Our quests rebuilt from Wizard404 name their spell in a combat "add a card to the hand" result with no
            // template; it would run at quest end and teach nothing. The list's learn results stand in for it.
            var removed = 0;
            foreach (var quest in byName.Values) {
                removed += quest.m_endResults?.m_results?.RemoveAll(r => r is ResAddSpell { m_templateID: 0 }) ?? 0;
            }

            var added = 0;
            foreach (var entry in entries) {
                if (entry?.Quest is null || !byName.TryGetValue(entry.Quest, out var quest)) {
                    continue;
                }

                var results = quest.m_endResults ??= new ResultList();
                results.m_results ??= [];
                if (!string.IsNullOrWhiteSpace(entry.Badge)) {
                    var badge = entry.Badge.Trim();
                    if (!results.m_results.OfType<ResAddBadge>().Any(r => (string) r.m_badgeName == badge)) {
                        results.m_results.Add(new ResAddBadge { m_badgeName = badge });
                        added++;
                    }

                    continue;
                }

                if (entry.Spell == 0) {
                    continue;
                }

                var school = string.IsNullOrWhiteSpace(entry.School) ? null : entry.School.Trim();
                if (AddLearn(results, entry.Spell, school)) {
                    added++;
                }

                if (!string.IsNullOrWhiteSpace(entry.Goal)) {
                    var goal = quest.m_goals?.FirstOrDefault(g => g is not null
                        && string.Equals((string) g.m_goalName, entry.Goal.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (goal is null) {
                        Logger.Warning("Quest spells: quest {0} has no step {1}; {2} is taught at the end only.",
                            Logger.Args(entry.Quest, entry.Goal, entry.Name));
                    } else {
                        var goalResults = goal.m_completeResults ??= new ResultList();
                        goalResults.m_results ??= [];
                        AddLearn(goalResults, entry.Spell, school);
                    }
                }
            }

            Logger.Information("Quest spells: added {0} spell rewards to quests, removed {1} empty ones.", Logger.Args(added, removed));
        } catch (Exception ex) {
            Logger.Warning("Quest spells could not be added: {0}", Logger.Args(ex.Message));
        }
    }

    private static bool AddLearn(ResultList results, uint spell, string school) {
        if (results.m_results.OfType<ResLearnSpell>().Any(r => r.m_templateID == spell && SchoolOf(r) == school)) {
            return false;
        }

        results.m_results.Add(new ResLearnSpell {
            m_templateID = spell,
            m_requirements = school is null ? null : new RequirementList {
                m_requirements = [new ReqSchoolOfFocus { m_magicSchool = school, m_operator = Operator.ROP_AND }],
                m_operator = Operator.ROP_AND,
            },
        });

        return true;
    }

    private static string SchoolOf(ResLearnSpell result)
        => result.m_requirements?.m_requirements?.OfType<ReqSchoolOfFocus>().Select(r => (string) r.m_magicSchool).FirstOrDefault();

}
