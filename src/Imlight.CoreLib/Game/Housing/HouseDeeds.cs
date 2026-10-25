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
 * Gives characters their houses. A house is an item whose template has a DeedBehaviorTemplate, and each one a character
 * owns has a lot of its own, which the client reads from the item. This issues those items, finds the house Home goes
 * to, and keeps every house item pointing at its lot.
 *
 * USAGE EXAMPLE:
 * if (HouseDeeds.TryPrepareHome(wizard, out var entry, out var failure)) {
 *     // send the player to entry.Zone, lot entry.LotGid
 * }
 * var grant = HouseDeeds.Issue(wizard, templateId);
 *
 * NOTE:
 * - A character has one lot per house template, in Wizard.HouseLots. The pointer is written before the item exists,
 *   so an item that is trashed or traded is issued again with the same lot and the house keeps its furniture.
 * - A deed that names a structure is issued together with it, each item with its own lot.
 * - A character's backpack lock (BackpackLock) covers issuing and every handler that moves a house item between the
 *   backpack, a house and the attic. Take it before any house or attic state, never inside one.
 * - Running an issue again changes nothing. Nothing is created when the backpack cannot hold everything that is
 *   missing, and a lot that was stored stays stored.
 * - The house document is not created by an issue. The first entry creates it from the template's layout.
 * - The dorm is the first starter template that is a house (Housing.StarterDeeds). A starter is a plain house template
 *   like any other; no data marks it.
 * - Home goes to the house equipped in the Islands slot, and to the dorm when none is (ResolveEquippedHouse). The game
 *   text says the same: "To go to your new house instead of your dorm room, you can equip it in your Backpack".
 * - An equipped castle is entered at its deed's lot, the lot zone of the deed, like the trainer hall door does.
 *
 * TODO:
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Shared.Utilities;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Housing;

/// <summary>
/// Why a house could not be issued.
/// </summary>
internal enum HouseGrantFailure {
    None,
    NotAHouse,
    BackpackFull,
    Unavailable,
}

/// <summary>
/// The outcome of issuing houses.
/// </summary>
/// <param name="Failure">Why nothing was issued, or <see cref="HouseGrantFailure.None"/>.</param>
/// <param name="Created">The items this call created, which the client does not know yet.</param>
internal readonly record struct HouseGrant(HouseGrantFailure Failure, IReadOnlyList<WizClientObjectItem> Created) {

    internal bool Succeeded => Failure == HouseGrantFailure.None;

}

/// <summary>
/// The house Home goes to.
/// </summary>
/// <param name="TemplateId">The template of the house, or 0 when the character has no home to go to.</param>
/// <param name="Lot">The character's lot for the template, or 0 when none was issued yet.</param>
/// <param name="Item">The house item the character holds, or <c>null</c> when it is missing.</param>
/// <param name="NeedsIssue">Whether the item or its lot has to be issued before the character goes there.</param>
internal readonly record struct HomeTarget(uint TemplateId, ulong Lot, WizClientObjectItem Item, bool NeedsIssue);

internal static class HouseDeeds {

    /// <summary>
    /// The location a character arrives at in a house zone. Every house zone defines it.
    /// </summary>
    internal const string EntryLocation = "Start";

    private const int BackpackLockCount = 32;

    private static readonly Lock[] s_backpackLocks = [.. Enumerable.Range(0, BackpackLockCount).Select(_ => new Lock())];

    /// <summary>
    /// Gets the lock that keeps one character's backpack list from changing in two places at once while house items are
    /// issued, placed, picked up and moved to or from the attic.
    /// </summary>
    /// <param name="wizard">The character.</param>
    /// <returns>The lock to hold for as long as the backpack is read and then changed.</returns>
    internal static Lock BackpackLock(Wizard wizard) => s_backpackLocks[(int) (wizard.CharId % BackpackLockCount)];

    /// <summary>
    /// Finds the house Home goes to.
    /// </summary>
    /// <param name="wizard">The character.</param>
    /// <returns>The first starter house, with what the character holds of it; the target has template 0 when no starter is a house.</returns>
    internal static HomeTarget ResolveHome(Wizard wizard) {
        var starters = HouseTemplates.Starters();
        if (starters.Count == 0) {
            return default;
        }

        var templateId = starters[0].TemplateId;
        var lot = wizard.HouseLots.GetValueOrDefault(templateId);
        var item = FindItem(wizard, templateId);

        return new HomeTarget(templateId, lot, item, item is null || lot == 0);
    }

    /// <summary>
    /// Finds the house the character has equipped in the Islands slot, which Home goes to instead of the dorm.
    /// </summary>
    /// <param name="wizard">The character.</param>
    /// <returns>The template of the equipped house, or 0 when none is equipped or the equipped one is a starter.</returns>
    internal static uint ResolveEquippedHouse(Wizard wizard) {
        var equipment = wizard.EquipmentBehavior;
        var item = equipment?.SlotList is null || equipment.EquippedItems is null ? null : equipment.GetItemInSlot(EquipmentSlotType.Islands);
        if (item is null) {
            return 0;
        }

        var templateId = TemplateIdOf(item);
        if (!HouseTemplates.TryGet(templateId, out _)) {
            Logger.Warning("Character {CharId} has item {ItemId} of template {TemplateId} in the Islands slot, which is not a house.",
                Logger.Args(wizard.CharId, item.m_globalID, templateId));

            return 0;
        }

        // A starter in the slot is the dorm itself, so Home has no other house to go to.
        return HouseTemplates.Starters().Any(starter => starter.TemplateId == templateId) ? 0 : templateId;
    }

    /// <summary>
    /// Makes sure the character holds every starter house, each with its lot.
    /// </summary>
    /// <param name="wizard">The character.</param>
    /// <returns>The items that were created. Calling it again creates none.</returns>
    internal static HouseGrant EnsureStarter(Wizard wizard) {
        var starters = HouseTemplates.Starters();
        if (starters.Count == 0) {
            Logger.Error("No template of Housing.StarterDeeds is a house.");

            return new HouseGrant(HouseGrantFailure.Unavailable, []);
        }

        return IssueAll(wizard, [.. starters.Select(starter => starter.TemplateId)]);
    }

    /// <summary>
    /// Gives a character a house, and its structure when the template names one.
    /// </summary>
    /// <param name="wizard">The character.</param>
    /// <param name="templateId">The deed or structure template.</param>
    /// <returns>The items that were created; none for a house the character already holds.</returns>
    internal static HouseGrant Issue(Wizard wizard, uint templateId)
        => IssueAll(wizard, [templateId]);

    /// <summary>
    /// Gets a character ready to enter their home: the starter items exist, and the house is stored.
    /// </summary>
    /// <param name="wizard">The character.</param>
    /// <param name="entry">What the zone transfer needs when this returns <c>true</c>.</param>
    /// <param name="failure">Why the character cannot go home when this returns <c>false</c>.</param>
    /// <returns><c>false</c> when the backpack cannot hold a missing item or the house cannot be stored.</returns>
    internal static bool TryPrepareHome(Wizard wizard, out HouseEntry entry, out HouseGrantFailure failure) {
        entry = null;
        var grant = EnsureStarter(wizard);
        failure = grant.Failure;
        if (!grant.Succeeded) {
            return false;
        }

        var target = ResolveHome(wizard);
        if (target.Item is null || target.Lot == 0) {
            failure = HouseGrantFailure.Unavailable;

            return false;
        }

        var house = HouseRegistry.GetOrCreate(wizard, target.TemplateId, out _);
        if (house is null || !HouseTemplates.TryGet(target.TemplateId, out var template)) {
            Logger.Error("Character {CharId} has no house for template {TemplateId} and none could be stored.",
                Logger.Args(wizard.CharId, target.TemplateId));
            failure = HouseGrantFailure.Unavailable;

            return false;
        }

        entry = HouseEntry.ForDeed(house.LotGid, house.OwnerCharId, template, target.Item.m_globalID, [.. grant.Created.Select(item => (ulong) item.m_globalID)]);

        return true;
    }

    /// <summary>
    /// Gets a character ready to enter one of their houses the way Home does: the house item exists, and the house is
    /// stored. A teleport request is not answered by the client for a house it was only just given, a deed request is.
    /// </summary>
    /// <param name="wizard">The character.</param>
    /// <param name="templateId">The house template.</param>
    /// <param name="entry">What the zone transfer needs when this returns <c>true</c>.</param>
    /// <param name="failure">Why the character cannot enter when this returns <c>false</c>.</param>
    /// <returns><c>false</c> when the backpack cannot hold the item or the house cannot be stored.</returns>
    internal static bool TryPrepareHouse(Wizard wizard, uint templateId, out HouseEntry entry, out HouseGrantFailure failure) {
        entry = null;
        var grant = Issue(wizard, templateId);
        failure = grant.Failure;
        if (!grant.Succeeded) {
            return false;
        }

        var item = FindItem(wizard, templateId);
        var house = HouseRegistry.GetOrCreate(wizard, templateId, out _);
        if (item is null || house is null || !HouseTemplates.TryGet(templateId, out var template)) {
            Logger.Error("Character {CharId} has no house for template {TemplateId} and none could be stored.",
                Logger.Args(wizard.CharId, templateId));
            failure = HouseGrantFailure.Unavailable;

            return false;
        }

        entry = HouseEntry.ForDeed(house.LotGid, house.OwnerCharId, template, item.m_globalID, [.. grant.Created.Select(created => (ulong) created.m_globalID)]);

        return true;
    }

    /// <summary>
    /// Points every house item the character holds at its lot, and issues a lot to a house item that has none, so
    /// whatever way the item came, the client reads its lot from it.
    /// </summary>
    /// <param name="wizard">The character.</param>
    internal static void ApplyLots(Wizard wizard) {
        lock (BackpackLock(wizard)) {
            foreach (var item in HouseItems(wizard)) {
                if (!TryEnsureLot(wizard, TemplateIdOf(item), out var lot)) {
                    Logger.Warning("Could not give house item {ItemId} of character {CharId} a lot.", Logger.Args(item.m_globalID, wizard.CharId));

                    continue;
                }

                if (!TrySetLot(item, lot, out var changed)) {
                    Logger.Warning("House item {ItemId} of character {CharId} has no deed behavior.", Logger.Args(item.m_globalID, wizard.CharId));
                } else if (changed && !WizardItemCollection.ApplyDeedLot(item, lot)) {
                    Logger.Warning("Could not store the lot on house item {ItemId} of character {CharId}.", Logger.Args(item.m_globalID, wizard.CharId));
                }
            }
        }
    }

    /// <summary>
    /// Finds the house template of a house item the character holds, such as the deed the Castle Tours window names.
    /// </summary>
    /// <param name="wizard">The character.</param>
    /// <param name="itemGid">The global id of the item.</param>
    /// <param name="templateId">The house template when this returns <c>true</c>.</param>
    /// <returns><c>false</c> when the character holds no house item with that id.</returns>
    internal static bool TryGetHeldTemplate(Wizard wizard, ulong itemGid, out uint templateId) {
        templateId = 0;
        if (itemGid == 0) {
            return false;
        }

        lock (BackpackLock(wizard)) {
            var item = HeldItems(wizard).FirstOrDefault(held => (ulong) held.m_globalID == itemGid);
            if (item is null || !HouseTemplates.TryGet(TemplateIdOf(item), out _)) {
                return false;
            }

            templateId = TemplateIdOf(item);

            return true;
        }
    }

    /// <summary>
    /// Points a house item at a lot.
    /// </summary>
    /// <param name="item">The house item.</param>
    /// <param name="lotGid">The lot.</param>
    /// <param name="changed">Whether the item pointed at another lot before.</param>
    /// <returns><c>false</c> when the item has no deed behavior.</returns>
    internal static bool TrySetLot(WizClientObjectItem item, ulong lotGid, out bool changed) {
        changed = false;
        if (item.m_inactiveBehaviors is null) {
            CoreObjectFactory.InitializeCoreObjectBehaviors(item, item.m_templateID);
        }

        if (!CoreObjectFactory.FindBehaviorInstance<DeedBehavior>(item, out var deed)) {
            return false;
        }

        changed = deed.m_lotInstanceGID != lotGid;
        deed.m_lotInstanceGID = lotGid;

        return true;
    }

    private static HouseGrant IssueAll(Wizard wizard, IReadOnlyList<uint> requested) {
        lock (BackpackLock(wizard)) {
            if (!TryCollectTemplates(requested, out var wanted, out var failure)) {
                return new HouseGrant(failure, []);
            }

            var missing = wanted.Where(template => FindItem(wizard, template.TemplateId) is null).ToList();
            if (missing.Count > wizard.BackpackFreeSlots) {
                return new HouseGrant(HouseGrantFailure.BackpackFull, []);
            }

            var lots = new Dictionary<uint, ulong>();
            foreach (var template in wanted) {
                if (!TryEnsureLot(wizard, template.TemplateId, out var lot)) {
                    Logger.Error("Character {CharId} has no lot for house template {TemplateId} and none could be stored.",
                        Logger.Args(wizard.CharId, template.TemplateId));

                    return new HouseGrant(HouseGrantFailure.Unavailable, []);
                }

                lots[template.TemplateId] = lot;
            }

            var created = new List<WizClientObjectItem>();
            foreach (var template in wanted) {
                var item = FindItem(wizard, template.TemplateId);
                if (item is null) {
                    item = CreateItem(wizard, template.TemplateId, lots[template.TemplateId]);
                    if (item is null) {
                        return new HouseGrant(HouseGrantFailure.Unavailable, created);
                    }

                    created.Add(item);
                } else if (!TrySetLot(item, lots[template.TemplateId], out var changed)) {
                    Logger.Error("House item {ItemId} of character {CharId} has no deed behavior.", Logger.Args(item.m_globalID, wizard.CharId));

                    return new HouseGrant(HouseGrantFailure.Unavailable, created);
                } else if (changed && !WizardItemCollection.ApplyDeedLot(item, lots[template.TemplateId])) {
                    Logger.Warning("Could not store the lot on house item {ItemId} of character {CharId}.", Logger.Args(item.m_globalID, wizard.CharId));
                }
            }

            return new HouseGrant(HouseGrantFailure.None, created);
        }
    }

    private static bool TryCollectTemplates(IReadOnlyList<uint> requested, out List<HouseTemplateInfo> wanted, out HouseGrantFailure failure) {
        wanted = [];
        failure = HouseGrantFailure.None;
        foreach (var templateId in requested) {
            if (!HouseTemplates.TryGet(templateId, out var template)) {
                Logger.Error("Template {TemplateId} is not a house.", Logger.Args(templateId));
                failure = HouseGrantFailure.NotAHouse;

                return false;
            }

            if (wanted.All(known => known.TemplateId != templateId)) {
                wanted.Add(template);
            }

            if (template.HasStructure && wanted.All(known => known.TemplateId != template.StructureTemplateId)) {
                if (!HouseTemplates.TryGet(template.StructureTemplateId, out var structure)) {
                    Logger.Error("The structure {StructureTemplateId} of house template {TemplateId} is not a house.",
                        Logger.Args(template.StructureTemplateId, templateId));
                    failure = HouseGrantFailure.Unavailable;

                    return false;
                }

                wanted.Add(structure);
            }
        }

        return true;
    }

    private static bool TryEnsureLot(Wizard wizard, uint templateId, out ulong lot) {
        lot = wizard.HouseLots.GetValueOrDefault(templateId);
        if (lot != 0) {
            return true;
        }

        var candidate = (ulong) RandomGen.GenerateGUID();
        if (WizardCollection.TryAddHouseLot(wizard, templateId, candidate)) {
            lot = candidate;

            return true;
        }

        // Another write stored a lot for the template first, so that one is the lot.
        lot = WizardCollection.GetCharacterUnloaded(wizard.CharId)?.HouseLots?.GetValueOrDefault(templateId) ?? 0;
        if (lot != 0) {
            wizard.HouseLots[templateId] = lot;
        }

        return lot != 0;
    }

    private static WizClientObjectItem CreateItem(Wizard wizard, uint templateId, ulong lotGid) {
        if (CoreObjectFactory.FinalizeCoreObject(templateId) is not WizClientObjectItem item) {
            Logger.Error("The house template {TemplateId} is not an item.", Logger.Args(templateId));

            return null;
        }

        item.m_characterId = (GID) wizard.CharId;
        CoreObjectFactory.InitializeCoreObjectBehaviors(item, item.m_templateID);
        if (!TrySetLot(item, lotGid, out _)) {
            Logger.Error("The house template {TemplateId} has no deed behavior.", Logger.Args(templateId));

            return null;
        }

        return wizard.AddInitializedItemToInventory(item) ? item : null;
    }

    private static WizClientObjectItem FindItem(Wizard wizard, uint templateId)
        => HeldItems(wizard).FirstOrDefault(item => TemplateIdOf(item) == templateId);

    private static List<WizClientObjectItem> HouseItems(Wizard wizard)
        => [.. HeldItems(wizard).Where(item => HouseTemplates.TryGet(TemplateIdOf(item), out _))];

    private static IEnumerable<WizClientObjectItem> HeldItems(Wizard wizard)
        => wizard.InventoryBehavior.Items.Concat(wizard.EquipmentBehavior?.EquippedItems ?? []);

    private static uint TemplateIdOf(WizClientObjectItem item)
        => (uint) Math.Min((ulong) item.m_templateID, uint.MaxValue);

}
