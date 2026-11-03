/*
 * Reactions (Niduus, 2026-11-03: the cheat list for his own Malistaire, "Are these programmed?"): the blocks that list
 * needed and no block did yet. Like every cheat, each works in a player's cheats and a creature's alike (BossCheats.cs).
 *
 *   SpellEffectSchool   only a spell whose charms, wards, auras or bubble are of this school ("Universal": Balanceblade,
 *                       Hex, Tower Shield and the like). "Whenever a Universal Blade is cast".
 *   When PetCasts       a pet on the other side is about to cast, a May Cast or a Will Cast. "The attacker" is its owner.
 *   Dispel              the game's own dispel of a school goes on the target, so it shows: School is a school, Their (the
 *                       target's own), TheirWeave (a wizard's second school, from Magic Weaving), commas between.
 *   RemoveGlobal        the battlefield's bubble is gone.
 *   Kick                the target leaves the battle once the round has played out, as if it fled; Lock keeps its circle
 *                       locked, Ban keeps it from coming back into this battle.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Combat;

public partial class CombatResolver {

    // ---- If: the school of the spell's effects ----

    /// <summary>The If "the spell's effects are of this school": true when the cheat asks for none.</summary>
    internal static bool SpellEffectSchoolAllows(BossCheats.BossCheat cheat, SpellTemplate spell) {
        var want = (cheat.SpellEffectSchool ?? "").Trim();
        if (want.Length == 0) {
            return true;
        }

        if (spell is null) {
            return false;
        }

        // Its charms, wards, auras and bubble: a damage spell with a universal weakness on it counts by that weakness.
        var effects = Flatten(spell)
            .Where(e => e is not null && (s_charmEffects.Contains(e.m_effectType) || s_wardEffects.Contains(e.m_effectType)
                                          || e.m_effectTarget == kEffectTarget.kGlobal))
            .ToList();

        return effects.Count > 0 && effects.Any(e => EffectSchoolIs(e, want));
    }

    // ---- When: a pet is about to cast ----

    /// <summary>
    /// A pet's May Cast or Will Cast is about to go off (CombatResolver.cs CastPetSpell, the delayed Will Casts): the
    /// When PetCasts of everyone on the other side. "The attacker" is the pet's owner, "the one hit" whom the pet aims at.
    /// </summary>
    private float PetCastCheats(CombatDuelSubCircle owner, SpellTemplate spell, CombatDuelSubCircle target,
                                CombatActionListObj combatActionList, float cinematicTimeSoFar) {
        if (owner is null || spell is null) {
            return 0;
        }

        var cinematicTime = 0.0f;
        try {
            var aimed = target is null ? new HashSet<int>() : AimedAt(owner, target, spell);
            foreach (var (boss, cheats) in BossesWithCheats().ToList()) {
                if (!IsFoe(boss, owner)) {
                    continue;
                }

                for (var i = 0; i < cheats.Count; i++) {
                    var cheat = cheats[i];
                    if (!cheat.Is("PetCasts") || !InPhase(boss, cheat) || !Matches(cheat, spell, aimed, boss, owner)) {
                        continue;
                    }

                    _cheatSpellId = SpellFactory.GetSpell((string) spell.m_name)?.m_templateID ?? 0;
                    cinematicTime += TryBossCheat(boss, i, cheat, owner, target, $"slot {owner.SlotIndex}'s pet casting {(string) spell.m_name}",
                        combatActionList, cinematicTimeSoFar + cinematicTime);
                    _cheatSpellId = 0;
                }
            }
        }
        catch (Exception e) {
            Logger.Error("Duel {0} | Pet cast cheats for slot {1} failed: {2}", Logger.Args(_duel.m_duelID.Full, owner.SlotIndex, e));
        }

        return cinematicTime;
    }

    // ---- Dos ----

    // The ones a Kick step sent out, for the duel to take out once the round has played out (CombatDuelComponent).
    private readonly List<(CombatDuelSubCircle Circle, ulong Participant, bool Lock, bool Ban)> _kicks = [];

    /// <summary>The Kick steps of this round, once: the duel takes each one out of the battle.</summary>
    internal List<(CombatDuelSubCircle Circle, ulong Participant, bool Lock, bool Ban)> TakeKicks() {
        var kicks = _kicks.ToList();
        _kicks.Clear();

        return kicks;
    }

    private bool TryReactionCheatSteps(CombatDuelSubCircle boss, BossCheats.BossCheat cheat, BossCheats.CheatAction step,
                                       CombatDuelSubCircle trigger, CombatDuelSubCircle receiver, string bossName,
                                       CombatActionListObj combatActionList, float cinematicTimeSoFar, bool custom, out float cinematicTime) {
        cinematicTime = 0;
        List<CombatDuelSubCircle> On(string fallback) => StepTargets(boss, step.Target.Length > 0 ? step.Target : fallback, null, trigger, receiver);
        var duel = _duel.m_duelID.Full;

        switch (step.Do.ToLowerInvariant()) {
            case "dispel": {
                // "He will give that player a dispel of their school and weaved school" (Niduus, 2026-11-03).
                var casts = new List<BossCheats.CheatAction>();
                foreach (var target in On("Caster").Where(t => t is { IsAlive: true, AddedToDuel: true })) {
                    foreach (var school in DispelSchools(target, step.School)) {
                        if (DispelSpellOf(school) is not { } dispel) {
                            Logger.Warning("Duel {0} | {1}: the game has no dispel of {2}.", Logger.Args(duel, bossName, school));
                            continue;
                        }

                        casts.Add(new BossCheats.CheatAction {
                            Do = "Cast", Spell = dispel.Name, SpellID = dispel.Id, Target = $"slot:{target.SlotIndex}", Silent = step.Silent,
                            DispelImmune = true, Tag = step.Tag,
                        });
                        Logger.Information("Duel {0} | Slot {1} | {2}: a {3} dispel ({4}) on slot {5}.",
                            Logger.Args(duel, boss.SlotIndex, bossName, school, dispel.Name, target.SlotIndex));
                    }
                }

                if (casts.Count > 0) {
                    cinematicTime = RunCheatStepsInOrder(boss, cheat, casts, trigger, receiver, bossName, custom, combatActionList, cinematicTimeSoFar);
                }

                return true;
            }
            case "removeglobal": {
                // "Whenever a global is casted, Malistaire will remove it" (Niduus, 2026-11-03). The game is shown its own
                // remove-global spell, so the bubble goes there too; the server's bubble goes either way.
                var bubbles = _duel.m_duelModifier?.m_battlefieldEffects;
                if (bubbles is null || bubbles.Count == 0) {
                    return true;
                }

                if (!step.Silent && SpellFactory.Exists(REMOVE_GLOBAL)) {
                    var show = new BossCheats.CheatAction { Do = "Cast", Spell = REMOVE_GLOBAL, Target = "Self", Silent = true, DispelImmune = true };
                    cinematicTime = RunCheatStepsInOrder(boss, cheat, [show], trigger, receiver, bossName, custom, combatActionList, cinematicTimeSoFar);
                }

                var gone = bubbles.Count;
                bubbles.Clear();
                _clearedCount += gone;
                Logger.Information("Duel {0} | Slot {1} | {2}: the global is gone ({3} effects).", Logger.Args(duel, boss.SlotIndex, bossName, gone));

                return true;
            }
            case "kick": {
                // "He will kick that wizard out of the duel and lock his spot" (Niduus, 2026-11-03). Taken out once the round
                // has played out, so the game finishes showing it first.
                foreach (var target in On("Caster").Where(t => t is { Occupied: true, AddedToDuel: true } && t.ParticipantObject is not null && t != boss)) {
                    if (_kicks.Any(k => k.Circle == target)) {
                        continue;
                    }

                    _kicks.Add((target, target.ParticipantObject.m_globalID.Full, step.Lock, step.Ban));
                    if (step.Lock) {
                        target.LockedUntil = int.MaxValue;
                    }

                    Logger.Information("Duel {0} | Slot {1} | {2}: slot {3} is kicked out of the battle{4}{5}.",
                        Logger.Args(duel, boss.SlotIndex, bossName, target.SlotIndex, step.Lock ? ", its circle locked" : "",
                            step.Ban ? ", and can't come back" : ""));
                }

                return true;
            }
            default:
                return false;
        }
    }

    private const string REMOVE_GLOBAL = "MOB_RemoveGlobal";

    // The schools a Dispel step names for a target: a school, Their (its own) and TheirWeave (a wizard's second school).
    private static List<string> DispelSchools(CombatDuelSubCircle target, string words) {
        var schools = new List<string>();
        foreach (var word in (string.IsNullOrWhiteSpace(words) ? "Their" : words).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            var school = word.ToLowerInvariant() switch {
                "their" or "theirs" or "own" => target.SchoolNow.ToString(),
                "theirweave" or "weave" or "weaved" or "second" => target._wizard is { } wizard ? World.VanillaTrainers.SecondarySchoolOf(wizard) : null,
                _ => word,
            };
            if (!string.IsNullOrWhiteSpace(school) && !school.Equals("None", StringComparison.OrdinalIgnoreCase)
                && !schools.Contains(school, StringComparer.OrdinalIgnoreCase)) {
                schools.Add(school);
            }
        }

        return schools;
    }

    // The game's own dispels: one harmful kDispel effect each, by the school it dispels.
    private static readonly Lazy<List<(string Name, uint Id, string School)>> s_dispels = new(() => {
        try {
            return [.. SpellFactory.AllSpellTemplates()
                .Where(t => t.Template.m_effects is [{ } e] && e.GetType() == typeof(SpellEffect) && e.m_effectType == kSpellEffects.kDispel
                            && e.m_effectTarget == kEffectTarget.kEnemySingle && !t.Template.m_Treasure
                            && SpellFactory.Exists((string) t.Template.m_name))
                .Select(t => ((string) t.Template.m_name, SpellFactory.GetSpell((string) t.Template.m_name)?.m_templateID ?? 0u,
                    (string) t.Template.m_effects[0].m_sDamageType ?? ""))
                .Where(t => t.Item2 != 0)];
        }
        catch (Exception e) {
            Logger.Error("Boss cheats | The dispels could not be listed: {0}", Logger.Args(e));

            return [];
        }
    });

    // The plainest dispel of a school (Quench, Melt...: the shortest name with no " - " in it first).
    private static (string Name, uint Id)? DispelSpellOf(string school) {
        var found = s_dispels.Value
            .Where(d => d.School.Equals(school, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.Name.Contains(" - ", StringComparison.Ordinal) || d.Name.Contains("MOB", StringComparison.OrdinalIgnoreCase))
            .ThenBy(d => d.Name.Contains("TC", StringComparison.Ordinal))
            .ThenBy(d => d.Name.Length)
            .FirstOrDefault();

        return found.Name is null ? null : (found.Name, found.Id);
    }

}
