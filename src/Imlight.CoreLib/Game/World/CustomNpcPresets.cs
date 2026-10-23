/*
 * Presets for the Customs tab's vendors and trainers (Niduus, 2026-10-23: "make Presets for Trainers and Vendors.
 * Include a Preset Vendor that sells housing gauntlets. All of them, including the one shots"; "add Vanilla Vendors").
 * The server works them out from the game files on each boot and writes them to the ID lookup folder (presets.json),
 * beside ids.csv; the app's "Add from a preset" copies one into a new vendor or trainer (CustomNpcs.cs), which is then
 * edited like any other.
 *
 * Vendors:
 *   Housing Gauntlets  every item with a housing sigil (HousingSigilBehavior): the gauntlets, Skeleton Key duels and
 *                      rematches, the ones used up after one run (m_usedDisposableDungeonTemplateID names the trophy
 *                      they become) and the ones kept. The game's test ones are left out.
 *   the house sellers' lists (HouseVendors): each reagent kind and jewel shape, Hao's pet snacks and Doctor Purreau's
 *                      elixir recipes, in their own windows.
 * Trainers:
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

        Add("Housing Gauntlets", () => [HousingGauntlets()]);
        Add("the house sellers", HouseSellers);
        Add("the school trainers", SchoolTrainers);
        Add("the test trainers", TestTrainers);
        Add("the vanilla trainers", VanillaTrainers);

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

    private static Preset HousingGauntlets() {
        var gauntlets = HouseVendors.TemplatesIn<WizItemTemplate>(HOUSING_FOLDER)
            .Select(item => (item.Id, item.Template, Sigil: (item.Template.m_behaviors ?? []).OfType<HousingSigilBehaviorTemplate>().FirstOrDefault()))
            .Where(item => item.Sigil is not null)
            .ToList();
        // The game's own test items (TEST_Disposable_Dungeon_01, "Single Use Test Dungeon", a test Rattlebones duel).
        var tests = gauntlets.Where(g => IsTest(g.Template)).ToList();
        var kept = gauntlets.Except(tests).OrderBy(g => Locale.GetEnglishName((string) g.Template.m_displayName) ?? "").ToList();
        var oneShots = kept.Count(g => g.Sigil.m_usedDisposableDungeonTemplateID != 0);
        Logger.Information("Vendor presets: {0} housing gauntlets ({1} used up after one run, {2} kept), left out the test ones {3}.",
            Logger.Args(kept.Count, oneShots, kept.Count - oneShots, string.Join(", ", tests.Select(t => $"{(string) t.Template.m_objectName} ({t.Id})"))));

        return Vendor("Housing Gauntlets", "Furniture",
            $"Every housing gauntlet, Skeleton Key duel and rematch: {kept.Count}, {oneShots} of them used up after one run.",
            kept.Select(g => g.Id));
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
        foreach (var school in trained.Select(s => s.School).Where(s => s.Length > 0).Distinct().OrderBy(s => Array.IndexOf(s_schools, s) is var i and >= 0 ? i : 99)) {
            yield return Trainer($"All {school} Spells", $"Every {school} spell a trainer of the game teaches.",
                trained.Where(s => s.School == school).Select(s => s.Id));
        }

        yield return Trainer("All Schools", "Every spell a trainer of the game teaches, every school.",
            InteractTrainerComponent.TestSpellsOf(InteractTrainerComponent.TestTrainerKind.Hall).Select(e => e.TemplateID));
    }

    private static IEnumerable<Preset> TestTrainers() {
        var lists = new (InteractTrainerComponent.TestTrainerKind Kind, string Name, string Help)[] {
            (InteractTrainerComponent.TestTrainerKind.Enchantments, "Enchantments", "Every enchantment card, as Mr. Lincoln teaches."),
            (InteractTrainerComponent.TestTrainerKind.Extracts, "Extract Spells", "Every Extract spell, as Monstrologist Burke teaches."),
            (InteractTrainerComponent.TestTrainerKind.Shadow, "Shadow Spells", "The Shadow spells, as Mellori teaches."),
            (InteractTrainerComponent.TestTrainerKind.Boss, "Monster and Boss Spells", "Spells only monsters and bosses cast, as Merle Ambrose teaches."),
        };
        foreach (var (kind, name, help) in lists) {
            yield return Trainer(name, help, InteractTrainerComponent.TestSpellsOf(kind).Select(e => e.TemplateID));
        }
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

            yield return Trainer($"Vanilla {school} Trainer",
                $"The spells {school} wizards learn from trainers in the game: their school's and the ones other trainers teach them.", found);
        }
    }

}
