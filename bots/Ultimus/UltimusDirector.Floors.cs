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
using System.Linq;
using Imlight.CoreLib.Game.Tools;

namespace Imlight.CoreLib.Game.Ultimus;

// Floors and heights (Niduus, 2026-11-15: "he doesn't seem to understand the layers. While the entrance he's looking for is
// BELOW him, he doesn't quite know that"; "Have him able to read his own XYZ and the XYZ of zones and critical items needed to
// complete his goals"). He reads his own X, Y and height and those of his step (an exit, a door, an NPC, a quest object),
// knows when it is on another floor, walks by the stairs and ramps between, and says so in his tracker and log.
internal sealed partial class UltimusDirectorActor {

    // How much higher or lower a step must be to be on another floor (a deck, a balcony, a hall under it).
    private const float OTHER_FLOOR = 150f;

    /// <summary>
    /// The height of his step: its own when the plan knows it (an exit, a door), else the height of the thing standing there
    /// (an NPC, a quest object, a creature, a teleporter), else the floor under the spot nearest his own height; NaN when none
    /// is known.
    /// </summary>
    internal static float TargetZ(UltimusMember.Status leader, UltimusPlanner.Target target) {
        if (target is null) {
            return float.NaN;
        }

        if (!float.IsNaN(target.Z)) {
            return target.Z;
        }

        try {
            if (target.GlobalId != 0 && MinimapIndex.In(leader.ZoneActor).FirstOrDefault(m => m.Object.m_globalID.Full == target.GlobalId) is { } mark) {
                return mark.Object.m_location.Z;
            }

            if (target.Kind == UltimusPlanner.EXIT && OverlayQuestRoute.ExitZ(leader.Zone, target.X, target.Y) is var exitZ && !float.IsNaN(exitZ)) {
                return exitZ;
            }
        }
        catch (Exception) {
            // The floor under the spot below.
        }

        return OverlayQuestPath.FloorAt(leader.Zone, target.X, target.Y, leader.Z);
    }

    /// <summary>How much higher his step is than he is (negative: below him), or NaN when not known.</summary>
    internal static float HeightTo(UltimusMember.Status leader, UltimusPlanner.Target target)
        => TargetZ(leader, target) - leader.Z;

    /// <summary>Whether his step is on another floor than the one he stands on.</summary>
    internal static bool OtherFloor(UltimusMember.Status leader, UltimusPlanner.Target target)
        => HeightTo(leader, target) is var up && !float.IsNaN(up) && MathF.Abs(up) > OTHER_FLOOR;

    private static string Xyz(float x, float y, float z) => float.IsNaN(z) ? $"({x:0}, {y:0}, height not known)" : $"({x:0}, {y:0}, {z:0})";

    /// <summary>
    /// Where he and his step are, in words: "me (7393, 50, 946), step (7085, 43, 0): 946 below me, I look for the stairs or
    /// ramp down". Empty with no step.
    /// </summary>
    internal static string FloorWords(UltimusMember.Status leader, UltimusPlanner.Target target) {
        if (leader is null || target is null) {
            return "";
        }

        var z = TargetZ(leader, target);
        var up = z - leader.Z;
        var words = $"me {Xyz(leader.X, leader.Y, leader.Z)}, step {Xyz(target.X, target.Y, z)}";
        if (float.IsNaN(up)) {
            return words;
        }

        return words + (up > OTHER_FLOOR ? $": {up:0} above me, I look for the stairs or ramp up"
            : up < -OTHER_FLOOR ? $": {-up:0} below me, I look for the stairs or ramp down" : ": on my floor");
    }

    /// <summary>
    /// His step is on another floor: thought once per step, so he knows it before he walks (straight over to a spot above or
    /// below it does nothing; a stair or ramp does).
    /// </summary>
    private void ThinkFloor(UltimusMember.Status leader, UltimusPlanner.Target target) {
        if (!OtherFloor(leader, target)) {
            return;
        }

        var up = HeightTo(leader, target);
        Think($"\"{target.What}\" is on another floor, {MathF.Abs(up):0} {(up > 0 ? "above" : "below")} me ({FloorWords(leader, target)}). " +
              $"Standing over or under it does nothing: I take the way {(up > 0 ? "up" : "down")} the map shows, a stair or a ramp.",
            "floor|" + target.Key);
    }

}
