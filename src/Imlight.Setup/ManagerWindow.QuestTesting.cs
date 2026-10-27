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
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

namespace Imlight.Setup;

/// <summary>
/// The Quest Testing tab on the Quests page (Niduus, 2026-10-27: "Update my quest testing section in Ahziael for ALL Main
/// quest that may have issues to test with descriptions of why they were flagged, what's the expected outcome. Make it
/// from Point A to B for the quest, that way I test it all from beginning to end"). Every main story quest in the order
/// it is played, each with why it may not work on this server, what to do and what should happen, and a tick for the
/// ones he has tested. The list is built into the app (Assets/quest-testing.json.gz, made by
/// tools/quest-testing/build_quest_testing.py); the ticks are kept on this PC (install folder, quest-testing-done.json).
/// </summary>
internal sealed partial class ManagerWindow {

    private const string QUEST_TESTING_ASSET = "quest-testing.json.gz";
    private const string QUEST_TESTING_DONE = "quest-testing-done.json";

    private sealed class QxFlag {
        public string Kind { get; init; } = "";
        public string Why { get; init; } = "";
        public string Check { get; init; } = "";
    }

    private sealed class QxQuest {
        public int Number { get; init; }
        public string Name { get; init; } = "";
        public string Area { get; init; } = "";
        public string Code { get; init; } = "";
        public List<string> Copies { get; init; } = [];
        public int Level { get; init; }
        public string Giver { get; init; } = "";
        public string Zone { get; init; } = "";
        public List<QxFlag> Flags { get; init; } = [];
        public List<string> Do { get; init; } = [];
        public List<string> Expect { get; init; } = [];

        // Filled in when read.
        public string World { get; set; } = "";
        public int Step { get; set; }
        public string Haystack { get; set; } = "";
        // What the tick is kept by: the quest code, or the world and name of a quest the server doesn't have.
        public string Key => Code.Length > 0 ? Code : $"{World}|{Name}";
        public bool OnServer => Code.Length > 0;
    }

    private sealed class QxWorld {
        public string Name { get; init; } = "";
        public bool SideWorld { get; init; }
        public List<QxQuest> Quests { get; init; } = [];
    }

    private sealed class QxFile {
        public string Source { get; init; } = "";
        public List<QxWorld> Worlds { get; init; } = [];
    }

    private enum QxRowKind { World, Quest }

    private sealed record QxRow(QxRowKind Kind, string Text, string Line, QxQuest? Quest);

    private const string QX_ONLY_FLAGGED = "Only quests with something to check";
    private static readonly string[] s_qxShow = [ANY_FILTER, "Not tested yet", "Tested"];
    private static readonly string[] s_qxServer = [ANY_FILTER, "On the server", "Not on the server"];

    private readonly TextBox _qxSearch = new() { Watermark = "Search a quest, an NPC, a step, a reward or why it's flagged" };
    private readonly ComboBox _qxWorld = FilterCombo([ANY_FILTER], 170);
    private readonly ComboBox _qxFlag = FilterCombo([ANY_FILTER], 210);
    private readonly ComboBox _qxShowing = FilterCombo(s_qxShow, 150);
    private readonly ComboBox _qxServer = FilterCombo(s_qxServer, 150);
    private readonly CheckBox _qxFlaggedOnly = new() { Content = QX_ONLY_FLAGGED };
    private readonly CheckBox _qxSideWorlds = new() { Content = "Include side worlds", IsChecked = true };
    private readonly TextBlock _qxCount = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _qxList = new() { Background = Brushes.Transparent };
    private readonly StackPanel _qxDetails = new() { Spacing = 6 };

    private List<QxWorld> _qxWorlds = [];
    private List<QxQuest> _qxQuests = [];
    private HashSet<string> _qxDone = new(StringComparer.OrdinalIgnoreCase);
    private string _qxSource = "";
    private QxQuest? _qxPicked;
    private ObservableCollection<QxRow> _qxRows = [];

    private string QuestTestingDonePath => Path.Combine(_manager.InstallFolder, QUEST_TESTING_DONE);

    private Control QuestTestingTab() {
        _qxList.ItemTemplate = new FuncDataTemplate<QxRow>((row, _) => row is null ? new Panel() : QuestTestingRowView(row));
        _qxList.SelectionChanged += (_, e) => {
            if (e.Source != _qxList) {
                return;
            }

            if (_qxList.SelectedItem is QxRow { Quest: { } quest }) {
                if (quest != _qxPicked) {
                    ShowQuestTesting(quest);
                }
            }
            else if (_qxList.SelectedItem is not null) {
                _qxList.SelectedItem = null;
            }
        };
        WhenFiltersChange(RenderQuestTesting, _qxSearch, _qxWorld, _qxFlag, _qxShowing, _qxServer, _qxFlaggedOnly, _qxSideWorlds);

        var next = Primary("Next to test");
        ToolTip.SetTip(next, "Shows the first quest in story order that isn't ticked yet.");

        var details = new ScrollViewer { Width = 470, Margin = new Thickness(14, 0, 0, 0), Content = Card(_qxDetails) };
        ShowQuestTesting(null);

        return new DockPanel {
            Margin = new Thickness(24, 12, 24, 16),
            Children = {
                WithDock(details, Dock.Right),
                WithDock(Fold("flag", "Quests", "Main quest testing",
                    "Every main story quest from the first to the last, in the order you play them, with why each one may not work " +
                    "here, what to do, and what should happen. Tick a quest once it worked.",
                    WithHint(_qxSearch, "Play the story top to bottom and tick each quest once it went the way \"Should happen\" says. " +
                        "Why it's flagged: Rebuilt by us = not recorded from the real game, so steps may point the wrong way; " +
                        "Rewards changed / Teaches a spell = new rewards in v0.10.477 and v0.10.463; Spawns or opens something = a boss, " +
                        "door or scene the quest should now start; Dungeon = its sigil or door; Not on the server = the server doesn't " +
                        "have it, so check the next quest is still offered. Ticks are kept on this PC only."),
                    SearchFilters(
                        SearchFilter("World", _qxWorld),
                        SearchFilter("Why it's flagged", _qxFlag),
                        SearchFilter("Showing", _qxShowing),
                        SearchFilter("On this server", _qxServer),
                        SearchTick(_qxFlaggedOnly),
                        SearchTick(_qxSideWorlds)),
                    new WrapPanel {
                        Children = {
                            Spaced(Action(next, () => {
                                QuestTestingNext();

                                return Task.CompletedTask;
                            })),
                        },
                    },
                    _qxCount), Dock.Top),
                WithMargin(_qxList, new Thickness(0, 14, 0, 0)),
            },
        };
    }

    /// <summary>Reads the built-in list and the ticks once. Runs when the tab shows.</summary>
    private async Task LoadQuestTestingAsync() {
        if (_qxWorlds.Count > 0) {
            return;
        }

        _qxCount.Text = "Loading…";
        var (file, quests) = await Task.Run(ReadQuestTesting);
        _qxWorlds = file?.Worlds ?? [];
        _qxSource = file?.Source ?? "";
        _qxQuests = quests;
        _qxDone = ReadQuestTestingDone();
        FillFilterCombo(_qxWorld, _qxWorlds.Select(w => w.Name));
        FillFilterCombo(_qxFlag, quests.SelectMany(q => q.Flags).GroupBy(f => f.Kind).OrderByDescending(g => g.Count()).Select(g => g.Key));
        RenderQuestTesting();
    }

    private static (QxFile?, List<QxQuest>) ReadQuestTesting() {
        QxFile? file;
        try {
            var assembly = typeof(ManagerWindow).Assembly.GetName().Name;
            using var stream = AssetLoader.Open(new Uri($"avares://{assembly}/Assets/{QUEST_TESTING_ASSET}"));
            using var unzip = new GZipStream(stream, CompressionMode.Decompress);
            file = JsonSerializer.Deserialize<QxFile>(unzip, s_qeJson);
        }
        catch (Exception) {
            // A build without the file, or a broken one.
            return (null, []);
        }

        var all = new List<QxQuest>();
        foreach (var world in file?.Worlds ?? []) {
            foreach (var quest in world.Quests) {
                quest.World = world.Name;
                quest.Step = all.Count + 1;
                quest.Haystack = SearchText.Normalize(string.Join(' ', [
                    quest.Name, quest.Area, quest.Code, string.Join(' ', quest.Copies), quest.Giver, world.Name,
                    string.Join(' ', quest.Flags.Select(f => $"{f.Kind} {f.Why}")), string.Join(' ', quest.Do), string.Join(' ', quest.Expect),
                ]));
                all.Add(quest);
            }
        }

        return (file, all);
    }

    private HashSet<string> ReadQuestTestingDone() {
        try {
            if (File.Exists(QuestTestingDonePath)) {
                var keys = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(QuestTestingDonePath)) ?? [];
                return new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            Say($"Could not read your tested quests ({QUEST_TESTING_DONE}): {ex.Message}", isError: true);
        }

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveQuestTestingDone() {
        try {
            Directory.CreateDirectory(_manager.InstallFolder);
            File.WriteAllText(QuestTestingDonePath, JsonSerializer.Serialize(_qxDone.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Say($"Could not save your tested quests ({QUEST_TESTING_DONE}): {ex.Message}", isError: true);
        }
    }

    private void SetQuestTested(QxQuest quest, bool tested) {
        if (tested ? !_qxDone.Add(quest.Key) : !_qxDone.Remove(quest.Key)) {
            return;
        }

        SaveQuestTestingDone();
        QuestTestingRefreshRow(quest);
        if (_qxPicked == quest) {
            ShowQuestTesting(quest);
        }
    }

    /// <summary>Redraws one quest's row and its world's heading after a tick, without moving the list.</summary>
    private void QuestTestingRefreshRow(QxQuest quest) {
        var index = -1;
        for (var i = 0; i < _qxRows.Count; i++) {
            if (_qxRows[i].Quest == quest) {
                index = i;
                break;
            }
        }

        if (index >= 0) {
            // A tested quest leaves a "Not tested yet" list, and an untested one a "Tested" list.
            if (_qxShowing.SelectedIndex > 0) {
                _qxRows.RemoveAt(index);
                _qxShown--;
            }
            else {
                _qxRows[index] = new QxRow(QxRowKind.Quest, quest.Name, "", quest);
            }

            for (var i = Math.Min(index, _qxRows.Count - 1); i >= 0; i--) {
                if (_qxRows[i].Kind == QxRowKind.World) {
                    var world = _qxWorlds.First(w => w.Name == quest.World);
                    _qxRows[i] = QuestTestingWorldRow(world);
                    break;
                }
            }
        }

        QuestTestingCount(null);
    }

    private QxRow QuestTestingWorldRow(QxWorld w) {
        var onServer = w.Quests.Where(q => q.OnServer).ToList();
        return new QxRow(QxRowKind.World, w.Name + (w.SideWorld ? "  (side world)" : ""),
            $"{onServer.Count(q => _qxDone.Contains(q.Key)):N0} of {onServer.Count:N0} quests on the server tested. " +
            $"{w.Quests.Count - onServer.Count:N0} not on the server.", null);
    }

    private bool QuestTestingMatches(QxQuest quest, string[] words, string? flag) {
        var done = _qxDone.Contains(quest.Key);
        return words.All(w => quest.Haystack.Contains(w, StringComparison.Ordinal))
               && (flag is null || quest.Flags.Any(f => f.Kind == flag))
               && (_qxFlaggedOnly.IsChecked != true || quest.Flags.Count > 0)
               && _qxShowing.SelectedIndex switch {
                   1 => !done,
                   2 => done,
                   _ => true,
               }
               && _qxServer.SelectedIndex switch {
                   1 => quest.OnServer,
                   2 => !quest.OnServer,
                   _ => true,
               };
    }

    private void RenderQuestTesting() {
        if (_qxWorlds.Count == 0) {
            _qxRows = [];
            _qxList.ItemsSource = null;
            _qxCount.Text = "The quest testing list isn't built into this copy of Ahziael. Update Ahziael at the top of the window.";

            return;
        }

        var words = SearchText.Words(_qxSearch.Text);
        var world = FilterPick(_qxWorld);
        var flag = FilterPick(_qxFlag);
        var rows = new List<QxRow>();
        var shown = 0;
        foreach (var w in _qxWorlds) {
            if (world is not null ? w.Name != world : w.SideWorld && _qxSideWorlds.IsChecked != true) {
                continue;
            }

            var matching = w.Quests.Where(q => QuestTestingMatches(q, words, flag)).ToList();
            if (matching.Count == 0) {
                continue;
            }

            rows.Add(QuestTestingWorldRow(w));
            rows.AddRange(matching.Select(q => new QxRow(QxRowKind.Quest, q.Name, "", q)));
            shown += matching.Count;
        }

        _qxRows = new ObservableCollection<QxRow>(rows);
        _qxList.ItemsSource = _qxRows;
        QuestTestingCount(shown);
    }

    private int _qxShown;

    private void QuestTestingCount(int? shown) {
        if (shown is { } s) {
            _qxShown = s;
        }

        var playable = _qxQuests.Where(q => q.OnServer).ToList();
        var done = playable.Count(q => _qxDone.Contains(q.Key));
        _qxCount.Text = $"{FilterCount(_qxShown, _qxQuests.Count, "main story quests")} You've tested {done:N0} of the {playable.Count:N0} on the server" +
                        (playable.Count > 0 ? $" ({done * 100 / playable.Count}%)." : ".") +
                        (_qxSource.Length > 0 ? $" Made from {_qxSource}." : "");
    }

    private Control QuestTestingRowView(QxRow row) {
        if (row.Kind == QxRowKind.World) {
            return new StackPanel {
                Margin = new Thickness(2, 16, 0, 4), Spacing = 2,
                Children = {
                    new TextBlock { Text = row.Text, FontSize = 20, FontWeight = FontWeight.Bold, Foreground = ManagerTheme.Accent("Quests") },
                    Sub(row.Line),
                },
            };
        }

        var quest = row.Quest!;
        var tick = new CheckBox { IsChecked = _qxDone.Contains(quest.Key), VerticalAlignment = VerticalAlignment.Center, IsEnabled = quest.OnServer };
        ToolTip.SetTip(tick, quest.OnServer ? "Tick once this quest worked." : "Not on the server, so there's nothing to test.");
        tick.IsCheckedChanged += (_, _) => SetQuestTested(quest, tick.IsChecked == true);

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(tick);
        title.Children.Add(new TextBlock { Text = $"{quest.Step}.", Foreground = MUTED, FontSize = 14, MinWidth = 40, VerticalAlignment = VerticalAlignment.Center });
        var name = new TextBlock { Text = quest.Name, FontWeight = FontWeight.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
        if (!quest.OnServer) {
            name.Foreground = MUTED;
        }

        title.Children.Add(name);
        // The flags go on a row of their own, wrapping, so a quest with many of them still fits.
        var tags = new WrapPanel { Margin = new Thickness(80, 2, 0, 0) };
        foreach (var kind in quest.Flags.Select(f => f.Kind).Distinct()) {
            tags.Children.Add(WithMargin(QtTag(kind, QuestFlagBrush(kind)), new Thickness(0, 0, 6, 2)));
        }

        var bits = new List<string> { quest.Area };
        if (quest.Giver.Length > 0) {
            bits.Add($"from {quest.Giver}");
        }

        if (quest.Code.Length > 0) {
            bits.Add(quest.Code);
        }

        return new Border {
            Padding = new Thickness(4, 5, 4, 5),
            BorderBrush = LINE,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Opacity = _qxDone.Contains(quest.Key) ? 0.6 : 1,
            Child = new StackPanel {
                Spacing = 2,
                Children = { title, tags, WithMargin(Sub(string.Join("  ·  ", bits.Where(b => b.Length > 0))), new Thickness(80, 0, 0, 0)) },
            },
        };
    }

    // Red = the story may stop here; amber = something new to check; grey = a reminder.
    private static IBrush QuestFlagBrush(string kind) => kind switch {
        "Not on the server" or "No giver" or "Needs a missing quest" or "Step the server can't do" => BAD,
        "Rebuilt by us" or "Click an object" or "Steps differ" or "No real-game copy" or "Not in the story list" or "One per school" => MUTED,
        _ => WARN,
    };

    private void QuestTestingNext() {
        var next = _qxQuests.FirstOrDefault(q => q.OnServer && !_qxDone.Contains(q.Key));
        if (next is null) {
            Say("Every quest on the server is ticked as tested.");

            return;
        }

        // Make sure it shows in the list.
        if (_qxRows.All(r => r.Quest != next)) {
            _qxSearch.Text = "";
            _qxWorld.SelectedIndex = 0;
            _qxFlag.SelectedIndex = 0;
            _qxShowing.SelectedIndex = 0;
            _qxServer.SelectedIndex = 0;
            _qxFlaggedOnly.IsChecked = false;
            _qxSideWorlds.IsChecked = true;
            RenderQuestTesting();
        }

        ShowQuestTesting(next);
        if (_qxRows.FirstOrDefault(r => r.Quest == next) is { } row) {
            _qxList.SelectedItem = row;
            _qxList.ScrollIntoView(row);
        }
    }

    private void ShowQuestTesting(QxQuest? quest) {
        _qxPicked = quest;
        _qxDetails.Children.Clear();
        if (quest is null) {
            _qxDetails.Children.Add(Sub("Click a quest on the left to see why it's flagged, what to do, and what should happen. " +
                                        "Or press Next to test to start where you left off."));

            return;
        }

        void Heading(string text) => _qxDetails.Children.Add(new TextBlock {
            Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.Bold, Foreground = MUTED, Margin = new Thickness(0, 10, 0, 0),
        });

        void Lines(IEnumerable<string> lines) => _qxDetails.Children.Add(new SelectableTextBlock {
            Text = string.Join('\n', lines), TextWrapping = TextWrapping.Wrap, FontSize = 13,
        });

        _qxDetails.Children.Add(new SelectableTextBlock { Text = quest.Name, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        _qxDetails.Children.Add(Sub($"Quest {quest.Step:N0} of {_qxQuests.Count:N0} in the story. {quest.World}, {quest.Area}." +
                                    (quest.Level > 1 ? $" Level {quest.Level}." : "")));

        if (quest.OnServer) {
            var tested = new CheckBox { Content = "Tested: it worked", IsChecked = _qxDone.Contains(quest.Key) };
            tested.IsCheckedChanged += (_, _) => SetQuestTested(quest, tested.IsChecked == true);
            _qxDetails.Children.Add(tested);

            var code = quest.Code;
            var buttons = new WrapPanel();
            var start = Plain("Start");
            ToolTip.SetTip(start, "Gives this quest now (.quest forcegrant), even if the quests before it are not done. " +
                                  "Use it when the story stops here. The wizard picked on the Quests tab gets it.");
            buttons.Children.Add(Spaced(Action(start, () => RunQuestCommand($".quest forcegrant {code}", $"Started {quest.Name}."))));
            if (quest.Zone.Length > 0) {
                var teleport = Plain("Teleport");
                ToolTip.SetTip(teleport, $"Takes you to {quest.Zone}, where {(quest.Giver.Length > 0 ? quest.Giver : "it starts")} is (.tp).");
                buttons.Children.Add(Spaced(Action(teleport, () => RunQuestCommand($".tp {quest.Zone}", $"Teleporting to {quest.Zone}."))));
            }

            var finish = Plain("Finish");
            ToolTip.SetTip(finish, "Ends this quest now (.quest complete) and gives its rewards, to get past a step that won't work.");
            buttons.Children.Add(Spaced(Action(finish, () => RunQuestCommand($".quest complete {code}", $"Finished {quest.Name}."))));
            var editor = Plain("Quest Editor");
            ToolTip.SetTip(editor, "Opens this quest in the Quest Editor, to change its giver, steps or rewards.");
            buttons.Children.Add(Spaced(Action(editor, () => {
                OpenInQuestEditor(code);

                return Task.CompletedTask;
            })));
            buttons.Children.Add(Spaced(Action(Plain("Copy code"), () => CopyAsync(code))));
            _qxDetails.Children.Add(buttons);
        }

        Heading($"Why it's flagged ({quest.Flags.Count})");
        if (quest.Flags.Count == 0) {
            Lines(["Nothing special: recorded from the real game and not changed. Play it through to keep the story going."]);
        }

        foreach (var flag in quest.Flags) {
            _qxDetails.Children.Add(new StackPanel {
                Spacing = 2,
                Children = {
                    new TextBlock { Text = flag.Kind, FontWeight = FontWeight.SemiBold, Foreground = QuestFlagBrush(flag.Kind) },
                    new SelectableTextBlock { Text = flag.Why, TextWrapping = TextWrapping.Wrap, FontSize = 13 },
                },
            });
        }

        if (quest.Do.Count > 0) {
            Heading("What to do");
            Lines(quest.Do);
        }

        Heading("Should happen");
        Lines(quest.Expect.Select(e => "• " + e));

        if (quest.Copies.Count > 0) {
            Heading("Codes");
            Lines([string.Join(", ", new[] { quest.Code }.Concat(quest.Copies))]);
        }
    }

}
