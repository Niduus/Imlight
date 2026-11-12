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
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace Imlight.Setup;

/// <summary>
/// Niduus's own overlay add-ons (Niduus, 2026-11-12: "have all UI's crafted by me as their own section in the repo for
/// the EXE to find and option to download"). They live in the <see cref="REPO_FOLDER"/> folder of Niduus/Imlight on
/// GitHub; Ahziael lists that folder and downloads the ones picked. A download goes through the same check as an add-on
/// file (<see cref="OverlayAddon.FromJson"/>), so it can only add texts, boxes, pictures and minimaps, never run anything.
/// </summary>
internal static class OverlayAddonShelf {

    internal const string REPO_FOLDER = "overlay-addons";

    internal static string PageUrl => $"https://github.com/{SelfUpdate.RELEASE_REPOSITORY}/tree/HEAD/{REPO_FOLDER}";

    /// <summary>One add-on on the shelf.</summary>
    internal sealed record Item(string FileName, OverlayAddon Addon);

    private static List<Item>? s_cached;

    /// <summary>The add-ons in the repo's folder (kept after the first look, unless <paramref name="again"/>).</summary>
    internal static async Task<List<Item>> ListAsync(bool again = false) {
        if (!again && s_cached is { } cached) {
            return cached;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ImlightSetup", SelfUpdate.CurrentVersion.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var listing = await http.GetAsync($"https://api.github.com/repos/{SelfUpdate.RELEASE_REPOSITORY}/contents/{REPO_FOLDER}");
        if (listing.StatusCode == System.Net.HttpStatusCode.NotFound) {
            return s_cached = [];
        }

        listing.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await listing.Content.ReadAsStringAsync());
        var items = new List<Item>();
        foreach (var entry in document.RootElement.EnumerateArray()) {
            var name = entry.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var type = entry.TryGetProperty("type", out var t) ? t.GetString() : null;
            var size = entry.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : long.MaxValue;
            var url = entry.TryGetProperty("download_url", out var d) ? d.GetString() : null;
            if (type != "file" || !name.EndsWith(OverlayAddon.EXTENSION, StringComparison.OrdinalIgnoreCase)
                || size > OverlayAddon.MAX_BYTES || !IsGitHubFile(url)) {
                continue;
            }

            try {
                if (OverlayAddon.FromJson(await http.GetStringAsync(url)) is { } addon) {
                    items.Add(new Item(name, addon));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
                // One that won't download is left out; the others still show.
            }
        }

        return s_cached = items.OrderBy(i => i.Addon.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Only files straight from GitHub are downloaded.</summary>
    private static bool IsGitHubFile(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>What this computer has of one: not added, added (the same one), or added but the shelf has a newer one.</summary>
    internal enum State { New, Have, Newer }

    internal static State StateOf(OverlayAddon shelf, IEnumerable<OverlayAddon> installed) {
        var mine = installed.FirstOrDefault(a => a.Name.Equals(shelf.Name, StringComparison.OrdinalIgnoreCase));
        if (mine is null) {
            return State.New;
        }

        return IsNewer(shelf.Version, mine.Version) ? State.Newer : State.Have;
    }

    /// <summary>"1.2" is newer than "1.1" (and "10" than "9"); words that aren't numbers only count when they differ.</summary>
    internal static bool IsNewer(string shelf, string mine) {
        if (Version.TryParse(Pad(shelf), out var a) && Version.TryParse(Pad(mine), out var b)) {
            return a > b;
        }

        return !string.Equals(shelf.Trim(), mine.Trim(), StringComparison.OrdinalIgnoreCase);

        static string Pad(string v) => v.Trim().TrimStart('v', 'V').Contains('.') ? v.Trim().TrimStart('v', 'V') : v.Trim().TrimStart('v', 'V') + ".0";
    }

}
