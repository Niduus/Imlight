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
 * WOODEN CHEST LOOT
 * ========================================================================
 *
 * PURPOSE:
 * What a wooden chest in the world gives: gold, and the basic drops of its world (reagents, pet snacks and
 * treasure cards). The lists come from spiraldb-extra/WoodenChests/WoodenChests.json (made from Wizard404 by
 * tools/wooden-chests/make_wooden_chests.py); how often each kind drops and how much gold from Imlight.ini
 * [WoodenChests]. Both are read again when they change, so a change applies from the next chest.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Imlight.Common;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.DropTables;

internal static class WoodenChestLoot {

    private const string SECTION = "WoodenChests";

    private sealed record Pick(string Id, string Name, int Weight);

    private sealed record WorldDrops(string World, int Tier, IReadOnlyList<Pick> Reagents, IReadOnlyList<Pick> Snacks,
        IReadOnlyList<Pick> TreasureCards);

    private sealed record Chest(string Name, string World, IReadOnlyList<string> Tables);

    private sealed record Data(IReadOnlyDictionary<ulong, Chest> Chests, IReadOnlyDictionary<string, IReadOnlyList<Pick>> CardTables,
        IReadOnlyDictionary<string, string> Fallbacks, IReadOnlyDictionary<string, WorldDrops> Worlds);

    private static readonly object s_lock = new();
    private static Data s_data;
    private static DateTime s_written;
    private static DateTime s_checked;
    private static long s_checkedTicks;

    private static bool Setting(string key, bool fallback) => ConfigurationManager.GetValue($"{SECTION}.{key}", fallback);
    private static int Setting(string key, int fallback) => ConfigurationManager.GetValue($"{SECTION}.{key}", fallback);
    private static double Setting(string key, double fallback) => ConfigurationManager.GetValue($"{SECTION}.{key}", fallback);

    private static string FilePath => Path.Combine(SpiralDB.ExtraPath, "WoodenChests", "WoodenChests.json");

    /// <summary>True when the template is one of the world's wooden chests.</summary>
    internal static bool IsWoodenChest(ulong templateId) => Load().Chests.ContainsKey(templateId);

    /// <summary>
    /// Rolls one chest: gold for its world, then a chance at a reagent, a pet snack and a treasure card of its world.
    /// </summary>
    /// <param name="templateId">The chest's template.</param>
    /// <param name="zonePath">The zone it stands in, such as WizardCity/WC_Ravenwood; its first part names the world.</param>
    internal static DropTableResult Roll(ulong templateId, string zonePath) {
        var data = Load();
        var random = Random.Shared;
        data.Chests.TryGetValue(templateId, out var chest);
        var world = FindWorld(data, zonePath, chest);
        var result = new DropTableResult { DropTableId = $"Wooden chest ({world?.World ?? "no world"})" };

        // Gold: the range below, larger by GoldPerWorld percent for every world further along the Spiral.
        var minGold = Math.Max(0, Setting("MinGold", 25));
        var maxGold = Math.Max(minGold, Setting("MaxGold", 100));
        var scale = 1 + Math.Max(0, Setting("GoldPerWorld", 50.0)) / 100 * (world?.Tier ?? 0);
        result.GoldAmount = (int) Math.Round(random.Next(minGold, maxGold + 1) * scale);

        if (world is null) {
            return result;
        }

        if (Chance(Setting("ReagentChance", 60.0)) && PickOne(world.Reagents) is { } reagent) {
            var min = Math.Max(1, Setting("ReagentMin", 1));
            Add(result, reagent, random.Next(min, Math.Max(min, Setting("ReagentMax", 3)) + 1));
        }

        if (Chance(Setting("SnackChance", 35.0)) && PickOne(world.Snacks) is { } snack) {
            Add(result, snack, 1);
        }

        if (Chance(Setting("TreasureCardChance", 50.0))) {
            // The older chests name the game's own treasure card table (TC-Commons and the like); the rest take the world's.
            var gameTable = Setting("UseGameCardTables", true) && chest is not null
                ? chest.Tables.Select(name => data.CardTables.GetValueOrDefault(name)).FirstOrDefault(list => list is { Count: > 0 })
                : null;
            if (PickOne(gameTable ?? world.TreasureCards) is { } card) {
                Add(result, card, 1);
            }
        }

        return result;
    }

    // The zone's world, else the world the chest's name belongs to, then a stand-in for worlds without drops.
    private static WorldDrops FindWorld(Data data, string zonePath, Chest chest) {
        var zoneWorld = zonePath?.Split('/', '\\')[0] ?? "";
        foreach (var name in new[] { zoneWorld, chest?.World ?? "" }) {
            var current = name;
            for (var hops = 0; hops < 4 && !string.IsNullOrEmpty(current); hops++) {
                if (data.Worlds.TryGetValue(current, out var world) && world.Reagents.Count + world.Snacks.Count + world.TreasureCards.Count > 0) {
                    return world;
                }

                current = data.Fallbacks.GetValueOrDefault(current);
            }
        }

        return data.Worlds.GetValueOrDefault("WizardCity");
    }

    private static bool Chance(double percent) => Random.Shared.NextDouble() * 100 < percent;

    private static Pick PickOne(IReadOnlyList<Pick> picks) {
        if (picks is not { Count: > 0 }) {
            return null;
        }

        var total = picks.Sum(pick => Math.Max(1, pick.Weight));
        var roll = Random.Shared.Next(total);
        foreach (var pick in picks) {
            roll -= Math.Max(1, pick.Weight);
            if (roll < 0) {
                return pick;
            }
        }

        return picks[^1];
    }

    private static void Add(DropTableResult result, Pick pick, int quantity)
        => result.Items.Add(new DropItemResult { ItemId = pick.Id, ItemName = pick.Name, Quantity = quantity });

    private static Data Load() {
        // Every object of every zone asks this while it loads; most of the time the answer is already here.
        if (Volatile.Read(ref s_data) is { } data && DateTime.UtcNow - new DateTime(Volatile.Read(ref s_checkedTicks), DateTimeKind.Utc) < TimeSpan.FromSeconds(3)) {
            return data;
        }

        lock (s_lock) {
            if (s_data is not null && DateTime.UtcNow - s_checked < TimeSpan.FromSeconds(3)) {
                return s_data;
            }

            s_checked = DateTime.UtcNow;
            Volatile.Write(ref s_checkedTicks, s_checked.Ticks);
            var path = FilePath;
            var written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (s_data is not null && written == s_written) {
                return s_data;
            }

            s_written = written;
            s_data = Read(path);

            return s_data;
        }
    }

    private static Data Read(string path) {
        var empty = new Data(new Dictionary<ulong, Chest>(), new Dictionary<string, IReadOnlyList<Pick>>(),
            new Dictionary<string, string>(), new Dictionary<string, WorldDrops>());
        if (!File.Exists(path)) {
            Logger.Warning("Wooden chests: {Path} is missing; chests give gold only.", Logger.Args(path));

            return empty;
        }

        try {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            var chests = new Dictionary<ulong, Chest>();
            foreach (var entry in root.GetProperty("Chests").EnumerateObject()) {
                if (ulong.TryParse(entry.Name, out var id)) {
                    chests[id] = new Chest(Text(entry.Value, "Name"), Text(entry.Value, "World"),
                        entry.Value.TryGetProperty("Tables", out var tables) ? [.. tables.EnumerateArray().Select(t => t.GetString() ?? "")] : []);
                }
            }

            var cardTables = new Dictionary<string, IReadOnlyList<Pick>>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("TreasureCardTables", out var tableList)) {
                foreach (var table in tableList.EnumerateObject()) {
                    cardTables[table.Name] = [.. table.Value.EnumerateArray().Select(card => new Pick(card.GetString() ?? "", "", 1))];
                }
            }

            var fallbacks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("Fallbacks", out var fallbackList)) {
                foreach (var fallback in fallbackList.EnumerateObject()) {
                    fallbacks[fallback.Name] = fallback.Value.GetString() ?? "";
                }
            }

            var worlds = new Dictionary<string, WorldDrops>(StringComparer.OrdinalIgnoreCase);
            foreach (var world in root.GetProperty("Worlds").EnumerateArray()) {
                var name = Text(world, "World");
                worlds[name] = new WorldDrops(name, world.TryGetProperty("Tier", out var tier) ? tier.GetInt32() : 0,
                    Picks(world, "Reagents"), Picks(world, "Snacks"), Picks(world, "TreasureCards"));
            }

            Logger.Information("Wooden chests: {Chests} chests and the drops of {Worlds} worlds read from {Path}.",
                Logger.Args(chests.Count, worlds.Count, path));

            return new Data(chests, cardTables, fallbacks, worlds);
        }
        catch (Exception exception) {
            Logger.Error("Wooden chests: could not read {Path}: {Reason}", Logger.Args(path, exception.Message));

            return s_data ?? empty;
        }
    }

    private static string Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static List<Pick> Picks(JsonElement world, string name)
        => world.TryGetProperty(name, out var list)
            ? [.. list.EnumerateArray().Select(pick => new Pick(
                pick.GetProperty("Id").ValueKind == JsonValueKind.Number ? pick.GetProperty("Id").GetUInt64().ToString() : Text(pick, "Id"),
                Text(pick, "Name"),
                pick.TryGetProperty("Weight", out var weight) ? weight.GetInt32() : 1))]
            : [];

}
