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
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Imlight.Setup.Blocks;

namespace Imlight.Setup;

/// <summary>
/// The boss and custom editor as sections with an icon each (Niduus, 2026-10-22): Identity, Stats, Deck, Drops, Forms
/// Rewards and Cheats, with a row of chips at the top to jump to one. Forms show only for a custom, the only kind of
/// file that has them.
/// </summary>
internal sealed partial class ManagerWindow {

    private readonly Border _bossPortrait = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly StackPanel _customIdentity = new() { Spacing = 8, IsVisible = false };
    private readonly WrapPanel _customDropChips = new() { Orientation = Orientation.Horizontal };
    private readonly TextBox _customDropSearch = new() { Watermark = "Search a drop table to add, such as Malistaire or CL-MAIN", Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _customDropResults = new() { Spacing = 2 };
    private readonly DispatcherTimer _customDropDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private List<DropTableInfo>? _dropTables;
    private Section? _identitySection, _statsSection, _deckSection, _dropsSection, _formsSection, _rewardsSection;
    private readonly TextBox _rewardXp = new() { Width = 140 };
    private readonly TextBox _rewardGoldMin = new() { Width = 140 };
    private readonly TextBox _rewardGoldMax = new() { Width = 140 };
    private readonly ComboBox _rewardShared = new() {
        Width = 300, ItemsSource = new[] { "Each wizard gets it all", "Split between the wizards" }, SelectedIndex = 0,
    };

    /// <summary>Fills the editor with its sections; <paramref name="stats"/> is the Other stats list the Bosses tab made.</summary>
    private void BuildBossSections(Control stats) {
        _identitySection = new Section("id", BlockColors.Accent("identity"), "Identity",
            "Who it is: the game creature it comes from and, for a custom, its number, name and the .joinme line that brings it in.",
            IdentityContent());
        _statsSection = new Section("stats", BlockColors.Accent("stats"), "Stats",
            "Health, level, school, and the damage, resist and other stats it fights with. Leave a box empty to keep the game's value.",
            new StackPanel {
                Children = {
                    new TextBlock { Text = "Stats by school", FontSize = 15, FontWeight = FontWeight.SemiBold },
                    Sub("The grey number in an empty box is what it fights with then. A number under All counts for every school " +
                        "you leave empty in that row."),
                    WithMargin(SchoolGrid(), new Thickness(0, 10, 0, 0)),
                    WithMargin(new TextBlock { Text = "Other stats", FontSize = 15, FontWeight = FontWeight.SemiBold }, new Thickness(0, 18, 0, 0)),
                    WithMargin(stats, new Thickness(0, 8, 0, 0)),
                },
            });
        _deckSection = new Section("cards", BlockColors.Accent("deck"), "Deck",
            "The spells it can cast. Copies is how many times it can use a spell in one battle; 0 means it never runs out, like the game's own creatures.",
            new StackPanel {
                Children = {
                    new DockPanel {
                        Children = {
                            WithDock(Action(Plain("Put back the game's deck"), () => {
                                if (_boss is not null && _bossCatalog is not null) {
                                    _bossDeck = _bossCatalog.GameDeck(_boss);
                                    _bossDeckChanged = true;
                                    RenderBossDeck();
                                }

                                return Task.CompletedTask;
                            }), Dock.Right),
                            WithCenter(_bossDeckNote),
                        },
                    },
                    WithMargin(_bossDeckRows, new Thickness(0, 8, 0, 0)),
                    WithMargin(new TextBlock { Text = "Add a spell", FontSize = 15, FontWeight = FontWeight.SemiBold }, new Thickness(0, 14, 0, 4)),
                    _bossSpellSearch,
                    WithMargin(_bossSpellResults, new Thickness(0, 6, 0, 0)),
                },
            });
        _dropsSection = new Section("bag", BlockColors.Accent("drops"), "Drops",
            "What it drops when it is defeated: one or more drop tables, the server's or your own. With none, it drops what the game's creature drops.",
            DropsContent());
        _rewardsSection = new Section("star", BlockColors.Accent("rewards"), "XP and gold",
            "What a win over it gives, instead of what its drop tables and the game give. Leave a box empty to keep that.",
            RewardsContent());
        _formsSection = new Section("swap", BlockColors.Accent("forms"), "Forms",
            "A boss that turns into another creature (Malistaire into Haunted Malistaire) is that creature from then on. " +
            "Name a custom of that creature here to have it fight as your custom instead.",
            new StackPanel {
                Spacing = 8,
                Children = {
                    _customFormRows,
                    Row(Action(Plain("Add a form"), () => {
                        AddCustomFormRow("", "");

                        return Task.CompletedTask;
                    })),
                    Sub("A cheat's Change form block can also bring in a custom; this list is for the forms the game itself makes."),
                },
            });

        var cheats = (Section) CheatsCard();
        var sections = new[] { _identitySection, _statsSection, _deckSection, _dropsSection, _rewardsSection, _formsSection, cheats };
        _bossEditor.Children.Add(Section.Nav(sections));
        foreach (var section in sections) {
            _bossEditor.Children.Add(section);
        }

        _bossEditor.Children.Add(Card(new StackPanel {
            Children = {
                Row(Action(Primary("Save changes"), SaveBossAsync),
                    Danger(Action(Plain("Put back the game's values"), ResetBossAsync))),
                WithMargin(Sub("Saved changes count from its next battle. There is no need to restart the server; " +
                    "a battle that is already going keeps the old values. The game files are never changed."), new Thickness(0, 8, 0, 0)),
            },
        }));

        _customDropDelay.Tick += (_, _) => {
            _customDropDelay.Stop();
            RenderDropResults();
        };
        _customDropSearch.TextChanged += (_, _) => {
            _customDropDelay.Stop();
            _customDropDelay.Start();
        };
        _customDrops.TextChanged += (_, _) => RenderDropChips();
        foreach (var box in new[] { _rewardXp, _rewardGoldMin, _rewardGoldMax }) {
            box.TextChanged += (_, _) => RewardsBadge();
        }
    }

    private Control IdentityContent() {
        _customIdentity.Children.Add(StatRow("Bring it in", Row(_customJoinMe, Action(Plain("Copy"), () => CopyAsync(_customJoinMe.Text ?? ""))),
            "Type this in a battle. Its stats, deck, cheats and drops count only for it."));
        _customIdentity.Children.Add(StatRow("Name", _customName, "The name it shows in battle. Empty: the creature's own name."));

        var info = new StackPanel {
            Spacing = 4,
            Children = { _bossTitle, _customHeaderTitle, _customHeaderBase, _bossGame, _bossGameStats, _bossChangedNote },
        };
        var top = new DockPanel { Children = { WithDock(new Border { Margin = new Thickness(0, 0, 16, 0), Child = _bossPortrait }, Dock.Left), info } };

        return new StackPanel { Spacing = 12, Children = { top, _customIdentity } };
    }

    private Control DropsContent() => new StackPanel {
        Spacing = 6,
        Children = {
            _customDropChips,
            StatRow("Drop tables", _customDrops, "Their names, with commas between. Empty: the creature's own drops."),
            WithMargin(_customGameDrops, new Thickness(190, 0, 0, 0)),
            WithMargin(new DockPanel {
                Children = {
                    WithDock(Action(Primary("New drop table"), () => {
                        NewDropTable();

                        return Task.CompletedTask;
                    }), Dock.Right),
                    _customDropSearch,
                },
            }, new Thickness(0, 8, 0, 0)),
            Sub("Search a table to add it, or to change it: your own tables change as they are, the server's change as a copy. " +
                "New drop table makes one of your own, with gold, XP, items and treasure cards."),
            _customDropResults,
            _dropEditorHost,
        },
    };

    private Control RewardsContent() => new StackPanel {
        Spacing = 6,
        Children = {
            StatRow("XP", _rewardXp, "Experience each wizard gets for the win. Empty: what its drop tables give."),
            StatRow("Gold from", _rewardGoldMin, "The least gold a win gives. Empty: what its drop tables or the game give."),
            StatRow("Gold to", _rewardGoldMax, "The most gold a win gives; a random amount in between. Empty: the same as Gold from."),
            StatRow("Who gets it", _rewardShared, "All: every wizard who fought gets the full XP and gold. Split: they share it, so four wizards get a quarter each."),
            Sub("Items still come from the drop tables above. Saved with Save changes; counts from the next win, without a restart."),
        },
    };

    /// <summary>Fills the XP and gold boxes, and for a boss of the Bosses tab its drops, from its file.</summary>
    private void LoadRewards(BossInfo boss) {
        var extra = (_bossCatalog is null ? null : BossChanges.Load(_editFolder, _editFileId, _bossCatalog.Spells))?.Extra ?? [];
        string Value(string key) => BossChanges.FindIn(extra, key) is JsonValue v ? v.ToString() : "";
        _rewardXp.Text = Value("XP");
        _rewardGoldMin.Text = Value("GoldMin");
        _rewardGoldMax.Text = Value("GoldMax");
        _rewardShared.SelectedIndex = Value("RewardShared").Equals("true", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        var level = Math.Max(1, boss.Level);
        var gold = (Min: level * 3 + 5, Max: level * 6 + 5);
        gold = boss.IsBoss ? (gold.Min * 3, gold.Max * 3) : gold;
        _rewardXp.Watermark = "Its drop tables' XP";
        _rewardGoldMin.Watermark = $"Game: about {gold.Min:N0}";
        _rewardGoldMax.Watermark = $"Game: about {gold.Max:N0}";
        RewardsBadge();

        if (_editCustom is null) {
            _customDrops.Text = BossChanges.FindIn(extra, "Drops") is JsonArray drops
                ? string.Join(", ", drops.Select(d => (d as JsonValue)?.ToString() ?? "").Where(d => d.Length > 0)) : "";
            var gameDrops = GameDrops(boss.Id);
            _customDrops.Watermark = gameDrops.Count > 0 ? "The creature's own drops" : "Only gold, as the creature";
            _customGameDrops.Text = gameDrops.Count > 0 ? "The creature's own: " + string.Join(", ", gameDrops) : "The creature drops only gold.";
        }
    }

    private void RewardsBadge() => _rewardsSection?.SetBadge(
        new[] { _rewardXp, _rewardGoldMin, _rewardGoldMax }.Any(b => (b.Text ?? "").Trim().Length > 0) ? "Yours" : "The game's");

    /// <summary>Puts the XP and gold, and a boss's drops, into the file Save writes (a custom's drops come from CustomHeaderInto).</summary>
    private void RewardsInto(BossChanges changes) {
        var xp = WholeNumber(_rewardXp, "XP", 0, 100_000_000);
        var min = WholeNumber(_rewardGoldMin, "Gold from", 0, 10_000_000);
        var max = WholeNumber(_rewardGoldMax, "Gold to", 0, 10_000_000);
        if (min is not null && max is not null && max < min) {
            throw new PanelException("Gold to is the most gold a win gives, so it cannot be less than Gold from.");
        }

        foreach (var key in new[] { "XP", "GoldMin", "GoldMax", "RewardShared", "Drops" }) {
            changes.Extra.Remove(key);
        }

        if (xp is { } x) {
            changes.Extra["XP"] = x;
        }

        if (min is { } lo) {
            changes.Extra["GoldMin"] = lo;
        }

        if (max is { } hi) {
            changes.Extra["GoldMax"] = hi;
        }

        if (_rewardShared.SelectedIndex == 1) {
            changes.Extra["RewardShared"] = true;
        }

        var drops = CustomDropNames();
        if (_editCustom is null && drops.Count > 0) {
            changes.Extra["Drops"] = new JsonArray([.. drops.Select(d => (JsonNode) JsonValue.Create(d)!)]);
        }
    }

    /// <summary>Shows the sections that fit what is open: Drops and Forms only for a custom.</summary>
    private void UpdateBossSections() {
        if (_boss is not { } boss) {
            return;
        }

        var custom = _editCustom is not null;
        _bossTitle.IsVisible = !custom;
        _customHeaderTitle.IsVisible = custom;
        _customHeaderBase.IsVisible = custom;
        _customIdentity.IsVisible = custom;
        if (_formsSection is not null) {
            _formsSection.IsVisible = custom;
        }

        _identitySection?.SetBadge(custom ? $"Custom {_editCustom!.Id}" : $"ID {boss.Id}");
        _deckSection?.SetBadge($"{_bossDeck.Count} spells" + (_bossDeckChanged ? ", yours" : ""));
        _bossPortrait.Child = MobPortrait(custom ? _editCustom!.Shown : boss.Name, boss.School, 76);
        _customDropSearch.Text = "";
        _customDropResults.Children.Clear();
        CloseDropEditor();
        LoadRewards(boss);
        RenderDropChips();
        EnsureGameIcons(() => {
            if (_boss is { } open) {
                _bossPortrait.Child = MobPortrait(_editCustom?.Shown ?? open.Name, open.School, 76);
                RenderBossDeck();
            }
        });
    }

    private List<string> CustomDropNames() =>
        [.. (_customDrops.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private void RenderDropChips() {
        _customDropChips.Children.Clear();
        var drops = CustomDropNames();
        _dropsSection?.SetBadge(drops.Count == 0 ? "The creature's own" : $"{drops.Count} tables");
        foreach (var name in drops) {
            var table = DropTables().FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var edit = new Button { Padding = new Thickness(4, 2), Background = Brushes.Transparent, Content = BlockIcons.Icon("pencil", 12, Brushes.White), IsVisible = table is not null };
            ToolTip.SetTip(edit, table is { AppMade: true } ? $"Change {name}" : $"Change a copy of {name}");
            edit.Click += (_, _) => Guarded(() => EditDropTable(table!));
            var remove = new Button { Padding = new Thickness(4, 2), Background = Brushes.Transparent, Content = BlockIcons.Icon("trash", 12, Brushes.White) };
            ToolTip.SetTip(remove, $"Take {name} off");
            remove.Click += (_, _) => {
                _customDrops.Text = string.Join(", ", CustomDropNames().Where(d => !d.Equals(name, StringComparison.OrdinalIgnoreCase)));
            };
            var description = table is null ? "This table was not found in spiraldb-extra/DropTables; the server may have it in its own data." : table.Description;
            var chip = new Border {
                Background = BlockColors.Accent("drops"), CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 3, 4, 3),
                Margin = new Thickness(0, 0, 8, 6),
                Child = new StackPanel {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children = {
                        BlockIcons.Icon("bag", 14, Brushes.White),
                        new TextBlock { Text = name, Foreground = Brushes.White, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                        edit,
                        remove,
                    },
                },
            };
            if (!string.IsNullOrEmpty(description)) {
                ToolTip.SetTip(chip, description);
            }

            _customDropChips.Children.Add(chip);
        }
    }

    /// <summary>The drop tables of spiraldb-extra/DropTables, the server's and your own, read again after a save.</summary>
    private List<DropTableInfo> DropTables() {
        if (_dropTables is not null) {
            return _dropTables;
        }

        _dropTables = [];
        try {
            var folder = Path.Combine(_manager.ExtraFolder, "DropTables");
            if (Directory.Exists(folder)) {
                foreach (var file in Directory.EnumerateFiles(folder, "*.json")) {
                    try {
                        if (JsonNode.Parse(File.ReadAllText(file), documentOptions: new System.Text.Json.JsonDocumentOptions {
                                AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                            }) is JsonObject json && json["Name"]?.ToString() is { Length: > 0 } name) {
                            var made = json["MadeInApp"] is JsonValue m && m.TryGetValue<bool>(out var b) && b;
                            _dropTables.Add(new DropTableInfo(name, json["Description"]?.ToString() ?? "", file, made));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) {
                        // A broken file is left out.
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // No list; the box still takes names.
        }

        _dropTables.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        return _dropTables;
    }

    private void RenderDropResults() {
        _customDropResults.Children.Clear();
        var words = (_customDropSearch.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) {
            return;
        }

        var found = DropTables().Where(t => words.All(w => t.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
                                                        || t.Description.Contains(w, StringComparison.OrdinalIgnoreCase))).Take(8).ToList();
        if (found.Count == 0) {
            _customDropResults.Children.Add(Sub(DropTables().Count == 0 ? "The server's drop tables were not found." : "No drop table matches."));
        }

        foreach (var table in found) {
            var (name, description) = (table.Name, table.Description);
            _customDropResults.Children.Add(new DockPanel {
                Children = {
                    WithDock(Action(Plain(table.AppMade ? "Change" : "Change a copy"), () => {
                        EditDropTable(table);

                        return Task.CompletedTask;
                    }), Dock.Right),
                    WithDock(Action(Plain("Add"), () => {
                        var drops = CustomDropNames();
                        if (!drops.Contains(name, StringComparer.OrdinalIgnoreCase)) {
                            drops.Add(name);
                            _customDrops.Text = string.Join(", ", drops);
                        }

                        return Task.CompletedTask;
                    }), Dock.Right),
                    WithCenter(new StackPanel {
                        Orientation = Orientation.Horizontal, Spacing = 8,
                        Children = {
                            BlockIcons.Icon("bag", 14, BlockColors.Accent("drops")),
                            new TextBlock { Text = name, FontWeight = FontWeight.SemiBold },
                            new TextBlock { Text = table.AppMade ? "yours" : "the server's", Foreground = BlockColors.Accent(table.AppMade ? "rewards" : "drops") },
                            new TextBlock { Text = description, Foreground = MUTED },
                        },
                    }),
                },
            });
        }
    }

}
