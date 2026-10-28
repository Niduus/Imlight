/*
 * Most quests captured from the live game keep what an NPC says on a "talk to" step under the dialog tag
 * "Hyperlink", with no "Completion" lines at all (Mortis's whole Darkmoor speech in "The Exalted Lord", Gretta in
 * "Gotta See Gretta"). The server only ever played "Completion", so talking to them finished the step in silence:
 * 661 steps of 443 side quests and 158 steps of main quests (Niduus 2026-10-28: "a lot of side quests are missing
 * dialog outright"). At boot each such step gets those lines as its "Completion" lines too, so everything that
 * plays or replays a step's lines (the talk itself, the quest book's Review Dialog) finds them.
 */

using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;

namespace Imlight.CoreLib.WizardData;

public static class QuestTalkDialog {

    private static readonly string COMPLETION_TAG = "Completion";
    private static readonly string HYPERLINK_TAG = "Hyperlink";

    /// <summary>Gives every talk step that has "Hyperlink" lines but no "Completion" lines those lines. Runs once after SpiralDB loads.</summary>
    public static void ApplyAll(System.Collections.Generic.IReadOnlyList<QuestTemplate> quests) {
        var steps = 0;
        var changedQuests = 0;
        foreach (var quest in quests) {
            var changed = false;
            foreach (var goal in (quest?.m_goals ?? []).OfType<PersonaGoalTemplate>()) {
                if (goal.m_dialogList is not ActorDialogList list || list.m_dialogs is null) {
                    continue;
                }

                bool HasLines(ActorDialog d) => d?.m_dialogEntries is { Count: > 0 };
                if (list.m_dialogs.Any(d => d is not null && d.m_dialogTag == COMPLETION_TAG && HasLines(d))) {
                    continue;
                }

                var hyperlink = list.m_dialogs.FirstOrDefault(d => d is not null && d.m_dialogTag == HYPERLINK_TAG && HasLines(d));
                if (hyperlink is null) {
                    continue;
                }

                // An empty "Completion" dialog would be found first and play nothing.
                list.m_dialogs.RemoveAll(d => d is not null && d.m_dialogTag == COMPLETION_TAG);
                list.m_dialogs.Add(new ActorDialog {
                    m_dialogTag = COMPLETION_TAG,
                    m_dialogEntries = hyperlink.m_dialogEntries,
                    m_madlibs = hyperlink.m_madlibs,
                });
                steps++;
                changed = true;
            }

            if (changed) {
                changedQuests++;
            }
        }

        if (steps > 0) {
            Logger.Information("Quest talk lines: {0} talk steps of {1} quests now say their lines (they were kept as Hyperlink only).",
                Logger.Args(steps, changedQuests));
        }
    }

}
