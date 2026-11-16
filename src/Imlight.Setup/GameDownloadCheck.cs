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
using System.Text.RegularExpressions;

namespace Imlight.Setup;

/// <summary>
/// Finds why Wizard101 may draw a wizard as a floating head and hands, or leave creatures (Rattlebones) unseen.
/// </summary>
/// <remarks>
/// Wizard101 fetches clothing and creature art itself while it plays, straight from KingsIsle's patch servers
/// (Bin\PatchConfig.xml names patch.us.wizard101.com), whichever server it logs in to. Its log shows it:
/// "ClientPatcher::RequestPackage Equipment-WorldData", "Patching: Textures/Characters/... (Equipment-HighDetail)",
/// "Patched Equipment-HighDetail". The head and hands are in Root.wad, but the robe and boots (the whole body) are in
/// Equipment-WorldData / Equipment-HighDetail and most creatures in Mob-WorldData. When those can't be fetched (a hosts
/// file or firewall blocking KingsIsle, the game's own download never finished), the game draws only the head and
/// hands. The Imlight server never sends these files, so no server or game-files setting changes it (a player's
/// report, 2026-11-16: same on this fork, on Revive101's Imlight, and with game files from the PC or from Aurorium).
/// </remarks>
internal static partial class GameDownloadCheck {

    /// <summary>KingsIsle hosts the game downloads its files from.</summary>
    internal static readonly string[] DOWNLOAD_HOSTS = [
        "patch.us.wizard101.com", "patch.eu.wizard101.com",
        "versionak.us.wizard101.com", "versionak.eu.wizard101.com",
        "version.us.wizard101.com", "version.eu.wizard101.com",
    ];

    /// <summary>Game files that hold the wizard's body (clothing) and the creatures, with what each one shows.</summary>
    internal static readonly (string Wad, string Holds)[] BODY_FILES = [
        ("Equipment-WorldData.wad", "clothing (a wizard's body)"),
        ("Mob-WorldData.wad", "creatures and characters"),
        ("Mob2-WorldData.wad", "creatures and characters"),
        ("_Shared-WorldData.wad", "shared art"),
    ];

    private const string MESSAGE_FIX = "Open Wizard101 from its own KingsIsle launcher once and let it finish downloading everything, " +
        "then play here again.";

    /// <summary>
    /// Plain-words problems found: the hosts file blocking KingsIsle's downloads, body and creature files missing,
    /// and (with <paramref name="readLog"/>) downloads the game's own log says did not finish. Empty when all is well.
    /// </summary>
    internal static List<string> Check(string installFolder, bool readLog) {
        var problems = new List<string>();
        var info = InstallInfo.Load(installFolder);

        if (BlockedHosts(ReadHostsFile()) is { Count: > 0 } blocked) {
            problems.Add($"This PC's hosts file sends {string.Join(", ", blocked)} somewhere else. Wizard101 downloads clothing and " +
                "creatures from there while you play, so wizards show as a floating head and hands and some characters stay unseen. " +
                $"Remove those lines from {HostsPath()} (open Notepad as administrator to change it), then restart Wizard101.");
        }

        if (info.GameDataFolder is { } gameData && Directory.Exists(gameData)) {
            var missing = BODY_FILES.Where(f => !File.Exists(Path.Combine(gameData, f.Wad))).ToList();
            if (missing.Count > 0) {
                problems.Add($"Wizard101 has not downloaded {string.Join(", ", missing.Select(f => $"{f.Wad} ({f.Holds})"))} " +
                    $"into {gameData} yet. Until it does, wizards show as a floating head and hands and some characters stay unseen. " +
                    MESSAGE_FIX);
            }
        }

        if (readLog && GameClient.ReadClientLog(installFolder).File is { } log) {
            try {
                problems.AddRange(ReadLogProblems(File.ReadLines(log)));
            }
            catch (IOException) {
                // The game is writing it; the next check reads it.
            }
            catch (UnauthorizedAccessException) {
            }
        }

        return problems;
    }

    /// <summary>A short warning for the message bar when Play starts the game, or null when nothing looks wrong.</summary>
    internal static string? PlayWarning(string installFolder) {
        try {
            var problems = Check(installFolder, false);

            return problems.Count == 0 ? null : problems[0];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) {
            return null;
        }
    }

    private static string HostsPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    private static IEnumerable<string> ReadHostsFile() {
        try {
            var path = HostsPath();

            return File.Exists(path) ? File.ReadAllLines(path) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) {
            return [];
        }
    }

    /// <summary>The KingsIsle download hosts a hosts file sends anywhere (any line naming one, comments aside).</summary>
    internal static List<string> BlockedHosts(IEnumerable<string> hostsLines) {
        var blocked = new List<string>();
        foreach (var raw in hostsLines) {
            var line = raw.Split('#')[0];
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) {
                continue;
            }

            foreach (var name in parts.Skip(1)) {
                var host = DOWNLOAD_HOSTS.FirstOrDefault(h => h.Equals(name.TrimEnd('.'), StringComparison.OrdinalIgnoreCase));
                if (host is not null && !blocked.Contains(host)) {
                    blocked.Add(host);
                }
            }
        }

        return blocked;
    }

    [GeneratedRegex(@"Patching: .+ \((?<pkg>[^()]+)\), priority")]
    private static partial Regex PatchingLine();

    [GeneratedRegex(@"\bPatched (?<pkg>[^\s()]+)")]
    private static partial Regex PatchedLine();

    [GeneratedRegex(@"m_lastFailCode = (?<code>-?\d+)")]
    private static partial Regex FailCodeLine();

    [GeneratedRegex(@"Pacakge name: (?<pkg>\S+)|Package name: (?<pkg>\S+)")]
    private static partial Regex PackageNameLine();

    /// <summary>
    /// What the newest run in a Wizard101 log says about its downloads: never reaching KingsIsle's patch server, a download
    /// that failed, or art it began fetching (clothing, creatures) that never arrived.
    /// </summary>
    internal static List<string> ReadLogProblems(IEnumerable<string> lines) {
        // Only the newest run counts: each one starts by loading PatchConfig.xml.
        var run = new List<string>();
        foreach (var line in lines) {
            if (line.Contains("Loaded PatchConfig.xml", StringComparison.Ordinal)) {
                run.Clear();
            }

            if (line.Contains("PATCH", StringComparison.Ordinal) || line.Contains("Patch", StringComparison.Ordinal)
                || line.Contains("Pacakge name", StringComparison.Ordinal) || line.Contains("Package name", StringComparison.Ordinal)) {
                run.Add(line);
            }
        }

        var problems = new List<string>();
        if (run.Count == 0) {
            return problems;
        }

        var tried = run.Any(l => l.Contains("Connecting to Patch Server", StringComparison.Ordinal));
        var reached = run.Any(l => l.Contains("Connected to Patch Server", StringComparison.Ordinal));
        if (tried && !reached) {
            problems.Add("Wizard101 could not reach KingsIsle's patch server when it started, so it can't download clothing and " +
                "creatures while you play (wizards show as a floating head and hands, some characters stay unseen). Check that the " +
                "internet works, and that no firewall, VPN or hosts file line blocks patch.us.wizard101.com or versionak.us.wizard101.com.");
        }

        // Packages a download failed for: the package named in the summary after a non-zero fail code.
        var failed = new List<string>();
        string? lastFail = null;
        foreach (var line in run) {
            if (FailCodeLine().Match(line) is { Success: true } fail) {
                lastFail = fail.Groups["code"].Value == "0" ? null : fail.Groups["code"].Value;
            }
            else if (lastFail is not null && PackageNameLine().Match(line) is { Success: true } pkg) {
                AddOnce(failed, pkg.Groups["pkg"].Value);
                lastFail = null;
            }
        }

        // Art the game began fetching that never came: a "Patching: ... (Package)" with no "Patched Package" after it.
        var waiting = new List<string>();
        foreach (var line in run) {
            if (PatchingLine().Match(line) is { Success: true } start) {
                AddOnce(waiting, start.Groups["pkg"].Value);
            }
            else if (line.Contains("[STAT] CLIENT", StringComparison.Ordinal) && PatchedLine().Match(line) is { Success: true } done) {
                waiting.RemoveAll(p => p.Equals(done.Groups["pkg"].Value, StringComparison.OrdinalIgnoreCase));
            }
        }

        var stuck = failed.Concat(waiting).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (stuck.Count > 0) {
            problems.Add($"Wizard101 has not finished downloading {string.Join(", ", stuck.Select(Describe))} from KingsIsle. " +
                "Until it does, those show as missing (a floating head and hands, unseen characters). If the game is still open it may " +
                "still be downloading; otherwise " + char.ToLowerInvariant(MESSAGE_FIX[0]) + MESSAGE_FIX[1..] +
                " If it still happens, check that no firewall, VPN or hosts file line blocks wizard101.com.");
        }

        return problems;
    }

    private static void AddOnce(List<string> list, string item) {
        if (!list.Contains(item, StringComparer.OrdinalIgnoreCase)) {
            list.Add(item);
        }
    }

    private static string Describe(string package) {
        if (package.StartsWith("Equipment", StringComparison.OrdinalIgnoreCase)) {
            return $"{package} (clothing)";
        }

        if (package.StartsWith("Mob", StringComparison.OrdinalIgnoreCase)) {
            return $"{package} (creatures and characters)";
        }

        return package;
    }

}
