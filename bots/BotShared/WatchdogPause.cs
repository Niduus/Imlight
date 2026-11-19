/*
 * Imlight
 * Copyright (C) 2026 Revive101
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
using System;
using System.IO;
using Imlight.Common;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// The watchdog's pause (no step done for hours: paused.txt starting "Watchdog:") clears itself when a new update is
/// installed, as the update may be the fix (Niduus, 2026-11-15, yes to "when an update installs, his safety pause could
/// clear itself"; Ultimus stood paused from 9:26 AM through the 1:16 PM update that fixed what stopped him). The pause
/// says which build wrote it; a different build running now resumes the bot. Pausing by hand in Ahziael is never cleared.
/// </summary>
internal static class WatchdogPause {

    private const string MARK = "Watchdog:";
    private const string BUILD = "Build: ";
    private static string s_stamp;

    /// <summary>This server build: its version and when its file was written.</summary>
    internal static string Stamp() {
        if (s_stamp is not null) {
            return s_stamp;
        }

        try {
            var dll = typeof(WatchdogPause).Assembly.Location;
            s_stamp = $"{typeof(WatchdogPause).Assembly.GetName().Version} {File.GetLastWriteTimeUtc(dll):o}";
        }
        catch (Exception) {
            s_stamp = "unknown";
        }

        return s_stamp;
    }

    /// <summary>What paused.txt says for a watchdog stop: its first line, then the build that stopped it.</summary>
    internal static string Text(string firstLine) => MARK + " " + firstLine.Trim() + Environment.NewLine + BUILD + Stamp();

    /// <summary>Whether a pause file's text is a watchdog stop written by another build than this one (pure, for the replay pack).</summary>
    internal static bool FromOtherBuild(string pauseText, string stamp)
        => pauseText.StartsWith(MARK, StringComparison.Ordinal) && !pauseText.Contains(BUILD + stamp, StringComparison.Ordinal);

    /// <summary>
    /// Whether the pause file there is the watchdog's own stop, not Ahziael's Pause button: the status then says so, so a
    /// bot the watchdog stopped is never shown as paused by hand (Niduus, 2026-11-19: six of the Calling of 7 showed
    /// "paused (Ahziael's Pause button)" while he had not touched them; the watchdog had stopped them).
    /// </summary>
    internal static bool ByWatchdog(string folder, string pauseFile) {
        try {
            var pause = Path.Combine(folder, pauseFile);
            return File.Exists(pause) && File.ReadAllText(pause).StartsWith(MARK, StringComparison.Ordinal);
        }
        catch (Exception) {
            return false;
        }
    }

    /// <summary>
    /// A watchdog pause from before this update is lifted: paused.txt goes, watchdog-stop.txt is kept as
    /// watchdog-stop-old.txt for the record. True when it was lifted.
    /// </summary>
    internal static bool ClearIfUpdated(string folder, string pauseFile, string stopFile) {
        try {
            var pause = Path.Combine(folder, pauseFile);
            if (!File.Exists(pause) || !FromOtherBuild(File.ReadAllText(pause), Stamp())) {
                return false;
            }

            File.Delete(pause);
            var stop = Path.Combine(folder, stopFile);
            if (File.Exists(stop)) {
                File.Copy(stop, Path.Combine(folder, Path.GetFileNameWithoutExtension(stopFile) + "-old.txt"), overwrite: true);
                File.Delete(stop);
            }

            return true;
        }
        catch (Exception e) {
            Logger.Warning("Bots: could not lift the watchdog pause after the update: {0}", Logger.Args(e.Message));
            return false;
        }
    }
}
