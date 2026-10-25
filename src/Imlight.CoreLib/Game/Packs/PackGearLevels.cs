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
 * PACK GEAR LEVELS
 * ========================================================================
 *
 * Pack gear comes in one version per 10 levels (Armor of the Silent Knight has 14: level 0, 10, ... 130), and the
 * real game gives the version for the wizard's level. SpiralDB's pack lists hold one id per name, the level 0 one,
 * so every pack gave level 0 gear (Niduus, 2026-10-25). This swaps a piece for its version with the same name,
 * slot and school whose level is the highest at or under the wizard's.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Packs;

public static class PackGearLevels {

    private const int MAX_VERSIONS = 200;

    // The wearable kinds that come in level versions; a version must share the kind, as two items of one name can be
    // a hat and a robe.
    private static readonly string[] s_kinds = ["Hat", "Robe", "Shoes", "Weapon", "Athame", "Amulet", "Ring", "Deck"];

    private static readonly ConcurrentDictionary<uint, (int Level, uint Id)[]> s_versions = new();

    /// <summary>The id of the version of this gear for a wizard of this level, or the id itself.</summary>
    public static uint ForLevel(uint templateId, int wizardLevel) {
        if (CoreObjectFactory.GetCoreTemplate(templateId) is not WizItemTemplate item || Kind(item) == "") {
            return templateId;
        }

        var versions = Versions(templateId, item);
        if (versions.Length < 2) {
            return templateId;
        }

        var chosen = versions.LastOrDefault(v => v.Level <= wizardLevel);
        if (chosen.Id == 0) {
            chosen = versions[0];
        }

        if (chosen.Id != templateId) {
            Logger.Information("Booster pack: {0} ({1}) became the level {2} version {3} for a level {4} wizard.",
                Logger.Args(Name(item), templateId, chosen.Level, chosen.Id, wizardLevel));
        }

        return chosen.Id;
    }

    private static (int Level, uint Id)[] Versions(uint templateId, WizItemTemplate item) {
        if (s_versions.TryGetValue(templateId, out var cached)) {
            return cached;
        }

        if (!IdCatalog.IsReady) {
            Logger.Warning("Booster pack: the ID lookup is not ready yet, so {0} keeps its own level.", Logger.Args(templateId));
            return [];
        }

        var name = Name(item);
        var kind = Kind(item);
        string school = item.m_school;
        var versions = IdCatalog.Search(IdCatalogKind.Item, name, MAX_VERSIONS)
            .Where(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(entry => (Entry: entry, Template: CoreObjectFactory.GetCoreTemplate(entry.Id) as WizItemTemplate))
            .Where(v => v.Template is not null && Kind(v.Template) == kind
                && string.Equals((string) v.Template.m_school ?? "", school ?? "", StringComparison.OrdinalIgnoreCase))
            .Select(v => (Level: RequiredLevel(v.Template), Id: (uint) v.Entry.Id))
            // Two versions at one level: keep the one the pack named, else the lowest id.
            .GroupBy(v => v.Level)
            .Select(g => g.FirstOrDefault(v => v.Id == templateId) is { Id: > 0 } own ? own : g.MinBy(v => v.Id))
            .OrderBy(v => v.Level)
            .ToArray();

        // Every version shares one list, so the next piece of the set is found at once.
        foreach (var version in versions) {
            s_versions[version.Id] = versions;
        }
        s_versions[templateId] = versions;

        return versions;
    }

    /// <summary>The level the item needs to be worn; the level 0 version has none.</summary>
    private static int RequiredLevel(WizItemTemplate item)
        => (item.m_equipRequirements?.m_requirements ?? [])
            .OfType<ReqMagicLevel>()
            .Where(requirement => !requirement.m_applyNOT)
            .Select(requirement => (int) requirement.m_numericValue)
            .DefaultIfEmpty(0)
            .Max();

    private static string Kind(WizItemTemplate item)
        => (item.m_adjectiveList ?? []).Select(a => (string) a)
            .FirstOrDefault(a => s_kinds.Contains(a, StringComparer.OrdinalIgnoreCase)) ?? "";

    private static string Name(WizItemTemplate item) {
        string key = item.m_displayName;
        var shown = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);

        return string.IsNullOrWhiteSpace(shown) ? (string) item.m_objectName ?? "" : shown;
    }

}
