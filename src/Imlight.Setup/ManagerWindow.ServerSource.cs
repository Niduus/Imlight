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
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Imlight.Setup;

/// <summary>
/// The Server source card on the Tools tab: run Niduus's server (the default), another GitHub copy of Imlight, or one
/// in a folder on this PC (Niduus, 2026-10-22: "make the manager work with any imlight copy"). Also the short notes a
/// page shows when the chosen copy doesn't have the feature it is for.
/// </summary>
internal sealed partial class ManagerWindow {

    private readonly RadioButton _sourceDefault = new() { GroupName = "source", Content = ServerSource.DefaultName + " (the usual one)" };
    private readonly RadioButton _sourceGitHub = new() { GroupName = "source", Content = "Another copy on GitHub" };
    private readonly RadioButton _sourceFolder = new() { GroupName = "source", Content = "A folder on this PC" };
    private readonly TextBox _sourceRepo = new() { Watermark = "Owner/name, such as Revive101/Imlight", MinWidth = 280 };
    private readonly TextBox _sourceBranch = new() { Watermark = "Branch, such as " + ServerSource.DEFAULT_BRANCH, Width = 220 };
    private readonly TextBox _sourcePath = new() { Watermark = "The Imlight folder, or a built server's folder" };
    private readonly TextBlock _sourcePathKind = Sub("");
    private readonly TextBlock _sourceNow = Sub("");
    private readonly WrapPanel _sourceGitHubRow = new() { Margin = new Thickness(28, 0, 0, 0) };
    private readonly StackPanel _sourceFolderRow = new() { Margin = new Thickness(28, 0, 0, 0), Spacing = 4 };
    private readonly Button _rebuildFromFolder = Plain("Rebuild from folder");

    // Each page's "doesn't have this feature" note, and what tells it to show.
    private readonly List<(Control Note, Func<bool> Missing)> _missingNotes = [];

    private Control ServerSourceCard() {
        _sourceGitHubRow.Children.AddRange([Spaced(_sourceRepo), Spaced(_sourceBranch)]);
        _sourceFolderRow.Children.AddRange([
            new DockPanel {
                Children = {
                    WithDock(WithMargin(Action(Plain("Choose folder…"), ChooseSourceFolderAsync), new Thickness(8, 0, 0, 0)), Dock.Right),
                    _sourcePath,
                },
            },
            _sourcePathKind,
        ]);
        ShowServerSource();
        foreach (var radio in new[] { _sourceDefault, _sourceGitHub, _sourceFolder }) {
            radio.IsCheckedChanged += (_, _) => ShowSourceRows();
        }

        _sourcePath.TextChanged += (_, _) => ShowFolderKind();
        Action(_rebuildFromFolder, RebuildFromFolderAsync);

        return Shut(Fold("server", "Server", "Server source",
            "Which copy of the Imlight server this app installs and runs.",
            _sourceNow,
            _sourceDefault,
            _sourceGitHub,
            _sourceGitHubRow,
            _sourceFolder,
            _sourceFolderRow,
            WithHint(Row(Action(Primary("Use this server"), SaveServerSourceAsync), _rebuildFromFolder),
                ServerSource.DefaultName + " is the one this app is made for. Another copy on GitHub is downloaded and built " +
                "instead, and Update Imlight checks its branch for anything new. A folder on this PC can hold Imlight's source, " +
                "which is built into the app's own Server folder (your folder is never changed), or a server that is already " +
                "built, which runs right where it is. Some pages of this app only work with " + ServerSource.DefaultName + ".")));
    }

    /// <summary>Puts the saved choice in the boxes.</summary>
    private void ShowServerSource() {
        var source = _manager.Source;
        _sourceDefault.IsChecked = source.Kind == ServerSourceKind.Default;
        _sourceGitHub.IsChecked = source.Kind == ServerSourceKind.GitHub;
        _sourceFolder.IsChecked = source.Kind == ServerSourceKind.Folder;
        _sourceRepo.Text = source.Kind == ServerSourceKind.GitHub ? source.Repository : "";
        _sourceBranch.Text = source.Kind == ServerSourceKind.GitHub ? source.Branch : "";
        _sourcePath.Text = source.Folder ?? "";
        _sourceNow.Text = $"Now: {source.Describe()}." + (source.BuiltFolder is not null ? " It runs from that folder." : "");
        _rebuildFromFolder.IsVisible = source.Kind == ServerSourceKind.Folder;
        ShowSourceRows();
    }

    private void ShowSourceRows() {
        _sourceGitHubRow.IsVisible = _sourceGitHub.IsChecked == true;
        _sourceFolderRow.IsVisible = _sourceFolder.IsChecked == true;
        ShowFolderKind();
    }

    private void ShowFolderKind() {
        var folder = (_sourcePath.Text ?? "").Trim().Trim('"');
        _sourcePathKind.Text = folder.Length == 0 ? "Click Choose folder… and pick the folder with Imlight in it."
            : !Directory.Exists(folder) ? "That folder doesn't exist."
            : ServerSource.DescribeFolder(folder) ?? "That folder doesn't look like Imlight. Pick the one with the src folder in it, " +
                "or a built server's folder (with Imlight.Director.exe and a Config folder).";
    }

    private async Task ChooseSourceFolderAsync() {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {
            Title = "Pick the Imlight folder", AllowMultiple = false,
        });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path) {
            _sourcePath.Text = path;
        }
    }

    /// <summary>The choice in the boxes, checked: a GitHub copy must have the branch, a folder must be Imlight.</summary>
    private async Task<ServerSource> ChosenSourceAsync() {
        if (_sourceGitHub.IsChecked == true) {
            var repo = ServerSource.ParseRepository(_sourceRepo.Text ?? "")
                ?? throw new PanelException("Type the GitHub copy as owner/name, such as Revive101/Imlight.");
            var branch = (_sourceBranch.Text ?? "").Trim();
            branch = branch.Length > 0 ? branch : ServerSource.DEFAULT_BRANCH;
            if (await ServerSource.LatestCommitAsync(repo, branch) is null) {
                throw new PanelException($"GitHub has no branch \"{branch}\" in {repo}, or can't be reached right now. Check the spelling and your internet connection.");
            }

            return new ServerSource { Kind = ServerSourceKind.GitHub, Repository = repo, Branch = branch };
        }

        if (_sourceFolder.IsChecked == true) {
            var folder = (_sourcePath.Text ?? "").Trim().Trim('"');
            if (folder.Length == 0 || !Directory.Exists(folder) || ServerSource.DescribeFolder(folder) is null) {
                throw new PanelException("Pick an Imlight folder first. " + _sourcePathKind.Text);
            }

            return new ServerSource { Kind = ServerSourceKind.Folder, Folder = Path.GetFullPath(folder) };
        }

        return new ServerSource();
    }

    private async Task SaveServerSourceAsync() {
        var source = await ChosenSourceAsync();
        var installFolder = _manager.InstallFolder;
        if (await Task.Run(() => InstalledCommands.IsServerRunning(installFolder))) {
            throw new PanelException("Stop the server first (Server tab), then change where it comes from.");
        }

        source.Save(installFolder);
        ShowServerSource();
        // The settings, lists and notes now come from the other server.
        _settings = [];
        _settingsList.Children.Clear();
        RefreshMissingNotes();
        if (source.BuiltFolder is { } built) {
            Say($"The app now runs the server in {built}, with that folder's own settings. Start it on the Server tab.");

            return;
        }

        if (await ConfirmAsync($"Install the server from {source.Describe()} now? The server stops, this app closes, and the " +
                "install opens in its own window. It can take a few minutes. Your settings, accounts and characters are kept.")) {
            _manager.RunInstaller("--update");
            Close();

            return;
        }

        Say($"Saved. The server comes from {source.Describe()} from the next update: click Update Imlight on the Tools tab.");
    }

    private async Task RebuildFromFolderAsync() {
        var source = _manager.Source;
        if (source.BuiltFolder is { } built) {
            Say($"The server in {built} is already built, and the app runs it from there. When you build it again yourself, " +
                "restart it on the Server tab.");

            return;
        }

        if (source.Checkout is null) {
            throw new PanelException($"{source.Folder} is gone or isn't an Imlight folder any more. Pick it again under Server source.");
        }

        await PrepareSourceUpdateAsync();
    }

    // ---------------------------------------------------------------------
    // Features another copy may not have
    // ---------------------------------------------------------------------

    /// <summary>
    /// A short note for a page whose feature the chosen server copy doesn't have (only Niduus's server has some), shown
    /// while <paramref name="missing"/> is true, so the page says so instead of failing.
    /// </summary>
    private Control MissingNote(string what, Func<bool> missing) {
        var note = new Border {
            Background = ManagerTheme.SURFACE_HIGH_BRUSH, BorderBrush = WARN, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(ManagerTheme.RADIUS_SMALL), Padding = new Thickness(14, 8),
            IsVisible = false,
            Child = new TextBlock {
                Text = $"This server copy doesn't have this feature: {what} Change the copy under Server source on the Tools tab.",
                Foreground = WARN, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        _missingNotes.Add((note, missing));

        return note;
    }

    private void RefreshMissingNotes() {
        foreach (var (note, missing) in _missingNotes) {
            note.IsVisible = missing();
        }
    }

}
