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

        // The version used before this update, for the What's New page's "Since your last update".
        if (seen is not null) {
            try {
                await File.WriteAllTextAsync(Path.Combine(_manager.InstallFolder, NOTES_PREVIOUS), seen.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // The page then shows every version instead.
            }
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
            list.Children.Add(Sub("The notes could not be loaded. Check your internet connection, then open Help › What's New in the list on the left."));
        }
        else if (shown.Count == 0) {
            list.Children.Add(Sub(since is null
                ? $"Version {current} has no notes yet."
                : $"No notes for the versions since {since}."));
        }

        foreach (var release in shown) {
            var bullets = new StackPanel { Spacing = 6 };
            // New first, then fixes, then the rest, each with its kind (ManagerWindow.PatchNotes.cs).
            foreach (var note in release.Changes.Select(c => PatchNote.Parse(release.Version, c))
                         .OrderBy(n => n.Kind == PatchNote.NEW ? 0 : n.Kind == PatchNote.FIXED ? 1 : 2)) {
                var words = new StackPanel { Spacing = 2, Children = { new TextBlock { Text = note.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap } } };
                if (note.Detail.Length > 0) {
                    words.Children.Add(new TextBlock { Text = note.Detail, Foreground = ManagerTheme.Brush(ManagerTheme.TEXT_SOFT), TextWrapping = TextWrapping.Wrap });
                }

                bullets.Children.Add(new DockPanel {
                    Children = {
                        WithDock(WithMargin(KindPill(note.Kind), new Thickness(0, 1, 10, 0)), Dock.Left),
                        words,
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

        Window? shownDialog = null;
        var all = Action(Plain("See all patch notes"), () => {
            shownDialog?.Close();
            SelectTab(WHATS_NEW);

            return Task.CompletedTask;
        });

        var dialog = new Window {
            Title = $"What's new in {Edition.NAME}", Width = 620, Height = 620, MinHeight = 320, Background = BACKGROUND,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        ManagerTheme.Apply(dialog);
        shownDialog = dialog;
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
                WithDock(WithMargin(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { all, ok } }, new Thickness(0, 16, 0, 0)), Dock.Bottom),
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


    /// <summary>The notes kept on this PC at once, then (once per session, or on Refresh) every release's from GitHub.</summary>
    private async Task LoadNotesAsync(bool refresh = false) {
        var cache = Path.Combine(_manager.InstallFolder, NOTES_CACHE);
        if (_notes.Count == 0) {
            _notes = await Task.Run(() => Merge(BuiltInNotes(), ReadNotesCache(cache)));
        }

        // Drawn at once, also when the search window already read the notes.
        RenderNotes();

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

}
