/*
 * Presets for the Customs tab's vendors and trainers (Niduus, 2026-10-23: "make Presets for Trainers and Vendors.
 * Include a Preset Vendor that sells housing gauntlets. All of them, including the one shots"; "add Vanilla Vendors").
 * The server works them out from the game files on each boot and writes them to the ID lookup folder (presets.json),
 * beside ids.csv; the app's "Add from a preset" copies one into a new vendor or trainer (CustomNpcs.cs), which is then
 * edited like any other.
 *
 * Vendors (Niduus, 2026-10-23: "Split Trainer for Gauntlets. Bundle Gauntlets, One Shot gauntlets, Rematches"), from
 * every item with a housing sigil (HousingSigilBehavior), the game's test ones left out:
 *   Pack Gauntlets     the ones the game has a booster pack of, their own crown shop pack (Niduus, 2026-10-24: "Also
 *                      include Pack gauntlets as their own vendor"): the Night Mire Gauntlet and the Night Mire Hoard
 *                      Pack, and so on (PackOf). They are left out of the lists below.
 *   Bundle Gauntlets   the ones kept after a run (no m_usedDisposableDungeonTemplateID).
 *   One-Shot Gauntlets the ones used up after one run (that id names the trophy they become), not rematches.
 *   Rematches          the one-shot boss rematches (object names with "Rematch").
 *   the house sellers' lists (HouseVendors): each reagent kind and jewel shape, Hao's pet snacks and Doctor Purreau's
 *                      elixir recipes, in their own windows.
 *   Garden Seeds       the seeds the game's garden vendors sell; All Seeds: every seed of the game, by name, in parts.
 * Trainers, each split into fixed parts of at most 50 spells, "<Name> 1 of 2" and so on, so a trainer always teaches
 * the same set and never pages (Niduus, 2026-10-23: "Split up Trainers into parts so they have a set train"):
 *   All <School> Spells  every spell some trainer of the game teaches, of that school (NpcSpellInventories).
 *   All Schools        all of those (the trainer hall's book).
 *   the test trainers' lists: enchantments, Extract spells, Shadow spells, monster and boss spells.
 *   Vanilla <School> Trainer  the spells the school's own trainers teach and the ones other trainers teach its
 *                      wizards, as Niduus's "All <School> Spells" pages list them (spiraldb-extra/Presets/
 *                      VanillaTrainers.json, by name). Each name is the game's card of that name a trainer teaches,
 *                      else the template the file names; a name with neither is logged.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.World;

internal static class CustomNpcPresets {

    internal sealed record Preset(string Name, string Kind, string Window, string Help, List<uint> Items, List<uint> Spells);

    private const string FILE = "presets.json";
    private const string VANILLA_FILE = "Presets/VanillaTrainers.json";
    private const string HOUSING_FOLDER = "ObjectData/Housing/";
    private const int PART_SIZE = 50;
    private const int RECIPE_PART_SIZE = 100;
    private static readonly string[] s_schools = ["Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance"];

    private static readonly JsonSerializerOptions s_write = new() {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions s_read = new() {
        PropertyNameCaseInsensitive = true, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Works the presets out and writes them for the app. A preset that fails is left out and logged.</summary>
    internal static void Write(string folder) {
        var presets = new List<Preset>();
        void Add(string what, Func<IEnumerable<Preset>> make) {
            try {
                presets.AddRange(make().Where(p => p.Items.Count + p.Spells.Count > 0));
            }
            catch (Exception e) {
                Logger.Warning("Vendor and trainer presets: {0} could not be made: {1}", Logger.Args(what, e.Message));
            }
        }

        Add("the housing gauntlets", HousingGauntlets);
        Add("the house sellers", HouseSellers);
        Add("the recipe vendors", RecipeVendors);
        Add("the seed vendors", SeedVendors);
        Add("the school trainers", SchoolTrainers);
        Add("the test trainers", TestTrainers);
        Add("the vanilla trainers", VanillaTrainers);
        // Its cards are the Deckathalon's that is on, so it has no list of its own (DeckathalonShopCards).
        presets.Add(new Preset("Deckathalon TC NPC", "Vendor", Events.DeckathalonShopCards.WINDOW,
            "Sells only the treasure cards of the Deckathalon that is on (all of its school's, or with ShopCardsAll off the lists " +
            "in Settings, Deckathalon: FireShopCards and so on), and nothing while none is.", [], []));

        try {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, FILE);
            File.WriteAllText(path + ".saving", JsonSerializer.Serialize(presets, s_write));
            File.Move(path + ".saving", path, overwrite: true);
            Logger.Information("Vendor and trainer presets: {0} written ({1}).",
                Logger.Args(presets.Count, string.Join(", ", presets.Select(p => $"{p.Name} {p.Items.Count + p.Spells.Count}"))));
        }
        catch (Exception e) {
            Logger.Warning("Vendor and trainer presets could not be written: {0}", Logger.Args(e.Message));
        }
    }

    private static Preset Vendor(string name, string window, string help, IEnumerable<uint> items)
        => new(name, "Vendor", window, help, [.. items.Distinct()], []);

    private static Preset Trainer(string name, string help, IEnumerable<ulong> spells)
        => new(name, "Trainer", "", help, [], [.. spells.Select(id => (uint) id).Distinct()]);

    // One trainer, or even parts of at most PART_SIZE spells in the order given; the help says what each part holds.
    private static IEnumerable<Preset> TrainerParts(string name, string help, IReadOnlyList<(ulong Id, string Label)> spells) {
        var unique = spells.DistinctBy(s => (uint) s.Id).ToList();
        if (unique.Count <= PART_SIZE) {
            yield return Trainer(name, help, unique.Select(s => s.Id));

            yield break;
        }

        var parts = (unique.Count + PART_SIZE - 1) / PART_SIZE;
        var size = (unique.Count + parts - 1) / parts;
        for (var part = 0; part < parts; part++) {
            var chunk = unique.Skip(part * size).Take(size).ToList();
            var range = chunk[0].Label == chunk[^1].Label ? chunk[0].Label : $"{chunk[0].Label} to {chunk[^1].Label}";
            yield return Trainer($"{name} {part + 1} of {parts}", $"{help} Part {part + 1} of {parts}: {range}.", chunk.Select(s => s.Id));
        }
    }

    // The lowest level any trainer of the game teaches each spell at.
    private static Dictionary<uint, int> TrainerLevels()
        => SpiralDB.NpcSpellInventories.Values
            .Where(inventory => inventory?.Spells is not null)
            .SelectMany(inventory => inventory.Spells)
            .GroupBy(entry => (uint) entry.TemplateID)
            .ToDictionary(group => group.Key, group => group.Min(entry => entry.Level));

    // The spells sorted by the level trainers teach them at, then by name, labelled with that level.
    private static List<(ulong Id, string Label)> ByLevel(IEnumerable<ulong> spells, Dictionary<uint, int> levels)
        => [.. spells
            .Select(id => (Id: id, Level: levels.TryGetValue((uint) id, out var level) ? level : 0, Name: SpellName(id)))
            .OrderBy(s => s.Level).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => (s.Id, $"level {s.Level}"))];

    // The spells by name, labelled with their first letter.
    private static List<(ulong Id, string Label)> ByName(IEnumerable<ulong> spells)
        => [.. spells
            .Select(id => (Id: id, Name: SpellName(id)))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => (s.Id, s.Name.Length > 0 ? s.Name[..1].ToUpperInvariant() : "?"))];

    private static string SpellName(ulong id)
        => CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate spell
            ? Locale.GetEnglishName((string) spell.m_displayName) ?? (string) spell.m_name ?? ""
            : "";

    private static IEnumerable<Preset> HousingGauntlets() {
        var gauntlets = HouseVendors.TemplatesIn<WizItemTemplate>(HOUSING_FOLDER)
            .Select(item => (item.Id, item.Template, Sigil: (item.Template.m_behaviors ?? []).OfType<HousingSigilBehaviorTemplate>().FirstOrDefault()))
            .Where(item => item.Sigil is not null)
            .ToList();
        // The game's own test items (TEST_Disposable_Dungeon_01, "Single Use Test Dungeon", a test Rattlebones duel).
        var tests = gauntlets.Where(g => IsTest(g.Template)).ToList();
        var kept = gauntlets.Except(tests).OrderBy(g => Locale.GetEnglishName((string) g.Template.m_displayName) ?? "").ToList();
        var packs = BoosterPacks();
        var packed = kept.Select(g => (Gauntlet: g, Pack: PackOf(g.Template, packs))).Where(g => g.Pack is not null).ToList();
        Logger.Information("Vendor presets: {0} gauntlets have a pack of their own: {1}.",
            Logger.Args(packed.Count, string.Join(", ", packed.Select(g => $"{Locale.GetEnglishName((string) g.Gauntlet.Template.m_displayName)} ({g.Gauntlet.Id}, {g.Pack})"))));
        kept = [.. kept.Except(packed.Select(g => g.Gauntlet))];
        var bundles = kept.Where(g => g.Sigil.m_usedDisposableDungeonTemplateID == 0).ToList();
        var rematches = kept.Except(bundles).Where(g => ((string) g.Template.m_objectName ?? "").Contains("Rematch", StringComparison.OrdinalIgnoreCase)).ToList();
        var oneShots = kept.Except(bundles).Except(rematches).ToList();
        Logger.Information("Vendor presets: {0} housing gauntlets ({1} pack, {2} bundle, {3} one-shot, {4} rematches), left out the test ones {5}.",
            Logger.Args(kept.Count + packed.Count, packed.Count, bundles.Count, oneShots.Count, rematches.Count,
                string.Join(", ", tests.Select(t => $"{(string) t.Template.m_objectName} ({t.Id})"))));

        yield return Vendor("Pack Gauntlets", "Furniture",
            $"The housing gauntlets the game has a booster pack of ({string.Join(", ", packed.Select(g => g.Pack).Distinct())}): {packed.Count}.",
            packed.Select(g => g.Gauntlet.Id));
        yield return Vendor("Bundle Gauntlets", "Furniture",
            $"The housing gauntlets and Skeleton Key duels kept after a run: {bundles.Count}.", bundles.Select(g => g.Id));
        yield return Vendor("One-Shot Gauntlets", "Furniture",
            $"The housing gauntlets used up after one run, rematches aside: {oneShots.Count}.", oneShots.Select(g => g.Id));
        yield return Vendor("Rematches", "Furniture",
            $"The one-shot boss rematches: {rematches.Count}.", rematches.Select(g => g.Id));
    }

    private static readonly HashSet<string> s_packStopWords = new(StringComparer.OrdinalIgnoreCase) {
        "the", "hoard", "pack", "gauntlet", "super", "ultra", "combo", "pk", "ii", "of", "and", "a",
    };

    // The words of a gauntlet's or pack's English name that tell it apart: no "Gauntlet", "Hoard Pack" or the like.
    private static HashSet<string> NameWords(string name)
        => [.. System.Text.RegularExpressions.Regex.Matches((name ?? "").Replace("'s", ""), "[A-Za-z0-9]+")
            .Select(m => m.Value.ToLowerInvariant())
            .Where(w => !s_packStopWords.Contains(w) && !w.All(char.IsDigit))];

    // Every booster pack of the game (BoosterPackTemplate), by its English name and that name's words.
    private static List<(string Name, HashSet<string> Words)> BoosterPacks() {
        var packs = new List<(string, HashSet<string>)>();
        foreach (var location in CoreObjectFactory.TemplateManifest?.m_serializedTemplates ?? []) {
            string file = location?.m_filename;
            if (file is null || !file.Contains("BoosterPack", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            try {
                if (CoreObjectFactory.GetCoreTemplate(location.m_id) is BoosterPackTemplate pack
                    && Locale.GetEnglishName((string) pack.m_displayName) is { Length: > 0 } name) {
                    packs.Add((name.Replace("\\n", " ").Trim(), NameWords(name)));
                }
            }
            catch (Exception e) {
                Logger.Warning("Vendor presets skip the pack {0} ({1}): {2}", Logger.Args(file, location.m_id, e.Message));
            }
        }

        return packs;
    }

    /// <summary>
    /// The booster pack that is a gauntlet's own, or null: one whose name holds every word of the gauntlet's ("Night
    /// Mire Gauntlet", "Night Mire Hoard Pack"), or whose every word is in the gauntlet's name and, a word of five
    /// letters or more, in its object name too ("Sinbad Hoard Pack", HOUSE_SP_SinbadRuins). The second keeps
    /// "Terror's Hoard Pack" (Darkmoor) off the Tanglewood Terror Gauntlet.
    /// </summary>
    private static string PackOf(WizItemTemplate gauntlet, List<(string Name, HashSet<string> Words)> packs) {
        var words = NameWords(Locale.GetEnglishName((string) gauntlet.m_displayName));
        var objectName = (string) gauntlet.m_objectName ?? "";
        if (words.Count == 0) {
            return null;
        }

        return packs.Where(p => p.Words.Count > 0 && (words.IsSubsetOf(p.Words)
                || (p.Words.IsSubsetOf(words) && p.Words.Any(w => w.Length >= 5 && objectName.Contains(w, StringComparison.OrdinalIgnoreCase)))))
            .OrderBy(p => p.Name.Length)
            .Select(p => p.Name)
            .FirstOrDefault();
    }

    private static bool IsTest(WizItemTemplate template) {
        string objectName = template.m_objectName;
        var shown = Locale.GetEnglishName((string) template.m_displayName) ?? "";

        return (objectName ?? "").Contains("TEST", StringComparison.OrdinalIgnoreCase)
               || shown.Contains("Test", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<Preset> HouseSellers() {
        foreach (var seller in HouseVendors.Sellers) {
            yield return Vendor(seller.Name.Replace(" Seller", "s"), seller.Window.ToString(), $"What the house's {seller.Name} sells.", seller.Items());
        }

        yield return Vendor("Pet Snacks", "Snack", "Every pet snack of rank 8 or higher, as Hao sells.", HouseVendors.Snacks);
        yield return Vendor("Pet Elixirs", "Recipe", "Doctor Purreau's recipes: buying one gives what it makes.",
            HouseVendors.RecipesOf(HouseVendors.PetElixirVendorId).Select(r => r.Id));
    }

    // Niduus (2026-10-24): "make a recipe vendor". A vendor for each crafting station of the game, with every recipe
    // made there, by name, in parts of at most RECIPE_PART_SIZE; buying one teaches it (ShopService.BuyRecipe).
    private static IEnumerable<Preset> RecipeVendors() {
        var byType = new Dictionary<string, List<(uint Id, string Name)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, id) in Crafting.Crafting.AllRecipes) {
            if (Crafting.Crafting.RecipeOf(id) is not { } recipe) {
                continue;
            }

            var type = (recipe.m_adjectives ?? []).Select(adjective => (string) adjective)
                .FirstOrDefault(adjective => adjective is not null && Crafting.CraftingStation.TypeNames.ContainsKey(adjective));
            if (type is null) {
                continue;
            }

            var shown = Locale.GetEnglishName((string) recipe.m_displayKey);
            if (!byType.TryGetValue(type, out var list)) {
                byType[type] = list = [];
            }

            list.Add((id, string.IsNullOrWhiteSpace(shown) ? file : shown.Trim()));
        }

        foreach (var type in Crafting.CraftingStation.TypeNames.Keys.Where(byType.ContainsKey)) {
            var station = Crafting.CraftingStation.TypeNames[type];
            var recipes = byType[type].DistinctBy(r => r.Id).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var name = $"Recipes: {station.Replace(" Crafting", "")}";
            var help = $"Every recipe crafted at the {station}, by name. Buying one teaches it.";
            if (recipes.Count <= RECIPE_PART_SIZE) {
                yield return Vendor(name, "Recipe", help, recipes.Select(r => r.Id));

                continue;
            }

            var parts = (recipes.Count + RECIPE_PART_SIZE - 1) / RECIPE_PART_SIZE;
            var size = (recipes.Count + parts - 1) / parts;
            for (var part = 0; part < parts; part++) {
                var chunk = recipes.Skip(part * size).Take(size).ToList();
                if (chunk.Count == 0) {
                    break;
                }

                string Letter(string text) => text.Length > 0 ? text[..1].ToUpperInvariant() : "?";
                var range = Letter(chunk[0].Name) == Letter(chunk[^1].Name) ? Letter(chunk[0].Name) : $"{Letter(chunk[0].Name)} to {Letter(chunk[^1].Name)}";
                yield return Vendor($"{name} {part + 1} of {parts}", "Recipe", $"{help} Part {part + 1} of {parts}: {range}.", chunk.Select(r => r.Id));
            }
        }
    }

    // Niduus (2026-10-25): "make seed vendor for gardening". What the game's garden vendors sell (Farley and the
    // others, with spiraldb-extra/NpcInventory/garden-*.json), and every seed of the game by name, in parts of at most
    // RECIPE_PART_SIZE.
    private static IEnumerable<Preset> SeedVendors() {
        var seeds = Gardening.GardenTemplates.SeedItems.ToHashSet();
        var sold = SpiralDB.NpcInventories.Values
            .Where(inventory => inventory?.Inventory is not null && Gardening.GardenLessons.IsTrainer((uint) inventory.TemplateID))
            .SelectMany(inventory => inventory.Inventory)
            .Select(id => (uint) id)
            .Where(seeds.Contains)
            .Distinct()
            .ToList();
        yield return Vendor("Garden Seeds", "Furniture", "The seeds the game's garden vendors (Farley, Charley, Barley and the others) sell.", sold);

        string Name(uint id) => CoreObjectFactory.GetCoreTemplate(id) is WizItemTemplate template
            ? Locale.GetEnglishName((string) template.m_displayName) ?? (string) template.m_objectName ?? ""
            : "";
        var all = seeds.Select(id => (Id: id, Name: Name(id).Trim()))
            .Where(seed => seed.Name.Length > 0)
            .OrderBy(seed => seed.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var parts = Math.Max(1, (all.Count + RECIPE_PART_SIZE - 1) / RECIPE_PART_SIZE);
        var size = Math.Max(1, (all.Count + parts - 1) / parts);
        for (var part = 0; part < parts; part++) {
            var chunk = all.Skip(part * size).Take(size).ToList();
            if (chunk.Count == 0) {
                break;
            }

            var name = parts == 1 ? "All Seeds" : $"All Seeds {part + 1} of {parts}";
            var range = $"{chunk[0].Name[..1].ToUpperInvariant()} to {chunk[^1].Name[..1].ToUpperInvariant()}";
            yield return Vendor(name, "Furniture", $"Every seed of the game, by name. Part {part + 1} of {parts}: {range}.", chunk.Select(seed => seed.Id));
        }
    }

    // Every spell the game's trainers teach, not treasure cards, with its school.
    private static List<(ulong Id, string School)> TrainedSpells()
        => [.. SpiralDB.NpcSpellInventories.Values
            .Where(inventory => inventory?.Spells is not null)
            .SelectMany(inventory => inventory.Spells)
            .Select(entry => entry.TemplateID)
            .Distinct()
            .Select(id => (Id: id, Spell: CoreObjectFactory.GetCoreTemplate(id) as SpellTemplate))
            .Where(pair => pair.Spell is not null && !pair.Spell.m_Treasure)
            .OrderBy(pair => (string) pair.Spell.m_name, StringComparer.OrdinalIgnoreCase)
            .Select(pair => (pair.Id, (string) pair.Spell.m_sMagicSchoolName ?? ""))];

    private static IEnumerable<Preset> SchoolTrainers() {
        var trained = TrainedSpells();
        var levels = TrainerLevels();
        var presets = new List<Preset>();
        foreach (var school in trained.Select(s => s.School).Where(s => s.Length > 0).Distinct().OrderBy(s => Array.IndexOf(s_schools, s) is var i and >= 0 ? i : 99)) {
            presets.AddRange(TrainerParts($"All {school} Spells", $"Every {school} spell a trainer of the game teaches.",
                ByLevel(trained.Where(s => s.School == school).Select(s => s.Id), levels)));
        }

        presets.AddRange(TrainerParts("All Schools", "Every spell a trainer of the game teaches, every school.",
            ByLevel(InteractTrainerComponent.TestSpellsOf(InteractTrainerComponent.TestTrainerKind.Hall).Select(e => e.TemplateID), levels)));

        return presets;
    }

    private static IEnumerable<Preset> TestTrainers() {
        var lists = new (InteractTrainerComponent.TestTrainerKind Kind, string Name, string Help)[] {
            (InteractTrainerComponent.TestTrainerKind.Enchantments, "Enchantments", "Every enchantment card."),
            (InteractTrainerComponent.TestTrainerKind.Extracts, "Extract Spells", "Every Extract spell, as Monstrologist Burke teaches."),
            (InteractTrainerComponent.TestTrainerKind.Shadow, "Shadow Spells", "The Shadow spells, as Mellori teaches."),
            (InteractTrainerComponent.TestTrainerKind.Boss, "Monster and Boss Spells", "Spells only monsters and bosses cast, as Merle Ambrose teaches."),
            (InteractTrainerComponent.TestTrainerKind.Fishing, "Fishing Spells", "Every fishing spell, as Lucky Hookline teaches. Free, no training points."),
        };
        foreach (var (kind, name, help) in lists) {
            foreach (var part in TrainerParts(name, help, ByName(InteractTrainerComponent.TestSpellsOf(kind).Select(e => e.TemplateID)))) {
                yield return part;
            }
        }

        // Niduus (2026-10-25): "Add trainer for Gardening and Fishing spells". Every garden spell the game's garden
        // trainers sell, by rank; a custom trainer sells them as those trainers do (gold, from the rank each needs).
        yield return Trainer("Garden Spells", "Every garden spell, by gardening rank. Each costs its gold price and needs its rank, as at Farley.",
            Gardening.GardenLessons.All.Where(lesson => lesson.Trainers.Count > 0)
                .OrderBy(lesson => lesson.Level).ThenBy(lesson => lesson.Order)
                .Select(lesson => (ulong) lesson.SpellId));
    }

    private sealed class VanillaFile {
        public Dictionary<string, List<VanillaSpell>> Trainers { get; set; } = [];
    }

    private sealed class VanillaSpell {
        public string Shown { get; set; } = "";
        public string School { get; set; } = "";
        public string Template { get; set; } = "";
    }

    private static IEnumerable<Preset> VanillaTrainers() {
        var path = Path.Combine(SpiralDB.ExtraPath, VANILLA_FILE);
        if (!File.Exists(path)) {
            Logger.Warning("Vendor and trainer presets: {0} is missing; no vanilla trainers.", Logger.Args(VANILLA_FILE));

            yield break;
        }

        var file = JsonSerializer.Deserialize<VanillaFile>(File.ReadAllText(path), s_read) ?? new VanillaFile();

        // Every spell template by its card's name and by its template name, with its id.
        var ids = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in CoreObjectFactory.TemplateManifest?.m_serializedTemplates ?? []) {
            ids.TryAdd(location.m_filename, location.m_id);
        }

        var byShown = new Dictionary<string, List<(uint Id, SpellTemplate Spell)>>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var (spellPath, spell) in Imlight.CoreLib.Game.Spells.SpellFactory.AllSpellTemplates()) {
            if (!ids.TryGetValue(spellPath, out var id)) {
                continue;
            }

            byName.TryAdd((string) spell.m_name ?? "", id);
            var shown = Locale.GetEnglishName((string) spell.m_displayName);
            if (!string.IsNullOrWhiteSpace(shown)) {
                if (!byShown.TryGetValue(shown.Trim(), out var list)) {
                    byShown[shown.Trim()] = list = [];
                }

                list.Add((id, spell));
            }
        }

        var taught = SpiralDB.NpcSpellInventories.Values.Where(i => i?.Spells is not null).SelectMany(i => i.Spells)
            .Select(e => (uint) e.TemplateID).ToHashSet();

        foreach (var (school, spells) in file.Trainers.Where(t => t.Value is not null).OrderBy(t => Array.IndexOf(s_schools, t.Key))) {
            var found = new List<ulong>();
            var missing = new List<string>();
            foreach (var wanted in spells) {
                var candidates = (byShown.TryGetValue(wanted.Shown.Trim(), out var list) ? list : [])
                    .Where(c => wanted.School.Length == 0 || string.Equals((string) c.Spell.m_sMagicSchoolName, wanted.School, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                // The card a trainer of the game teaches; else the template the file names; else the plainest card.
                uint? pick = candidates.Where(c => taught.Contains(c.Id) && !c.Spell.m_Treasure).Select(c => (uint?) c.Id).FirstOrDefault()
                    ?? (byName.TryGetValue(wanted.Template, out var named) ? named : (uint?) null)
                    ?? candidates.Where(c => !c.Spell.m_Treasure).OrderBy(c => ((string) c.Spell.m_name ?? "").Length).Select(c => (uint?) c.Id).FirstOrDefault();
                if (pick is { } id) {
                    found.Add(id);
                }
                else {
                    missing.Add(wanted.Shown);
                }
            }

            Logger.Information("Vanilla {0} trainer: {1} of {2} spells found{3}.",
                Logger.Args(school, found.Count, spells.Count, missing.Count == 0 ? "" : $"; not found: {string.Join(", ", missing)}"));

            foreach (var part in TrainerParts($"Vanilla {school} Trainer",
                         $"The spells {school} wizards learn from trainers in the game: their school's and the ones other trainers teach them.",
                         [.. found.Select(id => (id, SpellName(id)))])) {
                yield return part;
            }
        }
    }

}
