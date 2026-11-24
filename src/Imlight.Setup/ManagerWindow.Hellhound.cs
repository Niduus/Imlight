#if HELLHOUND
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// Hellhound's own pages (Niduus, 2026-11-22): Home with the kill switch, the locks, every bot with its switch and pause, and
/// the bot updates; Watch, one bot live (its map as it moves, its quest, and its hand in battle under the map: "battle hand
/// here", "Minimap to update in real time, not some choppy shit show"); and Hellhound, its own version and updates. The bot
/// pages themselves are Ahziael's, as they are.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string WATCH_TAB = "Watch";
    private const string HELLHOUND_TAB = "Hellhound";

    // The bot families and their Imlight.ini sections, in the side bar's order. On: what the server takes when Imlight.ini
    // has no Enabled line for it.
    private static readonly (string Name, string Section, string Folder, bool On)[] s_hhBots = [
        ("Ultimus", "Ultimus", "ultimus", false),
        ("TCO7", "TCO7", "tco7", false),
        ("Kitharas Family", "Kitharas", "kitharas", false),
        ("Zoom", "Zoom", "zoom", true),
        ("Savitar", "Savitar", "savitar", true),
        ("True Form", "TrueForm", "trueform", true),
        ("Quest Team", "QuestTeam", "quest-team", false),
        ("Street Bot Players", "BotPlayers", "bot-players", false),
    ];

    // The checks the bots write in logs\bots before they play.
    private static readonly (string Name, string File, string Missing)[] s_hhLocks = [
        ("Stays on this server", "home-guard.txt", "No check written yet."),
        ("Signed Hive Mind", "hive-mind-check.txt", "No check written yet."),
        ("Ahziael signal", "ahziael-signal.txt", "No check written yet."),
        ("Kill switch (what the bots last saw)", "killswitch.txt", "Not written yet."),
    ];

    private readonly DispatcherTimer _hhTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _hhFeedTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _hhGlide = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private bool _hhReading;

    // Home.
    private readonly TextBlock _hhKill = new() { TextWrapping = TextWrapping.Wrap, FontSize = ManagerTheme.FONT_HEADING };
    private readonly TextBlock _hhKillWhy = new() { TextWrapping = TextWrapping.Wrap, Foreground = MUTED };
    private readonly StackPanel _hhLocks = new() { Spacing = 4 };
    private readonly StackPanel _hhBotRows = new() { Spacing = 10 };
    private readonly TextBlock _hhUpdates = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _hhSavitar = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _hhSavitarBox = new() {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56, MaxHeight = 160, MinWidth = 420,
        Watermark = "Quest IDs or quest names, one a line (AZ-CLOUD-C02-003, or a quest's name as the game shows it)",
    };
    private readonly Dictionary<string, ToggleSwitch> _hhSwitches = [];
    private string _hhRowsKey = "";
    private bool _hhPainting;
    private bool _hhKillOn;

    // Watch.
    private readonly ComboBox _watchPicker = new() { MinWidth = 320 };
    private readonly TextBlock _watchNote = new() { TextWrapping = TextWrapping.Wrap, Foreground = MUTED, MaxWidth = 620 };
    private readonly ContentControl _watchMap = new();
    private readonly ContentControl _watchHand = new();
    private readonly ContentControl _watchQuest = new();
    private MinimapView? _watchView;
    private List<OverlayUltimusData.Someone> _watchList = [];
    private string _watchPick = LoadWatchPick();
    private int _watchSeen = -1;
    private bool _watchFilling;

    // Hellhound.
    private readonly TextBlock _hhSelfState = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _hhKeyBox = new() { PasswordChar = '•', Watermark = "Paste the key here (it starts with github_pat_)", Width = 420 };

    private static string WatchPickFile => Path.Combine(HellhoundStart.DataFolder, "watching.txt");

    private static string LoadWatchPick() {
        try {
            return File.Exists(WatchPickFile) ? File.ReadAllText(WatchPickFile).Trim() : "";
        }
        catch (Exception) {
            return "";
        }
    }

    // ---------------------------------------------------------------------
    // The window
    // ---------------------------------------------------------------------

    private Control HellhoundHeader() {
        var brand = new StackPanel {
            Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0),
            Children = {
                HellhoundLogo(34),
                new StackPanel {
                    VerticalAlignment = VerticalAlignment.Center, Spacing = 2,
                    Children = {
                        new TextBlock { Text = "Hellhound", FontSize = 19, FontWeight = FontWeight.Bold, Foreground = ManagerTheme.Gradient() },
                        new TextBlock { Text = $"The bots' own app  ·  {HellhoundUpdate.CurrentVersion}", FontSize = ManagerTheme.FONT_SMALL, Foreground = MUTED },
                    },
                },
            },
        };
        var bar = ManagerTheme.GlassPanel(new DockPanel {
            Children = {
                WithDock(_statusBadge, Dock.Right),
                WithDock(JumpBox(), Dock.Right),
                WithDock(brand, Dock.Left),
                new Panel(),
            },
        }, 18);
        bar.Padding = new Thickness(10, 6);
        bar.Margin = new Thickness(12, 10, 12, 0);

        return bar;
    }

    private static Control HellhoundLogo(double size) {
        try {
            using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Hellhound/Assets/hellhound.png"));
            return new Image { Source = new Avalonia.Media.Imaging.Bitmap(stream), Width = size, Height = size };
        }
        catch (Exception) {
            return ManagerTheme.Tile("users", ManagerTheme.Gradient(), size);
        }
    }

    private void HellhoundOpened() {
        try {
            using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Hellhound/Assets/hellhound.png"));
            Icon = new WindowIcon(stream);
        }
        catch (Exception) {
            // Ahziael's icon stays.
        }

        // The bots' maps, quests and battles come from this PC's own server, on its game port.
        var port = int.TryParse(_manager.GetSetting("Game Server", "GameServerPort"), out var set) && set is > 0 and < 65536 ? set : 12333;
        OverlayHost.Link.Here(port);
        _hhFeedTimer.Tick += (_, _) => FeedTick();
        _hhFeedTimer.Start();
        _hhGlide.Tick += (_, _) => _watchView?.InvalidateVisual();
        _hhTimer.Tick += async (_, _) => await Guard(RefreshHellhoundAsync);
        _hhTimer.Start();
        _ = Guard(RefreshHellhoundAsync);
        HellhoundUpdate.Start();
    }

    private void HellhoundClosed() {
        _hhTimer.Stop();
        _hhFeedTimer.Stop();
        _hhGlide.Stop();
        OverlayHost.Link.Stop();
    }

    // Four times a second: the feeds the pages show are asked for (a page asks for its feed while it shows), and the Watch
    // page is filled again when its bot's news changed. The map itself is drawn about 60 times a second while Watch shows,
    // gliding between the server's answers.
    private void FeedTick() {
        var watching = ShownPage() == WATCH_TAB;
        if (watching) {
            OverlayUltimusData.PageWants(OverlayUltimusData.ANYONE_KEY, "");
            // A bot page may have pointed the feed at its own bot: back to the one picked here.
            if (_watchPick.Length > 0 && OverlayUltimusData.AnyoneWatched?.Id != _watchPick && _watchList.Any(p => p.Id == _watchPick)) {
                OverlayUltimusData.WatchAnyone(_watchPick);
            }
        }

        foreach (var key in OverlayUltimusData.Keys) {
            OverlayUltimusData.SetWanted(key, OverlayUltimusData.PageWanted(key));
        }

        OverlayMinimapData.Tick();
        _hhGlide.IsEnabled = watching;
        if (watching) {
            FillWatch();
        }
    }

    // ---------------------------------------------------------------------
    // Home: the kill switch, the locks, every bot, the bot updates
    // ---------------------------------------------------------------------

    private Control HellhoundHome() {
        var check = Action(Plain("Look for a bot update now"), () => Task.FromResult(Say(CheckBotUpdateNow())));

        return Page(
            Fold("lock", "Bots", "Kill switch",
                "Your switch in the private bots repo. When it says on = true, no bot plays. Hellhound reads it exactly as the bots do.",
                _hhKill, _hhKillWhy),
            Fold("users", "Bots", "Every bot",
                "Turn each bot on or off, and pause one where it stands (it stays logged in). Each change applies within a few seconds.",
                _hhBotRows),
            Fold("flag", "Bots", "Savitar's quest tests",
                "Type quest IDs or quest names, one a line, and add them to Savitar's list. He tests the first one: he is given it, " +
                "does nothing else until it is finished (a step that never finishes is marked broken and he keeps at it until it is " +
                "fixed), then goes on to the next. Savitar must be switched on above.",
                _hhSavitarBox,
                WithMargin(Action(Primary("Add to Savitar's list"), AddSavitarTestsAsync), new Thickness(0, 8, 0, 8)),
                _hhSavitar),
            Fold("shield", "Bots", "What the bots last checked", "The checks the bots make before they play, as they last wrote them.", _hhLocks),
            Fold("download", "Bots", "Bot updates", "Which bot update the server runs, and its last look for a newer one.", _hhUpdates,
                WithMargin(check, new Thickness(0, 8, 0, 0))));
    }

    private sealed record SeatRow(string Id, string Name, string Doing, string Paused, string Folder);

    private sealed record BotRow(string Name, string Section, bool On, string Count, List<SeatRow> Seats);

    private sealed record HomeRead(bool Stop, string Why, List<(string Name, string Text, bool Written)> Locks, List<BotRow> Bots,
                                   string Updates, string Savitar);

    private async Task RefreshHellhoundAsync() {
        if (_hhReading) {
            return;
        }

        _hhReading = true;
        try {
            var folder = _manager.ServerFolder;
            var config = _manager.ConfigFile;
            var read = await Task.Run(() => ReadHome(folder, config));
            PaintHome(read);
            if (ShownPage() == HELLHOUND_TAB) {
                PaintSelf();
            }
        }
        finally {
            _hhReading = false;
        }
    }

    private static HomeRead ReadHome(string folder, string config) {
        var (stop, why) = HellhoundKillSwitch.Read(folder);
        var logs = Path.Combine(folder, "logs");
        var locks = s_hhLocks.Select(l => LastLine(Path.Combine(logs, "bots", l.File)) is { } line ? (l.Name, line, true) : (l.Name, l.Missing, false)).ToList();
        var ini = IniFile.ReadAll(config);
        string? Get(string section, string key) => ini.TryGetValue((section, key), out var value) ? value : null;
        var bots = new List<BotRow>();
        foreach (var (name, section, botFolder, onByDefault) in s_hhBots) {
            var on = Get(section, "Enabled") is { Length: > 0 } text ? text.Equals("true", StringComparison.OrdinalIgnoreCase) : onByDefault;
            var root = Path.Combine(logs, botFolder);
            var seats = Seats(root).Select(seat => {
                var status = ReadJson(Path.Combine(seat, "status.json"));
                var doing = Text(status?["doing"]) is { Length: > 0 } d ? d : Text(status?["progress"]);
                var quest = Text(status?["quest"]);
                var line = string.Join("  ·  ", new[] { quest, doing }.Where(t => t.Length > 0));
                if (FirstLine(Path.Combine(seat, "handoff.txt")) is { } handoff) {
                    line = "Handed over to Zoom: " + handoff;
                }

                return new SeatRow(Path.GetRelativePath(logs, seat).Replace('\\', '/'), seat == root ? name : Path.GetFileName(seat),
                    line, FirstLine(Path.Combine(seat, "paused.txt")) ?? "", seat);
            }).ToList();
            bots.Add(new BotRow(name, section, on, Get(section, "Count") ?? "", seats));
        }

        var state = ReadJson(Path.Combine(folder, "bots", "state.json"));
        var running = Text(state?["running"]);
        var updates = $"Running: {(running.Length > 0 ? running : "the server's own bots")}.  " +
                      $"Live bot updates: {(Get("Bots", "LiveUpdates") is { } live && live.Equals("false", StringComparison.OrdinalIgnoreCase) ? "off" : "on")}.";
        if (Text(state?["lastCheck"]) is { Length: > 0 } last) {
            updates += $"\nLast look: {last}, {Text(state?["lastCheckResult"])}.";
        }

        if (Text(state?["problem"]) is { Length: > 0 } problem) {
            updates += "\nProblem: " + problem;
        }

        return new HomeRead(stop, why, locks, bots, updates, ReadSavitarTests(Path.Combine(logs, "savitar")));
    }

    private void PaintHome(HomeRead read) {
        _hhKillOn = read.Stop;
        _hhKill.Text = read.Stop ? "ON: no bot may play." : "Off: the bots may play.";
        _hhKill.Foreground = read.Stop ? BAD : GOOD;
        _hhKillWhy.Text = "Hellhound read: " + read.Why + ".";

        _hhLocks.Children.Clear();
        foreach (var (name, text, written) in read.Locks) {
            _hhLocks.Children.Add(new DockPanel {
                Children = {
                    WithDock(new TextBlock { Text = name, Width = 260, FontWeight = FontWeight.SemiBold }, Dock.Left),
                    new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = written ? null : MUTED },
                },
            });
        }

        _hhUpdates.Text = read.Updates;
        _hhSavitar.Text = read.Savitar;

        // The rows are built again only when the bots or their seats change, so a switch being clicked isn't swept away.
        var key = string.Join("|", read.Bots.Select(b => b.Section + ":" + string.Join(",", b.Seats.Select(s => s.Id))));
        if (key != _hhRowsKey) {
            _hhRowsKey = key;
            _hhBotRows.Children.Clear();
            _hhSwitches.Clear();
            foreach (var bot in read.Bots) {
                _hhBotRows.Children.Add(BotHomeRow(bot));
            }
        }

        _hhPainting = true;
        try {
            foreach (var bot in read.Bots) {
                if (_hhSwitches.TryGetValue(bot.Section, out var toggle)) {
                    toggle.IsChecked = bot.On;
                }

                foreach (var seat in bot.Seats) {
                    if (_hhBotRows.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Tag as string == "doing:" + seat.Id) is { } doing) {
                        doing.Text = seat.Paused.Length > 0 ? "Paused" : seat.Doing.Length > 0 ? seat.Doing : "No news yet.";
                    }

                    if (_hhBotRows.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => b.Tag as string == "pause:" + seat.Id) is { } pause) {
                        pause.Content = ManagerTheme.ButtonContent(seat.Paused.Length > 0 ? "Resume" : "Pause");
                    }
                }
            }
        }
        finally {
            _hhPainting = false;
        }
    }

    private Control BotHomeRow(BotRow bot) {
        var toggle = new ToggleSwitch { OnContent = "On", OffContent = "Off", IsChecked = bot.On };
        _hhSwitches[bot.Section] = toggle;
        toggle.IsCheckedChanged += (_, _) => {
            if (!_hhPainting) {
                _ = Guard(() => SwitchBotAsync(bot, toggle));
            }
        };
        var seats = new StackPanel { Spacing = 2, Margin = new Thickness(16, 2, 0, 0) };
        foreach (var seat in bot.Seats) {
            var pause = Plain(seat.Paused.Length > 0 ? "Resume" : "Pause");
            pause.Tag = "pause:" + seat.Id;
            pause.Padding = new Thickness(10, 2);
            pause.MinHeight = 0;
            var picked = seat;
            pause.Click += (_, _) => Say(PauseSeat(picked));
            seats.Children.Add(new DockPanel {
                Children = {
                    WithDock(pause, Dock.Right),
                    WithDock(new TextBlock { Text = seat.Name, Width = 150, VerticalAlignment = VerticalAlignment.Center }, Dock.Left),
                    new TextBlock {
                        Tag = "doing:" + seat.Id, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = MUTED, VerticalAlignment = VerticalAlignment.Center,
                        Text = seat.Paused.Length > 0 ? "Paused" : seat.Doing.Length > 0 ? seat.Doing : "No news yet.",
                    },
                },
            });
        }

        return new StackPanel {
            Children = {
                new DockPanel {
                    Children = {
                        WithDock(toggle, Dock.Right),
                        new TextBlock {
                            Text = bot.Name + (bot.Count.Length > 0 ? $"  ·  {bot.Count} play" : ""), FontWeight = FontWeight.SemiBold,
                            FontSize = ManagerTheme.FONT_HEADING, VerticalAlignment = VerticalAlignment.Center,
                        },
                    },
                },
                seats,
            },
        };
    }

    private async Task SwitchBotAsync(BotRow bot, ToggleSwitch toggle) {
        var on = toggle.IsChecked == true;
        if (on && _hhKillOn) {
            _hhPainting = true;
            toggle.IsChecked = false;
            _hhPainting = false;
            Say($"{bot.Name} stays off: the kill switch says the bots must stop.", isError: true);

            return;
        }

        await Task.Run(() => _manager.SaveSettings([new Setting(bot.Section, "Enabled", on ? "true" : "false", "")]));
        // Every change Hellhound makes is written down (2026-11-22: nobody could tell who switched the bots off).
        HellhoundStart.Log($"Switched {bot.Name} ([{bot.Section}] Enabled) {(on ? "on" : "off")}.");
        Say(on ? $"{bot.Name} is on. The server starts it within a few seconds." : $"{bot.Name} is off. The server logs it out within a few seconds.");
    }

    private static string PauseSeat(SeatRow seat) {
        var file = Path.Combine(seat.Folder, "paused.txt");
        try {
            if (File.Exists(file)) {
                File.Delete(file);
                HellhoundStart.Log($"Resumed {seat.Name}.");

                return $"Resumed {seat.Name}. It goes on with its quest.";
            }

            File.WriteAllText(file, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            HellhoundStart.Log($"Paused {seat.Name}.");

            return $"Paused {seat.Name}. It stands where it is and stays logged in (a battle it is in still finishes).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            throw new PanelException($"Could not pause {seat.Name}: {ex.Message}");
        }
    }

    // Savitar's test list and results (the bots' SavitarTests reads and writes the same files).
    private const string SAVITAR_TESTS = "test-quests.txt", SAVITAR_RESULTS = "test-results.txt", SAVITAR_NOW = "testing-now.txt";

    private static string ReadSavitarTests(string folder) {
        var lines = new List<string>();
        var now = FirstLine(Path.Combine(folder, SAVITAR_NOW))?.Split('|');
        lines.Add(now is { Length: >= 1 } && now[0].Length > 0
            ? $"Testing now: {now[0]}{(now.Length >= 2 ? $" (since {now[1]})" : "")}."
            : "Testing now: nothing.");
        var list = ReadLines(Path.Combine(folder, SAVITAR_TESTS)).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        lines.Add(list.Count == 0 ? "His list is empty." : $"His list ({list.Count}): " + string.Join(", ", list.Take(12)) + (list.Count > 12 ? ", ..." : "") + ".");
        var results = ReadLines(Path.Combine(folder, SAVITAR_RESULTS)).Where(l => l.Length > 0).TakeLast(6).ToList();
        if (results.Count > 0) {
            lines.Add("Last results:");
            lines.AddRange(results.AsEnumerable().Reverse().Select(r => "  " + r));
        }

        return string.Join("\n", lines);
    }

    private static List<string> ReadLines(string path) {
        try {
            return File.Exists(path) ? [.. File.ReadAllLines(path).Select(l => l.Trim())] : [];
        }
        catch (Exception) {
            return [];
        }
    }

    private Task AddSavitarTestsAsync() {
        var asked = (_hhSavitarBox.Text ?? "").Split('\n').Select(l => l.Trim().Trim('"')).Where(l => l.Length > 0).ToList();
        if (asked.Count == 0) {
            Say("Type a quest ID or a quest name first, one a line.", isError: true);
            return Task.CompletedTask;
        }

        var folder = Path.Combine(_manager.ServerFolder, "logs", "savitar");
        try {
            Directory.CreateDirectory(folder);
            File.AppendAllLines(Path.Combine(folder, SAVITAR_TESTS), asked);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            throw new PanelException($"Could not add to Savitar's list: {ex.Message}");
        }

        HellhoundStart.Log($"Added to Savitar's quest tests: {string.Join(", ", asked)}.");
        _hhSavitarBox.Text = "";
        Say($"Added {asked.Count} quest{(asked.Count == 1 ? "" : "s")} to Savitar's list. He tests them in order, one at a time, each until it is finished.");
        return Task.CompletedTask;
    }

    private string CheckBotUpdateNow() {
        if (!_manager.Status().Running) {
            return "The server isn't running, so it can't look for a bot update now. It looks by itself when it starts.";
        }

        var folder = Path.Combine(_manager.ServerFolder, "bots");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "check-now.txt"), DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
        HellhoundStart.Log("Asked the server to look for a bot update now.");

        return "The server looks for a bot update within a few seconds. If there's a newer one, the bots log out and come back with it.";
    }

    // ---------------------------------------------------------------------
    // Watch: one bot live
    // ---------------------------------------------------------------------

    private Control WatchTab() {
        _watchPicker.SelectionChanged += (_, _) => {
            if (_watchFilling || _watchPicker.SelectedIndex < 0 || _watchPicker.SelectedIndex >= _watchList.Count) {
                return;
            }

            var pick = _watchList[_watchPicker.SelectedIndex];
            _watchPick = pick.Id;
            try {
                Directory.CreateDirectory(HellhoundStart.DataFolder);
                File.WriteAllText(WatchPickFile, pick.Id);
            }
            catch (Exception) {
                // Only a convenience: next time it starts on the first bot.
            }

            OverlayUltimusData.WatchAnyone(pick.Id);
            _watchSeen = -1;
        };

        var mapLook = new OverlayElement {
            Kind = OverlayElement.ULTIMUS_MAP, Watch = OverlayElement.WATCH_ANYONE, Width = 520, Height = 520, BorderColor = "#D0E07CFF",
            BorderWidth = 2, CornerRadius = 16,
            Background = new OverlayPaint { Kind = OverlayPaint.RADIAL, Color1 = "#E0302040", Color2 = "#E0100A1C" },
            Map = new OverlayMap { Zoom = 2600, Round = false, Turn = false, IconSize = 11, NameSize = 12 },
        };
        _watchView = new MinimapView(mapLook, null, OverlayUltimusData.ANYONE_KEY);
        _watchMap.Content = _watchView;

        return WidePage(
            Fold("eye", "Bots", "Watch a bot",
                "Pick a bot. Its map moves with it as it plays, its battle hand shows under the map while it fights, and its quest is beside it. " +
                "It stays on the bot you pick.",
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { new TextBlock { Text = "Watching", VerticalAlignment = VerticalAlignment.Center }, _watchPicker } },
                _watchNote,
                new WrapPanel {
                    Children = {
                        new StackPanel { Spacing = 10, Margin = new Thickness(0, 6, 18, 0), Children = { _watchMap, _watchHand } },
                        new Border { Width = 340, Margin = new Thickness(0, 6, 0, 0), Child = _watchQuest },
                    },
                }));
    }

    private static readonly OverlayElement s_watchQuestLook = new() {
        Kind = OverlayElement.ULTIMUS_QUEST, Watch = OverlayElement.WATCH_ANYONE, Width = 340, Size = 13, Padding = 12, BorderColor = "#A0E07CFF",
        BorderWidth = 1, CornerRadius = 12, Background = new OverlayPaint { Kind = OverlayPaint.SOLID, Color1 = "#E0120C22" },
    };

    private static readonly OverlayElement s_watchHandLook = new() {
        Kind = OverlayElement.ULTIMUS_SPELLS, Watch = OverlayElement.WATCH_ANYONE, Width = 520, Size = 13, Padding = 12, BorderColor = "#A0FFD24A",
        BorderWidth = 1, CornerRadius = 12, Background = new OverlayPaint { Kind = OverlayPaint.SOLID, Color1 = "#E00A0E1C" },
    };

    private void FillWatch() {
        // The picker: bots first (as the server lists them), then players.
        var everyone = OverlayUltimusData.Everyone.OrderBy(p => p.Kind == "Player" ? 1 : 0).ToList();
        if (everyone.Count > 0 && !everyone.Select(p => p.Id).SequenceEqual(_watchList.Select(p => p.Id))) {
            _watchFilling = true;
            try {
                _watchList = everyone;
                _watchPicker.ItemsSource = everyone.Select(p => $"{p.Name}  ({p.Kind}{(p.School.Length > 0 ? ", " + p.School : "")})").ToList();
                var at = everyone.FindIndex(p => p.Id == _watchPick);
                if (at < 0 && _watchPick.Length == 0 && OverlayUltimusData.AnyoneWatched is { } now) {
                    at = everyone.FindIndex(p => p.Id == now.Id);
                }

                _watchPicker.SelectedIndex = at;
            }
            finally {
                _watchFilling = false;
            }
        }

        var version = OverlayUltimusData.Version;
        if (version == _watchSeen) {
            return;
        }

        _watchSeen = version;
        var info = OverlayUltimusData.For(1, OverlayUltimusData.ANYONE_KEY);
        _watchNote.Text = !OverlayHost.Link.Connected
            ? "Waiting for the server on this PC. Start it in Ahziael; Hellhound links by itself."
            : _watchList.Count == 0 ? "Asking the server who is playing…"
            : _watchPick.Length > 0 && _watchList.All(p => p.Id != _watchPick) ? "The bot you picked isn't in the game right now. It shows again when it comes back."
            : info is { Note.Length: > 0 } ? info.Note : "";
        _watchQuest.Content = info is null ? null : UltimusViews.QuestBody(s_watchQuestLook, info, OverlayUltimusData.ANYONE_KEY, names: false);
        _watchHand.Content = UltimusViews.Spells(s_watchHandLook, 1, true) is { } hand
            ? OverlayRenderer.Framed(s_watchHandLook, hand)
            : new TextBlock { Text = info is null ? "" : $"{info.Name} isn't in a battle right now. Its hand shows here while it fights.", Foreground = MUTED };
        if (info is not null && _watchQuest.Content is Control quest) {
            _watchQuest.Content = OverlayRenderer.Framed(s_watchQuestLook, quest);
        }
    }

    // ---------------------------------------------------------------------
    // Hellhound: its version and its own updates
    // ---------------------------------------------------------------------

    private Control HellhoundTab() {
        var save = Action(Primary("Save key"), async () => {
            var key = _hhKeyBox.Text ?? "";
            var said = await Task.Run(() => HellhoundUpdate.SaveKey(key));
            _hhKeyBox.Text = "";
            Say(said, isError: !said.StartsWith("Key saved", StringComparison.Ordinal));
            PaintSelf();
        });
        var look = Action(Plain("Look for a Hellhound update now"), async () => {
            await HellhoundUpdate.CheckAsync();
            PaintSelf();
        });
        PaintSelf();

        return Page(
            Fold("download", "Bots", "Hellhound updates",
                "Hellhound looks for a newer copy of itself in your private bots repo when it opens and every 30 minutes, and puts it in place by itself.",
                _hhSelfState, WithMargin(look, new Thickness(0, 8, 0, 0))),
            Fold("key", "Bots", "GitHub key (once)",
                "The bots repo is private, so Hellhound needs a read-only key to see its updates. It is kept only on this PC (in %LOCALAPPDATA%\\Hellhound) and sent only to GitHub.",
                new TextBlock {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "1. Open github.com/settings/personal-access-tokens/new (signed in as you).\n" +
                           "2. Token name: Hellhound. Expiration: No expiration.\n" +
                           "3. Repository access: Only select repositories, then pick ahziael-bots.\n" +
                           "4. Permissions: Repository permissions, Contents: Read-only.\n" +
                           "5. Click Generate token, copy it, paste it below and click Save key.",
                },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Children = { _hhKeyBox, save } }));
    }

    private void PaintSelf() {
        _hhSelfState.Text = $"This is Hellhound {HellhoundUpdate.CurrentVersion}." +
                            (HellhoundUpdate.Latest.Length > 0 ? $" Newest in the bots repo: {HellhoundUpdate.Latest}." : "") +
                            "\n" + HellhoundUpdate.State +
                            (HellhoundUpdate.HasKey ? "\nThe GitHub key is saved." : "");
    }

    // ---------------------------------------------------------------------
    // Reading the bots' files (the server may be writing them at the same moment)
    // ---------------------------------------------------------------------

    /// <summary>A bot's seats: its own folder, then a family member's folders (logs\tco7\tulkar), each with a status or pause file.</summary>
    private static List<string> Seats(string folder) {
        var seats = new List<string>();
        if (!Directory.Exists(folder)) {
            return seats;
        }

        var all = new List<string> { folder };
        try {
            all.AddRange(Directory.GetDirectories(folder).Order(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        }

        seats.AddRange(all.Where(seat => File.Exists(Path.Combine(seat, "status.json")) || File.Exists(Path.Combine(seat, "paused.txt"))));

        return seats;
    }

    private static string Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node?.ToJsonString() ?? "";

    private static JsonNode? ReadJson(string path) {
        try {
            return File.Exists(path) ? JsonNode.Parse(Shared(path)) : null;
        }
        catch (Exception) {
            return null;
        }
    }

    private static string? FirstLine(string path) => Lines(path)?.FirstOrDefault(l => l.Trim().Length > 0)?.Trim();

    private static string? LastLine(string path) => Lines(path)?.LastOrDefault(l => l.Trim().Length > 0)?.Trim();

    private static string[]? Lines(string path) {
        try {
            return File.Exists(path) ? Shared(path).Split('\n') : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    private static string Shared(string path) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

}
#endif
