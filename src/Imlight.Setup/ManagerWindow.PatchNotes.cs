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
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// One patch note, read from a release's "What's new" line. Release notes are written as
/// "New: Title. What it does in plain words | Where: Game Rules › What NPCs Give | Test: how to try it"
/// (docs/release-notes/README.md); older ones were only a change's title, so their kind is guessed from their words.
/// </summary>
internal sealed record PatchNote(Version Version, string Kind, string Title, string Detail, string Where, string Test) {

    internal const string NEW = "New";
    internal const string FIXED = "Fixed";
    internal const string CHANGED = "Changed";

    private static readonly Regex s_kind = new(@"^\**\s*(New|Fixed|Fix|Changed|Change)\s*:\s*\**\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_fixWords = new(@"\b(fix|fixes|fixed|repair|repaired|no longer|stuck|broken|crash|crashed|wrong|missing|instead of|invisible wall|glitch|bug|freeze|froze|frozen|did nothing|didn't|doesn't|not working|lost|hang|hung)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_newWords = new(@"^(add|adds|added|new|make|makes|let|lets|show|shows|give|gives|allow|allows|build|builds|bring|brings)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static PatchNote Parse(Version version, string line) {
        var parts = line.Split(" | ");
        var main = parts[0].Trim();
        string? kind = null;
        var match = s_kind.Match(main);
        if (match.Success) {
            var word = match.Groups[1].Value.ToLowerInvariant();
            kind = word.StartsWith("fix", StringComparison.Ordinal) ? FIXED : word.StartsWith("change", StringComparison.Ordinal) ? CHANGED : NEW;
            main = main[match.Length..].Trim();
        }

        kind ??= s_fixWords.IsMatch(main) ? FIXED : s_newWords.IsMatch(main) ? NEW : CHANGED;

        // The first sentence is the title; the rest says more.
        var title = main;
        var detail = "";
        var stop = main.IndexOf(". ", StringComparison.Ordinal);
        if (stop > 0 && stop < main.Length - 2) {
            title = main[..stop];
            detail = main[(stop + 2)..].Trim();
        }

        var where = "";
        var test = "";
        foreach (var part in parts.Skip(1).Select(p => p.Trim())) {
            if (part.StartsWith("Where:", StringComparison.OrdinalIgnoreCase)) {
                where = part[6..].Trim().Replace(" > ", "  ›  ", StringComparison.Ordinal).Replace(" › ", "  ›  ", StringComparison.Ordinal);
            }
            else if (part.StartsWith("Test:", StringComparison.OrdinalIgnoreCase)) {
                test = part[5..].Trim();
            }
            else if (part.StartsWith("How to test:", StringComparison.OrdinalIgnoreCase)) {
                test = part[12..].Trim();
            }
            else {
                detail = (detail + " " + part).Trim();
            }
        }

        return new PatchNote(version, kind, title.TrimEnd('.'), detail, where, test);
    }

    /// <summary>The tab its Where line names (the last part of "Game Rules › What NPCs Give"), if there is one.</summary>
    internal string? Tab(Func<string, bool> isTab) {
        if (Where.Length == 0) {
            return null;
        }

        var last = Where.Split(['›', '>']).Last().Trim();

        return isTab(last) ? last : isTab(Where.Trim()) ? Where.Trim() : null;
    }

    /// <summary>What a Tested tick is kept by.</summary>
    internal string Key => $"{Version}|{Title}";

}

/// <summary>
/// The What's New page (Niduus, 2026-11-09, mockup "Patch notes 1": "Have actual notes for patches"): versions down the
/// left with what each holds, and on the right each change as a card that says New, Fixed or Changed, in plain words, with
/// a button that opens where it is, how to test it, and a Tested tick that is kept on this PC.
/// </summary>
internal sealed partial class ManagerWindow {

    // Ticks of the notes tried on this PC ("version|title" a line), and the version used before the last update.
    private const string NOTES_TESTED = "whats-new-tested.txt";
    private const string NOTES_PREVIOUS = "whats-new-previous.txt";
    private const string PICK_ALL = "all";
    private const string PICK_SINCE = "since";

    /// <summary>One line of the notes list: a version's heading (Note is null) or one of its notes.</summary>
    private sealed record NoteRow(Version Version, PatchNote? Note);

    /// <summary>One line of the versions list: what it shows, and its words.</summary>
    private sealed record VersionPick(string Key, string Label, string Line, bool Yours);

    private readonly TextBox _notesSearch = new() { Watermark = "Search the notes, such as quests, sigil, crafting or 0.10.700" };
    private readonly TextBlock _notesCount = Sub("");
    private readonly ToggleSwitch _whatsNewPopup = new() { OnContent = "On", OffContent = "Off" };
    private readonly DispatcherTimer _notesDelay = new() { Interval = TimeSpan.FromMilliseconds(200) };
    // Only the rows in view are drawn (recycling, virtualizing lists), however many versions there are.
    private readonly ListBox _notesList = new() { Background = Brushes.Transparent, ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()) };
    private readonly ListBox _notesVersions = new() { Background = Brushes.Transparent, ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()) };
    private readonly StackPanel _notesKinds = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _notesHeading = new() { FontSize = 22, FontWeight = FontWeight.Bold };
    private List<ReleaseNotes> _notes = [];
    private bool _notesFetched;
    private string _notesKind = "All";
    private string? _notesPick;
    private bool _notesPicking;
    private HashSet<string>? _notesTested;

    private bool WhatsNewPopupOff => File.Exists(Path.Combine(_manager.InstallFolder, WHATS_NEW_OFF));

    private Control WhatsNewPage() {
        _whatsNewPopup.IsChecked = !WhatsNewPopupOff;
        _whatsNewPopup.IsCheckedChanged += (_, _) => Guard(() => {
            var file = Path.Combine(_manager.InstallFolder, WHATS_NEW_OFF);
            if (_whatsNewPopup.IsChecked == true) {
                File.Delete(file);
                Say("The What's New window opens again after each update.");
            }
            else {
                File.WriteAllText(file, "The What's New window is turned off. Delete this file, or turn it on in the app, to see it again.");
                Say("The What's New window no longer opens after an update. The notes stay on this page.");
            }

            return Task.CompletedTask;
        });
        _notesDelay.Tick += (_, _) => {
            _notesDelay.Stop();
            RenderNotes();
        };
        _notesSearch.TextChanged += (_, _) => {
            _notesDelay.Stop();
            _notesDelay.Start();
        };
        _notesList.ItemTemplate = new FuncDataTemplate<NoteRow>((row, _) => NoteView(row), supportsRecycling: false);
        _notesVersions.ItemTemplate = new FuncDataTemplate<VersionPick>((pick, _) => VersionView(pick), supportsRecycling: false);
        _notesVersions.SelectionChanged += (_, _) => {
            if (!_notesPicking && _notesVersions.SelectedItem is VersionPick pick) {
                _notesPick = pick.Key;
                RenderNotes();
            }
        };
        RenderKindChips();

        var popupRow = new DockPanel {
            Margin = new Thickness(6, 10, 6, 2),
            Children = {
                WithDock(_whatsNewPopup, Dock.Right),
                new TextBlock { Text = "Pop-up after an update", VerticalAlignment = VerticalAlignment.Center, FontSize = ManagerTheme.FONT_SMALL, Foreground = MUTED, TextWrapping = TextWrapping.Wrap },
            },
        };
        ToolTip.SetTip(popupRow, "On: the What's New window opens the first time the app opens after an update. Off: it stays shut; the notes are always here.");

        var left = Card(new DockPanel {
            Children = {
                WithDock(new TextBlock { Text = "VERSIONS", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = MUTED, Margin = new Thickness(6, 0, 0, 8) }, Dock.Top),
                WithDock(popupRow, Dock.Bottom),
                _notesVersions,
            },
        });
        left.Padding = new Thickness(8, 12);
        left.Width = 240;
        left.Margin = new Thickness(0, 0, 14, 0);

        var right = Card(new DockPanel {
            Children = {
                WithDock(new DockPanel {
                    Children = {
                        WithDock(_notesCount, Dock.Right),
                        _notesHeading,
                    },
                }, Dock.Top),
                _notesList,
            },
        });
        _notesCount.VerticalAlignment = VerticalAlignment.Center;

        return new DockPanel {
            Margin = new Thickness(24, 12, 24, 16), MaxWidth = ManagerTheme.PAGE_MAX_WIDTH,
            Children = {
                WithDock(new DockPanel {
                    Margin = new Thickness(0, 0, 0, 12),
                    Children = {
                        WithDock(WithMargin(Action(Plain("Refresh"), () => LoadNotesAsync(refresh: true)), new Thickness(8, 0, 0, 0)), Dock.Right),
                        WithDock(WithMargin(_notesKinds, new Thickness(10, 0, 0, 0)), Dock.Right),
                        _notesSearch,
                    },
                }, Dock.Top),
                WithDock(left, Dock.Left),
                right,
            },
        };
    }

    /// <summary>All, New, Fixed and Changed, one of them lit.</summary>
    private void RenderKindChips() {
        _notesKinds.Children.Clear();
        foreach (var kind in new[] { "All", PatchNote.NEW, PatchNote.FIXED, PatchNote.CHANGED }) {
            var lit = kind == _notesKind;
            var chip = new Button {
                Classes = { "ui" }, Content = kind, CornerRadius = new CornerRadius(ManagerTheme.RADIUS_PILL), Padding = new Thickness(14, 0),
                Background = lit ? ManagerTheme.Brush(ManagerTheme.ACCENT, 200) : ManagerTheme.SURFACE_HIGH_BRUSH,
                BorderBrush = lit ? ManagerTheme.Brush(ManagerTheme.ACCENT_LIGHT) : LINE, BorderThickness = new Thickness(1),
                FontWeight = FontWeight.SemiBold,
            };
            var pick = kind;
            chip.Click += (_, _) => {
                _notesKind = pick;
                RenderKindChips();
                RenderNotes();
            };
            _notesKinds.Children.Add(chip);
        }
    }

    /// <summary>Opens What's New on one version's notes (the search window's patch notes open here).</summary>
    private void OpenNotesAt(Version version) {
        _notesPick = version.ToString();
        _notesSearch.Text = "";
        SelectTab(WHATS_NEW);
        RenderNotes();
    }

    private static IBrush KindColor(string kind) => ManagerTheme.Brush(kind switch {
        PatchNote.NEW => ManagerTheme.GOOD,
        PatchNote.FIXED => ManagerTheme.TIMING_NEXT_USE,
        _ => ManagerTheme.WARN,
    });

    private static Border KindPill(string kind) {
        var color = (KindColor(kind) as ISolidColorBrush)!.Color;

        return new Border {
            Background = ManagerTheme.Brush(color, 40), BorderBrush = ManagerTheme.Brush(color, 130), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 2), VerticalAlignment = VerticalAlignment.Top, MinWidth = 64,
            Child = new TextBlock {
                Text = kind.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = ManagerTheme.Brush(Color.FromRgb((byte) Math.Min(255, color.R + 80), (byte) Math.Min(255, color.G + 80), (byte) Math.Min(255, color.B + 80))),
            },
        };
    }

    private static Control VersionView(VersionPick? pick) {
        if (pick is null) {
            return new Panel();
        }

        // All versions and Since your last update: their words go under them, as they are longer.
        if (!pick.Key.Contains('.')) {
            return new StackPanel {
                Margin = new Thickness(2, 1), Spacing = 1,
                Children = {
                    new TextBlock { Text = pick.Label, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = pick.Line, FontSize = 11.5, Foreground = MUTED, TextWrapping = TextWrapping.Wrap },
                },
            };
        }

        return new DockPanel {
            Margin = new Thickness(2, 1),
            Children = {
                WithDock(new TextBlock {
                    Text = pick.Line, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = pick.Yours ? ManagerTheme.Brush(ManagerTheme.ACCENT_LIGHT) : MUTED,
                }, Dock.Right),
                new TextBlock { Text = pick.Label, FontWeight = pick.Key.Contains('.') ? FontWeight.Normal : FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) },
            },
        };
    }

    private Control NoteView(NoteRow? row) {
        if (row is null) {
            return new Panel();
        }

        if (row.Note is null) {
            return new TextBlock {
                Text = $"Version {row.Version}" + (row.Version == SelfUpdate.CurrentVersion ? "  (yours)" : ""),
                FontSize = ManagerTheme.FONT_HEADING, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 14, 0, 0),
            };
        }

        var note = row.Note;
        var body = new StackPanel { Spacing = 3 };
        body.Children.Add(new TextBlock { Text = note.Title, FontSize = 14.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (note.Detail.Length > 0) {
            body.Children.Add(new TextBlock { Text = note.Detail, Foreground = ManagerTheme.Brush(ManagerTheme.TEXT_SOFT), TextWrapping = TextWrapping.Wrap });
        }

        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        if (note.Tab(s_tabs.ContainsKey) is { } tab) {
            var show = Primary($"Show me: {note.Where}");
            show.Click += (_, _) => SelectTab(tab);
            buttons.Children.Add(Spaced(show));
        }

        if (note.Test.Length > 0) {
            var steps = new TextBlock {
                Text = note.Test, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 6, 0, 0),
                Foreground = ManagerTheme.Brush(ManagerTheme.TEXT_SOFT),
            };
            var how = Plain("How to test");
            how.Click += (_, _) => {
                steps.IsVisible = !steps.IsVisible;
                how.Content = steps.IsVisible ? "Hide how to test" : "How to test";
            };
            buttons.Children.Add(Spaced(how));
            body.Children.Add(buttons);
            body.Children.Add(steps);
        }
        else {
            body.Children.Add(buttons);
        }

        var tested = TestedNotes();
        var tick = Plain(tested.Contains(note.Key) ? "✓ Tested" : "Tested");
        if (tested.Contains(note.Key)) {
            tick.Foreground = ManagerTheme.Brush(ManagerTheme.GOOD);
        }

        ToolTip.SetTip(tick, "Tick it once you tried it, so you know what is left to test. Kept on this PC.");
        tick.Click += (_, _) => {
            var now = ToggleTested(note.Key);
            tick.Content = now ? "✓ Tested" : "Tested";
            tick.Foreground = now ? ManagerTheme.Brush(ManagerTheme.GOOD) : ManagerTheme.TEXT_BRUSH;
        };
        buttons.Children.Add(Spaced(tick));

        return new Border {
            BorderBrush = LINE, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 6, 10),
            Child = new DockPanel {
                Children = {
                    WithDock(WithMargin(KindPill(note.Kind), new Thickness(0, 1, 12, 0)), Dock.Left),
                    body,
                },
            },
        };
    }

    private HashSet<string> TestedNotes() {
        if (_notesTested is not null) {
            return _notesTested;
        }

        try {
            var path = Path.Combine(_manager.InstallFolder, NOTES_TESTED);
            _notesTested = File.Exists(path) ? [.. File.ReadAllLines(path).Where(l => l.Length > 0)] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            _notesTested = [];
        }

        return _notesTested;
    }

    private bool ToggleTested(string key) {
        var tested = TestedNotes();
        var now = tested.Add(key) || !tested.Remove(key);
        try {
            File.WriteAllLines(Path.Combine(_manager.InstallFolder, NOTES_TESTED), tested.Order());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Kept until the app closes.
        }

        return now;
    }

    /// <summary>The version used before the last update, when that update brought notes to catch up on.</summary>
    private Version? PreviousVersion() {
        try {
            var path = Path.Combine(_manager.InstallFolder, NOTES_PREVIOUS);
            return File.Exists(path) && Version.TryParse(File.ReadAllText(path).Trim(), out var v) && v < SelfUpdate.CurrentVersion ? v : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    private static string Summary(IEnumerable<PatchNote> notes) {
        var list = notes.ToList();
        var parts = new List<string>();
        var added = list.Count(n => n.Kind == PatchNote.NEW);
        var fixes = list.Count(n => n.Kind == PatchNote.FIXED);
        var changes = list.Count(n => n.Kind == PatchNote.CHANGED);
        if (added > 0) {
            parts.Add($"{added} new");
        }

        if (fixes > 0) {
            parts.Add($"{fixes} fix{(fixes == 1 ? "" : "es")}");
        }

        if (changes > 0) {
            parts.Add($"{changes} changed");
        }

        return string.Join(", ", parts);
    }

    /// <summary>The versions list and the notes for what is picked there, the kind chip and the search.</summary>
    private void RenderNotes() {
        var parsed = _notes.Select(r => (r.Version, Notes: r.Changes.Select(c => PatchNote.Parse(r.Version, c)).ToList())).ToList();
        var current = SelfUpdate.CurrentVersion;
        var previous = PreviousVersion();

        // The versions down the left.
        var picks = new List<VersionPick> { new(PICK_ALL, "All versions", $"{parsed.Count:N0} versions with notes", false) };
        if (previous is not null) {
            var since = parsed.Where(p => p.Version > previous && p.Version <= current).SelectMany(p => p.Notes);
            picks.Add(new VersionPick(PICK_SINCE, "Since your last update", Summary(since), false));
        }

        picks.AddRange(parsed.Select(p => new VersionPick(p.Version.ToString(), p.Version.ToString(),
            p.Version == current ? "You have this" : Summary(p.Notes), p.Version == current)));
        _notesPick ??= previous is not null ? PICK_SINCE : PICK_ALL;
        if (picks.All(p => p.Key != _notesPick)) {
            _notesPick = PICK_ALL;
        }

        _notesPicking = true;
        _notesVersions.ItemsSource = picks;
        _notesVersions.SelectedItem = picks.First(p => p.Key == _notesPick);
        _notesPicking = false;

        // The notes on the right.
        IEnumerable<(Version Version, List<PatchNote> Notes)> releases = _notesPick switch {
            PICK_ALL => parsed,
            PICK_SINCE => parsed.Where(p => p.Version > previous! && p.Version <= current),
            _ => parsed.Where(p => p.Version.ToString() == _notesPick),
        };
        _notesHeading.Text = _notesPick switch {
            PICK_ALL => "Every version",
            PICK_SINCE => "Since your last update",
            _ => $"Version {_notesPick}",
        };

        var query = (_notesSearch.Text ?? "").Trim();
        var rows = new List<NoteRow>();
        var count = 0;
        foreach (var (version, notes) in releases) {
            var whole = query.Length > 0 && version.ToString().StartsWith(query, StringComparison.Ordinal);
            var matched = notes.Where(n => (_notesKind == "All" || n.Kind == _notesKind)
                                           && (query.Length == 0 || whole || SearchText.Matches($"{n.Title} {n.Detail} {n.Where} {n.Test}", query))).ToList();
            if (matched.Count == 0) {
                continue;
            }

            rows.Add(new NoteRow(version, null));
            rows.AddRange(matched.Select(n => new NoteRow(version, n)));
            count += matched.Count;
        }

        _notesList.ItemsSource = rows;
        _notesCount.Text = _notes.Count == 0 ? _notesCount.Text
            : count == 0 ? "Nothing matches."
            : $"{count:N0} change{(count == 1 ? "" : "s")} in {rows.Count - count:N0} version{(rows.Count - count == 1 ? "" : "s")}";
    }

}
