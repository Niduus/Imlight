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
using System.Reflection;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;
using CombatResolver = Imlight.CoreLib.Game.Combat.CombatResolver;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Zoom studies the bosses' cheats (Niduus, 2026-11-22: "analyze cheats and their behaviors in later arcs. Citing which ones
/// worked and which did not, allowing for us to look in deeper, having Zoom purposefully trigger cheats and marking down all
/// that data"). In a battle with a creature that has cheats (BossCheats, the file the server runs them from), before he clears
/// the room he plays to set each one off on purpose, one a round: the card kind it answers (a blade, a heal, a shield, a
/// trap, a hit on it), a pass, waiting for its timed rounds, and small hits that walk its health down past each health line.
/// Each round he reads how often each cheat has gone off (the server's own count for the battle) and writes down what came
/// of his last move: it went off, or it should have and did not. All of it goes to his own hive (ZoomHive). He stops
/// studying and clears when every cheat he can set off was tried, after [Zoom] CheatStudyRounds rounds (15), or when he is
/// under half health. [Zoom] CheatStudy = false turns it off (live).
/// </summary>
internal static class ZoomCheats {

    internal static bool On => ConfigurationManager.GetValue((UltimusSeat.Current.IsScout ? UltimusSeat.Current : UltimusSeat.Zoom).Section + ".CheatStudy", true);

    private static int MostRounds => Math.Clamp(ConfigurationManager.GetValue((UltimusSeat.Current.IsScout ? UltimusSeat.Current : UltimusSeat.Zoom).Section + ".CheatStudyRounds", 15), 1, 60);

    // Under this share of his health he stops studying and clears.
    private const float SAFE = 0.5f;

    private static readonly Dictionary<string, string> s_castWhens = new(StringComparer.OrdinalIgnoreCase) {
        ["PlayerHeals"] = "Heal", ["PlayerCharms"] = "Charm", ["PlayerWards"] = "Ward", ["PlayerAuras"] = "Aura",
        ["PlayerGlobals"] = "Global", ["PlayerMinions"] = "Minion", ["PlayerShadows"] = "Shadow", ["PlayerDamages"] = "Damage",
    };

    // One creature with cheats in one of his battles.
    private sealed class Boss {
        internal CombatDuelSubCircle Circle;
        internal ulong Template;
        internal string Name = "";
        internal List<BossCheats.BossCheat> Cheats = [];
        internal int FirstRound;
        internal readonly Dictionary<int, int> Times = [];
        // Cheats already judged for what can only happen once (a health line, a battle start, a Once cheat that went off).
        internal readonly HashSet<int> Settled = [];
        // The round his health was first seen under each health line.
        internal readonly Dictionary<int, int> UnderSince = [];
        internal float LastShare = 1f;
    }

    // What he did in a round.
    private sealed record Did(int Round, bool Passed, RewrittenAI.Card Card, int Target, int Aimed, int Cheat, float TargetShare);

    private sealed class Study {
        internal ulong Duel;
        internal readonly Dictionary<int, Boss> Bosses = [];
        internal int StartRound;
        internal int LastRound = -1;
        internal Did Last;
        internal bool Over;
        internal string OverWhy = "";
        // Cheats he set off on purpose (boss slot, cheat).
        internal readonly HashSet<(int, int)> Tried = [];
        internal DateTime Seen = DateTime.UtcNow;
    }

    private static readonly ConcurrentDictionary<ulong, Study> s_studies = new();

    private static readonly FieldInfo s_counts = typeof(CombatResolver).GetField("_bossCheatCounts", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>Whether he is still studying cheats in this battle (his hand then has the cards to set them off, and no Epic).</summary>
    internal static bool Studying(ulong duel) => On && s_studies.TryGetValue(duel, out var study) && !study.Over && study.Bosses.Count > 0;

    // The server's count of each cheat this battle, by (boss, template, cheat).
    private static Dictionary<(CombatDuelSubCircle, ulong, int), (int, int)> Counts(CombatResolver resolver) {
        try {
            return s_counts?.GetValue(resolver) as Dictionary<(CombatDuelSubCircle, ulong, int), (int, int)>;
        }
        catch (Exception) {
            return null;
        }
    }

    /// <summary>
    /// Once a round, before he plans: finds the creatures with cheats, reads how often each went off since he last looked,
    /// and writes down what came of his last move (ZoomHive).
    /// </summary>
    internal static void Round(ulong duel, CombatDuelSubCircle me, IReadOnlyList<CombatDuelSubCircle> circles, CombatResolver resolver, int round) {
        if (!On || resolver is null || me is null) {
            return;
        }

        try {
            Forget();
            var study = s_studies.GetOrAdd(duel, d => new Study { Duel = d, StartRound = round });
            study.Seen = DateTime.UtcNow;
            if (study.LastRound == round) {
                return;
            }

            var counts = Counts(resolver);
            if (counts is null) {
                study.Over = true;
                study.OverWhy = "the server's cheat counts could not be read";
                return;
            }

            foreach (var c in circles.Where(c => c is not null && c.OccupiedTeam != me.OccupiedTeam && !c.IsHumanPlayer)) {
                if (study.Bosses.ContainsKey(c.SlotIndex) && study.Bosses[c.SlotIndex].Circle == c) {
                    continue;
                }

                var template = counts.Keys.Where(k => k.Item1 == c).Select(k => k.Item2).FirstOrDefault();
                if (template == 0) {
                    template = Customs.KeyOf(c.ParticipantObject);
                }

                var cheats = BossCheats.For(template) ?? [];
                if (cheats.Count == 0) {
                    continue;
                }

                // A boss whose cheats he already knows well is cleared at once.
                if (ZoomHive.Known(template, cheats.Count)) {
                    continue;
                }

                var boss = new Boss { Circle = c, Template = template, Name = BossCheats.NameOf(template) ?? "", Cheats = [.. cheats], FirstRound = round };
                study.Bosses[c.SlotIndex] = boss;
                ZoomHive.Met(boss.Name, template, cheats.Select((x, i) => (i, x.When ?? "", Words(x), Chance(x), Untestable(x, cheats))).ToList());
                UltimusLink.Note(UltimusSeat.Current.Name, $"round {round}: {boss.Name} has {cheats.Count} cheat(s); I set them off before I clear.");
            }

            foreach (var boss in study.Bosses.Values) {
                var share = RewrittenAI.HealthShare(boss.Circle);
                for (var i = 0; i < boss.Cheats.Count; i++) {
                    var cheat = boss.Cheats[i];
                    if (cheat.Off) {
                        continue;
                    }

                    var now = counts.Where(k => k.Key.Item1 == boss.Circle && k.Key.Item3 == i).Sum(k => k.Value.Item1);
                    var before = boss.Times.GetValueOrDefault(i);
                    boss.Times[i] = now;
                    var mine = study.Last is { } last && last.Round == round - 1 && Answers(cheat, last, boss, me);
                    var detail = Detail(study, boss, i, round, me);
                    if (now > before) {
                        if (cheat.Once || Once(cheat)) {
                            boss.Settled.Add(i);
                        }

                        ZoomHive.Saw(boss.Name, boss.Template, i, cheat.When ?? "", Words(cheat), Chance(cheat), mine, fired: true, missed: false,
                            $"went off {now - before}x by round {round}; {detail}");
                        continue;
                    }

                    if (boss.Settled.Contains(i) || !boss.Circle.IsAlive) {
                        continue;
                    }

                    // Should it have gone off since he last looked?
                    var plain = Plain(cheat, boss);
                    if (mine) {
                        ZoomHive.Saw(boss.Name, boss.Template, i, cheat.When ?? "", Words(cheat), Chance(cheat), setOff: true,
                            fired: false, missed: Chance(cheat) >= 100,
                            $"did not go off after he {What(study.Last)} in round {round - 1}; {detail}");
                        continue;
                    }

                    if (plain.Length > 0) {
                        continue;
                    }

                    var bossRound = round - 1 - boss.FirstRound + 1;
                    var due = boss.FirstRound == study.StartRound && round - 1 >= boss.FirstRound && Chance(cheat) >= 100 && (
                        (cheat.Is("BattleStart") && bossRound == 1)
                        || ((cheat.Is("RoundStart") || cheat.Is("RoundEnd")) && Due(cheat, bossRound))
                        || (cheat.Is("FromRound") && bossRound >= Math.Max(1, cheat.Round)
                            && (cheat.Every <= 1 || (bossRound - Math.Max(1, cheat.Round)) % cheat.Every == 0)));
                    if (due) {
                        if (cheat.Is("BattleStart")) {
                            boss.Settled.Add(i);
                        }

                        ZoomHive.Saw(boss.Name, boss.Template, i, cheat.When ?? "", Words(cheat), 100, setOff: false, fired: false, missed: true,
                            $"was due in its round {bossRound} and did not go off; {detail}");
                        continue;
                    }

                    // A health line: under it at an earlier look and still nothing.
                    if ((cheat.Is("HealthBelow") || cheat.Is("MyHealthBelow")) && Under(cheat, boss, share)) {
                        if (!boss.UnderSince.TryGetValue(i, out var since)) {
                            boss.UnderSince[i] = round;
                        }
                        else if (since < round) {
                            boss.Settled.Add(i);
                            ZoomHive.Saw(boss.Name, boss.Template, i, cheat.When ?? "", Words(cheat), Chance(cheat), setOff: true, fired: false,
                                missed: Chance(cheat) >= 100, $"its health went under the line by round {since} and it never went off; {detail}");
                        }
                    }
                }

                boss.LastShare = share;
            }

            study.LastRound = round;
            if (!study.Over && round - study.StartRound >= MostRounds) {
                study.Over = true;
                study.OverWhy = $"{MostRounds} rounds of study";
            }

            if (!study.Over && RewrittenAI.HealthShare(me) < SAFE) {
                study.Over = true;
                study.OverWhy = "I am under half health";
            }

            if (study.Over && study.OverWhy.Length > 0) {
                UltimusLink.Note(UltimusSeat.Current.Name, $"round {round}: done studying cheats ({study.OverWhy}); now I clear.");
                study.OverWhy = "";
            }
        }
        catch (Exception e) {
            Logger.Warning("Duel {0} | Zoom could not read the cheats: {1}", Logger.Args(duel, e.Message));
        }
    }

    /// <summary>
    /// His move to set off the next cheat not yet tried this battle, or null when he is done studying (then he clears).
    /// </summary>
    internal static UltimusTactics.Choice Move(ulong duel, CombatDuelSubCircle me, List<BotCore.InHand> hand, int round) {
        if (!Studying(duel) || !s_studies.TryGetValue(duel, out var study)) {
            return null;
        }

        foreach (var boss in study.Bosses.Values.Where(b => b.Circle.IsAlive)) {
            for (var i = 0; i < boss.Cheats.Count; i++) {
                var cheat = boss.Cheats[i];
                if (cheat.Off || study.Tried.Contains((boss.Circle.SlotIndex, i)) || boss.Settled.Contains(i) || Plain(cheat, boss).Length > 0) {
                    continue;
                }

                UltimusTactics.Choice choice = null;
                if (cheat.Is("PlayerPasses")) {
                    choice = new(UltimusTactics.Move.Pass, 0, -1, $"passes on purpose to set off {boss.Name}'s cheat #{i} ({Words(cheat)})");
                }
                else if (IsCast(cheat)) {
                    var card = hand.Where(h => Fits(h.Card, cheat)).OrderBy(h => h.Card.BaseDamage).FirstOrDefault();
                    if (card is not null) {
                        choice = new(UltimusTactics.Move.Cast, card.Index, TargetOf(card.Card, me, boss),
                            $"casts {card.Card.Shown} on purpose to set off {boss.Name}'s cheat #{i} ({Words(cheat)})");
                    }
                }

                if (choice is not null) {
                    study.Tried.Add((boss.Circle.SlotIndex, i));
                    return choice;
                }
            }
        }

        // Health lines next: the smallest hit at the boss walks its health down past each line.
        foreach (var boss in study.Bosses.Values.Where(b => b.Circle.IsAlive)) {
            var share = RewrittenAI.HealthShare(boss.Circle);
            var line = boss.Cheats.Select((c, i) => (c, i)).Where(x => !x.c.Off && (x.c.Is("HealthBelow") || x.c.Is("MyHealthBelow"))
                                                                       && !boss.Settled.Contains(x.i) && !Under(x.c, boss, share)).ToList();
            if (line.Count == 0) {
                continue;
            }

            var hit = hand.Where(h => h.Card.Hit && !h.Card.HitsAll).OrderBy(h => h.Card.BaseDamage).FirstOrDefault()
                      ?? hand.Where(h => h.Card.Hit).OrderBy(h => h.Card.BaseDamage).FirstOrDefault();
            if (hit is not null) {
                return new(UltimusTactics.Move.Cast, hit.Index, hit.Card.HitsAll ? -1 : boss.Circle.SlotIndex,
                    $"walks {boss.Name}'s health down with {hit.Card.Shown} to its health line ({Words(line[0].c)})");
            }
        }

        // Timed cheats still to come in the study's rounds: he waits for them (a shield on himself, else a pass).
        var waiting = study.Bosses.Values.Where(b => b.Circle.IsAlive).SelectMany(b => b.Cheats.Select((c, i) => (b, c, i)))
            .Where(x => !x.c.Off && !x.b.Settled.Contains(x.i) && Plain(x.c, x.b).Length == 0 && x.b.Times.GetValueOrDefault(x.i) == 0
                        && (x.c.Is("RoundStart") || x.c.Is("RoundEnd") || x.c.Is("FromRound"))
                        && FirstDue(x.c) is { } first && first + x.b.FirstRound - 1 <= study.StartRound + MostRounds && first + x.b.FirstRound - 1 >= round)
            .ToList();
        if (waiting.Count > 0) {
            var (b, c, i) = waiting[0];
            var shield = hand.FirstOrDefault(h => h.Card.Shield && !h.Card.Hit);
            return shield is not null
                ? new(UltimusTactics.Move.Cast, shield.Index, shield.Card.ShieldWholeTeam ? -1 : me.SlotIndex,
                    $"waits for {b.Name}'s timed cheat #{i} ({Words(c)}): shields himself")
                : new(UltimusTactics.Move.Pass, 0, -1, $"waits for {b.Name}'s timed cheat #{i} ({Words(c)})");
        }

        study.Over = true;
        UltimusLink.Note(UltimusSeat.Current.Name, $"round {round}: every cheat I can set off here was tried; now I clear.");
        return null;
    }

    /// <summary>What he does this round (cast or pass), for judging the cheats next round.</summary>
    internal static void Done(ulong duel, CombatDuelSubCircle me, IReadOnlyList<CombatDuelSubCircle> circles, UltimusTactics.Choice choice, int round) {
        if (!On || !s_studies.TryGetValue(duel, out var study) || choice is null) {
            return;
        }

        RewrittenAI.Card card = null;
        if (choice.Move == UltimusTactics.Move.Cast && me.GetSpellFromLastHand((byte) choice.Card) is { } spell) {
            card = RewrittenAI.Read(spell);
        }

        var aimed = choice.Target >= 0 ? circles.FirstOrDefault(c => c.SlotIndex == choice.Target) : null;
        study.Last = new Did(round, choice.Move == UltimusTactics.Move.Pass, card, choice.Target, aimed?.SlotIndex ?? -1, -1,
            aimed is null ? 1f : RewrittenAI.HealthShare(aimed));
    }

    // Studies of battles long over are let go.
    private static void Forget() {
        foreach (var (duel, study) in s_studies) {
            if (DateTime.UtcNow - study.Seen > TimeSpan.FromMinutes(30)) {
                s_studies.TryRemove(duel, out _);
            }
        }
    }

    // ---------------------------------------------------------------------
    // Reading a cheat
    // ---------------------------------------------------------------------

    private static int Chance(BossCheats.BossCheat c) => Math.Clamp(c.Chance <= 0 ? 100 : c.Chance, 1, 100);

    private static bool IsCast(BossCheats.BossCheat c) => c.Is("PlayerCasts") || c.Is("BossHit") || s_castWhens.ContainsKey(c.When ?? "");

    private static bool Once(BossCheats.BossCheat c) => c.Is("BattleStart") || c.Is("HealthBelow") || c.Is("MyHealthBelow") || c.MaxTimes == 1;

    // Why a cheat can't be set off on purpose by him alone, or "".
    private static string Untestable(BossCheats.BossCheat c, IReadOnlyList<BossCheats.BossCheat> all) {
        if (c.Off) {
            return "turned off in the app";
        }

        if (c.Is("PlayerCrits")) {
            return "needs a critical hit, which he can't choose";
        }

        if (!(IsCast(c) || c.Is("PlayerPasses") || c.Is("RoundStart") || c.Is("RoundEnd") || c.Is("BattleStart") || c.Is("FromRound")
              || c.Is("HealthBelow") || c.Is("MyHealthBelow"))) {
            return $"\"{c.When}\" is watched, not set off on purpose";
        }

        if (c.NotOnCaster) {
            return "needs a spell on a teammate (he fights alone)";
        }

        if (IsCast(c) && KindsOf(c).Any(k => k is "Aura" or "Minion" or "Shadow" or "Global")) {
            return $"needs a {string.Join("/", KindsOf(c))} spell, which he does not pick on purpose yet";
        }

        return "";
    }

    // Conditions that make "it should have gone off" unsure (a phase, health window, other creatures...), in words, or "".
    private static string Plain(BossCheats.BossCheat c, Boss boss) {
        var why = new List<string>();
        if (c.Phase > 1 || boss.Cheats.Any(x => x.SetPhase > 0)) {
            why.Add("phases");
        }

        if (c.HealthAbove > 0 || c.HealthUnder > 0) {
            why.Add("health window");
        }

        if (c.FromRound > 0 || c.ToRound > 0) {
            why.Add("round window");
        }

        if (c.PlayersMin > 0 || c.PlayersMax > 0 || c.HasAllies || c.NoAllies || (c.AlliesUp?.Count ?? 0) > 0 || (c.AlliesDown?.Count ?? 0) > 0) {
            why.Add("who stands");
        }

        if (c.Creature != 0 || (c.HasWho ?? "").Length > 0 || c.CostMin > 0 || c.CostMax != 0 || c.Pips > 0) {
            why.Add("other conditions");
        }

        if (c.Cooldown > 0 || (c.MaxTimes > 0 && boss.Times.GetValueOrDefault(boss.Cheats.IndexOf(c)) >= c.MaxTimes)) {
            why.Add("cooldown or most times");
        }

        if (c.Once && boss.Times.GetValueOrDefault(boss.Cheats.IndexOf(c)) > 0) {
            why.Add("once");
        }

        if (c.NotOnCaster || c.Is("PlayerCrits")) {
            why.Add("can't be done alone");
        }

        if (IsCast(c) && KindsOf(c).Any(k => k is "Aura" or "Minion" or "Shadow" or "Global")) {
            why.Add("kind he does not pick");
        }

        return string.Join(", ", why);
    }

    private static List<string> KindsOf(BossCheats.BossCheat c) {
        var kinds = (c.Category ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SpellNameFamily.KindSynonym).ToList();
        if (s_castWhens.TryGetValue(c.When ?? "", out var kind)) {
            kinds.Add(kind);
        }

        if ((c.Kind ?? "").Length > 0) {
            kinds.Add(SpellNameFamily.KindSynonym(c.Kind));
        }

        return [.. kinds.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    // The kinds a card of his is, in the cheat file's words.
    private static HashSet<string> KindsOf(RewrittenAI.Card card) {
        var kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (card is null) {
            return kinds;
        }

        if (card.Hit) {
            kinds.UnionWith(["Damage", "Attack"]);
        }

        if (card.Heal) {
            kinds.Add("Heal");
        }

        if (card.Blade) {
            kinds.UnionWith(["Blade", "Charm"]);
        }

        if (card.Weakness) {
            kinds.UnionWith(["Weakness", "Charm"]);
        }

        if (card.Trap) {
            kinds.UnionWith(["Trap", "Ward"]);
        }

        if (card.Shield) {
            kinds.UnionWith(["Shield", "Ward"]);
        }

        return kinds;
    }

    // Whether a card of his would set this cheat off (its kind, school and spells).
    private static bool Fits(RewrittenAI.Card card, BossCheats.BossCheat c) {
        if (card is null) {
            return false;
        }

        if (c.Is("BossHit") && !card.Hit) {
            return false;
        }

        var kinds = KindsOf(c);
        if (kinds.Count > 0 && !kinds.Any(KindsOf(card).Contains)) {
            return false;
        }

        if ((c.School ?? "").Length > 0 && !string.Equals(card.School, c.School, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        if ((c.Spells?.Count ?? 0) > 0 && !c.Spells.Any(s => card.Shown.StartsWith(s.Trim(), StringComparison.OrdinalIgnoreCase)
                                                              || card.Name.Contains(s.Trim(), StringComparison.OrdinalIgnoreCase))) {
            return false;
        }

        return true;
    }

    // Whether his last move should have set this cheat off: the right card, aimed right, and it landed.
    private static bool Answers(BossCheats.BossCheat c, Did did, Boss boss, CombatDuelSubCircle me) {
        if (c.Off || Plain(c, boss).Length > 0) {
            return false;
        }

        if (c.Is("PlayerPasses")) {
            return did.Passed;
        }

        if (!IsCast(c) || did.Card is null || !Fits(did.Card, c)) {
            return false;
        }

        var onBoss = did.Target == boss.Circle.SlotIndex || (did.Target < 0 && did.Card.Hit);
        if ((c.OnBoss || c.Is("BossHit")) && !onBoss) {
            return false;
        }

        // A hit landed when its target lost health; anything else is taken as landed.
        return !(did.Card.Hit && did.Target >= 0 && did.Target == boss.Circle.SlotIndex && RewrittenAI.HealthShare(boss.Circle) >= did.TargetShare);
    }

    private static bool Under(BossCheats.BossCheat c, Boss boss, float share)
        => c.Health > 0 ? RewrittenAI.Health(boss.Circle) < c.Health : share * 100 < c.Percent;

    private static bool Due(BossCheats.BossCheat c, int round)
        => c.Round > 0 && c.Every > 0 ? round >= c.Round && (round - c.Round) % c.Every == 0
            : c.Round > 0 ? round == c.Round
            : c.Every <= 0 || round % c.Every == 0;

    private static int? FirstDue(BossCheats.BossCheat c) {
        for (var r = 1; r <= 60; r++) {
            if (c.Is("FromRound") ? r >= Math.Max(1, c.Round) : Due(c, r)) {
                return r;
            }
        }

        return null;
    }

    private static int TargetOf(RewrittenAI.Card card, CombatDuelSubCircle me, Boss boss) {
        if (card.Hit) {
            return card.HitsAll ? -1 : boss.Circle.SlotIndex;
        }

        if (card.Trap || card.Weakness) {
            return card.TrapAll ? -1 : boss.Circle.SlotIndex;
        }

        if (card.Heal) {
            return card.HealWholeTeam ? -1 : me.SlotIndex;
        }

        if (card.Blade) {
            return card.BladeWholeTeam ? -1 : me.SlotIndex;
        }

        if (card.Shield) {
            return card.ShieldWholeTeam ? -1 : me.SlotIndex;
        }

        return me.SlotIndex;
    }

    private static string What(Did did) => did is null ? "did nothing"
        : did.Passed ? "passed"
        : did.Card is null ? "cast a card"
        : $"cast {did.Card.Shown}{(did.Aimed >= 0 ? $" at slot {did.Aimed}" : "")}";

    // Where and when, to look deeper: the battle, the boss's slot, health and the server log line.
    private static string Detail(Study study, Boss boss, int index, int round, CombatDuelSubCircle me)
        => $"battle {study.Duel}, boss slot {boss.Circle.SlotIndex} at {RewrittenAI.Percent(RewrittenAI.HealthShare(boss.Circle))}% health, " +
           $"Zoom at {RewrittenAI.Percent(RewrittenAI.HealthShare(me))}%; last move: {What(study.Last)}; " +
           $"server log: \"Duel {study.Duel} | Slot {boss.Circle.SlotIndex} |\"";

    /// <summary>A cheat in plain words: when it goes, its conditions, and what it casts.</summary>
    internal static string Words(BossCheats.BossCheat c) {
        var when = c.When ?? "";
        var parts = new List<string> { when.Length > 0 ? when : "?" };
        var kinds = KindsOf(c);
        if (IsCast(c) && kinds.Count > 0) {
            parts.Add(string.Join("/", kinds));
        }

        if ((c.School ?? "").Length > 0) {
            parts.Add(c.School);
        }

        if ((c.Spells?.Count ?? 0) > 0) {
            parts.Add("spells " + string.Join(", ", c.Spells.Take(3)));
        }

        if (c.Is("RoundStart") || c.Is("RoundEnd") || c.Is("FromRound")) {
            parts.Add(c.Every > 0 ? $"round {Math.Max(1, c.Round)} then every {c.Every}" : c.Round > 0 ? $"round {c.Round}" : "every round");
        }

        if (c.Is("HealthBelow") || c.Is("MyHealthBelow")) {
            parts.Add(c.Health > 0 ? $"under {c.Health:N0} health" : $"under {c.Percent}%");
        }

        if (c.OnBoss) {
            parts.Add("on it");
        }

        if (Chance(c) < 100) {
            parts.Add($"{Chance(c)}%");
        }

        if (c.Once) {
            parts.Add("once");
        }

        string does;
        try {
            var casts = c.Steps().Where(s => s.IsCast).Select(s => s.Spell.Length > 0 ? s.Spell : s.SpellID.ToString()).Take(3).ToList();
            does = casts.Count > 0 ? "casts " + string.Join(", ", casts) : "no spell";
        }
        catch (Exception) {
            does = "?";
        }

        return $"{string.Join(", ", parts)} -> {does}";
    }

}
