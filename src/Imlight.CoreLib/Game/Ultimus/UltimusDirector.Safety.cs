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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Tools;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Safety nets for playing the whole game on his own (Niduus, 2026-11-14: "expand more on this idea. Make safety nets for
/// this. I'm hoping to get him to automate the entire game soon"), all inside his rules (quests like a human, nobody moves
/// him or fights for him):
/// <list type="bullet">
/// <item>Loop guard: the same area or the same move coming back again and again with no story progress is a loop; he
/// breaks out with a new plan (that move set aside, the ways forgotten) and writes it down.</item>
/// <item>Park and ask: a spot where every fix was tried twice over is set aside for a while (other story work first), with
/// a "needs help" report and all his logs saved in logs/ultimus/needs-help/(time).</item>
/// <item>Lessons: a change to his own numbers needs two daily reviews in a row that say the same; a self-made number not
/// confirmed for a week fades halfway back to its start; Ahziael can undo them all.</item>
/// <item>A daily play limit ([Ultimus] MaxHoursPerDay, 0 = none): past it he rests until the next day.</item>
/// <item>A daily health check in his review and his diary.</item>
/// </list>
/// </summary>
internal sealed partial class UltimusDirectorActor {

    // ---------------------------------------------------------------------
    // Loop guard
    // ---------------------------------------------------------------------

    private static readonly TimeSpan s_loopWindow = TimeSpan.FromMinutes(20);
    private const int LOOP_REPEATS = 4;
    private readonly List<(DateTime At, string What)> _loopTrail = [];
    private string _loopZone = "";
    private DateTime _loopBrokeAt = DateTime.MinValue;
    private int _dayLoops;
    private int _dayParked;

    /// <summary>Each plan: a new area or a new move goes on the trail, and a loop on it is broken.</summary>
    private bool LoopWatch(UltimusMember.Status leader, UltimusPlanner.Target target, bool newMove) {
        var zone = leader.Zone ?? "";
        if (zone.Length > 0 && zone != _loopZone) {
            _loopZone = zone;
            _loopTrail.Add((DateTime.UtcNow, "area|" + zone));
        }

        if (newMove && target is not null && !string.IsNullOrEmpty(target.Key) && !target.Key.StartsWith(PARTY_KEY, StringComparison.Ordinal)) {
            _loopTrail.Add((DateTime.UtcNow, "move|" + target.Key));
        }

        _loopTrail.RemoveAll(t => DateTime.UtcNow - t.At > s_loopWindow);
        // Moving the story on is not a loop (a quest that sends him back and forth is fine while steps get done).
        if (DateTime.UtcNow - _storyProgressAt < TimeSpan.FromMinutes(10) || DateTime.UtcNow - _loopBrokeAt < TimeSpan.FromMinutes(5)) {
            return false;
        }

        var loop = _loopTrail.GroupBy(t => t.What).Where(g => g.Count() >= LOOP_REPEATS).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (loop is null) {
            return false;
        }

        BreakLoop(leader, loop.Key, loop.Count());
        return true;
    }

    private void BreakLoop(UltimusMember.Status leader, string what, int times) {
        _loopBrokeAt = DateTime.UtcNow;
        _dayLoops++;
        var isArea = what.StartsWith("area|", StringComparison.Ordinal);
        var name = what[(what.IndexOf('|') + 1)..];
        var said = isArea ? $"came back to {UltimusText.Zone(name)} {times} times" : $"picked the same move {times} times ({_target?.What ?? name})";
        var where = $"{UltimusText.Zone(leader.Zone)} at ({leader.X:0}, {leader.Y:0})";
        Note("Ultimus", $"LOOP: {said} in {(int) s_loopWindow.TotalMinutes} minutes with no story progress: breaks out with a new plan.", UltimusLink.ABNORMAL);
        Think($"I {said} in the last {(int) s_loopWindow.TotalMinutes} minutes and my story did not move. That is a loop, so I stop " +
              "doing the same thing: I set that move aside for a while, forget the ways I tried and plan again from here.", "loop|" + what);
        BotMistakes.Record("Ultimus", BotMistakes.STUCK, leader.Zone, leader.X, leader.Y, _currentQuest ?? "",
            $"Went in a loop: {said} with no story progress ({where}).", _lastReasoning,
            "The loop guard sets the move aside and plans again; the route or the step there needs a look.", "loop broken");
        Issue("team", _currentQuest, $"Ultimus went in a loop: he {said} with no story progress ({where}). He broke out with a new plan.",
            key: "loop|" + what, broken: false, level: UltimusLink.ABNORMAL);
        if (!isArea) {
            UltimusPlanner.SetAside(name, TimeSpan.FromMinutes(10));
        }

        _loopTrail.Clear();
        _sent.Remove(0);
        OverlayQuestPath.Forget(leader.CharId);
        _target = null;
        Replan();
    }

    // ---------------------------------------------------------------------
    // Park and ask for help
    // ---------------------------------------------------------------------

    private readonly Dictionary<string, DateTime> _parkedAt = [];

    /// <summary>
    /// Every fix was tried at this spot, twice over: the step is set aside for 30 minutes (other story work first, if
    /// any), a "needs help" report is made with all his logs, and he keeps trying after. Nothing is skipped.
    /// </summary>
    private void ParkStep(UltimusMember.Status member, string quest, string stepKey, string what, string where) {
        var key = stepKey ?? what ?? "";
        if (_parkedAt.TryGetValue(key, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(30)) {
            return;
        }

        _parkedAt[key] = DateTime.UtcNow;
        _parkedWhy[key] = (quest, what, where);
        // The fix he would suggest (Niduus, 2026-11-14, "suggest the likely fix"): from the shared quest diagnosis.
        var fix = SuggestFix(quest);
        _parkedFix[key] = fix;
        Announce(SAY_STUCK, $"I'm stuck on \"{UltimusText.Short(what, 60)}\". Anyone know how to do it?");
        _dayParked++;
        if (!string.IsNullOrEmpty(stepKey)) {
            UltimusPlanner.SetAside(stepKey, TimeSpan.FromMinutes(30));
        }

        var folder = SaveNeedsHelp($"Ultimus needs help: \"{what}\" for {UltimusText.Title(quest)} in {where}. He tried every way he knows, twice over." +
                                   $"\nSuggested fix: {fix}");
        Note("Ultimus", $"NEEDS HELP: tried every fix at {where} twice over for \"{what}\": sets the step aside for 30 minutes and does other story work if he has any. " +
                        (folder is null ? "" : $"His logs are saved in {Path.GetFileName(folder)} (needs-help folder)."), UltimusLink.CRITICAL);
        Think($"I tried every way I know at {where}, twice over, and \"{what}\" still does not work. I set it aside for half an hour, do " +
              "other story work if there is any, then try again. I wrote a \"needs help\" report so Niduus can look. " +
              $"My best guess at the fix: {fix}", "park|" + key);
        Issue("quest", quest, $"NEEDS HELP: Ultimus tried every fix he has at {where} twice over for \"{what}\". He set the step aside for 30 minutes " +
                              "and comes back to it after. His logs are in the needs-help folder. Suggested fix: " + fix,
            key: "needs-help|" + key, level: UltimusLink.CRITICAL);
        UltimusJournal.Write(UltimusJournal.STUCK, $"Set \"{UltimusText.Short(what, 60)}\" aside and asked for help: nothing I tried worked.",
            WorldOf(member.Zone), UltimusText.Zone(member.Zone), quest, -1);
    }

    private readonly Dictionary<string, string> _parkedFix = [];

    // The likely fix for a step that beat every unstick move, by what the diagnosis found most likely wrong. Words only:
    // he never applies a fix himself (he plays like a player).
    private string SuggestFix(string quest) {
        var d = Diagnose(quest);
        if (d is null || string.IsNullOrWhiteSpace(d.LikelyCause)) {
            return "no clear cause found; send the needs-help folder to Claude to look at the quest and his log together.";
        }

        var cause = d.LikelyCause.TrimEnd('.');
        return d.Category switch {
            "data" => $"fix the quest's data ({cause}): the quest file named in the report, or Create > Quest Editor.",
            "server" => $"a server fix ({cause}): a creature, object or person the step needs is missing or not answering; send the report to Claude.",
            "character" => $"something about Ultimus himself ({cause}): a level, item or earlier quest he still needs.",
            "route" => $"a way problem ({cause}): a door, teleporter or exit to the step's area does not take him there.",
            "setting" => $"a setting blocks it ({cause}): check that setting in Ahziael.",
            _ => $"most likely his own play ({cause}): his path, clicks or timing; send the report to Claude.",
        } + $" ({d.Confidence} confidence)";
    }

    // All his logs, saved for whoever helps (the newest 10 reports are kept).
    private string SaveNeedsHelp(string why) {
        try {
            var root = Path.Combine(UltimusWire.Folder, "needs-help");
            var folder = Path.Combine(root, DateTime.Now.ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            WriteCoachBundle(folder, why);
            foreach (var old in new DirectoryInfo(root).EnumerateDirectories().OrderByDescending(d => d.Name).Skip(10)) {
                old.Delete(true);
            }

            return folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Ultimus: the needs-help report could not be written: {0}", Logger.Args(ex.Message));
            return null;
        }
    }

    // ---------------------------------------------------------------------
    // Lessons: confirmed twice, fade, undo
    // ---------------------------------------------------------------------

    private const string PENDING_FILE = "tuning-pending.json";
    private const string UNDO_TUNING_FILE = "undo-tuning-request.txt";

    private sealed class Pending {
        public int Direction { get; set; }
        public DateTime Last { get; set; }
        public string Because { get; set; } = "";
    }

    /// <summary>
    /// Whether a change to one of his numbers is confirmed: the review before this one asked for the same change. The first
    /// time it is only noted ("if tomorrow says the same, I change it").
    /// </summary>
    private bool ConfirmedTune(string name, int direction, string because, List<string> lines) {
        var pending = ReadPending();
        if (pending.TryGetValue(name, out var before) && before.Direction == direction && DateTime.UtcNow - before.Last < TimeSpan.FromDays(3)) {
            pending.Remove(name);
            WritePending(pending);
            return true;
        }

        pending[name] = new Pending { Direction = direction, Last = DateTime.UtcNow, Because = because };
        WritePending(pending);
        lines.Add($"I'd change {name} ({because}), but one day is not enough: if my next review says the same, I change it.");

        return false;
    }

    /// <summary>A number he set himself and has not confirmed for a week goes halfway back to where it started.</summary>
    private void FadeOldTunes((bool Auto, Dictionary<string, (double Low, double High)> Ranges) limits, List<string> lines) {
        foreach (var (name, on) in UltimusPlaybook.Tuned()) {
            if (DateTime.Now - on < TimeSpan.FromDays(7) || UltimusPlaybook.Default(name) is not { } start
                || UltimusMission.HardLimit(name) || s_locked.Contains(name)) {
                continue;
            }

            var now = UltimusPlaybook.Value(name);
            var next = Math.Abs(now - start) < 0.02 ? start : Math.Round((now + start) / 2, 3);
            if (limits.Ranges.TryGetValue(name, out var range)) {
                next = Math.Clamp(next, range.Low, range.High);
            }

            if (Math.Abs(next - now) > 0.0001 && UltimusPlaybook.Set(name, next, "not confirmed for a week, so it fades back toward my starting value")) {
                lines.Add($"{name} faded from {now:0.##} to {next:0.##}: I set it a week ago and nothing has confirmed it since.");
            }
        }
    }

    /// <summary>Ahziael's "Undo his self-tuning": every number he changed himself goes back to its start.</summary>
    private void MaybeUndoTuning() {
        var request = Path.Combine(UltimusWire.Folder, UNDO_TUNING_FILE);
        if (!File.Exists(request)) {
            return;
        }

        try {
            File.Delete(request);
            File.Delete(Path.Combine(UltimusWire.Folder, PENDING_FILE));
        }
        catch (IOException) {
            return;
        }

        var undone = new List<string>();
        foreach (var (name, _) in UltimusPlaybook.Tuned()) {
            if (UltimusPlaybook.Default(name) is { } start && UltimusPlaybook.Set(name, start, "undone from Ahziael: back to my starting value")) {
                undone.Add(name);
            }
        }

        Note("Ultimus", $"UNDO SELF-TUNING (Ahziael): {(undone.Count == 0 ? "he had changed none of his numbers" : "back to their starting values: " + string.Join(", ", undone))}.",
            UltimusLink.COMMAND);
        Think("Niduus undid my self-tuning from Ahziael, so my numbers are back where they started and I learn them again.", "undo-tuning|" + DateTime.UtcNow.Ticks);
    }

    private static Dictionary<string, Pending> ReadPending() {
        try {
            var path = Path.Combine(UltimusWire.Folder, PENDING_FILE);
            if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, Pending>>(File.ReadAllText(path)) is { } read) {
                return read;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) {
            Logger.Debug("Ultimus: {0} could not be read: {1}", Logger.Args(PENDING_FILE, ex.Message));
        }

        return [];
    }

    private static void WritePending(Dictionary<string, Pending> pending) {
        try {
            Directory.CreateDirectory(UltimusWire.Folder);
            File.WriteAllText(Path.Combine(UltimusWire.Folder, PENDING_FILE), JsonSerializer.Serialize(pending, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Ultimus: {0} could not be written: {1}", Logger.Args(PENDING_FILE, ex.Message));
        }
    }

    // ---------------------------------------------------------------------
    // Daily play limit
    // ---------------------------------------------------------------------

    private const string PLAY_TIME_FILE = "play-time.txt";
    private DateTime _playDay = DateTime.MinValue;
    private double _playSeconds;
    private DateTime _playTick = DateTime.MinValue;
    private DateTime _playSaved = DateTime.MinValue;
    private bool _restSaid;

    private static double MaxHoursPerDay => Math.Clamp(ConfigurationManager.GetValue("Ultimus.MaxHoursPerDay", 0.0), 0, 24);

    /// <summary>Each plan: his play time today counted; past [Ultimus] MaxHoursPerDay he rests (true) until tomorrow.</summary>
    private bool RestingForTheDay(UltimusMember.Status leader) {
        var today = DateTime.Now.Date;
        if (_playDay != today) {
            (_playDay, _playSeconds, _restSaid) = (today, ReadPlayTime(today), false);
        }

        var now = DateTime.UtcNow;
        if (_playTick != DateTime.MinValue) {
            _playSeconds += Math.Min(5, (now - _playTick).TotalSeconds);
        }

        _playTick = now;
        if (now - _playSaved > TimeSpan.FromMinutes(1)) {
            _playSaved = now;
            try {
                Directory.CreateDirectory(UltimusWire.Folder);
                File.WriteAllText(Path.Combine(UltimusWire.Folder, PLAY_TIME_FILE),
                    $"{today:yyyy-MM-dd} {_playSeconds.ToString("0", CultureInfo.InvariantCulture)}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                Logger.Debug("Ultimus: {0} could not be written: {1}", Logger.Args(PLAY_TIME_FILE, ex.Message));
            }
        }

        var most = MaxHoursPerDay;
        if (most <= 0 || _playSeconds < most * 3600) {
            _restSaid = false;
            return false;
        }

        if (!_restSaid) {
            _restSaid = true;
            Note("Ultimus", $"DAILY LIMIT: played {most:0.#} hours today ([Ultimus] MaxHoursPerDay): rests until tomorrow.", UltimusLink.COMMAND);
            Think($"I've played my {most:0.#} hours for today, the limit Niduus set. I rest somewhere safe and carry on tomorrow.", "rest|" + today.Ticks);
            UltimusJournal.Write(UltimusJournal.QUEST, $"Played my {most:0.#} hours for the day and stopped to rest.", WorldOf(leader.Zone), UltimusText.Zone(leader.Zone));
        }

        _doing = $"resting: he played his {most:0.#} hours for today (daily limit)";
        _lastProgress = DateTime.UtcNow;
        StoryProgress();
        if (SafeSpot(leader) is { } safe) {
            WalkTo(0, safe);
        }
        else {
            StopWalking(0);
            Idle(true);
        }

        return true;
    }

    private static double ReadPlayTime(DateTime today) {
        try {
            var path = Path.Combine(UltimusWire.Folder, PLAY_TIME_FILE);
            if (File.Exists(path) && File.ReadAllText(path).Trim().Split(' ') is [var day, var seconds]
                && day == today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                && double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var played)) {
                return played;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Ultimus: {0} could not be read: {1}", Logger.Args(PLAY_TIME_FILE, ex.Message));
        }

        return 0;
    }

    // ---------------------------------------------------------------------
    // Daily health check
    // ---------------------------------------------------------------------

    private void HealthCheck(int won, int lost, List<string> lines) {
        var hours = _playSeconds / 3600;
        var health = $"Health check: played {hours:0.#} hours, won {won} fights and lost {lost}, got stuck {_dayStuck} times, " +
                     $"broke {_dayLoops} loop{(_dayLoops == 1 ? "" : "s")} and set {_dayParked} step{(_dayParked == 1 ? "" : "s")} aside to ask for help.";
        lines.Add(health);
        UltimusJournal.Write(UltimusJournal.HEALTH, health, "", "", "", _dayParked > 0 || _dayLoops > 2 ? -1 : 0);
        _dayLoops = 0;
        _dayParked = 0;
    }

}
