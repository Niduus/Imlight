/*
 * Extra zone objects (Justin, 2026-10-21), read from spiraldb-extra/ZoneObjects/*.json, one file per zone. The game
 * files are never changed.
 *
 * Each file names its zone ("Zone": "WizardCity/WC_Ravenwood"; the file is called after it with / as -, such as
 * WizardCity-WC_Ravenwood.json) and lists objects added to that zone's object list (ZoneData m_objectList) each time
 * the zone loads, before the supervisors see it, so they are made like the zone's own objects.
 *
 * Object fields:
 *   Name        the object's zone tag. It must be unique in the file: the object's global id comes from the zone
 *               and this name, so it stays the same on every load (and in every copy of the zone).
 *   TemplateID  the object template (a .lookup id).
 *   X, Y, Z     where it stands.
 *   Yaw         which way it faces, in radians (the in-game "Rotation"; "Angle" is the same in degrees).
 *   Scale       its size, 1 if left out.
 *   StartState  the state it starts in, empty for the template's own.
 *   Sigil       for a dungeon sigil pad (a MinigameSigilInfo, made by ZoneSigilSupervisor and used through
 *               InteractDungeonSigilComponent): SigilType (the Root.wad Sigils/ name, such as
 *               MinigameSemiCircleSigil4Actor), Tag (the pad's second tag), Radius (0 = the pad default),
 *               DestinationZone and DestinationLoc (the zone and spot the sigil sends the team to; the spot is
 *               "X,Y,Z,Yaw" or a named place in that zone). A pad's own zone resolves its destination from its
 *               teleport table; an added pad has no teleport there, so it names its destination here.
 * Leave Sigil out for any other object.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Shared.Utilities;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.Zone.Core;

internal static class ZoneObjects {

    internal sealed class ZoneObjectFile {
        public string Zone { get; set; } = "";
        public string Notes { get; set; } = "";
        public List<ZoneObject> Objects { get; set; } = [];
    }

    internal sealed class ZoneObject {
        public string Name { get; set; } = "";
        public string Notes { get; set; } = "";
        public ulong TemplateID { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Yaw { get; set; }
        public float Scale { get; set; } = 1.0f;
        public string StartState { get; set; } = "";
        public ZoneSigil Sigil { get; set; }
    }

    internal sealed class ZoneSigil {
        public string SigilType { get; set; } = "";
        public string Tag { get; set; } = "";
        public float Radius { get; set; }
        public string DestinationZone { get; set; } = "";
        public string DestinationLoc { get; set; } = "";
    }

    private sealed record Added(ulong GlobalId, ZoneObject Object);

    private const string FOLDER = "ZoneObjects";

    // The high 16 bits of every added object's global id. Real objects take random ids, so the mark keeps the added
    // ones in a range of their own (a random id falls in it one time in 65,536, and must then match all 48 bits).
    private const ulong GLOBAL_ID_MARK = 0x1A11_0000_0000_0000;
    private const ulong GLOBAL_ID_MASK = 0x0000_FFFF_FFFF_FFFF;

    internal static readonly bool Enabled = ConfigurationManager.GetValue("ZoneObjects.Enabled", true);

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly Lazy<Dictionary<string, ZoneObjectFile>> s_zones = new(Load);

    // The objects made from these files, for the global id and the sigil destination.
    private static readonly ConditionalWeakTable<CoreObjectInfo, Added> s_added = [];

    /// <summary>Adds a zone's extra objects to its zone data; returns how many were added.</summary>
    internal static int AddTo(string zonePath, WizZoneData zoneData) {
        if (!Enabled || zoneData is null || string.IsNullOrEmpty(zonePath)
            || !s_zones.Value.TryGetValue(zonePath, out var file)) {
            return 0;
        }

        zoneData.m_objectList ??= [];
        var count = 0;
        foreach (var obj in file.Objects) {
            var info = MakeInfo(obj);
            s_added.AddOrUpdate(info, new Added(GlobalIdOf(zonePath, obj.Name), obj));
            zoneData.m_objectList.Add(info);
            count++;
        }

        Logger.Information("Zone {0}: added {1} extra objects from {2}.", Logger.Args(zonePath, count, FOLDER));

        return count;
    }

    /// <summary>The global id an added object takes, or null for the zone's own objects.</summary>
    internal static GID? GlobalIdOf(CoreObjectInfo info)
        => info is not null && s_added.TryGetValue(info, out var added) ? new GID(added.GlobalId) : null;

    /// <summary>Where an added sigil pad sends the team, when its file names it.</summary>
    internal static bool TryGetDestination(CoreObjectInfo info, out string zone, out string loc) {
        zone = loc = null;
        if (info is null || !s_added.TryGetValue(info, out var added)
            || string.IsNullOrEmpty(added.Object.Sigil?.DestinationZone)) {
            return false;
        }

        zone = added.Object.Sigil.DestinationZone;
        loc = added.Object.Sigil.DestinationLoc ?? "";

        return true;
    }

    /// <summary>The file name a zone's objects are kept in: the zone path with / as -.</summary>
    internal static string FileNameOf(string zonePath)
        => zonePath.Replace('/', '-') + ".json";

    private static ulong GlobalIdOf(string zonePath, string name)
        => GLOBAL_ID_MARK | (RandomGen.GenerateHash($"{FOLDER}/{zonePath}/{name}".ToLowerInvariant()) & GLOBAL_ID_MASK);

    private static CoreObjectInfo MakeInfo(ZoneObject obj) {
        CoreObjectInfo info = obj.Sigil is null
            ? new CoreObjectInfo()
            : new MinigameSigilInfo {
                m_sigilType = obj.Sigil.SigilType ?? "",
                m_zoneTag2 = obj.Sigil.Tag ?? "",
                m_radius = obj.Sigil.Radius,
            };

        info.m_templateID = obj.TemplateID;
        info.m_location = new Vector3(obj.X, obj.Y, obj.Z);
        info.m_orientation = new Vector3(0, 0, obj.Yaw);
        info.m_fScale = obj.Scale > 0 ? obj.Scale : 1.0f;
        info.m_zoneTag = obj.Name;
        info.m_startState = obj.StartState ?? "";
        info.m_loadingType = LoadingType.DYNAMIC_SERVER;

        return info;
    }

    private static Dictionary<string, ZoneObjectFile> Load() {
        var zones = new Dictionary<string, ZoneObjectFile>(StringComparer.OrdinalIgnoreCase);
        var dir = Path.Combine(SpiralDB.ExtraPath, FOLDER);
        if (!Directory.Exists(dir)) {
            return zones;
        }

        foreach (var path in Directory.GetFiles(dir, "*.json")) {
            var fileName = Path.GetFileName(path);
            try {
                var file = JsonSerializer.Deserialize<ZoneObjectFile>(File.ReadAllText(path), s_options);
                if (file is null || string.IsNullOrWhiteSpace(file.Zone)) {
                    Logger.Warning("Zone objects {0} name no zone; skipped.", Logger.Args(fileName));

                    continue;
                }

                if (!string.Equals(fileName, FileNameOf(file.Zone), StringComparison.OrdinalIgnoreCase)) {
                    Logger.Warning("Zone objects {0} are for {1}; the file would usually be called {2}.",
                        Logger.Args(fileName, file.Zone, FileNameOf(file.Zone)));
                }

                if (zones.ContainsKey(file.Zone)) {
                    Logger.Warning("Zone objects {0}: another file is already for {1}; skipped.",
                        Logger.Args(fileName, file.Zone));

                    continue;
                }

                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                file.Objects = [.. (file.Objects ?? []).Where(o => IsUsable(fileName, o, names))];
                zones[file.Zone] = file;
            }
            catch (Exception e) {
                Logger.Warning("Zone objects {0} could not be read: {1}", Logger.Args(fileName, e.Message));
            }
        }

        Logger.Information("Zone objects loaded for {0} zones ({1} objects).",
            Logger.Args(zones.Count, zones.Values.Sum(z => z.Objects.Count)));

        return zones;
    }

    private static bool IsUsable(string fileName, ZoneObject obj, HashSet<string> names) {
        if (obj is null) {
            return false;
        }

        if (obj.TemplateID == 0 || string.IsNullOrWhiteSpace(obj.Name)) {
            Logger.Warning("Zone objects {0}: an object has no TemplateID or Name; skipped.", Logger.Args(fileName));

            return false;
        }

        if (!names.Add(obj.Name)) {
            Logger.Warning("Zone objects {0}: a second object is called {1}; skipped (names give the global ids).",
                Logger.Args(fileName, obj.Name));

            return false;
        }

        return true;
    }

}
