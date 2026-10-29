/*
 * Player minions and Sap (Niduus, 2026-10-29: "Implement Minion system along with their "sap" mechanic").
 *
 * What the game files say (Root.wad Spells/*.xml, read 2026-10-29):
 *   Summon      kSummonCreature, m_effectParam = the creature (Animate, Fire Elemental, Cyclops ...; X-pip ones pick it by
 *               pips). The minion fights on its summoner's side with its own AI (CombatCreatureAIComponent).
 *   Sap         "Sap Health" (Draw Health 350, Drain Health 450) and "Sap Power" (Draw Power, Take Power: 2 pips): a
 *               kInstantKill on kMinion (the minion is sacrificed), then kHeal or kModifyPips on kSelf.
 *   Utilities   Mend Minion (kHeal on kMinion), Buff Minion (kModifyOutgoingDamage), Shield Minion
 *               (kModifyIncomingDamage), Steal Health (kStealHealth: hurts the minion, heals the caster), Dimension
 *               Shift (kSwapAll), Minion Reshuffle (kReshuffle on kFriendlyMinion). These are plain effects aimed at
 *               the minion (CombatActionResolver.GetEffectTargets) and needed nothing new.
 *   Taunt       Mega Taunt and a minion's Taunt: kTaunt on kSelf ("Threat Self"); Mega Pacify family: kPacify on
 *               kSelf or kFriendlySingleNotMe. The enemies' AI now reads those (CombatCreatureAIComponent).
 *
 * The rules here, each one a [Minions] setting that applies right away:
 *   SapNeedsMinion   a Sap card with no minion to sacrifice does nothing: it plays as a pass and costs no pips.
 *   OnePerWizard     a wizard who has a minion standing cannot summon another (the card plays as a pass).
 *   LeaveWithOwner   a minion a wizard's card summoned leaves the battle when its wizard is defeated or leaves (flees, logs out).
 *   Taunt            Taunt and Pacify on one's own side change whom the enemies attack.
 */

using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Game.Combat;

internal static class PlayerMinions {

    internal static bool SapNeedsMinion => ConfigurationManager.GetValue("Minions.SapNeedsMinion", true);
    internal static bool OnePerWizard => ConfigurationManager.GetValue("Minions.OnePerWizard", true);
    internal static bool LeaveWithOwner => ConfigurationManager.GetValue("Minions.LeaveWithOwner", true);
    internal static bool Taunt => ConfigurationManager.GetValue("Minions.Taunt", true);

    private static bool IsMinionTarget(kEffectTarget target)
        => target is kEffectTarget.kMinion or kEffectTarget.kFriendlyMinion or kEffectTarget.kCasterMinion;

    /// <summary>
    /// The minions standing for this owner. summonedOnly: only the ones its own cards summoned (not a cheat's minion).
    /// </summary>
    internal static IEnumerable<CombatDuelSubCircle> OwnMinions(CombatDuelSubCircle owner, bool summonedOnly = false)
        => owner?._duelActor?.ActiveSubCircles.Where(c => c is not null && c != owner && c.IsSummonedMinion && c.IsAlive
                                                          && c.CombatParticipant?.m_minionSubCircle == owner.SlotIndex
                                                          && (summonedOnly ? c.MinionOwnerId == owner.ParticipantObject?.m_globalID.Full
                                                              : c.MinionOwnerId == 0 || c.MinionOwnerId == owner.ParticipantObject?.m_globalID.Full))
           ?? [];

    /// <summary>
    /// Why this cast does nothing under the minion rules, or null when it may go off. A cast with a reason plays as a pass.
    /// </summary>
    internal static string CastBlocked(QueuedCombatAction action) {
        var caster = action?.SpellCaster;
        if (caster is null || action.SpellTemplate?.m_effects is not { } effects) {
            return null;
        }

        var leaves = CombatEnchantments.Leaves(effects).ToList();

        // Sap: the minion it sacrifices is the card's target when that is a minion on the caster's side, else the
        // caster's own (CombatActionResolver.GetEffectTargets).
        if (SapNeedsMinion && leaves.Any(e => e.m_effectType == kSpellEffects.kInstantKill && IsMinionTarget(e.m_effectTarget))) {
            var picked = action.SelectedTarget is { IsSummonedMinion: true, IsAlive: true } t && t.OccupiedTeam == caster.FightsFor;
            if (!picked && !OwnMinions(caster).Any()) {
                return "no minion to sacrifice (Minions.SapNeedsMinion)";
            }
        }

        if (OnePerWizard && caster.IsHumanPlayer
            && leaves.Any(e => e.m_effectType is kSpellEffects.kSummonCreature or kSpellEffects.kSpawnCreature)
            && OwnMinions(caster, summonedOnly: true).Any()) {
            return "the wizard has a minion already (Minions.OnePerWizard)";
        }

        return null;
    }

    /// <summary>
    /// The minions of wizards who are defeated or no longer in the battle (LeaveWithOwner). A creature's minions stay.
    /// </summary>
    internal static List<CombatDuelSubCircle> Orphaned(IEnumerable<CombatDuelSubCircle> circles) {
        if (!LeaveWithOwner) {
            return [];
        }

        var all = circles.Where(c => c is not null).ToList();

        return [.. all.Where(m => m.IsSummonedMinion && m.IsAlive && m.MinionOwnerId != 0
                                 && !all.Any(o => o.IsHumanPlayer && o.IsAlive && o.ParticipantObject.m_globalID.Full == m.MinionOwnerId))];
    }
}
