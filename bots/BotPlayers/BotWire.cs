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

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Imlight.Common;

namespace Imlight.CoreLib.Game.BotPlayers;

/// <summary>
/// What the game server and the Bot Players mini server (the bot brain) say to each other over 127.0.0.1: one line of
/// JSON each way per question, like the Bazaar bot (Game/Bazaar).
/// </summary>
internal sealed class BotRequest {
    public string Op { get; set; }
    public int Id { get; set; }
    // "make": the gender of the look the bot gets, and how many names the game's name lists hold for it.
    public string Gender { get; set; }
    public int Firsts { get; set; }
    public int Middles { get; set; }
    public int Lasts { get; set; }
    public string School { get; set; }
    public int Level { get; set; }
    public string Zone { get; set; }
    // "event": what happened to the bot (spawned, walking, joined, kicked, defeated, gone...) and in what words.
    public string Event { get; set; }
    public string Text { get; set; }
    public string Name { get; set; }
    public int Round { get; set; }
}

internal sealed class BotReply {
    public bool Ok { get; set; }
    public BotIdentity Bot { get; set; }
    public BotWalkPlan Walk { get; set; }
    public string Move { get; set; }
    public int Out { get; set; }
    public int Known { get; set; }
    public string Error { get; set; }
}

/// <summary>Who a bot is: its number in the brain's roster and its name keys (the character maker's name lists).</summary>
internal sealed class BotIdentity {
    public int Id { get; set; }
    public uint NameKeys { get; set; }
    public string Gender { get; set; }
    public string School { get; set; }
    public int Level { get; set; }
    public bool Returning { get; set; }
    public int Times { get; set; }
}

/// <summary>How a bot walks around the spot it came in at: a loop of so many points so far out, at a speed, with stops.</summary>
internal sealed class BotWalkPlan {
    public float Radius { get; set; }
    public int Points { get; set; }
    public float Speed { get; set; }
    public float PauseSeconds { get; set; }
}

internal static class BotWire {

    // The folder of the brain's log, roster and status, inside the server's logs folder, like logs/bazaar-bot.
    internal const string FOLDER = "logs/bot-players";
    internal const string LOG_FILE = "bot-players.txt";
    internal const string STATUS_FILE = "status.txt";
    internal const string ROSTER_FILE = "bot-roster.json";

    internal static string Folder => Path.GetFullPath(FOLDER);

    internal static bool Enabled => ConfigurationManager.GetValue("BotPlayers.Enabled", false);

    internal static int Port => ConfigurationManager.GetValue("BotPlayers.Port", 12371);

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.General);

    internal static string Write<T>(T value) => JsonSerializer.Serialize(value, s_json);

    internal static T Read<T>(string line) => JsonSerializer.Deserialize<T>(line, s_json);

}
