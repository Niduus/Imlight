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

using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// "Examples to copy" in the cheat editor (Niduus, 2026-10-26: "You can legit do anything"): the example cheat files of
/// spiraldb-extra/CheatExamples (CheatExamples), each with what it shows, copied into the open list of cheats as new ones.
/// </summary>
internal sealed partial class ManagerWindow {

    private Button ExamplesButton(CheatBoard board) {
        var button = Plain("Examples to copy");
        ToolTip.SetTip(button, "Ready-made cheats that show off the blocks (rage, a timed meteor, a mirror phase...). Copy one in and change it as you like.");
        button.Click += (_, _) => Guarded(() => {
            var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft, ShowMode = FlyoutShowMode.Standard };
            var list = new StackPanel { Spacing = 14, Width = 460 };
            var examples = CheatExamples.Load(CheatExtraFolder);
            if (examples.Count == 0) {
                list.Children.Add(Sub("The examples come with the server (spiraldb-extra/CheatExamples). Update the server to get them."));
            }

            foreach (var example in examples) {
                var copy = Plain(example.Cheats.Count == 1 ? "Copy this cheat in" : $"Copy these {example.Cheats.Count} cheats in");
                copy.Click += (_, _) => Guarded(() => {
                    if (board.List is null) {
                        throw new PanelException(board.Player ? "The player cheats are not open yet." : "Pick a boss first.");
                    }

                    var rules = CheatExamples.Copy(example, board.List.Cheats.Count, board.Player);
                    board.List.Cheats.AddRange(rules);
                    board.Editing = null;
                    RenderCheats(board);
                    flyout.Hide();
                    Say($"\"{example.Name}\" is copied in as {rules.Count} new {(rules.Count == 1 ? "cheat" : "cheats")}. Save to keep {(rules.Count == 1 ? "it" : "them")}.");
                });
                var item = new StackPanel { Spacing = 4 };
                item.Children.Add(new TextBlock { Text = example.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
                if (example.Notes.Length > 0) {
                    item.Children.Add(Sub(example.Notes));
                }

                foreach (var cheat in example.Cheats) {
                    item.Children.Add(new TextBlock {
                        Text = "• " + CheatSpecs.Summary(CheatRule.FromCheat(cheat, player: board.Player)), FontSize = 12, TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(8, 0, 0, 0),
                    });
                }

                item.Children.Add(copy);
                list.Children.Add(item);
            }

            flyout.Content = new ScrollViewer { MaxHeight = 520, Content = list };
            flyout.ShowAt(button);
        });

        return button;
    }

}
