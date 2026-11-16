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

        // A minimap button acts on the map it sits on: the player's (by game window) or Ultimus's own.
        var mapKey = button.Command switch {
            OverlayRenderer.MapButton.ULTIMUS => OverlayUltimusData.KEY,
            OverlayRenderer.MapButton.TEAM => OverlayUltimusData.TEAM_KEY,
            OverlayRenderer.MapButton.ANYONE => OverlayUltimusData.ANYONE_KEY,
            _ => game.ToInt64(),
        };
        long[] otherKeys = [.. new[] { game.ToInt64() }.Concat(OverlayUltimusData.Keys).Where(k => k != mapKey)];
        switch (button.Action) {
            case OverlayAction.DISMISS_NOTE:
                OverlayHost.ShowNotice(game, "");
                done(true);

                return;
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
            case OverlayAction.TEAM_NEXT: {
                // The Quest Team Spectator (Niduus, 2026-11-15): only the four team members, one after the other.
                if (!OverlayUltimusData.IsWanted(OverlayUltimusData.TEAM_KEY)) {
                    Report("Add the Quest Team Spectator's map or quest box to the overlay to watch the Quest Team.", false, done);

                    return;
                }

                Report($"Watching the Quest Team's {OverlayUltimusData.NextMember()} wizard.", true, done);

                return;
            }
            case OverlayAction.TEAM_WATCH: {
                // The Quest Team Spectator (Niduus, 2026-11-15: "spectate the desired"): straight to the member picked.
                if (!OverlayUltimusData.IsWanted(OverlayUltimusData.TEAM_KEY)) {
                    Report("Add the Quest Team Spectator's map or quest box to the overlay to watch the Quest Team.", false, done);

                    return;
                }

                if (OverlayUltimusData.WatchMember(button.Command) is not { } role) {
                    Report($"\"{button.Command}\" isn't a Quest Team member. Use Freya, Terra, Anubis or Chaac.", false, done);

                    return;
                }

                Report($"Watching the Quest Team's {OverlayUltimusData.MemberName(role)} ({role}).", true, done);

                return;
            }
            case OverlayAction.ANYONE_NEXT:
            case OverlayAction.ANYONE_WATCH: {
                // Spectate Anyone (Niduus, 2026-11-16): anyone in the game, one at a time; the host only, on the server's PC.
                if (!OverlayUltimusData.IsWanted(OverlayUltimusData.ANYONE_KEY)) {
                    Report("Add the Spectate Anyone map or quest box to the overlay to watch someone.", false, done);

                    return;
                }

                var name = button.Action == OverlayAction.ANYONE_WATCH ? OverlayUltimusData.WatchAnyone(button.Command)
                    : OverlayUltimusData.NextAnyone(button.Command.Trim().Equals("back", StringComparison.OrdinalIgnoreCase) ? -1 : 1);
                if (name is null) {
                    Report(button.Action == OverlayAction.ANYONE_WATCH ? $"Nobody in the game is called \"{button.Command}\"." : "Nobody is in the game to watch yet.", false, done);

                    return;
                }

                Report($"Watching {name}.", true, done);

                return;
            }
            case OverlayAction.SPECTATE_GOTO: {
                // Right next to the bot watched (Niduus, 2026-11-15), with the same command Ahziael's pages use. Spectate
                // Anyone's (Command "anyone"): the bot's own command, or for a player the place they are in.
                var team = !string.Equals(button.Command.Trim(), "ultimus", StringComparison.OrdinalIgnoreCase);
                var command = team ? $".questteam tp {OverlayUltimusData.Who(OverlayUltimusData.TEAM_KEY)}" : ".ultimus tp";
                if (string.Equals(button.Command.Trim(), "anyone", StringComparison.OrdinalIgnoreCase)) {
                    if (OverlayUltimusData.AnyoneWatched is not { } one) {
                        Report("Nobody is watched yet.", false, done);

                        return;
                    }

                    var place = OverlayUltimusData.For(game.ToInt64(), OverlayUltimusData.ANYONE_KEY)?.Place ?? "";
                    command = one.Kind switch {
                        "Ultimus" => ".ultimus tp",
                        "TCO7" => $".ultimus tp {one.Name}",
                        "Quest Team" => $".questteam tp {one.School}",
                        _ when place.Length > 0 => $".tp {place}",
                        _ => "",
                    };
                    if (command.Length == 0) {
                        Report($"Where {one.Name} is isn't known yet.", false, done);

                        return;
                    }
                }

                var go = new OverlayElement {
                    Kind = OverlayElement.BUTTON, Action = OverlayAction.COMMAND, Visible = true,
                    Command = command,
                };
                _ = RunOnServerAsync(go, game, done);

                return;
            }
            case OverlayAction.PAUSE_AI: {
                // Ultimus's AI paused for an hour, or back on (Niduus, 2026-11-15): the host's own server settings only.
                var (ok, said) = UltimusAiSwitches.TogglePause(InstalledCommands.ConfigFile(OverlayHost.InstallFolder));
                Report(said, ok, done);

                return;
            }
            case OverlayAction.MAP_FULL:
                // One full screen map at a time: the player's, or Ultimus's (a button on Ultimus's map acts on his).
                if (OverlayMinimapData.FullZoom(mapKey) is null) {
                    foreach (var other in otherKeys.Where(k => OverlayMinimapData.FullZoom(k) is not null)) {
                        OverlayMinimapData.ToggleFull(other, 0);
                    }
                }

                OverlayMinimapData.ToggleFull(mapKey, button.Map is { FullZoom: > 0 } kept ? kept.FullZoom : (button.Map?.Zoom ?? 2200) * 2.5);
                done(true);

                return;
            case OverlayAction.MAP_IN or OverlayAction.MAP_OUT when OverlayMinimapData.FullZoom(mapKey) is null:
                // The corner minimap: its own zoom, by the minimap's step and limits, kept on this PC.
                if (button.Map is { } small) {
                    var closer = button.Action == OverlayAction.MAP_IN;
                    small.Zoom = Math.Round(Math.Clamp(closer ? small.Zoom / small.ZoomStep : small.Zoom * small.ZoomStep, Math.Max(300, small.ZoomMin), Math.Min(12000, small.ZoomMax)));
                    OverlayHost.SaveQuietly();
                }

                done(true);

                return;
            case OverlayAction.MAP_IN or OverlayAction.MAP_OUT:
                OverlayMinimapData.ZoomFull(mapKey, button.Action == OverlayAction.MAP_IN, button.Map?.ZoomStep ?? 1.5,
                    button.Map?.ZoomMin ?? OverlayMinimapData.MIN_ZOOM, button.Map?.ZoomMax ?? OverlayMinimapData.MAX_ZOOM);
                // Kept in the design on this PC, so the full screen map opens this close next time.
                if (button.Map is { } map && OverlayMinimapData.FullZoom(mapKey) is { } zoom) {
                    map.FullZoom = Math.Round(zoom);
                    OverlayHost.SaveQuietly();
                }
                done(true);

                return;
            case OverlayAction.SPECTATE_TRACK: {
                // A spectator's Track line: its dungeon quest or the quest it plays, on its box and map (Niduus, 2026-11-15).
                var parts = button.Command.Split('|', 2);
                OverlayUltimusData.Track(parts[0] switch { "team" => OverlayUltimusData.TEAM_KEY, "anyone" => OverlayUltimusData.ANYONE_KEY, _ => OverlayUltimusData.KEY },
                    parts.Length > 1 ? parts[1] : "");
                OverlayMinimapData.Redraw();
                done(true);

                return;
            }
            case OverlayAction.QUEST_PICK:
                OverlayQuestData.TogglePick(game.ToInt64(), button.Command);
                done(true);

                return;
            case OverlayAction.QUEST_FOLD:
                OverlayQuestData.ToggleFold(game.ToInt64(), button.Command);
                done(true);

                return;
            case OverlayAction.QUEST_SCROLL:
                OverlayQuestData.ScrollBy(game.ToInt64(), button.Command == "up" ? -QuestTrackerView.SCROLL_STEP * 2 : QuestTrackerView.SCROLL_STEP * 2);
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
