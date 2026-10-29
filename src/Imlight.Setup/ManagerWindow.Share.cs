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
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The Share tab (Niduus, 2026-10-29: "have every single custom able to be imported and exported for easy sharing with
/// safeguards to make sure those files do not touch anything else besides Ahziael"): tick any of your customs and save
/// them as one .ahziael file, or open someone's file, see what it adds and what it would replace, and import the ticked
/// ones. Every import is backed up first and can be undone. What it may write is fixed in CustomShare.
/// </summary>
internal sealed partial class ManagerWindow {

    private const int SHARE_ROWS_PER_KIND = 200;

    private List<ShareItem> _shareMine = [];
    private readonly HashSet<string> _shareTicked = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBox _shareSearch = new() { Watermark = "Search your customs by name, number or kind" };
    private readonly StackPanel _shareRows = new() { Spacing = 6 };
    private readonly TextBlock _shareCount = Sub("");
    private readonly DispatcherTimer _shareSearchDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private bool _shareSyncing;
    private ShareReading? _shareReading;
    private readonly StackPanel _shareImportRows = new() { Spacing = 6 };
    private readonly TextBlock _shareImportNote = Sub("Open a customs file (.ahziael) to see what is in it. Nothing changes until you press Import ticked.");
    private readonly Button _shareImportButton = Primary("Import ticked");
    private readonly Button _shareImportCancel = Plain("Cancel");
    private readonly TextBlock _shareUndoNote = Sub("");

    private static string ShareId(ShareItem item) => item.Kind.Id + "|" + item.Key;

    private Control ShareTab() {
        _shareSearchDelay.Tick += (_, _) => {
            _shareSearchDelay.Stop();
            RenderShareMine();
        };
        _shareSearch.TextChanged += (_, _) => {
            _shareSearchDelay.Stop();
            _shareSearchDelay.Start();
        };
        _shareImportButton.IsVisible = _shareImportCancel.IsVisible = false;
        _ = Action(_shareImportButton, ImportTickedAsync);
        _shareImportCancel.Click += (_, _) => {
            _shareReading = null;
            RenderShareImport();
        };

        return Page(
            MissingNote("sharing customs needs " + ServerSource.DefaultName + ", which keeps the customs in its own folder.",
                () => !_manager.HasExtraFolder),
            Fold("link", "Share", "Export customs",
                "Tick the customs to share and save them as one file. Send the file to a friend; they open it here with Import.",
                new DockPanel {
                    Children = {
                        WithDock(new StackPanel {
                            Orientation = Orientation.Horizontal, Spacing = 8,
                            Children = {
                                Action(Plain("Tick all"), () => {
                                    _shareTicked.UnionWith(ShownShareItems().Select(ShareId));
                                    RenderShareMine();

                                    return Task.CompletedTask;
                                }),
                                Action(Plain("Untick all"), () => {
                                    _shareTicked.Clear();
                                    RenderShareMine();

                                    return Task.CompletedTask;
                                }),
                                Action(Primary("Export ticked…"), ExportTickedAsync),
                            },
                        }, Dock.Right),
                        WithCenter(_shareCount),
                    },
                },
                _shareSearch,
                _shareRows),
            Fold("download", "Share", "Import a customs file",
                "Add customs from a .ahziael file. You see each one first: new, or replacing one of yours. Only Ahziael's own custom files are written.",
                new DockPanel {
                    Children = {
                        WithDock(new StackPanel {
                            Orientation = Orientation.Horizontal, Spacing = 8,
                            Children = { _shareImportCancel, _shareImportButton, Action(Plain("Open a customs file…"), OpenShareFileAsync) },
                        }, Dock.Right),
                        WithCenter(_shareImportNote),
                    },
                },
                _shareImportRows),
            Shut(Fold("lock", "Share", "Safety and undo",
                "What an import may touch, and how to take one back.",
                Sub("A customs file is plain text with your customs in it; nothing in it is ever run. Import writes only Ahziael's own " +
                    "custom files in the server's spiraldb-extra folder (customs, boss changes and cheats, areas, rooms, vendors, " +
                    "teleporters, recipes, drop tables, quest changes, player cheats) and its Crown Shop, events, codes and other " +
                    "custom lists. It never touches the game's files, the server program, the database or any other folder: a name " +
                    "with a folder or \"..\" in it, a link to somewhere else, or contents of the wrong shape are left out and listed."),
                Sub("Before anything is written, the files the import changes are saved in a backup zip, and replacing one of " +
                    "yours always asks first. A custom whose number you already use takes a free number instead, unless you pick " +
                    "Replace mine."),
                new DockPanel {
                    Children = {
                        WithDock(new StackPanel {
                            Orientation = Orientation.Horizontal, Spacing = 8,
                            Children = {
                                Action(Plain("Open backups folder"), () => {
                                    var folder = CustomShare.BackupsFolder(_manager);
                                    Directory.CreateDirectory(folder);

                                    return Open(folder);
                                }),
                                Action(Plain("Undo last import"), UndoLastImportAsync),
                            },
                        }, Dock.Right),
                        WithCenter(_shareUndoNote),
                    },
                })));
    }

    private async Task LoadShareAsync() {
        var manager = _manager;
        _shareMine = await Task.Run(() => CustomShare.Mine(manager));
        // Ticks of customs that are gone are dropped.
        _shareTicked.IntersectWith(_shareMine.Select(ShareId));
        RenderShareMine();
        RenderShareImport();
    }

    private IEnumerable<ShareItem> ShownShareItems() {
        var words = (_shareSearch.Text ?? "").Trim();

        return words.Length == 0
            ? _shareMine
            : _shareMine.Where(i => SearchText.Matches(i.Name + " " + i.Key + " " + i.Kind.Title, words));
    }

    private void RenderShareMine() {
        _shareRows.Children.Clear();
        _shareCount.Text = _shareMine.Count == 0
            ? "You have no customs yet. Make some on the Customs, Bosses, Player Cheats and other tabs, and they show here."
            : $"{_shareTicked.Count} of your {_shareMine.Count} customs ticked.";
        foreach (var group in ShownShareItems().GroupBy(i => i.Kind)) {
            var kind = group.Key;
            var items = group.ToList();
            var all = new CheckBox {
                IsChecked = items.All(i => _shareTicked.Contains(ShareId(i))),
                Content = new StackPanel {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = {
                        ManagerTheme.Icon(kind.Icon, 16, ManagerTheme.Accent("Share")),
                        new TextBlock { Text = $"{kind.Title} ({items.Count})", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            };
            all.IsCheckedChanged += (_, _) => {
                if (_shareSyncing) {
                    return;
                }

                if (all.IsChecked == true) {
                    _shareTicked.UnionWith(items.Select(ShareId));
                }
                else {
                    _shareTicked.ExceptWith(items.Select(ShareId));
                }

                RenderShareMine();
            };
            var list = new StackPanel { Spacing = 2, Margin = new Thickness(26, 2, 0, 0) };
            foreach (var item in items.Take(SHARE_ROWS_PER_KIND)) {
                var id = ShareId(item);
                var tick = new CheckBox { IsChecked = _shareTicked.Contains(id), Content = new TextBlock { Text = item.Name, TextWrapping = TextWrapping.Wrap } };
                tick.IsCheckedChanged += (_, _) => {
                    if (tick.IsChecked == true) {
                        _shareTicked.Add(id);
                    }
                    else {
                        _shareTicked.Remove(id);
                    }

                    // The kind's own tick follows, without ticking or unticking the rest.
                    _shareSyncing = true;
                    all.IsChecked = items.All(i => _shareTicked.Contains(ShareId(i)));
                    _shareSyncing = false;
                    _shareCount.Text = $"{_shareTicked.Count} of your {_shareMine.Count} customs ticked.";
                };
                list.Children.Add(tick);
            }

            if (items.Count > SHARE_ROWS_PER_KIND) {
                list.Children.Add(Sub($"…and {items.Count - SHARE_ROWS_PER_KIND} more. Search to find one, or tick the whole kind above."));
            }

            _shareRows.Children.Add(ManagerTheme.Item(new StackPanel { Spacing = 4, Children = { all, list } }));
        }
    }

    private async Task ExportTickedAsync() {
        var items = _shareMine.Where(i => _shareTicked.Contains(ShareId(i))).ToList();
        if (items.Count == 0) {
            throw new PanelException("Tick at least one custom to export.");
        }

        var suggested = items.Count == 1 ? Safe(items[0].Name) : "My customs";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
            Title = "Save the customs file", SuggestedFileName = suggested + CustomShare.EXTENSION, DefaultExtension = CustomShare.EXTENSION.TrimStart('.'),
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("Ahziael customs") { Patterns = ["*" + CustomShare.EXTENSION] }],
            SuggestedStartLocation = await StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Desktop),
        });
        if (file is null) {
            return;
        }

        var text = CustomShare.Pack(items);
        await using (var stream = await file.OpenWriteAsync()) {
            stream.SetLength(0);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(text);
        }

        Say($"Saved {items.Count} custom{(items.Count == 1 ? "" : "s")} as \"{file.Name}\". Send that file; your friend opens it on Ahziael's Share tab with Open a customs file.");

        // A file name made from a custom's name: letters, numbers, spaces and dashes.
        static string Safe(string name) {
            var cleaned = new string([.. name.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : ' ')]).Trim();

            return cleaned.Length == 0 ? "My customs" : cleaned.Length > 60 ? cleaned[..60].Trim() : cleaned;
        }
    }

    private async Task OpenShareFileAsync() {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title = "Open a customs file", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Ahziael customs") { Patterns = ["*" + CustomShare.EXTENSION, "*.json"] }, FilePickerFileTypes.All],
        });
        if (files.Count == 0) {
            return;
        }

        var path = files[0].TryGetLocalPath() ?? throw new PanelException("Ahziael can only open a customs file that is on this computer.");
        var manager = _manager;
        _shareReading = await Task.Run(() => CustomShare.Read(manager, path));
        RenderShareImport();
        if (_shareReading.Items.Count == 0) {
            Say("That file has no customs Ahziael can import." + (_shareReading.LeftOut.Count > 0 ? " The reasons are listed under Import." : ""), isError: true);
        }
    }

    private void RenderShareImport() {
        _shareImportRows.Children.Clear();
        var reading = _shareReading;
        _shareImportButton.IsVisible = _shareImportCancel.IsVisible = reading is { Items.Count: > 0 };
        if (reading is null) {
            _shareImportNote.Text = "Open a customs file (.ahziael) to see what is in it. Nothing changes until you press Import ticked.";
            var last = CustomShare.LastBackup(_manager);
            _shareUndoNote.Text = last is null ? "No import to undo." : $"Last import backup: {Path.GetFileNameWithoutExtension(last)}.";

            return;
        }

        var added = reading.Items.Count(i => !i.Exists);
        var replaces = reading.Items.Count(i => i.Exists && !i.Same);
        var same = reading.Items.Count(i => i.Same);
        _shareImportNote.Text = $"{reading.Items.Count} custom{(reading.Items.Count == 1 ? "" : "s")} in this file" +
                                (reading.MadeWith.Length > 0 ? $" (from {reading.MadeWith}{(reading.Made.Length > 0 ? ", " + reading.Made : "")})" : "") +
                                $": {added} new, {replaces} already here with changes, {same} just like yours (left unticked).";

        foreach (var group in reading.Items.GroupBy(i => i.Item.Kind)) {
            var list = new StackPanel { Spacing = 4 };
            list.Children.Add(new StackPanel {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = {
                    ManagerTheme.Icon(group.Key.Icon, 16, ManagerTheme.Accent("Share")),
                    new TextBlock { Text = group.Key.Title, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                },
            });
            foreach (var incoming in group) {
                list.Children.Add(ShareImportRow(incoming));
            }

            _shareImportRows.Children.Add(ManagerTheme.Item(list));
        }

        if (reading.LeftOut.Count > 0) {
            var why = new StackPanel { Spacing = 2 };
            why.Children.Add(new TextBlock { Text = $"Left out ({reading.LeftOut.Count}), never written:", FontWeight = FontWeight.SemiBold, Foreground = WARN });
            foreach (var line in reading.LeftOut.Take(50)) {
                why.Children.Add(Sub("• " + line));
            }

            if (reading.LeftOut.Count > 50) {
                why.Children.Add(Sub($"…and {reading.LeftOut.Count - 50} more."));
            }

            _shareImportRows.Children.Add(ManagerTheme.Item(why));
        }
    }

    private Control ShareImportRow(ShareIncoming incoming) {
        var tick = new CheckBox { IsChecked = incoming.Use, Content = new TextBlock { Text = incoming.Item.Name, TextWrapping = TextWrapping.Wrap } };
        var tag = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
        void Paint() {
            (tag.Text, tag.Foreground) = incoming switch {
                { Same: true } => ("Just like yours", MUTED),
                { Exists: false } => ("New", GOOD),
                { CanRenumber: true, AsNew: true } => ("Added with a free number", GOOD),
                _ => ("Replaces yours", WARN),
            };
        }

        Paint();
        tick.IsCheckedChanged += (_, _) => incoming.Use = tick.IsChecked == true;
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { tag } };
        if (incoming.CanRenumber) {
            var how = FilterCombo(["Add as a new one", "Replace mine"], 170);
            how.SelectedIndex = incoming.AsNew ? 0 : 1;
            how.SelectionChanged += (_, e) => {
                e.Handled = true;
                incoming.AsNew = how.SelectedIndex == 0;
                Paint();
            };
            right.Children.Insert(0, how);
        }

        return new DockPanel { Margin = new Thickness(26, 0, 0, 0), Children = { WithDock(right, Dock.Right), WithCenter(tick) } };
    }

    private async Task ImportTickedAsync() {
        if (_shareReading is not { } reading) {
            return;
        }

        var used = reading.Items.Where(i => i.Use).ToList();
        if (used.Count == 0) {
            throw new PanelException("Tick at least one custom to import.");
        }

        var replacing = used.Where(i => i.Exists && !(i.CanRenumber && i.AsNew)).ToList();
        if (replacing.Count > 0) {
            var names = string.Join(", ", replacing.Take(5).Select(i => i.Item.Name)) + (replacing.Count > 5 ? $" and {replacing.Count - 5} more" : "");
            if (!await ConfirmAsync($"This replaces {replacing.Count} of your customs ({names}). A backup is saved first, so Undo last import puts them back. Import?")) {
                return;
            }
        }

        var manager = _manager;
        var backup = await Task.Run(() => CustomShare.Import(manager, used));
        _shareReading = null;
        await LoadShareAsync();
        Say($"Imported {used.Count} custom{(used.Count == 1 ? "" : "s")}. The server picks them up by itself. " +
            $"Your old files are saved in {Path.GetFileName(backup)} (Undo last import puts them back).");
    }

    private async Task UndoLastImportAsync() {
        var last = CustomShare.LastBackup(_manager) ?? throw new PanelException("There is no import to undo.");
        if (!await ConfirmAsync($"Put your customs back as they were before the import of {Path.GetFileNameWithoutExtension(last)["Before import ".Length..]}? " +
                                "Customs that import added are taken out again.")) {
            return;
        }

        var manager = _manager;
        var count = await Task.Run(() => CustomShare.Undo(manager, last));
        await LoadShareAsync();
        Say($"Undone: {count} file{(count == 1 ? "" : "s")} put back as they were.");
    }

}
