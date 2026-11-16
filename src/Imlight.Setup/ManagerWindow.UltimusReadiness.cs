/*
 * Imlight
 * Copyright (C) 2026 Revive101
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
using System.Globalization;
using System.IO;
using System.Text;
using Avalonia.Controls;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// Spiral readiness (Niduus, 2026-11-16: "he scans every main-world quest ahead for missing data or likely stuck spots
/// before he reaches them"): the server's readiness.txt in his folder, shown as it is, with "Check now" to have it run
/// again. Ahziael only reads the file (and writes the request file).
/// </summary>
internal sealed partial class ManagerWindow {

    private TextBox? _ultReadinessBox;
    private DateTime _ultReadinessChanged = DateTime.MinValue;
    private string _ultReadinessText = "";

    private string UltimusReadinessPath => Path.Combine(UltimusSharedFolder, "readiness.txt");

    private Control UltimusReadinessFold() {
        _ultReadinessBox = new TextBox {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = MONO, FontSize = 12.5,
            Height = 300, Text = "Not checked yet. Ultimus checks a few minutes after he starts, once his quest books are built.",
        };

        return Fold("list", ULTIMUS_TAB, "Spiral readiness",
            "Every main story quest still ahead of Ultimus, in every main world, looked over before he gets there. MISSING DATA is " +
            "something the server can't read yet (a step with no area, a person the game's files place nowhere). LIKELY STUCK is a " +
            "spot where he will probably need a look (no door in, a quest check problem, a puzzle he hasn't solved, a quest he got " +
            "stuck on before). He still plays every quest as a player would. Checked when he starts and every 6 hours.",
            Row(Action(Plain("Check now"), async () => {
                    Directory.CreateDirectory(UltimusSharedFolder);
                    await File.WriteAllTextAsync(Path.Combine(UltimusSharedFolder, "readiness-request.txt"), DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                    Say(InstalledCommands.IsServerRunning(_manager.InstallFolder) && _ultEnabled.IsChecked == true
                        ? "Asked. The new check shows here in a few seconds."
                        : "Asked. It runs once the server is started and Ultimus is in the game.");
                }),
                Action(Plain("Copy"), () => CopyAsync(_ultReadinessText))),
            LogWell(_ultReadinessBox));
    }

    private void ShowUltimusReadiness() {
        if (_ultReadinessBox is null) {
            return;
        }

        var changed = File.Exists(UltimusReadinessPath) ? File.GetLastWriteTimeUtc(UltimusReadinessPath) : DateTime.MinValue;
        if (changed == DateTime.MinValue || changed == _ultReadinessChanged) {
            return;
        }

        try {
            using var stream = new FileStream(UltimusReadinessPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            _ultReadinessText = reader.ReadToEnd().ReplaceLineEndings("\n").TrimEnd();
            _ultReadinessBox.Text = _ultReadinessText;
            _ultReadinessChanged = changed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // Read again next tick.
        }
    }

}
