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
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// The BROKEN and PARTLY marks, the "Hide broken cards" box and Imlight's own descriptions, shared by the Spells tab
/// and the Lookup tab's spell view (Niduus, 2026-10-22: "mark cards that don't work as BROKEN and to exclude them from
/// searched (via toggle, on by default)").
/// </summary>
internal sealed partial class ManagerWindow {

    private const string CORRECTED_TIP = "The words in { } are Imlight's, written from the spell's effects, because the game's card " +
        "text is blank or gives no numbers. Search for { to find every card like this.";

    // Both spell views have the box; ticking one ticks the other.
    private readonly List<CheckBox> _hideBrokenBoxes = [];
    private bool? _hideBroken;

    private bool HideBroken {
        get => _hideBroken ??= _manager.HideBrokenSpells;
        set {
            if (_hideBroken == value) {
                return;
            }

            _hideBroken = value;
            _manager.HideBrokenSpells = value;
            foreach (var box in _hideBrokenBoxes) {
                box.IsChecked = value;
            }
        }
    }

    /// <summary>A "Hide broken cards" box; <paramref name="changed"/> runs when it is ticked or cleared.</summary>
    private CheckBox HideBrokenBox(Action changed) {
        var box = new CheckBox { Content = "Hide broken cards", IsChecked = HideBroken };
        ToolTip.SetTip(box, "Leaves out the cards Imlight's server can't do yet (marked BROKEN). " +
            "A card searched for by its ID still shows.");
        box.IsCheckedChanged += (_, _) => {
            var hide = box.IsChecked == true;
            if (hide != HideBroken) {
                HideBroken = hide;
                changed();
            }
        };
        _hideBrokenBoxes.Add(box);

        return box;
    }

    /// <summary>The "N broken hidden" note for a count line, or "".</summary>
    private static string BrokenHiddenNote(int hidden) => hidden > 0 ? $"  ·  {hidden:N0} broken hidden" : "";

    /// <summary>A small red BROKEN or amber PARTLY mark, with why in its tooltip; null for a card that works.</summary>
    private static Border? SpellStatusBadge(SpellCard card, double fontSize = 11) {
        if (card.Support.Status == SpellStatus.Works) {
            return null;
        }

        var broken = card.Broken;
        var badge = new Border {
            Background = broken ? BAD : WARN,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock {
                Text = broken ? "BROKEN" : "PARTLY", FontSize = fontSize, FontWeight = FontWeight.Bold,
                Foreground = broken ? Brushes.White : Brushes.Black,
            },
        };
        ToolTip.SetTip(badge, card.Support.Summary);

        return badge;
    }

    /// <summary>The mark and why, for a card's details: "BROKEN  The server does not do ...".</summary>
    private static Control? SpellStatusLine(SpellCard card) {
        if (SpellStatusBadge(card, 12) is not { } badge) {
            return null;
        }

        badge.VerticalAlignment = VerticalAlignment.Top;
        badge.Margin = new Thickness(0, 1, 8, 0);

        return new DockPanel {
            Margin = new Thickness(0, 2, 0, 2),
            Children = {
                WithDock(badge, Dock.Left),
                new SelectableTextBlock {
                    Text = card.Support.Summary, TextWrapping = TextWrapping.Wrap, Foreground = card.Broken ? BAD : WARN,
                },
            },
        };
    }

    /// <summary>What the card does: the game's words, then Imlight's corrected words in braces when it has any.</summary>
    private static SelectableTextBlock SpellDoesText(SpellCard card, int maxLines = 0) {
        var text = new SelectableTextBlock { Text = card.Shown, TextWrapping = TextWrapping.Wrap };
        if (maxLines > 0) {
            text.MaxLines = maxLines;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
        }

        if (card.Corrected.Length > 0) {
            ToolTip.SetTip(text, CORRECTED_TIP);
        }

        return text;
    }

}
