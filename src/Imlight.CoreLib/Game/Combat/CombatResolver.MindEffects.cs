/*
 * Beguile, Confuse and Spy (Niduus, 2026-10-28: "program Beguile, Confuse and Spy"). Before this the server did
 * nothing with kMindControl, kConfusion or kClue, so the cards only played their animation.
 *
 * The numbers come from the game files (Root.wad Spells/*.xml):
 *   Beguile       kMindControl, m_effectParam = rounds (Beguile 1, KH Beguile 2, Beguile - 3R Star 3, Mass Beguile on
 *                 every enemy). The target fights for the caster's side on its next turns: its attacks go on its own
 *                 side and its heals and buffs go on the caster's side. A boss set to Beguilable: No shrugs it off.
 *   Confuse       kConfusion, m_effectParam = chance in percent, m_numRounds = rounds (Confuse 100% 3 rounds,
 *                 Confusion 30 - 2R 30% 2 rounds, Medulla 75% 2 rounds). On each of its turns the target's spell has that
 *                 chance to go on a random wizard or creature instead of the one it picked. Spells on a whole side and
 *                 on itself are left alone. A confusion block (kConfusionBlock) on the target stops it once.
 *   Spy           kClue, m_effectParam = rounds (Arcane Deduction 3, Legendary Spy 99). The target shows "Spied" in its
 *                 effects bubble (GUI2.lang CluedRounds); the game reads m_clue for what it shows.
 *
 * Both sides can use all three: a wizard's card on a creature, a creature's card on a wizard (KH Beguile,
 * Beguile-AZBoss, Confusion - Medulla), and a boss cheat that casts one of these spells.
 *
 * A beguiled or confused wizard still picks a card; the server sends it to a fitting target if the pick no longer fits.
 * The participant's side for everything else (whose turn it is, the end of the battle, drops) does not change.
 */

using System;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Combat;

public partial class CombatResolver {

    private static string NameOf(CombatDuelSubCircle who)
        => (string) who?.ParticipantObject?.m_debugName is { Length: > 0 } name ? name : $"slot {who?.SlotIndex}";

    /// <summary>Beguile (kMindControl): each target fights for the caster's side for m_effectParam of its turns.</summary>
    internal static void ApplyMindControl(SpellEffect effect, CombatDuelSubCircle caster, CombatDuelSubCircle[] targets) {
        var rounds = Math.Max(1, effect.m_effectParam);
        foreach (var target in targets.Where(t => t?.CombatParticipant is not null && t.IsAlive && t != caster)) {
            if (!target.CanBeBeguiled) {
                Logger.Information("Beguile: {0} can't be beguiled (Bosses tab).", Logger.Args(NameOf(target)));

                continue;
            }

            var participant = target.CombatParticipant;
            participant.m_mindcontrolled = Math.Max(participant.m_mindcontrolled, rounds);
            participant.m_mindcontrolledDisplay = true;
            Logger.Information("Beguile: {0} fights for the {1} side for {2} round(s).",
                Logger.Args(NameOf(target), target.FightsFor, participant.m_mindcontrolled));
        }
    }

    /// <summary>Confuse (kConfusion): m_effectParam percent chance each turn, for m_numRounds turns.</summary>
    internal static void ApplyConfusion(SpellEffect effect, CombatDuelSubCircle[] targets) {
        var rounds = Math.Max(1, effect.m_numRounds);
        var chance = Math.Clamp(effect.m_effectParam, 1, 100);
        foreach (var target in targets.Where(t => t?.CombatParticipant is not null && t.IsAlive)) {
            var block = target._hangingEffects?.FirstOrDefault(x => x.m_effectType == kSpellEffects.kConfusionBlock);
            if (block is not null) {
                CombatCharms.RemoveExact(target._hangingEffects, [block]);
                Logger.Information("Confuse: {0}'s confusion block stops it.", Logger.Args(NameOf(target)));

                continue;
            }

            var participant = target.CombatParticipant;
            participant.m_confused = Math.Max(participant.m_confused, rounds);
            participant.m_confusionDisplay = true;
            target.ConfusionChance = Math.Max(target.ConfusionChance, chance);
            Logger.Information("Confuse: {0} is confused for {1} round(s), {2}% chance each turn.",
                Logger.Args(NameOf(target), participant.m_confused, target.ConfusionChance));
        }
    }

    /// <summary>Spy (kClue): the target is Spied for m_effectParam rounds.</summary>
    internal static void ApplySpied(SpellEffect effect, CombatDuelSubCircle[] targets) {
        var rounds = Math.Max(1, effect.m_effectParam);
        foreach (var target in targets.Where(t => t?.CombatParticipant is not null && t.IsAlive)) {
            target.CombatParticipant.m_clue = Math.Max(target.CombatParticipant.m_clue, rounds);
            Logger.Information("Spy: {0} is Spied for {1} round(s).", Logger.Args(NameOf(target), target.CombatParticipant.m_clue));
        }
    }

    /// <summary>
    /// Before a beguiled or confused participant's spell goes off: a confused one may send a spell on one target to a
    /// random wizard or creature, and a beguiled one's spell goes on a target on the side it now fights for.
    /// </summary>
    private void AimMindAffected(QueuedCombatAction action) {
        var caster = action?.SpellCaster;
        var participant = caster?.CombatParticipant;
        if (participant is null || action.Spell is null || action.SelectedTarget is null
            || participant.m_mindcontrolled <= 0 && participant.m_confused <= 0) {
            return;
        }

        try {
            var spell = action.SpellTemplate ?? CoreObjectFactory.GetCoreTemplate(action.Spell.m_templateID) as SpellTemplate;
            var aim = AllEffects(spell).Select(e => e.m_effectTarget).Where(s_onePicked.Contains).Cast<kEffectTarget?>().FirstOrDefault();
            if (aim is not { } kind) {
                return;
            }

            var wantsEnemy = kind is kEffectTarget.kEnemySingle or kEffectTarget.kPreselectedEnemySingle;
            var revive = CanGoOnFallen(spell);
            var standing = caster._duelActor.ActiveSubCircles.Where(c => c.Occupied && c.AddedToDuel && c.IsAlive).ToList();

            // Confused: the chance to go on anyone standing (not the caster for an attack or a "not me" spell).
            if (participant.m_confused > 0 && kind != kEffectTarget.kFriendlySingleNotMe
                && Random.Shared.Next(100) < Math.Clamp(caster.ConfusionChance, 0, 100)) {
                var anyone = standing.Where(c => c != caster || !wantsEnemy).ToList();
                if (anyone.Count > 0) {
                    var picked = anyone[Random.Shared.Next(anyone.Count)];
                    Logger.Information("Duel {0} | Confuse: {1}'s {2} goes on {3} instead of {4}.",
                        Logger.Args(_duel.m_duelID.Full, NameOf(caster), (string) spell.m_name, NameOf(picked), NameOf(action.SelectedTarget)));
                    action.SelectedTarget = picked;
                    action.Confused = true;

                    return;
                }
            }

            if (participant.m_mindcontrolled <= 0) {
                return;
            }

            // Beguiled: keep the pick if it fits the side the caster fights for now, else a random one that does.
            var side = caster.FightsFor;
            bool Fits(CombatDuelSubCircle c) => wantsEnemy ? c != caster && c.OccupiedTeam != side
                : c.OccupiedTeam == side && (kind != kEffectTarget.kFriendlySingleNotMe || c != caster);
            var target = action.SelectedTarget;
            if (Fits(target) && (target.IsAlive || revive)) {
                return;
            }

            var fitting = standing.Where(Fits).ToList();
            if (fitting.Count == 0) {
                return;
            }

            var chosen = fitting[Random.Shared.Next(fitting.Count)];
            Logger.Information("Duel {0} | Beguile: {1} fights for the {2} side, so {3} goes on {4} instead of {5}.",
                Logger.Args(_duel.m_duelID.Full, NameOf(caster), side, (string) spell.m_name, NameOf(chosen), NameOf(target)));
            action.SelectedTarget = chosen;
        }
        catch (Exception e) {
            Logger.Error("Duel {0} | Beguile/Confuse aim failed: {1}", Logger.Args(_duel.m_duelID.Full, e));
        }
    }

    // After a participant's turn: a round of beguile and confusion is used up.
    private static void CountDownMindEffects(CombatDuelSubCircle who) {
        if (who?.CombatParticipant is not { } participant) {
            return;
        }

        if (participant.m_mindcontrolled > 0 && --participant.m_mindcontrolled == 0) {
            participant.m_mindcontrolledDisplay = false;
            Logger.Information("Beguile: {0} is back on its own side.", Logger.Args(NameOf(who)));
        }

        if (participant.m_confused > 0 && --participant.m_confused == 0) {
            participant.m_confusionDisplay = false;
            who.ConfusionChance = 0;
            Logger.Information("Confuse: {0} is no longer confused.", Logger.Args(NameOf(who)));
        }
    }

    // At the end of the round: a round of Spied is used up.
    private void CountDownSpied() {
        foreach (var circle in _subCircles.Where(c => c?.CombatParticipant is { m_clue: > 0 })) {
            circle.CombatParticipant.m_clue--;
        }
    }
}
