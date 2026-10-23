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
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractVendorComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    public string ServiceName     => "WizShoppingService";
    public string NpcIcon         => null;
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
    public int? PriceOf(uint templateId)
        => _customPrices is not null && _customPrices.TryGetValue(templateId, out var price) ? price : FixedPrice;

    // A recipe shop's recipes by name (WizShoppingBehavior.m_recipeList): the recipe window lists these, not m_shopList.
    private List<ByteString> _recipeList = [];

    public static bool ShouldAttachToEntity(CoreTemplate template)
        // Attach if the template is an NPC and has an inventory in Dragon database,
        // or if the template is a vendor as per game client data.
        => template is GameObjectTemplate goTemplate
        && goTemplate.m_behaviors.Any(x => x is NPCBehaviorTemplate)
        && (NpcInventoryCollection.TryGetNpcInventory(goTemplate.m_templateID, out _)
        || WorldVendorLocations.IsVendor(goTemplate.m_templateID)
        || InteractTreasureVendorComponent.IsAbner(goTemplate)
        || HouseVendors.IsSellerNpc(goTemplate.m_templateID));

    public override void OnStart() {
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

        if (!NpcInventoryCollection.TryGetNpcInventory(Entity.ActiveGameObject.m_templateID, out var inventory)) {
            Logger.Error("Failed to get vendor inventory for NPC {0}",
                Logger.Args(Entity.ActiveGameObject.m_templateID));

            return;
        }

        _inventory = ShopOrder.Alphabetical(inventory.Inventory);

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

        if (!string.IsNullOrWhiteSpace(inventory.ShopTitle)) {
            _shopTitle = inventory.ShopTitle;
        }
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

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard _) {
        if (TestOption is { } test) {
            return [TestOptionFor(test)];
        }

        // A custom vendor of treasure cards only has the treasure card shop (InteractTreasureVendorComponent).
        if (_custom is not null && _inventory.Count == 0 && _custom.TreasureCards.Count > 0) {
            return [];
        }

        ServiceOptionBase option = _window switch {
            HouseVendors.ShopWindow.Snack => new PetSnackShopOption(),
            HouseVendors.ShopWindow.Reagent => new ReagentShopOption(),
            HouseVendors.ShopWindow.Jewel => new JewelShopOption(),
            HouseVendors.ShopWindow.Recipe => new RecipeShopOption(),
            HouseVendors.ShopWindow.Furniture => new HousingShopOption(),
            HouseVendors.ShopWindow.Mount => new MountShopOption(),
            _ => new EquipmentShopOption(),
        };
        option.m_displayKey = OptionKeyOf(_window);
        option.m_iconKey = NpcIcon;
        option.m_serviceName = ServiceName;

        return [option];
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
        result.m_iconKey = NpcIcon;
        result.m_serviceName = ServiceName;

        return result;
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        if (_custom is not null) {
            Logger.Information("Custom vendor {0} ({1}) clicked by {2}: sending {3} items ({4}) in the {5} window, shop kind {6}.",
                Logger.Args(_custom.Id, _custom.Name, playerCharacter?.PlayerNameBehavior?.GetWizardName(), _inventory?.Count ?? 0,
                    string.Join(", ", _custom.Items.Select(e => $"{e.ID} for {e.Price} gold")), _window, TestShopType ?? _furnitureShop));
        }

        SendShopOfferings(playerActor);
        SendPlayerIntoWizbang(playerObject.m_globalID);
        SendPlayerIntoState(playerObject.m_globalID);
    }

    public bool HasItem(GID itemGID)
        => _inventory is not null && _inventory.Any(x => x.MParts.TemplateId == itemGID.MParts.TemplateId);

    private void SendShopOfferings(IActorRef playerActor) {
        var shopOffering = new WizShopOffering() {
            m_sellModifier = 0.05f,
            // A shop with no title of its own is called after its kind ("Jewel Shop"), not "Robe Shop" (KrocNPC_00000013).
            m_shopTitle = string.IsNullOrWhiteSpace(_shopTitle) || _shopTitle == "Unknown Shop" ? OptionKeyOf(_window) : _shopTitle,
            m_shopList = _inventory,
            m_recipeList = _recipeList,
            // Changes the type of currency that is used
            // 0 - Gold
            // 1 - PvP tickets
            m_shopType = _shopType,
            m_furnitureShop = TestShopType ?? _furnitureShop,
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
