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
 * HOUSING SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * The live state of one house: its placed items under one lock, persisted on every change, shared
 * by every player who is in the house.
 * 
 * USAGE EXAMPLE:
 * var change = state.TryAdd(item);
 * if (change.Done) {
 *     // patch GID change.GidId, version change.Revision
 * }
 * 
 * NOTE:
 * - Every change is written to the database before it returns. A failed write undoes the change
 *   in memory, so the stored house and the live house never differ.
 * - A change that also moves an item to or from a backpack passes its own save, which writes both in one transaction.
 * - Stored items are never changed in place; an update replaces the item. A change names the item by its id,
 *   which survives a renumbering; the result carries the slot and the patch GID of the moment.
 * - The slot table numbers the items the way the client numbers its blob entries: GID id = base + slot.
 *   A removed item leaves a null slot, so the numbers in later patches stay valid. The table is built, with a
 *   new base and a revision no client has seen, when the state is created, and again when it is full. Clients
 *   that still use the old base ask for the blob when a patch does not fit.
 * - Use HouseRegistry.TryEnter and HouseRegistry.Release to count viewers.
 * 
 * TODO:
 * 
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/23/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Housing;

/// <summary>
/// How a change to a house ended.
/// </summary>
internal enum HouseEdit {
    Done,
    LimitReached,
    NotFound,
    SaveFailed,
    NeedsResync,
}

/// <summary>
/// The result of one change, with what its patch needs.
/// </summary>
/// <param name="Result">How the change ended; the other values are only set for <see cref="HouseEdit.Done"/>.</param>
/// <param name="Slot">The slot of the changed item.</param>
/// <param name="GidId">The patch's <c>GIDID</c>: the base plus the slot.</param>
/// <param name="Revision">The revision the change produced: the patch's version.</param>
/// <param name="Item">The item as it is stored after the change; for a removal, as it was before.</param>
internal readonly record struct HouseChange(HouseEdit Result, int Slot = -1, uint GidId = 0, uint Revision = 0, PlacedItem Item = null) {

    internal bool Done => Result == HouseEdit.Done;

}

/// <summary>
/// A consistent copy of a house for building a blob.
/// </summary>
/// <param name="Revision">The blob version.</param>
/// <param name="GidBase">The base the blob entries are numbered from.</param>
/// <param name="LiveCount">The number of placed items, not counting removed slots.</param>
/// <param name="Slots">The items in slot order; a removed slot is <c>null</c>.</param>
internal readonly record struct HouseSnapshot(uint Revision, uint GidBase, int LiveCount, IReadOnlyList<PlacedItem> Slots);

/// <summary>
/// The live, persisted state of one house.
/// </summary>
internal sealed class HouseState {

    /// <summary>
    /// The most blob entries one blob holds: a string field is at most 65,535 bytes, the blob header takes
    /// 47 and every entry 21.
    /// </summary>
    internal const int MaxSlots = HousingGids.MaxSlots;

    private readonly Lock _lock = new();
    private readonly House _house;
    private readonly HouseTemplateInfo _template;
    private List<PlacedItem> _slots;
    private uint _gidBase;
    private int _liveCount;
    private int _viewers;
    private bool _retired;

    private HouseState(House house, HouseTemplateInfo template, List<PlacedItem> slots) {
        _house = house;
        _template = template;
        _slots = slots;
        _liveCount = slots.Count;
        _gidBase = NewGidBase();
    }

    internal ulong LotGid => _house.LotGid;

    internal ulong OwnerCharId => _house.OwnerCharId;

    internal string ZoneName => _template.LotZone;

    internal HouseTemplateInfo Template => _template;

    internal uint GidBase {
        get {
            lock (_lock) {
                return _gidBase;
            }
        }
    }

    internal uint Revision {
        get {
            lock (_lock) {
                return _house.Revision;
            }
        }
    }

    internal int LiveCount {
        get {
            lock (_lock) {
                return _liveCount;
            }
        }
    }

    internal int SlotCount {
        get {
            lock (_lock) {
                return _slots.Count;
            }
        }
    }

    /// <summary>
    /// The number of items the house may hold: the client's own formula, the template's limit plus the bonus slots.
    /// </summary>
    internal int ItemLimit {
        get {
            lock (_lock) {
                return _template.ItemLimit + _house.BonusSlots;
            }
        }
    }

    internal int CastleBlockLimit => _template.CastleBlockLimit;

    internal bool IsRetired {
        get {
            lock (_lock) {
                return _retired;
            }
        }
    }

    /// <summary>
    /// Creates the live state of a stored house. The items are numbered by their ids, a new base is drawn,
    /// and the revision moves once and is stored, so a client with an older cache fetches the blob again.
    /// </summary>
    /// <param name="house">The stored house; the state owns it from now on.</param>
    /// <param name="template">The house's template.</param>
    /// <param name="state">The live state when this returns <c>true</c>.</param>
    /// <returns><c>false</c> when the new revision could not be stored.</returns>
    internal static bool TryCreate(House house, HouseTemplateInfo template, out HouseState state) {
        state = null;
        var previousItems = house.Items;
        var previousRevision = house.Revision;
        var ordered = previousItems.OrderBy(item => item.Id).ToList();
        house.Items = [.. ordered];
        house.Revision = previousRevision + 1;
        if (!HouseCollection.Save(house)) {
            house.Items = previousItems;
            house.Revision = previousRevision;

            return false;
        }

        state = new HouseState(house, template, ordered);

        return true;
    }

    /// <summary>
    /// Draws the base the client numbers a house's blob entries from.
    /// </summary>
    /// <returns>A new GID base.</returns>
    internal static uint NewGidBase()
        => HousingGids.NewBase();

    /// <summary>
    /// Copies the house for building a blob.
    /// </summary>
    /// <returns>The revision, base, live count and slots, taken together.</returns>
    internal HouseSnapshot Snapshot() {
        lock (_lock) {
            return new HouseSnapshot(_house.Revision, _gidBase, _liveCount, [.. _slots]);
        }
    }

    /// <summary>
    /// Gets the <c>GIDID</c> of a slot: the base plus the slot, wrapping like the client's 32-bit sum.
    /// </summary>
    /// <param name="slot">The slot, counting removed slots.</param>
    /// <returns>The <c>GIDID</c> of the slot's patches.</returns>
    internal uint GidIdOf(int slot) {
        lock (_lock) {
            return unchecked(_gidBase + (uint) slot);
        }
    }

    /// <summary>
    /// Finds the slot a housing object GID names. The client builds the GID as
    /// <c>(u16 &lt;&lt; 48) | (9 &lt;&lt; 40) | GIDID</c>.
    /// </summary>
    /// <param name="gid">The GID the client sent.</param>
    /// <param name="item">The item in the slot; <c>null</c> when the slot was removed.</param>
    /// <param name="slot">The slot when this returns <c>true</c>.</param>
    /// <returns><c>true</c> when the GID is a housing object GID inside this house's numbering.</returns>
    internal bool TryFindByGid(ulong gid, out PlacedItem item, out int slot) {
        item = null;
        lock (_lock) {
            if (!HousingGids.TryGetSlot(gid, _gidBase, _slots.Count, out slot)) {
                return false;
            }

            item = _slots[slot];

            return true;
        }
    }

    /// <summary>
    /// Finds the placed item that was placed from a backpack item. Some windows, such as the teleporter's targeting,
    /// name an object by that item's global id instead of its housing object GID.
    /// </summary>
    /// <param name="itemGid">The global id of the backpack item.</param>
    /// <param name="item">The placed item when this returns <c>true</c>.</param>
    /// <param name="slot">Its slot when this returns <c>true</c>.</param>
    /// <returns><c>true</c> when an item of this house was placed from that backpack item.</returns>
    internal bool TryFindByItemGid(ulong itemGid, out PlacedItem item, out int slot) {
        item = null;
        slot = -1;
        if (itemGid == 0) {
            return false;
        }

        lock (_lock) {
            slot = _slots.FindIndex(placed => placed?.ItemGid == itemGid);
            if (slot < 0) {
                return false;
            }

            item = _slots[slot];

            return true;
        }
    }

    /// <summary>
    /// Counts the placed items of a template.
    /// </summary>
    /// <param name="templateId">The item template.</param>
    /// <returns>The number of placed copies.</returns>
    internal int CountTemplate(uint templateId) {
        lock (_lock) {
            return _slots.Count(item => item?.TemplateId == templateId);
        }
    }

    /// <summary>
    /// Places an item in a new slot at the end of the table. When the table is full of removed slots it is
    /// numbered again instead and nothing is added.
    /// </summary>
    /// <param name="item">The item to place; its id is replaced by the next free one.</param>
    /// <param name="save">Writes the house; <c>null</c> writes the house alone.</param>
    /// <returns>
    /// <see cref="HouseEdit.LimitReached"/> at the item limit, <see cref="HouseEdit.NeedsResync"/> when the table was
    /// numbered again (every viewer needs the blob), <see cref="HouseEdit.SaveFailed"/> when the write failed.
    /// </returns>
    internal HouseChange TryAdd(PlacedItem item, Func<House, bool> save = null) {
        lock (_lock) {
            if (_liveCount >= _template.ItemLimit + _house.BonusSlots) {
                return new HouseChange(HouseEdit.LimitReached);
            }

            if (_slots.Count >= MaxSlots) {
                if (_liveCount >= _slots.Count) {
                    return new HouseChange(HouseEdit.LimitReached);
                }

                return new HouseChange(Renumber() ? HouseEdit.NeedsResync : HouseEdit.SaveFailed);
            }

            var revision = _house.Revision;
            var added = item with { Id = _house.NextItemId };
            _house.Items.Add(added);
            _house.NextItemId = added.Id + 1;
            _house.Revision = revision + 1;
            if (!Persist(save)) {
                _house.Items.RemoveAt(_house.Items.Count - 1);
                _house.NextItemId = added.Id;
                _house.Revision = revision;

                return new HouseChange(HouseEdit.SaveFailed);
            }

            _slots.Add(added);
            _liveCount++;
            var slot = _slots.Count - 1;

            return new HouseChange(HouseEdit.Done, slot, GidIdOf(slot), _house.Revision, added);
        }
    }

    /// <summary>
    /// Removes a placed item. Its slot stays in the table as a null slot.
    /// </summary>
    /// <param name="itemId">The id of the item to remove.</param>
    /// <param name="save">Writes the house; <c>null</c> writes the house alone.</param>
    /// <returns><see cref="HouseEdit.NotFound"/> when the house has no such item, <see cref="HouseEdit.SaveFailed"/> when the write failed.</returns>
    internal HouseChange TryRemove(uint itemId, Func<House, bool> save = null) {
        lock (_lock) {
            var slot = _slots.FindIndex(candidate => candidate?.Id == itemId);
            if (slot < 0) {
                return new HouseChange(HouseEdit.NotFound);
            }

            var previous = _slots[slot];
            var index = _house.Items.FindIndex(candidate => candidate.Id == itemId);
            var revision = _house.Revision;
            _house.Items.RemoveAt(index);
            _house.Revision = revision + 1;
            if (!Persist(save)) {
                _house.Items.Insert(index, previous);
                _house.Revision = revision;

                return new HouseChange(HouseEdit.SaveFailed);
            }

            _slots[slot] = null;
            _liveCount--;

            return new HouseChange(HouseEdit.Done, slot, GidIdOf(slot), _house.Revision, previous);
        }
    }

    /// <summary>
    /// Removes several placed items in one write. The revision moves by the number of items, so a
    /// client can be sent one delete patch per item with consecutive versions.
    /// </summary>
    /// <param name="itemIds">The ids of the items to remove; none may repeat.</param>
    /// <param name="removed">One change per item in the order of <paramref name="itemIds"/>: its slot, GID id, the version of its patch and the item.</param>
    /// <param name="save">Writes the house; <c>null</c> writes the house alone.</param>
    /// <returns><see cref="HouseEdit.NotFound"/> when an item is not in the house, <see cref="HouseEdit.SaveFailed"/> when the write failed; nothing changes then.</returns>
    internal HouseEdit TryRemoveMany(IReadOnlyCollection<uint> itemIds, out IReadOnlyList<HouseChange> removed, Func<House, bool> save = null) {
        removed = [];
        lock (_lock) {
            if (itemIds.Count == 0 || itemIds.Distinct().Count() != itemIds.Count) {
                return HouseEdit.NotFound;
            }

            var slots = itemIds.Select(id => _slots.FindIndex(candidate => candidate?.Id == id)).ToList();
            if (slots.Any(slot => slot < 0)) {
                return HouseEdit.NotFound;
            }

            var previousItems = _house.Items;
            var revision = _house.Revision;
            var removedIds = itemIds.ToHashSet();
            _house.Items = [.. previousItems.Where(candidate => !removedIds.Contains(candidate.Id))];
            _house.Revision = revision + (uint) itemIds.Count;
            if (!Persist(save)) {
                _house.Items = previousItems;
                _house.Revision = revision;

                return HouseEdit.SaveFailed;
            }

            var changes = new List<HouseChange>(slots.Count);
            for (var i = 0; i < slots.Count; i++) {
                var slot = slots[i];
                changes.Add(new HouseChange(HouseEdit.Done, slot, GidIdOf(slot), revision + (uint) i + 1, _slots[slot]));
                _slots[slot] = null;
            }

            _liveCount -= slots.Count;
            removed = changes;

            return HouseEdit.Done;
        }
    }

    /// <summary>
    /// Replaces a placed item with an updated copy of itself.
    /// </summary>
    /// <param name="itemId">The id of the item to update.</param>
    /// <param name="update">Returns the replacement for the stored item; it must keep the id and the template.</param>
    /// <param name="save">Writes the house; <c>null</c> writes the house alone.</param>
    /// <returns>
    /// <see cref="HouseEdit.NotFound"/> when the house has no such item or <paramref name="update"/> returns <c>null</c>
    /// or an item of another id or template, <see cref="HouseEdit.SaveFailed"/> when the write failed.
    /// </returns>
    internal HouseChange TryUpdate(uint itemId, Func<PlacedItem, PlacedItem> update, Func<House, bool> save = null) {
        lock (_lock) {
            var slot = _slots.FindIndex(candidate => candidate?.Id == itemId);
            if (slot < 0) {
                return new HouseChange(HouseEdit.NotFound);
            }

            var previous = _slots[slot];
            var replacement = update(previous);
            if (replacement is null || ReferenceEquals(replacement, previous)
                || replacement.Id != previous.Id || replacement.TemplateId != previous.TemplateId) {
                return new HouseChange(HouseEdit.NotFound);
            }

            var index = _house.Items.FindIndex(candidate => candidate.Id == itemId);
            var revision = _house.Revision;
            _house.Items[index] = replacement;
            _house.Revision = revision + 1;
            if (!Persist(save)) {
                _house.Items[index] = previous;
                _house.Revision = revision;

                return new HouseChange(HouseEdit.SaveFailed);
            }

            _slots[slot] = replacement;

            return new HouseChange(HouseEdit.Done, slot, GidIdOf(slot), _house.Revision, replacement);
        }
    }

    /// <summary>
    /// Gives a placed item another template where it stands, such as a used up housing gauntlet becoming its trophy
    /// (Game/Housing/HouseGauntlets). Unlike <see cref="TryUpdate"/> the template changes, so the client needs the blob.
    /// </summary>
    /// <param name="itemId">The id of the placed item.</param>
    /// <param name="fromTemplateId">The template the item must still have.</param>
    /// <param name="toTemplateId">The template it takes.</param>
    /// <returns>
    /// <see cref="HouseEdit.NotFound"/> when the house has no such item or it has another template,
    /// <see cref="HouseEdit.SaveFailed"/> when the write failed.
    /// </returns>
    internal HouseChange TrySwapTemplate(uint itemId, uint fromTemplateId, uint toTemplateId) {
        lock (_lock) {
            var slot = _slots.FindIndex(candidate => candidate?.Id == itemId);
            if (slot < 0 || _slots[slot].TemplateId != fromTemplateId || toTemplateId == 0) {
                return new HouseChange(HouseEdit.NotFound);
            }

            var previous = _slots[slot];
            var replacement = previous with { TemplateId = toTemplateId };
            var index = _house.Items.FindIndex(candidate => candidate.Id == itemId);
            var revision = _house.Revision;
            _house.Items[index] = replacement;
            _house.Revision = revision + 1;
            if (!Persist(null)) {
                _house.Items[index] = previous;
                _house.Revision = revision;

                return new HouseChange(HouseEdit.SaveFailed);
            }

            _slots[slot] = replacement;

            return new HouseChange(HouseEdit.Done, slot, GidIdOf(slot), _house.Revision, replacement);
        }
    }

    /// <summary>
    /// Counts a player into the house.
    /// </summary>
    /// <returns><c>false</c> when the state was retired by its last viewer leaving; get the house from the registry again.</returns>
    internal bool Enter() {
        lock (_lock) {
            if (_retired) {
                return false;
            }

            _viewers++;

            return true;
        }
    }

    /// <summary>
    /// Counts a player out of the house. The state is retired when the last viewer leaves.
    /// </summary>
    /// <returns><c>true</c> when this was the last viewer and the state is now retired.</returns>
    internal bool Leave() {
        lock (_lock) {
            if (_viewers > 0) {
                _viewers--;
            }

            if (_viewers > 0 || _retired) {
                return false;
            }

            _retired = true;

            return true;
        }
    }

    private bool Renumber() {
        var previousSlots = _slots;
        var previousBase = _gidBase;
        var previousRevision = _house.Revision;
        _slots = [.. _house.Items];
        _gidBase = NewGidBase();
        _house.Revision = previousRevision + 1;
        if (HouseCollection.Save(_house)) {
            return true;
        }

        _slots = previousSlots;
        _gidBase = previousBase;
        _house.Revision = previousRevision;

        return false;
    }

    private bool Persist(Func<House, bool> save) {
        try {
            return (save ?? HouseCollection.Save)(_house);
        } catch (Exception ex) {
            Logger.Error("Failed to write house {LotGid}: {Reason}", Logger.Args(_house.LotGid, ex.Message));

            return false;
        }
    }

}
