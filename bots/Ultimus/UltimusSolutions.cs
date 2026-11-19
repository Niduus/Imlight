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
using System.Text.Json;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// What Ultimus worked out when he was stuck, and whether it worked (Niduus, 2026-11-14: "give Ultimus a little of the
/// autonomy" Claude has: guess the cause, try a fix, remember what worked). Kept by quest step and spot, with his guess at
/// the cause and the move he made, in logs/ultimus/stuck-solutions.json, across restarts. Shared with the Quest Team (like
/// his drop memory and player tips): it reads what worked for a quest when it gets stuck on that quest.
/// </summary>
internal static class UltimusSolutions {

    private const string FILE = "stuck-solutions.json";
    private const int MOST = 600;

    // A solution this near counts for the same spot.
    private const float NEAR = 800f;

    internal sealed class Solution {
        public string Id { get; set; } = "";
        public string Quest { get; set; } = "";
        public string Step { get; set; } = "";
        public string Zone { get; set; } = "";
        public float X { get; set; }
        public float Y { get; set; }
        public string Cause { get; set; } = "";
        public string Move { get; set; } = "";
        public string Why { get; set; } = "";
        public string By { get; set; } = "";
        public int Worked { get; set; }
        public int Failed { get; set; }
        public DateTime Last { get; set; }

        internal bool Good => Worked > Failed;
    }

    private static readonly object s_lock = new();
    private static List<Solution> s_all;

    /// <summary>A move tried for a step at a spot (counted once its result is known with <see cref="Result"/>).</summary>
    internal static Solution Tried(string quest, string step, string zone, float x, float y, string cause, string move, string why, string by) {
        lock (s_lock) {
            var all = All();
            var known = Find(all, quest, step, zone, x, y).FirstOrDefault(s => s.Move == move);
            if (known is null) {
                all.Add(known = new Solution { Id = Guid.NewGuid().ToString("N")[..10], Quest = quest ?? "", Step = step ?? "", Zone = zone ?? "", X = x, Y = y, Move = move });
                if (all.Count > MOST) {
                    all.Remove(all.OrderBy(s => s.Last).First());
                }
            }

            if (!string.IsNullOrWhiteSpace(cause)) {
                known.Cause = cause;
            }

            if (!string.IsNullOrWhiteSpace(why)) {
                known.Why = why;
            }

            known.By = by ?? "";
            known.Last = DateTime.UtcNow;
            Save();

            return known;
        }
    }

    /// <summary>Whether the move got him going again.</summary>
    internal static void Result(Solution solution, bool worked) {
        if (solution is null) {
            return;
        }

        lock (s_lock) {
            if (worked) {
                solution.Worked++;
            }
            else {
                solution.Failed++;
            }

            solution.Last = DateTime.UtcNow;
            Save();
        }
    }

    /// <summary>The move that worked best for this step at this spot (null when none has worked yet).</summary>
    internal static Solution Best(string quest, string step, string zone, float x, float y, ICollection<string> allowed = null) {
        lock (s_lock) {
            return Find(All(), quest, step, zone, x, y).Where(s => s.Good && (allowed is null || allowed.Contains(s.Move)))
                .OrderByDescending(s => s.Worked - s.Failed).ThenByDescending(s => s.Last).FirstOrDefault();
        }
    }

    /// <summary>Everything tried for this step at this spot, for the thinking (what failed is not guessed again).</summary>
    internal static List<Solution> Known(string quest, string step, string zone, float x, float y) {
        lock (s_lock) {
            return [.. Find(All(), quest, step, zone, x, y).OrderByDescending(s => s.Last).Take(8)];
        }
    }

    /// <summary>
    /// For the Quest Team: what Ultimus worked out on this quest near this spot (or anywhere on the quest), as one plain
    /// line, and whether going well away from the spot is what worked. Null when nothing is known.
    /// </summary>
    internal static (string Line, bool GoFar)? ForTeam(string quest, string zone, float x, float y) {
        if (string.IsNullOrEmpty(quest)) {
            return null;
        }

        lock (s_lock) {
            var here = All().Where(s => string.Equals(s.Quest, quest, StringComparison.OrdinalIgnoreCase) && s.Good)
                .OrderBy(s => string.Equals(s.Zone, zone, StringComparison.OrdinalIgnoreCase) && Far(s.X, s.Y, x, y) <= NEAR ? 0 : 1)
                .ThenByDescending(s => s.Worked - s.Failed).FirstOrDefault();
            if (here is null) {
                return null;
            }

            var line = $"\"{UltimusText.Short(here.Step, 60)}\": {(here.Cause.Length > 0 ? $"he thought the cause was: {here.Cause} " : "")}" +
                       $"\"{UltimusReasonMoves.Words(here.Move)}\" got him going ({here.Worked} time(s)).";

            return (line, here.Move is UltimusReasonMoves.AWAY or UltimusReasonMoves.OTHER_WAY or UltimusReasonMoves.COMMONS);
        }
    }

    /// <summary>How many solutions are known and how many work (for the log).</summary>
    internal static (int Known, int Working) Count() {
        lock (s_lock) {
            return (All().Count, All().Count(s => s.Good));
        }
    }

    // The same step (or, with no step known, the same quest) near the spot.
    private static IEnumerable<Solution> Find(List<Solution> all, string quest, string step, string zone, float x, float y)
        => all.Where(s => string.Equals(s.Quest, quest ?? "", StringComparison.OrdinalIgnoreCase)
                          && (string.IsNullOrEmpty(step) || string.Equals(s.Step, step, StringComparison.OrdinalIgnoreCase))
                          && string.Equals(s.Zone, zone ?? "", StringComparison.OrdinalIgnoreCase) && Far(s.X, s.Y, x, y) <= NEAR);

    // Ahziael's "Forget" buttons (Niduus, 2026-11-15: see and clear what was learned): the ids to forget, one per line, or
    // "all". Read by whichever bot uses the memory next (Ultimus or the Quest Team), so it works with either one running.
    internal const string FORGET_REQUEST = "stuck-solutions-forget.txt";
    private static DateTime s_forgetLook = DateTime.MinValue;

    private static void ForgetIfAsked() {
        if (DateTime.UtcNow < s_forgetLook) {
            return;
        }

        s_forgetLook = DateTime.UtcNow.AddSeconds(5);
        var request = Path.Combine(UltimusWire.SharedFolder, FORGET_REQUEST);
        if (!File.Exists(request)) {
            return;
        }

        string[] ids;
        try {
            ids = File.ReadAllLines(request);
            File.Delete(request);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return;
        }

        var forget = new HashSet<string>(ids.Select(i => i.Trim()).Where(i => i.Length > 0), StringComparer.OrdinalIgnoreCase);
        var removed = forget.Contains("all") ? s_all.Count : s_all.RemoveAll(sol => forget.Contains(sol.Id));
        if (forget.Contains("all")) {
            s_all.Clear();
        }

        Save();
        Logger.Information("Ultimus: forgot {0} stuck solution(s), as asked in Ahziael.", Logger.Args(removed));
    }

    private static List<Solution> All() {
        if (s_all is not null) {
            ForgetIfAsked();
            return s_all;
        }

        s_all = [];
        try {
            var path = Path.Combine(UltimusWire.SharedFolder, FILE);
            if (File.Exists(path) && JsonSerializer.Deserialize<List<Solution>>(File.ReadAllText(path)) is { } read) {
                s_all = [.. read.Where(s => s is not null && !string.IsNullOrEmpty(s.Move)).Select(s => {
                    s.Quest ??= "";
                    s.Step ??= "";
                    s.Zone ??= "";
                    s.Cause ??= "";
                    s.Why ??= "";
                    s.By ??= "";
                    if (string.IsNullOrEmpty(s.Id)) {
                        s.Id = Guid.NewGuid().ToString("N")[..10];
                    }

                    return s;
                })];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            Logger.Debug("Ultimus: {0} could not be read: {1}", Logger.Args(FILE, ex.Message));
        }

        ForgetIfAsked();
        return s_all;
    }

    private static void Save() {
        try {
            Directory.CreateDirectory(UltimusWire.SharedFolder);
            var path = Path.Combine(UltimusWire.SharedFolder, FILE);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(All(), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Ultimus: {0} could not be written: {1}", Logger.Args(FILE, ex.Message));
        }
    }

    private static float Far(float ax, float ay, float bx, float by) => MathF.Sqrt(((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by)));

}

/// <summary>The moves the stuck thinking may pick: only unstuck moves a player makes (the Ultimus human rule).</summary>
internal static class UltimusReasonMoves {

    internal const string ASIDE = "step_aside";
    internal const string AWAY = "walk_away_and_back";
    internal const string OTHER_WAY = "other_route";
    internal const string COMMONS = "commons_and_walk_back";
    internal const string FIGHT = "fight_nearby";
    internal const string CLICK = "click_offered";
    internal const string PARK = "set_step_aside";

    internal static string Words(string move) => move switch {
        ASIDE => "step a little aside and try again",
        AWAY => "walk well away and come back another way",
        OTHER_WAY => "take another route (a teleporter or a new way round)",
        COMMONS => "press the Commons button and walk back from there",
        FIGHT => "fight the creatures around the spot first",
        CLICK => "click what the person or thing next to him offers",
        PARK => "set the step aside for a while and do other story work",
        _ => move ?? "",
    };

}
