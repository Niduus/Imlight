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
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>
/// The game's vendors and trainers for the Manager's Vendors &amp; Trainers search (Niduus, 2026-10-25: "add to manager
/// quests, vendor and trainer searches"), written to vendors.json beside ids.csv: each shop keeper and trainer with its
/// name, where it stands (spiraldb-extra/Shops/npc-zones.json, from Wizard404's zone lists), its shops (window, title,
/// where the list comes from, items and recipes, in the order the shop itself picks them: InteractVendorComponent),
/// its treasure cards and the spells it teaches (with the level each needs).
/// </summary>
internal static class VendorGuide {

    internal sealed record Shop(string Window, string Title, string From, List<uint> Items, List<string> Recipes);
    internal sealed record Spell(uint Id, int Level);
    internal sealed record Npc(uint Id, string Name, string Object, string Kind, string Test, List<string[]> Zones,
        List<Shop> Shops, List<string> TreasureCards, List<Spell> Spells);

    private const string FILE = "vendors.json";
    private const string ZONES_FILE = "Shops/npc-zones.json";

    private static readonly JsonSerializerOptions s_write = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Works the list out and writes it (IdCatalog runs it after the server's start).</summary>
    internal static void Write(string folder) {
        try {
            var (zoneNames, zonesOf) = LoadZones();
            var files = CoreObjectFactory.TemplateManifest.m_serializedTemplates.Where(t => t is not null)
                .GroupBy(t => (uint) t.m_id).ToDictionary(g => g.Key, g => (string) g.First().m_filename);

            // Every NPC that may sell or teach: the placed ones (their template says whether they keep a shop) and the
            // ones some list names.
            var candidates = new HashSet<uint>(zonesOf.Keys);
            candidates.UnionWith(SpiralDB.NpcInventories.Keys.Select(id => (uint) id));
            candidates.UnionWith(SpiralDB.NpcSpellInventories.Keys.Select(id => (uint) id));
            candidates.UnionWith(SpiralDB.TreasureCardInventories.Keys.Select(id => (uint) id));

            var npcs = new List<Npc>();
            foreach (var id in candidates.Order()) {
                try {
                    if (!files.TryGetValue(id, out var file) || CoreObjectFactory.LoadTemplateFile(file) is not GameObjectTemplate template
                        || Describe(id, template, zonesOf, zoneNames) is not { } npc) {
                        continue;
                    }

                    npcs.Add(npc);
                }
                catch (Exception e) {
                    Logger.Debug("Vendors and trainers: skipped {0}: {1}", Logger.Args(id, e.Message));
                }
            }

            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, FILE);
            File.WriteAllText(path + ".saving", JsonSerializer.Serialize(npcs, s_write));
            File.Move(path + ".saving", path, overwrite: true);
            Logger.Information("Vendors and trainers for the Manager: {0} vendors, {1} trainers.",
                Logger.Args(npcs.Count(n => n.Shops.Count + n.TreasureCards.Count > 0), npcs.Count(n => n.Spells.Count > 0)));
        }
        catch (Exception e) {
            Logger.Warning("Vendors and trainers for the Manager could not be written: {0}", Logger.Args(e.Message));
        }
    }

    // One NPC, or null when it neither sells nor teaches.
    private static Npc Describe(uint id, GameObjectTemplate template, Dictionary<uint, int[]> zonesOf, List<string[]> zoneNames) {
        if (template.m_behaviors is null || !template.m_behaviors.Any(b => b is NPCBehaviorTemplate)) {
            return null;
        }

        var shops = new List<Shop>();
        var own = template.m_behaviors.OfType<WizShoppingBehaviorTemplate>().FirstOrDefault();
        string Title(string title) => string.IsNullOrWhiteSpace(title) ? "" : title;
        if (InteractVendorComponent.IsRecipeShop(template)) {
            shops.Add(new Shop("Recipe", Title(own.m_shopTitle), "Game files (recipe list)", [], [.. own.m_recipeList.Select(r => (string) r)]));
        }
        else if (SpiralDB.IsExtraNpcInventory(id) && SpiralDB.NpcInventories.TryGetValue(id, out var extra)) {
            shops.Add(new Shop(WindowName(own?.m_furnitureShop ?? 0), Title(extra.ShopTitle ?? own?.m_shopTitle), "Our own list",
                [.. extra.Inventory.Select(g => g.MParts.TemplateId)], []));
        }
        else if (InteractVendorComponent.OwnShopList(template) is { } list) {
            shops.Add(new Shop(WindowName(own.m_furnitureShop), Title(own.m_shopTitle), "Game files", [.. list.Select(i => (uint) i)], []));
        }
        else if (LiveShopLists.For(id) is { Count: > 0 } live) {
            shops.AddRange(live.Select(s => new Shop(WindowName(s.Kind), Title(s.Title), "Wizard404 (live game)", [.. s.Items], [.. s.Recipes])));
        }
        else if (SpiralDB.NpcInventories.TryGetValue(id, out var old)) {
            shops.Add(new Shop(WindowName(own?.m_furnitureShop ?? 0), Title(old.ShopTitle ?? own?.m_shopTitle), "SpiralDB (old copy)",
                [.. old.Inventory.Select(g => g.MParts.TemplateId)], []));
        }

        var cards = SpiralDB.TreasureCardInventories.TryGetValue(id, out var tc)
            ? tc.TreasureCards.Select(c => c.SpellName).Where(n => !string.IsNullOrWhiteSpace(n)).ToList() : [];

        var spells = new List<Spell>();
        var test = InteractTrainerComponent.TestTrainerKindOf(template);
        if (test is { } kind) {
            spells.AddRange(InteractTrainerComponent.TestSpellsOf(kind).Select(s => new Spell((uint) s.TemplateID, s.Level)));
        }
        else if (SpiralDB.NpcSpellInventories.TryGetValue(id, out var taught) && taught.Spells is not null) {
            spells.AddRange(taught.Spells.Select(s => new Spell((uint) s.TemplateID, s.Level)));
        }
        else if (TradeTrainers.For(id) is { } trade) {
            spells.AddRange(trade.Spells.Select(s => new Spell(s, 0)));
        }
        else if (Gardening.GardenLessons.IsTrainer(id)) {
            spells.AddRange(Gardening.GardenLessons.ForTrainer(id).Select(l => new Spell(l.SpellId, l.Level)));
        }

        spells = [.. spells.DistinctBy(s => s.Id)];
        var sells = shops.Count > 0 || cards.Count > 0;
        if (!sells && spells.Count == 0) {
            return null;
        }

        string displayKey = template.m_displayName;
        string objectName = template.m_objectName;
        var name = !string.IsNullOrEmpty(displayKey) && Locale.GetEnglishName(displayKey) is { Length: > 0 } shown ? shown : objectName ?? "";
        var zones = zonesOf.TryGetValue(id, out var at) ? at.Where(i => i >= 0 && i < zoneNames.Count).Select(i => zoneNames[i]).ToList() : [];

        return new Npc(id, name, objectName ?? "", sells && spells.Count > 0 ? "Vendor and trainer" : sells ? "Vendor" : "Trainer",
            test?.ToString() ?? "", zones, shops, cards, spells);
    }

    private static string WindowName(int kind) => HouseVendors.WindowOf(kind) switch {
        HouseVendors.ShopWindow.Snack => "Pet snack",
        HouseVendors.ShopWindow.Furniture => "Furniture (housing)",
        var window => window.ToString(),
    };

    // Where each NPC stands: the zones ([zone, its name]) and each NPC's zone numbers.
    private static (List<string[]> Names, Dictionary<uint, int[]> Of) LoadZones() {
        var names = new List<string[]>();
        var of = new Dictionary<uint, int[]>();
        var path = Path.Combine(SpiralDB.ExtraPath, ZONES_FILE);
        try {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var zone in document.RootElement.GetProperty("Zones").EnumerateArray()) {
                names.Add([zone[0].GetString() ?? "", zone[1].GetString() ?? ""]);
            }

            foreach (var npc in document.RootElement.GetProperty("Npcs").EnumerateObject()) {
                if (uint.TryParse(npc.Name, out var id)) {
                    of[id] = [.. npc.Value.EnumerateArray().Select(i => i.GetInt32())];
                }
            }
        }
        catch (Exception e) {
            Logger.Warning("Vendors and trainers: {0} could not be read, so no places: {1}", Logger.Args(path, e.Message));
        }

        return (names, of);
    }

}
