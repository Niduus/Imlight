/*
 * Extra zone objects (Niduus, 2026-10-21), read from spiraldb-extra/ZoneObjects/*.json. The game files are never changed.
 *
 * Each file names its zone ("Zone": "WizardCity/WC_Ravenwood"; the file is called after it with / as -, such as
 * WizardCity-WC_Ravenwood.json) and adds to that zone each time it loads, before the supervisors see it, so what it
 * adds is made like the zone's own. A file with a Variant ("Variant": "RavenwoodMalistaire", file
 * <zone>@<variant>.json) is used only by a copy of the zone loaded for that variant: an added sigil with that Variant
 * sends its team into a fresh private copy loaded with it. A file with no Variant is used by every copy.
 *
 * File fields:
 *   Enabled     false switches the whole file off (its objects go at once, the rest counts as below) without
 *               deleting it; true or left out, it is used. The Manager's Customs tab has a switch for it.
 *   EditedInManager  set by the Manager's Customs tab when it saves the file (the date). An update keeps a file
 *               that has it instead of putting back the shipped one, so the changes made there stay. The server
 *               does not use it.
 *   Objects     objects added to the zone's object list (ZoneData m_objectList).
 *   Creatures   creatures spawned in the zone as the zone's own spawns are (a spawner on a one-node path): they stand
 *               still, fight like the zone's creatures and, in an instance, do not come back once defeated.
 *               Custom (a custom of the Customs tab, Customs.cs) spawns it as that custom; its TemplateID may then
 *               be left out, the custom's creature is used.
 *   Remove      template ids: the zone's own objects and spawners of these are left out (a boss the file places
 *               itself, so there are not two).
 *   DistinctSpawners  names of the zone's own spawners (SpawnData m_name) that never have two living creatures of
 *               the same template: each spawn is picked by its chance among the templates not already out. Such a
 *               spawner fills up to its maximum with its first spawn, a few seconds after the zone loads.
 *   Encounters  extra enemies for a battle: Boss (a template id), Minions (template ids), MinionCount and JoinRadius.
 *               When a battle in the zone starts against the boss, MinionCount different ones of Minions join it on
 *               the boss's side and fight from the first round: first the zone's own creatures of those templates
 *               that are not fighting (within JoinRadius of the battle circle; 0 for anywhere in the zone), then,
 *               a second later, spawned ones picked at random for the rest.
 *   ReplaceSpawns  creature templates the zone's spawners (its own and the file's) spawn as a custom of the app's
 *               Customs tab instead (Customs.cs): { "1336035": 1 } spawns Malistaire the Undying as custom 1. The
 *               Customs tab's rooms (Customs/Rooms.json) do the same without changing this file, and win over it.
 *   Exit        where the zone's teleports out of it go instead: Zone and Loc ("X,Y,Z,Yaw" or a named place there).
 *               Triggers names the teleports it changes (trigger or door names); empty for every one that leaves
 *               the zone. Doors are object template ids that become press-X doors to the exit in this copy, for an
 *               object the server has no teleport for (such as the warp portal a trigger puts up after a boss).
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
 *               LitState (the pad's look, Glowy if left out: Glowy is the blue glow, Off is dark, and Red (or
 *               "Advanced") is the red Advanced Mode glow; only pads whose animations have On_Red can show it, such
 *               as the 4 player pad 107081 and the Skeleton Key sigil art 1313007, not the 2 player pad 1634569). A
 *               pad's own zone resolves its destination from its
 *               teleport table; an added pad has no teleport there, so it names its destination here.
 *   CombatSigil for a battle circle (a CombatSigilObjectInfo, made by ZoneSigilSupervisor): SigilType (the Root.wad
 *               Sigils/ name, such as CombatSigilDarkmoorMalistaireBoss) and Radius (how near a player joins). A PvP
 *               circle (PvPSigil8Actor2Sides) is a duel circle: wizards on its two sides duel each other, and Combat
 *               says how: Turns (the sides take turns, the PvP way, the default) or Together (both sides act at once).
 * Leave Sigil and CombatSigil out for any other object.
 *
 * Placements (Niduus, 2026-10-22: "if I have to turn it or move it, it works immediately and does NOT require another
 * prompt or server reset"): the Manager's Customs tab saves where an object stands to spiraldb-extra/Customs/
 * Placements.json, which updates never replace (they replace the files here). For a room (the zone, with @variant for a
 * variant's file) and an object name it holds X, Y, Z, Yaw and Scale over the file's, a TemplateID to swap the object
 * (or, with a name the file does not have, to add one), and Removed to leave the object out.
 *
 * Vendors and trainers (Niduus, 2026-10-23): the Customs tab's own shopkeepers and trainers, in
 * spiraldb-extra/Customs/VendorsTrainers.json (CustomNpcs.cs), are added here as objects of their zone too, each named
 * by its zone tag (CustomNpc_<Id>). An NPC whose name, kind or lists changed goes and comes back as the new one.
 *
 * All three are watched: a change is applied to every loaded copy of the zone at once. An object that moved or turned is
 * taken away from the players there and sent again at its new place (a sigil pad's radius goes with it); an added
 * object appears and a removed one goes. Encounters, exits, distinct spawners and spawn swaps count from their next
 * use, and battle circles (CombatSigil) and creatures at once (Niduus, 2026-10-23: a battle added in the Customs tab
 * counts at once too). Remove counts from the next time the zone loads.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using Akka.Actor;
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
        public bool Enabled { get; set; } = true;
        public List<ZoneObject> Objects { get; set; } = [];
        public List<ZoneObject> Creatures { get; set; } = [];
        public List<ulong> Remove { get; set; } = [];
        public List<string> DistinctSpawners { get; set; } = [];
        public List<ZoneEncounter> Encounters { get; set; } = [];
        public Dictionary<string, int> ReplaceSpawns { get; set; } = [];
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
        // A creature's custom of the Customs tab (Customs.cs), 0 for the creature itself.
        public int Custom { get; set; }
        public ZoneSigil Sigil { get; set; }
        public ZoneCombatSigil CombatSigil { get; set; }
        // A custom vendor or trainer's name, kind and lists (CustomNpcs.SignatureOf); null for any other object.
        internal string CustomNpc { get; set; }
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
        // For a PvP circle (PvPSigil8Actor2Sides): Turns (the sides take turns, the PvP way) or Together (both sides
        // act at once, the monster battle way). Empty: Turns.
        public string Combat { get; set; } = "";
    }

    internal sealed class ZoneEncounter {
        public ulong Boss { get; set; }
        public string Notes { get; set; } = "";
        public List<ulong> Minions { get; set; } = [];
        public int MinionCount { get; set; }
        public float JoinRadius { get; set; }
    }

    internal sealed class ZoneExit {
        public string Zone { get; set; } = "";
        public string Loc { get; set; } = "";
        public List<string> Triggers { get; set; } = [];
        public List<ulong> Doors { get; set; } = [];
    }

    /// <summary>
    /// What the files give one copy of a zone: its encounters and its exit. It reads the files as they are now, so a
    /// change to them counts from its next use.
    /// </summary>
    internal sealed class Applied(string zonePath, string variant) {

        internal static readonly Applied None = new("", "");

        private List<ZoneObjectFile> Files => zonePath.Length == 0 ? [] : FilesFor(zonePath, variant);

        /// <summary>
        /// The custom (Customs.cs) this copy of the zone spawns instead of a creature template, or 0: the Customs tab's
        /// rooms first, then the files' ReplaceSpawns.
        /// </summary>
        internal int CustomFor(ulong templateId) {
            if (zonePath.Length == 0) {
                return 0;
            }

            // -1 in the rooms: the game's creature, whatever the files say.
            var room = Imlight.CoreLib.Game.Combat.Customs.RoomCustomFor(zonePath, variant, templateId);
            if (room != 0) {
                return Math.Max(0, room);
            }

            return Files.Select(f => f.ReplaceSpawns != null && f.ReplaceSpawns.TryGetValue(templateId.ToString(), out var id) ? id : 0)
                .LastOrDefault(id => id > 0);
        }

        /// <summary>The encounter for a battle started against this creature template, or null.</summary>
        internal ZoneEncounter EncounterFor(ulong bossTemplateId)
            => Files.SelectMany(f => f.Encounters).FirstOrDefault(e => e.Boss == bossTemplateId);

        /// <summary>Where a door of one of the exit's Doors templates goes in this copy of the zone.</summary>
        internal bool TryGetDoorExit(ulong templateId, out string zone, out string loc) {
            zone = loc = null;
            var exit = Files.Select(f => f.Exit).LastOrDefault(e => !string.IsNullOrEmpty(e?.Zone));
            if (exit?.Doors is null || !exit.Doors.Contains(templateId)) {
                return false;
            }

            zone = exit.Zone;
            loc = exit.Loc ?? "";

            return true;
        }

        /// <summary>Whether this spawner of the zone keeps its living creatures all of different templates.</summary>
        internal bool IsDistinctSpawner(string spawnerName)
            => !string.IsNullOrEmpty(spawnerName)
               && Files.Any(f => f.DistinctSpawners.Contains(spawnerName, StringComparer.OrdinalIgnoreCase));

        /// <summary>
        /// Where a teleport of this zone goes instead, when a file gives the zone an exit: the trigger or door name
        /// and the destination it has now.
        /// </summary>
        internal bool TryGetExit(string teleportName, string destinationZone, out string zone, out string loc) {
            zone = loc = null;
            var exit = Files.Select(f => f.Exit).LastOrDefault(e => !string.IsNullOrEmpty(e?.Zone));
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

    // The Advanced Mode look (Niduus, 2026-10-22: "Advance Mode" sigils): MinigameSigilStatesNew's Red plays the pad's
    // "On_Red" animation (AnimationData/FX_BattleCircle_4X_New.xml, animation 2), the red glow the game shows when a
    // team picks Advanced Mode at a dungeon sigil. The set only goes to Red from Glowy (Off goes to Red_02, the same
    // red with debug on), so a pad is sent Glowy just before it (SigilStatePath).
    internal const string SIGIL_ADVANCED_STATE = "Red";

    /// <summary>
    /// The states a sigil pad is sent, in order, to show a look: Red goes through Glowy (a pad the game just made is
    /// Off, and the state set only reaches Red from Glowy); any other look is sent as it is.
    /// </summary>
    internal static string[] SigilStatePath(string state)
        => string.Equals(state, SIGIL_ADVANCED_STATE, StringComparison.OrdinalIgnoreCase)
            ? [SIGIL_LIT_STATE, SIGIL_ADVANCED_STATE]
            : [state];

    /// <summary>A sigil pad's lit state as its file names it, with "Advanced" (or AdvancedMode) read as Red.</summary>
    internal static string SigilStateName(string state) {
        var s = state?.Trim() ?? "";

        return s.Replace(" ", "").ToLowerInvariant() is "advanced" or "advancedmode" or "advance" or "advancemode" ? SIGIL_ADVANCED_STATE : s;
    }

    // A battle circle's join radius when its file gives none.
    private const float DEFAULT_COMBAT_SIGIL_RADIUS = 600.0f;

    internal static readonly bool Enabled = ConfigurationManager.GetValue("ZoneObjects.Enabled", true);

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private const string PLACEMENTS_FOLDER = "Customs";
    internal const string PLACEMENTS_FILE = "Placements.json";
    // How long the files must stay still before they are read again (an editor writes a file in several steps).
    private const int RELOAD_DELAY_IN_MS = 400;

    private static readonly object s_loadLock = new();
    private static Dictionary<string, ZoneObjectFile> s_loaded;
    private static FileSystemWatcher s_watcher;
    private static FileSystemWatcher s_placementsWatcher;
    private static FileSystemWatcher s_customNpcsWatcher;
    private static Timer s_reloadTimer;

    // The loaded copies of zones that use these files, so a change reaches them.
    private static readonly ConcurrentDictionary<IActorRef, (string Zone, string Variant)> s_live = new();

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

        var files = FilesFor(zonePath, variant);
        if (!string.IsNullOrEmpty(variant) && !Zones.ContainsKey(KeyOf(zonePath, variant))) {
            Logger.Warning("Zone {0}: no {1} file for variant {2}.", Logger.Args(zonePath, FOLDER, variant));
        }

        if (files.Count == 0) {
            // Kept for the zone all the same, so objects added to it later (Placements.json) reach it.
            return new Applied(zonePath, variant ?? "");
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

        return new Applied(zonePath, variant ?? "");
    }

    // The files a copy of a zone uses: the one for every copy, then the variant's.
    private static List<ZoneObjectFile> FilesFor(string zonePath, string variant) {
        var zones = Zones;
        var files = new List<ZoneObjectFile>();
        if (zones.TryGetValue(KeyOf(zonePath, ""), out var always)) {
            files.Add(always);
        }

        if (!string.IsNullOrEmpty(variant) && zones.TryGetValue(KeyOf(zonePath, variant), out var forVariant)) {
            files.Add(forVariant);
        }

        return files;
    }

    private static Dictionary<string, ZoneObjectFile> Zones {
        get {
            var zones = s_loaded;
            if (zones is not null) {
                return zones;
            }

            lock (s_loadLock) {
                if (s_loaded is null) {
                    s_loaded = Load();
                    Watch();
                }

                return s_loaded;
            }
        }
    }

    /// <summary>A loaded copy of a zone, which a change to the files reaches.</summary>
    internal static void Register(IActorRef zone, string zonePath, string variant) {
        if (Enabled && zone is not null && !string.IsNullOrEmpty(zonePath)) {
            s_live[zone] = (zonePath, variant ?? "");
        }
    }

    internal static void Unregister(IActorRef zone) {
        if (zone is not null) {
            s_live.TryRemove(zone, out _);
        }
    }

    /// <summary>
    /// An entity that took a moved object's new placement keeps its own info: it is given the moved one's id and file
    /// object, so its sigil destination follows too.
    /// </summary>
    internal static void Rebind(CoreObjectInfo own, CoreObjectInfo moved) {
        if (own is not null && moved is not null && !ReferenceEquals(own, moved) && s_added.TryGetValue(moved, out var added)) {
            s_added.AddOrUpdate(own, added);
        }
    }

    private static void Watch() {
        try {
            s_reloadTimer = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
            var dir = Path.Combine(SpiralDB.ExtraPath, FOLDER);
            Directory.CreateDirectory(dir);
            s_watcher = MakeWatcher(dir, "*.json");
            var placements = Path.Combine(SpiralDB.ExtraPath, PLACEMENTS_FOLDER);
            Directory.CreateDirectory(placements);
            s_placementsWatcher = MakeWatcher(placements, PLACEMENTS_FILE);
            s_customNpcsWatcher = MakeWatcher(placements, Imlight.CoreLib.Game.World.CustomNpcs.FILE);
        }
        catch (Exception e) {
            Logger.Warning("Zone objects: changes to the files will need a server restart ({0}).", Logger.Args(e.Message));
        }
    }

    private static FileSystemWatcher MakeWatcher(string dir, string filter) {
        var watcher = new FileSystemWatcher(dir, filter) {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        watcher.Changed += (_, _) => ReloadSoon();
        watcher.Created += (_, _) => ReloadSoon();
        watcher.Deleted += (_, _) => ReloadSoon();
        watcher.Renamed += (_, _) => ReloadSoon();
        watcher.EnableRaisingEvents = true;

        return watcher;
    }

    private static void ReloadSoon() => s_reloadTimer?.Change(RELOAD_DELAY_IN_MS, Timeout.Infinite);

    // A timer thread runs it, where an exception would end the server.
    private static void Reload() {
        try {
            ReloadNow();
        }
        catch (Exception e) {
            Logger.Warning("Zone objects could not be read again: {0}", Logger.Args(e.Message));
        }
    }

    // Reads the files again and tells each loaded copy of a zone what changed for it.
    private static void ReloadNow() {
        Dictionary<string, ZoneObjectFile> before, after;
        lock (s_loadLock) {
            before = s_loaded ?? [];
            after = Load();
            s_loaded = after;
        }

        foreach (var (zone, (zonePath, variant)) in s_live) {
            LogNextLoadChanges(zonePath, variant, before, after);
            var change = Changes(zonePath, variant, before, after);
            if (change.Moved.Count + change.Added.Count + change.Removed.Count
                + change.AddedCreatures.Count + change.RemovedCreatures.Count == 0) {
                continue;
            }

            Logger.Information("Zone {0}{1}: {2} objects moved, {3} added and {4} removed, {5} creatures added and {6} removed while it runs.",
                Logger.Args(zonePath, variant.Length == 0 ? "" : $" ({variant})", change.Moved.Count, change.Added.Count,
                    change.Removed.Count, change.AddedCreatures.Count, change.RemovedCreatures.Count));
            zone.Tell(change);
        }
    }

    // Left-out objects are only read when a zone loads: a change to them is noted in the log for each copy that runs
    // now, so it is clear why it does not show yet.
    private static void LogNextLoadChanges(string zonePath, string variant, Dictionary<string, ZoneObjectFile> before,
                                           Dictionary<string, ZoneObjectFile> after) {
        static string Part(string zonePath, string variant, Dictionary<string, ZoneObjectFile> zones) {
            var files = new[] { KeyOf(zonePath, ""), string.IsNullOrEmpty(variant) ? null : KeyOf(zonePath, variant) }
                .Where(k => k is not null && zones.ContainsKey(k)).Select(k => zones[k]).ToList();

            return JsonSerializer.Serialize(files.SelectMany(f => f.Remove));
        }

        if (Part(zonePath, variant, before) != Part(zonePath, variant, after)) {
            Logger.Information("Zone {0}{1}: its left-out objects changed; they count from "
                + "the next time the zone loads.", Logger.Args(zonePath, variant.Length == 0 ? "" : $" ({variant})"));
        }
    }

    private static ZONE_102_PROTOCOL.MSG_ZONEOBJECTSCHANGED Changes(string zonePath, string variant,
                                                                     Dictionary<string, ZoneObjectFile> before,
                                                                     Dictionary<string, ZoneObjectFile> after) {
        var old = ObjectsFor(zonePath, variant, before);
        var now = ObjectsFor(zonePath, variant, after);
        var change = new ZONE_102_PROTOCOL.MSG_ZONEOBJECTSCHANGED();
        foreach (var (id, (fileVariant, obj)) in now) {
            if (!old.TryGetValue(id, out var was)) {
                change.Added.Add(InfoFor(zonePath, fileVariant, obj));

                continue;
            }

            // Another template, a pad that became an object, or a battle circle with other settings (its type, reach or
            // duel style): it goes and comes back as the new one.
            if (was.Object.TemplateID != obj.TemplateID || (was.Object.Sigil is null) != (obj.Sigil is null)
                || (was.Object.CombatSigil is null) != (obj.CombatSigil is null)
                || JsonSerializer.Serialize(was.Object.CombatSigil) != JsonSerializer.Serialize(obj.CombatSigil)
                || was.Object.CustomNpc != obj.CustomNpc) {
                change.Removed.Add(id);
                change.Added.Add(InfoFor(zonePath, fileVariant, obj));
            }
            else if (!SamePlacement(was.Object, obj)) {
                change.Moved[id] = InfoFor(zonePath, fileVariant, obj);
            }
        }

        foreach (var id in old.Keys.Where(id => !now.ContainsKey(id))) {
            change.Removed.Add(id);
        }

        // Creatures: one that is new, gone, moved or changed goes and comes back as a whole (its path, node and
        // spawner), by its path's id.
        var oldCreatures = CreaturesFor(zonePath, variant, before);
        var nowCreatures = CreaturesFor(zonePath, variant, after);
        foreach (var (pathId, (fileVariant, creature, sign)) in nowCreatures) {
            if (oldCreatures.TryGetValue(pathId, out var was) && was.Sign == sign) {
                continue;
            }

            if (was.Creature is not null) {
                change.RemovedCreatures.Add(pathId);
            }

            if (MakeCreature(zonePath, fileVariant, creature) is { } made) {
                change.AddedCreatures.Add((made.Path, [made.Node], made.Spawner));
            }
        }

        foreach (var pathId in oldCreatures.Keys.Where(id => !nowCreatures.ContainsKey(id))) {
            change.RemovedCreatures.Add(pathId);
        }

        return change;
    }

    private static Dictionary<ulong, (string Variant, ZoneObject Creature, string Sign)> CreaturesFor(
        string zonePath, string variant, Dictionary<string, ZoneObjectFile> zones) {
        var creatures = new Dictionary<ulong, (string, ZoneObject, string)>();
        foreach (var key in new[] { KeyOf(zonePath, ""), string.IsNullOrEmpty(variant) ? null : KeyOf(zonePath, variant) }) {
            if (key is null || !zones.TryGetValue(key, out var file)) {
                continue;
            }

            foreach (var c in file.Creatures) {
                creatures[GlobalIdOf(zonePath, file.Variant, c.Name, "/path")]
                    = (file.Variant, c, JsonSerializer.Serialize(new { c.TemplateID, c.X, c.Y, c.Z, c.Yaw, c.Custom }));
            }
        }

        return creatures;
    }

    private static Dictionary<ulong, (string Variant, ZoneObject Object)> ObjectsFor(string zonePath, string variant,
                                                                                    Dictionary<string, ZoneObjectFile> zones) {
        var objects = new Dictionary<ulong, (string, ZoneObject)>();
        foreach (var key in new[] { KeyOf(zonePath, ""), string.IsNullOrEmpty(variant) ? null : KeyOf(zonePath, variant) }) {
            if (key is null || !zones.TryGetValue(key, out var file)) {
                continue;
            }

            foreach (var obj in file.Objects) {
                objects[GlobalIdOf(zonePath, file.Variant, obj.Name)] = (file.Variant, obj);
            }
        }

        return objects;
    }

    private static CoreObjectInfo InfoFor(string zonePath, string variant, ZoneObject obj) {
        var info = MakeInfo(obj);
        s_added.AddOrUpdate(info, new Added(GlobalIdOf(zonePath, variant, obj.Name), obj));

        return info;
    }

    private static bool SamePlacement(ZoneObject a, ZoneObject b)
        => a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.Yaw == b.Yaw && a.Scale == b.Scale
           && a.StartState == b.StartState && JsonSerializer.Serialize(a.Sigil) == JsonSerializer.Serialize(b.Sigil)
           && JsonSerializer.Serialize(a.CombatSigil) == JsonSerializer.Serialize(b.CombatSigil);

    /// <summary>Whether any file makes objects of this template a door to its exit (the door component attaches).</summary>
    internal static bool IsExitDoor(ulong templateId)
        => Enabled && Zones.Values.Any(f => f.Exit?.Doors?.Contains(templateId) == true);

    /// <summary>The global id an added object takes, or null for the zone's own objects.</summary>
    internal static GID? GlobalIdOf(CoreObjectInfo info)
        => info is not null && s_added.TryGetValue(info, out var added) ? new GID(added.GlobalId) : null;

    /// <summary>
    /// Makes an object another part of the server adds to one running copy of a zone with MSG_ZONEOBJECTSCHANGED, such
    /// as the sigil pad of a housing gauntlet (Game/Housing/HouseGauntlets). Like a file's object it takes a global id
    /// of the added range, from its key, and a sigil pad names its own destination.
    /// </summary>
    /// <param name="key">What the object is, the same for the same object every time.</param>
    /// <param name="obj">The object.</param>
    internal static CoreObjectInfo MakeRuntimeObject(string key, ZoneObject obj) {
        var info = MakeInfo(obj);
        s_added.AddOrUpdate(info, new Added(RuntimeIdOf(key), obj));

        return info;
    }

    /// <summary>The global id <see cref="MakeRuntimeObject"/> gives the object of a key.</summary>
    internal static ulong RuntimeIdOf(string key)
        => GLOBAL_ID_MARK | (RandomGen.GenerateHash($"runtime/{key}".ToLowerInvariant()) & GLOBAL_ID_MASK);

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

    /// <summary>The battle circle settings of an added battle circle, or null for the zone's own.</summary>
    internal static ZoneCombatSigil CombatSigilOf(CoreObjectInfo info)
        => info is not null && s_added.TryGetValue(info, out var added) ? added.Object.CombatSigil : null;

    /// <summary>The lit state an added sigil pad names, or null.</summary>
    internal static string LitStateOf(CoreObjectInfo info)
        => info is not null && s_added.TryGetValue(info, out var added)
           && !string.IsNullOrWhiteSpace(added.Object.Sigil?.LitState) ? SigilStateName(added.Object.Sigil.LitState) : null;

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

        if (MakeCreature(zonePath, variant, creature) is not { } made) {
            return false;
        }

        results.NodeData.m_nodeList ??= [];
        results.NodeData.m_nodeList.Add(made.Node);
        results.PathData.m_pathList ??= [];
        results.PathData.m_pathList.Add(made.Path);
        results.SpawnData.m_spawners ??= [];
        results.SpawnData.m_spawners.Add(made.Spawner);

        return true;
    }

    // The customs of the files' creatures, by their spawner's id (ZonePath marks what such a spawner spawns).
    private static readonly ConcurrentDictionary<ulong, int> s_spawnerCustoms = new();

    /// <summary>The custom (Customs.cs) a spawner made for a file's creature spawns, or 0.</summary>
    internal static int CustomOfSpawner(ulong spawnerId) => s_spawnerCustoms.TryGetValue(spawnerId, out var id) ? id : 0;

    private static (NodeObject Node, PathObjectTemplate Path, SpawnObject Spawner)? MakeCreature(string zonePath, string variant,
                                                                                                 ZoneObject creature) {
        var templateId = creature.TemplateID;
        if (creature.Custom > 0) {
            var custom = Imlight.CoreLib.Game.Combat.Customs.Get(creature.Custom);
            if (custom is null) {
                Logger.Warning("Zone {0}: creature {1} is custom {2}, which does not exist; it is the creature itself.",
                    Logger.Args(zonePath, creature.Name, creature.Custom));
            }
            else if (templateId == 0) {
                templateId = custom.BaseTemplateID;
            }
        }

        if (templateId == 0) {
            Logger.Warning("Zone {0}: creature {1} has no creature; not added.", Logger.Args(zonePath, creature.Name));

            return null;
        }

        var nodeId = new GID(GlobalIdOf(zonePath, variant, creature.Name, "/node"));
        var pathId = new GID(GlobalIdOf(zonePath, variant, creature.Name, "/path"));
        var spawnerId = new GID(GlobalIdOf(zonePath, variant, creature.Name, "/spawner"));
        if (creature.Custom > 0) {
            s_spawnerCustoms[spawnerId.Full] = creature.Custom;
        }
        else {
            s_spawnerCustoms.TryRemove(spawnerId.Full, out _);
        }

        var node = new NodeObject {
            m_id = nodeId,
            m_location = new Vector3(creature.X, creature.Y, creature.Z),
            // A node's direction is in degrees.
            m_direction = (float) (creature.Yaw * (180.0 / Math.PI)),
        };
        var path = new PathObjectTemplate {
            m_id = pathId,
            m_name = creature.Name,
            m_nodeIDs = [nodeId],
        };
        var spawner = new SpawnObject {
            m_id = spawnerId,
            m_name = creature.Name,
            m_active = true,
            m_maxNumberOfSpawns = 1,
            m_atLeastOneSpawn = true,
            m_spawnTime = 0,
            m_spawnList = [
                new SpawnItem {
                    m_percentChance = 100,
                    m_objectInfo = new SpawnObjectInfo {
                        m_templateID = templateId,
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
        };

        return (node, path, spawner);
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
            info.m_startState = string.IsNullOrWhiteSpace(obj.Sigil.LitState) ? SIGIL_LIT_STATE : SigilStateName(obj.Sigil.LitState);
        }

        info.m_loadingType = LoadingType.DYNAMIC_SERVER;

        return info;
    }

    private static Dictionary<string, ZoneObjectFile> Load() {
        var zones = new Dictionary<string, ZoneObjectFile>();
        var off = new HashSet<string>();
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
                if (!file.Enabled) {
                    off.Add(KeyOf(file.Zone, file.Variant));
                    Logger.Information("Zone objects {0} are switched off (Enabled is false); skipped.", Logger.Args(fileName));

                    continue;
                }

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
                file.ReplaceSpawns ??= [];
                file.DistinctSpawners = [.. (file.DistinctSpawners ?? []).Where(n => !string.IsNullOrWhiteSpace(n))];
                if (file.Exit is not null) {
                    file.Exit.Triggers ??= [];
                    file.Exit.Doors = [.. (file.Exit.Doors ?? []).Where(d => d != 0)];
                }
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

        ApplyPlacements(zones, off);
        AddCustomNpcs(zones);
        Logger.Information("Zone objects loaded from {0} files ({1} objects, {2} creatures).",
            Logger.Args(zones.Count, zones.Values.Sum(z => z.Objects.Count), zones.Values.Sum(z => z.Creatures.Count)));

        return zones;
    }

    internal sealed class Placement {
        public ulong? TemplateID { get; set; }
        public float? X { get; set; }
        public float? Y { get; set; }
        public float? Z { get; set; }
        public float? Yaw { get; set; }
        public float? Scale { get; set; }
        public bool Removed { get; set; }
    }

    // The Customs tab's placements over the files: moved, swapped, added and removed objects.
    // A file that is switched off takes the placements of its room with it.
    private static void ApplyPlacements(Dictionary<string, ZoneObjectFile> zones, HashSet<string> off) {
        var path = Path.Combine(SpiralDB.ExtraPath, PLACEMENTS_FOLDER, PLACEMENTS_FILE);
        if (!File.Exists(path)) {
            return;
        }

        Dictionary<string, Dictionary<string, Placement>> rooms;
        try {
            rooms = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Placement>>>(File.ReadAllText(path), s_options);
        }
        catch (Exception e) {
            Logger.Warning("Zone objects: {0} could not be read: {1}", Logger.Args(PLACEMENTS_FILE, e.Message));

            return;
        }

        foreach (var (room, placements) in rooms ?? []) {
            if (string.IsNullOrWhiteSpace(room) || placements is null) {
                continue;
            }

            var at = room.IndexOf('@');
            var zone = at < 0 ? room : room[..at];
            var variant = at < 0 ? "" : room[(at + 1)..];
            if (off.Contains(KeyOf(zone, variant))) {
                continue;
            }

            if (!zones.TryGetValue(KeyOf(zone, variant), out var file)) {
                // Objects added to a zone with no file of its own.
                file = new ZoneObjectFile { Zone = zone, Variant = variant };
                zones[KeyOf(zone, variant)] = file;
            }

            foreach (var (name, placement) in placements) {
                if (string.IsNullOrWhiteSpace(name) || placement is null) {
                    continue;
                }

                var obj = file.Objects.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
                if (placement.Removed) {
                    if (obj is not null) {
                        file.Objects.Remove(obj);
                    }

                    continue;
                }

                if (obj is null) {
                    if (placement.TemplateID is not > 0 || file.Creatures.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))) {
                        continue;
                    }

                    obj = new ZoneObject { Name = name, Notes = "Added in the Customs tab." };
                    file.Objects.Add(obj);
                }

                obj.TemplateID = placement.TemplateID is > 0 ? placement.TemplateID.Value : obj.TemplateID;
                obj.X = placement.X ?? obj.X;
                obj.Y = placement.Y ?? obj.Y;
                obj.Z = placement.Z ?? obj.Z;
                obj.Yaw = placement.Yaw ?? obj.Yaw;
                obj.Scale = placement.Scale is > 0 ? placement.Scale.Value : obj.Scale;
            }
        }
    }

    // The Customs tab's vendors and trainers, each an object of its zone (in the file every copy of the zone uses).
    private static void AddCustomNpcs(Dictionary<string, ZoneObjectFile> zones) {
        foreach (var npc in Imlight.CoreLib.Game.World.CustomNpcs.Load()) {
            if (!zones.TryGetValue(KeyOf(npc.Zone, ""), out var file)) {
                file = new ZoneObjectFile { Zone = npc.Zone };
                zones[KeyOf(npc.Zone, "")] = file;
            }

            if (file.Objects.Any(o => string.Equals(o.Name, npc.Tag, StringComparison.OrdinalIgnoreCase))) {
                Logger.Warning("Custom NPC {0}: {1} already has an object called {2}; skipped.", Logger.Args(npc.Id, npc.Zone, npc.Tag));

                continue;
            }

            file.Objects.Add(new ZoneObject {
                Name = npc.Tag, Notes = "A vendor or trainer of the Customs tab.", TemplateID = npc.TemplateID,
                X = npc.X, Y = npc.Y, Z = npc.Z, Yaw = npc.Yaw,
                CustomNpc = Imlight.CoreLib.Game.World.CustomNpcs.SignatureOf(npc),
            });
        }
    }

    private static bool IsUsable(string fileName, ZoneObject obj, HashSet<string> names) {
        if (obj is null) {
            return false;
        }

        if ((obj.TemplateID == 0 && obj.Custom <= 0) || string.IsNullOrWhiteSpace(obj.Name)) {
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
