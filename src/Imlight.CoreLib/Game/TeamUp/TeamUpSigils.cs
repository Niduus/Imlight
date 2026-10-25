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
 * TEAM UP SIGILS
 * ========================================================================
 *
 * PURPOSE:
 * The dungeon sigils a team can be asked for at: where each pad leads, by the pad's global id, and the game's name
 * for that dungeon.
 *
 * USAGE EXAMPLE:
 * TeamUpSigils.Register(padGid, here, x, y, zone, loc, variant);   // InteractDungeonSigilComponent, once its pad resolves
 * TeamUpSigils.TryGet(message.NPCID, out var sigil);   // TeamUpService, on the sigil's Team Up! button
 *
 * NOTE:
 * The Team Up! button of the sigil window sends the pad's global id (MSG_REQUESTTEAMUP NPCID). The name shown in the
 * team window and the Join a Team list is a locale key (the game looks it up), the destination zone's
 * m_zoneDisplayName, read once from the zone's gamedata.bin on a worker thread.
 *
 * TODO:
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.TeamUp;

internal static class TeamUpSigils {

    internal sealed record Sigil(ulong PadGid, string PadZone, float X, float Y, string DestinationZone, string DestinationLoc,
                                 string Variant);

    private static readonly ConcurrentDictionary<ulong, Sigil> s_pads = new();
    private static readonly ConcurrentDictionary<string, string> s_displayKeys = new(StringComparer.OrdinalIgnoreCase);

    internal static void Register(ulong padGid, string padZone, float x, float y, string destinationZone, string destinationLoc,
                                  string variant) {
        if (padGid == 0 || string.IsNullOrEmpty(destinationZone)) {
            return;
        }

        s_pads[padGid] = new Sigil(padGid, padZone ?? "", x, y, destinationZone, destinationLoc ?? "", variant ?? "");
        if (!s_displayKeys.ContainsKey(destinationZone)) {
            s_displayKeys.TryAdd(destinationZone, "");
            Task.Run(() => ReadDisplayKey(destinationZone));
        }
    }

    internal static void Unregister(ulong padGid)
        => s_pads.TryRemove(padGid, out _);

    internal static bool TryGet(ulong padGid, out Sigil sigil)
        => s_pads.TryGetValue(padGid, out sigil);

    /// <summary>The pad nearest to a spot in a zone, within the given distance (for `.teamup ask`).</summary>
    internal static Sigil Nearest(string zone, float x, float y, float within) {
        Sigil best = null;
        var bestDistance = within * within;
        foreach (var sigil in s_pads.Values) {
            if (!string.Equals(sigil.PadZone, zone, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var dx = sigil.X - x;
            var dy = sigil.Y - y;
            if (dx * dx + dy * dy <= bestDistance) {
                bestDistance = dx * dx + dy * dy;
                best = sigil;
            }
        }

        return best;
    }

    /// <summary>The locale key of a dungeon's name ("WizardZone_00000698"), or empty while it is not known.</summary>
    internal static string DisplayKeyOf(string zone)
        => !string.IsNullOrEmpty(zone) && s_displayKeys.TryGetValue(zone, out var key) ? key ?? "" : "";

    /// <summary>
    /// The locale key of a dungeon's name, read now when it is not known yet (the sigil prompt names the dungeon the
    /// moment a player walks onto it). Empty when the zone has none.
    /// </summary>
    internal static string DisplayKeyNow(string zone) {
        if (string.IsNullOrEmpty(zone)) {
            return "";
        }

        if (!s_displayKeys.TryGetValue(zone, out var key) || string.IsNullOrEmpty(key)) {
            ReadDisplayKey(zone);
            s_displayKeys.TryGetValue(zone, out key);
        }

        return key ?? "";
    }

    private static void ReadDisplayKey(string zone) {
        try {
            if (!ResourceManager.TryLoadFile(zone, "gamedata.bin", out var stream) || stream is null) {
                return;
            }

            using (stream) {
                if (new BindSerializer().Deserialize<WizZoneData>(stream.ToArray(), 1, out var data) && data is not null) {
                    s_displayKeys[zone] = data.m_zoneDisplayName.ToString() ?? "";
                }
            }
        }
        catch (Exception ex) {
            Logger.Warning("Team Up: could not read the name of {0}. {1}", Logger.Args(zone, ex.Message));
        }
    }

}
