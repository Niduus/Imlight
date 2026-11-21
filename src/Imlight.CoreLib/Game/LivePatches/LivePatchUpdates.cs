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
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Imlight.Common;

namespace Imlight.CoreLib.Game.LivePatches;

/// <summary>
/// Live patches from GitHub (Niduus, 2026-11-20): a "live-v12" release of the repository (made by the Publish live patch
/// workflow) holds the server's code at a fix. With Ahziael's "Live patches" switch on ([LivePatches] Enabled, off by
/// default, read live) the game server looks for a newer one every 10 minutes and puts it in while everyone keeps playing.
/// A live patch that can't go into a running server waits for the next full update; nothing else changes.
/// </summary>
public static class LivePatchUpdates {

    private const string TAG_PREFIX = "live-v";
    private const string ASSET = "live-patch.zip";
    private const string STATE_FILE = "state.json";
    private const string CHECK_NOW_FILE = "check-now.txt";
    // Ahziael installed a full update while the server ran (Niduus, 2026-11-21: "What's the point of Live Updates if having
    // it on still requires the Update and restart method"): it names an "ahziael-0.10.937" folder under updates\ that holds
    // the update's server code; the server puts it in like a live patch and answers in that folder's result.json.
    private const string PUT_IN_FILE = "put-in.txt";
    private const string RESULT_FILE = "result.json";
    private const string AHZIAEL_PREFIX = "ahziael-";

    private static readonly List<string> s_notes = [];
    private static bool s_started;
    private static DateTime s_lastCheck;
    private static string s_lastCheckResult = "";
    private static string s_active = "";
    private static string s_waiting = "";
    private static string s_waitingWhy = "";

    /// <summary>Whether the server takes live patches ([LivePatches] Enabled, Ahziael's switch). Read live.</summary>
    internal static bool On => ConfigurationManager.GetValue("LivePatches.Enabled", false);

    private static string UpdatesFolder => Path.Combine(LivePatcher.Folder, "updates");

    private static string StatePath => Path.Combine(LivePatcher.Folder, STATE_FILE);

    /// <summary>The game server is up: puts back the live patch it ran last time (same build only) and starts looking.</summary>
    public static void Start() {
        if (s_started) {
            return;
        }

        s_started = true;
        LivePatcher.StartChain();
        var last = ReadState();
        var lastActive = (string) last?["active"] ?? "";
        var lastModule = (string) last?["module"] ?? "";
        s_waiting = (string) last?["waiting"] ?? "";
        s_waitingWhy = (string) last?["waitingWhy"] ?? "";
        if (lastModule != LivePatcher.Target.ManifestModule.ModuleVersionId.ToString()) {
            // A new build of the server (a full update) holds every fix that came before it.
            lastActive = "";
            s_waiting = "";
            s_waitingWhy = "";
        }

        WriteState();
        if (LivePatcher.WhyNot is { } why) {
            Logger.Information("Live patches: off for this server, because {0}.", Logger.Args(why));

            return;
        }

        _ = Task.Run(async () => {
            if (lastActive.Length > 0 && On) {
                await Task.Delay(TimeSpan.FromSeconds(20));
                await PutInAsync(lastActive, "it ran before the server restarted");
            }

            var next = DateTime.UtcNow.AddMinutes(1);
            while (true) {
                try {
                    var nowFile = Path.Combine(LivePatcher.Folder, CHECK_NOW_FILE);
                    var asked = File.Exists(nowFile);
                    if (asked) {
                        File.Delete(nowFile);
                    }

                    if (TakeRequest() is { } label) {
                        await PutInFromAhziaelAsync(label);
                    }

                    if (asked || (On && DateTime.UtcNow >= next)) {
                        next = DateTime.UtcNow.AddMinutes(10);
                        await CheckAsync();
                    }
                }
                catch (Exception e) {
                    Logger.Error("Live patches: the check failed: {0}", Logger.Args(e));
                }

                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        });
    }

    private sealed record Published(int Number, string Tag, string ZipUrl, string Notes, DateTime When);

    private static async Task CheckAsync() {
        s_lastCheck = DateTime.Now;
        var newest = await NewestPublishedAsync();
        if (newest is null) {
            s_lastCheckResult = "no live patch is published yet";
            WriteState();

            return;
        }

        if (newest.Tag == s_active || newest.Tag == s_waiting) {
            s_lastCheckResult = newest.Tag == s_active ? $"{newest.Tag} is in" : $"{newest.Tag} waits for the next full update";
            WriteState();

            return;
        }

        // An update Ahziael put in holds every live patch published before it.
        if (s_active.StartsWith(AHZIAEL_PREFIX, StringComparison.OrdinalIgnoreCase)
            && DateTimeOffset.TryParse((string) Info(s_active)?["published"], out var putIn)
            && new DateTimeOffset(newest.When.ToUniversalTime()) <= putIn) {
            s_lastCheckResult = $"{newest.Tag} is older than the update that is in";
            WriteState();

            return;
        }

        var folder = Path.Combine(UpdatesFolder, newest.Tag);
        if (!File.Exists(Path.Combine(folder, "update.json"))) {
            Note($"downloading {newest.Tag}");
            if (await DownloadAsync(newest, folder) is { } failed) {
                s_lastCheckResult = $"{newest.Tag} could not be downloaded: {failed}";
                Note(s_lastCheckResult);

                return;
            }
        }

        // A live patch made before this server was built is already in it.
        var commit = (string) Info(newest.Tag)?["commit"] ?? "";
        var server = BotUpdates.ServerCommit();
        if (commit.Length > 0 && server.Length > 0) {
            try {
                if (await BotUpdates.CompareAsync(server, commit) is "identical" or "behind") {
                    s_lastCheckResult = $"{newest.Tag} is older than this server, which has its fixes already";
                    s_active = "";
                    WriteState();

                    return;
                }
            }
            catch (Exception) {
                // GitHub could not say: the code itself tells, below (nothing new means nothing changes).
            }
        }

        await PutInAsync(newest.Tag, "a newer live patch was published");
    }

    /// <summary>The update Ahziael asks the server to put in (put-in.txt), taken once; null when none.</summary>
    private static string TakeRequest() {
        var path = Path.Combine(LivePatcher.Folder, PUT_IN_FILE);
        if (!File.Exists(path)) {
            return null;
        }

        var label = File.ReadAllText(path).Trim();
        File.Delete(path);

        // Only a folder name under updates\.
        return label.Length > 0 && label.IndexOfAny(['/', '\\', ':']) < 0 && !label.Contains("..") ? label : null;
    }

    private static async Task PutInFromAhziaelAsync(string label) {
        var outcome = await PutInAsync(label, "Ahziael installed an update while the server ran");
        try {
            File.WriteAllText(Path.Combine(UpdatesFolder, label, RESULT_FILE), new JsonObject {
                ["done"] = outcome?.Done == true,
                ["methods"] = outcome?.Methods ?? 0,
                ["problem"] = outcome is null ? "its code was not on this PC" : outcome.Problem ?? "",
                ["module"] = LivePatcher.Target.ManifestModule.ModuleVersionId.ToString(),
                ["at"] = DateTime.Now.ToString("o"),
            }.ToJsonString());
        }
        catch (Exception e) {
            Logger.Warning("Live patches: could not tell Ahziael how {0} went: {1}", Logger.Args(label, e.Message));
        }
    }

    private static async Task<LivePatcher.Outcome> PutInAsync(string tag, string why) {
        var source = Path.Combine(UpdatesFolder, tag, "source");
        if (!Directory.Exists(source)) {
            Note($"{tag} is no longer on this PC");

            return null;
        }

        var started = DateTime.UtcNow;
        var outcome = await LivePatcher.ApplyAsync(source, tag);
        if (outcome.Done) {
            s_active = tag;
            s_waiting = "";
            s_waitingWhy = "";
            s_lastCheckResult = outcome.NothingNew ? $"{tag} has nothing this server doesn't have" : $"{tag} is in";
            var notes = outcome.Notes.Count > 0 ? $" Notes: {string.Join(" | ", outcome.Notes)}" : "";
            if (!outcome.NothingNew) {
                Logger.Information("Live patches: {0} is in ({1}, {2} methods changed, {3:0.0} s). Nobody was disconnected.{4}",
                    Logger.Args(tag, why, outcome.Methods, (DateTime.UtcNow - started).TotalSeconds, notes));
                Note($"{tag} is in ({outcome.Methods} methods changed){notes}");
            }
        }
        else {
            s_waiting = tag;
            s_waitingWhy = outcome.Problem;
            s_lastCheckResult = $"{tag} waits for the next full update";
            Logger.Warning("Live patches: {0} waits for the next full update, because {1}. The server keeps running as it is.",
                Logger.Args(tag, outcome.Problem));
            Note($"{tag} waits for the next full update: {outcome.Problem}");
        }

        WriteState();

        return outcome;
    }

    private static async Task<Published> NewestPublishedAsync() {
        var json = await BotUpdates.Http.GetStringAsync($"https://api.github.com/repos/{BotUpdates.Repository}/releases?per_page=50");
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
                await using var stream = await BotUpdates.Http.GetStreamAsync(update.ZipUrl);
                await stream.CopyToAsync(file);
            }

            var source = Path.Combine(part, "source");
            using (var archive = ZipFile.OpenRead(zipPath)) {
                foreach (var entry in archive.Entries.Where(e => e.Name.Length > 0)) {
                    // Only inside the folder.
                    var target = Path.GetFullPath(Path.Combine(source, entry.FullName));
                    if (!target.StartsWith(Path.GetFullPath(source) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) {
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
            }

            File.Delete(zipPath);
            var made = File.Exists(Path.Combine(source, "live-patch.json"))
                ? JsonNode.Parse(File.ReadAllText(Path.Combine(source, "live-patch.json")))
                : null;
            File.WriteAllText(Path.Combine(part, "update.json"), new JsonObject {
                ["tag"] = update.Tag,
                ["number"] = update.Number,
                ["commit"] = (string) made?["commit"] ?? "",
                ["published"] = update.When.ToString("o"),
                ["notes"] = update.Notes,
            }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            if (Directory.Exists(folder)) {
                Directory.Delete(folder, recursive: true);
            }

            Directory.Move(part, folder);

            // Only the few newest stay on the PC.
            foreach (var old in Directory.GetDirectories(UpdatesFolder)
                         .Select(Path.GetFileName)
                         .Where(n => n.StartsWith(TAG_PREFIX, StringComparison.OrdinalIgnoreCase) && !n.EndsWith(".part"))
                         .OrderByDescending(n => int.TryParse(n[TAG_PREFIX.Length..], out var x) ? x : 0)
                         .Skip(5)) {
                if (old != s_active) {
                    Directory.Delete(Path.Combine(UpdatesFolder, old), recursive: true);
                }
            }

            return null;
        }
        catch (Exception e) {
            return e.Message;
        }
    }

    private static JsonNode Info(string tag) {
        try {
            var path = Path.Combine(UpdatesFolder, tag, "update.json");

            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception) {
            return null;
        }
    }

    private static void Note(string text) {
        lock (s_notes) {
            s_notes.Add($"{DateTime.Now:yyyy-MM-dd HH:mm}  {text}");
            if (s_notes.Count > 20) {
                s_notes.RemoveAt(0);
            }
        }

        WriteState();
    }

    private static JsonNode ReadState() {
        try {
            return File.Exists(StatePath) ? JsonNode.Parse(File.ReadAllText(StatePath)) : null;
        }
        catch (Exception) {
            return null;
        }
    }

    /// <summary>What Ahziael shows: whether live patches can go in, which one is in, and the live patches known.</summary>
    private static void WriteState() {
        try {
            var patches = new JsonArray();
            if (Directory.Exists(UpdatesFolder)) {
                foreach (var dir in Directory.GetDirectories(UpdatesFolder)
                             .Select(Path.GetFileName)
                             .Where(n => n.StartsWith(TAG_PREFIX, StringComparison.OrdinalIgnoreCase) && !n.EndsWith(".part"))
                             .OrderByDescending(n => int.TryParse(n[TAG_PREFIX.Length..], out var x) ? x : 0)) {
                    var info = Info(dir);
                    patches.Add(new JsonObject {
                        ["tag"] = dir,
                        ["published"] = (string) info?["published"] ?? "",
                        ["notes"] = (string) info?["notes"] ?? "",
                    });
                }
            }

            JsonArray log;
            lock (s_notes) {
                log = new JsonArray([.. s_notes.Select(n => (JsonNode) n)]);
            }

            var state = new JsonObject {
                ["module"] = LivePatcher.Target.ManifestModule.ModuleVersionId.ToString(),
                ["builtForPatches"] = LivePatcher.BuiltForPatches,
                ["whyNot"] = LivePatcher.WhyNot ?? "",
                ["on"] = On,
                ["active"] = s_active,
                ["waiting"] = s_waiting,
                ["waitingWhy"] = s_waitingWhy,
                ["lastCheck"] = s_lastCheck == default ? "" : s_lastCheck.ToString("yyyy-MM-dd HH:mm"),
                ["lastCheckResult"] = s_lastCheckResult,
                ["patches"] = patches,
                // Ahziael may hand this server a full update to put in while it runs (put-in.txt).
                ["takesUpdates"] = LivePatcher.WhyNot is null,
                ["log"] = log,
            };
            Directory.CreateDirectory(LivePatcher.Folder);
            File.WriteAllText(StatePath + ".tmp", state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(StatePath + ".tmp", StatePath, overwrite: true);
        }
        catch (Exception e) {
            Logger.Debug("Live patches: could not write the state: {0}", Logger.Args(e.Message));
        }
    }

    /// <summary>
    /// The pull request check and the Publish live patch workflow (Imlight.Director --live-patch-check &lt;repository&gt;):
    /// this program, built for live patches from the code a live patch starts from, tries to put the newer code in.
    /// Exit code 0: it can go live; 2: it needs a full update; 1: the check itself could not run.
    /// </summary>
    public static int Check(string repositoryRoot) {
        if (LivePatcher.WhyNot is { } why) {
            Console.WriteLine($"CHECK FAILED: {why}");

            return 1;
        }

        var outcome = LivePatcher.ApplyAsync(Path.GetFullPath(repositoryRoot), "check").GetAwaiter().GetResult();
        if (outcome.Done) {
            Console.WriteLine(outcome.NothingNew
                ? "CAN GO LIVE: nothing in the server's code changed."
                : $"CAN GO LIVE: {outcome.Methods} methods change in the running server.");
            foreach (var note in outcome.Notes) {
                Console.WriteLine($"NOTE: {note}");
            }

            return 0;
        }

        Console.WriteLine($"NEEDS A FULL UPDATE: {outcome.Problem}");

        return outcome.Problem.StartsWith("it can't go", StringComparison.Ordinal) || outcome.Problem.StartsWith("it needs", StringComparison.Ordinal) ? 2 : 1;
    }

}
