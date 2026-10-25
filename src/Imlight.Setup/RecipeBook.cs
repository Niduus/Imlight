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

/// <summary>One thing a recipe needs: a reagent, treasure card, item, fish or pet snack, and how many.</summary>
/// <param name="Name">Its name in the game.</param>
/// <param name="Kind">Reagent, Treasure card, Item, Fish or Pet snack.</param>
/// <param name="Id">Its template ID, or 0 for a treasure card (named by its spell).</param>
/// <param name="File">Its template file, for its picture, or "".</param>
/// <param name="Spell">A treasure card's spell, by the spell's game name, or "".</param>
/// <param name="Index">Its place in the recipe's ingredients, which RecipeChanges.json counts by.</param>
internal sealed record RecipeIngredient(string Name, string Kind, uint Id, string File, string Spell, int Quantity, int Index);

/// <summary>One of the game's recipes, read from its RecipeTemplate in Root.wad.</summary>
internal sealed class Recipe {

    internal required uint Id { get; init; }

    /// <summary>The recipe's name in the game, such as "Elixir of Fire Mastery".</summary>
    internal required string Name { get; init; }

    /// <summary>The recipe's file name, which the shops and the server's .recipe commands name it by.</summary>
    internal required string Code { get; init; }

    internal required string File { get; init; }

    /// <summary>The stations it is crafted at, in plain words; empty when it is no station's.</summary>
    internal required IReadOnlyList<string> Stations { get; init; }

    internal required string Category { get; init; }

    internal required string School { get; init; }

    internal required IReadOnlyList<RecipeIngredient> Ingredients { get; init; }

    /// <summary>What it makes: an item's name or a treasure card's.</summary>
    internal required string Makes { get; init; }

    /// <summary>The item it makes by template ID, or 0 when it makes a card or nothing.</summary>
    internal required uint MakesId { get; init; }

    internal required string MakesFile { get; init; }

    /// <summary>The treasure card it makes, by the spell's game name, or "".</summary>
    internal required string MakesSpell { get; init; }

    /// <summary>How long its crafting slot cools down after a craft, in seconds.</summary>
    internal required int Cooldown { get; init; }

    internal required int GoldCost { get; init; }

    internal required int CrownsCost { get; init; }

    internal string Haystack { get; set; } = "";

    /// <summary>The server's command that teaches it to the wizard playing (CommandCraftingProtocol), by ID.</summary>
    internal string LearnCommand => $".recipe learn {Id}";

    internal string Station => Stations.Count > 0 ? string.Join(", ", Stations) : RecipeBook.NO_STATION;

}

/// <summary>
/// Every recipe of the game (Niduus, 2026-10-24: "make a recipe vendor and recipe browser in manager"), read from the
/// recipe templates in Root.wad with the SpellBook's text and pictures: what each needs, what it makes, where it is
/// crafted and how long it cools down.
/// </summary>
internal sealed class RecipeBook {

    internal const string NO_STATION = "No crafting station";

    /// <summary>The crafting station types of the game's recipes, in plain words (the server's CraftingStation.TypeNames).</summary>
    internal static readonly Dictionary<string, string> STATIONS = new(StringComparer.OrdinalIgnoreCase) {
        ["CraftGeneric"] = "Basic Crafting Station",
        ["CraftEquipable"] = "Equipment Crafting Station",
        ["CraftCard"] = "Card Crafting Station",
        ["CraftFurniture"] = "Housing Crafting Station",
        ["CraftJewel"] = "Jewel Crafting Station",
        ["CraftDragonspyre"] = "Dragonspyre Crafting Anvil",
        ["CraftGrizzleheim"] = "Grizzleheim Crafting Anvil",
        ["CraftArcanum"] = "Arcanum Crafting Anvil",
        ["CraftTC"] = "Treasure Card crafting",
        ["CraftMount"] = "Mount crafting",
        ["CraftTreasureTowerSpells"] = "Treasure Tower spell crafting",
    };

    private readonly SpellBook _book;
    private readonly Dictionary<uint, string> _files = [];
    private readonly ConcurrentDictionary<uint, string> _names = new();
    private Dictionary<string, SpellCard>? _spellsByCode;

    private RecipeBook(SpellBook book) {
        _book = book;
    }

    internal IReadOnlyList<Recipe> Recipes { get; private set; } = [];

    /// <summary>Reads every recipe. Takes a few seconds, so run it in the background.</summary>
    internal static RecipeBook Load(SpellBook book) {
        var recipes = new RecipeBook(book);
        var all = book.TemplatesUnder("");
        foreach (var (id, file) in all) {
            recipes._files.TryAdd(id, file);
        }

        recipes._spellsByCode = book.Spells.GroupBy(s => s.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Hidden).First(), StringComparer.OrdinalIgnoreCase);
        var files = all.Where(t => t.File.Contains("Recipe", StringComparison.OrdinalIgnoreCase)).ToList();
        var read = new ConcurrentBag<Recipe>();
        System.Threading.Tasks.Parallel.ForEach(files, file => {
            try {
                if (book.DecodeFile(file.File) is RecipeTemplate template && recipes.Read(file.Id, file.File, template) is { } recipe) {
                    read.Add(recipe);
                }
            }
            catch (Exception) {
                // A template the reader can't make sense of is left out.
            }
        });
        recipes.Recipes = [.. read.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id)];

        return recipes;
    }

    /// <summary>A treasure card or spell by its game name, or null.</summary>
    internal SpellCard? Spell(string code) => code.Length > 0 ? _spellsByCode?.GetValueOrDefault(code) : null;

    private Recipe? Read(uint id, string file, RecipeTemplate template) {
        var code = file[(file.LastIndexOf('/') + 1)..];
        code = code.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? code[..^4] : code;
        var shown = _book.Text((string) template.m_displayKey);
        var stations = (template.m_adjectives ?? []).Select(a => (string) a ?? "")
            .Where(STATIONS.ContainsKey).Select(a => STATIONS[a]).Distinct().ToList();

        var ingredients = new List<RecipeIngredient>();
        var index = -1;
        foreach (var ingredient in template.m_ingredients ?? []) {
            index++;
            if (ingredient is null) {
                continue;
            }

            var kind = ingredient.m_ingredientType switch {
                INGREDIENT_TYPE.INGREDIENT_REAGENT => "Reagent",
                INGREDIENT_TYPE.INGREDIENT_TREASURE => "Treasure card",
                INGREDIENT_TYPE.INGREDIENT_FISH => "Fish",
                INGREDIENT_TYPE.INGREDIENT_PET_SNACK => "Pet snack",
                _ => "Item",
            };
            string spell = (string) ingredient.m_spellTemplate ?? "";
            var itemId = (uint) ingredient.m_itemID.MParts.TemplateId;
            var name = spell.Length > 0 ? Spell(spell)?.Name ?? spell : NameOf(itemId);
            ingredients.Add(new RecipeIngredient(name, kind, spell.Length > 0 ? 0 : itemId, _files.GetValueOrDefault(itemId, ""), spell,
                Math.Max(1, ingredient.m_quantity), index));
        }

        string makesSpell = (string) template.m_spellName ?? "";
        var makesId = (uint) template.m_itemID.MParts.TemplateId;
        var makes = makesSpell.Length > 0 ? Spell(makesSpell)?.Name ?? makesSpell
            : makesId != 0 ? NameOf(makesId)
            : (string) template.m_itemLootTable is { Length: > 0 } table ? $"A drop from the loot table {table}" : "";
        string categoryKey = (string) template.m_categoryKey ?? "";
        var category = _book.Text(categoryKey) is { Length: > 0 } categoryText ? categoryText : categoryKey.Replace('_', ' ');
        string school = (string) template.m_school ?? "";

        var recipe = new Recipe {
            Id = id,
            Name = shown.Length > 0 ? shown.Trim() : code,
            Code = code,
            File = file,
            Stations = stations,
            Category = category.Trim(),
            School = school,
            Ingredients = ingredients,
            Makes = makes,
            MakesId = makesSpell.Length > 0 ? 0 : makesId,
            MakesFile = _files.GetValueOrDefault(makesId, ""),
            MakesSpell = makesSpell,
            Cooldown = Math.Max(0, template.m_cookTime),
            GoldCost = Math.Max(0, template.m_goldCost),
            CrownsCost = Math.Max(0, template.m_crownsCost),
        };
        recipe.Haystack = $"{recipe.Name} {recipe.Code} {recipe.Id} {recipe.Makes} {recipe.Station} {recipe.Category} {recipe.School} " +
                          string.Join(' ', ingredients.Select(i => i.Name));
        recipe.Haystack = recipe.Haystack.ToLowerInvariant();

        return recipe;
    }

    /// <summary>An item's name in the game by its template ID, or "Item 123" when it has none.</summary>
    internal string NameOf(uint id) {
        if (id == 0) {
            return "";
        }

        return _names.GetOrAdd(id, _ => {
            if (!_files.TryGetValue(id, out var file)) {
                return $"Item {id}";
            }

            try {
                if (_book.DecodeFile(file) is GameObjectTemplate template) {
                    var shown = _book.Text((string) template.m_displayName);

                    return shown.Length > 0 ? shown.Trim() : (string) template.m_objectName ?? $"Item {id}";
                }
            }
            catch (Exception) {
                // Named by its ID below.
            }

            return $"Item {id}";
        });
    }

    /// <summary>The cooldown in plain words, such as "Instant", "15 minutes" or "1 day 6 hours".</summary>
    internal static string Time(int seconds) {
        if (seconds <= 0) {
            return "Instant";
        }

        var time = TimeSpan.FromSeconds(seconds);
        var parts = new List<string>();
        void Part(int n, string one) {
            if (n > 0) {
                parts.Add(n == 1 ? $"1 {one}" : $"{n} {one}s");
            }
        }

        Part(time.Days, "day");
        Part(time.Hours, "hour");
        Part(time.Minutes, "minute");
        if (parts.Count == 0) {
            Part(time.Seconds, "second");
        }

        return string.Join(" ", parts);
    }

}

/// <summary>
/// One recipe changed on the Recipes tab (Niduus, 2026-10-25: "like in areas where a GOLD SKELETON KEY takes 400, I want
/// to be able to lower to like 200"), an entry of spiraldb-extra/Customs/RecipeChanges.json, which the server's
/// RecipeChanges.cs reads again whenever it changes. Amounts follow the recipe's ingredients in order; null keeps the
/// game's.
/// </summary>
internal sealed class RecipeChange {

    public uint Id { get; set; }
    public string Name { get; set; } = "";
    public List<int?> Amounts { get; set; } = [];
    public int? Cooldown { get; set; }
    public int? GoldCost { get; set; }

    internal const string FILE = "RecipeChanges.json";

    private static readonly System.Text.Json.JsonSerializerOptions s_options = new() {
        WriteIndented = true, PropertyNameCaseInsensitive = true, AllowTrailingCommas = true,
        ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    };

    [System.Text.Json.Serialization.JsonIgnore]
    internal bool Empty => Amounts.All(a => a is null) && Cooldown is null && GoldCost is null;

    internal static List<RecipeChange> Load(string customsFolder) {
        var file = System.IO.Path.Combine(customsFolder, FILE);

        return System.IO.File.Exists(file)
            ? System.Text.Json.JsonSerializer.Deserialize<List<RecipeChange>>(System.IO.File.ReadAllText(file), s_options) ?? []
            : [];
    }

    /// <summary>Saves one recipe's change over the one it had; an empty change takes the recipe out of the file.</summary>
    internal static void Set(string customsFolder, RecipeChange change) {
        var changes = Load(customsFolder).Where(c => c.Id != change.Id).ToList();
        if (!change.Empty) {
            changes.Add(change);
        }

        System.IO.Directory.CreateDirectory(customsFolder);
        var file = System.IO.Path.Combine(customsFolder, FILE);
        System.IO.File.WriteAllText(file + ".saving", System.Text.Json.JsonSerializer.Serialize(changes.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(), s_options));
        System.IO.File.Move(file + ".saving", file, overwrite: true);
    }

}
