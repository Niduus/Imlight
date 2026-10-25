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
 * INTERACT VENDOR 
 * ========================================================================
 * 
 * PURPOSE:
 * Manages vendor interaction mechanics for NPCs, handling shop inventory 
 * and player shopping interactions in the game world.
 * 
 * USAGE EXAMPLE:
 * 
 * NOTE:
 * Dynamically resolves vendor inventory from World database.
 * 
 * TODO:
 * - Investigate CSR test shop configuration
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractVendorComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    public string ServiceName     => "WizShoppingService";
    public string NpcIcon         => null;
    // The game's icon of a shop's menu option (the loot bag), as its own shop keepers show.
    private const string OPTION_ICON = "Shopping";
    public string NpcNameKey      => null;
    public string NpcTextKey      => null;
    public WizBangs WizBang       => WizBangs.Shopping;
    public string StateName       => "Shop"; // Forbids the player from moving.
    public string InteractWizBang => "Registrar";
    public string DisplayKey      => "GUI_ShopOptionEquipment";


    private List<GID> _inventory;
    private string _shopTitle = "Unknown Shop";
    private int _shopType = 0;
    // The shop's kind, which picks the game's shop window (HouseVendors.ShopWindow: 0 equipment, 1 furniture,
    // 2 reagents, 3 recipes, 4 mounts, 5 pet snacks, 6 combined, 7 jewels). The robe shop window opened for every shop
    // while this was left at 0.
    private int _furnitureShop = 0;
    private bool _csrTestShop = false;
    // The window, which also gives the menu option its kind and words ("Housing Shop", "Reagent Shop"...). Abner's
    // lectern shop sells furniture, so it is a furniture shop (an equipment shop does not list it).
    private HouseVendors.ShopWindow _window = HouseVendors.ShopWindow.Equipment;

    /// <summary>The price of everything this NPC sells, or null for the items' own prices.</summary>
    public int? FixedPrice { get; private set; }

    // A vendor of the Customs tab (CustomNpcs): its own list, each item at its own price in gold.
    private CustomNpcs.CustomNpc _custom;
    private Dictionary<uint, int> _customPrices;

    /// <summary>What an item costs at this NPC: a custom vendor's own price, the fixed price, or null for the item's own.</summary>
    public int? PriceOf(uint templateId) {
        UseChanges();

        return _changePrices is not null && _changePrices.TryGetValue(templateId, out var changed) ? changed
            : _customPrices is not null && _customPrices.TryGetValue(templateId, out var price) ? price : FixedPrice;
    }

    // The Customs tab's changes to this game vendor (NpcChanges), used over what it sells by itself (_base).
    private sealed record BaseShop(List<GID> Inventory, HouseVendors.ShopWindow Window, int Kind, List<ShopSection> MoreShops);
    private BaseShop _base;
    private int _changesVersion = -1;
    private Dictionary<uint, int> _changePrices;
    private bool _hideShop;

    // A recipe shop's recipes by name (WizShoppingBehavior.m_recipeList): the recipe window lists these, not m_shopList.
    private List<ByteString> _recipeList = [];

    // A shop keeper's other shops, each its own menu option and window after the first (LiveShopLists: Guillermel has
    // an equipment, a housing and a reagent shop).
    private sealed record ShopSection(List<GID> Inventory, List<ByteString> Recipes, string Title, int ShopType, int Kind, HouseVendors.ShopWindow Window);
    private readonly List<ShopSection> _moreShops = [];

    public static bool ShouldAttachToEntity(CoreTemplate template)
        // Attach if the template is an NPC and has an inventory in Dragon database,
        // or if the template is a vendor as per game client data.
        => template is GameObjectTemplate goTemplate
        && goTemplate.m_behaviors.Any(x => x is NPCBehaviorTemplate)
        && (NpcInventoryCollection.TryGetNpcInventory(goTemplate.m_templateID, out _)
        || OwnShopList(goTemplate) is { Count: > 0 }
        || LiveShopLists.For(goTemplate.m_templateID) is not null
        || WorldVendorLocations.IsVendor(goTemplate.m_templateID)
        || InteractTreasureVendorComponent.IsAbner(goTemplate)
        || HouseVendors.IsSellerNpc(goTemplate.m_templateID)
        || IsRecipeShop(goTemplate)
        || NpcChanges.For(goTemplate.m_templateID) is { HideShop: false, Items.Count: > 0 });

    /// <summary>
    /// What an NPC sells in the live game: the shop list of its own shop (WizShoppingBehavior m_shopList), or null.
    /// </summary>
    internal static List<ulong> OwnShopList(GameObjectTemplate template)
        => template.m_behaviors?.OfType<WizShoppingBehaviorTemplate>().FirstOrDefault()?.m_shopList is { Count: > 0 } list ? list : null;

    /// <summary>
    /// Whether an NPC's own shop is a recipe shop (WizShoppingBehavior m_furnitureShop 3 with an m_recipeList).
    /// </summary>
    internal static bool IsRecipeShop(GameObjectTemplate template)
        => template.m_behaviors?.OfType<WizShoppingBehaviorTemplate>().FirstOrDefault() is { } shop
        && HouseVendors.WindowOf(shop.m_furnitureShop) == HouseVendors.ShopWindow.Recipe
        && shop.m_recipeList is { Count: > 0 };

    public override void OnStart() {
        StartShop();
        _base = new BaseShop(_inventory ?? [], _window, _furnitureShop, [.. _moreShops]);
    }

    // What the Customs tab changed (NpcChanges), applied to what the vendor sells by itself when the file has changed
    // since the last time: items added or the whole list replaced, items taken out, prices, the window, or no shop.
    private void UseChanges() {
        if (_custom is not null || _base is null) {
            return;
        }

        var version = NpcChanges.Version;
        if (version == _changesVersion) {
            return;
        }

        _changesVersion = version;
        _inventory = _base.Inventory;
        _window = _base.Window;
        _furnitureShop = _base.Kind;
        _moreShops.Clear();
        _moreShops.AddRange(_base.MoreShops);
        _changePrices = null;
        _hideShop = false;

        var templateId = Entity.ActiveGameObject.m_templateID.MParts.TemplateId;
        if (NpcChanges.For(templateId) is not { } change) {
            return;
        }

        _hideShop = change.HideShop;
        var removed = change.RemoveItems.ToHashSet();
        var items = change.ReplacesItems ? [] : _inventory.Where(item => !removed.Contains(item.MParts.TemplateId)).ToList();
        var added = KnownItems(change.Items.Where(e => !removed.Contains(e.ID)).Select(e => new GID((ulong) e.ID)))
            .Where(item => items.All(had => had.MParts.TemplateId != item.MParts.TemplateId));
        _inventory = ShopOrder.Alphabetical(items.Concat(added));
        _changePrices = change.Items.Where(e => e.Price > 0).ToDictionary(e => e.ID, e => e.Price);

        var more = change.ReplacesItems ? [] : _base.MoreShops
            .Select(shop => shop with { Inventory = [.. shop.Inventory.Where(item => !removed.Contains(item.MParts.TemplateId))] })
            .Where(shop => shop.Inventory.Count > 0).ToList();
        _moreShops.Clear();
        _moreShops.AddRange(more);

        if (!string.IsNullOrWhiteSpace(change.Window)) {
            var window = CustomNpcs.WindowFor(new CustomNpcs.CustomNpc {
                Window = change.Window,
                Items = [.. _inventory.Select(item => new CustomNpcs.Entry { ID = item.MParts.TemplateId })],
            });
            if (window != HouseVendors.ShopWindow.Recipe) {
                _window = window;
                _furnitureShop = HouseVendors.KindOf(window);
            }
        }

        Logger.Information("Vendor {0} ({1}) uses the Customs tab's changes: {2}{3} items, {4} window{5}.", Logger.Args(templateId,
            change.Name, change.ReplacesItems ? "only its " : "", _inventory.Count, _window, _hideShop ? ", shop hidden" : ""));
    }

    private void StartShop() {
        if (CustomNpcs.For(Entity.Info?.m_zoneTag) is { } custom) {
            StartCustom(custom);

            return;
        }

        // Hao sells the snacks and a house seller its kind (HouseVendors), whatever the NPC sells elsewhere.
        if (HouseVendors.StockFor(Entity.ActiveGameObject.m_templateID.MParts.TemplateId, Entity.Info?.m_zoneTag) is { } stock) {
            _inventory = ShopOrder.Alphabetical(stock.Items.Select(id => new GID((ulong) id)));
            _window = stock.Window;
            FixedPrice = HouseVendors.Price;
            _furnitureShop = stock.Window is HouseVendors.ShopWindow.Recipe ? 0 : HouseVendors.KindOf(stock.Window);
            if (HouseVendors.ShopLookFor(stock.Window) is { } look) {
                _shopTitle = string.IsNullOrWhiteSpace(look.m_shopTitle) ? _shopTitle : look.m_shopTitle;
                _shopType = look.m_shopType;
                _furnitureShop = look.m_furnitureShop != 0 ? look.m_furnitureShop : _furnitureShop;
                _csrTestShop = look.m_CSRTestShop;
            }
            else if (Entity.Template?.m_behaviors?.OfType<WizShoppingBehaviorTemplate>().FirstOrDefault() is { } own) {
                // The seller's own shop (Doctor Purreau: "Purreau's Potions and More"). A recipe shop opens the game's
                // recipe window, which lists the recipes by name.
                _shopTitle = string.IsNullOrWhiteSpace(own.m_shopTitle) ? _shopTitle : own.m_shopTitle;
                if (stock.Window == HouseVendors.ShopWindow.Recipe) {
                    _shopType = own.m_shopType;
                    _furnitureShop = HouseVendors.KindOf(HouseVendors.ShopWindow.Recipe);
                    _csrTestShop = own.m_CSRTestShop;
                    var sold = HouseVendors.RecipesOf(Entity.ActiveGameObject.m_templateID.MParts.TemplateId).Select(recipe => recipe.Name).ToHashSet();
                    _recipeList = [.. (own.m_recipeList ?? []).Where(name => sold.Contains((string) name))];
                }
            }
            Logger.Information("Seller {0} ({1}): {2} items, {3} recipes, {4} window, shop kind {5}, title {6}.",
                Logger.Args(Entity.ActiveGameObject.m_templateID.MParts.TemplateId, Entity.Info?.m_zoneTag, _inventory.Count, _recipeList.Count, _window, _furnitureShop, _shopTitle));

            return;
        }

        // Abner also sells the trainer tome, the furniture that brings a trainer into a house (HouseTrainers).
        if (Entity.Template is GameObjectTemplate template && InteractTreasureVendorComponent.IsAbner(template)
            && !NpcInventoryCollection.TryGetNpcInventory(Entity.ActiveGameObject.m_templateID, out _)) {
            _inventory = [new GID(HouseTrainers.ItemTemplateId)];
            _window = HouseVendors.ShopWindow.Furniture;
            _furnitureShop = HouseVendors.KindOf(_window);

            return;
        }

        // A recipe shop of the game (Eudora Tangletree, Zasha EmberForge...) sells the recipes of its own list, which
        // the recipe window shows by name; the recipes go into the recipe book (Game/Crafting).
        if (Entity.Template is GameObjectTemplate recipeShop && IsRecipeShop(recipeShop)) {
            StartRecipeShop(recipeShop);

            return;
        }

        // The live game's list is the NPC's own shop list in the game files. SpiralDB's copy is older (some shops
        // there lack items or still sell retired ones), so it is only used for a shop with no list of its own; a list
        // of our own (spiraldb-extra, such as the garden vendors' seeds) still comes first.
        var templateId = Entity.ActiveGameObject.m_templateID.MParts.TemplateId;
        var ownList = Entity.Template is GameObjectTemplate shopKeeper ? OwnShopList(shopKeeper) : null;
        NPCInventory inventory = null;
        if (ownList is not null && !SpiralDB.IsExtraNpcInventory(templateId)) {
            _inventory = ShopOrder.Alphabetical(KnownItems(ownList.Select(id => new GID(id))));
        }
        else if (!SpiralDB.IsExtraNpcInventory(templateId) && LiveShopLists.For(templateId) is { Count: > 0 } liveShops) {
            StartLiveShops(templateId, liveShops);

            return;
        }
        else if (NpcInventoryCollection.TryGetNpcInventory(Entity.ActiveGameObject.m_templateID, out inventory)) {
            _inventory = ShopOrder.Alphabetical(KnownItems(inventory.Inventory));
        }
        else {
            // A shop keeper with nothing known to sell shows no shop, rather than an empty one.
            Logger.Warning("Vendor {0} ({1}) has no shop list in the game files or SpiralDB; no shop shown.",
                Logger.Args(templateId, Entity.ActiveGameObject.m_debugName));
            _inventory = [];

            return;
        }

        var shoppingBehavior = Entity.Template?.m_behaviors?.OfType<WizShoppingBehaviorTemplate>().FirstOrDefault();
        if (shoppingBehavior != null) {
            if (!string.IsNullOrWhiteSpace(shoppingBehavior.m_shopTitle)) {
                _shopTitle = shoppingBehavior.m_shopTitle;
            }
            _shopType = shoppingBehavior.m_shopType;
            _furnitureShop = shoppingBehavior.m_furnitureShop;
            _csrTestShop = shoppingBehavior.m_CSRTestShop;
            // The menu option of the shop's kind, as the game's own (a recipe shop's list is its m_recipeList).
            _window = HouseVendors.WindowOf(_furnitureShop) is var window and not HouseVendors.ShopWindow.Recipe ? window : _window;
        }

        if (!string.IsNullOrWhiteSpace(inventory?.ShopTitle)) {
            _shopTitle = inventory.ShopTitle;
        }
    }

    // Only ids the game files have an item for: SpiralDB lists a few shops with spell ids instead (Harold Argleston,
    // whose treasure cards are his treasure card shop), and an equipment shop of those opened empty. A spell is a
    // template too, so only templates in ObjectData count.
    private List<GID> KnownItems(IEnumerable<GID> items) {
        var all = items.ToList();
        var known = all.Where(item => CoreObjectFactory.HasObjectTemplate(item.MParts.TemplateId)).ToList();
        if (known.Count < all.Count) {
            Logger.Warning("Vendor {0} ({1}): left out {2} of {3} ids the game files have no item for (spells or unknown ids).",
                Logger.Args(Entity.ActiveGameObject.m_templateID.MParts.TemplateId, Entity.ActiveGameObject.m_debugName, all.Count - known.Count, all.Count));
        }

        return known;
    }

    // A shop keeper whose shop list in the game files is empty sells what it sells live (LiveShopLists), in one menu
    // option and window per shop.
    private void StartLiveShops(uint templateId, IReadOnlyList<LiveShop> shops) {
        var sections = new List<ShopSection>();
        foreach (var shop in shops) {
            var window = HouseVendors.WindowOf(shop.Kind);
            List<GID> inventory;
            List<ByteString> recipes = [];
            if (window == HouseVendors.ShopWindow.Recipe) {
                var found = shop.Recipes.Select(name => (Name: name, Id: HouseVendors.RecipeIdOf(name, templateId))).Where(r => r.Id is not null).ToList();
                inventory = [.. found.Select(r => new GID((ulong) r.Id.Value))];
                recipes = [.. found.Select(r => (ByteString) r.Name)];
            }
            else {
                inventory = ShopOrder.Alphabetical(KnownItems(shop.Items.Select(id => new GID((ulong) id))));
            }

            if (inventory.Count > 0) {
                sections.Add(new ShopSection(inventory, recipes, shop.Title, shop.ShopType, shop.Kind, window));
            }
        }

        if (sections.Count == 0) {
            _inventory = [];

            return;
        }

        var first = sections[0];
        _inventory = first.Inventory;
        _recipeList = first.Recipes;
        _shopTitle = string.IsNullOrWhiteSpace(first.Title) ? _shopTitle : first.Title;
        _shopType = first.ShopType;
        _furnitureShop = first.Kind;
        _window = first.Window;
        _moreShops.AddRange(sections.Skip(1));
        Logger.Information("Shop keeper {0} ({1}): {2} live shops ({3}).", Logger.Args(templateId, Entity.ActiveGameObject.m_debugName,
            sections.Count, string.Join(", ", sections.Select(section => $"{section.Window} with {section.Inventory.Count}"))));
    }

    private void StartRecipeShop(GameObjectTemplate template) {
        var shop = template.m_behaviors.OfType<WizShoppingBehaviorTemplate>().First();
        var recipes = HouseVendors.RecipesOf(template.m_templateID);
        var sold = recipes.Select(recipe => recipe.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _inventory = [.. recipes.Select(recipe => new GID((ulong) recipe.Id))];
        _recipeList = [.. shop.m_recipeList.Where(name => sold.Contains((string) name))];
        _window = HouseVendors.ShopWindow.Recipe;
        _furnitureShop = shop.m_furnitureShop;
        _shopType = shop.m_shopType;
        _csrTestShop = shop.m_CSRTestShop;
        _shopTitle = string.IsNullOrWhiteSpace(shop.m_shopTitle) ? _shopTitle : shop.m_shopTitle;
        Logger.Information("Recipe shop {0}: {1} recipes of {2} listed, title {3}.",
            Logger.Args(template.m_templateID, _recipeList.Count, shop.m_recipeList.Count, _shopTitle));
    }

    // A vendor of the Customs tab sells exactly its list, in the window its items belong in (or the one it names).
    private void StartCustom(CustomNpcs.CustomNpc custom) {
        _custom = custom;
        _customPrices = custom.Items.ToDictionary(e => e.ID, e => e.Price);
        // An ID that is not an item (or a recipe) is left out: the game's window has nothing to show for it.
        var notItems = custom.Items.Where(e => CustomNpcs.TemplateOf(e.ID) is not (WizItemTemplate or RecipeTemplate)).Select(e => e.ID).ToList();
        if (notItems.Count > 0) {
            Logger.Warning("Custom vendor {0} ({1}): left out {2}, not items.", Logger.Args(custom.Id, custom.Name, string.Join(", ", notItems)));
        }

        _inventory = ShopOrder.Alphabetical(custom.Items.Where(e => !notItems.Contains(e.ID)).Select(e => new GID((ulong) e.ID)));
        // The window and kind of the game's own shop keepers of that kind of goods; gold is the currency. The title is
        // the vendor's own name (Niduus, 2026-10-23: "Vendor's are not using proper UI").
        var window = CustomNpcs.WindowFor(custom);
        _window = window;
        _furnitureShop = HouseVendors.KindOf(window);
        _shopTitle = CustomNpcs.TitleFor(custom) is { Length: > 0 } title ? title : _shopTitle;

        // The recipe window lists its recipes by name.
        if (window == HouseVendors.ShopWindow.Recipe) {
            _recipeList = [.. custom.Items.Select(e => CustomNpcs.RecipeNameOf(e.ID)).Where(name => !string.IsNullOrEmpty(name)).Select(name => (ByteString) name)];
        }

        Logger.Information("Custom vendor {0} ({1}) in {2}: {3} items, {4} window ({5}), shop kind {6}, title {7}{8}.",
            Logger.Args(custom.Id, custom.Name, custom.Zone, _inventory.Count, window, custom.Window, _furnitureShop, _shopTitle,
                custom.TreasureCards.Count > 0 ? $", and {custom.TreasureCards.Count} treasure cards in the treasure card shop" : ""));
    }

    // `.shoptest` (Niduus, 2026-10-19): every shop opens with this shop type and menu option, to find what makes the
    // game open its reagent, snack and jewel windows instead of the robe shop window.
    internal static int? TestShopType;
    internal static string TestOption;

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard wizard) {
        UseChanges();
        if (TestOption is { } test) {
            return [TestOptionFor(test)];
        }

        // A garden trainer's spell list is shown alone: the game takes training options beside a shop as the spell picker.
        if (InteractTrainerComponent.IsShowingSpellOptions(Entity, wizard) || _hideShop) {
            return [];
        }

        // A custom vendor of treasure cards only has the treasure card shop (InteractTreasureVendorComponent).
        if (_custom is not null && _inventory.Count == 0 && (_custom.TreasureCards.Count > 0
            || string.Equals(_custom.Window, Events.DeckathalonShopCards.WINDOW, StringComparison.OrdinalIgnoreCase))) {
            return [];
        }

        // Nothing to sell: no shop option (and so no loot bag over the NPC's head).
        if (_custom is null && (_inventory?.Count ?? 0) == 0 && _recipeList.Count == 0) {
            return [];
        }

        return [OptionFor(_window), .. _moreShops.Select(shop => OptionFor(shop.Window))];
    }

    private ServiceOptionBase OptionFor(HouseVendors.ShopWindow window) {
        ServiceOptionBase option = window switch {
            HouseVendors.ShopWindow.Snack => new PetSnackShopOption(),
            HouseVendors.ShopWindow.Reagent => new ReagentShopOption(),
            HouseVendors.ShopWindow.Jewel => new JewelShopOption(),
            HouseVendors.ShopWindow.Recipe => new RecipeShopOption(),
            HouseVendors.ShopWindow.Furniture => new HousingShopOption(),
            HouseVendors.ShopWindow.Mount => new MountShopOption(),
            _ => new EquipmentShopOption(),
        };
        option.m_displayKey = OptionKeyOf(window);
        option.m_iconKey = OPTION_ICON;
        option.m_serviceName = ServiceName;

        return option;
    }

    // The words of the shop's menu option, the game's own (GUI.lang: "Housing Shop", "Reagent Shop"...). Every shop
    // said "Equipment Shop" before, and the furniture shops' GUI_HousingShop is no line of the game.
    private static string OptionKeyOf(HouseVendors.ShopWindow window) => window switch {
        HouseVendors.ShopWindow.Snack => "GUI_ShopOptionPetSnack",
        HouseVendors.ShopWindow.Reagent => "GUI_ShopOptionReagent",
        HouseVendors.ShopWindow.Jewel => "GUI_ShopOptionJewel",
        HouseVendors.ShopWindow.Recipe => "GUI_ShopOptionRecipe",
        HouseVendors.ShopWindow.Furniture => "GUI_ShopOptionHousing",
        HouseVendors.ShopWindow.Mount => "GUI_ShopOptionMount",
        _ => "GUI_ShopOptionEquipment",
    };

    internal static readonly string[] TestOptions = ["equipment", "reagent", "snack", "jewel", "housing", "recipe", "mount"];

    private ServiceOptionBase TestOptionFor(string option) {
        ServiceOptionBase result = option switch {
            "reagent" => new ReagentShopOption(),
            "snack" => new PetSnackShopOption(),
            "jewel" => new JewelShopOption(),
            "housing" => new HousingShopOption(),
            "recipe" => new RecipeShopOption(),
            "mount" => new MountShopOption(),
            _ => new EquipmentShopOption(),
        };
        result.m_displayKey = option == "housing" ? "GUI_ShopOptionHousing" : DisplayKey;
        result.m_iconKey = OPTION_ICON;
        result.m_serviceName = ServiceName;

        return result;
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        UseChanges();
        if (_custom is not null) {
            Logger.Information("Custom vendor {0} ({1}) clicked by {2}: sending {3} items ({4}) in the {5} window, shop kind {6}.",
                Logger.Args(_custom.Id, _custom.Name, playerCharacter?.PlayerNameBehavior?.GetWizardName(), _inventory?.Count ?? 0,
                    string.Join(", ", _custom.Items.Select(e => $"{e.ID} for {e.Price} gold")), _window, TestShopType ?? _furnitureShop));
        }

        // The first option is the NPC's main shop; any after it are its other shops.
        var shop = TestOption is null && serviceOptionIndex > 0 && serviceOptionIndex <= _moreShops.Count
            ? _moreShops[(int) serviceOptionIndex - 1]
            : new ShopSection(_inventory, _recipeList, _shopTitle, _shopType, _furnitureShop, _window);
        SendShopOfferings(playerActor, shop);
        SendPlayerIntoWizbang(playerObject.m_globalID);
        SendPlayerIntoState(playerObject.m_globalID);
    }

    public bool HasItem(GID itemGID)
        => UseChangesThen(true) && (_inventory is not null && _inventory.Any(x => x.MParts.TemplateId == itemGID.MParts.TemplateId))
        || _moreShops.Any(shop => shop.Inventory.Any(x => x.MParts.TemplateId == itemGID.MParts.TemplateId));

    private bool UseChangesThen(bool value) {
        UseChanges();

        return value;
    }

    private void SendShopOfferings(IActorRef playerActor, ShopSection shop) {
        var shopOffering = new WizShopOffering() {
            m_sellModifier = 0.05f,
            // A shop with no title of its own is called after its kind ("Jewel Shop"), not "Robe Shop" (KrocNPC_00000013).
            m_shopTitle = string.IsNullOrWhiteSpace(shop.Title) || shop.Title == "Unknown Shop" ? OptionKeyOf(shop.Window) : shop.Title,
            m_shopList = shop.Inventory,
            m_recipeList = shop.Recipes,
            // Changes the type of currency that is used
            // 0 - Gold
            // 1 - PvP tickets
            m_shopType = shop.ShopType,
            m_furnitureShop = TestShopType ?? shop.Kind,
            // todo: figure this out for QA
            m_CSRTestShop = _csrTestShop,
        };

        // Serialize the offerings and send them to the player.
        var serializer = new ObjectSerializer(
            Versionable: false,
            Behaviors: SerializerFlags.None
        );
        if (!serializer.Serialize(shopOffering, 4, out var serializedShopList)) {
            Logger.Error("Failed to serialize shop offering for NPC {0}",
                Logger.Args(Entity.ActiveGameObject.m_templateID));

            return;
        }

        var shopListMsg = new WIZARD_12_PROTOCOL.MSG_SHOPLIST() {
            GlobalID = Entity.ActiveGameObject.m_globalID.Full,
            Data = serializedShopList,
        };
        playerActor.Tell(shopListMsg);
    }

    private void SendPlayerIntoWizbang(ulong playerObjID) {
        // Create the wiz bang message, and wrap it in a broadcast message.
        var wizBangMsg = new GAME_5_PROTOCOL.MSG_WIZBANG {
            WizBangID = (uint) WizBang,
            GameObjectID = playerObjID
        };
        var broadcastMsg = new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = wizBangMsg,
            Selfless = false,
        };

        Entity.ZoneRef.Tell(broadcastMsg);
    }

    private void SendPlayerIntoState(ulong playerObjID) {
        // Create the change state message, and wrap it in a broadcast message.
        var changeStateMsg = new GAME_5_PROTOCOL.MSG_ENTERSTATE {
            State = StringHash.Compute(StateName),
            GameObjectID = playerObjID
        };
        var broadcastMsg = new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Message = changeStateMsg,
            Selfless = false,
        };

        Entity.ZoneRef.Tell(broadcastMsg);
    }

}
