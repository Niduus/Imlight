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

    internal static string Folder => Path.GetFullPath(FOLDER);

    internal static int Port => ConfigurationManager.GetValue("Bazaar.Port", 12370);

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.General);

    internal static string Write<T>(T value) => JsonSerializer.Serialize(value, s_json);

    internal static T Read<T>(string line) => JsonSerializer.Deserialize<T>(line, s_json);

}
