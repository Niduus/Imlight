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
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Imlight.Setup;

/// <summary>
/// The Customs tab's house gauntlet sigils (Niduus, 2026-10-24: "Can we make a feature so I can manually adjust the
/// sigil?"): the sigils set by hand in the game with .sigil, as the server keeps them in gauntlet-sigils.json, each with a
/// Reset that puts that gauntlet back to its defaults. The server reads the file again at once.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string GAUNTLET_SIGILS_FILE = "gauntlet-sigils.json";

    private readonly StackPanel _sigilRows = new() { Spacing = 6 };
    private readonly TextBlock _sigilNote = Sub("");

    private string GauntletSigilsPath => Path.Combine(_manager.ServerFolder, GAUNTLET_SIGILS_FILE);

    private Control GauntletSigilsCard() {
        RenderGauntletSigils();

        return Fold("home", "Customs", "House gauntlet sigils",
            "The sigils of house gauntlets you set by hand in the game. Each one is for that gauntlet everywhere, in every house.",
            WithHint(Sub("In your house, stand near a placed gauntlet and type .sigil here where the sigil's centre should be, " +
                    "or .sigil size, up, down, turn, forward, back, left, right, reset or info."),
                ".sigil here puts the sigil's centre where you stand, with its arc facing away from the gauntlet (.sigil here " +
                "face: the way you face). .sigil size 0.8 sets its size, .sigil up 10 and .sigil down 10 raise and lower it, " +
                ".sigil turn 90 turns its arc, .sigil info shows what it has now and .sigil reset puts it back. Changes show at " +
                "once. Only the house's owner (or quality assurance) can use it. Reset below does the same as .sigil reset."),
            new DockPanel {
                Children = {
                    WithDock(Action(Plain("Refresh"), () => {
                        RenderGauntletSigils();

                        return Task.CompletedTask;
                    }), Dock.Right),
                    WithCenter(_sigilNote),
                },
            },
            _sigilRows);
    }

    private JsonObject ReadGauntletSigils() {
        if (!File.Exists(GauntletSigilsPath)) {
            return [];
        }

        return JsonNode.Parse(File.ReadAllText(GauntletSigilsPath),
            documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject ?? [];
    }

    private void RenderGauntletSigils() {
        _sigilRows.Children.Clear();
        JsonObject json;
        try {
            json = ReadGauntletSigils();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            _sigilNote.Text = $"Could not read {GAUNTLET_SIGILS_FILE}: {ex.Message}";

            return;
        }

        var entries = json.Where(p => p.Value is JsonObject).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        _sigilNote.Text = entries.Count == 0
            ? "None set by hand yet: every gauntlet has its defaults."
            : $"{entries.Count} gauntlet(s) set by hand, in {GAUNTLET_SIGILS_FILE}.";
        foreach (var (id, node) in entries) {
            var entry = (JsonObject) node!;
            var name = SigilText(entry, "Name");
            var values = new List<string>();
            if (SigilNumber(entry, "OffsetX") is { } along && SigilNumber(entry, "OffsetY") is { } side) {
                values.Add($"centre {along:0} in front, {side:0} to the side");
            }

            foreach (var (key, label, format) in new[] { ("Height", "up", "0"), ("Turn", "turned", "0"), ("Scale", "size", "0.##") }) {
                if (SigilNumber(entry, key) is { } value) {
                    values.Add(key == "Turn" ? $"arc turned {value.ToString(format, CultureInfo.InvariantCulture)}°"
                        : $"{label} {value.ToString(format, CultureInfo.InvariantCulture)}");
                }
            }

            var updated = SigilText(entry, "Updated");
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = $"{(name.Length > 0 ? name : "Gauntlet")}  ·  template {id}", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            text.Children.Add(Sub(string.Join(", ", values) + (updated.Length > 0 ? $"  (set {updated})" : "")));
            var templateId = id;
            _sigilRows.Children.Add(ManagerTheme.Item(new DockPanel {
                Children = {
                    WithDock(Action(Plain("Reset"), () => ResetGauntletSigil(templateId, name)), Dock.Right),
                    WithCenter(text),
                },
            }));
        }
    }

    private Task ResetGauntletSigil(string templateId, string name) {
        var json = ReadGauntletSigils();
        if (json.Remove(templateId)) {
            Directory.CreateDirectory(_manager.ServerFolder);
            File.WriteAllText(GauntletSigilsPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        RenderGauntletSigils();
        Say($"{(name.Length > 0 ? name : $"Gauntlet {templateId}")} has its default sigil again." +
            (InstalledCommands.IsServerRunning(_manager.InstallFolder) ? " Players in a house see it move on their next step." : ""));

        return Task.CompletedTask;
    }

    private static string SigilText(JsonObject entry, string key)
        => entry.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    private static double? SigilNumber(JsonObject entry, string key) {
        var node = entry.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value as JsonValue;
        if (node is null) {
            return null;
        }

        if (node.TryGetValue<double>(out var number)) {
            return number;
        }

        return node.TryGetValue<string>(out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : null;
    }

}
