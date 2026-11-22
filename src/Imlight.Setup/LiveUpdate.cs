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
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Imlight.Setup;

/// <summary>
/// A full update that goes into the running server (Niduus, 2026-11-21: "why do I keep having to update and restart while
/// Live updates is on? What's the point of Live Updates if having it on still requires the Update and restart method").
/// With the Live patches switch on and a server built for it, an update whose program change is only the server's own
/// code (Imlight.CoreLib) and the bots is handed to the running server, which puts the new code in like a live patch
/// (Game/LivePatches). Nobody is disconnected; the update's files are swapped in the next time the server starts on its
/// own. When the server can't take it (a deleted field, a change to another server part), Ahziael restarts it as before.
/// </summary>
internal static class LiveUpdate {

    // In the Pending folder: which update was handed to the server, and how it went.
    private const string MARK_FILE = "server-update-live.json";
    private const string PREFIX = "ahziael-";

    /// <summary>How long Ahziael waits for the server's answer before it restarts the server as before.</summary>
    internal static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(6);

    private static string LiveFolder(string serverFolder) => Path.Combine(serverFolder, "live-patches");

    private static string MarkPath(string installFolder) => Path.Combine(installFolder, "Pending", MARK_FILE);

    /// <summary>Why this update can't go into the running server, or null when it can be tried.</summary>
    internal static string? WhyNot(string installFolder, string pendingServer, string serverFolder, ServerUpdateSort sort) {
        if (!File.Exists(Path.Combine(installFolder, ServerManager.LIVE_PATCHES_ON_FILE))) {
            return "the Live patches switch is off";
        }

        if (InstalledCommands.LivePatchBuild(installFolder) != "patchable") {
            return "the running server isn't built for live patches yet";
        }

        if (BuildKind(pendingServer) != "patchable") {
            return "the update wasn't built for live patches";
        }

        var state = ReadJson(Path.Combine(LiveFolder(serverFolder), "state.json"));
        if ((bool?) state?["takesUpdates"] != true) {
            return (string?) state?["whyNot"] is { Length: > 0 } why
                ? $"the running server can't take live patches ({why})"
                : "the running server is too old to take an update while it runs";
        }

        // Imlight.Director.dll is built again whenever the server part it points at changes; with the same code of its own
        // it is the same program.
        var sameDirector = SameText(Path.Combine(pendingServer, "live-patches", DIRECTOR_CODE), Path.Combine(serverFolder, "live-patches", DIRECTOR_CODE));
        var other = sort.Program.Where(file => !GoesInLive(file) && !(sameDirector && IsDirector(file))).ToList();
        if (other.Count > 0) {
            return "it changes server parts other than the server's own code (" + string.Join(", ", other.Take(5)) + ")";
        }

        return null;
    }

    // The server's own code (put in live), the bots (bot updates swap them by themselves) and the live patch baseline.
    private static bool GoesInLive(string file)
        => file.Equals("Imlight.CoreLib.dll", StringComparison.OrdinalIgnoreCase)
           || file.Equals("Imlight.CoreLib.pdb", StringComparison.OrdinalIgnoreCase)
           || file.StartsWith("live-patches/", StringComparison.OrdinalIgnoreCase)
           || file.StartsWith("bots/", StringComparison.OrdinalIgnoreCase);

    private const string DIRECTOR_CODE = "director-code.txt";

    private static bool IsDirector(string file)
        => file.Equals("Imlight.Director.dll", StringComparison.OrdinalIgnoreCase)
           || file.Equals("Imlight.Director.pdb", StringComparison.OrdinalIgnoreCase);

    private static bool SameText(string a, string b) {
        try {
            return File.Exists(a) && File.Exists(b) && File.ReadAllText(a).ReplaceLineEndings("\n").Trim() is { Length: > 0 } text
                   && text == File.ReadAllText(b).ReplaceLineEndings("\n").Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    private static string BuildKind(string serverFolder) {
        try {
            var path = Path.Combine(serverFolder, "live-patches", "build.txt");

            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return "";
        }
    }

    /// <summary>
    /// Hands the server code of the update (from the source it was built from) to the running server and asks it to put
    /// it in. <paramref name="dataReload"/>: the update also brought game data the server should read again once it's in.
    /// </summary>
    internal static void Ask(string installFolder, string sourceFolder, string serverFolder, string version, bool dataReload) {
        var label = HandOver(sourceFolder, serverFolder, version);
        File.WriteAllText(MarkPath(installFolder), new JsonObject {
            ["label"] = label,
            ["version"] = WaitingVersion(installFolder),
            ["asked"] = DateTime.Now.ToString("o"),
            ["reload"] = dataReload,
            ["state"] = "asked",
            ["why"] = "",
        }.ToJsonString());
        File.WriteAllText(Path.Combine(LiveFolder(serverFolder), "put-in.txt"), label);
    }

    /// <summary>Copies the update's server code where the running server reads it; returns its name ("ahziael-0.10.962").</summary>
    private static string HandOver(string sourceFolder, string serverFolder, string version) {
        var label = PREFIX + version;
        var updates = Path.Combine(LiveFolder(serverFolder), "updates");
        var folder = Path.Combine(updates, label);
        if (Directory.Exists(folder)) {
            Directory.Delete(folder, recursive: true);
        }

        // The same files a published live patch holds: the server's code and the spell file it shares with Ahziael.
        var source = Path.Combine(folder, "source");
        var project = Path.Combine(sourceFolder, "src", "Imlight.CoreLib");
        foreach (var file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)) {
            var relative = Path.GetRelativePath(sourceFolder, file);
            var first = Path.GetRelativePath(project, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (first is "bin" or "obj") {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(source, relative))!);
            File.Copy(file, Path.Combine(source, relative), overwrite: true);
        }

        var spells = Path.Combine("src", "Imlight.Setup", "SpellSupport.cs");
        if (File.Exists(Path.Combine(sourceFolder, spells))) {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(source, spells))!);
            File.Copy(Path.Combine(sourceFolder, spells), Path.Combine(source, spells), overwrite: true);
        }

        File.WriteAllText(Path.Combine(folder, "update.json"), new JsonObject {
            ["tag"] = label,
            ["published"] = DateTimeOffset.Now.ToString("o"),
            ["notes"] = $"- Ahziael {version}, put in while the server ran.",
        }.ToJsonString());

        // Only the newest few stay on the PC.
        foreach (var old in Directory.GetDirectories(updates, PREFIX + "*")
                     .Select(d => new DirectoryInfo(d)).OrderByDescending(d => d.CreationTimeUtc).Skip(3)) {
            try {
                old.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // In use: next time.
            }
        }

        // A result from an earlier try of the same update is not this one's answer.
        File.Delete(Path.Combine(folder, "result.json"));

        return label;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Before the build (Niduus, 2026-11-21: "what the fuck was the point in streamlining updates if my envision for them
    // still required me to do the things I wanted to dodge. UPDATE, BUILD AND RESTART"): an update whose only program
    // change is the server's own code goes into the running server as soon as its code is downloaded, before anything is
    // built. The build still runs after, in the background, for the server's files at its next start; nobody waits for it.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The source of the build that runs now, as SourceParts.Hash wrote it (the program parts other than the server's own code).</summary>
    internal const string SOURCE_PARTS = "source-parts.txt";

    /// <summary>Why the update downloaded to <paramref name="sourceFolder"/> can't go into the running server before it is built, or null.</summary>
    internal static string? EarlyWhyNot(string installFolder, string sourceFolder, string serverFolder) {
        if (!File.Exists(Path.Combine(installFolder, ServerManager.LIVE_PATCHES_ON_FILE))) {
            return "the Live patches switch is off";
        }

        if (!InstalledCommands.IsServerRunning(installFolder)) {
            return "the server isn't running";
        }

        if (InstalledCommands.LivePatchBuild(installFolder) != "patchable") {
            return "the running server isn't built for live patches yet";
        }

        var state = ReadJson(Path.Combine(LiveFolder(serverFolder), "state.json"));
        if ((bool?) state?["takesUpdates"] != true) {
            return "the running server can't take an update while it runs";
        }

        // The other program parts the running server was built from: its own files, or those of an update that already went
        // into it live (their other parts are the same, or it wouldn't have).
        var pending = Path.Combine(installFolder, "Pending", "Server");
        var running = WentIn(installFolder, serverFolder) && File.Exists(Path.Combine(pending, "live-patches", SOURCE_PARTS))
            ? Path.Combine(pending, "live-patches", SOURCE_PARTS)
            : Path.Combine(LiveFolder(serverFolder), SOURCE_PARTS);
        if (!File.Exists(running)) {
            return "the running server was built before Ahziael noted its parts (this one update builds first)";
        }

        var now = SourceParts.Hash(sourceFolder);
        var then = File.ReadAllText(running).Trim();
        return now == then ? null : "it changes server parts other than the server's own code";
    }

    /// <summary>
    /// Hands the update's server code to the running server and waits for its answer. Null when it is in; else why not.
    /// <paramref name="say"/> gets a line for the person while it waits.
    /// </summary>
    internal static async Task<string?> PutInNowAsync(string sourceFolder, string serverFolder, string version, Action<string> say) {
        var label = HandOver(sourceFolder, serverFolder, version);
        File.WriteAllText(Path.Combine(LiveFolder(serverFolder), "put-in.txt"), label);
        say("The update's fixes are going into the running server now; nobody is disconnected.");
        var resultPath = Path.Combine(LiveFolder(serverFolder), "updates", label, "result.json");
        var started = DateTime.Now;
        while (DateTime.Now - started < LongestWait) {
            await Task.Delay(TimeSpan.FromSeconds(2));
            if (ReadJson(resultPath) is not { } result) {
                continue;
            }

            return (bool?) result["done"] == true ? null : (string?) result["problem"] is { Length: > 0 } why ? why : "the server couldn't take it";
        }

        return "the server didn't answer in time";
    }

    internal enum Stage { None, Asked, In, Refused }

    /// <summary>Where the update handed to the server is: still asked, in, or refused (with why).</summary>
    internal static (Stage Stage, string Why, bool Reload, DateTime Asked) Look(string installFolder, string serverFolder) {
        var mark = ReadJson(MarkPath(installFolder));
        if (mark is null || (string?) mark["version"] != WaitingVersion(installFolder) || !SetupSteps.ServerWaits(installFolder)) {
            return (Stage.None, "", false, default);
        }

        var asked = DateTime.TryParse((string?) mark["asked"], out var when) ? when : DateTime.Now;
        var reload = (bool?) mark["reload"] == true;
        switch ((string?) mark["state"]) {
            case "in":
                return (Stage.In, "", reload, asked);
            case "refused":
                return (Stage.Refused, (string?) mark["why"] ?? "", reload, asked);
        }

        var result = ReadJson(Path.Combine(LiveFolder(serverFolder), "updates", (string?) mark["label"] ?? "", "result.json"));
        if (result is null) {
            return DateTime.Now - asked > LongestWait
                ? Settle(installFolder, mark, false, "the server didn't answer in time")
                : (Stage.Asked, "", reload, asked);
        }

        return (bool?) result["done"] == true
            ? Settle(installFolder, mark, true, "")
            : Settle(installFolder, mark, false, (string?) result["problem"] ?? "the server couldn't take it");
    }

    private static (Stage, string, bool, DateTime) Settle(string installFolder, JsonNode mark, bool done, string why) {
        mark["state"] = done ? "in" : "refused";
        mark["why"] = why;
        try {
            File.WriteAllText(MarkPath(installFolder), mark.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Read again next time.
        }

        return (done ? Stage.In : Stage.Refused, why, (bool?) mark["reload"] == true,
            DateTime.TryParse((string?) mark["asked"], out var when) ? when : DateTime.Now);
    }

    /// <summary>Whether the waiting server update is already in the running server, so it needs no restart.</summary>
    internal static bool WentIn(string installFolder, string serverFolder) => Look(installFolder, serverFolder).Stage == Stage.In;

    /// <summary>Whether Ahziael is still waiting for the server's answer.</summary>
    internal static bool Waiting(string installFolder, string serverFolder) => Look(installFolder, serverFolder).Stage == Stage.Asked;

    private static string WaitingVersion(string installFolder) {
        try {
            var path = Path.Combine(installFolder, "Pending", ServerUpdate.WAITING_FILE);

            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return "";
        }
    }

    private static JsonNode? ReadJson(string path) {
        try {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception) {
            return null;
        }
    }

}
