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

namespace Imlight.CoreLib.Game.Sigils;

/// <summary>
/// The one on/off test for sigil pads, used by the pad and by the countdown alike (Niduus, 2026-10-21: "Have Sigils
/// have a consistent check"). Flat distance only, and a player counts as off only a little past the edge, so standing
/// on the rim or being slid onto a sigil circle does not flip them on and off.
/// </summary>
internal static class SigilPadCheck {

    private const float OFF_MARGIN = 1.25f;

    public static bool IsOn(float dx, float dy, float radius)
        => (dx * dx) + (dy * dy) <= radius * radius;

    public static bool IsOff(float dx, float dy, float radius)
        => (dx * dx) + (dy * dy) > (radius * OFF_MARGIN) * (radius * OFF_MARGIN);

}
