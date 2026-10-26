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
/// your own. Since 2026-10-26 ("make sure all system notifications from the server are able to be configed and able to
/// be turned on or off individually or as a category") it also lists every other message the server sends a player
/// (ServerMessageCatalog, and the ones on no list it has sent, from server-messages-seen.json), and each group has a
/// switch of its own. Changes are saved to announcements.json next to the server a moment after the last key; the
/// server reads that file again within a second (Game/Tools/Announcements.cs), so nothing needs a restart.
/// </summary>
internal sealed partial class ManagerWindow {

    private sealed record AnnouncementRow(AnnouncementLine Line, TextBox Words, ToggleSwitch On, Control View);

    private sealed record AnnouncementGroup(string Group, Section Card, List<AnnouncementRow> Rows, ToggleSwitch On, Control RowsView, bool IsMessage);

    private const string KIND_ANNOUNCEMENTS = "Announcements";
    private const string KIND_MESSAGES = "Messages to one player";

    private readonly List<AnnouncementRow> _announcementRows = [];
    private readonly TextBox _announcementSearch = new() { Watermark = "Search every announcement and message, such as event, backpack, raid or Majid" };
    private readonly DispatcherTimer _announcementSave = new() { Interval = TimeSpan.FromMilliseconds(700) };
    // Niduus, 2026-10-25: every search with filters like the Gear tab's.
    private readonly ComboBox _announcementGroup = FilterCombo(new[] { ANY_FILTER }.Concat(AnnouncementCatalog.All.Select(l => l.Group)
        .Concat(ServerMessageCatalog.All.Select(l => l.Group).Order()).Append(ServerMessageCatalog.OTHER_GROUP).Distinct()), 190);
    private readonly ComboBox _announcementKind = FilterCombo([ANY_FILTER, KIND_ANNOUNCEMENTS, KIND_MESSAGES], 190);
    private readonly ComboBox _announcementState = FilterCombo([ANY_FILTER, "On", "Off"], 100);
    private readonly ComboBox _announcementWords = FilterCombo([ANY_FILTER, "Changed from the first words", "Still the first words"], 220);
    private readonly CheckBox _announcementFields = new() { Content = "Has words the server fills in, such as {event}" };
    private readonly TextBlock _announcementCount = Sub("");
    private readonly List<AnnouncementGroup> _announcementGroups = [];
    private bool _showingAnnouncements;

    private string AnnouncementsPath => Path.Combine(_manager.ServerFolder, AnnouncementCatalog.FILE);

    private Control AnnouncementsTab() {
        var (texts, off, offGroups) = LoadAnnouncements();
        _showingAnnouncements = true;
        // The server's other messages, by group, then the ones on no list that it has sent (newest first).
        var groups = AnnouncementCatalog.All.GroupBy(l => l.Group).Select(g => (Group: g.Key, Lines: g.ToList(), IsMessage: false))
            .Concat(ServerMessageCatalog.All.GroupBy(l => l.Group).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => (Group: g.Key, Lines: g.ToList(), IsMessage: true)))
            .Append((Group: ServerMessageCatalog.OTHER_GROUP, Lines: SeenMessages(), IsMessage: true));
        foreach (var (group, lines, isMessage) in groups) {
            var rows = lines.Select(line => AnnouncementRowFor(line, texts.GetValueOrDefault(line.Key), !off.Contains(line.Key))).ToList();
            _announcementRows.AddRange(rows);
            var groupOn = new ToggleSwitch { OnContent = "On", OffContent = "Off", IsChecked = !offGroups.Contains(group) };
            var rowsView = new StackPanel { Spacing = 10, IsEnabled = groupOn.IsChecked == true, Opacity = groupOn.IsChecked == true ? 1 : 0.5 };
            rowsView.Children.AddRange(rows.Select(r => r.View));
            groupOn.IsCheckedChanged += (_, _) => {
                rowsView.IsEnabled = groupOn.IsChecked == true;
                rowsView.Opacity = groupOn.IsChecked == true ? 1 : 0.5;
                QueueAnnouncementSave();
            };
            var help = isMessage ? ServerMessageCatalog.GroupHelp(group) : AnnouncementGroupHelp(group);
            var card = Fold(isMessage ? ServerMessageCatalog.GroupIcon(group) : AnnouncementIcon(group), "Announcements", group, help,
                SwitchRow("Send this whole group", groupOn,
                    $"Off stops every line in {group}, whatever their own switches say. On, each line's own switch decides."),
                rows.Count == 0 ? Sub("The server has not sent any of these yet. Once it does, they show here.") : rowsView);
            card.SetBadge(rows.Count == 1 ? "1 line" : $"{rows.Count} lines");
            // The long lists of messages start shut, so the tab opens quickly; a search opens the ones it finds.
            _announcementGroups.Add(new AnnouncementGroup(group, isMessage ? Shut(card) : card, rows, groupOn, rowsView, isMessage));
        }

        _showingAnnouncements = false;

        _announcementSave.Tick += async (_, _) => {
            _announcementSave.Stop();
            await Guard(SaveAnnouncementsAsync);
        };
        _announcementSearch.TextChanged += (_, _) => FilterAnnouncements();
        WhenFiltersChange(FilterAnnouncements, _announcementGroup, _announcementKind, _announcementState, _announcementWords, _announcementFields);

        var resetAll = Action(Plain("Reset all to the first words"), async () => {
            if (!await ConfirmAsync("Put every announcement and message back to its first words and turn them all on, with every group?")) {
                return;
            }

            _showingAnnouncements = true;
            foreach (var row in _announcementRows) {
                row.Words.Text = row.Line.Default;
                row.On.IsChecked = true;
            }

            foreach (var group in _announcementGroups) {
                group.On.IsChecked = true;
            }

            _showingAnnouncements = false;
            await SaveAnnouncementsAsync();
            Say("Every announcement and message is back to its first words, and all of them are on.");
        });

        var cards = new List<Control> {
            Fold("search", "Announcements", "Find an announcement or message",
                "Change the words of anything the server announces or tells a player, or turn it off, one line or a whole group at a time. "
                + "Changes are used the next time the line is sent; no restart.",
                WithHint(_announcementSearch,
                    "Each line has a box with its words. Words in {curly brackets} are filled in by the server, such as {event} "
                    + "becoming Fire Deckathalon; the list under each box says what each one becomes. Leave them in, move them, "
                    + "or take out the ones you do not want. Turn a line Off to stop it being sent at all, or turn a group's "
                    + "\"Send this whole group\" switch Off to stop every line in it. Reset puts back the first words. "
                    + "The groups after Server notices are the [Server Message] lines and boxes the server sends one player, such as "
                    + "replies to commands or \"Your backpack is full\"; they start folded shut, so click a group's title to open it. "
                    + "Changes save by themselves a moment after you stop typing."),
                SearchFilters(SearchFilter("Group", _announcementGroup), SearchFilter("Kind", _announcementKind),
                    SearchFilter("On or off", _announcementState), SearchFilter("Words", _announcementWords), SearchTick(_announcementFields)),
                _announcementCount,
                Row(resetAll)),
            AnnouncementCard(),
        };
        cards.AddRange(_announcementGroups.Select(g => (Control) g.Card));
        cards.Add(Fold("sword", "Bosses", "Battle announcements",
            "Lines a boss or cheat shows in battle are part of each cheat, so you change them where the cheat is made.",
            Sub("Open the Bosses tab, pick the boss, and change the words in its cheat's blocks (such as say a line, "
                + "show a banner or announce to the battle). Player cheats are on the Player Cheats tab (Create). "
                + "The \"Announcement\" heading and chat start that announce to the battle puts around its words are the two "
                + "lines under Your announcements above.")));

        FilterAnnouncements();

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
        if (line.Fields.Length > 0 && line.Fields.All(f => f.Means == ServerMessageCatalog.FILLED)) {
            body.Children.Add(Sub($"Filled in by the server: {string.Join(", ", line.Fields.Select(f => f.Field))}"));
        }
        else if (line.Fields.Length > 0) {
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
        var pickedGroup = FilterPick(_announcementGroup);
        var pickedKind = FilterPick(_announcementKind);
        var state = _announcementState.SelectedIndex;
        var words = _announcementWords.SelectedIndex;
        var fields = _announcementFields.IsChecked == true;
        var narrowed = query.Length > 0 || pickedGroup is not null || state > 0 || words > 0 || fields;
        int total = 0, all = 0;
        foreach (var (group, card, rows, groupOn, _, isMessage) in _announcementGroups) {
            var shown = 0;
            var groupMatches = (pickedGroup is null || group == pickedGroup)
                && (pickedKind is null || pickedKind == (isMessage ? KIND_MESSAGES : KIND_ANNOUNCEMENTS));
            foreach (var row in rows) {
                var changed = (row.Words.Text ?? "") != row.Line.Default;
                var on = row.On.IsChecked == true && groupOn.IsChecked == true;
                var match = groupMatches
                    && state switch { 1 => on, 2 => !on, _ => true }
                    && words switch { 1 => changed, 2 => !changed, _ => true }
                    && (!fields || row.Line.Fields.Length > 0)
                    && (query.Length == 0
                        || SearchText.Matches($"{group} {row.Line.Name} {row.Line.When} {row.Line.Default} {row.Words.Text} " +
                                              string.Join(' ', row.Line.Fields.Select(f => $"{f.Field} {f.Means}")), query));
                row.View.IsVisible = match;
                shown += match ? 1 : 0;
                all++;
            }

            total += shown;
            // A group with nothing listed yet still shows its switch, unless a search or filter is narrowing the list.
            card.IsVisible = shown > 0 || (rows.Count == 0 && groupMatches && !narrowed);
            if (narrowed && shown > 0 && isMessage) {
                card.SetOpen(true);
            }
        }

        _announcementCount.Text = FilterCount(total, all, "announcements and messages");
    }

    private void QueueAnnouncementSave() {
        if (_showingAnnouncements) {
            return;
        }

        _announcementSave.Stop();
        _announcementSave.Start();
    }

    private (Dictionary<string, string> Texts, HashSet<string> Off, HashSet<string> OffGroups) LoadAnnouncements() {
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var off = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

                if (json["OffGroups"] is JsonArray groups) {
                    offGroups.UnionWith(groups.Where(v => v?.GetValueKind() == JsonValueKind.String).Select(v => v!.GetValue<string>()));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            Say($"Could not read {AnnouncementCatalog.FILE}: {ex.Message}. The first words are shown.", isError: true);
        }

        return (texts, off, offGroups);
    }

    /// <summary>
    /// The messages on no list that the server has sent (server-messages-seen.json, newest first), as lines of the
    /// Other messages group. Their words are matched whole, as they have no {fields}.
    /// </summary>
    private List<AnnouncementLine> SeenMessages() {
        var path = Path.Combine(_manager.ServerFolder, ServerMessageCatalog.SEEN_FILE);
        var lines = new List<AnnouncementLine>();
        try {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonArray seen) {
                foreach (var entry in seen.OfType<JsonObject>()) {
                    var text = entry["Text"]?.GetValueKind() == JsonValueKind.String ? entry["Text"]!.GetValue<string>() : "";
                    if (text.Trim().Length == 0) {
                        continue;
                    }

                    var count = entry["Count"]?.GetValueKind() == JsonValueKind.Number ? entry["Count"]!.GetValue<int>() : 0;
                    var flat = string.Join(' ', text.Split((char[]) ['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)).Trim();
                    lines.Add(new AnnouncementLine(ServerMessageCatalog.SeenKey(text), ServerMessageCatalog.OTHER_GROUP,
                        flat.Length <= 60 ? flat : flat[..57].TrimEnd() + "...",
                        $"Sent {(count == 1 ? "once" : $"{count} times")} so far. On no list, so these exact words are changed or turned off as a whole.",
                        text, []));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) {
            Say($"Could not read {ServerMessageCatalog.SEEN_FILE}: {ex.Message}.", isError: true);
        }

        return lines;
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
            ["OffGroups"] = new JsonArray([.. _announcementGroups.Where(g => g.On.IsChecked != true).Select(g => (JsonNode) JsonValue.Create(g.Group)!)]),
        };
        Directory.CreateDirectory(_manager.ServerFolder);
        var temp = $"{AnnouncementsPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temp, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, AnnouncementsPath, overwrite: true);
        Say("Announcements and messages saved. The server uses them from now on.");
    }

}
