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
 *
 * ========================================================================
 * PET GAME ZONES
 * ========================================================================
 *
 * PURPOSE:
 * The rooms the pet games are played in (ThePhantomZoneWorld/PetGame<Name>), and, per character, the
 * game they went there to play, because the player arrives in a new session after the zone transfer
 * (Niduus, 2026-10-24: pet games you actually play instead of skipping them).
 *
 * USAGE EXAMPLE:
 * if (PetGameZones.TryGetZone("PetGameDance", track, out var zone)) { PetGameZones.Begin(visit); }
 *
 * NOTE:
 * The Dance room is a black box like the hatch room (one object, PetGameDance, and the window
 * PetGameSplash.gui); the game draws the dance in its own window. The obstacle course has five rooms,
 * one per level (Course01..05), with the course, its obstacles and the PetGameObstacleCourse object.
 * Read from Niduus's game files and the game's code (2026-10-24).
 *
 * TODO:
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Imlight.Common;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Pet;

/// <summary>
/// One player's trip from a pet game kiosk to a pet game room.
/// </summary>
/// <param name="CharId">The player.</param>
/// <param name="Game">The game's name in PetGames.xml (PetGameDance, ...).</param>
/// <param name="Track">The track (level) picked at the kiosk.</param>
/// <param name="Zone">The room.</param>
/// <param name="ReturnZone">Where the player goes back to: the kiosk's zone.</param>
/// <param name="ReturnLocation">Where the player stood at the kiosk, "X,Y,Z,heading".</param>
internal sealed record PetGameVisit(
    ulong CharId,
    string Game,
    int Track,
    string Zone,
    string ReturnZone,
    string ReturnLocation) {

    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;

}

internal static class PetGameZones {

    internal const string LOCATION = "Start";
    internal const string PET_PARK_ZONE = "WizardCity/WC_Streets/Interiors/WC_PET_Park";

    // The games the server can run, and their rooms; {0} is the course number, the track (level) + 1 (the kiosk
    // checks Course01..Course05 are installed).
    private static readonly Dictionary<string, string> s_zones = new(StringComparer.OrdinalIgnoreCase) {
        ["PetGameDance"] = "ThePhantomZoneWorld/PetGameDance",
        ["PetGameObstacleCourse"] = "ThePhantomZoneWorld/PetGameObstacleCourse/Course{0:00}",
    };

    private const int COURSES = 5;

    private static readonly ConcurrentDictionary<ulong, PetGameVisit> s_visits = new();
    private static readonly ConcurrentDictionary<string, bool> s_available = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// [Pets] PlayGames: whether the Play button of the pet game kiosks starts the game (Skip works either way).
    /// </summary>
    internal static bool Enabled => ConfigurationManager.GetValue("Pets.PlayGames", true);

    /// <summary>
    /// [Pets] PetGameMinutes: how long a player may stay in a pet game room before being sent back.
    /// </summary>
    internal static TimeSpan Timeout
        => TimeSpan.FromMinutes(Math.Max(2, ConfigurationManager.GetValue("Pets.PetGameMinutes", 10)));

    internal static IEnumerable<string> PlayableGames => s_zones.Keys;

    /// <summary>
    /// The room of a game the server can run, if the game's files have it.
    /// </summary>
    internal static bool TryGetZone(string game, int track, out string zone, out string why) {
        zone = null;
        if (string.IsNullOrEmpty(game) || !s_zones.TryGetValue(game, out var pattern)) {
            why = $"{game} is not one of the pet games the server can run yet";

            return false;
        }

        var name = string.Format(pattern, Math.Clamp(track, 0, COURSES - 1) + 1);

        if (!IsAvailable(name, out why)) {
            return false;
        }

        zone = name;

        return true;
    }

    private static bool IsAvailable(string zone, out string why) {
        if (s_available.ContainsKey(zone)) {
            why = null;

            return true;
        }

        try {
            if (!AccessPassManager.Zones.Any(z => string.Equals(z, zone, StringComparison.OrdinalIgnoreCase))) {
                why = $"{zone} is not in the game's zone list (AccessPass.xml)";

                return false;
            }

            if (!ResourceManager.TryLoadArchive(zone, out _)) {
                why = $"the archive of {zone} could not be opened";

                return false;
            }
        }
        catch (Exception ex) {
            why = $"checking {zone} failed: {ex.Message}";

            return false;
        }

        s_available[zone] = true;
        why = null;

        return true;
    }

    internal static bool IsPetGameZone(string zoneName)
        => zoneName is not null && s_zones.Values.Any(pattern => Enumerable.Range(1, COURSES)
            .Any(n => string.Equals(string.Format(pattern, n), zoneName, StringComparison.OrdinalIgnoreCase)));

    internal static void Begin(PetGameVisit visit)
        => s_visits[visit.CharId] = visit;

    internal static bool TryGet(ulong charId, out PetGameVisit visit)
        => s_visits.TryGetValue(charId, out visit);

    internal static void End(ulong charId)
        => s_visits.TryRemove(charId, out _);

}
