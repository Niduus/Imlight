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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Fishing;

/// <summary>One fish template, from its FishBehaviorTemplate (Root/ObjectData/Fish).</summary>
internal sealed record FishInfo(
    uint TemplateId,
    string Name,
    string School,
    int Rank,
    float MinSize,
    float MaxSize,
    float BaseLength,
    int Xp,
    int SalePrice,
    bool IsChest,
    bool IsTest) {

    /// <summary>The length the catch window shows: the base length times the size.</summary>
    internal float LengthOf(float size) => BaseLength * size;

    /// <summary>A small fry is in the smallest tenth of the sizes this fish comes in.</summary>
    internal bool IsSmallFry(float size) => MaxSize > MinSize && size <= MinSize + (MaxSize - MinSize) * 0.1f;

    /// <summary>A whopper is in the biggest tenth of the sizes this fish comes in.</summary>
    internal bool IsWhopper(float size) => MaxSize > MinSize && size >= MaxSize - (MaxSize - MinSize) * 0.1f;

}

/// <summary>A pond (BodyOfWater) of a zone, read from the zone's FishingInfo file.</summary>
internal sealed record FishingWater(int Index, float X, float Y, int FishCount, uint SecondsToRespawn, List<ZoneFish> Fish);

/// <summary>
/// What the game files say about fishing: the fish templates, the ponds of each zone (the zone archive's FishingInfo
/// file, which the client reads too), the fishing level table (FishingXPConfig in Root.wad) and the fishing spells.
/// Everything is read once and kept.
/// </summary>
internal static class FishingData {

    private static readonly ConcurrentDictionary<uint, FishInfo> s_fish = new();
    private static readonly ConcurrentDictionary<string, Task<List<FishingWater>>> s_waters = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<List<FishingLevelInfo>> s_levels = new(LoadLevels);
    private static readonly Lazy<List<(uint Id, FishingSpellTemplate Template)>> s_spells = new(LoadSpells);

    // Fishing files are small; anything bigger is a model, a texture or the zone's own data.
    private const int MaxScannedFileSize = 4 * 1024 * 1024;

    /// <summary>The fish with this template id, or null when the template is not a fish.</summary>
    internal static FishInfo Fish(uint templateId) {
        if (templateId == 0) {
            return null;
        }

        if (s_fish.TryGetValue(templateId, out var known)) {
            return known;
        }

        FishInfo info = null;
        try {
            if (CoreObjectFactory.GetCoreTemplate(templateId) is { } template) {
                var behavior = template.m_behaviors?.OfType<FishBehaviorTemplate>().FirstOrDefault();
                if (behavior is not null) {
                    var objectName = template is WizGameObjectTemplate wizTemplate ? wizTemplate.m_objectName.ToString() : "";
                    var displayKey = template is WizGameObjectTemplate named ? named.m_displayName.ToString() : "";
                    var name = string.IsNullOrEmpty(displayKey) ? "" : Locale.GetEnglishName(displayKey);
                    if (string.IsNullOrEmpty(name) || name == displayKey) {
                        name = objectName;
                    }

                    var holiday = behavior.m_holidayFlags.ToString() ?? "";
                    var minSize = behavior.m_minimumSize > 0 ? behavior.m_minimumSize : 0.5f;
                    var maxSize = behavior.m_maximumSize >= minSize ? behavior.m_maximumSize : minSize;
                    info = new FishInfo(
                        templateId,
                        name,
                        behavior.m_schoolName.ToString() ?? "",
                        behavior.m_rank,
                        minSize,
                        maxSize,
                        behavior.m_baseLength > 0 ? behavior.m_baseLength : 20f,
                        Math.Max(behavior.m_fishingXP, 1),
                        Math.Max(behavior.m_fishSalePrice, 0),
                        objectName.Contains("Chest", StringComparison.OrdinalIgnoreCase),
                        holiday.Contains("Test", StringComparison.OrdinalIgnoreCase));
                }
            }
        }
        catch (Exception e) {
            Logger.Error("Fishing: reading fish template {0} failed: {1}", Logger.Args(templateId, e.Message));
        }

        // A template that is not a fish is remembered too, so it is not read again.
        s_fish[templateId] = info;

        return info;
    }

    /// <summary>
    /// The ponds of a zone. The zone archive is read on a worker the first time; until then this gives null and
    /// <paramref name="ready"/> is false.
    /// </summary>
    internal static List<FishingWater> Waters(string zonePath, out bool ready) {
        ready = false;
        if (string.IsNullOrEmpty(zonePath)) {
            ready = true;

            return [];
        }

        var task = s_waters.GetOrAdd(zonePath, path => Task.Run(() => ReadWaters(path)));
        if (!task.IsCompleted) {
            return null;
        }

        ready = true;

        return task.IsCompletedSuccessfully ? task.Result : [];
    }

    private static List<FishingWater> ReadWaters(string zonePath) {
        try {
            // The zone is loading at the same time, so a busy archive cache gets a few more tries.
            Imcodec.Wad.Archive wad = null;
            for (var attempt = 0; attempt < 4 && wad is null; attempt++) {
                if (attempt > 0) {
                    Thread.Sleep(TimeSpan.FromSeconds(2));
                }

                ResourceManager.TryLoadArchive(zonePath, out wad);
            }

            if (wad is null) {
                Logger.Warning("Fishing: could not open the archive of zone {0}, so it has no fish.", Logger.Args(zonePath));

                return [];
            }

            // Files named for fishing first, then every other small data file of the zone.
            var names = wad.Files.Keys
                .OrderByDescending(name => name.Contains("fish", StringComparison.OrdinalIgnoreCase))
                .ThenBy(name => name, StringComparer.Ordinal)
                .ToList();
            foreach (var name in names) {
                if (name.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".kfm", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".kf", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                var bytes = wad.OpenFile(name);
                if (!bytes.HasValue || bytes.Value.Length < 4 || bytes.Value.Length > MaxScannedFileSize) {
                    continue;
                }

                var span = bytes.Value.Span;
                // Data files are BINd; the game's other files (scripts, layouts) are not property objects.
                if (span[0] != (byte) 'B' || span[1] != (byte) 'I' || span[2] != (byte) 'N' || span[3] != (byte) 'd') {
                    continue;
                }

                FishingInfo info = null;
                try {
                    if (new BindSerializer().Deserialize<PropertyClass>(bytes.Value.ToArray(), 1, out var parsed)) {
                        info = parsed as FishingInfo;
                    }
                }
                catch (Exception) {
                    // A file that is not a fishing file can fail anywhere in the decode.
                }

                if (info is null) {
                    continue;
                }

                var waters = new List<FishingWater>();
                var index = 0;
                foreach (var body in info.m_bodyOfWaterList ?? []) {
                    if (body is null) {
                        continue;
                    }

                    var fish = (body.m_zoneFishList ?? []).Where(f => f is not null && f.m_fishTemplateID != 0).ToList();
                    waters.Add(new FishingWater(index++, body.m_waterX, body.m_waterY, body.m_numberOfFish, body.m_secondsToRespawn, fish));
                }

                Logger.Information("Fishing: zone {0} has {1} pond(s) in {2}: {3}.", Logger.Args(zonePath, waters.Count, name,
                    string.Join("; ", waters.Select(w => $"pond {w.Index} at ({w.X:0},{w.Y:0}) {w.FishCount} fish of {w.Fish.Count} kinds, respawn {w.SecondsToRespawn}s"))));

                return waters;
            }

            Logger.Debug("Fishing: zone {0} has no fishing file.", Logger.Args(zonePath));
        }
        catch (Exception e) {
            Logger.Error("Fishing: reading the ponds of zone {0} failed: {1}", Logger.Args(zonePath, e));
        }

        return [];
    }

    /// <summary>The fishing levels, lowest first (FishingXPConfig; a made-up table when the game files have none).</summary>
    internal static List<FishingLevelInfo> Levels => s_levels.Value;

    private static List<FishingLevelInfo> LoadLevels() {
        try {
            var root = RootArchiveLoader.GetRootWad();
            var candidates = root?.Files.Keys
                .Where(name => name.Contains("Fish", StringComparison.OrdinalIgnoreCase)
                            && !name.StartsWith("ObjectData", StringComparison.OrdinalIgnoreCase)
                            && !name.StartsWith("Spells", StringComparison.OrdinalIgnoreCase))
                .ToList() ?? [];
            foreach (var name in candidates) {
                FishingXPConfig config = null;
                try {
                    config = RootArchiveLoader.GetFile<PropertyClass>(name) as FishingXPConfig;
                }
                catch (Exception) {
                    // Not a property file.
                }

                if (config?.m_levelInfo is { Count: > 0 } levels) {
                    var sorted = levels.Where(l => l is not null).OrderBy(l => l.m_level).ToList();
                    Logger.Information("Fishing: level table from {0}, max level {1}: {2}", Logger.Args(name, config.m_maxLevel,
                        string.Join(", ", sorted.Select(l => $"{l.m_level}={l.m_xpToLevel}"))));

                    return sorted;
                }
            }

            Logger.Warning("Fishing: no FishingXPConfig in Root.wad ({0} fishing files looked at); using a made-up level table.",
                Logger.Args(candidates.Count));
        }
        catch (Exception e) {
            Logger.Error("Fishing: reading the level table failed: {0}", Logger.Args(e.Message));
        }

        return Enumerable.Range(1, 50)
            .Select(level => new FishingLevelInfo { m_level = (byte) level, m_xpToLevel = 10 * level * level })
            .ToList();
    }

    /// <summary>The highest fishing level.</summary>
    internal static int MaxLevel => Levels.Count == 0 ? 1 : Levels.Max(l => l.m_level);

    /// <summary>
    /// The fishing level for this much fishing XP. Each level's m_xpToLevel is read as the total XP that level needs,
    /// so level 1 is where everyone starts.
    /// </summary>
    internal static int LevelAt(int xp) {
        var level = 1;
        foreach (var info in Levels) {
            if (info.m_level > level && xp >= info.m_xpToLevel) {
                level = info.m_level;
            }
        }

        return Math.Max(level, 1);
    }

    /// <summary>Every fishing spell (lures, reveals, winnows and the rest) with its template id.</summary>
    internal static List<(uint Id, FishingSpellTemplate Template)> Spells => s_spells.Value;

    private static List<(uint Id, FishingSpellTemplate Template)> LoadSpells() {
        var ids = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in CoreObjectFactory.TemplateManifest?.m_serializedTemplates ?? []) {
            string file = location?.m_filename;
            if (!string.IsNullOrEmpty(file) && location.m_id <= uint.MaxValue) {
                ids.TryAdd(file, (uint) location.m_id);
            }
        }

        var spells = new List<(uint, FishingSpellTemplate)>();
        foreach (var (path, template) in SpellFactory.AllSpellTemplates()) {
            if (template is FishingSpellTemplate fishing && ids.TryGetValue(path, out var id)) {
                spells.Add((id, fishing));
            }
        }

        Logger.Information("Fishing: {0} fishing spells.", Logger.Args(spells.Count));

        return spells;
    }

    /// <summary>The fishing spell with this template id, or null.</summary>
    internal static FishingSpellTemplate Spell(uint templateId)
        => Spells.FirstOrDefault(s => s.Id == templateId).Template;

    /// <summary>
    /// Spells every wizard should be able to cast: the Lesser lure of each school (CatchFish&lt;School&gt;1) and Reveal Fish.
    /// </summary>
    internal static IEnumerable<(uint Id, FishingSpellTemplate Template)> StarterSpells()
        => Spells.Where(s => {
            var name = s.Template.m_name.ToString() ?? "";

            return (name.StartsWith("CatchFish", StringComparison.Ordinal) && name.EndsWith('1') && !name.Contains("All"))
                || name == "RevealFish";
        });

    /// <summary>Spells no player should get: tests, CSR tools and pet-only copies.</summary>
    internal static bool IsPlayerSpell(FishingSpellTemplate template) {
        var name = template.m_name.ToString() ?? "";

        return !name.StartsWith("CSR", StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith("Pet -", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("Test", StringComparison.OrdinalIgnoreCase);
    }

}
