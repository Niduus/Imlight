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
 * GIFTING SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Handles the functionality of gifting
 * 
 * 
 * USAGE EXAMPLE:
 *
 * 
 * NOTE:
 * 
 * 
 * TODO:
 * - There are 3 types of possible ways for "gifts"
 * 
 * Created by: Phill030
 * Version: KALI 1.0
 * Last Updated: 17.10.2026
 */
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Pet;
using Imlight.Common;
using Imlight.CoreLib.Game.Packs;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imcodec.Types;
using System;
using System.Linq;
using System.Text;
using Imlight.CoreLib.Game.Bank;

namespace Imlight.CoreLib.Game.Services;

internal class GiftingService(SessionActor sessionActor) : MessageService(sessionActor) {

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new GiftingService(parentActor));

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceiveAttachComplete(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        var wizard = GetActiveWizard();

        if (wizard.Account?.GiftMailbox != null && wizard.Account.GiftMailbox.Count > 0) {
            SendToSocket(new GAME_5_PROTOCOL.MSG_NEW_MAIL {
                AccountMail = 0,
                CharacterID = wizard.CharId,
                MailType = 1
            });
        }

        if (wizard.Account?.ClaimGifts is { Count: > 0 } claimGifts) {
            SendToSocket(new GAME_5_PROTOCOL.MSG_NOTIFY_GIFT { Gift = 1 });
        }
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_RETRIEVE_MAIL))]
    private void ReceiveRetrieveMail(GAME_5_PROTOCOL.MSG_RETRIEVE_MAIL message) {
        var wizard = GetActiveWizard();

        var mailBox = wizard.Account.GiftMailbox;
        if (mailBox == null || mailBox.Count <= 0) {
            return;
        }

        var mailData = new MailList { m_messages = mailBox };
        var mailSerializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.Compress);
        if (!mailSerializer.Serialize(mailData, 5, out var serializedData)) {
            Logger.Error("Failed to serialize MailList");
            return;
        }

        SendToSocket(new GAME_5_PROTOCOL.MSG_MAIL_DATA {
            Data = serializedData
        });
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_REDEEM_MAIL_GIFT))]
    private void ReceiveRedeemGift(GAME_5_PROTOCOL.MSG_REDEEM_MAIL_GIFT message) {
        var wizard = GetActiveWizard();

        // 1. Get Item by mailId
        // 2. Check if the item is in the Gift list and check if the item is already epired
        // 3. Remove the item from DB and add to Inventory.
        // The message tells the player, that the item was added to their Bank tho.

        if (wizard.Account.GiftMailbox == null || wizard.Account.GiftMailbox.Count <= 0) {
            return;
        }

        var selectedMail = wizard.Account.GiftMailbox.SingleOrDefault(m => m.m_mailId == message.MailId, null);
        if (selectedMail == null) {
            SendToSocket(new GAME_5_PROTOCOL.MSG_REDEEM_MAIL_GIFT_RESPONSE {
                ErrorCode = 1,
                MailId = message.MailId,
            });
            return;
        }

        // How could that even happen??
        if (selectedMail.m_recipientId != wizard.AccountId) {
            SendToSocket(new GAME_5_PROTOCOL.MSG_REDEEM_MAIL_GIFT_RESPONSE {
                ErrorCode = 1,
                MailId = message.MailId,
            });
            return;
        }

        if (selectedMail.m_expireDuration > 0 && ((ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds() > selectedMail.m_timeStamp + selectedMail.m_expireDuration)) {
            // Expired, remove the item from the Giftlist
            wizard.Account.RemoveMail(selectedMail.m_mailId);
            InformGameClient("This item expired and you cannot claim it anymore! This item gets removed from the list.");
            return;
        }

        if (!TryGetGiftRedemption(selectedMail, out var giftRedemption)) {
            SendToSocket(new GAME_5_PROTOCOL.MSG_REDEEM_MAIL_GIFT_RESPONSE {
                ErrorCode = 1,
                MailId = message.MailId,
            });
            return;
        }

        wizard.Account.RemoveMail(selectedMail.m_mailId);

        var itemTemplate = CoreObjectFactory.GetCoreTemplate(giftRedemption.m_itemId);
        switch (itemTemplate) {
            case BoosterPackTemplate:
                for (uint i = 0; i < giftRedemption.m_itemCount; i++) {
                    // A pack that would not fit in the backpack and bank stays in the mailbox.
                    if (!PackManager.OpenPack(SessionActor.ActorRef, wizard, giftRedemption.m_itemId)) {
                        wizard.Account.AddMail(selectedMail);
                        SendToSocket(new GAME_5_PROTOCOL.MSG_REDEEM_MAIL_GIFT_RESPONSE {
                            ErrorCode = 1,
                            MailId = message.MailId,
                        });
                        InformGameClient("Your backpack and bank are full, so the pack stays closed.");
                        return;
                    }
                }
                break;
            case GoldAmountTemplate goldItem:
                wizard.AddGold(goldItem.m_goldAmount);
                SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
                    Gold = wizard.GameStats.m_currentGold,
                    MaxGold = wizard.GameStats.m_baseGoldPouch
                });
                break;
            case LunariAmountTemplate lunariTemplate:
                wizard.AddLunari(lunariTemplate.m_lunariAmount);
                SendToSocket(new WIZARD2_53_PROTOCOL.MSG_UPDATEEVENTCURRENCY1 {
                    EventCurrency1 = wizard.GameStats.m_currentEventCurrency1,
                    MaxEventCurrency1 = wizard.GameStats.m_baseEventCurrency1Pouch
                });
                break;
            case ItemBundleTemplate:
                // Bundles belong in the Claim Gift list, where the game lets the player pick a tier; claiming one from
                // the friend gift box crashed the game (Niduus, 2026-10-21). Move it there.
                AddClaimGift(wizard.Account, giftRedemption.m_itemId);
                SendToSocket(new GAME_5_PROTOCOL.MSG_REDEEM_MAIL_GIFT_RESPONSE {
                    ErrorCode = 1,
                    MailId = message.MailId,
                });
                SendMailList(wizard.Account);
                SendToSocket(new GAME_5_PROTOCOL.MSG_NOTIFY_GIFT { Gift = 1 });
                InformGameClient("That bundle was moved to your gift list (the gift button), where you can pick the tier.");
                return;
            default:
                // Check if the item is emote or teleport effect
                var customEmote = itemTemplate?.m_behaviors?.OfType<CustomEmoteBehaviorTemplate>().FirstOrDefault();
                if (customEmote != null && customEmote.m_bitFieldNumber >= 0) {
                    if (customEmote.m_emoteType == CustomEmoteType.CE_Teleport) {
                        wizard.UnlockCustomTeleportEffect(customEmote.m_bitFieldNumber);
                    }
                    else {
                        wizard.UnlockCustomEmote(customEmote.m_bitFieldNumber);
                    }
                }

                // A full backpack sends the gift to the bank, as the game does.
                for (uint i = 0; i < giftRedemption.m_itemCount; i++) {
                    if (!wizard.TryCreateItem(giftRedemption.m_itemId, out var itemCoreObject)
                        || ItemPlacement.Give(SessionActor.ActorRef, wizard, itemCoreObject) == Placement.Lost) {
                        Logger.Warning("Could not add gift item {0} to the backpack or bank.", Logger.Args(giftRedemption.m_itemId));
                        SendToSocket(new GAME_5_PROTOCOL.MSG_REDEEM_MAIL_GIFT_RESPONSE {
                            ErrorCode = 1,
                            MailId = message.MailId,
                        });
                        return;
                    }
                }
                break;
        }

        var mailData = new MailList { m_messages = wizard.Account.GiftMailbox };
        var mailSerializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.Compress);
        if (!mailSerializer.Serialize(mailData, 5, out var serializedData)) {
            Logger.Error("Failed to serialize MailList");
            return;
        }

        SendToSocket(new GAME_5_PROTOCOL.MSG_REDEEM_MAIL_GIFT_RESPONSE {
            ErrorCode = 0,
            MailId = message.MailId,
            TemplateId = giftRedemption.m_itemId
        });

        SendToSocket(new GAME_5_PROTOCOL.MSG_MAIL_DATA {
            Data = serializedData
        });
    }

    private void SendMailList(Account account) {
        var mailData = new MailList { m_messages = account.GiftMailbox };
        var mailSerializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.Compress);
        if (mailSerializer.Serialize(mailData, 5, out var serializedData)) {
            SendToSocket(new GAME_5_PROTOCOL.MSG_MAIL_DATA { Data = serializedData });
        }
    }

    internal static ClaimGift AddClaimGift(Account account, ulong templateId) {
        var gift = new ClaimGift { GiftId = Guid.NewGuid().ToString("N"), TemplateId = templateId, Modifier = 1 };
        account.ClaimGifts ??= new();
        account.ClaimGifts.Add(gift);
        AccountCollection.AddClaimGifts(account.AccountId, [gift]);

        return gift;
    }

    #region Claim Gift list

    // The game's gift button list (promo gifts). Data is a PromoGiftList, non-versionable, uncompressed, property mask
    // 0x18; read from the game client (2026-10-21). A choose-one bundle opens a picker and the pick comes back as
    // BundleChoiceID (a template id).
    private const uint CLAIM_GIFT_FLAGS = 0x18;

    private static readonly ObjectSerializer s_claimGiftSerializer = new(Versionable: false, Behaviors: SerializerFlags.None);

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_REQUEST_GIFTS))]
    private void ReceiveRequestGifts(GAME_5_PROTOCOL.MSG_REQUEST_GIFTS message) {
        var account = GetActiveAccount();
        string giftType = message.GiftType;
        var gifts = account?.ClaimGifts;
        Logger.Information("Claim gifts: the game asked for \"{0}\"; {1} waiting.", Logger.Args(giftType, gifts?.Count ?? 0));

        // Crowns rewards and periodic items are not kept here; AutoRedeem=1 makes the game show nothing.
        if (!string.IsNullOrEmpty(giftType) || gifts is null || gifts.Count == 0) {
            SendToSocket(new GAME_5_PROTOCOL.MSG_RECEIVE_GIFTS { Success = 1, Data = Array.Empty<byte>(), AutoRedeem = 1 });

            return;
        }

        var list = new PromoGiftList {
            m_promoGifts = gifts.Select(g => new PromoGift {
                m_rewardID = (GID) g.TemplateId,
                m_rewardModifier = (GID) Math.Max(g.Modifier, 1),
                m_giftID = g.GiftId,
                m_autoRedeem = false,
            }).ToList(),
        };

        if (!s_claimGiftSerializer.Serialize(list, CLAIM_GIFT_FLAGS, out var data)) {
            Logger.Error("Claim gifts: could not serialize the gift list.");
            SendToSocket(new GAME_5_PROTOCOL.MSG_RECEIVE_GIFTS { Success = 0, Data = Array.Empty<byte>() });

            return;
        }

        SendToSocket(new GAME_5_PROTOCOL.MSG_RECEIVE_GIFTS { Success = 1, Data = data });
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_REDEEM_GIFT))]
    private void ReceiveRedeemClaimGift(GAME_5_PROTOCOL.MSG_REDEEM_GIFT message) {
        var wizard = GetActiveWizard();
        var account = wizard?.Account;
        string giftId = message.GiftID;
        ulong choice = message.BundleChoiceID;
        var gift = account?.ClaimGifts?.FirstOrDefault(g => g.GiftId == giftId);
        Logger.Information("Claim gifts: {0} claims {1} (template {2}, choice {3}).",
            Logger.Args(wizard?.CharId, giftId, gift?.TemplateId, choice));
        if (gift is null) {
            SendGiftRedeemed(giftId, false, 0, 0, account);

            return;
        }

        var template = CoreObjectFactory.GetCoreTemplate(gift.TemplateId);
        var granted = gift.TemplateId;
        if (template is ItemBundleTemplate bundle && (int) bundle.m_acquireCondition == 1 && choice != 0
            && bundle.m_bundleItems.Any(id => (ulong) id == choice)) {
            granted = choice;
        }

        ulong lastItem = 0;
        var count = 0;
        GrantTemplate(wizard, granted, 0, ref lastItem, ref count);
        if (count == 0) {
            SendGiftRedeemed(giftId, false, 0, 0, account);

            return;
        }

        account.ClaimGifts.Remove(gift);
        AccountCollection.RemoveClaimGift(account.AccountId, giftId);
        SendGiftRedeemed(giftId, true, lastItem, granted, account);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_DELETE_GIFT))]
    private void ReceiveDeleteClaimGift(GAME_5_PROTOCOL.MSG_DELETE_GIFT message) {
        var account = GetActiveAccount();
        string giftId = message.GiftID;
        account?.ClaimGifts?.RemoveAll(g => g.GiftId == giftId);
        if (account is not null) {
            AccountCollection.RemoveClaimGift(account.AccountId, giftId);
        }

        SendToSocket(new GAME_5_PROTOCOL.MSG_DELETE_GIFT { GiftID = giftId, Success = 1, IsPeriodicItem = message.IsPeriodicItem });
    }

    private void SendGiftRedeemed(string giftId, bool success, ulong itemGid, ulong templateId, Account account)
        => SendToSocket(new GAME_5_PROTOCOL.MSG_GIFT_REDEEMED {
            Success = success ? 1 : 0,
            GiftID = giftId ?? "",
            GID = itemGid,
            Data = Array.Empty<byte>(),
            TemplateID = templateId,
            // 5 is "inventory full" to the game.
            ErrorCode = success ? 0 : 5,
            Quantity = 1,
            HasMorePromoGifts = (sbyte) (account?.ClaimGifts?.Count > 0 ? 1 : 0),
        });

    /// <summary>
    /// Gives a template: a bundle gives everything in it (a choose-one bundle given whole gives all its choices).
    /// </summary>
    private void GrantTemplate(Wizard wizard, ulong templateId, int depth, ref ulong lastItem, ref int count) {
        if (depth > 4) {
            return;
        }

        if (CoreObjectFactory.GetCoreTemplate(templateId) is ItemBundleTemplate bundle) {
            foreach (ulong inner in bundle.m_bundleItems.Concat(bundle.m_bundleFreeItems ?? [])) {
                GrantTemplate(wizard, inner, depth + 1, ref lastItem, ref count);
            }

            return;
        }

        switch (CoreObjectFactory.GetCoreTemplate(templateId)) {
            // A booster pack in the backpack stopped the character from loading; open it like the crown shop does.
            case BoosterPackTemplate:
                if (!PackManager.OpenPack(SessionActor.ActorRef, wizard, templateId)) {
                    InformGameClient("Your backpack and bank are full, so a pack stayed closed.");

                    return;
                }

                count++;

                return;
            case GoldAmountTemplate goldItem:
                wizard.AddGold(goldItem.m_goldAmount);
                SendToSocket(new WIZARD_12_PROTOCOL.MSG_UPDATEGOLD {
                    Gold = wizard.GameStats.m_currentGold,
                    MaxGold = wizard.GameStats.m_baseGoldPouch
                });
                count++;

                return;
            case LunariAmountTemplate lunariTemplate:
                wizard.AddLunari(lunariTemplate.m_lunariAmount);
                SendToSocket(new WIZARD2_53_PROTOCOL.MSG_UPDATEEVENTCURRENCY1 {
                    EventCurrency1 = wizard.GameStats.m_currentEventCurrency1,
                    MaxEventCurrency1 = wizard.GameStats.m_baseEventCurrency1Pouch
                });
                count++;

                return;
            case CurrencyTemplate or TourneyTokensAmountTemplate:
                Logger.Warning("Claim gifts: {0} is a currency the server cannot give; skipped.", Logger.Args(templateId));

                return;
        }

        // A full backpack sends the gift to the bank, as the game does.
        if (!wizard.TryCreateItem(templateId, out var item) || ItemPlacement.Give(SessionActor.ActorRef, wizard, item) == Placement.Lost) {
            Logger.Warning("Claim gifts: could not add {0} to the backpack or bank of {1}.", Logger.Args(templateId, wizard.CharId));

            return;
        }

        lastItem = item.m_globalID.Full;
        count++;
    }

    #endregion

    public static bool TryGetGiftRedemption(Mail mail, out CrownShopGiftRedemption? giftRedemption) {
        giftRedemption = null;

        if (string.IsNullOrEmpty(mail.m_messageData)) {
            return false;
        }

        byte[] itemBytes = Encoding.Latin1.GetBytes(mail.m_messageData);
        var serializer = new ObjectSerializer(Versionable: true, Behaviors: SerializerFlags.None);
        return serializer.Deserialize(itemBytes, 31, out giftRedemption);
    }

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_DELIVER_GIFT))]
    private void ReceiveDeliverGift(SERVICE_101_PROTOCOL.MSG_DELIVER_GIFT message) {
        var account = GetActiveAccount();

        if (account is null) {
            return;
        }
        var wizard = GetActiveWizard();

        account.GiftMailbox ??= new();
        account.GiftMailbox.Add(message.Mail);

        SendToSocket(new GAME_5_PROTOCOL.MSG_NEW_MAIL {
            AccountMail = 0,
            CharacterID = wizard.CharId,
            MailType = 1
        });
    }

}
