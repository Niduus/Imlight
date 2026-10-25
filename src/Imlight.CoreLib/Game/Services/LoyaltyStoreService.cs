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
 * LOYALTY STORE SERVICE
 * ========================================================================
 *
 * PURPOSE:
 * Answers the game's Loyalty Store window (Majid, Game/LoyaltyStore/LoyaltyStore.cs): opening it, buying a ware with
 * Loyalty Tokens and claiming the tokens of this rotation.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/25/2026
 */

using System;
using System.Collections.Generic;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.Loyalty;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Services;

internal class LoyaltyStoreService(SessionActor sessionActor) : MessageService(sessionActor) {

    // The window remembers the update id it was last sent and only reads the "Updates" field when it changes.
    private const uint UPDATE_ID = 1;

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new LoyaltyStoreService(parentActor));

    // The dialog option: AskClientToRequestLoyaltyInfo 0 makes the client ask for the list itself.
    [MessageHandler(typeof(GAME2_55_PROTOCOL.MSG_REQUESTEXPLICITLOYALTYSTOREUPDATE))]
    private void ReceiveExplicitUpdate(GAME2_55_PROTOCOL.MSG_REQUESTEXPLICITLOYALTYSTOREUPDATE message) {
        Logger.Information("Loyalty Store | {0} opens Majid's store.", Logger.Args(GetActiveWizard()?.CharId));
        SendToSocket(new GAME2_55_PROTOCOL.MSG_REQUESTEXPLICITLOYALTYSTOREUPDATE { AskClientToRequestLoyaltyInfo = 0 });
    }

    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_LOYALTYSTORELISTREQUEST))]
    private void ReceiveListRequest(WIZARD3_56_PROTOCOL.MSG_LOYALTYSTORELISTREQUEST message) {
        try {
            if (GetActiveWizard() is not { } wizard) {
                return;
            }

            var now = LoyaltyStore.Now();
            Logger.Information("Loyalty Store | Sending Majid's wares to {0}: {1}.",
                Logger.Args(wizard.CharId, now is null ? "none" : LoyaltyStore.Describe(now)));
            SendToSocket(LoyaltyStore.ListMessage(wizard.AccountId, wizard.Crowns, UPDATE_ID));
        }
        catch (Exception ex) {
            Logger.Error("Loyalty Store | Sending the list failed. {0}", Logger.Args(ex));
        }
    }

    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_LOYALTYSTORE_PURCHASE_LOCK_REQUEST))]
    private void ReceiveLockRequest(WIZARD3_56_PROTOCOL.MSG_LOYALTYSTORE_PURCHASE_LOCK_REQUEST message) {
        try {
            var wizard = GetActiveWizard();
            var templateId = (uint) message.Item;
            var ware = LoyaltyStore.WareOf(message.SaleID, templateId);
            var error = wizard is null || ware is null ? 1
                : LoyaltyStore.Change(wizard.AccountId, account => WhyNot(account, ware) is null ? 0 : 1);
            Logger.Information("Loyalty Store | {0} picked item {1} (sale {2}): {3}.",
                Logger.Args(wizard?.CharId, templateId, message.SaleID, error == 0 ? $"{ware.Price} tokens" : "refused"));
            SendToSocket(new WIZARD3_56_PROTOCOL.MSG_LOYALTYSTORE_PURCHASE_LOCK_RESPONSE {
                Item = message.Item,
                LoyaltyTokensCost = ware?.Price ?? 0,
                Error = error,
            });
            if (error != 0 && wizard is not null && ware is not null) {
                InformGameClient(LoyaltyStore.Change(wizard.AccountId, account => WhyNot(account, ware)) ?? "");
            }
        }
        catch (Exception ex) {
            Logger.Error("Loyalty Store | Picking a ware failed. {0}", Logger.Args(ex));
        }
    }

    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_LOYALTYSTORE_PURCHASE_REQUEST))]
    private void ReceivePurchaseRequest(WIZARD3_56_PROTOCOL.MSG_LOYALTYSTORE_PURCHASE_REQUEST message) {
        try {
            if (GetActiveWizard() is not { } wizard) {
                return;
            }

            var templateId = (uint) message.Item;
            var ware = LoyaltyStore.WareOf(message.SaleID, templateId);
            if (ware is null) {
                InformGameClient("Majid does not sell that any more. Talk to him again to see his new wares.");

                return;
            }

            // The tokens go first; a ware that cannot be given (a full backpack) gives them back.
            var name = wizard.PlayerNameBehavior?.GetWizardName() ?? "";
            var refused = LoyaltyStore.Change(wizard.AccountId, account => {
                if (WhyNot(account, ware) is { } why) {
                    return why;
                }

                account.Tokens -= ware.Price;
                account.Bought[ware.TemplateId] = account.Bought.GetValueOrDefault(ware.TemplateId) + 1;

                return null;
            });
            if (refused is not null) {
                InformGameClient(refused);

                return;
            }

            var result = new DropTableResult { Items = [new DropItemResult { ItemId = ware.TemplateId.ToString(), ItemName = ware.Name, Quantity = 1 }] };
            var granted = LootGranter.Grant(SessionActor.ActorRef, wizard, result);
            if (granted.Items.Count == 0 && granted.TreasureCards.Count == 0) {
                LoyaltyStore.Change(wizard.AccountId, account => {
                    account.Tokens += ware.Price;
                    account.Bought[ware.TemplateId] = Math.Max(0, account.Bought.GetValueOrDefault(ware.TemplateId) - 1);

                    return 0;
                });
                Logger.Warning("Loyalty Store | {0} could not be given item {1}; the tokens went back.", Logger.Args(wizard.CharId, ware.TemplateId));
                InformGameClient($"{ware.Name} did not fit. Your tokens were given back; make room and try again.");

                return;
            }

            var purchase = LoyaltyStore.Change(wizard.AccountId, account => {
                var entry = new LoyaltyStore.Purchase {
                    Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), CharId = wizard.CharId, Name = name,
                    ItemId = ware.TemplateId, Tokens = ware.Price, Balance = account.Tokens,
                };
                LoyaltyStore.AddHistory(account, entry);

                return entry;
            });
            Logger.Information("Loyalty Store | {0} bought {1} ({2}) for {3} tokens; {4} left.",
                Logger.Args(wizard.CharId, ware.Name, ware.TemplateId, ware.Price, purchase.Balance));
            SendPurchase(purchase);
        }
        catch (Exception ex) {
            Logger.Error("Loyalty Store | Buying failed. {0}", Logger.Args(ex));
        }
    }

    // The Claim button: the tokens of this rotation, once per account.
    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_LOYALTYCLAIMLOYALTYTOKENS))]
    private void ReceiveClaim(WIZARD3_56_PROTOCOL.MSG_LOYALTYCLAIMLOYALTYTOKENS message) {
        try {
            if (GetActiveWizard() is not { } wizard) {
                return;
            }

            var purchase = Claim(wizard.AccountId, wizard.CharId, wizard.PlayerNameBehavior?.GetWizardName() ?? "");
            SendToSocket(new WIZARD3_56_PROTOCOL.MSG_LOYALTYCLAIMLOYALTYTOKENSCONFIRM { Failure = (sbyte) (purchase is null ? 1 : 0) });
            if (purchase is not null) {
                SendPurchase(purchase);
            }
        }
        catch (Exception ex) {
            Logger.Error("Loyalty Store | Claiming tokens failed. {0}", Logger.Args(ex));
        }
    }

    // The game's daily spiral screen asks whether the account is in the loyalty program.
    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_LOYALTYPROGRAMFORDAILYSPIRAL))]
    private void ReceiveProgramQuery(WIZARD3_56_PROTOCOL.MSG_LOYALTYPROGRAMFORDAILYSPIRAL message) {
        if (GetActiveWizard() is not { } wizard) {
            return;
        }

        SendToSocket(new WIZARD3_56_PROTOCOL.MSG_LOYALTYPROGRAMFORDAILYSPIRAL {
            LoyaltyProgramFlag = 1,
            LoyaltyNumTokensCanClaim = LoyaltyStore.Change(wizard.AccountId, LoyaltyStore.CanClaim),
        });
    }

    /// <summary>Gives an account the tokens of this rotation; null when they were claimed already.</summary>
    internal static LoyaltyStore.Purchase Claim(ulong accountId, ulong charId, string name)
        => LoyaltyStore.Change(accountId, account => {
            var tokens = LoyaltyStore.CanClaim(account);
            if (tokens <= 0 || LoyaltyStore.Now() is not { } now) {
                return null;
            }

            account.Tokens += tokens;
            account.ClaimedKey = now.Key;
            var entry = new LoyaltyStore.Purchase {
                Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), CharId = charId, Name = name,
                ItemId = 0, Tokens = tokens, Balance = account.Tokens,
            };
            LoyaltyStore.AddHistory(account, entry);
            Logger.Information("Loyalty Store | Account {0} claimed {1} tokens; it has {2}.", Logger.Args(accountId, tokens, account.Tokens));

            return entry;
        });

    // The window's balance, purchase log and purchase count follow this one entry.
    private void SendPurchase(LoyaltyStore.Purchase purchase)
        => SendToSocket(new WIZARD3_56_PROTOCOL.MSG_UPDATELOYALTYPURCHASE {
            LoyaltyPurchaseData = LoyaltyStore.Serialize(LoyaltyStore.EntryOf(purchase), Imcodec.ObjectProperty.SerializerFlags.None),
        });

    private static string WhyNot(LoyaltyStore.AccountState account, LoyaltyStore.RotationItem ware) {
        if (account.Tokens < ware.Price) {
            return $"{ware.Name} costs {ware.Price} Loyalty Token{(ware.Price == 1 ? "" : "s")} and you have {account.Tokens}.";
        }

        if (ware.Limit > 0 && account.Bought.GetValueOrDefault(ware.TemplateId) >= ware.Limit) {
            return $"You bought all {ware.Limit} of {ware.Name} this rotation.";
        }

        return null;
    }

}
