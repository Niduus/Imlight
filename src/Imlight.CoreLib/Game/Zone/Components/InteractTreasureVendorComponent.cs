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
 * INTERACT TREASURE VENDOR COMPONENT
 * ========================================================================
 * 
 * PURPOSE:
 * Manages treasure card vendor interaction mechanics for NPCs, handling 
 * treasure card shop inventory and player interactions.
 * 
 * Created by: Reasonix
 * Version: KALI 1.0
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.Events;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractTreasureVendorComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    public string ServiceName     => "TreasureShopService";
    public string NpcIcon         => null;
    public string NpcNameKey      => null;
    public string NpcTextKey      => null;
    public WizBangs WizBang       => WizBangs.Shopping;
    public string StateName       => "Shop"; // Forbids the player from moving.
    public string InteractWizBang => "Registrar";
    public string DisplayKey      => "GUI_TreasureShop";

    private List<TreasureCardEntry> _inventory = [];
    private string _title = "Treasure Cards";

    // The Deckathalon's cards (DeckathalonShopCards): Harold adds them to his own, a Deckathalon TC NPC sells only them.
    private enum EventCards { None, Added, Only }
    private EventCards _eventCards;

    public static bool ShouldAttachToEntity(CoreTemplate template)
        // Attach if the template is an NPC and has a treasure card inventory in SpiralDB, or is one of the
        // test shops below.
        => template is GameObjectTemplate goTemplate
        && goTemplate.m_behaviors.Any(x => x is NPCBehaviorTemplate)
        && (NpcTreasureCardInventoryCollection.TryGetInventory(goTemplate.m_templateID, out _)
            || TestShopFor(goTemplate) is not TestShop.None);

    // Test shop: Abner K. Doodle sells every enchantment treasure card. (Every other treasure card is in the
    // Bazaar's treasure card tab, see AuctionHouseService.)
    private enum TestShop { None, AllEnchantments }
    private const int MIN_TEST_PRICE = 1;
    private const int MAX_TEST_PRICE = 200;
    private static readonly System.Lazy<List<TreasureCardEntry>> s_allEnchantments = new(() => BuildTestInventory(true));

    private static TestShop TestShopFor(GameObjectTemplate template)
        => IsAbner(template) ? TestShop.AllEnchantments : TestShop.None;

    /// <summary>
    /// True for Abner K. Doodle in the Commons, who runs the enchantment test shop and trainer.
    /// </summary>
    internal static bool IsAbner(GameObjectTemplate template) {
        var names = $"{template.m_objectName} {Imlight.CoreLib.Shared.Resources.Locale.GetEnglishName(template.m_displayName.ToString())}";

        return names.Contains("Abner", System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every treasure card (or only the enchantments) with a school, one per name, priced from its own cost
    /// but kept between 1 and 200 gold. The shop sorts them into its school tabs; stock never runs out.
    /// </summary>
    private static List<TreasureCardEntry> BuildTestInventory(bool enchantmentsOnly) {
        var entries = new List<TreasureCardEntry>();
        var seen = new HashSet<string>();
        foreach (var (_, spell) in Imlight.CoreLib.Game.Spells.SpellFactory.AllSpellTemplates()) {
            string name = spell.m_name;
            if (string.IsNullOrEmpty(name) || !(spell.m_Treasure || name.EndsWith(" TC"))
                || string.IsNullOrEmpty((string) spell.m_sMagicSchoolName) || string.IsNullOrEmpty((string) spell.m_displayName)
                || (enchantmentsOnly && !Imlight.CoreLib.Game.Combat.CombatEnchantments.IsEnchantment(spell))
                || !seen.Add(name)) {
                continue;
            }

            entries.Add(new TreasureCardEntry {
                SpellName = name,
                Price = System.Math.Clamp(spell.m_baseCost, MIN_TEST_PRICE, MAX_TEST_PRICE),
            });
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.SpellName, b.SpellName));
        Logger.Information("Test treasure shop ({0}) has {1} cards.",
            Logger.Args(enchantmentsOnly ? "enchantments" : "all treasure cards", entries.Count));

        return entries;
    }

    public override void OnStart() {
        // A vendor of the Customs tab sells its own treasure cards at its own prices, under its own name.
        if (CustomNpcs.For(Entity.Info?.m_zoneTag) is { } custom) {
            if (string.Equals(custom.Window, DeckathalonShopCards.WINDOW, StringComparison.OrdinalIgnoreCase)) {
                _eventCards = EventCards.Only;
            }

            _inventory = [.. CustomNpcs.TreasureCardsOf(custom).Select(card => new TreasureCardEntry { SpellName = card.Name, Price = card.Price })];
            _title = CustomNpcs.TitleFor(custom) is { Length: > 0 } title ? title : _title;
            Logger.Information("Custom vendor {0} ({1}): {2} treasure cards, title {3}.", Logger.Args(custom.Id, custom.Name, _inventory.Count, _title));

            return;
        }

        // The window's name bar shows a game text, so a shop keeper's shop is named after the shop keeper.
        if (Entity.Template is GameObjectTemplate keeper && !keeper.m_displayName.IsEmpty) {
            _title = keeper.m_displayName;
        }

        if (Entity.Template is GameObjectTemplate template && TestShopFor(template) is not TestShop.None) {
            _inventory = s_allEnchantments.Value;

            return;
        }

        if (!NpcTreasureCardInventoryCollection.TryGetInventory(Entity.ActiveGameObject.m_templateID, out var inventory)) {
            Logger.Error("Failed to get treasure card inventory for NPC {0}",
                Logger.Args(Entity.ActiveGameObject.m_templateID));

            return;
        }

        _inventory = inventory.TreasureCards;
        if (Entity.ActiveGameObject.m_templateID.MParts.TemplateId == DeckathalonShopCards.HAROLD_TEMPLATE_ID) {
            _eventCards = EventCards.Added;
        }
    }

    // What the shop sells now: its own cards, and the Deckathalon's while one is on.
    private List<TreasureCardEntry> Stock => _eventCards switch {
        EventCards.Only => DeckathalonShopCards.Current(),
        EventCards.Added => [.. _inventory.Concat(DeckathalonShopCards.Current()).DistinctBy(card => card.SpellName)],
        _ => _inventory,
    };

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard _)
        => _eventCards == EventCards.Only && Stock.Count == 0 ? [] : [
            new TreasureShopOption {
                m_displayKey = DisplayKey,
                m_iconKey = "TreasureShop",
                m_serviceName = ServiceName,
            }
        ];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        SendTreasureShopOfferings(playerActor);
        SendPlayerIntoWizbang(playerObject.m_globalID);
        SendPlayerIntoState(playerObject.m_globalID);
    }

    public bool HasSpell(string spellName)
        => Stock.Any(x => x.SpellName == spellName);

    /// <summary>
    /// Checks if the vendor carries a spell with the given hash.
    /// The hash is computed from the spell name via StringHash.Compute.
    /// </summary>
    public bool HasSpell(uint spellHash)
        => Stock.Any(x => StringHash.Compute(x.SpellName) == spellHash);

    /// <summary>
    /// Gets the spell name for a given spell hash, or null if not found.
    /// </summary>
    public string GetSpellName(uint spellHash)
        => Stock.Find(x => StringHash.Compute(x.SpellName) == spellHash)?.SpellName;

    public int GetSpellPrice(string spellName) {
        var entry = Stock.Find(x => x.SpellName == spellName);
        return entry?.Price ?? 0;
    }

    /// <summary>
    /// Gets the spell price for a given spell hash.
    /// </summary>
    public int GetSpellPrice(uint spellHash) {
        var spellName = GetSpellName(spellHash);
        return spellName != null ? GetSpellPrice(spellName) : 0;
    }

    private void SendTreasureShopOfferings(IActorRef playerActor) {
        var shopOffering = new TreasureShopOffering() {
            m_treasureShopTitle = _title,
            m_treasureSpellNames = Stock.Select(x => (ByteString)x.SpellName).ToList(),
            m_crownShop = false,
            m_pvpCurrencyShop = false,
            m_pvpTourneyCurrencyShop = false,
        };

        // Serialize the offerings and send them to the player.
        var serializer = new ObjectSerializer(
            Versionable: false,
            Behaviors: SerializerFlags.None
        );
        if (!serializer.Serialize(shopOffering, (PropertyFlags) 31, out var serializedShopList)) {
            Logger.Error("Failed to serialize treasure shop offering for NPC {0}",
                Logger.Args(Entity.ActiveGameObject.m_templateID));

            return;
        }

        var shopListMsg = new WIZARD_12_PROTOCOL.MSG_TREASURESHOPLIST() {
            GlobalID = Entity.ActiveGameObject.m_globalID.Full,
            Data = serializedShopList
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
