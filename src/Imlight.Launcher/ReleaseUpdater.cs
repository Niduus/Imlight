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
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Imlight.Launcher;

/// <summary>
/// A release on GitHub, trimmed to what the launcher reads.
/// </summary>
internal sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("assets")] List<GitHubAsset> Assets) {

    internal Version? Version => ReleaseUpdater.ParseVersion(TagName);

    internal GitHubAsset? Asset(string name)
        => Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

}

/// <summary>
/// A file attached to a release.
/// </summary>
internal sealed record GitHubAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string DownloadUrl,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("digest")] string? Digest);

/// <summary>
/// Finds and installs releases from a public GitHub repository. No token is needed for a public repository.
/// </summary>
internal sealed class ReleaseUpdater : IDisposable {

    internal const string LAUNCHER_ASSET = "ImlightLauncher-win-x64.zip";
    internal const string SERVER_ASSET = "ImlightServer-win-x64.zip";
    internal const string SERVER_VERSION_FILE = "version.txt";

    // Files the server owner edits or the server writes; an update never replaces them.
    private static readonly string[] s_keptFiles = [
        "Config/Imlight.ini",
    ];

    private readonly HttpClient _http;
    private readonly LauncherSettings _settings;

    internal ReleaseUpdater(LauncherSettings settings, string userAgentVersion) {
        _settings = settings;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ImlightLauncher", userAgentVersion));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>
    /// Returns the newest release with a version tag, or null when there is none or GitHub cannot be reached.
    /// </summary>
    internal async Task<GitHubRelease?> GetLatestReleaseAsync() {
        try {
            var url = $"https://api.github.com/repos/{_settings.Repository}/releases?per_page=100"; // bot updates (bots-v...) are releases too, many of them (Niduus, 2026-11-19)
            using var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode) {
                Console.WriteLine($"Could not check for updates: GitHub answered {(int) response.StatusCode} {response.ReasonPhrase}.");

                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(stream) ?? [];

            return releases
                .Where(r => !r.Draft && (_settings.IncludePrereleases || !r.Prerelease) && r.Version is not null)
                .OrderByDescending(r => r.Version)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) {
            Console.WriteLine($"Could not check for updates: {ex.Message}");

            return null;
        }
    }

    /// <summary>
    /// Downloads a release asset to a temporary file, checking its size and, when GitHub gives one, its SHA-256.
    /// </summary>
    internal async Task<string> DownloadAsync(GitHubAsset asset) {
        var path = Path.Combine(Path.GetTempPath(), $"imlight-{Guid.NewGuid():N}-{asset.Name}");
        Console.WriteLine($"Downloading {asset.Name} ({asset.Size / 1024 / 1024} MB)..");

        using (var response = await _http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead)) {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var target = File.Create(path);
            await source.CopyToAsync(target);
        }

        var length = new FileInfo(path).Length;
        if (asset.Size > 0 && length != asset.Size) {
            File.Delete(path);
            throw new InvalidDataException($"{asset.Name} downloaded {length} bytes but should be {asset.Size}.");
        }

        if (asset.Digest is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) {
            await using var file = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(file));
            if (!actual.Equals(digest["sha256:".Length..], StringComparison.OrdinalIgnoreCase)) {
                file.Close();
                File.Delete(path);
                throw new InvalidDataException($"{asset.Name} did not match its checksum. The download was discarded.");
            }
        }

        return path;
    }

    /// <summary>
    /// Installs the server from a downloaded zip, keeping the owner's configuration.
    /// </summary>
    internal static void InstallServer(string zipPath, string serverFolder, Version version) {
        var root = Path.GetFullPath(serverFolder);
        Directory.CreateDirectory(root);

        using (var zip = ZipFile.OpenRead(zipPath)) {
            foreach (var entry in zip.Entries) {
                var relative = entry.FullName.Replace('\\', '/');
                var target = Path.GetFullPath(Path.Combine(root, relative));

                // Refuse entries that would land outside the server folder.
                if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                if (relative.EndsWith('/')) {
                    Directory.CreateDirectory(target);

                    continue;
                }

                if (File.Exists(target) && s_keptFiles.Any(k => k.Equals(relative, StringComparison.OrdinalIgnoreCase))) {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }

        File.WriteAllText(Path.Combine(root, SERVER_VERSION_FILE), version.ToString());
    }

    /// <summary>
    /// Replaces the running launcher with the one in a downloaded zip. Windows lets a running program
    /// be renamed, so the old one is moved aside and removed on the next start.
    /// </summary>
    internal static void InstallLauncher(string zipPath) {
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("The launcher cannot find itself.");
        var folder = Path.GetDirectoryName(current)!;
        var exeName = Path.GetFileName(current);

        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(exeName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"The launcher update does not contain {exeName}.");

        var staged = Path.Combine(folder, exeName + ".new");
        entry.ExtractToFile(staged, overwrite: true);

        var old = current + ".old";
        File.Delete(old);
        File.Move(current, old);
        File.Move(staged, current);
    }

    /// <summary>
    /// Removes the launcher left behind by the last self-update.
    /// </summary>
    internal static void CleanUpOldLauncher() {
        var old = Environment.ProcessPath + ".old";
        try {
            File.Delete(old);
        }
        catch (IOException) {
            // Still in use if the old launcher has not exited yet; next start removes it.
        }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Reads "v1.2.3", "1.2.3" or "v1.2.3-beta" as a version.
    /// </summary>
    internal static Version? ParseVersion(string? text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return null;
        }

        text = text.Trim().TrimStart('v', 'V');
        var dash = text.IndexOfAny(['-', '+']);
        if (dash >= 0) {
            text = text[..dash];
        }

        if (!Version.TryParse(text.Contains('.') ? text : text + ".0", out var version)) {
            return null;
        }

        // Treat missing parts as zero so 1.2 and 1.2.0 compare equal.
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }

    public void Dispose()
        => _http.Dispose();

}
