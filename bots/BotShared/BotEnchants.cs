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

using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// What an enchantment card does to the card it is played on, read from the game's data (Niduus, 2026-11-17: "I want him to
/// use/buy the strongest, being Vanilla Gargantuan ... he should get more Giants [than Tough]. Maximizing damage because
/// that's how to win battles. He also has sniper and keen eyes ... the same rule should apply"). Damage first, by its
/// amount, so the strongest damage enchantment always wins; then pierce, accuracy, and the ones for heals and shields.
/// </summary>
internal static class BotEnchants {

    internal const string DAMAGE = "damage";
    internal const string PIERCE = "pierce";
    internal const string ACCURACY = "accuracy";
    internal const string HEAL = "heal";
    internal const string WARD = "ward";
    internal const string OTHER = "other";

    /// <summary>One enchantment: its kind and its amount (the biggest of its card changes of that kind).</summary>
    internal sealed record Enchant(string Kind, int Amount) {

        /// <summary>How much he wants it: damage far above the rest (Giant over Tough), each kind by its amount.</summary>
        internal int Worth => Kind switch {
            DAMAGE => 400 + (Amount * 2),
            PIERCE => 200 + (Amount * 5),
            ACCURACY => 60 + Amount,
            HEAL => 50 + (Amount / 2),
            WARD => 40 + (Amount / 2),
            _ => 5,
        };

        internal string Plain => Kind switch {
            DAMAGE => $"+{Amount} damage on the hit it is played on",
            PIERCE => $"+{Amount}% pierce on the hit it is played on",
            ACCURACY => $"+{Amount}% accuracy on the card it is played on",
            HEAL => $"+{Amount} on the heal it is played on",
            WARD => $"+{Amount} on the shield or trap it is played on",
            _ => "changes the card it is played on",
        };

    }

    /// <summary>
    /// Whether a wizard of this level may play this card: every enchantment treasure card of the game needs level 50 or
    /// more (Tough and Giant 52, Sniper 54, Gargantuan 58), so a level 26 wizard holding them can't use them yet.
    /// </summary>
    internal static bool Playable(SpellTemplate template, int level) => template is not null && template.m_levelRestriction <= level;

    /// <summary>The enchantment a card is, or null when it is not one.</summary>
    internal static Enchant Of(uint templateId)
        => CoreObjectFactory.GetCoreTemplate(templateId) is SpellTemplate template ? Of(template) : null;

    internal static Enchant Of(SpellTemplate template) {
        if (template is null || !CombatEnchantments.IsEnchantment(template)) {
            return null;
        }

        var best = (Kind: OTHER, Amount: 0);
        foreach (var e in CombatEnchantments.Leaves(template.m_effects ?? []).Where(e => e is not null)) {
            var (kind, amount) = e.m_effectType switch {
                kSpellEffects.kModifyCardDamage or kSpellEffects.kModifyCardOutgoingDamage => (DAMAGE, e.m_effectParam),
                // A damage-by-rank enchantment adds its amount for every pip of the card: about a four-pip hit's worth.
                kSpellEffects.kModifyCardDamagebyRank => (DAMAGE, e.m_effectParam * 4),
                kSpellEffects.kModifyCardArmorPiercing or kSpellEffects.kModifyCardOutgoingArmorPiercing => (PIERCE, e.m_effectParam),
                kSpellEffects.kModifyCardAccuracy or kSpellEffects.kModifyCardOutgoingAccuracy => (ACCURACY, e.m_effectParam),
                kSpellEffects.kModifyCardHeal or kSpellEffects.kModifyCardOutgoingHeal => (HEAL, e.m_effectParam),
                kSpellEffects.kModifyCardCharm or kSpellEffects.kModifyCardWard or kSpellEffects.kModifyCardIncomingDamage
                    or kSpellEffects.kModifyCardAbsorbDamage => (WARD, e.m_effectParam),
                _ => (OTHER, 0),
            };
            if (amount <= 0) {
                continue;
            }

            var candidate = new Enchant(kind, amount);
            if (best.Kind == OTHER || candidate.Worth > new Enchant(best.Kind, best.Amount).Worth) {
                best = (kind, amount);
            }
        }

        return new Enchant(best.Kind, best.Amount);
    }

    /// <summary>
    /// How much this enchantment adds to this card in a fight: a damage one on a hit (more on a hit that strikes every
    /// enemy, each gets it), accuracy only on a card that can miss, a heal one on a heal, a ward one on a shield or trap.
    /// 0 when it is no use on that card.
    /// </summary>
    internal static int On(Enchant enchant, RewrittenAI.Card card, int accuracy, int enemies) {
        if (enchant is null || card is null) {
            return 0;
        }

        return enchant.Kind switch {
            DAMAGE when card.Hit => enchant.Worth * (card.HitsAll ? System.Math.Max(1, enemies) : 1),
            PIERCE when card.Hit => enchant.Worth,
            ACCURACY when (card.Hit || card.Trap || card.Shield || card.Blade) && accuracy < 100 => enchant.Worth,
            HEAL when card.Heal && !card.Hit => enchant.Worth,
            WARD when card.Shield || card.Trap => enchant.Worth,
            _ => 0,
        };
    }

}
