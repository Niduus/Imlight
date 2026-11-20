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
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace Imlight.Setup;

/// <summary>
/// The Hive mind's pages on GitHub (Niduus, 2026-11-19: "project from a new github folder after the same name. This way, when
/// it updates on github, it displays in the EXE"). Ahziael reads the <see cref="REPO_FOLDER"/> folder of Niduus/Imlight (and
/// the folders inside it, one deep), downloads only pages that changed, and keeps the last copy on this PC so the tab still
/// shows them with no internet. Pages are plain text with a few marks (# title, ## heading, - point), never code.
/// </summary>
internal static class HiveMindShelf {

    internal const string REPO_FOLDER = "Hive mind";
    private const string CACHE_FOLDER = "github";
    private const string INDEX_FILE = "index.json";
    private const long MAX_BYTES = 2 * 1024 * 1024;

    internal static string PageUrl => $"https://github.com/{SelfUpdate.RELEASE_REPOSITORY}/tree/HEAD/{Uri.EscapeDataString(REPO_FOLDER)}";

    /// <summary>One page: where it is in the folder ("Quests/Wizard City.md"), and its text.</summary>
    internal sealed record Page(string Path, string Text) {
        /// <summary>The page's name: its "# " line, or the file name without its number and ending.</summary>
        internal string Title {
            get {
                var first = Text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
                if (first.StartsWith("# ", StringComparison.Ordinal)) {
                    return first[2..].Trim();
                }

                var name = System.IO.Path.GetFileNameWithoutExtension(Path);
                var cut = name.TakeWhile(c => char.IsDigit(c) || c is ' ' or '-' or '_' or '.').Count();

                return cut < name.Length ? name[cut..] : name;
            }
        }

        /// <summary>The folder inside "Hive mind" it is in, or "" for the top.</summary>
        internal string Group => Path.Contains('/') ? Path[..Path.LastIndexOf('/')] : "";
    }

    private sealed class Index {
        public Dictionary<string, string> Sha { get; set; } = [];
        public DateTime Checked { get; set; }
    }

    private static string Cache(string logs) => Path.Combine(logs, HiveMind.FOLDER, CACHE_FOLDER);

    /// <summary>The pages kept on this PC from the last look, with no internet needed.</summary>
    internal static (List<Page> Pages, DateTime Checked) Kept(string logs) {
        var folder = Cache(logs);
        var index = ReadIndex(folder);
        var pages = new List<Page>();
        foreach (var path in index.Sha.Keys) {
            var file = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(file)) {
                pages.Add(new Page(path, File.ReadAllText(file)));
            }
        }

        return (Sorted(pages), index.Checked);
    }

    /// <summary>Looks at GitHub: pages that are new or changed are downloaded, pages taken off are dropped.</summary>
    internal static async Task<List<Page>> RefreshAsync(string logs) {
        var folder = Cache(logs);
        var index = ReadIndex(folder);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ImlightSetup", SelfUpdate.CurrentVersion.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var found = new Dictionary<string, (string Sha, string Url)>(StringComparer.Ordinal);
        await ListAsync(http, REPO_FOLDER, "", found, depth: 0);

        Directory.CreateDirectory(folder);
        var fresh = new Index { Checked = DateTime.Now };
        foreach (var (path, (sha, url)) in found) {
            var file = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
            if (index.Sha.TryGetValue(path, out var had) && had == sha && File.Exists(file)) {
                fresh.Sha[path] = sha;
                continue;
            }

            try {
                var text = await http.GetStringAsync(url);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllTextAsync(file, text);
                fresh.Sha[path] = sha;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
                // Keep the old copy of a page that won't download this time.
                if (had is not null && File.Exists(file)) {
                    fresh.Sha[path] = had;
                }
            }
        }

        // Pages no longer on GitHub are removed here too.
        foreach (var gone in index.Sha.Keys.Where(p => !fresh.Sha.ContainsKey(p))) {
            var file = Path.Combine(folder, gone.Replace('/', Path.DirectorySeparatorChar));
            try {
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                // Left for next time.
            }
        }

        await File.WriteAllTextAsync(Path.Combine(folder, INDEX_FILE), JsonSerializer.Serialize(fresh));

        return Kept(logs).Pages;
    }

    private static async Task ListAsync(HttpClient http, string repoPath, string relative, Dictionary<string, (string, string)> found, int depth) {
        var url = $"https://api.github.com/repos/{SelfUpdate.RELEASE_REPOSITORY}/contents/{string.Join('/', repoPath.Split('/').Select(Uri.EscapeDataString))}";
        using var listing = await http.GetAsync(url);
        if (listing.StatusCode == System.Net.HttpStatusCode.NotFound) {
            return;
        }

        listing.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await listing.Content.ReadAsStringAsync());
        if (document.RootElement.ValueKind != JsonValueKind.Array) {
            return;
        }

        foreach (var entry in document.RootElement.EnumerateArray()) {
            var name = entry.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var type = entry.TryGetProperty("type", out var t) ? t.GetString() : null;
            var sha = entry.TryGetProperty("sha", out var h) ? h.GetString() ?? "" : "";
            var size = entry.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : long.MaxValue;
            var download = entry.TryGetProperty("download_url", out var d) ? d.GetString() : null;
            if (name.Length == 0 || name.StartsWith('.') || name.Contains("..", StringComparison.Ordinal) || name.IndexOfAny(['\\', ':', '/']) >= 0) {
                continue;
            }

            var path = relative.Length == 0 ? name : relative + "/" + name;
            if (type == "dir" && depth < 1) {
                await ListAsync(http, repoPath + "/" + name, path, found, depth + 1);
            }
            else if (type == "file" && IsPage(name) && size <= MAX_BYTES && IsGitHubFile(download)) {
                found[path] = (sha, download!);
            }
        }
    }

    private static bool IsPage(string name) =>
        name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    /// <summary>Only files straight from GitHub are downloaded.</summary>
    private static bool IsGitHubFile(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    private static Index ReadIndex(string folder) {
        try {
            var path = Path.Combine(folder, INDEX_FILE);

            return File.Exists(path) ? JsonSerializer.Deserialize<Index>(File.ReadAllText(path)) ?? new Index() : new Index();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
            return new Index();
        }
    }

    /// <summary>The top pages first (README first of all), then each folder's, by name.</summary>
    private static List<Page> Sorted(List<Page> pages) => pages
        .OrderBy(p => p.Group.Length == 0 ? 0 : 1)
        .ThenBy(p => p.Group, StringComparer.OrdinalIgnoreCase)
        .ThenBy(p => Path.GetFileNameWithoutExtension(p.Path).Equals("README", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
        .ToList();

}
