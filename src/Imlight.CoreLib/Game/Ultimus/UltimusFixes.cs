/*
 * Imlight
 * Copyright (C) 2025 Revive101
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
using System.Text.Json;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// What Ultimus did each time it was flagged ABNORMAL for being stuck, and what got it free (Niduus, 2026-11-13: "Each time
/// it gets flagged as abnormal, it then marks it down, making the bot forcefully pick new routes to achieve it's goal. It
/// should never allow itself to make the same or adjacent abnormal flags more than once. I want Ultimus to strictly
/// improve"). A flag is kept by its street, its spot and the step it was on; a flag at the same or a nearby spot for the
/// same step is the same flag. The next time, the fix that worked there is tried first; when none has worked yet, a fix
/// not tried there before; a fix is never tried twice at one flag until every other one was. Kept in
/// logs/ultimus/abnormal-fixes.json, so it is not lost when the server restarts or updates.
/// </summary>
internal static class UltimusFixes {

    // The fixes, in the order they are tried when nothing is known yet: the cheapest first.
    internal const string ASIDE = "step aside";
    internal const string OTHER_WAY = "another way (a teleporter or a new route)";
    internal const string AWAY = "walk well away and come back another way";
    internal const string START = "go back to where the area starts";
    internal const string SKIP = "have Ahziael finish the step";

    internal static readonly string[] Order = [ASIDE, OTHER_WAY, AWAY, START, SKIP];

    // A flag this near another one for the same step is the same flag.
    private const float ADJACENT = 600f;

    private const string FILE = "abnormal-fixes.json";
    private const int MOST = 500;

    internal sealed class Flag {
        public string Zone { get; set; } = "";
        public float X { get; set; }
        public float Y { get; set; }
        public string Step { get; set; } = "";
        public int Times { get; set; }
        public List<string> Tried { get; set; } = [];
        public string Worked { get; set; } = "";
        public DateTime Last { get; set; }
    }

    private static readonly object s_lock = new();
    private static List<Flag> s_flags;

    /// <summary>
    /// A flag at this spot for this step: written down (or counted again), and the fix to make now. <paramref name="skip"/>:
    /// fixes that can't be made here (going back to the start while standing on it). The flag is returned to say how
    /// many times it came up.
    /// </summary>
    internal static (string Fix, Flag Flag) Next(string zone, float x, float y, string step, ICollection<string> skip = null) {
        lock (s_lock) {
            var all = Flags();
            var flag = all.Where(f => string.Equals(f.Zone, zone, StringComparison.OrdinalIgnoreCase) && f.Step == (step ?? "")
                                      && Far(f.X, f.Y, x, y) <= ADJACENT)
                .OrderBy(f => Far(f.X, f.Y, x, y)).FirstOrDefault();
            if (flag is null) {
                all.Add(flag = new Flag { Zone = zone ?? "", X = x, Y = y, Step = step ?? "" });
                if (all.Count > MOST) {
                    all.Remove(all.OrderBy(f => f.Last).First());
                }
            }

            flag.Times++;
            flag.Last = DateTime.UtcNow;

            // What worked here before, once; then what was not tried yet; then, all tried, round again from the start.
            var allowed = Order.Where(f => skip is null || !skip.Contains(f)).ToList();
            string fix;
            if (flag.Worked.Length > 0 && allowed.Contains(flag.Worked) && flag.Tried.LastOrDefault() != flag.Worked) {
                fix = flag.Worked;
            }
            else if (allowed.FirstOrDefault(f => !flag.Tried.Contains(f)) is { } fresh) {
                fix = fresh;
            }
            else {
                flag.Tried.Clear();
                fix = allowed.FirstOrDefault(f => f != SKIP) ?? SKIP;
            }

            flag.Tried.Add(fix);
            Save();

            return (fix, flag);
        }
    }

    /// <summary>It got free after <paramref name="flag"/>'s last fix: that fix is what works there.</summary>
    internal static void Worked(Flag flag) {
        if (flag is null || flag.Tried.Count == 0) {
            return;
        }

        lock (s_lock) {
            flag.Worked = flag.Tried[^1];
            Save();
        }
    }

    /// <summary>How many flags are known and how many have a fix that worked (for the log).</summary>
    internal static (int Flags, int Fixed) Count() {
        lock (s_lock) {
            return (Flags().Count, Flags().Count(f => f.Worked.Length > 0));
        }
    }

    private static List<Flag> Flags() {
        if (s_flags is not null) {
            return s_flags;
        }

        s_flags = [];
        try {
            var path = Path.Combine(UltimusWire.Folder, FILE);
            if (File.Exists(path) && JsonSerializer.Deserialize<List<Flag>>(File.ReadAllText(path)) is { } read) {
                s_flags = [.. read.Where(f => f is not null).Select(f => {
                    f.Tried ??= [];
                    f.Worked ??= "";
                    f.Step ??= "";
                    f.Zone ??= "";
                    return f;
                })];
            }
        }
        catch (Exception ex) {
            Logger.Debug("Ultimus: {0} could not be read: {1}", Logger.Args(FILE, ex.Message));
        }

        return s_flags;
    }

    private static void Save() {
        try {
            Directory.CreateDirectory(UltimusWire.Folder);
            var path = Path.Combine(UltimusWire.Folder, FILE);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(Flags(), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) {
            Logger.Debug("Ultimus: {0} could not be written: {1}", Logger.Args(FILE, ex.Message));
        }
    }

    private static float Far(float ax, float ay, float bx, float by) => MathF.Sqrt(((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by)));

}
