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
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup.Blocks;

/// <summary>One block a palette offers: its kind, icon, short words and what it does in plain words.</summary>
internal sealed record PaletteEntry(BlockKind Kind, string Icon, string Label, string Help, object Payload);

/// <summary>
/// The side palette of an editor: its blocks grouped by kind (When, If, Do), each a small block to click or drag into
/// a script. A search box narrows the list.
/// </summary>
internal sealed class BlockPalette : Border {

    private readonly StackPanel _groups = new() { Spacing = 4 };
    private readonly TextBox _search = new() { Watermark = "Find a block", FontSize = 13 };
    private readonly List<PaletteEntry> _entries;
    private readonly Action<PaletteEntry> _picked;

    internal BlockPalette(IEnumerable<PaletteEntry> entries, Action<PaletteEntry> picked, string title = "Blocks") {
        _entries = [.. entries];
        _picked = picked;
        Background = new SolidColorBrush(Color.FromArgb(28, 128, 128, 128));
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(10);
        _search.TextChanged += (_, _) => Render();
        Child = new DockPanel {
            Children = {
                Docked(new StackPanel {
                    Spacing = 6, Margin = new Thickness(0, 0, 0, 8),
                    Children = {
                        new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 15 },
                        new TextBlock {
                            Text = "Click a block to add it to the open cheat, or drag it where you want it.",
                            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(170, 160, 160, 160)),
                        },
                        _search,
                    },
                }, Dock.Top),
                new ScrollViewer { Content = _groups, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
            },
        };
        Render();
    }

    private static T Docked<T>(T control, Dock dock) where T : Control {
        DockPanel.SetDock(control, dock);

        return control;
    }

    private void Render() {
        _groups.Children.Clear();
        var words = (_search.Text ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var kind in new[] { BlockKind.When, BlockKind.If, BlockKind.Do }) {
            var found = _entries.Where(e => e.Kind == kind && words.All(w => (e.Label + " " + e.Help).ToLowerInvariant().Contains(w))).ToList();
            if (found.Count == 0) {
                continue;
            }

            _groups.Children.Add(new StackPanel {
                Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 4),
                Children = {
                    new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = BlockColors.Fill(kind), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock {
                        Text = kind switch { BlockKind.When => "When… (what sets it off)", BlockKind.If => "If… (only when)", _ => "Do… (what it does)" },
                        FontWeight = FontWeight.SemiBold, FontSize = 13,
                    },
                },
            });
            foreach (var entry in found) {
                _groups.Children.Add(Chip(entry));
            }
        }
    }

    /// <summary>A small block of the palette.</summary>
    internal static BlockShape MiniBlock(PaletteEntry entry) => new() {
        Hat = entry.Kind == BlockKind.When,
        Fill = BlockColors.Fill(entry.Kind),
        Edge = BlockColors.Edge(entry.Kind),
        Padding = new Thickness(8, (entry.Kind == BlockKind.When ? BlockShape.HAT_HEIGHT : BlockShape.NOTCH_DEPTH) + 3, 8, 5),
        MinWidth = 150,
        Child = new StackPanel {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children = {
                BlockIcons.Icon(entry.Icon, 15, BlockColors.TEXT),
                new TextBlock { Text = entry.Label, Foreground = BlockColors.TEXT, FontSize = 13, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center },
            },
        },
    };

    private Control Chip(PaletteEntry entry) {
        var chip = MiniBlock(entry);
        chip.Margin = new Thickness(0, 2, 0, 4);
        chip.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
        ToolTip.SetTip(chip, entry.Help);
        BlockDrag.MakeDraggable(chip, entry.Kind, entry, () => MiniBlock(entry), () => _picked(entry));

        return chip;
    }

}
