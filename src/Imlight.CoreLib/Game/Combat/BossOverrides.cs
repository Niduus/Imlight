/*
 * Boss changes from the app's Bosses tab (Niduus, 2026-10-21: "edit the bosses deck and stats"), read from
 * spiraldb-extra/BossOverrides/<template id>.json, one file per creature template. The game files are never changed.
 *
 * A creature's stats and deck are built when its zone loads (NpcComponent, StatsComponent, CombatCreatureEquipmentComponent
 * and CombatCreatureDeckComponent). The changes are laid over them each time the creature joins a battle, so a saved file
 * counts from the next battle, without a restart. A field left out (null) keeps what the game gives.
 *
 *   Health, Level, School    its most health, its level (crits and the level shown) and its school.
 *   DamageBySchool, ResistBySchool, IncomingBoostBySchool, PierceBySchool (percent, 20 = 20%) and CriticalBySchool,
 *   BlockBySchool (rating): by school name, with "All" for the schools not named; a school with neither keeps the game's
 *   value. Incoming boost is a weakness: the game keeps it as resist below zero, so the boss's resist to a school is
 *   its Resist minus its IncomingBoost. Pierce takes that much off the target's shields, then off its resist (above zero).
 *   Damage, Resist, Critical, Block   one number, as the files of v0.10.96 saved them: read as "All" of the row above.
 *   FlatDamage, FlatResist   hit points added to each hit it makes, or taken off each hit it takes, every school.
 *   Accuracy                 percent added to its spells' accuracy, every school.
 *   OutgoingHealing, IncomingHealing   percent more healing it gives and gets.
 *   PowerPipChance           percent chance a new pip is a power pip.
 *   StartingPips, StartingPowerPips    the pips it starts the battle with.
 *   ShadowPips, ShadowPipChance        the most shadow pips it holds (0 = none) and its percent a round to gain one.
 *                            They win over a BossCheats file's ShadowPips and the [Battle] creature settings.
 *   Stunable, Beguilable     false: stun spells do nothing to it, or it cannot be beguiled. The server has no
 *                            beguile (mind control) effect yet, so Beguilable is kept for when it does (CanBeBeguiled).
 *   Deck                     the spells it casts, replacing its game deck when there is at least one. Copies: how many
 *                            times it can use the spell in one battle; 0 = it never runs out (as the game's creatures).
 *   Cheats                   changes to its BossCheats file (CheatsOf), laid over it each time the cheats are read:
 *     Changed  by the cheat's place in that file ("0" is the first): the fields that differ, as the file names them
 *              (Casts, Chance, Line...), "Off": true to turn the cheat off, and "Match": its When|Cast|Line as it was,
 *              so the change still finds its cheat when the file gains or loses cheats before it (a change whose cheat
 *              is gone is left out).
 *     Added    whole new cheats, after the file's own, as a BossCheats file writes them. A creature with no cheat file
 *              can have them too.
 *   A form a boss is polymorphed into uses its own template's file (Haunted Malistaire: 1336037.json).
 * A custom creature of the Customs tab (Customs.cs) has a file of this format of its own, in spiraldb-extra/Customs,
 * with its CustomID, BaseTemplateID, Name (shown in battle), Drops and Forms; For reads it by the custom's key.
 *   Drops                    drop table names (DropTables): what a win over it drops, instead of the creature's own
 *                            (NpcDropTable). A boss of the Bosses tab can have them too.
 *   XP, GoldMin, GoldMax     what a win over it gives, instead of what its drop tables and the game give. Gold is a
 *                            random amount from GoldMin to GoldMax. RewardShared: true splits them between the wizards
 *                            who fought; otherwise each wizard gets them all. Counted from the next win.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.Combat;

internal static class BossOverrides {

    internal sealed class BossOverride {
        public ulong TemplateID { get; set; }
        public string Name { get; set; } = "";
        public string Notes { get; set; } = "";
        public int? Health { get; set; }
        public int? Level { get; set; }
        public string School { get; set; }
        public float? Damage { get; set; }
        public float? Resist { get; set; }
        public Dictionary<string, float> DamageBySchool { get; set; }
        public Dictionary<string, float> ResistBySchool { get; set; }
        public Dictionary<string, float> IncomingBoostBySchool { get; set; }
        public Dictionary<string, float> PierceBySchool { get; set; }
        public Dictionary<string, float> CriticalBySchool { get; set; }
        public Dictionary<string, float> BlockBySchool { get; set; }
        public float? FlatDamage { get; set; }
        public float? FlatResist { get; set; }
        public float? Accuracy { get; set; }
        public float? Critical { get; set; }
        public float? Block { get; set; }
        public float? OutgoingHealing { get; set; }
        public float? IncomingHealing { get; set; }
        public float? PowerPipChance { get; set; }
        public int? StartingPips { get; set; }
        public int? StartingPowerPips { get; set; }
        public int? ShadowPips { get; set; }
        public int? ShadowPipChance { get; set; }
        public bool? Stunable { get; set; }
        public bool? Beguilable { get; set; }
        public List<BossCard> Deck { get; set; }
        public CheatChanges Cheats { get; set; }
        // A custom's (Customs.cs).
        public int CustomID { get; set; }
        public ulong BaseTemplateID { get; set; }
        public List<string> Drops { get; set; }
        public Dictionary<string, int> Forms { get; set; }
        // What a win over it gives (CombatService.GrantMobLoot); left out keeps what the game gives.
        public int? XP { get; set; }
        public int? GoldMin { get; set; }
        public int? GoldMax { get; set; }
        // True: the XP and gold above are split between the wizards who fought; false or left out: each gets all.
        public bool? RewardShared { get; set; }
    }

    internal sealed class CheatChanges {
        public Dictionary<string, JsonObject> Changed { get; set; } = [];
        public List<BossCheats.BossCheat> Added { get; set; } = [];
    }

    internal sealed class BossCard {
        public uint SpellID { get; set; }
        public string Name { get; set; } = "";
        public int Copies { get; set; }
    }

    // What a creature's deck holds of a spell that never runs out.
    internal const uint UNLIMITED = 9999;

    internal const string FOLDER = "BossOverrides";

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    // Each file as last read, kept until the file changes.
    private static readonly ConcurrentDictionary<ulong, (DateTime Written, BossOverride Boss)> s_read = new();

    /// <summary>The changes saved for a creature template, or null.</summary>
    internal static BossOverride For(ulong templateId) {
        var file = Customs.IsCustom(templateId)
            ? Customs.FileOf(Customs.IdOf(templateId))
            : Path.Combine(SpiralDB.ExtraPath, FOLDER, $"{templateId}.json");
        if (!File.Exists(file)) {
            s_read.TryRemove(templateId, out _);

            return null;
        }

        var written = File.GetLastWriteTimeUtc(file);
        if (s_read.TryGetValue(templateId, out var kept) && kept.Written == written) {
            return kept.Boss;
        }

        BossOverride boss = null;
        try {
            boss = JsonSerializer.Deserialize<BossOverride>(File.ReadAllText(file), s_options);
            if (boss is not null) {
                boss.TemplateID = templateId;
                if (Customs.IsCustom(templateId)) {
                    boss.CustomID = Customs.IdOf(templateId);
                    boss.Drops = boss.Drops?.Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
                }

                boss.Deck = boss.Deck?.Where(c => c is not null && c.SpellID != 0).ToList();
                boss.DamageBySchool = Row(boss.DamageBySchool, boss.Damage);
                boss.ResistBySchool = Row(boss.ResistBySchool, boss.Resist);
                boss.IncomingBoostBySchool = Row(boss.IncomingBoostBySchool, null);
                boss.PierceBySchool = Row(boss.PierceBySchool, null);
                boss.CriticalBySchool = Row(boss.CriticalBySchool, boss.Critical);
                boss.BlockBySchool = Row(boss.BlockBySchool, boss.Block);
                Logger.Information("Boss changes for {0} ({1}) read.", Logger.Args(boss.Name, templateId));
            }
        }
        catch (Exception e) {
            Logger.Warning("Boss changes {0} could not be read: {1}", Logger.Args(Path.GetFileName(file), e.Message));
        }

        s_read[templateId] = (written, boss);

        return boss;
    }

    /// <summary>
    /// A copy of the creature's stats with the changes laid over it, so the creature itself keeps the game's stats.
    /// </summary>
    internal static ServerWizGameStats Apply(BossOverride boss, ServerWizGameStats stats, ref MagicSchool school, ref int level) {
        var copy = stats.CopyForDuel();
        if (boss.School is { Length: > 0 } name && Enum.TryParse<MagicSchool>(name, true, out var parsed)) {
            school = parsed;
            copy.MagicSchool = parsed;
        }

        if (boss.Level is { } newLevel && newLevel > 0) {
            level = newLevel;
            copy.Level = newLevel;
        }

        if (boss.Health is { } health && health > 0) {
            copy.m_baseHitpoints = health;
            copy.m_currentHitpoints = health;
        }

        // Each row is laid out school by school, the every-school value set to 0. The damage, resist and pierce totals
        // pass through CombatEffectApplicator.AsFraction, which reads anything above 1.5 as percent.
        if (boss.DamageBySchool is not null) {
            copy.m_dmgBonusPercent = BySchoolList(s => ForAsFraction(Pick(boss.DamageBySchool, s) ?? GameValue(stats, "Damage", s)));
            copy.m_dmgBonusPercentAll = 0;
        }

        if (boss.ResistBySchool is not null || boss.IncomingBoostBySchool is not null) {
            copy.m_dmgReducePercent = BySchoolList(s => ForAsFraction(
                (Pick(boss.ResistBySchool, s) ?? GameValue(stats, "Resist", s))
                - (Pick(boss.IncomingBoostBySchool, s) ?? GameValue(stats, "IncomingBoost", s))));
            copy.m_dmgReducePercentAll = 0;
        }

        if (boss.PierceBySchool is not null) {
            copy.m_apBonusPercent = BySchoolList(s => ForAsFraction(Pick(boss.PierceBySchool, s) ?? GameValue(stats, "Pierce", s)));
            copy.m_apBonusPercentAll = 0;
        }

        if (boss.CriticalBySchool is not null) {
            copy.m_criticalHitRatingBySchool = BySchoolList(s => Pick(boss.CriticalBySchool, s) ?? GameValue(stats, "Critical", s));
            copy.m_criticalHitRatingAll = 0;
        }

        if (boss.BlockBySchool is not null) {
            copy.m_blockRatingBySchool = BySchoolList(s => Pick(boss.BlockBySchool, s) ?? GameValue(stats, "Block", s));
            copy.m_blockRatingAll = 0;
        }

        if (boss.FlatDamage is { } flatDamage) {
            copy.m_dmgBonusFlat = null;
            copy.m_dmgBonusFlatAll = flatDamage;
        }

        if (boss.FlatResist is { } flatResist) {
            copy.m_dmgReduceFlat = null;
            copy.m_dmgReduceFlatAll = flatResist;
        }

        if (boss.Accuracy is { } accuracy) {
            copy.m_accBonusPercent = null;
            copy.m_accBonusPercentAll = accuracy / 100f;
        }

        if (boss.OutgoingHealing is { } outgoing) {
            copy.m_healBonusPercentAll = outgoing / 100f;
        }

        if (boss.IncomingHealing is { } incoming) {
            copy.m_healIncBonusPercentAll = incoming / 100f;
        }

        if (boss.PowerPipChance is { } powerPip) {
            copy.m_powerPipBase = powerPip / 100f;
            copy.m_powerPipBonusPercentAll = 0;
        }

        if (boss.StartingPips is { } pips) {
            copy.m_startingPips = (byte) Math.Clamp(pips, 0, 7);
        }

        if (boss.StartingPowerPips is { } powerPips) {
            copy.m_startingPowerPips = (byte) Math.Clamp(powerPips, 0, 7);
        }

        return copy;
    }

    /// <summary>
    /// The deck saved for the creature, or null to keep its game deck. Spells the game does not know are left out.
    /// </summary>
    internal static List<CombatDeckSpellData> DeckOf(BossOverride boss) {
        if (boss.Deck is not { Count: > 0 }) {
            return null;
        }

        var deck = new List<CombatDeckSpellData>();
        foreach (var card in boss.Deck) {
            if (SpellFactory.GetSpell(card.SpellID) is null) {
                Logger.Warning("Boss changes for {0}: spell {1} ({2}) is not in the game, so it is left out.",
                    Logger.Args(boss.Name, card.SpellID, card.Name));

                continue;
            }

            var copies = card.Copies > 0 ? (uint) card.Copies : UNLIMITED;
            if (deck.Find(d => d.TemplateId == card.SpellID) is { } same) {
                same.Quantity = Math.Min(UNLIMITED, same.Quantity + copies);

                continue;
            }

            deck.Add(new CombatDeckSpellData { TemplateId = card.SpellID, Quantity = copies });
        }

        return deck.Count > 0 ? deck : null;
    }

    // The cheats as last laid out, kept while neither the file nor the changes are read again.
    private static readonly ConcurrentDictionary<ulong, (BossOverride Boss, List<BossCheats.BossCheat> Shipped,
        List<BossCheats.BossCheat> Cheats)> s_cheats = new();

    /// <summary>
    /// A boss's cheats with the Bosses tab's changes laid over its BossCheats file. A cheat turned off keeps its place
    /// (When "Off" never comes), so the cheats after it keep theirs and once-only cheats stay counted.
    /// </summary>
    internal static List<BossCheats.BossCheat> CheatsOf(ulong templateId, List<BossCheats.BossCheat> shipped) {
        if (For(templateId) is not { Cheats: { } changes } boss) {
            return shipped;
        }

        if (s_cheats.TryGetValue(templateId, out var kept) && kept.Boss == boss && kept.Shipped == shipped) {
            return kept.Cheats;
        }

        var cheats = new List<BossCheats.BossCheat>(shipped);
        var taken = new HashSet<int>();
        foreach (var (key, change) in (changes.Changed ?? []).OrderBy(c => int.TryParse(c.Key, out var n) ? n : int.MaxValue)) {
            var match = change?.FirstOrDefault(p => p.Key.Equals("Match", StringComparison.OrdinalIgnoreCase)).Value?.ToString();
            var index = int.TryParse(key, out var n) ? n : -1;
            if (match is { Length: > 0 } && !(index >= 0 && index < shipped.Count && Fingerprint(shipped[index]) == match.ToLowerInvariant())) {
                index = shipped.FindIndex(c => !taken.Contains(shipped.IndexOf(c)) && Fingerprint(c) == match.ToLowerInvariant());
            }

            if (change is null || index < 0 || index >= shipped.Count || !taken.Add(index)) {
                Logger.Warning("Boss changes for {0}: cheat {1} ({2}) is no longer in its cheat file, so its change is left out.",
                    Logger.Args(boss.Name, key, match));

                continue;
            }

            try {
                var json = JsonSerializer.SerializeToNode(shipped[index], s_options)!.AsObject();
                var off = false;
                foreach (var (field, value) in change) {
                    if (field.Equals("Match", StringComparison.OrdinalIgnoreCase)) {
                        continue;
                    }

                    if (field.Equals("Off", StringComparison.OrdinalIgnoreCase)) {
                        off = value is JsonValue v && v.TryGetValue<bool>(out var flag) && flag;

                        continue;
                    }

                    json.Remove(json.FirstOrDefault(p => p.Key.Equals(field, StringComparison.OrdinalIgnoreCase)).Key ?? field);
                    json[field] = value?.DeepClone();
                }

                var cheat = json.Deserialize<BossCheats.BossCheat>(s_options);
                if (off) {
                    cheat.When = "Off";
                }

                cheat.Custom = true;

                cheats[index] = cheat;
            }
            catch (Exception e) {
                Logger.Warning("Boss changes for {0}: cheat {1} could not be read: {2}", Logger.Args(boss.Name, key, e.Message));
            }
        }

        foreach (var added in (changes.Added ?? []).Where(c => c is not null)) {
            added.Custom = true;
            if (added.Off) {
                added.When = "Off";
            }

            added.Actions = [.. (added.Actions ?? []).Where(a => a is not null)];
            cheats.Add(added);
        }

        s_cheats[templateId] = (boss, shipped, cheats);

        return cheats;
    }

    /// <summary>A cheat as Changed's Match names it: When|Cast|Line, lower case.</summary>
    internal static string Fingerprint(BossCheats.BossCheat cheat)
        => $"{cheat.When}|{cheat.Cast}|{cheat.Line}".ToLowerInvariant();

    // 20% is sent as 0.2, and more than 150% as the percent itself, so AsFraction turns both into the same fraction.
    private static float ForAsFraction(float percent)
        => Math.Abs(percent) > 150f ? percent : percent / 100f;

    /// <summary>The rows of the Bosses tab's school grid, in order, as creatures.csv and the boss files name them.</summary>
    internal static readonly string[] ROWS = ["Damage", "Resist", "IncomingBoost", "Pierce", "Critical", "Block"];

    /// <summary>The school columns of the Bosses tab, in order.</summary>
    internal static readonly string[] SCHOOLS = ["Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance", "Star", "Sun", "Moon", "Shadow"];

    private const string ALL = "All";

    // A row read case-blind, with a v0.10.96 single value as its "All"; null when the row changes nothing.
    private static Dictionary<string, float> Row(Dictionary<string, float> bySchool, float? all) {
        var row = new Dictionary<string, float>(bySchool ?? [], StringComparer.OrdinalIgnoreCase);
        if (all is { } value && !row.ContainsKey(ALL)) {
            row[ALL] = value;
        }

        return row.Count > 0 ? row : null;
    }

    private static float? Pick(Dictionary<string, float> row, string school)
        => row is null ? null : row.TryGetValue(school, out var value) ? value : row.TryGetValue(ALL, out var all) ? all : null;

    // A list with a value for every school the game defines (one longer than the highest index, as GetStatBySchool reads).
    private static List<float> BySchoolList(Func<string, float> value) {
        var list = Enumerable.Repeat(0f, (int) MagicSchools.GetMaxMagicSchoolIndex() + 1).ToList();
        foreach (var school in MagicSchools.All) {
            list[school.m_schoolIndex] = value(school.m_schoolName);
        }

        return list;
    }

    /// <summary>
    /// What the game gives a creature in a row of the school grid, in the tab's units: percent for damage, resist,
    /// incoming boost and pierce, rating for critical and block. Resist below zero shows as incoming boost.
    /// </summary>
    internal static float GameValue(ServerWizGameStats stats, string row, string school) {
        float Fraction(List<float> list, float all) => CombatEffectApplicator.AsFraction(BySchool(list, school) + all) * 100f;

        return row switch {
            "Damage" => Fraction(stats.m_dmgBonusPercent, stats.m_dmgBonusPercentAll),
            "Resist" => Math.Max(0f, Fraction(stats.m_dmgReducePercent, stats.m_dmgReducePercentAll)),
            "IncomingBoost" => Math.Max(0f, -Fraction(stats.m_dmgReducePercent, stats.m_dmgReducePercentAll)),
            "Pierce" => Fraction(stats.m_apBonusPercent, stats.m_apBonusPercentAll),
            "Critical" => BySchool(stats.m_criticalHitRatingBySchool, school) + stats.m_criticalHitRatingAll,
            "Block" => BySchool(stats.m_blockRatingBySchool, school) + stats.m_blockRatingAll,
            _ => 0f,
        };
    }

    /// <summary>
    /// The school grid in one creatures.csv field: "Damage=0 0 25 ...;Resist=..." with a number per school of SCHOOLS.
    /// </summary>
    internal static string GameGrid(ServerWizGameStats stats)
        => string.Join(';', ROWS.Select(row => row + "=" + string.Join(' ', SCHOOLS.Select(school
            => MathF.Round(GameValue(stats, row, school), 1).ToString(System.Globalization.CultureInfo.InvariantCulture)))));

    // ---------------------------------------------------------------------
    // The game's own values, for the app (IdCatalog writes them to creatures.csv)
    // ---------------------------------------------------------------------

    /// <summary>
    /// The stats a creature gets from its template, its gear and its base effects, as the zone builds them.
    /// </summary>
    internal static ServerWizGameStats GameStatsOf(GameObjectTemplate template, NPCBehaviorTemplate npc) {
        string schoolName = npc.m_schoolOfFocus;
        var school = Enum.TryParse<MagicSchool>(schoolName, out var parsed) ? parsed : MagicSchool.Balance;
        var stats = new ServerWizGameStats(school, npc.m_nLevel);
        foreach (var itemId in template.m_behaviors.OfType<EquipmentBehaviorTemplate>().SelectMany(e => e.m_itemList)) {
            if (CoreObjectFactory.GetCoreTemplate(itemId) is WizItemTemplate item) {
                CharacterEffectHelper.AddEffectsToGameStats(stats, item);
            }
        }

        stats.m_baseHitpoints = npc.m_nStartingHealth;
        foreach (var effect in npc.m_baseEffects ?? []) {
            CharacterEffectHelper.AddGameEffectToStats(stats, effect);
        }

        return stats;
    }

    /// <summary>
    /// The stats that matter in battle, in words, such as "Damage: Fire 25%, others 10%; Resist: every school 30%".
    /// </summary>
    internal static string Describe(ServerWizGameStats stats) {
        var parts = new List<string>();
        void Add(string label, Func<string, float> value, Func<float, string> shown) {
            var bySchool = Schools().Select(s => (School: s, Value: value(s))).ToList();
            if (bySchool.Count == 0 || bySchool.All(s => Math.Abs(s.Value) < 0.0005f)) {
                return;
            }

            var common = bySchool.GroupBy(s => MathF.Round(s.Value, 3)).OrderByDescending(g => g.Count()).First().Key;
            var odd = bySchool.Where(s => MathF.Round(s.Value, 3) != common).ToList();
            var text = odd.Count == 0 ? $"every school {shown(common)}"
                : string.Join(", ", odd.Select(s => $"{s.School} {shown(s.Value)}"))
                  + (Math.Abs(common) < 0.0005f ? "" : $", others {shown(common)}");
            parts.Add($"{label}: {text}");
        }

        string Percent(float fraction) => $"{MathF.Round(fraction * 100f, 1)}%";
        string Number(float value) => $"{MathF.Round(value, 1)}";

        Add("Damage", s => CombatEffectApplicator.AsFraction(BySchool(stats.m_dmgBonusPercent, s) + stats.m_dmgBonusPercentAll), Percent);
        Add("Resist", s => CombatEffectApplicator.AsFraction(BySchool(stats.m_dmgReducePercent, s) + stats.m_dmgReducePercentAll), Percent);
        Add("Pierce", s => CombatEffectApplicator.AsFraction(BySchool(stats.m_apBonusPercent, s) + stats.m_apBonusPercentAll), Percent);
        Add("Flat damage", s => BySchool(stats.m_dmgBonusFlat, s) + stats.m_dmgBonusFlatAll, Number);
        Add("Flat resist", s => BySchool(stats.m_dmgReduceFlat, s) + stats.m_dmgReduceFlatAll, Number);
        Add("Accuracy", s => BySchool(stats.m_accBonusPercent, s) + stats.m_accBonusPercentAll, Percent);
        Add("Critical", s => BySchool(stats.m_criticalHitRatingBySchool, s) + stats.m_criticalHitRatingAll, Number);
        Add("Block", s => BySchool(stats.m_blockRatingBySchool, s) + stats.m_blockRatingAll, Number);
        if (stats.m_healBonusPercentAll != 0) {
            parts.Add($"Outgoing healing: {Percent(stats.m_healBonusPercentAll)}");
        }

        if (stats.m_healIncBonusPercentAll != 0) {
            parts.Add($"Incoming healing: {Percent(stats.m_healIncBonusPercentAll)}");
        }

        if (stats.m_powerPipBase + stats.m_powerPipBonusPercentAll != 0) {
            parts.Add($"Power pip chance: {Percent(stats.m_powerPipBase + stats.m_powerPipBonusPercentAll)}");
        }

        if (stats.m_startingPips > 0 || stats.m_startingPowerPips > 0) {
            parts.Add($"Starting pips: {stats.m_startingPips}, power pips: {stats.m_startingPowerPips}");
        }

        return string.Join("; ", parts);
    }

    private static IEnumerable<string> Schools() {
        foreach (var school in Enum.GetValues<MagicSchool>()) {
            if (school is not (MagicSchool.None or MagicSchool.Sun or MagicSchool.Star or MagicSchool.Moon)
                && MagicSchools.GetMagicSchool(school.ToString()) is not null) {
                yield return school.ToString();
            }
        }
    }

    private static float BySchool(List<float> list, string school) {
        var template = MagicSchools.GetMagicSchool(school);
        if (list is null || template is null || template.m_schoolIndex < 0 || template.m_schoolIndex >= list.Count) {
            return 0f;
        }

        return list[template.m_schoolIndex];
    }

}
