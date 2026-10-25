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
 * CRAFTING
 * ========================================================================
 *
 * PURPOSE:
 * The game's own crafting: a wizard learns recipes (buying them at a recipe shop, or .recipe learn), a crafting
 * station opens the client's crafting window (MSG_ALCHEMYSTATION) with the recipes it allows, and crafting a recipe
 * (MSG_USERECIPE) uses up its reagents, items, treasure cards and snacks, gives what it makes and fills a crafting
 * slot until the recipe's cooldown is over.
 *
 * USAGE EXAMPLE:
 * Crafting.Learn(session, wizard, recipeId);
 * Crafting.OpenStation(session, wizard, CraftingStation.Of(template, objectId));
 * var result = Crafting.Craft(session, wizard, characterObject, "Recipe-Name", 1);
 *
 * NOTE:
 * - The recipes live in the game's own files (RecipeTemplate): ingredients, what they make, cook time and cost.
 * - A recipe's adjectives name the stations that make it (CraftEquipable, CraftCard, CraftFurniture...). A station
 *   sends those types, and an NPC crafter (a recipe vendor) the names of its own recipes.
 * - The client names a recipe by the hash of its name (ClientRecipe.m_recipeNameID, MSG_EQUIPRECIPE.RecipeID).
 * - Fish ingredients come out of the fishing basket, smallest first.
 *
 * TODO:
 * - The quests that teach recipes and give crafting slots (ResAddRecipe, ResAddCraftingSlot) carry no fields yet.
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.IO;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Shared.Utilities;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Crafting;

/// <summary>
/// A crafting station a wizard opened: what it lets them craft.
/// </summary>
/// <param name="ObjectId">The station's global id (0 for .craft).</param>
/// <param name="Name">What the log calls it.</param>
/// <param name="Types">The recipe types it crafts (a recipe adjective such as CraftEquipable).</param>
/// <param name="Recipes">The names of the recipes it crafts besides its types (an NPC crafter's own recipes).</param>
/// <param name="NoCooldown">Whether crafting here fills no crafting slot.</param>
/// <param name="NoCategories">Whether the window shows no categories.</param>
/// <param name="Title">The window's title, or empty for the game's own.</param>
/// <param name="CreateButton">The craft button's words, or empty for the game's own.</param>
/// <param name="Anything">Whether every recipe the wizard knows can be crafted here (.craft).</param>
internal sealed record CraftingStation(
    ulong ObjectId,
    string Name,
    IReadOnlyList<string> Types,
    IReadOnlyList<string> Recipes,
    bool NoCooldown,
    bool NoCategories,
    string Title,
    string CreateButton,
    bool Anything = false) {

    /// <summary>
    /// The station types of the game's crafting stations whose behavior names none (from the game's recipes: each
    /// station's recipes carry its type as an adjective).
    /// </summary>
    private static readonly Dictionary<uint, string> s_knownTypes = new() {
        [164644] = "CraftEquipable",    // Equipment Crafting Station
        [164645] = "CraftCard",         // Card Crafting Station
        [164646] = "CraftFurniture",    // Housing Crafting Station
        [164647] = "CraftGeneric",      // Basic Crafting Station
        [164716] = "CraftDragonspyre",  // Dragonspyre Crafting Anvil
        [184181] = "CraftGrizzleheim",  // Grizzleheim Crafting Anvil
        [1350009] = "CraftJewel",       // Jewel Crafting Station
        [1391042] = "CraftArcanum",     // Arcanum Crafting Anvil
    };

    /// <summary>The station types in plain words, as the stations are named in the game.</summary>
    internal static readonly Dictionary<string, string> TypeNames = new(StringComparer.OrdinalIgnoreCase) {
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

    /// <summary>Every station type, for .craft.</summary>
    internal static readonly string[] AllTypes = [
        "CraftGeneric", "CraftEquipable", "CraftCard", "CraftFurniture", "CraftJewel", "CraftDragonspyre",
        "CraftGrizzleheim", "CraftArcanum", "CraftTC", "CraftMount", "CraftTreasureTowerSpells",
    ];

    /// <summary>
    /// The station a template makes, or null when it is not one.
    /// </summary>
    /// <param name="template">A world object, NPC or furniture template.</param>
    /// <param name="objectId">The object's global id.</param>
    internal static CraftingStation Of(CoreTemplate template, ulong objectId) {
        var behaviors = template switch {
            GameObjectTemplate gameObject => gameObject.m_behaviors,
            _ => null,
        };
        var station = behaviors?.OfType<AlchemyStationBehaviorTemplate>().FirstOrDefault();
        if (station is null) {
            return null;
        }

        var templateId = (uint) ((GameObjectTemplate) template).m_templateID;
        var types = new List<string>();
        if (StationTypesInFile(templateId) is { } listed) {
            types.AddRange(listed);
        }
        else {
            foreach (var part in (station.m_stringA ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)) {
                types.Add(part.Trim());
            }
        }

        if (types.Count == 0 && s_knownTypes.TryGetValue(templateId, out var known)) {
            types.Add(known);
        }

        // Some stations name their type only in their adjectives (AR_Crafting_Anvil: CraftArcanum).
        if (types.Count == 0 && template is GameObjectTemplate tagged) {
            types.AddRange((tagged.m_adjectiveList ?? []).Select(adjective => (string) adjective)
                .Where(adjective => adjective?.StartsWith("Craft", StringComparison.Ordinal) == true));
        }

        var name = template is GameObjectTemplate named ? (string) named.m_objectName : templateId.ToString();

        return new CraftingStation(objectId, name ?? "", types,
            [.. (station.m_recipeList ?? []).Select(recipe => (string) recipe).Where(recipe => !string.IsNullOrWhiteSpace(recipe))],
            station.m_noCooldown, station.m_noCategories, station.m_titleOverride ?? "", station.m_createButtonOverride ?? "");
    }

    // The game's AlchemyStationBehavior keeps its station types as a list of strings (hash 0x9E4F2A12), which the
    // server type reads as one string (m_stringA): the Deckathalon Crafting Stations list "FireTT, CraftCardTT" and
    // came out as "F", which crafted nothing of their own (Niduus, 2026-10-25: "acting like a crafting station in
    // general instead of a specific crafting station"). So the list is read from the template's own file.
    private const uint STATION_TYPES_HASH = 0x9E4F2A12;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, string[]> s_fileTypes = new();

    /// <summary>The station types the template's file lists, or null when it lists none or cannot be read.</summary>
    internal static string[] StationTypesInFile(uint templateId) {
        var types = s_fileTypes.GetOrAdd(templateId, ReadStationTypes);

        return types.Length > 0 ? types : null;
    }

    private static string[] ReadStationTypes(uint templateId) {
        try {
            var file = CoreObjectFactory.TemplateManifest?.m_serializedTemplates?
                .FirstOrDefault(location => location?.m_id == templateId)?.m_filename;
            if (string.IsNullOrEmpty(file)) {
                return [];
            }

            using var stream = RootArchiveLoader.GetFileStream(file);
            var data = stream.ToArray();
            var hash = BitConverter.GetBytes(STATION_TYPES_HASH);
            var at = data.AsSpan().IndexOf(hash);
            if (at < 0) {
                return [];
            }

            // After the hash: the list's length, then each string's length and its bytes (lengths are written as
            // a byte holding twice the value, or four bytes when the low bit is set).
            var position = at + hash.Length;
            var count = ReadCompactLength(data, ref position);
            var types = new List<string>();
            for (var i = 0; i < count && position < data.Length; i++) {
                var length = ReadCompactLength(data, ref position);
                if (length <= 0 || position + length > data.Length) {
                    break;
                }

                types.Add(System.Text.Encoding.ASCII.GetString(data, position, length));
                position += length;
            }

            return [.. types.Where(type => type.Length > 0)];
        }
        catch (Exception e) {
            Logger.Warning("Crafting: could not read the station types of template {0}: {1}", Logger.Args(templateId, e.Message));

            return [];
        }
    }

    private static int ReadCompactLength(byte[] data, ref int position) {
        if (position >= data.Length) {
            return 0;
        }

        if ((data[position] & 1) == 0) {
            return data[position++] >> 1;
        }

        if (position + 4 > data.Length) {
            return 0;
        }

        var value = BitConverter.ToUInt32(data, position);
        position += 4;

        return (int) (value >> 1);
    }

    /// <summary>
    /// Whether a recipe can be crafted here.
    /// </summary>
    internal bool Makes(string recipeName, RecipeTemplate recipe)
        => Anything
        || Recipes.Contains(recipeName, StringComparer.OrdinalIgnoreCase)
        || (recipe.m_adjectives ?? []).Any(adjective => Types.Contains((string) adjective, StringComparer.OrdinalIgnoreCase));

}

/// <summary>
/// How a craft went.
/// </summary>
internal enum CraftError : uint {
    None = 0,
    UnknownRecipe = 1,
    NotKnown = 2,
    WrongStation = 3,
    OnCooldown = 4,
    NoFreeSlot = 5,
    MissingIngredients = 6,
    NothingMade = 7,
    Disabled = 8,
}

/// <summary>
/// What a craft did, for the client's answer and the chat window.
/// </summary>
internal sealed record CraftResult(CraftError Error, string Message, ulong MadeItemId = 0, int Made = 0, CraftingCooldown Slot = null, int Crafted = 0);

/// <summary>
/// The game's crafting.
/// </summary>
internal static class Crafting {

    // [Crafting] Enabled: false sends no recipes or crafting slots to the game and stations do nothing.
    private static readonly bool s_enabled = ConfigurationManager.GetValue("Crafting.Enabled", true);

    // [Crafting] Slots: how many crafting slots every wizard has (the game gives them with the crafting quests).
    private static readonly int s_slots = Math.Clamp(ConfigurationManager.GetValue("Crafting.Slots", 30), 1, 200);

    // [Crafting] CooldownMultiplier: multiplies every recipe's cooldown. 0.5 is the member speed, 0 has no cooldowns.
    private static readonly float s_timerMultiplier = Math.Clamp(ConfigurationManager.GetValue("Crafting.CooldownMultiplier", 0.5f), 0f, 10f);

    // [Crafting] RecipeShopGivesItem: true makes buying a recipe give what it makes, as before crafting existed.
    private static readonly bool s_shopGivesItem = ConfigurationManager.GetValue("Crafting.RecipeShopGivesItem", false);

    // [Crafting] MaxQuantity: the most items one craft makes.
    private static readonly int s_maxQuantity = Math.Clamp(ConfigurationManager.GetValue("Crafting.MaxQuantity", 99), 1, 999);

    private const uint SERIALIZATION_FLAGS = 27;
    private static readonly CoreObjectSerializer s_serializer = new(behaviors: SerializerFlags.None);

    // The station each wizard opened last, which a craft is checked against.
    private static readonly ConcurrentDictionary<ulong, CraftingStation> s_openStations = new();

    // Recipe names to template ids, from the template manifest.
    private static readonly Lazy<Dictionary<string, uint>> s_byName = new(IndexRecipes);
    private static readonly ConcurrentDictionary<string, uint> s_searched = new(StringComparer.OrdinalIgnoreCase);

    // [Crafting] AnswerFailedCrafts: also answer a failed craft in the crafting window (a blank Info box).
    private static readonly bool s_answerFailed = ConfigurationManager.GetValue("Crafting.AnswerFailedCrafts", false);

    internal static bool AnswerFailedCrafts => s_answerFailed;

    internal static bool Enabled => s_enabled;

    internal static int Slots => s_slots;

    /// <summary>The crafting slots the client is told the wizard has.</summary>
    internal static int BonusSlots => s_enabled ? s_slots : 0;

    internal static float TimerMultiplier => s_timerMultiplier;

    internal static bool ShopGivesItem => s_shopGivesItem || !s_enabled;

    #region Recipes

    private static Dictionary<string, uint> IndexRecipes() {
        var index = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in CoreObjectFactory.TemplateManifest?.m_serializedTemplates ?? []) {
            string file = location?.m_filename;
            if (string.IsNullOrEmpty(file) || file.IndexOf("Recipe", StringComparison.OrdinalIgnoreCase) < 0) {
                continue;
            }

            var name = file[(file.LastIndexOf('/') + 1)..];
            name = name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            index.TryAdd(name, location.m_id);
        }

        Logger.Information("Crafting: {0} recipe files in the template manifest.", Logger.Args(index.Count));

        return index;
    }

    /// <summary>
    /// The recipe of a template id, or null when the id is no recipe.
    /// </summary>
    internal static RecipeTemplate RecipeOf(uint id) {
        try {
            return id == 0 ? null : CoreObjectFactory.GetCoreTemplate(id) as RecipeTemplate;
        }
        catch (Exception exception) {
            Logger.Warning("Crafting: could not load recipe {0}: {1}", Logger.Args(id, exception.Message));

            return null;
        }
    }

    /// <summary>
    /// Finds a recipe by its name (its file's name, which is what the client and the shops name it by).
    /// </summary>
    internal static bool TryFind(string name, out uint id, out RecipeTemplate recipe) {
        id = 0;
        recipe = null;
        if (string.IsNullOrWhiteSpace(name)) {
            return false;
        }

        name = name.Trim();
        if (uint.TryParse(name, out var number) && RecipeOf(number) is { } byId) {
            (id, recipe) = (number, byId);

            return true;
        }

        if (s_byName.Value.TryGetValue(name, out id) && RecipeOf(id) is { } indexed) {
            recipe = indexed;

            return true;
        }

        id = s_searched.GetOrAdd(name, Search);
        recipe = RecipeOf(id);

        return recipe is not null;
    }

    /// <summary>
    /// The names of the game's recipes that contain some text, for .recipe find.
    /// </summary>
    internal static List<string> NamesContaining(string text, int max)
        => [.. s_byName.Value.Keys.Where(name => name.Contains(text ?? "", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Take(max)];

    /// <summary>How many recipe files the game has.</summary>
    internal static int RecipeCount => s_byName.Value.Count;

    /// <summary>Every recipe file of the game, by its name and template id (the vendor presets read them).</summary>
    internal static IEnumerable<KeyValuePair<string, uint>> AllRecipes => s_byName.Value;

    private static uint Search(string name) {
        var file = "/" + name + ".xml";

        return CoreObjectFactory.TryGetCoreTemplateID(location => location is not null
                && ((string) location.m_filename)?.EndsWith(file, StringComparison.OrdinalIgnoreCase) == true, out var id)
            && RecipeOf(id) is not null ? id : 0;
    }

    /// <summary>The name the client knows a recipe by.</summary>
    internal static string NameOf(uint id, RecipeTemplate recipe) {
        string name = recipe?.m_recipeName;
        if (!string.IsNullOrWhiteSpace(name)) {
            return name;
        }

        return World.CustomNpcs.RecipeNameOf(id) ?? id.ToString();
    }

    /// <summary>The number the client names a recipe by (MSG_EQUIPRECIPE, MSG_RECIPEREMOVE).</summary>
    internal static uint NameIdOf(uint id, RecipeTemplate recipe) => StringHash.Compute(NameOf(id, recipe));

    /// <summary>The recipe a client number names among the wizard's recipes, or 0.</summary>
    internal static uint KnownIdOf(ServerAlchemyBehavior alchemy, uint number)
        => (alchemy.RecipeIds ?? []).FirstOrDefault(id => id == number || NameIdOf(id, RecipeOf(id)) == number);

    private static ClientRecipe ClientRecipeOf(uint id, RecipeTemplate recipe) => new() {
        m_globalID = RandomGen.GenerateGUID(),
        m_templateID = new GID(id),
        m_recipeNameID = NameIdOf(id, recipe),
        m_displayKey = recipe.m_displayKey,
        m_fScale = 1f,
    };

    /// <summary>The wizard's recipes for the client's recipe bag.</summary>
    internal static List<CoreObject> ClientRecipesOf(ServerAlchemyBehavior alchemy) {
        if (!s_enabled) {
            return [];
        }

        var recipes = new List<CoreObject>();
        foreach (var id in alchemy.RecipeIds ?? []) {
            if (RecipeOf(id) is { } recipe) {
                recipes.Add(ClientRecipeOf(id, recipe));
            }
        }

        return recipes;
    }

    /// <summary>The crafting slots in use, for the client's crafting slot bag.</summary>
    internal static List<CoreObject> ClientSlotsOf(ServerAlchemyBehavior alchemy) {
        if (!s_enabled) {
            return [];
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        alchemy.Cooldowns?.RemoveAll(slot => slot.FinishedAt <= now);

        return [.. (alchemy.Cooldowns ?? []).Select(ClientSlotOf)];
    }

    private static CraftingSlot ClientSlotOf(CraftingCooldown slot) => new() {
        m_globalID = new GID(slot.SlotId),
        m_templateID = new GID(slot.RecipeId),
        m_recipeName = slot.RecipeName ?? "",
        m_timeFinished = (int) Math.Min(int.MaxValue, slot.FinishedAt),
        m_fScale = 1f,
    };

    /// <summary>
    /// Teaches a wizard a recipe and puts it in their recipe book.
    /// </summary>
    /// <returns>True when the recipe was new to them.</returns>
    internal static bool Learn(IActorRef session, Wizard wizard, uint id) {
        var recipe = RecipeOf(id);
        if (recipe is null || wizard?.AlchemyBehavior is null) {
            return false;
        }

        var alchemy = wizard.AlchemyBehavior;
        alchemy.RecipeIds ??= [];
        if (alchemy.RecipeIds.Contains(id)) {
            return false;
        }

        alchemy.RecipeIds.Add(id);
        WizardCollection.UpdateCharacterItems(wizard);
        Logger.Information("Crafting: {0} learned recipe {1} ({2}).", Logger.Args(wizard.CharId, id, NameOf(id, recipe)));

        if (s_enabled && session is not null && s_serializer.Serialize(ClientRecipeOf(id, recipe), SERIALIZATION_FLAGS, out var data)) {
            session.Tell(new WIZARD_12_PROTOCOL.MSG_RECIPEADD { GlobalID = wizard.GameObjectID, Data = data });
        }

        return true;
    }

    /// <summary>
    /// Takes a recipe out of a wizard's recipe book.
    /// </summary>
    internal static bool Forget(IActorRef session, Wizard wizard, uint id) {
        var alchemy = wizard?.AlchemyBehavior;
        if (alchemy?.RecipeIds is null || !alchemy.RecipeIds.Remove(id)) {
            return false;
        }

        var nameId = NameIdOf(id, RecipeOf(id));
        alchemy.EquippedRecipes?.RemoveAll(equipped => equipped == nameId || equipped == id);
        WizardCollection.UpdateCharacterItems(wizard);
        session?.Tell(new WIZARD_12_PROTOCOL.MSG_RECIPEREMOVE { GlobalID = wizard.GameObjectID, RecipeID = nameId });

        return true;
    }

    #endregion

    #region Stations

    /// <summary>
    /// Opens the game's crafting window for a station.
    /// </summary>
    internal static void OpenStation(IActorRef session, Wizard wizard, CraftingStation station) {
        if (session is null || wizard is null || station is null) {
            return;
        }

        if (!s_enabled) {
            Logger.Information("Crafting: {0} used station {1}, but [Crafting] Enabled is off.", Logger.Args(wizard.CharId, station.Name));

            return;
        }

        s_openStations[wizard.CharId] = station;
        ExpireSlots(session, wizard);

        var recipes = station.Recipes.ToList();
        var types = station.Types.ToList();
        if (station.Anything) {
            types = [.. CraftingStation.AllTypes];
            recipes.AddRange((wizard.AlchemyBehavior?.RecipeIds ?? []).Select(id => NameOf(id, RecipeOf(id))));
        }

        var allowed = new RecipeTypeList {
            m_allowedTypes = [.. types.Distinct(StringComparer.OrdinalIgnoreCase).Select(type => (ByteString) type)],
            m_recipeList = [.. recipes.Distinct(StringComparer.OrdinalIgnoreCase).Select(recipe => (ByteString) recipe)],
            m_failedRequirementsList = [],
            m_noCooldown = station.NoCooldown,
            m_noCategories = station.NoCategories,
        };

        // The client reads this list as a versionable object (ClientAlchemyStationWindow: its serializer is made with
        // versionable set), so a plain one came out empty and the window listed every recipe the wizard knows
        // (Niduus, 2026-10-25: "Still being used as a universal crafting station instead of deckathalon").
        var serializer = new ObjectSerializer(Versionable: true, Behaviors: SerializerFlags.None);
        if (!serializer.Serialize(allowed, 4, out var data)) {
            Logger.Error("Crafting: could not write the recipes of station {0}.", Logger.Args(station.Name));

            return;
        }

        session.Tell(new WIZARD_12_PROTOCOL.MSG_ALCHEMYSTATION {
            AllowedRecipes = data,
            TitleOverride = station.Title ?? "",
            CreateButtonOverride = station.CreateButton ?? "",
            SegmentedMessage = 0,
            LastSegment = 0,
        });
        Logger.Information("Crafting: {0} opened station {1} ({2}; types {3}; {4} recipes listed; no cooldown {5}).",
            Logger.Args(wizard.CharId, station.Name, station.ObjectId, string.Join(", ", types), recipes.Count, station.NoCooldown));
    }

    /// <summary>The station a wizard opened last, or null.</summary>
    internal static CraftingStation OpenedBy(ulong charId) => s_openStations.GetValueOrDefault(charId);

    /// <summary>Forgets the station a wizard had open (logging out).</summary>
    internal static void Close(ulong charId) => s_openStations.TryRemove(charId, out _);

    /// <summary>
    /// Frees the crafting slots whose cooldown is over and tells the client.
    /// </summary>
    /// <returns>How many slots were freed.</returns>
    internal static int ExpireSlots(IActorRef session, Wizard wizard) {
        var cooldowns = wizard?.AlchemyBehavior?.Cooldowns;
        if (cooldowns is null || cooldowns.Count == 0) {
            return 0;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var done = cooldowns.Where(slot => slot.FinishedAt <= now).ToList();
        foreach (var slot in done) {
            cooldowns.Remove(slot);
            session?.Tell(new WIZARD_12_PROTOCOL.MSG_CRAFTINGSLOTREMOVE { GlobalID = wizard.GameObjectID, ItemID = slot.SlotId });
        }

        if (done.Count > 0) {
            WizardCollection.UpdateCharacterItems(wizard);
        }

        return done.Count;
    }

    #endregion

    #region Crafting

    private sealed record Need(Ingredient Ingredient, INGREDIENT_TYPE Kind, uint Id, int Count, string Name);

    /// <summary>
    /// Crafts a recipe at the station the wizard has open.
    /// </summary>
    /// <param name="session">The wizard's session, which forwards to the client.</param>
    /// <param name="wizard">The wizard.</param>
    /// <param name="characterObject">The wizard's game object, for loot tables.</param>
    /// <param name="recipeName">The recipe's name, as the client sends it.</param>
    /// <param name="quantity">How many to make.</param>
    internal static CraftResult Craft(IActorRef session, Wizard wizard, CoreObject characterObject, string recipeName, int quantity) {
        if (!s_enabled) {
            return new(CraftError.Disabled, "Crafting is turned off in the server settings ([Crafting] Enabled).");
        }

        if (!TryFind(recipeName, out var id, out var recipe)) {
            return new(CraftError.UnknownRecipe, $"There is no recipe called {recipeName}.");
        }

        var name = NameOf(id, recipe);
        var alchemy = wizard.AlchemyBehavior;
        alchemy.RecipeIds ??= [];
        alchemy.Cooldowns ??= [];
        var station = OpenedBy(wizard.CharId) ?? new CraftingStation(0, "none", [], [], false, false, "", "");
        var listedHere = station.Recipes.Contains(name, StringComparer.OrdinalIgnoreCase);
        if (!alchemy.RecipeIds.Contains(id) && !listedHere) {
            return new(CraftError.NotKnown, $"You don't know the recipe {name} yet. Buy it at a recipe shop first.");
        }

        if (!station.Makes(name, recipe)) {
            Logger.Warning("Crafting: {0} crafts {1} at station {2}, which does not list it (types {3}); allowed anyway.",
                Logger.Args(wizard.CharId, name, station.Name, string.Join(", ", station.Types)));
        }

        ExpireSlots(session, wizard);
        var cooldown = station.NoCooldown ? 0 : (long) Math.Round(Math.Max(0, (int) recipe.m_cookTime) * s_timerMultiplier);
        quantity = Math.Clamp(quantity, 1, s_maxQuantity);
        // Niduus (2026-10-24): "you can make as much as you want, it's just each craft takes a crafting slot for
        // cooldown". As in the game, a recipe cooling down can be crafted again in another free slot; each item made
        // fills one slot, so a craft makes at most as many as there are free slots.
        if (cooldown > 0) {
            var free = s_slots - alchemy.Cooldowns.Count;
            if (free <= 0) {
                var next = alchemy.Cooldowns.Min(slot => slot.FinishedAt);
                var left = TimeSpan.FromSeconds(Math.Max(1, next - DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                return new(CraftError.NoFreeSlot, $"All {s_slots} of your crafting slots are in use. The next one is free in {Describe(left)}.");
            }

            quantity = Math.Min(quantity, free);
        }

        var needs = NeedsOf(recipe, quantity);
        var missing = needs.Where(need => Has(wizard, need) < need.Count).ToList();
        if (missing.Count > 0) {
            var list = string.Join(", ", missing.Select(need => $"{need.Count - Has(wizard, need)} more {need.Name}"));
            return new(CraftError.MissingIngredients, $"You need {list} to craft {name}.");
        }

        foreach (var need in needs) {
            Take(session, wizard, need);
        }

        var (madeId, made) = Give(session, wizard, characterObject, recipe, quantity);
        if (made == 0) {
            Logger.Error("Crafting: recipe {0} ({1}) made nothing for {2}; its ingredients were used up.",
                Logger.Args(id, name, wizard.CharId));
            return new(CraftError.NothingMade, $"{name} made nothing; tell the server owner its recipe is not supported yet.");
        }

        CraftingCooldown slot = null;
        for (var i = 0; cooldown > 0 && i < quantity; i++) {
            var filled = new CraftingCooldown {
                SlotId = RandomGen.GenerateGUID().Full,
                RecipeId = id,
                RecipeName = name,
                FinishedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + cooldown,
            };
            slot ??= filled;
            alchemy.Cooldowns.Add(filled);
            if (s_serializer.Serialize(ClientSlotOf(filled), SERIALIZATION_FLAGS, out var data)) {
                session.Tell(new WIZARD_12_PROTOCOL.MSG_CRAFTINGSLOTADD { GlobalID = wizard.GameObjectID, Data = data });
            }
        }

        WizardCollection.UpdateCharacterItems(wizard);
        Logger.Information("Crafting: {0} crafted {1} x{2} ({3}) at {4}; cooldown {5} s.",
            Logger.Args(wizard.CharId, name, quantity, id, station.Name, cooldown));

        return new(CraftError.None, null, madeId, made, slot, quantity);
    }

    private static string Describe(TimeSpan time)
        => time.TotalHours >= 1 ? $"{(int) time.TotalHours} h {time.Minutes} min"
         : time.TotalMinutes >= 1 ? $"{(int) time.TotalMinutes} min {time.Seconds} s"
         : $"{time.Seconds} s";

    private static List<Need> NeedsOf(RecipeTemplate recipe, int quantity) {
        var needs = new List<Need>();
        foreach (var ingredient in recipe.m_ingredients ?? []) {
            if (ingredient is null || ingredient.m_quantity <= 0) {
                continue;
            }

            var count = ingredient.m_quantity * quantity;
            switch (ingredient.m_ingredientType) {
                case INGREDIENT_TYPE.INGREDIENT_TREASURE: {
                    string spellName = ingredient.m_spellTemplate;
                    var spell = string.IsNullOrWhiteSpace(spellName) || !SpellFactory.Exists(spellName) ? null : SpellFactory.GetSpell(spellName);
                    var spellId = spell is null ? (uint) ingredient.m_itemID.MParts.TemplateId : (uint) spell.m_templateID;
                    needs.Add(new(ingredient, ingredient.m_ingredientType, spellId, count, spellName ?? spellId.ToString()));
                    break;
                }
                default: {
                    var itemId = ingredient.m_itemID.MParts.TemplateId;
                    needs.Add(new(ingredient, ingredient.m_ingredientType, itemId, count, DisplayOf(itemId)));
                    break;
                }
            }
        }

        return needs;
    }

    private static string DisplayOf(uint templateId) {
        try {
            if (CoreObjectFactory.GetCoreTemplate(templateId) is GameObjectTemplate template) {
                string name = template.m_objectName;
                return string.IsNullOrWhiteSpace(name) ? templateId.ToString() : name;
            }
        }
        catch (Exception) {
            // The id is logged instead.
        }

        return templateId.ToString();
    }

    private static int Has(Wizard wizard, Need need) => need.Kind switch {
        INGREDIENT_TYPE.INGREDIENT_REAGENT => wizard.AlchemyBehavior.GetReagent(need.Id)?.m_quantity ?? 0,
        INGREDIENT_TYPE.INGREDIENT_PET_SNACK => wizard.PetSnackBehavior?.GetSnack(need.Id)?.m_quantity ?? 0,
        INGREDIENT_TYPE.INGREDIENT_TREASURE => (wizard.SpellbookBehavior?.TreasureCardTemplateIds ?? []).Count(card => card == need.Id),
        INGREDIENT_TYPE.INGREDIENT_ITEM => BackpackItems(wizard, need.Id).Count,
        INGREDIENT_TYPE.INGREDIENT_FISH => (wizard.FishingBehavior?.Basket ?? []).Count(fish => fish.TemplateId == need.Id),
        _ => 0,
    };

    // Items in the backpack, not worn ones.
    private static List<WizClientObjectItem> BackpackItems(Wizard wizard, uint templateId)
        => [.. (wizard.InventoryBehavior?.Items ?? [])
            .Where(item => item is not null && item.m_templateID.MParts.TemplateId == templateId
                        && wizard.EquipmentBehavior?.GetItem(item.m_globalID) is null)];

    private static void Take(IActorRef session, Wizard wizard, Need need) {
        switch (need.Kind) {
            case INGREDIENT_TYPE.INGREDIENT_REAGENT: {
                var reagent = wizard.AlchemyBehavior.GetReagent(need.Id);
                if (reagent is null || !wizard.RemoveReagents(reagent.m_globalID, need.Count, out var left)) {
                    return;
                }

                session.Tell(left.m_quantity > 0
                    ? new WIZARD_12_PROTOCOL.MSG_REAGENTUPDATE { GlobalID = wizard.GameObjectID, ItemID = left.m_globalID, Quantity = left.m_quantity }
                    : new WIZARD_12_PROTOCOL.MSG_REAGENTREMOVE { GlobalID = wizard.GameObjectID, ItemID = left.m_globalID });
                break;
            }
            case INGREDIENT_TYPE.INGREDIENT_PET_SNACK: {
                var snack = wizard.PetSnackBehavior.GetSnack(need.Id);
                if (snack is null || !wizard.RemoveSnack(snack.m_globalID, need.Count, out var left)) {
                    return;
                }

                session.Tell(left.m_quantity > 0
                    ? new PET_9_PROTOCOL.MSG_PETSNACKUPDATE { GlobalID = wizard.GameObjectID, ItemID = left.m_globalID, Quantity = left.m_quantity }
                    : new PET_9_PROTOCOL.MSG_PETSNACKREMOVE { GlobalID = wizard.GameObjectID, ItemID = left.m_globalID });
                break;
            }
            case INGREDIENT_TYPE.INGREDIENT_TREASURE: {
                var hash = CoreObjectFactory.GetCoreTemplate(need.Id) is SpellTemplate spell ? StringHash.Compute(spell.m_name) : 0u;
                for (var i = 0; i < need.Count; i++) {
                    if (!wizard.SpellbookBehavior.RemoveTreasureCard(need.Id)) {
                        break;
                    }

                    WizardCollection.RemoveTreasureCard(wizard, need.Id);
                    session.Tell(new WIZARD_12_PROTOCOL.MSG_REMOVETREASURESPELLFROMBOOK { SpellID = (int) hash, EnchantmentID = 0, Quantity = 1 });
                }
                break;
            }
            case INGREDIENT_TYPE.INGREDIENT_FISH: {
                // The smallest fish of the kind go first.
                var basket = wizard.FishingBehavior?.Basket ?? [];
                foreach (var fish in basket.Where(fish => fish.TemplateId == need.Id).OrderBy(fish => fish.Size).Take(need.Count).ToList()) {
                    basket.Remove(fish);
                    session.Tell(new WIZARDHOUSING_50_PROTOCOL.MSG_DELETEFISH { GlobalID = wizard.GameObjectID, FishTemplateID = fish.TemplateId, Size = fish.Size });
                }

                WizardCollection.UpdateCharacterFishingBehavior(wizard);
                break;
            }
            case INGREDIENT_TYPE.INGREDIENT_ITEM: {
                foreach (var item in BackpackItems(wizard, need.Id).Take(need.Count)) {
                    if (wizard.RemoveItemFromInventory(item.m_globalID)) {
                        session.Tell(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_REMOVEITEM { GlobalID = wizard.GameObjectID, ItemID = item.m_globalID });
                    }
                }
                break;
            }
        }
    }

    // Gives what a recipe makes: an item, a treasure card or a roll of its loot table.
    private static (ulong MadeId, int Made) Give(IActorRef session, Wizard wizard, CoreObject characterObject, RecipeTemplate recipe, int quantity) {
        var results = new DropTableResult { DropTableId = "Crafting" };
        var itemId = recipe.m_itemID.MParts.TemplateId;
        string spellName = recipe.m_spellName;
        string lootTable = recipe.m_itemLootTable;
        if (itemId != 0) {
            // A reagent or snack goes into its bag as one stack; any other item is one backpack item per copy.
            var stacks = CoreObjectFactory.GetCoreTemplate(itemId) is ReagentItemTemplate or PetSnackItemTemplate;
            results.Items.AddRange(stacks
                ? [new DropItemResult { ItemId = itemId.ToString(), Quantity = quantity }]
                : Enumerable.Range(0, quantity).Select(_ => new DropItemResult { ItemId = itemId.ToString(), Quantity = 1 }));
        }
        else if (!string.IsNullOrWhiteSpace(spellName) && SpellFactory.Exists(spellName) && SpellFactory.GetSpell(spellName) is { } spell) {
            itemId = (uint) spell.m_templateID;
            results.Items.Add(new DropItemResult { ItemId = itemId.ToString(), Quantity = quantity });
        }
        else if (!string.IsNullOrWhiteSpace(lootTable)) {
            for (var i = 0; i < quantity; i++) {
                var rolled = DropTableRoller.Roll([lootTable], session, characterObject, wizard);
                results.Items.AddRange(rolled.Items);
                results.GoldAmount += rolled.GoldAmount;
            }
        }

        if (results.Items.Count == 0 && results.GoldAmount == 0) {
            return (0, 0);
        }

        var before = (wizard.InventoryBehavior?.Items ?? []).Select(item => item.m_globalID.Full).ToHashSet();
        var granted = LootGranter.Grant(session, wizard, results);
        var made = granted.Items.Sum(item => Math.Max(1, item.Quantity)) + granted.TreasureCards.Sum(card => card.Quantity)
                 + (granted.GoldAmount > 0 ? 1 : 0);
        var madeId = (wizard.InventoryBehavior?.Items ?? []).Select(item => item.m_globalID.Full).FirstOrDefault(gid => !before.Contains(gid));

        return (madeId, made);
    }

    #endregion

}
