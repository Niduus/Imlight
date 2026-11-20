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
 *
 */

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The Live patches switch at the top of the window (Niduus, 2026-11-20: "make this a toggle right on the top of the EXE UI
/// so people can toggle it, if they don't, they get updates like the patches are now ... OFF by default"). On: the server is
/// built so fixes go into it while it runs, and it takes each published live patch without a restart. Off: updates work
/// exactly as before. The server says what it did in its live-patches\state.json (Game/LivePatches); its hover text shows it.
/// </summary>
internal sealed partial class ManagerWindow {

    private readonly ToggleSwitch _livePatches = new() { OnContent = "On", OffContent = "Off", VerticalAlignment = VerticalAlignment.Center };
    private Control? _livePatchesBox;
    private DispatcherTimer? _livePatchesTimer;
    private bool _showingLivePatches;

    private Control LivePatchesSwitch() {
        _livePatches.IsCheckedChanged += async (_, _) => {
            if (!_showingLivePatches) {
                await Guard(() => SetLivePatchesAsync(_livePatches.IsChecked == true));
            }
        };

        _livePatchesBox = new StackPanel {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Avalonia.Thickness(14, 0, 0, 0),
            Children = {
                new TextBlock { Text = "Live patches", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold },
                _livePatches,
            },
        };
        _livePatchesTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => RefreshLivePatches());
        _livePatchesTimer.Start();
        RefreshLivePatches();

        return _livePatchesBox;
    }

    private void RefreshLivePatches() {
        _showingLivePatches = true;
        try {
            _livePatches.IsChecked = _manager.LivePatches;
        }
        finally {
            _showingLivePatches = false;
        }

        if (_livePatchesBox is not null) {
            ToolTip.SetTip(_livePatchesBox, LivePatchesTip());
        }
    }

    private string LivePatchesTip() {
        var intro = "Live patches put server fixes into the running server, so nobody is disconnected and there is no " +
                    "\"SERVER RESTART\". Off (the default): updates work exactly as before.";
        if (!_manager.LivePatches) {
            return intro;
        }

        if (_manager.ServerBuildKind != "patchable") {
            return intro + "\n\nOn, but the server still has to be built for it once: turn the switch off and on again to build it now, " +
                   "or it happens with the next update.";
        }

        JsonNode? state = null;
        try {
            var path = Path.Combine(_manager.ServerFolder, "live-patches", "state.json");
            state = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception) {
            // Being written right now.
        }

        if (state is null || !InstalledCommands.IsServerRunning(_manager.InstallFolder)) {
            return intro + "\n\nOn. The server takes live patches while it runs.";
        }

        var whyNot = (string?) state["whyNot"] ?? "";
        var active = (string?) state["active"] ?? "";
        var waiting = (string?) state["waiting"] ?? "";
        var waitingWhy = (string?) state["waitingWhy"] ?? "";
        var checkedAt = (string?) state["lastCheck"] ?? "";
        var lines = new[] {
            whyNot.Length > 0 ? $"Live patches can't go in right now: {whyNot}." : "",
            active.Length > 0 ? $"In use: live patch {active[6..]}." : whyNot.Length == 0 ? "No live patch is in yet (this server has every fix up to its build)." : "",
            waiting.Length > 0 ? $"Live patch {waiting[6..]} waits for the next full update: {waitingWhy}." : "",
            checkedAt.Length > 0 ? $"Last looked at {checkedAt}." : "",
        }.Where(l => l.Length > 0);
        var notes = ((string?) state["patches"]?.AsArray().FirstOrDefault(p => (string?) p?["tag"] == active)?["notes"] ?? "")
            .ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("- ", StringComparison.Ordinal))
            .Select(l => "• " + l[2..]).Take(8).ToList();

        return intro + "\n\n" + string.Join("\n", lines) + (notes.Count > 0 ? "\n\nWhat it fixed:\n" + string.Join("\n", notes) : "");
    }

    private async Task SetLivePatchesAsync(bool on) {
        _manager.LivePatches = on;
        _manager.SaveSettings([new Setting("LivePatches", "Enabled", on ? "true" : "false", "")]);
        _settings = [];
        RefreshLivePatches();
        var built = _manager.ServerBuildKind;
        if (on && built != "patchable") {
            if (await ConfirmAsync("Live patches need the server built for them once. Build it now in the background? " +
                    "Keep playing; the server restarts one time when it is ready, the same way as an update. After that, fixes go in " +
                    "without restarts.")) {
                await PrepareSourceUpdateAsync();
            }
            else {
                Say("Live patches are on. The server is built for them with the next update; until then updates work as before.");
            }

            return;
        }

        if (on) {
            Say("Live patches are on. The server looks for a new live patch every 10 minutes and puts it in without a restart.");

            return;
        }

        if (built == "patchable" && await ConfirmAsync("Live patches are off: no new ones go in. Build the server the usual way " +
                "again now? It builds in the background and the server restarts one time when it is ready. If you say no, that " +
                "happens with the next update.")) {
            await PrepareSourceUpdateAsync();

            return;
        }

        Say("Live patches are off. Updates work as before.");
    }

}
