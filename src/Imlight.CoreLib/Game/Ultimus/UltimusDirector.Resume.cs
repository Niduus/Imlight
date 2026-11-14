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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Picking up exactly where he was after a server restart (Niduus, 2026-11-14, "resume mid-step exactly after a restart").
/// The quest he was on, the step he picked for it, the steps he set aside or parked, the spot he marked before a trip and
/// the collect spots he already did are saved every 20 seconds to logs/ultimus/resume.json and read back when he logs in.
/// His quests' own progress is the game's and is always kept; a dungeon he was in is closed by a restart, so he walks back
/// in like a player. Older than 6 hours, or after Ahziael's Restart (a new Ultimus), the file is not used.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    private const string RESUME_FILE = "resume.json";
    private DateTime _resumeSavedAt = DateTime.MinValue;
    private bool _resumeRead;

    private sealed class ResumeState {
        public DateTime Saved { get; set; }
        public ulong CharId { get; set; }
        public string Quest { get; set; }
        public string Doing { get; set; }
        public Dictionary<string, string> Picks { get; set; } = [];
        public Dictionary<string, DateTime> Asides { get; set; } = [];
        public Dictionary<string, DateTime> Parked { get; set; } = [];
        public string MarkedZone { get; set; }
        public DateTime MarkedAt { get; set; }
        public Dictionary<string, List<float[]>> CollectSpots { get; set; } = [];
    }

    private static string ResumePath => Path.Combine(UltimusWire.Folder, RESUME_FILE);

    /// <summary>Every 20 seconds while he plays: what he is in the middle of.</summary>
    private void SaveResume() {
        if (_team.Count == 0 || _team[0].Wizard is null || DateTime.UtcNow - _resumeSavedAt < TimeSpan.FromSeconds(20)) {
            return;
        }

        _resumeSavedAt = DateTime.UtcNow;
        var (quest, picks, asides) = UltimusPlanner.Saved();
        var state = new ResumeState {
            Saved = DateTime.UtcNow, CharId = _team[0].CharId, Quest = _currentQuest ?? quest, Doing = _target?.What ?? "",
            Picks = picks, Asides = asides,
            Parked = _parkedAt.Where(p => DateTime.UtcNow - p.Value < ParkedFor(p.Key)).ToDictionary(p => p.Key, p => p.Value),
            MarkedZone = _markedZone, MarkedAt = _markedAt, CollectSpots = BotCollect.Spots(_team[0].CharId),
        };
        try {
            Directory.CreateDirectory(UltimusWire.Folder);
            var temp = ResumePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state));
            File.Move(temp, ResumePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Ultimus: the resume file could not be saved: {0}", Logger.Args(ex.Message));
        }
    }

    /// <summary>Once, after he logs in: picks up what he was in the middle of before the restart.</summary>
    private void ReadResume() {
        if (_resumeRead || _team.Count == 0 || _team[0].Wizard is null) {
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
            Logger.Debug("Ultimus: the resume file could not be read: {0}", Logger.Args(ex.Message));
            return;
        }

        if (state is null || state.CharId != _team[0].CharId || DateTime.UtcNow - state.Saved > TimeSpan.FromHours(6)) {
            return;
        }

        UltimusPlanner.Restore(state.Quest, state.Picks, state.Asides);
        foreach (var (key, at) in state.Parked ?? []) {
            _parkedAt.TryAdd(key, at);
        }

        if (_markedZone is null && !string.IsNullOrEmpty(state.MarkedZone) && DateTime.UtcNow - state.MarkedAt < TimeSpan.FromHours(1)) {
            (_markedZone, _markedAt) = (state.MarkedZone, state.MarkedAt);
        }

        BotCollect.RestoreSpots(_team[0].CharId, state.CollectSpots);
        var held = state.Quest is not null && _team[0].Wizard.HasQuest(state.Quest);
        Note("Ultimus", held
            ? $"picks up where he was before the restart: {UltimusText.Title(state.Quest)}" +
              (string.IsNullOrEmpty(state.Doing) ? "." : $" ({state.Doing}).")
            : "picks up after the restart: the quest he was on is done or gone, so he plans fresh.");
        if (held) {
            Think($"The server restarted. I was on {UltimusText.Title(state.Quest)}" +
                  (string.IsNullOrEmpty(state.Doing) ? "" : $", doing \"{state.Doing}\"") + ", so I pick up right there.", "resume");
        }
    }

    /// <summary>A new Ultimus (Ahziael's Restart): nothing of the old one is picked up.</summary>
    private static void ForgetResume() {
        try {
            File.Delete(ResumePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Ultimus: the resume file could not be removed: {0}", Logger.Args(ex.Message));
        }
    }

}
