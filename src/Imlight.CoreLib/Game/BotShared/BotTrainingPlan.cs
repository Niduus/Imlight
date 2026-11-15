/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// What Ultimus and each Quest Team wizard spend training points on, worked out from their own fights (Niduus,
/// 2026-11-15: "have it be based around his experiences to what to train up to first and why"). Each fight is written
/// down: how low its health went, the biggest hit it took in one go, how long it lasted, and whether it was won. From the
/// last fights a need is picked (a heal when fights leave it low, a shield when big hits land, a way to hit harder when
/// fights drag on), then a spell from another school that answers it, and the whole chain of spells the game's trainers
/// ask for before it (Sprite needs Imp, Leprechaun and Legend Shield first). Points are kept for that chain, and the plan
/// changes when the fights do. Kept in logs/bots/training-experience.json.
/// </summary>
internal static class BotTrainingPlan {

    private const string FOLDER = "logs/bots";
    private const string FILE = "training-experience.json";

    // The fights a plan looks at (the latest), and what counts as low, a big hit, and a long fight.
    private const int RECENT = 30;
    private const double LOW = 0.40;
    private const double BIG_HIT = 0.30;
    private const double LONG_MINUTES = 4.0;

    internal const string HEAL = "heal";
    internal const string SHIELD = "shield";
    internal const string DAMAGE = "damage";

    // The spell from another school that answers each need, first wanted first.
    private static readonly Dictionary<string, string[]> s_answers = new() {
        [HEAL] = ["Sprite", "Satyr"],
        [SHIELD] = ["Tower Shield"],
        [DAMAGE] = ["Feint"],
    };

    private static readonly Dictionary<string, string> s_needWords = new() {
        [HEAL] = "a heal of my own",
        [SHIELD] = "a strong shield",
        [DAMAGE] = "a way to hit harder",
    };

    internal sealed class Fight {
        public DateTime At { get; set; }
        public bool Won { get; set; }
        public double Lowest { get; set; }
        public double BiggestHit { get; set; }
        public double Minutes { get; set; }
    }

    internal sealed record Plan(string Need, string Goal, string GoalSchool, List<(uint Id, string Name, int Level)> Chain, int Points, string Why);

    private sealed class Watching {
        public int Last = -1;
        public int Max = 1;
        public double Lowest = 1;
        public double Biggest;
    }

    private static readonly object s_lock = new();
    private static Dictionary<string, List<Fight>> s_fights;
    private static readonly Dictionary<ulong, Watching> s_watching = [];
    private static readonly Dictionary<ulong, string> s_lastGoal = [];

    /// <summary>Health during a fight (each look): the lowest point and the biggest drop between looks are kept.</summary>
    internal static void Watch(ulong charId, int health, int maxHealth) {
        if (charId == 0 || maxHealth <= 0) {
            return;
        }

        lock (s_lock) {
            if (!s_watching.TryGetValue(charId, out var w)) {
                s_watching[charId] = w = new Watching();
            }

            w.Max = maxHealth;
            var share = Math.Clamp(health / (double) maxHealth, 0, 1);
            w.Lowest = Math.Min(w.Lowest, share);
            if (w.Last >= 0 && health < w.Last) {
                w.Biggest = Math.Max(w.Biggest, (w.Last - health) / (double) maxHealth);
            }

            w.Last = health;
        }
    }

    /// <summary>The fight ended: written down with what was seen during it.</summary>
    internal static void Ended(ulong charId, bool won, double minutes) {
        if (charId == 0) {
            return;
        }

        lock (s_lock) {
            if (!s_watching.Remove(charId, out var w)) {
                return;
            }

            var all = Fights();
            var key = charId.ToString();
            if (!all.TryGetValue(key, out var list)) {
                all[key] = list = [];
            }

            list.Add(new Fight { At = DateTime.UtcNow, Won = won, Lowest = Math.Round(w.Lowest, 2), BiggestHit = Math.Round(w.Biggest, 2), Minutes = Math.Round(minutes, 1) });
            if (list.Count > RECENT * 2) {
                list.RemoveRange(0, list.Count - RECENT * 2);
            }

            Save();
        }
    }

    /// <summary>
    /// The plan for this wizard now: its need, the spell that answers it, and the spells still to learn on the way there
    /// (prerequisites first). Null when there is nothing to train toward. <paramref name="roleWants"/>: the spells its
    /// team job asks for (the Quest Team), a tie-breaker only.
    /// </summary>
    internal static Plan For(Wizard wizard, ulong charId, IReadOnlyCollection<string> roleWants = null) {
        if (wizard?.MagicSchoolBehavior is null || wizard.SpellbookBehavior is null) {
            return null;
        }

        List<Fight> recent;
        lock (s_lock) {
            recent = Fights().TryGetValue(charId.ToString(), out var list) ? [.. list.TakeLast(RECENT)] : [];
        }

        var lost = recent.Count(f => !f.Won);
        var low = recent.Count(f => f.Lowest < LOW);
        var big = recent.Count(f => f.BiggestHit >= BIG_HIT);
        var slow = recent.Count(f => f.Minutes >= LONG_MINUTES);
        var scores = new Dictionary<string, int> {
            [HEAL] = (low * 2) + (lost * 2),
            [SHIELD] = (big * 2) + lost,
            [DAMAGE] = slow * 2,
        };
        var own = wizard.MagicSchoolBehavior.MagicSchool.ToString();

        // A chain already started is kept until done, unless another need now weighs far more (no flip-flopping).
        lock (s_lock) {
            if (s_lastGoal.TryGetValue(charId, out var goalBefore)
                && s_answers.FirstOrDefault(a => a.Value.Contains(goalBefore)).Key is { } needBefore
                && scores.Values.Max() - scores[needBefore] < 6) {
                scores[needBefore] += 6;
            }
        }

        foreach (var need in scores.OrderByDescending(s => s.Value)
                     .ThenByDescending(s => roleWants?.Any(w => s_answers[s.Key].Contains(w, StringComparer.OrdinalIgnoreCase)) == true)
                     .ThenBy(s => s.Key switch { HEAL => 0, SHIELD => 1, _ => 2 })
                     .Select(s => s.Key)) {
            foreach (var goal in s_answers[need]) {
                if (Find(goal) is not { } goalSpell || string.Equals(goalSpell.School, own, StringComparison.OrdinalIgnoreCase)
                    || wizard.SpellbookBehavior.HasSpell(goalSpell.Id)) {
                    continue;
                }

                var chain = Chain(wizard, goalSpell);
                if (chain is null || chain.Count == 0) {
                    continue;
                }

                var points = chain.Sum(c => Find(c.Name)?.Cost ?? 1);
                var why = Reason(need, scores[need], recent.Count, low, big, slow, lost, goalSpell, chain, points, wizard.MagicSchoolBehavior.TrainingPoints);
                lock (s_lock) {
                    s_lastGoal[charId] = goalSpell.Name;
                }

                return new Plan(need, goalSpell.Name, goalSpell.School, chain, points, why);
            }
        }

        return null;
    }

    /// <summary>True when the plan's goal changed since it was last told (so the wizard says the new one once).</summary>
    internal static bool Changed(ulong charId, Plan plan, ref string told) {
        var now = plan?.Goal ?? "";
        if (now == told) {
            return false;
        }

        told = now;

        return plan is not null;
    }

    /// <summary>The next spell of the plan's chain, by id (0 when none).</summary>
    internal static uint NextId(Wizard wizard, ulong charId, IReadOnlyCollection<string> roleWants = null)
        => For(wizard, charId, roleWants)?.Chain.FirstOrDefault().Id ?? 0;

    private static string Reason(string need, int score, int fights, int low, int big, int slow, int lost, VanillaTrainers.Spell goal,
        List<(uint Id, string Name, int Level)> chain, int points, int have) {
        string seen;
        if (fights == 0 || score == 0) {
            seen = fights == 0
                ? "I have no fights written down yet, so I start with what a wizard alone needs most"
                : $"my last {fights} fights went well, so I start with what a wizard alone needs most";
        }
        else {
            seen = need switch {
                HEAL => $"{low} of my last {fights} fights left me under {LOW:P0} health" + (lost > 0 ? $" and I lost {lost}" : ""),
                SHIELD => $"in {big} of my last {fights} fights one round took over {BIG_HIT:P0} of my health" + (lost > 0 ? $" and I lost {lost}" : ""),
                _ => $"{slow} of my last {fights} fights went on for {LONG_MINUTES:0} minutes or more",
            };
        }

        var before = chain.Count > 1 ? $"; first {string.Join(", ", chain.Take(chain.Count - 1).Select(c => c.Name))}, which the trainer asks for before it" : "";
        var levels = chain.Where(c => c.Level > 0).Select(c => c.Level).DefaultIfEmpty(0).Max();

        return $"{char.ToUpperInvariant(seen[0])}{seen[1..]}: I need {s_needWords[need]}. I train toward {goal.Name} ({goal.School}){before}. " +
               $"That is {points} training point(s) in all and I have {have}" + (levels > 0 ? $"; the last one opens at level {levels}" : "") + ".";
    }

    // The spells still to learn for the goal, prerequisites first (by the trainers' own needs); null when it can't be reached.
    private static List<(uint Id, string Name, int Level)> Chain(Wizard wizard, VanillaTrainers.Spell goal) {
        var chain = new List<(uint, string, int)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Add(VanillaTrainers.Spell spell, int depth) {
            if (spell is null || depth > 12 || !seen.Add(spell.Name)) {
                return spell is not null;
            }

            if (wizard.SpellbookBehavior.HasSpell(spell.Id)) {
                return true;
            }

            foreach (var name in Needed(spell.Needs)) {
                if (!Add(Find(name), depth + 1)) {
                    return false;
                }
            }

            chain.Add((spell.Id, spell.Name, spell.Rank));

            return true;
        }

        return Add(goal, 0) ? chain : null;
    }

    private static IEnumerable<string> Needed(VanillaTrainers.Need need) {
        if (need is null || need.Not) {
            yield break;
        }

        if (need.Class == "HasSpell" && !string.IsNullOrEmpty(need.Spell)) {
            yield return need.Spell;
        }

        foreach (var item in need.Items ?? []) {
            foreach (var name in Needed(item)) {
                yield return name;
            }
        }
    }

    private static readonly Dictionary<string, VanillaTrainers.Spell> s_byName = new(StringComparer.OrdinalIgnoreCase);

    private static VanillaTrainers.Spell Find(string name) {
        lock (s_byName) {
            if (s_byName.Count == 0) {
                foreach (var spell in VanillaTrainers.All.SelectMany(t => t.Spells).Where(s => s.Kind == "School")) {
                    if (!s_byName.TryGetValue(spell.Name, out var have) || spell.Rank > have.Rank) {
                        s_byName[spell.Name] = spell;
                    }
                }
            }

            return s_byName.GetValueOrDefault(name ?? "");
        }
    }

    // ---- The file. ----

    private static string FilePath => Path.Combine(Path.GetFullPath(FOLDER), FILE);

    private static Dictionary<string, List<Fight>> Fights() {
        if (s_fights is not null) {
            return s_fights;
        }

        try {
            s_fights = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, List<Fight>>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (Exception e) {
            Logger.Warning("Bot training: could not read {0}: {1}", Logger.Args(FilePath, e.Message));
            s_fights = [];
        }

        return s_fights;
    }

    private static void Save() {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s_fights, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) {
            Logger.Debug("Bot training: could not write {0}: {1}", Logger.Args(FilePath, e.Message));
        }
    }
}
