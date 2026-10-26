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
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
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
/// The Quest Tree tab on the Database page (Niduus, 2026-10-26: "in database, add Quest Tree in detail to show the main
/// quest tree's in their entirety"): every world's main story, quest by quest in the order it is played, with the side
/// quests each one opens, and for each quest who gives it, its steps, its rewards, what comes before and after, and
/// whether this server has it. The order and the real game's details are built into the app (Assets/quest-tree.json.gz,
/// made by tools/quest-tree/build_quest_tree.py from Spiral Tracker and Wizard404); the server's own quests come from
/// quest-details.json and quest-guide.txt in the ID lookup folder, the same files the Quests and Quest Editor tabs read.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string QUEST_TREE_ASSET = "quest-tree.json.gz";

    // The built-in tree, as tools/quest-tree/build_quest_tree.py writes it.
    private sealed class QtWiki {
        public ulong Id { get; init; }
        public string Giver { get; init; } = "";
        public List<string> Goals { get; init; } = [];
        public int Level { get; init; }
        public int Xp { get; init; }
        public int Gold { get; init; }
        public int Tp { get; init; }
        public List<ulong> Items { get; init; } = [];
        public List<string> Spells { get; init; } = [];
        public List<string> Other { get; init; } = [];
    }

    private sealed class QtStep {
        public string Kind { get; init; } = "";
        public string Text { get; init; } = "";
        public int Count { get; init; }
        public List<string> Flags { get; init; } = [];
    }

    private sealed class QtMain {
        public string Name { get; init; } = "";
        public string Area { get; init; } = "";
        public string Location { get; init; } = "";
        public List<QtStep> Steps { get; init; } = [];
        public int Xp { get; init; }
        public int Gold { get; init; }
        public List<string> Prev { get; init; } = [];
        public List<string> Next { get; init; } = [];
        public bool Instance { get; init; }
        public bool Solo { get; init; }
        public int Level { get; init; }
        public QtWiki? Wiki { get; init; }
    }

    private sealed class QtSide {
        public string Name { get; init; } = "";
        public string Area { get; init; } = "";
        public int Xp { get; init; }
        public int Gold { get; init; }
        public List<string> UnlockedBy { get; init; } = [];
        public int Level { get; init; }
        public QtWiki? Wiki { get; init; }
        public List<QtSide> Next { get; init; } = [];
    }

    private sealed class QtWorld {
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public bool SideWorld { get; init; }
        public List<QtMain> Main { get; init; } = [];
        public List<QtSide> Side { get; init; } = [];
    }

    private sealed class QtFile {
        public List<QtWorld> Worlds { get; init; } = [];
    }

    /// <summary>One quest of the tree: a main story quest, or a side quest under the main quest that opens it.</summary>
    private sealed class QtQuest {
        public required QtWorld World { get; init; }
        public required string Name { get; init; }
        public required string Area { get; init; }
        // Its place in the world's main story (1 is the first), or 0 for a side quest.
        public int Number { get; init; }
        // 0 for a main quest; 1 for a side quest a main quest opens, 2 for the one after it, and so on.
        public int Depth { get; init; }
        public QtMain? Main { get; init; }
        public QtSide? Side { get; init; }
        public QtWiki? Wiki => Main?.Wiki ?? Side?.Wiki;
        // The main quest that opens this side quest (null for a main quest or one open from the start), and the side quest before it.
        public QtQuest? OpenedBy { get; set; }
        public QtQuest? After { get; set; }
        public List<QtQuest> Sides { get; } = [];
        public List<QtQuest> SideNext { get; } = [];

        // The server's quest of the same name, joined in when the server's files are read.
        public QeQuest? Server { get; set; }
        public QuestInfo? Guide { get; set; }
        public string Haystack { get; set; } = "";

        public bool IsMain => Main is not null;
        public int Level => Server is { Level: > 0 } s ? s.Level : Wiki is { Level: > 0 } w ? w.Level : Main?.Level ?? Side?.Level ?? 0;
        public int Xp => Main?.Xp ?? Side?.Xp ?? 0;
        public int Gold => Main?.Gold ?? Side?.Gold ?? 0;
        public bool Dungeon => Main is { } m && (m.Instance || m.Steps.Any(s => s.Kind == "instance" || s.Flags.Contains("instance")));
        public bool Branches => Main is { Next.Count: > 1 };
        public string Giver => Server is { GiverName.Length: > 0 } s ? s.GiverName : Wiki?.Giver ?? "";
    }

    private enum QtRowKind { World, Area, Quest }

    private sealed record QtRow(QtRowKind Kind, string Text, string Line, QtQuest? Quest, QtWorld? World);

    private static readonly string[] s_qtServer = [ANY_FILTER, "On the server", "Not on the server"];
    private static readonly string[] s_qtGivers = [ANY_FILTER, "Known", "Not known", "Starts by itself"];

    private readonly TextBox _qtSearch = new() { Watermark = "Search a quest, an area, an NPC, a boss, a step or a reward" };
    private readonly ComboBox _qtWorld = FilterCombo([ANY_FILTER], 170);
    private readonly ComboBox _qtArea = FilterCombo([ANY_FILTER], 200);
    private readonly ComboBox _qtServer = FilterCombo(s_qtServer, 150);
    private readonly ComboBox _qtStepKind = FilterCombo([ANY_FILTER], 130);
    private readonly ComboBox _qtGiver = FilterCombo(s_qtGivers, 140);
    private readonly NumericUpDown _qtLevelMin = FilterNumber(5);
    private readonly NumericUpDown _qtLevelMax = FilterNumber(5);
    private readonly NumericUpDown _qtXpMin = FilterNumber(1000, 140);
    private readonly NumericUpDown _qtXpMax = FilterNumber(1000, 140);
    private readonly CheckBox _qtSides = new() { Content = "Show side quests under the quest that opens them" };
    private readonly CheckBox _qtDungeons = new() { Content = "Only dungeons" };
    private readonly CheckBox _qtBranches = new() { Content = "Only quests that branch" };
    private readonly CheckBox _qtSideWorlds = new() { Content = "Include side worlds", IsChecked = true };
    private readonly TextBlock _qtCount = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _qtList = new() { Background = Brushes.Transparent };
    private readonly StackPanel _qtDetails = new() { Spacing = 6 };

    private List<QtWorld> _qtWorlds = [];
    private List<QtQuest> _qtQuests = [];
    private DateTime _qtJoinedDetails = DateTime.MinValue;
    private DateTime _qtJoinedGuide = DateTime.MinValue;
    private QtQuest? _qtPicked;

    private Control QuestTreeTab() {
        _qtList.ItemTemplate = new FuncDataTemplate<QtRow>((row, _) => row is null ? new Panel() : QuestTreeRowView(row));
        _qtList.SelectionChanged += (_, e) => {
            if (e.Source != _qtList) {
                return;
            }

            if (_qtList.SelectedItem is QtRow { Quest: { } quest }) {
                if (quest != _qtPicked) {
                    ShowQuestTreeQuest(quest);
                }
            }
            else if (_qtList.SelectedItem is not null) {
                // A world or area heading is not something to pick.
                _qtList.SelectedItem = null;
            }
        };
        WhenFiltersChange(RenderQuestTree, _qtSearch, _qtWorld, _qtArea, _qtServer, _qtStepKind, _qtGiver, _qtLevelMin, _qtLevelMax,
            _qtXpMin, _qtXpMax, _qtSides, _qtDungeons, _qtBranches, _qtSideWorlds);

        var details = new ScrollViewer { Width = 450, Margin = new Thickness(14, 0, 0, 0), Content = Card(_qtDetails) };
        ShowQuestTreeQuest(null);

        return new DockPanel {
            Margin = new Thickness(24, 12, 24, 16),
            Children = {
                WithDock(details, Dock.Right),
                WithDock(Fold("flag", "Quests", "Main quest tree",
                    "Every world's main story in the order it is played, and the side quests each quest opens. Click a quest to see all of it.",
                    WithHint(_qtSearch, "With nothing typed, every world's whole main story shows, top to bottom. Type words of a quest's name, " +
                        "its area, the NPC who gives it, a boss or creature it sends you after, a step or a reward. " +
                        "Not on the server: the real game has the quest, but this server has no quest by that name yet, so it can't be " +
                        "played here. The order comes from Spiral Tracker and the real game's steps and rewards from Wizard404; " +
                        "the server's own steps and rewards show once the server has started (it writes them as it starts)."),
                    SearchFilters(
                        SearchFilter("World", _qtWorld),
                        SearchFilter("Area", _qtArea),
                        SearchFilter("On this server", _qtServer),
                        SearchFilter("Has a step to", _qtStepKind),
                        SearchFilter("Who gives it", _qtGiver),
                        SearchFilter("Level from", _qtLevelMin),
                        SearchFilter("Level to", _qtLevelMax),
                        SearchFilter("XP from", _qtXpMin),
                        SearchFilter("XP to", _qtXpMax),
                        SearchTick(_qtSides),
                        SearchTick(_qtDungeons),
                        SearchTick(_qtBranches),
                        SearchTick(_qtSideWorlds)),
                    _qtCount), Dock.Top),
                WithMargin(_qtList, new Thickness(0, 14, 0, 0)),
            },
        };
    }

    /// <summary>Reads the built-in tree once, and the server's quests again when it wrote them since. Runs when the tab shows.</summary>
    private async Task LoadQuestTreeAsync() {
        await LoadLookupAsync();
        if (_qtWorlds.Count == 0) {
            _qtCount.Text = "Loading…";
            var (worlds, quests) = await Task.Run(ReadQuestTree);
            (_qtWorlds, _qtQuests) = (worlds, quests);
            FillFilterCombo(_qtWorld, worlds.Select(w => w.Name));
            FillFilterCombo(_qtArea, quests.Select(q => q.Area).OrderBy(a => a, StringComparer.OrdinalIgnoreCase));
            FillFilterCombo(_qtStepKind, quests.SelectMany(q => q.Main?.Steps ?? []).Select(s => StepKindShown(s.Kind))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
            _qtJoinedDetails = _qtJoinedGuide = DateTime.MinValue;
        }

        LoadQuestDetails();
        LoadQuestGuide();
        if (_qtJoinedDetails != _qeDataWritten || _qtJoinedGuide != _questsWritten) {
            (_qtJoinedDetails, _qtJoinedGuide) = (_qeDataWritten, _questsWritten);
            JoinServerQuests();
            RenderQuestTree();
            if (_qtPicked is not null) {
                ShowQuestTreeQuest(_qtPicked);
            }
        }
        else if (_qtList.ItemsSource is null) {
            RenderQuestTree();
        }
    }

    private static (List<QtWorld>, List<QtQuest>) ReadQuestTree() {
        QtFile? file;
        try {
            var assembly = typeof(ManagerWindow).Assembly.GetName().Name;
            using var stream = AssetLoader.Open(new Uri($"avares://{assembly}/Assets/{QUEST_TREE_ASSET}"));
            using var unzip = new GZipStream(stream, CompressionMode.Decompress);
            file = JsonSerializer.Deserialize<QtFile>(unzip, s_qeJson);
        }
        catch (Exception) {
            // A build without the file, or a broken one.
            return ([], []);
        }

        var worlds = file?.Worlds ?? [];
        var all = new List<QtQuest>();
        foreach (var world in worlds) {
            var mains = world.Main.Select((m, i) => new QtQuest { World = world, Name = m.Name, Area = m.Area, Number = i + 1, Main = m }).ToList();
            all.AddRange(mains);
            var byName = mains.GroupBy(m => SearchText.Normalize(m.Name)).ToDictionary(g => g.Key, g => g.First());

            void AddChain(QtSide side, QtQuest? opener, QtQuest? before, int depth) {
                var quest = new QtQuest { World = world, Name = side.Name, Area = side.Area, Depth = depth, Side = side, OpenedBy = opener, After = before };
                all.Add(quest);
                if (before is not null) {
                    before.SideNext.Add(quest);
                }
                else if (opener is not null) {
                    opener.Sides.Add(quest);
                }

                foreach (var next in side.Next) {
                    AddChain(next, opener, quest, depth + 1);
                }
            }

            foreach (var chain in world.Side) {
                // The main quest that opens it: the earliest of its "unlocked by" quests this world's story has.
                var opener = chain.UnlockedBy.Select(n => byName.GetValueOrDefault(SearchText.Normalize(n))).Where(q => q is not null)
                    .OrderBy(q => q!.Number).FirstOrDefault();
                AddChain(chain, opener, null, 1);
            }
        }

        return (worlds, all);
    }

    /// <summary>Finds each tree quest's quest on the server by its name (in the same world when two share a name).</summary>
    private void JoinServerQuests() {
        var byTitle = _qeData.Quests.Where(q => q.Title.Length > 0).GroupBy(q => SearchText.Normalize(q.Title))
            .ToDictionary(g => g.Key, g => g.ToList());
        var guide = _quests.GroupBy(q => q.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        // The Catacombs are part of Wizard City for the server's quest codes.
        static string WorldOfTree(QtWorld world) => world.Key == "catacombs" ? "Wizard City" : world.Name;
        var treeNames = _qtQuests.Select(q => (SearchText.Normalize(q.Name), WorldOfTree(q.World))).ToHashSet();
        foreach (var quest in _qtQuests) {
            QeQuest? found = null;
            var name = SearchText.Normalize(quest.Name);
            if (byTitle.TryGetValue(name, out var same)) {
                string WorldOf(QeQuest q) => QuestWorldOf(q.Code, guide.GetValueOrDefault(q.Code)?.Zone ?? "");
                found = same.OrderBy(q => WorldOf(q) == WorldOfTree(quest.World) ? 0 : 1)
                    .ThenBy(q => q.Main == quest.IsMain ? 0 : 1).ThenBy(q => q.Code, StringComparer.OrdinalIgnoreCase).First();
                // A quest of another world whose own world has a quest by this name is that one's, not this one's.
                var foundWorld = WorldOf(found);
                if (foundWorld != WorldOfTree(quest.World) && treeNames.Contains((name, foundWorld))) {
                    found = null;
                }
            }

            quest.Server = found;
            quest.Guide = found is null ? null : guide.GetValueOrDefault(found.Code);
            var main = quest.Main;
            var wiki = quest.Wiki;
            quest.Haystack = SearchText.Normalize(string.Join(' ', [
                quest.Name, quest.Area, main?.Location ?? "", quest.World.Name, quest.Giver, wiki?.Giver ?? "",
                string.Join(' ', main?.Steps.Select(s => $"{StepKindShown(s.Kind)} {s.Text}") ?? []),
                string.Join(' ', wiki?.Goals ?? []), string.Join(' ', wiki?.Spells ?? []), string.Join(' ', wiki?.Other ?? []),
                string.Join(' ', wiki?.Items.Select(id => ItemName(id)) ?? []),
                found is null ? "" : $"{found.Code} {string.Join(' ', found.Goals.Select(g => $"{g.Text} {g.Place}"))} {string.Join(' ', found.Rewards.Items)}",
                quest.Guide?.Zone ?? "",
            ]));
        }
    }

    private string ItemName(ulong id) {
        var key = id.ToString(CultureInfo.InvariantCulture);
        return _lookup?.Entries.FirstOrDefault(e => e.Kind == "Item" && e.Id == key)?.Name ?? $"Item {key}";
    }

    private static string StepKindShown(string kind) => kind switch {
        "talk" => "Talk",
        "explore" => "Go to a place",
        "mob" => "Defeat creatures",
        "boss" => "Defeat a boss",
        "elite" => "Defeat an elite",
        "interact" => "Use an object",
        "collect" => "Collect",
        "d&c" => "Defeat and collect",
        "instance" => "Dungeon",
        "puzzle" => "Puzzle",
        "cheat" => "Cheating boss",
        "" => "Other",
        _ => char.ToUpperInvariant(kind[0]) + kind[1..],
    };

    private bool QuestTreeMatches(QtQuest quest, string[] words, string? area, string? kind) {
        var server = _qtServer.SelectedIndex;
        var giver = _qtGiver.SelectedIndex;
        var levels = _qtLevelMin.Value is not null || _qtLevelMax.Value is not null;

        return words.All(w => quest.Haystack.Contains(w, StringComparison.Ordinal))
               && (area is null || string.Equals(quest.Area, area, StringComparison.OrdinalIgnoreCase))
               && server switch {
                   1 => quest.Server is not null,
                   2 => quest.Server is null,
                   _ => true,
               }
               && giver switch {
                   1 => quest.Giver.Length > 0,
                   2 => quest.Giver.Length == 0 && quest.Server?.StartsByItself != true,
                   3 => quest.Server?.StartsByItself == true,
                   _ => true,
               }
               && (kind is null || quest.Main?.Steps.Any(s => StepKindShown(s.Kind) == kind) == true)
               && (!levels || quest.Level > 0 && InFilterRange(quest.Level, _qtLevelMin, _qtLevelMax))
               && InFilterRange(quest.Xp, _qtXpMin, _qtXpMax)
               && (_qtDungeons.IsChecked != true || quest.Dungeon)
               && (_qtBranches.IsChecked != true || quest.Branches);
    }

    private void RenderQuestTree() {
        if (_qtWorlds.Count == 0) {
            _qtList.ItemsSource = null;
            _qtCount.Text = "The quest tree isn't built into this copy of Ahziael. Update Ahziael on the Tools tab.";

            return;
        }

        var words = SearchText.Words(_qtSearch.Text);
        var world = FilterPick(_qtWorld);
        var area = FilterPick(_qtArea);
        var kind = FilterPick(_qtStepKind);
        var sides = _qtSides.IsChecked == true;
        var rows = new List<QtRow>();
        int shownMain = 0, shownSide = 0;
        foreach (var w in _qtWorlds) {
            if (world is not null ? w.Name != world : w.SideWorld && _qtSideWorlds.IsChecked != true) {
                continue;
            }

            var mains = _qtQuests.Where(q => q.World == w && q.IsMain).ToList();
            var worldRows = new List<QtRow>();
            var lastArea = "";
            void AddQuest(QtQuest quest) {
                if (quest.IsMain && !string.Equals(quest.Area, lastArea, StringComparison.OrdinalIgnoreCase)) {
                    lastArea = quest.Area;
                    worldRows.Add(new QtRow(QtRowKind.Area, quest.Area, "", null, w));
                }

                worldRows.Add(new QtRow(QtRowKind.Quest, quest.Name, "", quest, w));
            }

            void AddSides(IEnumerable<QtQuest> chain) {
                foreach (var side in chain) {
                    if (QuestTreeMatches(side, words, area, kind)) {
                        AddQuest(side);
                        shownSide++;
                    }

                    AddSides(side.SideNext);
                }
            }

            foreach (var main in mains) {
                if (QuestTreeMatches(main, words, area, kind)) {
                    AddQuest(main);
                    shownMain++;
                }

                if (sides) {
                    AddSides(main.Sides);
                }
            }

            if (sides) {
                var fromStart = _qtQuests.Where(q => q.World == w && q.Side is not null && q.OpenedBy is null && q.After is null).ToList();
                var before = worldRows.Count;
                lastArea = "\0";
                foreach (var side in fromStart) {
                    if (worldRows.Count == before) {
                        // The header only shows when one of them matches.
                        var any = Flatten(side).Any(q => QuestTreeMatches(q, words, area, kind));
                        if (!any) {
                            continue;
                        }

                        worldRows.Add(new QtRow(QtRowKind.Area, "Side quests open from the start", "", null, w));
                    }

                    AddSides([side]);
                }
            }

            if (worldRows.Count == 0) {
                continue;
            }

            var onServer = mains.Count(q => q.Server is not null);
            rows.Add(new QtRow(QtRowKind.World, w.Name + (w.SideWorld ? "  (side world)" : ""),
                $"{mains.Count:N0} main story quests: {onServer:N0} on this server, {mains.Count - onServer:N0} not. " +
                $"{_qtQuests.Count(q => q.World == w && !q.IsMain):N0} side quests.", null, w));
            rows.AddRange(worldRows);
        }

        _qtList.ItemsSource = rows;
        var allMain = _qtQuests.Count(q => q.IsMain);
        var serverMain = _qtQuests.Count(q => q.IsMain && q.Server is not null);
        var head = FilterCount(shownMain, allMain, "main story quests");
        var serverLine = _qeData.Quests.Count == 0
            ? " Start the server once to see which ones it has (it writes its quest list as it starts)."
            : $" This server has {serverMain:N0} of the {allMain:N0}; {allMain - serverMain:N0} are not on it yet.";
        _qtCount.Text = head + (sides ? $" {shownSide:N0} side quests shown." : "") + serverLine;
    }

    private static IEnumerable<QtQuest> Flatten(QtQuest quest) => new[] { quest }.Concat(quest.SideNext.SelectMany(Flatten));

    private Control QuestTreeRowView(QtRow row) {
        if (row.Kind == QtRowKind.World) {
            return new StackPanel {
                Margin = new Thickness(2, 16, 0, 4), Spacing = 2,
                Children = {
                    new TextBlock { Text = row.Text, FontSize = 20, FontWeight = FontWeight.Bold, Foreground = ManagerTheme.Accent("Quests") },
                    Sub(row.Line),
                },
            };
        }

        if (row.Kind == QtRowKind.Area) {
            return new TextBlock {
                Text = row.Text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.Bold, Foreground = MUTED,
                Margin = new Thickness(4, 10, 0, 2),
            };
        }

        var quest = row.Quest!;
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock {
            Text = quest.IsMain ? $"{quest.Number}." : "↳", Foreground = MUTED, FontSize = 14, MinWidth = quest.IsMain ? 34 : 0,
            VerticalAlignment = VerticalAlignment.Center,
        });
        title.Children.Add(new TextBlock {
            Text = quest.Name, FontWeight = quest.IsMain ? FontWeight.SemiBold : FontWeight.Normal, FontSize = quest.IsMain ? 15 : 14,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (!quest.IsMain) {
            title.Children.Add(QtTag("Side quest", MUTED));
        }

        title.Children.Add(quest.Server is null ? QtTag("Not on the server", BAD) : QtTag("On the server", GOOD));
        if (quest.Dungeon) {
            title.Children.Add(QtTag("Dungeon", WARN));
        }

        if (quest.Branches) {
            title.Children.Add(QtTag($"Leads to {quest.Main!.Next.Count}", ManagerTheme.Accent("Quests")));
        }

        var bits = new List<string>();
        if (quest.Level > 0) {
            bits.Add($"level {quest.Level}");
        }

        if (quest.Giver.Length > 0) {
            bits.Add($"from {quest.Giver}");
        }
        else if (quest.Server?.StartsByItself == true) {
            bits.Add("starts by itself");
        }

        if (quest.Main is { Steps.Count: > 0 } main) {
            bits.Add(StepsSummary(main));
        }
        else if (quest.Wiki is { Goals.Count: > 0 } wiki) {
            bits.Add($"{wiki.Goals.Count} step{(wiki.Goals.Count == 1 ? "" : "s")}");
        }

        if (quest.Xp > 0 || quest.Gold > 0) {
            bits.Add($"{quest.Xp:N0} XP, {quest.Gold:N0} gold");
        }

        if (!quest.IsMain && quest.Depth == 1 && quest.OpenedBy is { } opener) {
            bits.Add($"opened by {opener.Name}");
        }
        else if (!quest.IsMain && quest.Area.Length > 0) {
            bits.Add(quest.Area);
        }

        return new Border {
            Padding = new Thickness(4 + (quest.IsMain ? 0 : 22 * quest.Depth), 5, 4, 5),
            BorderBrush = LINE,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel { Spacing = 2, Children = { title, WithMargin(Sub(string.Join("  ·  ", bits)), new Thickness(quest.IsMain ? 42 : 18, 0, 0, 0)) } },
        };
    }

    private static Control QtTag(string text, IBrush brush) => WithCenter(new Border {
        BorderBrush = brush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 0),
        Child = new TextBlock { Text = text, Foreground = brush, FontSize = 11 },
    });

    // "3 steps: talk, defeat Lost Soul, boss Rattlebones".
    private static string StepsSummary(QtMain main) {
        var parts = main.Steps.Select(s => s.Text.Length > 0 ? $"{StepKindShown(s.Kind).ToLowerInvariant()}: {s.Text}" : StepKindShown(s.Kind).ToLowerInvariant()).ToList();
        var shown = string.Join(", ", parts.Take(5)) + (parts.Count > 5 ? $" and {parts.Count - 5} more" : "");

        return $"{main.Steps.Count} step{(main.Steps.Count == 1 ? "" : "s")}: {shown}";
    }

    private QtQuest? FindTreeQuest(QtWorld world, string name) {
        var key = SearchText.Normalize(name);
        return _qtQuests.Where(q => SearchText.Normalize(q.Name) == key).OrderBy(q => q.World == world ? 0 : 1).ThenBy(q => q.IsMain ? 0 : 1).FirstOrDefault();
    }

    /// <summary>Shows a quest in the details on the right, and picks it in the list when it shows there.</summary>
    private void PickTreeQuest(QtQuest quest) {
        ShowQuestTreeQuest(quest);
        if (_qtList.ItemsSource is List<QtRow> rows && rows.FirstOrDefault(r => r.Quest == quest) is { } row) {
            _qtList.SelectedItem = row;
            _qtList.ScrollIntoView(row);
        }
    }

    private void ShowQuestTreeQuest(QtQuest? quest) {
        _qtPicked = quest;
        _qtDetails.Children.Clear();
        if (quest is null) {
            _qtDetails.Children.Add(Sub("Click a quest on the left to see who gives it, every step, its rewards, what comes before and after it, " +
                                        "and whether this server has it."));

            return;
        }

        var main = quest.Main;
        var wiki = quest.Wiki;
        var server = quest.Server;

        void Heading(string text) => _qtDetails.Children.Add(new TextBlock {
            Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.Bold, Foreground = MUTED, Margin = new Thickness(0, 10, 0, 0),
        });

        void Lines(IEnumerable<string> lines) => _qtDetails.Children.Add(new SelectableTextBlock {
            Text = string.Join('\n', lines), TextWrapping = TextWrapping.Wrap, FontSize = 13,
        });

        // Names of other quests, as buttons that show that quest.
        void Links(string label, IEnumerable<string> names) {
            var panel = new WrapPanel();
            panel.Children.Add(WithCenter(new TextBlock { Text = label, Foreground = MUTED, Margin = new Thickness(0, 0, 8, 0) }));
            foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase)) {
                var other = FindTreeQuest(quest.World, name);
                if (other is null) {
                    panel.Children.Add(WithCenter(new TextBlock { Text = name + "  ", TextWrapping = TextWrapping.Wrap }));
                    continue;
                }

                var link = Plain(other.IsMain ? name : $"{name} (side)");
                link.Margin = new Thickness(0, 2, 6, 2);
                ToolTip.SetTip(link, $"Show {name}.");
                panel.Children.Add(Action(link, () => {
                    PickTreeQuest(other);

                    return Task.CompletedTask;
                }));
            }

            _qtDetails.Children.Add(panel);
        }

        _qtDetails.Children.Add(new SelectableTextBlock { Text = quest.Name, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        var mains = _qtQuests.Count(q => q.World == quest.World && q.IsMain);
        _qtDetails.Children.Add(Sub(quest.IsMain
            ? $"{quest.World.Name}, main story quest {quest.Number} of {mains}. {quest.Area}{(main!.Location.Length > 0 ? $" ({main.Location})" : "")}."
            : $"{quest.World.Name}, side quest in {quest.Area}" +
              (quest.OpenedBy is { } by ? $", opened by {by.Name} (main story quest {by.Number})." : ".")));

        _qtDetails.Children.Add(new TextBlock {
            Text = server is null
                ? "Not on this server yet: the real game has this quest, but the server has no quest by this name, so nobody can get it here."
                : $"On this server as {server.Code}.",
            Foreground = server is null ? BAD : GOOD, TextWrapping = TextWrapping.Wrap,
        });
        if (server is null && _qeData.Quests.Count == 0) {
            _qtDetails.Children.Add(Sub("The server hasn't written its quest list here yet. Start it once and this shows what it has."));
        }

        if (server is not null) {
            var code = server.Code;
            var buttons = new WrapPanel();
            var edit = Primary("Change in Quest Editor");
            ToolTip.SetTip(edit, "Opens Create, Quest Editor, with this quest picked: change its rewards, steps, giver, level and what it needs.");
            buttons.Children.Add(Spaced(Action(edit, () => {
                OpenInQuestEditor(code);

                return Task.CompletedTask;
            })));
            var start = Plain("Start");
            ToolTip.SetTip(start, "Gives this quest now (.quest forcegrant), even if the quests before it are not done. Start the server and log in first.");
            buttons.Children.Add(Spaced(Action(start, () => RunQuestCommand($".quest forcegrant {code}", $"Started {quest.Name}. It is in your quest book now."))));
            if (quest.Guide is { Zone.Length: > 0 } guide) {
                var teleport = Plain("Teleport");
                ToolTip.SetTip(teleport, $"Takes you to {guide.Zone}, where {(guide.Giver.Length > 0 ? guide.Giver : "it starts")} is (.tp).");
                buttons.Children.Add(Spaced(Action(teleport, () => RunQuestCommand($".tp {guide.Zone}", $"Teleporting to {guide.Zone}."))));
            }

            buttons.Children.Add(Spaced(Action(Plain("Copy code"), () => CopyAsync(code))));
            _qtDetails.Children.Add(buttons);
        }

        Heading("Who gives it");
        var givers = new List<string>();
        if (server is not null) {
            givers.Add(server.StartsByItself ? "On this server it starts by itself when the quest before it is done."
                : server.GiverName.Length > 0 ? $"On this server: {server.GiverName}{(quest.Guide is { Zone.Length: > 0 } g ? $", in {g.Zone}" : "")}."
                : "On this server: not known.");
        }

        if (wiki is { Giver.Length: > 0 }) {
            givers.Add($"In the real game: {wiki.Giver}.");
        }

        Lines(givers.Count > 0 ? givers : ["Not known."]);

        if (quest.Level > 0) {
            Heading("Level");
            Lines([server is { Level: > 0 } ? $"Level {server.Level} on this server." : $"Level {quest.Level}."]);
        }

        Heading("Order");
        if (main is not null) {
            if (main.Prev.Count > 0) {
                Links("Comes after:", main.Prev);
            }

            if (main.Next.Count > 0) {
                Links(main.Next.Count > 1 ? "Leads to (branches):" : "Leads to:", main.Next);
            }

            if (main.Prev.Count == 0 && main.Next.Count == 0) {
                Lines(["Spiral Tracker lists nothing before or after it."]);
            }
        }
        else {
            if (quest.OpenedBy is { } opener) {
                Links("Opened by:", [opener.Name]);
            }
            else if (quest.Side!.UnlockedBy.Count > 0) {
                Links("Opened by:", quest.Side.UnlockedBy);
            }

            if (quest.After is { } after) {
                Links("Comes after:", [after.Name]);
            }

            if (quest.SideNext.Count > 0) {
                Links("Leads to:", quest.SideNext.Select(q => q.Name));
            }
        }

        if (server is { Needs.Count: > 0 }) {
            var titles = server.Needs.Select(c => _qeByCode.TryGetValue(c, out var q) ? $"{q.Title} ({c})" : c);
            Lines([$"On this server it needs first: {string.Join(", ", titles)}."]);
        }

        if (server is { Goals.Count: > 0 }) {
            Heading($"Steps on this server ({server.Goals.Count})");
            Lines(server.Goals.Select((g, i) => $"{i + 1}. {g.Text}{(g.Place.Length > 0 && !g.Text.Contains(g.Place, StringComparison.OrdinalIgnoreCase) ? $" ({g.Place})" : "")}"));
        }

        if (wiki is { Goals.Count: > 0 }) {
            Heading($"Steps in the real game ({wiki.Goals.Count})");
            Lines(wiki.Goals.Select((g, i) => $"{i + 1}. {g}"));
        }

        if (main is { Steps.Count: > 0 }) {
            Heading("Step types (Spiral Tracker)");
            Lines(main.Steps.Select((s, i) => {
                var flags = s.Flags.Count > 0 ? $" [{string.Join(", ", s.Flags)}]" : "";
                var count = s.Count > 1 ? $" x{s.Count}" : "";
                return $"{i + 1}. {StepKindShown(s.Kind)}{(s.Text.Length > 0 ? $": {s.Text}" : "")}{count}{flags}";
            }));
            if (main.Instance || main.Solo) {
                Lines([string.Join(" ", new[] { main.Instance ? "Has a dungeon." : "", main.Solo ? "Played alone." : "" }.Where(t => t.Length > 0))]);
            }
        }

        Heading("Rewards");
        var rewards = new List<string>();
        if (server is not null) {
            var r = server.Rewards;
            var gold = r.GoldMin == r.GoldMax ? $"{r.GoldMin:N0}" : $"{r.GoldMin:N0} to {r.GoldMax:N0}";
            var parts = new List<string> { $"{r.Xp:N0} XP", $"{gold} gold" };
            if (r.TrainingPoints > 0) {
                parts.Add($"{r.TrainingPoints} training point{(r.TrainingPoints == 1 ? "" : "s")}");
            }

            parts.AddRange(r.Items);
            rewards.Add($"On this server: {string.Join(", ", parts)}.");
        }

        var real = new List<string>();
        var xp = wiki is { Xp: > 0 } ? wiki.Xp : quest.Xp;
        var realGold = wiki is { Gold: > 0 } ? wiki.Gold : quest.Gold;
        if (xp > 0 || realGold > 0) {
            real.Add($"{xp:N0} XP, {realGold:N0} gold");
        }

        if (wiki is not null) {
            if (wiki.Tp > 0) {
                real.Add("a training point");
            }

            real.AddRange(wiki.Items.Select(ItemName));
            real.AddRange(wiki.Spells.Select(s => $"the spell {s}"));
            real.AddRange(wiki.Other.Select(o => o.ToLowerInvariant()));
        }

        if (real.Count > 0) {
            rewards.Add($"In the real game: {string.Join(", ", real)}.");
        }

        Lines(rewards.Count > 0 ? rewards : ["Not known."]);

        if (quest.Sides.Count > 0) {
            Heading($"Side quests this opens ({quest.Sides.Sum(s => Flatten(s).Count())})");
            foreach (var side in quest.Sides) {
                Links("", Flatten(side).Select(q => q.Name));
            }
        }

        Heading("IDs");
        var ids = new List<string>();
        if (server is not null) {
            ids.Add($"Server quest code: {server.Code}");
        }

        if (wiki is { Id: > 0 }) {
            ids.Add($"Game template ID (Wizard404): {wiki.Id}");
        }

        Lines(ids.Count > 0 ? ids : ["None known."]);
    }

    /// <summary>Opens the Quest Editor (Create page) with this quest picked.</summary>
    private void OpenInQuestEditor(string code) {
        SelectTab("Quest Editor");
        LoadQuestEditor();
        _qeSearch.Text = code;
        if (AllQuestRows().FirstOrDefault(r => r.Quest.Code.Equals(code, StringComparison.OrdinalIgnoreCase)) is { } row) {
            EditQuest(row.Quest, row.Change);
        }
    }

}
