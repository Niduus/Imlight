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
 *
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Imlight.Common;

namespace Imlight.CoreLib.Game;

/// <summary>
/// Bot updates (Niduus, 2026-11-19): the bots take a new version only when Niduus publishes one (a "bots-v12" release of the
/// repository, made by the Publish bot update workflow), never just because a fix was merged. The server looks for one
/// every 10 minutes ([Bots] LiveUpdates, on by default), downloads it, builds it here and swaps the bots without a
/// restart. When the server itself is updated and its own bots are as new as the bot update or newer, its own run.
/// </summary>
internal static class BotUpdates {

    private const string TAG_PREFIX = "bots-v";
    private const string ASSET = "bots-update.zip";
    private const string INFO_FILE = "update.json";
    private const string STATE_FILE = "state.json";
    private const string CHECK_NOW_FILE = "check-now.txt";

    private static readonly HttpClient s_http = MakeClient();
    private static readonly List<string> s_notes = [];
    private static bool s_started;
    private static DateTime s_lastCheck;
    private static string s_lastCheckResult = "";

    /// <summary>Whether the server looks for bot updates ([Bots] LiveUpdates). Read live.</summary>
    internal static bool On => ConfigurationManager.GetValue("Bots.LiveUpdates", true);

    /// <summary>The repository bot updates come from ([Bots] UpdateRepository).</summary>
    private static string Repository => ConfigurationManager.GetValue("Bots.UpdateRepository", "Niduus/Imlight");

    private static HttpClient MakeClient() {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Imlight-Server");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        return client;
    }

    /// <summary>A line for Ahziael's Bots page (the last few are kept).</summary>
    internal static void Note(string text) {
        lock (s_notes) {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm}  {text}";
            if (s_notes.Count > 0 && s_notes[^1][18..] == line[18..]) {
                return;
            }

            s_notes.Add(line);
            if (s_notes.Count > 20) {
                s_notes.RemoveAt(0);
            }
        }

        WriteState();
    }

    /// <summary>Starts looking for bot updates (the game server only; once).</summary>
    internal static void Start() {
        if (s_started) {
            return;
        }

        s_started = true;
        _ = Task.Run(async () => {
            await Task.Delay(TimeSpan.FromMinutes(1));
            var next = DateTime.UtcNow;
            while (true) {
                try {
                    var asked = File.Exists(Path.Combine(BotLoader.Folder, CHECK_NOW_FILE));
                    if (asked) {
                        File.Delete(Path.Combine(BotLoader.Folder, CHECK_NOW_FILE));
                    }

                    if (asked || (On && DateTime.UtcNow >= next)) {
                        next = DateTime.UtcNow.AddMinutes(10);
                        await CheckAsync();
                    }
                }
                catch (Exception e) {
                    Logger.Error("Bots: the bot update check failed: {0}", Logger.Args(e));
                }

                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        });
    }

    // Like the server's own restart for an update (Ahziael, 2 minutes with warnings at 1 minute and 30 seconds; Niduus,
    // 2026-11-19: "have an announcement for when bots go down like when the server goes down (2 minutes)"). Nothing holds
    // the bots back after it: no battle or player waits it out, they go down and come back right after.
    private static readonly TimeSpan s_countdown = TimeSpan.FromMinutes(2);

    private static async Task<bool> CountDownAsync() {
        if (Tell($"The bots go down for an update in {(int) s_countdown.TotalMinutes} minutes. They log back in by themselves right after.") == 0) {
            // Nobody online to warn: no wait.
            return false;
        }

        Note($"bots go down in {(int) s_countdown.TotalMinutes} minutes (players told)");
        await Task.Delay(s_countdown - TimeSpan.FromMinutes(1));
        Tell("The bots go down for an update in 1 minute.");
        await Task.Delay(TimeSpan.FromSeconds(30));
        Tell("The bots go down for an update in 30 seconds.");
        await Task.Delay(TimeSpan.FromSeconds(30));
        Tell("The bots are going down for their update now. They will be back in a moment.");
        return true;
    }

    private static int Tell(string text) {
        try {
            return Tools.ManagerCommands.Announce(box: false, chat: true, text);
        }
        catch (Exception e) {
            Logger.Warning("Bots: could not tell players about the bot update: {0}", Logger.Args(e.Message));
            return 0;
        }
    }

    private sealed record Published(int Number, string Tag, string ZipUrl, string Notes, DateTime When);

    /// <summary>Looks for a newer bot update, gets it and swaps the bots to it when it is newer than what runs.</summary>
    private static async Task CheckAsync() {
        s_lastCheck = DateTime.Now;
        var newest = await NewestPublishedAsync();
        if (newest is null) {
            s_lastCheckResult = "no bot update is published yet";
            WriteState();

            return;
        }

        var folder = Path.Combine(BotLoader.UpdatesFolder, newest.Tag);
        if (!File.Exists(Path.Combine(folder, INFO_FILE))) {
            Note($"downloading {newest.Tag}");
            if (await DownloadAsync(newest, folder) is { } failed) {
                s_lastCheckResult = $"{newest.Tag} could not be downloaded: {failed}";
                Note(s_lastCheckResult);

                return;
            }
        }

        s_lastCheckResult = $"newest bot update: {newest.Tag}";
        var pick = await PickAsync(newest.Tag);
        if (pick != BotDoor.Version) {
            var told = await CountDownAsync();
            var why = await BotLoader.SwapAsync(pick, pick == BotLoader.BUILT_IN ? "the server's own bots are newer" : "a newer bot update was published");
            if (why is not null) {
                Note($"could not swap to {BotLoader.Label(pick)}: {why}");
            }

            if (told) {
                Tell(why is null ? "The bots are back from their update." : "The bots could not take their update and are back as they were.");
            }
        }

        WriteState();
    }

    // The newest bots-v<number> release (not a draft).
    private static async Task<Published> NewestPublishedAsync() {
        var json = await s_http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases?per_page=50");
        Published best = null;
        foreach (var release in JsonNode.Parse(json)?.AsArray() ?? []) {
            var tag = (string) release?["tag_name"] ?? "";
            if ((bool?) release?["draft"] == true || !tag.StartsWith(TAG_PREFIX, StringComparison.OrdinalIgnoreCase)
                || !int.TryParse(tag[TAG_PREFIX.Length..], out var number)) {
                continue;
            }

            var zip = release["assets"]?.AsArray().FirstOrDefault(a => (string) a?["name"] == ASSET);
            if (zip is null || (best is not null && best.Number >= number)) {
                continue;
            }

            best = new Published(number, $"{TAG_PREFIX}{number}", (string) zip["browser_download_url"], (string) release["body"] ?? "",
                DateTime.TryParse((string) release["published_at"], out var when) ? when : DateTime.UtcNow);
        }

        return best;
    }

    private static async Task<string> DownloadAsync(Published update, string folder) {
        var part = folder + ".part";
        try {
            if (Directory.Exists(part)) {
                Directory.Delete(part, recursive: true);
            }

            Directory.CreateDirectory(part);
            var zipPath = Path.Combine(part, ASSET);
            await using (var file = File.Create(zipPath)) {
                await using var stream = await s_http.GetStreamAsync(update.ZipUrl);
                await stream.CopyToAsync(file);
            }

            var source = Path.Combine(part, "source");
            using (var archive = ZipFile.OpenRead(zipPath)) {
                foreach (var entry in archive.Entries.Where(e => e.Name.Length > 0)) {
                    // Only the bots' own files, never anything outside the folder.
                    var target = Path.GetFullPath(Path.Combine(source, entry.FullName));
                    if (!target.StartsWith(Path.GetFullPath(source) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) {
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
            }

            File.Delete(zipPath);
            var made = File.Exists(Path.Combine(source, "bot-update.json"))
                ? JsonNode.Parse(File.ReadAllText(Path.Combine(source, "bot-update.json")))
                : null;
            File.WriteAllText(Path.Combine(part, INFO_FILE), new JsonObject {
                ["tag"] = update.Tag,
                ["number"] = update.Number,
                ["commit"] = (string) made?["commit"] ?? "",
                ["needsServer"] = (string) made?["needsServer"] ?? "",
                ["published"] = update.When.ToString("o"),
                ["notes"] = update.Notes,
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if (Directory.Exists(folder)) {
                Directory.Delete(folder, recursive: true);
            }

            Directory.Move(part, folder);

            return null;
        }
        catch (Exception e) {
            return e.Message;
        }
    }

    /// <summary>
    /// Which bots should run: the newest bot update, unless the server was built from the same or a later commit (its own
    /// bots then hold the update already). Without GitHub, what ran last stays.
    /// </summary>
    private static async Task<string> PickAsync(string newest) {
        var botsCommit = Info(newest)?["commit"]?.GetValue<string>() ?? "";
        var serverCommit = ServerCommit();
        if (botsCommit.Length == 0 || serverCommit.Length == 0) {
            return newest;
        }

        try {
            var status = await CompareAsync(serverCommit, botsCommit);

            // "ahead": the bot update came after the server's own code. "identical" or "behind": the server's own bots are
            // as new or newer. "diverged" cannot happen on one branch; the update is taken then.
            if (status is "identical" or "behind") {
                return BotLoader.BUILT_IN;
            }

            // A bot fix made together with a change to the server's own code (one pull request touching both) waits for
            // the full update that brings that server change; until then the bots that run stay (Niduus, 2026-11-19:
            // every bot fix goes out on its own, so the ones that cannot run on this server yet must not break it).
            var needs = Info(newest)?["needsServer"]?.GetValue<string>() ?? "";
            if (needs.Length > 0 && await CompareAsync(serverCommit, needs) == "ahead") {
                s_lastCheckResult = $"{newest} needs the next full update first (it goes with a server change)";
                Note(s_lastCheckResult);

                return BotDoor.Version is { Length: > 0 } running && running != newest ? running : BotLoader.BUILT_IN;
            }

            return newest;
        }
        catch (Exception) {
            return BotDoor.Version is { Length: > 0 } running ? running : BotLoader.Wanted;
        }
    }

    // How commit "to" stands against commit "from" on GitHub: "ahead" when it came after, "identical", "behind" or "diverged".
    private static async Task<string> CompareAsync(string from, string to) {
        var json = await s_http.GetStringAsync($"https://api.github.com/repos/{Repository}/compare/{from}...{to}");

        return (string) JsonNode.Parse(json)?["status"] ?? "";
    }

    // The commit the server was built from (Ahziael writes it next to the server: "repo branch commit").
    private static string ServerCommit() {
        try {
            var path = Path.Combine(AppContext.BaseDirectory, "server-source-commit.txt");
            var parts = File.Exists(path) ? File.ReadAllText(path).Trim().Split(' ') : [];

            return parts.Length == 3 && parts[2].Length >= 7 && parts[2].All(Uri.IsHexDigit) ? parts[2] : "";
        }
        catch (IOException) {
            return "";
        }
    }

    private static JsonNode Info(string version) {
        try {
            var path = Path.Combine(BotLoader.UpdatesFolder, version, INFO_FILE);

            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception) {
            return null;
        }
    }

    /// <summary>Writes what Ahziael's Bots page shows: which bots run, the bot updates known and their notes.</summary>
    internal static void WriteState() {
        try {
            var updates = new JsonArray();
            if (Directory.Exists(BotLoader.UpdatesFolder)) {
                foreach (var dir in Directory.GetDirectories(BotLoader.UpdatesFolder)
                             .Select(Path.GetFileName)
                             .Where(n => n.StartsWith(TAG_PREFIX, StringComparison.OrdinalIgnoreCase) && !n.EndsWith(".part"))
                             .OrderByDescending(n => int.TryParse(n[TAG_PREFIX.Length..], out var x) ? x : 0)
                             .Take(20)) {
                    var info = Info(dir);
                    updates.Add(new JsonObject {
                        ["tag"] = dir,
                        ["published"] = (string) info?["published"] ?? "",
                        ["notes"] = (string) info?["notes"] ?? "",
                    });
                }
            }

            JsonArray notes;
            lock (s_notes) {
                notes = new JsonArray([.. s_notes.Select(n => (JsonNode) n)]);
            }

            var state = new JsonObject {
                ["running"] = BotDoor.Side is null ? "" : BotDoor.Version,
                ["liveUpdates"] = On,
                ["lastCheck"] = s_lastCheck == default ? "" : s_lastCheck.ToString("yyyy-MM-dd HH:mm"),
                ["lastCheckResult"] = s_lastCheckResult,
                ["problem"] = BotLoader.Problem,
                ["updates"] = updates,
                ["log"] = notes,
            };
            Directory.CreateDirectory(BotLoader.Folder);
            var path = Path.Combine(BotLoader.Folder, STATE_FILE);
            File.WriteAllText(path + ".tmp", state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception e) {
            Logger.Debug("Bots: could not write the bots' state: {0}", Logger.Args(e.Message));
        }
    }

}
