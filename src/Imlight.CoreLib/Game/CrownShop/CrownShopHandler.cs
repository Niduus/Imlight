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
 * COMBAT SPELL DECK MANAGEMENT SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * Manages the Crown Shop configuration and runtime data. Loads Crown Shop tabs,
 * categories, and items from JSON configuration files, converts them into the
 * corresponding game objects, and serializes the complete Crown Shop data structure for client consumption.
 * 
 * USAGE EXAMPLE:
 * 
 * 
 * NOTE:
 * 
 * Created by: Phill030
 * Version: KALI 1.0
 * Last Updated: 28.09.2026
 */

using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Intrinsics.Arm;
using System.Text;
using System.Text.Json;

namespace Imlight.CoreLib.Game.CrownShop;

public static class CrownShopHandler {
    private static readonly bool s_enabled
        = ConfigurationManager.Settings["CrownShop.Enabled"].AsBool();
    private static readonly uint s_wishlistMaxSize
        = ConfigurationManager.Settings["CrownShop.WishlistMaxSize"].AsUInt();
    private static readonly uint s_wishlistSBExpansionSize
    = ConfigurationManager.Settings["CrownShop.WishlistSBExpansionSize"].AsUInt();
    private static readonly bool s_autoReloadOnChanges
        = ConfigurationManager.Settings["CrownShop.AutoReloadOnChanges"].AsBool();
    public static readonly uint s_maxBuyCount
        = ConfigurationManager.Settings["CrownShop.MaxBuyCount"].AsUInt();
    private static readonly string s_crownShopConfigPath
        = ConfigurationManager.Settings["Database.SpiralDBLocalPath"] + "/CrownShop";


    private static readonly JsonSerializerOptions s_jsonSerializerOptions = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static FileSystemWatcher s_watcher;
    private static DateTime s_lastReloadTime = DateTime.MinValue;
    private static readonly object s_reloadLock = new();



    private static List<CrownShopTabConfig> s_crownShopTabs = new();
    private static List<CrownShopCategoryConfig> s_crownShopCategories = new();

    private static Dictionary<ulong, CrownShopItem> s_items = new();


    // [CrownShop] ShopListBytes: the biggest the packed list may be. The game reads it with a 2 byte length.
    private static readonly int s_maxListBytes
        = Math.Clamp(ConfigurationManager.GetValue("CrownShop.ShopListBytes", 60000), 1000, 65000);

    private static List<CrownShopItem> s_shownToAll = [];
    private static List<CrownShopGameItems.Versioned> s_versioned = [];
    private static CrownShopLayout s_layout = new() { m_tabs = [], m_categories = [] };
    private static readonly Dictionary<int, ByteString> s_dataByVersions = [];
    private static List<int> s_versionLevels = [];

    public static void Load() {
        if (!s_enabled) {
            Logger.Information("CrownShop not initializing (disabled in config)");
            return;
        }

        var basePath = Path.GetFullPath(s_crownShopConfigPath);
        if (!Directory.Exists(basePath)) {
            Logger.Warning("CrownShop configuration directory not found: {0}", Logger.Args(basePath));
            return;
        }

        // Load data from config files
        lock(s_reloadLock) {
            Logger.Information("Loading CrownShop configuration from: {0}", Logger.Args(s_crownShopConfigPath));

            var loadedTabs = new List<CrownShopTabConfig>();
            var loadedCategories = new List<CrownShopCategoryConfig>();

            var filePaths = Directory.GetFiles(basePath, "*", SearchOption.TopDirectoryOnly);
            foreach(var path in filePaths) {
                if (!File.Exists(path)) {
                    Logger.Warning("CrownShop tab configuration file not found: {0}", Logger.Args(path));
                    continue;
                }

                try {
                    var json = File.ReadAllText(path);
                    var tab = JsonSerializer.Deserialize<CrownShopTabConfig>(json, s_jsonSerializerOptions);

                    if (tab == null) {
                        Logger.Warning("CrownShop tab configuration file is empty or invalid: {0}", Logger.Args(path));
                        continue;
                    }

                    loadedTabs.Add(tab);
                    foreach (var category in tab.Categories) {
                        loadedCategories.Add(category);
                    }

                }
                catch (Exception e) {
                    Logger.Warning("Failed to load CrownShop tab configuration file: {0}. Error: {1}", Logger.Args(path, e.Message));
                }
            }

            s_crownShopTabs = loadedTabs.OrderBy(t => t.TabId).ToList();
            s_crownShopCategories = s_crownShopTabs.SelectMany(t => t.Categories).ToList();

            var layoutTabs = new List<CrownShopCategoryMenu>();
            var layoutCategories = new List<CrownShopCategory>();
            var crownShopItems = new Dictionary<ulong, CrownShopItem>();

            foreach (var tab in s_crownShopTabs) {
                var layoutTab = new CrownShopCategoryMenu() {
                    m_ID = tab.TabId,
                    m_name = tab.Name,
                    m_iconResource = tab.Icon,
                    m_categoryIDs = tab.CategoryIds,
                    m_tags = tab.Tags,
                    m_description = tab.Description
                };
                layoutTabs.Add(layoutTab);

                foreach (var category in tab.Categories) {
                    var layoutCategory = new CrownShopCategory() {
                        m_ID = category.CategoryId,
                        m_parentTabID = category.ParentTabId,
                        m_name = category.Name,
                        m_description = category.Description,
                        m_iconResource = category.Icon,
                        m_tags = category.Tags,
                        m_allowMultipleBuy = category.Flags.AllowMultipleBuy,
                        m_forceDisallowMultipleBuy = category.Flags.ForceDisallowMultipleBuy,
                        m_dontFilterOwnedRecoItems = category.Flags.DontFilterOwnedRecoItems,
                        m_isHousesCategory = category.Flags.IsHousesCategory,
                        m_isEverythingCategory = category.Flags.IsEverythingCategory,
                        m_isGroupElixirsCategory = category.Flags.IsGroupElixirsCategory
                    };
                    layoutCategories.Add(layoutCategory);
                }

                foreach(var item in tab.Items) {

                    var displayPriority = new StringBuilder();
                    for(int i = 0; i < item.DisplayPriority.Count; i++) {
                        displayPriority.Append($"{item.DisplayPriority[i]}:{i},");
                    }
                    displayPriority.Length--; // Remove last comma

                    var crownShopItem = new CrownShopItem() {
                        m_itemTemplateId = item.TemplateId,
                        m_itemFlags = item.ItemFlags,
                        m_goldCost = item.GoldCost,
                        m_strikethruGold = item.StrikethruGold,
                        m_crownsCost = item.CrownsCost,
                        m_strikethruCrowns = item.StrikethruCrowns,
                        m_ticketCost = item.TicketCost,
                        m_displayPriority = displayPriority.ToString(),
                        m_combatOnly = item.CrownShopItemFlags.CombatOnly,
                        m_noGift = item.CrownShopItemFlags.NoGift,
                        m_recommendIfOwned = item.CrownShopItemFlags.RecommendIfOwned,
                        m_saleID = 0,
                        m_description = "",
                        m_segReqsPoolsStatements = [],
                        m_segReqsStatement = "",
                        // Example: HasBadge(Raid01_QuestComplete_01) = True
                        // Example: ExcludeIfNoSegData = True,HasBadge(FinishAR-PostLM-MAIN-001) = True
                        // Example: ExcludeIfNoSegData = True,NLevel &gt; 169,HasBadge(WinNightmare) = True,HasBadge(FinishAR-PostWL-MAIN-001) = True
                    };

                    crownShopItems[item.TemplateId] = crownShopItem;
                }
            }

            // The items the game files price in crowns that SpiralDB's list leaves out; gear and bundles with level
            // versions are added per wizard level in GetCrownShopData.
            var (gameItems, versioned) = CrownShopGameItems.Load();
            var shownToAll = crownShopItems.Values.ToList();
            foreach (var entry in gameItems.Where(e => !crownShopItems.ContainsKey(e.TemplateId))) {
                var gameItem = ToShopItem(entry);
                crownShopItems[entry.TemplateId] = gameItem;
                shownToAll.Add(gameItem);
            }

            var versionedItems = new List<CrownShopGameItems.Versioned>();
            foreach (var group in versioned.Where(g => g.Versions.All(v => !crownShopItems.ContainsKey(v.TemplateId)))) {
                group.Versions.ForEach(version => crownShopItems[version.TemplateId] = ToShopItem(version));
                versionedItems.Add(group);
            }

            s_items = crownShopItems;
            s_shownToAll = shownToAll;
            s_versioned = versionedItems;
            s_layout = new CrownShopLayout() {
                m_tabs = layoutTabs,
                m_categories = layoutCategories
            };
            s_versionLevels = [.. versionedItems.SelectMany(g => g.Versions).Select(v => v.Level).Distinct().Order()];
            s_dataByVersions.Clear();

            Logger.Information("CrownShop successfully initialized: {0} tabs, {1} categories, {2} items for every wizard and {3} with level versions.",
                Logger.Args(s_crownShopTabs.Count, s_crownShopCategories.Count, shownToAll.Count, versionedItems.Count));

            if (s_autoReloadOnChanges && s_watcher == null) {
                SetupAutoReloadWatcher(basePath);
            }

        }
    }

    private static void SetupAutoReloadWatcher(string basePath) {
        try {
            s_watcher = new FileSystemWatcher(basePath, "*.json") {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            s_watcher.Changed += OnConfigFileChanged;
            s_watcher.Created += OnConfigFileChanged;
            s_watcher.Renamed += (s, e) => OnConfigFileChanged(s, e);

            Logger.Information("CrownShop: Live hot-reload watcher enabled for {0}.", Logger.Args(s_crownShopConfigPath));
        } catch(Exception e) {
            Logger.Warning("Could not enable CrownShop auto-reload watcher: {0}", Logger.Args(e.Message));
        }
    }

    private static void OnConfigFileChanged(object sender, FileSystemEventArgs e) {
        if ((DateTime.UtcNow - s_lastReloadTime).TotalMilliseconds < 500) {
            return;
        }

        s_lastReloadTime = DateTime.UtcNow;
        Logger.Information("CrownShop file changed. Reloading...");
        Load();
    }

    /// <summary>
    /// The packed list for a wizard of this level: every item, with the level version of gear and bundles for them.
    /// Wizards whose level picks the same versions share one packed list.
    /// </summary>
    public static ByteString GetCrownShopData(int level) {
        // Two levels pick the same versions when no version starts between them.
        var key = s_versionLevels.LastOrDefault(start => start <= level, -1);
        lock (s_reloadLock) {
            if (s_dataByVersions.TryGetValue(key, out var cached)) {
                return cached;
            }

            var listed = s_crownShopTabs.SelectMany(t => t.Items).Select(i => i.TemplateId).ToHashSet();
            var spiral = s_shownToAll.Where(item => listed.Contains(item.m_itemTemplateId)).ToList();
            var extras = s_shownToAll.Where(item => !listed.Contains(item.m_itemTemplateId))
                .Concat(s_versioned.Select(group => s_items[group.ForLevel(level).TemplateId]))
                .ToList();

            // A list too big for the game is cut down, the game file items last in first out, until it fits.
            var take = extras.Count;
            var data = Serialize([.. spiral, .. extras]);
            while (data is { } packed && packed.Length > s_maxListBytes && take > 0) {
                take = take * 3 / 4;
                data = Serialize([.. spiral, .. extras.Take(take)]);
            }

            if (take < extras.Count) {
                Logger.Warning("CrownShop: the list for level {0} was over ShopListBytes ({1}), so {2} of {3} game file items are shown.",
                    Logger.Args(level, s_maxListBytes, take, extras.Count));
            }
            Logger.Information("CrownShop: list for level {0} has {1} items ({2} bytes).",
                Logger.Args(level, spiral.Count + take, data?.Length ?? 0));

            s_dataByVersions[key] = data ?? new ByteString();

            return s_dataByVersions[key];
        }
    }

    private static ByteString? Serialize(List<CrownShopItem> items) {
        var crownShopData = new CrownShopData() {
            m_items = items,
            m_crownShopLayout = s_layout,
            m_wishlistMaxSize = (int) s_wishlistMaxSize,
            m_wishlistSBExpansionSize = (int) s_wishlistSBExpansionSize,
            // Probably has something todo with MSG_PCS_SEGDATA_RESPONSE
            m_crownShopSegReqsSummary = new(),
            // todo: recommended items?
            m_recomendedItems = new LevelData()
        };

        var serializer = new ObjectSerializer(
            Versionable: false,
            Behaviors: SerializerFlags.SerializeFlags | SerializerFlags.Compress
        );

        var propertyFlags = PropertyFlags.Prop_Save | PropertyFlags.Prop_Public;
        if (!serializer.Serialize(crownShopData, propertyFlags, out var serializedData)) {
            Logger.Error("Failed to serialize CrownShopData");
            return null;
        }

        return serializedData;
    }

    private static CrownShopItem ToShopItem(CrownShopGameItems.Entry entry) => new() {
        m_itemTemplateId = entry.TemplateId,
        m_itemFlags = 0,
        m_goldCost = entry.GoldCost,
        m_strikethruGold = 0,
        m_crownsCost = entry.CrownsCost,
        m_strikethruCrowns = 0,
        m_ticketCost = 0,
        m_displayPriority = $"{entry.Category}:0",
        m_combatOnly = false,
        m_noGift = entry.NoGift,
        m_recommendIfOwned = false,
        m_saleID = 0,
        m_description = "",
        m_segReqsPoolsStatements = [],
        m_segReqsStatement = "",
    };

    public static List<CrownShopItem> GetCrownShopItems() => s_items.Values.ToList();
    public static bool TryGetCrownShopItem(ulong id, out CrownShopItem item)
        => s_items.TryGetValue(id, out item);

}
