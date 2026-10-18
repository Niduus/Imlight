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
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The app's window: Play (this server, other private servers), Lookup (every command and ID), Server
/// (start, stop, randomizer, log), Settings, Accounts, Game Files and Tools.
/// </summary>
internal sealed class ManagerWindow : Window {

    private static readonly IBrush BACKGROUND = new SolidColorBrush(Color.FromRgb(24, 27, 33));
    private static readonly IBrush CARD = new SolidColorBrush(Color.FromArgb(20, 128, 128, 128));
    private static readonly IBrush LINE = new SolidColorBrush(Color.FromArgb(50, 128, 128, 128));
    private static readonly IBrush GOOD = new SolidColorBrush(Color.FromRgb(46, 160, 92));
    private static readonly IBrush WARN = new SolidColorBrush(Color.FromRgb(214, 150, 40));
    private static readonly IBrush BAD = new SolidColorBrush(Color.FromRgb(220, 70, 60));
    private static readonly IBrush MUTED = new SolidColorBrush(Color.FromArgb(170, 128, 128, 128));
    private static readonly IBrush LOG_BACK = new SolidColorBrush(Color.FromRgb(17, 21, 28));
    private static readonly IBrush LOG_TEXT = new SolidColorBrush(Color.FromRgb(216, 222, 233));
    private static readonly FontFamily MONO = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");

    private readonly ServerManager _manager;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    // Header and message bar.
    private readonly Border _dot = new() { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = MUTED };
    private readonly TextBlock _statusText = new() { Text = "Checking…", Foreground = MUTED, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border _messageBar = new() { Padding = new Thickness(16, 10), IsVisible = false };

    // Play.
    private readonly Button _playButton = Primary("Play Wizard101");
    private readonly TextBlock _playHelp = Sub("Starts Wizard101 connected to the server on this computer. If the server is off, it starts it first.");
    private readonly StackPanel _serverRows = new() { Spacing = 6 };
    private readonly TextBox _newServerName = new() { Watermark = "Name (optional)", Width = 160 };
    private readonly TextBox _newServerHost = new() { Watermark = "Address, such as play.example.com", MinWidth = 260 };
    private readonly TextBox _newServerPort = new() { Watermark = "Port", Width = 90 };
    private readonly SelectableTextBlock _friendAddress = new() { Text = "Finding your address…", FontSize = 16, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _friendResult = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private string? _publicIp;

    // Server.
    private readonly Button _startButton = Primary("Start server");
    private readonly Button _restartButton = Plain("Restart");
    private readonly Button _stopButton = Plain("Stop server");
    private readonly Border _randomizerCard;
    private readonly ToggleSwitch _randomizer = new() { OnContent = "On", OffContent = "Off" };
    private readonly CheckBox _hideDebug = new() { Content = "Hide debug lines", IsChecked = true };
    private readonly CheckBox _hideWarnings = new() { Content = "Hide warnings" };
    private readonly CheckBox _follow = new() { Content = "Follow", IsChecked = true };
    private readonly CheckBox _pauseLog = new() { Content = "Pause" };
    private List<string> _shownLog = [];
    private readonly StackPanel _logLines = new();
    private readonly ScrollViewer _logScroll;
    private readonly Border _dieDeleteCard;
    private readonly ToggleSwitch _dieDelete = new() { OnContent = "On", OffContent = "Off" };
    private bool _updatingRandomizer;
    private readonly Border _trainersCard;
    private readonly List<(string Key, string Name, ToggleSwitch Switch)> _trainerSwitches = [
        ("RandomizeSpellRequirements", "Random spell requirements", new() { OnContent = "On", OffContent = "Off" }),
        ("NoSpellRequirements", "No spell requirements", new() { OnContent = "On", OffContent = "Off" }),
        ("NoLevelRequirements", "No level requirements", new() { OnContent = "On", OffContent = "Off" }),
        ("FreeTraining", "Free training (no training points)", new() { OnContent = "On", OffContent = "Off" }),
        ("TrainingPointPerLevel", "Training points for every level gained", new() { OnContent = "On", OffContent = "Off" }),
        ("PlayersCanGainTrainingPoints", "Players can type .gaintp <amount> to get training points", new() { OnContent = "On", OffContent = "Off" }),
    ];
    private readonly Border _battleCard;
    private readonly List<(string Section, string Key, string Name, ToggleSwitch Switch)> _battleSwitches = [
        ("Battle", "ManaOnFizzle", "Reduce mana even if fizzle", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "UnlimitedMana", "Unlimited mana (spells cost none, the Commons refills it)", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "BattleWisps", "Battles drop health, mana and gold wisps", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "FullPipConserve", "100% pip conserve (a half-used power pip always comes back)", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "NoPipsFromDaybreakerNightbringer", "Daybreaker and Nightbringer do not add pips to card", new() { OnContent = "On", OffContent = "Off" }),
        ("Archmastery", "Enabled", "Archmastery (Power Orb turns power pips into school pips from level 50)", new() { OnContent = "On", OffContent = "Off" }),
        ("Archmastery", "AllLevels", "Archmastery at every level, not just 50 and up", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "SchoolPips", "School pips", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "AnySchoolPips", "School pips count as any school pips", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "GainSchoolPips", "Gain school pips without stats (chance under Settings, Battle)", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "RandomSchoolPips", "Random school pips", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "ShadowPips", "Shadow pips (min, max and chance under Settings, Battle)", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "NaturalShadowPips", "Gain shadow pips without stats", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "CreatureShadowPips", "Creatures can get shadow pips", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "RandomCreatureShadowPips", "Creatures get shadow magic only by chance (chance under Settings, Battle)", new() { OnContent = "On", OffContent = "Off" }),
        ("Randomizer", "Tame", "Randomizer: tame mode (creatures, their decks and pips stay normal so quests can be done)", new() { OnContent = "On", OffContent = "Off" }),
        ("Randomizer", "RandomizeShadowPips", "Randomizer: random shadow pip limits and creature shadow magic", new() { OnContent = "On", OffContent = "Off" }),
        ("Character", "RefillCrowns", "Crowns refill to the starting amount (amount under Settings, Character)", new() { OnContent = "On", OffContent = "Off" }),
        ("Character", "MemberBenefits", "Every wizard is a member with all member benefits", new() { OnContent = "On", OffContent = "Off" }),
        ("Login Server", "DisableIdleKick", "Disable idle kick (never disconnect idle players)", new() { OnContent = "On", OffContent = "Off" }),
        ("World", "OpenAllDoors", "Open every door (no quest or level needed, such as Nightside)", new() { OnContent = "On", OffContent = "Off" }),
    ];
    private readonly NumericUpDown _pointsPerLevel = new() { Minimum = 0, Maximum = 100, Increment = 1, FormatString = "0", Width = 130 };

    // Settings.
    private readonly TextBox _settingSearch = new() { Watermark = "Search settings" };
    private readonly StackPanel _settingsList = new() { Spacing = 8 };
    private readonly Dictionary<(string Section, string Key), Setting> _pending = [];
    private readonly Border _saveBar = new() { IsVisible = false, Padding = new Thickness(16, 10) };
    private readonly TextBlock _saveCount = new() { VerticalAlignment = VerticalAlignment.Center };
    private List<(string Section, List<Setting> Settings)> _settings = [];

    // Accounts.
    private readonly TextBox _newUsername = new() { Watermark = "Username", Width = 180 };
    private readonly TextBox _newPassword = new() { Watermark = "Password", PasswordChar = '•', Width = 180 };
    private readonly ComboBox _newLevel = new() { Width = 180 };
    private readonly StackPanel _accountRows = new() { Spacing = 6 };
    private readonly TextBlock _accountsNote = Sub("");

    // Lookup.
    private readonly TextBox _lookupSearch = new() { Watermark = "Search by name, ID or what it does, such as gold, Fire Cat or 1317127" };
    private readonly ComboBox _lookupKind = new() { ItemsSource = Lookup.KINDS, SelectedIndex = 0, Width = 150 };
    private readonly TextBlock _lookupCount = Sub("");
    private readonly ListBox _lookupResults = new() { Background = Brushes.Transparent };
    private readonly DispatcherTimer _lookupDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Lookup? _lookup;
    private DateTime _lookupLoadedFrom;

    // Game files.
    private readonly GameFiles _gameFiles;
    private readonly TextBlock _gameFilesProgress = Sub("");
    private readonly Button _copyGameFiles = Primary("Copy my game files");
    private readonly Button _readGameFiles = Primary("Read my copy");
    private readonly Button _cancelGameFiles = Plain("Stop");
    private System.Threading.CancellationTokenSource? _gameFilesCancel;

    internal ManagerWindow(ServerManager manager) {
        _manager = manager;
        _gameFiles = new GameFiles(manager.InstallFolder);
        Title = Edition.NAME;
        Width = 1120;
        Height = 820;
        Background = BACKGROUND;
        MinWidth = 640;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _randomizerCard = Card(new DockPanel {
            Children = {
                WithDock(_randomizer, Dock.Right),
                new StackPanel {
                    Children = {
                        Heading("Randomizer mode"),
                        Sub("Shuffles schools, spells, drops and more for every character, kept balanced. Fine-tune it under Settings, Randomizer."),
                    },
                },
            },
        });
        _randomizerCard.IsVisible = false;
        _dieDeleteCard = Card(new DockPanel {
            Children = {
                WithDock(_dieDelete, Dock.Right),
                new StackPanel {
                    Children = {
                        Heading("Die = Delete mode"),
                        Sub("If a wizard dies in battle and is still dead when the battle ends, that wizard is deleted for good. " +
                            "Fleeing doesn't count, and neither does the tutorial battle."),
                    },
                },
            },
        });
        _dieDeleteCard.IsVisible = false;
        var trainerRows = new StackPanel {
            Spacing = 6,
            Children = {
                Heading("Spell trainers"),
                Sub("Change what trainers ask for before they teach a spell. These work with or without randomizer mode. " +
                    "Random spell requirements makes each spell that needs another spell first need a different one from the same trainer instead."),
            },
        };
        foreach (var (key, name, toggle) in _trainerSwitches) {
            trainerRows.Children.Add(new DockPanel {
                Children = {
                    WithDock(toggle, Dock.Right),
                    new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center },
                },
            });
        }

        trainerRows.Children.Add(new DockPanel {
            Children = {
                WithDock(_pointsPerLevel, Dock.Right),
                new TextBlock { Text = "Training points per level", VerticalAlignment = VerticalAlignment.Center },
            },
        });
        _trainersCard = Card(trainerRows);
        _trainersCard.IsVisible = false;
        var battleRows = new StackPanel {
            Spacing = 6,
            Children = {
                Heading("Battle and world"),
                Sub("School pips: once every pip slot is full, each round turns a power pip into a school pip of your own school, " +
                    "for spells that cost school pips."),
            },
        };
        foreach (var (_, key, name, toggle) in _battleSwitches) {
            battleRows.Children.Add(new DockPanel {
                Children = {
                    WithDock(toggle, Dock.Right),
                    new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center },
                },
            });
        }

        _battleCard = Card(battleRows);
        _battleCard.IsVisible = false;
        _logScroll = new ScrollViewer {
            Height = 440,
            Background = LOG_BACK,
            Content = new Border { Padding = new Thickness(12), Child = _logLines },
        };

        var tabs = new TabControl {
            Padding = new Thickness(0),
            ItemsSource = new[] {
                Tab("Play", PlayTab()),
                Tab("Lookup", LookupTab()),
                Tab("Server", ServerTab()),
                Tab("Logs", LogsTab()),
                Tab("Settings", SettingsTab()),
                Tab("Accounts", AccountsTab()),
                Tab("Game Files", GameFilesTab()),
                Tab("Tools", ToolsTab()),
            },
        };
        tabs.SelectionChanged += async (_, _) => {
            switch ((tabs.SelectedItem as TabItem)?.Header as string) {
                case "Play": await LoadPlayAsync(); break;
                case "Lookup": await LoadLookupAsync(); break;
                case "Settings" when _settings.Count == 0: LoadSettings(); break;
                case "Accounts": await LoadAccountsAsync(); break;
            }

            // The position card only reads its file while the Play tab is showing, and the game log only while Logs is.
            var shownTab = (tabs.SelectedItem as TabItem)?.Header as string;
            if (shownTab == "Play") {
                RefreshPosition();
                _positionTimer.Start();
            }
            else {
                _positionTimer.Stop();
            }

            if (shownTab == "Logs") {
                RefreshClientLog();
                _clientLogTimer.Start();
            }
            else {
                _clientLogTimer.Stop();
            }
        };

        var header = new DockPanel {
            Margin = new Thickness(24, 14, 24, 4),
            Children = {
                new TextBlock { Text = Edition.NAME, FontSize = 20, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                new StackPanel {
                    Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(18, 0, 0, 0),
                    Children = { WithCenter(_dot), _statusText },
                },
            },
        };

        _messageBar.Child = _message;
        _saveBar.Child = new StackPanel {
            Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center,
            Children = { _saveCount, Action(Plain("Discard"), DiscardSettings), Action(Primary("Save changes"), SaveSettingsAsync) },
        };
        _saveBar.Background = CARD;

        Content = new DockPanel {
            Children = {
                WithDock(header, Dock.Top),
                WithDock(_messageBar, Dock.Bottom),
                WithDock(_saveBar, Dock.Bottom),
                tabs,
            },
        };

        _timer.Tick += (_, _) => Refresh();
        Opened += async (_, _) => {
            Refresh();
            await LoadPlayAsync();
            _timer.Start();
            await Guard(AutoUpdateAsync);
        };
        Closed += (_, _) => {
            _timer.Stop();
            _positionTimer.Stop();
            _clientLogTimer.Stop();
        };
    }

    // ---------------------------------------------------------------------
    // Tabs
    // ---------------------------------------------------------------------

    private Control PlayTab() {
        Action(_playButton, async () => Say(await _manager.PlayHereAsync()));
        _playButton.FontSize = 16;
        _playButton.Padding = new Thickness(22, 12);

        return Page(
            Card(new DockPanel {
                Children = {
                    WithDock(WithCenter(_playButton), Dock.Right),
                    new StackPanel { Margin = new Thickness(0, 0, 16, 0), Children = { Heading("Play on my server"), _playHelp } },
                },
            }),
            Card(new StackPanel {
                Children = {
                    Heading("Other servers"),
                    Sub("Play on someone else's private server with your Wizard101. The live game is blocked, so these only work for private servers."),
                    WithMargin(_serverRows, new Thickness(0, 10, 0, 10)),
                    new WrapPanel {
                        Children = {
                            Spaced(_newServerName), Spaced(_newServerHost), Spaced(_newServerPort),
                            Spaced(Action(Plain("Save server"), SaveServerAsync)),
                        },
                    },
                },
            }),
            Card(new StackPanel {
                Children = {
                    Heading("Play with friends"),
                    Sub("Friends on other internet connections join with this address and login port. Click \"Let friends connect\" " +
                        "once: Windows asks to allow the change, and the app asks your router to let friends in. Leave the server running."),
                    WithMargin(new DockPanel {
                        Children = {
                            WithDock(Row(Action(Primary("Let friends connect"), LetFriendsConnectAsync),
                                Action(Plain("Copy address"), () => _publicIp is null
                                    ? throw new PanelException("Your address is not found yet. Check your internet connection.")
                                    : CopyAsync($"{_publicIp} {FriendAccess.Ports(_manager.InstallFolder)[0]}"))), Dock.Right),
                            WithCenter(_friendAddress),
                        },
                    }, new Thickness(0, 10, 0, 6)),
                    _friendResult,
                },
            }),
            PositionCard());
    }

    private async Task LetFriendsConnectAsync() {
        Say("Opening the way in for friends. Click Yes if Windows asks…");
        var result = await FriendAccess.TurnOnAsync(_manager.InstallFolder);
        _publicIp = result.PublicIp ?? _publicIp;
        ShowFriendAddress();

        var lines = new List<string> {
            (result.Firewall ? "✓ " : "✗ ") + result.FirewallNote,
            (result.Router ? "✓ " : "✗ ") + result.RouterNote,
        };
        if (!result.Router) {
            lines.Add($"To open them by hand, forward TCP ports {string.Join(", ", result.Ports)} on your router to " +
                      $"{result.HomeIp ?? "this computer"}.");
        }

        if (result.SharedAddress) {
            lines.Add("✗ Your internet company shares one internet address between many homes, so friends cannot reach " +
                      "this computer through the router. Use a free VPN app such as Radmin VPN instead: you and your friend " +
                      "join the same network in it, and your friend uses your Radmin VPN address.");
        }

        _friendResult.Text = string.Join("\n", lines);
        _friendResult.IsVisible = true;
        Say(result.Firewall && result.Router && !result.SharedAddress
            ? "Friends can connect now. Send them the address shown under \"Play with friends\"."
            : "Some of it needs doing by hand. See \"Play with friends\" on the Play tab.",
            isError: !(result.Firewall && result.Router) || result.SharedAddress);
    }

    private void ShowFriendAddress() {
        var port = FriendAccess.Ports(_manager.InstallFolder)[0];
        _friendAddress.Text = _publicIp is null
            ? "Your address could not be found. Check your internet connection."
            : $"Address: {_publicIp}    Login port: {port}";
    }

    private Control LookupTab() {
        _lookupDelay.Tick += (_, _) => {
            _lookupDelay.Stop();
            RenderLookup();
        };
        _lookupSearch.TextChanged += (_, _) => {
            _lookupDelay.Stop();
            _lookupDelay.Start();
        };
        _lookupKind.SelectionChanged += (_, _) => RenderLookup();
        _lookupResults.ItemTemplate = new FuncDataTemplate<LookupEntry>((entry, _) => entry is null ? new Panel() : LookupRow(entry));

        return new DockPanel {
            Margin = new Thickness(24, 12, 24, 16),
            Children = {
                WithDock(Card(new StackPanel {
                    Children = {
                        Heading("Commands and IDs"),
                        Sub("Every in-game command, item, spell, creature, NPC, zone and quest. Type commands in the game's chat, " +
                            "starting with the dot. Copy puts the text on your clipboard."),
                        new DockPanel {
                            Margin = new Thickness(0, 10, 0, 6),
                            Children = { WithDock(WithMargin(_lookupKind, new Thickness(8, 0, 0, 0)), Dock.Right), _lookupSearch },
                        },
                        _lookupCount,
                    },
                }), Dock.Top),
                WithMargin(_lookupResults, new Thickness(0, 10, 0, 0)),
            },
        };
    }

    private Control ServerTab() {
        Action(_startButton, () => Task.FromResult(Say(_manager.StartServer())));
        Action(_restartButton, async () => {
            if (await ConfirmAsync("Restart the server? Everyone playing is disconnected.")) {
                Say(await _manager.RestartServerAsync());
            }
        });
        Action(_stopButton, async () => {
            if (await ConfirmAsync("Stop the server? Everyone playing is disconnected.")) {
                Say(_manager.StopServer());
            }
        });
        _stopButton.Foreground = BAD;
        _randomizer.IsCheckedChanged += async (_, _) => {
            if (!_updatingRandomizer) {
                await Guard(() => SetRandomizerAsync(_randomizer.IsChecked == true));
            }
        };
        _dieDelete.IsCheckedChanged += async (_, _) => {
            if (!_updatingRandomizer) {
                await Guard(() => SetDieDeleteAsync(_dieDelete.IsChecked == true));
            }
        };
        foreach (var (key, name, toggle) in _trainerSwitches) {
            toggle.IsCheckedChanged += async (_, _) => {
                if (!_updatingRandomizer) {
                    await Guard(() => SetTrainerSwitchAsync(key, name, toggle.IsChecked == true));
                }
            };
        }

        foreach (var (section, key, name, toggle) in _battleSwitches) {
            toggle.IsCheckedChanged += async (_, _) => {
                if (!_updatingRandomizer) {
                    await Guard(() => SetSwitchAsync(section, key, name, toggle.IsChecked == true));
                }
            };
        }

        _pointsPerLevel.ValueChanged += async (_, _) => {
            if (!_updatingRandomizer && _pointsPerLevel.Value is { } value) {
                await Guard(() => SetPointsPerLevelAsync((int) value));
            }
        };

        foreach (var box in new[] { _hideDebug, _hideWarnings }) {
            box.IsCheckedChanged += (_, _) => RefreshLog();
        }

        return Page(
            Card(new StackPanel {
                Children = {
                    Heading("Server"),
                    Sub("Start the server here. It gets the game files from your Wizard101 folder by itself. Closing this window stops it."),
                    WithMargin(Row(_startButton, _restartButton, _stopButton), new Thickness(0, 10, 0, 0)),
                },
            }),
            _randomizerCard,
            _dieDeleteCard,
            _battleCard,
            _trainersCard);
    }

    private Control SettingsTab() {
        _settingSearch.TextChanged += (_, _) => RenderSettings();

        return Page(
            Card(new StackPanel {
                Children = {
                    Heading("Settings"),
                    Sub("Every setting in the server's Imlight.ini. Changes apply the next time the server starts."),
                    WithMargin(_settingSearch, new Thickness(0, 10, 0, 0)),
                },
            }),
            _settingsList);
    }

    private Control AccountsTab() {
        _newLevel.ItemsSource = PlayerAccounts.AUTH_LEVELS.Select(LevelName).ToList();
        _newLevel.SelectedIndex = 0;

        return Page(
            Card(new StackPanel {
                Children = {
                    Heading("New account"),
                    Sub("Accounts can only be changed while the server is running."),
                    WithMargin(new WrapPanel {
                        Children = {
                            Spaced(_newUsername), Spaced(_newPassword), Spaced(_newLevel),
                            Spaced(Action(Primary("Create account"), CreateAccountAsync)),
                        },
                    }, new Thickness(0, 10, 0, 0)),
                },
            }),
            Card(new StackPanel {
                Children = {
                    new DockPanel {
                        Children = { WithDock(Action(Plain("Refresh"), LoadAccountsAsync), Dock.Right), Heading("Accounts") },
                    },
                    WithMargin(_accountsNote, new Thickness(0, 6, 0, 10)),
                    _accountRows,
                },
            }));
    }

    private CheckBox AutoUpdateBox() {
        var box = new CheckBox {
            Content = "Update by itself when a new version is out (checked each time this app opens)",
            IsChecked = _manager.AutoUpdate,
            Margin = new Thickness(0, 0, 0, 8),
        };
        box.IsCheckedChanged += (_, _) => _manager.AutoUpdate = box.IsChecked == true;

        return box;
    }

    private Control GameFilesTab() {
        Action(_copyGameFiles, CopyGameFilesAsync);
        Action(_readGameFiles, ReadGameFilesAsync);
        _cancelGameFiles.IsEnabled = false;
        _cancelGameFiles.Click += (_, _) => _gameFilesCancel?.Cancel();
        _gameFilesProgress.Text = GameFilesState();

        return Page(
            Card(new StackPanel {
                Spacing = 6,
                Children = {
                    Heading("Read the game's data files"),
                    Sub("Shows how Wizard101 works from the data files installed with it: quests, badges, triggers, pets, creatures, " +
                        "items, spells, zones and the messages the game and server send. It never changes your Wizard101 folder: " +
                        "step 1 copies the files into Imlight's own folder, and step 2 reads only that copy."),
                },
            }),
            Card(new StackPanel {
                Spacing = 6,
                Children = {
                    Heading("Step 1: Copy my game files"),
                    Sub("Copies the game's data files into a separate folder. It asks first and shows how much room it needs. " +
                        "The next time, it only copies files the game has changed since."),
                    WithMargin(Row(_copyGameFiles), new Thickness(0, 8, 0, 0)),
                },
            }),
            Card(new StackPanel {
                Spacing = 6,
                Children = {
                    Heading("Step 2: Read my copy"),
                    Sub("Reads the copy and writes everything into readable files. The \"Key details\" folder has the tables for running " +
                        "the game on a private server, and Overview.txt in it says what each one holds."),
                    WithMargin(new WrapPanel {
                        Children = {
                            Spaced(_readGameFiles),
                            Spaced(Action(Plain("Open key details"), OpenKeyDetailsAsync)),
                            Spaced(Action(Plain("Open game files folder"), () => Open(Directory.CreateDirectory(_gameFiles.Folder).FullName))),
                        },
                    }, new Thickness(0, 8, 0, 0)),
                },
            }),
            Card(new DockPanel {
                Children = {
                    WithDock(WithCenter(_cancelGameFiles), Dock.Right),
                    _gameFilesProgress,
                },
            }));
    }

    private string GameFilesState()
        => _gameFiles.HasReadable ? $"Last read is in {_gameFiles.ReadableFolder}."
            : _gameFiles.HasCopy ? "Your files are copied. Click \"Read my copy\" next."
            : "Nothing copied yet. Start with step 1.";

    private async Task CopyGameFilesAsync() {
        var (files, bytes, free) = _gameFiles.PlanCopy();
        if (files == 0) {
            Say("Your copy is already up to date. Click \"Read my copy\" to read it.");

            return;
        }

        if (!await ConfirmAsync($"Copy {files} game files ({GameFiles.Size(bytes)}) into {_gameFiles.CopyFolder}? " +
                $"This drive has {GameFiles.Size(free)} free. Your Wizard101 folder is only read from, never changed.")) {
            return;
        }

        Say(await RunGameFilesAsync(_gameFiles.CopyAsync));
    }

    private async Task ReadGameFilesAsync() {
        Say(await RunGameFilesAsync(_gameFiles.ReadAsync));
        if (_gameFiles.HasReadable) {
            await OpenKeyDetailsAsync();
        }
    }

    private async Task<string> RunGameFilesAsync(Func<IProgress<string>, System.Threading.CancellationToken, Task<string>> work) {
        _gameFilesCancel = new System.Threading.CancellationTokenSource();
        _copyGameFiles.IsEnabled = false;
        _readGameFiles.IsEnabled = false;
        _cancelGameFiles.IsEnabled = true;
        try {
            var result = await work(new Progress<string>(text => _gameFilesProgress.Text = text), _gameFilesCancel.Token);
            _gameFilesProgress.Text = result;

            return result;
        }
        catch (OperationCanceledException) {
            _gameFilesProgress.Text = "Stopped. Click the same button again to carry on.";

            return "Stopped.";
        }
        finally {
            _cancelGameFiles.IsEnabled = false;
            _copyGameFiles.IsEnabled = true;
            _readGameFiles.IsEnabled = true;
            _gameFilesCancel.Dispose();
            _gameFilesCancel = null;
        }
    }

    private Task OpenKeyDetailsAsync() {
        if (!_gameFiles.HasReadable) {
            Say("There is nothing to open yet. Copy your game files, then click \"Read my copy\".");

            return Task.CompletedTask;
        }

        return Open(_gameFiles.KeyDetailsFolder);
    }

    // Position: where each wizard on this server stands, from the logs/positions.txt file the server writes.
    private readonly DispatcherTimer _positionTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly TextBox _positionBox = new() {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = MONO, MinHeight = 70,
        Text = "Walk around in the game to see your position here.",
    };
    private string _firstPositionXyz = "";
    private string _firstPositionRaw = "";

    private string PositionFile => Path.Combine(_manager.LogFolder, "positions.txt");

    private void RefreshPosition() {
        // Leave the text alone while it is being selected, so a copy is not cut short.
        if (_positionBox.IsFocused) {
            return;
        }

        string[] lines;
        try {
            lines = File.Exists(PositionFile) ? File.ReadAllLines(PositionFile) : [];
        }
        catch (IOException) {
            return;
        }

        var shown = new List<string>();
        _firstPositionXyz = "";
        _firstPositionRaw = "";
        foreach (var line in lines) {
            var parts = line.Split('\t');
            if (parts.Length < 7) {
                continue;
            }

            // The game only sends which way the wizard faces: Rotation is that in radians (as the game files store it),
            // Angle the same in degrees. Wizards are always drawn at size 1, so Width is 1.
            var radians = float.TryParse(parts[5], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : 0;
            var rotation = radians.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            var angle = (radians * 180 / MathF.PI % 360).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            const string WIDTH = "1";
            if (_firstPositionXyz.Length == 0) {
                _firstPositionXyz = $"{parts[2]}, {parts[3]}, {parts[4]}";
                _firstPositionRaw = $"{rotation}, {angle}, {WIDTH}";
            }

            shown.Add($"{parts[0]}  |  {parts[1]}  |  X {parts[2]}  Y {parts[3]}  Z {parts[4]}  |  Rotation {rotation}  Angle {angle}°  Width {WIDTH}  |  {parts[6]}");
        }

        var text = shown.Count > 0 ? string.Join(Environment.NewLine, shown) : "Walk around in the game to see your position here.";
        if (_positionBox.Text != text) {
            _positionBox.Text = text;
        }
    }

    private Task CopyPositionAsync() {
        RefreshPosition();
        if (_firstPositionXyz.Length == 0) {
            throw new PanelException("There is no position yet. Walk around in the game first.");
        }

        return CopyAsync(_firstPositionXyz);
    }

    private Task CopyRawAsync() {
        RefreshPosition();
        if (_firstPositionRaw.Length == 0) {
            throw new PanelException("There is no position yet. Walk around in the game first.");
        }

        return CopyAsync(_firstPositionRaw);
    }

    // Logs: the server's log and the newest log Wizard101 wrote.
    private readonly DispatcherTimer _clientLogTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly TextBox _clientLogBox = new() {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = MONO, FontSize = 12.5,
        Height = 440, Text = "Play once to see the game's log here.",
    };
    private readonly TextBlock _clientLogFile = new() { Foreground = MUTED, TextWrapping = TextWrapping.Wrap };
    private string? _clientLogFolder;

    private void RefreshClientLog() {
        // Leave the text alone while it is being selected, so a copy is not cut short.
        if (_clientLogBox.IsFocused) {
            return;
        }

        GameClient.ClientLog log;
        try {
            log = GameClient.ReadClientLog(_manager.InstallFolder);
        }
        catch (Exception) {
            return;
        }

        _clientLogFolder = log.File is null ? null : Path.GetDirectoryName(log.File);
        _clientLogFile.Text = log.File is null ? log.Reason ?? "" : $"From {log.File}";
        var text = log.File is null ? "Play once to see the game's log here." : log.Text;
        if (_clientLogBox.Text != text) {
            _clientLogBox.Text = text;
            _clientLogBox.CaretIndex = text.Length;
        }
    }

    private Control LogsTab() {
        _clientLogTimer.Tick += (_, _) => RefreshClientLog();

        return Page(
            Card(new StackPanel {
                Children = {
                    new DockPanel {
                        Margin = new Thickness(0, 0, 0, 8),
                        Children = {
                            WithDock(Row(_hideDebug, _hideWarnings, _follow, _pauseLog), Dock.Right),
                            Heading("Server log"),
                        },
                    },
                    new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _logScroll },
                    WithMargin(Row(Action(Plain("Copy all"), CopyLogAsync), Action(Plain("Save log to desktop"), SaveLogAsync)),
                        new Thickness(0, 8, 0, 0)),
                    WithMargin(Sub("The same message repeated in a row shows once, with how many times it happened."), new Thickness(0, 8, 0, 0)),
                },
            }),
            Card(new StackPanel {
                Children = {
                    Heading("Game log"),
                    Sub("The newest log Wizard101 wrote, updated every 2 seconds while this tab is open. Select any part to copy it."),
                    WithMargin(_clientLogFile, new Thickness(0, 6, 0, 8)),
                    new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _clientLogBox },
                    WithMargin(Row(Action(Plain("Copy all"), () => CopyAsync(_clientLogBox.Text ?? "")),
                        Action(Plain("Open game log folder"), () => _clientLogFolder is null
                            ? throw new PanelException("Wizard101 has not written a log yet. Play once, then look again.")
                            : Open(_clientLogFolder))), new Thickness(0, 8, 0, 0)),
                },
            }));
    }

    private Control PositionCard() {
        _positionTimer.Tick += (_, _) => RefreshPosition();

        return Card(new StackPanel {
            Children = {
                Heading("Position (X, Y, Z and Rotation, Angle, Width)"),
                Sub("Where each wizard on your server is standing, as X, Y and Z, updated twice a second while this tab is open. "
                    + "Rotation is the way the wizard faces in radians, the way the game files store it, and Angle is the same in degrees. "
                    + "Width is the wizard's size, which is always 1. "
                    + "It reads what the game already tells your server; nothing is added to the game. The newest mover is on top. "
                    + "Select any part of the text to copy it, or use the button. Positions are exact to about 4 units."),
                WithMargin(_positionBox, new Thickness(0, 10, 0, 10)),
                Row(Action(Primary("Copy X, Y, Z"), CopyPositionAsync), Action(Plain("Copy Rotation, Angle, Width"), CopyRawAsync), Action(Plain("Copy all"), () => CopyAsync(_positionBox.Text ?? ""))),
            },
        });
    }

    private Control ToolsTab() {
        return Page(Card(new StackPanel {
            Children = {
                Heading("Tools"),
                Sub("Update or remove Imlight, or open the server's files."),
                WithMargin(new WrapPanel {
                    Children = {
                        Spaced(Action(Primary("Update Imlight"), UpdateAsync)),
                        Spaced(Danger(Action(Plain("Uninstall Imlight"), UninstallAsync))),
                        Spaced(Action(Plain("Open log folder"), () => Open(_manager.LogFolder))),
                        Spaced(Action(Plain("Open settings folder"), () => Open(_manager.ConfigFolder))),
                        Spaced(Action(Plain("Open install folder"), () => Open(_manager.InstallFolder))),
                    },
                }, new Thickness(0, 10, 0, 10)),
                AutoUpdateBox(),
                Sub($"{Edition.NAME} {SelfUpdate.CurrentVersion}, installed in {_manager.InstallFolder}."),
            },
        }));
    }

    // ---------------------------------------------------------------------
    // Status and log
    // ---------------------------------------------------------------------

    private void Refresh() {
        var status = _manager.Status();
        _dot.Background = status.Ready ? GOOD : status.Running ? WARN : MUTED;
        _statusText.Text = status.Ready ? "Running, ready for players" : status.Running ? "Starting…" : "Stopped";
        _startButton.IsEnabled = !status.Running;
        _stopButton.IsEnabled = status.Running;
        _restartButton.IsEnabled = status.Running;
        _playButton.IsEnabled = status.ClientFound;
        if (!status.ClientFound) {
            _playHelp.Text = "Wizard101 could not be found. Click \"Update Imlight\" on the Tools tab of the app once.";
        }

        _randomizerCard.IsVisible = status.Randomizer is not null;
        _updatingRandomizer = true;
        _randomizer.IsChecked = status.Randomizer == true;
        _dieDeleteCard.IsVisible = status.DieDelete is not null;
        _dieDelete.IsChecked = status.DieDelete == true;
        _trainersCard.IsVisible = _manager.IsInstalled && _manager.HasSetting("Trainers", "FreeTraining");
        foreach (var (key, _, toggle) in _trainerSwitches) {
            toggle.IsChecked = _manager.GetSwitch("Trainers", key);
        }

        _battleCard.IsVisible = _manager.IsInstalled && _manager.HasSetting("Battle", "ManaOnFizzle");
        foreach (var (section, key, _, toggle) in _battleSwitches) {
            toggle.IsChecked = _manager.GetSwitch(section, key);
            toggle.IsEnabled = _manager.HasSetting(section, key);
        }

        if (!_pointsPerLevel.IsFocused) {
            _pointsPerLevel.Value = int.TryParse(_manager.GetSetting("Trainers", "TrainingPointsPerLevel"), out var points) ? points : 1;
        }

        _updatingRandomizer = false;
        RefreshLog();
    }

    private void RefreshLog() {
        // Paused: keep what is on screen so it can be read or copied.
        if (_pauseLog.IsChecked == true) {
            return;
        }

        List<LogLine> lines;
        try {
            lines = _manager.ReadLog();
        }
        catch (Exception) {
            return;
        }

        var atBottom = _logScroll.Offset.Y + _logScroll.Viewport.Height >= _logScroll.Extent.Height - 30;
        _logLines.Children.Clear();
        _shownLog = [];
        if (lines.Count == 0) {
            _logLines.Children.Add(new TextBlock { Text = "No log yet. Start the server to see it here.", Foreground = MUTED });

            return;
        }

        foreach (var line in lines) {
            if ((_hideDebug.IsChecked == true && line.Level is "DBG" or "VRB") || (_hideWarnings.IsChecked == true && line.Level == "WRN")) {
                continue;
            }

            var shown = line.Count > 1 ? $"{line.Text}   ×{line.Count}" : line.Text;
            _shownLog.Add(shown);
            var text = new SelectableTextBlock {
                Text = shown,
                FontFamily = MONO,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = line.Level switch {
                    "WRN" => WARN,
                    "ERR" or "FTL" => BAD,
                    "DBG" or "VRB" => MUTED,
                    _ => LOG_TEXT,
                },
            };
            _logLines.Children.Add(text);
        }

        if (_follow.IsChecked == true && atBottom) {
            Dispatcher.UIThread.Post(() => _logScroll.ScrollToEnd(), DispatcherPriority.Background);
        }
    }

    private async Task SetRandomizerAsync(bool on) {
        var restart = _manager.SaveSettings([new Setting("Randomizer", "Enabled", on ? "true" : "false", "")]);
        _settings = [];
        Say($"Randomizer mode is now {(on ? "on" : "off")}.");
        if (restart && await ConfirmAsync($"Randomizer mode is now {(on ? "on" : "off")}. Restart the server now to use it?")) {
            Say(await _manager.RestartServerAsync());
        }
    }

    private Task SetTrainerSwitchAsync(string key, string name, bool on) => SetSwitchAsync("Trainers", key, name, on);

    private async Task SetSwitchAsync(string section, string key, string name, bool on) {
        var restart = _manager.SaveSettings([new Setting(section, key, on ? "true" : "false", "")]);
        _settings = [];
        Say($"{name} is now {(on ? "on" : "off")}.");
        if (restart && await ConfirmAsync($"{name} is now {(on ? "on" : "off")}. Restart the server now to use it?")) {
            Say(await _manager.RestartServerAsync());
        }
    }

    private Task SetPointsPerLevelAsync(int points) {
        if (!_manager.HasSetting("Trainers", "TrainingPointsPerLevel")
                || _manager.GetSetting("Trainers", "TrainingPointsPerLevel") == points.ToString()) {
            return Task.CompletedTask;
        }

        _manager.SaveSettings([new Setting("Trainers", "TrainingPointsPerLevel", points.ToString(), "")]);
        _settings = [];
        Say($"Each level now gives {points} training points while that switch is on. It applies the next time the server starts.");

        return Task.CompletedTask;
    }

    private async Task SetDieDeleteAsync(bool on) {
        if (on && !await ConfirmAsync("Turn on Die = Delete? From now on, any wizard on this server that dies in battle " +
                "and is still dead when the battle ends is deleted for good. It can't be undone.")) {
            _updatingRandomizer = true;
            _dieDelete.IsChecked = false;
            _updatingRandomizer = false;

            return;
        }

        var restart = _manager.SaveSettings([new Setting("Die Delete", "Enabled", on ? "true" : "false", "")]);
        _settings = [];
        Say($"Die = Delete mode is now {(on ? "on" : "off")}.");
        if (restart && await ConfirmAsync($"Die = Delete mode is now {(on ? "on" : "off")}. Restart the server now to use it?")) {
            Say(await _manager.RestartServerAsync());
        }
    }

    // ---------------------------------------------------------------------
    // Play
    // ---------------------------------------------------------------------

    private async Task LoadPlayAsync() {
        RenderServers();
        _publicIp ??= await FriendAccess.PublicIpAsync();
        ShowFriendAddress();
    }

    private void RenderServers() {
        _serverRows.Children.Clear();
        var servers = GameClient.LoadServers(_manager.InstallFolder);
        if (servers.Count == 0) {
            _serverRows.Children.Add(Sub("No other servers saved yet."));

            return;
        }

        foreach (var server in servers) {
            _serverRows.Children.Add(new DockPanel {
                Children = {
                    WithDock(Row(
                        Action(Primary("Play"), async () => {
                            await GameClient.LaunchAsync(_manager.InstallFolder, server.Host, server.Port);
                            Say($"Starting Wizard101 on {server.Name}.");
                        }),
                        Danger(Action(Plain("Remove"), async () => {
                            if (await ConfirmAsync($"Remove {server.Name}?")) {
                                GameClient.RemoveServer(_manager.InstallFolder, server.Name);
                                RenderServers();
                                Say($"{server.Name} was removed.");
                            }
                        }))), Dock.Right),
                    new TextBlock {
                        Text = $"{server.Name}    {server.Host}:{server.Port}", VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            });
        }
    }

    private async Task SaveServerAsync() {
        var port = int.TryParse(_newServerPort.Text, out var parsed) ? parsed : 0;
        var saved = await GameClient.AddServerAsync(_manager.InstallFolder, _newServerName.Text ?? "", _newServerHost.Text ?? "", port);
        _newServerName.Text = _newServerHost.Text = _newServerPort.Text = "";
        RenderServers();
        Say($"{saved.Name} was saved.");
    }

    // ---------------------------------------------------------------------
    // Lookup
    // ---------------------------------------------------------------------

    private async Task LoadLookupAsync() {
        var folder = _manager.IdLookupFolder;
        var changed = new[] { "ids.csv", "zones.txt", "quests.csv" }
            .Select(name => Path.Combine(folder, name))
            .Where(File.Exists)
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();
        if (_lookup is not null && changed <= _lookupLoadedFrom) {
            return;
        }

        _lookupCount.Text = "Loading…";
        try {
            _lookup = await Task.Run(() => Lookup.Load(folder));
            _lookupLoadedFrom = changed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            _lookup = Lookup.Load("");
        }

        RenderLookup();
    }

    private void RenderLookup() {
        if (_lookup is null) {
            return;
        }

        const int MAX = 300;
        var kind = _lookupKind.SelectedItem as string ?? Lookup.EVERYTHING;
        var query = _lookupSearch.Text ?? "";
        var results = _lookup.Search(kind, query, MAX, out var total);
        _lookupResults.ItemsSource = results;

        var count = query.Trim().Length == 0 && kind == Lookup.EVERYTHING
            ? $"Showing all {total} commands. Type to search everything ({_lookup.Count:N0} entries)."
            : total == 0 ? "Nothing matches." : total > MAX ? $"{total:N0} matches. Showing the best {MAX}; add words to narrow it down." : $"{total:N0} matches.";
        _lookupCount.Text = _lookup.Missing is null ? count : $"{count} {_lookup.Missing}";
    }

    private Control LookupRow(LookupEntry entry) {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (entry.Typed is not null) {
            buttons.Children.Add(Action(Plain("Copy command"), () => CopyAsync(entry.Typed)));
        }

        if (entry.Id.Length > 0) {
            buttons.Children.Add(Action(Plain("Copy ID"), () => CopyAsync(entry.Id)));
        }

        var details = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 12, 0) };
        details.Children.Add(new StackPanel {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children = {
                new SelectableTextBlock { Text = entry.Name, FontWeight = FontWeight.SemiBold, FontSize = 15 },
                WithCenter(new TextBlock {
                    Text = string.Join("  ·  ", new[] { entry.Kind, entry.Id.Length > 0 ? $"ID {entry.Id}" : "", entry.Detail }.Where(t => t.Length > 0)),
                    Foreground = MUTED,
                }),
            },
        });
        if (entry.Typed is not null) {
            details.Children.Add(new SelectableTextBlock { Text = entry.Typed, FontFamily = MONO, Foreground = GOOD });
        }

        if (entry.Help.Length > 0) {
            details.Children.Add(Sub(entry.Help));
        }

        return new Border {
            Padding = new Thickness(4, 6),
            BorderBrush = LINE,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new DockPanel { Children = { WithDock(buttons, Dock.Right), details } },
        };
    }

    private async Task CopyLogAsync() {
        if (_shownLog.Count == 0) {
            throw new PanelException("There is no log to copy yet.");
        }

        var clipboard = GetTopLevel(this)?.Clipboard ?? throw new PanelException("The clipboard is not available.");
        await clipboard.SetTextAsync(string.Join(Environment.NewLine, _shownLog));
        Say($"Copied {_shownLog.Count} log lines. Paste them with Ctrl+V.");
    }

    private Task SaveLogAsync() {
        var path = _manager.SaveLogToDesktop();
        Say($"Saved the whole log to your desktop as \"{Path.GetFileName(path)}\". Drag it into the chat to send it.");

        return Task.CompletedTask;
    }

    private async Task CopyAsync(string text) {
        var clipboard = GetTopLevel(this)?.Clipboard ?? throw new PanelException("The clipboard is not available.");
        await clipboard.SetTextAsync(text);
        Say($"Copied {text}");
    }

    // ---------------------------------------------------------------------
    // Tools
    // ---------------------------------------------------------------------

    /// <summary>
    /// When the app opens, updates Imlight by itself if a newer release is out (unless turned off on the Tools tab).
    /// While the server is running it asks first, so nobody playing is thrown out.
    /// </summary>
    private async Task AutoUpdateAsync() {
        if (!_manager.AutoUpdate || await SelfUpdate.NewerReleaseAsync() is not { } version) {
            return;
        }

        if (_manager.Status().Running && !await ConfirmAsync($"Imlight {version} is out. Update now? The server stops and everyone playing is disconnected.")) {
            Say($"Imlight {version} is out. Click Update Imlight on the Tools tab when you're ready.");

            return;
        }

        Say($"Updating to Imlight {version}…");
        _manager.RunInstaller("--update");
        Close();
    }

    private async Task UpdateAsync() {
        if (await ConfirmAsync("Update Imlight now? The server stops, this app closes, and the update opens in its own window. " +
                "Your settings, accounts and characters are kept.")) {
            _manager.RunInstaller("--update");
            Close();
        }
    }

    private async Task UninstallAsync() {
        if (await ConfirmAsync("Uninstall Imlight? The server stops, this app closes, and the uninstaller opens in its own window. " +
                "It asks whether to keep your accounts and characters. Wizard101 itself is not touched.")) {
            _manager.RunInstaller("--uninstall");
            Close();
        }
    }

    // ---------------------------------------------------------------------
    // Settings
    // ---------------------------------------------------------------------

    private void LoadSettings() {
        _settings = _manager.ReadSettings();
        _pending.Clear();
        UpdateSaveBar();
        RenderSettings();
    }

    private void RenderSettings() {
        var query = _settingSearch.Text?.Trim() ?? "";
        _settingsList.Children.Clear();
        foreach (var (section, settings) in _settings) {
            var matches = settings.Where(s => query.Length == 0
                || $"{section} {s.Key} {s.Help}".Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) {
                continue;
            }

            var rows = new StackPanel();
            foreach (var setting in matches) {
                rows.Children.Add(SettingRow(setting));
            }

            _settingsList.Children.Add(new Expander {
                Header = section,
                IsExpanded = query.Length > 0 || section == "Randomizer",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = rows,
            });
        }
    }

    private Control SettingRow(Setting setting) {
        var current = _pending.TryGetValue((setting.Section, setting.Key), out var changed) ? changed.Value : setting.Value;
        Control input;
        if (bool.TryParse(setting.Value, out _)) {
            var toggle = new ToggleSwitch { IsChecked = current.Equals("true", StringComparison.OrdinalIgnoreCase), OnContent = "", OffContent = "" };
            toggle.IsCheckedChanged += (_, _) => Change(setting, toggle.IsChecked == true ? "true" : "false");
            input = toggle;
        }
        else {
            var box = new TextBox { Text = current };
            if (setting.Key.Contains("password", StringComparison.OrdinalIgnoreCase)) {
                box.PasswordChar = '•';
            }

            box.TextChanged += (_, _) => Change(setting, box.Text ?? "");
            input = box;
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*"), Margin = new Thickness(0, 8) };
        grid.Children.Add(new TextBlock { Text = setting.Key, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);

        var row = new StackPanel { Children = { grid } };
        if (setting.Help.Length > 0) {
            row.Children.Add(Sub(setting.Help));
        }

        return new Border { BorderBrush = LINE, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 8), Child = row };
    }

    private void Change(Setting setting, string value) {
        if (value == setting.Value) {
            _pending.Remove((setting.Section, setting.Key));
        }
        else {
            _pending[(setting.Section, setting.Key)] = setting with { Value = value };
        }

        UpdateSaveBar();
    }

    private void UpdateSaveBar() {
        _saveBar.IsVisible = _pending.Count > 0;
        _saveCount.Text = $"{_pending.Count} unsaved change{(_pending.Count == 1 ? "" : "s")}";
    }

    private Task DiscardSettings() {
        _pending.Clear();
        UpdateSaveBar();
        RenderSettings();

        return Task.CompletedTask;
    }

    private async Task SaveSettingsAsync() {
        var count = _pending.Count;
        var restart = _manager.SaveSettings(_pending.Values.ToList());
        LoadSettings();
        Refresh();
        Say($"Saved {count} setting{(count == 1 ? "" : "s")}.");
        if (restart && await ConfirmAsync("Restart the server now to use the new settings? Everyone playing is disconnected.")) {
            Say(await _manager.RestartServerAsync());
        }
    }

    // ---------------------------------------------------------------------
    // Accounts
    // ---------------------------------------------------------------------

    private async Task LoadAccountsAsync() {
        _accountRows.Children.Clear();
        List<AccountSummary> list;
        try {
            using var accounts = _manager.Accounts();
            list = await accounts.ListAsync();
        }
        catch (Exception ex) when (ex is AccountException or PanelException) {
            _accountsNote.Text = ex.Message;

            return;
        }

        _accountsNote.Text = $"{list.Count} account{(list.Count == 1 ? "" : "s")}. Level changes apply the next time that player logs in. " +
            "To delete an account, use .account delete in game.";
        foreach (var account in list) {
            _accountRows.Children.Add(AccountRow(account));
        }
    }

    private Control AccountRow(AccountSummary account) {
        var level = new ComboBox { ItemsSource = PlayerAccounts.AUTH_LEVELS.Select(LevelName).ToList(), Width = 170 };
        level.SelectedIndex = Math.Max(0, Array.IndexOf(PlayerAccounts.AUTH_LEVELS, account.AuthLevel));
        level.SelectionChanged += async (_, _) => {
            var chosen = PlayerAccounts.AUTH_LEVELS[Math.Max(0, level.SelectedIndex)];
            if (chosen != account.AuthLevel) {
                await Guard(async () => {
                    using var accounts = _manager.Accounts();
                    await accounts.SetAuthLevelAsync(account.Username, chosen);
                    Say($"{account.Username} is now {LevelName(chosen)}. It applies the next time they log in.");
                });
            }
        };

        var password = Action(Plain("New password"), async () => {
            if (await AskAsync($"New password for {account.Username}", secret: true) is { Length: > 0 } text) {
                using var accounts = _manager.Accounts();
                await accounts.SetPasswordAsync(account.Username, text);
                Say($"The password for {account.Username} was changed.");
            }
        });
        var lockButton = Action(Plain(account.IsLocked ? "Unlock" : "Lock"), async () => {
            if (!account.IsLocked && !await ConfirmAsync($"Lock {account.Username}? They cannot log in until unlocked.")) {
                return;
            }

            using var accounts = _manager.Accounts();
            await accounts.SetLockedAsync(account.Username, !account.IsLocked);
            Say(account.IsLocked ? $"{account.Username} can log in again." : $"{account.Username} can no longer log in.");
            await LoadAccountsAsync();
        });
        if (!account.IsLocked) {
            Danger(lockButton);
        }

        var lastLogin = account.LastLoginTime is { } time && !time.StartsWith("0001", StringComparison.Ordinal) && DateTime.TryParse(time, out var parsed)
            ? parsed.ToLocalTime().ToString("g")
            : "Never";
        var details = new TextBlock {
            Text = $"{account.Characters} character{(account.Characters == 1 ? "" : "s")} · last login {lastLogin}" + (account.IsLocked ? " · Locked" : ""),
            Foreground = account.IsLocked ? BAD : MUTED,
        };

        return new Border {
            BorderBrush = LINE, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 6),
            Child = new DockPanel {
                Children = {
                    WithDock(Row(level, password, lockButton), Dock.Right),
                    new StackPanel {
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = { new TextBlock { Text = account.Username, FontWeight = FontWeight.SemiBold }, details },
                    },
                },
            },
        };
    }

    private async Task CreateAccountAsync() {
        using var accounts = _manager.Accounts();
        var username = _newUsername.Text ?? "";
        await accounts.CreateAsync(username, _newPassword.Text ?? "", PlayerAccounts.AUTH_LEVELS[Math.Max(0, _newLevel.SelectedIndex)]);
        _newUsername.Text = _newPassword.Text = "";
        Say($"Account {username} was created.");
        await LoadAccountsAsync();
    }

    private static string LevelName(string level) => level switch {
        "None" => "Player",
        "QualityAssurance" => "Quality assurance",
        "HallMonitor" => "Hall monitor",
        _ => level,
    };

    // ---------------------------------------------------------------------
    // Messages and dialogs
    // ---------------------------------------------------------------------

    private string Say(string text, bool isError = false) {
        _message.Text = text;
        _messageBar.Background = isError ? BAD : CARD;
        _message.Foreground = isError ? Brushes.White : null;
        _messageBar.IsVisible = true;

        return text;
    }

    /// <summary>
    /// Runs an action, showing any problem in the message bar instead of failing.
    /// </summary>
    private async Task Guard(Func<Task> action) {
        try {
            await action();
        }
        catch (Exception ex) when (ex is PanelException or AccountException) {
            Say(ex.Message, isError: true);
        }
        catch (Exception ex) {
            Say($"Something went wrong: {ex.Message}", isError: true);
        }
    }

    private Task Open(string path) {
        ServerManager.Open(path);

        return Task.CompletedTask;
    }

    private Task<bool> ConfirmAsync(string question) {
        var dialog = Dialog(question, null, out var ok);

        return dialog.ShowDialog<bool>(this);
    }

    private async Task<string?> AskAsync(string question, bool secret) {
        var box = new TextBox { PasswordChar = secret ? '•' : '\0' };
        var dialog = Dialog(question, box, out _);
        var confirmed = await dialog.ShowDialog<bool>(this);

        return confirmed ? box.Text : null;
    }

    private static Window Dialog(string question, Control? input, out Button ok) {
        var dialog = new Window {
            Title = Edition.NAME, Width = 420, SizeToContent = SizeToContent.Height, CanResize = false, Background = BACKGROUND,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        ok = Primary("OK");
        ok.Click += (_, _) => dialog.Close(true);
        var cancel = Plain("Cancel");
        cancel.Click += (_, _) => dialog.Close(false);
        var body = new StackPanel { Margin = new Thickness(20), Spacing = 14, Children = { new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap } } };
        if (input is not null) {
            body.Children.Add(input);
        }

        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } });
        dialog.Content = body;

        return dialog;
    }

    // ---------------------------------------------------------------------
    // Building blocks
    // ---------------------------------------------------------------------

    private Button Action(Button button, Func<Task> action) {
        button.Click += async (_, _) => {
            button.IsEnabled = false;
            try {
                await Guard(action);
            }
            finally {
                button.IsEnabled = true;
            }
        };

        return button;
    }

    private static TabItem Tab(string header, Control content) => new() { Header = header, Content = content, FontSize = 17, Padding = new Thickness(14, 4) };

    private static Control Page(params Control[] cards) {
        var stack = new StackPanel { Margin = new Thickness(24, 12, 24, 40), Spacing = 14, MaxWidth = 1060 };
        stack.Children.AddRange(cards);

        return new ScrollViewer { Content = stack };
    }

    private static Border Card(Control content) => new() {
        Background = CARD, CornerRadius = new CornerRadius(12), Padding = new Thickness(18), Child = content,
    };

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) };

    private static TextBlock Sub(string text) => new() { Text = text, Foreground = MUTED, TextWrapping = TextWrapping.Wrap };

    private static Button Primary(string text) => new() { Content = text, Classes = { "accent" } };

    private static Button Plain(string text) => new() { Content = text };

    private static Button Danger(Button button) {
        button.Foreground = BAD;

        return button;
    }

    private static StackPanel Row(params Control[] controls) {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.AddRange(controls);

        return row;
    }

    private static T Spaced<T>(T control) where T : Control {
        control.Margin = new Thickness(0, 0, 10, 8);

        return control;
    }

    private static T WithDock<T>(T control, Dock dock) where T : Control {
        DockPanel.SetDock(control, dock);

        return control;
    }

    private static T WithCenter<T>(T control) where T : Control {
        control.VerticalAlignment = VerticalAlignment.Center;

        return control;
    }

    private static T WithMargin<T>(T control, Thickness margin) where T : Control {
        control.Margin = margin;

        return control;
    }

}
