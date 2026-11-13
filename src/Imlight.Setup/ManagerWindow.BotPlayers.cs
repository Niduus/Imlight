using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The Bot Players card on Game Rules > Battles and World, and its log on the Logs tab (Niduus, 2026-11-12: bots get
/// "their own mini server like how Bazaar has one", off by default, killed when turned off, with logs). The server starts
/// the mini server (a second copy of Imlight.Director with --bots) only while [BotPlayers] Enabled is on. It writes its log,
/// roster and a status file in Server\logs\bot-players; this card reads them. This is the test stage: bots come out with
/// .bot test, walk, and pass in a battle they join.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string BOT_PLAYERS_FOLDER = "bot-players";
    private const int BOT_LOG_SHOWN_LINES = 200;

    private readonly TextBlock _botState = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly ToggleSwitch _botEnabled = new() { OnContent = "On", OffContent = "Off" };
    private readonly NumericUpDown _botJoinWait = new() { Minimum = 0, Maximum = 60, Increment = 1, FormatString = "0", Width = 140 };
    private readonly NumericUpDown _botReturning = new() { Minimum = 0, Maximum = 100, Increment = 5, FormatString = "0", Width = 140 };
    private readonly ToggleSwitch _botPopulate = new() { OnContent = "On", OffContent = "Off" };
    private readonly NumericUpDown _botMinPer = new() { Minimum = 0, Maximum = 18, Increment = 1, FormatString = "0", Width = 140 };
    private readonly NumericUpDown _botMaxPer = new() { Minimum = 0, Maximum = 18, Increment = 1, FormatString = "0", Width = 140 };
    private readonly NumericUpDown _botMaxStreet = new() { Minimum = 0, Maximum = 40, Increment = 1, FormatString = "0", Width = 140 };
    private readonly NumericUpDown _botMaxCommons = new() { Minimum = 0, Maximum = 40, Increment = 1, FormatString = "0", Width = 140 };
    private readonly DispatcherTimer _botTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private TextBox? _botLogBox;
    private bool _showingBots;
    private string _botLogText = "";
    private string _botSheetsText = "";

    // The bots out now, as the Quest Team and Ultimus pages show their wizards: stats, gear, deck and trained spells
    // (Niduus, 2026-11-13: the Trained Spells section "goes to QT and Bots as well").
    private readonly WizardSheet _botSheet = new("No bots out right now. Once bots are out, pick one here to see its stats, gear, deck and trained spells.") { Compact = true };

    private string BotPlayersFolder => Path.Combine(_manager.LogFolder, BOT_PLAYERS_FOLDER);

    private Control BotPlayersCard() {
        _botEnabled.IsCheckedChanged += async (_, _) => {
            if (!_showingBots) {
                await Guard(() => SetBotsEnabledAsync(_botEnabled.IsChecked == true));
            }
        };
        _botPopulate.IsCheckedChanged += async (_, _) => {
            if (!_showingBots) {
                await Guard(() => SaveBotSwitchAsync("Populate", _botPopulate.IsChecked == true));
            }
        };
        foreach (var (box, key) in new[] {
                     (_botJoinWait, "JoinWaitSeconds"), (_botReturning, "ReturningPercent"), (_botMinPer, "MinPerPlayer"),
                     (_botMaxPer, "MaxPerPlayer"), (_botMaxStreet, "MaxPerStreet"), (_botMaxCommons, "MaxInCommons"),
                 }) {
            box.ValueChanged += (_, _) => {
                if (!_showingBots && box.Value is { } value) {
                    _ = Guard(() => SaveBotNumberAsync(key, (int) value));
                }
            };
        }

        LoadBotSettings();
        ShowCardsOn(_botSheet);
        _botTimer.Tick += (_, _) => RefreshBotPlayers();
        _botTimer.Start();
        RefreshBotPlayers();

        return Shut(Fold("users", "Battles", "Bot Players",
            "Wizards the server plays by itself, so the game feels like a busy live server. They run in a little program of " +
            "their own next to the server (like the Bazaar bot). Off: that program does not run at all, and turning it off " +
            "stops it and every bot leaves. Bots fill the streets real players are in (never dungeons, houses, guild areas or " +
            "the arena) and walk around there. In the game, .bot test brings one out next to you. In a battle, .bot join brings it onto your side (it fights: it picks after you, heals and blades you, and finishes enemies), and " +
            ".bot kick takes the bots out of your battle; no bot joins that battle again. .bot remove sends the bots here " +
            "away, .bot list shows them. Changes count right away.",
            _botState,
            SwitchRow("Bot Players", _botEnabled, "On starts the Bot Players program within a few seconds. Off stops it and every bot leaves."),
            SwitchRow("Fill the streets", _botPopulate, "On: every street with real players in it gets bots, walking in one by one."),
            SwitchRow("Bots each player brings, at least", _botMinPer, "Each real player in a street brings a random number of bots from this..."),
            SwitchRow("Bots each player brings, at most", _botMaxPer, "...to this."),
            SwitchRow("Most bots in one street", _botMaxStreet, "However many players are there."),
            SwitchRow("Most bots in the Commons", _botMaxCommons, "The Commons of Wizard City has its own, smaller limit."),
            SwitchRow("Wait before joining a battle (seconds)", _botJoinWait,
                "How long a bot waits before it joins a battle, so a real friend can take the circle first."),
            SwitchRow("Bots that come back (percent)", _botReturning,
                "How often a bot that comes out is one you have seen before (same name), rather than a new one."),
            new Expander {
                Header = "Bots out now: stats, gear, deck and trained spells", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, Content = _botSheet.View,
            },
            Row(Action(Plain("Restart Bot Players program"), RestartBotPlayersAsync), Action(Plain("Open its folder"), () => {
                Directory.CreateDirectory(BotPlayersFolder);

                return Open(BotPlayersFolder);
            }))));
    }

    /// <summary>The bots' log on the Logs tab: which bots came out, where they walk, the battles they join and why they left.</summary>
    private Control BotPlayersLogCard() {
        _botLogBox = new TextBox {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = MONO, FontSize = 12.5,
            Height = 360, Text = "Turn Bot Players on (Game Rules > Battles and World) and start the server; the Bot Players program writes here.",
        };

        return Shut(Fold("users", "Logs", "Bot Players log",
            "What the bots do: who came out (new or coming back), where they walk, the battles they join, each round they " +
            "play, and why each one left. Updated every 3 seconds.",
            LogWell(_botLogBox),
            Row(Action(Plain("Copy all"), () => CopyAsync(_botLogText)), Action(Plain("Open its folder"), () => {
                Directory.CreateDirectory(BotPlayersFolder);

                return Open(BotPlayersFolder);
            }))));
    }

    private void LoadBotSettings() {
        _showingBots = true;
        try {
            _botEnabled.IsChecked = _manager.GetSwitch("BotPlayers", "Enabled");
            _botJoinWait.Value = Number("JoinWaitSeconds", 5);
            _botReturning.Value = Number("ReturningPercent", 70);
            _botPopulate.IsChecked = !_manager.HasSetting("BotPlayers", "Populate") || _manager.GetSwitch("BotPlayers", "Populate");
            _botMinPer.Value = Number("MinPerPlayer", 3);
            _botMaxPer.Value = Number("MaxPerPlayer", 6);
            _botMaxStreet.Value = Number("MaxPerStreet", 18);
            _botMaxCommons.Value = Number("MaxInCommons", 12);
        }
        finally {
            _showingBots = false;
        }

        decimal Number(string key, int fallback)
            => int.TryParse(_manager.GetSetting("BotPlayers", key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    private Task SetBotsEnabledAsync(bool on) {
        if (!_manager.HasSetting("BotPlayers", "Enabled")) {
            LoadBotSettings();
            throw new PanelException("This server's Imlight.ini has no [BotPlayers] section yet. Click \"Update Ahziael\" once, then try again.");
        }

        _manager.SaveSettings([new Setting("BotPlayers", "Enabled", on ? "true" : "false", "")]);
        _settings = [];
        Say(on ? "Bot Players is on. Its program starts within a few seconds; then type .bot test in the game."
            : "Bot Players is off. Its program stops and every bot leaves within a few seconds.");

        return Task.CompletedTask;
    }

    private Task SaveBotSwitchAsync(string key, bool on) {
        if (!_manager.HasSetting("BotPlayers", key)) {
            LoadBotSettings();
            throw new PanelException("This server's Imlight.ini has no [BotPlayers] " + key + " yet. Click \"Update Ahziael\" once, then try again.");
        }

        _manager.SaveSettings([new Setting("BotPlayers", key, on ? "true" : "false", "")]);
        _settings = [];
        Say("Saved. The bots use it right away.");

        return Task.CompletedTask;
    }

    private Task SaveBotNumberAsync(string key, int value) {
        if (!_manager.HasSetting("BotPlayers", key)) {
            throw new PanelException("This server's Imlight.ini has no [BotPlayers] " + key + " yet. Click \"Update Ahziael\" once, then try again.");
        }

        if (_manager.GetSetting("BotPlayers", key) == value.ToString(CultureInfo.InvariantCulture)) {
            return Task.CompletedTask;
        }

        _manager.SaveSettings([new Setting("BotPlayers", key, value.ToString(CultureInfo.InvariantCulture), "")]);
        _settings = [];
        Say("Saved. The bots use it right away.");

        return Task.CompletedTask;
    }

    // The program's status file: its state, process id, bots out, when it wrote it, its port and bots in the roster.
    private (string State, int Pid, int Out, DateTime Written, int Known)? ReadBotStatus() {
        try {
            var path = Path.Combine(BotPlayersFolder, "status.txt");
            if (!File.Exists(path)) {
                return null;
            }

            var lines = File.ReadAllLines(path);
            if (lines.Length < 4) {
                return null;
            }

            return (lines[0], int.TryParse(lines[1], out var pid) ? pid : 0, int.TryParse(lines[2], out var count) ? count : 0,
                DateTime.TryParse(lines[3], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var written) ? written.ToUniversalTime() : DateTime.MinValue,
                lines.Length > 5 && int.TryParse(lines[5], out var known) ? known : 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    private void RefreshBotPlayers() {
        var cardShown = _botState.IsEffectivelyVisible;
        var logShown = _botLogBox?.IsEffectivelyVisible == true;
        if (!cardShown && !logShown) {
            return;
        }

        if (cardShown) {
            var status = ReadBotStatus();
            var fresh = status is { } s && DateTime.UtcNow - s.Written < TimeSpan.FromSeconds(20);
            _botState.Text = !InstalledCommands.IsServerRunning(_manager.InstallFolder) ? "The server is not running, so Bot Players is not either."
                : _botEnabled.IsChecked != true ? "Off: the Bot Players program is not running and there are no bots."
                : fresh && status!.Value.State == "ready"
                    ? $"Running: {status.Value.Out} bot{(status.Value.Out == 1 ? "" : "s")} out, {status.Value.Known} in the roster."
                : "Starting: the server starts the Bot Players program within a few seconds.";
            _botState.Foreground = fresh && status!.Value.State == "ready" ? ManagerTheme.Accent("good") : MUTED;
            if (_botSheet.View.IsEffectivelyVisible) {
                ShowBotSheets(fresh && status!.Value.State == "ready");
            }
        }

        if (!logShown) {
            return;
        }

        var text = ReadBotLog();
        if (text != _botLogText) {
            _botLogText = text;
            _botLogBox!.Text = string.Join('\n', text.Split('\n').TakeLast(BOT_LOG_SHOWN_LINES));
            _botLogBox.CaretIndex = _botLogBox.Text.Length;
        }
    }

    private void ShowBotSheets(bool running) {
        try {
            var path = Path.Combine(BotPlayersFolder, "sheets.json");
            var text = running && File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromSeconds(30)
                ? File.ReadAllText(path) : "";
            if (text == _botSheetsText) {
                return;
            }

            _botSheetsText = text;
            _botSheet.Show(text.Length == 0 ? null : System.Text.Json.Nodes.JsonNode.Parse(text)?["members"] as System.Text.Json.Nodes.JsonArray);
        }
        catch (Exception) {
            // Half written: the next tick reads it again.
        }
    }

    private string ReadBotLog() {
        try {
            var file = Directory.Exists(BotPlayersFolder)
                ? new DirectoryInfo(BotPlayersFolder).EnumerateFiles("bot-players*.txt").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
                : null;
            if (file is null) {
                return "";
            }

            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - 128 * 1024), SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            return reader.ReadToEnd().ReplaceLineEndings("\n").TrimEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return _botLogText;
        }
    }

    private async Task RestartBotPlayersAsync() {
        if (!InstalledCommands.IsServerRunning(_manager.InstallFolder)) {
            throw new PanelException("Start the server first. It starts the Bot Players program by itself while Bot Players is on.");
        }

        if (_botEnabled.IsChecked != true) {
            throw new PanelException("Bot Players is off. Turn it on first.");
        }

        if (ReadBotStatus() is not { Pid: > 0 } status) {
            throw new PanelException("The Bot Players program is not running yet. The server starts it a few seconds after it is turned on.");
        }

        await Task.Run(() => {
            try {
                using var brain = Process.GetProcessById(status.Pid);
                if (brain.ProcessName.Contains("Imlight.Director", StringComparison.OrdinalIgnoreCase) || brain.ProcessName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)) {
                    brain.Kill();
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) {
                // Already stopped.
            }
        });
        Say("The Bot Players program was stopped; the bots left. The server starts it again within half a minute.");
    }

}
