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
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// The What's New window (Niduus, 2026-10-25: "Add a What's New pop up when the launcher opens for the first time after
/// an update"). Each release's notes on GitHub list what changed under a "What's new" heading (the release workflow
/// writes it from the merged changes); the window shows every version since the one last seen on this PC.
/// </summary>
internal sealed partial class ManagerWindow {

    // The version whose notes were last shown, next to the app.
    private const string WHATS_NEW_SEEN = "whats-new-seen.txt";
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
            // Offline or GitHub refused: said in the window.
        }

        var shown = notes?
            .Where(n => n.Version <= current && (since is null ? n.Version == current : n.Version > since))
            .Where(n => n.Changes.Count > 0)
            .OrderByDescending(n => n.Version)
            .ToList() ?? [];

        var list = new StackPanel { Spacing = 14 };
        if (notes is null) {
            list.Children.Add(Sub("The notes could not be loaded. Check your internet connection, then click What's new on Home."));
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
    private static async Task<List<ReleaseNotes>> LoadReleaseNotesAsync() {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ImlightSetup", SelfUpdate.CurrentVersion.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var document = JsonDocument.Parse(await http.GetStringAsync(
            $"https://api.github.com/repos/{SelfUpdate.RELEASE_REPOSITORY}/releases?per_page={WHATS_NEW_RELEASES}"));

        var notes = new List<ReleaseNotes>();
        foreach (var release in document.RootElement.EnumerateArray()) {
            var tag = release.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v'), out var version)) {
                continue;
            }

            var body = release.TryGetProperty("body", out var text) ? text.GetString() ?? "" : "";
            notes.Add(new ReleaseNotes(version, ParseChanges(body)));
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

}
