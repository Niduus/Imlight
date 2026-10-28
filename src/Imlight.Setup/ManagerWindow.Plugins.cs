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
/// The Plugins tab (Niduus, 2026-10-28: "Begin implementation of plugins as well that can modify Ahziael"): add-ons
/// from GitHub or a folder that change the server, each with its own on/off switch and settings. The server compiles
/// them itself and reloads them when anything here changes, so nothing needs a restart (Plugins.cs, the server's
/// PluginLoader).
/// </summary>
internal sealed partial class ManagerWindow {

    private readonly TextBox _pluginRepo = new() { Watermark = "GitHub link or owner/name, such as Niduus/Imlight-Roguelike", MinWidth = 380 };
    private readonly TextBox _pluginBranch = new() { Watermark = "Branch (empty: the main one)", Width = 200 };
    private readonly StackPanel _pluginRows = new() { Spacing = 8 };
    private readonly Dictionary<string, TextBlock> _pluginStates = [];
    private readonly DispatcherTimer _pluginTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private string _pluginsShown = "";

    private Control PluginsTab() {
        _pluginTimer.Tick += (_, _) => {
            if (ShownPage() == "Plugins") {
                RefreshPluginStates();
            }
            else {
                _pluginTimer.Stop();
            }
        };

        return Page(
            Fold("puzzle", "Plugins", "What plugins are",
                "Plugins are add-ons that change the server, made by you or by friends. They sit on top of Ahziael, so they " +
                "keep working when Ahziael updates, and nobody has to keep a copy of the whole server up to date.",
                Sub("Turning a plugin on or off, changing its settings, updating or removing it all work at once while the server " +
                    "runs, no restart needed. Plugins run inside your server, so only add ones from people you trust."),
                Row(Action(Plain("Open the Plugins folder"), OpenPluginsFolderAsync))),
            Fold("plus", "Plugins", "Add a plugin",
                "Paste the GitHub page of a plugin, or pick a plugin folder on this computer. A plugin you add starts turned on.",
                new WrapPanel { Children = { Spaced(_pluginRepo), Spaced(_pluginBranch) } },
                Row(Action(Primary("Add from GitHub"), AddPluginFromGitHubAsync), Action(Plain("Add from a folder…"), AddPluginFromFolderAsync))),
            Fold("list", "Plugins", "Your plugins",
                "Every plugin in your server. The line under each name says what the server made of it.",
                Row(Action(Plain("Refresh"), () => { ShowPlugins(force: true); return Task.CompletedTask; })),
                _pluginRows));
    }

    /// <summary>Builds the list again when the plugins changed (or always, with force), and keeps their states fresh.</summary>
    private void ShowPlugins(bool force = false) {
        _pluginTimer.Start();
        List<InstalledPlugin> plugins;
        try {
            plugins = Plugins.List(_manager.ServerFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Say($"The Plugins folder could not be read: {ex.Message}", isError: true);
            return;
        }

        // Rebuilt only when plugins came or went, so a setting being typed keeps its place.
        var shown = string.Join("|", plugins.Select(p => $"{p.Id}:{p.Version}:{p.On}"));
        if (!force && shown == _pluginsShown) {
            RefreshPluginStates(plugins);
            return;
        }

        _pluginsShown = shown;
        _pluginRows.Children.Clear();
        _pluginStates.Clear();
        if (plugins.Count == 0) {
            _pluginRows.Children.Add(Sub("No plugins yet. Add one above."));
            return;
        }

        foreach (var plugin in plugins) {
            _pluginRows.Children.Add(PluginRow(plugin));
        }

        RefreshPluginStates(plugins);
    }

    private Control PluginRow(InstalledPlugin plugin) {
        var on = new ToggleSwitch { OnContent = "On", OffContent = "Off", IsChecked = plugin.On };
        on.IsCheckedChanged += async (_, _) => await Guard(() => {
            Plugins.Switch(_manager.ServerFolder, plugin.Id, on.IsChecked == true);
            Say(on.IsChecked == true ? $"{plugin.Name} is turning on." : $"{plugin.Name} is turned off.");
            _pluginsShown = "";
            return Task.CompletedTask;
        });

        var title = string.Join("  ·  ", new[] { plugin.Name, plugin.Version.Length > 0 ? $"version {plugin.Version}" : "", plugin.Author.Length > 0 ? $"by {plugin.Author}" : "" }.Where(t => t.Length > 0));
        var state = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _pluginStates[plugin.Id] = state;

        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(new DockPanel {
            Children = {
                WithDock(WithCenter(on), Dock.Right),
                WithCenter(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = ManagerTheme.FONT_HEADING, TextWrapping = TextWrapping.Wrap }),
            },
        });
        if (plugin.Description.Length > 0) {
            body.Children.Add(Sub(plugin.Description));
        }

        body.Children.Add(state);
        if (!Plugins.NewEnough(plugin.MinAhziael)) {
            body.Children.Add(new TextBlock {
                Text = $"This plugin needs Ahziael {plugin.MinAhziael} or newer. Update Ahziael on the Tools tab.", Foreground = BAD, TextWrapping = TextWrapping.Wrap,
            });
        }

        if (plugin.Commands.Length > 0) {
            body.Children.Add(Sub("Chat commands: " + string.Join("   ", plugin.Commands.Select(c => c.Help.Length > 0 ? $"{c.Name} ({c.Help})" : c.Name))));
        }

        body.Children.Add(Sub(plugin.Source?.Describe() ?? "Comes with Ahziael."));
        if (plugin.Settings.Length > 0) {
            body.Children.Add(PluginSettings(plugin));
        }

        var buttons = Row(Action(Plain("Open its folder"), () => Open(plugin.Folder)));
        if (plugin.Source is not null) {
            buttons.Children.Add(Action(Plain("Update"), async () => {
                await Plugins.UpdateAsync(_manager.ServerFolder, plugin);
                Say($"{plugin.Name} is up to date.");
                ShowPlugins(force: true);
            }));
            buttons.Children.Add(Action(Danger(Plain("Remove")), async () => {
                if (!await ConfirmAsync($"Remove {plugin.Name}? Its files and settings are deleted.")) {
                    return;
                }

                Plugins.Remove(_manager.ServerFolder, plugin);
                Say($"{plugin.Name} was removed.");
                ShowPlugins(force: true);
            }));
        }

        body.Children.Add(buttons);

        return ManagerTheme.Item(body);
    }

    /// <summary>A plugin's settings, as its plugin.json lists them. Each one is saved the moment it changes.</summary>
    private Control PluginSettings(InstalledPlugin plugin) {
        var values = Plugins.ReadSettings(plugin);
        var rows = new StackPanel { Spacing = 6 };
        void Save(string key, string value) {
            try {
                Plugins.SaveSetting(plugin, key, value);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                Say($"Could not save {plugin.Name}'s setting: {ex.Message}", isError: true);
            }
        }

        foreach (var setting in plugin.Settings) {
            var value = values.GetValueOrDefault(setting.Key, setting.Default);
            Control input;
            switch (setting.Type) {
                case "switch" or "bool" or "toggle": {
                    var toggle = new ToggleSwitch { OnContent = "On", OffContent = "Off", IsChecked = value.Trim().ToLowerInvariant() is "true" or "on" or "yes" or "1" };
                    toggle.IsCheckedChanged += (_, _) => Save(setting.Key, toggle.IsChecked == true ? "true" : "false");
                    input = toggle;
                    break;
                }
                case "number" or "decimal": {
                    var whole = setting.Type == "number";
                    var box = new NumericUpDown {
                        Width = 150, Increment = whole ? 1 : 0.1m, FormatString = whole ? "0" : "0.###",
                        Minimum = (decimal) (setting.Min ?? -1_000_000_000), Maximum = (decimal) (setting.Max ?? 1_000_000_000),
                        Value = decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0,
                    };
                    box.ValueChanged += (_, _) => Save(setting.Key, (box.Value ?? 0).ToString(CultureInfo.InvariantCulture));
                    input = box;
                    break;
                }
                case "choice" when setting.Choices.Length > 0: {
                    var combo = new ComboBox { ItemsSource = setting.Choices, Width = 220, SelectedItem = setting.Choices.FirstOrDefault(c => c.Equals(value, StringComparison.OrdinalIgnoreCase)) };
                    combo.SelectionChanged += (_, _) => Save(setting.Key, combo.SelectedItem as string ?? "");
                    input = combo;
                    break;
                }
                default: {
                    var text = new TextBox { Text = value, Width = 320 };
                    text.LostFocus += (_, _) => Save(setting.Key, text.Text ?? "");
                    input = text;
                    break;
                }
            }

            rows.Children.Add(SwitchRow(setting.Label, input, setting.Help.Length > 0 ? setting.Help : null));
        }

        return new Expander { Header = "Settings", Content = rows, HorizontalAlignment = HorizontalAlignment.Stretch };
    }

    /// <summary>Each plugin's line from what the server last wrote about it.</summary>
    private void RefreshPluginStates(List<InstalledPlugin>? plugins = null) {
        try {
            plugins ??= Plugins.List(_manager.ServerFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return;
        }

        var running = InstalledCommands.IsServerRunning(_manager.InstallFolder);
        foreach (var plugin in plugins) {
            if (!_pluginStates.TryGetValue(plugin.Id, out var line)) {
                continue;
            }

            (line.Text, line.Foreground) = (plugin.On, plugin.State) switch {
                (false, _) => ("Off.", MUTED),
                (true, "problem") => ("Problem: " + plugin.StateMessage + (plugin.Errors.Length > 0 ? "\n" + string.Join("\n", plugin.Errors.Take(4)) : "")
                    + "\nAsk the plugin's maker for an update, or turn it off.", BAD),
                (true, "on") when running => ("On and working.", GOOD),
                (true, _) when !running => ("On. It starts with the server.", MUTED),
                _ => ("Turning on…", MUTED),
            };
        }
    }

    private async Task AddPluginFromGitHubAsync() {
        var repo = ServerSource.ParseRepository(_pluginRepo.Text ?? "")
            ?? throw new PanelException("Paste the plugin's GitHub page, or type it as owner/name.");
        var branch = (_pluginBranch.Text ?? "").Trim();
        Say($"Getting {repo} from GitHub…");
        var added = await Plugins.AddFromGitHubAsync(_manager.ServerFolder, repo, branch);
        TurnOn(added);
        _pluginRepo.Text = "";
    }

    private async Task AddPluginFromFolderAsync() {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {
            Title = "Pick the plugin's folder (the one with plugin.json in it)", AllowMultiple = false,
        });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is not { } path) {
            return;
        }

        TurnOn(Plugins.AddFromFolder(_manager.ServerFolder, path));
    }

    /// <summary>A plugin someone just added is meant to be used, so it is turned on (the ones that ship with Ahziael start off).</summary>
    private void TurnOn(List<(string Id, string Name)> added) {
        foreach (var (id, _) in added) {
            Plugins.Switch(_manager.ServerFolder, id, on: true);
        }

        ShowPlugins(force: true);
        Say($"Added {string.Join(", ", added.Select(a => a.Name))}. " + (InstalledCommands.IsServerRunning(_manager.InstallFolder)
            ? "It turns on in a few seconds." : "It turns on when the server starts."));
    }

    private Task OpenPluginsFolderAsync() {
        var folder = Plugins.Folder(_manager.ServerFolder);
        Directory.CreateDirectory(folder);

        return Open(folder);
    }

}
