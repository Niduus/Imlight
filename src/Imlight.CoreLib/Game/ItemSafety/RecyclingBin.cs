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
 *
 * ========================================================================
 * RECYCLING BIN
 * ========================================================================
 *
 * PURPOSE:
 * Niduus, 2026-11-04: "begin implementing recycle bin feature". The game's own Recycling Bin: items a wizard threw
 * away (backpack trash, a released pet, the bank window's trash) wait there for [RecyclingBin] Days (30 like the game)
 * and come back for [RecyclingBin] Cost gold (1000 like the game). The bin is the item history: every thrown-away item
 * already has an ItemBackup record with a full copy, so the bin is those records that were not given back yet.
 *
 * HOW THE GAME TALKS (read from the 1.610 client, WizardGraphicalClient.exe, RecyclingBinComponent):
 * - Opening the bin sends MSG_REQUESTRECYCLINGBIN with ItemGID 0, after the game empties its own list.
 * - The server answers with one MSG_REQUESTRECYCLINGBIN per item, SerializedItem = the item at mask 0x18 (the same
 *   blob as the inventory add). Status 1 empties the game's list first, Status 0 opens the window, any other value
 *   below 1000 only adds. A Status 0 with no item opens the window without adding one.
 * - Reclaim sends MSG_REQUESTRECYCLINGBIN with the item's ItemGID. Status 1000 = "not enough gold"
 *   (GaryGUI_00000037), 1001 = "not enough backpack space" (GaryGUI_00000038), 1002 + SerializedItem = "$ITEM$ has
 *   been reclaimed" (GaryGUI_00000039), and the game takes the item with that global id out of its list.
 * - The game's confirm box always says 1000 gold: the client writes the number itself.
 *
 * USAGE EXAMPLE:
 * var items = RecyclingBin.ItemsFor(wizard.CharId);
 *
 * NOTE:
 * Every setting is read when it is used, so Ahziael's changes apply without a restart.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Databases;

namespace Imlight.CoreLib.Game.ItemSafety;

internal static class RecyclingBin {

    // The reasons InventoryService and BankCollection write for an item the player threw away.
    internal const string THROWN_AWAY = "thrown away";

    internal const uint STATUS_OPEN = 0;
    internal const uint STATUS_FIRST = 1;
    internal const uint STATUS_ADD = 2;
    internal const uint STATUS_NOT_ENOUGH_GOLD = 1000;
    internal const uint STATUS_NO_ROOM = 1001;
    internal const uint STATUS_RECLAIMED = 1002;

    /// <summary>[RecyclingBin] Enabled (live): the game's Recycling Bin window lists thrown-away items.</summary>
    internal static bool Enabled => ConfigurationManager.GetValue("RecyclingBin.Enabled", true);

    /// <summary>[RecyclingBin] Days (live): how long a thrown-away item waits in the bin. 0 keeps it forever.</summary>
    internal static int Days => Math.Clamp(ConfigurationManager.GetValue("RecyclingBin.Days", 30), 0, 3650);

    /// <summary>[RecyclingBin] Cost (live): the gold one reclaim costs.</summary>
    internal static int Cost => Math.Clamp(ConfigurationManager.GetValue("RecyclingBin.Cost", 1000), 0, int.MaxValue);

    /// <summary>[RecyclingBin] MaxItems (live): the most items the window lists, newest first.</summary>
    internal static int MaxItems => Math.Clamp(ConfigurationManager.GetValue("RecyclingBin.MaxItems", 100), 1, 1000);

    /// <summary>
    /// The items in a wizard's bin, newest first: thrown away within Days, with a copy, and not given back yet.
    /// Only the newest record of an item counts.
    /// </summary>
    internal static List<ItemBackup> ItemsFor(ulong charId) {
        var days = Days;
        var since = days == 0 ? DateTime.MinValue : DateTime.UtcNow.AddDays(-days);
        try {
            using var session = PlayerDatabase.Instance.Store.OpenSession();
            var records = session.Query<ItemBackup>()
                .Customize(query => query.WaitForNonStaleResults(TimeSpan.FromSeconds(5)))
                .Where(backup => backup.CharId == charId && backup.Kind == ItemRecordKind.Removed && !backup.Restored
                    && backup.Time >= since)
                .OrderByDescending(backup => backup.Time)
                .Take(1024)
                .ToList();

            return [.. records
                .Where(backup => backup.Item is not null && backup.ItemId != 0 && IsThrownAway(backup))
                .DistinctBy(backup => backup.ItemId)
                .Take(MaxItems)];
        }
        catch (Exception ex) {
            Logger.Error("Recycling Bin: could not read the bin of {0}: {1}", Logger.Args(charId, ex.Message));

            return [];
        }
    }

    /// <summary>
    /// The bin record of one item of a wizard, or null when the item is not in the bin.
    /// </summary>
    internal static ItemBackup Find(ulong charId, ulong itemId)
        => ItemsFor(charId).FirstOrDefault(backup => backup.ItemId == itemId);

    // "thrown away", "thrown away while worn", "thrown away from the bank window".
    internal static bool IsThrownAway(ItemBackup backup)
        => backup.Reason?.StartsWith(THROWN_AWAY, StringComparison.OrdinalIgnoreCase) == true;

}
