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
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Imlight.Setup;

/// <summary>
/// What happens when a button on the game overlay is clicked (Niduus, 2026-11-11: overlay buttons that run cheats and
/// commands on the host's server). Server actions go through <see cref="OverlayCommands"/>, which for now can't reach the
/// server (the password sign-in was taken out), so those buttons say so. A button can only do one of the
/// fixed <see cref="OverlayAction"/> kinds, so a shared design can't make it do anything else.
/// </summary>
internal static class OverlayButtons {

    /// <summary>The same button again this soon after is ignored (a double click runs it once).</summary>
    private static readonly TimeSpan s_again = TimeSpan.FromMilliseconds(600);

    private static readonly Dictionary<OverlayElement, DateTime> s_last = [];

    /// <summary>Opens Ahziael's window (Ahziael answers; the overlay on its own starts Ahziael).</summary>
    internal static event Action? AhziaelRequested;

    /// <summary>What the last button did, in plain words, for Ahziael's overlay page.</summary>
    internal static string LastResult { get; private set; } = "";

    /// <summary>
    /// Runs the button. <paramref name="done"/> gets true when it worked (a green flash on the button) or false (red).
    /// Called on the UI thread.
    /// </summary>
    internal static void Click(OverlayElement button, bool server, IntPtr game, Action<bool> done) {
        var now = DateTime.UtcNow;
        if (s_last.TryGetValue(button, out var last) && now - last < s_again) {
            return;
        }

        s_last[button] = now;
        LastResult = "";
        if (s_last.Count > 200) {
            s_last.Clear();
        }

        switch (button.Action) {
            case OverlayAction.DESIGNER:
                OverlayHost.RequestDesigner(game);
                done(true);

                return;
            case OverlayAction.TOGGLE_MINE: {
                // A server that needs the overlay keeps the player's pieces on.
                if (OverlayHost.ServerRequires) {
                    Report("This server needs the overlay, so your own texts and boxes stay on.", false, done);

                    return;
                }

                var design = OverlayHost.Design.Copy();
                design.Enabled = !design.Enabled;
                OverlayHost.Apply(design);
                Report(design.Enabled ? "Your texts and boxes are shown." : "Your texts and boxes are hidden.", true, done);

                return;
            }
            case OverlayAction.OPEN_AHZIAEL:
                OpenAhziael();
                done(true);

                return;
            case OverlayAction.MAP_FULL:
                OverlayMinimapData.ToggleFull(game.ToInt64(), (button.Map?.Zoom ?? 2200) * 2.5);
                done(true);

                return;
            case OverlayAction.MAP_IN or OverlayAction.MAP_OUT:
                OverlayMinimapData.ZoomFull(game.ToInt64(), button.Action == OverlayAction.MAP_IN);
                done(true);

                return;
            case OverlayAction.QUEST_PICK:
                OverlayQuestData.TogglePick(game.ToInt64(), button.Command);
                done(true);

                return;
            case OverlayAction.QUEST_FOLD:
                OverlayQuestData.ToggleFold(game.ToInt64(), button.Command);
                done(true);

                return;
            case OverlayAction.COMMAND or OverlayAction.CHEAT or OverlayAction.MOB_CHEAT:
                _ = RunOnServerAsync(button, game, done);

                return;
        }
    }

    private static void Report(string text, bool ok, Action<bool> done) {
        LastResult = text;
        done(ok);
    }

    private static async Task RunOnServerAsync(OverlayElement button, IntPtr game, Action<bool> done) {
        var value = button.Command.Trim();
        if (value.Length == 0) {
            Report(button.Action == OverlayAction.COMMAND ? "This button has no command yet." : "This button has no cheat picked yet.", false, done);

            return;
        }

        try {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            OverlayCommands.Result result;
            if (button.Action == OverlayAction.COMMAND) {
                result = await OverlayCommands.SendAsync("command", value, game, stop.Token);
            }
            else if (button.Action == OverlayAction.MOB_CHEAT) {
                // A creature's "when pressed" cheat in this wizard's battle, by its name.
                result = await OverlayCommands.SendAsync("mobcheat", value, game, stop.Token);
            }
            else {
                var how = button.CheatHow switch {
                    OverlayAction.ON => "on",
                    OverlayAction.OFF => "off",
                    OverlayAction.SWITCH => await IsOnAsync(value, game, stop.Token) ? "off" : "on",
                    _ => "use",
                };
                result = await OverlayCommands.SendAsync("cheat", $"{how} {value}", game, stop.Token);
            }

            // Not linked to a wizard yet: the code shows on that game window, for ".link code" in its chat.
            if (result.Code.Length > 0) {
                OverlayHost.ShowNotice(game, $"Type .link {result.Code} in this game's chat once, then click again.");
            }
            else if (result.Ok) {
                OverlayHost.ShowNotice(game, "");
            }

            Report(result.Text, result.Ok, done);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.IO.IOException) {
            Report("The server didn't answer. Check that the game was started from Play in Ahziael.", false, done);
        }
    }

    // Whether the player's cheat of this name (or number) is on now, for a Switch button.
    private static async Task<bool> IsOnAsync(string cheat, IntPtr game, CancellationToken stop) {
        var list = await OverlayCommands.SendAsync("cheats", "", game, stop);
        var match = list.Cheats?.FirstOrDefault(c => string.Equals(c.Name, cheat, StringComparison.OrdinalIgnoreCase)
                                                     || c.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) == cheat);

        return match?.On == true;
    }

    private static void OpenAhziael() {
        if (!OverlayHost.Standalone) {
            AhziaelRequested?.Invoke();

            return;
        }

        // On its own: start Ahziael (it shows its window; the overlay hands over drawing by itself).
        try {
            Process.Start(new ProcessStartInfo(OverlayStandalone.Exe(OverlayHost.InstallFolder)) {
                ArgumentList = { "--panel", "--dir", OverlayHost.InstallFolder }, UseShellExecute = false, WorkingDirectory = OverlayHost.InstallFolder,
            })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) {
            LastResult = "Ahziael could not be started: " + ex.Message;
        }
    }

}
