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
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The My Quests tab (Niduus, 2026-11-04: "Make quests render in the UI so that way, players can view their next steps
/// and so on with ability to execute commands to teleport, complete or finish quests" and "Implement quest tree in UI and
/// make it nice and direct so people can visually see what's the next quest that they are on ... auto update based on the
/// client"). Two parts, for the wizard picked at the top (by default whoever is playing on this PC right now):
/// the quest book, each held quest with its steps done, the step it is on and the steps after it, with buttons to go to
/// a step, complete it or finish the quest; and the story path, the world's main story drawn as a line of quests with
/// the done ones ticked, the one you are on lit up and what comes next. Both refresh by themselves every two seconds
/// from the server's quest-progress/&lt;id&gt;.txt (written when a quest or a step starts or ends, and on every zone change).
/// In the game, .questbook says the same in chat.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string MQ_WHERE_YOU_ARE = "Where you are";
    private const int MQ_DONE_SHOWN = 2;
    private const int MQ_LATER_SHOWN = 6;

    private enum MqStep { Done, Now, Later, Unknown }

    private readonly ComboBox _mqWizard = new() { MinWidth = 220, PlaceholderText = "Pick a wizard" };
    private readonly CheckBox _mqFollow = new() { Content = "Follow the wizard playing now", IsChecked = true };
    private readonly ComboBox _mqWorld = new() { Width = 170 };
    private readonly TextBlock _mqStatus = Sub("");
    private readonly StackPanel _mqBook = new() { Spacing = 12 };
    private readonly StackPanel _mqPath = new() { Spacing = 0 };
    private readonly TextBlock _mqPathTitle = new() { FontWeight = FontWeight.SemiBold, FontSize = 15 };
    private readonly ProgressBar _mqPathBar = new() { Minimum = 0, Maximum = 1, Height = 8, MinWidth = 200 };
    private readonly DispatcherTimer _mqTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private HashSet<string> _mqDone = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _mqNow = [];
    private Dictionary<string, HashSet<string>> _mqSteps = new(StringComparer.OrdinalIgnoreCase);
    private string _mqName = "", _mqZone = "", _mqLevel = "", _mqUpdated = "";
    private DateTime _mqWritten;
    private bool _mqFilling, _mqAllDone, _mqAllLater;

    private Control MyQuestsTab() {
        _mqTimer.Tick += (_, _) => LoadMyQuests();
        _mqWizard.SelectionChanged += (_, _) => {
            if (_mqFilling) {
                return;
            }

            // Picking a wizard by hand stops following whoever plays.
            _mqFollow.IsChecked = false;
            _mqWritten = default;
            LoadMqProgress();
            RenderMyQuests();
        };
        _mqFollow.IsCheckedChanged += (_, _) => {
            if (_mqFollow.IsChecked == true) {
                LoadMyQuests(force: true);
            }
        };
        _mqWorld.SelectionChanged += (_, _) => {
            if (!_mqFilling) {
                _mqAllDone = _mqAllLater = false;
                RenderMqPath();
            }
        };

        var refresh = Plain("Refresh");
        ToolTip.SetTip(refresh, "Reads the wizard's quests again now. It also does this by itself every two seconds.");

        return new ScrollViewer {
            Content = new StackPanel {
                Margin = new Thickness(24, 12, 24, 24), Spacing = ManagerTheme.GAP, MaxWidth = ManagerTheme.PAGE_MAX_WIDTH,
                Children = {
                    MissingNote("it doesn't write the quest list this page shows.", () => !_manager.HasQuestGuide),
                    WithHint(new WrapPanel {
                        Children = {
                            Spaced(_mqWizard), Spaced(WithCenter(_mqFollow)),
                            Spaced(Action(refresh, () => {
                                LoadMyQuests(force: true);

                                return Task.CompletedTask;
                            })),
                        },
                    }, "Pick your wizard, or leave \"Follow the wizard playing now\" ticked: the page then shows whoever played last " +
                       "on this PC and changes by itself when you switch wizards. Everything here updates by itself while you play, a " +
                       "moment after a step or a quest starts or ends. The buttons run in the game for that wizard only, so start the " +
                       "server and log in first. Go there teleports you right to where a step happens (.tpstep). Complete this step " +
                       "finishes only that step (.quest completegoalfor). Finish quest completes the whole quest with its rewards " +
                       "(.quest complete). Start gives you a quest now (.quest forcegrant). In the game, type .questbook to see the " +
                       "same in chat."),
                    _mqStatus,
                    Fold("flag", "Quests", "My quest book",
                        "Every quest your wizard has right now: the steps done, the step you are on, and what comes after it.",
                        _mqBook),
                    Fold("sigil", "Quests", "Story path",
                        "The world's main story as a path: the quests done, the one you are on, and the ones that come next.",
                        new WrapPanel {
                            Children = { Spaced(_mqWorld), Spaced(WithCenter(_mqPathTitle)), Spaced(WithCenter(_mqPathBar)) },
                        },
                        _mqPath),
                },
            },
        };
    }

    /// <summary>Reads the quest list and the picked wizard's quests again when the server wrote them since. Runs every 2 s while the tab shows.</summary>
    private void LoadMyQuests(bool force = false) {
        if (force) {
            _questsWritten = default;
            _mqWritten = default;
        }

        var changed = LoadQuestGuide();
        changed |= LoadQuestDetails();
        changed |= FillMqWizards();
        changed |= LoadMqProgress();
        if (changed || force) {
            RenderMyQuests();
        }
    }

    /// <summary>Lists the wizards; while following, picks the one on this PC whose quests changed last. True when anything changed.</summary>
    private bool FillMqWizards() {
        var wizards = ReadQuestWizards();
        var current = _mqWizard.ItemsSource as List<QuestWizard> ?? [];
        var picked = (_mqWizard.SelectedItem as QuestWizard)?.File;
        var want = picked;
        if (_mqFollow.IsChecked == true || picked is null) {
            want = (wizards.Where(w => w.OnThisPc).MaxBy(w => w.Written) ?? wizards.MaxBy(w => w.Written))?.File;
        }

        var sameList = current.Select(w => (w.File, w.Name, w.OnThisPc)).SequenceEqual(wizards.Select(w => (w.File, w.Name, w.OnThisPc)));
        if (sameList && want == picked) {
            return false;
        }

        _mqFilling = true;
        try {
            if (!sameList) {
                _mqWizard.ItemsSource = wizards;
            }

            _mqWizard.SelectedItem = wizards.FirstOrDefault(w => w.File == want) ?? wizards.FirstOrDefault();
        }
        finally {
            _mqFilling = false;
        }

        if (want != picked) {
            _mqWritten = default;
            _mqAllDone = _mqAllLater = false;
            SelectMqWorld(MQ_WHERE_YOU_ARE);
        }

        return true;
    }

    /// <summary>Reads the picked wizard's finished quests, held quests and the steps they are on. True when they changed.</summary>
    private bool LoadMqProgress() {
        if (_mqWizard.SelectedItem is not QuestWizard wizard) {
            var had = _mqDone.Count + _mqNow.Count > 0 || _mqName.Length > 0;
            (_mqDone, _mqNow, _mqSteps) = (new(StringComparer.OrdinalIgnoreCase), [], new(StringComparer.OrdinalIgnoreCase));
            (_mqName, _mqZone, _mqLevel, _mqUpdated) = ("", "", "", "");

            return had;
        }

        try {
            var written = File.GetLastWriteTimeUtc(wizard.File);
            if (written == _mqWritten) {
                return false;
            }

            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var now = new List<string>();
            var steps = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            string name = "", zone = "", level = "", updated = "";
            foreach (var line in File.ReadLines(wizard.File)) {
                var parts = line.Split('\t');
                if (parts.Length < 2) {
                    continue;
                }

                switch (parts[0]) {
                    case "name": name = parts[1]; break;
                    case "zone": zone = parts[1]; break;
                    case "level": level = parts[1]; break;
                    case "updated": updated = parts[1]; break;
                    case "done": done.Add(parts[1]); break;
                    case "now": now.Add(parts[1]); break;
                    case "step" when parts.Length >= 3:
                        if (!steps.TryGetValue(parts[1], out var set)) {
                            steps[parts[1]] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        }

                        set.Add(parts[2]);
                        break;
                }
            }

            (_mqDone, _mqNow, _mqSteps, _mqWritten) = (done, now, steps, written);
            (_mqName, _mqZone, _mqLevel, _mqUpdated) = (name, zone, level, updated);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    private QuestInfo? MqQuest(string code) => _quests.FirstOrDefault(q => q.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

    private QuestState MqStateOf(QuestInfo quest) =>
        _mqNow.Contains(quest.Code, StringComparer.OrdinalIgnoreCase) ? QuestState.InProgress
        : _mqDone.Contains(quest.Code) ? QuestState.Done
        : quest.Needs.All(_mqDone.Contains) ? QuestState.NextUp
        : QuestState.Later;

    private void RenderMyQuests() {
        if (_mqWizard.SelectedItem is not QuestWizard) {
            _mqStatus.Text = "No wizard has played on this server yet. Start the server and log in once: your quests show here by themselves.";
        }
        else {
            var bits = new List<string> { _mqName.Length > 0 ? _mqName : "Your wizard" };
            if (_mqLevel.Length > 0 && _mqLevel != "0") {
                bits.Add($"level {_mqLevel}");
            }

            if (_mqZone.Length > 0) {
                bits.Add($"in {_mqZone}");
            }

            bits.Add($"{_mqDone.Count:N0} quests done");
            if (_mqUpdated.Length > 0) {
                bits.Add($"last change {_mqUpdated}");
            }

            _mqStatus.Text = string.Join("  ·  ", bits) + (_mqFollow.IsChecked == true ? "  ·  following the wizard playing now" : "");
        }

        RenderMqBook();
        FillMqWorlds();
        RenderMqPath();
    }

    // ---------------------------------------------------------------------
    // The quest book
    // ---------------------------------------------------------------------

    private void RenderMqBook() {
        _mqBook.Children.Clear();
        if (_mqNow.Count == 0) {
            _mqBook.Children.Add(Sub(_mqWizard.SelectedItem is QuestWizard
                ? "Your quest book is empty. The story path below shows the quest to start next."
                : "Nothing yet."));

            return;
        }

        // Main story first, then the rest in the order they are played.
        var codes = _mqNow
            .Select(code => (Code: code, Info: MqQuest(code)))
            .OrderByDescending(q => q.Info?.Main ?? q.Code.Contains("MAIN", StringComparison.OrdinalIgnoreCase))
            .ThenBy(q => q.Info?.Order ?? int.MaxValue)
            .ToList();
        foreach (var (code, info) in codes) {
            _mqBook.Children.Add(MqQuestCard(code, info));
        }
    }

    private Control MqQuestCard(string code, QuestInfo? info) {
        var accent = ManagerTheme.Accent("Quests");
        var title = info?.Title ?? (_qeByCode.TryGetValue(code, out var qe) ? qe.Title : code);
        var card = new StackPanel { Spacing = 6 };
        card.Children.Add(new WrapPanel {
            Children = {
                WithMargin(new SelectableTextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 16 }, new Thickness(0, 0, 10, 0)),
                WithMargin(QtTag(info?.Main == true ? "Main story" : "Side quest", info?.Main == true ? accent : MUTED), new Thickness(0, 0, 8, 0)),
                WithCenter(new TextBlock {
                    Text = string.Join("  ·  ", new[] { info?.World ?? "", info is { Level: > 0 } ? $"level {info.Level}" : "", code }.Where(t => t.Length > 0)),
                    Foreground = MUTED,
                }),
            },
        });

        var held = _mqSteps.GetValueOrDefault(code);
        if (_qeByCode.TryGetValue(code, out var full) && full.Goals.Count > 0) {
            var now = Enumerable.Range(0, full.Goals.Count).Where(i => held?.Contains(full.Goals[i].Name) == true).ToList();
            var first = now.Count > 0 ? now.Min() : -1;
            for (var i = 0; i < full.Goals.Count; i++) {
                var state = first < 0 ? MqStep.Unknown : now.Contains(i) ? MqStep.Now : i < first ? MqStep.Done : MqStep.Later;
                var goal = full.Goals[i];
                var text = goal.Text + (goal.Place.Length > 0 && !goal.Text.Contains(goal.Place, StringComparison.OrdinalIgnoreCase) ? $" ({goal.Place})" : "");
                card.Children.Add(MqStepRow(code, i + 1, full.Goals.Count, text, state));
            }

            if (first < 0) {
                card.Children.Add(Sub("The server didn't say which step you are on yet. It will after your next step or zone change; type .stuck in the game if the quest seems stuck."));
            }
        }
        else {
            card.Children.Add(Sub("This quest's steps aren't in the server's quest list, so only the buttons below work for it."));
        }

        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (full is null || full.Goals.Count == 0) {
            var step = Plain("Complete the step I'm on");
            ToolTip.SetTip(step, "Finishes only the step you are on (.quest completegoalfor), so the quest moves on.");
            buttons.Children.Add(Spaced(Action(step, () => RunMq($".quest completegoalfor {code}", $"Completing the step you are on in {title}."))));
        }

        var finish = Plain("Finish quest");
        ToolTip.SetTip(finish, "Completes the whole quest now, with its rewards (.quest complete).");
        buttons.Children.Add(Spaced(Action(finish, () => RunMq($".quest complete {code}", $"Finishing {title}. Its rewards show in the game."))));
        if (info is { Zone.Length: > 0 }) {
            var giver = Plain("Go to the giver");
            ToolTip.SetTip(giver, $"Takes you to {info.Zone}{(info.Giver.Length > 0 ? $", where {info.Giver} is" : "")} (.tp).");
            buttons.Children.Add(Spaced(Action(giver, () => RunMq($".tp {info.Zone}", $"Teleporting to {info.Zone}."))));
        }

        card.Children.Add(buttons);

        // What it opens once it is done.
        var after = _quests.Where(q => q.Needs.Contains(code, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(q => q.Main).ThenBy(q => q.Order).Take(4).Select(q => q.Title).ToList();
        if (after.Count > 0) {
            card.Children.Add(Sub($"When it's done, next: {string.Join(", ", after)}."));
        }

        return new Border {
            Background = ManagerTheme.SURFACE_BRUSH, BorderBrush = ManagerTheme.Brush(ManagerTheme.AccentColor("Quests"), 90),
            BorderThickness = new Thickness(3, 1, 1, 1), CornerRadius = new CornerRadius(ManagerTheme.RADIUS), Padding = new Thickness(14, 10),
            Child = card,
        };
    }

    /// <summary>One step of a held quest: a tick, a lit dot or an empty dot, its words, and buttons on the step you are on.</summary>
    private Control MqStepRow(string code, int step, int steps, string text, MqStep state) {
        var accent = ManagerTheme.Accent("Quests");
        var (mark, brush) = state switch {
            MqStep.Done => ("✓", GOOD),
            MqStep.Now => ("●", accent),
            _ => ("○", MUTED),
        };

        var words = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock {
            Text = state == MqStep.Now ? $"STEP {step} OF {steps} · YOU ARE HERE" : state == MqStep.Later && step > 0 ? $"Step {step} · coming up" : $"Step {step}",
            FontSize = 11, FontWeight = FontWeight.Bold, Foreground = brush,
        });
        var stepText = new SelectableTextBlock {
            Text = text, TextWrapping = TextWrapping.Wrap,
            FontWeight = state == MqStep.Now ? FontWeight.SemiBold : FontWeight.Normal,
        };
        if (state == MqStep.Done) {
            stepText.Foreground = MUTED;
            stepText.TextDecorations = TextDecorations.Strikethrough;
        }

        words.Children.Add(stepText);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (state == MqStep.Now) {
            var go = Primary("Go there");
            ToolTip.SetTip(go, "Teleports you right to where this step happens: where the quest arrow points, or where its NPC stands (.tpstep).");
            buttons.Children.Add(Action(go, () => RunMq($".tpstep {code} {step}", $"Going to step {step}: {text}.")));
            var complete = Plain("Complete this step");
            ToolTip.SetTip(complete, "Finishes only this step (.quest completegoalfor), so the quest moves on to the next one.");
            buttons.Children.Add(Action(complete, () => RunMq($".quest completegoalfor {code}", $"Completing step {step}: {text}. The quest moves on.")));
        }
        else {
            var go = Plain("Go");
            go.Padding = new Thickness(10, 3);
            ToolTip.SetTip(go, $"Teleports you to where step {step} happens (.tpstep), even if you are not on it yet.");
            buttons.Children.Add(Action(go, () => RunMq($".tpstep {code} {step}", $"Going to step {step}: {text}.")));
        }

        var dot = new TextBlock {
            Text = mark, Foreground = brush, FontSize = state == MqStep.Now ? 18 : 15, Width = 26, TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        return new Border {
            Padding = new Thickness(8, 6), CornerRadius = new CornerRadius(8),
            Background = state == MqStep.Now ? ManagerTheme.Brush(ManagerTheme.AccentColor("Quests"), 34) : Brushes.Transparent,
            BorderBrush = state == MqStep.Now ? accent : Brushes.Transparent, BorderThickness = new Thickness(1.5),
            Child = new DockPanel { Children = { WithDock(dot, Dock.Left), WithDock(buttons, Dock.Right), WithMargin(words, new Thickness(6, 0, 10, 0)) } },
        };
    }

    // ---------------------------------------------------------------------
    // The story path
    // ---------------------------------------------------------------------

    private void FillMqWorlds() {
        var worlds = _quests.Where(q => q.Main).Select(q => q.World).Distinct().OrderBy(WorldOrder)
            .ThenBy(w => w, StringComparer.OrdinalIgnoreCase).ToList();
        var items = new[] { MQ_WHERE_YOU_ARE }.Concat(worlds).ToList();
        var current = _mqWorld.ItemsSource as List<string> ?? [];
        if (current.SequenceEqual(items)) {
            return;
        }

        var picked = _mqWorld.SelectedItem as string;
        _mqFilling = true;
        try {
            _mqWorld.ItemsSource = items;
            _mqWorld.SelectedItem = picked is not null && items.Contains(picked) ? picked : MQ_WHERE_YOU_ARE;
        }
        finally {
            _mqFilling = false;
        }
    }

    private void SelectMqWorld(string world) {
        if (_mqWorld.ItemsSource is List<string> items && items.Contains(world)) {
            _mqFilling = true;
            try {
                _mqWorld.SelectedItem = world;
            }
            finally {
                _mqFilling = false;
            }
        }
    }

    /// <summary>The world the wizard is in the story of: their main quest's, else the next main quest's, else the first.</summary>
    private string MqWhereYouAre() {
        var mains = _quests.Where(q => q.Main).ToList();
        var held = mains.FirstOrDefault(q => _mqNow.Contains(q.Code, StringComparer.OrdinalIgnoreCase));
        if (held is not null) {
            return held.World;
        }

        // The furthest world with a main quest done that still has quests left, else the first world with any left.
        var lastDone = mains.LastOrDefault(q => _mqDone.Contains(q.Code));
        if (lastDone is not null && mains.Any(q => q.World == lastDone.World && !_mqDone.Contains(q.Code))) {
            return lastDone.World;
        }

        return mains.FirstOrDefault(q => !_mqDone.Contains(q.Code) && MqStateOf(q) == QuestState.NextUp)?.World
               ?? mains.FirstOrDefault()?.World ?? "";
    }

    private void RenderMqPath() {
        _mqPath.Children.Clear();
        var picked = _mqWorld.SelectedItem as string ?? MQ_WHERE_YOU_ARE;
        var world = picked == MQ_WHERE_YOU_ARE ? MqWhereYouAre() : picked;
        var mains = _quests.Where(q => q.Main && q.World == world).ToList();
        if (mains.Count == 0) {
            _mqPathTitle.Text = "";
            _mqPathBar.IsVisible = false;
            _mqPath.Children.Add(Sub(_quests.Count == 0
                ? "No quest list yet. Start the server once: it writes the list a little after it starts (with the ID lookup on)."
                : "No main story quests in this world on the server."));

            return;
        }

        var states = mains.Select(MqStateOf).ToList();
        var doneCount = states.Count(s => s == QuestState.Done);
        _mqPathTitle.Text = $"{world}: {doneCount:N0} of {mains.Count:N0} main story quests done";
        _mqPathBar.IsVisible = true;
        _mqPathBar.Value = (double) doneCount / mains.Count;

        // The quest you are on: one in your quest book, else the first one you can start.
        var focus = states.IndexOf(QuestState.InProgress);
        if (focus < 0) {
            focus = states.IndexOf(QuestState.NextUp);
        }

        if (focus < 0) {
            focus = states.FindIndex(s => s != QuestState.Done);
        }

        if (focus < 0) {
            _mqPath.Children.Add(WithMargin(Sub($"Every main story quest of {world} is done. Pick the next world above to see its path."), new Thickness(0, 0, 0, 8)));
            focus = mains.Count;
        }

        var firstShown = _mqAllDone ? 0 : Math.Max(0, focus - MQ_DONE_SHOWN);
        var lastShown = _mqAllLater ? mains.Count - 1 : Math.Min(mains.Count - 1, focus + MQ_LATER_SHOWN);
        if (firstShown > 0) {
            var more = Plain($"✓  Show the {firstShown:N0} quest{(firstShown == 1 ? "" : "s")} before these");
            more.Click += (_, _) => {
                _mqAllDone = true;
                RenderMqPath();
            };
            _mqPath.Children.Add(MqPathNode(null, QuestState.Done, false, top: false, bottom: true, more));
        }

        for (var i = firstShown; i <= lastShown; i++) {
            _mqPath.Children.Add(MqPathNode(mains[i], states[i], i == focus, top: i > 0, bottom: i < mains.Count - 1, null));
        }

        if (lastShown < mains.Count - 1) {
            var left = mains.Count - 1 - lastShown;
            var more = Plain($"Show the {left:N0} quest{(left == 1 ? "" : "s")} after these");
            more.Click += (_, _) => {
                _mqAllLater = true;
                RenderMqPath();
            };
            _mqPath.Children.Add(MqPathNode(null, QuestState.Later, false, top: true, bottom: false, more));
        }
        else if (focus < mains.Count) {
            var nextWorld = _quests.Where(q => q.Main && WorldOrder(q.World) > WorldOrder(world)).OrderBy(q => WorldOrder(q.World)).FirstOrDefault()?.World;
            if (nextWorld is not null) {
                _mqPath.Children.Add(WithMargin(Sub($"Then the story goes on in {nextWorld}."), new Thickness(44, 6, 0, 0)));
            }
        }
    }

    /// <summary>
    /// One stop on the story path: a line down the left joining the quests, a round mark (tick, lit or empty), and the
    /// quest with its buttons. The quest you are on is lit up and larger; the ones you can start now say Next.
    /// </summary>
    private Control MqPathNode(QuestInfo? quest, QuestState state, bool focus, bool top, bool bottom, Control? instead) {
        var accent = ManagerTheme.Accent("Quests");
        var (mark, brush) = state switch {
            QuestState.Done => ("✓", GOOD),
            QuestState.InProgress => ("★", accent),
            QuestState.NextUp => ("→", WARN),
            _ => ("", MUTED),
        };
        var size = focus ? 30.0 : 22.0;
        var circle = new Border {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, focus ? 12 : 10, 0, 0),
            Background = state is QuestState.Done or QuestState.InProgress || focus ? brush : ManagerTheme.SURFACE_HIGH_BRUSH,
            BorderBrush = brush, BorderThickness = new Thickness(2),
            Child = new TextBlock {
                Text = mark, FontSize = focus ? 15 : 12, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = state is QuestState.Done or QuestState.InProgress || focus ? Brushes.White : brush,
            },
        };
        var rail = new Grid { Width = 40 };
        rail.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        rail.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        if (top) {
            rail.Children.Add(new Rectangle { Width = 3, Fill = state == QuestState.Done || focus ? GOOD : LINE, HorizontalAlignment = HorizontalAlignment.Center });
        }

        if (bottom) {
            var line = new Rectangle { Width = 3, Fill = state == QuestState.Done ? GOOD : LINE, HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetRow(line, 1);
            rail.Children.Add(line);
        }

        Grid.SetRowSpan(circle, 2);
        rail.Children.Add(circle);

        Control body;
        if (quest is null) {
            body = WithMargin(instead ?? new Panel(), new Thickness(0, 8, 0, 8));
        }
        else {
            body = MqPathCard(quest, state, focus);
        }

        return new DockPanel { Children = { WithDock(rail, Dock.Left), WithMargin(body, new Thickness(8, 4, 0, 4)) } };
    }

    private Control MqPathCard(QuestInfo quest, QuestState state, bool focus) {
        var accent = ManagerTheme.Accent("Quests");
        var stack = new StackPanel { Spacing = 3 };
        var head = new WrapPanel();
        if (focus) {
            head.Children.Add(WithMargin(QtTag(state == QuestState.InProgress ? "YOU ARE HERE" : "START HERE", accent), new Thickness(0, 0, 8, 0)));
        }
        else if (state == QuestState.NextUp) {
            head.Children.Add(WithMargin(QtTag("Next", WARN), new Thickness(0, 0, 8, 0)));
        }
        else if (state == QuestState.InProgress) {
            head.Children.Add(WithMargin(QtTag("In your quest book", accent), new Thickness(0, 0, 8, 0)));
        }

        var title = new SelectableTextBlock {
            Text = quest.Title, FontSize = focus ? 17 : 14,
            FontWeight = focus ? FontWeight.Bold : state == QuestState.Done ? FontWeight.Normal : FontWeight.SemiBold,
        };
        if (state is QuestState.Done or QuestState.Later && !focus) {
            title.Foreground = MUTED;
        }

        head.Children.Add(WithMargin(title, new Thickness(0, 0, 10, 0)));
        head.Children.Add(WithCenter(new TextBlock { Text = string.Join("  ·  ", new[] { quest.Level > 0 ? $"level {quest.Level}" : "", quest.Code }.Where(t => t.Length > 0)), Foreground = MUTED, FontSize = 12 }));
        stack.Children.Add(head);

        if (state != QuestState.Done || focus) {
            var where = quest.ZoneFrom switch {
                "auto" => "Starts by itself when the quest before it is done.",
                "npc" => $"Given by {(quest.Giver.Length > 0 ? quest.Giver : "an NPC")}{(quest.Zone.Length > 0 ? $" in {quest.Zone}" : "")}.",
                _ => quest.Giver.Length > 0 ? $"Given by {quest.Giver}." : "",
            };
            if (where.Length > 0) {
                stack.Children.Add(Sub(where));
            }
        }

        // The step you are on, right on the path.
        if (state == QuestState.InProgress && _qeByCode.TryGetValue(quest.Code, out var full) && full.Goals.Count > 0) {
            var held = _mqSteps.GetValueOrDefault(quest.Code);
            var step = full.Goals.FindIndex(g => held?.Contains(g.Name) == true);
            if (step >= 0) {
                stack.Children.Add(new TextBlock {
                    Text = $"Step {step + 1} of {full.Goals.Count}: {full.Goals[step].Text}", Foreground = accent, FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                });
                if (step + 1 < full.Goals.Count) {
                    stack.Children.Add(Sub($"Then: {full.Goals[step + 1].Text}"));
                }
            }
        }

        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var code = quest.Code;
        if (state == QuestState.InProgress) {
            var held = _mqSteps.GetValueOrDefault(code);
            var step = _qeByCode.TryGetValue(code, out var full2) ? full2.Goals.FindIndex(g => held?.Contains(g.Name) == true) : -1;
            if (step >= 0) {
                var go = Primary("Go to my step");
                ToolTip.SetTip(go, "Teleports you right to where the step you are on happens (.tpstep).");
                buttons.Children.Add(Spaced(Action(go, () => RunMq($".tpstep {code} {step + 1}", $"Going to step {step + 1} of {quest.Title}."))));
            }

            var complete = Plain("Complete this step");
            ToolTip.SetTip(complete, "Finishes only the step you are on (.quest completegoalfor).");
            buttons.Children.Add(Spaced(Action(complete, () => RunMq($".quest completegoalfor {code}", $"Completing the step you are on in {quest.Title}."))));
            var finish = Plain("Finish quest");
            ToolTip.SetTip(finish, "Completes the whole quest now, with its rewards (.quest complete).");
            buttons.Children.Add(Spaced(Action(finish, () => RunMq($".quest complete {code}", $"Finishing {quest.Title}. Its rewards show in the game."))));
        }
        else if (state != QuestState.Done) {
            var start = state == QuestState.NextUp ? Primary("Start") : Plain("Start");
            ToolTip.SetTip(start, state == QuestState.NextUp
                ? "Gives you this quest now (.quest forcegrant)."
                : "Gives you this quest now (.quest forcegrant), even though the quests before it are not done.");
            buttons.Children.Add(Spaced(Action(start, () => RunMq($".quest forcegrant {code}", $"Started {quest.Title}. It is in your quest book now."))));
            if (quest.Zone.Length > 0) {
                var giver = Plain("Go to the giver");
                ToolTip.SetTip(giver, $"Takes you to {quest.Zone}{(quest.Giver.Length > 0 ? $", where {quest.Giver} is" : "")} (.tp).");
                buttons.Children.Add(Spaced(Action(giver, () => RunMq($".tp {quest.Zone}", $"Teleporting to {quest.Zone}."))));
            }
        }

        if (buttons.Children.Count > 0) {
            stack.Children.Add(buttons);
        }

        // Side quests this one opens, for the quest you are on and the next ones.
        if (focus || state == QuestState.NextUp) {
            var sides = _quests.Where(q => !q.Main && q.Needs.Contains(code, StringComparer.OrdinalIgnoreCase) && !_mqDone.Contains(q.Code))
                .Select(q => q.Title).Distinct().ToList();
            if (sides.Count > 0) {
                stack.Children.Add(Sub($"Opens side quest{(sides.Count == 1 ? "" : "s")}: {string.Join(", ", sides.Take(4))}{(sides.Count > 4 ? $" and {sides.Count - 4} more" : "")}."));
            }
        }

        return new Border {
            Padding = focus ? new Thickness(14, 10) : new Thickness(10, 6), CornerRadius = new CornerRadius(10),
            Background = focus ? ManagerTheme.Brush(ManagerTheme.AccentColor("Quests"), 34) : state == QuestState.Done ? Brushes.Transparent : ManagerTheme.SURFACE_BRUSH,
            BorderBrush = focus ? accent : state == QuestState.NextUp ? ManagerTheme.Brush(ManagerTheme.WARN, 120) : Brushes.Transparent,
            BorderThickness = new Thickness(focus ? 2 : 1),
            Child = stack,
        };
    }

    // Only the wizard picked at the top runs the buttons' commands.
    private Task RunMq(string command, string said) {
        var wizard = _mqWizard.SelectedItem as QuestWizard;
        _manager.SendCommand(command, wizard is null ? null : Path.GetFileNameWithoutExtension(wizard.File));
        Say(said + (wizard is { Name.Length: > 0 } ? $" Only {wizard.Name} gets it." : "") + " The game's chat says if it worked.");

        return Task.CompletedTask;
    }

}
