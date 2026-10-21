/*
 * Boss changes from the app's Bosses tab (Justin, 2026-10-21: "edit the bosses deck and stats"), read from
 * spiraldb-extra/BossOverrides/<template id>.json, one file per creature template. The game files are never changed.
 *
 * A creature's stats and deck are built when its zone loads (NpcComponent, StatsComponent, CombatCreatureEquipmentComponent
 * and CombatCreatureDeckComponent). The changes are laid over them each time the creature joins a battle, so a saved file
 * counts from the next battle, without a restart. A field left out (null) keeps what the game gives.
 *
 *   Health, Level, School    its most health, its level (crits and the level shown) and its school.
 *   Damage, Resist           percent for every school (20 = 20%). Replaces the game's value for every school.
 *   FlatDamage, FlatResist   hit points added to each hit it makes, or taken off each hit it takes, every school.
 *   Accuracy                 percent added to its spells' accuracy, every school.
 *   Critical, Block          critical and block rating, every school.
 *   OutgoingHealing, IncomingHealing   percent more healing it gives and gets.
 *   PowerPipChance           percent chance a new pip is a power pip.
 *   StartingPips, StartingPowerPips    the pips it starts the battle with.
 *   Deck                     the spells it casts, replacing its game deck when there is at least one. Copies: how many
 *                            times it can use the spell in one battle; 0 = it never runs out (as the game's creatures).
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
        public List<BossCard> Deck { get; set; }
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
        var file = Path.Combine(SpiralDB.ExtraPath, FOLDER, $"{templateId}.json");
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
                boss.Deck = boss.Deck?.Where(c => c is not null && c.SpellID != 0).ToList();
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

        // A school list set to null leaves only the every-school value. The damage and resist totals pass through
        // CombatEffectApplicator.AsFraction, which reads anything above 1.5 as percent.
        if (boss.Damage is { } damage) {
            copy.m_dmgBonusPercent = null;
            copy.m_dmgBonusPercentAll = ForAsFraction(damage);
        }

        if (boss.Resist is { } resist) {
            copy.m_dmgReducePercent = null;
            copy.m_dmgReducePercentAll = ForAsFraction(resist);
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

        if (boss.Critical is { } critical) {
            copy.m_criticalHitRatingBySchool = null;
            copy.m_criticalHitRatingAll = critical;
        }

        if (boss.Block is { } block) {
            copy.m_blockRatingBySchool = null;
            copy.m_blockRatingAll = block;
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

    // 20% is sent as 0.2, and more than 150% as the percent itself, so AsFraction turns both into the same fraction.
    private static float ForAsFraction(float percent)
        => Math.Abs(percent) > 150f ? percent : percent / 100f;

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
