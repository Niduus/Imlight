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
using System.Linq;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// The main worlds, in the order they are played (Niduus, 2026-11-15: "Make sure to mark main worlds: Wizard City,
/// Krokotopia, Marlybone, Mooshu, Dragonspyre, Celestia, Zafaria, Avalon, Azteca, Khyrsalis, Polaris, Mirage, Empyrea,
/// Karamelle, Lemuria, Novus, Wallaru, Darkmoor, Ahmarra (NEW, don't have the data for questing yet)"). The one list
/// Ultimus, the Quest Team, their routes and the story progress use. Ahmarra is shown (World 19 of 19) but no bot quests
/// there until it has questing data and it is taken off <see cref="s_noQuestingYet"/>.
/// </summary>
internal static class MainWorlds {

    /// <summary>The main worlds by zone folder, in order, with the name players use.</summary>
    internal static readonly (string Folder, string Name)[] All = [
        ("WizardCity", "Wizard City"), ("Krokotopia", "Krokotopia"), ("Marleybone", "Marleybone"), ("MooShu", "MooShu"),
        ("DragonSpire", "Dragonspyre"), ("Celestia", "Celestia"), ("Zafaria", "Zafaria"), ("Avalon", "Avalon"), ("Azteca", "Azteca"),
        ("Khrysalis", "Khrysalis"), ("Polaris", "Polaris"), ("Mirage", "Mirage"), ("Empyrea", "Empyrea"), ("Karamelle", "Karamelle"),
        ("Lemuria", "Lemuria"), ("Novus", "Novus"), ("Wallaru", "Wallaru"), ("Darkmoor", "Darkmoor"), ("Ahmarra", "Ahmarra"),
    ];

    // Main worlds without questing data yet: shown, never quested in by a bot.
    private static readonly HashSet<string> s_noQuestingYet = new(StringComparer.OrdinalIgnoreCase) { "Ahmarra" };

    /// <summary>The main worlds' zone folders, in order (the bots' ladder).</summary>
    internal static readonly string[] Folders = [.. All.Select(w => w.Folder)];

    /// <summary>The main worlds the bots quest in, in order (all but the ones without questing data yet).</summary>
    internal static readonly string[] Questable = [.. Folders.Where(f => !s_noQuestingYet.Contains(f))];

    /// <summary>The main worlds plus Arcanum (their hub), for where a bot on the story may go.</summary>
    internal static HashSet<string> WithHub() => new(Folders.Append("Arcanum"), StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether bots may quest in this world (a zone folder or a zone path).</summary>
    internal static bool QuestsIn(string zone) {
        var folder = (zone ?? "").Split('/')[0].Trim();
        return !s_noQuestingYet.Contains(folder);
    }

    /// <summary>The world's place in the main worlds (1 for Wizard City), or 0.</summary>
    internal static int Number(string folder)
        => Array.FindIndex(Folders, f => string.Equals(f, (folder ?? "").Split('/')[0].Trim(), StringComparison.OrdinalIgnoreCase)) + 1;

    /// <summary>The name players use for a main world's folder ("Dragonspyre" for DragonSpire), or the folder itself.</summary>
    internal static string Name(string folder)
        => All.FirstOrDefault(w => string.Equals(w.Folder, folder, StringComparison.OrdinalIgnoreCase)).Name ?? folder ?? "";

}
