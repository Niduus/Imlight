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
    // A shield against the one school the creatures it fights hit it with most (Niduus, 2026-11-15, "all").
    internal const string SCHOOL_SHIELD = "school shield";

    // The school shield that stops each school's hits (Balance has none).
    private static readonly Dictionary<string, string> s_schoolShields = new(StringComparer.OrdinalIgnoreCase) {
        ["Fire"] = "Fire Shield", ["Ice"] = "Snow Shield", ["Storm"] = "Storm Shield",
        ["Life"] = "Life Shield", ["Death"] = "Death Shield", ["Myth"] = "Myth Shield",
    };

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

    private static string[] Answers(string need, string school)
        => need == SCHOOL_SHIELD ? school is not null && s_schoolShields.TryGetValue(school, out var shield) ? [shield] : [] : s_answers[need];

    private static string NeedOf(string goal)
        => s_schoolShields.Values.Contains(goal, StringComparer.OrdinalIgnoreCase) ? SCHOOL_SHIELD
            : s_answers.FirstOrDefault(a => a.Value.Contains(goal, StringComparer.OrdinalIgnoreCase)).Key;

    internal sealed class Fight {
        public DateTime At { get; set; }
        public bool Won { get; set; }
        public double Lowest { get; set; }
        public double BiggestHit { get; set; }
        public double Minutes { get; set; }
        // The school the creatures in it hit with most (from their decks), when they were read.
        public string HitBy { get; set; }
    }

    internal sealed record Plan(string Need, string Goal, string GoalSchool, List<(uint Id, string Name, int Level)> Chain, int Points, string Why);

    private sealed class Watching {
        public int Last = -1;
        public int Max = 1;
        public double Lowest = 1;
        public double Biggest;
        public readonly Dictionary<string, double> Schools = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>
    /// The schools the creatures in a fight hit with, most first (read from their decks each round): the fight is written
    /// down with the one seen most, for a school shield against it.
    /// </summary>
    internal static void Faced(ulong globalId, IReadOnlyList<string> schools) {
        if (globalId <= 2 || schools is null || schools.Count == 0) {
            return;
        }

        var charId = globalId - 2;
        lock (s_lock) {
            if (!s_watching.TryGetValue(charId, out var w)) {
                s_watching[charId] = w = new Watching();
            }

            for (var i = 0; i < Math.Min(3, schools.Count); i++) {
                w.Schools[schools[i]] = w.Schools.GetValueOrDefault(schools[i]) + (3 - i);
            }
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

            list.Add(new Fight { At = DateTime.UtcNow, Won = won, Lowest = Math.Round(w.Lowest, 2), BiggestHit = Math.Round(w.Biggest, 2), Minutes = Math.Round(minutes, 1),
                HitBy = w.Schools.Count == 0 ? null : w.Schools.MaxBy(s => s.Value).Key });
            if (list.Count > RECENT * 2) {
                list.RemoveRange(0, list.Count - RECENT * 2);
            }

            Save();
        }
    }

    /// <summary>
    /// The plan for this wizard now: its need, the spell that answers it, and the spells still to learn on the way there
    /// (prerequisites first). Null when there is nothing to train toward. <paramref name="roleWants"/>: the spells its
    /// team job asks for (the Quest Team), a tie-breaker only. <paramref name="covered"/>: spells a teammate already
    /// brings (knows, or is training toward), skipped so the team doesn't spend points twice on the same answer.
    /// </summary>
    internal static Plan For(Wizard wizard, ulong charId, IReadOnlyCollection<string> roleWants = null, IReadOnlyCollection<string> covered = null) {
        if (wizard?.MagicSchoolBehavior is null || wizard.SpellbookBehavior is null) {
            return null;
        }

        // Pinned by hand in Ahziael (Niduus, 2026-11-15): that spell is the goal, whatever the fights say, until it is learned.
        if (Pinned(wizard, charId) is { } pinned) {
            return pinned;
        }

        List<Fight> recent;
        lock (s_lock) {
            recent = Fights().TryGetValue(charId.ToString(), out var list) ? [.. list.TakeLast(RECENT)] : [];
        }

        var lost = recent.Count(f => !f.Won);
        var low = recent.Count(f => f.Lowest < LOW);
        var big = recent.Count(f => f.BiggestHit >= BIG_HIT);
        var slow = recent.Count(f => f.Minutes >= LONG_MINUTES);
        // The school that hit hardest in the rough fights (left low, a big hit, or lost), when one stands out.
        var rough = recent.Where(f => !string.IsNullOrEmpty(f.HitBy) && (!f.Won || f.Lowest < LOW || f.BiggestHit >= BIG_HIT)).ToList();
        var hitBy = rough.GroupBy(f => f.HitBy, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault();
        var school = hitBy is not null && s_schoolShields.ContainsKey(hitBy.Key) && hitBy.Count() >= 2 && hitBy.Count() * 5 >= rough.Count * 3
            ? hitBy.Key : null;
        var bySchool = school is null ? 0 : hitBy.Count();
        var scores = new Dictionary<string, int> {
            [HEAL] = (low * 2) + (lost * 2),
            [SHIELD] = (big * 2) + lost,
            [DAMAGE] = slow * 2,
            [SCHOOL_SHIELD] = school is null ? 0 : (bySchool * 2) + 1,
        };
        var own = wizard.MagicSchoolBehavior.MagicSchool.ToString();

        // A chain already started is kept until done, unless another need now weighs far more (no flip-flopping).
        lock (s_lock) {
            if (s_lastGoal.TryGetValue(charId, out var goalBefore)
                && NeedOf(goalBefore) is { } needBefore && scores.ContainsKey(needBefore)
                && scores.Values.Max() - scores[needBefore] < 6) {
                scores[needBefore] += 6;
            }
        }

        var skipped = new List<string>();
        foreach (var need in scores.OrderByDescending(s => s.Value)
                     .ThenByDescending(s => roleWants?.Any(w => Answers(s.Key, school).Contains(w, StringComparer.OrdinalIgnoreCase)) == true)
                     .ThenBy(s => s.Key switch { HEAL => 0, SCHOOL_SHIELD => 1, SHIELD => 2, _ => 3 })
                     .Select(s => s.Key)) {
            foreach (var goal in Answers(need, school)) {
                if (Find(goal) is not { } goalSpell || string.Equals(goalSpell.School, own, StringComparison.OrdinalIgnoreCase)
                    || wizard.SpellbookBehavior.HasSpell(goalSpell.Id)) {
                    continue;
                }

                if (covered?.Contains(goalSpell.Name, StringComparer.OrdinalIgnoreCase) == true) {
                    skipped.Add(goalSpell.Name);
                    continue;
                }

                var chain = Chain(wizard, goalSpell);
                if (chain is null || chain.Count == 0) {
                    continue;
                }

                var points = chain.Sum(c => Find(c.Name)?.Cost ?? 1);
                var why = Reason(need, scores[need], recent.Count, low, big, slow, lost, goalSpell, chain, points, wizard.MagicSchoolBehavior.TrainingPoints,
                    school, bySchool, rough.Count);
                if (skipped.Count > 0) {
                    why += $" A teammate already brings {string.Join(" and ", skipped)}, so I don't spend points on it twice.";
                }

                lock (s_lock) {
                    s_lastGoal[charId] = goalSpell.Name;
                }

                return new Plan(need, goalSpell.Name, goalSpell.School, chain, points, why);
            }
        }

        return null;
    }

    /// <summary>
    /// What the teammates already bring of the spells a plan can aim for: ones they know, ones of their own school (their
    /// trainer teaches them anyway), and the goal a teammate before this one is already training toward (the one with the
    /// lower id keeps a shared goal, so two never swap it back and forth).
    /// </summary>
    internal static IReadOnlyCollection<string> CoveredBy(ulong me, IEnumerable<(Wizard Wizard, ulong CharId)> mates) {
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = s_answers.Values.SelectMany(a => a).Concat(s_schoolShields.Values).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var (mate, charId) in mates ?? []) {
            if (mate is null || charId == me) {
                continue;
            }

            var school = mate.MagicSchoolBehavior?.MagicSchool.ToString();
            foreach (var name in names) {
                if (Find(name) is { } spell && (mate.SpellbookBehavior?.HasSpell(spell.Id) == true
                                                || string.Equals(spell.School, school, StringComparison.OrdinalIgnoreCase))) {
                    covered.Add(spell.Name);
                }
            }

            lock (s_lock) {
                if (charId < me && s_lastGoal.TryGetValue(charId, out var goal)) {
                    covered.Add(goal);
                }
            }
        }

        return covered;
    }

    // ---- Pins from Ahziael: logs/bots/training-pins.json, { "<charId>": "<spell name>" }, read whenever it changes. ----

    internal const string PINS_FILE = "training-pins.json";
    internal const string PINNED = "pinned";
    private static Dictionary<string, string> s_pins = [];
    private static DateTime s_pinsWritten = DateTime.MinValue;
    private static DateTime s_pinsLook = DateTime.MinValue;

    private static string PinFor(ulong charId) {
        lock (s_lock) {
            if (DateTime.UtcNow >= s_pinsLook) {
                s_pinsLook = DateTime.UtcNow.AddSeconds(5);
                var path = Path.Combine(Path.GetFullPath(FOLDER), PINS_FILE);
                try {
                    var written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                    if (written != s_pinsWritten) {
                        s_pinsWritten = written;
                        s_pins = written == DateTime.MinValue ? []
                            : JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
                    Logger.Debug("Bot training: could not read {0}: {1}", Logger.Args(path, e.Message));
                }
            }

            return s_pins.GetValueOrDefault(charId.ToString());
        }
    }

    private static Plan Pinned(Wizard wizard, ulong charId) {
        if (PinFor(charId) is not { Length: > 0 } name) {
            return null;
        }

        var have = wizard.MagicSchoolBehavior.TrainingPoints;
        if (Find(name) is not { } spell) {
            return new Plan(PINNED, name, "?", [], 0, $"Ahziael pinned {name}, but no trainer teaches a spell by that name, so I can't train toward it.");
        }

        if (wizard.SpellbookBehavior.HasSpell(spell.Id)) {
            return null;
        }

        if (Chain(wizard, spell) is not { Count: > 0 } chain) {
            return new Plan(PINNED, spell.Name, spell.School, [], 0, $"Ahziael pinned {spell.Name}, but the trainers' needs for it can't be met from my spells.");
        }

        var points = chain.Sum(c => Find(c.Name)?.Cost ?? 1);
        var before = chain.Count > 1 ? $"; first {string.Join(", ", chain.Take(chain.Count - 1).Select(c => c.Name))}, which the trainer asks for before it" : "";
        lock (s_lock) {
            s_lastGoal[charId] = spell.Name;
        }

        return new Plan(PINNED, spell.Name, spell.School, chain, points,
            $"Niduus pinned this plan in Ahziael: I train toward {spell.Name} ({spell.School}){before}. That is {points} training point(s) in all and I have {have}.");
    }

    /// <summary>The plan in a few plain lines, for the Training plan card in Ahziael (each bot's own card).</summary>
    internal static string Card(Plan plan, int have) {
        if (plan is null) {
            return "Training plan: nothing to save points for right now.";
        }

        var steps = plan.Chain.Count == 0 ? plan.Goal : string.Join(", then ", plan.Chain.Select(c => c.Name));

        return $"Training plan: toward {plan.Goal} ({plan.GoalSchool}). Still to learn, in order: {steps}. " +
               $"{plan.Points} training point(s) in all, has {have}.\nWhy: {plan.Why}";
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
        List<(uint Id, string Name, int Level)> chain, int points, int have, string school = null, int bySchool = 0, int rough = 0) {
        string seen;
        if (fights == 0 || score == 0) {
            seen = fights == 0
                ? "I have no fights written down yet, so I start with what a wizard alone needs most"
                : $"my last {fights} fights went well, so I start with what a wizard alone needs most";
        }
        else {
            seen = need switch {
                HEAL => $"{low} of my last {fights} fights left me under {LOW:P0} health" + (lost > 0 ? $" and I lost {lost}" : ""),
                SCHOOL_SHIELD => $"{bySchool} of my last {rough} hard fights were against creatures that hit mostly with {school}" + (lost > 0 ? $", and I lost {lost}" : ""),
                SHIELD => $"in {big} of my last {fights} fights one round took over {BIG_HIT:P0} of my health" + (lost > 0 ? $" and I lost {lost}" : ""),
                _ => $"{slow} of my last {fights} fights went on for {LONG_MINUTES:0} minutes or more",
            };
        }

        var before = chain.Count > 1 ? $"; first {string.Join(", ", chain.Take(chain.Count - 1).Select(c => c.Name))}, which the trainer asks for before it" : "";
        var levels = chain.Where(c => c.Level > 0).Select(c => c.Level).DefaultIfEmpty(0).Max();

        return $"{char.ToUpperInvariant(seen[0])}{seen[1..]}: I need {(need == SCHOOL_SHIELD ? $"a shield against {school}" : s_needWords[need])}. I train toward {goal.Name} ({goal.School}){before}. " +
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
