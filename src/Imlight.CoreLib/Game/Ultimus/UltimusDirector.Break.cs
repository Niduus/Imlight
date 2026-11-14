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
using Akka.Actor;
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
        RetryTick();
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

    // ---------------------------------------------------------------------
    // Retry now (Niduus, 2026-11-14): one parked step tried again at once, without saying it is fixed
    // ---------------------------------------------------------------------

    internal const string RETRY_REQUEST = "retry-request.txt";

    private void RetryTick() {
        var path = Path.Combine(UltimusWire.Folder, RETRY_REQUEST);
        if (!File.Exists(path)) {
            return;
        }

        string[] keys;
        try {
            keys = File.ReadAllLines(path);
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Ultimus: {0} could not be read: {1}", Logger.Args(RETRY_REQUEST, ex.Message));
            return;
        }

        foreach (var key in keys.Select(k => k.Trim()).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal)) {
            var (quest, what, _) = _parkedWhy.GetValueOrDefault(key);
            // The wait ends now; how many times it was parked stays, so a step that fails again still waits longer next time.
            _parkedAt.Remove(key);
            UltimusPlanner.ClearAsideKey(key);
            _problemSince.Remove(key);
            _breakUntil = DateTime.MinValue;
            Note("Ultimus", $"RETRY NOW, asked in Ahziael: tries \"{what ?? key}\"{(string.IsNullOrEmpty(quest) ? "" : $" ({UltimusText.Title(quest)})")} again right away.");
            Think($"Niduus asked me to try \"{what ?? key}\" again now. I go back to it.", "retry|" + key);
        }

        Replan();
    }

    // ---------------------------------------------------------------------
    // Tips from players (Niduus, 2026-11-14): what a player tells him while he is stuck is kept with the step
    // ---------------------------------------------------------------------

    private readonly Dictionary<string, List<string>> _tips = [];

    /// <summary>Tips players gave for a parked step (newest last).</summary>
    private IReadOnlyList<string> TipsFor(string key) => _tips.TryGetValue(key, out var list) ? list : [];

    // Words a tip usually has: a way to go or a thing to do (a question to him or a hello is not a tip).
    private static readonly string[] s_tipWords = [
        "go ", "left", "right", "behind", "upstairs", "downstairs", "door", "talk to", "click", "use ", "kill", "defeat", "fight",
        "jump", "north", "south", "east", "west", "back", "through", "teleport", "portal", "sigil", "you need", "you have to",
        "try ", "first", "then", "after", "before", "near", "next to", "inside", "outside", "the ",
    ];

    /// <summary>
    /// A player's line while he is stuck or has a parked step: when it reads like advice, it is written on that step's
    /// issue (scrubbed of private details) and he thanks them. True when it was taken as a tip.
    /// </summary>
    private bool MaybeTip(string speaker, ulong speakerCharId, string text, bool whisper) {
        var stuckKey = _parkedAt.Where(p => DateTime.UtcNow - p.Value < ParkedFor(p.Key)).OrderByDescending(p => p.Value).Select(p => p.Key).FirstOrDefault();
        var stuckNow = _doing?.StartsWith("stuck", StringComparison.Ordinal) == true || _doing?.StartsWith("looking around", StringComparison.Ordinal) == true
                       || DateTime.UtcNow < _breakUntil;
        stuckKey ??= stuckNow ? _target?.Key : null;
        if (stuckKey is null || string.IsNullOrWhiteSpace(text)) {
            return false;
        }

        var line = text.Trim();
        var lower = " " + line.ToLowerInvariant() + " ";
        if (line.EndsWith('?') || line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 2 || !s_tipWords.Any(w => lower.Contains(w, StringComparison.Ordinal))) {
            return false;
        }

        var tip = BotShared.BotSocial.Scrub(line);
        if (string.IsNullOrWhiteSpace(tip)) {
            return false;
        }

        var list = _tips.TryGetValue(stuckKey, out var known) ? known : _tips[stuckKey] = [];
        list.Add($"{speaker}: {UltimusText.Short(tip, 160)}");
        if (list.Count > 5) {
            list.RemoveAt(0);
        }

        var quest = _parkedWhy.TryGetValue(stuckKey, out var why) && !string.IsNullOrEmpty(why.Quest) ? why.Quest : _target?.Quest ?? _currentQuest;
        var step = why.What ?? _target?.What ?? "";
        Note("Ultimus", $"TIP from {speaker} for \"{step}\": \"{UltimusText.Short(tip, 120)}\" (written on the issue).", UltimusLink.NOTABLE);
        Think($"{speaker} told me \"{UltimusText.Short(tip, 80)}\" while I'm stuck on \"{step}\". I wrote it down for Niduus and keep it in mind.",
            "tip|" + stuckKey + "|" + list.Count);
        UltimusLink.Issue("tip", quest ?? "", string.IsNullOrEmpty(quest) ? "" : UltimusText.Title(quest), "", $"TIP from {speaker} for \"{step}\": {tip}",
            "", null, step, [$"Step key: {stuckKey}", $"Said by: {speaker} ({(whisper ? "whisper" : "chat")})"]);
        var thanks = "Thanks for the tip! I'll keep that in mind.";
        _actors[0].Tell(whisper ? new UltimusMember.WhisperText(thanks, speakerCharId) : new UltimusMember.SayText(thanks));
        return true;
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
            _tips.Remove(key);
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
