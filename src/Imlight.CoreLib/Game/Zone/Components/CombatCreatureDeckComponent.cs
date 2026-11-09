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
 * COMBAT CREATURE DECK
 * ========================================================================
 * 
 * PURPOSE:
 * Manages spelldecks for combat-enabled creature entities, initializing 
 * their spellbooks from both client spell names and SpiralDB deck data.
 * 
 * USAGE EXAMPLE:
 * var deckComponent = new CombatCreatureDeckComponent(zoneEntity);
 * var availableSpells = deckComponent.Spells;
 * 
 * NOTE:
 * Requires prior initialization of CoreObjectFactory and SpellFactory.
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 08/15/2026
 */

using System.Collections.Generic;
using System.Linq;
using Imcodec.IO;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class CombatCreatureDeckComponent : ZoneEntityComponent, IComponentFactory {

    public List<SpellData> Spells { get; } = [];

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate gameObjectTemplate
        && gameObjectTemplate.m_behaviors.Any(x => x is NPCBehaviorTemplate)
        && gameObjectTemplate.m_behaviors.Any(x => x is DuelistBehaviorTemplate);

    // ctor
    public CombatCreatureDeckComponent(ZoneEntity entity) : base(entity) { 
        foreach (var spellId in GameSpellIds(entity.Template)) {
            AddSpell(spellId);
        }

        // A creature with an empty deck (no usable client names and no SpiralDB deck) falls
        // back to the default spellbook so it can cast instead of passing every round.
        if (Spells.Count == 0) {
            Logger.Warning(
                "{0} {1} has no usable spells from its client deck or SpiralDB deck, falling back to the default spellbook.",
                Logger.Args(nameof(ZoneEntity), entity.ActiveGameObject.m_debugName)
            );
            foreach (var spellId in CreatureSpellbookCollection.GetDefaultCreatureSpellbook().SpellTemplateIds) {
                AddSpell(spellId);
            }
        }
    }

    /// <summary>
    /// The spells of a creature's game deck, once each, before randomizer mode. The app's Bosses tab shows these
    /// (IdCatalog writes them to creatures.csv).
    /// </summary>
    internal static List<uint> GameSpellIds(CoreTemplate template) {
        var spellIds = new List<uint>();
        if (template?.m_behaviors is null) {
            return spellIds;
        }

        // Collect every behavior from the creature's equipment items.
        var equipmentItemBehaviors = template.m_behaviors
            .OfType<EquipmentBehaviorTemplate>()
            .SelectMany(x => x.m_itemList)
            .Select(x => CoreObjectFactory.GetCoreTemplate(x))
            .Where(x => x != null)
            .SelectMany(x => x.m_behaviors ?? []);

        // Combine equipment item behaviors with the entity's own behaviors.
        // MobDeckBehaviorTemplate can appear in either location.
        var allBehaviors = template.m_behaviors
            .Concat(equipmentItemBehaviors)
            .ToList();

        void Add(uint spellId) {
            // Both sources may list the same spell; creatures carry one entry per spell.
            // Unknown spell ids are skipped; SpellFactory logs the miss.
            if (!spellIds.Contains(spellId) && SpellFactory.GetSpell(spellId) is not null) {
                spellIds.Add(spellId);
            }
        }

        var realCards = false;

        // MobDeckBehaviorTemplate stores spell names directly, if it exists.
        var mobDeck = allBehaviors.OfType<MobDeckBehaviorTemplate>().FirstOrDefault();
        if (mobDeck != null) {
            foreach (var spellName in mobDeck.m_spellList) {
                if (spellName.ToString().Length == 0) {
                    continue;
                }

                // Unknown names are skipped; SpellFactory logs the miss.
                var spell = SpellFactory.GetSpell(spellName.ToString());
                if (spell is not null) {
                    // "NA <creature>" is its Natural Attack, the hit it makes with no card.
                    realCards |= !spellName.ToString().StartsWith("NA ", System.StringComparison.Ordinal);
                    Add(spell.m_templateID);
                }
            }
        }

        // DeckBehaviorTemplate stores a deck name that maps to a SpiralDB spellbook.
        // The deck is the union of both sources: the client's own spell names and the
        // SpiralDB spellbook named by the deck behavior. Either source may be missing.
        var deckBehavior = allBehaviors.OfType<DeckBehaviorTemplate>().FirstOrDefault();
        if (deckBehavior is not null && !string.IsNullOrEmpty(deckBehavior.m_defaultDeck)) {
            // An Advanced Mode deck (Mdeck-...-Adv-...) SpiralDB lacks plays its normal deck.
            var spellbook = CreatureSpellbookCollection.GetCreatureSpellbook(deckBehavior.m_defaultDeck)
                ?? (Imlight.CoreLib.Game.World.AdvancedMode.NormalDeckOf(deckBehavior.m_defaultDeck) is { } normalDeck
                    ? CreatureSpellbookCollection.GetCreatureSpellbook(normalDeck) : null);
            foreach (var spellId in spellbook?.SpellTemplateIds ?? []) {
                Add(spellId);
            }

            // From Azteca on most decks (and from Mirage on every deck) live only on the real game's servers: not in
            // the game files, SpiralDB or Wizard404. [Battle] StandInDecks (live, for creatures that appear after the
            // change): such a creature plays the school deck of its rank instead of the six starter spells.
            if (spellbook is null && !realCards && StandInDecksOn) {
                var npc = template.m_behaviors.OfType<NPCBehaviorTemplate>().FirstOrDefault();
                foreach (var spellId in StandInDeck(deckBehavior.m_defaultDeck, npc?.m_schoolOfFocus, npc?.m_nLevel ?? 0)) {
                    Add(spellId);
                }
            }
        }

        return spellIds;
    }

    /// <summary>[Battle] StandInDecks (live): creatures whose deck nobody has get the school deck of their rank.</summary>
    internal static bool StandInDecksOn => ConfigurationManager.GetValue("Battle.StandInDecks", true);

    private static readonly (string Code, string School)[] s_schoolCodes = [
        ("F", "Fire"), ("I", "Ice"), ("S", "Storm"), ("M", "Myth"), ("L", "Life"), ("D", "Death"), ("B", "Balance"),
    ];

    private static readonly System.Text.RegularExpressions.Regex s_deckName = new(
        @"^[MH]deck-(?<schools>[A-Za-z]+)-(?:R(?<rank>\d+))?",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex s_rankInName = new(
        @"-R(\d+)(?:-|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// A stand-in for a deck SpiralDB lacks: SpiralDB's plain school deck (Mdeck-F-R13 and so on) for each school the
    /// deck's name starts with ("Mdeck-LF-R13": Life and Fire), at the rank in its name or else the creature's level
    /// (from Azteca on a creature's level is its rank). The nearest rank SpiralDB has below it, or above when none is.
    /// </summary>
    internal static List<uint> StandInDeck(string deckName, string schoolOfFocus, int level) {
        var spells = new List<uint>();
        var match = s_deckName.Match(deckName ?? "");
        // Only a short code of school letters counts (O is in some, as in Mdeck-FO-R14-EM, and names no school here).
        // Mdeck-YMorganthe-01, Mdeck-Moon-R9 and Mdeck-Sh-... (Shadow) name none of the seven schools.
        var codes = match.Success ? match.Groups["schools"].Value.ToUpperInvariant() : "";
        // Shadow, Star and Sun decks (Mdeck-SH-, Mdeck-ST-, Mdeck-SU-) play the school deck nearest to them, as the
        // Moon ones (Mdeck-MO-) already play Myth: before, they kept the six starter spells (a tester, 2026-11-07: "some
        // bosses/mobs don't have a deck" in the Catacombs).
        if (s_astralCodes.TryGetValue(codes, out var stand)) {
            codes = stand;
        }

        if (codes.Length > 3 || codes.Any(c => !"FISMLDBO".Contains(c))) {
            codes = "";
        }

        // A plain deck with something after its rank (Mdeck-M-R13-AZ, Mdeck-F-R5-KH) is that plain deck.
        var schools = new List<string>();
        foreach (var c in codes) {
            var school = s_schoolCodes.FirstOrDefault(x => x.Code[0] == c).School;
            if (school is not null && !schools.Contains(school)) {
                schools.Add(school);
            }
        }

        var focus = s_astralSchools.TryGetValue(schoolOfFocus ?? "", out var near) ? near : schoolOfFocus;
        if (schools.Count == 0 && s_schoolCodes.FirstOrDefault(x => x.School.Equals(focus, System.StringComparison.OrdinalIgnoreCase)).School is { } own) {
            schools.Add(own);
        }

        var rank = match.Success && match.Groups["rank"].Success ? int.Parse(match.Groups["rank"].Value, System.Globalization.CultureInfo.InvariantCulture)
            : s_rankInName.Match(deckName ?? "") is { Success: true } inName ? int.Parse(inName.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : level;

        foreach (var school in schools) {
            var code = s_schoolCodes.First(x => x.School == school).Code;
            var book = NearestPlainDeck(code, rank);
            foreach (var spellId in book?.SpellTemplateIds ?? []) {
                if (!spells.Contains(spellId)) {
                    spells.Add(spellId);
                }
            }

            // SpiralDB's plain decks stop at rank 13 (Azteca), so a rank 18 creature of the level 130 Catacombs played
            // Azteca's spells (a tester, 2026-11-07: Renegade Druid "got the classic R18 storm deck"). From rank 14 on,
            // the school's later attacks come too, each from the rank whose wizards learn it.
            foreach (var (lateSchool, minRank, spellId, _) in s_lateSpells) {
                if (lateSchool == school && rank >= minRank && !spells.Contains(spellId)) {
                    spells.Add(spellId);
                }
            }
        }

        return spells;
    }

    // Deck codes of the schools beside the seven, and the school whose plain deck stands in for each.
    private static readonly Dictionary<string, string> s_astralCodes = new(System.StringComparer.OrdinalIgnoreCase) {
        ["SH"] = "D", ["ST"] = "B", ["SU"] = "F", ["MO"] = "M",
    };

    private static readonly Dictionary<string, string> s_astralSchools = new(System.StringComparer.OrdinalIgnoreCase) {
        ["Shadow"] = "Death", ["Star"] = "Balance", ["Sun"] = "Fire", ["Moon"] = "Myth",
    };

    // The later school attacks (from Root.wad Spells/Tiered Spells, the wizard level they are learned at in the notes),
    // and the creature rank that gets them: level 100 from rank 14, 110 from 16, 120 from 17, 130 from 18. Pick-your-
    // targets spells (Qismah's Curse) and the level 170 ones (some pick their targets too) are left out.
    private static readonly (string School, int MinRank, uint SpellId, string Name)[] s_lateSpells = [
        ("Fire", 14, 576217214, "Fire from Above (100)"), ("Fire", 16, 1946402814, "Raging Bull (110)"),
        ("Fire", 17, 592954350, "Scorching Scimitars (120)"), ("Fire", 18, 1125749132, "Scion of Fire (130)"),
        ("Ice", 14, 1635493379, "Winter's Web (100)"), ("Ice", 17, 2109842312, "Climaclysm (120)"),
        ("Ice", 18, 414216592, "Scion of Ice (130)"),
        ("Storm", 14, 1881888814, "Glowbug Squall (100)"), ("Storm", 16, 348289258, "Rusalka's Wrath (110)"),
        ("Storm", 17, 655769628, "Iron Sultan (120)"), ("Storm", 18, 1039085841, "Scion of Storm (130)"),
        ("Myth", 14, 2062086375, "Colossus Mantis (100)"), ("Myth", 16, 1305255487, "Witch's Housecall (110)"),
        ("Myth", 17, 943026498, "Snake Charmer (120)"), ("Myth", 18, 1873219188, "Scion of Myth (130)"),
        ("Life", 14, 1793130233, "Hungry Caterpillar (100)"), ("Life", 16, 1106087120, "Wings of Fate (110)"),
        ("Life", 17, 499675719, "Llamasu (120)"), ("Life", 18, 1226363276, "Scion of Life (130)"),
        ("Death", 14, 169474362, "Call of Khrulhu (100)"), ("Death", 16, 625950838, "Winged Sorrow (110)"),
        ("Death", 18, 1521078555, "Scion of Death (130)"),
        ("Balance", 14, 1039918614, "Gaze of Fate (100)"), ("Balance", 16, 1927982914, "Nested Fury (110)"),
        ("Balance", 17, 1373405296, "Sand Worm (120)"), ("Balance", 18, 205203928, "Scion of Balance (130)"),
    ];

    private static CreatureSpellbook NearestPlainDeck(string code, int rank) {
        rank = System.Math.Clamp(rank, 1, 30);
        for (var r = rank; r >= 1; r--) {
            if (CreatureSpellbookCollection.GetCreatureSpellbook($"Mdeck-{code}-R{r}") is { SpellTemplateIds.Length: > 0 } book) {
                return book;
            }
        }

        for (var r = rank + 1; r <= 30; r++) {
            if (CreatureSpellbookCollection.GetCreatureSpellbook($"Mdeck-{code}-R{r}") is { SpellTemplateIds.Length: > 0 } book) {
                return book;
            }
        }

        return null;
    }

    private void AddSpell(uint spellId) {
        // Both sources may list the same spell; creatures carry one entry per spell.
        if (Spells.Any(x => x.m_templateID == spellId)) {
            return;
        }

        // Unknown spell ids are skipped; SpellFactory logs the miss.
        if (SpellFactory.GetSpell(spellId) is null) {
            return;
        }

        // Randomizer mode may swap it for another spell of the same type and pip cost.
        var rolled = Randomization.Randomizer.RollCreatureSpell(spellId);
        if (rolled != spellId && SpellFactory.GetSpell(rolled) is not null && Spells.All(x => x.m_templateID != rolled)) {
            spellId = rolled;
        }

        // Creatures have infinite spells in their spellbook.
        Spells.Add(new SpellData {
            m_templateID = spellId,
            m_quantity = 9999,
        });
    }

}