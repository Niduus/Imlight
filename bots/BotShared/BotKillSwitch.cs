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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Imlight.Common;
using Imlight.CoreLib.Game.BotPlayers;
using Imlight.CoreLib.Game.QuestTeam;
using Imlight.CoreLib.Game.Ultimus;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// The bots' kill switch (Niduus, 2026-11-22: "I want a killswitch file in the repo. This way if its on (= true) then the
/// bots all won't work. Even with the key", and "the killswitch goes to the bots repo").
/// The switch is killswitch.txt at the top of Niduus's private bots repo (Niduus/ahziael-bots). It says "on = true" or
/// "on = false". The bots have no way to the internet and no login to that repo: every Bot Update round signs the switch
/// into the Hive Mind file together with the brain (tools/hive-sign/sign.py) and copies that file to Server\bots, so the
/// bots read the switch from the signed file. Only Niduus's key can sign it, so nobody can change it on their own PC.
/// The bots may play only when all of this holds; otherwise none starts, and bots already playing log out:
///   - Server\bots\hive-mind.ahzhive is there and signed by Niduus, with nothing in it changed;
///   - it holds the switch, and the switch says "on = false";
///   - it was signed in the last <see cref="MAX_AGE_DAYS"/> days (an old copy of the file does not keep bots going forever).
/// It is read at start and every 5 minutes. What the bots saw is written to logs/bots/killswitch.txt.
/// After the switch goes back to "on = false" (and the next round signs it), the bots come back at the next bot update or
/// server start.
/// </summary>
internal static class BotKillSwitch {

    /// <summary>The switch's path in the signed Hive Mind file (and at the top of Niduus/ahziael-bots).</summary>
    internal const string SIGNED_NAME = "killswitch.txt";
    internal const int MAX_AGE_DAYS = 7;
    private const string STATE_FILE = "killswitch.txt";

    private static int s_started;
    private static volatile bool s_on;
    private static volatile bool s_killed;

    /// <summary>True when the bots must not play: the switch is on, or it could not be read from a fresh signed file.</summary>
    internal static bool On => s_on;

    /// <summary>Reads the switch now and says whether the bots may play.</summary>
    internal static bool AllowsPlay() {
        s_on = Read(out var why);
        if (s_on) {
            Logger.Error("{0}", Logger.Args("BOTS KILL SWITCH: no bot plays, because " + why + "."));
        }

        return !s_on;
    }

    /// <summary>Checks every 5 minutes while these bots run; when the bots must stop, every bot logs out.</summary>
    internal static void StartWatching() {
        if (Interlocked.Exchange(ref s_started, 1) == 1) {
            return;
        }

        _ = Task.Run(async () => {
            while (await BotLife.Wait(TimeSpan.FromMinutes(5))) {
                if (Read(out var why) && !s_killed) {
                    s_on = true;
                    Kill(why);
                    return;
                }
            }
        });
    }

    private static void Kill(string why) {
        s_killed = true;
        Logger.Error("{0}", Logger.Args("BOTS KILL SWITCH: every bot logs out now, because " + why + ". None starts until that changes."));
        try {
            BotLife.End();
            const string STOP = "the bots' kill switch is on";
            BotLink.StopForSwap(STOP);
            QuestTeamLink.StopForSwap(STOP);
            UltimusLink.StopForSwap(STOP);
        }
        catch (Exception e) {
            Logger.Error("Bots: the kill switch could not stop every bot: {0}", Logger.Args(e.Message));
        }
    }

    /// <summary>True when the bots must stop; why says what was found.</summary>
    private static bool Read(out string why) {
        bool stop;
        if (!BotHive.Open(BotHive.FilePath, out var what, out var signed, out var made)) {
            stop = true;
            why = "the switch could not be read: " + what;
        }
        else if (!signed.TryGetValue(SIGNED_NAME, out var data)) {
            stop = true;
            why = "the signed Hive Mind file has no kill switch in it (" + what + ")";
        }
        else if (DateTime.UtcNow - made > TimeSpan.FromDays(MAX_AGE_DAYS)) {
            stop = true;
            why = $"the signed Hive Mind file is more than {MAX_AGE_DAYS} days old ({what})";
        }
        else if (Parse(Encoding.UTF8.GetString(data))) {
            stop = true;
            why = "the switch says on = true (" + what + ")";
        }
        else {
            stop = false;
            why = "the switch says on = false (" + what + ")";
        }

        try {
            var folder = Path.GetFullPath(BotMistakes.FOLDER);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, STATE_FILE),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} bots {(stop ? "OFF" : "may play")}: {why}{Environment.NewLine}");
        }
        catch (Exception) {
            // Only the note is lost; the answer stands.
        }

        return stop;
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
