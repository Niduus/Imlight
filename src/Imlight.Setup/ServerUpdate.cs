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

namespace Imlight.Setup;

/// <summary>
/// What an update changes in the server, sorted by whether it needs a restart (Niduus, 2026-10-28: "hotload not just new
/// builds, but updates as a whole ... without disconnecting the server unless we absolutely have to").
/// </summary>
/// <param name="Program">The server's program files that changed. Windows can't replace them while it runs.</param>
/// <param name="LiveData">Game data the server reads again while it runs (quests, shops, drop tables...).</param>
/// <param name="LaterData">Game data the server reads only when it starts; copied in now, used after its next start.</param>
internal sealed record ServerUpdateSort(List<string> Program, List<string> LiveData, List<string> LaterData) {

    internal bool NeedsRestart => Program.Count > 0;

    /// <summary>How the update goes in, in a few words for Ahziael.</summary>
    internal string Describe() => NeedsRestart
        ? $"it changes the server's program ({Program.Count} {(Program.Count == 1 ? "file" : "files")}), so the server has to restart once"
        : LiveData.Count + LaterData.Count == 0
            ? "the server is the same, so it keeps running and nobody is disconnected"
            : "it only changes game data, so it goes in while the server keeps running and nobody is disconnected";

}

internal static class ServerUpdate {

    /// <summary>Written next to the server after new game data went in; the server reads its data again (GameDataReload).</summary>
    internal const string RELOAD_REQUEST_FILE = "reload-game-data.txt";

    /// <summary>
    /// Written in the Pending folder when an update's server waits for a restart, so the next server start installs it
    /// first (ServerManager, InstalledCommands.StartAsync).
    /// </summary>
    internal const string WAITING_FILE = "server-waits-for-restart.txt";

    // Files the build writes that say which version it is: never a reason to restart.
    private static readonly string[] s_stamps = ["ahziael-version.txt", ServerSource.STAMP_FILE];

    // The spiraldb-extra folders and files the server reads again while it runs: SpiralDB's own folders
    // (SpiralDB.Reload), the files its load applies (quest goal events, quest spells), the ones it watches (ZoneObjects,
    // WoodenChests, DropTables, DropOdds) and the ones it loads again after a reload (ReloadingLazy).
    private static readonly string[] s_liveData = [
        "BadgeDefinitions/", "BossCheats/", "CreatureSpellbook/", "DropTables/", "DungeonTeleports/", "Gardening/",
        "GlobalRegistry/", "NpcDropTable/", "NpcInventory/", "NpcSeamstress/", "NpcSpellInventory/", "Packs/",
        "QuestTemplates/", "TradeTrainers/", "TreasureCardInventory/", "VanillaTrainers/", "WoodenChests/", "ZoneObjects/",
        "ZoneTransfer/", "Shops/live-shop-lists.json", "DropOdds.json", "QuestGoalEvents.json", "QuestSpellRewards.json",
    ];

    /// <summary>Sorts what installing the server in <paramref name="build"/> over <paramref name="server"/> changes.</summary>
    internal static ServerUpdateSort Sort(string build, string server) {
        var sort = new ServerUpdateSort([], [], []);
        foreach (var file in Directory.EnumerateFiles(build, "*", SearchOption.AllDirectories)) {
            var relative = Path.GetRelativePath(build, file).Replace('\\', '/');
            if (s_stamps.Contains(relative, StringComparer.OrdinalIgnoreCase)
                || relative.Equals("Config/Imlight.ini", StringComparison.OrdinalIgnoreCase)
                || SameFile(file, Path.Combine(server, relative))) {
                // Imlight.ini only gets new settings added, and the server reads it again by itself.
                continue;
            }

            if (!relative.StartsWith("spiraldb-extra/", StringComparison.OrdinalIgnoreCase)) {
                sort.Program.Add(relative);
            }
            else if (s_liveData.Any(live => relative["spiraldb-extra/".Length..].StartsWith(live, StringComparison.OrdinalIgnoreCase))) {
                sort.LiveData.Add(relative);
            }
            else {
                sort.LaterData.Add(relative);
            }
        }

        return sort;
    }

    /// <summary>Whether <paramref name="target"/> exists and holds exactly what <paramref name="source"/> does.</summary>
    internal static bool SameFile(string source, string target) {
        try {
            var info = new FileInfo(target);
            if (!info.Exists || info.Length != new FileInfo(source).Length) {
                return false;
            }

            using var a = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var b = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bufferA = new byte[81920];
            var bufferB = new byte[81920];
            while (true) {
                var read = a.ReadAtLeast(bufferA, bufferA.Length, throwOnEndOfStream: false);
                if (b.ReadAtLeast(bufferB, read, throwOnEndOfStream: false) != read
                    || !bufferA.AsSpan(0, read).SequenceEqual(bufferB.AsSpan(0, read))) {
                    return false;
                }

                if (read < bufferA.Length) {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    /// <summary>Asks the running server to read its game data again.</summary>
    internal static void RequestReload(string serverFolder) {
        try {
            File.WriteAllText(Path.Combine(serverFolder, RELOAD_REQUEST_FILE), DateTime.Now.ToString("O"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            SetupLog.Detail($"Could not ask the server to read its game data again: {ex.Message}");
        }
    }

}
