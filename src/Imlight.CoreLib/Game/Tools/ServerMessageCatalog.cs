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
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>
/// Every other message the server sends a player as a [Server Message] chat line or message box (Niduus, 2026-10-26:
/// "make sure all system notifications from the server are able to be configed and able to be turned on or off
/// individually or as a category"). The list itself (ServerMessageCatalog.Generated.cs) is written by
/// tools/server-messages from the server's source. The server matches each message it sends against it just before it
/// goes out (ServerMessages.cs), so these need no change where they are sent. Settings share announcements.json with the
/// Announcements lines. This file is shared by the server and the app.
/// </summary>
internal static partial class ServerMessageCatalog {

    /// <summary>The group for messages that are on no list: words made elsewhere, such as lists and summaries.</summary>
    internal const string OTHER_GROUP = "Other messages";

    /// <summary>Messages the server sent that are on no list, with how often, so the Manager can show them too.</summary>
    internal const string SEEN_FILE = "server-messages-seen.json";

    /// <summary>What a {field} of a listed message means: the tool cannot tell more than that.</summary>
    internal const string FILLED = "filled in by the server";

    internal static readonly AnnouncementLine[] All = Found();

    private static partial AnnouncementLine[] Found();

    private static readonly HashSet<string> s_groups = [.. All.Select(l => l.Group)];

    internal static bool IsMessageGroup(string group) => s_groups.Contains(group) || group == OTHER_GROUP;

    /// <summary>The key of a message on no list: its own words.</summary>
    internal static string SeenKey(string text)
        => "seen." + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..10].ToLowerInvariant();

    internal static string GroupHelp(string group) => group switch {
        "Battles" => "Lines a player gets in battle, such as a duel starting, a creature joining or a spell that could not be cast.",
        "Castle Magic" => "Lines about Castle Magic spells in houses.",
        "Commands" => "Replies to the dot commands a player types in chat, such as .backpack or .garden.",
        "Crafting" => "Lines a player gets while crafting.",
        "Events" => "Lines about events, the event button and Deckathalon portals (the event announcements are in the Events group above).",
        "Fishing" => "Lines a player gets while fishing.",
        "Friends, chat and teams" => "Lines about friends, groups, Team Up, Pixie Post and PvP ratings.",
        "Gardening" => "Lines a player gets while gardening.",
        "Guilds and raids" => "Lines about guild grounds, the museum, daily rewards and raid gates.",
        "Housing" => "Lines about houses: placing and picking up objects, the attic, vaults and teleporters.",
        "Items and backpack" => "Lines about the backpack, bank, equipment sets, gifts and jewels.",
        "Login and account" => "Lines when a player logs in, is renamed, or their account is in use elsewhere.",
        "Pet hatching" => "Lines a player gets while hatching pets.",
        "Pets" => "Lines about pets, pet games and eggs.",
        "Places and travel" => "Lines about going to other areas and houses.",
        "Quests and badges" => "Lines about quests, badges and Daily Assignments.",
        "Shops" => "Lines about buying and selling at shops, Majid and the Bazaar.",
        "Crown Shop" => "Lines about the Crown Shop.",
        "Imlight tools" => "Lines from Imlight's own tools.",
        OTHER_GROUP => "Messages put together from many pieces (lists, summaries, a pet's stats ...) that are on no list above. "
                       + "Turning this group off stops all of them. The ones the server has sent show below, so each can be changed too.",
        _ => "Lines the server sends a player.",
    };

    internal static string GroupIcon(string group) => group switch {
        "Battles" => "sword",
        "Castle Magic" => "star",
        "Commands" => "code",
        "Crafting" => "gear",
        "Events" => "star",
        "Fishing" => "drop",
        "Friends, chat and teams" => "users",
        "Gardening" => "sparkle",
        "Guilds and raids" => "users",
        "Housing" => "home",
        "Items and backpack" => "bag",
        "Login and account" => "user",
        "Pet hatching" => "paw",
        "Pets" => "paw",
        "Places and travel" => "map",
        "Quests and badges" => "list",
        "Shops" => "bag",
        "Crown Shop" => "coin",
        _ => "chat",
    };

}
