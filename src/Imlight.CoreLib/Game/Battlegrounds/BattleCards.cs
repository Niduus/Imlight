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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Battlegrounds;

/*
 * The Battle Card Vendor in an arena's base (BG-TCVendor-001A, Actaeon): battle cards for the wizard's beast form,
 * bought with Battle Coins (BeastmoonProgress). What the game shows and sends, read from WizardGraphicalClient
 * (BattleCardShop.gui, WizardGUIManager::HandleOpenBattleCardShop, BattleCardShop):
 * - MSG_BATTLECARDSHOPLIST: GlobalID the vendor, Data a BattleCardShopOffering (read with property mask 5): the cards
 *   for sale (the form's PolymorphBattlegroundsBehaviorTemplate m_battleCardsForSale: spell name and most copies),
 *   the cards the wizard has (m_givenSpells) and how many they may carry (the form level's m_ratingInfo
 *   m_maxBattleCards). m_nextLevelBattleCards is a list of texts not worked out yet; it is sent empty.
 * - MSG_BATTLECARDBUY: Data is the cards picked, a SpellDataList (spell template IDs and counts, mask 0x18); the server
 *   answers MSG_BATTLECARDBUYCONFIRM (Failure 0 when bought) and sends the list again.
 * - MSG_BATTLECARDTRASH: SpellID a card to throw away; MSG_BATTLECARDTRASHCONFIRM.
 * A card costs its spell's m_baseCost in Battle Coins ([Beastmoon] BattleCardCost when the spell has none). The cards
 * bought for a form go into its deck in battle (BeastCombat.For).
 */
internal static class BattleCards {

    internal const string VENDOR_PREFIX = "BG-TCVendor";
    private const uint SHOP_MASK = 5;
    private const uint BUY_MASK = 0x18;

    internal static int DefaultCost => Math.Max(0, ConfigurationManager.GetValue("Beastmoon.BattleCardCost", 10));

    private static readonly ObjectSerializer s_serializer = new(Versionable: false, Behaviors: SerializerFlags.None);

    /// <summary>The form's shop behavior, or null when the wizard is no beast.</summary>
    private static PolymorphBattlegroundsBehaviorTemplate ShopOf(Wizard wizard, out string formName) {
        formName = Battlegrounds.Of(wizard.CharId)?.FormName;

        return formName is null ? null
            : Battlegrounds.Form(formName, out _)?.m_behaviors?.OfType<PolymorphBattlegroundsBehaviorTemplate>().FirstOrDefault();
    }

    /// <summary>How many battle cards the form may carry at the wizard's level of it.</summary>
    private static int MaxCards(PolymorphBattlegroundsBehaviorTemplate shop, int level) {
        var ratings = shop.m_ratingInfo ?? [];

        return ratings.Count == 0 ? 0 : ratings[Math.Clamp(level, 0, ratings.Count - 1)]?.m_maxBattleCards ?? 0;
    }

    /// <summary>The vendor's list for the wizard, or null with why not.</summary>
    internal static IMessage ShopList(Wizard wizard, ulong vendorId, out string why) {
        why = null;
        var shop = ShopOf(wizard, out var formName);
        if (shop is null) {
            why = "Take a beast form at a statue first; the vendor sells cards for your beast.";

            return null;
        }

        var level = BeastmoonProgress.LevelOf(wizard.CharId, formName);
        var owned = BeastmoonProgress.BattleCardsOf(wizard.CharId, formName);
        var offering = new BattleCardShopOffering {
            m_battleCardsForSale = [.. shop.m_battleCardsForSale ?? []],
            m_givenSpells = [.. owned.GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Spell: SpellFactory.GetSpell(g.Key), Count: g.Count()))
                .Where(s => s.Spell is not null)
                .Select(s => new SpellData { m_templateID = s.Spell.m_templateID, m_enchantment = 0, m_quantity = (uint) s.Count })],
            m_maxTotalBattleCards = MaxCards(shop, level),
            // A list of texts in the game's class; what it holds is not known yet.
            m_nextLevelBattleCards = [],
        };

        if (!s_serializer.Serialize(offering, SHOP_MASK, out ByteString data)) {
            why = "The vendor's list could not be made.";

            return null;
        }

        return new WIZARD2_53_PROTOCOL.MSG_BATTLECARDSHOPLIST { GlobalID = vendorId, Data = data };
    }

    /// <summary>Buys the cards picked (MSG_BATTLECARDBUY Data). Returns why not, or null when bought.</summary>
    internal static string Buy(Wizard wizard, IActorRef session, ByteString data) {
        var shop = ShopOf(wizard, out var formName);
        if (shop is null) {
            return "Take a beast form at a statue first.";
        }

        var names = PickedNames(data);
        if (names.Count == 0) {
            return "No cards were picked.";
        }

        var forSale = (shop.m_battleCardsForSale ?? []).Where(c => c is not null)
            .ToDictionary(c => c.m_spellName.ToString(), c => c.m_maxCopies, StringComparer.OrdinalIgnoreCase);
        var level = BeastmoonProgress.LevelOf(wizard.CharId, formName);
        var max = MaxCards(shop, level);
        var owned = BeastmoonProgress.BattleCardsOf(wizard.CharId, formName);

        var cost = 0;
        foreach (var name in names) {
            if (!forSale.TryGetValue(name, out var copies)) {
                return $"{name} is not sold for this beast.";
            }

            if (owned.Count(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) + names.Count(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) > copies) {
                return $"This beast may carry {copies} of that card.";
            }

            cost += CostOf(name);
        }

        if (owned.Count + names.Count > max) {
            return $"This beast may carry {max} battle cards at its level.";
        }

        if (BeastmoonProgress.BattleCoinsOf(wizard) < cost) {
            return $"That costs {cost} Battle Coins.";
        }

        BeastmoonProgress.AddBattleCoins(wizard, session, -cost);
        BeastmoonProgress.Change(wizard.CharId, h => {
            if (!h.BattleCards.TryGetValue(formName, out var cards)) {
                cards = [];
                h.BattleCards[formName] = cards;
            }

            cards.AddRange(names);

            return true;
        });
        Logger.Information("Beastmoon | {0} bought {1} for {2} ({3} Battle Coins).",
            Logger.Args(wizard.CharId, string.Join(", ", names), formName, cost));

        return null;
    }

    /// <summary>Throws away one card the form carries. Returns why not, or null.</summary>
    internal static string Trash(Wizard wizard, int spellId) {
        var formName = Battlegrounds.Of(wizard.CharId)?.FormName;
        if (formName is null) {
            return "Take a beast form at a statue first.";
        }

        var removed = BeastmoonProgress.Change(wizard.CharId, h => {
            if (!h.BattleCards.TryGetValue(formName, out var cards)) {
                return false;
            }

            var index = cards.FindIndex(n => SpellFactory.GetSpell(n)?.m_templateID == (uint) spellId);
            if (index < 0) {
                return false;
            }

            cards.RemoveAt(index);

            return true;
        });

        return removed ? null : "This beast does not carry that card.";
    }

    /// <summary>The wizard's Battle Book as it stands: their form's battle cards and how many it may carry.</summary>
    internal static ClientBattleBookBehavior BattleBook(Wizard wizard) {
        var (cards, rating) = BookContents(wizard);

        return new ClientBattleBookBehavior {
            m_behaviorTemplateNameID = Imcodec.Cryptography.StringHash.Compute("BattleBookBehavior"),
            m_spellList = cards,
            m_rating = rating,
        };
    }

    private static (List<SpellData> Cards, PolymorphRatingInfo Rating) BookContents(Wizard wizard) {
        var shop = ShopOf(wizard, out var formName);
        var level = formName is null ? 0 : BeastmoonProgress.LevelOf(wizard.CharId, formName);
        var cards = formName is null ? [] : DeckCards(wizard.CharId, formName)
            .Select(c => new SpellData { m_templateID = c.TemplateId, m_enchantment = 0, m_quantity = c.Quantity })
            .ToList();

        return (cards, new PolymorphRatingInfo { m_maxBattleCards = shop is null ? 0 : MaxCards(shop, level) });
    }

    /// <summary>
    /// Brings the wizard's Battle Book in their game up to date (a new form, cards bought or thrown away):
    /// MSG_BattleBookClear, MSG_BattleBookAdd for each card, MSG_BattleBookRating.
    /// </summary>
    internal static void SyncBattleBook(Wizard wizard, IActorRef session) {
        if (wizard is null || session is null || !Battlegrounds.IsBattlegroundZone(wizard.Zone)) {
            return;
        }

        var (cards, rating) = BookContents(wizard);
        session.Tell(new WIZARD2_53_PROTOCOL.MSG_BattleBookClear { GlobalID = wizard.GameObjectID });
        foreach (var card in cards) {
            for (var i = 0; i < card.m_quantity; i++) {
                session.Tell(new WIZARD2_53_PROTOCOL.MSG_BattleBookAdd { GlobalID = wizard.GameObjectID, SpellID = card.m_templateID, EnchantmentID = 0 });
            }
        }

        if (s_serializer.Serialize(rating, SHOP_MASK, out ByteString data)) {
            session.Tell(new WIZARD2_53_PROTOCOL.MSG_BattleBookRating { GlobalID = wizard.GameObjectID, RatingData = data });
        }
    }

    /// <summary>The cards the wizard bought for this form, for its deck in battle.</summary>
    internal static List<CombatDeckSpellData> DeckCards(ulong charId, string formName)
        => [.. BeastmoonProgress.BattleCardsOf(charId, formName)
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Spell: SpellFactory.GetSpell(g.Key), Count: g.Count()))
            .Where(s => s.Spell is not null)
            .Select(s => new CombatDeckSpellData { TemplateId = s.Spell.m_templateID, Quantity = (uint) s.Count })];

    private static int CostOf(string spellName) {
        var spell = SpellFactory.GetSpell(spellName);
        var template = spell is null ? null : CoreObjectFactory.GetCoreTemplate(spell.m_templateID) as SpellTemplate;

        return template is { m_baseCost: > 0 } ? template.m_baseCost : DefaultCost;
    }

    // The game's pick: a SpellDataList (seen 2026-11-14). Any other object's first list of texts is taken as names.
    private static List<string> PickedNames(ByteString data) {
        var names = new List<string>();
        try {
            if (!s_serializer.Deserialize<PropertyClass>(data, BUY_MASK, out var picked) || picked is null) {
                Logger.Warning("Beastmoon | could not read a battle card pick ({0} bytes).", Logger.Args(data.Length));

                return names;
            }

            // The game sends a SpellDataList: the cards' spell template IDs and how many of each.
            if (picked is SpellDataList spells) {
                foreach (var spell in spells.m_spellDataList ?? []) {
                    var name = spell is null ? null : (CoreObjectFactory.GetCoreTemplate(spell.m_templateID) as SpellTemplate)?.m_name.ToString();
                    if (!string.IsNullOrEmpty(name)) {
                        names.AddRange(Enumerable.Repeat(name, (int) Math.Max(1u, spell.m_quantity)));
                    }
                }
            }

            foreach (var property in names.Count > 0 ? [] : picked.GetType().GetProperties()) {
                if (property.GetValue(picked) is not (string or ByteString) && property.GetValue(picked) is IEnumerable list) {
                    foreach (var item in list) {
                        if (item is ByteString or string) {
                            names.Add(item.ToString());
                        }
                    }
                }

                if (names.Count > 0) {
                    break;
                }
            }

            Logger.Information("Beastmoon | a battle card pick ({0}): {1}.", Logger.Args(picked.GetType().Name, string.Join(", ", names)));
        }
        catch (Exception ex) {
            Logger.Warning("Beastmoon | could not read a battle card pick: {0}", Logger.Args(ex.Message));
        }

        return names;
    }

}
