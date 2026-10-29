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
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Imlight.Setup;

/// <summary>How a kind of custom is kept: a file each, entries of a list, entries of a map, or one whole file.</summary>
internal enum ShareShape { Files, List, Map, Whole }

/// <summary>
/// A kind of custom the Share tab can export and import. Where it goes is fixed here, never taken from a shared file:
/// InExtra puts Path under spiraldb-extra, otherwise under the Server folder. Files: Path is a folder with one file per
/// custom. List: Path is a file holding an array (at ListProperty when set) of entries told apart by KeyField. Map: Path
/// is a file whose properties are the entries. Whole: the file is one custom. NumberField: entries numbered by the app
/// (1, 2, 3...) that can take a free number when the one they have is taken. OwnOnly: the folder or file also ships with
/// Imlight, so only files changed or made on this PC count as the player's.
/// </summary>
internal sealed record ShareKind(string Id, string Title, string Icon, ShareShape Shape, bool InExtra, string Path,
    string? KeyField = null, string? ListProperty = null, string? NumberField = null, bool OwnOnly = false, string? NameField = "Name");

/// <summary>One custom in a share file or on this PC: its kind, what tells it apart, the name shown, and its contents.</summary>
internal sealed record ShareItem(ShareKind Kind, string Key, string Name, JsonNode Data);

/// <summary>A custom of a share file, checked against this PC's.</summary>
internal sealed class ShareIncoming {

    internal required ShareItem Item { get; init; }

    /// <summary>The path it writes, for the backup and the safety check.</summary>
    internal required string Target { get; init; }

    /// <summary>This PC has a custom with its key already.</summary>
    internal bool Exists { get; init; }

    /// <summary>This PC's custom with its key is just the same.</summary>
    internal bool Same { get; init; }

    /// <summary>It can take a free number instead of replacing this PC's custom.</summary>
    internal bool CanRenumber => Exists && !Same && Item.Kind.NumberField is not null;

    /// <summary>Ticked to import.</summary>
    internal bool Use { get; set; }

    /// <summary>Takes a free number instead of replacing (only when CanRenumber).</summary>
    internal bool AsNew { get; set; }

}

/// <summary>A share file read and checked: the customs it brings and the ones left out, with why.</summary>
internal sealed record ShareReading(string MadeWith, string Made, List<ShareIncoming> Items, List<string> LeftOut);

/// <summary>
/// Sharing customs (Niduus, 2026-10-29: "have every single custom able to be imported and exported for easy sharing with
/// safeguards to make sure those files do not touch anything else besides Ahziael"). A share file (.ahziael) is plain
/// JSON text: the format's name, and a list of customs, each its kind, key, name and contents. Nothing in it is run.
/// Import only writes Ahziael's own custom files: the place each kind goes is fixed in s_kinds, a key must be a plain
/// name (no folders, no ".."), the path is checked to be inside that place, and contents must be JSON of the shape
/// the kind has. Before writing, the files it changes are zipped into the install folder's "Import backups", so Undo
/// last import puts them back.
/// </summary>
internal static class CustomShare {

    internal const string EXTENSION = ".ahziael";
    internal const string FORMAT = "Ahziael customs";
    internal const int FORMAT_VERSION = 1;
    internal const string BACKUP_FOLDER = "Import backups";
    private const string UNDO_LIST = "undo.json";
    private const long MAX_FILE_BYTES = 64L * 1024 * 1024;
    private const int MAX_ITEMS = 50_000;

    private static readonly JsonDocumentOptions s_read = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 96 };
    private static readonly JsonSerializerOptions s_write = new() { WriteIndented = true };

    // Plain names only: letters, numbers and a few marks, no slashes and no "..", at most 150 long.
    private static readonly Regex s_fileKey = new(@"^[A-Za-z0-9][A-Za-z0-9 _\-@.,'()&+]{0,149}$", RegexOptions.CultureInvariant);
    private static readonly Regex s_number = new(@"^[1-9][0-9]{0,8}$", RegexOptions.CultureInvariant);

    /// <summary>Every kind of custom, in the order the Share tab lists them.</summary>
    internal static readonly ShareKind[] s_kinds = [
        new("custom", "Custom creatures", "paw", ShareShape.Files, true, "Customs", NumberField: "CustomID"),
        new("boss", "Boss changes", "crown", ShareShape.Files, true, "BossOverrides"),
        new("bosscheats", "Boss cheats", "bolt", ShareShape.Files, true, "BossCheats", OwnOnly: true),
        new("playercheat", "Player cheats", "bolt", ShareShape.List, true, "PlayerCheats.json", KeyField: "Name", ListProperty: "Cheats"),
        new("area", "Custom areas", "map", ShareShape.Files, true, "ZoneObjects", OwnOnly: true, NameField: "Zone"),
        new("room", "Room bosses", "home", ShareShape.Map, true, "Customs/Rooms.json"),
        new("placement", "Moved and added objects", "home", ShareShape.Map, true, "Customs/Placements.json"),
        new("npc", "Vendors and trainers", "bag", ShareShape.List, true, "Customs/VendorsTrainers.json", KeyField: "Id", NumberField: "Id"),
        new("npcchange", "Changed vendors and trainers", "bag", ShareShape.List, true, "Customs/NpcChanges.json", KeyField: "TemplateID"),
        new("hub", "Teleporter hubs", "map", ShareShape.List, true, "Customs/Teleporters.json", KeyField: "Id", NumberField: "Id"),
        new("recipe", "Recipe changes", "flask", ShareShape.List, true, "Customs/RecipeChanges.json", KeyField: "Id"),
        new("drops", "Drop tables", "gift", ShareShape.Files, true, "DropTables", OwnOnly: true),
        new("quest", "Quest changes", "flag", ShareShape.List, true, "QuestOptions.json", KeyField: "Quest", ListProperty: "Quests", NameField: "Quest"),
        new("code", "Promo codes", "gift", ShareShape.List, false, "codes.json", KeyField: "Code", ListProperty: "Codes", NameField: "Code"),
        new("benefit", "Member benefits", "star", ShareShape.List, false, "member-benefits.json", KeyField: "Key", ListProperty: "Benefits", NameField: "Key"),
        new("sigil", "House gauntlet sigils", "home", ShareShape.Map, false, "gauntlet-sigils.json"),
        new("crownshop", "Crown Shop", "crown", ShareShape.Whole, false, "crown-shop.json"),
        new("events", "Events", "clock", ShareShape.Whole, false, "events.json"),
        new("loyalty", "Loyalty Store", "bag", ShareShape.Whole, false, "loyalty-store.json"),
        new("loyaltyrotations", "Loyalty Store wares", "bag", ShareShape.Whole, true, "LoyaltyStore/Rotations.json", OwnOnly: true),
        new("tameddrops", "Tamed drops", "gift", ShareShape.Whole, false, "tamed-drops.json"),
        new("garden", "Garden growth", "sparkle", ShareShape.Whole, false, "garden-growth.json"),
        new("announcements", "Announcement words", "chat", ShareShape.Whole, false, "announcements.json"),
        new("dropodds", "Drop odds", "dice", ShareShape.Whole, true, "DropOdds.json", OwnOnly: true),
    ];

    internal static ShareKind? KindOf(string id) => s_kinds.FirstOrDefault(k => k.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------------
    // Where each kind lives
    // ---------------------------------------------------------------------

    private static string BaseOf(ServerManager manager, ShareKind kind) => Path.GetFullPath(kind.InExtra ? manager.ExtraFolder : manager.ServerFolder);

    /// <summary>The kind's folder (Files) or file (the others), on this PC.</summary>
    internal static string PlaceOf(ServerManager manager, ShareKind kind)
        => Path.GetFullPath(Path.Combine(BaseOf(manager, kind), kind.Path.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>The place as the shipped list names it (spiraldb-extra/...), or null outside spiraldb-extra.</summary>
    private static string? ShippedName(ShareKind kind, string? fileName = null)
        => kind.InExtra ? "spiraldb-extra/" + kind.Path + (fileName is null ? "" : "/" + fileName) : null;

    /// <summary>
    /// The file a custom of this kind writes. Throws when the key is not a plain name or the path would leave the kind's
    /// place: the one check every write goes through.
    /// </summary>
    internal static string TargetOf(ServerManager manager, ShareKind kind, string key) {
        var place = PlaceOf(manager, kind);
        var root = BaseOf(manager, kind);
        if (!IsInside(place, root)) {
            throw new PanelException($"{kind.Title}: its folder is outside the server folder.");
        }

        if (kind.Shape != ShareShape.Files) {
            return place;
        }

        var keyOk = kind.NumberField is not null ? s_number.IsMatch(key) : s_fileKey.IsMatch(key) && !key.Contains("..", StringComparison.Ordinal);
        if (!keyOk) {
            throw new PanelException($"{kind.Title}: \"{Shorten(key)}\" is not a plain file name.");
        }

        var file = Path.GetFullPath(Path.Combine(place, key + ".json"));
        if (!string.Equals(Path.GetDirectoryName(file), place.TrimEnd(Path.DirectorySeparatorChar), PathComparison)) {
            throw new PanelException($"{kind.Title}: \"{Shorten(key)}\" would be saved outside its folder.");
        }

        return file;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool IsInside(string path, string folder) {
        var root = folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return path.StartsWith(root, PathComparison);
    }

    // A link (or Windows junction) in the way could lead a write somewhere else, so it is never written through.
    private static void RefuseLinks(string file, string root) {
        for (var path = file; path.Length > root.Length && IsInside(path, root); path = Path.GetDirectoryName(path) ?? "") {
            var info = new FileInfo(path);
            if (info.Exists || Directory.Exists(path)) {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) {
                    throw new PanelException($"{Path.GetFileName(path)} is a link to somewhere else, so nothing is written through it.");
                }
            }
        }
    }

    // ---------------------------------------------------------------------
    // This PC's customs
    // ---------------------------------------------------------------------

    /// <summary>Every custom on this PC, by kind. Files that cannot be read are left out.</summary>
    internal static List<ShareItem> Mine(ServerManager manager) {
        var shipped = ShippedExtraFiles.Load(manager.ServerFolder);
        var items = new List<ShareItem>();
        foreach (var kind in s_kinds) {
            try {
                items.AddRange(MineOf(manager, kind, shipped));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or PanelException) {
                // A file being written or broken by hand is left out until it reads again.
            }
        }

        return items;
    }

    private static IEnumerable<ShareItem> MineOf(ServerManager manager, ShareKind kind, ShippedExtraFiles shipped) {
        var place = PlaceOf(manager, kind);
        switch (kind.Shape) {
            case ShareShape.Files:
                if (!Directory.Exists(place)) {
                    yield break;
                }

                foreach (var file in Directory.GetFiles(place, "*.json").Order(StringComparer.OrdinalIgnoreCase)) {
                    var key = Path.GetFileNameWithoutExtension(file);
                    if (kind.NumberField is not null ? !s_number.IsMatch(key) : !s_fileKey.IsMatch(key)) {
                        continue;
                    }

                    var text = TryRead(file);
                    if (text is null || TryParse(text) is not { } data) {
                        continue;
                    }

                    if (kind.OwnOnly && !IsOwnFile(kind, file, text, shipped)) {
                        continue;
                    }

                    yield return new ShareItem(kind, key, NameOf(kind, key, data), data);
                }

                break;
            case ShareShape.Whole:
                if (TryRead(place) is { } whole && TryParse(whole) is { } wholeData
                    && (!kind.OwnOnly || shipped.IsOwn(ShippedName(kind)!, place, fallback: false))) {
                    yield return new ShareItem(kind, kind.Path, kind.Title, wholeData);
                }

                break;
            default:
                if (TryRead(place) is not { } listText || TryParse(listText) is not { } root) {
                    yield break;
                }

                foreach (var (key, entry) in EntriesOf(kind, root)) {
                    yield return new ShareItem(kind, key, NameOf(kind, key, entry), entry.DeepClone());
                }

                break;
        }
    }

    // A shipped folder's file counts as the player's when the app made or edited it, or when Imlight never shipped it as it is.
    private static bool IsOwnFile(ShareKind kind, string file, string text, ShippedExtraFiles shipped) {
        if (text.Contains("\"MadeInApp\"", StringComparison.OrdinalIgnoreCase) || text.Contains($"\"{AreaFiles.EDITED}\"", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        return shipped.IsOwn(ShippedName(kind, Path.GetFileName(file))!, file, fallback: false);
    }

    /// <summary>The entries of a List or Map file, by key.</summary>
    private static IEnumerable<(string Key, JsonNode Entry)> EntriesOf(ShareKind kind, JsonNode root) {
        if (kind.Shape == ShareShape.Map) {
            foreach (var (key, value) in root as JsonObject ?? []) {
                if (value is not null) {
                    yield return (key, value);
                }
            }

            yield break;
        }

        foreach (var entry in (ListOf(kind, root) ?? []).OfType<JsonObject>()) {
            yield return (KeyOf(kind, entry), entry);
        }
    }

    private static JsonArray? ListOf(ShareKind kind, JsonNode root)
        => kind.ListProperty is null ? root as JsonArray : root is JsonObject o ? Find(o, kind.ListProperty) as JsonArray : null;

    /// <summary>What tells a list entry apart: its key field, or (a player cheat with no name) its contents.</summary>
    private static string KeyOf(ShareKind kind, JsonObject entry) {
        var value = Find(entry, kind.KeyField!) is JsonValue v ? ValueText(v) : "";
        if (value.Length > 0) {
            return value;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(entry.ToJsonString()));

        return "#" + Convert.ToHexString(hash, 0, 6);
    }

    private static string NameOf(ShareKind kind, string key, JsonNode data) {
        var name = kind.NameField is { } field && data is JsonObject o && Find(o, field) is JsonValue v ? ValueText(v).Trim() : "";
        if (kind.Id == "custom") {
            return $"#{key} {(name.Length > 0 ? name : "Custom " + key)}";
        }

        if (kind.Id == "area" && data is JsonObject area && Find(area, "Variant") is JsonValue variant && ValueText(variant).Trim() is { Length: > 0 } variantName) {
            name = $"{name} ({variantName})";
        }

        if (name.Length > 0 && !string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) {
            return $"{name}  ·  {key}";
        }

        return key.StartsWith('#') ? "Cheat with no name" : key;
    }

    // ---------------------------------------------------------------------
    // Export
    // ---------------------------------------------------------------------

    /// <summary>The share file's text for these customs.</summary>
    internal static string Pack(IEnumerable<ShareItem> items) {
        var list = new JsonArray();
        foreach (var item in items) {
            list.Add(new JsonObject {
                ["Kind"] = item.Kind.Id,
                ["Key"] = item.Key,
                ["Name"] = item.Name,
                ["Data"] = item.Data.DeepClone(),
            });
        }

        return new JsonObject {
            ["Format"] = FORMAT,
            ["FormatVersion"] = FORMAT_VERSION,
            ["MadeWith"] = $"{Edition.NAME} {SelfUpdate.CurrentVersion}",
            ["Made"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ["Note"] = "Customs shared from Ahziael. Open it in Ahziael: Create > Share > Import a customs file. It holds only data, never programs.",
            ["Items"] = list,
        }.ToJsonString(s_write);
    }

    // ---------------------------------------------------------------------
    // Import
    // ---------------------------------------------------------------------

    /// <summary>Reads a share file and checks every custom in it against this PC's. Nothing is written.</summary>
    internal static ShareReading Read(ServerManager manager, string file) {
        var info = new FileInfo(file);
        if (!info.Exists) {
            throw new PanelException("That file is not there any more.");
        }

        if (info.Length > MAX_FILE_BYTES) {
            throw new PanelException($"That file is {info.Length / (1024 * 1024)} MB, too big to be a customs file.");
        }

        JsonObject root;
        try {
            root = JsonNode.Parse(File.ReadAllText(file), documentOptions: s_read) as JsonObject
                   ?? throw new PanelException("That file is not an Ahziael customs file.");
        }
        catch (JsonException) {
            throw new PanelException("That file is not an Ahziael customs file (it is not readable JSON text).");
        }

        if (Find(root, "Format") is not JsonValue format || ValueText(format) != FORMAT) {
            throw new PanelException("That file is not an Ahziael customs file. Make one with Export on the Share tab.");
        }

        if (Find(root, "FormatVersion") is JsonValue version && int.TryParse(ValueText(version), out var v) && v > FORMAT_VERSION) {
            throw new PanelException("That customs file was made by a newer Ahziael. Update Ahziael at the top of the window first.");
        }

        var list = Find(root, "Items") as JsonArray ?? [];
        if (list.Count > MAX_ITEMS) {
            throw new PanelException($"That file has {list.Count:N0} customs, more than Ahziael takes at once.");
        }

        var incoming = new List<ShareIncoming>();
        var leftOut = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = new Dictionary<ShareKind, Dictionary<string, JsonNode>>();
        var index = 0;
        foreach (var node in list) {
            index++;
            if (node is not JsonObject entry) {
                leftOut.Add($"Entry {index}: not a custom.");

                continue;
            }

            var kindId = Find(entry, "Kind") is JsonValue k ? ValueText(k) : "";
            var key = Find(entry, "Key") is JsonValue kv ? ValueText(kv) : "";
            var shown = Find(entry, "Name") is JsonValue n ? Shorten(ValueText(n)) : key;
            if (KindOf(kindId) is not { } kind) {
                leftOut.Add($"{Shorten(shown)}: \"{Shorten(kindId)}\" is not a kind of custom this Ahziael knows.");

                continue;
            }

            if (Find(entry, "Data") is not { } data) {
                leftOut.Add($"{kind.Title}, {Shorten(shown)}: it has nothing in it.");

                continue;
            }

            try {
                if (kind.Shape == ShareShape.Whole) {
                    key = kind.Path;
                }

                CheckShape(kind, key, data);
                var target = TargetOf(manager, kind, key);
                if (!seen.Add(kind.Id + "|" + key)) {
                    leftOut.Add($"{kind.Title}, {Shorten(shown)}: it is in the file twice; the first one is used.");

                    continue;
                }

                var mine = Current(manager, kind, current);
                var exists = kind.Shape == ShareShape.Files ? File.Exists(target) : mine.ContainsKey(key);
                JsonNode? old = null;
                if (exists) {
                    old = kind.Shape == ShareShape.Files ? TryParse(TryRead(target) ?? "") : mine[key];
                }

                var same = old is not null && JsonNode.DeepEquals(old, data);
                var item = new ShareItem(kind, key, NameOf(kind, key, data), data.DeepClone());
                incoming.Add(new ShareIncoming {
                    Item = item, Target = target, Exists = exists, Same = same, Use = !same, AsNew = exists && !same && kind.NumberField is not null,
                });
            }
            catch (PanelException ex) {
                leftOut.Add(ex.Message);
            }
        }

        var madeWith = Find(root, "MadeWith") is JsonValue m ? Shorten(ValueText(m)) : "";
        var made = Find(root, "Made") is JsonValue d ? Shorten(ValueText(d)) : "";

        return new ShareReading(madeWith, made, incoming, leftOut);
    }

    // This PC's entries of a List or Map kind, read once per import.
    private static Dictionary<string, JsonNode> Current(ServerManager manager, ShareKind kind, Dictionary<ShareKind, Dictionary<string, JsonNode>> cache) {
        if (cache.TryGetValue(kind, out var found)) {
            return found;
        }

        found = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
        if (kind.Shape != ShareShape.Files && TryRead(PlaceOf(manager, kind)) is { } text) {
            var root = TryParse(text) ?? throw new PanelException($"{kind.Title}: your {Path.GetFileName(kind.Path)} cannot be read, so nothing is added to it.");
            if (kind.Shape == ShareShape.Whole) {
                found[kind.Path] = root;
            }
            else {
                foreach (var (key, entry) in EntriesOf(kind, root)) {
                    found.TryAdd(key, entry);
                }
            }
        }

        cache[kind] = found;

        return found;
    }

    /// <summary>Throws when a custom's contents are not the shape its kind has.</summary>
    private static void CheckShape(ShareKind kind, string key, JsonNode data) {
        var title = $"{kind.Title}, {Shorten(key)}";
        switch (kind.Shape) {
            case ShareShape.Files when data is not JsonObject:
            case ShareShape.List when data is not JsonObject:
            case ShareShape.Map when data is not JsonObject:
                throw new PanelException($"{title}: its contents are not the shape this kind has.");
            case ShareShape.Whole when data is not (JsonObject or JsonArray):
                throw new PanelException($"{title}: its contents are not the shape this kind has.");
            case ShareShape.List when key.Length == 0 || key.Length > 200:
                throw new PanelException($"{kind.Title}: an entry has no {kind.KeyField}.");
            case ShareShape.List when !key.StartsWith('#') && KeyOf(kind, (JsonObject) data) != key:
                throw new PanelException($"{title}: its {kind.KeyField} does not match.");
            case ShareShape.Map when key.Length == 0 || key.Length > 300:
                throw new PanelException($"{kind.Title}: an entry has no name.");
        }

        if (kind.Id == "custom") {
            if (Find((JsonObject) data, "BaseTemplateID") is not JsonValue b || !ulong.TryParse(ValueText(b), NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) {
                throw new PanelException($"{title}: it does not say which creature it is made from.");
            }
        }

        if (kind.Id == "area" && (Find((JsonObject) data, "Zone") is not JsonValue zone || ValueText(zone).Trim().Length == 0)) {
            throw new PanelException($"{title}: it does not say which zone it is for.");
        }
    }

    /// <summary>
    /// Writes the ticked customs. First zips the files it changes into the install folder's Import backups (with a list of
    /// the files it makes new), so Undo puts them back. Returns the backup's path.
    /// </summary>
    internal static string Import(ServerManager manager, IReadOnlyList<ShareIncoming> items) {
        var used = items.Where(i => i.Use).ToList();
        if (used.Count == 0) {
            throw new PanelException("Tick at least one custom to import.");
        }

        // Every target checked again right before writing.
        foreach (var item in used) {
            var target = TargetOf(manager, item.Item.Kind, item.Item.Key);
            if (!string.Equals(target, item.Target, PathComparison)) {
                throw new PanelException("The server folder changed since the file was opened. Open it again.");
            }

            RefuseLinks(target, BaseOf(manager, item.Item.Kind));
        }

        // Customs that take a free number, and the numbers taken by them, so rooms, areas and forms of the same file follow.
        var customNumbers = new Dictionary<int, int>();
        var customsFolder = PlaceOf(manager, KindOf("custom")!);
        var takenCustoms = Directory.Exists(customsFolder)
            ? Directory.GetFiles(customsFolder, "*.json").Select(f => int.TryParse(Path.GetFileNameWithoutExtension(f), out var n) ? n : 0).Where(n => n > 0).ToHashSet()
            : [];
        foreach (var item in used.Where(i => i.Item.Kind.Id == "custom")) {
            if (!item.AsNew || !item.CanRenumber) {
                takenCustoms.Add(int.Parse(item.Item.Key, CultureInfo.InvariantCulture));
            }
        }

        foreach (var item in used.Where(i => i.Item.Kind.Id == "custom" && i.AsNew && i.CanRenumber)) {
            var next = 1;
            while (takenCustoms.Contains(next)) {
                next++;
            }

            takenCustoms.Add(next);
            customNumbers[int.Parse(item.Item.Key, CultureInfo.InvariantCulture)] = next;
        }

        // The writes, worked out in full before anything is touched.
        var writes = new Dictionary<string, (ShareKind Kind, JsonNode Root)>(StringComparer.OrdinalIgnoreCase);
        var lists = new Dictionary<ShareKind, JsonNode>();
        foreach (var item in used) {
            var kind = item.Item.Kind;
            var data = item.Item.Data.DeepClone();
            if (customNumbers.Count > 0 && kind.Id is "custom" or "area" or "room") {
                RenumberCustoms(kind, data, customNumbers);
            }

            switch (kind.Shape) {
                case ShareShape.Files: {
                    var key = item.Item.Key;
                    if (kind.Id == "custom" && customNumbers.TryGetValue(int.Parse(key, CultureInfo.InvariantCulture), out var newNumber)) {
                        key = newNumber.ToString(CultureInfo.InvariantCulture);
                        ((JsonObject) data)["CustomID"] = newNumber;
                    }
                    else if (kind.Id == "custom") {
                        ((JsonObject) data)["CustomID"] = int.Parse(key, CultureInfo.InvariantCulture);
                    }

                    writes[TargetOf(manager, kind, key)] = (kind, data);

                    break;
                }
                case ShareShape.Whole:
                    writes[item.Target] = (kind, data);

                    break;
                default: {
                    if (!lists.TryGetValue(kind, out var root)) {
                        root = TryRead(item.Target) is { } text
                            ? TryParse(text) ?? throw new PanelException($"{kind.Title}: your {Path.GetFileName(kind.Path)} cannot be read, so nothing is added to it.")
                            : kind.Shape == ShareShape.Map ? new JsonObject() : kind.ListProperty is null ? new JsonArray() : new JsonObject();
                        lists[kind] = root;
                    }

                    if (kind.Shape == ShareShape.Map) {
                        var map = root as JsonObject ?? throw new PanelException($"{kind.Title}: your {Path.GetFileName(kind.Path)} is not the shape it should be.");
                        var old = map.Select(p => p.Key).FirstOrDefault(k => k.Equals(item.Item.Key, StringComparison.OrdinalIgnoreCase));
                        if (old is not null) {
                            map.Remove(old);
                        }

                        map[item.Item.Key] = data;
                    }
                    else {
                        var list = ListOf(kind, root);
                        if (list is null && kind.ListProperty is not null && root is JsonObject holder) {
                            holder[kind.ListProperty] = list = [];
                        }

                        if (list is null) {
                            throw new PanelException($"{kind.Title}: your {Path.GetFileName(kind.Path)} is not the shape it should be.");
                        }

                        var entry = (JsonObject) data;
                        if (item.AsNew && item.CanRenumber) {
                            var numbers = list.OfType<JsonObject>().Select(e => Find(e, kind.NumberField!) is JsonValue v && int.TryParse(ValueText(v), out var n) ? n : 0).ToHashSet();
                            var next = 1;
                            while (numbers.Contains(next)) {
                                next++;
                            }

                            SetField(entry, kind.NumberField!, next);
                        }
                        else {
                            if (list.OfType<JsonObject>().FirstOrDefault(e => KeyOf(kind, e).Equals(item.Item.Key, StringComparison.OrdinalIgnoreCase)) is { } mine) {
                                list.Remove(mine);
                            }
                        }

                        list.Add(entry);
                    }

                    writes[item.Target] = (kind, root);

                    break;
                }
            }
        }

        foreach (var target in writes.Keys) {
            RefuseLinks(target, BaseOf(manager, writes[target].Kind));
        }

        var backup = Backup(manager, writes.Keys.ToList());

        foreach (var (target, (_, root)) in writes) {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target + ".saving", root.ToJsonString(s_write));
            File.Move(target + ".saving", target, overwrite: true);
        }

        return backup;
    }

    // A custom's forms and cheats (an action's "Custom" brings one in), an area's creatures and a room's bosses name customs
    // by number; ones that took a free number follow.
    private static void RenumberCustoms(ShareKind kind, JsonNode data, Dictionary<int, int> numbers) {
        int? Swap(JsonNode? node) => node is JsonValue v && int.TryParse(ValueText(v), out var n) && numbers.TryGetValue(n, out var to) ? to : null;

        switch (kind.Id) {
            case "custom":
                if (Find((JsonObject) data, "Forms") is JsonObject forms) {
                    foreach (var key in forms.Select(p => p.Key).ToList()) {
                        if (Swap(forms[key]) is { } to) {
                            forms[key] = to;
                        }
                    }
                }

                foreach (var (key, value) in ((JsonObject) data).ToList()) {
                    if (!key.Equals("Forms", StringComparison.OrdinalIgnoreCase)) {
                        Walk(value);
                    }
                }

                break;
            case "room" when data is JsonObject swaps:
                foreach (var key in swaps.Select(p => p.Key).ToList()) {
                    if (Swap(swaps[key]) is { } to) {
                        swaps[key] = to;
                    }
                }

                break;
            case "area":
                Walk(data);

                break;
        }

        void Walk(JsonNode? node) {
            switch (node) {
                case JsonObject o:
                    foreach (var key in o.Select(p => p.Key).ToList()) {
                        if (key.Equals("Custom", StringComparison.OrdinalIgnoreCase) && Swap(o[key]) is { } to) {
                            o[key] = to;
                        }
                        else {
                            Walk(o[key]);
                        }
                    }

                    break;
                case JsonArray a:
                    foreach (var child in a) {
                        Walk(child);
                    }

                    break;
            }
        }
    }

    // ---------------------------------------------------------------------
    // Backups and undo
    // ---------------------------------------------------------------------

    internal static string BackupsFolder(ServerManager manager) => Path.Combine(manager.InstallFolder, BACKUP_FOLDER);

    private static string Backup(ServerManager manager, List<string> targets) {
        var folder = BackupsFolder(manager);
        Directory.CreateDirectory(folder);
        var zipPath = Path.Combine(folder, $"Before import {DateTime.Now:yyyy-MM-dd HH-mm-ss}.zip");
        for (var i = 2; File.Exists(zipPath); i++) {
            zipPath = Path.Combine(folder, $"Before import {DateTime.Now:yyyy-MM-dd HH-mm-ss} ({i}).zip");
        }

        var server = Path.GetFullPath(manager.ServerFolder);
        var created = new JsonArray();
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create)) {
            foreach (var target in targets) {
                var relative = Path.GetRelativePath(server, target).Replace('\\', '/');
                if (File.Exists(target)) {
                    zip.CreateEntryFromFile(target, "files/" + relative, CompressionLevel.Optimal);
                }
                else {
                    created.Add(relative);
                }
            }

            var list = zip.CreateEntry(UNDO_LIST);
            using var writer = new StreamWriter(list.Open());
            writer.Write(new JsonObject { ["Made"] = DateTime.Now.ToString("o"), ["New"] = created }.ToJsonString(s_write));
        }

        return zipPath;
    }

    /// <summary>The newest import backup, or null.</summary>
    internal static string? LastBackup(ServerManager manager) {
        var folder = BackupsFolder(manager);

        return Directory.Exists(folder)
            ? Directory.GetFiles(folder, "Before import *.zip").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
    }

    /// <summary>
    /// Puts back the files of an import backup and deletes the ones that import made new. Each path is checked to be one
    /// of Ahziael's custom places first. The backup is renamed "Undone" so it is not undone twice.
    /// </summary>
    internal static int Undo(ServerManager manager, string zipPath) {
        var server = Path.GetFullPath(manager.ServerFolder);
        var allowed = s_kinds.Select(k => PlaceOf(manager, k)).ToList();
        string Checked(string relative) {
            var path = Path.GetFullPath(Path.Combine(server, relative.Replace('/', Path.DirectorySeparatorChar)));
            var ok = allowed.Any(place => string.Equals(path, place, PathComparison)
                                          || string.Equals(Path.GetDirectoryName(path), place, PathComparison));
            if (!ok || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) {
                throw new PanelException($"The backup names {Shorten(relative)}, which is not one of Ahziael's custom files, so nothing was put back.");
            }

            RefuseLinks(path, server);

            return path;
        }

        var count = 0;
        using (var zip = ZipFile.OpenRead(zipPath)) {
            var restores = new List<(ZipArchiveEntry Entry, string Path)>();
            var deletes = new List<string>();
            foreach (var entry in zip.Entries) {
                if (entry.FullName.StartsWith("files/", StringComparison.Ordinal) && !entry.FullName.EndsWith('/')) {
                    restores.Add((entry, Checked(entry.FullName["files/".Length..])));
                }
            }

            if (zip.GetEntry(UNDO_LIST) is { } listEntry) {
                using var reader = new StreamReader(listEntry.Open());
                if (JsonNode.Parse(reader.ReadToEnd()) is JsonObject list) {
                    foreach (var made in Find(list, "New") as JsonArray ?? []) {
                        if (made is JsonValue v) {
                            deletes.Add(Checked(ValueText(v)));
                        }
                    }
                }
            }

            foreach (var (entry, path) in restores) {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path + ".saving", overwrite: true);
                File.Move(path + ".saving", path, overwrite: true);
                count++;
            }

            foreach (var path in deletes.Where(File.Exists)) {
                File.Delete(path);
                count++;
            }
        }

        var undone = Path.Combine(Path.GetDirectoryName(zipPath)!, "Undone " + Path.GetFileName(zipPath));
        File.Move(zipPath, undone, overwrite: true);

        return count;
    }

    // ---------------------------------------------------------------------
    // Small helpers
    // ---------------------------------------------------------------------

    private static string? TryRead(string file) {
        try {
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    private static JsonNode? TryParse(string text) {
        try {
            return JsonNode.Parse(text, documentOptions: s_read);
        }
        catch (JsonException) {
            return null;
        }
    }

    private static JsonNode? Find(JsonObject json, string key)
        => json.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    private static void SetField(JsonObject json, string key, JsonNode value) {
        var old = json.Select(p => p.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (old is not null) {
            json.Remove(old);
        }

        json[key] = value;
    }

    private static string ValueText(JsonValue value)
        => value.TryGetValue<string>(out var text) ? text : value.ToJsonString();

    private static string Shorten(string text) => text.Length > 80 ? text[..80] + "…" : text;

}
