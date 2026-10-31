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
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Imlight.RemoteAdmin;

namespace Imlight.Setup;

/// <summary>
/// The Account Transfer tab on the Server page (Niduus, 2026-10-31: "Allow for players using the Ahziael application
/// whilst in another server, to copy their player data for easy transfer from one to another. Obviously the host would
/// have to download their files to implement"). A player downloads their own account from any Imlight server with their
/// game username and password (RemoteSession.DownloadAccountAsync); the host of another server opens that file here and
/// adds it (AccountTransfer). Hosts can also save any account of their own server as a file, and choose whether their
/// players may download theirs ([Remote Admin] PlayerDownloads).
/// </summary>
internal sealed partial class ManagerWindow {

    private const string ACCOUNT_SERVERS_FILE = "account-download-servers.json";

    private readonly TextBox _moveHost = new() { Watermark = "The server's address, the same one you play on" };
    private readonly TextBox _movePort = new() { Text = RemoteAdminWire.DEFAULT_PORT.ToString(), Width = 110, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _moveUser = new() { Watermark = "Your username on that server" };
    private readonly TextBox _movePassword = new() { PasswordChar = '•', Watermark = "Your password on that server" };
    private readonly TextBlock _moveProgress = Sub("");

    private readonly TextBlock _moveImportNote = Sub("");
    private readonly TextBox _moveImportName = new() { Watermark = "Username for the account here" };
    private readonly TextBox _moveImportPassword = new() { PasswordChar = '•', Watermark = "Leave empty to keep their password" };
    private readonly StackPanel _moveImportBoxes = new() { Spacing = 10, IsVisible = false };
    private AccountTransfer.AccountFile? _moveFile;
    private byte[]? _moveFileBytes;

    private readonly ComboBox _moveExportAccount = new() { MinWidth = 240 };
    private readonly TextBlock _moveExportNote = Sub("");

    private readonly ToggleSwitch _moveHostSwitch = new() { OnContent = "On", OffContent = "Off" };
    private bool _showingMoveHost;

    private Control AccountTransferTab() {
        LoadAccountServer();
        var cards = new List<Control> {
            Fold("download", "Accounts", "Download my account",
                "Playing on someone else's Imlight server? Save your whole account as a file: every wizard with their items, bank, pets, " +
                "quests and houses. Give the file to the host of another server, and they add it with \"Bring an account into this server\" below.",
                Field("Address", "The server's address, the one you play on.", _moveHost),
                Field("Port", "12410 unless the host changed it.", _movePort),
                Field("Username", "", _moveUser),
                Field("Password", "Sent only to that server, encrypted. Ahziael never saves it.", _movePassword),
                Row(Action(Primary("Download my account"), DownloadMyAccountAsync)),
                _moveProgress,
                Sub("Log out of the game first, so your last few minutes are in the file too. Nothing changes on that server: " +
                    "your account stays there as it is.")),
            ImportAccountCard(),
            ExportAccountCard(),
        };
        if (!IsRemote) {
            cards.Add(MoveHostCard());
        }

        cards.Add(Shut(Fold("info", "Guides", "How it works and what moves",
            "What the file holds, what stays behind, and how it keeps accounts safe.",
            Sub("The file holds the account and everything in the server's player database that belongs to it: every wizard, their items " +
                "and equipment, bank and shared bank, decks, pets, snacks, reagents, quests, houses with everything placed in them, and attics."),
            Sub("What stays behind: friends lists, guilds, Loyalty Store tokens and anything the server keeps outside its database. " +
                "The account comes in as a plain player, whatever rank it had; the host can give it a rank on the Accounts tab."),
            Sub("Importing never changes anything already on the server: the account, its wizards, items, pets and houses all get new numbers, " +
                "and either everything arrives or nothing does. A file from one person can't bring in things that belong to someone else."),
            Sub("The file keeps the account's password in scrambled form so the player can log in with the same one. Only give it to the host " +
                "you are moving to. The host can type a new password when importing instead."))));

        return Page([.. cards]);
    }

    // ---------------------------------------------------------------------
    // Downloading your own account from a server
    // ---------------------------------------------------------------------

    private sealed record AccountServer(string Host, int Port, string Username, string? Fingerprint);

    private string AccountServersPath => Path.Combine(_manager.InstallFolder, ACCOUNT_SERVERS_FILE);

    private List<AccountServer> AccountServers() {
        try {
            return File.Exists(AccountServersPath) ? JsonSerializer.Deserialize<List<AccountServer>>(File.ReadAllText(AccountServersPath)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            return [];
        }
    }

    // The last server downloaded from fills the boxes.
    private void LoadAccountServer() {
        if (AccountServers().LastOrDefault() is { } last && string.IsNullOrEmpty(_moveHost.Text)) {
            _moveHost.Text = last.Host;
            _movePort.Text = last.Port.ToString();
            _moveUser.Text = last.Username;
        }
    }

    private async Task DownloadMyAccountAsync() {
        var host = (_moveHost.Text ?? "").Trim();
        var user = (_moveUser.Text ?? "").Trim();
        var password = _movePassword.Text ?? "";
        if (host.Length == 0 || user.Length == 0 || password.Length == 0) {
            throw new PanelException("Fill in the server's address, your username and your password first.");
        }

        if (!int.TryParse(_movePort.Text, out var port) || port is < 1 or > 65535) {
            throw new PanelException("The port is a number, 12410 unless the host changed it.");
        }

        // Known already: its fingerprint must be the same as last time.
        var known = AccountServers().LastOrDefault(s => string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase) && s.Port == port);
        var server = new RemoteServer("", host, port, user, known?.Fingerprint
            ?? RemoteServers.Load(_manager.InstallFolder).FirstOrDefault(s => string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase) && s.Port == port)?.Fingerprint);
        _moveProgress.Text = $"Connecting to {host}…";
        Func<string, Task<bool>> trust = fingerprint => Dispatcher.UIThread.InvokeAsync(() => ConfirmAsync(
            $"This is the first time you download from {host}. Its fingerprint is:\n\n{fingerprint}\n\n" +
            "Ask the host to read theirs out from Server > Account Transfer in their Ahziael. Click OK only if they are the same."));
        RemoteSession.DownloadedAccount downloaded;
        try {
            try {
                downloaded = await Task.Run(() => RemoteSession.DownloadAccountAsync(server, password, trust));
            }
            catch (RemoteSession.FingerprintChangedException changed) {
                // The host may have made a new one (a new PC, a fresh install): only they can say so.
                if (!await ConfirmAsync($"{host} has a different fingerprint than last time:\n\n{changed.Fingerprint}\n\n" +
                        "That happens when the host moved their server to a new PC or installed it again; it can also mean someone is " +
                        "pretending to be the server. Ask the host to read theirs out from Server > Account Transfer. Click OK only if they are the same.")) {
                    throw new PanelException("Not downloaded. Check the fingerprint with the host first.");
                }

                downloaded = await Task.Run(() => RemoteSession.DownloadAccountAsync(server with { Fingerprint = null }, password,
                    fingerprint => Task.FromResult(fingerprint == changed.Fingerprint)));
            }
        }
        catch {
            _moveProgress.Text = "";
            throw;
        }

        _movePassword.Text = "";
        SaveAccountServer(new AccountServer(host, port, user, downloaded.Fingerprint));
        _moveProgress.Text = $"Got {downloaded.Account}: {downloaded.Summary}.";
        var saved = await SaveAccountFileAsync(downloaded.Account, downloaded.File);
        if (saved is null) {
            _moveProgress.Text = "Not saved.";

            return;
        }

        _moveProgress.Text = $"Saved {downloaded.Account} ({downloaded.Summary}) as \"{saved}\".";
        Say($"Your account is saved as \"{saved}\". Give that file to the host of the server you are moving to." + (downloaded.WasOnline
            ? " You were still in the game, so the last few minutes may be missing: log out and download it again to be sure."
            : ""));
    }

    private void SaveAccountServer(AccountServer server) {
        try {
            var all = AccountServers().Where(s => !(string.Equals(s.Host, server.Host, StringComparison.OrdinalIgnoreCase) && s.Port == server.Port)).ToList();
            all.Add(server);
            File.WriteAllText(AccountServersPath, JsonSerializer.Serialize(all.TakeLast(20).ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Only means the fingerprint is asked about again next time.
        }
    }

    /// <summary>Asks where to save an account file and writes it there. The file's name, or null when cancelled.</summary>
    private async Task<string?> SaveAccountFileAsync(string account, byte[] bytes) {
        var cleaned = new string([.. account.Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : ' ')]).Trim();
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
            Title = "Save the account file", SuggestedFileName = (cleaned.Length == 0 ? "My account" : cleaned) + " account" + AccountTransfer.EXTENSION,
            DefaultExtension = AccountTransfer.EXTENSION.TrimStart('.'), ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("Ahziael account") { Patterns = ["*" + AccountTransfer.EXTENSION] }],
            SuggestedStartLocation = await StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Desktop),
        });
        if (file is null) {
            return null;
        }

        await using (var stream = await file.OpenWriteAsync()) {
            stream.SetLength(0);
            await stream.WriteAsync(bytes);
        }

        return file.Name;
    }

    // ---------------------------------------------------------------------
    // Bringing an account into this server
    // ---------------------------------------------------------------------

    private Control ImportAccountCard() {
        ShowImportFile();

        return Fold("file", "Accounts", "Bring an account into this server",
            "For the host: add a player's account from a file they downloaded on another server. The server must be running.",
            Row(Action(Primary("Open an account file…"), OpenAccountFileAsync)),
            _moveImportNote,
            _moveImportBoxes);
    }

    private async Task OpenAccountFileAsync() {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
            Title = "Open an account file", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Ahziael account") { Patterns = ["*" + AccountTransfer.EXTENSION] }, FilePickerFileTypes.All],
        });
        if (files.Count == 0) {
            return;
        }

        var path = files[0].TryGetLocalPath() ?? throw new PanelException("Ahziael can only open an account file that is on this computer.");
        if (new FileInfo(path).Length > RemoteAdminWire.MAX_BODY) {
            throw new PanelException("That file is too big to be one player's account.");
        }

        var bytes = await File.ReadAllBytesAsync(path);
        var file = await Task.Run(() => AccountTransfer.Read(bytes));
        (_moveFile, _moveFileBytes) = (file, bytes);
        _moveImportName.Text = file.Username;
        _moveImportPassword.Text = "";
        ShowImportFile();
    }

    private void ShowImportFile() {
        _moveImportBoxes.Children.Clear();
        if (_moveFile is not { } file) {
            _moveImportBoxes.IsVisible = false;
            _moveImportNote.Text = "Open the account file (" + AccountTransfer.EXTENSION + ") the player sent you to see what is in it. Nothing changes until you press Import.";

            return;
        }

        var made = DateTime.TryParse(file.Made, out var time) ? time.ToLocalTime().ToString("g") : "?";
        _moveImportNote.Text = $"{file.Username}{(file.Server.Length > 0 ? $" from {file.Server}" : "")}, saved {made}: {AccountTransfer.Describe(file.Counts)}.";
        _moveImportBoxes.IsVisible = true;
        foreach (var character in file.Characters) {
            _moveImportBoxes.Children.Add(Sub("• " + character));
        }

        _moveImportBoxes.Children.Add(Field("Username", "The name they log in with here. Change it if someone here already has it.", _moveImportName));
        _moveImportBoxes.Children.Add(Field("Password", file.HasPassword
            ? "Empty: they log in with the password they had. Or type a new one and tell them."
            : "The file has no password: type one and tell them.", _moveImportPassword));
        _moveImportBoxes.Children.Add(Row(Action(Primary("Import"), ImportAccountAsync), Action(Plain("Cancel"), () => {
            (_moveFile, _moveFileBytes) = (null, null);
            ShowImportFile();

            return Task.CompletedTask;
        })));
    }

    private async Task ImportAccountAsync() {
        if (_moveFile is not { } file || _moveFileBytes is not { } bytes) {
            return;
        }

        var username = (_moveImportName.Text ?? "").Trim();
        var password = _moveImportPassword.Text ?? "";
        if (username.Length == 0) {
            throw new PanelException("Type a username for the account.");
        }

        if (RemoteSession.Current is { } remote) {
            await remote.ImportAccountAsync(bytes, username, password.Length > 0 ? password : null);
        }
        else {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            await AccountTransfer.ImportAsync(_manager.LocalPlayerDatabase(http), file, username,
                password.Length > 0 ? PlayerAccounts.HashPassword(password) : null);
        }

        (_moveFile, _moveFileBytes) = (null, null);
        ShowImportFile();
        Say($"{username} is on this server now ({AccountTransfer.Describe(file.Counts)}). They log in with {(password.Length > 0 ? "the new password you typed" : "the password they had")}. " +
            "It is a plain player account: give it a rank on the Accounts tab if it needs one.");
        _ = LoadTransferAccountsAsync();
    }

    // ---------------------------------------------------------------------
    // Saving an account of this server
    // ---------------------------------------------------------------------

    private Control ExportAccountCard() => Shut(Fold("users", "Accounts", "Save an account of this server as a file",
        "For the host: the same file a player gets with \"Download my account\", for any account here. The server must be running.",
        Row(_moveExportAccount, Action(Primary("Save as a file…"), ExportAccountAsync)),
        _moveExportNote));

    private async Task LoadTransferAccountsAsync() {
        try {
            using var accounts = _manager.Accounts();
            var list = await accounts.ListAsync();
            var selected = _moveExportAccount.SelectedItem as string;
            _moveExportAccount.ItemsSource = list.Select(a => a.Username).ToList();
            _moveExportAccount.SelectedItem = selected is not null && list.Any(a => a.Username == selected) ? selected : list.FirstOrDefault()?.Username;
            _moveExportNote.Text = $"{list.Count} account{(list.Count == 1 ? "" : "s")}.";
        }
        catch (Exception ex) when (ex is AccountException or PanelException) {
            _moveExportNote.Text = ex.Message;
        }
    }

    private async Task ExportAccountAsync() {
        if (_moveExportAccount.SelectedItem is not string username) {
            throw new PanelException("Pick an account first. Start the server if the list is empty.");
        }

        byte[] bytes;
        if (RemoteSession.Current is { } remote) {
            bytes = await remote.ExportAccountAsync(username);
        }
        else {
            // The wizards' names from the list the server writes for Ahziael.
            var characters = new JsonArray();
            foreach (var wizard in WizardListNow()?.FirstOrDefault(a => string.Equals(a.Account, username, StringComparison.OrdinalIgnoreCase))?.Wizards ?? []) {
                characters.Add(new JsonObject { ["Name"] = wizard.Name, ["School"] = wizard.School, ["Level"] = wizard.Level });
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            var file = await AccountTransfer.ExportAsync(_manager.LocalPlayerDatabase(http), username,
                _manager.GetSetting("Remote Admin", "ServerName") ?? "", characters);
            bytes = AccountTransfer.ToBytes(file);
        }

        var summary = AccountTransfer.Describe(AccountTransfer.Read(bytes).Counts);
        if (await SaveAccountFileAsync(username, bytes) is { } saved) {
            Say($"Saved {username} ({summary}) as \"{saved}\". Another server's host adds it on their Account Transfer tab.");
        }
    }

    // ---------------------------------------------------------------------
    // For the host: may players download theirs
    // ---------------------------------------------------------------------

    private Control MoveHostCard() {
        _moveHostSwitch.IsCheckedChanged += async (_, _) => {
            if (_showingMoveHost) {
                return;
            }

            await Guard(() => {
                var on = _moveHostSwitch.IsChecked == true;
                var lines = File.ReadAllLines(_manager.ConfigFile).ToList();
                IniFile.Set(lines, "Remote Admin", "PlayerDownloads", on ? "true" : "false");
                if (IniFile.Get(_manager.ConfigFile, "Remote Admin", "Port") is null) {
                    IniFile.Set(lines, "Remote Admin", "Port", RemoteAdminWire.DEFAULT_PORT.ToString());
                }

                File.WriteAllLines(_manager.ConfigFile, lines);
                ShowMoveHost();
                Say(on
                    ? "Players can download their own account now. " + (FriendAccess.WasTurnedOn(_manager.InstallFolder)
                        ? "Click \"Let friends connect\" on the Play tab once more so your router lets its port through too."
                        : "Players on your Wi-Fi can already; for players on the internet, click \"Let friends connect\" on the Play tab.")
                    : "Players can no longer download their account from your server.");

                return Task.CompletedTask;
            });
        };
        ShowMoveHost();

        return Fold("server", "Settings", "Let players download their account",
            "On by default. Each player gets only their own account, with their own username and password, and nothing changes on your server.",
            SwitchRow("Let players download their account", _moveHostSwitch, "Applies right away, no restart."),
            _moveHostInfo,
            Sub("Players need your address (the Play tab shows it under Play with friends), the port, and the fingerprint above to check the first time."));
    }

    private readonly TextBlock _moveHostInfo = Sub("");

    private void ShowMoveHost() {
        _showingMoveHost = true;
        // On unless turned off, as the server reads it.
        var on = _manager.GetSetting("Remote Admin", "PlayerDownloads")?.Equals("false", StringComparison.OrdinalIgnoreCase) != true;
        _moveHostSwitch.IsChecked = on;
        _showingMoveHost = false;
        var port = _manager.GetSetting("Remote Admin", "Port") ?? RemoteAdminWire.DEFAULT_PORT.ToString();
        var fingerprintFile = Path.Combine(_manager.ConfigFolder, "remote-admin", "fingerprint.txt");
        var fingerprint = File.Exists(fingerprintFile) ? RemoteAdminWire.ShortFingerprint(File.ReadAllText(fingerprintFile).Trim()) : null;
        _moveHostInfo.Text = !on
            ? "Off."
            : $"On, port {port}. " + (fingerprint is null
                ? "Your server shows its fingerprint here once it has started with this on."
                : $"Fingerprint: {fingerprint}");
    }

}
