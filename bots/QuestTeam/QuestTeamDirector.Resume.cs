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
using System.Linq;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;

namespace Imlight.CoreLib.Game.QuestTeam;

/// <summary>
/// The team picks up where it left off after a server restart or being turned off and on (Niduus, 2026-11-15: "give the QT
/// resume-where-it-left-off memory", as Ultimus has). Every 20 seconds what they are in the middle of goes to
/// logs/quest-team/resume.json: the quest and step, how long each story quest has taken so far, the alarms and losses on each
/// step, a break or a clear-around after ten alarms, and the collect spots each of them already did. Their quests' own
/// progress is the game's and is always kept. Read back once all four are in the game; older than 6 hours, or after
/// Ahziael's Restart (four new wizards), it is not used.
/// </summary>
internal sealed partial class QuestTeamDirectorActor {

    private const string RESUME_FILE = "resume.json";
    private DateTime _resumeSavedAt = DateTime.MinValue;
    private bool _resumeRead;

    private sealed class ResumeState {
        public DateTime Saved { get; set; }
        public List<ulong> CharIds { get; set; } = [];
        public string Quest { get; set; }
        public string Doing { get; set; }
        public Dictionary<string, double> RunSeconds { get; set; } = [];
        public Dictionary<string, int> Alarms { get; set; } = [];
        public Dictionary<string, int> Losses { get; set; } = [];
        public DateTime BreakUntil { get; set; }
        public string ClearZone { get; set; }
        public float ClearX { get; set; }
        public float ClearY { get; set; }
        public DateTime ClearUntil { get; set; }
        public Dictionary<string, Dictionary<string, List<float[]>>> CollectSpots { get; set; } = [];
    }

    private static string ResumePath => Path.Combine(QuestTeamWire.Folder, RESUME_FILE);

    /// <summary>Every 20 seconds while they play: what they are in the middle of.</summary>
    private void SaveResume() {
        if (!_resumeRead || _team.Count != 4 || _team.Any(m => m.Wizard is null) || DateTime.UtcNow - _resumeSavedAt < TimeSpan.FromSeconds(20)) {
            return;
        }

        _resumeSavedAt = DateTime.UtcNow;
        var state = new ResumeState {
            Saved = DateTime.UtcNow, CharIds = [.. _team.Select(m => m.CharId)], Quest = _currentQuest, Doing = _target?.What ?? _doing,
            RunSeconds = _runs.ToDictionary(r => r.Key, r => r.Value.Seconds),
            Alarms = new Dictionary<string, int>(_alarms), Losses = new Dictionary<string, int>(_losses),
            BreakUntil = _breakUntil,
            CollectSpots = _team.ToDictionary(m => m.CharId.ToString(System.Globalization.CultureInfo.InvariantCulture), m => BotCollect.Spots(m.CharId)),
        };
        if (_clearAround is { } clear) {
            (state.ClearZone, state.ClearX, state.ClearY, state.ClearUntil) = clear;
        }

        try {
            Directory.CreateDirectory(QuestTeamWire.Folder);
            var temp = ResumePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state));
            File.Move(temp, ResumePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Quest Team: the resume file could not be saved: {0}", Logger.Args(ex.Message));
        }
    }

    /// <summary>Once, when all four are in the game: picks up what they were in the middle of.</summary>
    private void ReadResume() {
        if (_resumeRead || _team.Count != 4 || _team.Any(m => m.State != "playing" || m.Wizard is null)) {
            return;
        }

        _resumeRead = true;
        ResumeState state;
        try {
            if (!File.Exists(ResumePath)) {
                return;
            }

            state = JsonSerializer.Deserialize<ResumeState>(File.ReadAllText(ResumePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            Logger.Debug("Quest Team: the resume file could not be read: {0}", Logger.Args(ex.Message));
            return;
        }

        // The same four wizards, and not long ago: else they start fresh.
        if (state is null || state.CharIds is not { Count: 4 } ids || !ids.SequenceEqual(_team.Select(m => m.CharId))
            || DateTime.UtcNow - state.Saved > TimeSpan.FromHours(6)) {
            return;
        }

        // Time a story quest had taken before is kept; the time the server was down does not count against it.
        foreach (var (quest, seconds) in state.RunSeconds ?? []) {
            if (_team.Any(m => m.Wizard?.HasQuest(quest) == true)) {
                StartRun(quest);
                if (_runs.TryGetValue(quest, out var run)) {
                    run.Started = DateTime.UtcNow.AddSeconds(-seconds);
                    run.ParkedSeconds = 0;
                }
            }
        }

        foreach (var (key, count) in state.Alarms ?? []) {
            _alarms[key] = Math.Max(_alarms.GetValueOrDefault(key), count);
        }

        foreach (var (key, count) in state.Losses ?? []) {
            _losses[key] = Math.Max(_losses.GetValueOrDefault(key), count);
        }

        if (state.BreakUntil > DateTime.UtcNow) {
            _breakUntil = state.BreakUntil;
        }

        if (!string.IsNullOrEmpty(state.ClearZone) && state.ClearUntil > DateTime.UtcNow) {
            _clearAround = (state.ClearZone, state.ClearX, state.ClearY, state.ClearUntil);
        }

        foreach (var member in _team) {
            if (state.CollectSpots?.TryGetValue(member.CharId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var spots) == true) {
                BotCollect.RestoreSpots(member.CharId, spots);
            }
        }

        var held = !string.IsNullOrEmpty(state.Quest) && _team[0].Wizard.HasQuest(state.Quest);
        Note("Team", held
            ? $"picks up where it left off: {QuestTeamText.Title(state.Quest)}" + (string.IsNullOrEmpty(state.Doing) ? "." : $" ({state.Doing}).")
            : "picks up where it left off: the quest it was on is done or gone, so it plans fresh.");
    }

    /// <summary>Four new wizards (Ahziael's Restart): nothing of the old team is picked up.</summary>
    private void ForgetResume() {
        _resumeRead = false;
        try {
            File.Delete(ResumePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Quest Team: the resume file could not be removed: {0}", Logger.Args(ex.Message));
        }
    }

}
