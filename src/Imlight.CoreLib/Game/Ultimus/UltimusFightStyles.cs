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
using System.IO;
using System.Linq;
using System.Text;
using Imlight.CoreLib.Game.Combat;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Battle choices from his own results (Niduus, 2026-11-15, "all" of the robustness pitches). Each fight is of a kind (how
/// many enemies, and how strong they are together next to him), and he plays it in one of two styles: "build up" (a blade
/// or trap first, then one big hit, the old way) or "hit now" (his best hit at once, every round). Every fight's result
/// (won, rounds, health lost) is scored for its kind and style in fight-styles.json, and the style that really did best for
/// that kind is the one he plays, instead of a hand-set rule that needs tuning. Until a style has 5 fights in a kind he
/// tries it now and then, only in fights where the enemies are not stronger than him; a heal, a shield or a finishing blow
/// still always comes first. Off switch: playbook Rules.FightStyles (2 on, 1 watch only, 0 off).
/// </summary>
internal static class UltimusFightStyles {

    internal const string BUILD_UP = "build up", HIT_NOW = "hit now";
    internal const string RULE = "FightStyles";
    private const string DATA = "fight-styles.json", REPORT = "fight-styles.txt";
    internal const int TRIES = 5;
    private const double TRY_CHANCE = 0.25, MARGIN = 0.05;

    internal sealed class Score {
        public int Fights { get; set; }
        public int Wins { get; set; }
        public int Rounds { get; set; }
        public double HealthLost { get; set; }

        /// <summary>Wins count most; then fewer rounds and less health lost.</summary>
        internal double Value => Fights == 0 ? 0 : (Wins / (double) Fights) - (0.03 * Rounds / Fights) - (0.5 * HealthLost / Fights);
    }

    private static readonly object s_lock = new();
    private static Dictionary<string, Dictionary<string, Score>> s_scores;
    private static readonly ConcurrentDictionary<ulong, (string Kind, string Style, bool Tried)> s_battles = new();
    private static readonly Random s_random = new();

    /// <summary>The kind of fight: enemies (1, 2, 3+) and how strong they are together next to his own health.</summary>
    internal static string KindOf(int enemies, double enemyHealth, double myHealth) {
        var ratio = enemyHealth / Math.Max(1, myHealth);
        var count = enemies <= 1 ? "1 enemy" : enemies == 2 ? "2 enemies" : "3+ enemies";
        var strength = ratio < 0.75 ? "weaker" : ratio < 1.5 ? "even" : "stronger";

        return $"{count}, {strength}";
    }

    /// <summary>The style his own results say is best for this kind, or null when the data does not show one is better.</summary>
    internal static string Best(Dictionary<string, Score> scores) {
        if (scores is null || !scores.TryGetValue(BUILD_UP, out var build) || !scores.TryGetValue(HIT_NOW, out var hit)
            || build.Fights < TRIES || hit.Fights < TRIES) {
            return null;
        }

        return hit.Value > build.Value + MARGIN ? HIT_NOW : build.Value > hit.Value + MARGIN ? BUILD_UP : null;
    }

    /// <summary>The style for this battle, picked on his first turn and kept to the end.</summary>
    internal static string StyleFor(CombatDuelSubCircle me, IReadOnlyCollection<CombatDuelSubCircle> enemies) {
        var id = me.ParticipantObject?.m_globalID.Full ?? 0;
        if (id == 0) {
            return BUILD_UP;
        }

        if (s_battles.TryGetValue(id, out var held)) {
            return UltimusRules.On(RULE) ? held.Style : BUILD_UP;
        }

        var mine = me.ParticipantGameStats is { } s ? s.m_baseHitpoints + s.m_bonusHitpoints : 1;
        var theirs = enemies.Sum(e => e.ParticipantGameStats is { } t ? t.m_baseHitpoints + t.m_bonusHitpoints : 0);
        var kind = KindOf(enemies.Count, theirs, mine);
        var scores = Scores(kind);
        var style = Best(scores);
        var tried = false;
        var why = style is null ? "" : $"my results for fights of this kind ({kind}) say \"{style}\" wins more";
        if (style is null) {
            style = BUILD_UP;
            var few = new[] { BUILD_UP, HIT_NOW }.Where(st => (scores?.GetValueOrDefault(st)?.Fights ?? 0) < TRIES).ToList();
            if (few.Count > 0 && !kind.EndsWith("stronger", StringComparison.Ordinal) && Roll() < TRY_CHANCE) {
                style = few[Roll() < 0.5 ? 0 : few.Count - 1];
                tried = true;
                why = $"I have fewer than {TRIES} fights of this kind ({kind}) played \"{style}\", so I try it to learn which wins";
            }
        }

        s_battles[id] = (kind, style, tried);
        if (style == BUILD_UP && !tried) {
            return BUILD_UP;
        }

        if (UltimusRules.Watching(RULE)) {
            UltimusRules.WouldHave(RULE, $"played this fight \"{style}\": {why}", kind);
            s_battles[id] = (kind, BUILD_UP, false);

            return BUILD_UP;
        }

        if (!UltimusRules.On(RULE)) {
            s_battles[id] = (kind, BUILD_UP, false);

            return BUILD_UP;
        }

        UltimusLink.Note(UltimusRoster.NAME, $"I play this fight \"{style}\": {why}.", UltimusLink.THOUGHT);

        return style;
    }

    private static double Roll() {
        lock (s_random) {
            return s_random.NextDouble();
        }
    }

    /// <summary>The fight ended: its result goes to its kind and style. Returns (kind, style) for the fight record.</summary>
    internal static (string Kind, string Style) End(ulong globalId, bool won, int rounds, double healthLost, bool count = true) {
        if (!s_battles.TryRemove(globalId, out var battle) || !count) {
            return ("", "");
        }

        lock (s_lock) {
            s_scores ??= Read();
            if (!s_scores.TryGetValue(battle.Kind, out var kind)) {
                s_scores[battle.Kind] = kind = [];
            }

            if (!kind.TryGetValue(battle.Style, out var score)) {
                kind[battle.Style] = score = new Score();
            }

            score.Fights++;
            score.Wins += won ? 1 : 0;
            score.Rounds += Math.Max(0, rounds);
            score.HealthLost += Math.Clamp(healthLost, 0, 1);
            Write();
        }

        return (battle.Kind, battle.Style);
    }

    private static Dictionary<string, Score> Scores(string kind) {
        lock (s_lock) {
            s_scores ??= Read();

            return s_scores.TryGetValue(kind, out var found) ? found.ToDictionary(k => k.Key, k => new Score {
                Fights = k.Value.Fights, Wins = k.Value.Wins, Rounds = k.Value.Rounds, HealthLost = k.Value.HealthLost,
            }) : null;
        }
    }

    private static Dictionary<string, Dictionary<string, Score>> Read() {
        try {
            var path = Path.Combine(UltimusWire.Folder, DATA);
            if (File.Exists(path) && UltimusWire.Read<Dictionary<string, Dictionary<string, Score>>>(File.ReadAllText(path)) is { } read) {
                return read;
            }
        }
        catch (Exception) {
            // A broken file starts the scores again (fights.jsonl still holds every fight to rebuild them).
        }

        return [];
    }

    private static void Write() {
        try {
            Directory.CreateDirectory(UltimusWire.Folder);
            File.WriteAllText(Path.Combine(UltimusWire.Folder, DATA), UltimusWire.Write(s_scores));
            File.WriteAllText(Path.Combine(UltimusWire.Folder, REPORT), Report(s_scores, DateTime.Now));
        }
        catch (IOException) {
        }
        catch (UnauthorizedAccessException) {
        }
    }

    internal static string Report(Dictionary<string, Dictionary<string, Score>> scores, DateTime written) {
        var text = new StringBuilder();
        text.AppendLine($"HOW HE FIGHTS EACH KIND OF FIGHT, FROM HIS OWN RESULTS (written {written:yyyy-MM-dd HH:mm})");
        text.AppendLine($"\"{BUILD_UP}\" = a blade or trap first, then one big hit. \"{HIT_NOW}\" = his best hit at once. A style needs {TRIES} fights of a kind before it counts.");
        foreach (var (kind, styles) in scores.OrderBy(k => k.Key, StringComparer.Ordinal)) {
            var best = Best(styles);
            text.AppendLine();
            text.AppendLine($"{kind}: {(best is null ? $"plays \"{BUILD_UP}\" (no clear winner yet)" : $"plays \"{best}\"")}");
            foreach (var (style, s) in styles.OrderBy(k => k.Key, StringComparer.Ordinal)) {
                if (s.Fights == 0) {
                    continue;
                }

                text.AppendLine($"  {style,-9} {s.Fights,4} fights, won {s.Wins * 100.0 / s.Fights,3:0}%, {s.Rounds / (double) s.Fights,4:0.0} rounds, {s.HealthLost * 100 / s.Fights,3:0}% health lost on average");
            }
        }

        return text.ToString();
    }
}
