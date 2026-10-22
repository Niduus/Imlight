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
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Imlight.Setup;

/// <summary>
/// Aurorium (Revive101's patch server), installed into a folder the player picks. It downloads every Wizard101 game
/// file into its own folder, and the server can be given those files instead of the ones in the player's Wizard101
/// folder (Niduus, 2026-10-22: people got confused setting it up by hand).
/// </summary>
/// <remarks>
/// Aurorium keeps each game revision in <c>&lt;folder&gt;\data\V_r…\</c> and stores a file only in the revision that changed
/// it, so a file is looked for in the newest revision first. Its own address is moved to <see cref="PORT"/>, so it never
/// takes the port the app's game files server uses.
/// </remarks>
internal static partial class Aurorium {

    internal const string FILE_NAME = "aurorium.ini";
    internal const string REPOSITORY = "Revive101/Aurorium";
    internal const string RELEASES_PAGE = "https://github.com/" + REPOSITORY + "/releases";
    internal const int PORT = 12368;
    private const string SECTION = "Aurorium";
    private const string DEFAULT_PATCH_PORT = "12369";

    /// <summary>Where Aurorium is, and whether the server takes its game files.</summary>
    internal sealed record Settings(string? Folder, bool UseForServer);

    internal static string DefaultFolder(string installFolder) => Path.Combine(installFolder, "Aurorium");

    internal static Settings Load(string installFolder) {
        try {
            var values = IniFile.ReadAll(Path.Combine(installFolder, FILE_NAME));
            string? Value(string key) => values.TryGetValue((SECTION, key), out var value) && value.Trim().Length > 0 ? value.Trim() : null;

            return new Settings(Value("Folder"), string.Equals(Value("UseForServer"), "true", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return new Settings(null, false);
        }
    }

    internal static void Save(string installFolder, Settings settings) {
        File.WriteAllLines(Path.Combine(installFolder, FILE_NAME), [
            "; Where Aurorium is installed, and whether the server takes its game files. The Aurorium card on the Tools",
            "; tab in the app changes it.",
            $"[{SECTION}]",
            $"Folder = {settings.Folder}",
            $"UseForServer = {(settings.UseForServer ? "true" : "false")}",
        ]);
    }

    // ---------------------------------------------------------------------
    // The folder
    // ---------------------------------------------------------------------

    /// <summary>Aurorium's program in the folder, or null when it isn't installed there.</summary>
    internal static string? FindExe(string folder) {
        if (!Directory.Exists(folder)) {
            return null;
        }

        var programs = OperatingSystem.IsWindows()
            ? Directory.GetFiles(folder, "*.exe")
            : Directory.GetFiles(folder).Where(f => Path.GetFileName(f).StartsWith("aurorium", StringComparison.OrdinalIgnoreCase)).ToArray();

        return programs.FirstOrDefault(f => Path.GetFileName(f).Contains("aurorium", StringComparison.OrdinalIgnoreCase))
            ?? (programs.Length == 1 ? programs[0] : null);
    }

    private static string ConfigFile(string folder) => Path.Combine(folder, "config.toml");

    /// <summary>The folder Aurorium saves the game files in (save_directory in its config.toml, "data" by default).</summary>
    internal static string DataFolder(string folder) {
        var config = ConfigFile(folder);
        var saved = File.Exists(config) ? SaveDirectoryLine().Match(File.ReadAllText(config)) : Match.Empty;

        return Path.GetFullPath(Path.Combine(folder, saved.Success ? saved.Groups[1].Value : "data"));
    }

    /// <summary>Each revision's GameData folder, newest revision first.</summary>
    internal static List<string> GameDataFolders(string folder) {
        var data = DataFolder(folder);
        if (!Directory.Exists(data)) {
            return [];
        }

        return Directory.GetDirectories(data, "V_*")
            .Select(revision => (Number: RevisionNumber(Path.GetFileName(revision)), GameData: Path.Combine(revision, "Data", "GameData")))
            .Where(r => Directory.Exists(r.GameData))
            .OrderByDescending(r => r.Number)
            .Select(r => r.GameData)
            .ToList();
    }

    private static long RevisionNumber(string name) {
        var match = RevisionName().Match(name);

        return match.Success && long.TryParse(match.Groups[1].Value, out var number) ? number : 0;
    }

    /// <summary>How many game files Aurorium has so far, and their size.</summary>
    internal static (int Files, long Bytes, int Revisions) CountFiles(string folder) {
        var revisions = GameDataFolders(folder);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var gameData in revisions) {
            foreach (var file in Directory.EnumerateFiles(gameData, "*.wad")) {
                if (names.Add(Path.GetFileName(file))) {
                    bytes += new FileInfo(file).Length;
                }
            }
        }

        return (names.Count, bytes, revisions.Count);
    }

    // ---------------------------------------------------------------------
    // Running it
    // ---------------------------------------------------------------------

    /// <summary>The port Aurorium answers on, from its config.toml.</summary>
    internal static int Port(string folder) {
        var config = ConfigFile(folder);
        var endpoint = File.Exists(config) ? EndpointLine().Match(File.ReadAllText(config)) : Match.Empty;

        return endpoint.Success && int.TryParse(endpoint.Groups[2].Value, out var port) ? port : PORT;
    }

    internal static bool IsRunning(string folder) => RunningProcesses(folder).Count > 0;

    private static List<Process> RunningProcesses(string folder) {
        var exe = FindExe(folder);
        if (exe is null) {
            return [];
        }

        var found = new List<Process>();
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe))) {
            try {
                if (string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) {
                    found.Add(process);
                    continue;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) {
                // Gone, or not ours to look at.
            }

            process.Dispose();
        }

        return found;
    }

    /// <summary>Starts Aurorium in its own window, so the player sees it downloading.</summary>
    internal static void Start(string folder) {
        var exe = FindExe(folder) ?? throw new InvalidOperationException("Aurorium isn't installed in " + folder + ".");
        WriteConfig(folder);
        Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = folder, UseShellExecute = true })?.Dispose();
    }

    /// <summary>Closes Aurorium. Returns how many copies were closed.</summary>
    internal static int Stop(string folder) {
        var processes = RunningProcesses(folder);
        foreach (var process in processes) {
            using (process) {
                try {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) {
                    // Already closed.
                }
            }
        }

        return processes.Count;
    }

    /// <summary>
    /// Writes Aurorium's config.toml on a first run, so it answers on <see cref="PORT"/>. An existing one is kept, but its
    /// address is moved off the port the app's own game files server needs.
    /// </summary>
    internal static void WriteConfig(string folder) {
        var config = ConfigFile(folder);
        if (!File.Exists(config)) {
            File.WriteAllLines(config, [
                "[server]",
                $"endpoint = \"127.0.0.1:{PORT}\"",
                "",
                "[fetcher]",
                "concurrent_downloads = 2",
                "save_directory = \"data\"",
                "fetch_interval = 28800",
                "",
                "[patch]",
                "host = \"patch.us.wizard101.com\"",
                "port = \"12500\"",
                "",
                "[database]",
                "path = \"aurorium.db\"",
            ]);

            return;
        }

        var text = File.ReadAllText(config);
        var endpoint = EndpointLine().Match(text);
        if (endpoint.Success && endpoint.Groups[2].Value == DEFAULT_PATCH_PORT) {
            File.WriteAllText(config, text[..endpoint.Index] + $"endpoint = \"{endpoint.Groups[1].Value}:{PORT}\"" + text[(endpoint.Index + endpoint.Length)..]);
        }
    }

    internal static bool IsListening(int port) {
        try {
            using var client = new TcpClient();

            return client.ConnectAsync("127.0.0.1", port).Wait(300) && client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException) {
            return false;
        }
    }

    // ---------------------------------------------------------------------
    // Installing
    // ---------------------------------------------------------------------

    /// <summary>
    /// Downloads Aurorium's newest Windows release from GitHub into the folder. A zip is unpacked there; a program is saved
    /// as it is. The game files it already downloaded are kept.
    /// </summary>
    /// <returns>The version installed.</returns>
    internal static async Task<string> InstallAsync(string folder, IProgress<int>? percent = null) {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ImlightSetup", SelfUpdate.CurrentVersion.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var document = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{REPOSITORY}/releases/latest"));
        var release = document.RootElement;
        var version = release.GetProperty("tag_name").GetString() ?? "?";
        var assets = release.GetProperty("assets").EnumerateArray()
            .Select(a => (Name: a.GetProperty("name").GetString() ?? "", Url: a.GetProperty("browser_download_url").GetString() ?? ""))
            .ToList();
        var asset = PickAsset(assets)
            ?? throw new InvalidOperationException($"Aurorium {version} has no Windows download. Get it from {RELEASES_PAGE} and put it in {folder}.");

        Directory.CreateDirectory(folder);
        using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        var download = Path.Combine(folder, asset.Name + ".download");
        await using (var file = File.Create(download)) {
            await using var stream = await response.Content.ReadAsStreamAsync();
            var chunk = new byte[81920];
            long done = 0;
            var lastShown = -1;
            int read;
            while ((read = await stream.ReadAsync(chunk)) > 0) {
                await file.WriteAsync(chunk.AsMemory(0, read));
                done += read;
                var shown = total > 0 ? (int) (done * 100 / total) : -1;
                if (shown != lastShown) {
                    lastShown = shown;
                    percent?.Report(shown);
                }
            }
        }

        try {
            if (asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) {
                ZipFile.ExtractToDirectory(download, folder, overwriteFiles: true);
                // A zip with everything in one folder: bring the program up to the chosen folder.
                if (FindExe(folder) is null) {
                    var inner = Directory.GetDirectories(folder).Select(FindExe).FirstOrDefault(e => e is not null);
                    if (inner is not null) {
                        File.Copy(inner, Path.Combine(folder, Path.GetFileName(inner)), overwrite: true);
                    }
                }
            }
            else {
                var name = asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? asset.Name : "aurorium.exe";
                File.Copy(download, Path.Combine(folder, name), overwrite: true);
            }
        }
        finally {
            File.Delete(download);
        }

        if (FindExe(folder) is null) {
            throw new InvalidOperationException($"The download had no Aurorium program in it. Get it from {RELEASES_PAGE} and put it in {folder}.");
        }

        WriteConfig(folder);

        return version;
    }

    /// <summary>The release download for Windows: a program or zip that names Windows, then any program, then any zip.</summary>
    internal static (string Name, string Url)? PickAsset(List<(string Name, string Url)> assets) {
        static bool Is(string name, string end) => name.EndsWith(end, StringComparison.OrdinalIgnoreCase);
        static bool NamesWindows(string name) => name.Contains("win", StringComparison.OrdinalIgnoreCase)
            || name.Contains("msvc", StringComparison.OrdinalIgnoreCase);
        static bool NamesOther(string name) => name.Contains("linux", StringComparison.OrdinalIgnoreCase)
            || name.Contains("mac", StringComparison.OrdinalIgnoreCase) || name.Contains("darwin", StringComparison.OrdinalIgnoreCase);

        var usable = assets.Where(a => (Is(a.Name, ".exe") || Is(a.Name, ".zip")) && !NamesOther(a.Name)).ToList();
        var pick = usable.FirstOrDefault(a => Is(a.Name, ".exe") && NamesWindows(a.Name));
        pick = pick.Name is not null ? pick : usable.FirstOrDefault(a => Is(a.Name, ".zip") && NamesWindows(a.Name));
        pick = pick.Name is not null ? pick : usable.FirstOrDefault(a => Is(a.Name, ".exe"));
        pick = pick.Name is not null ? pick : usable.FirstOrDefault(a => Is(a.Name, ".zip"));

        return pick.Name is not null ? pick : null;
    }

    [GeneratedRegex(@"^\s*save_directory\s*=\s*""([^""]*)""", RegexOptions.Multiline)]
    private static partial Regex SaveDirectoryLine();

    [GeneratedRegex(@"^\s*endpoint\s*=\s*""([^""]*):(\d+)""", RegexOptions.Multiline)]
    private static partial Regex EndpointLine();

    [GeneratedRegex(@"^V_r(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RevisionName();

}
