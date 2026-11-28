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

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Zone.Core;

/// <summary>
/// The objects of a zone file the game builds itself (STATIC_CLIENT_SERVER and STATIC_CLIENT), with the global id the
/// game gives each one, so the server can remove one for a player.
/// </summary>
/// <remarks>
/// GameClient::LoadZone walks the zone file's object list in order. For entry <c>k</c> (empty entries count too) it calls
/// ClientObjectInfo::SetIDs(DynamicServerProcID, DynamicZoneID + 1 + k), and the object it builds gets the id
/// (DynamicServerProcID &amp; 0xFFFF) &lt;&lt; 48 | type &lt;&lt; 40 | (DynamicZoneID + 1 + k). The type is 9 when the
/// object's template is known, which it always is for an object the game can build. Both numbers come from
/// MSG_LOGINCOMPLETE, where this server sends the zone's dynamic id for both. MSG_REMOVEOBJECT with that id removes the
/// object like any other. Found in the game's own code (WizardGraphicalClient.exe 1.620: GameClient::LoadZone 0x1738620,
/// ClientObjectInfo 0x17c2b00/0x17c2c20, CoreObjectInfo::ConfigureInstance 0x15a8ad0), 2026-11-27.
/// </remarks>
internal static class ClientBuiltObjects {

    private const string INSTANCE = " instance";
    // The type byte the game puts in the id of an object it builds from its zone file.
    private const ulong ZONE_OBJECT_TYPE = 9;

    private static readonly ConcurrentDictionary<string, Entry[]> s_byZone = new(StringComparer.OrdinalIgnoreCase);

    // How far the server's copy may be from the zone file's spot and still be the same object (one moved by ZoneMoves or the
    // editor is not: the game's own copy stands at the file's spot).
    private const float SAME_SPOT = 1f;

    /// <summary>
    /// One object the game builds: its tag (without " instance"), its place in the zone's list, whether it has states, and
    /// its template and spot in the zone file.
    /// </summary>
    internal sealed record Entry(string Tag, int Index, bool HasStates, ulong TemplateId, float X, float Y, float Z);

    internal static string Plain(string tag)
        => tag?.EndsWith(INSTANCE, StringComparison.OrdinalIgnoreCase) == true ? tag[..^INSTANCE.Length] : tag ?? "";

    /// <summary>The global id the game gave the object at <paramref name="index"/> in the zone's list.</summary>
    internal static ulong GlobalIdOf(uint dynamicZoneId, int index)
        => ((ulong) (dynamicZoneId & 0xFFFF) << 48) | (ZONE_OBJECT_TYPE << 40) | (uint) (dynamicZoneId + 1 + (uint) index);

    /// <summary>Whether <paramref name="id"/> has the shape of an id the game gives an object it built from its zone file.</summary>
    internal static bool IsClientBuiltId(ulong id)
        => ((id >> 40) & 0xFF) == ZONE_OBJECT_TYPE && (id >> 48) != 0 && ((id >> 32) & 0xFF) == 0;

    /// <summary>The zone (its dynamic id's low 16 bits) a client-built id belongs to.</summary>
    internal static ushort ZoneOf(ulong id) => (ushort) (id >> 48);

    /// <summary>
    /// The id the game gave its own copy of the placed object <paramref name="info"/>, when the server's copy is that same
    /// object at the same spot.
    /// </summary>
    internal static bool TryGlobalId(string zonePath, uint dynamicZoneId, CoreObjectInfo info, out ulong id) {
        id = 0;
        if (info is null || info.m_loadingType is not (LoadingType.STATIC_CLIENT_SERVER or LoadingType.STATIC_CLIENT)) {
            return false;
        }

        var tag = Plain((string) info.m_zoneTag);
        var templateId = info.m_templateID.Full;
        Entry best = null;
        var bestDistance = float.MaxValue;
        foreach (var entry in For(zonePath)) {
            if (entry.TemplateId != templateId || !string.Equals(entry.Tag, tag, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var dx = entry.X - info.m_location.X;
            var dy = entry.Y - info.m_location.Y;
            var dz = entry.Z - info.m_location.Z;
            var distance = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            if (distance < bestDistance) {
                best = entry;
                bestDistance = distance;
            }
        }

        if (best is null || bestDistance > SAME_SPOT) {
            return false;
        }

        id = GlobalIdOf(dynamicZoneId, best.Index);

        return true;
    }

    internal static Entry[] For(string zonePath)
        => string.IsNullOrEmpty(zonePath) ? [] : s_byZone.GetOrAdd(zonePath, Read);

    private static Entry[] Read(string zonePath) {
        var entries = new List<Entry>();
        try {
            if (!ResourceManager.TryLoadArchive(zonePath, out var wad)
                || ResourceManager.OpenZoneData(wad)?.ToArray() is not { } bytes
                || !new BindSerializer().Deserialize<WizZoneData>(bytes, 1u, out var data)
                || data?.m_objectList is not { } objects) {
                return [];
            }

            for (var index = 0; index < objects.Count; index++) {
                var info = objects[index];
                if (info is null || info.m_templateID == 0
                    || info.m_loadingType is not (LoadingType.STATIC_CLIENT_SERVER or LoadingType.STATIC_CLIENT)) {
                    continue;
                }

                var tag = Plain((string) info.m_zoneTag);
                if (string.IsNullOrEmpty(tag)) {
                    continue;
                }

                var template = CoreObjectFactory.GetCoreTemplate(info.m_templateID) as GameObjectTemplate;
                var hasStates = template?.m_behaviors?.OfType<ObjectStateBehaviorTemplate>().Any() == true;
                entries.Add(new Entry(tag, index, hasStates, info.m_templateID.Full,
                    info.m_location.X, info.m_location.Y, info.m_location.Z));
            }
        }
        catch (Exception ex) {
            Logger.Warning("Client-built objects: could not read {0}: {1}", Logger.Args(zonePath, ex.Message));
        }

        Logger.Debug("Client-built objects: {0} has {1}.", Logger.Args(zonePath, entries.Count));

        return [.. entries];
    }

}
