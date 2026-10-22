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
using System.IO;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Housing;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.World;

/// <summary>
/// Set trainers (Niduus, 2026-10-19): every spell the game's own trainers teach, plus Merle's boss spells and the dev
/// cards, split by kind (attacks, shields, traps, mantles...) into parts of at most <see cref="SpellsPerTrainer"/>, one
/// part per trainer tome ("Shield Trainer 1", "Shield Trainer 2", ...). A part never takes spells of the next kind. The tomes of a house are handed the parts in order along their row, starting at
/// the end where the first tome was placed; `.hall flip` starts from the other end. Which tome holds which part is
/// saved in hall-trainers.txt by house and tome, so it stays put. Tomes past the last part keep teaching everything.
/// </summary>
internal static class HouseTrainerSets {

    /// <summary>The most spells one trainer offers; the game drops the connection when an NPC offers hundreds.</summary>
    internal const int SpellsPerTrainer = 50;

    private const string FILE = "hall-trainers.txt";

    internal sealed record TrainerSet(string Name, List<NPCSpellEntry> Spells);

    private static readonly string[] s_schoolOrder = [
        "Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance", "Sun", "Moon", "Star", "Shadow",
    ];

    private static readonly Lazy<List<TrainerSet>> s_sets = new(BuildSets);

    // Which part each spawned trainer teaches, by its zone tag.
    private static readonly ConcurrentDictionary<string, int> s_setByTag = new();

    private static readonly object s_lock = new();
    // By house lot: which part each tome (by its id in the house) holds.
    private static Dictionary<ulong, Dictionary<uint, int>> s_saved;
    // Houses whose row is handed out from the other end.
    private static HashSet<ulong> s_flipped;

    internal static IReadOnlyList<TrainerSet> Sets => s_sets.Value;

    /// <summary>The part a trainer with this zone tag teaches, or null for a tome past the last part.</summary>
    internal static TrainerSet SetFor(string zoneTag)
        => zoneTag is not null && s_setByTag.TryGetValue(zoneTag, out var index) && index >= 0 && index < Sets.Count
            ? Sets[index]
            : null;

    /// <summary>
    /// Hands the house's tomes their parts: a tome keeps the part it already holds, the others get the free parts in
    /// row order. Then notes, by each tome's trainer tag, which part it teaches.
    /// </summary>
    /// <param name="house">The house.</param>
    /// <param name="tagFor">The trainer tag of the tome in a slot (by its GIDID).</param>
    internal static void Assign(HouseState house, Func<uint, string> tagFor) {
        if (house is null) {
            return;
        }

        var snapshot = house.Snapshot();
        var tomes = new List<(uint GidId, PlacedItem Item)>();
        for (var slot = 0; slot < snapshot.Slots.Count; slot++) {
            if (snapshot.Slots[slot] is { } item && HouseTrainers.IsTrainerItem(item.TemplateId)) {
                tomes.Add((unchecked(snapshot.GidBase + (uint) slot), item));
            }
        }

        var sets = Sets;
        lock (s_lock) {
            Load();
            if (!s_saved.TryGetValue(house.LotGid, out var saved)) {
                s_saved[house.LotGid] = saved = [];
            }

            var changed = false;
            var live = tomes.Select(tome => tome.Item.Id).ToHashSet();
            foreach (var id in saved.Keys.ToList()) {
                if (!live.Contains(id) || saved[id] >= sets.Count) {
                    saved.Remove(id);
                    changed = true;
                }
            }

            var used = saved.Values.ToHashSet();
            var next = 0;
            foreach (var (_, item) in RowOrder(tomes, s_flipped.Contains(house.LotGid))) {
                if (saved.ContainsKey(item.Id)) {
                    continue;
                }

                while (used.Contains(next)) {
                    next++;
                }

                if (next >= sets.Count) {
                    break;
                }

                saved[item.Id] = next;
                used.Add(next);
                changed = true;
            }

            if (changed) {
                Save();
            }

            foreach (var (gidId, item) in tomes) {
                s_setByTag[tagFor(gidId)] = saved.TryGetValue(item.Id, out var index) ? index : -1;
            }

            if (tomes.Count < sets.Count) {
                Logger.Information("House {0} has {1} trainer tomes; all {2} trainer parts need {2}.",
                    Logger.Args(house.LotGid, tomes.Count, sets.Count));
            }
        }
    }

    /// <summary>
    /// `.hall flip`: hands the house's row out again, from the other end.
    /// </summary>
    /// <returns>True when the row now starts from the end opposite the first placed tome.</returns>
    internal static bool Flip(ulong lotGid) {
        lock (s_lock) {
            Load();
            var flipped = s_flipped.Add(lotGid) || !s_flipped.Remove(lotGid);
            s_saved.Remove(lotGid);
            Save();

            return flipped;
        }
    }

    /// <summary>
    /// The tomes in order along their row. The row's direction is its longest spread; it starts at the end nearer the
    /// first tome placed (the lowest id), or the other end when flipped.
    /// </summary>
    private static List<(uint GidId, PlacedItem Item)> RowOrder(List<(uint GidId, PlacedItem Item)> tomes, bool flipped) {
        if (tomes.Count < 2) {
            return tomes;
        }

        var meanX = tomes.Average(t => t.Item.X);
        var meanY = tomes.Average(t => t.Item.Y);
        double sxx = 0, syy = 0, sxy = 0;
        foreach (var (_, item) in tomes) {
            var dx = item.X - meanX;
            var dy = item.Y - meanY;
            sxx += dx * dx;
            syy += dy * dy;
            sxy += dx * dy;
        }

        var angle = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        var (dirX, dirY) = (Math.Cos(angle), Math.Sin(angle));
        double Along(PlacedItem item) => (item.X - meanX) * dirX + (item.Y - meanY) * dirY;

        var ordered = tomes.OrderBy(t => Along(t.Item)).ThenBy(t => t.Item.Id).ToList();
        var first = tomes.MinBy(t => t.Item.Id).Item;
        var startsAtFirst = Along(first) <= Along(ordered[ordered.Count / 2].Item);
        if (startsAtFirst == flipped) {
            ordered.Reverse();
        }

        return ordered;
    }

    // The kinds of trainer, in the order they go down the row (Niduus, 2026-10-19: "Trainers with Shields, Mantle,
    // Traps and so on. Boss cards are their own along with dev cards").
    private static readonly string[] s_kindOrder = [
        "Attack", "All-Enemy Attack", "Heal", "Blade", "Shield", "Trap", "Mantle & Weakness", "Aura", "Global",
        "Minion", "Polymorph", "Stun & Control", "Manipulation", "Enchantment", "Other", "Weaving", "Boss", "Dev",
    ];

    private static List<TrainerSet> BuildSets() {
        var sets = new List<TrainerSet>();
        try {
            // Every spell some trainer in the game teaches, once, at the lowest level any trainer asks for it.
            var trained = Imlight.CoreLib.WizardData.SpiralDB.NpcSpellInventories.Values
                .Where(inventory => inventory?.Spells is not null)
                .SelectMany(inventory => inventory.Spells)
                .GroupBy(entry => entry.TemplateID)
                .Select(group => (Id: group.Key, Level: group.Min(entry => entry.Level),
                    Spell: CoreObjectFactory.GetCoreTemplate(group.Key) as SpellTemplate))
                .Where(spell => spell.Spell is not null && !spell.Spell.m_Treasure)
                .Select(spell => (spell.Id, spell.Level, spell.Spell, Kind: KindOf(spell.Spell)));

            // Merle's boss spells, and the developers' cards, are trainers of their own.
            var boss = Imlight.CoreLib.Game.Zone.Components.InteractTrainerComponent
                .TestSpellsOf(Imlight.CoreLib.Game.Zone.Components.InteractTrainerComponent.TestTrainerKind.Boss)
                .Select(entry => (Id: entry.TemplateID, Level: 0, Spell: CoreObjectFactory.GetCoreTemplate(entry.TemplateID) as SpellTemplate, Kind: "Boss"))
                .Where(spell => spell.Spell is not null);

            // The weaving (formula) spells, trained or boss, are trainers of their own (Niduus).
            var weaving = WeavingIds();
            var spells = trained.Concat(boss).Concat(DevSpells())
                .Select(spell => weaving.Contains((uint) spell.Id) ? spell with { Kind = "Weaving" } : spell)
                .Select(spell => (spell.Id, spell.Level, spell.Kind, School: (string) spell.Spell.m_sMagicSchoolName ?? "",
                    Shown: ShownName(spell.Spell)))
                // A card shows once: the trained copy before the boss copy before the dev copy.
                .GroupBy(spell => (spell.School, spell.Shown))
                .Select(group => group.OrderBy(spell => KindRank(spell.Kind)).ThenBy(spell => spell.Level).First())
                .ToList();

            foreach (var kind in spells.Select(spell => spell.Kind).Distinct().OrderBy(KindRank)) {
                // School by school (Fire first), then by level and name.
                var list = spells.Where(spell => spell.Kind == kind)
                    .OrderBy(spell => SchoolRank(spell.School))
                    .ThenBy(spell => spell.Level)
                    .ThenBy(spell => spell.Shown, StringComparer.OrdinalIgnoreCase)
                    .Select(spell => new NPCSpellEntry { TemplateID = spell.Id, RequiredSpellID = 0, Level = 1 })
                    .ToList();
                for (int start = 0, part = 1; start < list.Count; start += SpellsPerTrainer, part++) {
                    sets.Add(new TrainerSet($"{kind} Trainer {part}", list.GetRange(start, Math.Min(SpellsPerTrainer, list.Count - start))));
                }
            }
        }
        catch (Exception e) {
            Logger.Error("Could not build the set trainers: {0}", Logger.Args(e));
        }

        Logger.Information("Set trainers: {0} parts ({1}).",
            Logger.Args(sets.Count, string.Join(", ", sets.Select(set => $"{set.Name} {set.Spells.Count}"))));

        return sets;
    }

    private static int KindRank(string kind) => Array.IndexOf(s_kindOrder, kind) is var index and >= 0 ? index : s_kindOrder.Length;

    private static int SchoolRank(string school) => Array.IndexOf(s_schoolOrder, school) is var index and >= 0 ? index : s_schoolOrder.Length;

    private static readonly HashSet<kSpellEffects> s_damage = [
        kSpellEffects.kDamage, kSpellEffects.kDamageNoCrit, kSpellEffects.kStealHealth, kSpellEffects.kDamageOverTime,
        kSpellEffects.kDeferredDamage, kSpellEffects.kDamagePerTotalPipPower, kSpellEffects.kMaxHealthDamage,
        kSpellEffects.kDetonateOverTime, kSpellEffects.kInstantKill,
    ];
    private static readonly HashSet<kSpellEffects> s_heal = [
        kSpellEffects.kHeal, kSpellEffects.kHealPercent, kSpellEffects.kSetHealPercent, kSpellEffects.kHealOverTime,
        kSpellEffects.kMaxHealthHeal, kSpellEffects.kHealByWard,
    ];
    private static readonly HashSet<kSpellEffects> s_ward = [
        kSpellEffects.kModifyIncomingDamage, kSpellEffects.kModifyIncomingDamageFlat, kSpellEffects.kMaximumIncomingDamage,
        kSpellEffects.kModifyIncomingDamageType, kSpellEffects.kAbsorbDamage, kSpellEffects.kBounceNext,
        kSpellEffects.kBouncePrevious, kSpellEffects.kBounceBack, kSpellEffects.kBounceAll, kSpellEffects.kModifyIncomingHeal,
        kSpellEffects.kModifyIncomingHealFlat, kSpellEffects.kModifyIncomingArmorPiercing, kSpellEffects.kCritBlock,
        kSpellEffects.kStunResist, kSpellEffects.kCloakedWard, kSpellEffects.kIntercept,
    ];
    private static readonly HashSet<kSpellEffects> s_charm = [
        kSpellEffects.kModifyOutgoingDamage, kSpellEffects.kModifyOutgoingDamageFlat, kSpellEffects.kModifyOutgoingHeal,
        kSpellEffects.kModifyOutgoingHealFlat, kSpellEffects.kModifyOutgoingDamageType, kSpellEffects.kModifyOutgoingArmorPiercing,
        kSpellEffects.kModifyAccuracy, kSpellEffects.kCritBoost, kSpellEffects.kCloakedCharm, kSpellEffects.kModifyPowerPipChance,
    ];
    private static readonly HashSet<kSpellEffects> s_control = [
        kSpellEffects.kStun, kSpellEffects.kConfusion, kSpellEffects.kMindControl, kSpellEffects.kPacify, kSpellEffects.kTaunt,
    ];
    private static readonly HashSet<kEffectTarget> s_enemyTargets = [
        kEffectTarget.kEnemySingle, kEffectTarget.kEnemyTeam, kEffectTarget.kEnemyTeamAllAtOnce, kEffectTarget.kAtLeastOneEnemy,
        kEffectTarget.kPreselectedEnemySingle, kEffectTarget.kMultiTargetEnemy, kEffectTarget.kEnemyMinion,
    ];
    private static readonly HashSet<kEffectTarget> s_allEnemies = [
        kEffectTarget.kEnemyTeam, kEffectTarget.kEnemyTeamAllAtOnce, kEffectTarget.kMultiTargetEnemy,
    ];

    /// <summary>What kind of trainer teaches a spell, from what the spell does (the first that fits, in this order).</summary>
    private static string KindOf(SpellTemplate spell) {
        string type = spell.m_sTypeName;
        type ??= "";
        var leaves = Imlight.CoreLib.Game.Combat.CombatEnchantments.Leaves(spell.m_effects).ToList();
        bool Has(HashSet<kSpellEffects> kinds) => leaves.Any(effect => kinds.Contains(effect.m_effectType));
        bool OnEnemy(HashSet<kSpellEffects> kinds) => leaves.Any(effect => kinds.Contains(effect.m_effectType) && s_enemyTargets.Contains(effect.m_effectTarget));

        if (leaves.Count > 0 && Imlight.CoreLib.Game.Combat.CombatEnchantments.IsEnchantment(spell) || type == "Enchantment") {
            return "Enchantment";
        }

        if (leaves.Any(effect => effect.m_effectType is kSpellEffects.kSummonCreature or kSpellEffects.kSpawnCreature)) {
            return "Minion";
        }

        if (type == "Polymorph" || leaves.Any(effect => effect.m_effectType == kSpellEffects.kPolymorph)) {
            return "Polymorph";
        }

        if (type == "Global" || leaves.Any(effect => effect.m_effectTarget == kEffectTarget.kGlobal)) {
            return "Global";
        }

        if (type == "Aura") {
            return "Aura";
        }

        if (Has(s_damage)) {
            return leaves.Any(effect => s_damage.Contains(effect.m_effectType) && s_allEnemies.Contains(effect.m_effectTarget))
                ? "All-Enemy Attack"
                : "Attack";
        }

        if (Has(s_heal)) {
            return "Heal";
        }

        if (Has(s_ward)) {
            return OnEnemy(s_ward) ? "Trap" : "Shield";
        }

        if (Has(s_charm)) {
            return OnEnemy(s_charm) ? "Mantle & Weakness" : "Blade";
        }

        if (Has(s_control)) {
            return "Stun & Control";
        }

        return leaves.Count > 0 ? "Manipulation" : "Other";
    }

    // The game keeps them in Spells/Trained Spells/Weaving Spells and Spells/Fight Specific/Weaving.
    private static HashSet<uint> WeavingIds() {
        var ids = new HashSet<uint>();
        foreach (var location in CoreObjectFactory.TemplateManifest?.m_serializedTemplates ?? []) {
            string file = location?.m_filename;
            if (file is not null && file.Replace('\\', '/').Contains("Spells/", StringComparison.OrdinalIgnoreCase)
                && file.Replace('\\', '/').Contains("/Weaving", StringComparison.OrdinalIgnoreCase)) {
                ids.Add(location.m_id);
            }
        }

        Logger.Information("Set trainers: {0} weaving spells.", Logger.Args(ids.Count));

        return ids;
    }

    // Cards the developers made for testing: in a Test folder, or named Dev, Debug, GM or Admin.
    private static readonly System.Text.RegularExpressions.Regex s_devName
        = new(@"(^|[^A-Za-z])(Dev|DEV|Debug|DEBUG|GM|Admin|ADMIN)([^a-z]|$)");

    private static IEnumerable<(ulong Id, int Level, SpellTemplate Spell, string Kind)> DevSpells() {
        var templateIds = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in CoreObjectFactory.TemplateManifest?.m_serializedTemplates ?? []) {
            templateIds.TryAdd(location.m_filename, location.m_id);
        }

        var found = new List<(ulong, int, SpellTemplate, string)>();
        foreach (var (path, spell) in Imlight.CoreLib.Game.Spells.SpellFactory.AllSpellTemplates()) {
            string name = spell.m_name;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty((string) spell.m_displayName)
                || Array.IndexOf(s_schoolOrder, (string) spell.m_sMagicSchoolName ?? "") < 0
                || !templateIds.TryGetValue(path, out var id)) {
                continue;
            }

            var inTestFolder = path.Replace('\\', '/').Contains("/Test/", StringComparison.OrdinalIgnoreCase)
                || path.Replace('\\', '/').StartsWith("Test/", StringComparison.OrdinalIgnoreCase);
            if (inTestFolder || s_devName.IsMatch(name)) {
                found.Add((id, 0, spell, "Dev"));
            }
        }

        Logger.Information("Set trainers: {0} dev cards ({1}).",
            Logger.Args(found.Count, string.Join(", ", found.Take(40).Select(spell => (string) spell.Item3.m_name))));

        return found;
    }

    private static string ShownName(SpellTemplate spell) {
        string key = spell.m_displayName;
        var shown = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);

        return string.IsNullOrWhiteSpace(shown) ? (string) spell.m_name ?? "" : shown;
    }

    private static void Load() {
        if (s_saved is not null) {
            return;
        }

        s_saved = [];
        s_flipped = [];
        try {
            if (!File.Exists(FILE)) {
                return;
            }

            // "lot flip", or "lot tome part".
            foreach (var line in File.ReadAllLines(FILE)) {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[1] == "flip" && ulong.TryParse(parts[0], out var flippedLot)) {
                    s_flipped.Add(flippedLot);
                }
                else if (parts.Length == 3 && ulong.TryParse(parts[0], out var lot) && uint.TryParse(parts[1], out var tome)
                         && int.TryParse(parts[2], out var set)) {
                    if (!s_saved.TryGetValue(lot, out var tomes)) {
                        s_saved[lot] = tomes = [];
                    }

                    tomes[tome] = set;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Warning("Could not read {0}: {1}", Logger.Args(FILE, ex.Message));
        }
    }

    private static void Save() {
        try {
            File.WriteAllLines(FILE, s_flipped.Select(lot => $"{lot} flip")
                .Concat(s_saved.SelectMany(house => house.Value.Select(tome => $"{house.Key} {tome.Key} {tome.Value}"))));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Warning("Could not save {0}: {1}", Logger.Args(FILE, ex.Message));
        }
    }

}
