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

using System.IO;
using System.Text.Json;
using Imlight.Common;

namespace Imlight.CoreLib.Game.QuestTeam;

/// <summary>
/// What the game server and the Quest Team mini server (QuestTeamBrain) say to each other over 127.0.0.1: one line of JSON
/// each way per question, like Bot Players (Game/BotPlayers). Also where the Quest Team keeps its files, which Ahziael reads.
/// </summary>
internal sealed class TeamRequest {
    public string Op { get; set; }
    // "note": which wizard (Ice, Life, Death, Storm or Team) and what happened.
    public string Who { get; set; }
    public string Text { get; set; }
    // "quest": "start", "step" or "done"; the quest's code, title, world and what it asks.
    public string Event { get; set; }
    public string Quest { get; set; }
    public string Title { get; set; }
    public string World { get; set; }
    public int Steps { get; set; }
    public int Fights { get; set; }
    public double Seconds { get; set; }
    // "issue": "quest" (the quest is broken) or "team" (the Quest Team itself got stuck), or "slow".
    public string Kind { get; set; }
    // "status": the whole status as JSON, written as it is for Ahziael.
    public string Json { get; set; }
}

internal sealed class TeamReply {
    public bool Ok { get; set; }
    public double Expected { get; set; }
    public bool Learned { get; set; }
    public string Error { get; set; }
}

internal static class QuestTeamWire {

    // The folder of the Quest Team's logs, issues and status, inside the server's logs folder (like logs/bot-players).
    internal const string FOLDER = "logs/quest-team";
    internal const string LOG_FILE = "quest-team.txt";
    internal const string STATUS_FILE = "status.txt";
    internal const string TEAM_FILE = "team.json";
    internal const string QUEST_LOG_FILE = "quest-log.txt";
    internal const string QUEST_TIMES_FILE = "quest-times.json";
    internal const string ISSUES_FILE = "issues.json";
    // Ahziael writes the number of the last issue it showed here, so the banner only shows new ones.
    internal const string ISSUES_SEEN_FILE = "issues-seen.txt";
    // Ahziael writes this file to make four new level 1 wizards (its Restart button); the server deletes it when done.
    internal const string RESTART_FILE = "restart-request.txt";

    internal static string Folder => Path.GetFullPath(FOLDER);

    internal static bool Enabled => ConfigurationManager.GetValue("QuestTeam.Enabled", false);

    internal static int Port => ConfigurationManager.GetValue("QuestTeam.Port", 12372);

    /// <summary>How many times longer than expected a quest may take before Ahziael is told (2 = twice as long).</summary>
    internal static double SlowFactor => System.Math.Clamp(ConfigurationManager.GetValue("QuestTeam.SlowFactor", 2.0), 1.2, 20);

    /// <summary>How many times the team may lose the same fight before it is an issue.</summary>
    internal static int LossesBeforeIssue => System.Math.Clamp(ConfigurationManager.GetValue("QuestTeam.LossesBeforeIssue", 3), 1, 20);

    /// <summary>How many minutes without any progress before the team counts as stuck.</summary>
    internal static int StuckMinutes => System.Math.Clamp(ConfigurationManager.GetValue("QuestTeam.StuckMinutes", 8), 2, 120);

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.General);

    internal static string Write<T>(T value) => JsonSerializer.Serialize(value, s_json);

    internal static T Read<T>(string line) => JsonSerializer.Deserialize<T>(line, s_json);

}
