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
 * ITEM AUDIT
 * ========================================================================
 *
 * PURPOSE:
 * The item check at login (Niduus, 2026-10-28: items disappearing). Each time a wizard logs in or changes area the
 * server notes which items it holds (backpack, worn, bank) in ItemSnapshots/<char id>. An item that was there last
 * time, is gone now, has no item history record and is not held by someone else (shared bank, a trade) disappeared
 * without a reason: it gets a Missing record, with the item itself when it is still stored, so it can be given back.
 *
 * NOTE:
 * The check runs in the background after the wizard's object is built, so it never holds up a login.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Databases;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.ItemSafety;

/// <summary>
/// The items a wizard held the last time it was checked.
/// </summary>
public sealed class ItemSnapshot {

    public string Id { get; set; }
    public ulong CharId { get; set; }
    public DateTime Time { get; set; }
    public List<ItemSnapshotEntry> Items { get; set; } = [];

}

public sealed class ItemSnapshotEntry {

    public ulong ItemId { get; set; }
    public ulong TemplateId { get; set; }

}

internal static class ItemAudit {

    private const string ID_PREFIX = "ItemSnapshots/";
    // Records written just before the last check still explain an item, whatever the clocks did.
    private static readonly TimeSpan s_slack = TimeSpan.FromMinutes(2);
    private static readonly ConcurrentDictionary<ulong, object> s_locks = new();

    /// <summary>
    /// Notes the wizard's items now and checks them against the last time in the background.
    /// </summary>
    internal static void Check(Wizard wizard) {
        List<ItemSnapshotEntry> held;
        try {
            held = [.. Held(wizard)
                .GroupBy(item => item.m_globalID.Full)
                .Select(group => new ItemSnapshotEntry { ItemId = group.Key, TemplateId = group.First().m_templateID.Full })];
        }
        catch (Exception ex) {
            Logger.Error("Item check: could not list the items of {0}: {1}", Logger.Args(wizard.CharId, ex.Message));

            return;
        }

        var charId = wizard.CharId;
        var now = DateTime.UtcNow;
        Task.Run(() => {
            lock (s_locks.GetOrAdd(charId, _ => new object())) {
                try {
                    Compare(charId, held, now);
                }
                catch (Exception ex) {
                    Logger.Error("Item check: could not check the items of {0}: {1}", Logger.Args(charId, ex.Message));
                }
            }
        });
    }

    private static IEnumerable<WizClientObjectItem> Held(Wizard wizard)
        => (wizard.InventoryBehavior?.Items ?? [])
            .Concat(wizard.EquipmentBehavior?.EquippedItems ?? [])
            .Concat(wizard.StorageBehavior?.Items ?? [])
            .Where(item => item is not null);

    private static void Compare(ulong charId, List<ItemSnapshotEntry> held, DateTime now) {
        using var session = PlayerDatabase.Instance.Store.OpenSession();
        var snapshot = session.Load<ItemSnapshot>(ID_PREFIX + charId);
        var heldIds = held.Select(entry => entry.ItemId).ToHashSet();
        if (snapshot is not null) {
            var gone = snapshot.Items.Where(entry => !heldIds.Contains(entry.ItemId)).ToList();
            if (gone.Count > 0) {
                ReportUnexplained(charId, gone, snapshot.Time - s_slack);
            }

            if (gone.Count == 0 && snapshot.Items.Count == held.Count) {
                return;
            }
        }
        else {
            snapshot = new ItemSnapshot { Id = ID_PREFIX + charId, CharId = charId };
            session.Store(snapshot, snapshot.Id);
        }

        snapshot.Time = now;
        snapshot.Items = held;
        session.SaveChanges();
    }

    private static void ReportUnexplained(ulong charId, List<ItemSnapshotEntry> gone, DateTime since) {
        var recorded = ItemHistory.RecordedSince(charId, since);
        foreach (var entry in gone.Where(entry => !recorded.Contains(entry.ItemId))) {
            var stored = WizardItemCollection.GetItemOfAnyOwner(entry.ItemId);
            if (stored is not null && stored.m_characterId.Full != charId) {
                // Another wizard or the shared bank holds it now.
                continue;
            }

            ItemHistory.Record(charId, entry.ItemId, entry.TemplateId, stored, ItemRecordKind.Missing,
                stored is null
                    ? "gone since the last login with no record of why; giving it back makes a new one"
                    : "gone from the backpack, bank and gear since the last login, but still stored; giving it back returns it");
        }
    }

}
