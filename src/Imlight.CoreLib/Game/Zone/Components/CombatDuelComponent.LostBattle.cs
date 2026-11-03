/*
 * A boss defeated in a battle the creatures won comes back (Niduus, 2026-11-02, Fire Globe Theater: "the boss is supposed
 * to respawn if you don't beat them for your quest but kill them and die in battle").
 *
 * Inside an instance a spawn point whose creature was defeated never refills (ZonePath._defeatedInInstance). That held
 * even when the battle was lost: he beat Alicane Swiftarrow, the Fire Elf with him beat the wizard, and on Dungeon Recall
 * the theater was empty while "Defeat Alicane Swiftarrow" was still to do. On live a loss gives no credit (quest steps
 * and the zone's own "Monster_Killed" triggers only count a won battle), and the game's files keep Alicane's spawn point
 * on until such a won battle: the theater's "Trigger MobSpawnFix OnDeath" (Monster_Killed of Alicane) is what despawns
 * him and switches his spawn point off. So a lost battle leaves him there for the next try.
 *
 * Now, when the creatures win a battle in an instance, every creature defeated in it that is a boss, or that a wizard of
 * the battle still has an open "defeat" step for, comes back to its spot like the creatures that lived (a few seconds
 * later, full health). Other defeated monsters stay gone, as before.
 */

using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed partial class CombatDuelComponent {

    /// <summary>
    /// The global ids of the creatures defeated in this lost battle that come back: bosses and creatures a wizard of the
    /// battle still needs for a quest. Only inside an instance (elsewhere spawn points refill on their own).
    /// </summary>
    private HashSet<ulong> DefeatedThatComeBack() {
        var back = new HashSet<ulong>();
        if (Entity.Zone?.IsInstance != true) {
            return back;
        }

        var wizards = new List<Wizard>();
        EnactActionOnSubCircles(circle => {
            if (circle.IsHumanPlayer && circle._wizard is { } wizard) {
                wizards.Add(wizard);
            }
        });

        EnactActionOnSubCircles(circle => {
            if (circle.OccupiedTeam != CombatTeam.Monster || circle.IsSummonedMinion || circle.IsAlive
                || circle.ParticipantObject is not { } creature) {
                return;
            }

            try {
                var why = WhyItComesBack(creature, wizards);
                if (why is null) {
                    return;
                }

                back.Add(creature.m_globalID.Full);
                Logger.Information("Duel {0} | {1} was defeated but the battle was lost; it comes back ({2}).",
                    Logger.Args(Duel.m_duelID.Full, NameOf(creature), why));
            }
            catch (System.Exception e) {
                Logger.Error("Duel {0} | Could not tell whether a defeated creature comes back: {1}", Logger.Args(Duel.m_duelID.Full, e.Message));
            }
        });

        return back;
    }

    private static string WhyItComesBack(CoreObject creature, List<Wizard> wizards) {
        var templates = new List<GameObjectTemplate>();
        if (CoreObjectFactory.GetCoreTemplate(creature.m_templateID) is GameObjectTemplate own) {
            templates.Add(own);
        }

        // A creature the randomizer swapped in also stands for the one its spawn point asked for.
        if (Randomization.Randomizer.TryPeekReplacedCreature(creature.m_globalID.Full, out var replacedId)
            && CoreObjectFactory.GetCoreTemplate(replacedId) is GameObjectTemplate replaced) {
            templates.Add(replaced);
        }

        if (templates.Any(t => t.m_behaviors?.OfType<NPCBehaviorTemplate>().Any(npc => npc.IsBoss()) == true)) {
            return "a boss";
        }

        var adjectives = templates.SelectMany(t => (t.m_adjectiveList ?? []).Select(a => (string) a)).ToHashSet();
        foreach (var wizard in wizards) {
            foreach (var instance in wizard.QuestBehavior?.CurrentQuestInstances ?? []) {
                var goals = SpiralDB.GetQuestByName(instance.QuestName)?.m_goals;
                if (goals is null) {
                    continue;
                }

                foreach (var goal in goals.OfType<BountyGoalTemplate>()) {
                    if (!instance.IsGoalActive((string) goal.m_goalName)) {
                        continue;
                    }

                    var wanted = (goal.m_npcAdjectives ?? []).Select(a => (string) a).ToList();
                    if (wanted.Any(adjectives.Contains)
                        || templates.Any(t => Quests.BountyStandIns.Counts(wanted, t.m_templateID))) {
                        return $"{wizard.CharId} still needs it for {instance.QuestName}";
                    }
                }
            }
        }

        return null;
    }

    private static string NameOf(CoreObject creature)
        => (CoreObjectFactory.GetCoreTemplate(creature.m_templateID) as GameObjectTemplate)?.m_objectName.ToString()
           ?? creature.m_templateID.ToString();

}
