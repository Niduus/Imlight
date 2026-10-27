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
/// The Bazaar bot card on the Server tab and its log on the Logs tab (Niduus, 2026-10-27: "I want the Bazaar to have
/// it's own mini server" and "a bazaar bot log into Ahziael as well"). The server starts the bot, a second copy of
/// Imlight.Director, which keeps the Bazaar's entries and stock (Game/Bazaar/BazaarBot). The bot writes its log, stock
/// and a status file in Server\logs\bazaar-bot; this card reads them. Its settings are [Bazaar] in Imlight.ini, which
/// the server and the bot read again by themselves when saved.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string BAZAAR_BOT_FOLDER = "bazaar-bot";
    private const int BAZAAR_LOG_SHOWN_LINES = 150;

    private readonly TextBlock _bazaarBotState = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly ToggleSwitch _bazaarOwnServer = new() { OnContent = "Own program", OffContent = "In the server" };
    private readonly ToggleSwitch _bazaarNoAuction = new() { OnContent = "Included", OffContent = "Left out" };
    private readonly NumericUpDown _bazaarStartStock = new() { Minimum = 1, Maximum = 1_000_000, Increment = 10, FormatString = "0", Width = 140 };
    private readonly NumericUpDown _bazaarRestockMinutes = new() { Minimum = 0, Maximum = 100_000, Increment = 5, FormatString = "0", Width = 140 };
    private readonly NumericUpDown _bazaarRestockAmount = new() { Minimum = 0, Maximum = 1_000_000, Increment = 5, FormatString = "0", Width = 140 };
    private readonly NumericUpDown _bazaarPricePercent = new() { Minimum = 0, Maximum = 100_000, Increment = 25, FormatString = "0", Width = 140 };
    private readonly DispatcherTimer _bazaarTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private TextBox? _bazaarLogBox;
    private bool _showingBazaar;
    private string _bazaarLogText = "";

    private string BazaarBotFolder => Path.Combine(_manager.LogFolder, BAZAAR_BOT_FOLDER);

    private Control BazaarBotCard() {
        _bazaarOwnServer.IsCheckedChanged += async (_, _) => {
            if (!_showingBazaar) {
                await Guard(() => SetSwitchAsync("Bazaar", "OwnServer", "The Bazaar as its own program", _bazaarOwnServer.IsChecked == true));
            }
        };
        _bazaarNoAuction.IsCheckedChanged += async (_, _) => {
            if (!_showingBazaar) {
                await Guard(() => SetSwitchAsync("Bazaar", "NoAuctionItems", "No-auction items in the Bazaar", _bazaarNoAuction.IsChecked == true));
            }
        };
        foreach (var (box, key) in new[] {
                     (_bazaarStartStock, "StartStock"), (_bazaarRestockMinutes, "RestockMinutes"),
                     (_bazaarRestockAmount, "RestockAmount"), (_bazaarPricePercent, "ItemPricePercent"),
                 }) {
            box.ValueChanged += (_, _) => {
                if (!_showingBazaar && box.Value is { } value) {
                    _ = Guard(() => SaveBazaarNumberAsync(key, (int) value));
                }
            };
        }

        LoadBazaarSettings();
        _bazaarTimer.Tick += (_, _) => RefreshBazaarBot();
        _bazaarTimer.Start();
        RefreshBazaarBot();

        return Shut(Fold("bag", "Server", "Bazaar bot",
            "The Bazaar runs as a little program of its own next to the server, so its lists and stock never slow the server " +
            "down. The server starts it by itself and starts it again if it stops; if it is down, the server runs the Bazaar " +
            "itself. It sells every item, snack, reagent and treasure card the real Bazaar takes, and restocks over time. " +
            "Changes count right away.",
            _bazaarBotState,
            SwitchRow("Run the Bazaar as its own program", _bazaarOwnServer,
                "Off has the server run the Bazaar itself, as before."),
            SwitchRow("Items the real Bazaar never takes", _bazaarNoAuction,
                "No-auction items (most pet jewels, quest rewards, crown-only gear). Left out unless you turn this on."),
            SwitchRow("Stock at the start", _bazaarStartStock, "How many of each thing the Bazaar has before anyone buys."),
            SwitchRow("Restock every", _bazaarRestockMinutes, "Minutes between restocks. 0 = never restock."),
            SwitchRow("Restock amount", _bazaarRestockAmount, "How many more of each thing below the start stock, per restock."),
            SwitchRow("Gear and housing price (percent)", _bazaarPricePercent,
                "Percent of what the game says an item is worth. 200 = twice. Snacks, reagents and jewels stay 1 gold."),
            Row(Action(Plain("Restart Bazaar bot"), RestartBazaarBotAsync), Action(Plain("Open its folder"), () => {
                Directory.CreateDirectory(BazaarBotFolder);

                return Open(BazaarBotFolder);
            }))));
    }

    /// <summary>The bot's log on the Logs tab: what it stocks and restocks, and what players buy and sell.</summary>
    private Control BazaarBotLogCard() {
        _bazaarLogBox = new TextBox {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = MONO, FontSize = 12.5,
            Height = 360, Text = "Start the server; the Bazaar bot writes here about a minute later.",
        };

        return Shut(Fold("bag", "Logs", "Bazaar bot log",
            "What the Bazaar bot does: what it stocked, restocks, and what players buy from it and sell to it. Updated every 3 seconds.",
            LogWell(_bazaarLogBox),
            Row(Action(Plain("Copy all"), () => CopyAsync(_bazaarLogText)))));
    }

    private void LoadBazaarSettings() {
        _showingBazaar = true;
        try {
            _bazaarOwnServer.IsChecked = !string.Equals(_manager.GetSetting("Bazaar", "OwnServer"), "false", StringComparison.OrdinalIgnoreCase);
            _bazaarNoAuction.IsChecked = _manager.GetSwitch("Bazaar", "NoAuctionItems");
            _bazaarStartStock.Value = Number("StartStock", 100);
            _bazaarRestockMinutes.Value = Number("RestockMinutes", 30);
            _bazaarRestockAmount.Value = Number("RestockAmount", 10);
            _bazaarPricePercent.Value = Number("ItemPricePercent", 200);
        }
        finally {
            _showingBazaar = false;
        }

        decimal Number(string key, int fallback)
            => int.TryParse(_manager.GetSetting("Bazaar", key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    private Task SaveBazaarNumberAsync(string key, int value) {
        if (!_manager.HasSetting("Bazaar", key)) {
            throw new PanelException("This server's Imlight.ini has no [Bazaar] " + key + " yet. Click \"Update Ahziael\" once, then try again.");
        }

        if (_manager.GetSetting("Bazaar", key) == value.ToString(CultureInfo.InvariantCulture)) {
            return Task.CompletedTask;
        }

        _manager.SaveSettings([new Setting("Bazaar", key, value.ToString(CultureInfo.InvariantCulture), "")]);
        _settings = [];
        Say("Saved. The Bazaar uses it right away.");

        return Task.CompletedTask;
    }

    // The bot's status file: its state, process id, how many things it sells, when it wrote it, and its port.
    private (string State, int Pid, int Count, DateTime Written)? ReadBazaarStatus() {
        try {
            var path = Path.Combine(BazaarBotFolder, "status.txt");
            if (!File.Exists(path)) {
                return null;
            }

            var lines = File.ReadAllLines(path);
            if (lines.Length < 4) {
                return null;
            }

            return (lines[0], int.TryParse(lines[1], out var pid) ? pid : 0, int.TryParse(lines[2], out var count) ? count : 0,
                DateTime.TryParse(lines[3], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var written) ? written.ToUniversalTime() : DateTime.MinValue);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    private void RefreshBazaarBot() {
        var cardShown = _bazaarBotState.IsEffectivelyVisible;
        var logShown = _bazaarLogBox?.IsEffectivelyVisible == true;
        if (!cardShown && !logShown) {
            return;
        }

        var status = ReadBazaarStatus();
        var fresh = status is { } s && DateTime.UtcNow - s.Written < TimeSpan.FromSeconds(20);
        _bazaarBotState.Text = !cardShown ? _bazaarBotState.Text
            : !InstalledCommands.IsServerRunning(_manager.InstallFolder) ? "The server is not running, so the Bazaar bot is not either."
            : _bazaarOwnServer.IsChecked != true ? "Off: the server runs the Bazaar itself."
            : fresh && status!.Value.State == "ready" ? $"Running: the Bazaar sells {status.Value.Count:N0} things."
            : fresh && status!.Value.State == "starting" ? "Starting: reading the game files (about a minute)."
            : "Not running right now: the server runs the Bazaar itself, and starts the bot again within a minute.";
        _bazaarBotState.Foreground = fresh && status!.Value.State == "ready" ? ManagerTheme.Accent("good") : MUTED;

        if (!logShown) {
            return;
        }

        var text = ReadBazaarLog();
        if (text != _bazaarLogText) {
            _bazaarLogText = text;
            _bazaarLogBox.Text = string.Join('\n', text.Split('\n').TakeLast(BAZAAR_LOG_SHOWN_LINES));
            _bazaarLogBox.CaretIndex = _bazaarLogBox.Text.Length;
        }
    }

    private string ReadBazaarLog() {
        try {
            var file = Directory.Exists(BazaarBotFolder)
                ? new DirectoryInfo(BazaarBotFolder).EnumerateFiles("bazaar-bot*.txt").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
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
            return _bazaarLogText;
        }
    }

    private async Task RestartBazaarBotAsync() {
        if (!InstalledCommands.IsServerRunning(_manager.InstallFolder)) {
            throw new PanelException("Start the server first. It starts the Bazaar bot by itself.");
        }

        if (ReadBazaarStatus() is not { Pid: > 0 } status) {
            throw new PanelException("The Bazaar bot is not running yet. The server starts it about 20 seconds after it is ready.");
        }

        await Task.Run(() => {
            try {
                using var bot = Process.GetProcessById(status.Pid);
                if (bot.ProcessName.Contains("Imlight.Director", StringComparison.OrdinalIgnoreCase) || bot.ProcessName.Equals("dotnet", StringComparison.OrdinalIgnoreCase)) {
                    bot.Kill();
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) {
                // Already stopped.
            }
        });
        Say("The Bazaar bot was stopped. The server starts it again within half a minute; until then it runs the Bazaar itself.");
    }

}
