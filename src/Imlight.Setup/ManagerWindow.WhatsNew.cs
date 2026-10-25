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
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The What's New window (Niduus, 2026-10-25: "Add a What's New pop up when the launcher opens for the first time after
/// an update"). Each release's notes on GitHub list what changed under a "What's new" heading (the release workflow
/// writes it from the merged changes); the window shows every version since the one last seen on this PC.
/// </summary>
internal sealed partial class ManagerWindow {

    // The version whose notes were last shown, next to the app.
    private const string WHATS_NEW_SEEN = "whats-new-seen.txt";
    // There when the pop-up is turned off (Niduus, 2026-10-25: "add option to disable What's New").
    private const string WHATS_NEW_OFF = "whats-new-popup-off.txt";
    // Every release's notes, kept on this PC so the What's New page opens at once.
    private const string NOTES_CACHE = "whats-new-notes.json";
    private const string WHATS_NEW = "What's New";
    private const string WHATS_NEW_HEADING = "## What's new";
    // How many releases are read back: threads release often, so a few days away can be many versions.
    private const int WHATS_NEW_RELEASES = 60;

    private sealed record ReleaseNotes(Version Version, List<string> Changes);

    /// <summary>Shows What's New once after each update, and never on a build with no version (a local build).</summary>
    private async Task ShowWhatsNewIfUpdatedAsync() {
        var current = SelfUpdate.CurrentVersion;
        if (current == new Version(0, 0, 0)) {
            return;
        }

        var file = Path.Combine(_manager.InstallFolder, WHATS_NEW_SEEN);
        Version? seen = null;
        try {
            if (File.Exists(file) && Version.TryParse((await File.ReadAllTextAsync(file)).Trim(), out var read)) {
                seen = read;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return;
        }

        if (seen is not null && seen >= current) {
            return;
        }

        // Turned off: the version still counts as seen, so turning it back on shows only the next update's.
        if (WhatsNewPopupOff) {
            try {
                await File.WriteAllTextAsync(file, current.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // Shown as seen next time instead.
            }

            return;
        }

        try {
            await File.WriteAllTextAsync(file, current.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Not written: it shows again next time, which is better than never.
        }

        await ShowWhatsNewAsync(seen);
    }

    /// <summary>The What's New window: every version after <paramref name="since"/> up to this one (only this one when null).</summary>
    private async Task ShowWhatsNewAsync(Version? since) {
        var current = SelfUpdate.CurrentVersion;
        List<ReleaseNotes>? notes = null;
        try {
            notes = await LoadReleaseNotesAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException) {
            // Offline or GitHub refused: the notes built into the app, else said in the window.
            var builtIn = BuiltInNotes();
            notes = builtIn.Count > 0 ? builtIn : null;
        }

        var shown = notes?
            .Where(n => n.Version <= current && (since is null ? n.Version == current : n.Version > since))
            .Where(n => n.Changes.Count > 0)
            .OrderByDescending(n => n.Version)
            .ToList() ?? [];

        var list = new StackPanel { Spacing = 14 };
        if (notes is null) {
            list.Children.Add(Sub("The notes could not be loaded. Check your internet connection, then open What's New on the left."));
        }
        else if (shown.Count == 0) {
            list.Children.Add(Sub(since is null
                ? $"Version {current} has no notes yet."
                : $"No notes for the versions since {since}."));
        }

        foreach (var release in shown) {
            var bullets = new StackPanel { Spacing = 6 };
            foreach (var change in release.Changes) {
                bullets.Children.Add(new DockPanel {
                    Children = {
                        WithDock(new Border {
                            Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Margin = new Thickness(2, 7, 10, 0),
                            VerticalAlignment = VerticalAlignment.Top, Background = ManagerTheme.Gradient(),
                        }, Dock.Left),
                        new TextBlock { Text = change, TextWrapping = TextWrapping.Wrap },
                    },
                });
            }

            list.Children.Add(Card(new StackPanel {
                Spacing = 10,
                Children = {
                    new TextBlock { Text = $"Version {release.Version}", FontSize = ManagerTheme.FONT_HEADING, FontWeight = FontWeight.Bold },
                    bullets,
                },
            }));
        }

        var dialog = new Window {
            Title = $"What's new in {Edition.NAME}", Width = 620, Height = 620, MinHeight = 320, Background = BACKGROUND,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        ManagerTheme.Apply(dialog);
        var ok = Primary("OK");
        ok.Content = "Got it";
        ok.Click += (_, _) => dialog.Close();
        dialog.Content = new DockPanel {
            Margin = new Thickness(22),
            Children = {
                WithDock(new DockPanel {
                    Margin = new Thickness(0, 0, 0, 16),
                    Children = {
                        WithDock(WithMargin(ManagerTheme.Tile("sparkle", ManagerTheme.Gradient(), 46), new Thickness(0, 0, 14, 0)), Dock.Left),
                        new StackPanel {
                            VerticalAlignment = VerticalAlignment.Center, Spacing = 2,
                            Children = {
                                new TextBlock { Text = $"What's new in {Edition.NAME}", FontSize = ManagerTheme.FONT_PAGE, FontWeight = FontWeight.Bold },
                                new TextBlock { Text = $"You have version {current}.", Foreground = MUTED },
                            },
                        },
                    },
                }, Dock.Top),
                WithDock(WithMargin(new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Children = { ok } }, new Thickness(0, 16, 0, 0)), Dock.Bottom),
                new ScrollViewer { Content = list },
            },
        };

        await dialog.ShowDialog(this);
    }

    /// <summary>The newest releases' "What's new" lines, from GitHub.</summary>
    private static async Task<List<ReleaseNotes>> LoadReleaseNotesAsync(int pages = 1) {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ImlightSetup", SelfUpdate.CurrentVersion.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        var notes = new List<ReleaseNotes>();
        var perPage = pages == 1 ? WHATS_NEW_RELEASES : 100;
        for (var page = 1; page <= pages; page++) {
            using var document = JsonDocument.Parse(await http.GetStringAsync(
                $"https://api.github.com/repos/{SelfUpdate.RELEASE_REPOSITORY}/releases?per_page={perPage}&page={page}"));
            var count = 0;
            foreach (var release in document.RootElement.EnumerateArray()) {
                count++;
                var tag = release.GetProperty("tag_name").GetString() ?? "";
                if (!Version.TryParse(tag.TrimStart('v'), out var version)) {
                    continue;
                }

                var body = release.TryGetProperty("body", out var text) ? text.GetString() ?? "" : "";
                notes.Add(new ReleaseNotes(version, ParseChanges(body)));
            }

            if (count < perPage) {
                break;
            }
        }

        return notes;
    }

    /// <summary>The "- " lines under a release's "What's new" heading, without their pull request numbers.</summary>
    internal static List<string> ParseChanges(string body) {
        var changes = new List<string>();
        var inside = false;
        foreach (var raw in body.ReplaceLineEndings("\n").Split('\n')) {
            var line = raw.Trim();
            if (line.StartsWith('#')) {
                inside = line.Equals(WHATS_NEW_HEADING, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inside && line.StartsWith("- ", StringComparison.Ordinal)) {
                var change = Regex.Replace(line[2..], @"\s*\(#\d+\)\s*$", "").Trim();
                if (change.Length > 0) {
                    changes.Add(change);
                }
            }
        }

        return changes;
    }


    // ---------------------------------------------------------------------
    // The What's New page
    // ---------------------------------------------------------------------

    /// <summary>One line of the page: a version's heading (Change is null) or one of its changes.</summary>
    private sealed record NoteRow(Version Version, string? Change);

    private readonly TextBox _notesSearch = new() { Watermark = "Search the notes, such as Deckathalon, crafting, cheats or 0.10.300" };
    private readonly TextBlock _notesCount = Sub("");
    // Niduus, 2026-10-25: every search with filters like the Gear tab's.
    private readonly TextBox _notesFrom = new() { Watermark = "Such as 0.10.300", Width = 150 };
    private readonly TextBox _notesTo = new() { Watermark = "Such as 0.10.380", Width = 150 };
    private readonly ComboBox _notesSort = FilterCombo(["Newest first", "Oldest first"], 150);
    private readonly CheckBox _notesNewer = new() { Content = "Only versions newer than mine" };
    private readonly ToggleSwitch _whatsNewPopup = new() { OnContent = "On", OffContent = "Off" };
    private readonly DispatcherTimer _notesDelay = new() { Interval = TimeSpan.FromMilliseconds(200) };
    // Only the rows in view are drawn (a recycling, virtualizing list), however many versions there are.
    private readonly ListBox _notesList = new() {
        Background = Brushes.Transparent,
        ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
    };
    private List<ReleaseNotes> _notes = [];
    private bool _notesFetched;

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
        WhenFiltersChange(RenderNotes, _notesFrom, _notesTo, _notesSort, _notesNewer);
        _notesList.ItemTemplate = new FuncDataTemplate<NoteRow>((row, _) => NoteView(row), supportsRecycling: false);

        var content = new DockPanel {
            Margin = new Thickness(24, 12, 24, 16), MaxWidth = ManagerTheme.PAGE_MAX_WIDTH,
            Children = {
                WithDock(Fold("sparkle", WHATS_NEW, "Pop-up after an update",
                    "The What's New window the first time the app opens after an update.",
                    SwitchRow("Show What's New after an update", _whatsNewPopup,
                        "Off: it stays shut. The notes are always here.")), Dock.Top),
                WithDock(WithMargin(Fold("search", WHATS_NEW, "Patch notes",
                    "Every version's changes, newest first.",
                    new DockPanel {
                        Children = {
                            WithDock(WithMargin(Action(Plain("Refresh"), () => LoadNotesAsync(refresh: true)), new Thickness(8, 0, 0, 0)), Dock.Right),
                            _notesSearch,
                        },
                    },
                    SearchFilters(SearchFilter("From version", _notesFrom), SearchFilter("To version", _notesTo), SearchFilter("Sort by", _notesSort),
                        SearchTick(_notesNewer)),
                    _notesCount), new Thickness(0, 14, 0, 0)), Dock.Top),
                WithMargin(Card(_notesList), new Thickness(0, 14, 0, 0)),
            },
        };

        return new DockPanel {
            Children = {
                WithDock(ManagerTheme.PageHeader("sparkle", ManagerTheme.Accent(WHATS_NEW), WHATS_NEW, "What changed in each version of " + Edition.NAME + "."), Dock.Top),
                content,
            },
        };
    }

    private static Control NoteView(NoteRow? row) {
        if (row is null) {
            return new Panel();
        }

        if (row.Change is null) {
            return new TextBlock {
                Text = $"Version {row.Version}" + (row.Version == SelfUpdate.CurrentVersion ? "  (yours)" : ""),
                FontSize = ManagerTheme.FONT_HEADING, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 10, 0, 2),
            };
        }

        return new DockPanel {
            Margin = new Thickness(4, 0, 0, 0),
            Children = {
                WithDock(new Border {
                    Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 7, 10, 0),
                    VerticalAlignment = VerticalAlignment.Top, Background = ManagerTheme.Gradient(),
                }, Dock.Left),
                new TextBlock { Text = row.Change, TextWrapping = TextWrapping.Wrap },
            },
        };
    }

    /// <summary>The notes kept on this PC at once, then (once per session, or on Refresh) every release's from GitHub.</summary>
    private async Task LoadNotesAsync(bool refresh = false) {
        var cache = Path.Combine(_manager.InstallFolder, NOTES_CACHE);
        if (_notes.Count == 0) {
            _notes = await Task.Run(() => Merge(BuiltInNotes(), ReadNotesCache(cache)));
            RenderNotes();
        }

        if (_notesFetched && !refresh) {
            return;
        }

        _notesFetched = true;
        if (_notes.Count == 0) {
            _notesCount.Text = "Loading the notes…";
        }

        List<ReleaseNotes> fetched;
        try {
            // 100 releases a page; ten pages is far more than there are.
            fetched = await LoadReleaseNotesAsync(pages: 10);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException) {
            _notesFetched = false;
            _notesCount.Text = _notes.Count == 0
                ? "The notes could not be loaded. Check your internet connection, then click Refresh."
                : _notesCount.Text + " (Could not check for newer notes.)";

            return;
        }

        _notes = Merge(_notes, fetched);
        RenderNotes();
        var notes = _notes;
        await Task.Run(() => {
            try {
                File.WriteAllText(cache, JsonSerializer.Serialize(notes.Select(n => new { Version = n.Version.ToString(), n.Changes })));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // Not kept; fetched again next time.
            }
        });
    }

    /// <summary>Both lists, the later one's notes winning for a version in both; only versions with changes, newest first.</summary>
    private static List<ReleaseNotes> Merge(List<ReleaseNotes> older, List<ReleaseNotes> newer) {
        var byVersion = older.Where(n => n.Changes.Count > 0).ToDictionary(n => n.Version);
        foreach (var notes in newer.Where(n => n.Changes.Count > 0)) {
            byVersion[notes.Version] = notes;
        }

        return [.. byVersion.Values.OrderByDescending(n => n.Version)];
    }

    /// <summary>
    /// Every version's notes up to this one, built into the app by the release workflow from the repository's history,
    /// so versions released before release notes listed their changes have notes too, even offline.
    /// </summary>
    private static List<ReleaseNotes> BuiltInNotes() {
        try {
            var assembly = typeof(ManagerWindow).Assembly.GetName().Name;
            using var stream = AssetLoader.Open(new Uri($"avares://{assembly}/Assets/patch-notes.json"));
            using var reader = new StreamReader(stream);

            return ParseNotesJson(reader.ReadToEnd());
        }
        catch (Exception) {
            // A build without the file (a local build).
            return [];
        }
    }

    private static List<ReleaseNotes> ReadNotesCache(string path) {
        try {
            return File.Exists(path) ? ParseNotesJson(File.ReadAllText(path)) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    /// <summary>[{"Version": "0.10.375", "Changes": ["…"]}], the notes file's and the built-in list's shape.</summary>
    private static List<ReleaseNotes> ParseNotesJson(string json) {
        try {
            using var document = JsonDocument.Parse(json);
            return [.. document.RootElement.EnumerateArray()
                .Select(e => (Version: Version.TryParse(e.GetProperty("Version").GetString(), out var v) ? v : null,
                              Changes: Strings(e.GetProperty("Changes"))))
                .Where(e => e.Version is not null && e.Changes.Count > 0)
                .Select(e => new ReleaseNotes(e.Version!, e.Changes))
                .OrderByDescending(n => n.Version)];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) {
            return [];
        }
    }

    // A list of lines, or a single line (how a one-line list can come out of the workflow's JSON).
    private static List<string> Strings(JsonElement element) => element.ValueKind switch {
        JsonValueKind.Array => [.. element.EnumerateArray().Select(c => c.GetString() ?? "").Where(c => c.Length > 0)],
        JsonValueKind.String when element.GetString() is { Length: > 0 } one => [one],
        _ => [],
    };

    /// <summary>The rows matching the search: each version with a match gets its heading, then its matching changes.</summary>
    private void RenderNotes() {
        var query = (_notesSearch.Text ?? "").Trim();
        var rows = new List<NoteRow>();
        var changes = 0;
        var from = Version.TryParse((_notesFrom.Text ?? "").Trim(), out var least) ? least : null;
        var to = Version.TryParse((_notesTo.Text ?? "").Trim(), out var most) ? most : null;
        var newer = _notesNewer.IsChecked == true;
        var filtered = from is not null || to is not null || newer;
        IEnumerable<ReleaseNotes> releases = _notes.Where(r => (from is null || r.Version >= from) && (to is null || r.Version <= to)
                                                               && (!newer || r.Version > SelfUpdate.CurrentVersion));
        if (_notesSort.SelectedIndex == 1) {
            releases = releases.Reverse();
        }

        foreach (var release in releases) {
            // A version's number finds all of its changes.
            var whole = query.Length > 0 && release.Version.ToString().StartsWith(query, StringComparison.Ordinal);
            var matched = query.Length == 0 || whole ? release.Changes : release.Changes.Where(c => SearchText.Matches(c, query)).ToList();
            if (matched.Count == 0) {
                continue;
            }

            rows.Add(new NoteRow(release.Version, null));
            rows.AddRange(matched.Select(c => new NoteRow(release.Version, c)));
            changes += matched.Count;
        }

        _notesList.ItemsSource = rows;
        var versions = rows.Count - changes;
        _notesCount.Text = _notes.Count == 0 ? _notesCount.Text
            : query.Length == 0 && !filtered ? $"{_notes.Count:N0} versions with notes, {(_notesSort.SelectedIndex == 1 ? "oldest" : "newest")} first."
            : changes == 0 ? "Nothing matches."
            : $"{changes:N0} change{(changes == 1 ? "" : "s")} in {versions:N0} version{(versions == 1 ? "" : "s")}.";
    }

}
