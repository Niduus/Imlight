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
                                 string ToZone, float ToX, float ToY, float ToZ, float ToYaw);

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
        if (!File.Exists(path)) {
            s_byTag = new(StringComparer.OrdinalIgnoreCase);

            return [];
        }

        List<Hub> hubs;
        try {
            hubs = JsonSerializer.Deserialize<List<Hub>>(File.ReadAllText(path), s_options) ?? [];
        }
        catch (Exception e) {
            // The last good list stays (the app writes the file whole, so this is a file broken by hand).
            Logger.Warning("Teleporter hubs: {0} could not be read: {1}", Logger.Args(FILE, e.Message));

            return [.. s_byTag.Values];
        }

        var byTag = new Dictionary<string, Stone>(StringComparer.OrdinalIgnoreCase);
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
        Logger.Information("Teleporter hubs: {0} hubs read, with {1} stones in all.", Logger.Args(ids.Count, byTag.Count));

        return [.. byTag.Values];
    }

    /// <summary>
    /// What tells two versions of a stone apart for ZoneObjects: a stone whose name changed is taken away and made again,
    /// so its press-X box shows the new one. Where it sends is read when it is used, so a new destination counts at once.
    /// </summary>
    internal static string SignatureOf(Stone stone) => "Teleporter:" + stone.Name;

    // A step in front of a spot, the way a wizard standing there with that yaw faces.
    private static (float X, float Y) InFront(float x, float y, float yaw)
        => (x - (MathF.Sin(yaw) * ARRIVE_DISTANCE), y - (MathF.Cos(yaw) * ARRIVE_DISTANCE));

}
