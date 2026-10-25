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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.Setup;

/// <summary>
/// One line of an item's stats as the game's tooltip shows it: "+1150 MAX" and the health icon, "+7%" and the accuracy
/// icon, "+135" with the critical icon and "RATING".
/// </summary>
/// <param name="Value">The number, with its sign and % when it is a percent, and MAX for health, mana and energy.</param>
/// <param name="Icons">The stat's pictures, in order (see <see cref="GearIcons"/>); a school's own icon goes first.</param>
/// <param name="School">The school it is for, or "" for every school.</param>
/// <param name="After">Words after the icons, such as RATING.</param>
/// <param name="Words">What it is, in plain words, for pointing at it.</param>
internal sealed record GearStat(string Value, string[] Icons, string School, string After, string Words) {

    /// <summary>Which stat it is, such as Damage, ReduceDamage or MaxHealth, for the stat filters.</summary>
    internal string Kind { get; init; } = "";

    /// <summary>The number without its sign or %, so stats can be compared and added up.</summary>
    internal int Amount { get; init; }

    internal bool Percent { get; init; }

}

/// <summary>One step of a set bonus: what wearing this many pieces of the set adds.</summary>
internal sealed record GearSetTier(int Pieces, string Description, IReadOnlyList<GearStat> Stats, IReadOnlyList<GearSpell> Spells);

/// <summary>
/// A set bonus (ItemSetBonusTemplate in ObjectData/ItemSetBonus), such as Krokotopian Ice Set: extra stats for wearing
/// several of its pieces. Its pieces name it by template ID.
/// </summary>
internal sealed record GearSetBonus(uint Id, string Name, string Code, IReadOnlyList<GearSetTier> Tiers) {

    /// <summary>Everything the set adds once all its steps are reached.</summary>
    internal IEnumerable<GearStat> AllStats => Tiers.SelectMany(t => t.Stats);

}

/// <summary>A jewel socket: its shape (Square, Circle, Triangle, Tear, Star, Power, Shield, Sword) and whether it starts locked.</summary>
internal sealed record GearSocket(string Shape, bool Locked);

/// <summary>A spell an item puts in the deck (an item card), by the spell's game name, and how many copies.</summary>
internal sealed record GearSpell(string Code, int Copies);

/// <summary>What a pet's template says: its stat maximums, talent pools and the spells it gives.</summary>
internal sealed record GearPet(IReadOnlyList<(string Stat, int Max)> MaxStats, int Talents, int DerbyTalents, int SpellsAtLevel,
    IReadOnlyList<string> Spells, string HatchTime);

/// <summary>A deck's limits.</summary>
internal sealed record GearDeck(int MaxSpells, int MaxCopies, int MaxSchoolCopies, int MaxTreasureCards, string School);

/// <summary>
/// One piece of gear (a hat, robe, boots, wand, athame, amulet, ring, deck, mount or pet) as the game's item tooltip
/// shows it, read from its template in the game's Root.wad.
/// </summary>
internal sealed class GearItem {

    internal required uint Id { get; init; }

    internal required string Name { get; init; }

    /// <summary>The game's own name for the template, such as Hat-PL-L102-Fire-02.</summary>
    internal required string Code { get; init; }

    internal required string File { get; init; }

    /// <summary>Hat, Robe, Boots, Wand, Athame, Amulet, Ring, Deck, Mount, Pet or Bundle.</summary>
    internal required string Slot { get; init; }

    /// <summary>The school a wizard must be to wear it, or "" when any school may.</summary>
    internal required string SchoolNeeded { get; init; }

    /// <summary>The item's own school (gear made for a school), which may differ from <see cref="SchoolNeeded"/>.</summary>
    internal required string School { get; init; }

    /// <summary>The level needed to wear it, or 0.</summary>
    internal required int Level { get; init; }

    internal required string Rarity { get; init; }

    /// <summary>The game's FLAG_ adjectives without FLAG_, such as NoAuction and CrownsOnly.</summary>
    internal required IReadOnlyList<string> Flags { get; init; }

    internal required IReadOnlyList<GearStat> Stats { get; init; }

    internal required IReadOnlyList<GearSpell> Spells { get; init; }

    internal required IReadOnlyList<GearSocket> Sockets { get; init; }

    /// <summary>A rental's time, such as 7d, or "" when it is kept for good.</summary>
    internal required string Rental { get; init; }

    internal required GearPet? Pet { get; init; }

    internal required GearDeck? Deck { get; init; }

    internal required int GoldCost { get; init; }

    internal required int CrownsCost { get; init; }

    /// <summary>A bundle's items by template ID (what it gives, free items included), or empty for anything else.</summary>
    internal IReadOnlyList<uint> BundleItems { get; init; } = [];

    /// <summary>The set bonus template it belongs to, or 0.</summary>
    internal uint SetId { get; init; }

    /// <summary>Its set bonus, when it is part of a set the game files have.</summary>
    internal GearSetBonus? Set { get; set; }

    internal bool CrownsOnly => Flags.Contains("CrownsOnly");

    internal bool Retired => Flags.Contains("Retired");

    internal string Haystack { get; set; } = "";

    /// <summary>The chat command that gives it, by ID, so the version is exact (the server's CommandGearProtocol).</summary>
    internal string Command => Slot == "Bundle" ? $".bundle {Name}" : (Slot switch {
        "Hat" => ".hat",
        "Robe" => ".robe",
        "Boots" => ".boot",
        "Wand" => ".wand",
        "Athame" => ".athame",
        "Amulet" => ".amulet",
        "Ring" => ".ring",
        "Pet" => ".pet",
        "Deck" => ".deck",
        _ => ".gear",
    }) + $" 1 {Id}";

}

/// <summary>
/// Every piece of gear in the game (Niduus, 2026-10-22: "Make a Gear category in the manager and have it portray gear as
/// it would in game"), read from the item templates in Root.wad with the SpellBook's text and pictures. Stats are worked
/// out the way the server does (CanonicalStatEffects): an equip effect names an effect template, the template names a
/// stat table in GameEffectRuleData, and the effect's lookup index picks the number in that table.
/// </summary>
internal sealed partial class GearBook {

    internal static readonly string[] SLOTS = ["Hat", "Robe", "Boots", "Wand", "Athame", "Amulet", "Ring", "Deck", "Mount", "Pet", "Bundle"];

    // Folders of ObjectData that never hold gear, left out so reading is quicker.
    private static readonly string[] s_skipped = [
        "ObjectData/Housing/", "ObjectData/Reagents/", "ObjectData/MagicMirrorShopOptions/", "ObjectData/Polymorphs/",
        "ObjectData/Jewels/", "ObjectData/PetSnacks/",
    ];

    private static readonly string[] s_schools = ["Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance", "Shadow", "Sun", "Moon", "Star", "All"];

    private readonly SpellBook _book;
    private readonly Dictionary<string, WizStatisticEffectTemplate> _effects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (List<float> Values, bool Fraction)> _tables = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, SpellCard>? _spellsByCode;

    private GearBook(SpellBook book) {
        _book = book;
    }

    internal IReadOnlyList<GearItem> Items { get; private set; } = [];

    /// <summary>Every set bonus, by name.</summary>
    internal IReadOnlyList<GearSetBonus> Sets { get; private set; } = [];

    /// <summary>Reads every piece of gear. Takes a few seconds, so run it in the background.</summary>
    internal static GearBook Load(SpellBook book) {
        var gear = new GearBook(book);
        gear.ReadStatTables();
        var files = book.TemplatesUnder("ObjectData/").Where(t => !s_skipped.Any(s => t.File.StartsWith(s, StringComparison.OrdinalIgnoreCase))).ToList();
        var items = new ConcurrentBag<GearItem>();
        var sets = new ConcurrentBag<GearSetBonus>();
        System.Threading.Tasks.Parallel.ForEach(files, file => {
            try {
                switch (book.DecodeFile(file.File)) {
                    case WizItemTemplate template when gear.Read(file.Id, file.File, template) is { } item:
                        items.Add(item);
                        break;
                    case ItemSetBonusTemplate set:
                        sets.Add(gear.ReadSet(file.Id, set));
                        break;
                }
            }
            catch (Exception) {
                // A template the reader can't make sense of is left out.
            }
        });
        gear.Items = [.. items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Level).ThenBy(i => i.Id)];
        gear.Sets = [.. sets.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
        // Pieces of a set can be found by the set's name (Niduus, 2026-10-25: "also to search for set bonuses").
        var setsById = sets.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var item in gear.Items) {
            if (item.SetId != 0 && setsById.TryGetValue(item.SetId, out var set)) {
                item.Set = set;
                item.Haystack += $" {set.Name} {set.Code}".ToLowerInvariant();
            }
        }

        return gear;
    }

    private Dictionary<uint, GearItem>? _byId;

    /// <summary>A piece of gear (or bundle) by template ID, or null when it isn't gear.</summary>
    internal GearItem? Item(uint id) {
        _byId ??= Items.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());

        return _byId.GetValueOrDefault(id);
    }

    /// <summary>The spell card an item gives, by the spell's game name (ProvideSpellEffectInfo names it so).</summary>
    internal SpellCard? Spell(string code) {
        _spellsByCode ??= _book.Spells.GroupBy(s => s.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Hidden).First(), StringComparer.OrdinalIgnoreCase);

        return _spellsByCode.GetValueOrDefault(code);
    }

    // ---------------------------------------------------------------------
    // Stat tables
    // ---------------------------------------------------------------------

    private void ReadStatTables() {
        foreach (var (_, file) in _book.FilesUnder("GameEffectData/")) {
            if (_book.DecodeFile(file) is GameEffectTemplateList list) {
                foreach (var effect in list.m_effectTemplates ?? []) {
                    if (effect is WizStatisticEffectTemplate stat && (string) stat.m_effectName is { Length: > 0 } name) {
                        _effects.TryAdd(name, stat);
                    }
                }
            }
        }

        foreach (var (_, file) in _book.FilesUnder("GameEffectRuleData/")) {
            if (_book.DecodeFile(file) is WizardStatTable table && table.m_statVector is { Count: > 0 } values) {
                // Percent tables hold fractions (0.07 is 7%, up to 3 for damage); rating and health tables hold whole numbers.
                _tables[Path.GetFileNameWithoutExtension(file)] = (values, values.All(v => Math.Abs(v) <= 5f));
            }
        }
    }

    private (List<float> Values, bool Fraction)? Table(string name) {
        if (_tables.TryGetValue(name, out var table)) {
            return table;
        }

        // Some effects name their table the other way round from its file: ReduceDamageFlat_AllSchools is the file
        // FlatReduceDamage_AllSchools (the same fix as the server's GameEffectRuleData).
        var split = name.IndexOf('_');
        if (split > 4 && name[..split].EndsWith("Flat", StringComparison.Ordinal)
                && _tables.TryGetValue("Flat" + name[..(split - 4)] + name[split..], out table)) {
            return table;
        }

        return null;
    }

    // ---------------------------------------------------------------------
    // One item
    // ---------------------------------------------------------------------

    private GearItem? Read(uint id, string file, WizItemTemplate template) {
        var adjectives = (template.m_adjectiveList ?? []).Select(a => (string) a ?? "").ToList();
        var behaviors = (template.m_behaviors ?? []).Where(b => b is not null).ToList();
        // A bundle (Niduus, 2026-10-22: "I want Bundle to be included") shows the items it gives.
        var bundle = template as ItemBundleTemplate;
        var slot = bundle is not null ? "Bundle" : SlotOf(adjectives, behaviors);
        if (slot is null) {
            return null;
        }

        var (level, schoolNeeded) = Requirements(template.m_equipRequirements);
        var stats = new List<GearStat>();
        var spells = new List<GearSpell>();
        ReadEffects(template.m_equipEffects, stats, spells);

        var sockets = behaviors.OfType<JewelSocketBehaviorTemplate>().SelectMany(b => b.m_jewelSockets ?? [])
            .Where(s => s is not null).Select(s => new GearSocket(SocketShape(s.m_socketType.ToString()), s.m_bLockable)).ToList();
        var rental = behaviors.OfType<RentalBehaviorTemplate>().Select(r => (string) r.m_expireTime ?? "").FirstOrDefault(t => t.Length > 0) ?? "";
        var pet = behaviors.OfType<PetItemBehaviorTemplate>().FirstOrDefault() is { } petBehavior ? Pet(petBehavior) : null;
        var deck = behaviors.OfType<DeckBehaviorTemplate>().FirstOrDefault() is { } deckBehavior
            ? new GearDeck(deckBehavior.m_maxSpells, deckBehavior.m_genericMaxInstances, deckBehavior.m_schoolMaxInstances,
                deckBehavior.m_maxTreasureCards, (string) deckBehavior.m_primarySchoolName ?? "")
            : null;

        string displayName = template.m_displayName;
        string code = (string) template.m_objectName ?? "";
        var item = new GearItem {
            Id = id,
            Name = _book.Text(displayName) is { Length: > 0 } shown ? shown.Trim() : code,
            Code = code,
            File = file,
            Slot = slot,
            SchoolNeeded = schoolNeeded,
            School = (string) template.m_school ?? "",
            Level = level,
            Rarity = template.m_rarity.ToString() is { Length: > 3 } rarity && rarity.StartsWith("RT_", StringComparison.Ordinal)
                ? rarity[3..] switch { "ULTRARARE" => "Ultra rare", var r => char.ToUpperInvariant(r[0]) + r[1..].ToLowerInvariant() }
                : "",
            Flags = [.. adjectives.Where(a => a.StartsWith("FLAG_", StringComparison.OrdinalIgnoreCase)).Select(a => a[5..]).Distinct()],
            Stats = stats,
            Spells = spells,
            Sockets = sockets,
            Rental = rental,
            Pet = pet,
            Deck = deck,
            GoldCost = (int) Math.Max(0, template.m_baseCost),
            CrownsCost = (int) Math.Max(0, template.m_creditsCost),
            SetId = template.m_itemSetBonusTemplateID,
            BundleItems = bundle is null ? [] : [.. (bundle.m_bundleItems ?? []).Concat(bundle.m_bundleFreeItems ?? []).Select(g => (uint) (ulong) g).Distinct()],
        };
        item.Haystack = $"{item.Name} {item.Code} {item.Id} {item.Slot} {item.SchoolNeeded} {item.School} {string.Join(' ', item.Flags)}".ToLowerInvariant();

        return item;
    }

    private static string? SlotOf(List<string> adjectives, List<BehaviorTemplate> behaviors) {
        bool Has(string adjective) => adjectives.Contains(adjective, StringComparer.OrdinalIgnoreCase);

        if (behaviors.Any(b => b is MountItemBehaviorTemplate) || Has("Mount")) {
            return "Mount";
        }

        if (behaviors.Any(b => b is PetItemBehaviorTemplate)) {
            return "Pet";
        }

        return Has("Hat") ? "Hat"
            : Has("Robe") ? "Robe"
            : Has("Shoes") ? "Boots"
            : Has("Weapon") || Has("Wand") ? "Wand"
            : Has("Athame") ? "Athame"
            : Has("Amulet") ? "Amulet"
            : Has("Ring") ? "Ring"
            : Has("Deck") || behaviors.Any(b => b is DeckBehaviorTemplate) ? "Deck"
            : null;
    }

    /// <summary>The level and school needed to wear it, from its equip requirements (and any lists inside them).</summary>
    private static (int Level, string School) Requirements(Requirement? requirement) {
        var level = 0;
        var school = "";
        void Walk(Requirement? r, int depth) {
            if (r is null || depth > 6 || r.m_applyNOT) {
                return;
            }

            switch (r) {
                case RequirementList list:
                    foreach (var inner in list.m_requirements ?? []) {
                        Walk(inner, depth + 1);
                    }

                    break;
                case ReqMagicLevel magicLevel:
                    level = Math.Max(level, (int) magicLevel.m_numericValue);
                    break;
                case ReqSchoolOfFocus focus when (string) focus.m_magicSchool is { Length: > 0 } name:
                    school = name;
                    break;
            }
        }

        Walk(requirement, 0);

        return (level, school);
    }

    /// <summary>The stat lines and item cards a list of equip effects gives.</summary>
    private void ReadEffects(IEnumerable<GameEffectInfo>? effects, List<GearStat> stats, List<GearSpell> spells) {
        foreach (var effect in effects ?? []) {
            switch (effect) {
                case StatisticEffectInfo stat when Stat(stat) is { } line:
                    stats.Add(line);
                    break;
                case SpeedEffectInfo speed when speed.m_speedMultiplier != 0:
                    stats.Add(new GearStat($"{Signed(speed.m_speedMultiplier)}%", ["speed"], "", "", "Speed: how much faster you move on it") {
                        Kind = "Speed", Amount = speed.m_speedMultiplier, Percent = true,
                    });
                    break;
                case StartingPipEffectInfo pips:
                    AddPips(stats, pips.m_pipsGiven, pips.m_powerPipsGiven);
                    break;
                case TempStartingPipEffectInfo pips:
                    AddPips(stats, pips.m_pipsGiven, pips.m_powerPipsGiven);
                    break;
                case ProvideSpellEffectInfo spell when (string) spell.m_spellName is { Length: > 0 } spellCode:
                    spells.Add(new GearSpell(spellCode, Math.Max(1, spell.m_numSpells)));
                    break;
            }
        }
    }

    private GearSetBonus ReadSet(uint id, ItemSetBonusTemplate template) {
        var tiers = new List<GearSetTier>();
        foreach (var data in (template.m_itemSetBonusDataList ?? []).Where(d => d is not null).OrderBy(d => d.m_numItemsToEquip)) {
            var stats = new List<GearStat>();
            var spells = new List<GearSpell>();
            ReadEffects(data.m_equipEffectsGranted, stats, spells);
            string description = data.m_description;
            tiers.Add(new GearSetTier(data.m_numItemsToEquip, _book.Text(description ?? "")?.Trim() ?? "", stats, spells));
        }

        string displayName = template.m_displayName;
        string code = (string) template.m_objectName ?? "";

        return new GearSetBonus(id, _book.Text(displayName ?? "") is { Length: > 0 } shown ? shown.Trim() : code, code, tiers);
    }

    private static void AddPips(List<GearStat> stats, int pips, int powerPips) {
        if (pips != 0) {
            stats.Add(new GearStat(Signed(pips), ["pip"], "", "", $"Starts each battle with {pips} more pip{(pips == 1 ? "" : "s")}") {
                Kind = "StartingPips", Amount = pips,
            });
        }

        if (powerPips != 0) {
            stats.Add(new GearStat(Signed(powerPips), ["powerpip"], "", "",
                $"Starts each battle with {powerPips} more power pip{(powerPips == 1 ? "" : "s")}") { Kind = "StartingPowerPips", Amount = powerPips });
        }
    }

    private static string SocketShape(string type) => (type.StartsWith("SOCKETTYPE_", StringComparison.Ordinal) ? type[11..] : type) switch {
        "SQUARE" => "Square",
        "CIRCLE" => "Circle",
        "TRIANGLE" => "Triangle",
        "TEAR" => "Tear",
        "PET" => "Star",
        "PINSQUAREPIP" => "Power",
        "PINSQUARESHIELD" => "Shield",
        "PINSQUARESWORD" => "Sword",
        var other => other.Length > 0 ? char.ToUpperInvariant(other[0]) + other[1..].ToLowerInvariant() : "Unknown",
    };

    private GearPet Pet(PetItemBehaviorTemplate pet) {
        // The order the game's pet tooltip lists them in.
        string[] order = ["Strength", "Intellect", "Agility", "Will", "Power", "Happiness"];
        var stats = (pet.m_maxStats ?? []).Where(s => s is not null).Select(s => ((string) s.m_name ?? "", s.m_value))
            .OrderBy(s => Array.IndexOf(order, s.Item1) is var i and >= 0 ? i : 99).ToList();
        var spells = new List<string>();
        var spellsAt = 0;
        foreach (var level in (pet.m_Levels ?? []).Where(l => l is not null).OrderBy(l => l.m_level)) {
            foreach (var name in new[] { (string) level.m_powerCardName, (string) level.m_powerCardName2, (string) level.m_powerCardName3 }) {
                if (!string.IsNullOrEmpty(name) && !spells.Contains(name, StringComparer.OrdinalIgnoreCase)) {
                    spells.Add(name);
                    if (spellsAt == 0) {
                        spellsAt = Math.Max(1, (int) level.m_level);
                    }
                }
            }
        }

        return new GearPet(stats, pet.m_talents?.Count ?? 0, pet.m_derbyTalents?.Count ?? 0, spellsAt, spells, (string) pet.m_sHatchRate ?? "");
    }

    // ---------------------------------------------------------------------
    // Stat lines
    // ---------------------------------------------------------------------

    [GeneratedRegex("^(Fire|Ice|Storm|Myth|Life|Death|Balance|Shadow|Sun|Moon|Star|All)(.+)$")]
    private static partial Regex SchoolPrefix();

    /// <summary>
    /// A stat line for an equip effect, worked out as the server does; null when its effect or table is not in the game
    /// files (the line is left out rather than shown wrong).
    /// </summary>
    private GearStat? Stat(StatisticEffectInfo info) {
        if (!_effects.TryGetValue((string) info.m_effectName ?? "", out var effect)) {
            return null;
        }

        var category = (string) effect.m_effectCategory ?? "";
        var school = (string) effect.m_school ?? "";
        var kind = category;
        var effectName = (string) effect.m_effectName ?? "";
        if (category.StartsWith("ShadowPip", StringComparison.Ordinal)) {
            // Shadow pip stats start with "Shadow" but aren't Shadow school stats. CanonicalShadowPipRating shares its
            // category with Shadow pip conversion, so its own name tells them apart; the game shows the shadow pip icon.
            kind = effectName.Contains("ShadowPipRating", StringComparison.Ordinal) ? "ShadowPipRating"
                : category == "ShadowPips" ? "ShadowPips" : "PipConversion";
            school = kind == "PipConversion" ? school : "";
        }
        else if (SchoolPrefix().Match(category) is { Success: true } prefixed) {
            school = school.Length > 0 ? school : prefixed.Groups[1].Value;
            kind = prefixed.Groups[2].Value;
        }

        // Flat damage shares its category with percent damage (CanonicalIceFlatDamage is IceDamage), so its own name
        // tells them apart, as the server's CanonicalStatEffects does.
        if (effectName.Contains("Flat", StringComparison.Ordinal) && !kind.Contains("Flat", StringComparison.Ordinal)) {
            kind = "Flat" + kind;
        }

        // FlatReduceDamageAll names its school at the end.
        if (kind.EndsWith("All", StringComparison.Ordinal) && kind.Length > 3) {
            kind = kind[..^3];
            school = "All";
        }

        if (school.Equals("All", StringComparison.OrdinalIgnoreCase) || school.Equals("AllSchools", StringComparison.OrdinalIgnoreCase)) {
            school = "";
        }

        if (Table((string) effect.m_statTableName ?? "") is not { } table || info.m_lookupIndex < 0 || info.m_lookupIndex >= table.Values.Count) {
            return null;
        }

        var raw = table.Values[info.m_lookupIndex];
        var percent = table.Fraction;
        var amount = (int) Math.Round(percent ? raw * 100 : raw, MidpointRounding.AwayFromZero);
        var value = Signed(amount) + (percent ? "%" : "");
        var forSchool = school.Length > 0 ? $" ({school} spells)" : "";

        return (kind switch {
            "MaxHealth" => new GearStat($"{value} MAX", ["health"], "", "", "Max Health"),
            "MaxMana" => new GearStat($"{value} MAX", ["mana"], "", "", "Max Mana"),
            "MaxEnergy" => new GearStat($"{value} MAX", ["energy"], "", "", "Max Energy"),
            "Damage" => new GearStat(value, ["damage"], school, "", "Damage: hits harder" + forSchool),
            "FlatDamage" => new GearStat(value, ["damageflat"], school, "", "Flat Damage: this much more damage on every hit" + forSchool),
            "Accuracy" => new GearStat(value, ["accuracy"], school, "", "Accuracy: spells fizzle less" + forSchool),
            "ArmorPiercing" => new GearStat(value, ["pierce"], school, "", "Armor Piercing: goes through the enemy's resist" + forSchool),
            "ReduceDamage" => new GearStat(value, ["resist"], school, "", "Resist: takes less damage" + (school.Length > 0 ? $" from {school}" : "")),
            "FlatReduceDamage" => new GearStat(value, ["resistflat"], school, "",
                "Flat Resist: this much less damage from every hit" + (school.Length > 0 ? $" from {school}" : "")),
            "CriticalHit" => new GearStat(value, ["critical"], school, "RATING", "Critical Hit Rating: more chance of a critical hit" + forSchool),
            "Block" => new GearStat(value, ["block"], school, "RATING", "Block Rating: more chance of blocking a critical hit" +
                (school.Length > 0 ? $" from {school}" : "")),
            "AccuracyRating" => new GearStat(value, ["accuracy"], school, "RATING", "Accuracy Rating" + forSchool),
            "ReduceDamageRating" => new GearStat(value, ["resist"], school, "RATING", "Resist Rating"),
            "PowerPipRating" => new GearStat(value, ["powerpip"], school, "RATING", "Power Pip Rating"),
            "PowerPips" => new GearStat(value, ["powerpip"], school, "", "Power Pip Chance: more of your pips come as power pips"),
            "ShadowPipRating" => new GearStat(value, ["shadowpip"], "", "RATING", "Shadow Pip Rating: shadow pips come sooner"),
            "ShadowPips" => new GearStat(value, ["shadowpip"], "", "", "Shadow Pip Chance: more chance of a shadow pip"),
            "PipConversion" => new GearStat(value, ["pipconversion"], school, "RATING",
                "Pip Conversion Rating: more chance a normal pip turns into a power pip after a spell" + forSchool),
            "ShadowPipConversion" => new GearStat(value, ["pipconversion"], school, "RATING", "Shadow Pip Conversion Rating"),
            "Archmastery" => new GearStat(value, ["archmastery"], school, "RATING", "Archmastery Rating: school pips come sooner"),
            "Healing" => new GearStat(value, ["outgoing", "heal"], school, "", "Outgoing Healing: your heals heal more"),
            "IncHealing" => new GearStat(value, ["incoming", "heal"], school, "", "Incoming Healing: heals on you heal more"),
            "IncomingAccuracy" => new GearStat(value, ["incoming", "accuracy"], school, "", "Incoming Accuracy"),
            "StunResistance" => new GearStat(value, ["stunresist"], school, "", "Stun Resist: shorter stuns"),
            "Mastery" => new GearStat("", [], school, "MASTERY", $"{school} Mastery: power pips count as 2 for {school} spells"),
            "XPPercent" => new GearStat(value, [], "", "XP", "More experience from battles"),
            "GoldPercent" => new GearStat(value, [], "", "GOLD", "More gold from battles"),
            "FishingLuck" => new GearStat(value, ["fishing"], school, "", "Fishing Luck"),
            "WispBonus" => new GearStat(value, [], "", "WISPS", "Wisp Bonus: health and mana wisps give more"),
            "MaxManaPercentReduce" => new GearStat(value, ["mana"], "", "", "Mana: spells cost less mana"),
            _ => new GearStat(value, [], school, Spaced(kind).ToUpperInvariant(), Spaced(kind)),
        }) with { Kind = kind, Amount = amount, Percent = percent };
    }

    /// <summary>A stat's name for the stat filters, such as "Resist" for ReduceDamage.</summary>
    internal static string StatName(string kind) => kind switch {
        "Damage" => "Damage",
        "FlatDamage" => "Flat Damage",
        "ReduceDamage" => "Resist",
        "FlatReduceDamage" => "Flat Resist",
        "Accuracy" => "Accuracy",
        "ArmorPiercing" => "Armor Piercing",
        "CriticalHit" => "Critical Rating",
        "Block" => "Block Rating",
        "MaxHealth" => "Max Health",
        "MaxMana" => "Max Mana",
        "MaxEnergy" => "Max Energy",
        "PowerPips" => "Power Pip Chance",
        "Healing" => "Outgoing Healing",
        "IncHealing" => "Incoming Healing",
        "ShadowPipRating" => "Shadow Pip Rating",
        "ShadowPips" => "Shadow Pip Chance",
        "PipConversion" => "Pip Conversion Rating",
        "Archmastery" => "Archmastery Rating",
        "StunResistance" => "Stun Resist",
        "StartingPips" => "Starting Pips",
        "StartingPowerPips" => "Starting Power Pips",
        "XPPercent" => "Bonus XP",
        "GoldPercent" => "Bonus Gold",
        "MaxManaPercentReduce" => "Mana Cost",
        var other => Spaced(other),
    };

    private static string Signed(int value) => value >= 0 ? $"+{value.ToString(CultureInfo.InvariantCulture)}" : value.ToString(CultureInfo.InvariantCulture);

    /// <summary>"StunResistance" as "Stun Resistance".</summary>
    private static string Spaced(string name) => string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]) ? " " + c : c.ToString()));

    /// <summary>A rental's time in words, such as "7 Day" for 7d, as the game's names write it.</summary>
    internal static string RentalWords(string time) {
        var match = Regex.Match(time ?? "", @"^(\d+)\s*([dhm])$", RegexOptions.IgnoreCase);
        if (!match.Success) {
            return time ?? "";
        }

        var unit = char.ToLowerInvariant(match.Groups[2].Value[0]) switch { 'd' => "Day", 'h' => "Hour", _ => "Minute" };

        return $"{match.Groups[1].Value} {unit}";
    }

}

/// <summary>
/// What the item tooltip's marks mean, as the game's Help pages name them, with a plain-words line for pointing at
/// each (Niduus, 2026-10-22: "marks such as no sell, no auction, no trade, crowns etc.").
/// </summary>
internal static class GearMarks {

    /// <summary>The flags that show a mark, in the order the game draws them, with the game's text key and plain words.</summary>
    internal static readonly (string Flag, string TextKey, string Name, string Words)[] MARKS = [
        ("NoAuction", "Help_IconsFlagNoAuction", "No Auction", "It can't be sold at the Bazaar, so no other player can buy it there."),
        ("CrownsOnly", "Help_IconsFlagCrownsOnly", "Crowns Only", "It comes from the Crown Shop and is bought with Crowns."),
        ("NoSell", "Help_IconsFlagNoSell", "No Sell", "Shops won't buy it back: it can't be sold for gold."),
        ("NoTrade", "Help_IconsFlagNoTrade", "No Trade", "It can't be traded to another player."),
        ("NoGift", "", "No Gift", "It can't be sent to a friend as a gift."),
        ("NoBank", "", "No Bank", "It can't be put in the bank or the shared bank."),
        ("NoDrop", "Help_IconsFlagNoDrop", "No Trash", "It can't be thrown away."),
        ("NoDye", "Help_IconsFlagNoDye", "No Dye", "Its colours can't be changed at the dye shop."),
        ("NoShatter", "Help_IconsFlagNoShatter", "No Shatter", "Its jewels can't be shattered out of it."),
        ("NoHatchmaking", "Help_IconsFlagNoHatchmaking", "No Hatchmaking", "This pet can't be used to hatch new pets."),
        ("NoPVP", "Help_IconsFlagNoPVP", "No PvP", "It can't be used in PvP (player against player) battles."),
        ("PVPOnly", "Help_IconsFlagPVPOnly", "PvP Only", "It can only be used in PvP (player against player) battles."),
        ("NoCombat", "Help_IconsFlagNoCombat", "No Combat", "It can't be used in battle."),
        ("ArenaPointsOnly", "Help_IconsFlagArenaPointsOnly", "Arena Tickets Only", "It is bought with Arena tickets from PvP."),
        ("Retired", "GUI2_HF_Retired", "Retired", "The game no longer sells or drops it."),
    ];

}
