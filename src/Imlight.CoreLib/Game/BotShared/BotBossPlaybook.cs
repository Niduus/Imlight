/*
 * Imlight
 * Copyright (C) 2026 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;
using Card = Imlight.CoreLib.Game.Combat.RewrittenAI.Card;
using CombatResolver = Imlight.CoreLib.Game.Combat.CombatResolver;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// Boss playbooks (Niduus, 2026-11-16: "before a boss fight, he reads its cheats from the game data and plans around them, for
/// example not blading into a trap"). A creature's cheats are read from the very file the server runs them from (BossCheats,
/// with the Bosses tab's changes laid over it), so the plan is built on what will really happen in that fight, never on a
/// guess. Each cheat becomes one plain rule:
/// <list type="bullet">
/// <item>an answer: something a player does (a blade, a heal, a hit on it, a pass...) that it punishes, and how;</item>
/// <item>a timed move: a free spell at the start or end of certain rounds (a trap every 3 rounds, a hit on round 5);</item>
/// <item>a health line: what it does when it drops under some share of its health;</item>
/// <item>an immunity: a school it can't be hurt by.</item>
/// </list>
/// Ultimus, the Quest Team and the street bots all fight with it (one battle brain): a card that would set off a punishing
/// answer is held back unless it saves a life or ends the fight, a free hit due next round gets a heal or shield first,
/// blades wait when the boss is about to strip them, and the deck brings fewer of the cards it punishes (BotCards.Plan).
/// </summary>
internal static class BotBossPlaybook {

    /// <summary>Something a player does that the creature answers, and what it does then.</summary>
    internal sealed record Answer(string Trigger, IReadOnlyList<string> Kinds, string School, IReadOnlyList<string> Spells, bool AnyVersion,
                                  bool OnBossOnly, bool NotOnCaster, bool SingleTarget, bool MultiTarget, int Chance, int Phase,
                                  string Does, int Weight, bool Interrupts) {

        /// <summary>The rule in a player's words: "a blade gets a trap on the caster (always)".</summary>
        internal string Describe() {
            var what = Trigger switch {
                PASS => "passing",
                CRIT => "a critical hit",
                HIT => "hitting it",
                _ => Kinds.Count == 0 ? "any spell" : string.Join(" or ", Kinds.Select(Word)),
            };
            if (School.Length > 0) {
                what += $" ({School})";
            }

            if (Spells.Count > 0) {
                what = string.Join(" or ", Spells.Take(3));
            }

            if (OnBossOnly && Trigger != HIT) {
                what += " on it";
            }

            if (NotOnCaster) {
                what += " on someone else";
            }

            return $"{what} {(Interrupts ? "is stopped and " : "")}gets {Does}{(Chance < 100 ? $" ({Chance}% of the time)" : "")}";
        }

    }

    /// <summary>A free move at the start or end of certain rounds.</summary>
    internal sealed record Timed(bool AtStart, int Round, int Every, int Phase, string Does, bool HitsPlayers, bool StripsCharms, int Chance) {

        /// <summary>Whether it goes off in this round of the boss's (counted from 1).</summary>
        internal bool In(int round) {
            if (round < 1) {
                return false;
            }

            if (Round <= 0 && Every <= 0) {
                return true;
            }

            if (Every <= 0) {
                return round == Round;
            }

            var first = Round > 0 ? Round : Every;
            return round >= first && (round - first) % Every == 0;
        }

        internal string Describe() {
            var when = Round <= 0 && Every <= 0 ? "every round"
                : Every <= 0 ? $"round {Round}"
                : Round > 0 && Round != Every ? $"round {Round}, then every {Every} rounds"
                : $"every {Every} rounds";
            return $"{(AtStart ? "at the start of" : "at the end of")} {when}: {Does}{(Chance < 100 ? $" ({Chance}% of the time)" : "")}";
        }

    }

    /// <summary>What it does when its health drops under a line.</summary>
    internal sealed record Below(int Percent, int Health, string Does) {
        internal string Describe() => $"under {(Percent > 0 ? $"{Percent}% health" : $"{Health:N0} health")}: {Does}";
    }

    /// <summary>A creature's whole playbook. Empty when it has no cheats.</summary>
    internal sealed record Plan(ulong Template, string Name, IReadOnlyList<Answer> Answers, IReadOnlyList<Timed> Timed, IReadOnlyList<Below> Lines,
                                IReadOnlyList<string> Immune, IReadOnlyList<string> Other) {

        internal bool Empty => Answers.Count + Timed.Count + Lines.Count + Immune.Count + Other.Count == 0;

        /// <summary>Kinds of cards (Blade, Heal, Trap...) it punishes hard: the deck brings fewer of them.</summary>
        internal IReadOnlyList<string> Punished => [.. Answers.Where(a => a.Trigger == CAST && a.Weight * a.Chance >= 150 && a.Spells.Count == 0 && a.School.Length == 0)
            .SelectMany(a => a.Kinds).Select(SpellNameFamily.KindSynonym).Where(k => k is not ("Damage" or "Attack")).Distinct(StringComparer.OrdinalIgnoreCase)];

        /// <summary>Whether its free moves hit the players hard (a timed hit or trap): the gear leans to resist and health.</summary>
        internal bool HitsHard => Timed.Count(t => t.HitsPlayers) >= 1 || Answers.Count(a => a.Weight >= HURTS) >= 2;

        /// <summary>Its rules in plain words, one per line.</summary>
        internal List<string> Describe() {
            var lines = new List<string>();
            lines.AddRange(Answers.Select(a => a.Describe()));
            lines.AddRange(Timed.Select(t => t.Describe()));
            lines.AddRange(Lines.Select(l => l.Describe()));
            if (Immune.Count > 0) {
                lines.Add($"can't be hurt by {string.Join(", ", Immune)} for a while");
            }

            lines.AddRange(Other);
            return lines;
        }

        /// <summary>What it means for how he plays, in plain words.</summary>
        internal List<string> Advice() {
            var advice = new List<string>();
            foreach (var kind in Punished) {
                advice.Add(kind switch {
                    "Blade" or "Charm" => "no blading into its answer: I hit with what I have instead, or blade only when the hit lands the same round",
                    "Heal" => "I heal only when I must, and with the biggest heal at once",
                    "Shield" or "Ward" => "few shields: it answers them",
                    "Trap" => "no traps on it",
                    "Damage" => "fewer, bigger hits: every hit on it sets off its answer",
                    _ => $"I hold back {Word(kind).ToLowerInvariant()}s",
                });
            }

            if (Answers.Any(a => a.Trigger is HIT or CAST && Unavoidable(a) && a.Weight >= HURTS)) {
                advice.Add("it answers every hit, so I build up (blade, trap) and hit fewer, bigger times");
            }

            if (Answers.Any(a => a.Trigger == PASS && a.Weight * a.Chance >= 100)) {
                advice.Add("never pass: it punishes a pass, so I always play something");
            }

            if (Timed.Any(t => t.HitsPlayers)) {
                advice.Add("its timed free hits come on known rounds: I heal or shield the round before");
            }

            if (Timed.Any(t => t.StripsCharms)) {
                advice.Add("it strips blades on known rounds: I blade right after, not right before");
            }

            if (Immune.Count > 0) {
                advice.Add($"no {string.Join(" or ", Immune)} hits while it is immune");
            }

            return [.. advice.Distinct()];
        }

    }

    internal const string CAST = "cast", PASS = "pass", CRIT = "crit", HIT = "hit";

    // How much an answer hurts: 3 it hurts the player or stops their spell, 2 the boss gets stronger, 1 it only talks or marks.
    private const int HURTS = 3, STRONGER = 2, TALKS = 1;

    // The weight times chance an answer needs before a card that sets it off is held back (a 50% stronger-boss answer: 100).
    private const int HOLD_BACK = 150;

    private static readonly ConcurrentDictionary<ulong, (DateTime At, Plan Plan)> s_plans = new();

    /// <summary>The playbook of a creature (by template, as the server keys its cheats), read again once a minute.</summary>
    internal static Plan For(ulong template) {
        if (template == 0) {
            return Nothing(0);
        }

        if (s_plans.TryGetValue(template, out var known) && DateTime.UtcNow - known.At < TimeSpan.FromMinutes(1)) {
            return known.Plan;
        }

        Plan plan;
        try {
            plan = Read(template, BossCheats.For(template) ?? []);
        }
        catch (Exception) {
            plan = Nothing(template);
        }

        s_plans[template] = (DateTime.UtcNow, plan);
        return plan;
    }

    /// <summary>The playbook of a creature in a battle (its custom or its own template), or an empty one.</summary>
    internal static Plan Of(CombatDuelSubCircle circle) {
        if (circle?.ParticipantObject is not { } who || circle.IsHumanPlayer) {
            return Nothing(0);
        }

        try {
            return For(Customs.KeyOf(who));
        }
        catch (Exception) {
            return Nothing(0);
        }
    }

    private static Plan Nothing(ulong template) => new(template, "", [], [], [], [], []);

    // ---------------------------------------------------------------------
    // Reading the cheat file
    // ---------------------------------------------------------------------

    private static readonly Dictionary<string, string> s_castWhens = new(StringComparer.OrdinalIgnoreCase) {
        ["PlayerHeals"] = "Heal", ["PlayerCharms"] = "Charm", ["PlayerWards"] = "Ward", ["PlayerAuras"] = "Aura",
        ["PlayerGlobals"] = "Global", ["PlayerMinions"] = "Minion", ["PlayerShadows"] = "Shadow", ["PlayerDamages"] = "Damage",
    };

    internal static Plan Read(ulong template, IReadOnlyList<BossCheats.BossCheat> cheats) {
        var answers = new List<Answer>();
        var timed = new List<Timed>();
        var lines = new List<Below>();
        var immune = new List<string>();
        var other = new List<string>();
        foreach (var cheat in cheats.Where(c => c is not null && !c.Off)) {
            List<BossCheats.CheatAction> steps;
            try {
                steps = cheat.Steps();
            }
            catch (Exception) {
                continue;
            }

            var (does, weight, interrupts, hits, strips) = WhatItDoes(steps);
            foreach (var step in steps.Where(s => s.Is("Immune"))) {
                immune.Add(step.School.Length > 0 ? step.School : "every school");
            }

            if (weight == 0) {
                continue;
            }

            var chance = Math.Clamp(cheat.Chance <= 0 ? 100 : cheat.Chance, 1, 100);
            var when = cheat.When ?? "";
            if (cheat.Is("PlayerCasts") || cheat.Is("EnemyCasts") || s_castWhens.ContainsKey(when) || cheat.Is("BossHit") || cheat.Is("PlayerCrits")
                || cheat.Is("PlayerPasses")) {
                var kinds = Split(cheat.Category).Select(SpellNameFamily.KindSynonym).ToList();
                if (s_castWhens.TryGetValue(when, out var kind)) {
                    kinds.Add(kind);
                }

                if (cheat.Kind.Length > 0) {
                    kinds.Add(cheat.Kind);
                }

                var trigger = cheat.Is("PlayerPasses") ? PASS : cheat.Is("PlayerCrits") ? CRIT : cheat.Is("BossHit") ? HIT : CAST;
                if (trigger == HIT && kinds.Count == 0) {
                    kinds.Add("Damage");
                }

                answers.Add(new Answer(trigger, [.. kinds.Distinct(StringComparer.OrdinalIgnoreCase)], cheat.School ?? "", cheat.Spells ?? [], cheat.AnyVersion,
                    cheat.OnBoss || trigger == HIT, cheat.NotOnCaster, cheat.SingleTarget, cheat.MultiTarget, chance, cheat.Phase, does, weight, interrupts));
                continue;
            }

            if (cheat.Is("RoundStart") || cheat.Is("RoundEnd") || cheat.Is("BattleStart") || cheat.Is("FromRound")) {
                var start = !cheat.Is("RoundEnd");
                var round = cheat.Is("BattleStart") ? 1 : cheat.Is("FromRound") ? Math.Max(1, cheat.Round) : cheat.Round;
                var every = cheat.Is("BattleStart") ? 0 : cheat.Is("FromRound") ? Math.Max(1, cheat.Every) : cheat.Every;
                timed.Add(new Timed(start, round, every, cheat.Phase, does, hits, strips, chance));
                continue;
            }

            if (cheat.Is("HealthBelow") || cheat.Is("MyHealthBelow")) {
                lines.Add(new Below(cheat.Percent, cheat.Health, does));
                continue;
            }

            if (weight >= STRONGER && when.Length > 0 && !when.Equals("Passive", StringComparison.OrdinalIgnoreCase)) {
                other.Add($"{Plain(when)}: {does}");
            }
        }

        var name = "";
        try {
            name = BossCheats.NameOf(template);
        }
        catch (Exception) {
            // The name is only for the words.
        }

        return new Plan(template, name ?? "", answers, timed, [.. lines.OrderByDescending(l => l.Percent)], [.. immune.Distinct()], [.. other.Distinct().Take(6)]);
    }

    // What a cheat's steps do to the players, in words, how much it hurts (0 = nothing that matters), whether it stops the
    // player's spell, whether it hits the players and whether it strips their charms.
    private static (string Does, int Weight, bool Interrupts, bool Hits, bool Strips) WhatItDoes(List<BossCheats.CheatAction> steps) {
        var words = new List<string>();
        var weight = 0;
        var interrupts = false;
        var hits = false;
        var strips = false;
        foreach (var step in steps) {
            var onPlayers = OnPlayers(step.Target);
            switch (step.Do) {
                case var d when Is(d, "Interrupt"):
                    words.Add("the spell fizzles");
                    weight = Math.Max(weight, HURTS);
                    interrupts = true;
                    break;
                case var d when Is(d, "Reflect"):
                    words.Add("the spell cast back at the caster");
                    weight = Math.Max(weight, HURTS);
                    interrupts = true;
                    break;
                case var d when Is(d, "Redirect"):
                    words.Add("the spell sent elsewhere");
                    weight = Math.Max(weight, HURTS);
                    interrupts = true;
                    break;
                case var d when Is(d, "Cast") || Is(d, "Polymorph") || Is(d, "FreeCast") || Is(d, "CastBy"):
                    var (spellWords, spellWeight, spellHits) = SpellWords(step, onPlayers);
                    words.Add(spellWords);
                    weight = Math.Max(weight, spellWeight);
                    hits |= spellHits;
                    break;
                case var d when Is(d, "Stun"):
                    words.Add("a stun");
                    weight = Math.Max(weight, HURTS);
                    hits = true;
                    break;
                case var d when Is(d, "StealPips"):
                    words.Add("pips taken");
                    weight = Math.Max(weight, HURTS);
                    break;
                case var d when Is(d, "RemoveCharms") && onPlayers:
                    words.Add("blades and charms stripped");
                    weight = Math.Max(weight, HURTS);
                    strips = true;
                    break;
                case var d when Is(d, "RemoveWards") && onPlayers:
                    words.Add("shields stripped");
                    weight = Math.Max(weight, HURTS);
                    break;
                case var d when Is(d, "RemoveAuras") && onPlayers:
                    words.Add("auras stripped");
                    weight = Math.Max(weight, STRONGER);
                    break;
                case var d when Is(d, "AddCharm"):
                    var kind = SpellNameFamily.KindSynonym(step.Kind);
                    var onSelf = !onPlayers;
                    words.Add(onSelf ? $"a {Word(kind).ToLowerInvariant()} on itself" : $"a {Word(kind).ToLowerInvariant()} on the player");
                    weight = Math.Max(weight, onSelf ? STRONGER : HURTS);
                    hits |= !onSelf && kind is "Trap" or "Weakness" or "DoT";
                    break;
                case var d when Is(d, "Heal") || Is(d, "HealthTo") || Is(d, "SetHealth"):
                    words.Add("it heals");
                    weight = Math.Max(weight, STRONGER);
                    break;
                case var d when Is(d, "Boost"):
                    words.Add($"it grows stronger ({step.Kind.ToLowerInvariant()} {step.Amount:+0;-0}%)");
                    weight = Math.Max(weight, STRONGER);
                    break;
                case var d when Is(d, "Minion") || Is(d, "Form"):
                    words.Add("help joins it");
                    weight = Math.Max(weight, STRONGER);
                    break;
                case var d when Is(d, "Pips") && step.Amount > 0 && !onPlayers:
                    words.Add("it gains pips");
                    weight = Math.Max(weight, STRONGER);
                    break;
                case var d when Is(d, "Pips") && step.Amount < 0 && onPlayers:
                    words.Add("pips taken");
                    weight = Math.Max(weight, HURTS);
                    break;
                case var d when Is(d, "SwapHealth"):
                    words.Add("health swapped with it");
                    weight = Math.Max(weight, HURTS);
                    break;
                case var d when Is(d, "Immune"):
                    words.Add($"it can't be hurt by {(step.School.Length > 0 ? step.School : "anything")}");
                    weight = Math.Max(weight, STRONGER);
                    break;
                case var d when Is(d, "Say") || Is(d, "Announce"):
                    weight = Math.Max(weight, TALKS);
                    break;
            }
        }

        var does = words.Count == 0 ? "a line said" : string.Join(", ", words.Distinct().Take(3));
        // Talk alone is nothing to plan around.
        return (does, weight <= TALKS ? 0 : weight, interrupts, hits, strips);
    }

    // A free spell: what it does, read from the spell itself (its kinds, as the cheats read cards).
    private static (string Words, int Weight, bool Hits) SpellWords(BossCheats.CheatAction step, bool onPlayers) {
        SpellTemplate spell = null;
        try {
            spell = step.SpellID != 0 ? CoreObjectFactory.GetCoreTemplate(step.SpellID) as SpellTemplate
                : SpellFactory.GetSpell(step.Spell) is { } found ? CoreObjectFactory.GetCoreTemplate(found.m_templateID) as SpellTemplate : null;
        }
        catch (Exception) {
            // Read by its name below.
        }

        var name = spell is null ? step.Spell : Locale.GetEnglishName((string) spell.m_displayName) is { Length: > 0 } shown ? shown : (string) spell.m_name;
        var kinds = spell is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : CombatResolver.CategoriesOf(spell);
        // A spell aimed at no one in particular goes where the spell itself aims: a hit or trap at the players, a blade or
        // heal on itself.
        var harmful = kinds.Overlaps(["Damage", "Trap", "Weakness", "Stun", "Steal", "Dispel", "DoT", "Polymorph"]);
        var atPlayers = onPlayers || (string.IsNullOrEmpty(step.Target) && harmful);
        if (atPlayers && harmful) {
            return ($"{(string.IsNullOrWhiteSpace(name) ? "a spell" : name)} on the players", HURTS, kinds.Overlaps(["Damage", "Trap", "Weakness", "DoT"]));
        }

        return ($"{(string.IsNullOrWhiteSpace(name) ? "a spell" : name)} on itself", STRONGER, false);
    }

    private static bool OnPlayers(string target) {
        var t = (target ?? "").Trim();
        return t.Length > 0 && !t.Equals("Self", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("Ally", StringComparison.OrdinalIgnoreCase)
               && !t.Equals("AllAllies", StringComparison.OrdinalIgnoreCase) && !t.Equals("WeakestAlly", StringComparison.OrdinalIgnoreCase)
               && !t.Equals("StrongestAlly", StringComparison.OrdinalIgnoreCase) && !t.Equals("Summoner", StringComparison.OrdinalIgnoreCase)
               && !t.Equals("me", StringComparison.OrdinalIgnoreCase) && !t.StartsWith('p');
    }

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Split(string text)
        => (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Word(string kind) => kind switch {
        "Damage" => "Hit",
        "Charm" => "Charm (blade or weakness)",
        "Ward" => "Ward (shield or trap)",
        _ => kind,
    };

    private static string Plain(string when) => when switch {
        "MinionDefeated" => "when one of its helpers falls",
        "PlayerDefeated" => "when a player falls",
        "EnemyJoins" => "when someone joins the fight",
        "AllyHit" => "when its helpers are hit",
        "BossDefeated" => "when it falls",
        "PlayerFlees" => "when a player runs",
        _ => when,
    };

    // ---------------------------------------------------------------------
    // In a battle
    // ---------------------------------------------------------------------

    /// <summary>The creatures here that have a playbook.</summary>
    internal static List<(CombatDuelSubCircle Who, Plan Plan)> Watch(IEnumerable<CombatDuelSubCircle> enemies)
        => [.. (enemies ?? []).Where(e => e is not null && e.IsAlive).Select(e => (Who: e, Plan: Of(e))).Where(w => !w.Plan.Empty)];

    /// <summary>
    /// Why a card should be held back this turn, or null. <paramref name="target"/> is the circle it is aimed at (null for a
    /// spell on a whole side). A heal while in danger and a hit that ends the one who would answer are never held back.
    /// </summary>
    internal static string HoldBack(List<(CombatDuelSubCircle Who, Plan Plan)> watch, Card card, CombatDuelSubCircle me, CombatDuelSubCircle target,
                                    int round, bool inDanger) {
        if (watch.Count == 0 || card is null) {
            return null;
        }

        SpellTemplate spell;
        try {
            spell = CoreObjectFactory.GetCoreTemplate(card.Id) as SpellTemplate;
        }
        catch (Exception) {
            spell = null;
        }

        var kinds = spell is null ? KindsOf(card) : CombatResolver.CategoriesOf(spell);
        var school = spell is null ? card.School : (string) spell.m_sMagicSchoolName ?? card.School;
        var many = card.HitsAll || card.HealWholeTeam || card.ShieldWholeTeam || card.BladeWholeTeam || card.TrapAll;
        foreach (var (who, plan) in watch) {
            // A hit that ends it: its answers no longer matter.
            if (card.Hit && (target == who || card.HitsAll) && BotCore.Estimate(card, me, who) >= RewrittenAI.Health(who)) {
                continue;
            }

            // Its immunity: a hit of that school on it does nothing.
            if (card.Hit && (target == who || card.HitsAll) && plan.Immune.Any(s => s == "every school" || Is(s, card.DamageSchool))) {
                return $"{Name(who)} can't be hurt by {card.DamageSchool} right now (its cheat makes it immune)";
            }

            foreach (var answer in plan.Answers.Where(a => a.Trigger is CAST or HIT or CRIT)) {
                if (answer.Weight * answer.Chance < HOLD_BACK) {
                    continue;
                }

                if (answer.Trigger == HIT && !(card.Hit && (target == who || card.HitsAll))) {
                    continue;
                }

                if (answer.Trigger == CRIT) {
                    continue;
                }

                // An answer to every spell, or to every hit on it, can't be played around by holding cards back (the fight
                // has to be won by hitting it): it only counts when it names a kind of card other than a hit, a school or spells.
                if (Unavoidable(answer)) {
                    continue;
                }

                if (answer.OnBossOnly && target != who && !(many && target is null && card.Hit)) {
                    continue;
                }

                if (answer.NotOnCaster && (target == me || target is null && !card.Hit)) {
                    continue;
                }

                if (answer.SingleTarget && many || answer.MultiTarget && !many) {
                    continue;
                }

                if (answer.School.Length > 0 && !Is(answer.School, school)) {
                    continue;
                }

                if (answer.Spells.Count > 0 && !SpellNameFamily.IsOneOf(answer.Spells, answer.AnyVersion, card.Name, card.Shown, spell is null ? null : (string) spell.m_name)) {
                    continue;
                }

                if (answer.Trigger == CAST && answer.Kinds.Count > 0 && !answer.Kinds.Any(k => kinds.Contains(SpellNameFamily.KindSynonym(k)))) {
                    continue;
                }

                // A heal while in danger goes out anyway: staying alive comes before its answer.
                if (inDanger && card.Heal && !card.Hit) {
                    return null;
                }

                return $"{Name(who)} answers {answer.Describe()}";
            }

            // Blades and traps just before a timed strip are wasted.
            if ((card.Blade || card.Trap || card.Weakness) && !card.Hit
                && plan.Timed.FirstOrDefault(t => t.StripsCharms && (t.AtStart ? t.In(round + 1) : t.In(round))) is { } strip) {
                return $"{Name(who)} strips charms {strip.Describe().Split(':')[0]}, so a {(card.Blade ? "blade" : card.Trap ? "trap" : "charm")} now would be wasted";
            }
        }

        return null;
    }

    /// <summary>An answer to any spell, or to any hit, with nothing narrowing it.</summary>
    internal static bool Unavoidable(Answer answer)
        => answer.School.Length == 0 && answer.Spells.Count == 0
           && (answer.Kinds.Count == 0 || answer.Kinds.All(k => SpellNameFamily.KindSynonym(k) is "Damage" or "Attack"));

    /// <summary>Whether someone here punishes a pass (then he plays something rather than pass).</summary>
    internal static string PunishesPass(List<(CombatDuelSubCircle Who, Plan Plan)> watch)
        => watch.SelectMany(w => w.Plan.Answers.Where(a => a.Trigger == PASS && a.Weight * a.Chance >= 100).Select(a => $"{Name(w.Who)} answers {a.Describe()}"))
            .FirstOrDefault();

    /// <summary>A timed free hit on the players due before his next move (this round's end or next round's start), in words, or null.</summary>
    internal static string HitComing(List<(CombatDuelSubCircle Who, Plan Plan)> watch, int round)
        => watch.SelectMany(w => w.Plan.Timed.Where(t => t.HitsPlayers && t.Chance >= 50 && (t.AtStart ? t.In(round + 1) : t.In(round)))
                .Select(t => $"{Name(w.Who)}'s cheat: {t.Describe()}"))
            .FirstOrDefault();

    private static HashSet<string> KindsOf(Card card) {
        var kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (card.Hit) {
            kinds.Add("Damage");
        }

        if (card.Heal) {
            kinds.Add("Heal");
        }

        if (card.Blade) {
            kinds.Add("Blade");
            kinds.Add("Charm");
        }

        if (card.Weakness) {
            kinds.Add("Weakness");
            kinds.Add("Charm");
        }

        if (card.Trap) {
            kinds.Add("Trap");
            kinds.Add("Ward");
        }

        if (card.Shield) {
            kinds.Add("Shield");
            kinds.Add("Ward");
        }

        if (card.Stun) {
            kinds.Add("Stun");
        }

        return kinds;
    }

    private static string Name(CombatDuelSubCircle circle) {
        try {
            return CombatEffectApplicator.TargetName(circle);
        }
        catch (Exception) {
            return "the boss";
        }
    }

}
