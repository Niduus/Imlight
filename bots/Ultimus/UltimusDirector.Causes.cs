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
using System.Text;
using Imlight.Common;
using Imlight.CoreLib.Game.Zone.Components;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// The nightly cause report (Niduus, 2026-11-15, "all" of the robustness pitches). Every second he plays is put under one
/// cause: questing, fighting, talking, a break or side trip, or time lost (getting unstuck, a flip-flop, a step he can't
/// work out, waiting to heal, standing still, moving with no real progress). Minutes add up per cause per day
/// (cause-days.json, the last 14 days), with the 5 longest moments of each cause (time, area, step) as examples, so a
/// morning look at cause-report.txt says what cost him the most last night and where to look first, instead of a
/// search through the whole log. Written every 30 minutes and when the day changes.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    private const string CAUSE_DATA = "cause-days.json";
    private const string CAUSE_FILE = "cause-report.txt";
    private const int CAUSE_DAYS_KEPT = 14;
    private const int CAUSE_EXAMPLES = 5;

    // Causes; the ones in s_lostCauses are time lost.
    internal const string CAUSE_QUESTING = "questing";
    internal const string CAUSE_FIGHTING = "fighting";
    internal const string CAUSE_TALKING = "talking to people";
    internal const string CAUSE_READING = "taking a moment between steps";
    internal const string CAUSE_UNSTUCK = "getting unstuck";
    internal const string CAUSE_FLIP_FLOP = "two rules taking turns (flip-flop hold)";
    internal const string CAUSE_NO_STEP = "a step he can't work out";
    internal const string CAUSE_HEAL = "waiting to heal";
    internal const string CAUSE_BREAK = "on a break";
    internal const string CAUSE_SIDE_TRIP = "side trip (shop, trainer, Bazaar)";
    internal const string CAUSE_STILL = "standing still";
    internal const string CAUSE_NO_PROGRESS = "moving with no real progress for over 10 minutes";

    private static readonly HashSet<string> s_lostCauses = [CAUSE_UNSTUCK, CAUSE_FLIP_FLOP, CAUSE_NO_STEP, CAUSE_HEAL, CAUSE_STILL, CAUSE_NO_PROGRESS];

    internal sealed record CauseFacts(bool InBattle, bool Talking, bool Reading, bool Unsticking, bool Holding, bool NoStep, bool HealWaiting,
        bool OnBreak, bool SideTrip, double StillSeconds, double MinutesSinceRealProgress);

    /// <summary>The one cause of this moment, most telling first (a flip-flop hold while getting unstuck is "getting unstuck").</summary>
    internal static string CauseOf(CauseFacts f) {
        if (f.InBattle) {
            return CAUSE_FIGHTING;
        }

        if (f.Talking) {
            return CAUSE_TALKING;
        }

        if (f.Unsticking) {
            return CAUSE_UNSTUCK;
        }

        if (f.Holding) {
            return CAUSE_FLIP_FLOP;
        }

        if (f.NoStep) {
            return CAUSE_NO_STEP;
        }

        if (f.HealWaiting) {
            return CAUSE_HEAL;
        }

        if (f.OnBreak) {
            return CAUSE_BREAK;
        }

        if (f.Reading) {
            return CAUSE_READING;
        }

        if (f.SideTrip) {
            return CAUSE_SIDE_TRIP;
        }

        if (f.StillSeconds > 15) {
            return CAUSE_STILL;
        }

        return f.MinutesSinceRealProgress > 10 ? CAUSE_NO_PROGRESS : CAUSE_QUESTING;
    }

    internal static bool IsLost(string cause) => s_lostCauses.Contains(cause);

    internal sealed class CauseMoment {
        public string At { get; set; } = "";
        public double Minutes { get; set; }
        public string Zone { get; set; } = "";
        public string Step { get; set; } = "";
        public string Quest { get; set; } = "";
    }

    internal sealed class CauseDay {
        public Dictionary<string, double> Seconds { get; set; } = [];
        public Dictionary<string, List<CauseMoment>> Moments { get; set; } = [];
    }

    private Dictionary<string, CauseDay> _causeDays;
    private DateTime _causeTick = DateTime.MinValue;
    private DateTime _causeWritten = DateTime.UtcNow;
    private string _causeDate = "";

    // The run of one cause now going on (closed into an example when the cause changes).
    private (string Cause, DateTime Since, string Zone, string Step, string Quest) _causeRun;

    private void CauseTick(UltimusMember.Status leader) {
        var now = DateTime.UtcNow;
        var dt = (now - _causeTick).TotalSeconds;
        _causeTick = now;
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (_causeDate.Length > 0 && _causeDate != today) {
            CloseCauseRun(now);
            WriteCauses();
        }

        _causeDate = today;
        if (dt <= 0 || dt > 10 || string.IsNullOrEmpty(leader.Zone) || leader.State != "playing") {
            CloseCauseRun(now);
            return;
        }

        var cause = CauseOf(new CauseFacts(
            InBattle: CombatDuelComponent.IsInBattle(leader.GlobalId),
            Talking: leader.Talking,
            Reading: now < _pauseUntil || _grabWaiting,
            Unsticking: Unsticking(0),
            Holding: _hold is not null,
            NoStep: _target?.Kind == UltimusPlanner.NONE,
            HealWaiting: _healWaiting,
            OnBreak: now < _breakUntil,
            SideTrip: !string.IsNullOrEmpty(_sideTripKey),
            StillSeconds: (now - _wasteStill.At).TotalSeconds,
            MinutesSinceRealProgress: (now - _realProgressAt).TotalMinutes));

        _causeDays ??= ReadCauses();
        var day = CauseDayOf(today);
        day.Seconds[cause] = day.Seconds.GetValueOrDefault(cause) + dt;
        if (_causeRun.Cause != cause) {
            CloseCauseRun(now);
            _causeRun = (cause, now - TimeSpan.FromSeconds(dt), leader.Zone, _target?.What ?? _doing ?? "", _target?.Quest ?? _currentQuest ?? "");
        }

        if (now - _causeWritten > TimeSpan.FromMinutes(30)) {
            CloseCauseRun(now);
            WriteCauses();
        }
    }

    private CauseDay CauseDayOf(string date) {
        if (!_causeDays.TryGetValue(date, out var day)) {
            _causeDays[date] = day = new CauseDay();
            foreach (var old in _causeDays.Keys.OrderByDescending(k => k, StringComparer.Ordinal).Skip(CAUSE_DAYS_KEPT).ToList()) {
                _causeDays.Remove(old);
            }
        }

        return day;
    }

    /// <summary>The run now going on, kept as an example when it is one of the day's longest for its cause.</summary>
    private void CloseCauseRun(DateTime now) {
        if (_causeRun.Cause is null) {
            return;
        }

        var run = _causeRun;
        _causeRun = default;
        var minutes = (now - run.Since).TotalMinutes;
        if (minutes < 0.5 || _causeDays is null) {
            return;
        }

        var day = CauseDayOf(run.Since.ToLocalTime().ToString("yyyy-MM-dd"));
        if (!day.Moments.TryGetValue(run.Cause, out var list)) {
            day.Moments[run.Cause] = list = [];
        }

        list.Add(new CauseMoment { At = run.Since.ToLocalTime().ToString("HH:mm"), Minutes = Math.Round(minutes, 1), Zone = run.Zone, Step = run.Step, Quest = run.Quest });
        list.Sort((a, b) => b.Minutes.CompareTo(a.Minutes));
        if (list.Count > CAUSE_EXAMPLES) {
            list.RemoveRange(CAUSE_EXAMPLES, list.Count - CAUSE_EXAMPLES);
        }
    }

    private static Dictionary<string, CauseDay> ReadCauses() {
        try {
            var path = Path.Combine(UltimusWire.Folder, CAUSE_DATA);
            if (File.Exists(path) && UltimusWire.Read<Dictionary<string, CauseDay>>(File.ReadAllText(path)) is { } read) {
                return read;
            }
        }
        catch (Exception ex) {
            Logger.Debug("Ultimus: {0} could not be read: {1}", Logger.Args(CAUSE_DATA, ex.Message));
        }

        return [];
    }

    private void WriteCauses() {
        _causeWritten = DateTime.UtcNow;
        if (_causeDays is null) {
            return;
        }

        try {
            Directory.CreateDirectory(UltimusWire.Folder);
            File.WriteAllText(Path.Combine(UltimusWire.Folder, CAUSE_DATA), UltimusWire.Write(_causeDays));
            File.WriteAllText(Path.Combine(UltimusWire.Folder, CAUSE_FILE), CauseReport(_causeDays, DateTime.Now));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Ultimus: {0} could not be written: {1}", Logger.Args(CAUSE_FILE, ex.Message));
        }
    }

    /// <summary>The report: today (and last night) by cause, the top 3 causes of lost time with examples, and the last 14 days.</summary>
    internal static string CauseReport(Dictionary<string, CauseDay> days, DateTime written) {
        static string Min(double seconds) => seconds >= 3600 ? $"{seconds / 3600:0.0} h" : $"{seconds / 60:0} min";

        var text = new StringBuilder();
        text.AppendLine($"WHAT HIS TIME WENT ON (written {written:yyyy-MM-dd HH:mm})");
        text.AppendLine("Every second he plays is put under one cause. Time lost is time that did not move his story on.");
        foreach (var date in days.Keys.OrderByDescending(k => k, StringComparer.Ordinal).Take(2)) {
            var day = days[date];
            var total = day.Seconds.Values.Sum();
            if (total <= 0) {
                continue;
            }

            var lost = day.Seconds.Where(s => IsLost(s.Key)).Sum(s => s.Value);
            text.AppendLine();
            text.AppendLine($"{date}: played {Min(total)}, lost {Min(lost)} ({lost * 100 / total:0}%)");
            foreach (var (cause, seconds) in day.Seconds.OrderByDescending(s => s.Value)) {
                text.AppendLine($"  {Min(seconds),8}  {seconds * 100 / total,3:0}%  {(IsLost(cause) ? "LOST " : "     ")} {cause}");
            }

            var top = day.Seconds.Where(s => IsLost(s.Key) && s.Value >= 60).OrderByDescending(s => s.Value).Take(3).ToList();
            if (top.Count == 0) {
                text.AppendLine("  No cause of lost time over a minute.");
                continue;
            }

            text.AppendLine($"  Look at first ({top.Count} biggest causes of lost time, longest moments):");
            for (var i = 0; i < top.Count; i++) {
                text.AppendLine($"  {i + 1}. {top[i].Key}, {Min(top[i].Value)}");
                foreach (var m in day.Moments.GetValueOrDefault(top[i].Key) ?? []) {
                    var quest = string.IsNullOrEmpty(m.Quest) ? "" : $" (quest \"{UltimusText.Short(m.Quest, 50)}\")";
                    text.AppendLine($"     {m.At}, {m.Minutes:0.#} min in {UltimusText.Zone(m.Zone)}, on \"{UltimusText.Short(m.Step, 70)}\"{quest}");
                }
            }
        }

        text.AppendLine();
        text.AppendLine($"LAST {CAUSE_DAYS_KEPT} DAYS (played, lost, biggest cause of lost time)");
        foreach (var (date, day) in days.OrderByDescending(d => d.Key, StringComparer.Ordinal)) {
            var total = day.Seconds.Values.Sum();
            if (total <= 0) {
                continue;
            }

            var lost = day.Seconds.Where(s => IsLost(s.Key)).Sum(s => s.Value);
            var worst = day.Seconds.Where(s => IsLost(s.Key)).OrderByDescending(s => s.Value).FirstOrDefault();
            text.AppendLine($"  {date}  played {Min(total),7}  lost {Min(lost),7} ({lost * 100 / total,3:0}%)  {(worst.Key is null ? "" : $"{worst.Key} {Min(worst.Value)}")}");
        }

        return text.ToString();
    }
}
