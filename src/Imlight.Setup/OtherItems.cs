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
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.Setup;

/// <summary>
/// One item that is not gear and not a house item: a reagent, jewel, pin, pet snack, elixir, henchman, emote,
/// transformation or pack.
/// </summary>
internal sealed class OtherItem {

    internal required uint Id { get; init; }

    internal required string Name { get; init; }

    /// <summary>The game's own name for the template, such as Harvest-Wood-01.</summary>
    internal required string Code { get; init; }

    internal required string File { get; init; }

    /// <summary>What kind of item it is (see <see cref="ItemBook.CATEGORIES"/>).</summary>
    internal required string Category { get; init; }

    /// <summary>The kind inside its kind, such as Circle for a jewel, Rare harvest for a reagent or Dessert for a snack.</summary>
    internal required string Type { get; init; }

    /// <summary>What the game's tooltip says about it, or "".</summary>
    internal required string Description { get; init; }

    internal required string Rarity { get; init; }

    internal required string School { get; init; }

    /// <summary>The level needed to use it, or 0.</summary>
    internal required int Level { get; init; }

    /// <summary>How long it lasts once used (an elixir or transformation), in the game's words such as 7d, or "".</summary>
    internal required string Lasts { get; init; }

    /// <summary>The game's FLAG_ adjectives without FLAG_, such as NoAuction and CrownsOnly.</summary>
    internal required IReadOnlyList<string> Flags { get; init; }

    internal required IReadOnlyList<GearStat> Stats { get; init; }

    internal required IReadOnlyList<GearSpell> Spells { get; init; }

    internal required int GoldCost { get; init; }

    internal required int CrownsCost { get; init; }

    internal bool CrownsOnly => Flags.Contains("CrownsOnly", StringComparer.OrdinalIgnoreCase);

    internal bool Test { get; init; }

    internal string Haystack { get; set; } = "";

    /// <summary>The command that gives it: reagents and snacks go in their own bags, everything else in the backpack.</summary>
    internal string Command => Category switch {
        ItemBook.REAGENTS => $".mod addreagent {Id}",
        ItemBook.SNACKS => $".mod addsnack {Id}",
        _ => $".mod additem {Id}",
    };

}

/// <summary>
/// Every item of the game that the Gear and Furniture tabs don't show (Niduus, 2026-10-28: "Implement more to the
/// Database section"): reagents, jewels and pins, pet snacks, elixirs and potions, henchmen, emotes, transformations
/// and packs, read from ObjectData in Root.wad.
/// </summary>
internal sealed class ItemBook {

    internal const string REAGENTS = "Reagents";
    internal const string JEWELS = "Jewels";
    internal const string PINS = "Pins";
    internal const string SNACKS = "Pet snacks";
    internal const string ELIXIRS = "Elixirs and potions";
    internal const string TRANSFORMATIONS = "Transformations";
    internal const string HENCHMEN = "Henchmen";
    internal const string EMOTES = "Emotes";
    internal const string PACKS = "Packs";
    internal const string REAGENT_BUNDLES = "Reagent bundles";

    /// <summary>The kinds, in the order the filter lists them.</summary>
    internal static readonly string[] CATEGORIES = [
        REAGENTS, JEWELS, PINS, SNACKS, ELIXIRS, TRANSFORMATIONS, HENCHMEN, EMOTES, PACKS, REAGENT_BUNDLES,
    ];

    // Folders that hold nothing for this list: house items (the Furniture tab), the Magic Mirror's looks, and the
    // stat hats creatures wear when a player turns into them.
    private static readonly string[] s_skipped = ["ObjectData/Housing/", "ObjectData/MagicMirrorShopOptions/", "ObjectData/Polymorphs/"];

    internal IReadOnlyList<OtherItem> Items { get; private set; } = [];

    /// <summary>Reads every item. Takes a few seconds, so run it in the background.</summary>
    internal static ItemBook Load(SpellBook book) {
        var stats = GearBook.StatsOnly(book);
        var files = book.TemplatesUnder("ObjectData/").Where(t => !s_skipped.Any(s => t.File.StartsWith(s, StringComparison.OrdinalIgnoreCase))).ToList();
        var items = new ConcurrentBag<OtherItem>();
        System.Threading.Tasks.Parallel.ForEach(files, file => {
            try {
                if (book.DecodeTemplate(file.File) is WizItemTemplate template && Read(book, stats, file.Id, file.File, template) is { } item) {
                    items.Add(item);
                }
            }
            catch (Exception) {
                // A template the reader can't make sense of is left out.
            }
        });

        return new ItemBook {
            Items = [.. items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Id)],
        };
    }

    private static OtherItem? Read(SpellBook book, GearBook stats, uint id, string file, WizItemTemplate template) {
        var adjectives = (template.m_adjectiveList ?? []).Select(a => (string) a ?? "").Where(a => a.Length > 0).ToList();
        var behaviors = (template.m_behaviors ?? []).Where(b => b is not null).ToList();
        bool Has(string adjective) => adjectives.Contains(adjective, StringComparer.OrdinalIgnoreCase);

        // A house item belongs to the Furniture tab, wherever its file is.
        if (Has("Housing") || Has("WallpaperCarpet")) {
            return null;
        }

        var category = template switch {
            ReagentItemTemplate => REAGENTS,
            PetSnackItemTemplate => SNACKS,
            BoosterPackTemplate => PACKS,
            _ when Has("ReagentBundle") => REAGENT_BUNDLES,
            _ when adjectives.Any(a => a.StartsWith("PinSquare", StringComparison.OrdinalIgnoreCase)) => PINS,
            _ when Has("Jewel") => JEWELS,
            _ when Has("Emote") || behaviors.Any(b => b is CustomEmoteBehaviorTemplate) => EMOTES,
            _ when Has("Henchman") => HENCHMEN,
            _ when Has("Transformation") || file.Contains("Transformation", StringComparison.OrdinalIgnoreCase) => TRANSFORMATIONS,
            _ when Has("Elixir") || behaviors.Any(b => b is ElixirBehaviorTemplate) => ELIXIRS,
            _ => null,
        };
        if (category is null) {
            return null;
        }

        string displayName = template.m_displayName;
        var code = (string) template.m_objectName ?? "";
        var name = book.Text(displayName) is { Length: > 0 } shown ? Plain(shown) : "";
        string descriptionKey = template.m_description;
        var (lines, spells, level, school) = stats.Effects(template);
        var item = new OtherItem {
            Id = id,
            Name = name.Length > 0 ? name : code,
            Code = code,
            File = file,
            Category = category,
            Type = TypeOf(category, adjectives),
            Description = Plain(book.Text(descriptionKey)),
            Rarity = template.m_rarity.ToString() is { Length: > 3 } rarity && rarity.StartsWith("RT_", StringComparison.Ordinal)
                ? rarity[3..] switch { "ULTRARARE" => "Ultra rare", var r => char.ToUpperInvariant(r[0]) + r[1..].ToLowerInvariant() }
                : "",
            School = school.Length > 0 ? school : (string) template.m_school ?? "",
            Level = level,
            Lasts = behaviors.OfType<ElixirBehaviorTemplate>().Select(e => ((string) e.m_expireTime ?? "").Trim()).FirstOrDefault(t => t.Length > 0 && t != "0") ?? "",
            Flags = [.. adjectives.Where(a => a.StartsWith("FLAG_", StringComparison.OrdinalIgnoreCase)).Select(a => a[5..]).Distinct()],
            Stats = lines,
            Spells = spells,
            GoldCost = (int) Math.Max(0, template.m_baseCost),
            CrownsCost = (int) Math.Max(0, template.m_creditsCost),
            // Unnamed copies and the game's own test and "do not use" items.
            Test = name.Length == 0 || Has("EditorOnly") || name.Contains("Do Not Use", StringComparison.OrdinalIgnoreCase)
                || name.Contains("DoNotUse", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Test ", StringComparison.OrdinalIgnoreCase),
        };
        item.Haystack = SearchText.Normalize($"{item.Name} {item.Code} {item.Id} {item.Category} {item.Type} {item.School} {string.Join(' ', item.Flags)}");

        return item;
    }

    /// <summary>The kind inside a kind, from the game's marks.</summary>
    private static string TypeOf(string category, List<string> adjectives) {
        bool Has(string adjective) => adjectives.Contains(adjective, StringComparer.OrdinalIgnoreCase);

        return category switch {
            REAGENTS => Has("ReagentHarvestRare") ? "Rare harvest"
                : Has("ReagentHarvest") ? "Harvest"
                : Has("ReagentShop") ? "Shop"
                : Has("ReagentDrop") ? "Drop"
                : Has("ReagentEvent") ? "Event"
                : Has("ReagentPet") ? "Pet"
                : Has("ReagentShard") ? "Shard"
                : "Other",
            JEWELS => Has("PetJewel") ? "Pet"
                : Has("CircleJewel") ? "Circle"
                : Has("SquareJewel") ? "Square"
                : Has("TriangleJewel") ? "Triangle"
                : Has("TearJewel") ? "Tear"
                : Has("StarJewel") ? "Star"
                : "Other",
            PINS => Has("PinSquareSword") ? "Power"
                : Has("PinSquareShield") ? "Shield"
                : Has("PinSquarePip") ? "Pip"
                : "Other",
            SNACKS => Has("Meal") ? "Meal"
                : Has("Dessert") ? "Dessert"
                : Has("Candy") ? "Candy"
                : Has("Fruit") ? "Fruit"
                : Has("Vegetable") || Has("Vegetables") ? "Vegetable"
                : Has("Snack") ? "Snack"
                : "Other",
            ELIXIRS => Has("GroupElixir") ? "Group elixir"
                : Has("Fishing") ? "Fishing"
                : Has("PrepaidTime") ? "Membership time"
                : Has("PrimaMateria") ? "Guild"
                : "Elixir",
            PACKS => Has("CCGPet") ? "Pet and snack packs"
                : Has("CCGCraft") ? "Reagent packs"
                : Has("SetHouse") || Has("CCGHousing") ? "House item sets"
                : Has("CrownShop_Gardening_Tab") ? "Gardening packs"
                : Has("LootRarityReverse") ? "Spellement packs"
                : Has("CCG") ? "Hoard packs"
                : "Other",
            _ => "",
        };
    }

    // The game's text marks such as <center> and <color;ff0000>, which are left out.
    private static string Plain(string text)
        => System.Text.RegularExpressions.Regex.Replace(text ?? "", "<[^>]*>", "").Trim();

}
