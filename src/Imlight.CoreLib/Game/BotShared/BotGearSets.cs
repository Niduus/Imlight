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

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Equipment;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// Gear sets for every job (Niduus, 2026-11-15: "Have him also make gear sets and lock gear he may need for Support, Blading,
/// Healing, anything in between. This way, he has a set for everything"). From the gear he owns (worn and in the backpack),
/// the best piece per slot for each job goes in a set of the game's Equipment Sets, saved as a player saves one, and every
/// piece in a set is locked with the backpack padlock so it is never sold. A piece no set uses any more is unlocked again,
/// so the backpack does not fill up with gear kept for nothing. Ultimus and the Quest Team both keep them.
/// </summary>
internal static class BotGearSets {

    /// <summary>A job and the set kept for it (the set's name is a pick of the game's name wheel).</summary>
    internal sealed record Role(string Name, BotStyle Style, uint SetName, string What);

    internal static readonly Role[] Roles = [
        new("Damage", BotStyle.Hitter, 1u << 8, "hitting as hard as he can"),
        new("Blading", BotStyle.Backup, 2u << 8, "blades, traps and second hits for the team"),
        new("Support", BotStyle.Tank, 3u << 8, "shields, resist and health to take the hits"),
        new("Healing", BotStyle.Healer, 4u << 8, "heals"),
        new("Questing", BotStyle.Solo, 5u << 8, "a bit of everything, for questing alone"),
    ];

    private static uint TemplateOf(WizClientObjectItem item) => (uint) Math.Min((ulong) item.m_templateID, uint.MaxValue);

    /// <summary>The set for a job from the gear the wizard owns: per slot the best piece it can wear, else what it wears.</summary>
    internal static List<GearSetItem> OutfitFor(Wizard wizard, BotStyle style) {
        var items = new List<GearSetItem>();
        if (wizard?.EquipmentBehavior is not { } equipment) {
            return items;
        }

        var school = BotGear.SchoolOf(wizard);
        var level = BotGear.LevelOf(wizard);
        var owned = (wizard.InventoryBehavior?.Items ?? []).Where(i => i is not null).ToList();
        foreach (var slot in GearSets.SavedSlots) {
            var worn = equipment.GetItemInSlot(slot);
            var pick = worn;
            if (BotGear.Slots.Contains(slot)) {
                var wornPiece = worn is null ? null : BotGear.Read(TemplateOf(worn));
                var wornScore = wornPiece is null ? double.MinValue : BotGear.Score(wornPiece, school, style);
                var best = owned.Select(i => (Item: i, Piece: BotGear.Read(TemplateOf(i))))
                    .Where(x => x.Piece is not null && x.Piece.Slot == slot.ToString() && BotGear.CanWear(x.Piece, school, level)
                                && !BotGear.Forbidden(x.Piece.Id) && !BotGear.PartlyRead(x.Piece.Id))
                    .Select(x => (x.Item, Score: BotGear.Score(x.Piece, school, style)))
                    .OrderByDescending(x => x.Score).FirstOrDefault();
                if (best.Item is not null && best.Score > wornScore + 0.5) {
                    pick = best.Item;
                }
            }

            if (pick is not null && pick.m_globalID.Full != 0) {
                items.Add(new GearSetItem { ItemId = pick.m_globalID.Full, SlotType = slot });
            }
        }

        return items;
    }

    /// <summary>
    /// Saves (or brings up to date) the set of this job, as the backpack's Equipment Sets window saves one; true when it
    /// changed. A full list of sets is left alone.
    /// </summary>
    internal static bool Save(Wizard wizard, Role role, List<GearSetItem> items) {
        if (wizard?.EquipmentBehavior is not { } behavior || items.Count == 0) {
            return false;
        }

        lock (behavior) {
            behavior.GearSets ??= [];
            var at = behavior.GearSets.FindIndex(s => s.Name == role.SetName);
            if (at >= 0 && Same(behavior.GearSets[at].Items, items)) {
                return false;
            }

            if (at < 0 && behavior.GearSets.Count >= GearSets.MaxSets) {
                return false;
            }

            if (at >= 0) {
                behavior.GearSets[at].Items = items;
            }
            else {
                behavior.GearSets.Add(new GearSet { Name = role.SetName, Items = items });
            }
        }

        WizardCollection.UpdateCharacterItems(wizard);
        return true;
    }

    private static bool Same(List<GearSetItem> a, List<GearSetItem> b)
        => a is not null && a.Count == b.Count && a.All(x => b.Any(y => y.ItemId == x.ItemId && y.SlotType == x.SlotType));

    /// <summary>Every item (global id) in one of the wizard's gear sets.</summary>
    internal static HashSet<ulong> InSets(Wizard wizard) {
        var behavior = wizard?.EquipmentBehavior;
        if (behavior?.GearSets is null) {
            return [];
        }

        lock (behavior) {
            return behavior.GearSets.SelectMany(s => s.Items ?? []).Select(i => i.ItemId).Where(id => id != 0).ToHashSet();
        }
    }

    /// <summary>
    /// The padlock clicks to make, one per item: lock each piece in a set that is not locked, and unlock gear in the
    /// backpack that is locked but in no set and not worn (kept for a set that no longer uses it).
    /// </summary>
    internal static List<(ulong Id, bool Lock, string Name)> LockClicks(Wizard wizard) {
        var clicks = new List<(ulong, bool, string)>();
        if (wizard?.InventoryBehavior?.Items is not { } items || wizard.EquipmentBehavior is not { } equipment) {
            return clicks;
        }

        var inSets = InSets(wizard);
        var worn = BotGear.Slots.Append(EquipmentSlotType.Mount).Append(EquipmentSlotType.Pet)
            .Select(s => equipment.GetItemInSlot(s)?.m_globalID.Full ?? 0).Where(id => id != 0).ToHashSet();
        var all = items.Where(i => i is not null).ToList();
        foreach (var slot in GearSets.SavedSlots) {
            if (equipment.GetItemInSlot(slot) is { } on && all.All(i => i.m_globalID.Full != on.m_globalID.Full)) {
                all.Add(on);
            }
        }

        foreach (var item in all) {
            var id = item.m_globalID.Full;
            var name = BotGear.Read(TemplateOf(item))?.Name ?? $"item {id}";
            if (inSets.Contains(id) && !ItemHelper.IsLocked(item)) {
                clicks.Add((id, true, name));
            }
            else if (!inSets.Contains(id) && !worn.Contains(id) && ItemHelper.IsLocked(item) && BotGear.Read(TemplateOf(item)) is not null) {
                clicks.Add((id, false, name));
            }
        }

        return clicks;
    }

}
