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
 * CROWN SHOP GAME FILE ITEMS
 * ========================================================================
 *
 * PURPOSE:
 * The items the game files price in crowns that SpiralDB's Crown Shop list leaves out (hats, robes, boots, weapons,
 * rings, athames, furniture, gardening, castle blocks, bundles and more), read from
 * spiraldb-extra/CrownShop/GameFileItems.json (tools/crown-shop/make_crown_shop_items.py makes it).
 *
 * NOTE:
 * - Gear and bundles that come in level versions are one entry with every version; each wizard is shown the version
 *   for their level (the highest at or under it, else the lowest), as the real shop does. Every version can be bought.
 * - Items without a template in this game's files are left out.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.CrownShop;

public static class CrownShopGameItems {

    private const string FILE_NAME = "GameFileItems.json";

    private static readonly JsonSerializerOptions s_jsonOptions = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    public sealed class Entry {
        public ulong TemplateId { get; set; }
        public int Category { get; set; }
        public string Name { get; set; } = "";
        public int Level { get; set; }
        public int CrownsCost { get; set; }
        public int GoldCost { get; set; }
        public bool NoGift { get; set; }
    }

    public sealed class Versioned {
        public int Category { get; set; }
        public string Name { get; set; } = "";
        public List<Entry> Versions { get; set; } = [];

        /// <summary>The version for a wizard of this level: the highest at or under it, else the lowest.</summary>
        public Entry ForLevel(int level)
            => Versions.LastOrDefault(v => v.Level <= level) ?? Versions[0];
    }

    private sealed class FileData {
        public List<Entry> Items { get; set; } = [];
        public List<Versioned> Versioned { get; set; } = [];
    }

    /// <summary>Reads the file; both lists are empty when it is missing or broken.</summary>
    public static (List<Entry> Items, List<Versioned> Versioned) Load() {
        var path = Path.Combine(SpiralDB.ExtraPath, "CrownShop", FILE_NAME);
        if (!File.Exists(path)) {
            Logger.Information("CrownShop: no {0}, so only SpiralDB's list is sold.", Logger.Args(path));
            return ([], []);
        }

        try {
            using var stream = File.OpenRead(path);
            var data = JsonSerializer.Deserialize<FileData>(stream, s_jsonOptions) ?? new FileData();
            var items = data.Items.Where(e => CoreObjectFactory.HasTemplate(e.TemplateId)).ToList();
            var versioned = data.Versioned
                .Select(v => new Versioned {
                    Category = v.Category,
                    Name = v.Name,
                    Versions = [.. v.Versions.Where(e => CoreObjectFactory.HasTemplate(e.TemplateId)).OrderBy(e => e.Level)]
                })
                .Where(v => v.Versions.Count > 0)
                .ToList();
            foreach (var group in versioned) {
                group.Versions.ForEach(version => version.Category = group.Category);
            }

            Logger.Information("CrownShop: {0} items from the game files, and {1} more with level versions.",
                Logger.Args(items.Count, versioned.Count));

            return (items, versioned);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) {
            Logger.Warning("CrownShop: could not read {0}: {1}", Logger.Args(path, ex.Message));
            return ([], []);
        }
    }

}
