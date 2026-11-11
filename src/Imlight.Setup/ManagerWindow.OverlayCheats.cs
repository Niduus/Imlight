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
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Imlight.RemoteAdmin;

namespace Imlight.Setup;

/// <summary>
/// Play &gt; Overlay &gt; My cheats on this server (Niduus, 2026-11-11): a player on someone else's server signs in once
/// and then sees their player cheats there, switches them on or off, uses the ones made as buttons ("When pressed"),
/// and runs any chat command they may use, all through OverlayCommands instead of game chat. The host's switch for it
/// ([Overlay] PlayerCommands) is on the same page.
/// </summary>
internal sealed partial class ManagerWindow {

    private readonly TextBlock _ocServer = Sub("");
    private readonly TextBox _ocUser = new() { Watermark = "Your game username on that server" };
    private readonly TextBox _ocPassword = new() { PasswordChar = '•', Watermark = "Your game password on that server" };
    private readonly StackPanel _ocSignInBox = new() { Spacing = 10 };
    private readonly StackPanel _ocSignedInBox = new() { Spacing = 10 };
    private readonly StackPanel _ocList = new() { Spacing = 6 };
    private readonly TextBox _ocCommand = new() { Watermark = "A chat command, such as .cheats or .tp Ravenwood" };
    private readonly TextBlock _ocAnswer = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ToggleSwitch _ocHostSwitch = new() { OnContent = "On", OffContent = "Off" };
    private (string Host, int Port) _ocShown;

    private Control OverlayCheatsCard() {
        _ocCommand.KeyDown += (_, e) => {
            if (e.Key == Avalonia.Input.Key.Enter) {
                _ = Guard(RunOverlayCommandAsync);
            }
        };
        _ocSignInBox.Children.Add(Field("Username", "", _ocUser));
        _ocSignInBox.Children.Add(Field("Password", "Sent only to that server, encrypted. Ahziael never saves it: it keeps a key the server gives instead.", _ocPassword));
        _ocSignInBox.Children.Add(Row(Action(Primary("Sign in"), SignInOverlayCommandsAsync)));
        _ocSignedInBox.Children.Add(Row(Action(Primary("Show my cheats"), () => SendOverlayCommandAsync("cheats", "")),
            Action(Plain("Sign out"), () => {
                OverlayCommands.SignOut();
                ShowOverlayCheatsState();
                Say("Signed out. Sign in again to use your cheats on this server.");

                return Task.CompletedTask;
            })));
        _ocSignedInBox.Children.Add(_ocList);
        _ocSignedInBox.Children.Add(Field("Run a command", "Runs as your wizard there, exactly as if you typed it in the game's chat, with what your account may use. Press Enter to send.",
            _ocCommand));
        _ocSignedInBox.Children.Add(Row(Action(Plain("Run"), RunOverlayCommandAsync)));
        _ocSignedInBox.Children.Add(_ocAnswer);

        // Each second (with the page's timer): the server the overlay links to may have changed (Play started the game elsewhere).
        _ovTimer.Tick += (_, _) => {
            if (OverlayCommands.Server != _ocShown) {
                ShowOverlayCheatsState();
            }
        };
        ShowOverlayCheatsState();

        return Fold("bolt", "Play", "My cheats on this server",
            "Use your cheats and commands on the server you play on, from here instead of the game's chat.",
            _ocServer,
            _ocSignInBox,
            _ocSignedInBox,
            Sub("How it stays safe: you sign in once with your game username and password, and the server gives this PC a secret key. " +
                "Every command is signed with that key and numbered, so nobody can fake one for you or send one twice. It only works while " +
                "your wizard is in the game from this PC, and the host's rules decide what you may use, the same as in chat. " +
                "Changing your game password signs you out everywhere."));
    }

    private void ShowOverlayCheatsState() {
        var server = OverlayCommands.Server;
        _ocShown = server;
        var signedIn = OverlayCommands.SignedIn;
        _ocServer.Text = signedIn
            ? $"Signed in to {server.Host} as {OverlayCommands.SignedInAs}."
            : $"The server: {server.Host} (the one you last started the game on with Play). Sign in with your game account there.";
        _ocSignInBox.IsVisible = !signedIn;
        _ocSignedInBox.IsVisible = signedIn;
        if (!signedIn) {
            _ocList.Children.Clear();
            _ocAnswer.Text = "";
        }
    }

    private async Task SignInOverlayCommandsAsync() {
        var user = (_ocUser.Text ?? "").Trim();
        var password = _ocPassword.Text ?? "";
        if (user.Length == 0 || password.Length == 0) {
            throw new PanelException("Type your game username and password for that server first.");
        }

        var (host, port) = OverlayCommands.Server;
        Func<string, Task<bool>> trust = fingerprint => Dispatcher.UIThread.InvokeAsync(() => ConfirmAsync(
            $"This is the first time you sign in to {host}. Its fingerprint is:\n\n{fingerprint}\n\n" +
            "Ask the host to read theirs out from Server > Account Transfer in their Ahziael. Click OK only if they are the same."));
        var account = await Task.Run(() => OverlayCommands.SignInAsync(host, port, user, password, trust));
        _ocPassword.Text = "";
        ShowOverlayCheatsState();
        Say($"Signed in to {host} as {account}. Your password was not saved.");
        await SendOverlayCommandAsync("cheats", "");
    }

    private Task RunOverlayCommandAsync() {
        var text = (_ocCommand.Text ?? "").Trim();
        if (text.Length == 0) {
            throw new PanelException("Type a command first, such as .cheats.");
        }

        return SendOverlayCommandAsync("command", text);
    }

    private async Task SendOverlayCommandAsync(string kind, string value) {
        var result = await Task.Run(() => OverlayCommands.SendAsync(kind, value));
        if (!result.Ok) {
            ShowOverlayCheatsState();
            _ocAnswer.Text = result.Text;
            throw new PanelException(result.Text);
        }

        if (kind == "command") {
            _ocCommand.Text = "";
        }

        _ocAnswer.Text = result.Text.Length > 0 ? result.Text : kind == "cheats" ? "" : "Done.";
        if (kind is "cheats" or "cheat") {
            ShowCheatList(result.Cheats);
        }
    }

    private void ShowCheatList(IReadOnlyList<OverlayCommands.CheatInfo> cheats) {
        _ocList.Children.Clear();
        if (cheats.Count == 0) {
            _ocList.Children.Add(Sub("You have no cheats on this server. The host gives them on their Create > Player Cheats page."));

            return;
        }

        foreach (var cheat in cheats) {
            var name = cheat.Name;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            if (cheat.CanUse) {
                var use = Action(Primary("Use"), () => SendOverlayCommandAsync("cheat", $"use {name}"));
                use.IsEnabled = cheat.On;
                buttons.Children.Add(use);
            }

            if (cheat.CanSwitch) {
                buttons.Children.Add(Action(Plain(cheat.On ? "Turn off" : "Turn on"), () => SendOverlayCommandAsync("cheat", $"{(cheat.On ? "off" : "on")} {name}")));
            }

            var state = cheat.On ? "On" : "Off";
            var how = cheat.CanUse ? "a button: click Use in a battle" : "goes off by itself";
            var line = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(buttons, Dock.Right);
            line.Children.Add(buttons);
            line.Children.Add(new StackPanel {
                Spacing = 2,
                Children = {
                    new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                    Sub($"{state}, {how}{(cheat.CanSwitch ? "" : ", only the host can turn it off")}."),
                },
            });
            _ocList.Children.Add(new Border {
                Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)), Child = line,
            });
        }
    }

    // ---------------------------------------------------------------------
    // The host's switch
    // ---------------------------------------------------------------------

    private Control OverlayCheatsHostSwitch() {
        _ocHostSwitch.IsEnabled = false;
        _ocHostSwitch.IsCheckedChanged += async (_, _) => {
            if (!_updatingRandomizer) {
                await Guard(() => SetSwitchAsync(OVERLAY_SECTION, "PlayerCommands", "Let players use their cheats from Ahziael", _ocHostSwitch.IsChecked == true));
            }
        };

        return SwitchRow("Let players use their cheats from Ahziael", _ocHostSwitch,
            "On (the start): players on your server can sign in from their own Ahziael (Play > Overlay > My cheats on this server) and " +
            "switch or use their player cheats and run chat commands from there or from buttons on their overlay, instead of typing in chat. " +
            "They can only do what they could type in chat anyway. Off: only chat works. Applies right away.");
    }

    private void ShowOverlayCheatsHostSwitch(Dictionary<(string Section, string Key), string> ini) {
        var has = ini.TryGetValue((OVERLAY_SECTION, "PlayerCommands"), out var value);
        _ocHostSwitch.IsEnabled = has;
        _ocHostSwitch.IsChecked = !has || !value!.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

}
