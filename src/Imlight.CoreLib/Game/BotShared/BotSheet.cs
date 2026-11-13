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
using System.Text.Json.Nodes;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// A server-played wizard's sheet for Ahziael (Niduus, 2026-11-13: "pages ... to view their gear, stats and their current
/// deck and quest"), shared by the Quest Team and Ultimus. It adds these keys to a status member: "school", "mana",
/// "stats" (lines like "Damage: 12%"), "deckCards" (lines like "Frost Beetle x4"), "deckSize". Ahziael's WizardSheet shows
/// them, with "gear", "questNow", "stepNow", "questList" and "spells" when the status has them.
/// </summary>
internal static class BotSheet {

    private static readonly string[] s_schools = ["Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance", "Shadow"];

    internal static void Fill(JsonObject member, Wizard wizard, string school) {
        var stats = wizard?.GameStats;
        member["school"] = school ?? "";
        member["mana"] = stats is null ? "" : $"{stats.m_currentMana}/{stats.m_baseMana + stats.m_bonusMana}";
        member["stats"] = new JsonArray([.. Lines(wizard, school).Select(l => (JsonNode) l)]);
        // The same with the game's icons for Ahziael (Niduus, 2026-11-13: "Use the in-game icons to present those stats"),
        // and each slot's item for the gear list with the slot's icon ("it would have the hat icon then say the hat's name").
        member["statRows"] = StatRows(wizard, school);
        member["gearRows"] = GearRows(wizard);
        // The deck as the game's deck window sorts it (Niduus, 2026-11-13: "Include Item and Treasure cards as in the deck
        // per those classifications"): its spells, the item cards its gear gives, and its treasure cards, as a battle deals them.
        var (spells, items, treasure) = DeckParts(wizard);
        member["deckCards"] = Cards(spells);
        member["deckSize"] = spells.Sum(c => c.Count);
        member["itemCards"] = Cards(items);
        member["itemCount"] = items.Sum(c => c.Count);
        member["treasureCards"] = Cards(treasure);
        member["treasureCount"] = treasure.Sum(c => c.Count);

        // The same as pictures for Ahziael, laid out like the game's deck window (Niduus, 2026-11-13: "I want the deck UI to
        // be similar to how it is in game"): each card with its count, and every item card with whether it is turned off.
        member["deckGrid"] = Grid(spells);
        member["deckMax"] = wizard?.SpellbookBehavior?.MaxSpells ?? 0;
        member["treasureGrid"] = Grid(treasure);

        // The XP bar and the quest book (BotQuestBook), live with every status.
        var (xpNow, xpMax) = BotQuestBook.Xp(wizard);
        member["xp"] = xpNow;
        member["xpMax"] = xpMax;
        member["questBook"] = BotQuestBook.Book(wizard, (string) (member["questNow"] as JsonValue) ?? "");
        member["itemGrid"] = new JsonArray([.. ItemCopies(wizard).Select(c => (JsonNode) new JsonObject {
            ["id"] = c.Id, ["school"] = SchoolOf(c.Id), ["off"] = c.Off,
        })]);
    }

    private static JsonArray Grid(List<(uint Id, int Count)> cards)
        => new([.. cards.Select(c => (JsonNode) new JsonObject { ["id"] = c.Id, ["n"] = c.Count, ["school"] = SchoolOf(c.Id) })]);

    private static string SchoolOf(uint spellId)
        => Imlight.CoreLib.Shared.Resources.CoreObjectFactory.GetCoreTemplate(spellId) is Imcodec.ObjectProperty.TypeCache.SpellTemplate t
            ? (string) t.m_sMagicSchoolName ?? "" : "";

    /// <summary>Every item card the gear gives, one per copy in the order the gear gives them, and whether it is crossed out.</summary>
    private static List<(uint Id, bool Off)> ItemCopies(Wizard wizard) {
        var copies = new List<(uint Id, bool Off)>();
        var book = wizard?.SpellbookBehavior;
        if (book is null) {
            return copies;
        }

        var deckId = wizard.EquipmentBehavior?.SlotList?.FirstOrDefault(s => s.SlotType == EquipmentSlotType.Deck)?.ItemId;
        var crossed = new Dictionary<uint, int>();
        foreach (var temp in (book.TemporarySpells ?? []).Where(t => t is not null)) {
            var id = (uint) temp.m_templateID;
            crossed.TryGetValue(id, out var done);
            var off = deckId is { } worn && done < book.ExcludedItemSpellCount(worn, temp.m_templateID);
            if (off) {
                crossed[id] = done + 1;
            }

            copies.Add((id, off));
        }

        // As in Niduus's screenshot of the game's deck window: the crossed-out ones first, then the ones in play.
        return [.. copies.OrderBy(c => c.Off ? 0 : 1)];
    }

    private static JsonArray Cards(List<(uint Id, int Count)> cards)
        => new([.. BotCards.Describe(cards).Split(", ", StringSplitOptions.RemoveEmptyEntries).Select(c => (JsonNode) c)]);

    /// <summary>The deck's spells, item cards and treasure cards, sorted the way CombatDuelSubCircle builds a battle deck.</summary>
    private static (List<(uint Id, int Count)> Spells, List<(uint Id, int Count)> Items, List<(uint Id, int Count)> Treasure) DeckParts(Wizard wizard) {
        var (spells, items, treasure) = (new List<(uint Id, int Count)>(), new List<(uint Id, int Count)>(), new List<(uint Id, int Count)>());
        var book = wizard?.SpellbookBehavior;
        if (book is null) {
            return (spells, items, treasure);
        }

        foreach (var entry in (book.SpellList ?? []).Where(d => d is not null && d.m_quantity > 0)) {
            var template = Imlight.CoreLib.Shared.Resources.CoreObjectFactory.GetCoreTemplate(entry.m_templateID) as Imcodec.ObjectProperty.TypeCache.SpellTemplate;
            var isTreasure = template is not null && (template.m_Treasure || ((string) template.m_name ?? "").EndsWith(" TC", StringComparison.Ordinal));
            if (!isTreasure && book.LearnedSpellTemplateIds is { Count: > 0 } && !book.LearnedSpellTemplateIds.Contains(entry.m_templateID)
                && book.TreasureCardCount(entry.m_templateID) > 0) {
                isTreasure = true;
            }

            (isTreasure ? treasure : spells).Add(((uint) entry.m_templateID, (int) entry.m_quantity));
        }

        // Item cards: one for each the gear gives, less the ones crossed out for the deck worn.
        var deckId = wizard.EquipmentBehavior?.SlotList?.FirstOrDefault(s => s.SlotType == EquipmentSlotType.Deck)?.ItemId;
        var skipped = new Dictionary<uint, int>();
        foreach (var temp in (book.TemporarySpells ?? []).Where(t => t is not null)) {
            var id = (uint) temp.m_templateID;
            if (deckId is { } worn) {
                skipped.TryGetValue(id, out var done);
                if (done < book.ExcludedItemSpellCount(worn, temp.m_templateID)) {
                    skipped[id] = done + 1;
                    continue;
                }
            }

            var at = items.FindIndex(c => c.Id == id);
            if (at >= 0) {
                items[at] = (id, items[at].Count + 1);
            }
            else {
                items.Add((id, 1));
            }
        }

        return (spells, items, treasure);
    }

    // The slots in the order of the game's backpack tabs.
    private static readonly (EquipmentSlotType Slot, string Name)[] s_slots = [
        (EquipmentSlotType.Hat, "Hat"), (EquipmentSlotType.Robe, "Robe"), (EquipmentSlotType.Shoes, "Boots"), (EquipmentSlotType.Weapon, "Wand"),
        (EquipmentSlotType.Athame, "Athame"), (EquipmentSlotType.Amulet, "Amulet"), (EquipmentSlotType.Ring, "Ring"), (EquipmentSlotType.Pet, "Pet"),
        (EquipmentSlotType.Mount, "Mount"), (EquipmentSlotType.Deck, "Deck"),
    ];

    private static JsonArray GearRows(Wizard wizard) {
        var rows = new JsonArray();
        foreach (var (slot, name) in s_slots) {
            var item = wizard?.EquipmentBehavior?.GetItemInSlot(slot);
            var id = item is null ? 0u : (uint) Math.Min((ulong) item.m_templateID, uint.MaxValue);
            var piece = id == 0 ? null : BotGear.Read(id);
            var shown = piece?.Name is { Length: > 0 } n ? n : id == 0 ? "" : Imlight.CoreLib.Game.Pet.PetWillCasts.ItemName(id);
            rows.Add(new JsonObject {
                ["slot"] = name, ["id"] = id, ["name"] = shown ?? "", ["level"] = piece?.Level ?? 0,
            });
        }

        return rows;
    }

    /// <summary>
    /// The wizard's stats as rows of the game's stat tooltip: the icon keys (Ahziael's GearIcon names, such as "health" or
    /// "critical"), a school's icon in front for school stats, the number, the words after the icons, and what it means.
    /// </summary>
    private static JsonArray StatRows(Wizard wizard, string school) {
        var rows = new JsonArray();
        var stats = wizard?.GameStats;
        if (stats is null) {
            return rows;
        }

        static float At(List<float> list, string name)
            => list is not null && MagicSchools.GetMagicSchool(name) is { } s && s.m_schoolIndex < list.Count ? list[(int) s.m_schoolIndex] : 0f;
        static string Pct(float fraction) => $"{Math.Round(fraction * 100f)}%";
        // Only stats of 1 or more show (Niduus, 2026-11-13: "display stats that are only 1 or greater"); health and mana always.
        static bool Shown(string value)
            => value.Contains('/') || (double.TryParse(value.TrimEnd('%'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var number) && number >= 1);
        void Row(string value, string after, string words, string rowSchool, params string[] icons) {
            if (!Shown(value)) {
                return;
            }

            rows.Add(new JsonObject {
                ["value"] = value, ["after"] = after, ["words"] = words, ["school"] = rowSchool,
                ["icons"] = new JsonArray([.. icons.Select(i => (JsonNode) i)]),
            });
        }

        var own = string.IsNullOrEmpty(school) ? "Fire" : school;
        Row($"{stats.m_currentHitpoints}/{stats.m_baseHitpoints + stats.m_bonusHitpoints}", "", "Health: now and most", "", "health");
        Row($"{stats.m_currentMana}/{stats.m_baseMana + stats.m_bonusMana}", "", "Mana: now and most", "", "mana");
        if (stats.m_energyMax > 0) {
            Row($"{wizard.PetOwnerBehavior?.Energy ?? 0}/{stats.m_energyMax}", "", "Energy: now and most", "", "energy");
        }
        Row(Pct(stats.m_powerPipBase + stats.m_powerPipBonusPercentAll), "CHANCE", "Power pip chance", "", "powerpip");
        Row(Pct(At(stats.m_dmgBonusPercent, own) + stats.m_dmgBonusPercentAll), "", $"{own} damage", own, "damage");
        if (At(stats.m_dmgBonusFlat, own) + stats.m_dmgBonusFlatAll is var flat and > 0) {
            Row($"{flat:0}", "", $"{own} flat damage", own, "damageflat");
        }

        Row(Pct(At(stats.m_accBonusPercent, own) + stats.m_accBonusPercentAll), "", $"{own} accuracy", own, "accuracy");
        Row(Pct(At(stats.m_apBonusPercent, own) + stats.m_apBonusPercentAll), "", $"{own} pierce", own, "pierce");
        Row($"{At(stats.m_criticalHitRatingBySchool, own) + stats.m_criticalHitRatingAll:0}", "RATING", $"{own} critical rating", own, "critical");
        Row($"{stats.m_blockRatingAll:0}", "RATING", "Block rating", "", "block");
        Row(Pct(stats.m_healBonusPercentAll), "", "Outgoing healing", "", "outgoing", "heal");
        Row(Pct(stats.m_healIncBonusPercentAll), "", "Incoming healing", "", "incoming", "heal");
        Row($"{At(stats.m_pipConversionRatingPerSchool, own) + stats.m_pipConversionRatingAll:0}", "RATING", $"{own} pip conversion rating", own, "pipconversion");
        Row($"{stats.m_shadowPipRating + stats.m_bonusShadowPipRating:0}", "RATING", "Shadow pip rating", "", "shadowpip");
        Row($"{stats.m_archmasteryBase:0}", "RATING", "Archmastery rating", "", "archmastery");
        Row(Pct(stats.m_stunResistancePercent), "", "Stun resistance", "", "stunresist");
        foreach (var s in s_schools.Where(s => s != "Shadow")) {
            Row(Pct(At(stats.m_dmgReducePercent, s) + stats.m_dmgReducePercentAll), "", $"{s} resist", s, "resist");
        }

        return rows;
    }

    private static List<string> Lines(Wizard wizard, string school) {
        var stats = wizard?.GameStats;
        if (stats is null) {
            return [];
        }

        static float At(List<float> list, string name)
            => list is not null && MagicSchools.GetMagicSchool(name) is { } s && s.m_schoolIndex < list.Count ? list[(int) s.m_schoolIndex] : 0f;
        static string Pct(float fraction) => $"{Math.Round(fraction * 100f)}%";

        var own = string.IsNullOrEmpty(school) ? "Fire" : school;
        var lines = new List<string> {
            $"Health: {stats.m_currentHitpoints}/{stats.m_baseHitpoints + stats.m_bonusHitpoints}",
            $"Mana: {stats.m_currentMana}/{stats.m_baseMana + stats.m_bonusMana}",
            $"Damage ({own}): {Pct(At(stats.m_dmgBonusPercent, own) + stats.m_dmgBonusPercentAll)}" +
            (At(stats.m_dmgBonusFlat, own) + stats.m_dmgBonusFlatAll is var flat and > 0 ? $", +{flat:0} flat" : ""),
            $"Accuracy ({own}): {Pct(At(stats.m_accBonusPercent, own) + stats.m_accBonusPercentAll)}",
            $"Pierce ({own}): {Pct(At(stats.m_apBonusPercent, own) + stats.m_apBonusPercentAll)}",
            $"Critical rating ({own}): {At(stats.m_criticalHitRatingBySchool, own) + stats.m_criticalHitRatingAll:0}",
            $"Block rating: {stats.m_blockRatingAll:0}",
            $"Power pip chance: {Pct(stats.m_powerPipBase + stats.m_powerPipBonusPercentAll)}",
            $"Outgoing healing: {Pct(stats.m_healBonusPercentAll)}, incoming healing: {Pct(stats.m_healIncBonusPercentAll)}",
            $"Pip conversion ({own}): {At(stats.m_pipConversionRatingPerSchool, own) + stats.m_pipConversionRatingAll:0}",
            $"Shadow pip rating: {stats.m_shadowPipRating + stats.m_bonusShadowPipRating:0}",
            $"Archmastery: {stats.m_archmasteryBase:0}",
            $"Stun resistance: {Pct(stats.m_stunResistancePercent)}",
        };
        var resists = s_schools.Where(s => s != "Shadow")
            .Select(s => $"{s} {Pct(At(stats.m_dmgReducePercent, s) + stats.m_dmgReducePercentAll)}");
        lines.Add("Resist: " + string.Join(", ", resists));

        return lines;
    }

}
