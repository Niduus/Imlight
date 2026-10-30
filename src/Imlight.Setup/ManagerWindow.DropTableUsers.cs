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
using Imlight.Setup.Blocks;

namespace Imlight.Setup;

/// <summary>Who drops a drop table: a creature of the game (a boss or not), a quest, or one of your customs or boss changes.</summary>
internal sealed record DropTableUser(string Name, string Kind, string Where) {

    internal const string BOSS = "Boss";
    internal const string CREATURE = "Creature";
    internal const string QUEST = "Quest";
    internal const string CUSTOM = "Your custom";
    internal const string CHANGED = "Your boss change";

    internal bool IsCreature => Kind is BOSS or CREATURE or CUSTOM or CHANGED;

    internal string Line => Where.Length > 0 ? $"{Name} ({Kind.ToLowerInvariant()}, {Where})" : $"{Name} ({Kind.ToLowerInvariant()})";

}

/// <summary>
/// Which creatures, bosses and quests use each drop table (Niduus, 2026-10-30: "become transparent about drop tables
/// about from which mob/boss when searched"): the server's drop-table-users.json (written each start beside ids.csv)
/// plus your own customs and boss changes, read fresh each search so a save shows at once.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string DROP_USERS_FILE = "drop-table-users.json";
    private const int DROP_RESULTS_SHOWN = 25;

    private const string DROP_WHOSE_MINE = "Yours";
    private const string DROP_WHOSE_SERVER = "The server's";
    private const string DROP_USED_BOSS = "A boss";
    private const string DROP_USED_CREATURE = "Any creature";
    private const string DROP_USED_QUEST = "A quest";
    private const string DROP_USED_MINE = "Your customs";
    private const string DROP_USED_NOTHING = "Nothing";

    private readonly ComboBox _dropWhose = FilterCombo([ANY_FILTER, DROP_WHOSE_MINE, DROP_WHOSE_SERVER]);
    private readonly ComboBox _dropUsedBy = FilterCombo([ANY_FILTER, DROP_USED_BOSS, DROP_USED_CREATURE, DROP_USED_QUEST, DROP_USED_MINE, DROP_USED_NOTHING]);
    private readonly TextBlock _dropFound = Sub("");

    private Dictionary<string, List<DropTableUser>>? _dropGameUsers;
    private DateTime _dropGameUsersFrom;

    private sealed record UsersNpc(uint Id, string Name, List<string> Zones, bool Boss);

    private sealed record UsersQuest(string Code, string Title);

    private sealed record UsersOf(List<int> Creatures, List<int> Quests);

    private sealed record UsersFile(List<UsersNpc> Creatures, List<UsersQuest> Quests, Dictionary<string, UsersOf> Tables);

    /// <summary>Whether the server has written which creatures use each table yet (it does so a minute or two after it starts).</summary>
    private bool HasDropGameUsers => File.Exists(Path.Combine(_manager.IdLookupFolder, DROP_USERS_FILE));

    /// <summary>The game's creatures and quests that use each table, read again when the server writes the file again.</summary>
    private Dictionary<string, List<DropTableUser>> DropGameUsers() {
        var path = Path.Combine(_manager.IdLookupFolder, DROP_USERS_FILE);
        try {
            var written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (_dropGameUsers is not null && written == _dropGameUsersFrom) {
                return _dropGameUsers;
            }

            var users = new Dictionary<string, List<DropTableUser>>(StringComparer.OrdinalIgnoreCase);
            if (written != DateTime.MinValue && JsonSerializer.Deserialize<UsersFile>(File.ReadAllText(path)) is { } file) {
                foreach (var (table, of) in file.Tables ?? []) {
                    var list = new List<DropTableUser>();
                    foreach (var npc in (of.Creatures ?? []).Where(i => i >= 0 && i < file.Creatures.Count).Select(i => file.Creatures[i])) {
                        list.Add(new DropTableUser(npc.Name is { Length: > 0 } name ? name : $"Creature {npc.Id}",
                            npc.Boss ? DropTableUser.BOSS : DropTableUser.CREATURE, ZonePlace(npc.Zones)));
                    }

                    foreach (var quest in (of.Quests ?? []).Where(i => i >= 0 && i < file.Quests.Count).Select(i => file.Quests[i])) {
                        list.Add(new DropTableUser(quest.Title is { Length: > 0 } title ? title : quest.Code, DropTableUser.QUEST, "reward"));
                    }

                    users[table] = list;
                }
            }

            (_dropGameUsers, _dropGameUsersFrom) = (users, written);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            // Being written: read on the next search.
        }

        return _dropGameUsers ?? new Dictionary<string, List<DropTableUser>>(StringComparer.OrdinalIgnoreCase);
    }

    // The first place a creature stands, as "Zone, World" (or just the world).
    private static string ZonePlace(List<string>? zones) {
        var zone = zones?.FirstOrDefault() ?? "";
        if (zone.Length == 0) {
            return "";
        }

        var parts = zone.Split('|');
        var world = WorldOfZone(parts[0]);
        var name = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : parts[0];

        return name.Equals(world, StringComparison.OrdinalIgnoreCase) ? world : $"{name}, {world}";
    }

    /// <summary>Every user of every table: the game's, then your customs and boss changes (which drop those tables instead).</summary>
    private Dictionary<string, List<DropTableUser>> AllDropUsers() {
        var all = new Dictionary<string, List<DropTableUser>>(StringComparer.OrdinalIgnoreCase);
        void Add(string table, DropTableUser user) {
            if (!all.TryGetValue(table, out var list)) {
                all[table] = list = [];
            }

            list.Add(user);
        }

        try {
            foreach (var custom in CustomMob.All(_manager.CustomsFolder)) {
                foreach (var table in custom.Drops.Distinct(StringComparer.OrdinalIgnoreCase)) {
                    Add(table, new DropTableUser(custom.Shown, DropTableUser.CUSTOM, $"custom {custom.Id}"));
                }
            }

            if (Directory.Exists(_manager.BossChangesFolder)) {
                foreach (var file in Directory.EnumerateFiles(_manager.BossChangesFolder, "*.json")) {
                    try {
                        if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject json || BossChanges.FindIn(json, "Drops") is not JsonArray drops) {
                            continue;
                        }

                        var id = Path.GetFileNameWithoutExtension(file);
                        var name = _bossCatalog?.Creatures.FirstOrDefault(c => c.Id == id)?.Name is { Length: > 0 } known ? known : $"Creature {id}";
                        foreach (var table in drops.Select(d => (d as JsonValue)?.ToString() ?? "").Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)) {
                            Add(table, new DropTableUser(name, DropTableUser.CHANGED, $"ID {id}"));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
                        // A file being written is left out until the next search.
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Your own users show again on the next search.
        }

        foreach (var (table, users) in DropGameUsers()) {
            foreach (var user in users) {
                Add(table, user);
            }
        }

        return all;
    }

    /// <summary>"Malistaire the Undying (boss, Castle Darkmoor) and 3 more", or what to say when nothing uses it.</summary>
    private string DropUsersLine(List<DropTableUser>? users, int names = 2) {
        if (users is not { Count: > 0 }) {
            return HasDropGameUsers ? "No creature or quest drops it." : "Start the server once to see which creatures drop it.";
        }

        // Bosses and your own first, then other creatures, then quests; one line per name.
        var shown = users.OrderBy(u => u.Kind switch { DropTableUser.CUSTOM or DropTableUser.CHANGED => 0, DropTableUser.BOSS => 1, DropTableUser.CREATURE => 2, _ => 3 })
            .DistinctBy(u => u.Name + "|" + u.Kind, StringComparer.OrdinalIgnoreCase).ToList();
        var line = string.Join(", ", shown.Take(names).Select(u => u.Line));

        return shown.Count > names ? $"{line} and {shown.Count - names} more" : line;
    }

    /// <summary>The whole list of who drops a table, for its tooltip.</summary>
    private static string DropUsersTip(List<DropTableUser>? users) {
        if (users is not { Count: > 0 }) {
            return "Nothing drops this table.";
        }

        var lines = users.DistinctBy(u => u.Line, StringComparer.OrdinalIgnoreCase).Select(u => u.Line).ToList();

        return "Dropped by:\n" + string.Join("\n", lines.Take(40)) + (lines.Count > 40 ? $"\n…and {lines.Count - 40} more" : "");
    }

    private bool DropUsedByFilter(List<DropTableUser>? users) => FilterPick(_dropUsedBy) switch {
        DROP_USED_BOSS => users?.Any(u => u.Kind is DropTableUser.BOSS) == true,
        DROP_USED_CREATURE => users?.Any(u => u.IsCreature) == true,
        DROP_USED_QUEST => users?.Any(u => u.Kind is DropTableUser.QUEST) == true,
        DROP_USED_MINE => users?.Any(u => u.Kind is DropTableUser.CUSTOM or DropTableUser.CHANGED) == true,
        DROP_USED_NOTHING => users is not { Count: > 0 },
        _ => true,
    };

    private bool DropWhoseFilter(DropTableInfo table) => FilterPick(_dropWhose) switch {
        DROP_WHOSE_MINE => table.AppMade,
        DROP_WHOSE_SERVER => !table.AppMade,
        _ => true,
    };

    private void RenderDropResults() {
        _customDropResults.Children.Clear();
        // An empty box lists the tables too (Niduus, 2026-10-25: "have them all generate search results if left empty").
        // The words also find a table by who drops it, so "Malistaire" lists the tables Malistaire drops.
        var query = _customDropSearch.Text ?? "";
        var users = AllDropUsers();
        List<DropTableUser>? UsersOf(DropTableInfo table) => users.TryGetValue(table.Name, out var list) ? list : null;
        var found = DropTables()
            .Where(t => DropWhoseFilter(t) && DropUsedByFilter(UsersOf(t)))
            .Select(t => (Table: t, Users: UsersOf(t), ByName: SearchText.Matches($"{t.Name} {t.Description}", query)))
            .Where(f => f.ByName || f.Users?.Any(u => SearchText.Matches($"{u.Name} {u.Where}", query)) == true)
            // A table named by the words comes before one found by who drops it; your own and boss tables first.
            .OrderBy(f => !f.ByName)
            .ThenBy(f => !f.Table.AppMade)
            .ThenBy(f => f.Users?.Any(u => u.Kind is DropTableUser.BOSS or DropTableUser.CUSTOM or DropTableUser.CHANGED) != true)
            .ThenBy(f => f.Table.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _dropFound.Text = DropTables().Count == 0 ? "" : found.Count > DROP_RESULTS_SHOWN
            ? $"Showing {DROP_RESULTS_SHOWN} of {found.Count} tables. Type more words to narrow it."
            : $"{found.Count} table{(found.Count == 1 ? "" : "s")} found.";
        if (found.Count == 0) {
            _customDropResults.Children.Add(Sub(DropTables().Count == 0 ? "The server's drop tables were not found." : "No drop table matches."));
        }

        foreach (var (table, tableUsers, byName) in found.Take(DROP_RESULTS_SHOWN)) {
            _customDropResults.Children.Add(DropResultRow(table, tableUsers, byName ? null : query));
        }
    }

    // A table of the search: its name, whose it is, who drops it, and Use / Change / Copy / Export.
    private Control DropResultRow(DropTableInfo table, List<DropTableUser>? users, string? foundBy) {
        var name = table.Name;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var use = Action(Plain("Use"), () => {
            var drops = CustomDropNames();
            if (!drops.Contains(name, StringComparer.OrdinalIgnoreCase)) {
                drops.Add(name);
                _customDrops.Text = string.Join(", ", drops);
            }

            Say($"This creature now drops {name}. Press Save changes at the bottom to keep it.");

            return Task.CompletedTask;
        });
        ToolTip.SetTip(use, "This creature drops this table as it is. Changing the table later changes it for everyone who drops it.");
        buttons.Children.Add(use);
        if (table.AppMade) {
            var change = Action(Plain("Change"), () => {
                EditDropTable(table);

                return Task.CompletedTask;
            });
            ToolTip.SetTip(change, "Change your table. Everything that drops it gets the change.");
            buttons.Children.Add(change);
        }

        var copy = Action(Plain(table.AppMade ? "Copy" : "Copy to change"), () => {
            CopyDropTable(table);

            return Task.CompletedTask;
        });
        ToolTip.SetTip(copy, "Make a copy of your own to change. The table itself stays as it is, and so does everything that drops it.");
        buttons.Children.Add(copy);
        if (table.AppMade) {
            var export = Action(Plain("Export…"), () => ExportDropTablesAsync([table]));
            ToolTip.SetTip(export, "Save this table as a .ahziael file to share. A friend opens it on the Share tab.");
            buttons.Children.Add(export);
        }

        var dropped = new TextBlock {
            Text = "Dropped by: " + DropUsersLine(users), Foreground = MUTED, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };
        ToolTip.SetTip(dropped, DropUsersTip(users));
        var info = new StackPanel {
            Spacing = 1,
            Children = {
                new StackPanel {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = {
                        BlockIcons.Icon("bag", 14, BlockColors.Accent("drops")),
                        new TextBlock { Text = name, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = table.AppMade ? "yours" : "the server's", Foreground = BlockColors.Accent(table.AppMade ? "rewards" : "drops") },
                    },
                },
                dropped,
            },
        };
        if (table.Description.Length > 0) {
            info.Children.Add(new TextBlock { Text = table.Description, Foreground = MUTED, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        }

        if (foundBy is { Length: > 0 } && users?.FirstOrDefault(u => SearchText.Matches($"{u.Name} {u.Where}", foundBy)) is { } match) {
            info.Children.Add(new TextBlock { Text = $"Found because {match.Line} drops it.", Foreground = BlockColors.Accent("drops"), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        }

        return new Border {
            Padding = new Thickness(8, 6), Margin = new Thickness(0, 0, 0, 2), CornerRadius = new CornerRadius(8), Background = ManagerTheme.Glass(6),
            Child = new DockPanel { Children = { WithDock(WithMargin(buttons, new Thickness(10, 0, 0, 0)), Dock.Right), info } },
        };
    }

    /// <summary>Opens a copy of any table in the editor, under a free name; the table itself stays as it is.</summary>
    private void CopyDropTable(DropTableInfo table) {
        var draft = DropDraft.Load(table.File);
        draft.CopiedFrom = draft.Name;
        draft.Name = UniqueTableName(draft.Name + "-Copy");
        draft.OldName = "";
        draft.File = null;
        if (draft.Description.Length == 0 || !table.AppMade) {
            draft.Description = $"A copy of {draft.CopiedFrom}, made in the app.";
        }

        OpenDropEditor(draft);
        Say($"You are changing a copy of {draft.CopiedFrom} named {draft.Name}; {draft.CopiedFrom} stays as it is. Save the table to keep the copy.");
    }

    /// <summary>Saves these tables of yours as one .ahziael file, the Share tab's format.</summary>
    private async Task ExportDropTablesAsync(IReadOnlyCollection<DropTableInfo>? tables) {
        var manager = _manager;
        var mine = await Task.Run(() => CustomShare.Mine(manager, "drops"));
        var items = tables is null
            ? mine
            : mine.Where(i => tables.Any(t => string.Equals(Path.GetFullPath(t.File), Path.GetFullPath(CustomShare.TargetOf(manager, i.Kind, i.Key)),
                StringComparison.OrdinalIgnoreCase))).ToList();
        if (items.Count == 0) {
            throw new PanelException(tables is null
                ? "You have no drop tables of your own yet. Make one with New drop table, or Copy to change one of the server's."
                : "Only your own tables can be exported. Copy to change this one first, then save the copy.");
        }

        await ExportShareItemsAsync(items, items.Count == 1 ? items[0].Name : "My drop tables");
    }

    // The Export and Import buttons under the drop table search.
    private Control DropShareButtons() {
        var exportAll = Action(Plain("Export my tables…"), () => ExportDropTablesAsync(null));
        ToolTip.SetTip(exportAll, "Save all your own drop tables as one .ahziael file to share or keep.");
        var import = Action(Plain("Import tables…"), async () => {
            SelectTab("Share");
            await OpenShareFileAsync();
        });
        ToolTip.SetTip(import, "Open a .ahziael file on the Share tab: you see every table in it first, and nothing changes until you press Import ticked.");

        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { exportAll, import } };
    }

}
