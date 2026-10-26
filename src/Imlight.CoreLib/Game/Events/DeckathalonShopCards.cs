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
 *
 * ========================================================================
 * DECKATHALON SHOP CARDS
 * ========================================================================
 *
 * PURPOSE:
 * The sought-after treasure cards of the Deckathalon that is on (Niduus, 2026-10-25: "while a Deckathalon is going on,
 * he sells the more sought after TC's players would want in his shop for ONLY that class"):
 *   - Harold Argleston adds them to his own treasure cards while it is on;
 *   - a custom vendor whose shop window is "Deckathalon" (the "Deckathalon TC NPC" preset) sells only them.
 * [Deckathalon] ShopCards turns it on or off. ShopCardsAll (Niduus, 2026-10-26: "Instead of select Fire TC's, just do all
 * of them for the Fire Deckathalon") sells every treasure card of the school; turned off, <School>ShopCards lists each
 * school's cards by name. All are read each time a shop opens, so a change in the app's Settings tab counts at once.
 * Each card costs its own gold price.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/26/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Imlight.Common;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Events;

internal static class DeckathalonShopCards {

    /// <summary>The custom vendor shop window that sells only the Deckathalon's cards.</summary>
    internal const string WINDOW = "Deckathalon";

    /// <summary>Harold Argleston (WC-HUB-NPC03), Ravenwood's treasure card seller.</summary>
    internal const uint HAROLD_TEMPLATE_ID = 38214;

    private static readonly Dictionary<string, string> s_defaults = new(StringComparer.OrdinalIgnoreCase) {
        ["Fire"] = "Efreet TC, Fire Dragon TC, Helephant TC, Rain of Fire TC, Sun Serpent TC, Phoenix TC, Meteor Strike TC, " +
                   "Scald TC, Heck Hound TC, Immolate TC, Fireblade TC, Fire Trap TC, Fire Prism TC, Fuel TC, Link TC, Power Link TC, Smoke Screen TC",
        ["Ice"] = "Frost Giant TC, Snow Angel TC, Woolly Mammoth TC, Lord of Winter TC, Colossus TC, Blizzard TC, Ice Wyvern TC, " +
                  "Frostbite TC, Iceblade TC, Ice Trap TC, Ice Prism TC, Tower Shield TC, Taunt TC, Mega Taunt TC, Frozen Armor TC, Freeze TC",
        ["Storm"] = "Storm Lord TC, Leviathan TC, Sirens TC, Storm Owl TC, Triton TC, Tempest TC, Stormzilla TC, Kraken TC, " +
                    "Stormblade TC, Storm Trap TC, Storm Prism TC, Supercharge TC, Wild Bolt TC, Insane Bolt TC, Catalan TC",
        ["Myth"] = "Orthrus TC, Medusa TC, Basilisk TC, Celestial Calendar TC, Earthquake TC, Minotaur TC, Humongofrog TC, " +
                   "Mythblade TC, Myth Trap TC, Myth Prism TC, Shatter TC, Pierce TC, Stun TC, Blinding Light TC, Aftershock TC",
        ["Life"] = "Forest Lord TC, Rebirth TC, Gnomes TC, Spinysaur TC, Centaur TC, Earth Walker TC, Seraph TC, Unicorn TC, " +
                   "Lifeblade TC, Life Trap TC, Life Prism TC, Sanctuary TC, Regenerate TC, Guidance TC, Triage TC, Pixie TC",
        ["Death"] = "Scarecrow TC, Skeletal Dragon TC, Katzenstein's Monster TC, Avenging Fossil TC, Wraith TC, Vampire TC, " +
                    "Skeletal Pirate TC, Poison TC, Deathblade TC, Death Trap TC, Death Prism TC, Feint TC, Curse TC, Empower TC, Sacrifice TC",
        ["Balance"] = "Power Nova TC, Ra TC, Chimera TC, Sabertooth TC, Hydra TC, Judgement TC, Sandstorm TC, Spectral Blast TC, " +
                      "Balanceblade TC, Spirit Blade TC, Tri Blade TC, Hex TC, Weakness TC, Bladestorm TC, Reshuffle TC",
    };

    // Each card's gold price, from its spell template (at least 1).
    private static readonly Lazy<Dictionary<string, int>> s_prices = new(() => {
        var prices = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, spell) in SpellFactory.AllSpellTemplates()) {
            string name = spell.m_name;
            if (!string.IsNullOrEmpty(name)) {
                prices.TryAdd(name, Math.Max(1, spell.m_baseCost));
            }
        }

        return prices;
    });

    private static readonly ConcurrentDictionary<string, List<TreasureCardEntry>> s_lists = new();

    // A plain card name like "Fire Dragon TC" (no test or developer names).
    private static readonly Regex s_cardName = new(@"^[A-Za-z0-9' ]+ TC$", RegexOptions.Compiled);

    /// <summary>A treasure card's own gold price (its spell's), at least 1; 1 for a name the game does not have.</summary>
    internal static int PriceOf(string spellName)
        => spellName is not null && s_prices.Value.TryGetValue(spellName, out var price) ? price : 1;

    /// <summary>Whether the Deckathalon's cards are sold while one is on ([Deckathalon] ShopCards).</summary>
    internal static bool Enabled => ConfigurationManager.GetValue("Deckathalon.ShopCards", true);

    /// <summary>Whether every treasure card of the school is sold ([Deckathalon] ShopCardsAll), not the hand-picked list.</summary>
    internal static bool AllCards => ConfigurationManager.GetValue("Deckathalon.ShopCardsAll", true);

    /// <summary>The cards of the Deckathalon that is on, or none (no event, or ShopCards off).</summary>
    internal static List<TreasureCardEntry> Current()
        => Enabled && Deckathalon.CurrentEvent is { } running ? ForSchool(running.School) : [];

    /// <summary>A school's cards (all of them, or [Deckathalon] &lt;School&gt;ShopCards), each with its price.</summary>
    internal static List<TreasureCardEntry> ForSchool(string school) {
        if (string.IsNullOrWhiteSpace(school)) {
            return [];
        }

        if (AllCards) {
            return s_lists.GetOrAdd($"{school}|*", _ => AllOfSchool(school));
        }

        var text = ConfigurationManager.GetValue($"Deckathalon.{school}ShopCards", s_defaults.GetValueOrDefault(school, "")) ?? "";

        return s_lists.GetOrAdd($"{school}|{text}", _ => {
            var cards = new List<TreasureCardEntry>();
            var missing = new List<string>();
            foreach (var name in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct()) {
                if (s_prices.Value.TryGetValue(name, out var price)) {
                    cards.Add(new TreasureCardEntry { SpellName = name, Price = price });
                }
                else {
                    missing.Add(name);
                }
            }

            Logger.Information("Deckathalon | {0} shop cards: {1}{2}.", Logger.Args(school, cards.Count,
                missing.Count > 0 ? $", left out {string.Join(", ", missing)} (no spell of that name)" : ""));

            return cards;
        });
    }
    // Every treasure card of a school a player can buy: a plain " TC" name, with a gold price, not battlegrounds only.
    private static List<TreasureCardEntry> AllOfSchool(string school) {
        var cards = new List<TreasureCardEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, spell) in SpellFactory.AllSpellTemplates()) {
            string name = spell.m_name;
            string spellSchool = spell.m_sMagicSchoolName;
            if (spell.m_Treasure && !spell.m_battlegroundsOnly && spell.m_baseCost > 0
                && string.Equals(spellSchool, school, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(name) && s_cardName.IsMatch(name) && seen.Add(name)) {
                cards.Add(new TreasureCardEntry { SpellName = name, Price = spell.m_baseCost });
            }
        }

        cards.Sort((a, b) => string.CompareOrdinal(a.SpellName, b.SpellName));
        Logger.Information("Deckathalon | {0} shop cards: all {1} treasure cards of the school.", Logger.Args(school, cards.Count));

        return cards;
    }

}
