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
 * PACK MANAGER
 * ========================================================================
 *
 * PURPOSE:
 * Handles rolling booster pack loot tables, granting the resulting items
 * (spells, reagents, pet snacks, equipment/housing items) to a wizard, and
 * dispatching the pack reward confirmation packet to the client.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.CoreLib.Game.Pet;
using Imlight.Common;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.World;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.Game.Bank;

namespace Imlight.CoreLib.Game.Packs;

public static class PackManager {

    private static readonly CoreObjectSerializer s_coreObjectSerializer = new(behaviors: SerializerFlags.None);
    private static readonly ObjectSerializer s_objectSerializer = new(Versionable: false);

    /// <summary>
    /// Opens a booster pack, grants the drops to the player, and sends the client confirmation.
    /// </summary>
    public static void OpenPack(IActorRef sessionActorRef, Wizard wizard, ulong packTemplateId) {
        if (CoreObjectFactory.GetCoreTemplate(packTemplateId) is not BoosterPackTemplate boosterTemplate) {
            Logger.Warning("Template {0} is not a BoosterPackTemplate.", Logger.Args(packTemplateId));
            return;
        }

        var lootTables = boosterTemplate.m_lootTables;
        if (lootTables is null || lootTables.Count == 0) {
            Logger.Warning("Booster pack {0} has no m_lootTables defined in TemplateManifest.", Logger.Args(packTemplateId));
            return;
        }

        Logger.Information("Opening booster pack {0} ({1}), slots: {2}.", Logger.Args(packTemplateId,
            (string) boosterTemplate.m_displayName, string.Join(", ", lootTables.Select(t => (string) t))));

        var lootItems = new List<LootInfo>(lootTables.Count);
        var lootRarities = new List<LootRarity>(lootTables.Count);

        foreach (var tableName in lootTables) {
            var rollTable = (string) tableName;
            if (DropTableCollection.GetDropTable(rollTable) is null) {
                rollTable = StandInTable(boosterTemplate, rollTable);
            }

            var rollResult = rollTable is null ? new DropTableResult { Items = [] }
                : DropTableRoller.Roll([rollTable], sessionActorRef, wizard.GameObject, wizard);
            if (rollResult.Items.Count == 0 && rollTable is null && IsSpellementSlot(tableName)
                    && RandomSpellement() is { } spellement) {
                rollResult.Items.Add(new DropItemResult { ItemId = spellement.ToString(), ItemName = "Spellement", Quantity = 1 });
                Logger.Information("Booster pack {0}: slot {1} has no list in SpiralDB, so it gives a random spellement.",
                    Logger.Args(packTemplateId, tableName));
            }

            if (rollResult.Items.Count == 0) {
                Logger.Warning("Booster pack {0}: slot {1} gave nothing.", Logger.Args(packTemplateId, tableName));
                continue;
            }

            foreach (var droppedItem in rollResult.Items) {
                if (!uint.TryParse(droppedItem.ItemId, out var itemTemplateId)) {
                    continue;
                }

                itemTemplateId = PackGearLevels.ForLevel(itemTemplateId, wizard.MagicSchoolBehavior.Level);

                var itemTemplate = CoreObjectFactory.GetCoreTemplate(itemTemplateId);
                var rarity = ResolveRarity(itemTemplate, tableName);
                var count = Math.Max(1, droppedItem.Quantity);

                // The pack window looks a card up by its loot type: a house item sent as a treasure card showed a
                // question mark (Niduus's Friendship Pack, 2026-10-20).
                lootItems.Add(LootFor(itemTemplate, itemTemplateId, count));

                lootRarities.Add(new LootRarity {
                    m_rarity = rarity,
                    m_lootGid = (GID) itemTemplateId,
                    m_odds = 0
                });

                for (var i = 0; i < count; i++) {
                    GrantDroppedItem(sessionActorRef, wizard, itemTemplateId, itemTemplate);
                }

                Logger.Information("Granted {0} ({1}) x{2} from slot {3}",
                    Logger.Args(droppedItem.ItemName, droppedItem.ItemId, count, tableName));
            }
        }

        Logger.Information("Booster pack {0} shows {1} cards for {2} slots.",
            Logger.Args(packTemplateId, lootItems.Count, lootTables.Count));

        var lootInfoList = new LootInfoList {
            m_loot = lootItems,
            m_goldInfo = null,
            m_lootRarityList = new LootRarityList {
                m_loot = lootRarities
            }
        };

        if (!s_objectSerializer.Serialize(lootInfoList, PropertyFlags.Prop_Public, out var serializedLoot)) {
            Logger.Error("Failed to serialize LootInfoList for booster pack {0}.", Logger.Args(packTemplateId));
            return;
        }

        sessionActorRef.Tell(new WIZARD_12_PROTOCOL.MSG_CROWNSBUYCONFIRM {
            Failure = 0,
            WebFailure = 0,
            Credits = wizard.Account.Crowns,
            Data = serializedLoot,
            TemplateID = packTemplateId
        });
    }

    /// <summary>
    /// A slot whose list SpiralDB does not have (TreasureTower03-Heal, Spellements_P_Lore_HH_01) would leave the pack a
    /// card short, so it rolls another of the pack's lists that shares its name up to the last dash, or null.
    /// </summary>
    private static string StandInTable(BoosterPackTemplate pack, string tableName) {
        var cut = tableName.LastIndexOf('-');
        var prefix = cut > 0 ? tableName[..cut] : null;
        var standIn = prefix is null ? null : pack.m_lootTables
            .Select(t => (string) t)
            .FirstOrDefault(t => t != tableName && t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && DropTableCollection.GetDropTable(t) is not null);
        Logger.Warning("Booster pack {0}: slot {1} has no list in SpiralDB{2}.", Logger.Args(pack.m_templateID, tableName,
            standIn is null ? "" : $", so it rolls {standIn} instead"));

        return standIn;
    }

    private static bool IsSpellementSlot(string tableName) => tableName.StartsWith("Spellements", StringComparison.OrdinalIgnoreCase);

    private static uint? RandomSpellement() {
        var spellements = HouseVendors.Sellers.FirstOrDefault(seller => seller.Key == "ReagentShard")?.Items() ?? [];

        return spellements.Count == 0 ? null : spellements[Random.Shared.Next(spellements.Count)];
    }

    private static LootInfo LootFor(CoreTemplate template, uint templateId, int count) => template switch {
        SpellTemplate => new TreasureCardLootInfo {
            m_lootType = LOOT_TYPE.LOOT_TYPE_TREASURE_CARD,
            m_spellID = templateId,
            m_numItems = count
        },
        ReagentItemTemplate => new ReagentLootInfo {
            m_lootType = LOOT_TYPE.LOOT_TYPE_REAGENT,
            m_itemID = templateId,
            m_numItems = count
        },
        PetSnackItemTemplate => new PetSnackLootInfo {
            m_lootType = LOOT_TYPE.LOOT_TYPE_PETSNACK,
            m_itemID = templateId,
            m_numItems = count
        },
        _ => new ItemLootInfo {
            m_lootType = LOOT_TYPE.LOOT_TYPE_ITEM,
            m_itemID = templateId,
            m_numItems = count
        },
    };

    private static void GrantDroppedItem(
        IActorRef sessionActorRef,
        Wizard wizard,
        uint itemTemplateId,
        CoreTemplate itemTemplate) {

        switch (itemTemplate) {
            case SpellTemplate spellTemplate:
                GrantTreasureCard(sessionActorRef, wizard, itemTemplateId, spellTemplate);
                break;

            case ReagentItemTemplate:
                GrantReagent(sessionActorRef, wizard, itemTemplateId);
                break;

            case PetSnackItemTemplate:
                GrantPetSnack(sessionActorRef, wizard, itemTemplateId);
                break;

            default:
                GrantInventoryItem(sessionActorRef, wizard, itemTemplateId);
                break;
        }
    }

    private static void GrantTreasureCard(
        IActorRef sessionActorRef,
        Wizard wizard,
        uint spellTemplateId,
        SpellTemplate spellTemplate) {

        wizard.SpellbookBehavior.AddTreasureCard(spellTemplateId);
        WizardCollection.AddTreasureCard(wizard, spellTemplateId);

        if (!string.IsNullOrEmpty(spellTemplate.m_name)) {
            var spellHash = StringHash.Compute(spellTemplate.m_name);
            sessionActorRef.Tell(new WIZARD_12_PROTOCOL.MSG_ADDTREASURESPELLTOBOOK {
                SpellID = (int) spellHash,
                EnchantmentID = 0
            });
        }
    }

    private static void GrantReagent(IActorRef sessionActorRef, Wizard wizard, uint reagentTemplateId) {
        if (!wizard.AddReagent(reagentTemplateId, out var reagentItem)) {
            Logger.Warning("Could not add reagent {0} to reagent bag.", Logger.Args(reagentTemplateId));
            return;
        }

        var flags = PropertyFlags.Prop_Save | PropertyFlags.Prop_Copy | PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;
        if (!s_coreObjectSerializer.Serialize(reagentItem, flags, out var serializedReagent)) {
            Logger.Warning("Failed to serialize reagent {0} for booster pack grant.", Logger.Args(reagentTemplateId));
            return;
        }

        sessionActorRef.Tell(new WIZARD_12_PROTOCOL.MSG_REAGENTADD {
            GlobalID = wizard.GameObjectID,
            Data = serializedReagent
        });

        sessionActorRef.Tell(new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION {
            ItemGlobalID = reagentItem.m_globalID,
            ItemTemplateID = (uint) reagentItem.m_templateID,
            ItemLocation = 1
        });

        sessionActorRef.Tell(new WIZARD_12_PROTOCOL.MSG_REAGENTUPDATE {
            GlobalID = wizard.GameObjectID,
            ItemID = reagentItem.m_globalID,
            Quantity = reagentItem.m_quantity
        });
    }

    private static void GrantPetSnack(IActorRef sessionActorRef, Wizard wizard, uint snackTemplateId) {
        if (!wizard.AddSnack(snackTemplateId, out var snackItem)) {
            Logger.Warning("Could not add pet snack {0} to snack bag.", Logger.Args(snackTemplateId));
            return;
        }

        if (!s_coreObjectSerializer.Serialize(snackItem, PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit, out var serializedSnack)) {
            Logger.Warning("Failed to serialize pet snack {0} for booster pack grant.", Logger.Args(snackTemplateId));
            return;
        }

        sessionActorRef.Tell(new PET_9_PROTOCOL.MSG_PETSNACKADD {
            GlobalID = wizard.GameObjectID,
            Data = serializedSnack
        });

        sessionActorRef.Tell(new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION {
            ItemGlobalID = snackItem.m_globalID,
            ItemTemplateID = (uint) snackItem.m_templateID,
            ItemLocation = 1
        });

        sessionActorRef.Tell(new PET_9_PROTOCOL.MSG_PETSNACKUPDATE {
            GlobalID = wizard.GameObjectID,
            ItemID = snackItem.m_globalID,
            Quantity = snackItem.m_quantity
        });
    }

    private static void GrantInventoryItem(IActorRef sessionActorRef, Wizard wizard, uint itemTemplateId) {
        // A full backpack sends the item to the bank, as the game does.
        if (!wizard.TryCreateItem(itemTemplateId, out var itemCoreObject)) {
            Logger.Warning("Could not create item {0}.", Logger.Args(itemTemplateId));
            return;
        }

        var placement = ItemPlacement.Place(wizard, itemCoreObject);
        ItemPlacement.Announce(sessionActorRef, wizard, itemCoreObject, placement);
        if (placement != Placement.Backpack) {
            return;
        }

        sessionActorRef.Tell(new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION {
            ItemGlobalID = itemCoreObject.m_globalID,
            ItemTemplateID = (uint) itemCoreObject.m_templateID,
            ItemLocation = 1
        });
    }

    /// <summary>
    /// Resolves the card border rarity:
    /// - Items declare their own rarity via m_rarity.
    /// - Spells inherit rarity from the drop table name.
    /// </summary>
    private static RarityType ResolveRarity(CoreTemplate template, string tableName) {
        if (template is WizItemTemplate wizItem) {
            return wizItem.m_rarity;
        }

        if (tableName.Contains("Epic", StringComparison.OrdinalIgnoreCase)) {
            return RarityType.RT_EPIC;
        }

        if (tableName.Contains("UltraRare", StringComparison.OrdinalIgnoreCase)) {
            return RarityType.RT_ULTRARARE;
        }

        if (tableName.Contains("Rare", StringComparison.OrdinalIgnoreCase)) {
            return RarityType.RT_RARE;
        }

        if (tableName.Contains("Uncommon", StringComparison.OrdinalIgnoreCase)) {
            return RarityType.RT_UNCOMMON;
        }

        return RarityType.RT_COMMON;
    }

}

