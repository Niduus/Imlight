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

using System.Diagnostics;

namespace Imlight.CoreLib.Shared.Resources;

/// <summary>
/// The mini servers (Ultimus, Quest Team, Bot Players, the Bazaar bot) are this same program started again, so they
/// got the game server's memory clean-up too: one heap per core (12 on Niduus's PC) sized for zones loading on every
/// thread. They are small, mostly-waiting programs, so they use the ordinary one-heap clean-up, which keeps far less
/// memory set aside (Niduus, 2026-11-15: "this is too much memory"). Nothing they do changes.
/// </summary>
internal static class HelperProcessMemory {

    internal static void UseSmallMemory(ProcessStartInfo start) {
        start.Environment["DOTNET_gcServer"] = "0";
        start.Environment["DOTNET_GCConserveMemory"] = "5";
    }

}
