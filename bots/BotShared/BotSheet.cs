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
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;
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
        // Every treasure card it owns, in its deck or not, with how many of each (Niduus, 2026-11-19: "not able to see Treasure
        // Card stock of the bots along with the number of that treasure card"): the deck's treasure cards above are only the
        // ones it put in its deck.
        var stock = TreasureStock(wizard);
        member["treasureStock"] = Cards(stock);
        member["treasureStockCount"] = stock.Sum(c => c.Count);
        member["treasureStockGrid"] = Grid(stock);

        // The XP bar and the quest book (BotQuestBook), live with every status.
        var (xpNow, xpMax) = BotQuestBook.Xp(wizard);
        member["xp"] = xpNow;
        member["xpMax"] = xpMax;
        member["questBook"] = BotQuestBook.Book(wizard, (string) (member["questNow"] as JsonValue) ?? "");
        // The trained spells as the game's spellbook shows them (Niduus, 2026-11-13: "Include a Trained Spell Section next to
        // this"): the cards it can put in a deck, by school then rank, each with how many are in the deck now; then the rest
        // (fishing, gardening, utility spells) by name.
        var inDeck = spells.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.Sum(c => c.Count));
        var learned = (wizard?.SpellbookBehavior?.LearnedSpellTemplateIds ?? []).Select(i => (uint) i).Distinct().ToList();
        var cardsOf = learned.Select(id => (Id: id, Template: Imlight.CoreLib.Shared.Resources.CoreObjectFactory.GetCoreTemplate(id)
                as Imcodec.ObjectProperty.TypeCache.SpellTemplate))
            .Where(c => c.Template is not null).ToList();
        bool Card((uint Id, Imcodec.ObjectProperty.TypeCache.SpellTemplate Template) c)
            => SpellFactory.CanGoInDeck(c.Id) && !c.Template.m_Treasure && !BotCards.IsDevSpell(c.Id);
        member["spellGrid"] = new JsonArray([.. cardsOf.Where(Card)
            .OrderBy(c => SchoolRank((string) c.Template.m_sMagicSchoolName))
            .ThenBy(c => c.Template.m_spellRank?.m_spellRank ?? 0)
            .ThenBy(c => SpellName(c.Id))
            .Select(c => (JsonNode) new JsonObject {
                ["id"] = c.Id, ["school"] = (string) c.Template.m_sMagicSchoolName ?? "", ["n"] = inDeck.GetValueOrDefault(c.Id),
                ["name"] = SpellName(c.Id),
            })]);
        member["spellsOther"] = new JsonArray([.. cardsOf.Where(c => !Card(c)).Select(c => SpellName(c.Id))
            .Where(n => n.Length > 0).Distinct().Order().Select(n => (JsonNode) n)]);
        member["otherGrid"] = new JsonArray([.. cardsOf.Where(c => !Card(c)).Select(c => (JsonNode) new JsonObject {
            ["id"] = c.Id, ["school"] = (string) c.Template.m_sMagicSchoolName ?? "", ["name"] = SpellName(c.Id),
        })]);
        if (member["spells"] is null) {
            member["spells"] = new JsonArray([.. learned.Select(SpellName).Where(n => n.Length > 0).Distinct().Order().Select(n => (JsonNode) n)]);
        }

        member["itemGrid"] = new JsonArray([.. ItemCopies(wizard).Select(c => (JsonNode) new JsonObject {
            ["id"] = c.Id, ["school"] = SchoolOf(c.Id), ["off"] = c.Off,
        })]);
    }

    /// <summary>Every treasure card the wizard owns: the treasure book's and the deck's together, most first.</summary>
    private static List<(uint Id, int Count)> TreasureStock(Wizard wizard) {
        var owned = new Dictionary<uint, int>();
        var book = wizard?.SpellbookBehavior;
        if (book is null) {
            return [];
        }

        foreach (var id in book.TreasureCardTemplateIds ?? []) {
            owned[id] = owned.GetValueOrDefault(id) + 1;
        }

        foreach (var (id, count) in DeckParts(wizard).Treasure) {
            owned[id] = owned.GetValueOrDefault(id) + count;
        }

        return [.. owned.Where(o => o.Value > 0).OrderByDescending(o => o.Value).ThenBy(o => SpellName(o.Key)).Select(o => (o.Key, o.Value))];
    }

    private static int SchoolRank(string school) {
        var at = Array.FindIndex(s_schools, x => string.Equals(x, school, StringComparison.OrdinalIgnoreCase));

        return at < 0 ? s_schools.Length : at;
    }

    private static string SpellName(uint id) {
        try {
            if (Imlight.CoreLib.Shared.Resources.CoreObjectFactory.GetCoreTemplate(id) is Imcodec.ObjectProperty.TypeCache.SpellTemplate spell) {
                string key = spell.m_displayName;
                var shown = string.IsNullOrEmpty(key) ? "" : Imlight.CoreLib.Shared.Resources.Locale.GetEnglishName(key);

                return string.IsNullOrWhiteSpace(shown) ? (string) spell.m_name ?? "" : shown;
            }
        }
        catch (Exception) {
            // A name it can't read is left out.
        }

        return "";
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
            var row = new JsonObject {
                ["slot"] = name, ["id"] = id, ["name"] = shown ?? "", ["level"] = piece?.Level ?? 0,
            };
            // Each socket's jewel under its piece, and the pet's talents under the pet (Niduus, 2026-11-20: "we should put sub
            // areas for the Pin/Jewel icons and pet for Talents.... Make it more in detail and nicer").
            try {
                if (item is not null && JewelRows(item) is { Count: > 0 } jewels) {
                    row["jewels"] = jewels;
                }

                if (item is not null && slot == EquipmentSlotType.Pet && TalentRows(item) is { Count: > 0 } talents) {
                    row["talents"] = talents;
                }
            }
            catch (Exception) {
                // A piece whose sockets or talents can't be read just shows without them.
            }

            rows.Add(row);
        }

        return rows;
    }

    // Every socket of a worn piece, in order: its shape as Ahziael draws it, the jewel in it (or none) and what that gives.
    private static JsonArray JewelRows(WizClientObjectItem item) {
        var rows = new JsonArray();
        var template = ItemHelper.GetItemTemplate(item);
        var sockets = JewelSockets.GetSockets(template);
        var pairs = JewelSockets.GetPairs(item);
        for (var socket = 0; socket < sockets.Count; socket++) {
            if (sockets[socket]?.m_socketType is not { } type) {
                continue;
            }

            var jewelId = pairs.FirstOrDefault(p => p?.m_socketNumber == socket)?.m_jewelTemplateID ?? 0;
            var jewelTemplate = jewelId == 0 ? null : CoreObjectFactory.GetCoreTemplate((uint) jewelId) as WizItemTemplate;
            var jewel = jewelId == 0 ? null : BotJewels.All.FirstOrDefault(j => j.Id == (uint) jewelId);
            var stats = new JsonArray();
            if (jewel is not null) {
                foreach (var stat in jewel.Stats) {
                    stats.Add(StatChip(stat));
                }

                if (jewel.TalentKeys.Count == 0) {
                    foreach (var shownTalent in jewel.Talents) {
                        stats.Add(new JsonObject { ["icon"] = "", ["text"] = shownTalent });
                    }
                }

                foreach (var key in jewel.TalentKeys) {
                    if (BotPetTalentStats.Of(key) is { } talent) {
                        stats.Add(new JsonObject { ["icon"] = "", ["text"] = talent.Shown });
                        foreach (var stat in talent.Stats) {
                            stats.Add(StatChip(stat));
                        }

                        if (talent.MayCast is { Length: > 0 } cast) {
                            stats.Add(new JsonObject { ["icon"] = "", ["text"] = $"may cast {cast}" });
                        }
                    }
                }
            }

            rows.Add(new JsonObject {
                ["shape"] = ShapeKey(type),
                ["socket"] = JewelSockets.ShapeName(type),
                ["name"] = jewelTemplate is null ? "" : BotJewels.NameOf(jewelTemplate),
                ["school"] = jewel?.Stats.Select(st => st.School).FirstOrDefault(sc => sc.Length > 0) ?? "",
                ["stats"] = stats,
            });
        }

        return rows;
    }

    // The pet's own talents (the ones it shows, not its derby ones), each with what it gives or may cast.
    private static JsonArray TalentRows(WizClientObjectItem pet) {
        var rows = new JsonArray();
        var behavior = Imlight.CoreLib.Game.Pet.PetProgress.GetBehavior(pet);
        foreach (var id in behavior?.m_expressedTalents ?? []) {
            var key = (string) Imlight.CoreLib.Game.Pet.PetData.GetTalent(id)?.m_talentName ?? "";
            var info = key.Length == 0 ? null : BotPetTalentStats.Of(key);
            if (info is null) {
                continue;
            }

            rows.Add(new JsonObject {
                ["name"] = info.Shown,
                ["cast"] = info.MayCast ?? "",
                ["school"] = info.Stats.Select(st => st.School).FirstOrDefault(sc => sc.Length > 0) ?? "",
                ["stats"] = new JsonArray([.. info.Stats.Select(StatChip)]),
            });
        }

        return rows;
    }

    private static string ShapeKey(JewelSocketType type) => type switch {
        JewelSocketType.SOCKETTYPE_SQUARE => "Square",
        JewelSocketType.SOCKETTYPE_CIRCLE => "Circle",
        JewelSocketType.SOCKETTYPE_TRIANGLE => "Triangle",
        JewelSocketType.SOCKETTYPE_TEAR => "Tear",
        JewelSocketType.SOCKETTYPE_PET => "Star",
        JewelSocketType.SOCKETTYPE_PINSQUAREPIP => "Power",
        JewelSocketType.SOCKETTYPE_PINSQUARESHIELD => "Shield",
        JewelSocketType.SOCKETTYPE_PINSQUARESWORD => "Sword",
        _ => type.ToString(),
    };

    // One stat as Ahziael shows it: the game's icon key and plain words ("+16% Myth Accuracy").
    private static JsonObject StatChip(BotGear.Stat stat) {
        var (icon, words, percent) = stat.Kind switch {
            "Damage" => ("damage", "Damage", true),
            "FlatDamage" => ("damageflat", "Flat Damage", false),
            "Accuracy" => ("accuracy", "Accuracy", true),
            "ArmorPiercing" => ("pierce", "Pierce", true),
            "ReduceDamage" => ("resist", "Resist", true),
            "FlatReduceDamage" => ("resistflat", "Flat Resist", false),
            "Healing" => ("outgoing", "Outgoing Healing", true),
            "IncHealing" => ("incoming", "Incoming Healing", true),
            "MaxHealth" => ("health", "Health", false),
            "MaxMana" => ("mana", "Mana", false),
            "PowerPips" => ("powerpip", "Power Pip Chance", true),
            "CriticalHit" => ("critical", "Critical Rating", false),
            "Block" => ("block", "Block Rating", false),
            "StartingPips" => ("pip", "Starting Pip", false),
            var k when k.Contains("PipConversion", StringComparison.Ordinal) => ("pipconversion", "Pip Conversion", false),
            var k when k.Contains("ShadowPip", StringComparison.Ordinal) => ("shadowpip", "Shadow Pip Rating", false),
            var k when k.Contains("Archmastery", StringComparison.Ordinal) => ("archmastery", "Archmastery", false),
            var k when k.Contains("Stun", StringComparison.Ordinal) => ("stunresist", "Stun Resist", true),
            var k => ("", k, false),
        };
        var school = stat.School.Length == 0 ? "" : stat.School + " ";
        if (stat.School.Length == 0 && icon is "damage" or "accuracy" or "pierce" or "resist" or "critical" or "damageflat" or "resistflat") {
            school = "Universal ";
        }

        return new JsonObject {
            ["icon"] = icon,
            ["text"] = $"{(stat.Value >= 0 ? "+" : "")}{stat.Value:0.#}{(percent ? "%" : "")} {school}{words}",
            ["school"] = stat.School,
        };
    }

    // The worn pieces that give to one stat, as "Hat name: +5%", and what the rest comes from. Only the wizard's own stats:
    // a school stat counts from pieces for that school or for every school.
    // exact: the row holds only stats for exactly that school ("" = only every-school stats), as the split resist lines do.
    private static JsonArray Sources(List<BotGear.Piece> worn, string kind, string school, string shownValue, bool exact = false) {
        var lines = new JsonArray();
        double fromGear = 0;
        var percent = shownValue.EndsWith('%');
        foreach (var piece in worn) {
            var sum = piece.Stats.Where(st => (st.Kind == kind || kind == "PipConversion" && st.Kind.Contains("PipConversion", StringComparison.Ordinal)
                                                || kind == "ShadowPip" && st.Kind.Contains("ShadowPip", StringComparison.Ordinal)
                                                || kind == "Archmastery" && st.Kind.Contains("Archmastery", StringComparison.Ordinal)
                                                || kind == "StunResist" && st.Kind.Contains("Stun", StringComparison.Ordinal))
                                               && (exact ? st.School.Equals(school, StringComparison.OrdinalIgnoreCase)
                                                   : st.School.Length == 0 || school.Length == 0 || st.School.Equals(school, StringComparison.OrdinalIgnoreCase)))
                .Sum(st => BotGear.Percent(st));
            if (Math.Abs(sum) < 0.5) {
                continue;
            }

            fromGear += sum;
            lines.Add((JsonNode) $"{piece.Name}: {(sum > 0 ? "+" : "")}{Math.Round(sum)}{(percent ? "%" : "")}");
        }

        // The rest: the wizard's level and school, pet talents, jewels, set bonuses and other effects.
        var total = double.TryParse(shownValue.Split('/').Last().TrimEnd('%'), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : 0;
        var rest = Math.Round(total - fromGear);
        if (rest >= 1) {
            lines.Add((JsonNode) $"Level, pet, jewels and other: +{rest}{(percent ? "%" : "")}");
        }

        return lines;
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
        // Each worn piece's stats, read once, for "where does this stat come from" (Niduus, 2026-11-14: "when hovering over a
        // stat, it will show what items are giving to that stat").
        var worn = s_slots.Select(x => wizard.EquipmentBehavior?.GetItemInSlot(x.Slot))
            .Where(i => i is not null)
            .Select(i => BotGear.Read((uint) Math.Min((ulong) i.m_templateID, uint.MaxValue)))
            .Where(p => p is not null).ToList();
        string kindNow = null;
        var exactNow = false;
        void Row(string value, string after, string words, string rowSchool, params string[] icons) {
            var kind = kindNow;
            var exact = exactNow;
            kindNow = null;
            exactNow = false;
            if (!Shown(value)) {
                return;
            }

            var row = new JsonObject {
                ["value"] = value, ["after"] = after, ["words"] = words, ["school"] = rowSchool,
                ["icons"] = new JsonArray([.. icons.Select(i => (JsonNode) i)]),
            };
            if (kind is not null) {
                row["from"] = Sources(worn, kind, rowSchool, value, exact);
            }

            rows.Add(row);
        }

        // The next Row is this kind of stat (BotGear's names: "Damage", "ReduceDamage", "MaxHealth"...).
        void Kind(string kind) => kindNow = kind;

        var own = string.IsNullOrEmpty(school) ? "Fire" : school;
        Kind("MaxHealth");
        Row($"{stats.m_currentHitpoints}/{stats.m_baseHitpoints + stats.m_bonusHitpoints}", "", "Health: now and most", "", "health");
        Kind("MaxMana");
        Row($"{stats.m_currentMana}/{stats.m_baseMana + stats.m_bonusMana}", "", "Mana: now and most", "", "mana");
        if (stats.m_energyMax > 0) {
            Row($"{wizard.PetOwnerBehavior?.Energy ?? 0}/{stats.m_energyMax}", "", "Energy: now and most", "", "energy");
        }
        Kind("PowerPips");
        Row(Pct(stats.m_powerPipBase + stats.m_powerPipBonusPercentAll), "CHANCE", "Power pip chance", "", "powerpip");
        Kind("Damage");
        Row(Pct(At(stats.m_dmgBonusPercent, own) + stats.m_dmgBonusPercentAll), "", $"{own} damage", own, "damage");
        if (At(stats.m_dmgBonusFlat, own) + stats.m_dmgBonusFlatAll is var flat and > 0) {
            Kind("FlatDamage");
            Row($"{flat:0}", "", $"{own} flat damage", own, "damageflat");
        }

        Kind("Accuracy");
        Row(Pct(At(stats.m_accBonusPercent, own) + stats.m_accBonusPercentAll), "", $"{own} accuracy", own, "accuracy");
        Kind("ArmorPiercing");
        Row(Pct(At(stats.m_apBonusPercent, own) + stats.m_apBonusPercentAll), "", $"{own} pierce", own, "pierce");
        Kind("CriticalHit");
        Row($"{At(stats.m_criticalHitRatingBySchool, own) + stats.m_criticalHitRatingAll:0}", "RATING", $"{own} critical rating", own, "critical");
        Kind("Block");
        Row($"{stats.m_blockRatingAll:0}", "RATING", "Block rating", "", "block");
        Kind("Healing");
        Row(Pct(stats.m_healBonusPercentAll), "", "Outgoing healing", "", "outgoing", "heal");
        Kind("IncHealing");
        Row(Pct(stats.m_healIncBonusPercentAll), "", "Incoming healing", "", "incoming", "heal");
        Kind("PipConversion");
        Row($"{At(stats.m_pipConversionRatingPerSchool, own) + stats.m_pipConversionRatingAll:0}", "RATING", $"{own} pip conversion rating", own, "pipconversion");
        Kind("ShadowPip");
        Row($"{stats.m_shadowPipRating + stats.m_bonusShadowPipRating:0}", "RATING", "Shadow pip rating", "", "shadowpip");
        Kind("Archmastery");
        Row($"{stats.m_archmasteryBase:0}", "RATING", "Archmastery rating", "", "archmastery");
        Kind("StunResist");
        Row(Pct(stats.m_stunResistancePercent), "", "Stun resistance", "", "stunresist");
        // Resist: one "Universal resist" line, then a school line only for resist that school has on top of it (Niduus,
        // 2026-11-19: "if I have 10 universal resist, it says that. If I have 4 fire, it shows separately").
        Kind("ReduceDamage");
        exactNow = true;
        Row(Pct(stats.m_dmgReducePercentAll), "", "Universal resist", "", "resist");
        foreach (var s in s_schools.Where(s => s != "Shadow")) {
            Kind("ReduceDamage");
            exactNow = true;
            Row(Pct(At(stats.m_dmgReducePercent, s)), "", $"{s} resist", s, "resist");
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
        // One universal number, then only the schools with extra resist on top of it.
        var extra = s_schools.Where(s => s != "Shadow" && Math.Round(At(stats.m_dmgReducePercent, s) * 100f) >= 1)
            .Select(s => $"{s} +{Pct(At(stats.m_dmgReducePercent, s))}").ToList();
        lines.Add($"Universal resist: {Pct(stats.m_dmgReducePercentAll)}" + (extra.Count > 0 ? $" (on top: {string.Join(", ", extra)})" : ""));

        return lines;
    }

}
