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

using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;

namespace Imlight.Setup.Blocks;

/// <summary>
/// A coloured group of blocks (Niduus, 2026-11-19: "Get more in detail with cheat blocks so it's more than just 4 blocks";
/// he picked "Split into ~12 colored kinds"). The four kinds (When, Passive, If, Do) still say where a block goes in a
/// cheat; a group only says what it is about, with its own colour and its own heading in the palette, so the 90 Do blocks
/// are no longer one long purple list.
/// </summary>
internal sealed record BlockGroup(string Key, BlockKind Kind, string Title, string Legend, IBrush Fill, IBrush Edge) {

    private static SolidColorBrush Rgb(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));

    private static BlockGroup Make(string key, BlockKind kind, string title, string legend, (byte R, byte G, byte B) fill)
        => new(key, kind, title, legend, Rgb(fill.R, fill.G, fill.B),
            Rgb((byte) (fill.R * 0.74), (byte) (fill.G * 0.74), (byte) (fill.B * 0.74)));

    // The groups in palette order: a cheat's hat, its Ifs, then its Dos.
    internal static readonly BlockGroup WHEN = new("when", BlockKind.When, "When… (what sets it off)", "When… what sets it off", BlockColors.WHEN, BlockColors.WHEN_EDGE);
    internal static readonly BlockGroup PASSIVE = new("passive", BlockKind.Passive, "Passive… (always, no When)", "Passive… always true", BlockColors.PASSIVE, BlockColors.PASSIVE_EDGE);
    internal static readonly BlockGroup WHO = Make("who", BlockKind.If, "Who… (which wizards)", "Who… which wizards", (20, 160, 170));
    internal static readonly BlockGroup LIMIT = Make("limit", BlockKind.If, "Limits… (how often)", "Limits… how often, which rounds", (96, 125, 139));
    internal static readonly BlockGroup IF = new("if", BlockKind.If, "If… (only when)", "If… only when something is true", BlockColors.IF, BlockColors.IF_EDGE);
    internal static readonly BlockGroup CAST = new("cast", BlockKind.Do, "Cast… (spells)", "Cast… spells", BlockColors.DO, BlockColors.DO_EDGE);
    internal static readonly BlockGroup HIT = Make("hit", BlockKind.Do, "Hit & Heal… (health)", "Hit & Heal… health", (214, 69, 80));
    internal static readonly BlockGroup EFFECTS = Make("effects", BlockKind.Do, "Effects… (blades, traps, shields, auras)", "Effects… blades, traps, shields", (194, 84, 168));
    internal static readonly BlockGroup PIPS = Make("pips", BlockKind.Do, "Pips & Cards…", "Pips & Cards…", (226, 120, 40));
    internal static readonly BlockGroup BATTLE = Make("battle", BlockKind.Do, "Battle… (turns, stuns, circles)", "Battle… turns, stuns, circles", (93, 107, 214));
    internal static readonly BlockGroup SHOW = Make("show", BlockKind.Do, "Show… (words and sounds)", "Show… words and sounds", (60, 150, 90));
    internal static readonly BlockGroup MEMORY = Make("memory", BlockKind.Do, "Memory… (variables)", "Memory… variables", (150, 120, 70));
    internal static readonly BlockGroup FLOW = Make("flow", BlockKind.Do, "Flow… (wait, repeat, otherwise, stop)", "Flow… wait, repeat, otherwise, stop", (110, 110, 125));

    internal static readonly IReadOnlyList<BlockGroup> ALL = [WHEN, PASSIVE, WHO, LIMIT, IF, CAST, HIT, EFFECTS, PIPS, BATTLE, SHOW, MEMORY, FLOW];

    /// <summary>The plain group of a kind (for a block no group names).</summary>
    internal static BlockGroup Of(BlockKind kind) => kind switch {
        BlockKind.When => WHEN, BlockKind.Passive => PASSIVE, BlockKind.If => IF, _ => CAST,
    };

    internal static BlockGroup? ByKey(string key) => ALL.FirstOrDefault(g => g.Key == key);

}
