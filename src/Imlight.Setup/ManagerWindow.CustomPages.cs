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
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The Customs tab's own pages (Niduus, 2026-10-26: "when making a custom, make a new sub page for edit/creations so
/// it's more direct instead of confusing"). The tab shows the list of every kind of custom; making or editing one opens
/// a page of that one alone, with Back to Customs at its top. The list stays as it was behind it, scrolled where it was.
/// </summary>
internal sealed partial class ManagerWindow {

    private readonly Grid _customsRoot = new();
    private readonly DockPanel _customsPage = new() { IsVisible = false };
    private readonly ScrollViewer _customsPageScroll = new();
    private readonly StackPanel _customsPageBody = new() { Spacing = ManagerTheme.GAP };
    private readonly Border _customsPageTile = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _customsPageTitle = new() { FontSize = ManagerTheme.FONT_PAGE, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _customsPageLine = Sub("");
    private Control? _customsList;

    // What the open page shows ("mob", "new", "npc", "change", "hub" or "area") and which one of them; null on the list.
    private string? _customsPageKind;
    private string _customsPageKey = "";
    private Action? _customsPageClosed;

    /// <summary>The tab: its list, and in the same place the page of one custom, shown instead of it.</summary>
    private Control CustomsPages(Control list) {
        _customsList = list;
        var back = Action(Plain("← Back to Customs"), () => {
            BackToCustoms();

            return Task.CompletedTask;
        });
        var head = Card(new DockPanel {
            Children = {
                WithDock(WithMargin(WithCenter(back), new Thickness(0, 0, 16, 0)), Dock.Left),
                WithDock(WithMargin(_customsPageTile, new Thickness(0, 0, 12, 0)), Dock.Left),
                new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2, Children = { _customsPageTitle, _customsPageLine } },
            },
        });
        head.Margin = new Thickness(24, 12, 24, 0);
        head.MaxWidth = ManagerTheme.PAGE_MAX_WIDTH;
        _customsPageBody.Margin = new Thickness(24, 12, 24, 24);
        _customsPageBody.MaxWidth = ManagerTheme.PAGE_MAX_WIDTH;
        _customsPageScroll.Content = _customsPageBody;
        _customsPage.Children.Add(WithDock(head, Dock.Top));
        _customsPage.Children.Add(_customsPageScroll);
        _customsRoot.Children.Add(list);
        _customsRoot.Children.Add(_customsPage);

        return _customsRoot;
    }

    /// <summary>
    /// Shows the page of one custom. Opening the page that is already open only draws it again, where it was scrolled;
    /// opening another one closes the one before (its <paramref name="closed"/> runs) and starts at its top.
    /// </summary>
    private void ShowCustomPage(string kind, string key, string icon, string title, string line, Action? closed, params Control[] body) {
        var same = _customsPageKind == kind && _customsPageKey == key;
        if (!same) {
            ClosePageContent();
        }

        _customsPageKind = kind;
        _customsPageKey = key;
        _customsPageClosed = closed;
        _customsPageTile.Child = ManagerTheme.Tile(icon, ManagerTheme.Accent("Customs"), 46);
        _customsPageTitle.Text = title;
        _customsPageLine.Text = line;
        _customsPageLine.IsVisible = line.Length > 0;
        _customsPageBody.Children.Clear();
        foreach (var control in body) {
            Detach(control);
            _customsPageBody.Children.Add(control);
        }

        if (!same) {
            _customsPageScroll.Offset = default;
        }

        if (_customsList is not null) {
            _customsList.IsVisible = false;
        }

        _customsPage.IsVisible = true;
        SelectTab("Customs");
    }

    /// <summary>Whether the page of this custom is the one open.</summary>
    private bool IsCustomPage(string kind, string key) => _customsPage.IsVisible && _customsPageKind == kind && _customsPageKey == key;

    private bool IsCustomPage(string kind) => _customsPage.IsVisible && _customsPageKind == kind;

    /// <summary>Back to the list of every custom, with what it shows drawn again.</summary>
    private void BackToCustoms() {
        ClosePageContent();
        _customsPage.IsVisible = false;
        if (_customsList is not null) {
            _customsList.IsVisible = true;
        }

        RenderCustoms();
        RenderNpcs();
        RenderNpcChanges();
        RenderHubs();
        RenderAreas();
    }

    private void ClosePageContent() {
        var closed = _customsPageClosed;
        _customsPageKind = null;
        _customsPageKey = "";
        _customsPageClosed = null;
        _customsPageBody.Children.Clear();
        closed?.Invoke();
    }

    /// <summary>Takes a control out of the panel or border it is in, so it can go on a page.</summary>
    private static void Detach(Control control) {
        switch (control.Parent) {
            case Panel panel: panel.Children.Remove(control); break;
            case Decorator decorator: decorator.Child = null; break;
            case ContentControl content: content.Content = null; break;
            case ContentPresenter presenter: presenter.Content = null; break;
        }
    }

    /// <summary>Scrolls the open page to a part of it once it is drawn.</summary>
    private static void ShowOnPage(Control control) => Dispatcher.UIThread.Post(() => control.BringIntoView(), DispatcherPriority.Background);

}
