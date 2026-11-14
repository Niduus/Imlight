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
using System.Text;
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// How Ultimus plans (Niduus, 2026-11-14, "All" to the brain upgrades, and "I need more logic, reason and tactics
/// documented"):
/// <list type="bullet">
/// <item>Goals stack: the world's story he is finishing, the quest, its step and his next move, said in his thoughts with
/// each new step, so it is always clear why he does what he does.</item>
/// <item>Confidence: each plan gets a score out of 100 from what he knows (the creature, the spot, his mood, his health).
/// Under the playbook's Confidence.Careful he does it the careful way: full health first, the map's way and no
/// shortcut by sight.</item>
/// <item>Predict then check: before a move he says what he expects (where an exit takes him, how a fight goes). When
/// something else happens it is a surprise: a lesson in the mistakes book and the bots' shared brain.</item>
/// <item>Mood (UltimusMood): careful after a lost fight, many stuck spots or surprises; bold after a winning streak.</item>
/// <item>Daily review: once a day (and when he stops) he reads his day, writes "today I learned" in learned-today.txt and
/// changes a few of his playbook numbers, only inside the limits in self-tuning.txt.</item>
/// </list>
/// </summary>
internal sealed partial class UltimusDirectorActor {

    // ---------------------------------------------------------------------
    // Goals stack
    // ---------------------------------------------------------------------

    private void ThinkGoals(UltimusMember.Status leader, UltimusPlanner.Target target, int confidence) {
        // His quest even while the move itself is not a quest step (a heal, a shop, a way there; audit A8).
        var questName = string.IsNullOrEmpty(target.Quest) ? _currentQuest : target.Quest;
        var quest = string.IsNullOrEmpty(questName) ? null : QuestTemplateCollection.GetQuestByName(questName);
        var step = leader is null ? "" : SocialSelf(leader).Step ?? "";
        var world = WorldOf(leader.Zone);
        var text = new StringBuilder($"My goals: (1) finish {world}'s story; ");
        text.Append(quest is null ? "(2) no quest this moment; " : $"(2) the quest {UltimusText.Title(questName)}; ");
        text.Append(step.Length > 0 ? $"(3) its step: {step}; " : "(3) its next step: being worked out; ");
        text.Append($"(4) my next move: {Describe(target)}. I trust this plan {confidence}/100.");
        Think(text.ToString(), "goals|" + target.Key);
    }

    private static string WorldOf(string zone) {
        var world = (zone ?? "").Split('/')[0];

        return world switch {
            "WizardCity" => "Wizard City",
            "Krokotopia" => "Krokotopia",
            "Marleybone" => "Marleybone",
            "MooShu" => "MooShu",
            "DragonSpire" => "Dragonspyre",
            "Celestia" => "Celestia",
            "Zafaria" => "Zafaria",
            "Avalon" => "Avalon",
            "Azteca" => "Azteca",
            "Khrysalis" => "Khrysalis",
            "Polaris" => "Polaris",
            "Mirage" => "Mirage",
            "Empyrea" => "Empyrea",
            "Karamelle" => "Karamelle",
            "Lemuria" => "Lemuria",
            "" => "this world",
            _ => world,
        };
    }

    // ---------------------------------------------------------------------
    // Confidence
    // ---------------------------------------------------------------------

    // The plan of the step he is on now done the careful way (low confidence).
    private bool _carefulPlan;

    /// <summary>How much he trusts a plan, out of 100, and the reasons that moved it.</summary>
    private (int Score, List<string> Reasons) Confidence(UltimusMember.Status leader, UltimusPlanner.Target target) {
        var score = 75;
        var reasons = new List<string>();
        void Move(int by, string why) {
            score += by;
            reasons.Add($"{why} ({(by > 0 ? "+" : "")}{by})");
        }

        if (target.Kind == UltimusPlanner.FIGHT) {
            var template = (uint) Math.Min(target.Template, uint.MaxValue);
            var foe = UltimusFoes.Of(template);
            var habit = BotKnowledge.Of(template);
            if (foe is null) {
                Move(-10, "I never fought it");
            }
            else if (foe.Lost > 0) {
                Move(-25, $"it beat me {foe.Lost} time(s)");
            }
            else if (foe.Tough) {
                Move(-15, $"it takes about {foe.AverageHealthLost:P0} of my health");
            }
            else if (foe.Won >= 2) {
                Move(+15, $"I beat it {foe.Won} times");
            }

            if (habit is { Casts: >= 5 }) {
                Move(+5, "the shared brain knows its habits");
            }
        }

        if (BotMistakes.Near(BotMistakes.STUCK, leader.Zone, target.X, target.Y) is { } stuck) {
            Move(stuck.Times >= 3 ? -25 : -10, $"bots got stuck by there {stuck.Times} time(s)" + (stuck.Worked.Length > 0 ? $", and \"{stuck.Worked}\" worked" : ""));
        }

        if (BotMistakes.Near(BotMistakes.NO_WAY, leader.Zone, target.X, target.Y) is not null) {
            Move(-10, "the map has no walk there");
        }

        if (BotMistakes.Near(BotMistakes.SURPRISE, leader.Zone, target.X, target.Y) is { } surprise) {
            Move(-10, $"something surprised a bot there before ({UltimusText_Short(surprise.What)})");
        }

        switch (UltimusMood.Now) {
            case UltimusMood.Level.Careful:
                Move(-10, "I am careful now");
                break;
            case UltimusMood.Level.Bold:
                Move(+5, "I am on a winning streak");
                break;
        }

        if (HealthShare(leader) < UltimusPlaybook.Value("Quest.ReadyHealth")) {
            Move(-10, "I am hurt");
        }

        return (Math.Clamp(score, 5, 95), reasons);
    }

    private static string UltimusText_Short(string text) => UltimusText.Short(text ?? "", 80);

    private void ThinkConfidence(UltimusMember.Status leader, UltimusPlanner.Target target, int score, List<string> reasons) {
        var careful = UltimusPlaybook.Value("Confidence.Careful");
        var sure = UltimusPlaybook.Value("Confidence.Sure");
        _carefulPlan = score < careful;
        var why = reasons.Count == 0 ? "nothing I know says otherwise" : string.Join(", ", reasons);
        var plan = score < careful
            ? $"That is under {careful:0} ({UltimusPlaybook.Cite("Confidence.Careful")}), so I do it the careful way: full health and mana first, the map's way, no shortcuts."
            : score >= sure
                ? "I am sure of it, so I go at once."
                : "I go, and watch how it goes.";
        Think($"I trust \"{Describe(target)}\" {score}/100: {why}. {plan}", $"confidence|{target.Key}|{score / 10}");
    }

    // ---------------------------------------------------------------------
    // Predict then check
    // ---------------------------------------------------------------------

    private sealed record Prediction(string Kind, string Key, string Expect, string Zone, double Health, DateTime At, float X, float Y, string AtZone);

    private Prediction _prediction;

    private void Predict(UltimusMember.Status leader, UltimusPlanner.Target target) {
        switch (target.Kind) {
            case UltimusPlanner.EXIT or UltimusPlanner.DOOR or UltimusPlanner.SPIRAL when !string.IsNullOrEmpty(target.To):
                _prediction = new("travel", target.Key, $"\"{Describe(target)}\" takes me to {UltimusText.Zone(target.To)}", target.To, 0, DateTime.UtcNow,
                    target.X, target.Y, leader.Zone ?? "");
                Think($"I expect {_prediction.Expect}.", "expect|" + target.Key);
                break;
            case UltimusPlanner.FIGHT: {
                var foe = UltimusFoes.Of((uint) Math.Min(target.Template, uint.MaxValue));
                var loss = foe is { Fought: > 0 } ? foe.AverageHealthLost : 0.3;
                _prediction = new("fight", target.Key, $"I beat {FoeName(target)} and lose about {loss:P0} of my health", "", loss, DateTime.UtcNow,
                    target.X, target.Y, leader.Zone ?? "");
                Think($"I expect: {_prediction.Expect}" + (foe is null ? " (a guess: I never fought it)." : $" (what I remember of {foe.Fought} fight(s))."),
                    "expect|" + target.Key);
                break;
            }
        }
    }

    /// <summary>He arrived in an area: was it where he expected?</summary>
    private void CheckArrival(UltimusMember.Status leader) {
        if (_prediction is not { Kind: "travel" } expected || DateTime.UtcNow - expected.At > TimeSpan.FromMinutes(15)) {
            return;
        }

        _prediction = null;
        if (OverlayQuestTracker.SameZone(expected.Zone, leader.Zone) || OverlayQuestTracker.SameZone(leader.Zone, expected.Zone)) {
            Think($"As I expected, {expected.Expect}.", "expected|" + expected.Key);
            // An area reached on the way to the story is progress (the guardrails' off-track alarm).
            StoryProgress();

            return;
        }

        Surprise(leader, expected, $"I expected {expected.Expect}, but I am in {UltimusText.Zone(leader.Zone)}.",
            $"This way goes to {UltimusText.Zone(leader.Zone)}, not {UltimusText.Zone(expected.Zone)}: plan from where it really goes.");
    }

    /// <summary>A fight ended: did it go as he expected?</summary>
    private void CheckFight(UltimusMember.Status leader, bool won, double lostShare, string foeName) {
        if (_prediction is not { Kind: "fight" } expected) {
            return;
        }

        _prediction = null;
        if (!won) {
            Surprise(leader, expected, $"I expected that {expected.Expect}, but I lost to {foeName}.",
                $"{foeName} is stronger than it looks: go in at full health with a deck made for it, or level up first.");
        }
        else if (lostShare > expected.Health + 0.25) {
            Surprise(leader, expected, $"I beat {foeName}, but it took {lostShare:P0} of my health, not about {expected.Health:P0} as I expected.",
                $"{foeName} hits harder than it looks: rest before it.");
        }
        else {
            Think($"As I expected, I beat {foeName} and lost {lostShare:P0} of my health.", "expected|" + expected.Key);
        }
    }

    private void Surprise(UltimusMember.Status leader, Prediction expected, string what, string lesson) {
        UltimusMood.Saw(UltimusMood.SURPRISE);
        Think($"That surprised me. {what} I write it down as a lesson: {lesson}", "surprise|" + expected.Key);
        BotMistakes.Record("Ultimus", BotMistakes.SURPRISE, expected.AtZone, expected.X, expected.Y, _target?.What ?? "", what,
            $"Before acting I expected that {expected.Expect}.", lesson);
        BotKnowledge.Learn("Ultimus", "surprise", $"{what} Lesson: {lesson}");
    }

    // ---------------------------------------------------------------------
    // Mood
    // ---------------------------------------------------------------------

    private UltimusMood.Level _moodSaid = UltimusMood.Level.Steady;

    private void ThinkMood() {
        var now = UltimusMood.Now;
        if (now == _moodSaid) {
            return;
        }

        _moodSaid = now;
        Think($"My mood is now {now.ToString().ToLowerInvariant()}. {UltimusMood.Why()}.", "mood|" + now);
    }

    // ---------------------------------------------------------------------
    // Daily review ("today I learned") and self-tuning
    // ---------------------------------------------------------------------

    private const string LIMITS_FILE = "self-tuning.txt";
    private const string LEARNED_TODAY_FILE = "learned-today.txt";
    private const string REVIEW_FILE = "review-last.txt";

    // What happened since the last review.
    private readonly List<(DateTime At, string Foe, bool Won, double Lost)> _dayFights = [];
    private int _dayStuck;
    private DateTime _reviewCheckedAt = DateTime.MinValue;

    private void NoteDayFight(string foe, bool won, double lost) {
        _dayFights.Add((DateTime.UtcNow, foe, won, lost));
        UltimusMood.Saw(won ? UltimusMood.WON : UltimusMood.LOST);
    }

    private void NoteDayStuck() {
        _dayStuck++;
        UltimusMood.Saw(UltimusMood.STUCK);
    }

    /// <summary>Once a day (after midnight, his PC's time), or when he stops with a day's play to look back on.</summary>
    private void MaybeReview(bool stopping = false) {
        if (!stopping && DateTime.UtcNow - _reviewCheckedAt < TimeSpan.FromMinutes(5)) {
            return;
        }

        _reviewCheckedAt = DateTime.UtcNow;
        var last = LastReview();
        var due = stopping
            ? DateTime.Now - last > TimeSpan.FromHours(1) && (_dayFights.Count > 0 || _dayStuck > 0)
            : DateTime.Now.Date > last.Date && (_dayFights.Count > 0 || _dayStuck > 0);
        if (due) {
            Review();
        }
    }

    private static DateTime LastReview() {
        try {
            var path = Path.Combine(UltimusWire.Folder, REVIEW_FILE);
            if (File.Exists(path) && DateTime.TryParse(File.ReadAllText(path).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) {
                return at;
            }

            Directory.CreateDirectory(UltimusWire.Folder);
            File.WriteAllText(path, DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Looked at again in five minutes.
        }

        return DateTime.Now;
    }

    private void Review() {
        var lines = new List<string>();
        var won = _dayFights.Count(f => f.Won);
        var lost = _dayFights.Count(f => !f.Won);
        var heavy = _dayFights.Where(f => f.Won && f.Lost >= 0.5).ToList();
        lines.Add($"I fought {_dayFights.Count} times: won {won}, lost {lost}. I got stuck {_dayStuck} times.");
        foreach (var fight in _dayFights.Where(f => !f.Won).GroupBy(f => f.Foe)) {
            lines.Add($"{fight.Key} beat me {fight.Count()} time(s): I go in at full health against it now.");
        }

        foreach (var fight in heavy.GroupBy(f => f.Foe)) {
            lines.Add($"{fight.Key} took half my health or more {fight.Count()} time(s).");
        }

        foreach (var lesson in BotKnowledge.Since(DateTime.UtcNow.AddDays(-1)).Where(l => l.Topic == "surprise").Take(5)) {
            lines.Add($"Surprise: {lesson.Text}");
        }

        // His numbers, changed only inside the limits Niduus sets (self-tuning.txt).
        var limits = Limits();
        if (limits.Auto) {
            if (lost > 0) {
                Tune(limits, "Quest.ReadyHealth", +0.05, $"I lost {lost} fight(s) today, so I start steps with more health", lines);
            }
            else if (won >= 10 && heavy.Count == 0) {
                Tune(limits, "Quest.ReadyHealth", -0.05, $"I won all {won} fights today and none took half my health, so I rest a little less", lines);
            }

            if (heavy.Count >= 3) {
                Tune(limits, "Battle.HealAt", +0.05, $"{heavy.Count} fights took half my health or more, so I heal sooner in battle", lines);
            }
            else if (won >= 15 && lost == 0) {
                Tune(limits, "Battle.HealAt", -0.05, $"{won} fights won and none lost, so I heal a little later and hit more", lines);
            }

            if (_dayStuck >= 15) {
                Tune(limits, "Confidence.Careful", +5, $"I got stuck {_dayStuck} times today, so I do more plans the careful way", lines);
            }
        }
        else {
            lines.Add("Self-tuning is off (self-tuning.txt), so I leave my numbers as they are.");
        }

        // Old self-made rules fade back toward their start, and the day's health check (UltimusDirector.Safety.cs).
        FadeOldTunes(limits, lines);
        HealthCheck(won, lost, lines);

        var text = new StringBuilder();
        text.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm}: today I learned ===");
        foreach (var line in lines) {
            text.AppendLine("- " + line);
        }

        text.AppendLine();
        try {
            Directory.CreateDirectory(UltimusWire.Folder);
            File.AppendAllText(Path.Combine(UltimusWire.Folder, LEARNED_TODAY_FILE), text.ToString());
            File.WriteAllText(Path.Combine(UltimusWire.Folder, REVIEW_FILE), DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Ultimus: {0} could not be written: {1}", Logger.Args(LEARNED_TODAY_FILE, ex.Message));
        }

        Think("Today I learned: " + string.Join(" ", lines), "review|" + DateTime.Now.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        BotKnowledge.Learn("Ultimus", "today I learned", string.Join(" ", lines));
        _dayFights.Clear();
        _dayStuck = 0;
    }

    private void Tune((bool Auto, Dictionary<string, (double Low, double High)> Ranges) limits, string name, double by, string because, List<string> lines) {
        if (!limits.Ranges.TryGetValue(name, out var range)) {
            return;
        }

        // The guardrails (Niduus, 2026-11-14): a hard limit is his alone to change, and a rule locked in Ahziael is left as it is.
        if (UltimusMission.HardLimit(name) || s_locked.Contains(name)) {
            lines.Add($"I would change {name} ({because}), but it is " + (UltimusMission.HardLimit(name) ? "a hard limit only Niduus changes." : "locked in Ahziael."));

            return;
        }

        var now = UltimusPlaybook.Value(name);
        var next = Math.Clamp(Math.Round(now + by, 3), range.Low, range.High);
        if (Math.Abs(next - now) < 0.0001) {
            lines.Add($"I would change {name}, but it is at its limit ({range.Low:0.##} to {range.High:0.##}).");

            return;
        }

        // A lesson must show up on two reviews in a row before it becomes a rule (UltimusDirector.Safety.cs).
        if (!ConfirmedTune(name, Math.Sign(by), because, lines)) {
            return;
        }

        if (UltimusPlaybook.Set(name, next, because)) {
            lines.Add($"I changed {name} from {now:0.##} to {next:0.##}: {because}.");
            Note("Ultimus", $"changes its playbook: {name} {now:0.##} -> {next:0.##} ({because}).", UltimusLink.COMMAND);
        }
    }

    // Rules locked in Ahziael ("Locked = a, b" in self-tuning.txt): his daily review leaves them alone.
    private static HashSet<string> s_locked = new(StringComparer.OrdinalIgnoreCase);

    // The limits Niduus sets for the numbers Ultimus may change himself: written with safe ones the first time.
    private static (bool Auto, Dictionary<string, (double Low, double High)> Ranges) Limits() {
        var ranges = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);
        var auto = true;
        try {
            var path = Path.Combine(UltimusWire.Folder, LIMITS_FILE);
            if (!File.Exists(path)) {
                Directory.CreateDirectory(UltimusWire.Folder);
                File.WriteAllText(path, string.Join(Environment.NewLine, [
                    "# What Ultimus may change in his own playbook at his daily review, and how far.",
                    "# Each line: the rule = the lowest to the highest he may set it. Delete a line to keep him off that rule.",
                    "# Auto = off stops him changing anything (he still writes \"today I learned\").",
                    "# Locked = rules he must leave alone, by name, split by commas (Ahziael's locks write this line).",
                    "Auto = on",
                    "Quest.ReadyHealth = 0.6 to 0.95",
                    "Battle.HealAt = 0.35 to 0.7",
                    "Confidence.Careful = 30 to 70",
                    "",
                ]));
            }

            s_locked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in File.ReadAllLines(path)) {
                var line = raw.Trim();
                var eq = line.IndexOf('=');
                if (line.StartsWith('#') || eq <= 0) {
                    continue;
                }

                var name = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();
                if (name.Equals("Locked", StringComparison.OrdinalIgnoreCase)) {
                    s_locked = new HashSet<string>(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
                    continue;
                }

                if (name.Equals("Auto", StringComparison.OrdinalIgnoreCase)) {
                    auto = !value.StartsWith("off", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("no", StringComparison.OrdinalIgnoreCase)
                           && value != "0";
                    continue;
                }

                var parts = value.Split(" to ", StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var low)
                                      && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var high)) {
                    ranges[name] = (Math.Min(low, high), Math.Max(low, high));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Ultimus: {0} could not be read: {1}", Logger.Args(LIMITS_FILE, ex.Message));
        }

        return (auto, ranges);
    }

}
