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

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// What the game server and Ultimus's mini server (UltimusBrain) say to each other over 127.0.0.1: one line of JSON each
/// way per question, a fork of the Quest Team's (Game/QuestTeam/QuestTeamWire). Also where Ultimus keeps its files, which
/// Ahziael reads.
/// </summary>
internal sealed class UltimusRequest {
    public string Op { get; set; }
    // "note": who (Ultimus) and what happened.
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
    // "issue": "quest" (the quest is broken) or "team" (Ultimus itself got stuck), or "slow".
    public string Kind { get; set; }
    // "issue": what a player would do and see at that step, and where each wizard was and what it held.
    public string Expected { get; set; }
    public List<string> Seen { get; set; }
    // "issue": the debug facts (quest code, step code, goal type, zone, spot, NPC/creature template, last server text...),
    // one "Name: value" per line, so a quest can be debugged from the report alone (Niduus, 2026-11-12: "so you can also
    // run quest debugs from that Quester").
    public List<string> Debug { get; set; }
    // "issue": the step's code (goal name).
    public string Step { get; set; }
    // "status": the whole status as JSON, written as it is for Ahziael.
    public string Json { get; set; }
}

internal sealed class UltimusReply {
    public bool Ok { get; set; }
    public double Expected { get; set; }
    public bool Learned { get; set; }
    public string Error { get; set; }
}

internal static class UltimusWire {

    // The folder of the Ultimus's logs, issues and status, inside the server's logs folder (like logs/bot-players).
    internal const string FOLDER = "logs/ultimus";
    internal const string LOG_FILE = "ultimus.txt";
    internal const string STATUS_FILE = "status.txt";
    internal const string TEAM_FILE = "team.json";
    internal const string QUEST_LOG_FILE = "quest-log.txt";
    internal const string QUEST_TIMES_FILE = "quest-times.json";
    internal const string ISSUES_FILE = "issues.json";
    // Ahziael writes the number of the last issue it showed here, so the banner only shows new ones.
    internal const string ISSUES_SEEN_FILE = "issues-seen.txt";
    // Ahziael writes this file to make a new level 1 Ultimus (its Restart button); the server deletes it when done.
    internal const string RESTART_FILE = "restart-request.txt";
    // Ahziael's Pause: there while Ultimus stands still.
    internal const string PAUSE_FILE = "paused.txt";
    // Ahziael's "Pause and explain": the server pauses Ultimus and writes his whole plan to explain.txt.
    internal const string EXPLAIN_FILE = "explain-request.txt";

    /// <summary>Ahziael's "Back to story" (the guardrails, Niduus 2026-11-14): side trips dropped, a fresh plan toward the story.</summary>
    internal const string BACK_TO_STORY_FILE = "back-to-story-request.txt";
    // One plain-text debug report per issue (debug/issue-0001.txt), made to be read by a person or handed to Claude.
    internal const string DEBUG_FOLDER = "debug";
    // What Ultimus learned from its own mistakes, one JSON line each (learned.jsonl), for future bots.
    internal const string LEARNED_FILE = "learned.jsonl";

    internal static string Folder => Path.GetFullPath(FOLDER);

    internal static bool Enabled => ConfigurationManager.GetValue("Ultimus.Enabled", false);

    internal static int Port => ConfigurationManager.GetValue("Ultimus.Port", 12373);

    /// <summary>How many times longer than expected a quest may take before Ahziael is told (2 = twice as long).</summary>
    internal static double SlowFactor => System.Math.Clamp(ConfigurationManager.GetValue("Ultimus.SlowFactor", 2.0), 1.2, 20);

    /// <summary>How many times Ultimus may lose the same fight before it is an issue.</summary>
    internal static int QuestMinutes => System.Math.Clamp(ConfigurationManager.GetValue("Ultimus.QuestMinutes", 45), 0, 100000);

    internal static int LossesBeforeIssue => System.Math.Clamp(ConfigurationManager.GetValue("Ultimus.LossesBeforeIssue", 3), 1, 20);

    /// <summary>How many minutes without any progress before Ultimus counts as stuck.</summary>
    /// <summary>Whether Ultimus joins a real player's battle when the creature it needs is in it (Niduus: "Ultimus will join players if it's convenient for quests").</summary>
    internal static bool JoinPlayers => ConfigurationManager.GetValue("Ultimus.JoinPlayers", true);

    internal static int StuckMinutes => System.Math.Clamp(ConfigurationManager.GetValue("Ultimus.StuckMinutes", 8), 2, 120);

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.General);

    internal static string Write<T>(T value) => JsonSerializer.Serialize(value, s_json);

    internal static T Read<T>(string line) => JsonSerializer.Deserialize<T>(line, s_json);

}
