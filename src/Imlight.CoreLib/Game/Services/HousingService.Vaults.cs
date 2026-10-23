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
 * HOUSING SERVICE: VAULTS
 * ========================================================================
 *
 * PURPOSE:
 * Does what the windows of the house's storage furniture ask: moves gear, jewels, seeds and treasure cards between the
 * backpack and a Gear Vault, Jewel Vault, Seed Vault (Gardening Shed) or Treasure Card Vault, and puts a treasure card
 * on a Treasure Card Poster.
 *
 * USAGE EXAMPLE:
 * Internal message handlers of HousingService, which holds the house the player is in.
 *
 * NOTE:
 * - The client opens the windows itself and sends only the moves; there is no answer but the vault's patch (see
 *   Game/Housing/HouseVaults), which every player in the house gets and which refreshes an open window.
 * - Every move is one write: the house with the vault, and the backpack or the treasure book, in one transaction. The
 *   backpack or book changes in memory first and is put back when the write fails, so nothing is lost or doubled.
 * - Gear, jewels and seeds keep their item documents while they are in a vault, so they come back as the same items.
 * - Only the owner of the house uses its vaults; a visitor is told so and nothing moves.
 * - A poster shows a card the owner has; the card stays in the treasure book.
 * - Every request is logged with its fields, so a log shows what the game sends.
 *
 * TODO:
 * - Does the poster use the card up in the game? Here it does not.
 * - MSG_REMOVETREASURESPELLFROMVAULT is sent by the bank window's delete, not by a house vault; it is not handled here.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/23/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Bank;
using Imlight.CoreLib.Game.Housing;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.Trading;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal partial class HousingService {

    private const string MaxJewelsAllowedKey = "Character.MaxJewelsAllowed";
    private const int MaxBlobLength = 65535;
    private const int PlainEntryLength = 21;

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_MOVEGEARTOGEARVAULT))]
    private void ReceiveMoveGearToGearVault(WIZARDHOUSING_50_PROTOCOL.MSG_MOVEGEARTOGEARVAULT message) {
        Logger.Information("Vaults: character {CharId} asks to move gear {GearGid} into gear vault {VaultGid}.",
            Logger.Args(GetActiveWizard().CharId, message.GearGID, message.GearVaultGID));
        RunVaultRequest("move gear into a gear vault", () => MoveItemsIn(VaultKind.Gear, message.GearVaultGID, wizard => {
            var item = wizard.InventoryBehavior.GetItem(message.GearGID);
            if (item is null) {
                return (null, wizard.EquipmentBehavior?.HasItemEquipped(message.GearGID) == true
                    ? "Take the item off before you put it in the vault."
                    : null);
            }

            if (!IsGear(item)) {
                return (null, "Only gear goes in the Gear Vault.");
            }

            return ([item], null);
        }));
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_MOVEGEARFROMGEARVAULT))]
    private void ReceiveMoveGearFromGearVault(WIZARDHOUSING_50_PROTOCOL.MSG_MOVEGEARFROMGEARVAULT message) {
        byte[] gearData = message.GearData;
        Logger.Information("Vaults: character {CharId} asks to take gear out of gear vault {VaultGid} ({Length} bytes of gear data: {Data}).",
            Logger.Args(GetActiveWizard().CharId, message.GearVaultGID, gearData?.Length ?? 0, Convert.ToHexString(gearData ?? [])));
        RunVaultRequest("take gear out of a gear vault", () => MoveItemsOut(VaultKind.Gear, message.GearVaultGID, vault => {
            // The client names the gear by the entry it was shown; equal entries are equal gear to the player.
            var entry = (vault.Items ?? []).FirstOrDefault(item => item.Info is not null && gearData is not null
                && item.Info.AsSpan().SequenceEqual(gearData));

            return entry is null ? [] : [entry];
        }, 1));
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_MOVEJEWELTOJEWELVAULT))]
    private void ReceiveMoveJewelToJewelVault(WIZARDHOUSING_50_PROTOCOL.MSG_MOVEJEWELTOJEWELVAULT message) {
        Logger.Information("Vaults: character {CharId} asks to move {Count} of jewel template {TemplateId} into jewel vault {VaultGid}.",
            Logger.Args(GetActiveWizard().CharId, message.Count, message.JewelTemplateID, message.JewelVaultID));
        RunVaultRequest("move jewels into a jewel vault", () => MoveItemsIn(VaultKind.Jewel, message.JewelVaultID,
            wizard => PickFromBackpack(wizard, message.JewelTemplateID, message.Count, BankRules.IsJewel)));
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_MOVEJEWELFROMJEWELVAULT))]
    private void ReceiveMoveJewelFromJewelVault(WIZARDHOUSING_50_PROTOCOL.MSG_MOVEJEWELFROMJEWELVAULT message) {
        Logger.Information("Vaults: character {CharId} asks to take {Count} of jewel template {TemplateId} out of jewel vault {VaultGid}.",
            Logger.Args(GetActiveWizard().CharId, message.Count, message.JewelTemplateID, message.JewelVaultID));
        RunVaultRequest("take jewels out of a jewel vault", () => MoveItemsOut(VaultKind.Jewel, message.JewelVaultID,
            vault => PickFromVault(vault, message.JewelTemplateID, message.Count), message.Count));
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_MOVESEEDTOGARDENINGSHED))]
    private void ReceiveMoveSeedToGardeningShed(WIZARDHOUSING_50_PROTOCOL.MSG_MOVESEEDTOGARDENINGSHED message) {
        Logger.Information("Vaults: character {CharId} asks to move {Count} of seed template {TemplateId} into seed vault {VaultGid}.",
            Logger.Args(GetActiveWizard().CharId, message.Count, message.SeedTemplateID, message.GardeningShedID));
        RunVaultRequest("move seeds into a seed vault", () => MoveItemsIn(VaultKind.Seed, message.GardeningShedID,
            wizard => PickFromBackpack(wizard, message.SeedTemplateID, message.Count, IsSeed)));
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_MOVESEEDFROMGARDENINGSHED))]
    private void ReceiveMoveSeedFromGardeningShed(WIZARDHOUSING_50_PROTOCOL.MSG_MOVESEEDFROMGARDENINGSHED message) {
        Logger.Information("Vaults: character {CharId} asks to take {Count} of seed template {TemplateId} out of seed vault {VaultGid}.",
            Logger.Args(GetActiveWizard().CharId, message.Count, message.SeedTemplateID, message.GardeningShedID));
        RunVaultRequest("take seeds out of a seed vault", () => MoveItemsOut(VaultKind.Seed, message.GardeningShedID,
            vault => PickFromVault(vault, message.SeedTemplateID, message.Count), message.Count));
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_MOVETCTOTCVAULT))]
    private void ReceiveMoveTreasureCardToVault(WIZARDHOUSING_50_PROTOCOL.MSG_MOVETCTOTCVAULT message) {
        Logger.Information("Vaults: character {CharId} asks to move {Count} of treasure card {SpellId} (enchantment {Enchantment}) "
            + "into treasure card vault {VaultGid}.",
            Logger.Args(GetActiveWizard().CharId, message.Count, message.SpellTemplateID, message.EnchantmentTemplateID, message.TCVaultID));
        RunVaultRequest("move treasure cards into a treasure card vault",
            () => MoveCardsIn(message.TCVaultID, message.SpellTemplateID, message.EnchantmentTemplateID, message.Count));
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_MOVETCFROMTCVAULT))]
    private void ReceiveMoveTreasureCardFromVault(WIZARDHOUSING_50_PROTOCOL.MSG_MOVETCFROMTCVAULT message) {
        Logger.Information("Vaults: character {CharId} asks to take {Count} of treasure card {SpellId} (enchantment {Enchantment}) "
            + "out of treasure card vault {VaultGid}.",
            Logger.Args(GetActiveWizard().CharId, message.Count, message.SpellTemplateID, message.EnchantmentTemplateID, message.TCVaultID));
        RunVaultRequest("take treasure cards out of a treasure card vault",
            () => MoveCardsOut(message.TCVaultID, message.SpellTemplateID, message.EnchantmentTemplateID, message.Count));
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_ADDTOTREASURECARDPOSTER))]
    private void ReceiveAddToTreasureCardPoster(WIZARDHOUSING_50_PROTOCOL.MSG_ADDTOTREASURECARDPOSTER message) {
        Logger.Information("Vaults: character {CharId} asks to show treasure card {SpellId} (enchantment {Enchantment}, flags {Flags}) "
            + "on poster {PosterGid}.",
            Logger.Args(GetActiveWizard().CharId, message.SpellTemplateID, message.EnchantmentTemplateID, message.Flags, message.TreasureCardPosterID));
        RunVaultRequest("put a treasure card on a poster",
            () => ShowOnPoster(message.TreasureCardPosterID, message.SpellTemplateID, message.EnchantmentTemplateID, message.Flags));
    }

    private void RunVaultRequest(string request, System.Action work) {
        // An exception in an actor vanishes, and the move would look like it did nothing.
        try {
            work();
        } catch (Exception exception) {
            Logger.Error("Vaults: the request to {Request} of character {CharId} failed: {Exception}",
                Logger.Args(request, GetActiveWizard()?.CharId ?? 0, exception));
            InformGameClient("That did not work. Nothing was moved.");
        }
    }

    private void MoveItemsIn(VaultKind kind, ulong vaultGid,
                             Func<Wizard, (IReadOnlyList<WizClientObjectItem> Items, string Refusal)> pick) {
        if (!TryGetVault(kind, vaultGid, out var house, out var vaultItem)) {
            return;
        }

        var wizard = GetActiveWizard();
        IReadOnlyList<WizClientObjectItem> items;
        HouseChange change;
        var full = false;
        lock (HouseDeeds.BackpackLock(wizard)) {
            (items, var refusal) = pick(wizard);
            if (items is not { Count: > 0 }) {
                Logger.Warning("Vaults: refused to move items of character {CharId} into {Vault} {VaultGid}: {Reason}",
                    Logger.Args(wizard.CharId, HouseVaults.NameOf(kind), vaultGid, refusal ?? "they are not in the backpack"));
                InformGameClient(refusal ?? "That is not in your backpack.");

                return;
            }

            var entries = new List<VaultItem>(items.Count);
            foreach (var item in items) {
                byte[] info = null;
                if (kind == VaultKind.Gear && !HouseVaults.TryBuildGearInfo(item, out info)) {
                    InformGameClient("That item cannot go in the vault.");

                    return;
                }

                entries.Add(new VaultItem {
                    ItemGid = item.m_globalID,
                    TemplateId = (uint) Math.Min((ulong) item.m_templateID, uint.MaxValue),
                    Info = info,
                });
            }

            if (!VaultFits(house, vaultItem, (vaultItem.Vault ?? HouseVault.Empty) with { Items = [.. vaultItem.Vault?.Items ?? [], .. entries] })) {
                InformGameClient("This house cannot hold any more in its vaults.");

                return;
            }

            var removed = new List<WizClientObjectItem>(items.Count);
            foreach (var item in items) {
                if (!wizard.InventoryBehavior.RemoveItem(item)) {
                    break;
                }

                removed.Add(item);
            }

            change = removed.Count != items.Count
                ? new HouseChange(HouseEdit.NotFound)
                : house.TryUpdate(vaultItem.Id, stored => {
                    var vault = stored.Vault ?? HouseVault.Empty;
                    if ((vault.Items?.Count ?? 0) + entries.Count > HouseVaults.Capacity(kind)) {
                        full = true;

                        return null;
                    }

                    return stored with { Vault = vault with { Items = [.. vault.Items ?? [], .. entries] } };
                }, document => HouseCollection.SaveWithBackpack(document, wizard));
            if (!change.Done) {
                foreach (var item in removed) {
                    if (!wizard.InventoryBehavior.AddItem(item)) {
                        Logger.Error("Vaults: could not return item {ItemId} to the backpack of character {CharId} after it failed to go in the vault.",
                            Logger.Args(item.m_globalID.Full, wizard.CharId));
                    }
                }
            }
        }

        if (!change.Done) {
            RefuseVaultChange(kind, vaultGid, full, change.Result);

            return;
        }

        foreach (var item in items) {
            ItemSync.SendRemoved(SessionActor.ActorRef, wizard, item.m_globalID);
        }

        Logger.Information("Vaults: character {CharId} moved {Count} item(s) of template {TemplateId} into {Vault} {VaultGid}, which holds {Held}.",
            Logger.Args(wizard.CharId, items.Count, items[0].m_templateID.Full, HouseVaults.NameOf(kind), vaultGid, change.Item.Vault.Items.Count));
        BroadcastVault(house, kind, change);
    }

    private void MoveItemsOut(VaultKind kind, ulong vaultGid, Func<HouseVault, IReadOnlyList<VaultItem>> pick, uint count) {
        if (!TryGetVault(kind, vaultGid, out var house, out var vaultItem)) {
            return;
        }

        var wizard = GetActiveWizard();
        var entries = pick(vaultItem.Vault ?? HouseVault.Empty);
        if (entries.Count == 0 || entries.Count != count) {
            Logger.Warning("Vaults: refused to take {Count} item(s) of character {CharId} out of {Vault} {VaultGid}: the vault holds {Found} of them.",
                Logger.Args(count, wizard.CharId, HouseVaults.NameOf(kind), vaultGid, entries.Count));
            InformGameClient("The vault does not hold that.");

            return;
        }

        if (wizard.InventoryBehavior.FreeSlots < entries.Count) {
            InformGameClient("Your backpack is full.");

            return;
        }

        if (kind == VaultKind.Jewel && IsJewelPouchFull(wizard, entries.Count)) {
            InformGameClient("You cannot carry any more jewels.");

            return;
        }

        // The stored documents are the items; one that cannot be read keeps its place in the vault.
        var items = new List<WizClientObjectItem>(entries.Count);
        foreach (var entry in entries) {
            var item = WizardItemCollection.GetItem(wizard.CharId, entry.ItemGid);
            if (item is null) {
                Logger.Error("Vaults: item {ItemId} (template {TemplateId}) in {Vault} {VaultGid} of character {CharId} has no stored document; it stays in the vault.",
                    Logger.Args(entry.ItemGid, entry.TemplateId, HouseVaults.NameOf(kind), vaultGid, wizard.CharId));
                InformGameClient("That item cannot be taken out right now.");

                return;
            }

            items.Add(item);
        }

        var gids = entries.Select(entry => entry.ItemGid).ToHashSet();
        HouseChange change;
        var missing = false;
        lock (HouseDeeds.BackpackLock(wizard)) {
            var added = new List<WizClientObjectItem>(items.Count);
            foreach (var item in items) {
                if (!wizard.InventoryBehavior.AddItem(item)) {
                    break;
                }

                added.Add(item);
            }

            change = added.Count != items.Count
                ? new HouseChange(HouseEdit.LimitReached)
                : house.TryUpdate(vaultItem.Id, stored => {
                    var vault = stored.Vault ?? HouseVault.Empty;
                    var kept = (vault.Items ?? []).Where(entry => !gids.Contains(entry.ItemGid)).ToList();
                    if ((vault.Items?.Count ?? 0) - kept.Count != gids.Count) {
                        missing = true;

                        return null;
                    }

                    return stored with { Vault = vault with { Items = kept } };
                }, document => HouseCollection.SaveWithBackpack(document, wizard));
            if (!change.Done) {
                foreach (var item in added) {
                    wizard.InventoryBehavior.RemoveItem(item);
                }
            }
        }

        if (!change.Done) {
            if (missing) {
                InformGameClient("The vault does not hold that.");

                return;
            }

            RefuseVaultChange(kind, vaultGid, full: false, change.Result);

            return;
        }

        foreach (var item in items) {
            if (!ItemSync.SendAdded(SessionActor.ActorRef, wizard, item)) {
                Logger.Error("Vaults: could not tell the client about item {ItemId} of character {CharId}, which came out of a vault.",
                    Logger.Args(item.m_globalID.Full, wizard.CharId));
            }
        }

        Logger.Information("Vaults: character {CharId} took {Count} item(s) of template {TemplateId} out of {Vault} {VaultGid}, which holds {Held}.",
            Logger.Args(wizard.CharId, items.Count, entries[0].TemplateId, HouseVaults.NameOf(kind), vaultGid, change.Item.Vault.Items.Count));
        BroadcastVault(house, kind, change);
    }

    private void MoveCardsIn(ulong vaultGid, uint spellId, uint enchantment, uint count) {
        if (!TryGetVault(VaultKind.TreasureCard, vaultGid, out var house, out var vaultItem)
            || !TryGetTreasureCard(spellId, enchantment, count, out var template, out var templateId)) {
            return;
        }

        var wizard = GetActiveWizard();
        var book = wizard.SpellbookBehavior;
        var card = new VaultCard { ClientSpellId = spellId, TemplateId = templateId, Enchantment = enchantment };
        HouseChange change;
        var full = false;
        lock (HouseDeeds.BackpackLock(wizard)) {
            if (book.TreasureCardCount(templateId) < count) {
                Logger.Warning("Vaults: refused to move {Count} of treasure card {TemplateId} of character {CharId} into a vault: the book holds {Held}.",
                    Logger.Args(count, templateId, wizard.CharId, book.TreasureCardCount(templateId)));
                InformGameClient("You do not have that many of that treasure card.");

                return;
            }

            var cardsAfter = (vaultItem.Vault ?? HouseVault.Empty) with {
                Cards = [.. vaultItem.Vault?.Cards ?? [], .. Enumerable.Repeat(card, (int) count)],
            };
            if (!VaultFits(house, vaultItem, cardsAfter)) {
                InformGameClient("This house cannot hold any more in its vaults.");

                return;
            }

            var taken = Enumerable.Repeat(templateId, (int) count).ToList();
            if (!book.TryRemoveTreasureCards(taken)) {
                InformGameClient("You do not have that many of that treasure card.");

                return;
            }

            change = house.TryUpdate(vaultItem.Id, stored => {
                var vault = stored.Vault ?? HouseVault.Empty;
                if ((vault.Cards?.Count ?? 0) + (int) count > HouseVaults.Capacity(VaultKind.TreasureCard)) {
                    full = true;

                    return null;
                }

                return stored with { Vault = vault with { Cards = [.. vault.Cards ?? [], .. Enumerable.Repeat(card, (int) count)] } };
            }, document => HouseCollection.SaveWithTreasureCards(document, wizard));
            if (!change.Done) {
                book.AddTreasureCards(taken);
            }
        }

        if (!change.Done) {
            RefuseVaultChange(VaultKind.TreasureCard, vaultGid, full, change.Result);

            return;
        }

        var spellHash = (int) StringHash.Compute(template.m_name);
        for (var i = 0; i < count; i++) {
            SendToSocket(new WIZARD_12_PROTOCOL.MSG_REMOVETREASURESPELLFROMBOOK {
                SpellID = spellHash,
                EnchantmentID = (int) enchantment,
                Quantity = 1,
            });
        }

        Logger.Information("Vaults: character {CharId} moved {Count} of treasure card {TemplateId} into treasure card vault {VaultGid}, which holds {Held}.",
            Logger.Args(wizard.CharId, count, templateId, vaultGid, change.Item.Vault.Cards.Count));
        BroadcastVault(house, VaultKind.TreasureCard, change);
    }

    private void MoveCardsOut(ulong vaultGid, uint spellId, uint enchantment, uint count) {
        if (!TryGetVault(VaultKind.TreasureCard, vaultGid, out var house, out var vaultItem)) {
            return;
        }

        var wizard = GetActiveWizard();
        if (count == 0 || count > HouseVaults.Capacity(VaultKind.TreasureCard)) {
            Logger.Warning("Vaults: refused to take {Count} treasure cards of character {CharId} out of a vault: that is not a count.",
                Logger.Args(count, wizard.CharId));

            return;
        }

        var book = wizard.SpellbookBehavior;
        var cards = new List<VaultCard>();
        HouseChange change;
        var missing = false;
        lock (HouseDeeds.BackpackLock(wizard)) {
            if ((book.TreasureCardTemplateIds?.Count ?? 0) + count > TradeLimits.TreasureBookCapacity) {
                InformGameClient("Your treasure book is full.");

                return;
            }

            change = house.TryUpdate(vaultItem.Id, stored => {
                var vault = stored.Vault ?? HouseVault.Empty;
                var kept = new List<VaultCard>(vault.Cards ?? []);
                cards.Clear();
                for (var i = kept.Count - 1; i >= 0 && cards.Count < count; i--) {
                    if (kept[i].ClientSpellId == spellId && kept[i].Enchantment == enchantment) {
                        cards.Add(kept[i]);
                        kept.RemoveAt(i);
                    }
                }

                if (cards.Count != count) {
                    missing = true;

                    return null;
                }

                // The book changes with the write, so it is taken back when the write fails.
                book.AddTreasureCards(cards.Select(card => card.TemplateId));

                return stored with { Vault = vault with { Cards = kept } };
            }, document => HouseCollection.SaveWithTreasureCards(document, wizard));
            if (!change.Done && !missing && !book.TryRemoveTreasureCards([.. cards.Select(card => card.TemplateId)])) {
                Logger.Error("Vaults: the treasure book of character {CharId} did not hold the cards it was given for a failed write.",
                    Logger.Args(wizard.CharId));
            }
        }

        if (!change.Done) {
            if (missing) {
                Logger.Warning("Vaults: refused to take {Count} of treasure card {SpellId} of character {CharId} out of vault {VaultGid}: it holds {Found}.",
                    Logger.Args(count, spellId, wizard.CharId, vaultGid, cards.Count));
                InformGameClient("The vault does not hold that many of that treasure card.");

                return;
            }

            RefuseVaultChange(VaultKind.TreasureCard, vaultGid, full: false, change.Result);

            return;
        }

        foreach (var card in cards) {
            if (CoreObjectFactory.GetCoreTemplate(card.TemplateId) is SpellTemplate template) {
                SendToSocket(new WIZARD_12_PROTOCOL.MSG_ADDTREASURESPELLTOBOOK {
                    SpellID = (int) StringHash.Compute(template.m_name),
                    EnchantmentID = (int) card.Enchantment,
                });
            }
        }

        Logger.Information("Vaults: character {CharId} took {Count} of treasure card {SpellId} out of treasure card vault {VaultGid}, which holds {Held}.",
            Logger.Args(wizard.CharId, count, spellId, vaultGid, change.Item.Vault.Cards.Count));
        BroadcastVault(house, VaultKind.TreasureCard, change);
    }

    private void ShowOnPoster(ulong posterGid, uint spellId, uint enchantment, byte flags) {
        if (!TryGetVault(VaultKind.Poster, posterGid, out var house, out var posterItem)
            || !TryGetTreasureCard(spellId, enchantment, 1, out _, out var templateId)) {
            return;
        }

        var wizard = GetActiveWizard();
        if (wizard.SpellbookBehavior.TreasureCardCount(templateId) < 1) {
            Logger.Warning("Vaults: refused to show treasure card {TemplateId} on a poster for character {CharId}, who has none.",
                Logger.Args(templateId, wizard.CharId));
            InformGameClient("You do not have that treasure card.");

            return;
        }

        var card = new VaultCard { ClientSpellId = spellId, TemplateId = templateId, Enchantment = enchantment };
        var change = house.TryUpdate(posterItem.Id, stored => stored with {
            Vault = (stored.Vault ?? HouseVault.Empty) with { Poster = card, PosterFlags = flags },
        });
        if (!change.Done) {
            RefuseVaultChange(VaultKind.Poster, posterGid, full: false, change.Result);

            return;
        }

        Logger.Information("Vaults: character {CharId} shows treasure card {TemplateId} on poster {PosterGid}.",
            Logger.Args(wizard.CharId, templateId, posterGid));
        BroadcastVault(house, VaultKind.Poster, change);
    }

    private bool TryGetVault(VaultKind kind, ulong vaultGid, out HouseState house, out PlacedItem vaultItem) {
        vaultItem = null;
        house = _house;
        var wizard = GetActiveWizard();
        if (house is null) {
            Logger.Warning("Vaults: character {CharId} used {Vault} {VaultGid} outside a house.",
                Logger.Args(wizard.CharId, HouseVaults.NameOf(kind), vaultGid));

            return false;
        }

        if (!house.TryFindByGid(vaultGid, out vaultItem, out _) || vaultItem is null) {
            Logger.Warning("Vaults: character {CharId} used {Vault} {VaultGid}, which is not an object of lot {LotGid}.",
                Logger.Args(wizard.CharId, HouseVaults.NameOf(kind), vaultGid, house.LotGid));
            InformGameClient($"That {HouseVaults.NameOf(kind)} is not here anymore.");
            SendBlob(house);

            return false;
        }

        var placedKind = HouseVaults.KindOf(vaultItem.TemplateId);
        if (placedKind != kind) {
            Logger.Warning("Vaults: character {CharId} used object {VaultGid} (template {TemplateId}, {Placed}) as a {Vault}.",
                Logger.Args(wizard.CharId, vaultGid, vaultItem.TemplateId, HouseVaults.NameOf(placedKind), HouseVaults.NameOf(kind)));

            return false;
        }

        if (house.OwnerCharId != wizard.CharId) {
            Logger.Information("Vaults: character {CharId} used {Vault} {VaultGid} in the house of character {OwnerId}; only the owner can.",
                Logger.Args(wizard.CharId, HouseVaults.NameOf(kind), vaultGid, house.OwnerCharId));
            InformGameClient($"Only the owner of this house can use its {HouseVaults.NameOf(kind)}.");

            return false;
        }

        return true;
    }

    private bool TryGetTreasureCard(uint spellId, uint enchantment, uint count, out SpellTemplate template, out uint templateId) {
        var wizard = GetActiveWizard();
        templateId = 0;
        template = null;
        if (count == 0 || count > HouseVaults.Capacity(VaultKind.TreasureCard)) {
            Logger.Warning("Vaults: character {CharId} asked to move {Count} treasure cards, which is not a count.", Logger.Args(wizard.CharId, count));

            return false;
        }

        // The treasure book holds no enchanted cards, so none can be moved.
        if (enchantment != 0) {
            Logger.Warning("Vaults: character {CharId} asked to move treasure card {SpellId} with enchantment {Enchantment}; the treasure book holds none.",
                Logger.Args(wizard.CharId, spellId, enchantment));
            InformGameClient("Enchanted treasure cards cannot go there yet.");

            return false;
        }

        // The field is named a template id; the treasure book messages use the name hash, so both are accepted.
        if (CoreObjectFactory.GetCoreTemplate(spellId) is SpellTemplate direct) {
            template = direct;
            templateId = spellId;
        } else if (SpellFactory.GetTemplateIdByHash(spellId) is var byHash and not 0
                   && CoreObjectFactory.GetCoreTemplate(byHash) is SpellTemplate hashed) {
            template = hashed;
            templateId = byHash;
        }

        if (template is null) {
            Logger.Warning("Vaults: character {CharId} named treasure card {SpellId}, which is neither a spell template nor a spell hash.",
                Logger.Args(wizard.CharId, spellId));
            InformGameClient("That treasure card is not known.");

            return false;
        }

        Logger.Debug("Vaults: treasure card {SpellId} is spell template {TemplateId} ({Name}), found as a {How}.",
            Logger.Args(spellId, templateId, (string) template.m_name, templateId == spellId ? "template id" : "name hash"));

        return true;
    }

    /// <summary>
    /// Checks that the house blob still fits its string field with a vault's new contents, with room left for a plain
    /// entry for every object the house may still take. A blob that does not fit cannot be sent, and the house would not load.
    /// </summary>
    private static bool VaultFits(HouseState house, PlacedItem vaultItem, HouseVault vault) {
        var snapshot = house.Snapshot();
        try {
            byte[] serialized = HousingBlobs.Serialize(BuildBlob(house, snapshot, item => item.Id == vaultItem.Id ? item with { Vault = vault } : item));
            var room = Math.Max(0, house.ItemLimit - snapshot.LiveCount) * PlainEntryLength;

            return serialized.Length + room <= MaxBlobLength;
        } catch (InvalidOperationException) {
            return false;
        }
    }

    private void RefuseVaultChange(VaultKind kind, ulong vaultGid, bool full, HouseEdit result) {
        var wizard = GetActiveWizard();
        Logger.Warning("Vaults: the change to {Vault} {VaultGid} of character {CharId} was not made: {Reason}.",
            Logger.Args(HouseVaults.NameOf(kind), vaultGid, wizard.CharId, full ? "the vault is full" : result.ToString()));
        InformGameClient(full ? $"The {HouseVaults.NameOf(kind)} is full." : "That did not work. Nothing was moved.");
    }

    private void BroadcastVault(HouseState house, VaultKind kind, HouseChange change) {
        var patch = HouseVaults.Patch(kind, change.Item.Vault, change.Revision, change.GidId);
        if (patch is null) {
            // The vault changed but its data could not be built, so the blob shows what there is.
            SendBlob(house);

            return;
        }

        ZoneBroadcast(patch, isSelfless: false, ZoneBroadcastTarget.Players);
    }

    private static (IReadOnlyList<WizClientObjectItem> Items, string Refusal) PickFromBackpack(Wizard wizard, uint templateId, uint count,
                                                                                               Func<WizClientObjectItem, bool> isKind) {
        if (count == 0 || count > HouseVaults.Capacity(VaultKind.Jewel)) {
            return (null, "That is not a number of items.");
        }

        var items = wizard.InventoryBehavior.Items
            .Where(item => item.m_templateID == templateId && isKind(item))
            .Take((int) count)
            .ToList();

        return items.Count == count ? (items, null) : (null, "You do not have that many of those.");
    }

    private static IReadOnlyList<VaultItem> PickFromVault(HouseVault vault, uint templateId, uint count)
        => count == 0 ? [] : [.. (vault.Items ?? []).Where(item => item.TemplateId == templateId).Take((int) count)];

    private static bool IsGear(WizClientObjectItem item) {
        var template = ItemHelper.GetItemTemplate(item);
        if (template?.m_adjectiveList is null || PetFactory.IsPetTemplate((uint) Math.Min((ulong) item.m_templateID, uint.MaxValue))) {
            return false;
        }

        return ItemHelper.GetItemSlot(template) is { } slot && !HouseEquipment.IsHouseSlot(slot.SlotType);
    }

    private static bool IsSeed(WizClientObjectItem item)
        => ItemHelper.GetItemTemplate(item)?.m_behaviors?.OfType<SeedBehaviorTemplate>().Any() == true;

    private static bool IsJewelPouchFull(Wizard wizard, int adding) {
        var allowed = ConfigurationManager.GetValue(MaxJewelsAllowedKey, 0);

        return allowed > 0 && wizard.InventoryBehavior.Items.Count(BankRules.IsJewel) + adding > allowed;
    }

}
