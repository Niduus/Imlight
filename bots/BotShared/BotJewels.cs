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
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Game.Ultimus;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// The jewels and pins a wizard can get in the real game (Niduus, 2026-11-19: "Give him jewels that would be appropriate
/// for his play style. Give knowledge of how to access and obtain jewels, then how to use them"): every jewel a shop
/// sells, a boss drops, a quest gives or a sold recipe crafts, never a Crowns-only, retired or dev one. Each with its
/// shape, the stats it gives (read as gear is, BotGear.StatsOf) and, for a pet jewel, the pet talent it gives. Read once,
/// after the gear and the game's books (UltimusLookup) are read.
/// </summary>
internal static class BotJewels {

    internal sealed record Jewel(uint Id, string Name, JewelSocketType Shape, WizItemTemplate Template, IReadOnlyList<BotGear.Stat> Stats,
                                 IReadOnlyList<string> Talents, bool Sold, bool Dropped, bool Crafted) {

        /// <summary>A pet jewel's talents by the game's own names ("Talent-Damage-Myth02").</summary>
        internal IReadOnlyList<string> TalentKeys { get; init; } = [];
    }

    private static volatile List<Jewel> s_all;

    /// <summary>Every jewel a wizard can get, or empty while the game's books are still being read.</summary>
    internal static IReadOnlyList<Jewel> All {
        get {
            if (s_all is { } ready) {
                return ready;
            }

            var made = UltimusLookup.MadeItems;
            if (made is null) {
                UltimusLookup.Warm();
                return [];
            }

            // The jewels in the game's files too (TrueFormTopGear): the server's drop and shop lists miss the newest ones,
            // such as the +155 health tear. Kept only once they are read; until then this list is built again on each ask.
            var files = TrueFormTopGear.JewelIds;
            var sold = UltimusLookup.SoldItems ?? new HashSet<uint>();
            var list = new List<Jewel>();
            var known = ItemSources.ObtainableIds().Concat(made).ToHashSet();
            foreach (var id in known.Concat(files ?? []).Distinct()) {
                try {
                    if (CoreObjectFactory.GetCoreTemplate(id) is not WizItemTemplate template || JewelSockets.GetShape(template) is not { } shape) {
                        continue;
                    }

                    var adjectives = (template.m_adjectiveList ?? []).Select(a => (string) a ?? "").ToList();
                    if (adjectives.Any(a => a is "FLAG_CrownsOnly" or "FLAG_Retired" or "FLAG_DevItem") || BotGear.Forbidden(id)) {
                        continue;
                    }

                    // A jewel only in the game's files must have a name the game shows (not a test jewel).
                    if (!known.Contains(id) && ((string) template.m_displayName is not { Length: > 0 } shownKey
                                                || string.IsNullOrWhiteSpace(Locale.GetEnglishName(shownKey)))) {
                        continue;
                    }

                    var keys = TalentKeysOf(template);
                    list.Add(new Jewel(id, NameOf(template), shape, template, BotGear.StatsOf(template), ShownTalents(keys),
                        sold.Contains(id), ItemSources.BossesDropping(id).Count > 0, made.Contains(id)) { TalentKeys = keys });
                }
                catch (Exception e) {
                    Logger.Debug("Bot jewels: jewel {0} could not be read: {1}", Logger.Args(id, e.Message));
                }
            }

            if (files is null) {
                return list;
            }

            Logger.Information("Bot jewels: {0} jewels and pins the server's wizards can get.", Logger.Args(list.Count));
            return s_all = list;
        }
    }

    /// <summary>A jewel's or any item's name as the game shows it.</summary>
    internal static string NameOf(WizItemTemplate template) {
        if (template is null) {
            return "?";
        }

        string key = template.m_displayName;
        var shown = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);
        return string.IsNullOrWhiteSpace(shown) ? (string) template.m_objectName ?? "?" : shown.Trim();
    }

    // A pet jewel's talents by the game's names; some talent jewels name the talent only in their own name.
    private static List<string> TalentKeysOf(WizItemTemplate jewel) {
        var names = (jewel.m_behaviors?.OfType<PetJewelBehaviorTemplate>() ?? [])
            .SelectMany(b => b.m_petTalentName ?? []).Select(n => (string) n).Where(n => !string.IsNullOrEmpty(n)).ToList();
        if (names.Count == 0) {
            string objectName = jewel.m_objectName;
            var start = objectName?.IndexOf("Talent-", StringComparison.OrdinalIgnoreCase) ?? -1;
            if (start >= 0) {
                names.Add(objectName[start..]);
            }
        }

        return names;
    }

    // The talents by their shown names ("Pain-Giver").
    private static List<string> ShownTalents(List<string> keys)
        => keys.Select(n => PetData.GetTalent(n) is { } talent && Locale.GetEnglishName(talent.m_displayName) is { Length: > 0 } shown ? shown : null)
            .Where(n => n is not null).ToList();

    // ---------------------------------------------------------------------
    // What a jewel shape gives, in plain words
    // ---------------------------------------------------------------------

    private static readonly Dictionary<string, string> s_kindWords = new(StringComparer.Ordinal) {
        ["Damage"] = "damage", ["FlatDamage"] = "flat damage", ["ReduceDamage"] = "resist", ["FlatReduceDamage"] = "flat resist",
        ["MaxHealth"] = "health", ["Accuracy"] = "accuracy", ["ArmorPiercing"] = "pierce", ["CriticalHit"] = "critical",
        ["Block"] = "critical block", ["Healing"] = "outgoing healing", ["IncHealing"] = "incoming healing",
        ["PowerPips"] = "power pip chance", ["MaxMana"] = "mana", ["StartingPips"] = "starting pips",
    };

    /// <summary>What jewels of a shape mostly give, from the game's jewels ("health", "damage, accuracy and pierce").</summary>
    internal static string Gives(JewelSocketType shape) {
        var jewels = All.Where(j => j.Shape == shape).ToList();
        if (shape == JewelSocketType.SOCKETTYPE_PET) {
            return jewels.Any(j => j.Talents.Count > 0) ? "a pet talent" : "";
        }

        var kinds = jewels.SelectMany(j => j.Stats.Select(s => s.Kind).Distinct()).Where(s_kindWords.ContainsKey)
            .GroupBy(k => k).OrderByDescending(g => g.Count()).Take(3).Select(g => s_kindWords[g.Key]).ToList();
        if (jewels.Any(j => j.Template.m_equipEffects?.OfType<ProvideSpellEffectInfo>().Any() == true) && kinds.Count < 3) {
            kinds.Add("a spell card");
        }

        return kinds.Count switch {
            0 => "",
            1 => kinds[0],
            _ => string.Join(", ", kinds.Take(kinds.Count - 1)) + " or " + kinds[^1],
        };
    }

    internal static readonly JewelSocketType[] Shapes = [
        JewelSocketType.SOCKETTYPE_TEAR, JewelSocketType.SOCKETTYPE_CIRCLE, JewelSocketType.SOCKETTYPE_SQUARE,
        JewelSocketType.SOCKETTYPE_TRIANGLE, JewelSocketType.SOCKETTYPE_PINSQUAREPIP, JewelSocketType.SOCKETTYPE_PINSQUARESHIELD,
        JewelSocketType.SOCKETTYPE_PINSQUARESWORD, JewelSocketType.SOCKETTYPE_PET,
    ];

}
