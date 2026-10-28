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
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Imlight.Setup;

/// <summary>One setting a plugin's plugin.json offers on the Plugins page.</summary>
internal sealed record PluginSetting(string Key, string Label, string Type, string Default, string Help, string[] Choices, double? Min, double? Max);

/// <summary>A chat command a plugin adds, as its plugin.json describes it.</summary>
internal sealed record PluginCommand(string Name, string Help);

/// <summary>
/// A plugin in Server/Plugins, as the Plugins page shows it: what plugin.json says, whether it is on, and what the
/// server last said about it (status.json, written by the server's PluginLoader).
/// </summary>
internal sealed record InstalledPlugin(
    string Id, string Folder, string Name, string Version, string Author, string Description, string MinAhziael,
    PluginSetting[] Settings, PluginCommand[] Commands, PluginSource? Source, bool On,
    string? State, string? StateMessage, string[] Errors);

/// <summary>Where a plugin came from, so Update can fetch it again (source.txt in its folder). None: it ships with Ahziael.</summary>
internal sealed record PluginSource(string Kind, string Where, string Branch, string Path) {

    internal string Describe() => Kind == "github"
        ? $"GitHub: {Where}" + (Branch.Length > 0 ? $" ({Branch})" : "")
        : $"Folder: {Where}";

}

/// <summary>
/// Adds, updates, turns on and off and removes the plugins in the server's Plugins folder. The server reloads its
/// plugins whenever that folder changes, so none of this needs a restart.
/// </summary>
internal static partial class Plugins {

    internal const string FOLDER = "Plugins";
    internal const string SWITCHES_FILE = "plugins-on.txt";
    private const string MANIFEST_FILE = "plugin.json";
    private const string STATUS_FILE = "status.json";
    private const string SETTINGS_FILE = "settings.ini";
    private const string SOURCE_FILE = "source.txt";

    private static readonly JsonDocumentOptions s_json = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    [GeneratedRegex("[^A-Za-z0-9_-]+")]
    private static partial Regex NotIdCharacters();

    internal static string Folder(string serverFolder) => Path.Combine(serverFolder, FOLDER);

    /// <summary>Every plugin in the server's Plugins folder, by name.</summary>
    internal static List<InstalledPlugin> List(string serverFolder) {
        var folder = Folder(serverFolder);
        if (!Directory.Exists(folder)) {
            return [];
        }

        var on = SwitchedOn(serverFolder);

        return [.. Directory.GetDirectories(folder)
            .Where(d => File.Exists(Path.Combine(d, MANIFEST_FILE)))
            .Select(d => Read(d, on))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static InstalledPlugin Read(string folder, HashSet<string> on) {
        var id = Path.GetFileName(folder);
        string name = id, version = "", author = "", description = "", minAhziael = "";
        var settings = new List<PluginSetting>();
        var commands = new List<PluginCommand>();
        try {
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, MANIFEST_FILE)), s_json);
            var root = manifest.RootElement;
            name = Text(root, "name") ?? id;
            version = Text(root, "version") ?? "";
            author = Text(root, "author") ?? "";
            description = Text(root, "description") ?? "";
            minAhziael = Text(root, "minAhziael") ?? "";
            if (root.TryGetProperty("settings", out var list) && list.ValueKind == JsonValueKind.Array) {
                foreach (var s in list.EnumerateArray().Where(s => Text(s, "key") is not null)) {
                    var choices = s.TryGetProperty("choices", out var c) && c.ValueKind == JsonValueKind.Array
                        ? c.EnumerateArray().Select(x => x.ToString()).ToArray()
                        : [];
                    settings.Add(new PluginSetting(Text(s, "key")!, Text(s, "label") ?? Text(s, "key")!, (Text(s, "type") ?? "text").ToLowerInvariant(),
                        Text(s, "default") ?? "", Text(s, "help") ?? "", choices, Number(s, "min"), Number(s, "max")));
                }
            }

            if (root.TryGetProperty("commands", out var cmds) && cmds.ValueKind == JsonValueKind.Array) {
                commands.AddRange(cmds.EnumerateArray().Where(c => Text(c, "name") is not null)
                    .Select(c => new PluginCommand(Text(c, "name")!, Text(c, "help") ?? "")));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) {
            description = $"Its plugin.json could not be read: {ex.Message}";
        }

        string? state = null, stateMessage = null;
        var errors = Array.Empty<string>();
        try {
            var statusPath = Path.Combine(folder, STATUS_FILE);
            if (File.Exists(statusPath)) {
                using var status = JsonDocument.Parse(File.ReadAllText(statusPath), s_json);
                state = Text(status.RootElement, "state");
                stateMessage = Text(status.RootElement, "message");
                if (status.RootElement.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Array) {
                    errors = [.. e.EnumerateArray().Select(x => x.GetString() ?? "")];
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) {
            // Being written by the server right now; the next look gets it.
        }

        return new InstalledPlugin(id, folder, name, version, author, description, minAhziael, [.. settings], [.. commands],
            ReadSource(folder), on.Contains(id), state, stateMessage, errors);
    }

    private static string? Text(JsonElement element, string key)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString().ToLowerInvariant(), _ => null }
            : null;

    private static double? Number(JsonElement element, string key)
        => element.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    // ---------------------------------------------------------------------
    // On and off
    // ---------------------------------------------------------------------

    internal static HashSet<string> SwitchedOn(string serverFolder) {
        var path = Path.Combine(Folder(serverFolder), SWITCHES_FILE);
        try {
            return File.Exists(path)
                ? new HashSet<string>(File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#'), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException) {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal static void Switch(string serverFolder, string id, bool on) {
        var ids = SwitchedOn(serverFolder);
        if (on ? !ids.Add(id) : !ids.Remove(id)) {
            return;
        }

        Directory.CreateDirectory(Folder(serverFolder));
        File.WriteAllLines(Path.Combine(Folder(serverFolder), SWITCHES_FILE),
            ["# The plugins that are on, one per line (Ahziael: Plugins).", .. ids.OrderBy(i => i, StringComparer.OrdinalIgnoreCase)]);
    }

    // ---------------------------------------------------------------------
    // Settings
    // ---------------------------------------------------------------------

    /// <summary>The plugin's saved settings, with plugin.json's defaults for any not saved yet.</summary>
    internal static Dictionary<string, string> ReadSettings(InstalledPlugin plugin) {
        var values = plugin.Settings.ToDictionary(s => s.Key, s => s.Default, StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(plugin.Folder, SETTINGS_FILE);
        if (File.Exists(path)) {
            foreach (var line in File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] is not (';' or '#' or '['))) {
                var equals = line.IndexOf('=');
                if (equals > 0) {
                    values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
                }
            }
        }

        return values;
    }

    /// <summary>Saves one setting. The server reads settings.ini again on its next look, so it applies at once.</summary>
    internal static void SaveSetting(InstalledPlugin plugin, string key, string value) {
        var values = ReadSettings(plugin);
        values[key] = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
        var lines = new List<string> { $"; {plugin.Name}'s settings, from Ahziael's Plugins page." };
        lines.AddRange(values.Select(kv => $"{kv.Key} = {kv.Value}"));
        File.WriteAllLines(Path.Combine(plugin.Folder, SETTINGS_FILE), lines);
    }

    // ---------------------------------------------------------------------
    // Adding, updating and removing
    // ---------------------------------------------------------------------

    private static PluginSource? ReadSource(string folder) {
        var path = Path.Combine(folder, SOURCE_FILE);
        if (!File.Exists(path)) {
            return null;
        }

        var values = File.ReadAllLines(path).Select(l => l.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim());

        return values.TryGetValue("kind", out var kind) && values.TryGetValue("where", out var where)
            ? new PluginSource(kind, where, values.GetValueOrDefault("branch", ""), values.GetValueOrDefault("path", ""))
            : null;
    }

    private static void WriteSource(string folder, PluginSource source)
        => File.WriteAllLines(Path.Combine(folder, SOURCE_FILE), [
            "; Where this plugin came from, for Ahziael's Update button.",
            $"kind = {source.Kind}", $"where = {source.Where}", $"branch = {source.Branch}", $"path = {source.Path}",
        ]);

    /// <summary>
    /// Adds (or updates) every plugin in a GitHub repository: a plugin.json at the top, or in folders of its own. A
    /// branch of "" means the repository's main branch. Returns the plugins' names.
    /// </summary>
    internal static async Task<List<(string Id, string Name)>> AddFromGitHubAsync(string serverFolder, string repo, string branch, string? onlyPath = null) {
        var temp = Path.Combine(Path.GetTempPath(), $"ahziael-plugin-{Guid.NewGuid():N}");
        try {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Ahziael", SelfUpdate.CurrentVersion.ToString()));
            var url = branch.Length > 0
                ? $"https://codeload.github.com/{repo}/zip/refs/heads/{Uri.EscapeDataString(branch)}"
                : $"https://api.github.com/repos/{repo}/zipball";
            byte[] zip;
            try {
                zip = await http.GetByteArrayAsync(url);
            }
            catch (HttpRequestException ex) {
                throw new PanelException(ex.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? $"GitHub has no {repo}" + (branch.Length > 0 ? $" with a branch \"{branch}\"" : "") + ". Check the spelling; the repository must be public."
                    : $"GitHub could not be reached: {ex.Message}");
            }

            Extract(zip, temp);
            var found = FindPlugins(temp);
            if (onlyPath is not null) {
                found = [.. found.Where(f => string.Equals(Relative(temp, f), onlyPath, StringComparison.OrdinalIgnoreCase))];
            }

            if (found.Count == 0) {
                throw new PanelException($"{repo} has no plugin in it (no plugin.json was found).");
            }

            var names = new List<(string Id, string Name)>();
            foreach (var plugin in found) {
                var relative = Relative(temp, plugin);
                var id = relative.Length == 0 ? repo.Split('/')[1] : Path.GetFileName(plugin);
                names.Add(Install(serverFolder, plugin, id, new PluginSource("github", repo, branch, relative)));
            }

            return names;
        }
        finally {
            TryDelete(temp);
        }
    }

    /// <summary>Adds (or updates) the plugin in a folder on this PC, or every plugin in the folders inside it.</summary>
    internal static List<(string Id, string Name)> AddFromFolder(string serverFolder, string folder) {
        var found = FindPlugins(folder);
        if (found.Count == 0) {
            throw new PanelException("That folder has no plugin in it. Pick the folder that has plugin.json in it.");
        }

        return [.. found.Select(plugin => Install(serverFolder, plugin, Path.GetFileName(plugin), new PluginSource("folder", plugin, "", "")))];
    }

    /// <summary>Fetches the plugin again from where it came from.</summary>
    internal static async Task UpdateAsync(string serverFolder, InstalledPlugin plugin) {
        switch (plugin.Source) {
            case { Kind: "github" } github:
                await AddFromGitHubAsync(serverFolder, github.Where, github.Branch, github.Path);
                break;
            case { Kind: "folder" } folder when Directory.Exists(folder.Where):
                AddFromFolder(serverFolder, folder.Where);
                break;
            case { Kind: "folder" } folder:
                throw new PanelException($"The folder {folder.Where} is not there any more.");
            default:
                throw new PanelException($"{plugin.Name} comes with Ahziael, so it updates when Ahziael does.");
        }
    }

    internal static void Remove(string serverFolder, InstalledPlugin plugin) {
        Switch(serverFolder, plugin.Id, on: false);
        Directory.Delete(plugin.Folder, recursive: true);
    }

    /// <summary>Puts a plugin's files in Plugins/&lt;id&gt;, keeping its saved settings. Returns its id and name.</summary>
    private static (string Id, string Name) Install(string serverFolder, string from, string id, PluginSource source) {
        id = NotIdCharacters().Replace(id, "-").Trim('-');
        if (id.Length == 0) {
            id = "Plugin";
        }

        var target = Path.Combine(Folder(serverFolder), id);
        if (Path.GetFullPath(from).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) {
            throw new PanelException("That folder is the plugin's own folder in the server already.");
        }

        Directory.CreateDirectory(target);
        foreach (var entry in Directory.EnumerateFileSystemEntries(target)) {
            var name = Path.GetFileName(entry);
            if (name.Equals(SETTINGS_FILE, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            if (Directory.Exists(entry)) {
                Directory.Delete(entry, recursive: true);
            }
            else {
                File.Delete(entry);
            }
        }

        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) {
            var relative = Path.GetRelativePath(from, file);
            var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            // Its own git folder, and what a running server wrote there, are not part of the plugin.
            if (first.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || relative.Equals(STATUS_FILE, StringComparison.OrdinalIgnoreCase)
                || relative.Equals(SETTINGS_FILE, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var to = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to, overwrite: true);
        }

        WriteSource(target, source);

        return (id, Read(target, []).Name);
    }

    private static List<string> FindPlugins(string folder) {
        if (File.Exists(Path.Combine(folder, MANIFEST_FILE))) {
            return [folder];
        }

        return [.. Directory.GetFiles(folder, MANIFEST_FILE, SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .Where(d => d is not null && !d.Split(Path.DirectorySeparatorChar).Contains(".git"))
            .Select(d => d!)
            // A plugin inside another plugin's folder is part of that one.
            .OrderBy(d => d.Length)
            .Aggregate(new List<string>(), (kept, d) => {
                if (!kept.Any(k => d.StartsWith(k + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) {
                    kept.Add(d);
                }

                return kept;
            })];
    }

    private static string Relative(string root, string folder) {
        var relative = Path.GetRelativePath(root, folder).Replace('\\', '/');

        return relative == "." ? "" : relative;
    }

    private static void Extract(byte[] zipBytes, string target) {
        Directory.CreateDirectory(target);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        using var zip = new ZipArchive(new MemoryStream(zipBytes));
        foreach (var entry in zip.Entries) {
            // GitHub puts everything in one top folder (repo-branch/); drop it.
            var slash = entry.FullName.IndexOf('/');
            var relative = slash < 0 ? "" : entry.FullName[(slash + 1)..];
            if (relative.Length == 0) {
                continue;
            }

            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            if (relative.EndsWith('/')) {
                Directory.CreateDirectory(path);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: true);
        }
    }

    private static void TryDelete(string folder) {
        try {
            if (Directory.Exists(folder)) {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // A temp folder; Windows clears it in time.
        }
    }

    /// <summary>Whether this Ahziael is new enough for a plugin that asks for at least that version.</summary>
    internal static bool NewEnough(string minAhziael)
        => minAhziael.Length == 0 || !Version.TryParse(minAhziael, out var needed) || SelfUpdate.CurrentVersion >= needed
           || SelfUpdate.CurrentVersion == new Version(0, 0, 0);

}
