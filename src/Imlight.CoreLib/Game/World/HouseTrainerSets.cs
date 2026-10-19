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
/// Set trainers (Justin, 2026-10-19): every spell the game's own trainers teach, split by school into parts of at most
/// <see cref="SpellsPerTrainer"/>, one part per trainer tome ("Fire Trainer 1", "Fire Trainer 2", ...). A part never
/// takes spells of the next school. The tomes of a house are handed the parts in order along their row, starting at
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

    private static List<TrainerSet> BuildSets() {
        var sets = new List<TrainerSet>();
        try {
            // Every spell some trainer in the game teaches, once, at the lowest level any trainer asks for it.
            var spells = Imlight.CoreLib.WizardData.SpiralDB.NpcSpellInventories.Values
                .Where(inventory => inventory?.Spells is not null)
                .SelectMany(inventory => inventory.Spells)
                .GroupBy(entry => entry.TemplateID)
                .Select(group => (Id: group.Key, Level: group.Min(entry => entry.Level),
                    Spell: CoreObjectFactory.GetCoreTemplate(group.Key) as SpellTemplate))
                .Where(spell => spell.Spell is not null && !spell.Spell.m_Treasure)
                .Select(spell => (spell.Id, spell.Level, School: (string) spell.Spell.m_sMagicSchoolName ?? "Other",
                    Shown: ShownName(spell.Spell)))
                // The same card taught under two templates shows once.
                .GroupBy(spell => (spell.School, spell.Shown))
                .Select(group => group.OrderBy(spell => spell.Level).First())
                .ToList();

            var schools = spells.Select(spell => spell.School).Distinct()
                .OrderBy(school => Array.IndexOf(s_schoolOrder, school) is var index and >= 0 ? index : s_schoolOrder.Length)
                .ThenBy(school => school, StringComparer.OrdinalIgnoreCase);
            foreach (var school in schools) {
                var list = spells.Where(spell => spell.School == school)
                    .OrderBy(spell => spell.Level)
                    .ThenBy(spell => spell.Shown, StringComparer.OrdinalIgnoreCase)
                    .Select(spell => new NPCSpellEntry { TemplateID = spell.Id, RequiredSpellID = 0, Level = 1 })
                    .ToList();
                var name = string.IsNullOrWhiteSpace(school) ? "Other" : school;
                for (int start = 0, part = 1; start < list.Count; start += SpellsPerTrainer, part++) {
                    sets.Add(new TrainerSet($"{name} Trainer {part}", list.GetRange(start, Math.Min(SpellsPerTrainer, list.Count - start))));
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
