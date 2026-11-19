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
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Runs a player guided him through (Niduus, 2026-11-16: "Did you use any of the data accumulated when I dragged him
/// through by his hair?"). While Ultimus goes along with a group mate in a dungeon, the way they walk is written down:
/// each spot on it, and each teleporter that moved them on. Alone in that dungeon later, when the map shows no way on to
/// his step, he follows that run forward from where he stands, as a player retraces the way a friend showed them.
/// Kept in guided-runs.json (Ultimus's folder), the latest run of each area; every dungeon with a guided run uses it.
/// </summary>
internal static class UltimusGuided {

    /// <summary>One spot of a guided run; Jump: a teleporter moved them on from here.</summary>
    internal sealed record Spot(float X, float Y, float Z, bool Jump = false);

    /// <summary>A guided run through one area: who led it, when, and its spots in order.</summary>
    internal sealed record Run(string Guide, DateTime At, List<Spot> Spots);

    private const string FILE = "guided-runs.json";
    // How far apart the spots written down are, and how big a move in a moment counts as a teleporter.
    private const float EVERY = 200f;
    private const float JUMP = 1200f;
    private const int MOST = 4000;
    // Shorter than this is no run, just a few steps together.
    private const int LEAST = 15;
    private const float FLOOR = 300f;

    private static readonly object s_lock = new();
    private static Dictionary<string, Run> s_runs;

    // The run being written down now.
    private static string s_zone;
    private static string s_guide;
    private static List<Spot> s_now = [];
    private static (float X, float Y, float Z) s_seen;
    private static DateTime s_seenAt = DateTime.MinValue;
    private static DateTime s_savedAt = DateTime.MinValue;
    private static DateTime s_startedAt = DateTime.MinValue;

    /// <summary>He is with his group mate (<paramref name="guide"/>) in a dungeon at this spot: written down.</summary>
    internal static void Record(string zone, string guide, float x, float y, float z) {
        if (string.IsNullOrEmpty(zone) || !float.IsFinite(x) || !float.IsFinite(y)) {
            return;
        }

        lock (s_lock) {
            if (!string.Equals(zone, s_zone, StringComparison.OrdinalIgnoreCase)) {
                Finish();
                s_zone = zone;
                s_now = [];
                s_seenAt = DateTime.MinValue;
                s_startedAt = DateTime.UtcNow;
            }

            s_guide = guide ?? s_guide ?? "";
            var now = DateTime.UtcNow;
            // Moved far since the last look a moment ago: a teleporter. The spot he stood on just before is its spot.
            var jumped = s_seenAt != DateTime.MinValue && now - s_seenAt < TimeSpan.FromSeconds(5) && Far(s_seen.X, s_seen.Y, x, y) >= JUMP;
            var before = s_seen;
            s_seen = (x, y, z);
            s_seenAt = now;
            if (s_now.Count >= MOST) {
                return;
            }

            if (jumped) {
                if (s_now.Count == 0 || Far(s_now[^1].X, s_now[^1].Y, before.X, before.Y) > 1) {
                    s_now.Add(new Spot(before.X, before.Y, before.Z, Jump: true));
                }
                else {
                    s_now[^1] = s_now[^1] with { Jump = true };
                }
            }
            else if (s_now.Count > 0 && Far(s_now[^1].X, s_now[^1].Y, x, y) < EVERY) {
                return;
            }

            s_now.Add(new Spot(x, y, z));

            if (now - s_savedAt > TimeSpan.FromMinutes(1)) {
                Keep(false);
            }
        }
    }

    /// <summary>The run being written down now: its area, spots and teleporters (0 when none).</summary>
    internal static (string Zone, int Spots, int Jumps) Now() {
        lock (s_lock) {
            return (s_zone, s_now.Count, s_now.Count(p => p.Jump));
        }
    }

    /// <summary>Not with a group mate in a dungeon any more: the run so far is kept.</summary>
    internal static void Stop() {
        lock (s_lock) {
            Finish();
        }
    }

    /// <summary>The guided run of this area, or null.</summary>
    internal static Run Of(string zone) {
        if (string.IsNullOrEmpty(zone)) {
            return null;
        }

        lock (s_lock) {
            return Runs().GetValueOrDefault(zone);
        }
    }

    /// <summary>
    /// The next spot to walk to on the guided run, from (x, y, z) toward a step at (toX, toY, toZ): forward along the run
    /// from the spot nearest him on his floor, to its next teleporter or a spot a little way on, whichever comes first.
    /// Null when the area has no guided run, he is not near it, or the run does not go on toward the step.
    /// </summary>
    internal static (float X, float Y, bool Pad, float LandX, float LandY)? Ahead(string zone, float x, float y, float z, float toX, float toY, float toZ) {
        if (Of(zone) is not { Spots.Count: >= LEAST } run) {
            return null;
        }

        var spots = run.Spots;
        var here = -1;
        var best = 700f;
        for (var i = 0; i < spots.Count; i++) {
            var d = Far(spots[i].X, spots[i].Y, x, y);
            if (d < best && OnFloor(z, spots[i].Z)) {
                best = d;
                here = i;
            }
        }

        if (here < 0) {
            return null;
        }

        // Where the run comes nearest the step: it must be further on than here.
        var goal = here;
        var goalFar = Far(spots[here].X, spots[here].Y, toX, toY) + (OnFloor(toZ, spots[here].Z) ? 0 : 5000);
        for (var i = here + 1; i < spots.Count; i++) {
            var d = Far(spots[i].X, spots[i].Y, toX, toY) + (OnFloor(toZ, spots[i].Z) ? 0 : 5000);
            if (d < goalFar) {
                goalFar = d;
                goal = i;
            }
        }

        if (goal <= here) {
            return null;
        }

        for (var i = here; i <= goal; i++) {
            if (spots[i].Jump && i + 1 < spots.Count) {
                return (spots[i].X, spots[i].Y, true, spots[i + 1].X, spots[i + 1].Y);
            }

            if (Far(spots[i].X, spots[i].Y, x, y) >= 600) {
                return (spots[i].X, spots[i].Y, false, float.NaN, float.NaN);
            }
        }

        return (spots[goal].X, spots[goal].Y, false, float.NaN, float.NaN);
    }

    private static void Finish() {
        Keep(true);
        s_zone = null;
        s_now = [];
    }

    // The run so far replaces the area's older one once it is long enough.
    private static void Keep(bool done) {
        s_savedAt = DateTime.UtcNow;
        if (s_zone is null || s_now.Count < LEAST) {
            return;
        }

        var runs = Runs();
        // A shorter run (a few rooms together) does not replace a fuller one of the same area.
        if (runs.TryGetValue(s_zone, out var old) && old.Spots.Count > s_now.Count && !ReferenceEquals(old.Spots, s_now)
            && old.At < s_startedAt) {
            return;
        }

        runs[s_zone] = new Run(s_guide ?? "", DateTime.UtcNow, [.. s_now]);
        try {
            Directory.CreateDirectory(UltimusWire.SharedFolder);
            File.WriteAllText(Path.Combine(UltimusWire.SharedFolder, FILE), UltimusWire.Write(runs));
            if (done) {
                Logger.Information("Ultimus: kept the guided run through {0} with {1} ({2} spots, {3} teleporters).",
                    Logger.Args(s_zone, s_guide, s_now.Count, s_now.Count(s => s.Jump)));
            }
        }
        catch (Exception ex) {
            Logger.Debug("Ultimus: {0} could not be written: {1}", Logger.Args(FILE, ex.Message));
        }
    }

    private static Dictionary<string, Run> Runs() {
        if (s_runs is not null) {
            return s_runs;
        }

        s_runs = new Dictionary<string, Run>(StringComparer.OrdinalIgnoreCase);
        try {
            var path = Path.Combine(UltimusWire.SharedFolder, FILE);
            if (File.Exists(path) && UltimusWire.Read<Dictionary<string, Run>>(File.ReadAllText(path)) is { } read) {
                foreach (var (zone, run) in read) {
                    if (run?.Spots is { Count: > 0 }) {
                        s_runs[zone] = run;
                    }
                }
            }
        }
        catch (Exception ex) {
            Logger.Debug("Ultimus: {0} could not be read: {1}", Logger.Args(FILE, ex.Message));
        }

        return s_runs;
    }

    private static bool OnFloor(float z, float oz) => !float.IsFinite(z) || !float.IsFinite(oz) || MathF.Abs(z - oz) <= FLOOR;

    private static float Far(float x1, float y1, float x2, float y2) => MathF.Sqrt(((x1 - x2) * (x1 - x2)) + ((y1 - y2) * (y1 - y2)));

}

internal sealed partial class UltimusDirectorActor {

    private DateTime _guidedSaidAt = DateTime.MinValue;

    /// <summary>
    /// With a group mate in a dungeon: the way is written down as a guided run. The group mate's own steps, not his: a
    /// player who takes the wheel walks the true way, and Ultimus trails a little behind it and cuts corners (Niduus,
    /// 2026-11-17: "I want to take the wheel so badly and have him follow me"). While the mate is in a battle nothing is
    /// written (they stand on the circle). Written by Ultimus himself; every seat (TCO7 too) follows it.
    /// </summary>
    private void RecordGuided(UltimusMember.Status leader, UltimusPlanner.Target target) {
        if (!UltimusSeat.Current.IsUltimus) {
            return;
        }

        if (leader is null || !InDungeon(leader) || _partyMate == 0) {
            if (leader is not null && (!InDungeon(leader) || _partyMate == 0)) {
                UltimusGuided.Stop();
            }

            return;
        }

        // The group mate in the same copy of this dungeon, out of battle: their step is the run's.
        if (PlayerCheats.LiveWizard(_partyMate) is { } mate && OverlayQuestTracker.SameZone(mate.Zone ?? "", leader.Zone ?? "")
            && !CombatDuelComponent.IsInBattle(Wizard.GetGameObjectId(_partyMate))) {
            UltimusGuided.Record(leader.Zone, PartyMateName, mate.Location.X, mate.Location.Y, mate.Location.Z);
        }
    }

    /// <summary>
    /// No way on the map to his step in a dungeon: the next spot of the run a player guided him through here, as a walk.
    /// Null when there is none.
    /// </summary>
    private List<(float X, float Y)> GuidedWalk(UltimusMember.Status member, float toX, float toY, float toZ, string what) {
        if (!InDungeon(member) || UltimusGuided.Ahead(member.Zone, member.X, member.Y, member.Z, toX, toY, toZ) is not { } next) {
            return null;
        }

        // The run's spot before a teleporter is only near it (spots are written every few steps): the teleporter itself,
        // the known one there whose landing is where the run came out.
        if (next.Pad && UltimusPads.In(member.Zone)
                .Where(p => UltimusPlanner.Far(p.X, p.Y, next.X, next.Y) <= 500 && UltimusPlanner.Far(p.ToX, p.ToY, next.LandX, next.LandY) <= 700)
                .OrderBy(p => UltimusPlanner.Far(p.X, p.Y, next.X, next.Y)).FirstOrDefault() is { } pad) {
            next = (pad.X, pad.Y, true, next.LandX, next.LandY);
        }

        // At the run's teleporter spot and still here: it did not take him; the other ways get their turn.
        if (next.Pad && UltimusPlanner.Far(member.X, member.Y, next.X, next.Y) <= 120) {
            return null;
        }

        var points = OverlayQuestPath.Walk(member.CharId, member.Zone, member.X, member.Y, next.X, next.Y, body: true);
        if (points.Count == 0 || UltimusPlanner.Far(points[^1].X, points[^1].Y, next.X, next.Y) > 150) {
            // The map has a door shut that the run walked through: straight there, as they went.
            points = [(member.X, member.Y), (next.X, next.Y)];
        }

        if (DateTime.UtcNow - _guidedSaidAt > TimeSpan.FromSeconds(30)) {
            _guidedSaidAt = DateTime.UtcNow;
            var guide = UltimusGuided.Of(member.Zone)?.Guide;
            Note("Ultimus", $"{member.Name} follows the run {(string.IsNullOrEmpty(guide) ? "a player" : guide)} guided him through here, toward \"{what}\"" +
                            (next.Pad ? " (to the teleporter they took)." : "."));
            Think($"The map shows no way to \"{what}\", but I went through here with {(string.IsNullOrEmpty(guide) ? "a player" : guide)} " +
                  "before. I go the way they showed me.", "guided|" + member.Zone);
        }

        return points;
    }

}
