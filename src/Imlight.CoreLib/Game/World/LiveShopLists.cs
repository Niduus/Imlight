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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.World;

/// <summary>
/// One shop of a shop keeper as the live game has it: its title (a game text key or plain words), its currency
/// (WizShopOffering m_shopType), its kind (m_furnitureShop, which picks the window), and what it sells: items by
/// template id, or recipes by name.
/// </summary>
internal sealed record LiveShop(string Title, int ShopType, int Kind, IReadOnlyList<uint> Items, IReadOnlyList<string> Recipes);

/// <summary>
/// The live game's shops of the shop keepers whose own shop list in the game files is empty (Sir Nigel
/// Higgenbottom, the Mirage and Darkmoor vendors...), read from spiraldb-extra/Shops/live-shop-lists.json (from
/// Wizard404, 2026-10-25). Some of them run more than one shop ("Housing Shop" and "Equipment Shop"); each is
/// its own option and window, as live.
/// </summary>
internal static class LiveShopLists {

    private static readonly Lazy<IReadOnlyDictionary<uint, IReadOnlyList<LiveShop>>> s_shops = new(Load);

    /// <summary>The shops of the NPC with this template id, or null.</summary>
    internal static IReadOnlyList<LiveShop> For(uint templateId)
        => s_shops.Value.TryGetValue(templateId, out var shops) ? shops : null;

    private static IReadOnlyDictionary<uint, IReadOnlyList<LiveShop>> Load() {
        var path = Path.Combine(SpiralDB.ExtraPath, "Shops", "live-shop-lists.json");
        var result = new Dictionary<uint, IReadOnlyList<LiveShop>>();
        try {
            if (!File.Exists(path)) {
                Logger.Warning("Live shop lists: {0} is missing.", Logger.Args(path));

                return result;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var npc in document.RootElement.EnumerateObject()) {
                if (!uint.TryParse(npc.Name, out var npcId)) {
                    continue;
                }

                result[npcId] = [.. npc.Value.GetProperty("Shops").EnumerateArray().Select(shop => new LiveShop(
                    shop.TryGetProperty("Title", out var title) ? title.GetString() ?? "" : "",
                    shop.TryGetProperty("ShopType", out var type) ? type.GetInt32() : 0,
                    shop.TryGetProperty("Kind", out var kind) ? kind.GetInt32() : 0,
                    [.. shop.GetProperty("Items").EnumerateArray().Select(item => item.GetUInt32())],
                    [.. shop.GetProperty("Recipes").EnumerateArray().Select(recipe => recipe.GetString() ?? "")]))];
            }

            Logger.Information("Live shop lists: {0} shop keepers.", Logger.Args(result.Count));
        }
        catch (Exception e) {
            Logger.Error("Live shop lists: could not read {0}: {1}", Logger.Args(path, e.Message));
        }

        return result;
    }

}
