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
 * SPELLBOOK SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages player spell deck modifications, including adding and 
 * removing spells from spell decks.
 * 
 * USAGE EXAMPLE:
 * Internal service handling spellbook-related messages within 
 * the game server session.
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using System.Linq;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Services;

internal class SpellbookService(SessionActor sessionActor) : MessageService(sessionActor) {

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new SpellbookService(parentActor));

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_ADDSPELLTODECK))]
    private void ReceiveAddSpellToDeck(WIZARD_12_PROTOCOL.MSG_ADDSPELLTODECK message) {
        var wizard = GetActiveWizard();
        var deckAddSuccess = wizard.AddSpellToDeck((uint) message.SpellID, message.DeckID);

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_ADDSPELLTODECK() {
            SpellID = message.SpellID,
            DeckID = message.DeckID,
            Success = (byte) (deckAddSuccess ? 1 : 0)
        });
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_REMOVESPELLFROMDECK))]
    private void ReceiveRemoveSpellFromDeck(WIZARD_12_PROTOCOL.MSG_REMOVESPELLFROMDECK message) {
        var wizard = GetActiveWizard();
        var deckRemoveSuccess = wizard.RemoveSpellFromDeck((uint) message.SpellID, message.DeckID);

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_REMOVESPELLFROMDECK() {
            SpellID = message.SpellID,
            DeckID = message.DeckID,
            Success = (byte) (deckRemoveSuccess ? 1 : 0)
        });
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_ADDTREASURESPELLTODECK))]
    private void ReceiveAddTreasureSpellToDeck(WIZARD_12_PROTOCOL.MSG_ADDTREASURESPELLTODECK message) {
        var wizard = GetActiveWizard();

        // Resolve the spell hash to a template ID.
        var templateId = SpellFactory.GetTemplateIdByHash((uint) message.SpellID);
        if (templateId == 0) {
            Logger.Warning("Could not resolve treasure card spell hash {0} to a template ID.",
                Logger.Args(message.SpellID));

            SendToSocket(new WIZARD_12_PROTOCOL.MSG_ADDTREASURESPELLTODECK() {
                SpellID = message.SpellID,
                EnchantmentID = message.EnchantmentID,
                DeckID = message.DeckID,
                NewSpell = 0,
                Success = 0
            });

            return;
        }

        var deckAddSuccess = wizard.AddTreasureCardToDeck(templateId, message.DeckID);

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_ADDTREASURESPELLTODECK() {
            SpellID = message.SpellID,
            EnchantmentID = message.EnchantmentID,
            DeckID = message.DeckID,
            NewSpell = 0,
            Success = (byte) (deckAddSuccess ? 1 : 0)
        });
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_REMOVETREASURESPELLFROMDECK))]
    private void ReceiveRemoveTreasureSpellFromDeck(WIZARD_12_PROTOCOL.MSG_REMOVETREASURESPELLFROMDECK message) {
        var wizard = GetActiveWizard();

        // Resolve the spell hash to a template ID.
        var templateId = SpellFactory.GetTemplateIdByHash((uint) message.SpellID);
        if (templateId == 0) {
            Logger.Warning("Could not resolve treasure card spell hash {0} to a template ID.",
                Logger.Args(message.SpellID));

            SendToSocket(new WIZARD_12_PROTOCOL.MSG_REMOVETREASURESPELLFROMDECK() {
                SpellID = message.SpellID,
                EnchantmentID = message.EnchantmentID,
                DeckID = message.DeckID,
                Success = 0,
                Destroy = 0
            });

            return;
        }

        var destroy = message.Destroy != 0;
        var deckRemoveSuccess = wizard.RemoveTreasureCardFromDeck(templateId, message.DeckID, destroy);

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_REMOVETREASURESPELLFROMDECK() {
            SpellID = message.SpellID,
            EnchantmentID = message.EnchantmentID,
            DeckID = message.DeckID,
            Success = (byte) (deckRemoveSuccess ? 1 : 0),
            Destroy = message.Destroy
        });
    }

    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_REMOVETREASURESPELLFROMBOOK))]
    private void ReceiveRemoveTreasureSpellFromBook(WIZARD_12_PROTOCOL.MSG_REMOVETREASURESPELLFROMBOOK message) {
        var wizard = GetActiveWizard();

        // Resolve the spell hash to a template ID.
        var templateId = SpellFactory.GetTemplateIdByHash((uint) message.SpellID);
        if (templateId == 0) {
            Logger.Warning("Could not resolve treasure card spell hash {0} to a template ID.",
                Logger.Args(message.SpellID));

            SendToSocket(new WIZARD_12_PROTOCOL.MSG_REMOVETREASURESPELLFROMBOOK() {
                SpellID = message.SpellID,
                EnchantmentID = message.EnchantmentID
            });

            return;
        }

        // Remove one copy from the book and persist.
        wizard.SpellbookBehavior.RemoveTreasureCard(templateId);
        WizardData.Collections.WizardCollection.RemoveTreasureCard(wizard, templateId);

        // Echo back to the client to confirm.
        SendToSocket(new WIZARD_12_PROTOCOL.MSG_REMOVETREASURESPELLFROMBOOK() {
            SpellID = message.SpellID,
            EnchantmentID = message.EnchantmentID
        });
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_UPDATEITEMSPELLEXCLUSIONLIST))]
    private void ReceiveUpdateItemSpellExclusionList(WIZARD2_53_PROTOCOL.MSG_UPDATEITEMSPELLEXCLUSIONLIST message) {
        var wizard = GetActiveWizard();
        var exclude = message.Exclude != 0;

        // Resolve the spell hash (SpellID) to a template ID.
        var spellTemplateId = SpellFactory.GetTemplateIdByHash((uint) message.SpellID);
        if (spellTemplateId == 0) {
            Logger.Warning("Could not resolve item spell exclusion hash {0} to a template ID.",
                Logger.Args(message.SpellID));

            SendToSocket(new WIZARD2_53_PROTOCOL.MSG_UPDATEITEMSPELLEXCLUSIONLIST {
                SpellID = message.SpellID,
                DeckID = message.DeckID,
                Exclude = message.Exclude,
                Success = 0
            });

            return;
        }

        // Update the in-memory exclusion list.
        var copies = wizard.SpellbookBehavior.TemporarySpells.Count(spell => spell.m_templateID == spellTemplateId);
        wizard.SpellbookBehavior.SetItemSpellExclusion(message.DeckID, spellTemplateId, exclude, copies);
        Logger.Information("Item card {0} (template {1}) {2} in deck {3}: gear gives {4}, now crossed out {5}.",
            Logger.Args(message.SpellID, spellTemplateId, exclude ? "crossed out" : "put back", message.DeckID, copies,
                wizard.SpellbookBehavior.ExcludedItemSpellCount(message.DeckID, spellTemplateId)));

        // Persist the change.
        WizardData.Collections.WizardCollection.UpdateCharacterSpellbookBehavior(wizard);

        // Echo success back to the client.
        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_UPDATEITEMSPELLEXCLUSIONLIST {
            SpellID = message.SpellID,
            DeckID = message.DeckID,
            Exclude = message.Exclude,
            Success = 1
        });
    }

    /// <summary>
    /// The deck window's Archmastery pick, or the Power Orb clicked in battle: the client sets the deck's school
    /// itself and tells us, so the deck keeps it. In battle the new school counts from the next round.
    /// </summary>
    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_SetDeckArchmastery))]
    private void ReceiveSetDeckArchmastery(WIZARD3_56_PROTOCOL.MSG_SetDeckArchmastery message) {
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return;
        }

        if (Combat.Archmastery.ToSchoolId(message.School) is not { } school) {
            Logger.Warning("Archmastery school {0} for deck {1} is not one of the seven schools; ignored.",
                Logger.Args(message.School, message.ItemGID));

            return;
        }

        try {
            // The deck named, or the equipped one when none is.
            var deckId = message.ItemGID;
            if (deckId == 0) {
                var deckSlot = wizard.EquipmentBehavior.SlotList.FirstOrDefault(s => s.SlotType == EquipmentSlotType.Deck);
                deckId = deckSlot?.ItemId ?? 0;
            }

            var deckItem = wizard.EquipmentBehavior.EquippedItems.FirstOrDefault(i => i.m_globalID == deckId)
                ?? wizard.InventoryBehavior.Items.FirstOrDefault(i => i.m_globalID == deckId);
            if (deckItem is not null && CoreObjectFactory.FindBehaviorInstance<DeckBehavior>(deckItem, out var deck)) {
                deck.m_archmasterySchool = school;
                WizardData.Collections.WizardItemCollection.SaveItemBehaviors(deckItem);
                Logger.Information("Deck {0} of {1}: Archmastery school {2}.",
                    Logger.Args(deckId, wizard.CharId, school == 0 ? "own school" : ((Shared.Behaviors.MagicSchool) school).ToString()));
            }
            else {
                Logger.Warning("Archmastery school {0}: deck {1} not found for {2}.", Logger.Args(school, deckId, wizard.CharId));
            }

            // Only the equipped deck (or the orb) changes the battle the wizard is in.
            var equippedDeckId = wizard.EquipmentBehavior.SlotList.FirstOrDefault(s => s.SlotType == EquipmentSlotType.Deck)?.ItemId;
            if (message.ItemGID == 0 || deckId == equippedDeckId) {
                SessionActor.ActorRef.Tell(new COMBAT_106_PROTOCOL.MSG_SETARCHMASTERYSCHOOL { School = school });
            }
        }
        catch (System.Exception e) {
            Logger.Error("Archmastery school could not be saved for {0}: {1}", Logger.Args(wizard.CharId, e.Message));
        }
    }

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceiveAttachComplete(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        var wizard = GetActiveWizard();

        // The Archmastery the character sheet shows (the level table's part; the client adds gear).
        if (wizard is not null) {
            SendToSocket(new WIZARD3_56_PROTOCOL.MSG_UPDATEARCHMASTERY {
                Stat = Combat.Archmastery.BaseRating(wizard.GameStats.MagicSchool, wizard.GameStats.Level),
            });
        }

        // Find the equipped deck.
        var deckSlot = wizard.EquipmentBehavior.SlotList
            .FirstOrDefault(s => s.SlotType == EquipmentSlotType.Deck);
        if (deckSlot?.ItemId is null) {
            return;
        }

        var deckItem = wizard.EquipmentBehavior.EquippedItems
            .FirstOrDefault(i => i.m_globalID == deckSlot.ItemId);
        if (deckItem is null) {
            return;
        }

        if (!CoreObjectFactory.FindBehaviorInstance<DeckBehavior>(deckItem, out var deckBehavior)) {
            return;
        }

        // Send MSG_UPDATEITEMSPELLEXCLUSIONLIST for each excluded item spell so the
        // client restores the X'd-out state of item cards on login.
        foreach (var (excludedDeckId, excludedTemplateIds) in wizard.SpellbookBehavior.ExcludedItemSpellIds) {
            foreach (var templateId in excludedTemplateIds) {
                var spell = SpellFactory.GetSpell(templateId);
                if (spell == null) {
                    continue;
                }

                SendToSocket(new WIZARD2_53_PROTOCOL.MSG_UPDATEITEMSPELLEXCLUSIONLIST {
                    SpellID = (int) spell.m_spellID,
                    DeckID = excludedDeckId,
                    Exclude = 1,
                    Success = 1
                });
            }
        }

        var spellList = deckBehavior.m_spellList;

        if (spellList is null) {
            return;
        }

        // Send MSG_ADDTREASURESPELLTODECK for each treasure card in the deck.
        foreach (var spellData in spellList) {
            var template = CoreObjectFactory.GetCoreTemplate(spellData.m_templateID);
            if (template is not SpellTemplate spellTemplate) {
                continue;
            }

            // Only send treasure cards — regular spells are handled separately.
            if (!spellTemplate.m_Treasure) {
                continue;
            }

            var spellHash = StringHash.Compute(spellTemplate.m_name);

            // Send one message per copy.
            for (var i = 0; i < spellData.m_quantity; i++) {
                SendToSocket(new WIZARD_12_PROTOCOL.MSG_ADDTREASURESPELLTODECK() {
                    SpellID = (int) spellHash,
                    EnchantmentID = 0,
                    DeckID = deckItem.m_globalID,
                    NewSpell = 0,
                    Success = 0
                });
            }
        }
    }
    
}
