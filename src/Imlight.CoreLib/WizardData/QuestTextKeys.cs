/*
 * Our own quests in spiraldb-extra (Celestia onward, made from Wizard404, Niduus 2026-10-24) are written with the
 * game's English text: the quest title, what each NPC says, the step titles, places and "defeat X" names. The game
 * shows a quest's words by the key of its own line (WizQst1ED97_00000002), so at boot each English line is swapped for
 * the key of the game line that says the same thing. A line the game does not have is kept as it is.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.WizardData;

public static partial class QuestTextKeys {

    [GeneratedRegex(@"^[A-Za-z0-9\-]+_[A-Za-z0-9]+$")]
    private static partial Regex KeyPattern();

    private static bool IsText(string value)
        => !string.IsNullOrWhiteSpace(value) && !KeyPattern().IsMatch(value.Trim());

    /// <summary>Gives every quest line written in English the key of the game's own line. Runs once after SpiralDB loads.</summary>
    public static void ResolveAll(IReadOnlyList<QuestTemplate> quests) {
        try {
            Resolve(quests);
        } catch (Exception ex) {
            Logger.Warning("Quest text could not be matched to the game's lines: {0}", Logger.Args(ex.Message));
        } finally {
            Locale.ReleaseQuestTables();
        }
    }

    private static IEnumerable<NPCDialogEntry> DialogEntries(ActorDialogListBase list)
        => (list as ActorDialogList)?.m_dialogs?
            .SelectMany(d => d.m_dialogEntries ?? [])
            .OfType<NPCDialogEntry>() ?? [];

    private static void Resolve(IReadOnlyList<QuestTemplate> quests) {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        void Want(string value) {
            if (IsText(value)) {
                wanted.Add(Locale.PlainText(value));
            }
        }

        var ours = quests.Where(q => q is not null && (IsText(q.m_questTitle)
            || DialogEntries(q.m_dialogList).Any(e => IsText(e.m_dialog))
            || q.m_goals.Any(g => IsText(g.m_goalTitle) || IsText(g.m_locationName)
                || IsText(g.m_tallyCounter?.m_descriptor) || IsText(g.m_tallyCounter?.m_descriptor2)
                || DialogEntries(g.m_dialogList).Any(e => IsText(e.m_dialog))))).ToList();
        if (ours.Count == 0) {
            return;
        }

        foreach (var quest in ours) {
            Want(quest.m_questTitle);
            foreach (var entry in DialogEntries(quest.m_dialogList)) {
                Want(entry.m_dialog);
            }

            foreach (var goal in quest.m_goals) {
                Want(goal.m_goalTitle);
                Want(goal.m_locationName);
                Want(goal.m_tallyCounter?.m_descriptor);
                Want(goal.m_tallyCounter?.m_descriptor2);
                foreach (var entry in DialogEntries(goal.m_dialogList)) {
                    Want(entry.m_dialog);
                }
            }
        }

        var keys = Locale.KeysOfTexts(wanted);
        int matched = 0, kept = 0;

        string Pick(string value, string homeTable, params string[] tables) {
            if (!IsText(value)) {
                return value;
            }

            if (!keys.TryGetValue(Locale.PlainText(value), out var found) || found.Count == 0) {
                kept++;

                return value;
            }

            matched++;
            foreach (var table in (homeTable is null ? tables : tables.Prepend(homeTable))) {
                var key = found.FirstOrDefault(k => k.StartsWith(table + "_", StringComparison.Ordinal)
                    || table.EndsWith('*') && k.StartsWith(table.TrimEnd('*'), StringComparison.Ordinal));
                if (key is not null) {
                    return key;
                }
            }

            return found[0];
        }

        foreach (var quest in ours) {
            // The quest's own dialogue table is the one most of its lines are in.
            var allLines = DialogEntries(quest.m_dialogList)
                .Concat(quest.m_goals.SelectMany(g => DialogEntries(g.m_dialogList)))
                .Select(e => (string) e.m_dialog)
                .Concat(quest.m_goals.SelectMany(g => new[] { (string) g.m_tallyCounter?.m_descriptor, (string) g.m_tallyCounter?.m_descriptor2 }))
                .Where(IsText)
                .ToList();
            var home = allLines
                .SelectMany(line => keys.TryGetValue(Locale.PlainText(line), out var k) ? k.Select(x => x[..x.LastIndexOf('_')]).Distinct() : [])
                .Where(t => t.StartsWith("WizQst", StringComparison.Ordinal))
                .GroupBy(t => t)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault()?.Key;

            quest.m_questTitle = Pick(quest.m_questTitle, null, "QuestTitle", "QuestTitle*");
            foreach (var entry in DialogEntries(quest.m_dialogList)) {
                entry.m_dialog = Pick(entry.m_dialog, home, "WizQst*");
            }

            foreach (var goal in quest.m_goals) {
                goal.m_goalTitle = Pick(goal.m_goalTitle, null, "WizardQuestGoals");
                goal.m_locationName = Pick(goal.m_locationName, null, "ZoneLocName", "WizardZone", "WizQst*");
                if (goal.m_tallyCounter is { } tally) {
                    tally.m_descriptor = Pick(tally.m_descriptor, home, "WizardMobs", "WizItems", "WizardGameObjects", "WizQst*");
                    tally.m_descriptor2 = Pick(tally.m_descriptor2, home, "WizardMobs", "WizItems", "WizardGameObjects", "WizQst*");
                }

                foreach (var entry in DialogEntries(goal.m_dialogList)) {
                    entry.m_dialog = Pick(entry.m_dialog, home, "WizQst*");
                }
            }
        }

        Logger.Information("Quest text: {0} English lines of {1} quests got the game's own line, {2} the game does not have stay as written.",
            Logger.Args(matched, ours.Count, kept));
    }

}
