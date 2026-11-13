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
 * GAME BADGES
 * ========================================================================
 *
 * PURPOSE:
 * The game's own badge templates (Root.wad Badges/*.xml, BadgeTemplate), merged over SpiralDB's badge list.
 *
 * NOTE:
 * SpiralDB had 2,074 of the game's 2,308 badges (Niduus, 2026-11-13: "I think we are missing a FUCK ton of badges";
 * the badge book said 1511/1524 and showed two 0/0 PvP groups). The 234 it lacked (124 Ahmarra, 33 PvP ranks, 24 pet,
 * 22 event and more) now come from the game, and the game's own values win for the badges both have: 13 Battlegrounds
 * badges SpiralDB called normal are extra credit in the game (the 13 of 1511/1524), 7 Quartermaster badges are in
 * another group, and SpiralDB had no adjectives (so no kill counters), no requirements and no reward loot tables.
 * SpiralDB's English names and descriptions are kept; a badge it lacks gets its English text from the game's locale.
 * The game reads its own copy of these templates, so whatever this game version has is what the client shows.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.Cryptography;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Badges;

internal static class GameBadges {

    private const string TEMPLATE_FOLDER = "Badges/";

    /// <summary>
    /// SpiralDB's badges with the game's templates laid over them, by badge ID. Without Root.wad, SpiralDB's list as it is.
    /// </summary>
    internal static List<BadgeDefinition> Merge(IEnumerable<BadgeDefinition> spiralDb) {
        var merged = new Dictionary<uint, BadgeDefinition>();
        foreach (var definition in spiralDb.Where(d => d is not null)) {
            merged[definition.Id] = definition;
        }

        var templates = ReadTemplates();
        if (templates.Count == 0) {
            return [.. merged.Values];
        }

        int added = 0, updated = 0;
        foreach (var template in templates) {
            var name = template.m_badgeName.ToString();
            var id = StringHash.Compute(name);
            if (merged.TryGetValue(id, out var known)) {
                merged[id] = FromTemplate(template, id, name, known);
                updated++;
            }
            else {
                merged[id] = FromTemplate(template, id, name, null);
                added++;
            }
        }

        Logger.Information("Badges | {0} badge templates in the game files: {1} were missing from SpiralDB and were added, {2} updated.",
            Logger.Args(templates.Count, added, updated));

        return [.. merged.Values];
    }

    private static List<BadgeTemplate> ReadTemplates() {
        var templates = new List<BadgeTemplate>();
        try {
            foreach (var (_, data) in RootArchiveLoader.GetDirectoryStream(TEMPLATE_FOLDER)) {
                if (data is not { } bytes) {
                    continue;
                }

                try {
                    if (new BindSerializer().Deserialize<PropertyClass>(bytes.ToArray(), 1, out var parsed)
                        && parsed is BadgeTemplate template && !template.m_badgeName.IsEmpty) {
                        templates.Add(template);
                    }
                }
                catch (Exception ex) {
                    Logger.Warning("Badges | could not read a badge template: {0}", Logger.Args(ex.Message));
                }
            }
        }
        catch (Exception ex) {
            Logger.Error("Badges | reading the game's badge templates failed: {0}", Logger.Args(ex.Message));
        }

        return templates;
    }

    private static BadgeDefinition FromTemplate(BadgeTemplate template, uint id, string name, BadgeDefinition known) {
        var rewardTable = Text(template.m_rewardLootTable);
        var overcountTable = Text(template.m_overcountLootTable);

        return new BadgeDefinition {
            Id = id,
            Name = name,
            DisplayName = !string.IsNullOrEmpty(known?.DisplayName) ? known.DisplayName : English(template.m_badgeTitle, name),
            Description = !string.IsNullOrEmpty(known?.Description) ? known.Description : English(template.m_badgeInfo, ""),
            Title = Text(template.m_badgeTitle),
            Info = Text(template.m_badgeInfo),
            ProgressInfo = Text(template.m_badgeProgressInfo),
            Max = Math.Max(0, template.m_registryValue),
            Index = template.m_index,
            Outline = Text(template.m_badgeOutline),
            Image = Text(template.m_badgeImage),
            Dynamic = template.m_dynamicBadge,
            Filter = Text(template.m_badgeFilterName),
            ExtraCredit = template.m_extraCredit,
            ShowTitle = template.m_showTitle,
            Retired = known?.Retired ?? false,
            HasReward = !string.IsNullOrEmpty(rewardTable),
            RewardOverrideDescription = Text(template.m_rewardOverrideDescription),
            Overcount = template.m_overcount,
            OvercountInterval = template.m_overcountInterval,
            RewardLootTable = string.IsNullOrEmpty(rewardTable) ? null : rewardTable,
            OvercountLootTable = string.IsNullOrEmpty(overcountTable) ? null : overcountTable,
            RegistryName = string.IsNullOrEmpty(Text(template.m_registryName)) ? null : Text(template.m_registryName),
            Adjectives = [.. (template.m_badgeAdjectives ?? []).Select(Text).Where(a => a.Length > 0)],
            AutoAdd = template.m_autoAddBadge,
            Requirements = template.m_requirements,
            RequiredAutoAddBadges = [.. (template.m_requiredAutoAddBadgeList ?? []).Select(Text).Where(n => n.Length > 0).Select(StringHash.Compute)],
            CompleteResults = template.m_completeResults,
        };
    }

    private static string Text(ByteString value)
        => value.IsEmpty ? string.Empty : value.ToString();

    private static string English(ByteString key, string fallback) {
        var text = Text(key);
        if (text.Length == 0) {
            return fallback;
        }

        try {
            var shown = Locale.GetEnglishName(text);

            return string.IsNullOrWhiteSpace(shown) ? fallback : shown.Trim();
        }
        catch (Exception) {
            return fallback;
        }
    }

}
