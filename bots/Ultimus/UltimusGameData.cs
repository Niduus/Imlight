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
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Looks facts up in the game's own data the server holds (Niduus, 2026-11-16: "Allow him access to the databases of the
/// game"): a creature's level, starting health, schools and whether it is a boss, from its template; a card's school, pips
/// and kind, from the spell template. Read only, and knowledge only: it never changes the game, a quest or a battle, and
/// gives no way to skip or finish anything (the human rule). Ultimus and the Quest Team read it when they plan a fight and
/// when they think a stuck spot through.
/// </summary>
internal static class UltimusGameData {

    /// <summary>A creature by its template, in plain words ("Meowiarty (template 12345): rank 6, 2,000 health, Myth, a boss"), or empty.</summary>
    internal static string Creature(ulong template) {
        try {
            if (template == 0 || CoreObjectFactory.GetCoreTemplate(template) is not GameObjectTemplate t
                || t.m_behaviors?.OfType<NPCBehaviorTemplate>().FirstOrDefault() is not { } npc) {
                return "";
            }

            var name = MinimapIndex.NameOf(t);
            var parts = new List<string>();
            // The game's m_nLevel is the creature's rank (as Monstrology reads it, and as the game shows "Rank 6 Boss"), not
            // a wizard level (his log, 2026-11-16: "Meowiarty: level 6" for the rank 6 boss of Big Ben). Read by the exact
            // template of the creature in front of him, never by its name: many creatures share a name and health but not
            // their template (Niduus, 2026-11-16: "There are multiple mobs in the game with the same health, different ID's").
            if (npc.m_nLevel > 0) {
                parts.Add($"rank {npc.m_nLevel}");
            }

            if (npc.m_nStartingHealth > 0) {
                parts.Add($"{npc.m_nStartingHealth:N0} health");
            }

            string school = npc.m_schoolOfFocus;
            string second = npc.m_secondarySchoolOfFocus;
            if (!string.IsNullOrEmpty(school)) {
                parts.Add(!string.IsNullOrEmpty(second) && !second.Equals(school, StringComparison.OrdinalIgnoreCase) ? $"{school} and {second}" : school);
            }

            if (npc.m_bossMob) {
                parts.Add("a boss");
            }

            if (npc.m_maxShadowPips > 0) {
                parts.Add($"up to {npc.m_maxShadowPips} shadow pip(s)");
            }

            return parts.Count == 0 ? "" : $"{(name.Length > 0 ? name : "the creature")} (template {template}): {string.Join(", ", parts)}";
        }
        catch (Exception) {
            return "";
        }
    }

    /// <summary>The creature's starting health in the game's data, by its exact template (0 when not known).</summary>
    internal static int Health(ulong template) {
        try {
            return template != 0 && CoreObjectFactory.GetCoreTemplate(template) is GameObjectTemplate t
                   && t.m_behaviors?.OfType<NPCBehaviorTemplate>().FirstOrDefault() is { } npc ? Math.Max(0, npc.m_nStartingHealth) : 0;
        }
        catch (Exception) {
            return 0;
        }
    }

    /// <summary>Whether the game's data marks the creature a boss.</summary>
    internal static bool IsBoss(ulong template) {
        try {
            return template != 0 && CoreObjectFactory.GetCoreTemplate(template) is GameObjectTemplate t
                   && t.m_behaviors?.OfType<NPCBehaviorTemplate>().FirstOrDefault() is { m_bossMob: true };
        }
        catch (Exception) {
            return false;
        }
    }

    /// <summary>A card by its spell template, in plain words ("Fire Elf: Fire, 1 pip, a hit"), or empty.</summary>
    internal static string Card(uint spellId) {
        try {
            if (CoreObjectFactory.GetCoreTemplate(spellId) is not SpellTemplate t) {
                return "";
            }

            var card = BotShared.BotCards.CardOf(spellId);
            var name = card?.Shown is { Length: > 0 } shown ? shown : (string) t.m_name;
            var kinds = new List<string>();
            if (card is not null) {
                if (card.Hit) {
                    kinds.Add(card.HitsAll ? "a hit on every enemy" : "a hit");
                }

                if (card.Heal) {
                    kinds.Add("a heal");
                }

                if (card.Blade) {
                    kinds.Add("a blade");
                }

                if (card.Trap) {
                    kinds.Add("a trap");
                }

                if (card.Shield) {
                    kinds.Add("a shield");
                }
            }

            return $"{name}: {(card?.School is { Length: > 0 } s ? s + ", " : "")}{(card?.Rank ?? 0)} pip(s){(kinds.Count > 0 ? ", " + string.Join(", ", kinds) : "")}" +
                   (t.m_Treasure ? ", a treasure card" : "");
        }
        catch (Exception) {
            return "";
        }
    }

    /// <summary>Game data lines for a stuck bot's thinking: the step's creature and the creatures near it, from their templates.</summary>
    internal static List<string> Facts(ulong stepCreature, IEnumerable<ulong> near) {
        var facts = new List<string>();
        if (Creature(stepCreature) is { Length: > 0 } step) {
            facts.Add($"Game data (the step's creature): {step}.");
        }

        foreach (var line in (near ?? []).Where(t => t != 0 && t != stepCreature).Distinct().Select(Creature).Where(l => l.Length > 0).Take(3)) {
            facts.Add($"Game data (near him): {line}.");
        }

        return facts;
    }

}
