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
using System.IO;
using System.Linq;
using Imlight.Common;
using Imlight.CoreLib.Game.Zone.Components;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// A short break on a stuck step, and Ahziael's "Fixed it" (Niduus, 2026-11-14, "all"). When every fix failed at a step,
/// he sits down for 2 minutes (no moving, no clicking), then plays on; the step waits 5, 15, then 30 minutes. "Fixed it"
/// on a broken quest in Ahziael (fixed-request.txt, one quest code per line) clears everything that held that quest back,
/// its set-aside steps, parked marks, lost-fight counts and filed issues, so he tries it again right away.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    internal const string FIXED_REQUEST = "fixed-request.txt";
    private static readonly TimeSpan s_breakLength = TimeSpan.FromMinutes(2);
    private DateTime _breakUntil = DateTime.MinValue;
    private string _breakFor = "";
    private DateTime _fixedLook = DateTime.MinValue;

    private void StartBreak(string what) {
        if (_team.Count == 0 || CombatDuelComponent.IsInBattle(_team[0].GlobalId)) {
            return;
        }

        _breakUntil = DateTime.UtcNow + s_breakLength;
        _breakFor = what ?? "";
        StopWalking(0);
        Emote(UltimusEmotes.SIT, "sit-break", "sits down for a short break", TimeSpan.FromMinutes(1));
    }

    /// <summary>True while he takes his short break: nothing else is planned (Lead returns at once).</summary>
    private bool OnBreak(UltimusMember.Status leader) {
        if (DateTime.UtcNow >= _breakUntil) {
            return false;
        }

        // A battle (a creature walked into him) ends the break: it comes first.
        if (CombatDuelComponent.IsInBattle(leader.GlobalId)) {
            _breakUntil = DateTime.MinValue;
            return false;
        }

        _doing = $"a short break before trying \"{UltimusText.Short(_breakFor, 60)}\" again";
        _lastProgress = DateTime.UtcNow;
        StopWalking(0);
        return true;
    }

    /// <summary>Ahziael's "Fixed it": each quest named is tried again at once, from a clean slate.</summary>
    private void FixedTick() {
        if (DateTime.UtcNow < _fixedLook) {
            return;
        }

        _fixedLook = DateTime.UtcNow.AddSeconds(5);
        var path = Path.Combine(UltimusWire.Folder, FIXED_REQUEST);
        if (!File.Exists(path)) {
            return;
        }

        string[] quests;
        try {
            quests = File.ReadAllLines(path);
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Ultimus: {0} could not be read: {1}", Logger.Args(FIXED_REQUEST, ex.Message));
            return;
        }

        foreach (var quest in quests.Select(q => q.Trim()).Where(q => q.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)) {
            MarkFixed(quest);
        }
    }

    private void MarkFixed(string quest) {
        bool About(string key) => key is not null && key.Contains(quest, StringComparison.OrdinalIgnoreCase);
        var parked = _parkedWhy.Where(p => string.Equals(p.Value.Quest, quest, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key)
            .Concat(_parkedAt.Keys.Where(About)).Distinct().ToList();
        foreach (var key in parked) {
            _parkedAt.Remove(key);
            _parkedFor.Remove(key);
            _parkCount.Remove(key);
            _parkedWhy.Remove(key);
            _parkedFix.Remove(key);
        }

        UltimusPlanner.ClearAside(quest);
        foreach (var key in _reported.Keys.Where(About).ToList()) {
            _reported.Remove(key);
        }

        foreach (var key in _problemSince.Keys.Where(About).ToList()) {
            _problemSince.Remove(key);
        }

        foreach (var key in _losses.Keys.Where(About).ToList()) {
            _losses.Remove(key);
        }

        _collectSearch.Keys.Where(About).ToList().ForEach(k => _collectSearch.Remove(k));
        _readAheadFound.Remove(quest);
        if (_breakUntil > DateTime.UtcNow) {
            _breakUntil = DateTime.MinValue;
        }

        Note("Ultimus", $"FIXED, says Niduus: {UltimusText.Title(quest)}. Everything that held it back is cleared, and he tries it again now.",
            UltimusLink.NOTABLE);
        Think($"Niduus marked {UltimusText.Title(quest)} as fixed. I forget what went wrong there and try it again right away, " +
              "and I'll flag it again if it still doesn't work.", "fixed|" + quest);
        Replan();
    }

}
