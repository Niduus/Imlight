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

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// All the way into a dungeon's elevator (Niduus, 2026-11-17: "MAKE IT FORCE ITSELF TO LOOK DEEP IN THE ELEVATORS FOR THE
/// TRIGGERS"). Big Ben's teleporters up sit in a small elevator alcove past each floor's door, and our ground map draws the
/// alcove's mouth (the door) as a wall, so a walk to the teleporter can end short of it and he stands there. When he stops
/// short of a teleporter in a dungeon, he walks straight in: to its middle, then deeper into the alcove, then its left and
/// right sides, as a player who pushes into the back of the elevator until it fires.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    private const float DEEP_NEAR = 1200f;
    private const int DEEP_TRIES = 8;
    private static readonly TimeSpan s_deepStall = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan s_deepRest = TimeSpan.FromSeconds(60);

    private string _deepPad;
    private (float X, float Y) _deepFrom;
    private (float X, float Y) _deepLast;
    private DateTime _deepStillSince = DateTime.MinValue;
    private DateTime _deepRestUntil = DateTime.MinValue;
    private int _deepTry = -1;

    /// <summary>A spot deep in the teleporter at (toX, toY) to walk straight to, while he stands short of it; else null.</summary>
    private (float X, float Y, string Pad)? DeepIntoPad(UltimusMember.Status member, float toX, float toY) {
        if (!InDungeon(member) || string.IsNullOrEmpty(member.Zone)) {
            return null;
        }

        var pad = UltimusPads.In(member.Zone).Where(p => p.Click == 0 && UltimusPlanner.Far(p.X, p.Y, toX, toY) <= 300)
            .OrderBy(p => UltimusPlanner.Far(p.X, p.Y, toX, toY)).FirstOrDefault();
        var far = pad is null ? float.MaxValue : UltimusPlanner.Far(member.X, member.Y, pad.X, pad.Y);
        var key = pad is null ? null : $"{member.Zone}|{pad.X:0}|{pad.Y:0}";
        if (pad is null || far > DEEP_NEAR) {
            if (pad is null || key != _deepPad) {
                _deepPad = null;
                _deepTry = -1;
            }

            return null;
        }

        var now = DateTime.UtcNow;
        if (key != _deepPad) {
            _deepPad = key;
            _deepFrom = (member.X, member.Y);
            _deepTry = -1;
            _deepStillSince = DateTime.MinValue;
            _deepRestUntil = DateTime.MinValue;
        }

        if (now < _deepRestUntil) {
            return null;
        }

        // Still: not walking and not moved since the last look.
        var moved = UltimusPlanner.Far(member.X, member.Y, _deepLast.X, _deepLast.Y) > 40;
        _deepLast = (member.X, member.Y);
        if (member.Walking || moved) {
            _deepStillSince = DateTime.MinValue;
        }
        else if (_deepStillSince == DateTime.MinValue) {
            _deepStillSince = now;
        }

        var stalled = _deepStillSince != DateTime.MinValue && now - _deepStillSince >= s_deepStall;
        if (_deepTry < 0) {
            // Standing on its middle already is the step-off-and-in-again fix's job (UltimusDirector.Routes.cs).
            if (!stalled || far <= PAD_ON) {
                return null;
            }

            _deepTry = 0;
            _deepStillSince = DateTime.MinValue;
            Think($"I stopped {far:0} short of the teleporter at ({pad.X:0}, {pad.Y:0}). It sits in an elevator past the door, and " +
                  "my map has the way in shut, so I walk straight in, all the way to the back of it, until it takes me.", "deeppad|" + key);
        }
        else if (stalled) {
            _deepTry++;
            _deepStillSince = DateTime.MinValue;
            if (_deepTry >= DEEP_TRIES) {
                Note("Ultimus", $"{member.Name} walked into every part of the teleporter at ({pad.X:0}, {pad.Y:0}) in {UltimusText.Zone(member.Zone)} " +
                                $"({DEEP_TRIES} tries) and it never took it on; tries the other ways for a minute.", UltimusLink.ABNORMAL);
                _deepTry = -1;
                _deepRestUntil = now + s_deepRest;

                return null;
            }
        }

        return Spot(_deepTry);

        // In order: its middle, deeper in (away from where he came from), then its sides, deeper again.
        (float X, float Y, string Pad) Spot(int i) {
            var dx = pad.X - _deepFrom.X;
            var dy = pad.Y - _deepFrom.Y;
            var len = MathF.Sqrt((dx * dx) + (dy * dy));
            (dx, dy) = len < 1 ? (1f, 0f) : (dx / len, dy / len);
            var (px, py) = (-dy, dx);
            var reach = MathF.Max(pad.Reach, 150f);
            var (deep, side) = (i % 4) switch {
                0 => (0f, 0f),
                1 => (reach * 0.6f, 0f),
                2 => (reach * 0.4f, reach * 0.5f),
                _ => (reach * 0.4f, -reach * 0.5f),
            };
            if (i >= 4) {
                deep += reach * 0.3f;
            }

            return (pad.X + (dx * deep) + (px * side), pad.Y + (dy * deep) + (py * side), pad.Name);
        }
    }
}
