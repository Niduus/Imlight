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
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Imlight.Setup;

/// <summary>
/// The Ahziael signal (Niduus, 2026-11-22: "Bots can only run while receiving a Ahziael signal from the LOCAL HOST").
/// While Ahziael runs the server (Imlight.exe --start, which stays open as long as the server does), it answers on this
/// PC only (127.0.0.1, [Bots] AhziaelSignalPort, default 12395). The server is started with a fresh random token in
/// IMLIGHT_AHZIAEL_TOKEN; the bots send a random challenge and Ahziael answers with HMAC-SHA256(token, challenge), so only
/// the Ahziael that started this server can answer for it. The bots' side is bots/BotShared/BotAhziaelSignal.cs.
/// </summary>
internal sealed class AhziaelSignal : IDisposable {

    internal const string TOKEN_VARIABLE = "IMLIGHT_AHZIAEL_TOKEN";
    internal const int DEFAULT_PORT = 12395;

    private readonly TcpListener _listener;
    private readonly byte[] _token;
    private readonly CancellationTokenSource _stop = new();

    /// <summary>The token handed to the server this run (hex).</summary>
    internal string Token { get; }

    private AhziaelSignal(int port) {
        _token = RandomNumberGenerator.GetBytes(32);
        Token = Convert.ToHexString(_token);
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    /// <summary>Starts answering, or returns null when the port is taken (the bots then hear no signal).</summary>
    internal static AhziaelSignal? TryStart(int port) {
        var signal = new AhziaelSignal(port);
        try {
            signal._listener.Start();
        }
        catch (SocketException) {
            signal.Dispose();

            return null;
        }

        _ = signal.AcceptLoopAsync();

        return signal;
    }

    private async Task AcceptLoopAsync() {
        while (!_stop.IsCancellationRequested) {
            TcpClient client;
            try {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception) {
                return;
            }

            _ = AnswerAsync(client);
        }
    }

    private async Task AnswerAsync(TcpClient client) {
        using (client) {
            try {
                client.ReceiveTimeout = 2000;
                client.SendTimeout = 2000;
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 256, leaveOpen: true);
                var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
                if (line is null || !line.StartsWith("AHZ? ", StringComparison.Ordinal) || line.Length > 200) {
                    return;
                }

                var challenge = line[5..].Trim();
                var answer = Convert.ToHexString(HMACSHA256.HashData(_token, Encoding.ASCII.GetBytes(challenge)));
                var bytes = Encoding.ASCII.GetBytes($"AHZIAEL {answer}\n");
                await stream.WriteAsync(bytes);
            }
            catch (Exception) {
                // A bad or slow caller: nothing to answer.
            }
        }
    }

    public void Dispose() {
        _stop.Cancel();
        try {
            _listener.Stop();
        }
        catch (Exception) {
            // Already stopped.
        }

        _stop.Dispose();
    }

}
