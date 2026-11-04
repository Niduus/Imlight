/*
 * Teleporter hubs of the app's Customs tab (Niduus, 2026-10-25: "a TP hud of 3 teleporters and to make those sub
 * teleporters link to the hub. This way, I can make questing faster ... on streets that do not have the hubs"), read
 * from spiraldb-extra/Customs/Teleporters.json. The game files are never changed.
 *
 * A hub is a ring of teleporter stones (3 by default) like the game's own hub on Colossus Boulevard, each linked to one
 * street teleporter out on a street. Pressing X at a hub stone sends the player to its street teleporter; pressing X at
 * a street teleporter sends them back to the hub. The stones are the game's own Colossus Boulevard teleporters
 * (WC_Colossus_TeleporterA/B/C and their "To Teleporter Hub" stones), so they look and sound like the real ones.
 *
 * The file is a list. Each entry is one hub:
 *   Id          its number (1, 2, 3...). It gives its stones' zone tags (CustomTeleporter_<Id>_<pad>, with _Street for
 *               a street teleporter) and so their global ids.
 *   Name        the hub's name, shown on its street teleporters' press-X box ("To <Name>").
 *   Zone, X, Y, Z, Yaw  where the hub stands: the middle of the ring.
 *   Style       the world whose teleporter stones it uses (Niduus, 2026-10-25: "maybe style of the hub"): WizardCity
 *               (Colossus Boulevard, the default), Marleybone, MooShu, Dragonspyre, Celestia or Zafaria.
 *   CenterPiece true puts the style's middle piece (the hub base the game's own hubs stand round) in the middle.
 *   LandX, LandY, LandZ, LandYaw  where a player coming back from a street arrives (Niduus: "TP location to avoid getting
 *               stuck"); left out, the middle of the ring (a step towards stone 1 when there is a middle piece).
 *   Pads        the hub's stones, in order:
 *     Name        the stone's name on its press-X box, such as the street it goes to.
 *     X, Y, Z, Yaw  where the stone stands.
 *     Look        the stone's template, 0 for the style's stone of its place in the ring (A, B, C, A...).
 *     Street      its street teleporter, or null when it has none yet (the stone then has nothing to press):
 *       Zone, X, Y, Z, Yaw  where the street teleporter stands, and Look as above (0: the style's matching street
 *                   stone). LandX, LandY, LandZ, LandYaw: where a player sent there arrives; left out, a step in front.
 *
 * The stones are added to their zone's objects by ZoneObjects.cs, which watches this file as it does its own, so a hub
 * or street teleporter that is added, moved or deleted is so at once in every running copy of its zone.
 *
 * Portals (Niduus, 2026-11-04: "allow for interaction prompts to be made and customized ... remake how some of the
 * teleporters are with the shadow gate one ... a prompt that changes the name layer to the direction and able to interact
 * to teleport when in it. Include a setting in this to allow for interaction or walking. I would also like a 'Teleport
 * here' feature ... how it does in housing") are single teleporters of their own, in spiraldb-extra/Customs/Portals.json:
 *   Id          its number (1, 2, 3...); its zone tag is CustomTeleporter_Portal_<Id>.
 *   Name        the prompt's name line, such as "To Colossus Boulevard" or "North: Ravenwood"; empty: "To <place it goes>".
 *   Prompt      the prompt's second line: Teleport (the default), Travel, Enter, Exit, Interact (the game's own "Press X
 *               to ..." lines, which show the player's own key) or Custom, which shows Text as written.
 *   Text        the Custom prompt line.
 *   Walk        true: walking into it teleports (no prompt), like a house teleporter. false: press X in it.
 *   Reach       how near (in game units) a player has to be: to see the prompt, or to be sent when Walk is on.
 *   Zone, X, Y, Z, Yaw, Look  where it stands and its template (0: the Shadow Portal house teleporter, 1719467).
 *   ToZone, ToX, ToY, ToZ, ToYaw  where it sends ("Teleport here" sets it to where the wizard stands, in any zone).
 * Every setting is read when it is used, so a change counts at once; a new look or place makes the portal again.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.World;

internal static class CustomTeleporters {

    internal sealed class Hub {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Zone { get; set; } = "";
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Yaw { get; set; }
        public string Style { get; set; } = "";
        public bool CenterPiece { get; set; }
        public float? LandX { get; set; }
        public float? LandY { get; set; }
        public float? LandZ { get; set; }
        public float? LandYaw { get; set; }
        public List<Pad> Pads { get; set; } = [];
    }

    internal sealed class Pad {
        public string Name { get; set; } = "";
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Yaw { get; set; }
        public ulong Look { get; set; }
        public Street Street { get; set; }
    }

    internal sealed class Street {
        public string Zone { get; set; } = "";
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Yaw { get; set; }
        public ulong Look { get; set; }
        public float? LandX { get; set; }
        public float? LandY { get; set; }
        public float? LandZ { get; set; }
        public float? LandYaw { get; set; }
    }

    /// <summary>One stone in the game: a hub stone or a street teleporter, and where pressing X at it sends.</summary>
    internal sealed record Stone(string Tag, string Name, string Zone, float X, float Y, float Z, float Yaw, ulong Look,
                                 string ToZone, float ToX, float ToY, float ToZ, float ToYaw) {
        /// <summary>The prompt's second line: a lang key of the game's own lines, or the Custom line's key.</summary>
        public string TextKey { get; init; } = TELEPORT_TEXT_KEY;
        /// <summary>The Custom prompt line as written, or null.</summary>
        public string CustomText { get; init; }
        /// <summary>Walking into it sends the player (no prompt).</summary>
        public bool WalkIn { get; init; }
        /// <summary>How near a player has to be (0: the game's usual 300 for the prompt).</summary>
        public float Reach { get; init; }
        /// <summary>A portal of Portals.json (not a hub stone).</summary>
        public int PortalId { get; init; }
    }

    /// <summary>A portal of Portals.json: one teleporter with its own prompt.</summary>
    internal sealed class Portal {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Prompt { get; set; } = "";
        public string Text { get; set; } = "";
        public bool Walk { get; set; }
        public float Reach { get; set; }
        public string Zone { get; set; } = "";
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Yaw { get; set; }
        public ulong Look { get; set; }
        public string ToZone { get; set; } = "";
        public float ToX { get; set; }
        public float ToY { get; set; }
        public float ToZ { get; set; }
        public float ToYaw { get; set; }
    }

    internal const string PORTALS_FILE = "Portals.json";
    private const string PORTAL_TAG_PREFIX = TAG_PREFIX + "Portal_";

    /// <summary>The Shadow Portal house teleporter (SP_Teleporter_Shadow), a new portal's look.</summary>
    internal const ulong SHADOW_PORTAL = 1719467;

    // "Press X to Teleport", the Colossus Boulevard stones' own line.
    internal const string TELEPORT_TEXT_KEY = "GUI_00005578";

    /// <summary>
    /// The lang line that is only the NPC block's FIRSTNAME ("#1:$NPC_FIRSTNAME$"): with the Custom prompt's text in that
    /// slot, the prompt's second line shows it as written.
    /// </summary>
    internal const string CUSTOM_TEXT_KEY = "NPCFormats_First_Only";

    // The prompt lines a portal can pick: the game's own (they show the player's own interact key).
    private static readonly Dictionary<string, string> s_prompts = new(StringComparer.OrdinalIgnoreCase) {
        ["Teleport"] = TELEPORT_TEXT_KEY,      // Press X to Teleport
        ["Travel"] = "GUI_00008430",            // Press X to Travel
        ["Enter"] = "GUI_EnterDoor",            // Press X to Enter
        ["Exit"] = "GUI_00006703",              // Press X to Exit
        ["Interact"] = "GUI_00000034",          // Press X to Interact
    };

    // How near a player has to be to a portal by default: in it, not across the street.
    private const float PORTAL_REACH = 120f;

    internal const string FILE = "Teleporters.json";
    private const string FOLDER = "Customs";
    private const string TAG_PREFIX = "CustomTeleporter_";

    /// <summary>A world's hub teleporters: the middle piece, the hub's three stones and the street stone of each.</summary>
    internal sealed record HubStyle(ulong Center, ulong[] Hub, ulong[] Street);

    // The game's own hub teleporters of each world (Root.wad ObjectData). Wizard City is Colossus Boulevard's (Baron
    // Tower, Briskbreeze Tower, Hildreth Perch and their "To Teleporter Hub" stones, round WC_Teleporter_Hub).
    internal static readonly Dictionary<string, HubStyle> Styles = new(StringComparer.OrdinalIgnoreCase) {
        ["WizardCity"] = new(1488141, [1488125, 1488127, 1488130], [1488126, 1488129, 1488131]),
        ["Marleybone"] = new(164781, [164782, 164783, 164784], [164785, 164786, 164787]),
        ["MooShu"] = new(164793, [164794, 164795, 164796], [164801, 164802, 164803]),
        ["Dragonspyre"] = new(191075, [191077, 191079, 191081], [191078, 191080, 191082]),
        ["Celestia"] = new(227323, [227312, 1687018, 1687019], [227315, 1687017, 1687020]),
        ["Zafaria"] = new(557097, [557096, 557100, 557096], [557099, 557101, 557099]),
    };

    private static HubStyle StyleOf(string name) => Styles.GetValueOrDefault(name?.Replace(" ", "") ?? "") ?? Styles["WizardCity"];

    // How far from the middle piece a player coming back to a hub that has one arrives, towards stone 1.
    private const float CENTER_CLEARANCE = 125f;

    // How far in front of a street teleporter a player sent to it arrives, so they do not land inside the stone.
    private const float ARRIVE_DISTANCE = 120f;

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    // The stones as last read, by zone tag. ZoneObjects reads the file again whenever it changes.
    private static Dictionary<string, Stone> s_byTag = new(StringComparer.OrdinalIgnoreCase);

    internal static bool IsCustomTag(string zoneTag) => zoneTag?.StartsWith(TAG_PREFIX, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The stone of a zone tag, or null.</summary>
    internal static Stone For(string zoneTag)
        => IsCustomTag(zoneTag) && s_byTag.TryGetValue(zoneTag, out var stone) ? stone : null;

    /// <summary>
    /// Reads the file and returns every stone. ZoneObjects calls it each time it reads its files, and puts the stones
    /// in their zones.
    /// </summary>
    internal static List<Stone> Load() {
        var path = Path.Combine(SpiralDB.ExtraPath, FOLDER, FILE);
        List<Hub> hubs;
        try {
            hubs = File.Exists(path) ? JsonSerializer.Deserialize<List<Hub>>(File.ReadAllText(path), s_options) ?? [] : [];
        }
        catch (Exception e) {
            // The last good hubs stay (the app writes the file whole, so this is a file broken by hand).
            Logger.Warning("Teleporter hubs: {0} could not be read: {1}", Logger.Args(FILE, e.Message));
            hubs = null;
        }

        var portals = ReadPortals(out var portalsOk);
        var byTag = new Dictionary<string, Stone>(StringComparer.OrdinalIgnoreCase);
        if (hubs is null) {
            foreach (var (tag, stone) in s_byTag.Where(kv => kv.Value.PortalId == 0)) {
                byTag[tag] = stone;
            }
        }

        if (!portalsOk) {
            foreach (var (tag, stone) in s_byTag.Where(kv => kv.Value.PortalId != 0)) {
                byTag[tag] = stone;
            }
        }
        else {
            AddPortals(portals, byTag);
        }

        hubs ??= [];
        var ids = new HashSet<int>();
        foreach (var hub in hubs) {
            if (hub is null || hub.Id <= 0 || string.IsNullOrWhiteSpace(hub.Zone)) {
                Logger.Warning("Teleporter hubs: an entry has no Id or Zone; skipped.");

                continue;
            }

            if (!ids.Add(hub.Id)) {
                Logger.Warning("Teleporter hubs: a second hub has Id {0}; skipped.", Logger.Args(hub.Id));

                continue;
            }

            var zone = hub.Zone.Trim();
            var style = StyleOf(hub.Style);
            var (landX, landY) = (hub.X, hub.Y);
            if (hub.CenterPiece && hub.Pads is [{ } first, ..]) {
                var (dx, dy) = (first.X - hub.X, first.Y - hub.Y);
                var length = MathF.Sqrt((dx * dx) + (dy * dy));
                if (length > 1) {
                    (landX, landY) = (hub.X + (dx / length * CENTER_CLEARANCE), hub.Y + (dy / length * CENTER_CLEARANCE));
                }
            }

            (landX, landY) = (hub.LandX ?? landX, hub.LandY ?? landY);
            var (landZ, landYaw) = (hub.LandZ ?? hub.Z, hub.LandYaw ?? hub.Yaw);
            if (hub.CenterPiece) {
                var centerTag = $"{TAG_PREFIX}{hub.Id}_Center";
                byTag[centerTag] = new Stone(centerTag, "", zone, hub.X, hub.Y, hub.Z, hub.Yaw, style.Center, "", 0, 0, 0, 0);
            }

            var hubName = string.IsNullOrWhiteSpace(hub.Name) ? "Teleporter Hub" : hub.Name.Trim();
            var pads = hub.Pads ?? [];
            for (var i = 0; i < pads.Count; i++) {
                if (pads[i] is not { } pad) {
                    continue;
                }

                var street = string.IsNullOrWhiteSpace(pad.Street?.Zone) ? null : pad.Street;
                var padName = string.IsNullOrWhiteSpace(pad.Name) ? $"Teleporter {i + 1}" : pad.Name.Trim();
                var (arriveX, arriveY) = street is null ? (0f, 0f) : InFront(street.X, street.Y, street.Yaw);
                (arriveX, arriveY) = (street?.LandX ?? arriveX, street?.LandY ?? arriveY);
                var padTag = $"{TAG_PREFIX}{hub.Id}_{i + 1}";
                byTag[padTag] = new Stone(padTag, padName, zone, pad.X, pad.Y, pad.Z, pad.Yaw,
                    pad.Look != 0 ? pad.Look : style.Hub[i % style.Hub.Length],
                    street?.Zone.Trim() ?? "", arriveX, arriveY, street?.LandZ ?? street?.Z ?? 0, street?.LandYaw ?? street?.Yaw ?? 0);

                if (street is not null) {
                    var streetTag = padTag + "_Street";
                    byTag[streetTag] = new Stone(streetTag, $"To {hubName}", street.Zone.Trim(), street.X, street.Y, street.Z, street.Yaw,
                        street.Look != 0 ? street.Look : style.Street[i % style.Street.Length],
                        zone, landX, landY, landZ, landYaw);
                }
            }
        }

        s_byTag = byTag;
        Logger.Information("Teleporter hubs: {0} hubs and {1} portals read, with {2} teleporters in all.",
            Logger.Args(ids.Count, byTag.Values.Count(s => s.PortalId != 0), byTag.Count));

        return [.. byTag.Values];
    }

    /// <summary>
    /// What tells two versions of a stone apart for ZoneObjects: a stone whose name changed is taken away and made again,
    /// so its press-X box shows the new one. Where it sends is read when it is used, so a new destination counts at once.
    /// </summary>
    internal static string SignatureOf(Stone stone) => stone.PortalId != 0 ? "Portal" : "Teleporter:" + stone.Name;

    /// <summary>
    /// The prompt's name line: the stone's name, or for a portal without one "To" and the name of the place it goes.
    /// </summary>
    internal static string NameOf(Stone stone) {
        if (stone is null) {
            return null;
        }

        if (stone.Name.Length > 0 || stone.PortalId == 0) {
            return stone.Name;
        }

        return string.IsNullOrEmpty(stone.ToZone) ? $"Portal {stone.PortalId}" : "To " + PlaceName(stone.ToZone);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> s_placeNames = new(StringComparer.OrdinalIgnoreCase);

    // The game's name of a zone ("Colossus Boulevard"), or the last part of its path when it has none.
    private static string PlaceName(string zone) => s_placeNames.GetOrAdd(zone, z => {
        try {
            var key = Imlight.CoreLib.Game.TeamUp.TeamUpSigils.DisplayKeyNow(z);
            if (key.Length > 0 && Imlight.CoreLib.Shared.Resources.Locale.GetEnglishName(key) is { Length: > 0 } name && name != key) {
                return name;
            }
        }
        catch (Exception e) {
            Logger.Warning("Portals: the name of {0} could not be read: {1}", Logger.Args(z, e.Message));
        }

        var last = z.Split('/', '\\').LastOrDefault(p => p.Length > 0) ?? z;

        return last.Contains('_') ? last[(last.LastIndexOf('_') + 1)..] : last;
    });

    // ---------------------------------------------------------------------
    // Portals
    // ---------------------------------------------------------------------

    private static readonly object s_portalsLock = new();

    /// <summary>The portals in Portals.json; ok is false when the file is there but cannot be read.</summary>
    internal static List<Portal> ReadPortals(out bool ok) {
        ok = true;
        var path = Path.Combine(SpiralDB.ExtraPath, FOLDER, PORTALS_FILE);
        try {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<Portal>>(File.ReadAllText(path), s_options) ?? [] : [];
        }
        catch (Exception e) {
            Logger.Warning("Portals: {0} could not be read: {1}", Logger.Args(PORTALS_FILE, e.Message));
            ok = false;

            return [];
        }
    }

    /// <summary>
    /// Changes Portals.json (the in-game .portal command): reads it, lets change edit the list and writes it whole.
    /// ZoneObjects sees the file change and puts the portals in the game. False when the file cannot be read.
    /// </summary>
    internal static bool EditPortals(Func<List<Portal>, bool> change) {
        lock (s_portalsLock) {
            var portals = ReadPortals(out var ok);
            if (!ok) {
                return false;
            }

            if (!change(portals)) {
                return true;
            }

            var dir = Path.Combine(SpiralDB.ExtraPath, FOLDER);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, PORTALS_FILE);
            File.WriteAllText(path + ".saving", JsonSerializer.Serialize(portals.OrderBy(p => p.Id).ToList(), s_write));
            File.Move(path + ".saving", path, overwrite: true);

            return true;
        }
    }

    private static readonly JsonSerializerOptions s_write = new() { WriteIndented = true };

    private static void AddPortals(List<Portal> portals, Dictionary<string, Stone> byTag) {
        var ids = new HashSet<int>();
        foreach (var portal in portals) {
            if (portal is null || portal.Id <= 0 || string.IsNullOrWhiteSpace(portal.Zone)) {
                Logger.Warning("Portals: an entry has no Id or Zone; skipped.");

                continue;
            }

            if (!ids.Add(portal.Id)) {
                Logger.Warning("Portals: a second portal has Id {0}; skipped.", Logger.Args(portal.Id));

                continue;
            }

            var custom = string.Equals(portal.Prompt?.Trim(), "Custom", StringComparison.OrdinalIgnoreCase)
                         && !string.IsNullOrWhiteSpace(portal.Text);
            var tag = PORTAL_TAG_PREFIX + portal.Id;
            byTag[tag] = new Stone(tag, portal.Name?.Trim() ?? "", portal.Zone.Trim(), portal.X, portal.Y, portal.Z, portal.Yaw,
                portal.Look != 0 ? portal.Look : SHADOW_PORTAL,
                portal.ToZone?.Trim() ?? "", portal.ToX, portal.ToY, portal.ToZ, portal.ToYaw) {
                TextKey = custom ? CUSTOM_TEXT_KEY : s_prompts.GetValueOrDefault(portal.Prompt?.Trim() ?? "") ?? TELEPORT_TEXT_KEY,
                CustomText = custom ? portal.Text.Trim() : null,
                WalkIn = portal.Walk,
                Reach = portal.Reach > 0 ? Math.Clamp(portal.Reach, 20f, 1000f) : PORTAL_REACH,
                PortalId = portal.Id,
            };
        }
    }

    // A step in front of a spot, the way a wizard standing there with that yaw faces.
    private static (float X, float Y) InFront(float x, float y, float yaw)
        => (x - (MathF.Sin(yaw) * ARRIVE_DISTANCE), y - (MathF.Cos(yaw) * ARRIVE_DISTANCE));

}
