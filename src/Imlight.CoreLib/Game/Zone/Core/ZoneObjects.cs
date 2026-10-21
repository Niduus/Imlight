/*
 * Extra zone objects (Justin, 2026-10-21), read from spiraldb-extra/ZoneObjects/*.json. The game files are never changed.
 *
 * Each file names its zone ("Zone": "WizardCity/WC_Ravenwood"; the file is called after it with / as -, such as
 * WizardCity-WC_Ravenwood.json) and adds to that zone each time it loads, before the supervisors see it, so what it
 * adds is made like the zone's own. A file with a Variant ("Variant": "RavenwoodMalistaire", file
 * <zone>@<variant>.json) is used only by a copy of the zone loaded for that variant: an added sigil with that Variant
 * sends its team into a fresh private copy loaded with it. A file with no Variant is used by every copy.
 *
 * File fields:
 *   Objects     objects added to the zone's object list (ZoneData m_objectList).
 *   Creatures   creatures spawned in the zone as the zone's own spawns are (a spawner on a one-node path): they stand
 *               still, fight like the zone's creatures and, in an instance, do not come back once defeated.
 *   Remove      template ids: the zone's own objects and spawners of these are left out (a boss the file places
 *               itself, so there are not two).
 *   Encounters  extra enemies for a battle: Boss (a template id), Minions (template ids) and MinionCount. When a
 *               battle in the zone starts against the boss, MinionCount different ones of Minions, picked at random,
 *               join it on the boss's side and fight from the first round.
 *   Exit        where the zone's teleports out of it go instead: Zone and Loc ("X,Y,Z,Yaw" or a named place there).
 *               Triggers names the teleports it changes (trigger or door names); empty for every one that leaves
 *               the zone.
 *
 * Object and creature fields:
 *   Name        the object's zone tag. It must be unique in the file: the object's global id comes from the zone, the
 *               variant and this name, so it stays the same on every load (and in every copy of the zone).
 *   TemplateID  the object or creature template (a .lookup id).
 *   X, Y, Z     where it stands.
 *   Yaw         which way it faces, in radians (the in-game "Rotation"; "Angle" is the same in degrees).
 *   Scale       its size, 1 if left out (objects only).
 *   StartState  the state it starts in, empty for the template's own (objects only).
 *   Sigil       for a dungeon sigil pad (a MinigameSigilInfo, made by ZoneSigilSupervisor and used through
 *               InteractDungeonSigilComponent): SigilType (the Root.wad Sigils/ name, such as
 *               MinigameSemiCircleSigil4Actor), Tag (the pad's second tag), Radius (0 = the pad default),
 *               DestinationZone and DestinationLoc (the zone and spot the sigil sends the team to; the spot is
 *               "X,Y,Z,Yaw" or a named place in that zone), Variant (the destination's variant, empty for none) and
 *               LitState (the pad's lit state, Glowy if left out). A pad's own zone resolves its destination from its
 *               teleport table; an added pad has no teleport there, so it names its destination here.
 *   CombatSigil for a battle circle (a CombatSigilObjectInfo, made by ZoneSigilSupervisor): SigilType (the Root.wad
 *               Sigils/ name, such as CombatSigilDarkmoorMalistaireBoss) and Radius (how near a player joins).
 * Leave Sigil and CombatSigil out for any other object.
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
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Utilities;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.Zone.Core;

internal static class ZoneObjects {

    internal sealed class ZoneObjectFile {
        public string Zone { get; set; } = "";
        public string Variant { get; set; } = "";
        public string Notes { get; set; } = "";
        public List<ZoneObject> Objects { get; set; } = [];
        public List<ZoneObject> Creatures { get; set; } = [];
        public List<ulong> Remove { get; set; } = [];
        public List<ZoneEncounter> Encounters { get; set; } = [];
        public ZoneExit Exit { get; set; }
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
        public ZoneCombatSigil CombatSigil { get; set; }
    }

    internal sealed class ZoneSigil {
        public string SigilType { get; set; } = "";
        public string Tag { get; set; } = "";
        public float Radius { get; set; }
        public string DestinationZone { get; set; } = "";
        public string DestinationLoc { get; set; } = "";
        public string Variant { get; set; } = "";
        public string LitState { get; set; } = "";
    }

    internal sealed class ZoneCombatSigil {
        public string SigilType { get; set; } = "";
        public float Radius { get; set; }
    }

    internal sealed class ZoneEncounter {
        public ulong Boss { get; set; }
        public string Notes { get; set; } = "";
        public List<ulong> Minions { get; set; } = [];
        public int MinionCount { get; set; }
    }

    internal sealed class ZoneExit {
        public string Zone { get; set; } = "";
        public string Loc { get; set; } = "";
        public List<string> Triggers { get; set; } = [];
    }

    /// <summary>What the files gave one copy of a zone: its encounters and its exit.</summary>
    internal sealed class Applied(string zonePath, IReadOnlyList<ZoneObjectFile> files) {

        internal static readonly Applied None = new("", []);

        /// <summary>The encounter for a battle started against this creature template, or null.</summary>
        internal ZoneEncounter EncounterFor(ulong bossTemplateId)
            => files.SelectMany(f => f.Encounters).FirstOrDefault(e => e.Boss == bossTemplateId);

        /// <summary>
        /// Where a teleport of this zone goes instead, when a file gives the zone an exit: the trigger or door name
        /// and the destination it has now.
        /// </summary>
        internal bool TryGetExit(string teleportName, string destinationZone, out string zone, out string loc) {
            zone = loc = null;
            var exit = files.Select(f => f.Exit).LastOrDefault(e => !string.IsNullOrEmpty(e?.Zone));
            if (exit is null) {
                return false;
            }

            var named = exit.Triggers is { Count: > 0 };
            if (named ? !exit.Triggers.Contains(teleportName ?? "", StringComparer.OrdinalIgnoreCase)
                      : string.IsNullOrEmpty(destinationZone)
                        || string.Equals(destinationZone, zonePath, StringComparison.OrdinalIgnoreCase)) {
                return false;
            }

            zone = exit.Zone;
            loc = exit.Loc ?? "";

            return true;
        }

    }

    private sealed record Added(ulong GlobalId, ZoneObject Object);

    private const string FOLDER = "ZoneObjects";

    // The high 16 bits of every added object's global id (and of the paths, nodes and spawners made for added
    // creatures). Real objects take random ids, so the mark keeps the added ones in a range of their own (a random id
    // falls in it one time in 65,536, and must then match all 48 bits).
    private const ulong GLOBAL_ID_MARK = 0x1A11_0000_0000_0000;
    private const ulong GLOBAL_ID_MASK = 0x0000_FFFF_FFFF_FFFF;

    // A dungeon sigil pad's lit state (MinigameSigilStatesNew: Glowy plays the pad's "On" animation).
    internal const string SIGIL_LIT_STATE = "Glowy";

    // A battle circle's join radius when its file gives none.
    private const float DEFAULT_COMBAT_SIGIL_RADIUS = 600.0f;

    internal static readonly bool Enabled = ConfigurationManager.GetValue("ZoneObjects.Enabled", true);

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly Lazy<Dictionary<string, ZoneObjectFile>> s_zones = new(Load);

    // The objects made from these files, for the global id and the sigil destination.
    private static readonly ConditionalWeakTable<CoreObjectInfo, Added> s_added = [];

    /// <summary>
    /// Adds a zone's extra objects and creatures to its load results, for every copy and for the variant (if any) it
    /// is loaded for, and leaves out what the files remove. Returns what the copy keeps for later (encounters, exit).
    /// </summary>
    internal static Applied AddTo(string zonePath, string variant, ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS results) {
        if (!Enabled || results?.ZoneData is null || string.IsNullOrEmpty(zonePath)) {
            return Applied.None;
        }

        var files = new List<ZoneObjectFile>();
        if (s_zones.Value.TryGetValue(KeyOf(zonePath, ""), out var always)) {
            files.Add(always);
        }

        if (!string.IsNullOrEmpty(variant)) {
            if (s_zones.Value.TryGetValue(KeyOf(zonePath, variant), out var forVariant)) {
                files.Add(forVariant);
            } else {
                Logger.Warning("Zone {0}: no {1} file for variant {2}.", Logger.Args(zonePath, FOLDER, variant));
            }
        }

        if (files.Count == 0) {
            return Applied.None;
        }

        var zoneData = results.ZoneData;
        zoneData.m_objectList ??= [];
        var removed = RemoveOwn(files.SelectMany(f => f.Remove).ToHashSet(), results);

        var objects = 0;
        var creatures = 0;
        foreach (var file in files) {
            foreach (var obj in file.Objects) {
                var info = MakeInfo(obj);
                s_added.AddOrUpdate(info, new Added(GlobalIdOf(zonePath, file.Variant, obj.Name), obj));
                zoneData.m_objectList.Add(info);
                objects++;
            }

            foreach (var creature in file.Creatures) {
                if (AddCreature(zonePath, file.Variant, creature, results)) {
                    creatures++;
                }
            }
        }

        Logger.Information("Zone {0}{1}: added {2} objects and {3} creatures from {4}, left out {5} of the zone's own.",
            Logger.Args(zonePath, string.IsNullOrEmpty(variant) ? "" : $" ({variant})", objects, creatures, FOLDER, removed));

        return new Applied(zonePath, files);
    }

    /// <summary>The global id an added object takes, or null for the zone's own objects.</summary>
    internal static GID? GlobalIdOf(CoreObjectInfo info)
        => info is not null && s_added.TryGetValue(info, out var added) ? new GID(added.GlobalId) : null;

    /// <summary>Where an added sigil pad sends the team, when its file names it, and the variant it loads.</summary>
    internal static bool TryGetDestination(CoreObjectInfo info, out string zone, out string loc, out string variant) {
        zone = loc = variant = null;
        if (info is null || !s_added.TryGetValue(info, out var added)
            || string.IsNullOrEmpty(added.Object.Sigil?.DestinationZone)) {
            return false;
        }

        zone = added.Object.Sigil.DestinationZone;
        loc = added.Object.Sigil.DestinationLoc ?? "";
        variant = added.Object.Sigil.Variant ?? "";

        return true;
    }

    /// <summary>The lit state an added sigil pad names, or null.</summary>
    internal static string LitStateOf(CoreObjectInfo info)
        => info is not null && s_added.TryGetValue(info, out var added)
           && !string.IsNullOrEmpty(added.Object.Sigil?.LitState) ? added.Object.Sigil.LitState : null;

    /// <summary>The file name a zone's objects are kept in: the zone path with / as -, then @variant if it has one.</summary>
    internal static string FileNameOf(string zonePath, string variant = "")
        => zonePath.Replace('/', '-') + (string.IsNullOrEmpty(variant) ? "" : "@" + variant) + ".json";

    private static string KeyOf(string zonePath, string variant)
        => $"{zonePath}@{variant}".ToLowerInvariant();

    private static ulong GlobalIdOf(string zonePath, string variant, string name, string part = "")
        => GLOBAL_ID_MARK | (RandomGen.GenerateHash(
            $"{FOLDER}/{zonePath}{(string.IsNullOrEmpty(variant) ? "" : "@" + variant)}/{name}{part}".ToLowerInvariant())
            & GLOBAL_ID_MASK);

    private static int RemoveOwn(HashSet<ulong> templates, ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS results) {
        if (templates.Count == 0) {
            return 0;
        }

        var removed = results.ZoneData.m_objectList.RemoveAll(o => o is not null && templates.Contains(o.m_templateID));
        removed += results.SpawnData?.m_spawners?.RemoveAll(s => s?.m_spawnList is not null
            && s.m_spawnList.Any(i => i?.m_objectInfo is not null && templates.Contains(i.m_objectInfo.m_templateID))) ?? 0;

        return removed;
    }

    // A creature becomes a spawner of its own on a path of one node, so the zone's path code spawns it.
    private static bool AddCreature(string zonePath, string variant, ZoneObject creature,
                                    ZONE_102_PROTOCOL.MSG_ZONELOADRESULTS results) {
        if (results.SpawnData is null || results.PathData is null || results.NodeData is null) {
            Logger.Warning("Zone {0}: creature {1} not added, the zone has no spawn, path or node data.",
                Logger.Args(zonePath, creature.Name));

            return false;
        }

        var nodeId = new GID(GlobalIdOf(zonePath, variant, creature.Name, "/node"));
        var pathId = new GID(GlobalIdOf(zonePath, variant, creature.Name, "/path"));
        results.NodeData.m_nodeList ??= [];
        results.NodeData.m_nodeList.Add(new NodeObject {
            m_id = nodeId,
            m_location = new Vector3(creature.X, creature.Y, creature.Z),
            // A node's direction is in degrees.
            m_direction = (float) (creature.Yaw * (180.0 / Math.PI)),
        });

        results.PathData.m_pathList ??= [];
        results.PathData.m_pathList.Add(new PathObjectTemplate {
            m_id = pathId,
            m_name = creature.Name,
            m_nodeIDs = [nodeId],
        });

        results.SpawnData.m_spawners ??= [];
        results.SpawnData.m_spawners.Add(new SpawnObject {
            m_id = new GID(GlobalIdOf(zonePath, variant, creature.Name, "/spawner")),
            m_name = creature.Name,
            m_active = true,
            m_maxNumberOfSpawns = 1,
            m_atLeastOneSpawn = true,
            m_spawnTime = 0,
            m_spawnList = [
                new SpawnItem {
                    m_percentChance = 100,
                    m_objectInfo = new SpawnObjectInfo {
                        m_templateID = creature.TemplateID,
                        m_location = new Vector3(creature.X, creature.Y, creature.Z),
                        m_orientation = new Vector3(0, 0, creature.Yaw),
                        m_fScale = 1.0f,
                        m_zoneTag = creature.Name,
                        m_loadingType = LoadingType.DYNAMIC_SERVER,
                        m_kStartNodeType = StartNodeType.SNT_FIRST,
                        m_pathID = pathId,
                    },
                },
            ],
        });

        return true;
    }

    private static CoreObjectInfo MakeInfo(ZoneObject obj) {
        CoreObjectInfo info;
        if (obj.Sigil is not null) {
            info = new MinigameSigilInfo {
                m_sigilType = obj.Sigil.SigilType ?? "",
                m_zoneTag2 = obj.Sigil.Tag ?? "",
                m_radius = obj.Sigil.Radius,
            };
        } else if (obj.CombatSigil is not null) {
            info = new CombatSigilObjectInfo {
                m_sigilType = obj.CombatSigil.SigilType ?? "",
                m_zoneTag2 = obj.Name,
                m_radius = obj.CombatSigil.Radius > 0 ? obj.CombatSigil.Radius : DEFAULT_COMBAT_SIGIL_RADIUS,
            };
        } else {
            info = new CoreObjectInfo();
        }

        info.m_templateID = obj.TemplateID;
        info.m_location = new Vector3(obj.X, obj.Y, obj.Z);
        info.m_orientation = new Vector3(0, 0, obj.Yaw);
        info.m_fScale = obj.Scale > 0 ? obj.Scale : 1.0f;
        info.m_zoneTag = obj.Name;
        info.m_startState = obj.StartState ?? "";
        // An added sigil pad starts lit: the zone sends a joining player its state after the object, every time.
        if (obj.Sigil is not null && string.IsNullOrEmpty(obj.StartState)) {
            info.m_startState = string.IsNullOrEmpty(obj.Sigil.LitState) ? SIGIL_LIT_STATE : obj.Sigil.LitState;
        }

        info.m_loadingType = LoadingType.DYNAMIC_SERVER;

        return info;
    }

    private static Dictionary<string, ZoneObjectFile> Load() {
        var zones = new Dictionary<string, ZoneObjectFile>();
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

                file.Variant ??= "";
                var key = KeyOf(file.Zone, file.Variant);
                if (zones.ContainsKey(key)) {
                    Logger.Warning("Zone objects {0}: another file is already for {1} {2}; skipped.",
                        Logger.Args(fileName, file.Zone, file.Variant));

                    continue;
                }

                if (!string.Equals(fileName, FileNameOf(file.Zone, file.Variant), StringComparison.OrdinalIgnoreCase)) {
                    Logger.Warning("Zone objects {0} are for {1}; the file would usually be called {2}.",
                        Logger.Args(fileName, file.Zone, FileNameOf(file.Zone, file.Variant)));
                }

                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                file.Objects = [.. (file.Objects ?? []).Where(o => IsUsable(fileName, o, names))];
                file.Creatures = [.. (file.Creatures ?? []).Where(o => IsUsable(fileName, o, names))];
                file.Remove ??= [];
                file.Encounters = [.. (file.Encounters ?? []).Where(e => e is not null && e.Boss != 0)];
                foreach (var encounter in file.Encounters) {
                    encounter.Minions = [.. (encounter.Minions ?? []).Where(m => m != 0).Distinct()];
                }

                zones[key] = file;
            }
            catch (Exception e) {
                Logger.Warning("Zone objects {0} could not be read: {1}", Logger.Args(fileName, e.Message));
            }
        }

        Logger.Information("Zone objects loaded from {0} files ({1} objects, {2} creatures).",
            Logger.Args(zones.Count, zones.Values.Sum(z => z.Objects.Count), zones.Values.Sum(z => z.Creatures.Count)));

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
