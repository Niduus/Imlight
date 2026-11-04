/*
 * The automatic check of cheat steps (Niduus, 2026-11-04: "add automated checks to cheats for player and boss in the
 * case something is to have intended actions instead of open actions ... the client can't exactly show some animations
 * and requires actions to display certain things").
 *
 * The game works each round out itself from the spells it is shown cast: what the server changes with no spell (an
 * "open" change) the game never sees, so a removed shield stays drawn, an added damage over time never ticks on screen
 * (Niduus's Malistaire log, 2026-10-29: the server said a wizard died while the game showed 512 health), a heal does not
 * move the health ring. So every step of a cheat (player and creature cheats alike, they share the step runner) is
 * watched: when it changed something and sent the game no action, a real spell is cast for it (an "intended" action),
 * and the server then keeps exactly its own result:
 *
 *   effects taken off   a removal spell of that kind (charm, ward, over-time, aura) told to take that many (as
 *                       ConvertEffects has done since v0.10.623, ShowRemoval).
 *   effects put on      the spell they came from when it only puts charms and wards on (AddEffects), else a lookalike
 *                       of that kind and school at your size (as "add an effect of your own size"); a damage or heal over
 *                       time gets a real one of that type.
 *   health gained       a real heal spell of that size.
 *
 * Not cast, and logged (the editor and Boss Test warn about the step, Imlight.Common CheatDisplay): a cast that would use
 * up something in the game the server keeps (a blade on the caster for a damage over time, a heal boost for a heal), a
 * health loss (a damage spell would hit the game's shields and could show a wizard defeated who is not), and a change of
 * size or school only (the game keeps the old number). [BossCheats] ShowOpenChanges (live) turns the check off.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Spells;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Combat;

public partial class CombatResolver {

    private sealed record OpenWatch(int Actions, Dictionary<CombatDuelSubCircle, List<SpellEffect>> Hanging, Dictionary<CombatDuelSubCircle, int> Health);

    // Steps that never change effects or health, or show themselves (a real cast), are not watched.
    private static readonly HashSet<string> s_unwatchedSteps = new(StringComparer.OrdinalIgnoreCase) {
        "Cast", "Polymorph", "FreeCast", "Say", "Bubble", "Announce", "Countdown", "Sound", "Wait", "PickOne", "RandomOne",
        "Repeat", "IfCleared", "IfConverted", "IfVar", "OnlySchool", "SetVar", "Remember", "Mark", "Unmark", "Interrupt",
        "SetPhase", "Minion", "Clone", "Form", "SetHealth", "Defeat", "Dispel", "RemoveGlobal", "ConvertEffects", "CopyEffects",
        "RunCheat", "GiveCheat", "CheatOnOff", "Kick", "FirstTurn", "InTurn",
        // Stat changes the game never draws (an aura-like effect with no spell): shown as a blade, the game would use it up.
        "Boost", "Immune", "Aura", "HitShare", "DamageCap", "SpellRule", "ScaleEffects", "EffectsSchool", "ProtectEffects",
    };

    // An effect the game never drew, so nothing to show coming or going: a cheat's own aura-like boost (rounds, no spell).
    private static bool NeverDrawn(SpellEffect e)
        => e.m_spellTemplateID == 0 && e.m_numRounds > 0 && !CombatRequirements.IsOverTime(e) && e.m_effectType != kSpellEffects.kDeferredDamage;

    private OpenWatch WatchOpenChanges(BossCheats.CheatAction step, CombatActionListObj combatActionList) {
        if (combatActionList?.m_actionList is null || s_unwatchedSteps.Contains(step.Do ?? "") || !BossCheats.ShowOpenChanges) {
            return null;
        }

        var circles = _subCircles.Where(c => c is { Occupied: true } && c._hangingEffects is not null).ToList();

        return new OpenWatch(combatActionList.m_actionList.Count,
            circles.ToDictionary(c => c, c => c._hangingEffects.Where(e => e is not null).ToList()),
            circles.Where(c => c.ParticipantGameStats is not null).ToDictionary(c => c, c => c.ParticipantGameStats.m_currentHitpoints));
    }

    /// <summary>
    /// After a watched step: what it changed with no action sent gets a real spell cast for it (see the top of the file),
    /// and the server's own result is put back. Returns the casts' time.
    /// </summary>
    private float ShowOpenChanges(OpenWatch watch, CombatDuelSubCircle boss, BossCheats.BossCheat cheat, BossCheats.CheatAction step,
                                  CombatDuelSubCircle trigger, CombatDuelSubCircle receiver, string bossName,
                                  CombatActionListObj combatActionList, float cinematicTimeSoFar, bool custom) {
        if (watch is null || combatActionList.m_actionList.Count != watch.Actions || !boss.IsAlive) {
            // The step sent the game something of its own: it shows itself.
            return 0;
        }

        var duel = _duel.m_duelID.Full;
        var cinematicTime = 0f;
        var changes = new List<(CombatDuelSubCircle Circle, List<SpellEffect> Gone, List<SpellEffect> New)>();
        foreach (var (circle, had) in watch.Hanging) {
            var now = circle._hangingEffects?.Where(e => e is not null).ToList() ?? [];
            var nowSet = now.ToHashSet(ReferenceEqualityComparer.Instance);
            var hadSet = had.ToHashSet(ReferenceEqualityComparer.Instance);
            var gone = had.Where(e => !nowSet.Contains(e) && !NeverDrawn(e)).ToList();
            var added = now.Where(e => !hadSet.Contains(e) && !NeverDrawn(e)).ToList();
            // One changed in place (a new size, school or protection: ScaleEffects, EffectsSchool, ProtectEffects) is the
            // same effect to the game, which keeps showing it.
            foreach (var e in gone.ToList()) {
                var same = added.FirstOrDefault(a => a.m_effectType == e.m_effectType && a.m_spellTemplateID == e.m_spellTemplateID
                                                     && a.m_disposition == e.m_disposition && a.m_numRounds == e.m_numRounds);
                if (same is not null) {
                    gone.Remove(e);
                    added.Remove(same);
                }
            }

            if (gone.Count > 0 || added.Count > 0) {
                changes.Add((circle, gone, added));
            }
        }

        var healthAfter = watch.Health.Keys.Where(c => c.ParticipantGameStats is not null)
            .ToDictionary(c => c, c => c.ParticipantGameStats.m_currentHitpoints);
        if (changes.Count == 0 && healthAfter.All(h => watch.Health.GetValueOrDefault(h.Key) == h.Value)) {
            return 0;
        }

        // What the server ends up with, put back after each cast.
        var hangingAfter = watch.Hanging.Keys.ToDictionary(c => c, c => c._hangingEffects?.ToList() ?? []);
        void PutBack() {
            foreach (var (circle, list) in hangingAfter) {
                if (circle._hangingEffects is { } hanging) {
                    hanging.Clear();
                    hanging.AddRange(list);
                }
            }

            foreach (var (circle, health) in healthAfter) {
                if (circle.ParticipantGameStats is { } stats && stats.m_currentHitpoints != health) {
                    circle.SetHealthTo(health);
                }
            }
        }

        float Cast(string spell, uint spellId, int index, int param, CombatDuelSubCircle on, bool setParam) {
            var sentBefore = combatActionList.m_actionList.Count;
            var time = CheatCast(boss, cheat, new BossCheats.CheatAction {
                Do = "Cast", Spell = spell, SpellID = spellId, Target = $"slot:{on.SlotIndex}", Silent = step.Silent, DispelImmune = true,
            }, trigger, receiver, bossName, [], combatActionList, cinematicTimeSoFar + cinematicTime, custom);
            if (setParam) {
                for (var i = sentBefore; i < combatActionList.m_actionList.Count; i++) {
                    if (combatActionList.m_actionList[i]?.m_spell is { } sent) {
                        combatActionList.m_actionList[i].m_spell = sent with {
                            m_paramOverrides = [new SpellEffectParamOverride { m_effectIndex = index, m_paramValue = param }],
                        };
                    }
                }
            }

            PutBack();

            return time;
        }

        foreach (var (circle, gone, added) in changes) {
            if (!circle.IsAlive) {
                continue;
            }

            if (gone.Count > 0) {
                // ShowRemoval puts this circle's list back itself.
                cinematicTime += ShowRemoval(boss, cheat, step, circle, gone, trigger, receiver, bossName, combatActionList,
                    cinematicTimeSoFar + cinematicTime, custom);
                PutBack();
                Logger.Information("Duel {0} | Slot {1} | {2}: {3} took {4} effects off slot {5} with no spell; a removal spell is cast so the game shows it.",
                    Logger.Args(duel, boss.SlotIndex, bossName, step.Do, gone.Count, circle.SlotIndex));
            }

            if (added.Count > 0) {
                cinematicTime += ShowAdded(boss, step, circle, added, bossName, Cast);
            }
        }

        foreach (var (circle, before) in watch.Health) {
            var after = healthAfter.GetValueOrDefault(circle, before);
            if (after == before) {
                continue;
            }

            if (before <= 0 || after < before) {
                Logger.Information("Duel {0} | Slot {1} | {2}: {3} moved slot {4}'s health {5} -> {6} with no spell; the game is not shown it "
                                   + "(cast a real spell to show it).",
                    Logger.Args(duel, boss.SlotIndex, bossName, step.Do, circle.SlotIndex, before, after));
                continue;
            }

            var onEnemy = circle.OccupiedTeam != boss.OccupiedTeam;
            var heal = OneEffectLook(kSpellEffects.kHeal, kHangingDisposition.kBeneficial, onEnemy, circle == boss);
            var boosts = HealBoostsInTheWay(boss, circle);
            if (heal is null || boosts is not null) {
                Logger.Information("Duel {0} | Slot {1} | {2}: slot {3} healed {4} with no spell; not shown ({5}).",
                    Logger.Args(duel, boss.SlotIndex, bossName, circle.SlotIndex, after - before, boosts ?? "the game has no heal spell for that side"));
                continue;
            }

            cinematicTime += Cast(heal.Value.Name, 0, heal.Value.Index, after - before, circle, true);
            Logger.Information("Duel {0} | Slot {1} | {2}: slot {3} healed {4} with no spell; {5} is cast so the game shows it.",
                Logger.Args(duel, boss.SlotIndex, bossName, circle.SlotIndex, after - before, heal.Value.Name));
        }

        return cinematicTime;
    }

    // Effects put on one circle with no spell: the spell they came from if it only puts charms and wards on, else one
    // lookalike each.
    private float ShowAdded(CombatDuelSubCircle boss, BossCheats.CheatAction step, CombatDuelSubCircle circle, List<SpellEffect> added,
                            string bossName, Func<string, uint, int, int, CombatDuelSubCircle, bool, float> cast) {
        var duel = _duel.m_duelID.Full;
        var time = 0f;
        var onEnemy = circle.OccupiedTeam != boss.OccupiedTeam;
        var left = added.ToList();
        foreach (var group in added.Where(e => e.m_spellTemplateID != 0).GroupBy(e => e.m_spellTemplateID).ToList()) {
            var template = FindSpell("", group.Key).Template;
            var effects = template is null ? [] : Flatten(template).ToList();
            if (template is null || effects.Count == 0 || effects.Any(e => !s_charmEffects.Contains(e.m_effectType) && !s_wardEffects.Contains(e.m_effectType))
                || effects.Count != group.Count()) {
                continue;
            }

            time += cast((string) template.m_name, group.Key, 0, 0, circle, false);
            left.RemoveAll(group.Contains);
            Logger.Information("Duel {0} | Slot {1} | {2}: {3} put {4}'s effects on slot {5} with no spell; it is cast so the game shows them.",
                Logger.Args(duel, boss.SlotIndex, bossName, step.Do, (string) template.m_name, circle.SlotIndex));
        }

        foreach (var e in left) {
            var school = ((string) e.m_sDamageType ?? "").Trim();
            string spell = null;
            var index = 0;
            string why = null;
            if (e.m_effectType is kSpellEffects.kDamageOverTime or kSpellEffects.kHealOverTime) {
                var good = e.m_effectType == kSpellEffects.kHealOverTime;
                why = good ? HealBoostsInTheWay(boss, circle) : DamageBoostsInTheWay(boss, circle, school);
                if (why is null && OneEffectLook(e.m_effectType, good ? kHangingDisposition.kBeneficial : kHangingDisposition.kHarmful, onEnemy,
                        circle == boss, good ? null : school, e.m_numRounds) is { } look) {
                    (spell, index) = look;
                }
            }
            else if (SizedKind(e) is { } kind && CheatLookalikes.Plain(kind, school) is { } plain) {
                spell = plain.Spell;
                index = FindSpell(spell, 0).Template?.m_effects?.FindIndex(f => f is not null && f.m_effectType == e.m_effectType) ?? 0;
            }

            if (spell is null) {
                Logger.Information("Duel {0} | Slot {1} | {2}: {3} put a {4} on slot {5} with no spell; not shown ({6}).",
                    Logger.Args(duel, boss.SlotIndex, bossName, step.Do, e.m_effectType, circle.SlotIndex, why ?? "the game has no spell of that kind for that side"));
                continue;
            }

            time += cast(spell, 0, Math.Max(0, index), e.m_effectParam, circle, true);
            Logger.Information("Duel {0} | Slot {1} | {2}: {3} put a {4} {5} on slot {6} with no spell; {7} is cast so the game shows it.",
                Logger.Args(duel, boss.SlotIndex, bossName, step.Do, e.m_effectParam, e.m_effectType, circle.SlotIndex, spell));
        }

        return time;
    }

    // A heal cast in the game would use up the caster's heal charms or the target's heal wards, which the server keeps.
    private static string HealBoostsInTheWay(CombatDuelSubCircle caster, CombatDuelSubCircle target) {
        if (caster._hangingEffects?.Any(e => e?.m_effectType is kSpellEffects.kModifyOutgoingHeal or kSpellEffects.kModifyOutgoingHealFlat) == true) {
            return "the caster's heal charm would be used up in the game";
        }

        return target._hangingEffects?.Any(e => e?.m_effectType is kSpellEffects.kModifyIncomingHeal or kSpellEffects.kModifyIncomingHealFlat) == true
            ? "the target's heal ward would be used up in the game"
            : null;
    }

    // A damage over time cast in the game would use up the caster's blades or the target's traps and shields of its school.
    private static string DamageBoostsInTheWay(CombatDuelSubCircle caster, CombatDuelSubCircle target, string school) {
        bool Hits(SpellEffect e) {
            var of = ((string) e.m_sDamageType ?? "").Trim();
            return of.Length == 0 || of.Equals("All", StringComparison.OrdinalIgnoreCase) || school.Length == 0 || of.Equals(school, StringComparison.OrdinalIgnoreCase);
        }

        if (caster._hangingEffects?.Any(e => e?.m_effectType is kSpellEffects.kModifyOutgoingDamage or kSpellEffects.kModifyOutgoingDamageFlat && Hits(e)) == true) {
            return "the caster's blade would be used up in the game";
        }

        return target._hangingEffects?.Any(e => e?.m_effectType is kSpellEffects.kModifyIncomingDamage or kSpellEffects.kModifyIncomingDamageFlat
                                                    or kSpellEffects.kAbsorbDamage && Hits(e)) == true
            ? "the target's trap or shield would be used up in the game"
            : null;
    }

}
