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
/// The kiosk bot card on the Shops and Codes page and its log on the Logs tab (Niduus, 2026-11-21: "have it be a mini
/// server like Bazaar, only it activates the wake-up for it once it's interacted with. Same settings and behavior as
/// Bazaar. Toggles and all"). The server starts the kiosk bot, a second copy of Imlight.Director, when a player uses a
/// Pet Hatching Kiosk (Game/Pet/Kiosk/KioskBot). The bot writes its log and a status file in Server\logs\kiosk-bot; this
/// card reads them. Its settings are [HatchKiosk] in Imlight.ini, which the server and the bot read again by themselves
/// when saved.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string KIOSK_BOT_FOLDER = "kiosk-bot";
    private const int KIOSK_LOG_SHOWN_LINES = 150;

    private readonly TextBlock _kioskBotState = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly ToggleSwitch _kioskEnabled = new() { OnContent = "On", OffContent = "Off" };
    private readonly NumericUpDown _kioskIdleMinutes = new() { Minimum = 1, Maximum = 1440, Increment = 1, FormatString = "0", Width = 140 };
    private readonly NumericUpDown _kioskMaxPets = new() { Minimum = 1, Maximum = 100, Increment = 1, FormatString = "0", Width = 140 };
    private readonly NumericUpDown _kioskPeppers = new() { Minimum = 0, Maximum = 99, Increment = 1, FormatString = "0", Width = 140 };
    private readonly ToggleSwitch _kioskOwnServer = new() { OnContent = "Own program", OffContent = "In the server" };
    private readonly DispatcherTimer _kioskTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private TextBox? _kioskLogBox;
    private bool _showingKiosk;
    private string _kioskLogText = "";

    private string KioskBotFolder => Path.Combine(_manager.LogFolder, KIOSK_BOT_FOLDER);

    private Control KioskBotCard() {
        _kioskEnabled.IsCheckedChanged += async (_, _) => {
            if (!_showingKiosk) {
                await Guard(() => SetKioskSwitchAsync("Enabled", "The Pet Hatching Kiosk", _kioskEnabled.IsChecked == true));
            }
        };
        _kioskOwnServer.IsCheckedChanged += async (_, _) => {
            if (!_showingKiosk) {
                await Guard(() => SetKioskSwitchAsync("OwnServer", "The kiosk as its own program", _kioskOwnServer.IsChecked == true));
            }
        };
        _kioskIdleMinutes.ValueChanged += (_, _) => {
            if (!_showingKiosk && _kioskIdleMinutes.Value is { } value) {
                _ = Guard(() => SaveKioskNumberAsync("IdleMinutes", (int) value));
            }
        };
        _kioskMaxPets.ValueChanged += (_, _) => {
            if (!_showingKiosk && _kioskMaxPets.Value is { } value) {
                _ = Guard(() => SaveKioskNumberAsync("MaxPets", (int) value));
            }
        };
        _kioskPeppers.ValueChanged += (_, _) => {
            if (!_showingKiosk && _kioskPeppers.Value is { } value) {
                _ = Guard(() => SaveKioskNumberAsync("HatchPeppers", (int) value));
            }
        };

        LoadKioskSettings();
        _kioskTimer.Tick += (_, _) => RefreshKioskBot();
        _kioskTimer.Start();
        RefreshKioskBot();

        return Shut(Fold("paw", "Server", "Pet Hatching Kiosk bot",
            "The Pet Hatching Kiosk runs as a little program of its own next to the server, like the Bazaar bot, so the pets " +
            "put up at the kiosk never slow the server down. It wakes up when a player uses a kiosk, and sleeps when nobody " +
            "has for a while. Turned off, the kiosk is closed and players are told it is turned off. Changes count right away, no restart.",
            _kioskBotState,
            SwitchRow("Pet Hatching Kiosk", _kioskEnabled,
                "Off closes the kiosk: using one tells the player it is turned off. On wakes the kiosk bot when a player uses a kiosk."),
            SwitchRow("Sleep after (minutes)", _kioskIdleMinutes,
                "How long the kiosk bot keeps running after the last time someone used a kiosk, before it goes to sleep."),
            SwitchRow("Pets each player can put up", _kioskMaxPets,
                "How many pets one player can have at the kiosk at once. The game's own limit is five. Bots have no limit."),
            SwitchRow("Hatch Peppers per hatch", _kioskPeppers,
                "Hatch Peppers the owner of a kiosk pet gets each time another player hatches with it. 0 gives none."),
            SwitchRow("Run the kiosk as its own program", _kioskOwnServer,
                "Off has the server run the kiosk itself, as before."),
            Row(Action(Plain("Restart kiosk bot"), RestartKioskBotAsync), Action(Plain("Open its folder"), () => {
                Directory.CreateDirectory(KioskBotFolder);

                return Open(KioskBotFolder);
            }))));
    }

    /// <summary>The kiosk bot's log on the Logs tab: when it wakes and sleeps, and the pets put up, taken off and hatched with.</summary>
    private Control KioskBotLogCard() {
        _kioskLogBox = new TextBox {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = MONO, FontSize = 12.5,
            Height = 360, Text = "The kiosk bot writes here once a player uses a Pet Hatching Kiosk.",
        };

        return Shut(Fold("paw", "Logs", "Pet Hatching Kiosk bot log",
            "What the kiosk bot does: when it wakes and sleeps, and the pets players put up, take off and hatch with. Updated every 3 seconds.",
            LogWell(_kioskLogBox),
            Row(Action(Plain("Copy all"), () => CopyAsync(_kioskLogText)))));
    }

    private void LoadKioskSettings() {
        _showingKiosk = true;
        try {
            _kioskEnabled.IsChecked = !string.Equals(_manager.GetSetting("HatchKiosk", "Enabled"), "false", StringComparison.OrdinalIgnoreCase);
            _kioskIdleMinutes.Value = int.TryParse(_manager.GetSetting("HatchKiosk", "IdleMinutes"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var idle) ? idle : 5;
            _kioskMaxPets.Value = int.TryParse(_manager.GetSetting("HatchKiosk", "MaxPets"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var most) ? most : 5;
            _kioskPeppers.Value = int.TryParse(_manager.GetSetting("HatchKiosk", "HatchPeppers"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var peppers) ? peppers : 1;
            _kioskOwnServer.IsChecked = !string.Equals(_manager.GetSetting("HatchKiosk", "OwnServer"), "false", StringComparison.OrdinalIgnoreCase);
        }
        finally {
            _showingKiosk = false;
        }
    }

    private Task SetKioskSwitchAsync(string key, string name, bool on) {
        if (!_manager.HasSetting("HatchKiosk", key)) {
            throw new PanelException("This server's Imlight.ini has no [HatchKiosk] " + key + " yet. Click \"Update Ahziael\" once, then try again.");
        }

        return SetSwitchAsync("HatchKiosk", key, name, on);
    }

    private Task SaveKioskNumberAsync(string key, int value) {
        if (!_manager.HasSetting("HatchKiosk", key)) {
            throw new PanelException("This server's Imlight.ini has no [HatchKiosk] " + key + " yet. Click \"Update Ahziael\" once, then try again.");
        }

        if (_manager.GetSetting("HatchKiosk", key) == value.ToString(CultureInfo.InvariantCulture)) {
            return Task.CompletedTask;
        }

        _manager.SaveSettings([new Setting("HatchKiosk", key, value.ToString(CultureInfo.InvariantCulture), "")]);
        _settings = [];
        Say("Saved. The kiosk uses it right away.");

        return Task.CompletedTask;
    }

    // The bot's status file: its state, process id, how many pets are at the kiosk, when it wrote it, and its port. The
    // server writes "asleep", "off" or "inserver" while the bot is not running.
    private (string State, int Pid, int Count, DateTime Written)? ReadKioskStatus() {
        try {
            var path = Path.Combine(KioskBotFolder, "status.txt");
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

    private void RefreshKioskBot() {
        var cardShown = _kioskBotState.IsEffectivelyVisible;
        var logShown = _kioskLogBox?.IsEffectivelyVisible == true;
        if (!cardShown && !logShown) {
            return;
        }

        var status = ReadKioskStatus();
        var fresh = status is { } s && DateTime.UtcNow - s.Written < TimeSpan.FromSeconds(20);
        var pets = fresh ? $" {status!.Value.Count:N0} pets are up at the kiosk." : "";
        _kioskBotState.Text = !cardShown ? _kioskBotState.Text
            : !InstalledCommands.IsServerRunning(_manager.InstallFolder) ? "The server is not running, so the kiosk bot is not either."
            : _kioskEnabled.IsChecked != true ? "Off: the Pet Hatching Kiosk is closed, and players are told it is turned off."
            : _kioskOwnServer.IsChecked != true ? "On, run by the server itself (not as its own program)." + pets
            : fresh && status!.Value.State == "ready" ? "Running: a player used a kiosk a short while ago." + pets
            : fresh && status!.Value.State == "starting" ? "Starting: a player just used a kiosk."
            : fresh && status!.Value.State == "asleep" ? "Asleep: nobody used a kiosk lately. It wakes by itself when a player uses one." + pets
            : "Not running right now. It wakes when a player uses a Pet Hatching Kiosk.";
        _kioskBotState.Foreground = fresh && status!.Value.State == "ready" ? ManagerTheme.Accent("good") : MUTED;

        if (!logShown) {
            return;
        }

        var text = ReadKioskLog();
        if (text != _kioskLogText) {
            _kioskLogText = text;
            _kioskLogBox!.Text = string.Join('\n', text.Split('\n').TakeLast(KIOSK_LOG_SHOWN_LINES));
            _kioskLogBox.CaretIndex = _kioskLogBox.Text.Length;
        }
    }

    private string ReadKioskLog() {
        try {
            var file = Directory.Exists(KioskBotFolder)
                ? new DirectoryInfo(KioskBotFolder).EnumerateFiles("kiosk-bot*.txt").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
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
            return _kioskLogText;
        }
    }

    private async Task RestartKioskBotAsync() {
        if (!InstalledCommands.IsServerRunning(_manager.InstallFolder)) {
            throw new PanelException("Start the server first. It starts the kiosk bot by itself.");
        }

        if (ReadKioskStatus() is not { Pid: > 0 } status) {
            throw new PanelException("The kiosk bot is not running right now. It wakes by itself when a player uses a Pet Hatching Kiosk.");
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
        Say("The kiosk bot was stopped. The server starts it again within half a minute while players are using a kiosk.");
    }

}
