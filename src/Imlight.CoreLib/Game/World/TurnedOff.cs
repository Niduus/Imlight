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
 * TURNED OFF
 * ========================================================================
 *
 * PURPOSE:
 * Turns any NPC, creature, zone or world off and on again (Niduus, 2026-10-31: "allow free customizations of enabling or
 * disabling NPC/creatures/zones/areas across the board, including normal and custom"), from the app's Game Rules >
 * On and Off tab, read from spiraldb-extra/Customs/TurnedOff.json. The game files are never changed. Fields:
 *   Templates   template ids: NPCs, creatures and other objects of these are not in any zone. A spawner that can spawn
 *               other creatures too spawns only those.
 *   Customs     custom creatures of the Customs tab (their numbers): a creature placed as one is not there, and a
 *               creature a room or ReplaceSpawns swaps for one is the game's own creature again.
 *   CustomNpcs  vendors and trainers of the Customs tab (their Id): they are not in their zone.
 *   Zones       zones (WizardCity/WC_Ravenwood) or whole worlds (WizardCity): nobody can go in. A player who tries is
 *               told so and stays where they are; one who logs in inside goes to the Commons. Players already inside
 *               when it is turned off stay until they leave, so nobody is pulled out of a battle. The Commons can
 *               never be turned off, so there is always somewhere to go.
 *   Names       what the app shows for each entry (template id, Custom_<n>, CustomNpc_<n>, or the zone); not used here.
 * The file is watched: a change counts at once. What was turned off leaves every running zone (a creature in a battle
 * finishes it first), and what was turned on again comes back.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/31/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Imlight.Common;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.World;

internal static class TurnedOff {

    internal sealed class Switches {
        public List<ulong> Templates { get; set; } = [];
        public List<int> Customs { get; set; } = [];
        public List<int> CustomNpcs { get; set; } = [];
        public List<string> Zones { get; set; } = [];
        public Dictionary<string, string> Names { get; set; } = [];
    }

    private sealed record State(HashSet<ulong> Templates, HashSet<int> Customs, HashSet<string> NpcTags, HashSet<string> Zones);

    private const string FOLDER = "Customs";
    internal const string FILE = "TurnedOff.json";
    // How long the file must stay still before it is read again (the app writes it in one go, an editor in several steps).
    private const int RELOAD_DELAY_IN_MS = 400;

    // The Commons: where a player who logs in inside a turned-off zone goes, so it can never be turned off itself.
    internal const string ALWAYS_ON_ZONE = "WizardCity/WC_Hub";

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly State s_empty = new([], [], new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));
    private static readonly object s_lock = new();
    private static State s_state;
    private static FileSystemWatcher s_watcher;
    private static Timer s_reloadTimer;

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

    /// <summary>Whether NPCs, creatures and objects of this template are turned off.</summary>
    internal static bool IsTemplateOff(ulong templateId) => templateId != 0 && Current.Templates.Contains(templateId);

    /// <summary>Whether a custom creature of the Customs tab is turned off.</summary>
    internal static bool IsCustomOff(int customId) => customId > 0 && Current.Customs.Contains(customId);

    /// <summary>Whether an object of a zone is turned off: by its template, or a custom vendor or trainer by its zone tag.</summary>
    internal static bool IsObjectOff(ulong templateId, string zoneTag) {
        var state = Current;

        return (templateId != 0 && state.Templates.Contains(templateId))
            || (state.NpcTags.Count > 0 && !string.IsNullOrEmpty(zoneTag) && state.NpcTags.Contains(zoneTag));
    }

    /// <summary>Whether nobody may go into a zone: it, or its whole world, is turned off. The Commons never is.</summary>
    internal static bool IsZoneOff(string zonePath) {
        if (string.IsNullOrEmpty(zonePath) || string.Equals(zonePath, ALWAYS_ON_ZONE, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        var zones = Current.Zones;
        if (zones.Count == 0) {
            return false;
        }

        if (zones.Contains(zonePath)) {
            return true;
        }

        // A world, or a part of one (Krokotopia/KT_Selenopolis), turns off every zone under it.
        var parts = zonePath.Split('/');
        for (var i = 1; i < parts.Length; i++) {
            if (zones.Contains(string.Join('/', parts.Take(i)))) {
                return true;
            }
        }

        return false;
    }

    /// <summary>What a player is told when they cannot go into a turned-off zone.</summary>
    internal const string ZONE_OFF_MESSAGE
        = "That place is turned off on this server, so you cannot go there right now.";

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
            Logger.Warning("Turned off: changes to {0} will need a server restart ({1}).", Logger.Args(FILE, e.Message));
        }
    }

    private static void ReloadSoon() => s_reloadTimer?.Change(RELOAD_DELAY_IN_MS, Timeout.Infinite);

    // A timer thread runs it, where an exception would end the server.
    private static void Reload() {
        try {
            lock (s_lock) {
                s_state = Read();
            }

            // Every running zone takes away what is now off and brings back what is on again.
            Imlight.CoreLib.Game.Zone.Core.ZoneObjects.TellEveryZone(
                new Imlight.CoreLib.Shared.Packets.ZONE_102_PROTOCOL.MSG_ZONEOBJECTSCHANGED { SwitchesChanged = true });
        }
        catch (Exception e) {
            Logger.Warning("Turned off: {0} could not be read again: {1}", Logger.Args(FILE, e.Message));
        }
    }

    private static State Read() {
        var path = Path.Combine(SpiralDB.ExtraPath, FOLDER, FILE);
        if (!File.Exists(path)) {
            return s_empty;
        }

        try {
            var switches = JsonSerializer.Deserialize<Switches>(File.ReadAllText(path), s_options) ?? new Switches();
            var state = new State(
                [.. (switches.Templates ?? []).Where(t => t != 0)],
                [.. (switches.Customs ?? []).Where(c => c > 0)],
                new HashSet<string>((switches.CustomNpcs ?? []).Where(n => n > 0).Select(CustomNpcs.TagOf), StringComparer.OrdinalIgnoreCase),
                new HashSet<string>((switches.Zones ?? []).Select(z => z?.Trim().Trim('/') ?? "").Where(z => z.Length > 0),
                    StringComparer.OrdinalIgnoreCase));
            Logger.Information("Turned off: {0} NPCs and creatures, {1} customs, {2} custom vendors and trainers, {3} zones and worlds.",
                Logger.Args(state.Templates.Count, state.Customs.Count, state.NpcTags.Count, state.Zones.Count));

            return state;
        }
        catch (Exception e) {
            // The last good switches stay (the app writes the file whole, so this is a file broken by hand).
            Logger.Warning("Turned off: {0} could not be read: {1}", Logger.Args(FILE, e.Message));

            return s_state ?? s_empty;
        }
    }

}
