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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.Setup;

/// <summary>One house item (furniture, a decoration, a wall hanging, a plant, a music scroll and the rest).</summary>
internal sealed class FurnitureItem {

    internal required uint Id { get; init; }

    internal required string Name { get; init; }

    /// <summary>The game's own name for the template, such as HOUSE_MS_Scrollcase_Red.</summary>
    internal required string Code { get; init; }

    internal required string File { get; init; }

    /// <summary>What kind of house item it is, such as Furniture, Wall hangings or Castle blocks (see <see cref="FurnitureBook.CATEGORIES"/>).</summary>
    internal required string Category { get; init; }

    /// <summary>The world or set its file sits under, such as Mooshu or School sets.</summary>
    internal required string From { get; init; }

    internal required string Rarity { get; init; }

    /// <summary>The game's FLAG_ adjectives without FLAG_, such as NoAuction and CrownsOnly.</summary>
    internal required IReadOnlyList<string> Flags { get; init; }

    /// <summary>The game's other adjectives, such as Housing, Furniture and Crafted.</summary>
    internal required IReadOnlyList<string> Tags { get; init; }

    /// <summary>The game's behaviour names, such as InteractableBehavior.</summary>
    internal required IReadOnlyList<string> Behaviors { get; init; }

    internal required int GoldCost { get; init; }

    internal required int CrownsCost { get; init; }

    /// <summary>How many one house may hold, or 0 for no limit.</summary>
    internal required int HouseLimit { get; init; }

    internal bool Crafted => Tags.Contains("Crafted", StringComparer.OrdinalIgnoreCase);

    internal bool CrownsOnly => Flags.Contains("CrownsOnly", StringComparer.OrdinalIgnoreCase);

    internal bool Test => Name == Code || Tags.Any(t => t.Equals("EditorOnly", StringComparison.OrdinalIgnoreCase) || t.StartsWith("Test", StringComparison.OrdinalIgnoreCase));

    /// <summary>Something happens when a player presses X at it (a door, a lamp, a game, a teleporter).</summary>
    internal bool Interactive => Has("InteractableBehavior") || Has("BasicObjectStateBehavior");

    internal bool Sound => Behaviors.Any(b => b.Contains("Sound", StringComparison.OrdinalIgnoreCase)) || Has("HousingMusicBehavior");

    internal bool Animated => Has("AnimationBehavior");

    internal string Haystack { get; set; } = "";

    private bool Has(string behavior) => Behaviors.Contains(behavior, StringComparer.OrdinalIgnoreCase);

}

/// <summary>
/// Every house item in the game (Niduus, 2026-10-28: "a furniture browser with a .furniture command for it"), read from
/// ObjectData/Housing in Root.wad: each template with the game's Housing mark, which is what the game lets a player place
/// in a house, and the wallpapers and floors (WallpaperCarpet).
/// </summary>
internal sealed class FurnitureBook {

    /// <summary>The kinds, in the order the filter lists them.</summary>
    internal static readonly string[] CATEGORIES = [
        "Furniture", "Decorations", "Wall hangings", "Wallpapers and floors", "Outdoor", "Plants", "Gardening", "House guests", "Music scrolls",
        "Castle blocks", "Castle Magic", "Trophies", "Fishing", "Raid keys", "House deeds", "Other",
    ];

    // The worlds the game's housing folders are named after.
    private static readonly Dictionary<string, string> s_worlds = new(StringComparer.OrdinalIgnoreCase) {
        ["WC"] = "Wizard City", ["KT"] = "Krokotopia", ["MB"] = "Marleybone", ["MS"] = "Mooshu", ["DS"] = "Dragonspyre",
        ["GH"] = "Grizzleheim", ["CL"] = "Celestia", ["WL"] = "Wintertusk", ["ZF"] = "Zafaria", ["AV"] = "Avalon",
        ["AZ"] = "Azteca", ["AQ"] = "Aquila", ["KR"] = "Khrysalis", ["PL"] = "Polaris", ["MR"] = "Mirage", ["EM"] = "Empyrea",
        ["KM"] = "Karamelle", ["LM"] = "Lemuria", ["NV"] = "Novus", ["DM"] = "Darkmoor", ["SP"] = "Spiral (any world)", ["PA"] = "Wysteria",
        ["School Sets"] = "School sets", ["HouseGuests"] = "House guests", ["HouseGuestsCCG"] = "House guests",
        ["MusicalScrolls"] = "Music scrolls", ["BuildACastleBlocks"] = "Castle blocks", ["BobbleHeads"] = "Bobbleheads",
        ["RaidKeys"] = "Raid keys", ["SkeletonKeys"] = "Skeleton keys", ["CastleMagic"] = "Castle Magic",
        ["MusicalInstruments"] = "Musical instruments", ["DisposableDungeons"] = "Dungeons", ["HousingGauntlets"] = "Gauntlets",
        ["SocketWrenches"] = "Socket wrenches", ["ArtBooks"] = "Art books",
    };

    private const string HOUSING_FOLDER = "ObjectData/Housing/";

    internal IReadOnlyList<FurnitureItem> Items { get; private set; } = [];

    /// <summary>Reads every house item. Takes a few seconds, so run it in the background.</summary>
    internal static FurnitureBook Load(SpellBook book) {
        var items = new ConcurrentBag<FurnitureItem>();
        // A template may live in a WorldData archive (|Dir|WorldData|ObjectData/...), as recipes do, so the folder is
        // matched anywhere in its name.
        var files = book.TemplatesUnder("").Where(t => t.File.Contains(HOUSING_FOLDER, StringComparison.OrdinalIgnoreCase)).ToList();
        System.Threading.Tasks.Parallel.ForEach(files, file => {
            try {
                if (book.DecodeTemplate(file.File) is WizItemTemplate template && Read(book, file.Id, file.File, template) is { } item) {
                    items.Add(item);
                }
            }
            catch (Exception) {
                // A template the reader can't make sense of is left out.
            }
        });

        return new FurnitureBook {
            Items = [.. items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Id)],
        };
    }

    private static FurnitureItem? Read(SpellBook book, uint id, string file, WizItemTemplate template) {
        var adjectives = (template.m_adjectiveList ?? []).Select(a => (string) a ?? "").Where(a => a.Length > 0).ToList();
        // Wallpapers and floors carry WallpaperCarpet instead of Housing.
        if (!adjectives.Contains("Housing", StringComparer.Ordinal) && !adjectives.Contains("WallpaperCarpet", StringComparer.Ordinal)) {
            return null;
        }

        var behaviors = (template.m_behaviors ?? []).Where(b => b is not null).Select(b => (string) b.m_behaviorName ?? "")
            .Where(b => b.Length > 0).Distinct().ToList();
        var tags = adjectives.Where(a => !a.StartsWith("FLAG_", StringComparison.OrdinalIgnoreCase) && !a.EndsWith(".AdjRef", StringComparison.OrdinalIgnoreCase))
            .Distinct().ToList();
        string displayName = template.m_displayName;
        var code = (string) template.m_objectName ?? "";
        var item = new FurnitureItem {
            Id = id,
            Name = book.Text(displayName) is { Length: > 0 } shown ? shown.Trim() : code,
            Code = code,
            File = file,
            Category = CategoryOf(tags, behaviors),
            From = FromOf(file),
            Rarity = template.m_rarity.ToString() is { Length: > 3 } rarity && rarity.StartsWith("RT_", StringComparison.Ordinal)
                ? rarity[3..] switch { "ULTRARARE" => "Ultra rare", var r => char.ToUpperInvariant(r[0]) + r[1..].ToLowerInvariant() }
                : "",
            Flags = [.. adjectives.Where(a => a.StartsWith("FLAG_", StringComparison.OrdinalIgnoreCase)).Select(a => a[5..]).Distinct()],
            Tags = tags,
            Behaviors = behaviors,
            GoldCost = (int) Math.Max(0, template.m_baseCost),
            CrownsCost = (int) Math.Max(0, template.m_creditsCost),
            HouseLimit = (int) Math.Max(0, (long) template.m_itemLimit),
        };
        item.Haystack = SearchText.Normalize($"{item.Name} {item.Code} {item.Id} {item.Category} {item.From} {string.Join(' ', tags)} {string.Join(' ', item.Flags)}");

        return item;
    }

    private static string CategoryOf(List<string> tags, List<string> behaviors) {
        bool Tag(params string[] names) => tags.Any(t => names.Contains(t, StringComparer.OrdinalIgnoreCase));
        bool Behavior(string name) => behaviors.Contains(name, StringComparer.OrdinalIgnoreCase);

        return Behavior("DeedBehavior") ? "House deeds"
            : Tag("CastleBlock") ? "Castle blocks"
            : Tag("CastleMagic") || Behavior("CastleMagicBehavior") ? "Castle Magic"
            : Tag("MusicScroll") ? "Music scrolls"
            : Tag("HouseGuest") ? "House guests"
            : Tag("Seed", "GardeningPlant", "GardeningItem", "GardeningSoil", "GardeningPot", "GardeningPest", "GardeningMultiPot") || Behavior("SeedBehavior") ? "Gardening"
            : Tag("RaidKey") ? "Raid keys"
            : Tag("Trophies") ? "Trophies"
            : Tag("Fishing") || Behavior("AquariumBehavior") ? "Fishing"
            : Behavior("HousingTextureBehavior") || Tag("WallpaperCarpet") ? "Wallpapers and floors"
            : Tag("WallHangings", "WallHanging", "LowWallHanging") ? "Wall hangings"
            : Tag("Outdoor") ? "Outdoor"
            : Tag("Furniture") ? "Furniture"
            : Tag("PlantLife") ? "Plants"
            : Tag("Decoration") ? "Decorations"
            : "Other";
    }

    /// <summary>The world or set of ObjectData/Housing/&lt;folder&gt;/..., or "Other" for a file right in the housing folder.</summary>
    private static string FromOf(string file) {
        var start = file.IndexOf(HOUSING_FOLDER, StringComparison.OrdinalIgnoreCase);
        var rest = start < 0 ? "" : file[(start + HOUSING_FOLDER.Length)..];
        var slash = rest.IndexOf('/');
        if (slash <= 0) {
            return "Other";
        }

        var folder = rest[..slash];

        return s_worlds.GetValueOrDefault(folder, folder);
    }

}
