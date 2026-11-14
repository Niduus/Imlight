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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Imlight.Common;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// The bots' shared book of mistakes (Niduus, 2026-11-13: "have Ultimus log it's mistakes for us to find and refine later.
/// This way, other bots know what mistakes were made and what to do to prevent them", and "include his logic and reasoning
/// as well to compare their own with his"). Each mistake is kept once by its kind and spot (or its creature): what went
/// wrong, where, the step, what the bot was thinking when it happened, what prevents it, what was tried and what finally
/// worked. Ultimus writes it; the Quest Team reads it when it gets stuck at the same spot. Kept in logs/bots/mistakes.json,
/// with an easy-to-read copy in logs/bots/mistakes.txt (both survive restarts and updates).
/// </summary>
internal static class BotMistakes {

    internal const string FOLDER = "logs/bots";
    private const string FILE = "mistakes.json";
    private const string READABLE = "mistakes.txt";
    private const int MOST = 1000;

    // A mistake this near another of the same kind in the same area is the same mistake.
    private const float SAME_SPOT = 600f;

    internal const string STUCK = "stuck";
    internal const string LOST_FIGHT = "lost fight";
    internal const string NO_WAY = "no way on the map";
    internal const string SURPRISE = "surprise";

    internal sealed class Mistake {
        public string Kind { get; set; } = "";
        public string Bot { get; set; } = "";
        public string Zone { get; set; } = "";
        public float X { get; set; }
        public float Y { get; set; }
        public string Step { get; set; } = "";
        public string Foe { get; set; } = "";
        public string What { get; set; } = "";
        public string Reasoning { get; set; } = "";
        public string Prevention { get; set; } = "";
        public List<string> Tried { get; set; } = [];
        public string Worked { get; set; } = "";
        public int Times { get; set; }
        public DateTime First { get; set; }
        public DateTime Last { get; set; }
    }

    private static readonly object s_lock = new();
    private static List<Mistake> s_all;

    /// <summary>
    /// A mistake, written down (or counted again when it was made before at this spot or with this creature). Returns
    /// it, so the caller can say how many times it came up.
    /// </summary>
    internal static Mistake Record(string bot, string kind, string zone, float x, float y, string step, string what, string reasoning,
                                   string prevention, string tried = null, string foe = null) {
        lock (s_lock) {
            var all = All();
            var mistake = Find(all, kind, zone, x, y, foe);
            if (mistake is null) {
                all.Add(mistake = new Mistake { Kind = kind ?? "", Zone = zone ?? "", X = x, Y = y, Foe = foe ?? "", First = DateTime.UtcNow });
                if (all.Count > MOST) {
                    all.Remove(all.OrderBy(m => m.Last).First());
                }
            }

            mistake.Bot = bot ?? "";
            mistake.Step = step ?? "";
            mistake.What = what ?? "";
            mistake.Reasoning = reasoning ?? "";
            mistake.Prevention = prevention ?? "";
            mistake.Times++;
            mistake.Last = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(tried)) {
                mistake.Tried.Add(tried);
                if (mistake.Tried.Count > 20) {
                    mistake.Tried.RemoveAt(0);
                }
            }

            Save();

            return mistake;
        }
    }

    /// <summary>What finally fixed the mistake at this spot (the lesson every bot reads).</summary>
    internal static void Fixed(string kind, string zone, float x, float y, string worked, string foe = null) {
        lock (s_lock) {
            if (Find(All(), kind, zone, x, y, foe) is { } mistake) {
                mistake.Worked = worked ?? "";
                Save();
            }
        }
    }

    /// <summary>The mistake of this kind made at this spot (or with this creature) before, by any bot, or null.</summary>
    internal static Mistake Near(string kind, string zone, float x, float y, string foe = null) {
        lock (s_lock) {
            return Find(All(), kind, zone, x, y, foe);
        }
    }

    /// <summary>How many mistakes are written down and how many have a fix that worked (for the log).</summary>
    internal static (int Mistakes, int Fixed) Count() {
        lock (s_lock) {
            return (All().Count, All().Count(m => m.Worked.Length > 0));
        }
    }

    private static Mistake Find(List<Mistake> all, string kind, string zone, float x, float y, string foe) {
        if (!string.IsNullOrEmpty(foe)) {
            return all.FirstOrDefault(m => m.Kind == kind && string.Equals(m.Foe, foe, StringComparison.OrdinalIgnoreCase));
        }

        return all.Where(m => m.Kind == kind && m.Foe.Length == 0 && string.Equals(m.Zone, zone, StringComparison.OrdinalIgnoreCase)
                              && Far(m.X, m.Y, x, y) <= SAME_SPOT)
            .OrderBy(m => Far(m.X, m.Y, x, y)).FirstOrDefault();
    }

    private static List<Mistake> All() {
        if (s_all is not null) {
            return s_all;
        }

        s_all = [];
        try {
            var path = Path.Combine(Folder, FILE);
            if (File.Exists(path) && JsonSerializer.Deserialize<List<Mistake>>(File.ReadAllText(path)) is { } read) {
                s_all = [.. read.Where(m => m is not null).Select(m => {
                    m.Kind ??= "";
                    m.Bot ??= "";
                    m.Zone ??= "";
                    m.Step ??= "";
                    m.Foe ??= "";
                    m.What ??= "";
                    m.Reasoning ??= "";
                    m.Prevention ??= "";
                    m.Tried ??= [];
                    m.Worked ??= "";
                    return m;
                })];
            }
        }
        catch (Exception ex) {
            Logger.Debug("Bots: {0} could not be read: {1}", Logger.Args(FILE, ex.Message));
        }

        return s_all;
    }

    private static string Folder => Path.GetFullPath(FOLDER);

    private static void Save() {
        try {
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, FILE);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(All(), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, overwrite: true);
            File.WriteAllText(Path.Combine(Folder, READABLE), Readable());
        }
        catch (Exception ex) {
            Logger.Debug("Bots: {0} could not be written: {1}", Logger.Args(FILE, ex.Message));
        }
    }

    // The book in plain words, newest first, for reading and refining by hand.
    private static string Readable() {
        var text = new StringBuilder();
        text.AppendLine("The bots' mistakes, newest first. Written by Ultimus; the Quest Team reads them. (logs/bots/mistakes.json holds the same.)");
        text.AppendLine();
        foreach (var m in All().OrderByDescending(m => m.Last)) {
            text.AppendLine($"[{m.Last.ToLocalTime():yyyy-MM-dd HH:mm}] {m.Kind.ToUpperInvariant()} x{m.Times} ({m.Bot})" +
                            (m.Foe.Length > 0 ? $" against {m.Foe}" : $" in {m.Zone} at ({m.X:0}, {m.Y:0})"));
            if (m.Step.Length > 0) {
                text.AppendLine($"  Step: {m.Step}");
            }

            text.AppendLine($"  What went wrong: {m.What}");
            if (m.Reasoning.Length > 0) {
                text.AppendLine($"  Its reasoning: {m.Reasoning}");
            }

            if (m.Tried.Count > 0) {
                text.AppendLine($"  Tried: {string.Join(", ", m.Tried.Distinct())}");
            }

            text.AppendLine(m.Worked.Length > 0 ? $"  What worked: {m.Worked}" : "  What worked: nothing yet");
            if (m.Prevention.Length > 0) {
                text.AppendLine($"  To prevent it: {m.Prevention}");
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    private static float Far(float ax, float ay, float bx, float by) => MathF.Sqrt(((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by)));

}
