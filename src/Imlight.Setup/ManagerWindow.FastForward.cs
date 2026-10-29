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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The Fast Forward tab on the Progress page (Niduus, 2026-10-29: "if I want to be level 10, it does all the main quests
/// for me to get to level 10. If I want to complete wizard city, it will complete all wizard city quests for me (Choice
/// of Side or main) with rewards ... I don't want to bunker up XP such as getting to Khrysalis and I'm magically 180").
/// Pick a wizard and what to reach (a level, the end of a world or the start of one), main story only or with side
/// quests. The tab lays the quests out in story order (the Quest Testing list, built in), shows how many quests that is
/// and the level their XP adds up to (quest-xp.txt, written by the server), and on Fast forward writes the plan to
/// fast-forward/&lt;wizard id&gt;.txt and sends '.quest fastforward'. The server finishes them with their real rewards
/// (QuestService.FastForward) and writes what it did to fast-forward/&lt;wizard id&gt;-result.txt, shown here.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string FF_FOLDER = "fast-forward";
    private const string FF_XP_FILE = "quest-xp.txt";

    private const string FF_TO_LEVEL = "A level";
    private const string FF_WORLD_END = "The end of a world";
    private const string FF_WORLD_START = "The start of a world";
    private static readonly string[] s_ffTargets = [FF_TO_LEVEL, FF_WORLD_END, FF_WORLD_START];
    // Arcanum is no world of its own in the story list: its quests sit where they are played, first in the middle of
    // Polaris. As a pick it means that first visit (Niduus, 2026-10-29: "Arcanum is missing from world listing").
    private const string FF_ARCANUM = "Arcanum";
    private const string FF_ARCANUM_HOST = "Polaris";
    private const string FF_MAIN = "Main story only";
    private const string FF_SIDE = "Main story and side quests";
    private static readonly string[] s_ffWhich = [FF_MAIN, FF_SIDE];

    // Side quests that are never part of catching up.
    private static readonly string[] s_ffSkippedMarks = ["repeatable", "hidden", "outdated"];
    // Weekly and Beastmoon quests: not story, so never part of catching up either.
    private static readonly string[] s_ffSkippedCodes = ["WEEKLY", "-BM-"];
    private static readonly string[] s_ffSchools = ["Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance"];

    private readonly ComboBox _ffWizard = new() { MinWidth = 220, PlaceholderText = "Pick a wizard" };
    private readonly ComboBox _ffTarget = new() { ItemsSource = s_ffTargets, SelectedIndex = 0, MinWidth = 190 };
    private readonly NumericUpDown _ffLevel = new() { Minimum = 2, Maximum = 200, Value = 10, Increment = 1, FormatString = "0", Width = 130 };
    private readonly ComboBox _ffWorld = new() { MinWidth = 200 };
    private readonly ComboBox _ffWhich = new() { ItemsSource = s_ffWhich, SelectedIndex = 0, MinWidth = 220 };
    private readonly CheckBox _ffSideWorlds = new() { Content = "Include side worlds (Wysteria, Grizzleheim, Wintertusk, Aquila...)" };
    private readonly CheckBox _ffPassLevels = new() { Content = "Go past level needs" };
    private readonly CheckBox _ffGo = new() { Content = "Take me to my next quest", IsChecked = true };
    private readonly StackPanel _ffPreview = new() { Spacing = 6 };
    private readonly StackPanel _ffResult = new() { Spacing = 6 };
    private readonly DispatcherTimer _ffTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private List<QxWorld> _ffWorlds = [];
    private Dictionary<string, int> _ffXp = new(StringComparer.OrdinalIgnoreCase);
    private List<int> _ffLevelXp = [];
    private DateTime _ffXpRead;
    private FfWizard? _ffPicked;
    private DateTime _ffSent;
    private string _ffResultFile = "";
    private DateTime _ffResultRead;
    private bool _ffFilling;

    /// <summary>A wizard as its quest progress file has it.</summary>
    private sealed record FfWizard(string Id, string Name, bool OnThisPc, int Level, int Xp, string School, HashSet<string> Done) {
        public override string ToString() => $"{(Name.Length > 0 ? Name : "A wizard")}{(Level > 0 ? $", level {Level}" : "")}" +
                                             $"{(School.Length > 0 ? $" {School}" : "")}{(OnThisPc ? "  (this PC)" : "")}";
    }

    /// <summary>One quest of a plan, in the order it is finished.</summary>
    private sealed record FfStep(string Code, string Title, string World, bool Main, string[] Copies, int Xp);

    private Control FastForwardTab() {
        _ffTimer.Tick += (_, _) => ReadFastForwardResult();
        foreach (var box in new[] { _ffTarget, _ffWorld, _ffWhich }) {
            box.SelectionChanged += (_, e) => {
                if (e.Source == box) {
                    RenderFastForward();
                }
            };
        }

        _ffWizard.SelectionChanged += (_, _) => {
            if (!_ffFilling) {
                _ffPicked = _ffWizard.SelectedItem as FfWizard;
                _ffResultRead = default;
                ReadFastForwardResult();
                RenderFastForward();
            }
        };
        _ffLevel.ValueChanged += (_, _) => RenderFastForward();
        foreach (var tick in new[] { _ffSideWorlds, _ffPassLevels, _ffGo }) {
            tick.IsCheckedChanged += (_, _) => RenderFastForward();
        }

        var go = Primary("Fast forward");
        ToolTip.SetTip(go, "Finishes the quests listed below for the picked wizard, in story order, with each quest's real rewards. " +
                           "Start the server and log in with that wizard on this PC first.");
        var refresh = Plain("Refresh");
        ToolTip.SetTip(refresh, "Reads your wizards and the server's quest lists again.");

        return Page(
            WithMargin(MissingNote("it doesn't write the quest lists this page needs.", () => !_manager.HasQuestGuide), new Thickness(0)),
            Fold("bolt", "Fast Forward", "Fast Forward",
                "Bring a wizard up to a level or a world. The server finishes the quests before it, in story order, with their real rewards.",
                WithHint(new WrapPanel {
                    Children = { Spaced(_ffWizard), Spaced(Action(refresh, () => { LoadFastForward(force: true); return Task.CompletedTask; })) },
                }, "Pick your wizard. The list is every wizard the server has seen; its level and quests are as of the last time that " +
                   "wizard played. Log in with that wizard on this PC before you press Fast forward."),
                WithHint(new WrapPanel {
                    Children = {
                        SearchFilter("Bring me to", _ffTarget),
                        SearchFilter("Level", _ffLevel),
                        SearchFilter("World", _ffWorld),
                        SearchFilter("Quests", _ffWhich),
                    },
                }, "A level: the server finishes quests in story order and stops before the first quest once you have that level. " +
                   "The XP is only what those quests give, quest by quest, so you never end up higher than playing them would make " +
                   "you (fights in the real game give more on top). The end of a world: every quest up to the last one of that " +
                   "world. The start of a world: every quest before it, and its first quest is put in your quest book. " +
                   "Main story and side quests also finishes the side quests of each world on the way, right after the quest that opens them."),
                new WrapPanel {
                    Children = { SearchTick(_ffSideWorlds), SearchTick(_ffPassLevels), SearchTick(_ffGo) },
                },
                Sub("Go past level needs: some quests need a level that the quests before them may not give you without fights. " +
                    "Off, Fast Forward stops there and says so; on, it goes on anyway."),
                new WrapPanel { Children = { Spaced(Action(go, StartFastForwardAsync)) } }),
            Fold("list", "Fast Forward", "What it will do", "The quests in the order they are finished, and what they add up to.", _ffPreview),
            Fold("check", "Fast Forward", "What it did", "The last Fast Forward of the picked wizard, as the server wrote it.", _ffResult));
    }

    /// <summary>Reads the worlds, the XP and the wizards. Runs when the tab shows.</summary>
    private void LoadFastForward(bool force = false) {
        if (_ffWorlds.Count == 0) {
            _ffWorlds = ReadQuestTesting().Item1?.Worlds ?? [];
            var worlds = _ffWorlds.Select(w => w.Name).ToList();
            var host = worlds.IndexOf(FF_ARCANUM_HOST);
            if (host >= 0 && _ffWorlds[host].Quests.Any(IsArcanumQuest)) {
                worlds.Insert(host + 1, FF_ARCANUM);
            }

            _ffFilling = true;
            _ffWorld.ItemsSource = worlds;
            _ffWorld.SelectedIndex = worlds.Count > 1 ? 2 : 0;
            _ffFilling = false;
        }

        if (force) {
            _questsWritten = default;
            _ffXpRead = default;
        }

        LoadQuestGuide();
        ReadQuestXp();
        FillFastForwardWizards();
        ReadFastForwardResult();
        RenderFastForward();
    }

    private void ReadQuestXp() {
        var path = Path.Combine(_manager.IdLookupFolder, FF_XP_FILE);
        try {
            var written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (written == _ffXpRead) {
                return;
            }

            var xp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var levels = new List<int>();
            if (written != DateTime.MinValue) {
                foreach (var line in File.ReadLines(path)) {
                    var parts = line.Split('\t');
                    if (parts[0] == "level") {
                        levels = [.. parts.Skip(1).Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0)];
                    }
                    else if (parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount)) {
                        xp[parts[0]] = amount;
                    }
                }
            }

            (_ffXp, _ffLevelXp, _ffXpRead) = (xp, levels, written);
            if (levels.Count > 1) {
                _ffLevel.Maximum = levels.Count;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Being written: read on the next look.
        }
    }

    private void FillFastForwardWizards() {
        var wizards = new List<FfWizard>();
        try {
            if (Directory.Exists(QuestProgressFolder)) {
                foreach (var file in new DirectoryInfo(QuestProgressFolder).EnumerateFiles("*.txt")) {
                    string name = "", school = "";
                    int level = 0, xp = 0;
                    var local = false;
                    var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var line in File.ReadLines(file.FullName)) {
                        var parts = line.Split('\t', 2);
                        if (parts.Length < 2) {
                            continue;
                        }

                        switch (parts[0]) {
                            case "name": name = parts[1]; break;
                            case "pc": local = parts[1] == "local"; break;
                            case "level": int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out level); break;
                            case "xp": int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out xp); break;
                            case "school": school = parts[1]; break;
                            case "done": done.Add(parts[1]); break;
                        }
                    }

                    wizards.Add(new FfWizard(Path.GetFileNameWithoutExtension(file.Name), name, local, level, xp, school, done));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // A file being written right now is read on the next look.
        }

        wizards = [.. wizards.OrderByDescending(w => w.OnThisPc)];
        var picked = _ffPicked?.Id;
        _ffFilling = true;
        try {
            _ffWizard.ItemsSource = wizards;
            _ffPicked = wizards.FirstOrDefault(w => w.Id == picked) ?? wizards.FirstOrDefault();
            _ffWizard.SelectedItem = _ffPicked;
        }
        finally {
            _ffFilling = false;
        }
    }

    // The level a total of XP makes, by the game's table (quest-xp.txt): level L needs _ffLevelXp[L - 1].
    private int FfLevelAt(int xp) {
        var level = 1;
        for (var i = 1; i < _ffLevelXp.Count; i++) {
            if (xp >= _ffLevelXp[i] && _ffLevelXp[i] > 0) {
                level = i + 1;
            }
        }

        return level;
    }

    private int FfXpOf(string code) => _ffXp.GetValueOrDefault(code);

    // The lowest level where this much XP is no more than one level's worth.
    private int FfLevelForXp(int xp) {
        for (var i = 1; i < _ffLevelXp.Count; i++) {
            if (_ffLevelXp[i] - _ffLevelXp[i - 1] >= xp) {
                return i;
            }
        }

        return _ffLevelXp.Count;
    }

    /// <summary>
    /// The quests of the plan in the order they are finished: the main story world by world (side worlds only when
    /// asked or picked), and with side quests each one right after the last quest it needs.
    /// </summary>
    private List<FfStep> FastForwardPlan(out string[] next) {
        next = [];
        var target = _ffTarget.SelectedItem as string ?? FF_TO_LEVEL;
        var world = _ffWorld.SelectedItem as string ?? "";
        var sideWorlds = _ffSideWorlds.IsChecked == true;

        // Arcanum: its first visit, in Polaris. The start stops before the quest that takes you there; the end is the last
        // Arcanum quest of that visit.
        var arcanum = world == FF_ARCANUM;
        if (arcanum) {
            world = FF_ARCANUM_HOST;
        }

        // The worlds it goes through.
        var worlds = new List<QxWorld>();
        var pickedIndex = _ffWorlds.FindIndex(w => w.Name == world);
        for (var i = 0; i < _ffWorlds.Count; i++) {
            var w = _ffWorlds[i];
            if (target != FF_TO_LEVEL && pickedIndex >= 0) {
                if (i > pickedIndex || (target == FF_WORLD_START && i == pickedIndex && !arcanum)) {
                    break;
                }
            }

            if (w.SideWorld && !sideWorlds && i != pickedIndex) {
                continue;
            }

            worlds.Add(w);
        }

        // How many of the picked world's quests are done: all, or for Arcanum up to its start or end.
        var hostQuests = pickedIndex >= 0 ? _ffWorlds[pickedIndex].Quests.Where(q => q.Code.Length > 0).ToList() : [];
        var hostCount = hostQuests.Count;
        if (arcanum && target != FF_TO_LEVEL) {
            hostCount = target == FF_WORLD_START
                ? Math.Max(0, hostQuests.FindIndex(IsArcanumQuest))
                : hostQuests.FindLastIndex(IsArcanumQuest) + 1;
        }

        if (target == FF_WORLD_START && pickedIndex >= 0) {
            var first = arcanum ? hostQuests.ElementAtOrDefault(hostCount) : _ffWorlds[pickedIndex].Quests.FirstOrDefault(q => q.Code.Length > 0);
            next = first is null ? [] : [first.Code, .. first.Copies];
        }

        var keyed = new List<(double Key, FfStep Step)>();
        var keyOf = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var worldStart = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var w in worlds) {
            worldStart[w.Name] = index;
            var quests = w.Quests.Where(q => q.Code.Length > 0);
            if (arcanum && target != FF_TO_LEVEL && w.Name == FF_ARCANUM_HOST) {
                quests = quests.Take(hostCount);
            }

            foreach (var quest in quests) {
                index++;
                if (IsArcanumQuest(quest)) {
                    worldStart.TryAdd(FF_ARCANUM, index - 1);
                }

                var copies = quest.Copies.Where(c => c.Length > 0).ToArray();
                keyed.Add((index, new FfStep(quest.Code, quest.Name, IsArcanumQuest(quest) ? FF_ARCANUM : w.Name, true, copies,
                    new[] { quest.Code }.Concat(copies).Select(FfXpOf).DefaultIfEmpty().Max())));
                foreach (var code in copies.Prepend(quest.Code)) {
                    keyOf[code] = index;
                }
            }
        }

        if (_ffWhich.SelectedItem as string == FF_SIDE) {
            var worldNames = worlds.Select(w => w.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (worldStart.ContainsKey(FF_ARCANUM)) {
                worldNames.Add(FF_ARCANUM);
            }

            var done = _ffPicked?.Done ?? [];
            var waiting = _quests.Where(q => !q.Main && !keyOf.ContainsKey(q.Code) && worldNames.Contains(q.World)
                                             && !q.Marks.Any(m => s_ffSkippedMarks.Contains(m))
                                             && !s_ffSkippedCodes.Any(c => q.Code.Contains(c, StringComparison.OrdinalIgnoreCase))).ToList();
            var added = 0;
            // The wizard's level after each main quest, so a side quest only comes in once they are its level and its
            // XP is no more than about one level's worth: no big XP early on.
            var levelAfter = new List<int>();
            var xpSoFar = _ffPicked?.Xp ?? 0;
            levelAfter.Add(FfLevelAt(xpSoFar));
            foreach (var step in keyed) {
                xpSoFar += step.Step.Xp;
                levelAfter.Add(FfLevelAt(xpSoFar));
            }

            var levelOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            // A side quest can need another side quest: placed in rounds until nothing more can be.
            for (var round = 0; round < 40 && waiting.Count > 0; round++) {
                var placed = new List<QuestInfo>();
                foreach (var quest in waiting) {
                    if (!quest.Needs.All(n => keyOf.ContainsKey(n) || done.Contains(n))) {
                        continue;
                    }

                    var level = Math.Max(quest.Level, Math.Max(FfLevelForXp(FfXpOf(quest.Code)),
                        quest.Needs.Select(n => levelOf.GetValueOrDefault(n)).DefaultIfEmpty().Max()));
                    levelOf[quest.Code] = level;
                    var reached = levelAfter.FindIndex(l => l >= level);
                    if (reached < 0) {
                        // The main story here never gets them to its level: left out.
                        placed.Add(quest);
                        continue;
                    }

                    var after = quest.Needs.Where(keyOf.ContainsKey).Select(n => keyOf[n])
                        .Append(worldStart.GetValueOrDefault(quest.World)).Append(reached).Max();
                    var key = after + 0.5 + (++added) * 1e-7;
                    keyOf[quest.Code] = key;
                    keyed.Add((key, new FfStep(quest.Code, quest.Title, quest.World, false, [], FfXpOf(quest.Code))));
                    placed.Add(quest);
                }

                if (placed.Count == 0) {
                    break;
                }

                waiting = [.. waiting.Except(placed)];
            }
        }

        return [.. keyed.OrderBy(k => k.Key).Select(k => k.Step)];
    }

    // A quest played in Arcanum, by its code (AR-POL-MAIN-001 is the first one there).
    private static bool IsArcanumQuest(QxQuest quest) => quest.Code.StartsWith("AR-", StringComparison.OrdinalIgnoreCase);

    // A quest made for another school than the wizard's, by its code (WC-FIRE-C13-001 is Fire's).
    private static bool OtherSchoolQuest(string code, string school) {
        if (school.Length == 0) {
            return false;
        }

        var parts = code.ToUpperInvariant().Split('-');
        foreach (var other in s_ffSchools.Where(s => !s.Equals(school, StringComparison.OrdinalIgnoreCase))) {
            var upper = other.ToUpperInvariant();
            if (parts.Contains(upper) || (upper == "DEATH" && parts.Contains("DETH"))) {
                return true;
            }
        }

        return false;
    }

    private void RenderFastForward() {
        _ffLevel.IsEnabled = _ffTarget.SelectedItem as string == FF_TO_LEVEL;
        _ffWorld.IsEnabled = !_ffLevel.IsEnabled;
        _ffPreview.Children.Clear();
        if (_ffWorlds.Count == 0) {
            _ffPreview.Children.Add(Sub("The story list is missing from this copy of Ahziael."));
            return;
        }

        if (_quests.Count == 0 || _ffLevelXp.Count == 0) {
            _ffPreview.Children.Add(Sub("Start the server once (with the ID lookup on): it writes the quest lists this page reads a " +
                                        "minute or two after it starts. Then press Refresh."));
            if (_quests.Count == 0) {
                return;
            }
        }

        var wizard = _ffPicked;
        var plan = FastForwardPlan(out var next);
        var done = wizard?.Done ?? [];
        var school = wizard?.School ?? "";
        var targetLevel = _ffTarget.SelectedItem as string == FF_TO_LEVEL ? (int) (_ffLevel.Value ?? 0) : 0;
        var xp = wizard?.Xp ?? 0;
        var startLevel = wizard?.Level > 0 ? wizard.Level : FfLevelAt(xp);
        int finished = 0, main = 0, side = 0, already = 0, gained = 0;
        FfStep? stopBefore = null;
        var shown = new List<FfStep>();
        foreach (var step in plan) {
            if (step.Copies.Prepend(step.Code).Any(done.Contains)) {
                already++;
                continue;
            }

            if (!step.Main && OtherSchoolQuest(step.Code, school)) {
                continue;
            }

            if (targetLevel > 0 && _ffLevelXp.Count > 0 && FfLevelAt(xp + gained) >= targetLevel) {
                stopBefore = step;
                break;
            }

            finished++;
            gained += step.Xp;
            if (step.Main) {
                main++;
            }
            else {
                side++;
            }

            shown.Add(step);
        }

        var endLevel = _ffLevelXp.Count > 0 ? Math.Max(startLevel, FfLevelAt(xp + gained)) : 0;
        var who = wizard is null ? "Your wizard" : wizard.Name.Length > 0 ? wizard.Name : "This wizard";
        var lines = new List<string>();
        if (finished == 0) {
            lines.Add(targetLevel > 0 && startLevel >= targetLevel
                ? $"{who} is level {startLevel} already, so there is nothing to do."
                : $"{who} has done every quest of this already.");
        }
        else {
            lines.Add($"About {finished:N0} quests to finish ({main:N0} main" + (side > 0 ? $", {side:N0} side" : "") + ")" +
                      (already > 0 ? $"; {already:N0} are done already." : "."));
            if (_ffLevelXp.Count > 0) {
                lines.Add($"Their XP: +{gained:N0}, from level {startLevel} to about level {endLevel}." +
                          (targetLevel > 0 && endLevel < targetLevel
                              ? $" The quests run out before level {targetLevel}: try Main story and side quests, or side worlds."
                              : ""));
            }

            if (stopBefore is not null) {
                lines.Add($"It stops before '{stopBefore.Title}' ({stopBefore.World}), the first quest once you are level {targetLevel}. " +
                          "That quest goes in your quest book.");
            }
            else if (next.Length > 0 && _ffWorld.SelectedItem is string startWorld) {
                lines.Add($"Then the first quest of {startWorld} goes in your quest book.");
            }
        }

        lines.Add("Numbers are about: quests for other schools are skipped, and quests can need others first. The server says exactly " +
                  "what it did below.");
        foreach (var line in lines) {
            _ffPreview.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap });
        }

        // The first and the last quests, so it is clear where it starts and ends.
        if (shown.Count > 0) {
            var rows = new StackPanel { Spacing = 2, Margin = new Thickness(0, 6, 0, 0) };
            var list = shown.Count <= 16 ? shown : [.. shown.Take(8), .. shown.TakeLast(8)];
            for (var i = 0; i < list.Count; i++) {
                if (shown.Count > 16 && i == 8) {
                    rows.Children.Add(Sub($"... {shown.Count - 16:N0} more ..."));
                }

                var step = list[i];
                var row = new TextBlock {
                    Text = $"{(step.Main ? "Main" : "Side")}  ·  {step.World}  ·  {step.Title}" + (step.Xp > 0 ? $"  ·  {step.Xp:N0} XP" : ""),
                    TextWrapping = TextWrapping.Wrap,
                };
                if (!step.Main) {
                    row.Foreground = MUTED;
                }

                rows.Children.Add(row);
            }

            _ffPreview.Children.Add(rows);
        }
    }

    private async Task StartFastForwardAsync() {
        if (_ffPicked is not { } wizard) {
            throw new PanelException("Pick a wizard first. The list fills once that wizard has played on your server.");
        }

        var plan = FastForwardPlan(out var next);
        var steps = plan.Where(s => !s.Copies.Prepend(s.Code).Any(wizard.Done.Contains)).ToList();
        var targetLevel = _ffTarget.SelectedItem as string == FF_TO_LEVEL ? (int) (_ffLevel.Value ?? 0) : 0;
        if (steps.Count == 0) {
            throw new PanelException("There is nothing to finish: this wizard has done all of these quests.");
        }

        var name = wizard.Name.Length > 0 ? wizard.Name : "this wizard";
        if (!await ConfirmAsync($"Finish up to {steps.Count:N0} quests for {name}" + (targetLevel > 0 ? $", until level {targetLevel}" : "") +
                                "? They count as done, with their rewards, and can't be undone in one click. " +
                                $"{name} must be logged in on this PC.")) {
            return;
        }

        var text = new StringBuilder();
        if (targetLevel > 0) {
            text.Append("level\t").Append(targetLevel.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        if (_ffPassLevels.IsChecked == true) {
            text.Append("levels\tpass\n");
        }

        if (_ffGo.IsChecked == true) {
            text.Append("go\n");
        }

        foreach (var step in steps) {
            foreach (var code in step.Copies.Prepend(step.Code)) {
                text.Append("quest\t").Append(code).Append('\t').Append(step.Main ? "main" : "side").Append('\n');
            }
        }

        foreach (var code in next) {
            text.Append("next\t").Append(code).Append('\n');
        }

        var folder = Path.Combine(_manager.ServerFolder, FF_FOLDER);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, wizard.Id + ".txt"), text.ToString());
        TryDeleteFile(Path.Combine(folder, wizard.Id + "-result.txt"));
        _manager.SendCommand(".quest fastforward", wizard.Id);
        _ffSent = DateTime.UtcNow;
        _ffResultRead = default;
        _ffResult.Children.Clear();
        _ffResult.Children.Add(Sub("Sent. The game's chat says how it goes; this shows what it did once it is done."));
        _ffTimer.Start();
        Say($"Fast Forward sent for {name}. Keep the game open until the chat says it's done.");
    }

    private static void TryDeleteFile(string path) {
        try {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        }
    }

    /// <summary>Shows the server's account of the picked wizard's last Fast Forward, when it changed.</summary>
    private void ReadFastForwardResult() {
        if (_ffPicked is not { } wizard) {
            _ffResult.Children.Clear();
            _ffResult.Children.Add(Sub("Pick a wizard."));
            return;
        }

        var path = Path.Combine(_manager.ServerFolder, FF_FOLDER, wizard.Id + "-result.txt");
        try {
            var written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (path == _ffResultFile && written == _ffResultRead) {
                // Nothing yet: after half a minute, the wizard was probably not logged in.
                if (_ffSent != default && written < _ffSent && DateTime.UtcNow - _ffSent > TimeSpan.FromSeconds(30)) {
                    _ffTimer.Stop();
                    _ffSent = default;
                    _ffResult.Children.Clear();
                    _ffResult.Children.Add(new TextBlock {
                        Text = $"Nothing happened yet. Is {(wizard.Name.Length > 0 ? wizard.Name : "the wizard")} logged in on this PC, " +
                               "with the server running? Log in and press Fast forward again.",
                        TextWrapping = TextWrapping.Wrap, Foreground = WARN,
                    });
                }

                return;
            }

            (_ffResultFile, _ffResultRead) = (path, written);
            _ffResult.Children.Clear();
            if (written == DateTime.MinValue) {
                _ffResult.Children.Add(Sub(_ffSent != default ? "Waiting for the server..." : "This wizard hasn't used Fast Forward yet."));
                return;
            }

            var values = new Dictionary<string, string>();
            var doneCodes = new List<string>();
            var skipped = new List<(string Code, string Why)>();
            var items = new List<string>();
            foreach (var line in File.ReadLines(path)) {
                var parts = line.Split('\t');
                switch (parts[0]) {
                    case "done": doneCodes.Add(parts[1]); break;
                    case "skipped" when parts.Length > 2: skipped.Add((parts[1], parts[2])); break;
                    case "item" when parts.Length > 2: items.Add($"{parts[1]} x{parts[2]}"); break;
                    default: values[parts[0]] = parts.Length > 1 ? string.Join('\t', parts.Skip(1)) : ""; break;
                }
            }

            string Title(string code) => _quests.FirstOrDefault(q => q.Code.Equals(code, StringComparison.OrdinalIgnoreCase))?.Title ?? code;
            int Number(string key) => int.TryParse(values.GetValueOrDefault(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

            if (values.GetValueOrDefault("state") == "running") {
                _ffResult.Children.Add(new TextBlock {
                    Text = $"Working: {values.GetValueOrDefault("progress")} quests looked at, {Number("finished"):N0} finished so far...",
                    TextWrapping = TextWrapping.Wrap,
                });
                _ffTimer.Start();
                return;
            }

            _ffTimer.Stop();
            _ffSent = default;
            var levels = (values.GetValueOrDefault("levels") ?? "").Split('\t');
            _ffResult.Children.Add(new TextBlock {
                Text = $"{Number("finished"):N0} quests finished" + (Number("needed") > 0 ? $" ({Number("needed"):N0} of them needed by the others)" : "") +
                       $", +{Number("xp"):N0} XP" + (levels.Length == 2 ? $", level {levels[0]} to {levels[1]}" : "") +
                       (Number("gold") > 0 ? $", +{Number("gold"):N0} gold" : "") +
                       (Number("tp") > 0 ? $", +{Number("tp")} training points" : "") +
                       (Number("already") > 0 ? $". {Number("already"):N0} were done already." : "."),
                TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold,
            });
            _ffResult.Children.Add(Sub($"Written {values.GetValueOrDefault("written")}."));
            if (values.GetValueOrDefault("stopped") is { } stopped) {
                var parts = stopped.Split('\t');
                _ffResult.Children.Add(new TextBlock {
                    Text = parts[0].Length > 0
                        ? $"Stopped at '{Title(parts[0])}': {(parts.Length > 1 ? parts[1] : "")}. Fix that (or tick Go past level needs) and press Fast forward again."
                        : $"Stopped early: {(parts.Length > 1 ? parts[1] : "")}.",
                    TextWrapping = TextWrapping.Wrap, Foreground = WARN,
                });
            }

            if (values.GetValueOrDefault("next") is { Length: > 0 } nextCode) {
                _ffResult.Children.Add(new TextBlock { Text = $"Next quest, now in your quest book: {Title(nextCode)}.", TextWrapping = TextWrapping.Wrap });
            }

            if (items.Count > 0) {
                _ffResult.Children.Add(new SelectableTextBlock { Text = "Items: " + string.Join(", ", items), TextWrapping = TextWrapping.Wrap });
            }

            var notYours = skipped.Count(s => s.Why.StartsWith("for another school", StringComparison.Ordinal));
            var otherSkips = skipped.Where(s => !s.Why.StartsWith("for another school", StringComparison.Ordinal)).ToList();
            if (notYours > 0) {
                _ffResult.Children.Add(Sub($"{notYours:N0} quests were for other schools (or another wizard's choices) and were left out."));
            }

            if (otherSkips.Count > 0) {
                _ffResult.Children.Add(new TextBlock { Text = $"Left out ({otherSkips.Count:N0}):", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
                foreach (var (code, why) in otherSkips.Take(60)) {
                    _ffResult.Children.Add(new SelectableTextBlock { Text = $"{Title(code)} ({code}): {why}", TextWrapping = TextWrapping.Wrap, Foreground = MUTED });
                }
            }

            if (doneCodes.Count > 0) {
                var list = new SelectableTextBlock {
                    Text = string.Join("\n", doneCodes.Select((c, i) => $"{i + 1}. {Title(c)} ({c})")), TextWrapping = TextWrapping.Wrap, Foreground = MUTED,
                };
                var shut = new Expander { Header = $"Every quest it finished ({doneCodes.Count:N0})", Content = list, HorizontalAlignment = HorizontalAlignment.Stretch };
                _ffResult.Children.Add(shut);
            }

            // The wizard's level and quests changed: the preview reads them again.
            FillFastForwardWizards();
            RenderFastForward();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Being written: read on the next look.
        }
    }

}
