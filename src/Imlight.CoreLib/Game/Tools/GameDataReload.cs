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
using System.Threading;
using System.Threading.Tasks;
using Imlight.Common;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>
/// Reads the server's game data files again while it runs (Niduus, 2026-10-28: "hotload not just new builds, but updates
/// as a whole"). Ahziael copies an update's new spiraldb-extra files in while the server keeps running, then writes
/// reload-game-data.txt next to the server; the server reads SpiralDB and spiraldb-extra again and swaps them in, and
/// every <see cref="ReloadingLazy{T}"/> loads its file again the next time it is used. Nobody is disconnected.
/// </summary>
internal static class GameDataReload {

    internal const string REQUEST_FILE = "reload-game-data.txt";

    private static int s_version;
    private static int s_running;

    /// <summary>Goes up by one each time the game data was read again.</summary>
    internal static int Version => Volatile.Read(ref s_version);

    /// <summary>Called twice a second (ManagerCommands); starts a reload when Ahziael asked for one.</summary>
    internal static void Poll() {
        try {
            if (!File.Exists(REQUEST_FILE) || Volatile.Read(ref s_running) != 0) {
                return;
            }

            File.Delete(REQUEST_FILE);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Ahziael is still writing it; taken next round.
            return;
        }

        if (Interlocked.Exchange(ref s_running, 1) != 0) {
            return;
        }

        // Reading every file takes a while, so the game keeps running on the old data until the new data is ready.
        _ = Task.Run(() => {
            try {
                Logger.Information("Ahziael installed new game data; reading it again while the server keeps running.");
                if (SpiralDB.Reload()) {
                    Interlocked.Increment(ref s_version);
                    Logger.Information("The update's new game data is in use now; nobody had to log out.");
                }
            }
            catch (Exception ex) {
                Logger.Error("Could not read the new game data; the server keeps the data it had. {0}", Logger.Args(ex.Message));
            }
            finally {
                Volatile.Write(ref s_running, 0);
            }
        });
    }

    /// <summary>Deletes a request left from before the server started: a fresh start reads everything anyway.</summary>
    internal static void ForgetOldRequest() {
        try {
            File.Delete(REQUEST_FILE);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        }
    }

}

/// <summary>
/// Like <see cref="Lazy{T}"/>, but loads its file again after <see cref="GameDataReload"/> read the game data again, so a
/// file an update brought in is used without a server restart.
/// </summary>
internal sealed class ReloadingLazy<T>(Func<T> load) {

    private Lazy<T> _lazy = new(load);
    private int _version = GameDataReload.Version;

    internal T Value {
        get {
            var version = GameDataReload.Version;
            if (version != Volatile.Read(ref _version)) {
                Volatile.Write(ref _lazy, new Lazy<T>(load));
                Volatile.Write(ref _version, version);
            }

            return Volatile.Read(ref _lazy).Value;
        }
    }

}
