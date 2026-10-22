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
 * PET MORPH ZONE
 * ========================================================================
 *
 * PURPOSE:
 * The pet morphing "Phantom Zone" (ThePhantomZoneWorld/PetGameMorph) that the hatchery sigils send
 * players to, where the game's own hatch window lives (Niduus, 2026-10-20). Remembers, per character,
 * which sigil sent them, who they hatch with and where they go back to, because the player arrives in
 * a new session after the zone transfer.
 *
 * USAGE EXAMPLE:
 * PetMorphZone.Begin(new PetMorphVisit(charId, sigilGid, pad, zone, loc, ownerId, 0, null));
 * if (PetMorphZone.TryGet(charId, out var visit)) { ... }
 * PetMorphZone.End(charId);
 *
 * NOTE:
 * The zone's own data (decoded from Niduus's install): ZONETYPE_DYNAMIC, locations Start and Home,
 * one object (PetGameMorph, template 174081) and the adjectives ImmediateDisconnect, Gauntlet, NoChat,
 * HomeToFallback, Minigame, NoInventoryAccess. Each visit's character is assigned to the instance of
 * the visit's owner (ZoneInstanceOwners), so the two players of a duo sigil share one instance.
 *
 * TODO:
 * - Whether live kept a pair in one instance or gave each player their own is not known.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/20/2026
 */

using System;
using System.Collections.Concurrent;
using System.Linq;
using Imlight.Common;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Pet;

/// <summary>
/// One player's trip from a hatchery sigil to the pet morph zone.
/// </summary>
/// <param name="CharId">The player.</param>
/// <param name="SigilGid">The sigil pad's global id.</param>
/// <param name="PadName">The pad's name, for the log.</param>
/// <param name="ReturnZone">The zone of the pad, where the player goes back to.</param>
/// <param name="ReturnLocation">Where the player stood when the trip started (else the pad), "X,Y,Z,heading".</param>
/// <param name="InstanceOwnerCharId">The owner of the shared instance: the player who pressed the sigil.</param>
/// <param name="PartnerCharId">The other player of a duo sigil, or 0 when hatching alone.</param>
/// <param name="PartnerName">The other player's name.</param>
internal sealed record PetMorphVisit(
    ulong CharId,
    ulong SigilGid,
    string PadName,
    string ReturnZone,
    string ReturnLocation,
    ulong InstanceOwnerCharId,
    ulong PartnerCharId,
    string PartnerName) {

    public DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;

    public bool IsPair => PartnerCharId != 0;

}

internal static class PetMorphZone {

    internal const string ZONE = "ThePhantomZoneWorld/PetGameMorph";
    internal const string LOCATION = "Start";
    internal const string HATCHERY_ZONE = "WizardCity/Interiors/WC_Hatchery";
    internal const string GAME = "PetGameMorph";

    private static readonly ConcurrentDictionary<ulong, PetMorphVisit> s_visits = new();

    /// <summary>
    /// [Hatching] MorphZoneMinutes: how long a player may stay in the pet morph zone before being sent back.
    /// </summary>
    internal static TimeSpan Timeout
        => TimeSpan.FromMinutes(Math.Max(1, ConfigurationManager.GetValue("Hatching.MorphZoneMinutes", 5)));

    /// <summary>
    /// Whether the server can send players to the pet morph zone: the zone has to be a known zone (AccessPass.xml)
    /// and its archive has to be there.
    /// </summary>
    /// <param name="why">Why not, when this returns <c>false</c>.</param>
    internal static bool IsAvailable(out string why) {
        // Once found, the zone stays there. Two players pressing a duo pad together checked the archive at the same
        // moment and one check failed (Niduus, 2026-10-20), so it is checked one at a time and only until it works.
        if (s_available) {
            why = null;

            return true;
        }

        lock (s_availableLock) {
            return CheckAvailable(out why);
        }
    }

    private static readonly object s_availableLock = new();
    private static volatile bool s_available;

    private static bool CheckAvailable(out string why) {
        if (s_available) {
            why = null;

            return true;
        }

        try {
            if (!AccessPassManager.Zones.Any(zone => string.Equals(zone, ZONE, StringComparison.OrdinalIgnoreCase))) {
                why = $"{ZONE} is not in the game's zone list (AccessPass.xml)";

                return false;
            }

            if (!ResourceManager.TryLoadArchive(ZONE, out _)) {
                why = $"the archive of {ZONE} could not be opened";

                return false;
            }
        }
        catch (Exception ex) {
            why = $"checking {ZONE} failed: {ex.Message}";

            return false;
        }

        s_available = true;
        why = null;

        return true;
    }

    internal static bool IsMorphZone(string zoneName)
        => string.Equals(zoneName, ZONE, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Records the trip and puts the character in the owner's instance of the zone when they attach.
    /// </summary>
    internal static void Begin(PetMorphVisit visit) {
        s_visits[visit.CharId] = visit;
        ZoneInstanceOwners.Assign(visit.CharId, ZONE, visit.InstanceOwnerCharId);
    }

    internal static bool TryGet(ulong charId, out PetMorphVisit visit)
        => s_visits.TryGetValue(charId, out visit);

    /// <summary>
    /// Forgets the trip; the character attaches to their own instance of the zone again.
    /// </summary>
    internal static void End(ulong charId) {
        s_visits.TryRemove(charId, out _);
        ZoneInstanceOwners.Clear(charId, ZONE);
    }

}
