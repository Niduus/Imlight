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
using System.Text.Json;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Bazaar;

/// <summary>
/// What the game server and the Bazaar bot say to each other over 127.0.0.1: one line of JSON each way per question.
/// </summary>
internal sealed class BazaarRequest {
    public string Op { get; set; }
    public uint Category { get; set; }
    public bool Everything { get; set; }
    public ulong Id { get; set; }
    public List<ulong> Ids { get; set; }
    public int Count { get; set; }
    public string Who { get; set; }
    public int Gold { get; set; }
}

internal sealed class BazaarReply {
    public bool Ok { get; set; }
    public byte[] Records { get; set; }
    public BazaarQuote Quote { get; set; }
    public int Stock { get; set; }
    public string Error { get; set; }
}

internal static class BazaarWire {

    // The folder of the bot's log, stock and status, inside the server's logs folder (the Manager's server log reads only
    // the files directly in logs).
    internal const string FOLDER = "logs/bazaar-bot";
    internal const string LOG_FILE = "bazaar-bot.txt";
    internal const string STATUS_FILE = "status.txt";
    internal const string STOCK_FILE = "bazaar-stock.json";
    // What players sold in the Actual Bazaar ([Bazaar] Mode = Actual).
    internal const string ACTUAL_STOCK_FILE = "bazaar-actual-stock.json";

    internal static string Folder => Path.GetFullPath(FOLDER);

    internal static int Port => ConfigurationManager.GetValue("Bazaar.Port", 12370);

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.General);

    internal static string Write<T>(T value) => JsonSerializer.Serialize(value, s_json);

    internal static T Read<T>(string line) => JsonSerializer.Deserialize<T>(line, s_json);

    /// <summary>
    /// The status file Ahziael reads, one value a line: state (starting, ready, stopped, failed, asleep, off), process
    /// id, how many things it sells, when it was written, port, the mode, and how many things players put up in the
    /// Actual Bazaar. Written by the bot while it runs, and by the game server while it sleeps or is off.
    /// </summary>
    internal static void WriteStatus(string state, int pid, int count, int port, int actualCount) {
        try {
            var text = $"{state}\n{pid}\n{count}\n{DateTime.UtcNow:O}\n{port}\n{BazaarBook.Mode}\n{actualCount}\n";
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, STATUS_FILE);
            File.WriteAllText(path + ".tmp", text);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // Shown as unknown in Ahziael until the next try.
        }
    }

}
