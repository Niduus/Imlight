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
using System.Linq;
using Akka.Actor;
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.Housing;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.World;

/// <summary>
/// Sellers moved out of the bazaar (Niduus, 2026-10-19). Hao in the Pet Pavilion sells every snack of rank 8 or
/// higher, and `.hall vendors` gives one piece of furniture per seller: one per reagent kind (spellements included)
/// and one per jewel shape and pin. Wherever one is placed in a house its seller stands on it, like the trainer
/// tomes (<see cref="HouseTrainers"/>). Everything they sell costs <see cref="Price"/> gold.
/// </summary>
internal static class HouseVendors {

    internal enum ShopWindow { Equipment, Snack, Reagent, Jewel }

    internal sealed record Seller(string Key, string Name, uint FurnitureId, ShopWindow Window, Func<List<uint>> Items);

    private const string TAG_PREFIX = "HouseVendor_";
    private const string REAGENT_FOLDER = "ObjectData/Reagents/";
    private const string JEWEL_FOLDER = "ObjectData/Jewels/";
    private const string SNACK_FOLDER = "ObjectData/PetSnacks/";
    private const int MIN_SNACK_RANK = 8;

    /// <summary>What everything the sellers sell costs.</summary>
    internal static readonly int Price = ConfigurationManager.GetValue("HallVendors.Price", 1);

    // [HallVendors] SpecialShopWindows: the snack, reagent and jewel shop windows; false uses the equipment shop window.
    private static readonly bool s_specialWindows = ConfigurationManager.GetValue("HallVendors.SpecialShopWindows", true);

    private static readonly uint s_snackVendorId = ConfigurationManager.GetValue("HallVendors.SnackVendorId", 641800u);
    private static readonly uint s_reagentNpcId = ConfigurationManager.GetValue("HallVendors.ReagentSellerNpc", 136831u);
    private static readonly uint s_jewelNpcId = ConfigurationManager.GetValue("HallVendors.JewelSellerNpc", 1233878u);
    // A real snack shop (Sa'eed). The game picks a shop's window from its shop type, so each seller borrows the
    // shop type and title of a real shop of its kind (Niduus: Hao opened as an empty robe shop).
    private static readonly uint s_snackShopNpcId = ConfigurationManager.GetValue("HallVendors.SnackShopNpc", 227094u);

    private static readonly Lazy<List<uint>> s_snacks = new(() => [.. TemplatesIn<PetSnackItemTemplate>(SNACK_FOLDER)
        .Where(snack => snack.Template.m_rank >= MIN_SNACK_RANK)
        .Select(snack => snack.Id)]);

    // One seller per reagent kind and per jewel shape or pin, each on its own piece of furniture.
    private static readonly Seller[] s_sellers = [
        Reagents("ReagentHarvest", "Harvest Reagent Seller", 160289),
        Reagents("ReagentHarvestRare", "Rare Harvest Reagent Seller", 160290),
        Reagents("ReagentShop", "Shop Reagent Seller", 160368),
        Reagents("ReagentDrop", "Drop Reagent Seller", 160691),
        Reagents("ReagentEvent", "Event Reagent Seller", 470451),
        Reagents("ReagentShard", "Spellement Seller", 160816),
        Reagents("ReagentPet", "Pet Reagent Seller", 1421924),
        Jewels("CircleJewel", "Circle Jewel Seller", 160323),
        Jewels("SquareJewel", "Square Jewel Seller", 641572),
        Jewels("TriangleJewel", "Triangle Jewel Seller", 160326),
        Jewels("TearJewel", "Tear Jewel Seller", 160330),
        Jewels("PetJewel", "Star Jewel Seller", 160799),
        Jewels("PinSquareSword", "Power Pin Seller", 160589),
        Jewels("PinSquareShield", "Shield Pin Seller", 643153),
        Jewels("PinSquarePip", "Pip Pin Seller", 160325),
    ];

    private static Seller Reagents(string kind, string name, uint furniture)
        => new(kind, name, ConfigurationManager.GetValue($"HallVendors.{kind}", furniture), ShopWindow.Reagent,
            Catalog<ReagentItemTemplate>(REAGENT_FOLDER, kind));

    private static Seller Jewels(string shape, string name, uint furniture)
        => new(shape, name, ConfigurationManager.GetValue($"HallVendors.{shape}", furniture), ShopWindow.Jewel,
            Catalog<WizItemTemplate>(JEWEL_FOLDER, shape));

    private static Func<List<uint>> Catalog<T>(string folder, string adjective) where T : WizItemTemplate {
        var items = new Lazy<List<uint>>(() => [.. TemplatesIn<T>(folder)
            .Where(item => (item.Template.m_adjectiveList ?? []).Any(name => (string) name == adjective)
                && !((string) item.Template.m_objectName ?? "").Contains("TEST"))
            // The same item can have spare copies with no picture (Petrified Wood): keep the ones that have one.
            .GroupBy(item => (string) item.Template.m_displayName ?? item.Id.ToString())
            .SelectMany(same => same.Any(item => HasPicture(item.Template)) ? same.Where(item => HasPicture(item.Template)) : same)
            .Select(item => item.Id)
            .OrderBy(id => id)]);

        return () => items.Value;
    }

    private static bool HasPicture(WizItemTemplate template)
        => !string.IsNullOrWhiteSpace(template is ReagentItemTemplate reagent ? (string) reagent.m_imageName : (string) template.m_sIcon);

    internal static IReadOnlyList<Seller> Sellers => s_sellers;

    internal static Seller SellerForFurniture(uint templateId)
        => templateId == 0 ? null : s_sellers.FirstOrDefault(seller => seller.FurnitureId == templateId);

    internal static bool IsVendorItem(uint templateId) => SellerForFurniture(templateId) is not null;

    internal static bool IsVendorTag(string zoneTag) => zoneTag?.StartsWith(TAG_PREFIX, StringComparison.Ordinal) == true;

    /// <summary>The seller of a spawned seller's zone tag, or null.</summary>
    internal static Seller SellerForTag(string zoneTag) {
        if (!IsVendorTag(zoneTag)) {
            return null;
        }

        var key = zoneTag[TAG_PREFIX.Length..];
        var end = key.LastIndexOf('_');

        return end <= 0 ? null : s_sellers.FirstOrDefault(seller => seller.Key == key[..end]);
    }

    /// <summary>The overhead name for a seller's zone tag, or null.</summary>
    internal static string NameFor(string zoneTag) => SellerForTag(zoneTag)?.Name;

    /// <summary>NPCs that sell from these lists: Hao and the two house seller looks.</summary>
    internal static bool IsSellerNpc(uint templateId)
        => templateId != 0 && (templateId == s_snackVendorId || templateId == s_reagentNpcId || templateId == s_jewelNpcId);

    /// <summary>What an NPC sells: Hao the snacks, a house seller its kind; null for every other NPC.</summary>
    internal static (List<uint> Items, ShopWindow Window)? StockFor(uint templateId, string zoneTag) {
        if (SellerForTag(zoneTag) is { } seller) {
            return (seller.Items(), s_specialWindows ? seller.Window : ShopWindow.Equipment);
        }

        if (templateId == s_snackVendorId) {
            return (s_snacks.Value, s_specialWindows ? ShopWindow.Snack : ShopWindow.Equipment);
        }

        return null;
    }

    /// <summary>The real shop whose window a seller's window copies, or null for the equipment window.</summary>
    internal static WizShoppingBehaviorTemplate ShopLookFor(ShopWindow window) {
        var npcId = window switch {
            ShopWindow.Snack => s_snackShopNpcId,
            ShopWindow.Reagent => s_reagentNpcId,
            ShopWindow.Jewel => s_jewelNpcId,
            _ => 0u,
        };
        if (npcId == 0) {
            return null;
        }

        var shop = (CoreObjectFactory.GetCoreTemplate(npcId) as GameObjectTemplate)?.m_behaviors?.OfType<WizShoppingBehaviorTemplate>().FirstOrDefault();
        if (shop is null) {
            Logger.Warning("The {0} shop {1} has no shop to copy the window of.", Logger.Args(window, npcId));
        }

        return shop;
    }

    private static string Tag(Seller seller, uint gidId) => $"{TAG_PREFIX}{seller.Key}_{gidId}";

    /// <summary>Puts the seller on a placed piece of seller furniture.</summary>
    internal static void Spawn(IActorRef zone, uint gidId, PlacedItem item) {
        if (zone is null || item is null || SellerForFurniture(item.TemplateId) is not { } seller) {
            return;
        }

        zone.Tell(new SERVICE_101_PROTOCOL.MSG_SPAWNHOUSETRAINER {
            Tag = Tag(seller, gidId), X = item.X, Y = item.Y, Z = item.Z, Yaw = item.Yaw,
        }, ActorRefs.NoSender);
    }

    /// <summary>Takes away the seller of seller furniture that was moved or picked up (any seller when the furniture is unknown).</summary>
    internal static void Remove(IActorRef zone, uint gidId, uint templateId) {
        if (zone is null) {
            return;
        }

        foreach (var seller in templateId == 0 ? s_sellers : s_sellers.Where(seller => seller.FurnitureId == templateId)) {
            zone.Tell(new ZONE_102_PROTOCOL.MSG_REMOVETRIGGEROBJECT { ObjectName = Tag(seller, gidId) }, ActorRefs.NoSender);
        }
    }

    /// <summary>Puts a seller on every piece of seller furniture in a house.</summary>
    internal static void SpawnAll(IActorRef zone, HouseState house) {
        var snapshot = house.Snapshot();
        for (var slot = 0; slot < snapshot.Slots.Count; slot++) {
            if (snapshot.Slots[slot] is { } item) {
                Spawn(zone, unchecked(snapshot.GidBase + (uint) slot), item);
            }
        }
    }

    /// <summary>Builds the seller for a spawn request.</summary>
    internal static bool TryCreate(SERVICE_101_PROTOCOL.MSG_SPAWNHOUSETRAINER spawn, out CoreObjectInfo info, out CoreObject coreObject,
                                   out GameObjectTemplate template) {
        info = null;
        coreObject = null;
        template = null;
        if (SellerForTag(spawn.Tag) is not { } seller) {
            return false;
        }

        var npcId = seller.Window == ShopWindow.Jewel ? s_jewelNpcId : s_reagentNpcId;
        template = CoreObjectFactory.GetCoreTemplate(npcId) as GameObjectTemplate;
        if (template is null) {
            Logger.Warning("The seller template {0} was not found.", Logger.Args(npcId));

            return false;
        }

        info = new CoreObjectInfo {
            m_templateID = npcId,
            m_fScale = 1.0f,
            m_zoneTag = spawn.Tag,
            m_location = new Vector3(spawn.X, spawn.Y, spawn.Z),
            m_orientation = new Vector3(0, 0, spawn.Yaw),
        };
        coreObject = CoreObjectFactory.FinalizeCoreObject(info, template);
        if (coreObject is null) {
            return false;
        }

        coreObject = CoreObjectFactory.InitializeCoreObjectBehaviors(coreObject, template);

        return coreObject is not null;
    }

    private static List<(uint Id, T Template)> TemplatesIn<T>(string folder) where T : class {
        var found = new List<(uint, T)>();
        try {
            foreach (var location in CoreObjectFactory.TemplateManifest?.m_serializedTemplates ?? []) {
                string file = location?.m_filename;
                if (file is not null && file.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                    && CoreObjectFactory.GetCoreTemplate(location.m_id) is T template) {
                    found.Add((location.m_id, template));
                }
            }
        }
        catch (Exception e) {
            Logger.Error("Could not list the sellers' {0}: {1}", Logger.Args(folder, e));
        }

        return found;
    }

}
