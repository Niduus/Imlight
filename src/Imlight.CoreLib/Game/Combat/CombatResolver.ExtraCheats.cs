/*
 * More Dos from the cheat ideas list (Niduus, 2026-10-28: "approved", the list "Cheat ideas the server could do"). Each
 * works in a player's cheats and a creature's alike.
 *
 *   ConvertEffects  Count (0: all) of Target's (Self) effects of Kind (and EffectSchool, Spell) come off, and each comes back
 *                   as one of To (Blade, Weakness, Shield, Trap, Absorb) of the same size and school, drawn by the game as
 *                   "add an effect of your own size" draws one. "Turn every trap on me into a blade."
 *   CopyEffects     Count (0: all) of Target's (Caster) effects of Kind are copied onto it (Self), the same size and school;
 *                   Target keeps them.
 *   ScaleEffects    Target's (Self) effects of Kind get Amount percent of their size (200: double, 50: half). Only the
 *                   server knows: the game keeps showing the old number, but hits count the new one.
 *   EffectsSchool   Target's (Self) effects of Kind become of School ("" or All: universal). Server only, as above.
 *   ProtectEffects  Target's (Self) effects of Kind can't be removed (Kind Off: they can again).
 *   Damage          Target (Caster) loses Amount health (Percent: a share of its most health), never below 1.
 *   Drain           Target (Caster) loses Amount health (Percent: of its most) and it gets as much back.
 *
 * Whens after each move (the move's spell is "the spell that set it off"; "the attacker" the one who moved, "the one hit"
 * the one it happened to). RemovedFrom picks whose ("" itself, AnyAlly, AnyEnemy, Anyone), RemovedKind and RemovedSpell
 * which effects, as for EffectRemoved:
 *   EffectAdded     a charm, ward, aura or over-time is put on (after the spell lands, unlike EnemyCasts).
 *   EffectUsed      one is used up by a hit or a heal (a shield popped, a blade spent), not taken off by a spell.
 *   Healed          its health went up.         Stunned: it was stunned by the move.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Spells;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Combat;

public partial class CombatResolver {

    private bool TryExtraCheatSteps(CombatDuelSubCircle boss, BossCheats.BossCheat cheat, BossCheats.CheatAction step,
                                    CombatDuelSubCircle trigger, CombatDuelSubCircle receiver, string bossName,
                                    CombatActionListObj combatActionList, float cinematicTimeSoFar, bool custom, out float cinematicTime) {
        cinematicTime = 0;
        List<CombatDuelSubCircle> On(string fallback) => StepTargets(boss, step.Target.Length > 0 ? step.Target : fallback, null, trigger, receiver);
        var kinds = (step.Kind ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Fits(SpellEffect e) => e is not null && (kinds.Length == 0 || kinds.Any(k => IsKind(e, k)))
                                    && EffectSchoolIs(e, step.EffectSchool) && FromSpell(e, step.Spell);
        var duel = _duel.m_duelID.Full;

        switch (step.Do.ToLowerInvariant()) {
            case "converteffects":
            case "copyeffects": {
                var copying = step.Is("CopyEffects");
                var redo = new List<BossCheats.CheatAction>();
                foreach (var target in On(copying ? "Caster" : "Self").Where(t => t?._hangingEffects is not null && t.IsAlive)) {
                    var picked = target._hangingEffects.Where(e => Fits(e) && (copying || !e.m_protected) && SizedKind(e) is not null)
                        .Reverse().Take(step.Count > 0 ? step.Count : int.MaxValue).ToList();
                    if (!copying) {
                        var gone = picked.ToHashSet(ReferenceEqualityComparer.Instance);
                        target._hangingEffects.RemoveAll(gone.Contains);
                        _clearedCount += gone.Count;
                    }

                    foreach (var e in picked) {
                        var school = ((string) e.m_sDamageType ?? "").Trim();
                        redo.Add(new BossCheats.CheatAction {
                            Do = "AddCharm",
                            Amount = Math.Abs(e.m_effectParam),
                            School = school.Equals("All", StringComparison.OrdinalIgnoreCase) ? "" : school,
                            Kind = copying ? SizedKind(e) : (step.To ?? "").Trim().Length > 0 ? step.To.Trim() : "Blade",
                            Target = copying ? $"slot:{boss.SlotIndex}" : $"slot:{target.SlotIndex}",
                            Silent = step.Silent,
                            Tag = step.Tag,
                        });
                    }

                    Logger.Information("Duel {0} | Slot {1} | {2}: {3} {4} effects of slot {5}{6}.",
                        Logger.Args(duel, boss.SlotIndex, bossName, copying ? "copies" : "converts", picked.Count, target.SlotIndex,
                            copying ? "" : $" into {step.To}"));
                }

                if (redo.Count > 0) {
                    cinematicTime = RunCheatStepsInOrder(boss, cheat, redo, trigger, receiver, bossName, custom, combatActionList, cinematicTimeSoFar);
                }

                return true;
            }
            case "scaleeffects": {
                var percent = step.Amount;
                foreach (var target in On("Self").Where(t => t?._hangingEffects is not null)) {
                    var hanging = target._hangingEffects;
                    var scaled = 0;
                    for (var i = 0; i < hanging.Count; i++) {
                        if (Fits(hanging[i])) {
                            hanging[i] = hanging[i] with {
                                m_effectParam = (int) Math.Round(hanging[i].m_effectParam * percent / 100.0),
                                m_paramPerRound = (int) Math.Round(hanging[i].m_paramPerRound * percent / 100.0),
                            };
                            scaled++;
                        }
                    }

                    Logger.Information("Duel {0} | Slot {1} | {2}: {3} effects of slot {4} are now {5}% of their size.",
                        Logger.Args(duel, boss.SlotIndex, bossName, scaled, target.SlotIndex, percent));
                }

                return true;
            }
            case "effectsschool": {
                var school = (step.School ?? "").Trim();
                school = school.Length == 0 || school.Equals("Universal", StringComparison.OrdinalIgnoreCase) ? "All" : school;
                foreach (var target in On("Self").Where(t => t?._hangingEffects is not null)) {
                    var hanging = target._hangingEffects;
                    for (var i = 0; i < hanging.Count; i++) {
                        if (Fits(hanging[i])) {
                            hanging[i] = hanging[i] with { m_sDamageType = school };
                        }
                    }
                }

                Logger.Information("Duel {0} | {1}: effects now of {2}.", Logger.Args(duel, bossName, school));

                return true;
            }
            case "protecteffects": {
                var off = (step.To ?? "").Trim().Equals("Off", StringComparison.OrdinalIgnoreCase);
                foreach (var target in On("Self").Where(t => t?._hangingEffects is not null)) {
                    var hanging = target._hangingEffects;
                    for (var i = 0; i < hanging.Count; i++) {
                        if (Fits(hanging[i])) {
                            hanging[i] = hanging[i] with { m_protected = !off };
                        }
                    }
                }

                return true;
            }
            case "damage":
            case "drain": {
                var drained = 0;
                foreach (var target in On("Caster").Where(t => t?.ParticipantGameStats is not null && t.IsAlive)) {
                    var now = target.ParticipantGameStats.m_currentHitpoints;
                    var amount = Math.Max(0, step.Percent ? MostHealth(target) * step.Amount / 100 : step.Amount);
                    var taken = Math.Min(amount, Math.Max(0, now - 1));
                    if (taken > 0) {
                        SetHealthTo(target, now - taken);
                        drained += taken;
                    }

                    Logger.Information("Duel {0} | Slot {1} | {2}: slot {3} loses {4} health.",
                        Logger.Args(duel, boss.SlotIndex, bossName, target.SlotIndex, taken));
                }

                if (step.Is("Drain") && drained > 0) {
                    HealFromCheat(boss, drained);
                }

                return true;
            }
            default:
                return false;
        }
    }

    // Everyone's health and stun before the move, for Healed and Stunned.
    private Dictionary<CombatDuelSubCircle, (int Health, int Stunned)> _statsBeforeMove;

    private static bool IsAfterMoveWhen(BossCheats.BossCheat c)
        => c.Is("EffectAdded") || c.Is("EffectUsed") || c.Is("Healed") || c.Is("Stunned");

    private void NoteStatsBeforeMove()
        => _statsBeforeMove = _subCircles.Where(c => c is not null && c.Occupied && c.ParticipantGameStats is not null)
            .ToDictionary(c => c, c => (c.ParticipantGameStats.m_currentHitpoints, c.CombatParticipant?.m_stunned ?? 0));

    // The effects on someone now that were not before the move (two alike count as two).
    private List<SpellEffect> AddedTo(CombatDuelSubCircle who) {
        if (_effectsBeforeMove is null || !_effectsBeforeMove.TryGetValue(who, out var before)) {
            return [];
        }

        var had = before.ToList();
        var added = new List<SpellEffect>();
        foreach (var e in (who._hangingEffects ?? []).Where(e => e is not null)) {
            var i = had.IndexOf(e);
            if (i >= 0) {
                had.RemoveAt(i);
            }
            else {
                added.Add(e);
            }
        }

        return added;
    }

    private float AfterMoveWhenCheats(CombatDuelSubCircle mover, List<(CombatAction Action, SpellTemplate Spell)> landed, bool removingSpell,
                                      CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        var cinematicTime = 0.0f;
        if (_effectsBeforeMove is null) {
            return 0;
        }

        var spell = landed.FirstOrDefault(m => m.Spell is not null);
        foreach (var (boss, cheats) in BossesWithCheats().ToList()) {
            for (var i = 0; i < cheats.Count; i++) {
                var cheat = cheats[i];
                if (!InPhase(boss, cheat) || cheat.Interrupts || !IsAfterMoveWhen(cheat)) {
                    continue;
                }

                var kinds = (cheat.RemovedKind ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                bool Fits(SpellEffect e) => (kinds.Length == 0 || kinds.Any(k => IsKind(e, k))) && FromSpell(e, cheat.RemovedSpell);
                var from = (cheat.RemovedFrom ?? "").Trim().ToLowerInvariant();
                var who = from switch {
                    "anyally" => _subCircles.Where(c => c is not null && c.Occupied && c.OccupiedTeam == boss.OccupiedTeam),
                    "anyenemy" => _subCircles.Where(c => c is not null && c.Occupied && c.OccupiedTeam != boss.OccupiedTeam),
                    "anyone" => _subCircles.Where(c => c is not null && c.Occupied),
                    _ => [boss],
                };
                CombatDuelSubCircle hit = null;
                var what = "";
                foreach (var c in who) {
                    var stats = _statsBeforeMove?.GetValueOrDefault(c) ?? default;
                    if (cheat.Is("EffectAdded") && AddedTo(c).Count(Fits) is > 0 and var added) {
                        (hit, what) = (c, $"{added} effects put on slot {c.SlotIndex}");
                    }
                    else if (cheat.Is("EffectUsed") && !removingSpell && GoneFrom(c).Count(Fits) is > 0 and var used) {
                        (hit, what) = (c, $"{used} effects of slot {c.SlotIndex} used up");
                    }
                    else if (cheat.Is("Healed") && _statsBeforeMove is not null && c.ParticipantGameStats?.m_currentHitpoints > stats.Health && stats.Health > 0) {
                        (hit, what) = (c, $"slot {c.SlotIndex} healed");
                    }
                    else if (cheat.Is("Stunned") && _statsBeforeMove is not null && stats.Stunned <= 0 && (c.CombatParticipant?.m_stunned ?? 0) > 0) {
                        (hit, what) = (c, $"slot {c.SlotIndex} stunned");
                    }

                    if (hit is not null) {
                        break;
                    }
                }

                if (hit is null) {
                    continue;
                }

                _cheatSpellId = spell.Spell is not null ? spell.Action.m_spell.m_templateID : 0;
                _cheatCard = spell.Spell is not null ? spell.Action.m_spell : null;
                cinematicTime += TryBossCheat(boss, i, cheat, mover, hit, what, combatActionList, cinematicTimeSoFar + cinematicTime);
                _cheatSpellId = 0;
                _cheatCard = null;
            }
        }

        return cinematicTime;
    }

    // What "add an effect of your own size" calls an effect: Blade, Weakness, Shield, Trap or Absorb; null for any other.
    private static string SizedKind(SpellEffect e) {
        if (e.m_effectType == kSpellEffects.kAbsorbDamage) {
            return "Absorb";
        }

        foreach (var kind in new[] { "Blade", "Weakness", "Shield", "Trap" }) {
            if (IsKind(e, kind)) {
                return kind;
            }
        }

        return null;
    }

}
