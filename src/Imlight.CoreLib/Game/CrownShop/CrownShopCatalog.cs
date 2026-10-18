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
 * CROWN SHOP CATALOG
 * ========================================================================
 *
 * PURPOSE:
 * What the crown shop sells and the list the game shows, built from SpiralDB's CrownShop/Tab*.json.
 *
 * NOTE:
 * - Each tab file has the tab (its id, name, icon and category ids), its categories and its items (template,
 *   crowns, gold and the category ids the item shows under).
 * - The list goes to the game as a CrownShopData object, written like the badge list (no versioning, flags 4,
 *   compressed with its length in front). One message that is too big makes the game drop the connection, so
 *   items are taken from every tab in turn until the list is as big as MaxListBytes allows.
 * - The Level 50 Elixir is always first and costs nothing.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.Commands.Protocols;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.CrownShop;

internal static class CrownShopCatalog {

    internal sealed record Price(int Crowns, int Gold);

    private const string FOLDER = "CrownShop";
    private const uint SERIALIZATION_FLAGS = 4;
    private const int WISHLIST_SIZE = 100;

    // [CrownShop] MaxListBytes: the biggest the packed list may be. Badges are sent in 30000 byte pieces.
    private static readonly int s_maxListBytes = Math.Clamp(ReadSetting("CrownShop.MaxListBytes", 30000), 1000, 60000);

    private static readonly Lazy<(byte[] List, Dictionary<uint, Price> Prices, int Shown, int Total)> s_catalog = new(Build);

    /// <summary>
    /// The packed list for MSG_PCS_LIST_RESPONSE, or an empty array if there is no crown shop data.
    /// </summary>
    internal static byte[] PackedList => s_catalog.Value.List;

    /// <summary>
    /// What an item costs, or null if the shop does not sell it.
    /// </summary>
    internal static Price GetPrice(ulong item)
        => s_catalog.Value.Prices.GetValueOrDefault((uint) (item & 0xFFFFFFFFUL));

    internal static string Describe() => $"{s_catalog.Value.Shown} of {s_catalog.Value.Total} items";

    private static (byte[], Dictionary<uint, Price>, int, int) Build() {
        var prices = new Dictionary<uint, Price>();
        try {
            var tabs = new List<CrownShopCategoryMenu>();
            var categories = new List<CrownShopCategory>();
            var itemsByTab = new List<List<CrownShopItem>>();
            foreach (var file in TabFiles()) {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var root = document.RootElement;
                var tabId = Int(root, "tabId");
                tabs.Add(new CrownShopCategoryMenu {
                    m_name = Text(root, "name"),
                    m_ID = tabId,
                    m_description = Text(root, "description"),
                    m_iconResource = Text(root, "icon"),
                    m_categoryIDs = [.. Array(root, "categoryIds").Select(e => e.GetInt32())],
                    m_tags = Text(root, "tags"),
                });

                foreach (var category in Array(root, "categories")) {
                    var flags = category.TryGetProperty("flags", out var f) ? f : default;
                    categories.Add(new CrownShopCategory {
                        m_name = Text(category, "name"),
                        m_ID = Int(category, "categoryId"),
                        m_parentTabID = Int(category, "parentTabId", tabId),
                        m_description = Text(category, "description"),
                        m_iconResource = Text(category, "icon"),
                        m_tags = Text(category, "tags"),
                        m_dontFilterOwnedRecoItems = Bool(flags, "dontFilterOwnedRecoItems"),
                        m_allowMultipleBuy = Bool(flags, "allowMultipleBuy"),
                        m_forceDisallowMultipleBuy = Bool(flags, "forceDisallowMultipleBuy"),
                        m_isHousesCategory = Bool(flags, "isHousesCategory"),
                        m_isEverythingCategory = Bool(flags, "isEverythingCategory"),
                        m_isGroupElixirsCategory = Bool(flags, "isGroupElixirsCategory"),
                    });
                }

                var items = new List<CrownShopItem>();
                foreach (var entry in Array(root, "items")) {
                    var templateId = (uint) Int(entry, "templateId");
                    if (templateId == 0 || prices.ContainsKey(templateId)) {
                        continue;
                    }

                    var price = templateId == Level50Elixir.TEMPLATE_ID
                        ? new Price(0, 0)
                        : new Price(Math.Max(0, Int(entry, "crownsCost")), Math.Max(0, Int(entry, "goldCost")));
                    prices[templateId] = price;
                    items.Add(new CrownShopItem {
                        m_itemTemplateId = (GID) (ulong) templateId,
                        m_goldCost = price.Gold,
                        m_crownsCost = price.Crowns,
                        m_displayPriority = string.Join(",", Array(entry, "displayPriority").Select(e => e.GetInt32())),
                        m_description = "",
                        m_segReqsStatement = "",
                        m_segReqsPoolsStatements = [],
                    });
                }

                itemsByTab.Add(items);
            }

            if (prices.Count == 0) {
                Logger.Warning("Crown shop: no SpiralDB CrownShop/Tab*.json files were found, so the shop is empty.");

                return ([], prices, 0, 0);
            }

            // One item from each tab in turn, with the Level 50 Elixir first, so a short list still has every tab.
            var ordered = new List<CrownShopItem>();
            for (var i = 0; itemsByTab.Any(items => i < items.Count); i++) {
                ordered.AddRange(itemsByTab.Where(items => i < items.Count).Select(items => items[i]));
            }

            var elixir = ordered.FindIndex(item => (ulong) item.m_itemTemplateId == Level50Elixir.TEMPLATE_ID);
            if (elixir > 0) {
                var item = ordered[elixir];
                ordered.RemoveAt(elixir);
                ordered.Insert(0, item);
            }

            var layout = new CrownShopLayout { m_categories = categories, m_tabs = tabs };

            // The most items whose packed list still fits.
            int low = 0, high = ordered.Count;
            byte[] best = Pack(layout, []);
            while (low < high) {
                var middle = (low + high + 1) / 2;
                var packed = Pack(layout, ordered.Take(middle).ToList());
                if (packed is not null && packed.Length <= s_maxListBytes) {
                    low = middle;
                    best = packed;
                }
                else {
                    high = middle - 1;
                }
            }

            // Only what the game is shown can be bought.
            var shown = ordered.Take(low).Select(item => (uint) (ulong) item.m_itemTemplateId).ToHashSet();
            var shownPrices = prices.Where(p => shown.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);

            Logger.Information("Crown shop: {0} tabs, {1} categories, showing {2} of {3} items ({4} bytes packed).",
                Logger.Args(tabs.Count, categories.Count, low, ordered.Count, best?.Length ?? 0));

            return (best ?? [], shownPrices, low, ordered.Count);
        }
        catch (Exception e) {
            Logger.Error("Crown shop: could not build the list: {0}", Logger.Args(e));

            return ([], prices, 0, prices.Count);
        }
    }

    private static byte[] Pack(CrownShopLayout layout, List<CrownShopItem> items) {
        var data = new CrownShopData {
            m_items = items,
            m_crownShopLayout = layout,
            m_crownShopSegReqsSummary = new CrownShopSegReqsSummary {
                m_csvNItemsList = "",
                m_csvNItemsCategoryList = "",
                m_csvNDaysSinceItemPurchasedList = "",
                m_csvHasBadgeList = "",
            },
            m_wishlistMaxSize = WISHLIST_SIZE,
            m_wishlistSBExpansionSize = 0,
        };

        var serializer = new ObjectSerializer(Versionable: false);

        return serializer.Serialize(data, SERIALIZATION_FLAGS, out var bytes) ? Compression.CompressWithLength(bytes) : null;
    }

    // A tab in our additions replaces SpiralDB's tab of the same file name.
    private static IEnumerable<string> TabFiles()
        => SpiralDB.DataFolders
            .Select(folder => Path.Combine(folder, FOLDER))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.GetFiles(folder, "Tab*.json"))
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(file => int.TryParse(Path.GetFileNameWithoutExtension(file)[3..], out var n) ? n : int.MaxValue);

    private static IEnumerable<JsonElement> Array(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    private static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value.ToString() : "";

    private static int Int(JsonElement element, string name, int fallback = 0)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : fallback;

    private static bool Bool(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.True;

    private static T ReadSetting<T>(string key, T fallback) {
        try {
            return ConfigurationManager.GetValue(key, fallback);
        }
        catch (Exception) {
            return fallback;
        }
    }

}
