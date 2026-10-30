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
 * ITEM SOURCES
 * ========================================================================
 *
 * PURPOSE:
 * Answers the game's "where can I get this item" question (MSG_GETITEMINFO, sent by the item window that opens when
 * an item is identified in a house): the shops that sell it, the creatures that drop it and the quests that give it.
 *
 * USAGE EXAMPLE:
 * var info = ItemSources.InfoFor(templateId); // MSG_ITEMIDENTIFIERINFO
 *
 * NOTE:
 * Read from the game (WizardGraphicalClient.exe, Wizard_1_610, 2026-10-24):
 * - ItemDetailWindow sends MSG_GETITEMINFO{ItemID = the item's template id} (0x140937ed0) and asks for the crown shop
 *   data (MSG_PCS_SEGDATA_REQUEST); it fills in only once both answers came (0x140936100, 0x1409367f0).
 * - MSG_ItemIdentifierInfo: when IsAvailable is 0 Locations is not read. Otherwise Locations is a serialized
 *   ItemLocatorList, not versionable and without serializer flags (0x140943ef0), and every m_locations entry is
 *   drawn by its m_itemType: shop (WizItemVendorDataList, 0x140939770), quest (WizItemQuestList, 0x14093d0b0), boss
 *   (WizItemBossList, 0x14093b140), gardening, gold chest. The three flags add the mini-game, fishing and
 *   Monstrology lines.
 * - A shop keeper or boss is named from the game's own VendorLocationData.xml and BossLocationData.xml (template id to
 *   zone and world); one missing there is left out by the game, so only those are sent.
 * - A quest line shows m_sQuestTitle and m_sFirstGoalLocation through the game's text table, so both are text keys.
 *
 * TODO:
 * - Gardening and gold chest sources.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.Housing;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.World;

/// <summary>
/// Where an item can be had, for the game's item window.
/// </summary>
internal static class ItemSources {

    private const int MaxPerKind = 20;
    private static readonly TimeSpan Rebuild = TimeSpan.FromMinutes(10);
    private static readonly object s_lock = new();
    private static Index s_index;
    private static DateTime s_builtUtc = DateTime.MinValue;

    private sealed record Index(
        Dictionary<uint, List<uint>> Vendors,
        Dictionary<uint, List<uint>> Bosses,
        Dictionary<uint, List<QuestTemplate>> Quests);

    /// <summary>
    /// The answer to MSG_GETITEMINFO for an item.
    /// </summary>
    /// <param name="templateId">The item's template id.</param>
    /// <returns>The message; IsAvailable 0 when the server knows of no source.</returns>
    internal static WIZARD2_53_PROTOCOL.MSG_ITEMIDENTIFIERINFO InfoFor(uint templateId) {
        var list = ListFor(templateId);
        if (list.m_locations.Count == 0) {
            Logger.Information("Item sources: none known for item {TemplateId}.", Logger.Args(templateId));

            return new WIZARD2_53_PROTOCOL.MSG_ITEMIDENTIFIERINFO { IsAvailable = 0, Locations = string.Empty };
        }

        try {
            var info = new WIZARD2_53_PROTOCOL.MSG_ITEMIDENTIFIERINFO { IsAvailable = 1, Locations = HousingBlobs.Serialize(list) };
            Logger.Information("Item sources: item {TemplateId} has {Kinds}.", Logger.Args(templateId,
                string.Join(", ", list.m_locations.Select(location => location.m_itemType))));

            return info;
        }
        catch (InvalidOperationException exception) {
            Logger.Error("Item sources: item {TemplateId} could not be sent: {Reason}", Logger.Args(templateId, exception.Message));

            return new WIZARD2_53_PROTOCOL.MSG_ITEMIDENTIFIERINFO { IsAvailable = 0, Locations = string.Empty };
        }
    }

    /// <summary>
    /// The sources of an item as the game's ItemLocatorList.
    /// </summary>
    /// <param name="templateId">The item's template id.</param>
    /// <returns>The list; m_locations is empty when no source is known.</returns>
    internal static ItemLocatorList ListFor(uint templateId) {
        var index = Current();
        var list = new ItemLocatorList { m_locations = [], m_sUrl = string.Empty };

        if (index.Vendors.TryGetValue(templateId, out var vendors)) {
            list.m_locations.Add(new WizItemVendorDataList {
                m_itemType = eItemType.shop,
                m_vendors = [.. vendors.Take(MaxPerKind).Select(id => new WizItemVendorData { m_shopKeeperID = id, m_shopType = 0 })],
            });
        }

        if (index.Quests.TryGetValue(templateId, out var quests)) {
            list.m_locations.Add(new WizItemQuestList {
                m_itemType = eItemType.quest,
                m_quests = [.. quests.Take(MaxPerKind).Select(quest => new WizItemQuestData {
                    m_sQuestTitle = (string) quest.m_questTitle ?? string.Empty,
                    m_sFirstGoalLocation = FirstGoalLocation(quest),
                    m_sSchoolFocus = string.Empty,
                })],
            });
        }

        if (index.Bosses.TryGetValue(templateId, out var bosses)) {
            list.m_locations.Add(new WizItemBossList {
                m_itemType = eItemType.boss,
                m_bosses = [.. bosses.Take(MaxPerKind)],
            });
        }

        return list;
    }

    private static string FirstGoalLocation(QuestTemplate quest)
        => quest.m_goals?.Select(goal => (string) goal?.m_locationName).FirstOrDefault(name => !string.IsNullOrEmpty(name)) ?? string.Empty;

    private static Index Current() {
        lock (s_lock) {
            if (s_index is null || DateTime.UtcNow - s_builtUtc > Rebuild) {
                s_index = Build();
                s_builtUtc = DateTime.UtcNow;
            }

            return s_index;
        }
    }

    private static Index Build() {
        var vendorIds = LocationIds("VendorLocationData.xml");
        var bossIds = LocationIds("BossLocationData.xml");

        var vendors = new Dictionary<uint, List<uint>>();
        foreach (var (npcId, inventory) in SpiralDB.NpcInventories) {
            if (vendorIds is not null && !vendorIds.Contains((uint) npcId)) {
                continue;
            }

            foreach (var item in inventory.Inventory ?? []) {
                AddTo(vendors, (uint) item.Full, (uint) npcId);
            }
        }

        var bosses = new Dictionary<uint, List<uint>>();
        var tableItems = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (npcId, drops) in SpiralDB.NpcDropTables) {
            if (bossIds is not null && !bossIds.Contains((uint) npcId)) {
                continue;
            }

            foreach (var name in drops.DropTableNames ?? []) {
                foreach (var item in ItemsOf(name, tableItems)) {
                    AddTo(bosses, item, (uint) npcId);
                }
            }
        }

        var quests = new Dictionary<uint, List<QuestTemplate>>();
        foreach (var quest in SpiralDB.QuestTemplates) {
            if (quest is null || quest.m_outdated || quest.m_endResults?.m_results is not { } results) {
                continue;
            }

            foreach (var name in results.OfType<ResDropTable>().Select(result => (string) result.m_tableName)) {
                foreach (var item in ItemsOf(name, tableItems)) {
                    AddTo(quests, item, quest);
                }
            }
        }

        Logger.Information("Item sources: {Vendors} items sold, {Bosses} dropped by bosses, {Quests} given by quests{Note}.",
            Logger.Args(vendors.Count, bosses.Count, quests.Count,
                vendorIds is null || bossIds is null ? " (a location file of the game could not be read, so nothing is filtered by it)" : ""));

        return new Index(vendors, bosses, quests);
    }

    internal const string GUIDE_NAME = "item-sources.json";

    private sealed record GuideNpc(uint Id, string Name, List<string> Zones, bool Boss);

    private sealed record GuideQuest(string Code, string Title, string Zone);

    private sealed record GuideItem(List<int> Drops, List<int> Quests);

    private sealed record GuideFile(List<GuideNpc> Npcs, List<GuideQuest> Quests, Dictionary<uint, GuideItem> Items);

    /// <summary>
    /// Writes item-sources.json beside ids.csv for the Manager's Best Gear tab (Niduus, 2026-10-29: "If the player wants
    /// to play legit, that's fine, the recommended gear will show them were to obtain it"): for every item a creature
    /// drops or a quest gives, which ones, with where each creature stands (spiraldb-extra/Shops/npc-zones.json) and
    /// whether the game lists it as a boss. Shops are in vendors.json already.
    /// </summary>
    internal static void WriteGuide(string folder) {
        try {
            var bossIds = LocationIds("BossLocationData.xml") ?? [];
            var (zoneNames, zonesOf) = Imlight.CoreLib.Game.Tools.VendorGuide.LoadZones();
            var tableItems = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
            var npcs = new List<GuideNpc>();
            var quests = new List<GuideQuest>();
            var items = new Dictionary<uint, GuideItem>();
            GuideItem ItemEntry(uint id) => items.TryGetValue(id, out var known) ? known : items[id] = new GuideItem([], []);

            foreach (var (npcId, drops) in SpiralDB.NpcDropTables.OrderBy(p => p.Key)) {
                var found = (drops.DropTableNames ?? []).SelectMany(name => ItemsOf(name, tableItems)).Distinct().ToList();
                if (found.Count == 0) {
                    continue;
                }

                var id = (uint) npcId;
                var name = "";
                if (CoreObjectFactory.GetCoreTemplate(npcId) is GameObjectTemplate template) {
                    string key = template.m_displayName;
                    name = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);
                    if (string.IsNullOrEmpty(name)) {
                        name = (string) template.m_objectName ?? "";
                    }
                }

                var zones = zonesOf.TryGetValue(id, out var at)
                    ? at.Where(i => i >= 0 && i < zoneNames.Count).Select(i => zoneNames[i][0] + "|" + zoneNames[i][1]).Distinct().Take(4).ToList()
                    : [];
                var index = npcs.Count;
                npcs.Add(new GuideNpc(id, name, zones, bossIds.Contains(id)));
                foreach (var item in found) {
                    ItemEntry(item).Drops.Add(index);
                }
            }

            foreach (var quest in SpiralDB.QuestTemplates.Where(q => q is not null && !q.m_outdated && !string.IsNullOrEmpty(q.m_questName))
                         .GroupBy(q => (string) q.m_questName, StringComparer.OrdinalIgnoreCase).Select(g => g.First())) {
                var found = (quest.m_endResults?.m_results ?? []).OfType<ResDropTable>()
                    .SelectMany(r => ItemsOf(r.m_tableName, tableItems)).Distinct().ToList();
                if (found.Count == 0) {
                    continue;
                }

                string titleKey = quest.m_questTitle;
                var title = string.IsNullOrEmpty(titleKey) ? "" : Locale.GetEnglishName(titleKey);
                var index = quests.Count;
                quests.Add(new GuideQuest(quest.m_questName, string.IsNullOrEmpty(title) ? quest.m_questName : title, FirstGoalLocation(quest)));
                foreach (var item in found) {
                    ItemEntry(item).Quests.Add(index);
                }
            }

            Directory.CreateDirectory(folder);
            var path = System.IO.Path.Combine(folder, GUIDE_NAME);
            File.WriteAllText(path + ".tmp", System.Text.Json.JsonSerializer.Serialize(new GuideFile(npcs, quests, items)));
            File.Move(path + ".tmp", path, overwrite: true);
            Logger.Information("Item sources for the Manager: {0} items, from {1} creatures and {2} quests.",
                Logger.Args(items.Count, npcs.Count, quests.Count));
        }
        catch (Exception exception) {
            Logger.Warning("Item sources for the Manager could not be written: {0}", Logger.Args(exception.Message));
        }
    }

    internal const string TABLE_USERS_NAME = "drop-table-users.json";

    private sealed record UserNpc(uint Id, string Name, List<string> Zones, bool Boss);

    private sealed record UserQuest(string Code, string Title);

    private sealed record TableUsers(List<int> Creatures, List<int> Quests);

    private sealed record TableUsersFile(List<UserNpc> Creatures, List<UserQuest> Quests, Dictionary<string, TableUsers> Tables);

    /// <summary>
    /// Writes drop-table-users.json beside ids.csv for the Manager's drop table search (Niduus, 2026-10-30: "become
    /// transparent about drop tables about from which mob/boss when searched"): for every drop table, the creatures that
    /// drop it (with where they stand and whether the game lists them as a boss) and the quests that give it.
    /// </summary>
    internal static void WriteTableUsers(string folder) {
        try {
            var bossIds = LocationIds("BossLocationData.xml") ?? [];
            var (zoneNames, zonesOf) = Imlight.CoreLib.Game.Tools.VendorGuide.LoadZones();
            var creatures = new List<UserNpc>();
            var quests = new List<UserQuest>();
            var tables = new Dictionary<string, TableUsers>(StringComparer.OrdinalIgnoreCase);
            TableUsers Entry(string table) => tables.TryGetValue(table, out var known) ? known : tables[table] = new TableUsers([], []);

            foreach (var (npcId, drops) in SpiralDB.NpcDropTables.OrderBy(p => p.Key)) {
                var names = (drops.DropTableNames ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (names.Count == 0) {
                    continue;
                }

                var id = (uint) npcId;
                var name = "";
                if (CoreObjectFactory.GetCoreTemplate(npcId) is GameObjectTemplate template) {
                    string key = template.m_displayName;
                    name = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);
                    if (string.IsNullOrEmpty(name)) {
                        name = (string) template.m_objectName ?? "";
                    }
                }

                var zones = zonesOf.TryGetValue(id, out var at)
                    ? at.Where(i => i >= 0 && i < zoneNames.Count).Select(i => zoneNames[i][0] + "|" + zoneNames[i][1]).Distinct().Take(3).ToList()
                    : [];
                var index = creatures.Count;
                creatures.Add(new UserNpc(id, name, zones, bossIds.Contains(id)));
                foreach (var table in names) {
                    Entry(table).Creatures.Add(index);
                }
            }

            foreach (var quest in SpiralDB.QuestTemplates.Where(q => q is not null && !q.m_outdated && !string.IsNullOrEmpty(q.m_questName))
                         .GroupBy(q => (string) q.m_questName, StringComparer.OrdinalIgnoreCase).Select(g => g.First())) {
                var names = (quest.m_endResults?.m_results ?? []).OfType<ResDropTable>().Select(r => (string) r.m_tableName)
                    .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (names.Count == 0) {
                    continue;
                }

                string titleKey = quest.m_questTitle;
                var title = string.IsNullOrEmpty(titleKey) ? "" : Locale.GetEnglishName(titleKey);
                var index = quests.Count;
                quests.Add(new UserQuest(quest.m_questName, string.IsNullOrEmpty(title) ? quest.m_questName : title));
                foreach (var table in names) {
                    Entry(table).Quests.Add(index);
                }
            }

            Directory.CreateDirectory(folder);
            var path = System.IO.Path.Combine(folder, TABLE_USERS_NAME);
            File.WriteAllText(path + ".tmp", System.Text.Json.JsonSerializer.Serialize(new TableUsersFile(creatures, quests, tables)));
            File.Move(path + ".tmp", path, overwrite: true);
            Logger.Information("Drop table users for the Manager: {0} tables, used by {1} creatures and {2} quests.",
                Logger.Args(tables.Count, creatures.Count, quests.Count));
        }
        catch (Exception exception) {
            Logger.Warning("Drop table users for the Manager could not be written: {0}", Logger.Args(exception.Message));
        }
    }

    private static List<uint> ItemsOf(string tableName, Dictionary<string, List<uint>> cache) {
        if (string.IsNullOrWhiteSpace(tableName)) {
            return [];
        }

        if (!cache.TryGetValue(tableName, out var items)) {
            items = (DropTableFiles.Get(tableName)?.Items ?? [])
                .Select(drop => DropTableConverter.TryParseItemId(drop.ItemId, out var gid) ? (uint) gid.Full : 0)
                .Where(id => id != 0)
                .Distinct()
                .ToList();
            cache[tableName] = items;
        }

        return items;
    }

    private static void AddTo<T>(Dictionary<uint, List<T>> map, uint itemId, T source) {
        if (!map.TryGetValue(itemId, out var sources)) {
            sources = [];
            map[itemId] = sources;
        }

        if (!sources.Contains(source)) {
            sources.Add(source);
        }
    }

    // The template ids of a location file of the game (ObjectLocationList), or null when it cannot be read.
    private static HashSet<uint> LocationIds(string fileName) {
        try {
            using var stream = RootArchiveLoader.GetFileStream(fileName);
            if (stream is null || !new BindSerializer().Deserialize<ObjectLocationList>(stream.ToArray(), 1, out var list) || list is null) {
                Logger.Warning("Item sources: {File} could not be read.", Logger.Args(fileName));

                return null;
            }

            return [.. list.m_objectList.Where(location => location is not null).Select(location => location.m_templateID)];
        }
        catch (Exception exception) {
            Logger.Warning("Item sources: {File} could not be read: {Error}", Logger.Args(fileName, exception.Message));

            return null;
        }
    }

}
