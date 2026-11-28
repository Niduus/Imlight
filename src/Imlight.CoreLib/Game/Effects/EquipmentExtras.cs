/*
 * Imlight
 * Copyright (C) 2026 Revive101
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
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.Cryptography;
using Imcodec.CoreObject;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Equipment;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Effects;

/// <summary>
/// The stats a wizard gets from the worn pet's talents and from jewels in worn gear, on top of the gear itself.
/// </summary>
internal static class EquipmentExtras {

    /// <summary>
    /// Takes off the pet and jewel stats added before and adds them again for what is worn now.
    /// Call it after anything that changes the worn pet, a pet's stats or talents, or a socket.
    /// </summary>
    internal static void Refresh(Wizard wizard) {
        try {
            Remove(wizard);
            Apply(wizard);
        }
        catch (Exception e) {
            Logger.Error("Could not update pet and jewel stats for {0}: {1}", Logger.Args(wizard?.CharId, e));
        }
    }

    /// <summary>
    /// Forgets what was added without taking it off, for when the stats were just rebuilt from scratch.
    /// </summary>
    internal static void Forget(Wizard wizard) {
        // Rebuilt stats do not touch the cards, so those still come off: left in, every rebuild added the pet's
        // cards again (4 Black Mantles instead of 2).
        foreach (var info in wizard.AppliedJewelEffects) {
            if (info is ProvideSpellEffectInfo spell) {
                RemoveCards(wizard, spell);
            }
        }

        wizard.AppliedPetEffects = [];
        wizard.AppliedJewelEffects = [];
        wizard.AppliedCardEffects = [];
        // Rebuilt stats hold the carriers' own stats again and no custom ones: nothing of Blood Moon gear to undo.
        CustomGear.WornOn(wizard).Clear();
    }

    /// <summary>
    /// The card effects taken off and put on by the last refreshes, for the equipment service to tell the game.
    /// Cleared by reading them.
    /// </summary>
    internal static (List<GameEffectBase> Removed, List<GameEffectBase> Added) TakeCardEffectChanges(Wizard wizard) {
        var changes = (wizard.RemovedCardEffects, wizard.AddedCardEffects);
        wizard.RemovedCardEffects = [];
        wizard.AddedCardEffects = [];

        return changes;
    }

    /// <summary>
    /// The messages that tell the game which pet and jewel effects came off and went on since it was last told,
    /// for callers outside the equipment service, such as feeding a pet. Clears the changes like
    /// <see cref="TakeCardEffectChanges"/>.
    /// </summary>
    internal static List<IMessage> TakeCardEffectMessages(Wizard wizard) {
        var (removed, added) = TakeCardEffectChanges(wizard);
        var messages = new List<IMessage>();
        foreach (var effect in removed) {
            messages.Add(new GAME_5_PROTOCOL.MSG_REMOVEEFFECT {
                GameObjectID = wizard.GameObjectID,
                EffectNameID = effect.m_effectNameID,
                InternalID = effect.m_internalID,
            });
        }

        foreach (var effect in added) {
            if (!s_effectSerializer.Serialize(effect, PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit, out var data)) {
                Logger.Error("Failed to serialize effect {0} for {1}.", Logger.Args(effect.m_effectNameID, wizard.CharId));

                continue;
            }

            messages.Add(new GAME_5_PROTOCOL.MSG_ADDEFFECT {
                GameObjectID = wizard.GameObjectID,
                EffectData = data,
            });
        }

        return messages;
    }

    private static readonly CoreObjectSerializer s_effectSerializer = new(versionable: false, behaviors: SerializerFlags.None);

    private static void Remove(Wizard wizard) {
        foreach (var (name, effect) in wizard.AppliedPetEffects) {
            CharacterEffectHelper.RemoveStatisticEffectFromStats(wizard.GameStats, name, effect);
        }

        foreach (var info in wizard.AppliedJewelEffects) {
            // Cards come off in Forget.
            if (info is ProvideSpellEffectInfo) {
                continue;
            }

            CharacterEffectHelper.RemoveGameEffectFromStats(wizard.GameStats, info);
        }

        // The cards' effects tell the game to list them with the item cards, like a wand's.
        foreach (var effect in wizard.AppliedCardEffects) {
            wizard.GameEffects.Remove(effect);
            if (!wizard.AddedCardEffects.Remove(effect)) {
                wizard.RemovedCardEffects.Add(effect);
            }
        }

        UndoCustomGear(wizard);
        Forget(wizard);
    }

    private static void RemoveCards(Wizard wizard, ProvideSpellEffectInfo spell) {
        // The card's id is the hash of its template's own name, which the effect may spell differently.
        var spellId = SpellFactory.GetSpell(spell.m_spellName)?.m_spellID ?? StringHash.Compute(spell.m_spellName);
        for (var i = 0; i < Math.Max(1, spell.m_numSpells); i++) {
            wizard.RemoveTemporarySpell(spellId);
        }
    }

    private static void Apply(Wizard wizard) {
        var equipped = wizard.EquipmentBehavior?.EquippedItems ?? [];
        // Blood Moon gear first, before any jewel or pet effect of the same slot is listed.
        foreach (var item in equipped.ToList()) {
            if (CustomGear.Of(item) is { } piece) {
                ApplyCustomGear(wizard, item, piece);
            }
        }

        foreach (var item in equipped.ToList()) {
            var template = ItemHelper.GetItemTemplate(item);
            if (template is null) {
                continue;
            }

            if (PetProgress.GetBehavior(item) is not null) {
                if (PetProgress.EnsureInitialized(item)) {
                    WizardItemCollection.SaveItemBehaviors(item);
                }
                foreach (var (name, effect) in PetProgress.ComputeEffects(item, JewelSockets.GetPetJewelTalents(item))) {
                    CharacterEffectHelper.AddStatisticEffectToStats(wizard.GameStats, name, effect);
                    wizard.AppliedPetEffects.Add((name, effect));

                    // Also listed as an effect of the pet's slot, like gear's: the stats page adds up those effects,
                    // so without it a talent showed only in the hover text, not as a number (Niduus, 2026-10-19).
                    var shown = effect with {
                        m_itemSlotID = ItemHelper.GetItemSlotHash(template),
                        m_internalID = wizard.NextEffectId(),
                    };
                    wizard.GameEffects.Add(shown);
                    wizard.AppliedCardEffects.Add(shown);
                    wizard.AddedCardEffects.Add(shown);
                }

                // Card talents give their cards for battle; they are taken off like a jewel's cards.
                foreach (var info in PetProgress.CardEffects(item, JewelSockets.GetPetJewelTalents(item))) {
                    AddCards(wizard, info, ItemHelper.GetItemSlotHash(template));
                }

                continue;
            }

            foreach (var jewel in JewelSockets.GetSocketedJewels(item)) {
                foreach (var info in jewel.m_equipEffects ?? []) {
                    switch (info) {
                        case ProvideSpellEffectInfo spell:
                            AddCards(wizard, spell, ItemHelper.GetItemSlotHash(template));
                            break;
                        case StatisticEffectInfo or StartingPipEffectInfo:
                            if (CharacterEffectHelper.AddGameEffectToStats(wizard.GameStats, info) is not null) {
                                wizard.AppliedJewelEffects.Add(info);

                                // Also listed as an effect of the item's slot, like the pet's talents: the stats page
                                // adds up those effects, so a jewel's flat resist showed only in the hover text, not
                                // in the row under the schools (Niduus, 2026-10-19).
                                if (GameEffectFactory.CreateEffectFromInfo(info, ItemHelper.GetItemSlotHash(template)) is { } shown) {
                                    shown.m_internalID = wizard.NextEffectId();
                                    wizard.GameEffects.Add(shown);
                                    wizard.AppliedCardEffects.Add(shown);
                                    wizard.AddedCardEffects.Add(shown);
                                }
                            }
                            break;
                    }
                }
            }
        }
    }

    private static void AddCards(Wizard wizard, ProvideSpellEffectInfo info, uint slotHash) {
        if (GameEffectFactory.CreateEffectFromInfo(info, slotHash) is ProvideSpellEffect provide) {
            foreach (var card in SpellFactory.CreateSpellsFromEffect(provide) ?? []) {
                wizard.AddTemporarySpell(card);
            }

            wizard.AppliedJewelEffects.Add(info);

            // Listed as an effect of the worn item, so the deck window shows the cards with the item cards.
            provide.m_internalID = wizard.NextEffectId();
            wizard.GameEffects.Add(provide);
            wizard.AppliedCardEffects.Add(provide);
            wizard.AddedCardEffects.Add(provide);
            Logger.Verbose("Item card effect {0} x{1} (slot {2}, id {3}) for {4}.",
                Logger.Args(info.m_spellName, info.m_numSpells, slotHash, provide.m_internalID, wizard.CharId));
        }
    }


    // ------------------------------------------------------------------ Blood Moon gear (Game/Equipment/CustomGear.cs)

    /// <summary>
    /// Takes the QA carrier's own stats off a worn custom piece and puts the piece's own stats and cards on, each listed as
    /// an effect of the piece's slot so the stats page and the deck window show them.
    /// </summary>
    private static void ApplyCustomGear(Wizard wizard, WizClientObjectItem item, CustomGear.Piece piece) {
        var carrier = ItemHelper.GetItemTemplate(item);
        if (carrier is null) {
            return;
        }

        var worn = new CustomGear.Worn { ItemId = item.m_globalID.Full, Carrier = carrier, SlotHash = ItemHelper.GetItemSlotHash(carrier) };
        foreach (var info in carrier.m_equipEffects ?? []) {
            if (info is null) {
                continue;
            }

            var nameHash = StringHash.Compute(info.m_effectName);
            var shown = wizard.GameEffects.Find(e => e.m_effectNameID == nameHash && e.m_itemSlotID == worn.SlotHash
                                                     && !wizard.AppliedCardEffects.Contains(e));
            if (shown is null) {
                continue;
            }

            wizard.GameEffects.Remove(shown);
            if (!wizard.AddedCardEffects.Remove(shown)) {
                wizard.RemovedCardEffects.Add(shown);
            }

            if (shown is ProvideSpellEffect provide) {
                for (var i = 0; i < Math.Max(1, provide.m_numSpells); i++) {
                    wizard.RemoveTemporarySpell(StringHash.Compute(provide.m_spellName));
                }
            }
            else {
                CharacterEffectHelper.RemoveGameEffectFromStats(wizard.GameStats, info);
            }

            worn.CarrierShown.Add(shown);
        }

        foreach (var (name, effect) in CustomGear.Effects(piece)) {
            CharacterEffectHelper.AddStatisticEffectToStats(wizard.GameStats, name, effect);
            worn.Added.Add((name, effect));
            var shown = effect with { m_itemSlotID = worn.SlotHash, m_internalID = wizard.NextEffectId() };
            wizard.GameEffects.Add(shown);
            worn.Shown.Add(shown);
            wizard.AddedCardEffects.Add(shown);
        }

        // Cards the piece gives: Blood Moon cards (CustomCards.json) and the game's own cards by name.
        foreach (var provide in CustomGearCards(piece, worn.SlotHash)) {
            provide.m_internalID = wizard.NextEffectId();
            foreach (var card in Imlight.CoreLib.Game.Spells.CustomCards.CardsFor(provide)) {
                wizard.AddTemporarySpell(card);
            }

            wizard.GameEffects.Add(provide);
            worn.Shown.Add(provide);
            wizard.AddedCardEffects.Add(provide);
        }

        CustomGear.WornOn(wizard).Add(worn);
        Logger.Verbose("Custom gear {0} ({1}) worn by {2}: {3} QA stats off, {4} stats and {5} card lines on.",
            Logger.Args(piece.Id, piece.Name, wizard.CharId, worn.CarrierShown.Count, worn.Added.Count, worn.Shown.Count - worn.Added.Count));
    }

    private static IEnumerable<ProvideSpellEffect> CustomGearCards(CustomGear.Piece piece, uint slotHash) {
        foreach (var group in piece.Cards.GroupBy(c => c)) {
            if (Imlight.CoreLib.Game.Spells.CustomCards.Provide(group.Key, group.Count(), slotHash) is { } provide) {
                yield return provide;
            }
        }

        foreach (var group in piece.Spells.GroupBy(s => s, StringComparer.OrdinalIgnoreCase)) {
            var info = new ProvideSpellEffectInfo { m_effectName = "ProvideSpell", m_spellName = group.Key, m_numSpells = group.Count() };
            if (SpellFactory.Exists(group.Key) && GameEffectFactory.CreateEffectFromInfo(info, slotHash) is ProvideSpellEffect provide) {
                yield return provide;
            }
        }
    }

    /// <summary>Puts back what <see cref="ApplyCustomGear"/> changed: the carrier's stats come back only while it is still worn.</summary>
    private static void UndoCustomGear(Wizard wizard) {
        var list = CustomGear.WornOn(wizard);
        var equipped = (wizard.EquipmentBehavior?.EquippedItems ?? []).Select(i => i.m_globalID.Full).ToHashSet();
        foreach (var worn in list) {
            foreach (var (name, effect) in worn.Added) {
                CharacterEffectHelper.RemoveStatisticEffectFromStats(wizard.GameStats, name, effect);
            }

            foreach (var shown in worn.Shown) {
                wizard.GameEffects.Remove(shown);
                if (shown is ProvideSpellEffect provide) {
                    Imlight.CoreLib.Game.Spells.CustomCards.RemoveCardsFor(wizard, provide);
                }

                if (!wizard.AddedCardEffects.Remove(shown)) {
                    wizard.RemovedCardEffects.Add(shown);
                }
            }

            if (!equipped.Contains(worn.ItemId)) {
                continue;
            }

            foreach (var shown in worn.CarrierShown) {
                var info = (worn.Carrier.m_equipEffects ?? []).FirstOrDefault(i => i is not null && StringHash.Compute(i.m_effectName) == shown.m_effectNameID);
                wizard.GameEffects.Add(shown);
                if (!wizard.RemovedCardEffects.Remove(shown)) {
                    wizard.AddedCardEffects.Add(shown);
                }

                if (shown is ProvideSpellEffect provide) {
                    foreach (var card in SpellFactory.CreateSpellsFromEffect(provide) ?? []) {
                        wizard.AddTemporarySpell(card);
                    }
                }
                else if (info is not null) {
                    CharacterEffectHelper.AddGameEffectToStats(wizard.GameStats, info);
                }
            }
        }

        list.Clear();
    }

}
