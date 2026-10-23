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
 * PET ITEM FEEDING
 * ========================================================================
 *
 * PURPOSE:
 * The backpack's "Feed to pet": the pet eats an item and the owner gets gold and the loot of the item's
 * PetFeedingRewardConfig.xml tier, with the loot popup.
 *
 * USAGE EXAMPLE:
 * var problem = PetItemFeeding.Feed(SessionActor.ActorRef, wizard, message.PetID, message.FedObjectID, SendToSocket);
 *
 * NOTE:
 * The item's value is its template's m_baseCost (the gold price quick sell takes 5% of), and the gold is that value
 * times m_fGoldScalingFactor (0.05 in the game files), rounded up like quick sell. A FLAG_CrownsOnly item uses the
 * crowns tiers. Every tier in the game files names one loot table and costs no energy. Feeding gives the pet no XP.
 *
 * TODO:
 * - The PetFeeding*LT loot tables are missing from SpiralDB today, so a snack of a rank that fits the tier stands in.
 * - Whether the client removes the item itself after the echoed MSG_FEEDINVENTORYITEM is unverified; the server
 *   sends MSG_INVENTORYBEHAVIOR_REMOVEITEM after it either way.
 *
 * Version: KALI 1.0
 * Last Updated: 10/22/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Pet;

/// <summary>
/// Feeding backpack items to a pet for gold and loot.
/// </summary>
internal static class PetItemFeeding {

    private const string CROWNS_ONLY_ADJECTIVE = "FLAG_CrownsOnly";
    private const string NO_SELL_ADJECTIVE = "FLAG_NoSell";
    private const string SNACK_FOLDER = "ObjectData/PetSnacks/";

    private static readonly ConcurrentDictionary<string, byte> s_missingTables = new(StringComparer.Ordinal);

    // Every snack by its rank, for the stand-in reward when a tier's loot tables are missing.
    private static readonly Lazy<List<(uint Id, int Rank)>> s_snacks = new(() => [.. HouseVendors.TemplatesIn<PetSnackItemTemplate>(SNACK_FOLDER)
        .Where(snack => !((string) snack.Template.m_objectName ?? "").Contains("TEST"))
        .Select(snack => (snack.Id, (int) snack.Template.m_rank))]);

    /// <summary>
    /// Feeds a backpack item to a pet: the item is removed, energy is taken if the tier costs it, and the gold and
    /// loot are granted with the loot popup.
    /// </summary>
    /// <param name="session">The owner's session, which routes the loot messages.</param>
    /// <param name="wizard">The owner.</param>
    /// <param name="petId">The pet the client named, 0 for the worn pet.</param>
    /// <param name="itemId">The item to feed.</param>
    /// <param name="send">Sends a message to the owner's game.</param>
    /// <returns>Null when the pet ate the item, otherwise the reason in plain words. Nothing changes on a refusal.</returns>
    internal static string Feed(IActorRef session, Wizard wizard, ulong petId, ulong itemId, Action<IMessage> send) {
        var pet = (petId != 0 ? PetCare.FindPet(wizard, petId) : null) ?? PetCare.GetWornPet(wizard);
        var behavior = PetProgress.GetBehavior(pet);
        if (behavior is null) {
            return "You need a pet to feed items to.";
        }

        if (behavior.m_level == 0) {
            return "Your pet has to hatch before it can eat items.";
        }

        var item = wizard.InventoryBehavior.GetItem(itemId);
        if (wizard.EquipmentBehavior.HasItemEquipped(itemId)) {
            return "Take the item off before feeding it to your pet.";
        }

        if (item is null) {
            return "That item is not in your backpack.";
        }

        if (ItemHelper.IsLocked(item)) {
            Logger.Warning("Item lock: refused to feed {0} of {1} to pet {2}, the item is locked.",
                Logger.Args(ItemHelper.DescribeForLog(item), wizard.CharId, pet.m_globalID));

            return "That item is locked. Unlock it before feeding it to your pet.";
        }

        if (wizard.IsFilledVault(item.m_globalID)) {
            return "That vault still holds items. Empty it before feeding it to your pet.";
        }

        if (item.m_globalID == pet.m_globalID || PetProgress.GetBehavior(item) is not null) {
            return "Pets can't eat other pets.";
        }

        if (item is ClientPetSnackItem or ClientReagentItem) {
            return "Snacks and reagents can't be fed this way. Feed snacks from the snack window.";
        }

        if (CoreObjectFactory.GetCoreTemplate(item.m_templateID) is not WizItemTemplate template) {
            return "Your pet can't eat that item.";
        }

        var adjectives = (template.m_adjectiveList ?? []).Select(adjective => (string) adjective).ToList();
        if (adjectives.Contains(NO_SELL_ADJECTIVE)) {
            return "That item can't be sold, so your pet won't eat it.";
        }

        // The singleton loads at boot; touching it here makes sure the tiers are in on first use either way.
        if (PetFeedingRewardConfig.StandardTiers.Count == 0) {
            _ = PetFeedingRewardConfig.Instance;
        }

        var value = (int) template.m_baseCost;
        var crowns = adjectives.Contains(CROWNS_ONLY_ADJECTIVE);
        var tiers = crowns ? PetFeedingRewardConfig.CrownsTiers : PetFeedingRewardConfig.StandardTiers;
        if (tiers.Count == 0) {
            Logger.Error("Pet item feeding: no {0} reward tiers are loaded from PetFeedingRewardConfig.xml.",
                Logger.Args(crowns ? "crowns" : "standard"));

            return "Your pet isn't hungry for items right now.";
        }

        var tier = PetFeedingRewardConfig.FindTier(value, crowns);
        if (tier is null) {
            return "That item is worth nothing, so your pet won't eat it.";
        }

        var energyCost = tier.m_bCostsEnergy && PetRules.ItemFeedCostsEnergy ? PetRules.ItemFeedEnergyCost : 0;
        var energy = wizard.PetOwnerBehavior.Energy;
        if (energy < energyCost) {
            return $"Your pet needs {energyCost} energy to eat that and has {energy}.";
        }

        if (!wizard.RemoveItemFromInventory(item.m_globalID)) {
            return "Your pet couldn't eat that item.";
        }

        // The echo first, while the client still has the item: it likely plays the eating from it.
        send(new GAME_5_PROTOCOL.MSG_FEEDINVENTORYITEM {
            PetID = pet.m_globalID,
            FedObjectID = item.m_globalID,
        });
        send(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_REMOVEITEM {
            GlobalID = wizard.GameObjectID,
            ItemID = item.m_globalID,
        });

        if (energyCost > 0) {
            wizard.UpdateEnergy(energy - energyCost);
            send(new PET_9_PROTOCOL.MSG_PETENERGYTICK {
                GlobalID = wizard.GameObjectID,
                Energy = wizard.PetOwnerBehavior.Energy,
                MaxEnergy = wizard.GameStats.GetClientTypeAlternative().m_energyMax,
                TickTime = (int) wizard.PetOwnerBehavior.LastEnergyTickEpoch,
            });
        }

        var tierIndex = tiers.ToList().IndexOf(tier);
        var (loot, rolledTable) = RollLoot(session, wizard, tier, tierIndex);

        // Gold by value, rounded up like quick sell (which takes the same 5%).
        var gold = (int) Math.Ceiling(value * PetFeedingRewardConfig.GoldScalingFactor);
        loot.GoldAmount += gold;
        LootGranter.GrantAndDisplay(session, wizard, loot);

        Logger.Information("Pet item feeding: {0} fed {1} (value {2}, {3} tier {4} from {5}) to pet {6}; energy {7} ({8} -> {9}); rolled {10}; got {11} gold ({2} x {12}) and {13}.",
            Logger.Args(wizard.CharId, ItemHelper.DescribeForLog(item), value, crowns ? "crowns" : "standard", tierIndex,
                tier.m_minIntrinsicValue, pet.m_globalID, energyCost, energy, wizard.PetOwnerBehavior.Energy, rolledTable,
                loot.GoldAmount, PetFeedingRewardConfig.GoldScalingFactor,
                loot.Items.Count == 0 ? "no items" : string.Join(", ", loot.Items.Select(i => $"{i.ItemId} x{i.Quantity}"))));

        return null;
    }

    /// <summary>
    /// Rolls one of a tier's loot tables, or the stand-in snack when SpiralDB has none of them.
    /// </summary>
    /// <returns>The rolled loot and the name of what was rolled, for the log.</returns>
    private static (DropTableResult Loot, string Rolled) RollLoot(IActorRef session, Wizard wizard, PetFeedingRewardInfo tier, int tierIndex) {
        var named = tier.m_lootTables.Select(table => table.ToString()).Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
        var present = named.Where(name => DropTableFiles.Get(name) is not null).ToList();
        var missing = named.Except(present).Where(name => s_missingTables.TryAdd(name, 0)).ToList();
        if (missing.Count > 0) {
            Logger.Warning("Pet item feeding: the loot tables {0} are not in SpiralDB, so a snack stands in for them.",
                Logger.Args(string.Join(", ", missing)));
        }

        // The game files name one table per tier, so one is rolled; were there several, one is picked at random.
        if (present.Count > 0) {
            var table = present[Random.Shared.Next(present.Count)];

            return (DropTableRoller.Roll([table], session, wizard.GameObject, wizard), table);
        }

        var snack = StandInSnack(tierIndex);
        var loot = new DropTableResult { DropTableId = "PetItemFeedingStandIn" };
        if (snack != 0) {
            loot.Items.Add(new DropItemResult { ItemId = snack.ToString(), Quantity = 1 });
        }

        return (loot, snack == 0 ? "nothing (no snacks found)" : $"stand-in snack {snack}");
    }

    /// <summary>
    /// A random snack whose rank fits the tier: ranks 1 and 2 for the lowest tier, 3 and 4 for the next, and so on.
    /// </summary>
    /// <returns>The snack's template id, 0 when no snack is known.</returns>
    private static uint StandInSnack(int tierIndex) {
        var snacks = s_snacks.Value;
        if (snacks.Count == 0) {
            return 0;
        }

        var highest = 2 * (Math.Max(0, tierIndex) + 1);
        var fitting = snacks.Where(snack => snack.Rank >= highest - 1 && snack.Rank <= highest).ToList();
        if (fitting.Count == 0) {
            fitting = snacks;
        }

        return fitting[Random.Shared.Next(fitting.Count)].Id;
    }

}
