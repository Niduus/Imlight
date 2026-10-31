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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Imlight.RemoteAdmin;

namespace Imlight.Setup;

/// <summary>
/// The Remote tab on the Server page (Niduus, 2026-10-30: "people are able to use Ahziael while on non-locally hosted
/// servers, so long as they have admin perms"). It connects Ahziael to an Imlight server on another PC with an
/// Administrator account there (RemoteSession), and lets the host of this PC's server turn Remote Admin on. While
/// connected the whole window works on that server, with a banner saying so and a button back to this PC.
/// </summary>
internal sealed partial class ManagerWindow {

    private readonly TextBox _remoteName = new() { Watermark = "A name for it (you can leave this empty)" };
    private readonly TextBox _remoteHost = new() { Watermark = "The host's address, such as 203.0.113.5" };
    private readonly TextBox _remotePort = new() { Text = RemoteAdminWire.DEFAULT_PORT.ToString(), Width = 110, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _remoteUser = new() { Watermark = "Your Administrator username on that server" };
    private readonly TextBox _remotePassword = new() { PasswordChar = '•', Watermark = "Its password" };
    private readonly StackPanel _remoteSaved = new() { Spacing = 6 };
    private readonly TextBlock _remoteProgress = Sub("");
    private readonly ToggleSwitch _remoteHostSwitch = new() { OnContent = "On", OffContent = "Off" };
    private readonly TextBlock _remoteHostInfo = Sub("");
    private bool _showingRemoteHost;

    private static bool IsRemote => RemoteSession.Current is not null;

    private Control RemoteTab() {
        if (RemoteSession.Current is { } remote) {
            return Page(
                Fold("link", "Server", $"Connected to {remote.Label}",
                    $"You are managing {remote.Label} as {remote.Account}. Every page works on that server now, and what you change goes there.",
                    Sub("Settings, Customs, Bosses, cheats, drop tables, shops, announcements, Who's online, Logs, Accounts and Quests all work on " +
                        "that server. Commands run on your own wizard there, so log in to the game on that server first (Play Wizard101 does it)."),
                    Sub("Local only, so they only work on the host's PC: starting, stopping, restarting and updating the server, Play with friends, " +
                        "Privacy clean up, and the ports, paths, database and Remote Admin settings."),
                    Row(Action(Primary("Back to my PC"), BackToMyPcAsync))),
                RemoteHelpCard());
        }

        Action(_remoteConnect, ConnectNewRemoteAsync);
        ShowSavedRemotes();

        return Page(
            Fold("link", "Server", "Manage a server on another PC",
                "Use Ahziael on a friend's Imlight server, with an account that has the Administrator rank there.",
                Field("Address", "The host's internet address, or their PC's address if you share their Wi-Fi.", _remoteHost),
                Field("Port", "12410 unless the host changed it.", _remotePort),
                Field("Username", "", _remoteUser),
                Field("Password", "Sent only to that server, encrypted. Ahziael never saves it.", _remotePassword),
                Field("Name", "", _remoteName),
                Row(_remoteConnect),
                _remoteProgress,
                _remoteSaved),
            RemoteHostCard(),
            RemoteHelpCard());
    }

    private readonly Button _remoteConnect = Primary("Connect");

    private void ShowSavedRemotes() {
        _remoteSaved.Children.Clear();
        var saved = RemoteServers.Load(_manager.InstallFolder);
        if (saved.Count == 0) {
            return;
        }

        _remoteSaved.Children.Add(Heading("Servers you connected to before"));
        foreach (var server in saved) {
            var connect = Action(Primary("Connect"), async () => {
                var password = await AskAsync($"Password for {server.Username} on {server.Label}:", secret: true);
                if (!string.IsNullOrEmpty(password)) {
                    await ConnectRemoteAsync(server, password);
                }
            });
            var remove = Action(Plain("Remove"), () => {
                RemoteServers.Remove(_manager.InstallFolder, server);
                ShowSavedRemotes();

                return Task.CompletedTask;
            });
            _remoteSaved.Children.Add(SwitchRow(server.Label, Row(connect, remove), $"{server.Host} port {server.Port}, as {server.Username}"));
        }
    }

    private async Task ConnectNewRemoteAsync() {
        var host = (_remoteHost.Text ?? "").Trim();
        var user = (_remoteUser.Text ?? "").Trim();
        var password = _remotePassword.Text ?? "";
        if (host.Length == 0 || user.Length == 0 || password.Length == 0) {
            throw new PanelException("Fill in the address, your username and your password first.");
        }

        if (!int.TryParse(_remotePort.Text, out var port) || port is < 1 or > 65535) {
            throw new PanelException("The port is a number, 12410 unless the host changed it.");
        }

        var server = new RemoteServer((_remoteName.Text ?? "").Trim(), host, port, user, null);
        // Known already: keep the fingerprint it had, so a changed one is noticed.
        if (RemoteServers.Load(_manager.InstallFolder).FirstOrDefault(s => RemoteServers.SameServer(s, server)) is { } known) {
            server = server with { Fingerprint = known.Fingerprint, Name = server.Name.Length > 0 ? server.Name : known.Name };
        }

        await ConnectRemoteAsync(server, password);
    }

    private async Task ConnectRemoteAsync(RemoteServer server, string password) {
        var progress = new Progress<string>(text => _remoteProgress.Text = text);
        var installFolder = _manager.InstallFolder;
        // Away from the window: the first visit brings over thousands of small files.
        var session = await Task.Run(() => RemoteSession.ConnectAsync(installFolder, server, password,
            fingerprint => Dispatcher.UIThread.InvokeAsync(() => ConfirmAsync(
                $"This is the first time you connect to {server.Label}. Its fingerprint is:\n\n{fingerprint}\n\n" +
                "Ask the host to read theirs out from Server > Remote in their Ahziael. Click OK only if they are the same.")),
            progress));
        _remotePassword.Text = "";
        session.Activate();
        ReopenWindow();
    }

    private Task BackToMyPcAsync() {
        RemoteSession.Deactivate();
        ReopenWindow();

        return Task.CompletedTask;
    }

    /// <summary>A fresh window for the server now in use, in the same place, so every page reads that server's files.</summary>
    private void ReopenWindow() {
        var next = new ManagerWindow(new ServerManager(_manager.InstallFolder)) {
            WindowStartupLocation = WindowStartupLocation.Manual, Position = Position, Width = Width, Height = Height,
        };
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            desktop.MainWindow = next;
        }

        next.Opened += (_, _) => next.SelectTab(IsRemote ? HOME : "Remote");
        next.Show();
        next.WindowState = WindowState;
        Close();
    }

    /// <summary>The banner under the header while the window works on another PC's server.</summary>
    private Control? RemoteBanner() {
        if (RemoteSession.Current is not { } remote) {
            return null;
        }

        var bar = ManagerTheme.GlassPanel(new DockPanel {
            Children = {
                WithDock(Action(Plain("Back to my PC"), BackToMyPcAsync), Dock.Right),
                WithDock(WithMargin(ManagerTheme.Tile("link", ManagerTheme.Accent("Server"), 26), new Thickness(0, 0, 10, 0)), Dock.Left),
                WithCenter(new TextBlock {
                    Text = $"Managing {remote.Label} as {remote.Account}. Changes go to that server. Start, stop, updates and Play with friends are local only.",
                    TextWrapping = TextWrapping.Wrap,
                }),
            },
        }, 14);
        bar.Padding = new Thickness(12, 6);
        bar.Margin = new Thickness(12, 8, 12, 0);

        return bar;
    }

    /// <summary>For the host: turn Remote Admin on for the server on this PC, and what to tell the admins.</summary>
    private Control RemoteHostCard() {
        _remoteHostSwitch.IsCheckedChanged += async (_, _) => {
            if (_showingRemoteHost) {
                return;
            }

            await Guard(() => {
                var on = _remoteHostSwitch.IsChecked == true;
                var lines = File.ReadAllLines(_manager.ConfigFile).ToList();
                IniFile.Set(lines, "Remote Admin", "Enabled", on ? "true" : "false");
                if (IniFile.Get(_manager.ConfigFile, "Remote Admin", "Port") is null) {
                    IniFile.Set(lines, "Remote Admin", "Port", RemoteAdminWire.DEFAULT_PORT.ToString());
                }

                File.WriteAllLines(_manager.ConfigFile, lines);
                ShowRemoteHost();
                Say(on
                    ? "Remote Admin is on. " + (FriendAccess.WasTurnedOn(_manager.InstallFolder)
                        ? "Click \"Let friends connect\" on the Play tab once more so your router lets its port through too."
                        : "Admins on your Wi-Fi can connect now; for admins on the internet, click \"Let friends connect\" on the Play tab.")
                    : "Remote Admin is off. Nobody can connect from another PC, and its port is closed.");

                return Task.CompletedTask;
            });
        };
        ShowRemoteHost();

        return Fold("server", "Settings", "Let admins manage this server from their PC",
            "Remote Admin: off until you turn it on. Only accounts with the Administrator rank can use it.",
            SwitchRow("Remote Admin", _remoteHostSwitch, "Applies right away, no restart. While it and \"Let players download their account\" (Account Transfer tab) are both off, no port is opened."),
            _remoteHostInfo,
            Sub("The admin account every server starts with (admin, password compsci) is not let in until it has a new password: " +
                "change it on the Accounts tab."),
            Sub("Give an admin the Administrator rank on the Accounts tab first. They need your address (the Play tab shows it under " +
                "Play with friends), the port, their username and password, and the fingerprint above to check the first time."));
    }

    private void ShowRemoteHost() {
        _showingRemoteHost = true;
        var on = _manager.GetSwitch("Remote Admin", "Enabled");
        _remoteHostSwitch.IsChecked = on;
        _showingRemoteHost = false;
        var port = _manager.GetSetting("Remote Admin", "Port") ?? RemoteAdminWire.DEFAULT_PORT.ToString();
        var fingerprintFile = Path.Combine(_manager.ConfigFolder, "remote-admin", "fingerprint.txt");
        var fingerprint = File.Exists(fingerprintFile) ? RemoteAdminWire.ShortFingerprint(File.ReadAllText(fingerprintFile).Trim()) : null;
        _remoteHostInfo.Text = !on
            ? "Off."
            : $"On, port {port}. " + (fingerprint is null
                ? "Your server shows its fingerprint here once it is running with Remote Admin on."
                : $"Fingerprint: {fingerprint}");
    }

    private Control RemoteHelpCard() => Shut(Fold("info", "Guides", "How it keeps you safe",
        "What Remote Admin sends, and what it lets a remote admin do.",
        Sub("The connection is encrypted. The first time, you check the server's fingerprint with the host; after that Ahziael refuses a server " +
            "whose fingerprint changed, so your password can't be caught by someone pretending to be it."),
        Sub("Only Administrator accounts get in. 5 wrong passwords from one address lock it out for 15 minutes, and every login is written in " +
            "the server's log. A rank taken away ends the connection within a minute."),
        Sub("A remote admin changes the server's data files and settings only: never its programs, its database files, its ports, its paths " +
            "or Remote Admin itself. Commands run only on that admin's own wizards. Nothing is sent anywhere but between Ahziael and that server."),
        Sub("Ahziael keeps a copy of the server's files in its own folder (Remote servers) while you work, so pages open fast. Your own server is not touched.")));

}
