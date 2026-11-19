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
using Imlight.CoreLib.Game.Quests;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Ultimus;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.QuestTeam;

/// <summary>
/// The read-ahead before each world, as Ultimus does it (UltimusDirector.ReadAhead.cs; Niduus, 2026-11-15: the team
/// caught up on everything Ultimus can do): when the team starts a world's story, the server's quest checker (Quest Testing
/// tab) is read for that world's story quests, and every story step it says the server can't do yet is filed on Team
/// Issues before the team gets there, marked READ-AHEAD. The team still plays every step; nothing is skipped for it.
/// Also the team's stuck line for the Quest Team Spectator.
/// </summary>
internal sealed partial class QuestTeamDirectorActor {

    private readonly HashSet<string> _readAheadDone = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _readAheadFound = new(StringComparer.OrdinalIgnoreCase);
    private string _readAheadWaiting;
    private DateTime _readAheadNextLook = DateTime.MinValue;
    private DateTime _readAheadAskedAt = DateTime.MinValue;

    private void ReadAhead(string world) {
        if (string.IsNullOrEmpty(world) || _readAheadDone.Contains(world)) {
            return;
        }

        _readAheadWaiting = world;
        _readAheadNextLook = DateTime.MinValue;
        ReadAheadTick();
    }

    private void ReadAheadTick() {
        if (_readAheadWaiting is not { } world || DateTime.UtcNow < _readAheadNextLook) {
            return;
        }

        _readAheadNextLook = DateTime.UtcNow.AddMinutes(1);
        var path = UltimusDirectorActor.QuestCheckPath;
        if (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) >= TimeSpan.FromDays(1)) {
            if (_readAheadAskedAt == DateTime.MinValue) {
                _readAheadAskedAt = DateTime.UtcNow;
                if (QuestChecker.Start(IdCatalog.OutputPath)) {
                    Note("Team", $"reads ahead in {UltimusDirectorActor.WorldName(world)}: the quest check is being run in the background first.");
                }
            }
            else if (DateTime.UtcNow - _readAheadAskedAt > TimeSpan.FromHours(2)) {
                Note("Team", $"could not read ahead in {UltimusDirectorActor.WorldName(world)}: the quest check was not written. It plays on as usual.");
                _readAheadDone.Add(world);
                (_readAheadWaiting, _readAheadAskedAt) = (null, DateTime.MinValue);
            }

            return;
        }

        (_readAheadWaiting, _readAheadAskedAt) = (null, DateTime.MinValue);
        _readAheadDone.Add(world);
        if (UltimusDirectorActor.PuzzlesAhead(world, q => !_team.All(m => m.Wizard?.HasCompletedQuest(q) == true), QuestTeamWire.Folder) is { Length: > 0 } puzzles) {
            Note("Team", $"READ-AHEAD: {UltimusDirectorActor.WorldName(world)}: puzzle steps ahead: {puzzles}. The team works each one out from what " +
                         "the quest's people say there, as players would.");
        }

        if (UltimusDirectorActor.ReadAheadProblems(world) is not { } found) {
            return;
        }

        var open = found.Where(f => !_team.All(m => m.Wizard?.HasCompletedQuest(f.Quest) == true)).Take(40).ToList();
        if (open.Count == 0) {
            Note("Team", $"READ-AHEAD: {UltimusDirectorActor.WorldName(world)}: the quest check finds nothing wrong with its story quests.");

            return;
        }

        Note("Team", $"READ-AHEAD: {UltimusDirectorActor.WorldName(world)}: {open.Count} story quest(s) have a step the server may not be able to do " +
                     "yet; filed on Team Issues.", QuestTeamLink.NOTABLE);
        foreach (var (quest, problems) in open) {
            if (!_reported.TryAdd("readahead|" + quest, DateTime.UtcNow)) {
                continue;
            }

            // Known for the stuck line either way; filed on the Issues page only the first time, not again after a restart.
            _readAheadFound[quest] = string.Join(" ", problems.Take(2));
            if (!BotShared.ReadAheadFiled.FirstTime(QuestTeamWire.Folder, quest)) {
                continue;
            }

            Count("issues raised");
            var text = $"READ-AHEAD ({UltimusDirectorActor.WorldName(world)}): {string.Join(" ", problems.Take(3))} Found by the quest check before the " +
                       "team got there; it still tries it as players would when it does.";
            QuestTeamLink.Issue("quest", quest, QuestTeamText.Title(quest), world, text, "Every story step can be done on this server.",
                [$"Quest code: {quest}", $"Quest data file: data/spiraldb/QuestTemplates/questtemplates_{quest}.json", .. problems.Select(p => "Quest check: " + p)],
                QuestTeamLink.NOTABLE);
        }
    }

    // What the read-ahead warned of for the quest the team is on now ("" none).
    private string ReadAheadLine() => _currentQuest is { } quest && _readAheadFound.TryGetValue(quest, out var found) ? found : "";

    // The team's stuck line while it is stuck: where, how long, and the move it tries (cleared on progress or after 3 minutes).
    private string _stuckLine = "";
    private DateTime _stuckLineAt = DateTime.MinValue;

    private void SetStuckLine(string line) => (_stuckLine, _stuckLineAt) = (line ?? "", DateTime.UtcNow);

    private string StuckLine() {
        if (_stuckLine.Length == 0 || DateTime.UtcNow - _stuckLineAt > TimeSpan.FromMinutes(3) || _progressAt > _stuckLineAt) {
            return "";
        }

        return _stuckLine;
    }

    // ---------------------------------------------------------------------
    // Ahziael's "Retry now" and "Fixed it" on Team Issues, as Ultimus's (Niduus, 2026-11-15)
    // ---------------------------------------------------------------------

    internal const string RETRY_REQUEST = "retry-request.txt";
    internal const string FIXED_REQUEST = "fixed-request.txt";
    private DateTime _requestLook = DateTime.MinValue;

    private static string[] TakeRequest(string name) {
        var path = Path.Combine(QuestTeamWire.Folder, name);
        if (!File.Exists(path)) {
            return null;
        }

        try {
            var lines = File.ReadAllLines(path);
            File.Delete(path);

            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    private void RequestsTick() {
        if (DateTime.UtcNow < _requestLook) {
            return;
        }

        _requestLook = DateTime.UtcNow.AddSeconds(5);

        // Retry now: the break, the clearing round a stuck spot and the click memory end; the step is tried again at once.
        if (TakeRequest(RETRY_REQUEST) is not null) {
            _breakUntil = DateTime.MinValue;
            _clearAround = null;
            _alarms.Clear();
            _clicks.Clear();
            _sent.Clear();
            _target = null;
            _stuckLine = "";
            Note("Team", "RETRY NOW, asked in Ahziael: the team stops waiting and tries its step again.", QuestTeamLink.COMMAND);
        }

        // Fixed it: everything that held these quests back is forgotten (filed issues, alarms, clicks), so the team tries them
        // again from a clean slate and files them again only if they still go wrong.
        if (TakeRequest(FIXED_REQUEST) is { } quests) {
            foreach (var quest in quests.Select(q => q.Trim()).Where(q => q.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)) {
                foreach (var key in _reported.Keys.Where(k => k.Contains(quest, StringComparison.OrdinalIgnoreCase)).ToList()) {
                    _reported.Remove(key);
                }

                foreach (var key in _alarms.Keys.Where(k => k.Contains(quest, StringComparison.OrdinalIgnoreCase)).ToList()) {
                    _alarms.Remove(key);
                }

                _readAheadFound.Remove(quest);
                Note("Team", $"FIXED IT, marked in Ahziael: {QuestTeamText.Title(quest)} is tried again from a clean slate.", QuestTeamLink.COMMAND);
            }

            _clicks.Clear();
            _sent.Clear();
            _target = null;
            _breakUntil = DateTime.MinValue;
        }
    }

}
