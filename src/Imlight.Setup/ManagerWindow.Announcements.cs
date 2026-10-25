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
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Imlight.CoreLib.Game.Tools;
using Imlight.Setup.Blocks;

namespace Imlight.Setup;

/// <summary>
/// The Announcements tab (Niduus, 2026-10-25: "In the manager, if anything causes an announcement, I want it all under
/// an Announcement tab so I can adjust what it says"). Every line the server announces (AnnouncementCatalog, shared with
/// the server) with a box for its words, an On switch and a Reset button, plus the Announcement card for sending one of
/// your own. Changes are saved to announcements.json next to the server a moment after the last key; the server reads
/// that file again within a second (Game/Tools/Announcements.cs), so nothing needs a restart.
/// </summary>
internal sealed partial class ManagerWindow {

    private sealed record AnnouncementRow(AnnouncementLine Line, TextBox Words, ToggleSwitch On, Control View);

    private readonly List<AnnouncementRow> _announcementRows = [];
    private readonly TextBox _announcementSearch = new() { Watermark = "Search the announcements, such as event, raid or Majid" };
    private readonly DispatcherTimer _announcementSave = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly List<(string Group, Section Card, List<AnnouncementRow> Rows)> _announcementGroups = [];
    private bool _showingAnnouncements;

    private string AnnouncementsPath => Path.Combine(_manager.ServerFolder, AnnouncementCatalog.FILE);

    private Control AnnouncementsTab() {
        var (texts, off) = LoadAnnouncements();
        _showingAnnouncements = true;
        foreach (var group in AnnouncementCatalog.All.GroupBy(l => l.Group)) {
            var rows = group.Select(line => AnnouncementRowFor(line, texts.GetValueOrDefault(line.Key), !off.Contains(line.Key))).ToList();
            _announcementRows.AddRange(rows);
            var card = Fold(AnnouncementIcon(group.Key), "Announcements", group.Key, AnnouncementGroupHelp(group.Key),
                [.. rows.Select(r => r.View)]);
            _announcementGroups.Add((group.Key, card, rows));
        }

        _showingAnnouncements = false;

        _announcementSave.Tick += async (_, _) => {
            _announcementSave.Stop();
            await Guard(SaveAnnouncementsAsync);
        };
        _announcementSearch.TextChanged += (_, _) => FilterAnnouncements();

        var resetAll = Action(Plain("Reset all to the game's words"), async () => {
            if (!await ConfirmAsync("Put every announcement back to its first words and turn them all on?")) {
                return;
            }

            _showingAnnouncements = true;
            foreach (var row in _announcementRows) {
                row.Words.Text = row.Line.Default;
                row.On.IsChecked = true;
            }

            _showingAnnouncements = false;
            await SaveAnnouncementsAsync();
            Say("Every announcement is back to its first words.");
        });

        var cards = new List<Control> {
            Fold("search", "Announcements", "Find an announcement",
                "Change the words of anything the server announces. Changes are used the next time the line is sent; no restart.",
                WithHint(_announcementSearch,
                    "Each line has a box with its words. Words in {curly brackets} are filled in by the server, such as {event} "
                    + "becoming Fire Deckathalon; the list under each box says what each one becomes. Leave them in, move them, "
                    + "or take out the ones you do not want. Turn a line Off to stop it being sent at all. Reset puts back the first words. "
                    + "Changes save by themselves a moment after you stop typing."),
                Row(resetAll)),
            AnnouncementCard(),
        };
        cards.AddRange(_announcementGroups.Select(g => (Control) g.Card));
        cards.Add(Fold("sword", "Bosses", "Battle announcements",
            "Lines a boss or cheat shows in battle are part of each cheat, so you change them where the cheat is made.",
            Sub("Open the Bosses tab, pick the boss, and change the words in its cheat's blocks (such as say a line, "
                + "show a banner or announce to the battle). Player cheats are on the Commands tab under Player cheats. "
                + "The \"Announcement\" heading and chat start that announce to the battle puts around its words are the two "
                + "lines under Your announcements above.")));

        return Page([.. cards]);
    }

    private AnnouncementRow AnnouncementRowFor(AnnouncementLine line, string? text, bool on) {
        var multiLine = line.Default.Contains('\n');
        var words = new TextBox {
            Text = string.IsNullOrEmpty(text) ? line.Default : text,
            TextWrapping = TextWrapping.Wrap, AcceptsReturn = multiLine, MinHeight = multiLine ? 56 : 0,
        };
        var toggle = new ToggleSwitch { OnContent = "On", OffContent = "Off", IsChecked = on };
        var reset = Plain("Reset");
        reset.Click += (_, _) => words.Text = line.Default;
        words.TextChanged += (_, _) => QueueAnnouncementSave();
        toggle.IsCheckedChanged += (_, _) => {
            words.IsEnabled = toggle.IsChecked == true;
            QueueAnnouncementSave();
        };
        words.IsEnabled = on;

        var body = new StackPanel {
            Spacing = 6,
            Children = {
                new DockPanel {
                    Children = {
                        WithDock(Row(toggle), Dock.Right),
                        new StackPanel {
                            Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
                            Children = { new TextBlock { Text = line.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }, Sub(line.When) },
                        },
                    },
                },
                new DockPanel { Children = { WithDock(WithMargin(reset, new Thickness(8, 0, 0, 0)), Dock.Right), words } },
            },
        };
        if (line.Fields.Length > 0) {
            body.Children.Add(Sub(string.Join("\n", line.Fields.Select(f => $"{f.Field} = {f.Means}"))));
        }

        var view = new Border {
            Background = ManagerTheme.SURFACE_HIGH_BRUSH, CornerRadius = new CornerRadius(ManagerTheme.RADIUS_SMALL),
            Padding = new Thickness(14, 10, 10, 10), Child = body,
        };

        return new AnnouncementRow(line, words, toggle, view);
    }

    private static string AnnouncementIcon(string group) => group switch {
        "Events" => "star",
        "Deckathalon" => "cards",
        "Loyalty Store" => "bag",
        "Member benefits" => "gift",
        "Crowns Rewards" => "coin",
        "Guild raids" => "sword",
        "Guilds" => "users",
        "Your announcements" => "quote",
        "Server notices" => "server",
        _ => "chat",
    };

    private static string AnnouncementGroupHelp(string group) => group switch {
        "Events" => "Sent to everyone online when an event from the Server tab's Events card starts or ends.",
        "Deckathalon" => "Sent to the wizard climbing a Deckathalon tower.",
        "Loyalty Store" => "Sent to everyone online when Majid's wares change (Server tab, Loyalty Store card).",
        "Member benefits" => "Sent when a member benefit turns on or off, and to each wizard when they log in.",
        "Crowns Rewards" => "Sent to the buyer while the Crowns Rewards event is on (Server tab, Crown Shop card).",
        "Guild raids" => "Sent to everyone inside a guild raid.",
        "Guilds" => "Sent to the guild's members.",
        "Your announcements" => "How the announcements you send from the Announcement card above look.",
        "Server notices" => "The message boxes a player sees when they are sent off the server.",
        _ => "",
    };

    private void FilterAnnouncements() {
        var query = (_announcementSearch.Text ?? "").Trim();
        foreach (var (group, card, rows) in _announcementGroups) {
            var shown = 0;
            foreach (var row in rows) {
                var match = query.Length == 0
                    || $"{group} {row.Line.Name} {row.Line.When} {row.Line.Default} {row.Words.Text}".Contains(query, StringComparison.OrdinalIgnoreCase);
                row.View.IsVisible = match;
                shown += match ? 1 : 0;
            }

            card.IsVisible = shown > 0;
        }
    }

    private void QueueAnnouncementSave() {
        if (_showingAnnouncements) {
            return;
        }

        _announcementSave.Stop();
        _announcementSave.Start();
    }

    private (Dictionary<string, string> Texts, HashSet<string> Off) LoadAnnouncements() {
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var off = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try {
            if (File.Exists(AnnouncementsPath) && JsonNode.Parse(File.ReadAllText(AnnouncementsPath)) is JsonObject json) {
                if (json["Texts"] is JsonObject saved) {
                    foreach (var (key, value) in saved) {
                        if (value?.GetValueKind() == JsonValueKind.String) {
                            texts[key] = value.GetValue<string>();
                        }
                    }
                }

                if (json["Off"] is JsonArray list) {
                    off.UnionWith(list.Where(v => v?.GetValueKind() == JsonValueKind.String).Select(v => v!.GetValue<string>()));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            Say($"Could not read {AnnouncementCatalog.FILE}: {ex.Message}. The first words are shown.", isError: true);
        }

        return (texts, off);
    }

    private async Task SaveAnnouncementsAsync() {
        // Only words that differ from the first ones are kept, so a later Imlight can still improve the rest.
        var texts = new JsonObject();
        foreach (var row in _announcementRows) {
            var words = (row.Words.Text ?? "").ReplaceLineEndings("\n");
            if (words.Trim().Length > 0 && words != row.Line.Default) {
                texts[row.Line.Key] = words;
            }
        }

        var json = new JsonObject {
            ["Texts"] = texts,
            ["Off"] = new JsonArray([.. _announcementRows.Where(r => r.On.IsChecked != true).Select(r => (JsonNode) JsonValue.Create(r.Line.Key)!)]),
        };
        Directory.CreateDirectory(_manager.ServerFolder);
        var temp = $"{AnnouncementsPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temp, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, AnnouncementsPath, overwrite: true);
        Say("Announcements saved. The server uses the new words from now on.");
    }

}
