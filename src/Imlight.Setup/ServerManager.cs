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
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Imlight.Setup;

/// <summary>What the app shows about the server.</summary>
internal sealed record ServerStatus(bool Running, bool Ready, bool? Randomizer, bool? DieDelete, bool ClientFound);

/// <summary>One setting in Imlight.ini, with the comment above it as help.</summary>
internal sealed record Setting(string Section, string Key, string Value, string Help);

/// <summary>One line of the server log; Count is how many times in a row it was written.</summary>
internal sealed record LogLine(string Text, string Message, string Level, int Count);

/// <summary>
/// Everything the app does with an installed server: start and stop it, read its log and settings,
/// manage accounts and play Wizard101. Problems are reported as
/// <see cref="PanelException"/> with a message written for the person using the app.
/// </summary>
internal sealed partial class ServerManager(string installFolder) {

    private const int LOG_TAIL_BYTES = 512 * 1024;

    internal string InstallFolder { get; } = Path.GetFullPath(installFolder);

    internal string ConfigFile => InstalledCommands.ConfigFile(InstallFolder);

    private string ServerFolder => InstalledCommands.ServerFolder(InstallFolder);

    private int LoginPort => int.TryParse(IniFile.Get(ConfigFile, "Login Server", "LoginServerPort"), out var port) ? port : 12000;

    internal bool IsInstalled => File.Exists(ConfigFile);

    // ---------------------------------------------------------------------
    // Server
    // ---------------------------------------------------------------------

    internal ServerStatus Status() {
        var running = InstalledCommands.IsServerRunning(InstallFolder);
        var randomizer = IniFile.Get(ConfigFile, "Randomizer", "Enabled");
        var dieDelete = IniFile.Get(ConfigFile, "Die Delete", "Enabled");

        return new ServerStatus(
            running,
            running && IsListening(LoginPort),
            randomizer is null ? null : randomizer.Equals("true", StringComparison.OrdinalIgnoreCase),
            dieDelete is null ? null : dieDelete.Equals("true", StringComparison.OrdinalIgnoreCase),
            GameClient.ClientExe(InstallFolder) is not null);
    }

    /// <summary>
    /// Whether an on/off setting in Imlight.ini is on. A missing setting counts as off.
    /// </summary>
    internal bool GetSwitch(string section, string key)
        => IniFile.Get(ConfigFile, section, key)?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

    internal bool HasSetting(string section, string key) => IniFile.Get(ConfigFile, section, key) is not null;

    internal string? GetSetting(string section, string key) => IniFile.Get(ConfigFile, section, key);

    internal string StartServer() {
        if (InstalledCommands.IsServerRunning(InstallFolder)) {
            return "The server is already running.";
        }

        var self = Environment.ProcessPath ?? throw new PanelException("The app cannot find its own program.");

        // No window of its own: the app shows the server's log.
        var startInfo = new ProcessStartInfo(self) {
            WorkingDirectory = InstallFolder,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // A framework-dependent build runs through dotnet; pass the program file along.
        if (Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) {
            startInfo.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        }

        foreach (var arg in new[] { "--start", "--no-wait", "--dir", InstallFolder }) {
            startInfo.ArgumentList.Add(arg);
        }

        Process.Start(startInfo)?.Dispose();

        // Routers forget opened ports when they restart, so ask again each start once friends were let in.
        if (FriendAccess.WasTurnedOn(InstallFolder)) {
            _ = Task.Run(() => FriendAccess.RefreshRouterAsync(InstallFolder));
        }

        return "The server is starting. It is ready for players when the dot turns green.";
    }

    internal string StopServer() {
        var exe = Path.GetFullPath(InstalledCommands.ServerExe(InstallFolder));
        var stopped = 0;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe))) {
            using (process) {
                try {
                    if (!string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) {
                        continue;
                    }

                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(15000);
                    stopped++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) {
                    // Already gone, or not ours to stop.
                }
            }
        }

        return stopped > 0 ? "The server was stopped." : "The server was not running.";
    }

    internal async Task<string> RestartServerAsync() {
        StopServer();
        await Task.Delay(1500);

        return StartServer();
    }

    private static bool IsListening(int port) {
        try {
            using var client = new TcpClient();

            return client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromMilliseconds(500)) && client.Connected;
        }
        catch (AggregateException) {
            return false;
        }
    }

    // ---------------------------------------------------------------------
    // Play and commands
    // ---------------------------------------------------------------------

    /// <summary>
    /// Plays on the server on this computer, starting it first when it is not running.
    /// </summary>
    internal async Task<string> PlayHereAsync() {
        if (GameClient.ClientExe(InstallFolder) is null) {
            throw new PanelException("Wizard101 could not be found. Click \"Update Imlight\" on the Tools tab of the app once, then try again.");
        }

        if (IsListening(LoginPort)) {
            await GameClient.LaunchAsync(InstallFolder, "127.0.0.1", LoginPort);

            return "Starting Wizard101. Log in with your account on this server.";
        }

        StartServer();
        _ = Task.Run(async () => {
            for (var waited = 0; waited < 300; waited += 2) {
                await Task.Delay(TimeSpan.FromSeconds(2));
                if (IsListening(LoginPort)) {
                    await GameClient.LaunchAsync(InstallFolder, "127.0.0.1", LoginPort);

                    return;
                }
            }
        });

        return "Starting the server first. Wizard101 opens by itself when the server is ready.";
    }

    // ---------------------------------------------------------------------
    // Log
    // ---------------------------------------------------------------------

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(\.\d+)?\s+(?<level>[A-Z]{3})\s+")]
    private static partial Regex LogLinePrefix();

    /// <summary>
    /// The end of the newest log file, with runs of the same message folded into one line and a count.
    /// </summary>
    internal List<LogLine> ReadLog() {
        var file = NewestLogFile();
        if (file is null) {
            return [];
        }

        string text;
        using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
            var start = Math.Max(0, stream.Length - LOG_TAIL_BYTES);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
            if (start > 0) {
                // Drop the partial first line.
                text = text[(text.IndexOf('\n') + 1)..];
            }
        }

        return FoldLines(text.Split('\n'));
    }

    internal static List<LogLine> FoldLines(IEnumerable<string> rawLines) {
        var lines = new List<LogLine>();
        foreach (var raw in rawLines) {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) {
                continue;
            }

            var match = LogLinePrefix().Match(line);
            if (!match.Success && lines.Count > 0) {
                // A continuation, such as a stack trace line.
                lines[^1] = lines[^1] with { Text = lines[^1].Text + "\n" + line };

                continue;
            }

            var level = match.Success ? match.Groups["level"].Value : "INF";
            var message = match.Success ? line[match.Length..] : line;
            if (lines.Count > 0 && lines[^1].Message == message) {
                lines[^1] = lines[^1] with { Count = lines[^1].Count + 1, Text = line };

                continue;
            }

            lines.Add(new LogLine(line, message, level, 1));
        }

        return lines.Count > 400 ? lines[^400..] : lines;
    }

    // ---------------------------------------------------------------------
    // Settings
    // ---------------------------------------------------------------------

    /// <summary>
    /// Every key in Imlight.ini by section, with the comment above each key as its help text.
    /// </summary>
    internal List<(string Section, List<Setting> Settings)> ReadSettings() {
        var sections = new List<(string, List<Setting>)>();
        List<Setting>? entries = null;
        string? section = null;
        var help = new List<string>();

        foreach (var raw in File.ReadLines(ConfigFile)) {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) {
                section = line[1..^1].Trim();
                entries = [];
                sections.Add((section, entries));
                help.Clear();

                continue;
            }

            if (line.StartsWith(';') || line.StartsWith('#')) {
                help.Add(line.TrimStart(';', '#').Trim());

                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0 || entries is null) {
                help.Clear();

                continue;
            }

            entries.Add(new Setting(section!, line[..equals].Trim(), line[(equals + 1)..].Trim(), string.Join(" ", help)));
            help.Clear();
        }

        return sections;
    }

    /// <summary>
    /// Saves changed values of keys already in Imlight.ini. True when the server is running and needs a restart.
    /// </summary>
    internal bool SaveSettings(IReadOnlyCollection<Setting> changes) {
        var lines = File.ReadAllLines(ConfigFile).ToList();
        foreach (var change in changes) {
            // Only keys already in the file; the app never invents settings.
            if (IniFile.Get(ConfigFile, change.Section, change.Key) is null) {
                throw new PanelException($"{change.Section} > {change.Key} is not a setting in Imlight.ini.");
            }

            IniFile.Set(lines, change.Section, change.Key, change.Value.ReplaceLineEndings(" "));
        }

        File.WriteAllLines(ConfigFile, lines);

        return InstalledCommands.IsServerRunning(InstallFolder);
    }

    // ---------------------------------------------------------------------
    // Accounts
    // ---------------------------------------------------------------------

    internal PlayerAccounts Accounts()
        => PlayerAccounts.FromConfig(ConfigFile, out var reason) ?? throw new PanelException(reason!);

    // ---------------------------------------------------------------------
    // Tools
    // ---------------------------------------------------------------------

    /// <summary>
    /// Where the server writes its lists of IDs, zones and quests.
    /// </summary>
    internal string IdLookupFolder
        => Path.GetFullPath(Path.Combine(ServerFolder, IniFile.Get(ConfigFile, "ID Lookup", "OutputPath") ?? "./id-lookup"));

    /// <summary>
    /// Stops the server and opens Imlight.exe in its own window to update (--update) or uninstall (--uninstall).
    /// The app closes afterwards, so the files it uses can be replaced or removed.
    /// </summary>
    internal void RunInstaller(string mode) {
        var exe = Path.Combine(InstallFolder, InstalledCommands.INSTALLED_EXE);
        if (!File.Exists(exe)) {
            throw new PanelException($"{exe} is missing. Run {Edition.RELEASE_ASSET} again to repair Imlight.");
        }

        StopServer();
        Process.Start(new ProcessStartInfo(exe) {
            Arguments = $"{mode} --dir \"{InstallFolder}\"",
            WorkingDirectory = InstallFolder,
            UseShellExecute = true,
        })?.Dispose();
    }

    /// <summary>
    /// Whether the app updates Imlight by itself when it opens. On unless the marker file says off.
    /// </summary>
    internal bool AutoUpdate {
        get => !File.Exists(Path.Combine(InstallFolder, AUTO_UPDATE_OFF_FILE));
        set {
            var path = Path.Combine(InstallFolder, AUTO_UPDATE_OFF_FILE);
            if (value) {
                File.Delete(path);
            }
            else {
                File.WriteAllText(path, "Imlight does not update by itself while this file exists. The Tools tab in the app changes it.");
            }
        }
    }

    private const string AUTO_UPDATE_OFF_FILE = "auto-update-off.txt";

    internal string LogFolder => Path.Combine(ServerFolder, "logs");

    // The server runs each line of this file as a command of the wizard playing on this PC (ManagerCommands).
    private const string COMMANDS_FILE = "manager-commands.txt";

    /// <summary>
    /// Runs a command in the game on this PC only, as if typed in chat. Wizards on other PCs never run it.
    /// </summary>
    internal void SendCommand(string command) {
        command = command.Trim();
        if (command.Length == 0) {
            throw new PanelException("Type a command first, such as .elixir50.");
        }

        if (!Status().Running) {
            throw new PanelException("Start the server and log in to Wizard101 on this PC first.");
        }

        File.AppendAllText(Path.Combine(ServerFolder, COMMANDS_FILE), command.ReplaceLineEndings(" ") + Environment.NewLine);
    }

    // The server keeps where each wizard stands in the log folder too; it is not a log.
    internal const string POSITIONS_FILE = "positions.txt";

    /// <summary>
    /// The log file the server is writing now, or null before it has written one.
    /// </summary>
    internal FileInfo? NewestLogFile() {
        var logs = LogFolder;

        return Directory.Exists(logs)
            ? new DirectoryInfo(logs).EnumerateFiles()
                .Where(f => f.Extension is ".txt" or ".log" && !f.Name.Equals(POSITIONS_FILE, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
            : null;
    }

    /// <summary>
    /// Copies the whole newest log to the desktop, ready to drag into a chat. Returns the copy's path.
    /// </summary>
    internal string SaveLogToDesktop() {
        var file = NewestLogFile() ?? throw new PanelException("There is no log yet. Start the server first.");
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop)) {
            desktop = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        var target = Path.Combine(desktop, $"Imlight log {DateTime.Now:yyyy-MM-dd HH-mm-ss}.txt");
        using (var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var copy = File.Create(target)) {
            source.CopyTo(copy);
        }

        return target;
    }

    internal string ConfigFolder => Path.GetDirectoryName(ConfigFile)!;

    /// <summary>
    /// Opens a folder or file with the program Windows uses for it.
    /// </summary>
    internal static void Open(string path) {
        if (Directory.Exists(path) is false && File.Exists(path) is false) {
            Directory.CreateDirectory(path);
        }

        if (OperatingSystem.IsWindows()) {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
    }

}

/// <summary>
/// Something the app could not do. The message is written for the person using it.
/// </summary>
internal sealed class PanelException(string message) : Exception(message);
