/*
 * Chromatic schools (Niduus, 2026-10-27: "Chromatic SELF (Meaning gain a blade of that targets school) Chromatic ENEMY
 * (Give a weakness on that enemy specifically for their school) And so on. GET EXPANSIVE. COVER ALL CORNERS OF
 * APPLICAITON"). A Do block's School (AddCharm of every kind and the globals, Aura, Boost, Immune, ChangeSchool) can be a
 * Chromatic one, picked when the block runs from the fighters, for players and creatures alike:
 *   Chromatic          each target's own school; the block goes once for each target (ALL PLAYERS: each wizard their own).
 *   ChromaticRival     the rival of each target's school (Fire and Ice, Storm and Myth, Life and Death; a school with no
 *                      rival keeps its own), once for each target.
 *   ChromaticTrigger   the school of whoever set it off (the attacker), else whoever the spell was aimed at, else its own.
 *   ChromaticReceiver  the school of whoever the spell was aimed at, else the attacker, else its own.
 *   ChromaticFoes      one of each school standing on the other side (two Fire wizards and a Storm one: a Fire and a Storm).
 *   ChromaticAllies    one of each school standing on its own side.
 *   ChromaticLast      the school of the block before's target.
 *   ChromaticOwn       its own school.            ChromaticOwnRival  the rival of its own school.
 * A global goes on no one: "each target" is its own school there. An aura stat of school "chromatic" ("chromatic damage
 * 30") is the school of whoever gets the aura.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Imlight.Common;
using Imlight.CoreLib.Shared.Behaviors;

namespace Imlight.CoreLib.Game.Combat;

public partial class CombatResolver {

    private static readonly Regex s_chromaticWord = new(@"\bchromatic\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static bool IsChromatic(BossCheats.CheatAction step)
        => (step.School ?? "").Trim().StartsWith("Chromatic", StringComparison.OrdinalIgnoreCase) || s_chromaticWord.IsMatch(step.AuraStats ?? "");

    private static string SchoolOf(CombatDuelSubCircle c) => c is null || c.SchoolNow == MagicSchool.None ? "" : c.SchoolNow.ToString();

    // The rival of a fighter's school; a school with none (Balance, the astral schools, Shadow) is its own.
    private static string RivalSchoolOf(CombatDuelSubCircle c)
        => c is not null && RivalOf(c.SchoolNow) is var rival && rival != MagicSchool.None ? rival.ToString() : SchoolOf(c);

    // Whom a block works on when its Target is empty, as the block itself picks (AddCharm: the attacker for a harmful
    // effect, else itself).
    private static string DefaultAim(BossCheats.CheatAction step) {
        if (step.Is("AddCharm")) {
            var kind = (step.Kind ?? "").Trim();
            var harmful = kind.ToLowerInvariant() is "weakness" or "trap" or "dot" || (s_moreKinds.TryGetValue(kind, out var more) && !more.Good);

            return harmful ? "Caster" : "Self";
        }

        return "Self";
    }

    /// <summary>
    /// The steps a Chromatic step becomes: its school (and its aura stats' "chromatic") made a real one, once for each
    /// target or each school when the mode says so. Empty when there is no one to take a school from.
    /// </summary>
    private List<BossCheats.CheatAction> ChromaticSteps(CombatDuelSubCircle boss, BossCheats.CheatAction step, CombatDuelSubCircle trigger,
                                                        CombatDuelSubCircle receiver, string bossName) {
        var mode = (step.School ?? "").Trim().ToLowerInvariant();
        var chromatic = mode.StartsWith("chromatic", StringComparison.Ordinal);
        var rows = s_chromaticWord.IsMatch(step.AuraStats ?? "");
        var global = step.Is("AddCharm") && CheatLookalikes.IsGlobal(step.Kind);
        var standing = _subCircles.Where(c => c is not null && c.Occupied && c.AddedToDuel && c.IsAlive).ToList();
        CombatDuelSubCircle Alive(CombatDuelSubCircle c) => c is { IsAlive: true } ? c : null;

        // The schools that do not hang on the target (null: each target's own, or its rival's).
        List<string> schools = !chromatic ? [step.School ?? ""] : mode switch {
            "chromatictrigger" => [SchoolOf(Alive(trigger) ?? Alive(receiver) ?? boss)],
            "chromaticreceiver" => [SchoolOf(Alive(receiver) ?? Alive(trigger) ?? boss)],
            "chromaticlast" => [SchoolOf(_lastStepTargets.FirstOrDefault(c => c is { IsAlive: true }) ?? Alive(trigger) ?? boss)],
            "chromaticown" => [SchoolOf(boss)],
            "chromaticownrival" => [RivalSchoolOf(boss)],
            "chromaticfoes" => [.. standing.Where(c => c.OccupiedTeam != boss.OccupiedTeam).Select(SchoolOf).Distinct()],
            "chromaticallies" => [.. standing.Where(c => c.OccupiedTeam == boss.OccupiedTeam).Select(SchoolOf).Distinct()],
            // A global aims at no one: each target's school is its own.
            "chromaticrival" when global => [RivalSchoolOf(boss)],
            _ when global => [SchoolOf(boss)],
            _ => null,
        };
        if (global && schools is { Count: > 1 }) {
            // One global at a time: a second would only take the first one's place.
            schools = [schools[0]];
        }

        var picked = new List<BossCheats.CheatAction>();
        if (schools is not null && (!rows || global)) {
            picked.AddRange(schools.Select(school => step with { School = school }));
        }
        else {
            // Once for each target, which keeps its own school (and the aura's "chromatic" stats): pinned to its circle.
            var aim = step.Target.Length > 0 ? step.Target : DefaultAim(step);
            foreach (var target in StepTargets(boss, aim, null, trigger, receiver).Where(c => c is { IsAlive: true }).Distinct()) {
                var own = SchoolOf(target);
                var pinned = target == boss ? "Self" : $"slot:{target.SlotIndex}";
                var stats = rows ? s_chromaticWord.Replace(step.AuraStats, own.Length > 0 ? own.ToLowerInvariant() : "all") : step.AuraStats;
                foreach (var school in schools ?? [mode == "chromaticrival" ? RivalSchoolOf(target) : own]) {
                    picked.Add(step with { School = school, Target = pinned, AuraStats = stats });
                }
            }
        }

        Logger.Information("Duel {0} | Slot {1} | {2}: Chromatic {3} picks {4}.", Logger.Args(_duel.m_duelID.Full, boss.SlotIndex, bossName,
            step.Do, picked.Count == 0 ? "no one" : string.Join(", ", picked.Select(p => $"{(p.School.Length > 0 ? p.School : "every school")}"
                + (p.Target.StartsWith("slot:", StringComparison.Ordinal) ? $" on {p.Target}" : "")
                + (rows ? $" ({p.AuraStats})" : "")))));

        return picked;
    }

}
