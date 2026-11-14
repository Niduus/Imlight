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
using System.Text;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Staying on the story and on the map (Niduus, 2026-11-14: "our boy is on a journey in Sinbad. WAIT. THAT'S NOT A MAIN
/// QUEST!!", "MAke sure he respects the world/games boundaries"), and the approved extras of that day:
///   Story areas only   an area off the story (an event, a gauntlet, a house, a side world) that his step does not name
///                      is left first, before anything else: the way out on foot, else Ahziael takes him back to the
///                      last story area he was in (an unstuck move). It is written in the mistakes book.
///   Bounds watchdog    off the walkable ground (in a wall, on the Minimap's black) for a few seconds: back to the last
///                      spot on the floor he stood on, written in the mistakes book.
///   Arrival check      after each zone change: the area he meant and the spot the zone transfer book says he lands on;
///                      a wrong area is backed out of the same way (once for each way, so it can't loop).
///   Time-waste map     where he loses the most time (standing still, getting unstuck, a step he can't work out), by
///                      area and spot: logs/ultimus/time-waste.txt.
///   Nightly coach      [Ultimus] AutoCoach (off by default, live): each night all his logs in a dated folder
///                      (logs/ultimus/coach/date), each on its own plus one joint file.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    // ---------------------------------------------------------------------
    // Story areas only
    // ---------------------------------------------------------------------

    private string _lastStoryZone;
    private (string Zone, DateTime Since)? _offStory;
    private DateTime _offStoryCarried = DateTime.MinValue;

    /// <summary>Whether his quest's steps name this area (an area off the story he may be in).</summary>
    private bool StepNamesZone(string zone) {
        var quest = _currentQuest ?? _target?.Quest;
        if (string.IsNullOrEmpty(quest) || QuestTemplateCollection.GetQuestByName(quest) is not { } template) {
            return false;
        }

        return (template.m_goals ?? []).Where(g => g is not null)
            .Any(g => OverlayQuestTracker.StepZone(g) is { Length: > 0 } stepZone
                      && (OverlayQuestTracker.SameZone(stepZone, zone) || OverlayQuestTracker.SameZone(zone, stepZone)));
    }

    /// <summary>
    /// He is in an area off the story his step does not name: the way out first (a player who walked into the wrong
    /// portal walks back out). Null when he is on the story.
    /// </summary>
    private UltimusPlanner.Target LeaveOffStory(UltimusMember.Status leader) {
        var zone = leader.Zone ?? "";
        if (zone.Length == 0 || leader.State != "playing") {
            return null;
        }

        if (OverlayQuestRoute.OnStory(zone)) {
            _lastStoryZone = zone;
            _offStory = null;
            return null;
        }

        if (StepNamesZone(zone)) {
            return null;
        }

        if (_offStory is not { } off || !string.Equals(off.Zone, zone, StringComparison.OrdinalIgnoreCase)) {
            _offStory = off = (zone, DateTime.UtcNow);
            var why = $"{UltimusText.Zone(zone)} is not on the main story and my quest does not send me here";
            Note("Ultimus", $"ABNORMAL: is in {UltimusText.Zone(zone)}, off the main story: {why}. Leaving it comes first.", UltimusLink.ABNORMAL);
            Think($"I am in {UltimusText.Zone(zone)}. That is not a main story area and my quest does not send me here, so I leave it before " +
                  "anything else and write it down so the way that brought me here is never taken again.", "offstory|" + zone);
            BotMistakes.Record("Ultimus", BotMistakes.OFF_STORY, zone, leader.X, leader.Y, _currentQuest ?? "",
                $"Ended up in {UltimusText.Zone(zone)}, an area off the main story (from {UltimusText.Zone(_lastStoryZone ?? "")}).",
                _lastReasoning, "The route finder never goes through an event, a gauntlet, a house or a side world.", "leave it first");
        }

        var back = _lastStoryZone ?? "WizardCity/WC_Hub";
        // On foot, as a player walks back out. Niduus, 2026-11-14: "I don't want Ahziael to move Ultimus. I want Ultimus to
        // search and find his way": with no way out on foot he uses the buttons a player has (the Commons button, his
        // marked spot), and is never carried.
        if (UltimusPlanner.Travel(leader, back, $"leaving {UltimusText.Zone(zone)}: not on the main story", "offstory") is { } walk) {
            return walk;
        }

        if (DateTime.UtcNow - _offStoryCarried > TimeSpan.FromSeconds(60) && !Unsticking(0)) {
            _offStoryCarried = DateTime.UtcNow;
            Note("Ultimus", $"ABNORMAL: found no way out of {UltimusText.Zone(zone)} on foot: presses the Commons button to get back to the story, as a player does.",
                UltimusLink.ABNORMAL);
            if (!PressHubButton(leader, $"to get out of {UltimusText.Zone(zone)}")) {
                Aside(0, leader, 600f, 1600f, "looks around the area for a way out");
            }
        }

        return new UltimusPlanner.Target(UltimusPlanner.WAIT, leader.X, leader.Y, $"leaving {UltimusText.Zone(zone)}", Key: "offstory|wait");
    }

    // ---------------------------------------------------------------------
    // Arrival check: the area he meant, and the landing the zone transfer book says
    // ---------------------------------------------------------------------

    private readonly HashSet<string> _backedOut = new(StringComparer.OrdinalIgnoreCase);
    private (string To, string Wrong, DateTime At)? _backOut;

    /// <summary>
    /// A zone change: did he land where the book says? And when the way went to another area than he meant, back out the
    /// same way (once for each way and area: a second time he plans on from where he is).
    /// </summary>
    private void CheckLanding(string fromZone, float fromX, float fromY, UltimusMember.Status leader, string meant) {
        if (string.IsNullOrEmpty(fromZone) || string.IsNullOrEmpty(leader.Zone)) {
            return;
        }

        if (UltimusTransfers.Expected(fromZone, leader.Zone, fromX, fromY) is { } expected) {
            var off = UltimusPlanner.Far(expected.LandX, expected.LandY, leader.X, leader.Y);
            if (off > 600) {
                Think($"The zone transfer book says this way lands at ({expected.LandX:0}, {expected.LandY:0}, {expected.LandZ:0}) in " +
                      $"{UltimusText.Zone(leader.Zone)}, but I am at ({leader.X:0}, {leader.Y:0}), {off:0} away. I note it.", "landing|" + leader.Zone);
                BotMistakes.Record("Ultimus", BotMistakes.SURPRISE, leader.Zone, leader.X, leader.Y, meant ?? "",
                    $"Landed {off:0} away from where the zone transfer book says ({expected.Loc}) coming from {UltimusText.Zone(fromZone)}.",
                    "Each zone change is checked against the book.", "The book's landing spot or the game's needs a look.");
            }
        }

        if (string.IsNullOrEmpty(meant) || OverlayQuestTracker.SameZone(meant, leader.Zone) || OverlayQuestTracker.SameZone(leader.Zone, meant)) {
            return;
        }

        var key = fromZone + ">" + leader.Zone;
        if (!_backedOut.Add(key)) {
            return;
        }

        _backOut = (fromZone, leader.Zone, DateTime.UtcNow);
        Note("Ultimus", $"meant to go to {UltimusText.Zone(meant)} but is in {UltimusText.Zone(leader.Zone)}: backs out the same way to {UltimusText.Zone(fromZone)}.",
            UltimusLink.ABNORMAL);
    }

    /// <summary>Backing out of an area the way went to by mistake: the way back to where he came from.</summary>
    private UltimusPlanner.Target BackOut(UltimusMember.Status leader) {
        if (_backOut is not { } back) {
            return null;
        }

        if (!string.Equals(leader.Zone, back.Wrong, StringComparison.OrdinalIgnoreCase) || DateTime.UtcNow - back.At > TimeSpan.FromMinutes(3)) {
            _backOut = null;
            return null;
        }

        return UltimusPlanner.Travel(leader, back.To, $"backing out of {UltimusText.Zone(back.Wrong)}", "backout");
    }

    // ---------------------------------------------------------------------
    // Bounds watchdog
    // ---------------------------------------------------------------------

    private (string Zone, float X, float Y, float Z)? _legalSpot;
    private DateTime? _offFloorSince;
    private DateTime _boundsFixedAt = DateTime.MinValue;

    private void BoundsTick(UltimusMember.Status leader) {
        if (string.IsNullOrEmpty(leader.Zone) || leader.State != "playing" || CombatDuelComponent.IsInBattle(leader.GlobalId)
            || DateTime.UtcNow - leader.AttachedAt < TimeSpan.FromSeconds(6)) {
            _offFloorSince = null;
            return;
        }

        if (OverlayQuestPath.OnFloor(leader.Zone, leader.X, leader.Y)) {
            _legalSpot = (leader.Zone, leader.X, leader.Y, leader.Z);
            _offFloorSince = null;
            return;
        }

        // Just by the floor (a doorway, the grid is coarse): not out of bounds.
        if (OverlayQuestPath.NearestFloor(leader.Zone, leader.X, leader.Y) is { } near && UltimusPlanner.Far(near.X, near.Y, leader.X, leader.Y) < 90) {
            _offFloorSince = null;
            return;
        }

        _offFloorSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - _offFloorSince < TimeSpan.FromSeconds(3) || DateTime.UtcNow - _boundsFixedAt < TimeSpan.FromSeconds(20)) {
            return;
        }

        _boundsFixedAt = DateTime.UtcNow;
        _offFloorSince = null;
        var where = $"{UltimusText.Zone(leader.Zone)} at ({leader.X:0}, {leader.Y:0})";
        if (_legalSpot is not { } spot || !string.Equals(spot.Zone, leader.Zone, StringComparison.OrdinalIgnoreCase)) {
            Note("Ultimus", $"ABNORMAL: is off the walkable ground in {where} and has no spot on the floor here to go back to.", UltimusLink.ABNORMAL);
            return;
        }

        Note("Ultimus", $"ABNORMAL: OUT OF BOUNDS in {where} (off the walkable ground): walks back to ({spot.X:0}, {spot.Y:0}), the last spot on the floor he stood on.",
            UltimusLink.ABNORMAL);
        Think("I am off the walkable ground, where no player can stand. I go back to the last spot on the floor I stood on and write it down.",
            "bounds|" + leader.Zone);
        BotMistakes.Record("Ultimus", BotMistakes.OUT_OF_BOUNDS, leader.Zone, leader.X, leader.Y, _target?.What ?? _doing ?? "",
            $"Walked off the walkable ground in {where}; walked back to ({spot.X:0}, {spot.Y:0}).", _lastReasoning,
            "Every walk is checked against the floor; this spot's walk data or the step's spot needs a look.", "walk back");
        // He walks back himself (Niduus, 2026-11-14: Ahziael never moves him).
        OverlayQuestPath.Forget(leader.CharId);
        _actors[0].Tell(new UltimusMember.WalkTo([(spot.X, spot.Y)], "walks back onto the walkable ground"));
    }

    // ---------------------------------------------------------------------
    // Time-waste map
    // ---------------------------------------------------------------------

    private const string WASTE_FILE = "time-waste.txt";
    private const string WASTE_DATA = "time-waste.json";
    private const float WASTE_CELL = 500f;
    private Dictionary<string, WasteSpot> _waste;
    private DateTime _wasteTick = DateTime.MinValue;
    private DateTime _wasteWritten = DateTime.UtcNow;
    private (float X, float Y, DateTime At) _wasteStill = (0, 0, DateTime.UtcNow);

    internal sealed class WasteSpot {
        public string Zone { get; set; } = "";
        public float X { get; set; }
        public float Y { get; set; }
        public double Seconds { get; set; }
        public Dictionary<string, double> Why { get; set; } = [];
        public string LastStep { get; set; } = "";
    }

    private void WasteTick(UltimusMember.Status leader) {
        var now = DateTime.UtcNow;
        var dt = (now - _wasteTick).TotalSeconds;
        _wasteTick = now;
        if (dt <= 0 || dt > 10 || string.IsNullOrEmpty(leader.Zone) || leader.State != "playing" || CombatDuelComponent.IsInBattle(leader.GlobalId)
            || leader.Talking) {
            return;
        }

        if (UltimusPlanner.Far(leader.X, leader.Y, _wasteStill.X, _wasteStill.Y) > 60) {
            _wasteStill = (leader.X, leader.Y, now);
        }

        string why = null;
        if (Unsticking(0)) {
            why = "getting unstuck";
        }
        else if (_target?.Kind == UltimusPlanner.NONE) {
            why = "a step he can't work out";
        }
        else if (now - _wasteStill.At > TimeSpan.FromSeconds(15) && !Pausing()) {
            why = "standing still";
        }

        if (why is null) {
            return;
        }

        _waste ??= ReadWaste();
        var (cx, cy) = ((int) MathF.Floor(leader.X / WASTE_CELL), (int) MathF.Floor(leader.Y / WASTE_CELL));
        var key = $"{leader.Zone}|{cx}|{cy}";
        if (!_waste.TryGetValue(key, out var spot)) {
            _waste[key] = spot = new WasteSpot { Zone = leader.Zone, X = (cx + 0.5f) * WASTE_CELL, Y = (cy + 0.5f) * WASTE_CELL };
        }

        spot.Seconds += dt;
        spot.Why[why] = spot.Why.GetValueOrDefault(why) + dt;
        spot.LastStep = _target?.What ?? _doing ?? "";
        if (now - _wasteWritten > TimeSpan.FromMinutes(5)) {
            _wasteWritten = now;
            WriteWaste();
        }
    }

    private static Dictionary<string, WasteSpot> ReadWaste() {
        try {
            var path = Path.Combine(UltimusWire.Folder, WASTE_DATA);
            if (File.Exists(path) && UltimusWire.Read<Dictionary<string, WasteSpot>>(File.ReadAllText(path)) is { } read) {
                return new Dictionary<string, WasteSpot>(read, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) {
            Logger.Debug("Ultimus: {0} could not be read: {1}", Logger.Args(WASTE_DATA, ex.Message));
        }

        return new Dictionary<string, WasteSpot>(StringComparer.OrdinalIgnoreCase);
    }

    private void WriteWaste() {
        if (_waste is null) {
            return;
        }

        try {
            Directory.CreateDirectory(UltimusWire.Folder);
            File.WriteAllText(Path.Combine(UltimusWire.Folder, WASTE_DATA), UltimusWire.Write(_waste));
            var text = new StringBuilder();
            text.AppendLine($"WHERE ULTIMUS LOSES THE MOST TIME (written {DateTime.Now:yyyy-MM-dd HH:mm})");
            text.AppendLine("Time spent standing still, getting unstuck or on a step he can't work out, by area and spot (500 x 500), most first.");
            text.AppendLine();
            text.AppendLine("BY AREA");
            foreach (var area in _waste.Values.GroupBy(w => w.Zone, StringComparer.OrdinalIgnoreCase)
                         .Select(g => (Zone: g.Key, Minutes: g.Sum(w => w.Seconds) / 60)).OrderByDescending(a => a.Minutes).Take(15)) {
                text.AppendLine($"  {area.Minutes,7:0.0} min  {UltimusText.Zone(area.Zone)} ({area.Zone})");
            }

            text.AppendLine();
            text.AppendLine("BY SPOT");
            foreach (var spot in _waste.Values.OrderByDescending(w => w.Seconds).Take(30)) {
                var whys = string.Join(", ", spot.Why.OrderByDescending(w => w.Value).Select(w => $"{w.Key} {w.Value / 60:0.0} min"));
                text.AppendLine($"  {spot.Seconds / 60,7:0.0} min  {UltimusText.Zone(spot.Zone)} near ({spot.X:0}, {spot.Y:0}): {whys}; last step: {spot.LastStep}");
            }

            File.WriteAllText(Path.Combine(UltimusWire.Folder, WASTE_FILE), text.ToString());
        }
        catch (Exception ex) {
            Logger.Debug("Ultimus: {0} could not be written: {1}", Logger.Args(WASTE_FILE, ex.Message));
        }
    }

    // ---------------------------------------------------------------------
    // Nightly auto-coach (off by default)
    // ---------------------------------------------------------------------

    private static bool AutoCoachOn => ConfigurationManager.GetValue("Ultimus.AutoCoach", false);
    private DateTime _coachCheck = DateTime.MinValue;
    private const int COACH_HOUR = 4;
    private const int COACH_KEEP = 14;

    private void MaybeAutoCoach() {
        if (DateTime.UtcNow - _coachCheck < TimeSpan.FromMinutes(5)) {
            return;
        }

        _coachCheck = DateTime.UtcNow;
        if (!AutoCoachOn || DateTime.Now.Hour < COACH_HOUR) {
            return;
        }

        try {
            var root = Path.Combine(UltimusWire.Folder, "coach");
            var folder = Path.Combine(root, DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            if (Directory.Exists(folder)) {
                return;
            }

            Directory.CreateDirectory(folder);
            WriteWaste();
            var files = new List<string>();
            files.AddRange(new DirectoryInfo(UltimusWire.Folder).EnumerateFiles()
                .Where(f => f.Extension is ".txt" or ".json" or ".jsonl").OrderByDescending(f => f.LastWriteTimeUtc).Select(f => f.FullName));
            var bots = Path.GetFullPath(BotMistakes.FOLDER);
            if (Directory.Exists(bots)) {
                files.AddRange(new DirectoryInfo(bots).EnumerateFiles().Where(f => f.Extension is ".txt" or ".json").Select(f => f.FullName));
            }

            var joint = new StringBuilder();
            joint.AppendLine($"All of Ultimus's logs, saved by the nightly auto-coach {DateTime.Now:yyyy-MM-dd HH:mm}. Each line starts with its file.");
            foreach (var path in files) {
                var name = (Path.GetDirectoryName(path) ?? "").EndsWith("bots", StringComparison.OrdinalIgnoreCase) ? "bots-" + Path.GetFileName(path) : Path.GetFileName(path);
                string body;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream)) {
                    body = reader.ReadToEnd();
                }

                File.WriteAllText(Path.Combine(folder, name), body);
                joint.AppendLine().AppendLine($"===== [{name}] =====");
                foreach (var line in body.Split('\n')) {
                    joint.Append("    [").Append(name).Append("] ").AppendLine(line.TrimEnd('\r'));
                }
            }

            File.WriteAllText(Path.Combine(folder, "ALL LOGS (joint file).txt"), joint.ToString());
            File.WriteAllText(Path.Combine(folder, "README.txt"),
                "Ultimus's logs, saved each night by the auto-coach ([Ultimus] AutoCoach). Attach this folder (or the joint file) in the chat.\n");
            foreach (var old in new DirectoryInfo(root).EnumerateDirectories().OrderByDescending(d => d.Name).Skip(COACH_KEEP)) {
                old.Delete(true);
            }

            Note("Ultimus", $"auto-coach: saved {files.Count} logs for the coach in {folder}.");
        }
        catch (Exception ex) {
            Logger.Debug("Ultimus: the nightly coach folder could not be written: {0}", Logger.Args(ex.Message));
        }
    }

}
