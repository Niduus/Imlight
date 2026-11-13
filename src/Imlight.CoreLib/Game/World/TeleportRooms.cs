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
 *
 * ========================================================================
 * TELEPORT ROOMS
 * ========================================================================
 *
 * PURPOSE:
 * Which rooms of dungeons, towers and other instances can be teleported into (Niduus, 2026-11-04: "allow for a
 * instance, dungeon and tower instances section of the UI so I and others can choose what rooms/areas are able to be
 * TP into"), from the app's Game Rules > Teleport Rooms page, read from spiraldb-extra/Customs/TeleportRooms.json:
 *   Default   the choice of every dungeon or instance room with none of its own: "Game", "Open" or "Closed".
 *   Rooms     zone path to its own choice, such as "Azteca/Interiors/AZ_Z01_BlackSunPyramid_Room05": "Closed". Any
 *             zone can have one, not only instance rooms.
 *   AskBeforeFriendTeleport  true (the default): teleporting to a friend from the friends list asks "Do you want to go
 *             to your friend?" first, so one stray click never teleports (Niduus, 2026-11-13).
 * The choices:
 *   Game      as before: .tp, the app's step buttons and quest Go buttons work, and nobody teleports to a friend or
 *             group mate inside it (as on live).
 *   Open      also friends and group mates can teleport in to someone inside: they join that player's copy.
 *   Closed    nobody can teleport in: not .tp, not the step or quest buttons, not to a friend, not a Mark and Recall.
 *             Its doors, sigils and Dungeon Recall still work, so it is only reached by walking in.
 * A move inside the zone the player is already in is never refused. The file is watched: a change counts at once.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 11/04/2026
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Imlight.Common;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.World;

internal enum TeleportRule {
    Game,
    Open,
    Closed,
}

internal static class TeleportRooms {

    internal sealed class Rules {
        public string Default { get; set; } = nameof(TeleportRule.Game);
        public Dictionary<string, string> Rooms { get; set; } = [];
        public bool AskBeforeFriendTeleport { get; set; } = true;
    }

    private sealed record State(TeleportRule Default, Dictionary<string, TeleportRule> Rooms, bool AskBeforeFriendTeleport);

    private const string FOLDER = "Customs";
    internal const string FILE = "TeleportRooms.json";
    private const int RELOAD_DELAY_IN_MS = 400;

    /// <summary>What a player is told when they cannot teleport into a room.</summary>
    internal const string CLOSED_MESSAGE
        = "Teleporting into that place is turned off on this server. Walk in through its door or sigil instead.";

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly State s_empty = new(TeleportRule.Game, new(StringComparer.OrdinalIgnoreCase), AskBeforeFriendTeleport: true);
    private static readonly object s_lock = new();
    private static State? s_state;
    private static FileSystemWatcher? s_watcher;
    private static Timer? s_reloadTimer;

    private static State Current {
        get {
            var state = s_state;
            if (state is not null) {
                return state;
            }

            lock (s_lock) {
                if (s_state is null) {
                    s_state = Read();
                    Watch();
                }

                return s_state;
            }
        }
    }

    /// <summary>The rule of a zone: its own, else the default when it is a dungeon or instance room, else Game.</summary>
    internal static TeleportRule RuleOf(string? zonePath) {
        if (string.IsNullOrEmpty(zonePath)) {
            return TeleportRule.Game;
        }

        var state = Current;
        if (state.Rooms.TryGetValue(zonePath, out var own)) {
            return own;
        }

        return state.Default != TeleportRule.Game && IsInstanceRoom(zonePath) ? state.Default : TeleportRule.Game;
    }

    /// <summary>
    /// Why a teleport into a zone is refused (it is closed to teleporting), or null when it may go ahead. Being in the
    /// zone already is never refused, so a .tp or a step button inside the room still moves the wizard.
    /// </summary>
    internal static string? Refusal(string? destination, string? currentZone) {
        if (string.IsNullOrEmpty(destination)
            || string.Equals(destination, currentZone, StringComparison.OrdinalIgnoreCase)
            || RuleOf(destination) != TeleportRule.Closed) {
            return null;
        }

        Logger.Information("Teleport into {0} refused: Teleport Rooms has it closed.", Logger.Args(destination));

        return CLOSED_MESSAGE;
    }

    /// <summary>
    /// Whether teleporting to a friend from the friends list first asks "Do you want to go to your friend?" (Niduus,
    /// 2026-11-13: one click on a friend sometimes teleported). On unless the file says false.
    /// </summary>
    internal static bool AskBeforeFriendTeleport => Current.AskBeforeFriendTeleport;

    /// <summary>Whether friends and group mates may teleport in to someone inside this zone (it is Open).</summary>
    internal static bool FriendsMayJoin(string? zonePath) => RuleOf(zonePath) == TeleportRule.Open;

    // Wizard404's zone list marks instances (dungeons, towers, interiors with their own copy) "D".
    private static bool IsInstanceRoom(string zonePath)
        => ZoneNames.All.FirstOrDefault(z => z.Code.Equals(zonePath, StringComparison.OrdinalIgnoreCase)) is { Type: "D" };

    private static void Watch() {
        try {
            s_reloadTimer = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
            var dir = Path.Combine(SpiralDB.ExtraPath, FOLDER);
            Directory.CreateDirectory(dir);
            s_watcher = new FileSystemWatcher(dir, FILE) {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            s_watcher.Changed += (_, _) => ReloadSoon();
            s_watcher.Created += (_, _) => ReloadSoon();
            s_watcher.Deleted += (_, _) => ReloadSoon();
            s_watcher.Renamed += (_, _) => ReloadSoon();
            s_watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) {
            Logger.Warning("Teleport Rooms: changes to {0} will need a server restart ({1}).", Logger.Args(FILE, e.Message));
        }
    }

    private static void ReloadSoon() => s_reloadTimer?.Change(RELOAD_DELAY_IN_MS, Timeout.Infinite);

    // A timer thread runs it, where an exception would end the server.
    private static void Reload() {
        try {
            lock (s_lock) {
                s_state = Read();
            }
        }
        catch (Exception e) {
            Logger.Warning("Teleport Rooms: {0} could not be read again: {1}", Logger.Args(FILE, e.Message));
        }
    }

    private static TeleportRule Parse(string? text, TeleportRule fallback)
        => Enum.TryParse<TeleportRule>(text?.Trim(), ignoreCase: true, out var rule) ? rule : fallback;

    private static State Read() {
        var path = Path.Combine(SpiralDB.ExtraPath, FOLDER, FILE);
        if (!File.Exists(path)) {
            return s_empty;
        }

        try {
            var rules = JsonSerializer.Deserialize<Rules>(File.ReadAllText(path), s_options) ?? new Rules();
            var rooms = new Dictionary<string, TeleportRule>(StringComparer.OrdinalIgnoreCase);
            foreach (var (zone, rule) in rules.Rooms ?? []) {
                var key = zone?.Trim().Trim('/') ?? "";
                if (key.Length > 0) {
                    rooms[key] = Parse(rule, TeleportRule.Game);
                }
            }

            var state = new State(Parse(rules.Default, TeleportRule.Game), rooms, rules.AskBeforeFriendTeleport);
            Logger.Information("Teleport Rooms: dungeon and instance rooms are {0} by default; {1} closed, {2} open on their own. "
                + "Ask before teleporting to a friend: {3}.",
                Logger.Args(state.Default, rooms.Count(r => r.Value == TeleportRule.Closed), rooms.Count(r => r.Value == TeleportRule.Open),
                    state.AskBeforeFriendTeleport ? "on" : "off"));

            return state;
        }
        catch (Exception e) {
            // The last good rules stay (the app writes the file whole, so this is a file broken by hand).
            Logger.Warning("Teleport Rooms: {0} could not be read: {1}", Logger.Args(FILE, e.Message));

            return s_state ?? s_empty;
        }
    }

}
