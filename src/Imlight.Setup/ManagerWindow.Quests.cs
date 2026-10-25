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
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The Quests tab (Niduus, 2026-10-24: "Include quest checks into manager, allowing me to see which ones I need to do
/// and button to execute commands to start the quest. Another to tp to the exact zone it's given"): every quest in the
/// order it is played, marked Done, In progress, Next up (all the quests it needs are done) or Later for the picked
/// wizard, with Start (.quest forcegrant) and Teleport (.tp to the zone of the NPC who gives it). The server writes the
/// list (quest-guide.txt in the ID lookup folder) and each wizard's quests (quest-progress/&lt;id&gt;.txt).
/// </summary>
internal sealed partial class ManagerWindow {

    private const string QUEST_GUIDE_FILE = "quest-guide.txt";
    private const string QUEST_PROGRESS_FOLDER = "quest-progress";

    private static readonly string[] QUEST_SHOWS = ["Next up", "In progress", "Not done", "Done", "All"];
    private const string ALL_WORLDS = "Every world";

    // The worlds in story order, by the start of a quest's code (CL-MAIN-C01-001 is Celestia).
    private static readonly (string Code, string World)[] s_questWorlds = [
        ("WC", "Wizard City"), ("KT", "Krokotopia"), ("MB", "Marleybone"), ("MS", "Mooshu"), ("DS", "Dragonspyre"),
        ("GH", "Grizzleheim"), ("CL", "Celestia"), ("WY", "Wysteria"), ("ZF", "Zafaria"), ("AV", "Avalon"),
        ("AZ", "Azteca"), ("KR", "Khrysalis"), ("KH", "Khrysalis"), ("PL", "Polaris"), ("MR", "Mirage"), ("EM", "Empyrea"),
        ("KM", "Karamelle"), ("LM", "Lemuria"), ("NV", "Novus"), ("WL", "Wallaru"), ("SL", "Selenopolis"), ("DM", "Darkmoor"),
    ];

    private enum QuestState { Done, InProgress, NextUp, Later }

    private sealed record QuestInfo(int Order, string Code, string Title, int Level, bool Main, string[] Needs,
        string Giver, string Zone, string ZoneFrom, string World);

    private sealed record QuestRow(QuestInfo Quest, QuestState State, string[] Missing);

    private sealed record QuestWizard(string File, string Name, bool OnThisPc, string Updated, DateTime Written) {
        public override string ToString() => $"{(Name.Length > 0 ? Name : "A wizard")}{(OnThisPc ? "  (this PC)" : "")}";
    }

    private readonly ComboBox _questWizard = new() { MinWidth = 200, PlaceholderText = "Pick a wizard" };
    private readonly ComboBox _questShow = new() { ItemsSource = QUEST_SHOWS, SelectedIndex = 0, Width = 130 };
    private readonly ComboBox _questWorld = new() { Width = 150 };
    private readonly CheckBox _questMainOnly = new() { Content = "Main story only", IsChecked = true };
    private readonly TextBox _questSearch = new() { Watermark = "Search a quest's name, code, giver or zone" };
    // Niduus, 2026-10-25: every detail of a quest to filter by, like the Gear tab's filters.
    private static readonly string[] s_questStarts = [ANY_FILTER, "Given by an NPC", "Starts by itself", "Not known"];
    private static readonly string[] s_questSorts = ["Story order", "Level (low first)", "Level (high first)", "Name", "Giver", "Zone"];
    private readonly NumericUpDown _questLevelMin = FilterNumber(5);
    private readonly NumericUpDown _questLevelMax = FilterNumber(5);
    private readonly ComboBox _questGiver = FilterCombo([ANY_FILTER], 180);
    private readonly ComboBox _questZone = FilterCombo([ANY_FILTER], 200);
    private readonly ComboBox _questStarts = FilterCombo(s_questStarts, 150);
    private readonly ComboBox _questSort = FilterCombo(s_questSorts, 150);
    private readonly NumericUpDown _questNeedsMin = FilterNumber(1);
    private readonly NumericUpDown _questNeedsMax = FilterNumber(1);
    private readonly CheckBox _questTeleport = new() { Content = "Only ones I can teleport to" };
    private readonly TextBlock _questCount = Sub("");
    private readonly ListBox _questResults = new() { Background = Brushes.Transparent };
    private readonly DispatcherTimer _questTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _questDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private List<QuestInfo> _quests = [];
    private DateTime _questsWritten;
    private HashSet<string> _questsDone = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _questsNow = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _questProgressWritten;
    private bool _questWizardsFilling;

    private string QuestGuidePath => Path.Combine(_manager.IdLookupFolder, QUEST_GUIDE_FILE);

    private string QuestProgressFolder => Path.Combine(_manager.ServerFolder, QUEST_PROGRESS_FOLDER);

    private Control QuestsTab() {
        _questResults.ItemTemplate = new FuncDataTemplate<QuestRow>((row, _) => row is null ? new Panel() : QuestRowView(row));
        _questDelay.Tick += (_, _) => {
            _questDelay.Stop();
            RenderQuests();
        };
        _questSearch.TextChanged += (_, _) => {
            _questDelay.Stop();
            _questDelay.Start();
        };
        _questShow.SelectionChanged += (_, _) => RenderQuests();
        _questWorld.SelectionChanged += (_, _) => RenderQuests();
        _questMainOnly.IsCheckedChanged += (_, _) => RenderQuests();
        WhenFiltersChange(RenderQuests, _questLevelMin, _questLevelMax, _questGiver, _questZone, _questStarts, _questSort,
            _questNeedsMin, _questNeedsMax, _questTeleport);
        _questWizard.SelectionChanged += (_, _) => {
            if (!_questWizardsFilling) {
                _questProgressWritten = default;
                LoadQuestProgress();
                RenderQuests();
            }
        };

        // While the tab shows, a quest started, finished or removed in the game shows here within a few seconds.
        _questTimer.Tick += (_, _) => LoadQuests();

        return new DockPanel {
            Margin = new Thickness(24, 12, 24, 16), MaxWidth = ManagerTheme.PAGE_MAX_WIDTH,
            Children = {
                WithDock(WithMargin(MissingNote("it doesn't write the quest list this page shows.", () => !_manager.HasQuestGuide),
                    new Thickness(0, 0, 0, 10)), Dock.Top),
                WithDock(Fold("flag", "Quests", "My quests",
                    "Which quests your wizard has done and which come next, with buttons to start one or go where it's given.",
                    WithHint(new WrapPanel {
                        Children = {
                            Spaced(_questWizard), Spaced(_questShow), Spaced(_questWorld), Spaced(WithCenter(_questMainOnly)),
                            Spaced(Action(Plain("Refresh"), () => {
                                _questsWritten = default;
                                _questProgressWritten = default;
                                LoadQuests();

                                return Task.CompletedTask;
                            })),
                        },
                    }, "Pick your wizard: the list is what that wizard has done, as the server saw it the last time they played, " +
                       "and the buttons work for that wizard only. " +
                       "Next up: every quest it needs is done, so it is the one to do now, and the ones in your quest book stay " +
                       "in their place marked In progress. In progress: in your quest book. " +
                       "Not done: Next up and Later together. Start gives you the quest at once (.quest forcegrant), even when the " +
                       "quests before it are not done. Teleport takes you to the zone of the NPC who gives it (.tp). Finish " +
                       "completes a quest you have (.quest complete). The buttons run in the game on this PC, so start the server " +
                       "and log in first."),
                    _questSearch,
                    SearchFilters(
                        SearchFilter("Level from", _questLevelMin),
                        SearchFilter("Level to", _questLevelMax),
                        SearchFilter("Given by", _questGiver),
                        SearchFilter("Zone", _questZone),
                        SearchFilter("How it starts", _questStarts),
                        SearchFilter("Quests needed, from", _questNeedsMin),
                        SearchFilter("Quests needed, to", _questNeedsMax),
                        SearchFilter("Sort by", _questSort),
                        SearchTick(_questTeleport)),
                    _questCount), Dock.Top),
                WithMargin(Card(_questResults), new Thickness(0, 14, 0, 0)),
            },
        };
    }

    /// <summary>Reads the quest list and the wizards again when the server has written them since. Runs when the tab shows.</summary>
    private void LoadQuests() {
        var changed = LoadQuestGuide();
        changed |= FillQuestWizards();
        changed |= LoadQuestProgress();
        if (changed) {
            RenderQuests();
        }
    }

    private bool LoadQuestGuide() {
        var path = QuestGuidePath;
        try {
            if (!File.Exists(path)) {
                // DateTime.MaxValue: the file was looked for and isn't there, so the note is shown once, not every look.
                var changed = _quests.Count > 0 || _questsWritten != DateTime.MaxValue;
                (_quests, _questsWritten) = ([], DateTime.MaxValue);

                return changed;
            }

            var written = File.GetLastWriteTimeUtc(path);
            if (written == _questsWritten) {
                return false;
            }

            var quests = new List<QuestInfo>();
            foreach (var line in File.ReadLines(path)) {
                var parts = line.Split('\t');
                if (parts.Length < 8 || parts[0].Length == 0) {
                    continue;
                }

                quests.Add(new QuestInfo(quests.Count, parts[0], parts[1].Length > 0 ? parts[1] : parts[0],
                    int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) ? level : 0,
                    parts[3] == "main",
                    parts[4].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    parts[5], parts[6], parts[7], QuestWorldOf(parts[0], parts[6])));
            }

            _quests = quests;
            _questsWritten = written;
            var worlds = quests.Select(q => q.World).Distinct().OrderBy(WorldOrder).ThenBy(w => w, StringComparer.OrdinalIgnoreCase).ToList();
            var picked = _questWorld.SelectedItem as string;
            _questWorld.ItemsSource = new[] { ALL_WORLDS }.Concat(worlds).ToList();
            _questWorld.SelectedItem = picked is not null && worlds.Contains(picked) ? picked : ALL_WORLDS;
            FillFilterCombo(_questGiver, quests.Select(q => q.Giver).OrderBy(g => g, StringComparer.OrdinalIgnoreCase));
            FillFilterCombo(_questZone, quests.Select(q => q.Zone).OrderBy(z => z, StringComparer.OrdinalIgnoreCase));

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            _questCount.Text = $"Could not read {QUEST_GUIDE_FILE}: {ex.Message}";

            return false;
        }
    }

    private static int WorldOrder(string world) {
        var index = Array.FindIndex(s_questWorlds, w => w.World == world);

        return index < 0 ? int.MaxValue : index;
    }

    /// <summary>The world of a quest: by the start of its code, else by its zone (WizardCity/WC_Hub is Wizard City).</summary>
    private static string QuestWorldOf(string code, string zone) {
        var prefix = code.Split('-', '_')[0];
        foreach (var (start, world) in s_questWorlds) {
            if (prefix.Equals(start, StringComparison.OrdinalIgnoreCase)) {
                return world;
            }
        }

        var top = zone.Split('/')[0];
        if (top.Length == 0) {
            return "Other";
        }

        foreach (var (_, world) in s_questWorlds) {
            if (world.Replace(" ", "").Equals(top, StringComparison.OrdinalIgnoreCase)) {
                return world;
            }
        }

        return top;
    }

    /// <summary>Lists the wizards the server has written quests for, this PC's first. True when the list changed.</summary>
    private bool FillQuestWizards() {
        var wizards = new List<QuestWizard>();
        try {
            if (Directory.Exists(QuestProgressFolder)) {
                foreach (var file in new DirectoryInfo(QuestProgressFolder).EnumerateFiles("*.txt")) {
                    var head = File.ReadLines(file.FullName).Take(4).Select(l => l.Split('\t', 2)).Where(p => p.Length == 2)
                        .ToDictionary(p => p[0], p => p[1]);
                    wizards.Add(new QuestWizard(file.FullName, head.GetValueOrDefault("name") ?? "", head.GetValueOrDefault("pc") == "local",
                        head.GetValueOrDefault("updated") ?? "", file.LastWriteTimeUtc));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // A file being written right now is read on the next look.
        }

        wizards = [.. wizards.OrderByDescending(w => w.OnThisPc).ThenByDescending(w => w.Written)];
        var current = _questWizard.ItemsSource as List<QuestWizard> ?? [];
        if (current.Select(w => (w.File, w.Name, w.OnThisPc)).SequenceEqual(wizards.Select(w => (w.File, w.Name, w.OnThisPc)))) {
            return false;
        }

        var picked = (_questWizard.SelectedItem as QuestWizard)?.File;
        _questWizardsFilling = true;
        try {
            _questWizard.ItemsSource = wizards;
            _questWizard.SelectedItem = wizards.FirstOrDefault(w => w.File == picked) ?? wizards.FirstOrDefault();
        }
        finally {
            _questWizardsFilling = false;
        }

        _questProgressWritten = default;

        return true;
    }

    /// <summary>Reads the picked wizard's done and current quests. True when they changed.</summary>
    private bool LoadQuestProgress() {
        if (_questWizard.SelectedItem is not QuestWizard wizard) {
            var had = _questsDone.Count + _questsNow.Count > 0;
            _questsDone = new(StringComparer.OrdinalIgnoreCase);
            _questsNow = new(StringComparer.OrdinalIgnoreCase);

            return had;
        }

        try {
            var written = File.GetLastWriteTimeUtc(wizard.File);
            if (written == _questProgressWritten) {
                return false;
            }

            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var now = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(wizard.File)) {
                var parts = line.Split('\t', 2);
                if (parts.Length == 2 && parts[0] == "done") {
                    done.Add(parts[1]);
                }
                else if (parts.Length == 2 && parts[0] == "now") {
                    now.Add(parts[1]);
                }
            }

            (_questsDone, _questsNow, _questProgressWritten) = (done, now, written);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    private QuestState StateOf(QuestInfo quest, out string[] missing) {
        missing = [];
        if (_questsNow.Contains(quest.Code)) {
            return QuestState.InProgress;
        }

        if (_questsDone.Contains(quest.Code)) {
            return QuestState.Done;
        }

        missing = [.. quest.Needs.Where(n => !_questsDone.Contains(n))];

        return missing.Length == 0 ? QuestState.NextUp : QuestState.Later;
    }

    private void RenderQuests() {
        if (_quests.Count == 0) {
            _questResults.ItemsSource = null;
            _questCount.Text = _manager.HasQuestGuide
                ? "No quest list yet. Start the server once: it writes the list a little after it starts (with the ID lookup on)."
                : "";

            return;
        }

        var show = _questShow.SelectedItem as string ?? QUEST_SHOWS[0];
        var world = _questWorld.SelectedItem as string ?? ALL_WORLDS;
        var mainOnly = _questMainOnly.IsChecked == true;
        var words = SearchText.Words(_questSearch.Text);
        var giver = FilterPick(_questGiver);
        var zone = FilterPick(_questZone);
        var starts = _questStarts.SelectedIndex;
        var teleport = _questTeleport.IsChecked == true;

        var rows = new List<QuestRow>();
        int done = 0, now = 0, next = 0, inScope = 0;
        foreach (var quest in _quests) {
            if ((mainOnly && !quest.Main) || (world != ALL_WORLDS && quest.World != world)) {
                continue;
            }

            var state = StateOf(quest, out var missing);
            inScope++;
            switch (state) {
                case QuestState.Done: done++; break;
                case QuestState.InProgress: now++; break;
                case QuestState.NextUp: next++; break;
            }

            // A quest started from here stays in its place in Next up, marked In progress (Niduus, 2026-10-24: Start
            // took it out of the list at once).
            var shown = show switch {
                "Next up" => state is QuestState.NextUp or QuestState.InProgress,
                "In progress" => state == QuestState.InProgress,
                "Not done" => state is QuestState.NextUp or QuestState.Later,
                "Done" => state == QuestState.Done,
                _ => true,
            };
            if (!shown) {
                continue;
            }

            if (!InFilterRange(quest.Level, _questLevelMin, _questLevelMax)
                || (giver is not null && quest.Giver != giver) || (zone is not null && quest.Zone != zone)
                || (teleport && quest.Zone.Length == 0)
                || !InFilterRange(quest.Needs.Length, _questNeedsMin, _questNeedsMax)
                || starts switch {
                    1 => quest.ZoneFrom != "npc",
                    2 => quest.ZoneFrom != "auto",
                    3 => quest.ZoneFrom is "npc" or "auto",
                    _ => false,
                }) {
                continue;
            }

            if (words.Length > 0) {
                var text = SearchText.Normalize($"{quest.Title} {quest.Code} {quest.Giver} {quest.Zone} {quest.World} " +
                                                $"{(quest.Main ? "main story" : "side quest")} level {quest.Level} {string.Join(' ', quest.Needs)}");
                if (!words.All(w => text.Contains(w, StringComparison.Ordinal))) {
                    continue;
                }
            }

            rows.Add(new QuestRow(quest, state, missing));
        }

        rows = _questSort.SelectedIndex switch {
            1 => [.. rows.OrderBy(r => r.Quest.Level == 0).ThenBy(r => r.Quest.Level).ThenBy(r => r.Quest.Order)],
            2 => [.. rows.OrderByDescending(r => r.Quest.Level).ThenBy(r => r.Quest.Order)],
            3 => [.. rows.OrderBy(r => r.Quest.Title, StringComparer.OrdinalIgnoreCase)],
            4 => [.. rows.OrderBy(r => r.Quest.Giver.Length == 0).ThenBy(r => r.Quest.Giver, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Quest.Order)],
            5 => [.. rows.OrderBy(r => r.Quest.Zone.Length == 0).ThenBy(r => r.Quest.Zone, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Quest.Order)],
            _ => rows,
        };
        _questResults.ItemsSource = rows;
        var who = (_questWizard.SelectedItem as QuestWizard)?.Name;
        var scope = $"{(mainOnly ? "main story " : "")}quests{(world == ALL_WORLDS ? "" : $" in {world}")}";
        _questCount.Text = who is null
            ? $"Showing {rows.Count:N0}. No wizard has played on this server yet, so nothing is marked done. Log in once and it fills in."
            : $"{who}: {done:N0} of {inScope:N0} {scope} done, {now:N0} in progress, {next:N0} next up. Showing {rows.Count:N0}.";
    }

    private Control QuestRowView(QuestRow row) {
        var quest = row.Quest;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (row.State == QuestState.InProgress) {
            buttons.Children.Add(Action(Plain("Finish"), () => RunQuestCommand($".quest complete {quest.Code}",
                $"Finishing {quest.Title}. Its rewards show in the game.")));
        }
        else {
            var start = row.State == QuestState.NextUp ? Primary("Start") : Plain("Start");
            ToolTip.SetTip(start, row.State == QuestState.Done
                ? "Gives you this quest again, to play it once more."
                : "Gives you this quest now, even if the quests before it are not done.");
            buttons.Children.Add(Action(start, () => RunQuestCommand($".quest forcegrant {quest.Code}",
                $"Started {quest.Title}. It is in your quest book now.")));
        }

        var teleport = Plain("Teleport");
        teleport.IsEnabled = quest.Zone.Length > 0;
        ToolTip.SetTip(teleport, quest.Zone.Length == 0
            ? "The server doesn't know where this quest is given, or can't teleport there."
            : quest.ZoneFrom == "npc"
                ? $"Takes you to {quest.Zone}, where {(quest.Giver.Length > 0 ? quest.Giver : "the NPC who gives it")} is."
                : $"Takes you to {quest.Zone}, where its first step is (who gives it is not known).");
        buttons.Children.Add(Action(teleport, () => RunQuestCommand($".tp {quest.Zone}", $"Teleporting to {quest.Zone}.")));

        var (stateText, stateBrush) = row.State switch {
            QuestState.Done => ("Done", GOOD),
            QuestState.InProgress => ("In progress", ManagerTheme.Accent("Quests")),
            QuestState.NextUp => ("Next up", WARN),
            _ => ("Later", MUTED),
        };

        var details = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 12, 0) };
        details.Children.Add(new StackPanel {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children = {
                WithCenter(new Border {
                    BorderBrush = stateBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 1),
                    Child = new TextBlock { Text = stateText, Foreground = stateBrush, FontSize = 12 },
                }),
                new SelectableTextBlock { Text = quest.Title, FontWeight = FontWeight.SemiBold, FontSize = 15 },
                WithCenter(new TextBlock {
                    Text = string.Join("  ·  ", new[] { quest.World, quest.Main ? "main story" : "side quest",
                        quest.Level > 0 ? $"level {quest.Level}" : "", quest.Code }.Where(t => t.Length > 0)),
                    Foreground = MUTED,
                }),
            },
        });

        var where = quest.ZoneFrom switch {
            "auto" => $"Starts by itself when the quest before it is done. First step in {ShownZone(quest.Zone)}.",
            "npc" => $"Given by {(quest.Giver.Length > 0 ? quest.Giver : "an NPC")} in {ShownZone(quest.Zone)}.",
            _ => quest.Giver.Length > 0
                ? $"Given by {quest.Giver}. First step in {ShownZone(quest.Zone)}."
                : $"First step in {ShownZone(quest.Zone)}.",
        };
        details.Children.Add(Sub(where));
        if (row.Missing.Length > 0) {
            var titles = row.Missing.Take(3).Select(code => _quests.FirstOrDefault(q => q.Code.Equals(code, StringComparison.OrdinalIgnoreCase))?.Title ?? code);
            details.Children.Add(Sub($"Needs first: {string.Join(", ", titles)}{(row.Missing.Length > 3 ? $" and {row.Missing.Length - 3} more" : "")}."));
        }

        return new Border {
            Padding = new Thickness(4, 6),
            BorderBrush = LINE,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new DockPanel { Children = { WithDock(buttons, Dock.Right), details } },
        };
    }

    private static string ShownZone(string zone) => zone.Length > 0 ? zone : "a place the server can't teleport to";

    // Only the wizard picked at the top runs the buttons' commands, not every wizard playing on this PC (Niduus,
    // 2026-10-24: Start gave the quest to both of his wizards).
    private Task RunQuestCommand(string command, string said) {
        var wizard = _questWizard.SelectedItem as QuestWizard;
        _manager.SendCommand(command, wizard is null ? null : Path.GetFileNameWithoutExtension(wizard.File));
        Say(said + (wizard is { Name.Length: > 0 } ? $" Only {wizard.Name} gets it." : "") + " The game's chat says if it worked.");

        return Task.CompletedTask;
    }

}
