/*
 * Vendors and trainers of the app's Customs tab (Niduus, 2026-10-23: "Think we can implement custom Vendors/Trainer in
 * the Custom section?"), read from spiraldb-extra/Customs/VendorsTrainers.json. The game files are never changed.
 *
 * The file is a list. Each entry is one NPC:
 *   Id          its number (1, 2, 3...). It gives the NPC's zone tag (CustomNpc_<Id>) and so its global id.
 *   Name        the name over its head.
 *   Kind        Vendor (a shop) or Trainer (a spell list).
 *   TemplateID  its look: any NPC or creature template. It only gives the look: the template's own shop, spells and
 *               quests are not used, and a creature does not start battles.
 *   Zone, X, Y, Z, Yaw  where it stands (the Yaw in radians, as the position tool writes it).
 *   Window      a vendor's shop window: Automatic (picked from the items: snacks, reagents, jewels, recipes or
 *               furniture each have their own window; anything else, or a mix, the equipment shop), Equipment,
 *               Furniture, Snack, Reagent, Jewel or Recipe.
 *   Items       a vendor's items: ID (the item template) and Price (gold, 1 if left out).
 *   Spells      a trainer's spells: ID (the spell template) and Price (training points, 1 if left out).
 *
 * The NPCs are added to their zone's objects by ZoneObjects.cs, which watches this file as it does its own, so an NPC
 * that is added, moved, changed or deleted is so at once in every running copy of its zone. The vendor sells through
 * ShopService and the trainer teaches through TrainService, as the game's own do.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Housing;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.World;

internal static class CustomNpcs {

    internal sealed class CustomNpc {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Kind { get; set; } = VENDOR;
        public ulong TemplateID { get; set; }
        public string Zone { get; set; } = "";
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Yaw { get; set; }
        public string Window { get; set; } = AUTOMATIC;
        public List<Entry> Items { get; set; } = [];
        public List<Entry> Spells { get; set; } = [];

        internal bool IsTrainer => string.Equals(Kind, TRAINER, StringComparison.OrdinalIgnoreCase);

        internal string Tag => TagOf(Id);
    }

    internal sealed class Entry {
        public uint ID { get; set; }
        public int Price { get; set; } = 1;
    }

    internal const string FILE = "VendorsTrainers.json";
    private const string FOLDER = "Customs";
    private const string TAG_PREFIX = "CustomNpc_";
    private const string VENDOR = "Vendor";
    private const string TRAINER = "Trainer";
    private const string AUTOMATIC = "Automatic";

    /// <summary>The look an NPC has when its file names none: Elmer Meadowgrass, the house sellers' shopkeeper.</summary>
    internal const ulong DEFAULT_LOOK = 136831;

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    // The NPCs as last read, by zone tag. ZoneObjects reads the file again whenever it changes.
    private static Dictionary<string, CustomNpc> s_byTag = new(StringComparer.OrdinalIgnoreCase);

    internal static string TagOf(int id) => $"{TAG_PREFIX}{id}";

    internal static bool IsCustomTag(string zoneTag) => zoneTag?.StartsWith(TAG_PREFIX, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The custom NPC of a zone tag, or null.</summary>
    internal static CustomNpc For(string zoneTag)
        => IsCustomTag(zoneTag) && s_byTag.TryGetValue(zoneTag, out var npc) ? npc : null;

    /// <summary>The name over a custom NPC's head, or null.</summary>
    internal static string NameFor(string zoneTag) => For(zoneTag) is { Name.Length: > 0 } npc ? npc.Name : null;

    /// <summary>
    /// Reads the file. ZoneObjects calls it each time it reads its files, and puts the NPCs in their zones.
    /// </summary>
    internal static List<CustomNpc> Load() {
        var path = Path.Combine(SpiralDB.ExtraPath, FOLDER, FILE);
        if (!File.Exists(path)) {
            s_byTag = new(StringComparer.OrdinalIgnoreCase);

            return [];
        }

        List<CustomNpc> npcs;
        try {
            npcs = JsonSerializer.Deserialize<List<CustomNpc>>(File.ReadAllText(path), s_options) ?? [];
        }
        catch (Exception e) {
            // The last good list stays (the app writes the file whole, so this is a file broken by hand).
            Logger.Warning("Custom vendors and trainers: {0} could not be read: {1}", Logger.Args(FILE, e.Message));

            return [.. s_byTag.Values];
        }

        var byTag = new Dictionary<string, CustomNpc>(StringComparer.OrdinalIgnoreCase);
        foreach (var npc in npcs) {
            if (npc is null || npc.Id <= 0 || string.IsNullOrWhiteSpace(npc.Zone)) {
                Logger.Warning("Custom vendors and trainers: an entry has no Id or Zone; skipped.");

                continue;
            }

            if (byTag.ContainsKey(npc.Tag)) {
                Logger.Warning("Custom vendors and trainers: a second NPC has Id {0}; skipped.", Logger.Args(npc.Id));

                continue;
            }

            npc.Name ??= "";
            npc.Zone = npc.Zone.Trim();
            npc.TemplateID = npc.TemplateID == 0 ? DEFAULT_LOOK : npc.TemplateID;
            npc.Items = [.. (npc.Items ?? []).Where(e => e is not null && e.ID != 0).GroupBy(e => e.ID).Select(g => g.First())];
            npc.Spells = [.. (npc.Spells ?? []).Where(e => e is not null && e.ID != 0).GroupBy(e => e.ID).Select(g => g.First())];
            foreach (var entry in npc.Items.Concat(npc.Spells)) {
                entry.Price = Math.Max(0, entry.Price);
            }

            byTag[npc.Tag] = npc;
        }

        s_byTag = byTag;
        Logger.Information("Custom vendors and trainers: {0} read ({1}).",
            Logger.Args(byTag.Count, string.Join(", ", byTag.Values.Select(n => $"{n.Id} {n.Name} {(n.IsTrainer ? TRAINER : VENDOR)} in {n.Zone}"))));

        return [.. byTag.Values];
    }

    /// <summary>
    /// What tells two versions of an NPC apart for ZoneObjects: an NPC whose name, kind, window or lists changed is
    /// taken away and made again, so it opens with the new ones.
    /// </summary>
    internal static string SignatureOf(CustomNpc npc)
        => JsonSerializer.Serialize(new { npc.Name, npc.Kind, npc.Window, npc.Items, npc.Spells });

    /// <summary>The shop window a vendor opens with, and whether it is the furniture (housing) shop.</summary>
    internal static (HouseVendors.ShopWindow Window, bool Furniture) WindowFor(CustomNpc npc) {
        var window = (npc.Window ?? "").Replace(" ", "").ToLowerInvariant();
        switch (window) {
            case "equipment": return (HouseVendors.ShopWindow.Equipment, false);
            case "furniture" or "housing": return (HouseVendors.ShopWindow.Equipment, true);
            case "snack" or "snacks" or "petsnack" or "petsnacks": return (HouseVendors.ShopWindow.Snack, false);
            case "reagent" or "reagents": return (HouseVendors.ShopWindow.Reagent, false);
            case "jewel" or "jewels": return (HouseVendors.ShopWindow.Jewel, false);
            case "recipe" or "recipes": return (HouseVendors.ShopWindow.Recipe, false);
        }

        // Automatic: the window every item belongs in, or the equipment shop for a mix.
        var kinds = npc.Items.Select(e => KindOf(e.ID)).Distinct().ToList();
        if (kinds.Count != 1) {
            return (HouseVendors.ShopWindow.Equipment, false);
        }

        return kinds[0] switch {
            "Snack" => (HouseVendors.ShopWindow.Snack, false),
            "Reagent" => (HouseVendors.ShopWindow.Reagent, false),
            "Jewel" => (HouseVendors.ShopWindow.Jewel, false),
            "Recipe" => (HouseVendors.ShopWindow.Recipe, false),
            "Furniture" => (HouseVendors.ShopWindow.Equipment, true),
            _ => (HouseVendors.ShopWindow.Equipment, false),
        };
    }

    // Which window an item belongs in, the way the house sellers sort them (HouseVendors).
    private static string KindOf(uint id) {
        try {
            var template = CoreObjectFactory.GetCoreTemplate(id);
            switch (template) {
                case PetSnackItemTemplate:
                    return "Snack";
                case ReagentItemTemplate:
                    return "Reagent";
                case RecipeTemplate:
                    return "Recipe";
                case WizItemTemplate when (FileOf(id) ?? "").StartsWith("ObjectData/Jewels/", StringComparison.OrdinalIgnoreCase):
                    return "Jewel";
                case WizItemTemplate item when HousingItems.Classify(item) == HousingItemKind.Furniture:
                    return "Furniture";
            }
        }
        catch (Exception e) {
            Logger.Warning("Custom vendor item {0} could not be read: {1}", Logger.Args(id, e.Message));
        }

        return "Equipment";
    }

    /// <summary>A template by id, or null when there is none or it does not load.</summary>
    internal static CoreTemplate TemplateOf(uint id) {
        try {
            return CoreObjectFactory.GetCoreTemplate(id);
        }
        catch (Exception e) {
            Logger.Warning("Custom vendors and trainers: template {0} could not be read: {1}", Logger.Args(id, e.Message));

            return null;
        }
    }

    /// <summary>A template's file in the template manifest, such as ObjectData/Jewels/..., or null.</summary>
    internal static string FileOf(uint id) {
        var location = CoreObjectFactory.TemplateManifest?.m_serializedTemplates?.FirstOrDefault(l => l is not null && l.m_id == id);
        string file = location?.m_filename;

        return file;
    }

    /// <summary>
    /// The name a recipe window lists a recipe by (WizShoppingBehavior.m_recipeList): its file's name without the
    /// folder and .xml (HouseVendors.RecipesOf reads them the other way).
    /// </summary>
    internal static string RecipeNameOf(uint id) {
        var file = FileOf(id);
        if (string.IsNullOrEmpty(file)) {
            return null;
        }

        var name = file[(file.LastIndexOf('/') + 1)..];

        return name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

}
