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
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Imlight.Common;
using Imlight.CoreLib.Game.BotPlayers;
using Imlight.CoreLib.Game.QuestTeam;
using Imlight.CoreLib.Game.Ultimus;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// The bots' kill switch (Niduus, 2026-11-22: "I want a killswitch file in the repo. This way if its on (= true) then the
/// bots all won't work. Even with the key"). The file bots-killswitch.txt at the top of Niduus/Imlight (branch
/// quality-assurance) says "on = true" or "on = false". The bots read it at start and every 5 minutes. While it says true,
/// no bot starts, and running bots log out at once, whatever the signed Hive Mind file or Ahziael's signal say. The last
/// answer is kept in logs/bots/killswitch.txt, so a server that cannot reach GitHub keeps the last known state.
/// Turning it back to false lets the bots play again at the next bot update or server start.
/// This is the only place in the bots allowed a web client (tools/bots-home-check/check.ps1), and only for this one address.
/// </summary>
internal static class BotKillSwitch {

    internal const string URL = "https://raw.githubusercontent.com/Niduus/Imlight/quality-assurance/bots-killswitch.txt";
    private const string STATE_FILE = "killswitch.txt";

    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static int s_started;
    private static volatile bool s_on;
    private static volatile bool s_killed;

    /// <summary>True when the kill switch is on (or was on the last time it could be read).</summary>
    internal static bool On => s_on;

    /// <summary>Reads the switch now (falls back to the last known state) and says whether the bots may play.</summary>
    internal static bool AllowsPlay() {
        s_on = Read();
        if (s_on) {
            Logger.Error("{0}", Logger.Args("BOTS KILL SWITCH IS ON (bots-killswitch.txt in Niduus/Imlight): no bot plays."));
        }

        return !s_on;
    }

    /// <summary>Checks every 5 minutes while these bots run; when it turns on, every bot logs out.</summary>
    internal static void StartWatching() {
        if (Interlocked.Exchange(ref s_started, 1) == 1) {
            return;
        }

        _ = Task.Run(async () => {
            while (await BotLife.Wait(TimeSpan.FromMinutes(5))) {
                if (Read() && !s_killed) {
                    s_on = true;
                    Kill();
                    return;
                }
            }
        });
    }

    private static void Kill() {
        s_killed = true;
        const string WHY = "the bots' kill switch is on";
        Logger.Error("{0}", Logger.Args("BOTS KILL SWITCH turned ON: every bot logs out now and none starts until it is off again."));
        try {
            BotLife.End();
            BotLink.StopForSwap(WHY);
            QuestTeamLink.StopForSwap(WHY);
            UltimusLink.StopForSwap(WHY);
        }
        catch (Exception e) {
            Logger.Error("Bots: the kill switch could not stop every bot: {0}", Logger.Args(e.Message));
        }
    }

    // The switch from GitHub; when GitHub can't be reached, the last state this server saw (off when it never saw one).
    private static bool Read() {
        var path = Path.Combine(Path.GetFullPath(BotMistakes.FOLDER), STATE_FILE);
        try {
            var text = s_http.GetStringAsync(URL).GetAwaiter().GetResult();
            var on = Parse(text);
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} on = {(on ? "true" : "false")}{Environment.NewLine}");
            }
            catch (Exception) {
                // Only the memory of it is lost.
            }

            return on;
        }
        catch (Exception) {
            try {
                return File.Exists(path) && Parse(File.ReadAllText(path));
            }
            catch (Exception) {
                return false;
            }
        }
    }

    /// <summary>"on = true" (any case, spaces allowed) on any line that is not a # comment.</summary>
    internal static bool Parse(string text) {
        foreach (var raw in (text ?? "").Split('\n')) {
            var line = raw.Trim();
            if (line.StartsWith('#') || !line.Contains('=')) {
                continue;
            }

            var parts = line.Split('=', 2);
            if (parts[0].Trim().EndsWith("on", StringComparison.OrdinalIgnoreCase)) {
                return parts[1].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

}
