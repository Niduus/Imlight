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
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// Which areas the bots' books read from the game's own files (Niduus, 2026-11-22, "Creature-area book + wider": the books
/// covered the main worlds only). The main worlds and Arcanum, as before, plus the side worlds a quest can send a wizard to
/// (Grizzleheim and Wintertusk, Wysteria, Aquila, Selenopolis); never a house, an event or a gauntlet. The NPC location book,
/// the zone transfer book, the Quest Atlas and the creature-area book all read this one list.
/// </summary>
internal static class BotBookAreas {

    /// <summary>The side worlds' zone folders (the quest codes GH, WT, PA, WY, AQ, SL).</summary>
    internal static readonly HashSet<string> SideWorlds = new(StringComparer.OrdinalIgnoreCase) {
        "Grizzleheim", "Wintertusk", "Wysteria", "Aquila", "Selenopolis",
    };

    // Bumped when the list of areas changes, so a book written from the old list is built again at once instead of being
    // read for up to 3 more days.
    private const string VERSION = "2";

    /// <summary>Whether a book reads this area: a main-world area (OverlayQuestRoute.OnStory) or a side world's.</summary>
    internal static bool Holds(string zone) {
        if (string.IsNullOrWhiteSpace(zone)) {
            return false;
        }

        var z = zone.Trim();
        if (OverlayQuestRoute.OnStory(z)) {
            return true;
        }

        return IsSideWorld(z) && !OverlayQuestRoute.IsHouse(z) && !OverlayQuestRoute.EventArea(z);
    }

    /// <summary>Whether an area (or a world folder) is in a side world.</summary>
    internal static bool IsSideWorld(string zoneOrFolder) => SideWorlds.Contains((zoneOrFolder ?? "").Trim().Split('/')[0]);

    /// <summary>Every area the books read, from the game's zone list.</summary>
    internal static List<string> All()
        => [.. SpiralDB.GetAllZoneData().Select(z => z?.ZoneName).Where(Holds).Select(z => z.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Whether a book file is under 3 days old and was written from this list of areas.</summary>
    internal static bool Fresh(string bookPath) {
        try {
            return File.Exists(bookPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(bookPath) < TimeSpan.FromDays(3)
                   && File.Exists(bookPath + ".areas") && File.ReadAllText(bookPath + ".areas").Trim() == VERSION;
        }
        catch (Exception) {
            return false;
        }
    }

    /// <summary>Notes beside a book file that it was written from this list of areas.</summary>
    internal static void Mark(string bookPath) {
        try {
            File.WriteAllText(bookPath + ".areas", VERSION);
        }
        catch (Exception) {
            // Without the note the book is built again at the next start: slower, never wrong.
        }
    }
}
