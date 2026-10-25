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
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Imcodec.Wad;

namespace Imlight.Setup;

/// <summary>
/// The Game Files tab: copies the game's data files (the .wad archives in Data\GameData) into Imlight's
/// own folder, then reads only that copy and writes what is inside as files a person can open, plus a
/// "Key details" folder for running the game on a private server: quests, badges, triggers, pets and
/// so on. The game's own folder is only ever read from, never changed, and the game program itself is
/// never opened.
/// </summary>
internal sealed class GameFiles(string installFolder) {

    private const long SPARE_BYTES = 2L * 1024 * 1024 * 1024;

    // Pictures, models, sounds and other files with nothing to read.
    private static readonly HashSet<string> s_skipped = new(StringComparer.OrdinalIgnoreCase) {
        ".dds", ".nif", ".kf", ".kfm", ".mp3", ".wav", ".ogg", ".tga", ".png", ".jpg", ".bmp", ".cur", ".bik",
        ".bcd", ".nav", ".dat", ".bin", ".fnt", ".ttf", ".otf", ".swf", ".gfx", ".ico", ".fsb", ".fev",
    };

    // Files that are plain text when they are not BINd.
    private static readonly HashSet<string> s_text = new(StringComparer.OrdinalIgnoreCase) {
        ".xml", ".lua", ".gui", ".txt", ".notxml", ".ini", ".csv", ".json", ".html", ".htm", ".css", ".js", ".py", ".cfg",
    };

    private static readonly JsonSerializerOptions s_indented = new() {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal string Folder => Path.Combine(installFolder, "Game files");

    internal string CopyFolder => Path.Combine(Folder, "Copy of GameData");

    internal string ReadableFolder => Path.Combine(Folder, "Readable");

    internal string KeyDetailsFolder => Path.Combine(ReadableFolder, "Key details");

    /// <summary>The game's Data\GameData folder, as setup found it.</summary>
    internal string? GameDataFolder => InstallInfo.Load(installFolder).GameDataFolder;

    internal bool HasCopy => Directory.Exists(CopyFolder) && Directory.EnumerateFiles(CopyFolder, "*.wad").Any();

    internal bool HasReadable => File.Exists(Path.Combine(KeyDetailsFolder, "Overview.txt"));

    // ---------------------------------------------------------------------
    // 1. Copy
    // ---------------------------------------------------------------------

    /// <summary>What a copy would take: the files still to copy, their size and the free space.</summary>
    internal (int Files, long Bytes, long Free) PlanCopy() {
        var source = RequireGameData();
        var todo = WadsToCopy(source).ToList();
        Directory.CreateDirectory(Folder);
        var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Folder))!).AvailableFreeSpace;

        return (todo.Count, todo.Sum(f => f.Length), free);
    }

    /// <summary>
    /// Copies every .wad in the game's GameData folder into <see cref="CopyFolder"/>, skipping files
    /// already copied and unchanged since. Each file is copied under a temporary name first, so a copy
    /// that stops halfway never looks finished.
    /// </summary>
    internal async Task<string> CopyAsync(IProgress<string> progress, CancellationToken cancel) {
        var source = RequireGameData();
        var todo = WadsToCopy(source).ToList();
        var total = todo.Sum(f => f.Length);
        Directory.CreateDirectory(CopyFolder);
        var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(CopyFolder))!).AvailableFreeSpace;
        if (total + SPARE_BYTES > free) {
            throw new PanelException($"Copying needs {Size(total)} plus some room to spare, but the drive only has {Size(free)} free. " +
                "Free up some space and try again.");
        }

        long done = 0;
        var buffer = new byte[4 * 1024 * 1024];
        for (var i = 0; i < todo.Count; i++) {
            var file = todo[i];
            var target = Path.Combine(CopyFolder, file.Name);
            var partial = target + ".copying";
            progress.Report($"Copying {i + 1} of {todo.Count}: {file.Name} ({Size(done)} of {Size(total)})");
            await using (var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, useAsync: true))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true)) {
                int read;
                while ((read = await input.ReadAsync(buffer, cancel)) > 0) {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                }
            }

            File.Move(partial, target, overwrite: true);
            File.SetLastWriteTimeUtc(target, file.LastWriteTimeUtc);
            done += file.Length;
        }

        var count = Directory.EnumerateFiles(CopyFolder, "*.wad").Count();

        return todo.Count == 0
            ? $"Your copy is already up to date ({count} game files). Nothing in your Wizard101 folder was changed."
            : $"Copied {todo.Count} game files ({Size(total)}). The copy now has {count} files. Nothing in your Wizard101 folder was changed.";
    }

    private IEnumerable<FileInfo> WadsToCopy(string source) {
        foreach (var file in new DirectoryInfo(source).EnumerateFiles("*.wad").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)) {
            var copy = new FileInfo(Path.Combine(CopyFolder, file.Name));
            if (copy.Exists && copy.Length == file.Length && copy.LastWriteTimeUtc == file.LastWriteTimeUtc) {
                continue;
            }

            yield return file;
        }
    }

    private string RequireGameData() {
        var source = GameDataFolder;
        if (source is null || !Directory.Exists(source)) {
            throw new PanelException("Wizard101 could not be found. Click \"Update Ahziael\" on the Tools tab once so the app can find it.");
        }

        return source;
    }

    // ---------------------------------------------------------------------
    // 2. Read the copy
    // ---------------------------------------------------------------------

    /// <summary>
    /// Reads the copied game files and writes <see cref="ReadableFolder"/>: every data file decoded to
    /// JSON, plain text files as they are, the English game text, the network message list and the key
    /// details tables.
    /// </summary>
    internal Task<string> ReadAsync(IProgress<string> progress, CancellationToken cancel)
        => Task.Run(() => Read(progress, cancel), cancel);

    private string Read(IProgress<string> progress, CancellationToken cancel) {
        if (!HasCopy) {
            throw new PanelException("There is no copy to read yet. Click \"Copy my game files\" first.");
        }

        var wads = Directory.EnumerateFiles(CopyFolder, "*.wad")
            .Where(f => f.EndsWith(".wad", StringComparison.OrdinalIgnoreCase))
            // Root.wad first: it has the game text and the item, creature and spell list the rest refer to.
            .OrderBy(f => Path.GetFileName(f).Equals("Root.wad", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (Directory.Exists(ReadableFolder)) {
            progress.Report("Clearing the last read…");
            Directory.Delete(ReadableFolder, recursive: true);
        }

        Directory.CreateDirectory(KeyDetailsFolder);
        var report = new KeyDetails();

        // Pass 1: the English game text, so names can be shown instead of codes.
        for (var i = 0; i < wads.Count; i++) {
            cancel.ThrowIfCancellationRequested();
            progress.Report($"Reading game text, file {i + 1} of {wads.Count}…");
            WithArchive(wads[i], archive => {
                foreach (var name in archive.Files.Keys) {
                    if (IsEnglishText(name) && archive.OpenFile(name) is { } data) {
                        report.AddStrings(name, data.ToArray());
                    }
                }
            });
        }

        // Pass 2: everything else.
        var decodedFolder = Path.Combine(ReadableFolder, "Decoded");
        var textFolder = Path.Combine(ReadableFolder, "Text files");
        var messagesFolder = Path.Combine(ReadableFolder, "Messages");
        for (var i = 0; i < wads.Count; i++) {
            var wadName = Path.GetFileNameWithoutExtension(wads[i]);
            progress.Report($"Reading {i + 1} of {wads.Count}: {wadName} ({report.Decoded:N0} data files decoded so far)…");
            WithArchive(wads[i], archive => {
                var names = archive.Files.Keys
                    .Where(n => IsZoneData(n) || (!s_skipped.Contains(Path.GetExtension(n)) && !n.EndsWith(".lang", StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                Parallel.ForEach(names, new ParallelOptions { CancellationToken = cancel }, name => {
                    try {
                        ReadOne(archive, wadName, name, report, decodedFolder, textFolder, messagesFolder);
                    }
                    catch (Exception) {
                        // One unreadable or unwritable file shouldn't stop the rest.
                        report.Failed(wadName, name);
                    }
                });
            });
        }

        progress.Report("Writing the key details…");
        report.Write(KeyDetailsFolder, ReadableFolder, wads.Count);

        return $"Read {wads.Count} game files and decoded {report.Decoded:N0} data files. " +
            "Open \"Key details\" for quests, badges, triggers, pets and more.";
    }

    // ---------------------------------------------------------------------
    // Shop research (Niduus, 2026-10-19): every NPC shop opens the robe shop window, so this collects the
    // game's shop window layouts and the shop NPCs into one zip on the desktop to send.
    // ---------------------------------------------------------------------

    private const int RESEARCH_MAX_FILES = 600;
    private const long RESEARCH_MAX_BYTES = 40L * 1024 * 1024;

    internal string ResearchZip => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Imlight shop research.zip");

    internal Task<string> ShopResearchAsync(IProgress<string> progress, CancellationToken cancel)
        => Task.Run(() => ShopResearch(progress, cancel), cancel);

    private static bool IsShopFile(string name) {
        if (name.Contains("MagicMirrorShopOptions", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        if (name.Contains("shop", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        // Window layouts for reagents, snacks and jewels may not say "shop".
        return name.StartsWith("GUI/", StringComparison.OrdinalIgnoreCase)
            && (name.Contains("Reagent", StringComparison.OrdinalIgnoreCase) || name.Contains("Snack", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Jewel", StringComparison.OrdinalIgnoreCase) || name.Contains("Vendor", StringComparison.OrdinalIgnoreCase));
    }

    private string ShopResearch(IProgress<string> progress, CancellationToken cancel) {
        var folder = HasCopy ? CopyFolder : RequireGameData();
        var wads = Directory.EnumerateFiles(folder, "*.wad").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        var list = new StringBuilder();
        var written = 0;
        long bytes = 0;
        var zipPath = ResearchZip;
        var partial = zipPath + ".writing";
        using (var zip = System.IO.Compression.ZipFile.Open(partial, System.IO.Compression.ZipArchiveMode.Create)) {
            for (var i = 0; i < wads.Count; i++) {
                cancel.ThrowIfCancellationRequested();
                var wadName = Path.GetFileNameWithoutExtension(wads[i]);
                progress.Report($"Looking through {i + 1} of {wads.Count}: {wadName} ({written} shop files found)…");
                using var stream = new FileStream(wads[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
                Archive? archive;
                try {
                    archive = ArchiveParser.Parse(stream);
                }
                catch (Exception) {
                    continue;
                }

                foreach (var name in archive.Files.Keys.Where(IsShopFile).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)) {
                    list.AppendLine($"{wadName}: {name}");
                    if (written >= RESEARCH_MAX_FILES || bytes >= RESEARCH_MAX_BYTES || s_skipped.Contains(Path.GetExtension(name))) {
                        continue;
                    }

                    byte[]? data;
                    try {
                        data = archive.OpenFile(name)?.ToArray();
                    }
                    catch (Exception) {
                        continue;
                    }

                    if (data is null || data.Length == 0) {
                        continue;
                    }

                    var entry = $"files/{wadName}/{SafePath(name)}";
                    if (GameFileJson.IsBind(data) && GameFileJson.Decode(data) is { } decoded && GameFileJson.ToJson(decoded) is { } json) {
                        data = Encoding.UTF8.GetBytes(json.ToJsonString(s_indented));
                        entry += ".json";
                    }

                    using (var output = zip.CreateEntry(entry).Open()) {
                        output.Write(data);
                    }

                    written++;
                    bytes += data.Length;
                }
            }

            using var listOut = new StreamWriter(zip.CreateEntry("list of shop files.txt").Open(), Encoding.UTF8);
            listOut.Write(list.ToString());
        }

        File.Move(partial, zipPath, overwrite: true);

        return $"Saved {written} shop files to \"{Path.GetFileName(zipPath)}\" on your desktop. Send that file in the chat.";
    }

    private static void ReadOne(Archive archive, string wadName, string name, KeyDetails report,
            string decodedFolder, string textFolder, string messagesFolder) {
        var data = archive.OpenFile(name)?.ToArray();
        if (data is null || data.Length == 0) {
            return;
        }

        if (GameFileJson.IsBind(data) || IsZoneData(name)) {
            var decoded = IsZoneData(name) ? GameFileJson.DecodePlain(data) : GameFileJson.Decode(data);
            if (decoded is null) {
                report.Failed(wadName, name);

                return;
            }

            var json = GameFileJson.ToJson(decoded) as JsonObject;
            if (json is null) {
                return;
            }

            WriteFile(Path.Combine(decodedFolder, wadName, SafePath(name) + ".json"), json.ToJsonString(s_indented));
            report.Add(wadName, name, json);

            return;
        }

        if (s_text.Contains(Path.GetExtension(name)) && LooksLikeText(data)) {
            WriteFile(Path.Combine(textFolder, wadName, SafePath(name)), data);
            if (Path.GetFileName(name).Contains("Messages", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) {
                WriteFile(Path.Combine(messagesFolder, Path.GetFileName(name)), data);
                report.AddMessages(name, data);
            }
        }
    }

    private static void WithArchive(string path, Action<Archive> read) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        Archive? archive;
        try {
            archive = ArchiveParser.Parse(stream);
        }
        catch (Exception) {
            archive = null;
        }

        if (archive is not null) {
            read(archive);
        }
    }

    /// <summary>A zone's gamedata.bin: the NPCs and objects placed in it and its named places.</summary>
    private static bool IsZoneData(string name) => name.Equals("gamedata.bin", StringComparison.OrdinalIgnoreCase);

    private static bool IsEnglishText(string name)
        => name.EndsWith(".lang", StringComparison.OrdinalIgnoreCase)
            && (name.StartsWith("Locale/en-US/", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Locale/English/", StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeText(byte[] data) {
        var length = Math.Min(data.Length, 4096);
        for (var i = 0; i < length; i++) {
            if (data[i] == 0) {
                return false;
            }
        }

        return true;
    }

    /// <summary>A path inside a wad as a safe relative path on this computer.</summary>
    private static string SafePath(string name) {
        var parts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => {
                var clean = new string(part.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c).ToArray()).Trim();

                return clean is "" or "." or ".." ? "_" : clean;
            });

        return Path.Combine(parts.ToArray());
    }

    private static void WriteFile(string path, string text) => WriteFile(path, Encoding.UTF8.GetBytes(text));

    private static void WriteFile(string path, byte[] data) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    internal static string Size(long bytes) => bytes switch {
        >= 1L << 30 => $"{bytes / (double) (1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double) (1L << 20):0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    // ---------------------------------------------------------------------
    // Key details
    // ---------------------------------------------------------------------

    /// <summary>Collects the tables for the Key details folder while the files are read.</summary>
    private sealed class KeyDetails {

        private readonly ConcurrentDictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _tableOf = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _templateNames = new();
        private readonly ConcurrentDictionary<string, ConcurrentBag<string[]>> _tables = new();
        private readonly ConcurrentDictionary<string, int> _classes = new();
        private readonly ConcurrentBag<string> _failed = [];
        private readonly ConcurrentBag<(string File, XDocument Xml)> _messages = [];
        private int _decoded;

        internal int Decoded => _decoded;

        // The tables, their columns and what each one is for.
        private static readonly (string Name, string[] Columns, string About)[] s_tables = [
            ("Quests and goals", ["Where", "File", "What", "Name", "Details"],
                "Everything about quests the client has: quest and goal events zones fire, quest requirements and results, " +
                "daily quests and quest effects. The quests themselves (steps, NPC dialogue, rewards) live on the server, not in the game files."),
            ("Badges", ["Where", "File", "What", "Name", "Details"],
                "Every badge name and description the game can show, plus badge filters and anything that checks or gives a badge."),
            ("Triggers", ["Zone", "Trigger", "Starts on", "Fires", "Stops on", "Max times", "Cooldown", "Needs", "Does", "Position (X Y Z)"],
                "Zone triggers: when they switch on, the events they fire, what they need and what they do (show text, give a badge, and so on)."),
            ("Event areas", ["Zone", "Area", "Shape", "X", "Y", "Z", "Radius", "Length", "Width", "Enter events", "Exit events", "Needs"],
                "Invisible areas in zones that fire an event when a wizard walks in or out, with where they are and how big. " +
                "Quests and triggers listen for these, so this is where quest areas are."),
            ("Spawns", ["Zone", "Spawner", "ID", "Max at once", "Spawn time", "Respawn rate", "Spawns"],
                "Where creatures and objects appear in each zone, and how often."),
            ("Zones", ["Zone file", "Zone", "Type", "Teleporters", "Places", "Player limits", "Details"],
                "Every zone: its doors to other zones (teleporters), named places and player limits."),
            ("NPCs and objects", ["Zone", "Placed as", "Template ID", "Name", "X", "Y", "Z", "Facing", "Start state", "Object ID", "Loading"],
                "Every NPC and object placed in each zone, with where it stands and which way it faces (Facing is the turn in radians)."),
            ("Zone places", ["Zone", "Place", "X", "Y", "Z", "Facing"],
                "Named spots in each zone, such as where wizards arrive from a door or a teleport."),
            ("Houses", ["Template ID", "Name", "Shown as", "House zone", "Preview zone", "Item limit", "Building ID", "File"],
                "Every house deed: the zone the house is in, how many items can be placed and the house building that goes with it."),
            ("Pets", ["Template ID", "Name", "Shown as", "School", "Race", "Egg", "Wow factor", "Hatch rate", "Max stats", "Talents", "Derby talents", "File"],
                "Every pet with its stats, talent pool and derby talents."),
            ("Pet talents", ["Talent", "Shown as", "Description", "Rank", "What it does", "File"],
                "Every pet talent and derby talent and what it does."),
            ("Creatures", ["Template ID", "Name", "Shown as", "School", "Loot tables", "Tags", "File"],
                "Every creature the client knows, with its school and the names of the loot tables it drops from."),
            ("Items and objects", ["Template ID", "Name", "Shown as", "Class", "Behaviors", "School", "Tags", "File"],
                "Every item and object template: the IDs the server and the .lookup command use."),
            ("Spells", ["Name", "Shown as", "Description", "Class", "Details", "File"],
                "Every spell, with its school, cost, accuracy and so on."),
        ];

        internal void AddStrings(string name, byte[] data) {
            string text;
            try {
                text = Encoding.Unicode.GetString(data).TrimStart('﻿');
            }
            catch (Exception) {
                return;
            }

            var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None);
            if (lines.Length < 4) {
                return;
            }

            // First line "1:Badges", then each entry as three lines: key, note, text.
            var header = lines[0];
            var table = header.Contains(':') ? header[(header.IndexOf(':') + 1)..].Trim() : Path.GetFileNameWithoutExtension(name);
            for (var i = 1; i + 2 < lines.Length; i += 3) {
                if (lines[i].Length == 0) {
                    continue;
                }

                _strings[$"{table}_{lines[i]}"] = lines[i + 2].Trim();
                _tableOf[$"{table}_{lines[i]}"] = table;
            }
        }

        internal void AddMessages(string name, byte[] data) {
            try {
                _messages.Add((Path.GetFileName(name), XDocument.Parse(Encoding.UTF8.GetString(data).TrimStart('﻿'))));
            }
            catch (Exception) {
                // Not well-formed; the raw file is still under Text files.
            }
        }

        internal void Failed(string wad, string file) => _failed.Add($"{wad}\t{file}");

        private void Row(string table, params string[] cells) => _tables.GetOrAdd(table, _ => []).Add(cells);

        private string Shown(string key) => key.Length > 0 && _strings.TryGetValue(key, out var text) ? text : "";

        private string Describe(JsonNode? node) {
            if (node is not JsonObject obj) {
                return "";
            }

            var parts = new List<string>();
            foreach (var inner in ListItems(obj)) {
                var summary = GameFileJson.Summary(inner, 400);
                parts.Add(summary.Length > 0 ? $"{GameFileJson.ClassOf(inner)}({WithText(summary)})" : GameFileJson.ClassOf(inner));
            }

            return string.Join(" | ", parts);
        }

        /// <summary>The entries of a RequirementList or ResultList, or the object itself.</summary>
        private static IEnumerable<JsonObject> ListItems(JsonObject obj) {
            foreach (var key in new[] { "m_requirements", "m_results" }) {
                if (obj.TryGetPropertyValue(key, out var list) && list is JsonArray array) {
                    return array.OfType<JsonObject>().SelectMany(inner => GameFileJson.ClassOf(inner).EndsWith("List") ? ListItems(inner) : [inner]);
                }
            }

            return GameFileJson.ClassOf(obj).EndsWith("List") ? [] : [obj];
        }

        /// <summary>Adds the English text after any game text code, such as WizardPOI_00000002 ("Golem Court").</summary>
        private string WithText(string summary) {
            return System.Text.RegularExpressions.Regex.Replace(summary, @"\b[A-Za-z][A-Za-z0-9\-]*_\d{5,}\b",
                match => _strings.TryGetValue(match.Value, out var text) && text.Length > 0 ? $"{match.Value} (\"{text}\")" : match.Value);
        }

        internal void Add(string wad, string file, JsonObject json) {
            Interlocked.Increment(ref _decoded);
            var cls = GameFileJson.ClassOf(json);
            _classes.AddOrUpdate(cls, 1, (_, n) => n + 1);
            var where = $"{wad}/{file}";

            // Templates: the ID and name list everything else refers to.
            var id = GameFileJson.Text(json, "m_templateID");
            var objectName = GameFileJson.Text(json, "m_objectName");
            if (id.Length > 0 && id != "0" && objectName.Length > 0) {
                var shown = Shown(GameFileJson.Text(json, "m_displayName"));
                _templateNames[id] = shown.Length > 0 ? $"{objectName} ({shown})" : objectName;
                var behaviors = json["m_behaviors"] is JsonArray list
                    ? list.OfType<JsonObject>().Select(b => GameFileJson.Text(b, "m_behaviorName")).Where(b => b.Length > 0).ToList()
                    : [];
                var school = FirstOf(json, "m_school", "m_primarySchoolName");
                var tags = GameFileJson.Text(json, "m_adjectiveList");
                Row("Items and objects", id, objectName, shown, cls, string.Join(", ", behaviors), school, tags, where);

                var loot = GameFileJson.Text(json, "m_lootTable");
                if (cls is "WizGameObjectTemplate" && (loot.Length > 0 || GameFileJson.Text(json, "m_primarySchoolName").Length > 0)) {
                    Row("Creatures", id, objectName, shown, school, loot, tags, where);
                }

                if (json["m_behaviors"] is JsonArray pets
                        && pets.OfType<JsonObject>().FirstOrDefault(b => GameFileJson.ClassOf(b) == "PetItemBehaviorTemplate") is { } pet) {
                    Row("Pets", id, objectName, shown, school, GameFileJson.Text(pet, "m_eRace"),
                        WithShown(GameFileJson.Text(pet, "m_eggName")), GameFileJson.Text(pet, "m_wowFactor"), GameFileJson.Text(pet, "m_sHatchRate"),
                        Stats(pet["m_maxStats"]), GameFileJson.Text(pet, "m_talents"), GameFileJson.Text(pet, "m_derbyTalents"), where);
                }
            }

            if (id.Length > 0 && json["m_behaviors"] is JsonArray deeds
                    && deeds.OfType<JsonObject>().FirstOrDefault(b => GameFileJson.ClassOf(b) == "DeedBehaviorTemplate") is { } deed
                    && GameFileJson.Text(deed, "m_lotWorldZone").Length > 0) {
                Row("Houses", id, objectName, Shown(GameFileJson.Text(json, "m_displayName")), GameFileJson.Text(deed, "m_lotWorldZone"),
                    GameFileJson.Text(deed, "m_previewWorldZone"), GameFileJson.Text(deed, "m_itemLimit"), GameFileJson.Text(deed, "m_structureTemplateID"), where);
            }

            switch (cls) {
                case "PetTalentTemplate" or "PetDerbyTalentTemplate":
                    Row("Pet talents", GameFileJson.Text(json, "m_talentName"), Shown(GameFileJson.Text(json, "m_displayName")),
                        Shown(GameFileJson.Text(json, "m_description")), GameFileJson.Text(json, "m_rank"),
                        string.Join(" | ", (json["m_effectList"] as JsonArray ?? []).OfType<JsonObject>()
                            .Select(e => $"{GameFileJson.ClassOf(e)}({WithText(GameFileJson.Summary(e, 600))})")),
                        where);
                    break;
                case "WizZoneTriggers":
                    foreach (var trigger in (json["m_triggers"] as JsonArray ?? []).OfType<JsonObject>()) {
                        Row("Triggers", wad, GameFileJson.Text(trigger, "m_triggerName"), GameFileJson.Text(trigger, "m_activateEvents"),
                            GameFileJson.Text(trigger, "m_fireEvents"), GameFileJson.Text(trigger, "m_deactivateEvents"),
                            GameFileJson.Text(trigger, "m_triggerMax") is "4294967295" ? "no limit" : GameFileJson.Text(trigger, "m_triggerMax"),
                            GameFileJson.Text(trigger, "m_cooldown"), Describe(trigger["m_requirements"]), Describe(trigger["m_results"]),
                            Position(trigger["m_triggerObjInfo"]));
                    }

                    break;
                case "WizZoneVolumes":
                    foreach (var volume in (json["m_volumes"] as JsonArray ?? []).OfType<JsonObject>()) {
                        Row("Event areas", wad, GameFileJson.Text(volume, "m_volumeName"), GameFileJson.Text(volume, "m_primitiveType") is { Length: > 0 } shape ? shape : "Sphere",
                            GameFileJson.Text(volume, "m_locationX"), GameFileJson.Text(volume, "m_locationY"), GameFileJson.Text(volume, "m_locationZ"),
                            GameFileJson.Text(volume, "m_radius"), GameFileJson.Text(volume, "m_length"), GameFileJson.Text(volume, "m_width"),
                            GameFileJson.Text(volume, "m_enterEvents"), GameFileJson.Text(volume, "m_exitEvents"), Describe(volume["m_spawnRequirements"]));
                    }

                    break;
                case "SpawnManager":
                    foreach (var spawner in (json["m_spawners"] as JsonArray ?? []).OfType<JsonObject>()) {
                        var spawns = (spawner["m_spawnList"] as JsonArray ?? []).OfType<JsonObject>()
                            .Select(s => GameFileJson.Text(s["m_objectInfo"], "m_templateID"))
                            .Where(t => t.Length > 0)
                            .Select(t => _templateNames.TryGetValue(t, out var n) ? $"{t} {n}" : t);
                        Row("Spawns", wad, GameFileJson.Text(spawner, "m_name"), GameFileJson.Text(spawner, "m_id"),
                            GameFileJson.Text(spawner, "m_maxNumberOfSpawns"), GameFileJson.Text(spawner, "m_spawnTime"),
                            GameFileJson.Text(spawner, "m_respawnRate"), string.Join(" | ", spawns));
                    }

                    break;
                case "WizZoneData":
                    foreach (var placed in (json["m_objectList"] as JsonArray ?? []).OfType<JsonObject>()) {
                        var placedId = GameFileJson.Text(placed, "m_templateID");
                        Row("NPCs and objects", wad, GameFileJson.Text(placed, "m_zoneTag"), placedId,
                            _templateNames.TryGetValue(placedId, out var placedName) ? placedName : "",
                            GameFileJson.Text(placed["m_location"], "X"), GameFileJson.Text(placed["m_location"], "Y"), GameFileJson.Text(placed["m_location"], "Z"),
                            GameFileJson.Text(placed["m_orientation"], "Z"), GameFileJson.Text(placed, "m_startState"),
                            GameFileJson.Text(placed, "m_nObjectID"), GameFileJson.Text(placed, "m_loadingType"));
                    }

                    foreach (var place in (json["m_locationList"] as JsonArray ?? []).OfType<JsonObject>()) {
                        Row("Zone places", wad, GameFileJson.Text(place, "m_locName"),
                            GameFileJson.Text(place["m_location"], "X"), GameFileJson.Text(place["m_location"], "Y"), GameFileJson.Text(place["m_location"], "Z"),
                            GameFileJson.Text(place["m_orientation"], "Z"));
                    }

                    goto case "ZoneData";
                case "ZoneData":
                    var teleporters = (json["m_teleportList"] as JsonArray ?? []).OfType<JsonObject>()
                        .Select(t => $"{GameFileJson.Text(t, "m_targetZone")} @ {GameFileJson.Text(t, "m_targetLocation")}");
                    var places = (json["m_locationList"] as JsonArray ?? []).OfType<JsonObject>().Select(l => GameFileJson.Text(l, "m_locName"));
                    Row("Zones", wad, GameFileJson.Text(json, "m_zoneName"), GameFileJson.Text(json, "m_nType"), string.Join(" | ", teleporters),
                        string.Join(", ", places), $"{GameFileJson.Text(json, "m_nSoftLimit")} / {GameFileJson.Text(json, "m_nHardLimit")}",
                        WithText(GameFileJson.Summary(json, 800)));
                    break;
            }

            if (cls.Contains("Spell") && cls.EndsWith("Template") && json.ContainsKey("m_name")) {
                Row("Spells", GameFileJson.Text(json, "m_name"), Shown(GameFileJson.Text(json, "m_displayName")),
                    Shown(GameFileJson.Text(json, "m_description")), cls, WithText(GameFileJson.Summary(json, 1200)), where);
            }

            // Anything about quests or badges, wherever it is: a requirement in a trigger, a daily quest, a badge filter.
            var name = FirstOf(json, "m_objectName", "m_name", "m_triggerName", "m_zoneName");
            foreach (var inner in GameFileJson.AllObjects(json)) {
                var innerClass = GameFileJson.ClassOf(inner);
                if (innerClass.Contains("Quest") || innerClass.Contains("Goal")) {
                    Row("Quests and goals", wad, file, innerClass, name, WithText(GameFileJson.Summary(inner)));
                }

                if (innerClass.Contains("Badge")) {
                    Row("Badges", wad, file, innerClass, name, WithText(GameFileJson.Summary(inner)));
                }
            }

            // Quest and goal events that zones fire or listen for.
            if (cls is "WizZoneTriggers" or "WizZoneVolumes") {
                foreach (var inner in GameFileJson.AllObjects(json)) {
                    foreach (var key in new[] { "m_activateEvents", "m_fireEvents", "m_deactivateEvents", "m_enterEvents", "m_exitEvents" }) {
                        foreach (var e in (inner[key] as JsonArray ?? []).Select(v => v?.ToString() ?? "")) {
                            if (e.Contains("Quest", StringComparison.OrdinalIgnoreCase) || e.Contains("Goal", StringComparison.OrdinalIgnoreCase)) {
                                Row("Quests and goals", wad, file, "Zone event", FirstOf(inner, "m_triggerName", "m_volumeName"), $"{key[2..]}: {e}");
                            }
                        }
                    }
                }
            }
        }

        private string WithShown(string key) {
            var shown = Shown(key);

            return shown.Length > 0 ? $"{shown} ({key})" : key;
        }

        private static string Position(JsonNode? info) {
            var xyz = new[] { "m_locationX", "m_locationY", "m_locationZ" }.Select(k => GameFileJson.Text(info, k)).ToArray();

            return xyz.All(v => v is "" or "0") ? "" : string.Join(" ", xyz);
        }

        private static string Stats(JsonNode? stats)
            => string.Join(", ", (stats as JsonArray ?? []).OfType<JsonObject>()
                .Select(s => $"{GameFileJson.Text(s, "m_name")} {GameFileJson.Text(s, "m_value")}"));

        private static string FirstOf(JsonNode? node, params string[] keys)
            => keys.Select(k => GameFileJson.Text(node, k)).FirstOrDefault(v => v.Length > 0) ?? "";

        internal void Write(string keyFolder, string readableFolder, int wadCount) {
            foreach (var (name, columns, _) in s_tables) {
                var rows = _tables.TryGetValue(name, out var bag) ? bag.ToList() : [];
                if (name == "Zones") {
                    // A zone can have both ZoneData.xml and gamedata.bin: keep the fuller row.
                    rows = rows.GroupBy(r => r[0]).Select(g => g.OrderByDescending(r => string.Concat(r).Length).First()).ToList();
                }

                rows.Sort((a, b) => string.Compare(string.Join("\t", a), string.Join("\t", b), StringComparison.OrdinalIgnoreCase));
                WriteCsv(Path.Combine(keyFolder, name + ".csv"), columns, rows);
            }

            // Badge names straight from the game text, since badges themselves are given out by the server.
            var badgeText = _strings
                .Where(s => s.Key.StartsWith("Badges_", StringComparison.OrdinalIgnoreCase)
                    || s.Key.StartsWith("WizardBadges_", StringComparison.OrdinalIgnoreCase)
                    || s.Key.StartsWith("ClientBadgeNames_", StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
                .Select(s => new[] { s.Key, s.Value })
                .ToList();
            WriteCsv(Path.Combine(keyFolder, "Badge names.csv"), ["Code", "Text"], badgeText);

            // Quest text from the game, for the same reason.
            var questText = _strings
                .Where(s => _tableOf.TryGetValue(s.Key, out var table)
                    && (table.Contains("Quest", StringComparison.OrdinalIgnoreCase) || table.Contains("Goal", StringComparison.OrdinalIgnoreCase)))
                .OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
                .Select(s => new[] { s.Key, s.Value })
                .ToList();
            WriteCsv(Path.Combine(keyFolder, "Quest text.csv"), ["Code", "Text"], questText);

            WriteCsv(Path.Combine(readableFolder, "Game text (English).csv"), ["Code", "Text"],
                _strings.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase).Select(s => new[] { s.Key, s.Value }).ToList());
            WriteMessages(Path.Combine(keyFolder, "Network messages.txt"));

            var failed = _failed.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            if (failed.Count > 0) {
                File.WriteAllLines(Path.Combine(readableFolder, "Could not decode.txt"), failed);
            }

            var overview = new StringBuilder();
            overview.AppendLine("Wizard101 game files, read by Imlight");
            overview.AppendLine("=====================================");
            overview.AppendLine();
            overview.AppendLine($"Read {wadCount} game files from the copy and decoded {_decoded:N0} data files ({failed.Count} could not be decoded).");
            overview.AppendLine("Only the copy was read. Your Wizard101 folder was not changed.");
            overview.AppendLine();
            overview.AppendLine("The .csv files open in any spreadsheet app, or in Notepad.");
            overview.AppendLine();
            overview.AppendLine("Files in this folder");
            overview.AppendLine("--------------------");
            foreach (var (name, _, about) in s_tables) {
                var count = _tables.TryGetValue(name, out var bag) ? bag.Count : 0;
                overview.AppendLine($"{name}.csv ({count:N0} rows): {about}");
            }

            overview.AppendLine($"Badge names.csv ({badgeText.Count:N0} rows): every badge title and description in the game text.");
            overview.AppendLine($"Quest text.csv ({questText.Count:N0} rows): every piece of game text from the quest and goal text lists.");
            overview.AppendLine($"Network messages.txt: every message the game and server send each other, with its fields.");
            overview.AppendLine();
            overview.AppendLine("One folder up");
            overview.AppendLine("-------------");
            overview.AppendLine("Decoded: every data file as JSON, in the same folders as inside the game files.");
            overview.AppendLine("Text files: scripts, interface layouts and other plain text files.");
            overview.AppendLine("Messages: the message list files the server is built from.");
            overview.AppendLine("Game text (English).csv: every piece of English text, by its code (such as WizardPets_00000187).");
            overview.AppendLine();
            overview.AppendLine("Kinds of data found");
            overview.AppendLine("-------------------");
            foreach (var (cls, count) in _classes.OrderByDescending(c => c.Value)) {
                overview.AppendLine($"{count,8:N0}  {cls}");
            }

            File.WriteAllText(Path.Combine(keyFolder, "Overview.txt"), overview.ToString());
        }

        private void WriteMessages(string path) {
            var text = new StringBuilder();
            text.AppendLine("Network messages: what the game and the server send each other.");
            text.AppendLine("Each list is one service. The number before a message is its number inside that service.");
            foreach (var (file, xml) in _messages.OrderBy(m => m.File, StringComparer.OrdinalIgnoreCase)) {
                var root = xml.Root;
                if (root is null) {
                    continue;
                }

                var info = root.Element("_ProtocolInfo")?.Element("RECORD");
                string Info(params string[] names) => names.Select(n => info?.Element(n)?.Value).FirstOrDefault(v => v is not null) ?? "";
                text.AppendLine();
                text.AppendLine($"== {file}: service {Info("ServiceID", "_ServiceID")} {Info("ProtocolType", "_ProtocolType")} " +
                    $"(version {Info("ProtocolVersion", "_ProtocolVersion")}) {Info("ProtocolDescription", "_ProtocolDescription")}".TrimEnd());

                var records = root.Elements().Where(e => !e.Name.LocalName.StartsWith('_') && e.Element("RECORD") is not null).ToList();
                var ordered = records.Count > 1 && !records[0].Element("RECORD")!.Elements().Any(e => e.Name.LocalName is "_MsgOrder" or "_MsgType");
                if (ordered) {
                    records = records.OrderBy(e => e.Name.LocalName, StringComparer.Ordinal).ToList();
                }

                // A message defined twice keeps its last definition, at the end.
                var unique = new List<XElement>();
                foreach (var record in records) {
                    unique.RemoveAll(u => u.Name == record.Name);
                    unique.Add(record);
                }

                for (var i = 0; i < unique.Count; i++) {
                    var record = unique[i].Element("RECORD")!;
                    var fields = record.Elements().Where(f => !f.Name.LocalName.StartsWith('_'))
                        .Select(f => $"{f.Name.LocalName}:{f.Attribute("TYPE")?.Value ?? "STR"}");
                    var description = record.Element("_MsgDescription")?.Value.Trim();
                    text.AppendLine($"  {i + 1,3}  {unique[i].Name.LocalName}({string.Join(", ", fields)})" +
                        (string.IsNullOrEmpty(description) ? "" : $"  -- {description}"));
                }
            }

            File.WriteAllText(path, text.ToString());
        }

        private static void WriteCsv(string path, string[] columns, List<string[]> rows) {
            var text = new StringBuilder();
            text.AppendLine(string.Join(",", columns.Select(Cell)));
            foreach (var row in rows) {
                text.AppendLine(string.Join(",", row.Select(Cell)));
            }

            // With a byte order mark, so spreadsheet apps read the text right.
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        private static string Cell(string value) {
            // Spreadsheets cap a cell at 32,767 characters.
            if (value.Length > 32000) {
                value = value[..32000] + "…";
            }

            return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
        }

    }

}
