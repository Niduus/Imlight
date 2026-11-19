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

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// The bots' escape lessons by situation, not by spot (Niduus, 2026-11-15: "make sure all situations like this are also
/// future-proof for later worlds/quests/scenarios. This goes for all his success in this regard"). Each time Ultimus or the
/// Quest Team gets free, the move that did it is written down twice: for that spot (UltimusFixes, UltimusSolutions, as
/// before) and for the kind of situation it was (in a dungeon or on a street, what the step was, how the place was shaped,
/// and whether it stood still or walked without getting closer). A wizard stuck somewhere it has never been tries first the
/// move that worked most in situations like it, in any world. A move that failed in a situation counts against it there.
/// Kept in logs/bots/stuck-patterns.json; the patterns from his first worlds ship with Imlight
/// (spiraldb-extra/Routes/proven-patterns.json), so a new Ultimus, the Quest Team and later worlds start with them.
/// </summary>
internal static class BotLessons {

    // How it was stuck: standing still, or moving without getting closer (a progress check, no progress, a time budget).
    internal const string STILL = "standing still";
    internal const string NOT_CLOSER = "walking without getting closer";

    // The escape moves, by the names Ultimus's fixes use (the Quest Team's step aside and walk away map onto them).
    internal const string ASIDE = "step aside";
    internal const string OTHER_WAY = "another way (a teleporter or a new route)";
    internal const string AWAY = "walk well away and come back another way";
    internal const string COMMONS = "press the Commons button and walk back from there";

    private const string FILE = "stuck-patterns.json";

    /// <summary>A situation: where (dungeon or street), the step's kind, the place's shape, and how it was stuck.</summary>
    internal sealed record Situation(string Place, string Step, string Shape, string Seen) {
        internal string Key => $"{Place}|{Step}|{Shape}|{Seen}";

        // From the most alike to the most general.
        internal IEnumerable<string> Levels() {
            yield return Key;
            yield return $"{Place}|{Step}|*|{Seen}";
            yield return $"{Place}|*|*|{Seen}";
            yield return $"*|*|*|{Seen}";
        }

        internal static Situation FromKey(string key)
            => key?.Split('|') is [var place, var step, var shape, var seen] ? new Situation(place, step, shape, seen) : null;

        internal string Words => $"{(Place == "dungeon" ? "in a dungeon" : "on a street")}, {Step} step, {Shape}, {Seen}";
    }

    internal sealed class Tally {
        public int Worked { get; set; }
        public int Failed { get; set; }
        public DateTime Last { get; set; }
        public List<string> Where { get; set; } = [];
    }

    private static readonly object s_lock = new();
    private static Dictionary<string, Dictionary<string, Tally>> s_patterns;
    private static Dictionary<string, Dictionary<string, Tally>> s_shipped;

    /// <summary>The situation, from what the director sees. <paramref name="why"/>: the stuck alarm's own words.</summary>
    internal static Situation Of(bool dungeon, string stepKind, string shape, string why) {
        var seen = why is not null && (why.Contains("progress", StringComparison.OrdinalIgnoreCase) || why.Contains("budget", StringComparison.OrdinalIgnoreCase)
                                       || why.Contains("closer", StringComparison.OrdinalIgnoreCase))
            ? NOT_CLOSER : STILL;

        return new Situation(dungeon ? "dungeon" : "street", string.IsNullOrEmpty(stepKind) ? "walk" : stepKind.ToLowerInvariant(),
            string.IsNullOrEmpty(shape) ? "open ground" : shape, seen);
    }

    /// <summary>The Quest Team's and the stuck thinking's move names, in the fixes' names.</summary>
    internal static string Canon(string move) => move switch {
        "step_aside" => ASIDE,
        "walk_away_and_back" => AWAY,
        "other_route" => OTHER_WAY,
        "commons_and_walk_back" => COMMONS,
        _ => move ?? "",
    };

    /// <summary>A move was made in this situation and it freed the wizard (or not).</summary>
    internal static void Learn(Situation situation, string move, bool worked, string where) {
        move = Canon(move);
        if (situation is null || string.IsNullOrEmpty(move)) {
            return;
        }

        lock (s_lock) {
            var all = Patterns();
            foreach (var key in situation.Levels()) {
                if (!all.TryGetValue(key, out var moves)) {
                    all[key] = moves = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);
                }

                if (!moves.TryGetValue(move, out var tally)) {
                    moves[move] = tally = new Tally();
                }

                if (worked) {
                    tally.Worked++;
                    if (!string.IsNullOrEmpty(where) && !tally.Where.Contains(where)) {
                        tally.Where.Add(where);
                        if (tally.Where.Count > 5) {
                            tally.Where.RemoveAt(0);
                        }
                    }
                }
                else {
                    tally.Failed++;
                }

                tally.Last = DateTime.UtcNow;
            }

            Save();
        }

        if (worked) {
            Logger.Information("Bots: \"{0}\" freed a wizard {1}; written down for that kind of place too, in every world.",
                Logger.Args(move, situation.Words));
        }
    }

    /// <summary>
    /// The move that worked most in situations like this one, among <paramref name="allowed"/> and not in
    /// <paramref name="tried"/> (the ones already made at this spot): from the most alike situation that has one. Null
    /// when none has worked more than it failed. <paramref name="line"/>: in plain words, with where it worked.
    /// </summary>
    internal static string Best(Situation situation, ICollection<string> allowed, ICollection<string> tried, out string line) {
        line = "";
        if (situation is null) {
            return null;
        }

        lock (s_lock) {
            foreach (var key in situation.Levels()) {
                var mine = Patterns().GetValueOrDefault(key);
                var shipped = Shipped().GetValueOrDefault(key);
                var moves = (mine?.Keys ?? Enumerable.Empty<string>()).Concat(shipped?.Keys ?? Enumerable.Empty<string>())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(m => (allowed is null || allowed.Contains(m)) && (tried is null || !tried.Contains(m)))
                    .Select(m => (Move: m, Worked: (mine?.GetValueOrDefault(m)?.Worked ?? 0) + (shipped?.GetValueOrDefault(m)?.Worked ?? 0),
                        Failed: (mine?.GetValueOrDefault(m)?.Failed ?? 0) + (shipped?.GetValueOrDefault(m)?.Failed ?? 0),
                        Where: (mine?.GetValueOrDefault(m)?.Where ?? []).Concat(shipped?.GetValueOrDefault(m)?.Where ?? []).Distinct().Take(2).ToList()))
                    // Worked twice or more, and at least one try in four: in his logs (2026-11-13/14) no move freed him more
                    // often than that when he stood still, so there the usual order stays; walking without getting closer,
                    // the Commons button freed him 8 times in 11 and comes first.
                    .Where(m => m.Worked >= 2 && m.Worked * 3 >= m.Failed)
                    .OrderByDescending(m => m.Worked / (m.Worked + m.Failed + 4.0)).ThenByDescending(m => m.Worked).ToList();
                if (moves.Count == 0) {
                    continue;
                }

                var best = moves[0];
                var alike = key == situation.Key ? "situations just like this" : "situations like this";
                line = $"In {alike} ({situation.Words}) \"{best.Move}\" freed a wizard {best.Worked} time(s) and failed {best.Failed}" +
                       (best.Where.Count > 0 ? $" (for example {string.Join("; ", best.Where)})" : "") + ", so I try it first here.";

                return best.Move;
            }
        }

        return null;
    }

    /// <summary>How many situations are known and how many moves were written down (for the log).</summary>
    internal static int Count() {
        lock (s_lock) {
            return Patterns().Count(p => !p.Key.StartsWith("*", StringComparison.Ordinal) && p.Value.Values.Any(t => t.Worked > 0));
        }
    }

    // ---- The files. ----

    private static string FilePath => Path.Combine(Path.GetFullPath("logs/bots"), FILE);

    private static Dictionary<string, Dictionary<string, Tally>> Patterns() {
        if (s_patterns is not null) {
            return s_patterns;
        }

        s_patterns = Read(FilePath);

        return s_patterns;
    }

    private static Dictionary<string, Dictionary<string, Tally>> Shipped() {
        if (s_shipped is not null) {
            return s_shipped;
        }

        try {
            s_shipped = Read(Path.Combine(WizardData.SpiralDB.ExtraPath, "Routes", "proven-patterns.json"));
        }
        catch (Exception e) {
            Logger.Debug("Bots: the shipped stuck patterns could not be read: {0}", Logger.Args(e.Message));
            s_shipped = [];
        }

        return s_shipped;
    }

    private static Dictionary<string, Dictionary<string, Tally>> Read(string path) {
        var read = new Dictionary<string, Dictionary<string, Tally>>(StringComparer.OrdinalIgnoreCase);
        try {
            if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Tally>>>(File.ReadAllText(path)) is { } all) {
                foreach (var (key, moves) in all.Where(p => p.Value is not null)) {
                    read[key] = new Dictionary<string, Tally>(moves.Where(m => m.Value is not null)
                        .Select(m => {
                            m.Value.Where ??= [];
                            return m;
                        }), StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
            Logger.Debug("Bots: {0} could not be read: {1}", Logger.Args(path, e.Message));
        }

        return read;
    }

    private static void Save() {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(s_patterns, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(FilePath + ".tmp", FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Logger.Debug("Bots: {0} could not be written: {1}", Logger.Args(FILE, e.Message));
        }
    }

}
