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

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// What each bot is doing and last thought, for the in-game bot commands (".ultimus why", ".questteam why"). Ultimus and
/// each TCO7 member keep their own (UltimusSeat); the Quest Team has one. Written by the bots every half second.
/// </summary>
internal static class UltimusNow {

    private static readonly SeatLocal<(string Doing, string Thought, string Quest)> s_now = new(() => ("", "", ""));

    internal static void Note(string doing, string thought, string quest) => s_now.Value = (doing ?? "", thought ?? "", quest ?? "");

    internal static (string Doing, string Thought, string Quest) Of(UltimusSeat seat) => s_now.Of(seat);

    /// <summary>The Quest Team's doing and quest.</summary>
    internal static (string Doing, string Quest) Team { get; set; } = ("", "");

}
