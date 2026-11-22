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
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Imlight.Common;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// The bots' ear for the Ahziael signal (Niduus, 2026-11-22: "Bots can only run while receiving a Ahziael signal from the
/// LOCAL HOST"). Ahziael (src/Imlight.Setup/AhziaelSignal.cs) starts the server with a random token in
/// IMLIGHT_AHZIAEL_TOKEN and answers on 127.0.0.1 ([Bots] AhziaelSignalPort, default 12395): the bots send a random challenge,
/// and only the Ahziael that started this server can answer it right (HMAC-SHA256 of the challenge with the token).
/// Phase 1 (now): the bots only listen and say what they hear (server log and logs/bots/ahziael-signal.txt) every 5 minutes;
/// nothing about how they play changes. Phase 2 (a later bot update, Niduus's go): no signal means no bots.
/// </summary>
internal static class BotAhziaelSignal {

    private const string TOKEN_VARIABLE = "IMLIGHT_AHZIAEL_TOKEN";
    private const int DEFAULT_PORT = 12395;

    private static int s_started;
    private static volatile bool s_heard;
    private static string s_last = "";

    /// <summary>True when Ahziael answered the last check right.</summary>
    internal static bool Heard => s_heard;

    /// <summary>Starts listening (once): a check now and every 5 minutes while the server runs.</summary>
    internal static void StartListening() {
        if (Interlocked.Exchange(ref s_started, 1) == 1) {
            return;
        }

        _ = Task.Run(async () => {
            // Ends with these bots (a bot update or the server stopping), so an old copy never keeps asking.
            while (!BotLife.Over) {
                Check();
                if (!await BotLife.Wait(TimeSpan.FromMinutes(5))) {
                    return;
                }
            }
        });
    }

    /// <summary>Asks Ahziael once and records the answer.</summary>
    internal static bool Check() {
        var heard = Ask(out var why);
        s_heard = heard;
        var line = heard ? "Ahziael signal heard: the bots may play." : "Ahziael signal NOT heard: " + why + ".";
        if (line != s_last) {
            s_last = line;
            try {
                Logger.Information("{0}", Logger.Args(line));
            }
            catch (Exception) {
                // The file below still says it.
            }

            try {
                var folder = Path.GetFullPath(BotMistakes.FOLDER);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "ahziael-signal.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch (Exception) {
                // Nothing more to do.
            }
        }

        return heard;
    }

    private static bool Ask(out string why) {
        var token = Environment.GetEnvironmentVariable(TOKEN_VARIABLE);
        if (string.IsNullOrEmpty(token)) {
            why = "the server was not started by Ahziael (or by an Ahziael older than the signal)";
            return false;
        }

        var port = int.TryParse(ConfigurationManager.GetSetting("Bots.AhziaelSignalPort"), out var p) && p > 0 ? p : DEFAULT_PORT;
        try {
            using var line = new TcpClient { NoDelay = true, ReceiveTimeout = 2000, SendTimeout = 2000 };
            if (!line.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromSeconds(2))) {
                why = $"Ahziael does not answer on this PC (port {port})";
                return false;
            }

            var challenge = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var stream = line.GetStream();
            var ask = Encoding.ASCII.GetBytes($"AHZ? {challenge}\n");
            stream.Write(ask, 0, ask.Length);
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 256, leaveOpen: true);
            var answer = reader.ReadLine() ?? "";
            var right = "AHZIAEL " + Convert.ToHexString(HMACSHA256.HashData(Convert.FromHexString(token), Encoding.ASCII.GetBytes(challenge)));
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(answer.Trim()), Encoding.ASCII.GetBytes(right))) {
                why = "something answered on Ahziael's port, but not the Ahziael that started this server";
                return false;
            }

            why = "";
            return true;
        }
        catch (Exception ex) {
            why = "Ahziael could not be asked (" + ex.Message + ")";
            return false;
        }
    }

}
