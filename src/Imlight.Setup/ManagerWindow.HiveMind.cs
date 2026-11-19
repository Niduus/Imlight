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
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The Hive mind tab (Niduus, 2026-11-19: "Create a new tab in Ahziael called "Hive mind" while the server is loading, it
/// collects unique data for the hive mind to present to the player. This data is bullet proof and tells the user how the
/// bots complete each task. This is to project from a new github folder after the same name."). Two parts: the pages of the
/// "Hive mind" folder on GitHub (<see cref="HiveMindShelf"/>), and what this server's bots did, collected each time the
/// server starts (<see cref="HiveMind"/>).
/// </summary>
internal sealed partial class ManagerWindow {

    private const string HIVE_MIND_TAB = "Hive mind";

    private readonly TextBlock _hiveStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _hiveGitHubNote = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _hiveGitHub = new() { Spacing = ManagerTheme.GAP };
    private readonly TextBlock _hiveLocalNote = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _hiveLocal = new() { Spacing = ManagerTheme.GAP };
    private readonly TextBox _hiveSearch = new() { Watermark = "Search a quest, a bot or a place (for example Ghost Hunters, Ra or Big Ben)", MinWidth = 320 };
    private readonly DispatcherTimer _hiveSearchDelay = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _hiveGitHubTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private HiveMind.Book? _hiveBook;
    private List<HiveMindShelf.Page> _hivePages = [];
    private bool _hiveCollecting;
    private bool _hiveServerWasRunning;

    private Control HiveMindTab() {
        _hiveSearch.TextChanged += (_, _) => {
            _hiveSearchDelay.Stop();
            _hiveSearchDelay.Start();
        };
        _hiveSearchDelay.Tick += (_, _) => {
            _hiveSearchDelay.Stop();
            ShowHivePages();
            ShowHiveBook();
        };
        _hiveGitHubTimer.Tick += async (_, _) => await RefreshHiveGitHubAsync(quiet: true);
        _hiveGitHubTimer.Start();

        var intro = Fold("hive", HIVE_MIND_TAB, "Hive mind",
            "How the bots (Ultimus, the TCO7 and the Quest Team) get each quest done, in two parts. The pages from GitHub come from the " +
            "\"Hive mind\" folder of Niduus/Imlight, so a change there shows here without an update of Ahziael. The part from this server " +
            "is collected from the bots' own logs each time the server starts, and keeps only what is certain: a quest a bot finished " +
            "start to finish by itself, a fix that worked every time it was tried, and a fight style seen in at least 5 fights.",
            _hiveStatus,
            Row(Action(Primary("Check GitHub now"), () => RefreshHiveGitHubAsync(quiet: false)),
                Action(Plain("Collect again"), CollectHiveAsync),
                Action(Plain("Open the folder on GitHub"), () => {
                    OpenInBrowser(HiveMindShelf.PageUrl);
                    return Task.CompletedTask;
                })),
            _hiveSearch);

        var github = Fold("link", HIVE_MIND_TAB, "From GitHub",
            "The Hive mind's pages, as they are in the \"Hive mind\" folder on GitHub. Checked when Ahziael opens, when the server starts " +
            "and every 30 minutes. The last copy stays on this PC, so they show with no internet too.",
            _hiveGitHubNote, _hiveGitHub);

        var local = Fold("server", HIVE_MIND_TAB, "Collected on this server",
            "What this server's bots did, world by world: who finished each quest by itself, its best time, its steps and fights, and the " +
            "moves that got a bot unstuck there. Runs that are not certain (a step finished by Ahziael, a step flagged broken, a quest " +
            "picked up partway or after a restart) are left out and counted below.",
            _hiveLocalNote, _hiveLocal);

        // What was kept from last time shows at once; then GitHub is checked and the bots' logs read again.
        (_hivePages, var checkedAt) = HiveMindShelf.Kept(_manager.LogFolder);
        _hiveGitHubNote.Text = _hivePages.Count == 0 ? "Looking at GitHub…" : $"{HiveMind.Plural(_hivePages.Count, "page")}, last checked {checkedAt:MMM d HH:mm}. Looking again…";
        ShowHivePages();
        _hiveBook = HiveMind.Load(_manager.LogFolder);
        ShowHiveBook();
        Dispatcher.UIThread.Post(async () => {
            await RefreshHiveGitHubAsync(quiet: true);
            await CollectHiveAsync();
        }, DispatcherPriority.Background);

        return Page(intro, github, local);
    }

    /// <summary>Called with every server status: the server starting is the moment to collect (and to look at GitHub).</summary>
    private void TickHiveMind(ServerStatus status) {
        var started = status.Running && !_hiveServerWasRunning;
        _hiveServerWasRunning = status.Running;
        if (started) {
            Dispatcher.UIThread.Post(async () => {
                await CollectHiveAsync();
                await RefreshHiveGitHubAsync(quiet: true);
            }, DispatcherPriority.Background);
        }
    }

    private async Task CollectHiveAsync() {
        if (_hiveCollecting) {
            return;
        }

        _hiveCollecting = true;
        _hiveStatus.Text = "Collecting what the bots did from their logs…";
        try {
            var logs = _manager.LogFolder;
            var book = await Task.Run(() => {
                var made = HiveMind.Collect(logs);
                try {
                    HiveMind.Save(made, logs);
                }
                catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) {
                    // Shown anyway; saved next time.
                }

                return made;
            });
            _hiveBook = book;
            _hiveStatus.Text = $"Collected {book.Made:MMM d HH:mm}.";
            ShowHiveBook();
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) {
            _hiveStatus.Text = $"The bots' logs could not be read just now ({e.Message}). The last collection still shows.";
        }
        finally {
            _hiveCollecting = false;
        }
    }

    private async Task RefreshHiveGitHubAsync(bool quiet) {
        try {
            _hivePages = await HiveMindShelf.RefreshAsync(_manager.LogFolder);
            _hiveGitHubNote.Text = _hivePages.Count == 0
                ? "The \"Hive mind\" folder on GitHub has no pages yet."
                : $"{HiveMind.Plural(_hivePages.Count, "page")}, checked {DateTime.Now:MMM d HH:mm}.";
            ShowHivePages();
            if (!quiet) {
                Say("The Hive mind's pages are up to date with GitHub.");
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.IO.IOException or System.Text.Json.JsonException) {
            _hiveGitHubNote.Text = _hivePages.Count == 0
                ? "GitHub could not be reached just now, and no pages are kept on this PC yet. It tries again in 30 minutes."
                : "GitHub could not be reached just now, so the last copy shows. It tries again in 30 minutes.";
            if (!quiet) {
                Say("GitHub could not be reached just now.", isError: true, log: false);
            }
        }
    }

    private void ShowHivePages() {
        _hiveGitHub.Children.Clear();
        var query = _hiveSearch.Text?.Trim() ?? "";
        var first = true;
        foreach (var page in _hivePages) {
            var body = HiveText(page.Text, query);
            if (body is null) {
                continue;
            }

            var title = page.Group.Length > 0 ? $"{page.Group}: {page.Title}" : page.Title;
            var fold = Fold("book", HIVE_MIND_TAB, title, "", body);
            _hiveGitHub.Children.Add(first || query.Length > 0 ? fold : Shut(fold));
            first = false;
        }

        if (_hiveGitHub.Children.Count == 0 && _hivePages.Count > 0 && query.Length > 0) {
            _hiveGitHub.Children.Add(Sub($"No page on GitHub mentions \"{query}\"."));
        }
    }

    private void ShowHiveBook() {
        _hiveLocal.Children.Clear();
        if (_hiveBook is not { } book) {
            _hiveLocalNote.Text = "Nothing collected yet. It is collected when the server starts, or click Collect again.";
            return;
        }

        var bots = book.Bots.Count == 0 ? "no bot has a quest log yet" : string.Join(", ", book.Bots);
        var leftOut = book.LeftOutSkipped + book.LeftOutBroken + book.LeftOutPartway;
        _hiveLocalNote.Text = $"From {bots}. {HiveMind.Plural(book.Quests.Count(q => q.Done.Count > 0), "quest")} finished cleanly at least once. " +
                              $"Of {HiveMind.Plural(book.RunsRead, "finished run")}, {leftOut} left out as not certain: {book.LeftOutSkipped} with a step finished by Ahziael, " +
                              $"{book.LeftOutBroken} with a step flagged broken, {book.LeftOutPartway} picked up partway or after a restart. " +
                              $"{(book.FixesLeftOut == 1 ? "1 stuck fix" : $"{book.FixesLeftOut} stuck fixes")} left out because they failed at least once or were for no quest. " +
                              $"Collected {book.Made:MMM d HH:mm}.";
        var query = _hiveSearch.Text?.Trim() ?? "";
        foreach (var (name, text) in HiveMind.Pages(book)) {
            var body = HiveText(text, query);
            if (body is null) {
                continue;
            }

            var fold = Fold(name == "How the bots fight" ? "sword" : "globe", HIVE_MIND_TAB, name, "", body);
            _hiveLocal.Children.Add(query.Length > 0 ? fold : Shut(fold));
        }

        if (_hiveLocal.Children.Count == 0 && query.Length > 0) {
            _hiveLocal.Children.Add(Sub($"Nothing collected mentions \"{query}\"."));
        }
    }

    /// <summary>
    /// A page's text drawn plainly: "## " lines are headings, "- " lines are points, the rest are sentences. With a search,
    /// only the headings' blocks that mention it (or null when nothing does).
    /// </summary>
    private static Control? HiveText(string text, string query) {
        var blocks = new List<List<string>> { new() };
        foreach (var raw in text.Replace("\r", "").Split('\n')) {
            var line = raw.TrimEnd();
            if (line.StartsWith("# ", StringComparison.Ordinal)) {
                continue; // the page's title is the fold's title
            }

            if (line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("### ", StringComparison.Ordinal)) {
                blocks.Add([]);
            }

            blocks[^1].Add(line);
        }

        var stack = new StackPanel { Spacing = 4 };
        foreach (var block in blocks) {
            if (query.Length > 0 && !block.Any(l => l.Contains(query, StringComparison.OrdinalIgnoreCase))) {
                continue;
            }

            foreach (var line in block) {
                var plain = Unmark(line);
                if (line.StartsWith("##", StringComparison.Ordinal)) {
                    stack.Children.Add(new SelectableTextBlock {
                        Text = plain.TrimStart('#').Trim(), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
                    });
                }
                else if (line.TrimStart().StartsWith("- ", StringComparison.Ordinal) || line.TrimStart().StartsWith("* ", StringComparison.Ordinal)) {
                    stack.Children.Add(new SelectableTextBlock { Text = "•  " + plain.TrimStart()[2..], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 0, 0, 0) });
                }
                else if (plain.Trim().Length > 0) {
                    stack.Children.Add(new SelectableTextBlock { Text = plain.Trim(), TextWrapping = TextWrapping.Wrap, Foreground = MUTED });
                }
            }
        }

        return stack.Children.Count == 0 ? null : stack;

        // Marks a reader does not need to see: bold, code and links' brackets.
        static string Unmark(string line) => System.Text.RegularExpressions.Regex.Replace(
            line.Replace("**", "").Replace("__", "").Replace("`", ""), @"\[([^\]]+)\]\([^)]+\)", "$1");
    }

}
