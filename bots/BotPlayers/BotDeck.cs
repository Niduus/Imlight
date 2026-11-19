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
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.BotPlayers;

/// <summary>
/// A bot's deck (Niduus, 2026-11-12): the vanilla spells a wizard of its school would have at its level, from the real
/// game's trainers (quest-taught school spells included), and nothing above it: a level 50 Ice wizard never has a spell
/// of a later world. Like players train, it also has a few universal helpers (Weakness, Tower Shield, Feint) once its
/// level allows them, and some bots took Life as their second school for its heals. The deck never runs out: the battle
/// reshuffles it when it gets low (CombatDuelComponent.Bots.cs).
/// </summary>
internal static class BotDeck {

    // Universal helpers most players train, with the level a trainer teaches them.
    private static readonly string[] s_utility = ["Weakness", "Tower Shield", "Feint"];

    // A second school's heals, for the bots that picked Life.
    private static readonly string[] s_lifeHeals = ["Sprite", "Satyr", "Fairy"];

    private static readonly string[] s_neverNames = ["FUSE", "Mutate", "Polymorph", "_Trainable", "Ramp_", "Mega "];
    private static readonly HashSet<string> s_blockedNeeds = new(StringComparer.OrdinalIgnoreCase) {
        "SecondarySchool", "HasBadge", "ShadowMagic",
    };

    /// <summary>The bot's spells, as a wizard's book lists them.</summary>
    internal static List<SpellData> For(MagicSchool school, int level, bool lifeSecondary) {
        var schoolName = school.ToString();
        var all = VanillaTrainers.All.SelectMany(t => t.Spells)
            .Where(s => s.Kind == "School" && s.Rank <= level)
            .GroupBy(s => s.Id).Select(g => g.First())
            .ToList();

        var deck = new Dictionary<uint, int>();
        foreach (var spell in all) {
            if (!string.Equals(spell.School, schoolName, StringComparison.OrdinalIgnoreCase) || !Usable(spell)) {
                continue;
            }

            // The newest of a kind of spell matters most; the first few ranks drop out once there are better ones.
            deck[spell.Id] = spell.Rank >= level - 30 ? 4 : 2;
        }

        // Its school's blade and trap come from early school quests, not trainers.
        foreach (var (name, from) in new[] { ($"{schoolName} Blade", 3), ($"{schoolName} Trap", 6) }) {
            if (level >= from && Spells.SpellFactory.Exists(name) && Spells.SpellFactory.GetSpell(name)?.m_templateID is { } id and > 0) {
                deck.TryAdd(id, 3);
            }
        }

        foreach (var name in s_utility.Concat(lifeSecondary && school != MagicSchool.Life ? s_lifeHeals : [])) {
            if (all.FirstOrDefault(s => s.Name == name && Usable(s)) is { } helper) {
                deck.TryAdd(helper.Id, 2);
            }
        }

        return [.. deck.Where(p => CoreObjectFactory.GetCoreTemplate(p.Key) is SpellTemplate t && !t.m_Treasure)
            .Select(p => new SpellData { m_templateID = p.Key, m_quantity = (byte) p.Value })];
    }

    private static bool Usable(VanillaTrainers.Spell spell)
        => !s_neverNames.Any(n => spell.Name.Contains(n, StringComparison.Ordinal)) && !Needs(spell.Needs).Any(s_blockedNeeds.Contains);

    private static IEnumerable<string> Needs(VanillaTrainers.Need need) {
        if (need is null) {
            yield break;
        }

        yield return need.Class;
        foreach (var item in need.Items ?? []) {
            foreach (var inner in Needs(item)) {
                yield return inner;
            }
        }
    }

}
