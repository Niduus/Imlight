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

using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// The "Try it" box of a cheat's edit page (CheatTry walks the cheat): tick which Ifs to pretend true, and read what would
/// go off, in order, at once. It changes with every edit while it is open.
/// </summary>
internal sealed partial class ManagerWindow {

    private readonly HashSet<CheatRule> _tryOpen = [];

    // What the owner pretends of each If (true when not ticked off).
    private readonly ConditionalWeakTable<CheatBlock, StrongBox<bool>> _pretend = new();

    // Walks the open Try it box again (a box of a block changed).
    private System.Action? _tryRefresh;

    private bool Pretends(CheatBlock block) => !_pretend.TryGetValue(block, out var box) || box.Value;

    private Control TryButton(CheatBoard board) {
        var button = Action(Plain("▶ Try it"), () => {
            if (board.Editing is { } rule && !_tryOpen.Remove(rule)) {
                _tryOpen.Add(rule);
            }

            RenderCheats(board);

            return Task.CompletedTask;
        });
        ToolTip.SetTip(button, "Walk through this cheat in a pretend fight: see what goes off, in which order, and what is skipped. Nothing is cast.");

        return button;
    }

    /// <summary>The Try it box, or null while it is closed.</summary>
    private Control? TryPanel(CheatBoard board, CheatRule rule) {
        _tryRefresh = null;
        if (!_tryOpen.Contains(rule)) {
            return null;
        }

        var result = new StackPanel { Spacing = 2 };
        void Walk() {
            result.Children.Clear();
            foreach (var line in CheatTry.Walk(rule, Pretends)) {
                result.Children.Add(new SelectableTextBlock {
                    Text = line, TextWrapping = TextWrapping.Wrap, FontSize = 13,
                    FontFamily = line.StartsWith(' ') ? new FontFamily("Consolas, Menlo, monospace") : FontFamily.Default,
                });
            }
        }

        var pretend = new WrapPanel();
        foreach (var block in CheatTry.Pretendable(rule)) {
            var box = new CheckBox { IsChecked = Pretends(block), Margin = new Thickness(0, 0, 16, 2), Content = CheatTry.Bare(block) };
            box.IsCheckedChanged += (_, _) => {
                _pretend.AddOrUpdate(block, new StrongBox<bool>(box.IsChecked == true));
                Walk();
            };
            pretend.Children.Add(box);
        }

        Walk();
        _tryRefresh = Walk;
        var panel = new StackPanel {
            Spacing = 8,
            Children = {
                new TextBlock { Text = "Try it: a pretend fight", FontWeight = FontWeight.SemiBold, FontSize = 15 },
                Sub(pretend.Children.Count > 0 ? "Untick an If to pretend it does not hold this time:" : "This cheat has no If blocks; it goes off every time its When does."),
                pretend,
                result,
            },
        };

        return ManagerTheme.Item(panel);
    }

}
