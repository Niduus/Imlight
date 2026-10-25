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
using System.Globalization;
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
using Imlight.Setup.Blocks;

namespace Imlight.Setup;

/// <summary>
/// The app's window: Play (this server, other private servers), Lookup (every command and ID, and each spell's card in
/// ManagerWindow.Spells.cs), Bosses (a boss's
/// stats and deck, ManagerWindow.Bosses.cs), Server (start, stop, randomizer, log), Settings, Accounts, Game Files and Tools.
/// </summary>
internal sealed partial class ManagerWindow : Window {

    // The colours are the Manager's theme (ManagerTheme.cs), shared with the block editor and the Customs tab's areas.
    private static readonly IBrush BACKGROUND = ManagerTheme.BACKGROUND_BRUSH;
    private static readonly IBrush CARD = ManagerTheme.SURFACE_HIGH_BRUSH;
    private static readonly IBrush LINE = ManagerTheme.LINE_BRUSH;
    private static readonly IBrush GOOD = ManagerTheme.Brush(ManagerTheme.GOOD);
    private static readonly IBrush WARN = ManagerTheme.Brush(ManagerTheme.WARN);
    private static readonly IBrush BAD = ManagerTheme.Brush(ManagerTheme.BAD);
    private static readonly IBrush MUTED = ManagerTheme.MUTED_BRUSH;
    private static readonly IBrush LOG_BACK = ManagerTheme.Brush(ManagerTheme.LOG_BACK);
    private static readonly IBrush LOG_TEXT = ManagerTheme.Brush(ManagerTheme.LOG_TEXT);
    private static readonly FontFamily MONO = ManagerTheme.MONO;

    private readonly ServerManager _manager;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    // Header and message bar.
    private readonly Border _dot = new() { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), Background = MUTED, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _statusText = new() { Text = "Checking…", Foreground = MUTED, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _statusBadge;
    // At most a few lines: a copied log shown here in full once covered the whole window (Niduus, 2026-10-20).
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, MaxLines = 4, TextTrimming = TextTrimming.CharacterEllipsis };
    // Shows what a background update is doing on the button itself (downloading 40%, building...).
    private readonly Button _updateButton = Primary("Update Imlight");
    private readonly Border _messageBar = new() { Padding = new Thickness(16, 10), IsVisible = false };

    // Play.
    private readonly Button _playButton = Primary("Play Wizard101");
    private readonly TextBlock _playHelp = Sub("If the server is off, it starts it first.");
    private readonly StackPanel _serverRows = new() { Spacing = 6 };
    private readonly TextBox _newServerName = new() { Watermark = "Name (optional)", Width = 160 };
    private readonly TextBox _newServerHost = new() { Watermark = "Address, such as play.example.com", MinWidth = 260 };
    private readonly TextBox _newServerPort = new() { Watermark = "Port", Width = 90 };
    private readonly SelectableTextBlock _friendAddress = new() { Text = "Finding your address…", FontSize = 16, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _friendResult = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private string? _publicIp;
    // Addresses and ports stay hidden until shown, and hidden again each time the app opens (Niduus, 2026-10-23:
    // "have IP and port hidden unless revealed").
    private bool _showFriendAddress;
    private readonly Button _friendAddressToggle = Plain("Show");
    private List<string> _friendResultLines = [];
    private List<string> _friendResultSecrets = [];

    // Server.
    private readonly Button _startButton = Primary("Start server");
    private readonly Button _restartButton = Plain("Restart");
    private readonly Button _stopButton = Plain("Stop server");
    private readonly Border _modesCard;
    private readonly Control _randomizerCard;
    private readonly ToggleSwitch _randomizer = new() { OnContent = "On", OffContent = "Off" };
    private readonly CheckBox _hideDebug = new() { Content = "Hide debug lines", IsChecked = true };
    private readonly CheckBox _hideWarnings = new() { Content = "Hide warnings" };
    private readonly CheckBox _follow = new() { Content = "Follow", IsChecked = true };
    private readonly CheckBox _pauseLog = new() { Content = "Pause" };
    private List<string> _shownLog = [];
    // Only the lines in view are drawn (a recycling, virtualizing list), so a long log scrolls and grows smoothly.
    private System.Collections.ObjectModel.ObservableCollection<LogRow> _logRows = [];
    private readonly ItemsControl _logLines = new() {
        ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
        ItemTemplate = new FuncDataTemplate<LogRow>((row, _) => {
            var text = new SelectableTextBlock { FontFamily = MONO, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            void Show(LogRow? shown) {
                text.Text = shown?.Text;
                text.Foreground = shown?.Brush;
            }

            Show(row);
            text.DataContextChanged += (_, _) => Show(text.DataContext as LogRow);

            return text;
        }, supportsRecycling: true),
    };
    private readonly TextBlock _logEmpty = new() { Text = "No log yet. Start the server to see it here.", Foreground = MUTED, IsVisible = false };
    private readonly ScrollViewer _logScroll;
    private readonly Control _dieDeleteCard;
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
        ("RespecAtTrainers", "Mr. Lincoln offers to reset training points (needs a restart)", new() { OnContent = "On", OffContent = "Off" }),
    ];
    private readonly Border _battleCard;
    private readonly List<(string Section, string Key, string Name, ToggleSwitch Switch)> _battleSwitches = [
        ("Battle", "ManaOnFizzle", "Reduce mana even if fizzle", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "UnlimitedMana", "Unlimited mana (spells cost none, the Commons refills it)", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "BattleWisps", "Battles drop health, mana and gold wisps", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "LateJoinersJoinImmediately", "A wizard who joins a battle late plays the current round (off: waits for the next one)", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "MobsJoinLate", "Monsters can join a battle late", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "FastAnimationTiming", "Shorter waits between turns, using animation times measured in the game (extra seconds under Settings, Battle)", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "FullPipConserve", "100% pip conserve (a half-used power pip always comes back)", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "FullPipConversion", "100% pip conversion (with Pip Conversion gear, a spell's school always turns a normal pip into a power pip)", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "NoPipsFromDaybreakerNightbringer", "Daybreaker and Nightbringer do not add pips to card", new() { OnContent = "On", OffContent = "Off" }),
        ("Archmastery", "Enabled", "Archmastery (Power Orb turns power pips into school pips from level 50, or earlier with a jewel)", new() { OnContent = "On", OffContent = "Off" }),
        ("Archmastery", "AllLevels", "Archmastery at every level, not just 50 and up", new() { OnContent = "On", OffContent = "Off" }),
        ("Archmastery", "Creatures", "Creatures use their Archmastery too (school pips, and they set the pace)", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "SchoolPips", "School pips", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "AnySchoolPips", "School pips count as any school pips", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "GainSchoolPips", "Gain school pips without stats (chance under Settings, Battle)", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "RandomSchoolPips", "Random school pips", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "ShadowPips", "Shadow pips (min, max and chance under Settings, Battle)", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "NaturalShadowPips", "Gain shadow pips without stats", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "CreatureShadowPips", "Creatures can get shadow pips", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "RandomCreatureShadowPips", "Creatures get shadow magic only by chance (chance under Settings, Battle)", new() { OnContent = "On", OffContent = "Off" }),
        ("Randomizer", "Tame", "Randomizer: tame mode (creatures, their decks and pips stay normal so quests can be done)", new() { OnContent = "On", OffContent = "Off" }),
        ("Pets", "KeepExtraXp", "Pets keep extra XP when they reach a new age, like the real game (off: it is lost)", new() { OnContent = "On", OffContent = "Off" }),
        ("Pets", "RunningTotalXp", "Pet XP is a running total: XP from earlier ages counts toward the next (off: each age starts over, like the real game)", new() { OnContent = "On", OffContent = "Off" }),
        ("Pets", "DoubleSnackStats", "Double XP & Rewards also doubles snack stats (+15 becomes +30)", new() { OnContent = "On", OffContent = "Off" }),
        ("Pets", "SkipShowsFeedWindow", "Skipping a pet game opens the feed window", new() { OnContent = "On", OffContent = "Off" }),
        ("Pets", "StartAtMaxStats", "New pets start with every stat at the server maximum (off: their own starting stats, like the real game)", new() { OnContent = "On", OffContent = "Off" }),
        ("Pets", "SkipGameRewards", "Skipping a pet game gives its XP and stats (off: only the snack counts)", new() { OnContent = "On", OffContent = "Off" }),
        ("Pets", "ItemFeedCostsEnergy", "Feeding an item to your pet costs pet energy where the game files say so (off: always free)", new() { OnContent = "On", OffContent = "Off" }),
        ("Jewels", "KeepShatteredJewels", "Jewels taken out of a socket go back to your backpack (off: they shatter unless they are No Shatter)", new() { OnContent = "On", OffContent = "Off" }),
        ("Battle", "DaybreakerNightbringerAddPips", "Daybreaker and Nightbringer add a pip to the card's cost (off: the card costs what it did)", new() { OnContent = "On", OffContent = "Off" }),
        ("Housing", "PlaceAnyAsDecoration", "Houses: place any furniture as decoration (vaults, signs, music players show but do not work yet)", new() { OnContent = "On", OffContent = "Off" }),
        ("Instance", "ResetWhenEmpty", "Dungeons start fresh once everyone has left (off: kept for 10 minutes)", new() { OnContent = "On", OffContent = "Off" }),
        ("Randomizer", "RandomizeShadowPips", "Randomizer: random shadow pip limits and creature shadow magic", new() { OnContent = "On", OffContent = "Off" }),
        ("Randomizer", "RandomizeItemCards", "Randomizer: random item cards (from gear and pets)", new() { OnContent = "On", OffContent = "Off" }),
        ("Character", "RefillCrowns", "Crowns refill to the starting amount (amount under Settings, Character)", new() { OnContent = "On", OffContent = "Off" }),
        ("Login Server", "DisableIdleKick", "Disable idle kick (never disconnect idle players)", new() { OnContent = "On", OffContent = "Off" }),
        ("World", "OpenAllDoors", "Open every door (no quest or level needed, such as Nightside)", new() { OnContent = "On", OffContent = "Off" }),
        ("PvP", "DuelCircles", "Duel circles: wizards standing on both sides of a house's duel circle (or a Customs duel circle) duel each other", new() { OnContent = "On", OffContent = "Off" }),
        ("PvP", "HouseDuelTakesTurns", "House duel circles take turns, the PvP way (off: both sides act at once)", new() { OnContent = "On", OffContent = "Off" }),
    ];
    private readonly NumericUpDown _pointsPerLevel = new() { Minimum = 0, Maximum = 100, Increment = 1, FormatString = "0", Width = 130 };

    // Settings.
    private readonly TextBox _settingSearch = new() { Watermark = "Search settings" };
    private readonly StackPanel _settingsList = new() { Spacing = 8 };
    private readonly Dictionary<(string Section, string Key), Setting> _pending = [];
    private readonly Border _saveBar = new() { IsVisible = false, Padding = new Thickness(16, 10) };
    private readonly TextBlock _saveCount = new() { VerticalAlignment = VerticalAlignment.Center };
    private List<(string Section, List<Setting> Settings)> _settings = [];
    private const string PET_CASTING = "PetCasting";
    private readonly TextBox _petCastSearch = new() { Watermark = "Search May Cast cards, such as Aura, Ward, trap, Fire or Tower Shield" };
    private readonly StackPanel _petCastRows = new();

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
    private readonly Button _shopResearch = Primary("Save shop research");
    private readonly Button _cancelGameFiles = Plain("Stop");
    private System.Threading.CancellationTokenSource? _gameFilesCancel;

    internal ManagerWindow(ServerManager manager) {
        _manager = manager;
        _gameFiles = new GameFiles(manager.InstallFolder);
        Title = Edition.NAME;
        Width = 1240;
        Height = 840;
        ManagerTheme.Apply(this);
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        // The two game modes share one card, a row each (they were a card each, with a single switch in it).
        _randomizerCard = SwitchRow("Randomizer mode", _randomizer,
            "Shuffles schools, spells, drops and more, kept balanced. Fine-tune it under Settings, Randomizer.");
        _randomizerCard.IsVisible = false;
        _dieDeleteCard = SwitchRow("Die = Delete mode", _dieDelete, "A wizard who dies in battle is deleted for good.");
        ToolTip.SetTip(_dieDeleteCard, "If a wizard dies in battle and is still dead when the battle ends, that wizard is deleted for good. " +
            "Fleeing doesn't count, and neither does the tutorial battle.");
        _dieDeleteCard.IsVisible = false;
        _modesCard = Fold("dice", "Customs", "Game modes", "Whole-server modes, for every character.", _randomizerCard, _dieDeleteCard);
        _modesCard.IsVisible = false;
        var trainerRows = new StackPanel { Spacing = 4 };
        foreach (var (key, name, toggle) in _trainerSwitches) {
            var row = SwitchRow(name, toggle);
            if (key == "RandomizeSpellRequirements") {
                ToolTip.SetTip(row, "Each spell that needs another spell first needs a different one from the same trainer instead.");
            }

            trainerRows.Children.Add(row);
        }

        trainerRows.Children.Add(SwitchRow("Training points per level", _pointsPerLevel));
        // The two long lists of switches start folded shut: they are set once, not on every visit.
        _trainersCard = Shut(Fold("school", "Lookup", "Spell trainers",
            "What trainers ask for before they teach a spell, with or without randomizer mode.",
            trainerRows));
        _trainersCard.IsVisible = false;
        var battleRows = new StackPanel { Spacing = 4 };
        foreach (var (_, key, name, toggle) in _battleSwitches) {
            var row = SwitchRow(name, toggle);
            if (key == "MobsJoinLate") {
                ToolTip.SetTip(row, "A monster that walks into a battle with a free enemy circle joins it and fights from the next round, " +
                    "like the real game. Off: monsters walk past battles.");
            }

            if (key == "SchoolPips") {
                ToolTip.SetTip(row, "Once every pip slot is full, each round turns a power pip into a school pip of your own school, " +
                    "for spells that cost school pips.");
            }

            battleRows.Children.Add(row);
        }

        _battleCard = Shut(Fold("sword", "Bosses", "Battle and world",
            "Switches for battles, pips, pets, jewels, houses and the world.",
            battleRows));
        _battleCard.IsVisible = false;
        _logScroll = new ScrollViewer {
            Height = 440,
            Background = LOG_BACK,
            Content = new Border { Padding = new Thickness(12), Child = new StackPanel { Children = { _logEmpty, _logLines } } },
        };

        var tabs = new TabControl {
            Padding = new Thickness(0),
            TabStripPlacement = Dock.Left,
            ItemsSource = new[] {
                Tab("Play", PlayTab),
                Tab("Commands", CommandsTab),
                Tab("Lookup", LookupTab),
                Tab("Quests", QuestsTab),
                Tab("Phrases", PhrasesTab),
                Tab("Spells", SpellsTab),
                Tab("Gear", GearTab),
                Tab("Recipes", RecipesTab),
                Tab("Bosses", BossesTab),
                Tab("Customs", CustomsTab),
                Tab("Guides", GuidesTab),
                Tab("Server", ServerTab),
                Tab("Logs", LogsTab),
                Tab("Settings", SettingsTab),
                Tab("Accounts", AccountsTab),
                Tab("Game Files", GameFilesTab),
                Tab("Tools", ToolsTab),
            },
        };
        tabs.SelectionChanged += async (_, e) => {
            // A list or drop-down inside a tab sends its own SelectionChanged up here; only a new tab counts.
            if (e.Source != tabs) {
                return;
            }

            RefreshMissingNotes();
            switch ((tabs.SelectedItem as TabItem)?.Tag as string) {
                case "Play": await LoadPlayAsync(); break;
                case "Lookup": await LoadLookupAsync(); break;
                case "Quests": LoadQuests(); break;
                case "Bosses": await LoadBossesAsync(); break;
                case "Customs": await LoadCustomsAsync(); break;
                // The Make a custom wizard on the Guides tab searches the creature list.
                case "Guides":
                    await LoadBossesAsync();
                    UpdateCustomWizard();
                    break;
                case "Settings" when _settings.Count == 0: LoadSettings(); break;
                case "Accounts": await LoadAccountsAsync(); break;
            }

            // The position card only reads its file while the Play tab is showing, and the game log only while Logs is.
            var shownTab = (tabs.SelectedItem as TabItem)?.Tag as string;
            if (shownTab == "Play") {
                RefreshPosition();
                _positionTimer.Start();
            }
            else {
                _positionTimer.Stop();
            }

            _questTimer.IsEnabled = shownTab == "Quests";

            _logTabShown = shownTab == "Logs";
            if (shownTab == "Logs") {
                RefreshLog();
                RefreshClientLog();
                _clientLogTimer.Start();
            }
            else {
                _clientLogTimer.Stop();
            }
        };

        // The header: the app's name on the left, the server's state as a badge on the right.
        _statusBadge = ManagerTheme.Badge(_dot, _statusText);
        ToolTip.SetTip(_statusBadge, "The server on this computer. Start and stop it on the Server tab.");
        ManagerTheme.PaintBadge(_statusBadge, _statusText, MUTED);
        var header = new Border {
            Background = ManagerTheme.SIDEBAR_BRUSH, BorderBrush = LINE, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(18, 12, 24, 12),
            Child = new DockPanel {
                Children = {
                    WithDock(_statusBadge, Dock.Right),
                    WithDock(new Border { Margin = new Thickness(0, 0, 12, 0), Child = ManagerTheme.Tile("sparkle", ManagerTheme.Accent("accent"), 34) }, Dock.Left),
                    new StackPanel {
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = {
                            new TextBlock { Text = Edition.NAME, FontSize = ManagerTheme.FONT_APP, FontWeight = FontWeight.Bold },
                            new TextBlock { Text = "Your Wizard101 server", FontSize = ManagerTheme.FONT_SMALL, Foreground = MUTED },
                        },
                    },
                },
            },
        };

        _messageBar.Child = _message;
        _messageBar.BorderBrush = LINE;
        _messageBar.BorderThickness = new Thickness(0, 1, 0, 0);
        _messageBar.PointerPressed += (_, _) => _messageBar.IsVisible = false;
        _saveBar.Child = new StackPanel {
            Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center,
            Children = { _saveCount, Action(Plain("Discard"), DiscardSettings), Action(Primary("Save changes"), SaveSettingsAsync) },
        };
        _saveBar.Background = CARD;
        _saveBar.BorderBrush = LINE;
        _saveBar.BorderThickness = new Thickness(0, 1, 0, 0);

        // The tabs run down a side bar of their own colour, with the credit pinned to its bottom (ManagerWindow.Credit.cs).
        var credit = CreditFooter();
        var body = new Panel {
            Children = {
                new Border {
                    Width = ManagerTheme.SIDEBAR_WIDTH, HorizontalAlignment = HorizontalAlignment.Left, Background = ManagerTheme.SIDEBAR_BRUSH,
                    BorderBrush = LINE, BorderThickness = new Thickness(0, 0, 1, 0),
                },
                WithMargin(tabs, new Thickness(0, 10, 0, 0)),
                credit,
            },
        };
        FitCreditFooter(body, tabs, credit);

        Content = new DockPanel {
            Children = {
                WithDock(header, Dock.Top),
                WithDock(_messageBar, Dock.Bottom),
                WithDock(_saveBar, Dock.Bottom),
                body,
            },
        };

        _timer.Tick += (_, _) => Refresh();
        Opened += async (_, _) => {
            Refresh();
            RefreshMissingNotes();
            await LoadPlayAsync();
            _timer.Start();

            // Play is the first tab, and choosing the tab that is already showing does not count as a change.
            RefreshPosition();
            _positionTimer.Start();
            if (ManagerApp.StartServerOnOpen) {
                await Guard(() => {
                    Say($"Updated to Imlight {SelfUpdate.CurrentVersion}. " + _manager.StartServer());

                    return Task.CompletedTask;
                });
            }

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
        ToolTip.SetTip(_friendAddressToggle, "Shows or hides your address and ports. Copy address copies them either way.");
        _friendAddressToggle.Click += (_, _) => {
            _showFriendAddress = !_showFriendAddress;
            _friendAddressToggle.Content = ManagerTheme.ButtonContent(_showFriendAddress ? "Hide" : "Show");
            ShowFriendAddress();
        };
        _playButton.FontSize = 16;
        _playButton.Padding = new Thickness(22, 12);

        return Page(
            Fold("play", "Play", "Play on my server", "Wizard101 on this computer, on your own server.",
                new DockPanel {
                    Children = {
                        WithDock(WithCenter(_playButton), Dock.Right),
                        WithCenter(WithMargin(_playHelp, new Thickness(0, 0, 16, 0))),
                    },
                }),
            Shut(Fold("globe", "Lookup", "Other servers",
                "Play on someone else's private server.",
                WithMargin(_serverRows, new Thickness(0, 0, 0, 4)),
                WithHint(new WrapPanel {
                    Children = {
                        Spaced(_newServerName), Spaced(_newServerHost), Spaced(_newServerPort),
                        Spaced(Action(Plain("Save server"), SaveServerAsync)),
                    },
                }, "Play on someone else's private server with your Wizard101. The live game is blocked, so these only work for private servers."))),
            Fold("users", "Accounts", "Play with friends",
                "Friends join with this address and login port.",
                WithHint(new DockPanel {
                    Children = {
                        WithDock(Row(Action(Primary("Let friends connect"), LetFriendsConnectAsync),
                            Action(Plain("Copy address"), () => _publicIp is null
                                ? throw new PanelException("Your address is not found yet. Check your internet connection.")
                                : CopyAsync($"{_publicIp} {FriendAccess.Ports(_manager.InstallFolder)[0]}", "Copied your address and login port."))), Dock.Right),
                        WithDock(WithCenter(WithMargin(_friendAddressToggle, new Thickness(10, 0, 10, 0))), Dock.Right),
                        WithCenter(_friendAddress),
                    },
                }, "Friends on other internet connections join with this address and login port. Click \"Let friends connect\" " +
                   "once: Windows asks to allow the change, and the app asks your router to let friends in. Leave the server running."),
                _friendResult),
            CommandCard(),
            AnnouncementCard(),
            PositionCard());
    }

    private async Task LetFriendsConnectAsync() {
        Say("Opening the way in for friends. Click Yes if Windows asks…");
        var result = await FriendAccess.TurnOnAsync(_manager.InstallFolder);
        _publicIp = result.PublicIp ?? _publicIp;
        ShowFriendAddress();

        _friendResultLines = [
            (result.Firewall ? "✓ " : "✗ ") + result.FirewallNote,
            (result.Router ? "✓ " : "✗ ") + result.RouterNote,
        ];
        if (!result.Router) {
            _friendResultLines.Add($"To open them by hand, forward TCP ports {string.Join(", ", result.Ports)} on your router to " +
                      $"{result.HomeIp ?? "this computer"}.");
        }

        if (result.SharedAddress) {
            _friendResultLines.Add("✗ Your internet company shares one internet address between many homes, so friends cannot reach " +
                      "this computer through the router. Use a free VPN app such as Radmin VPN instead: you and your friend " +
                      "join the same network in it, and your friend uses your Radmin VPN address.");
        }

        _friendResultSecrets = [.. new[] { result.PublicIp, result.HomeIp }.OfType<string>(), .. result.Ports.Select(p => p.ToString())];
        ShowFriendAddress();
        _friendResult.IsVisible = true;
        Say(result.Firewall && result.Router && !result.SharedAddress
            ? "Friends can connect now. Send them the address shown under \"Play with friends\"."
            : "Some of it needs doing by hand. See \"Play with friends\" on the Play tab.",
            isError: !(result.Firewall && result.Router) || result.SharedAddress);
    }

    private void ShowFriendAddress() {
        var port = FriendAccess.Ports(_manager.InstallFolder)[0].ToString();
        _friendAddress.Text = _publicIp is null
            ? "Your address could not be found. Check your internet connection."
            : $"Address: {Reveal(_publicIp, _showFriendAddress)}    Login port: {Reveal(port, _showFriendAddress)}";
        _friendResult.Text = HideIn(string.Join("\n", _friendResultLines), _showFriendAddress ? [] : _friendResultSecrets);
    }

    /// <summary>An address or a port as it is, or with its letters and digits as dots: "•••.•••.•.••".</summary>
    private static string Reveal(string text, bool shown)
        => shown ? text : string.Concat(text.Select(c => char.IsLetterOrDigit(c) ? '•' : c));

    /// <summary>The text with each of the addresses and ports in it dotted out, longest first so a port inside an address stays whole.</summary>
    private static string HideIn(string text, IEnumerable<string> secrets) {
        foreach (var secret in secrets.Where(s => s.Length > 0).Distinct().OrderByDescending(s => s.Length)) {
            text = text.Replace(secret, Reveal(secret, false));
        }

        return text;
    }

    private readonly TextBox _commandSearch = new() { Watermark = "Search the commands" };
    private readonly ListBox _customCommands = new() { Background = Brushes.Transparent };
    private readonly ListBox _imlightCommands = new() { Background = Brushes.Transparent };

    /// <summary>
    /// Every in-game command, split into the ones made for this build (Custom) and the ones Imlight comes with.
    /// </summary>
    private Control CommandsTab() {
        foreach (var list in new[] { _customCommands, _imlightCommands }) {
            list.ItemTemplate = new FuncDataTemplate<LookupEntry>((entry, _) => entry is null ? new Panel() : LookupRow(entry));
        }

        var custom = new TabItem { Content = _customCommands, FontSize = 15 };
        var imlight = new TabItem { Content = _imlightCommands, FontSize = 15 };
        void Render() {
            var words = (_commandSearch.Text ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var shown = Lookup.COMMANDS.Where(c => words.All(c.Haystack.Contains)).ToList();
            _customCommands.ItemsSource = shown.Where(c => c.Custom).ToList();
            _imlightCommands.ItemsSource = shown.Where(c => !c.Custom).ToList();
            custom.Header = $"Custom ({shown.Count(c => c.Custom)})";
            imlight.Header = $"Imlight ({shown.Count(c => !c.Custom)})";
        }

        _commandSearch.TextChanged += (_, _) => Render();
        Render();

        return new DockPanel {
            Margin = new Thickness(24, 12, 24, 16), MaxWidth = ManagerTheme.PAGE_MAX_WIDTH,
            Children = {
                WithDock(WithMargin(MissingNote("the Custom commands and the cheats here are made for " + ServerSource.DefaultName +
                    ", so many of them won't work on it.", () => !_manager.HasIdLookup), new Thickness(0, 0, 0, 10)), Dock.Top),
                WithDock(Fold("search", "Commands", "Find a command",
                    "Type them in the game's chat, starting with the dot.",
                    WithHint(_commandSearch, "Custom has the commands made for your build. Imlight has the ones the server comes with. " +
                        "Type them in the game's chat, starting with the dot. Copy command puts the text on your clipboard.")), Dock.Top),
                WithMargin(Card(new TabControl { ItemsSource = new[] { custom, imlight, PlayerCheatsTab() } }), new Thickness(0, 14, 0, 0)),
            },
        };
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
        _lookupKind.SelectionChanged += (_, _) => {
            ShowSpellMode();
            RenderLookup();
        };
        _lookupResults.ItemTemplate = new FuncDataTemplate<LookupEntry>((entry, _) => entry is null ? new Panel() : LookupRow(entry));

        return new DockPanel {
            Margin = new Thickness(24, 12, 24, 16), MaxWidth = ManagerTheme.PAGE_MAX_WIDTH,
            Children = {
                WithDock(WithMargin(MissingNote("it doesn't write the lists of IDs this page searches.", () => !_manager.HasIdLookup),
                    new Thickness(0, 0, 0, 10)), Dock.Top),
                WithDock(Fold("search", "Lookup", "Commands and IDs",
                    "Search every command, item, spell, creature, zone and quest.",
                    WithHint(new DockPanel {
                        Children = { WithDock(WithMargin(_lookupKind, new Thickness(8, 0, 0, 0)), Dock.Right), _lookupSearch },
                    }, "Every in-game command, bundle, item, spell, creature, NPC, zone and quest. Type commands in the game's chat, " +
                       "starting with the dot. Copy puts the text on your clipboard. Pick Spell on the right to see each spell's card."),
                    _lookupCount), Dock.Top),
                WithMargin(Card(new Panel { Children = { _lookupResults, SpellView() } }), new Thickness(0, 14, 0, 0)),
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
                Say(await Task.Run(() => _manager.StopServer()));
            }
        });
        Danger(_stopButton);
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
            Fold("power", "Server", "Start and stop",
                "Closing this window stops the server.",
                WithHint(Row(_startButton, _restartButton, _stopButton),
                    "Start the server here. It gets the game files from your Wizard101 folder by itself. Closing this window stops it.")),
            _modesCard,
            _battleCard,
            TamedDropsCard(),
            CodesCard(),
            MemberBenefitsCard(),
            PlantGrowthCard(),
            EventsCard(),
            _trainersCard);
    }

    private Control SettingsTab() {
        // A short wait after the last key, as the Lookup search does, so typing a word shows its matches once.
        _settingDelay.Tick += (_, _) => {
            _settingDelay.Stop();
            UiTiming.Measure("search settings", RenderSettings);
        };
        _settingSearch.TextChanged += (_, _) => {
            _settingDelay.Stop();
            _settingDelay.Start();
        };
        _petCastSearch.TextChanged += (_, _) => {
            var query = _settingSearch.Text?.Trim() ?? "";
            var settings = _settings.FirstOrDefault(g => g.Section == PET_CASTING).Settings ?? [];
            RenderPetCastRows(settings.Where(s => query.Length == 0
                || $"{PET_CASTING} {s.Key} {s.Help}".Contains(query, StringComparison.OrdinalIgnoreCase)).ToList());
        };

        return Page(
            Fold("search", "Settings", "Find a setting",
                "Changes apply the next time the server starts.",
                WithHint(_settingSearch, "Every setting in the server's Imlight.ini. Changes apply the next time the server starts.")),
            _settingsList);
    }

    private Control AccountsTab() {
        _newLevel.ItemsSource = PlayerAccounts.AUTH_LEVELS.Select(LevelName).ToList();
        _newLevel.SelectedIndex = 0;

        // One card: the accounts, and the boxes to add one under them (they were two cards).
        return Page(
            Fold("users", "Accounts", "Accounts", "Accounts can only be changed while the server is running.",
                new DockPanel {
                    Children = { WithDock(Action(Plain("Refresh"), LoadAccountsAsync), Dock.Right), WithCenter(_accountsNote) },
                },
                _accountRows,
                new TextBlock { Text = "New account", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                new WrapPanel {
                    Children = {
                        Spaced(_newUsername), Spaced(_newPassword), Spaced(_newLevel),
                        Spaced(Action(Primary("Create account"), CreateAccountAsync)),
                    },
                }));
    }

    private CheckBox AutoUpdateBox() {
        var box = new CheckBox {
            Content = "Update by itself when a new version is out",
            IsChecked = _manager.AutoUpdate,
        };
        ToolTip.SetTip(box, "Checked each time this app opens.");
        box.IsCheckedChanged += (_, _) => _manager.AutoUpdate = box.IsChecked == true;

        return box;
    }

    private Control AutoInstallSwitch() {
        var toggle = new ToggleSwitch { OnContent = "On", OffContent = "Off", IsChecked = _manager.AutoInstallUpdates };
        toggle.IsCheckedChanged += (_, _) => {
            _manager.AutoInstallUpdates = toggle.IsChecked == true;
            Say(toggle.IsChecked == true
                ? "New versions now install by themselves when they are ready, without asking. If the server is running, it restarts with the update."
                : "The app asks before it restarts into a new version again.");
        };

        var row = SwitchRow("Install updates automatically", toggle, "Restarts into a new version without asking first.");
        ToolTip.SetTip(row, "When a new version has downloaded and built, the app restarts into it without asking \"Yes\" first. " +
            "If the server is running it restarts too, so anyone playing is disconnected for a moment.");

        return row;
    }

    private Control GameFilesTab() {
        Action(_copyGameFiles, CopyGameFilesAsync);
        Action(_readGameFiles, ReadGameFilesAsync);
        Action(_shopResearch, async () => Say(await RunGameFilesAsync(_gameFiles.ShopResearchAsync)));
        _cancelGameFiles.IsEnabled = false;
        _cancelGameFiles.Click += (_, _) => _gameFilesCancel?.Cancel();
        _gameFilesProgress.Text = GameFilesState();

        // Step 1, step 2 and how far they are share one card (they were four).
        static Control Step(string number, Control buttons, string hint) => WithHint(new DockPanel {
            Children = {
                WithDock(WithMargin(WithCenter(new TextBlock { Text = number, FontWeight = FontWeight.SemiBold, Width = 54 }), new Thickness(0, 0, 0, 8)), Dock.Left),
                buttons,
            },
        }, hint);

        return Page(
            Fold("info", "Game Files", "Read the game's data files",
                "Copies them, then makes them readable. Your Wizard101 folder is never changed.",
                Step("Step 1", new WrapPanel { Children = { Spaced(_copyGameFiles) } },
                    "Copies the game's data files into a separate folder. It asks first and shows how much room it needs. " +
                    "The next time, it only copies files the game has changed since. It never changes your Wizard101 folder."),
                Step("Step 2", new WrapPanel {
                    Children = {
                        Spaced(_readGameFiles),
                        Spaced(Action(Plain("Open key details"), OpenKeyDetailsAsync)),
                        Spaced(Action(Plain("Open game files folder"), () => Open(Directory.CreateDirectory(_gameFiles.Folder).FullName))),
                    },
                }, "Reads the copy and writes everything into readable files: quests, badges, triggers, pets, creatures, items, " +
                   "spells, zones and the messages the game and server send. The \"Key details\" folder has the tables for running " +
                   "the game on a private server, and Overview.txt in it says what each one holds."),
                ManagerTheme.Item(new DockPanel {
                    Children = {
                        WithDock(WithCenter(_cancelGameFiles), Dock.Right),
                        WithDock(WithMargin(WithCenter(ManagerTheme.Icon("hourglass", 18, MUTED)), new Thickness(0, 0, 10, 0)), Dock.Left),
                        WithCenter(_gameFilesProgress),
                    },
                })),
            Shut(Fold("bag", "Game Files", "Shop research",
                "Zips the game's shop files to your desktop, to find why some shops open as a robe shop.",
                Row(_shopResearch))));
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
        _shopResearch.IsEnabled = false;
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
            _shopResearch.IsEnabled = true;
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

    private string PositionFile => Path.Combine(_manager.LogFolder, ServerManager.POSITIONS_FILE);

    // Read twice a second away from the window, so a slow disk never makes it skip.
    private bool _positionReading;

    private void RefreshPosition() => _ = RefreshPositionAsync();

    private async Task RefreshPositionAsync() {
        // Leave the text alone while it is being selected, so a copy is not cut short.
        if (_positionBox.IsFocused || _positionReading) {
            return;
        }

        string[] lines;
        _positionReading = true;
        try {
            var file = PositionFile;
            lines = await Task.Run(() => File.Exists(file) ? File.ReadAllLines(file) : []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return;
        }
        finally {
            _positionReading = false;
        }

        if (_positionBox.IsFocused) {
            return;
        }

        // The wizard whose game runs on this PC comes first, so the copy buttons use the host's own position.
        lines = lines.OrderBy(line => line.EndsWith("\tlocal", StringComparison.Ordinal) ? 0 : 1).ToArray();
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

    private async Task CopyPositionAsync() {
        await RefreshPositionAsync();
        if (_firstPositionXyz.Length == 0) {
            throw new PanelException("There is no position yet. Walk around in the game first.");
        }

        await CopyAsync(_firstPositionXyz);
    }

    private async Task CopyRawAsync() {
        await RefreshPositionAsync();
        if (_firstPositionRaw.Length == 0) {
            throw new PanelException("There is no position yet. Walk around in the game first.");
        }

        await CopyAsync(_firstPositionRaw);
    }

    // Logs: the server's log and the newest log Wizard101 wrote.
    private readonly DispatcherTimer _clientLogTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly TextBox _clientLogBox = new() {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = MONO, FontSize = 12.5,
        Height = 440, Text = "Play once to see the game's log here.",
    };
    private readonly TextBlock _clientLogFile = new() { Foreground = MUTED, TextWrapping = TextWrapping.Wrap };
    private string? _clientLogFolder;

    // The box shows only the newest lines; laying out the whole log every 2 seconds froze the window.
    private const int CLIENT_LOG_SHOWN_LINES = 200;
    private string _clientLogText = "";
    private bool _clientLogReading;

    private async void RefreshClientLog() {
        // Leave the text alone while it is being selected, so a copy is not cut short.
        if (_clientLogBox.IsFocused || _clientLogReading) {
            return;
        }

        GameClient.ClientLog log;
        string text;
        _clientLogReading = true;
        try {
            var folder = _manager.InstallFolder;
            (log, text) = await Task.Run(() => {
                var read = GameClient.ReadClientLog(folder);
                var lines = read.File is null ? Array.Empty<string>() : read.Text.Split('\n');

                return (read, read.File is null ? "Play once to see the game's log here."
                    : string.Join("\n", lines.Length > CLIENT_LOG_SHOWN_LINES ? lines[^CLIENT_LOG_SHOWN_LINES..] : lines));
            });
        }
        catch (Exception) {
            return;
        }
        finally {
            _clientLogReading = false;
        }

        _clientLogFolder = log.File is null ? null : Path.GetDirectoryName(log.File);
        _clientLogFile.Text = log.File is null ? log.Reason ?? "" : $"From {log.File} (newest {CLIENT_LOG_SHOWN_LINES} lines; Copy all copies more)";
        _clientLogText = log.File is null ? "" : log.Text;
        if (_clientLogBox.IsFocused) {
            return;
        }

        if (_clientLogBox.Text != text) {
            _clientLogBox.Text = text;
            _clientLogBox.CaretIndex = text.Length;
        }
    }

    private Control LogsTab() {
        _clientLogTimer.Tick += (_, _) => RefreshClientLog();

        return Page(
            Fold("server", "Server", "Server log",
                "What the server on this computer is doing.",
                WithHint(Row(_hideDebug, _hideWarnings, _follow, _pauseLog),
                    "What the server on this computer is doing. The same message repeated in a row shows once, with how many times it happened."),
                LogWell(_logScroll),
                Row(Action(Plain("Copy all"), CopyLogAsync), Action(Plain("Save log to desktop"), SaveLogAsync))),
            // Folded shut at first: the server log is the one looked at most. It still reads while this tab is open.
            Shut(Fold("log", "Logs", "Game log",
                "The newest log Wizard101 wrote, updated every 2 seconds.",
                _clientLogFile,
                LogWell(_clientLogBox),
                Row(Action(Plain("Copy all"), () => CopyAsync(_clientLogText.Length > 0 ? _clientLogText : _clientLogBox.Text ?? "")),
                    Action(Plain("Save game log to desktop"), SaveClientLogAsync),
                    Action(Plain("Open game log folder"), () => _clientLogFolder is null
                        ? throw new PanelException("Wizard101 has not written a log yet. Play once, then look again.")
                        : Open(_clientLogFolder))))));
    }

    private readonly TextBox _commandBox = new() { Watermark = "A command, such as .elixir50 or .lookup fire cat" };

    private readonly ComboBox _commandHistory = new() { PlaceholderText = "Previous commands", Width = 220 };

    private Control CommandCard() {
        void RefreshHistory() {
            _commandHistory.SelectedIndex = -1;
            _commandHistory.ItemsSource = _manager.CommandHistory();
        }

        async Task SendAsync() {
            var command = _commandBox.Text ?? "";
            _manager.SendCommand(command);
            _commandBox.Text = "";
            RefreshHistory();
            Say($"Sent {command.Trim()} to your game. The answer shows in the game's chat.");
            await Task.CompletedTask;
        }

        // Picking a previous command puts it in the box, ready to run again or change.
        RefreshHistory();
        _commandHistory.SelectionChanged += (_, _) => {
            if (_commandHistory.SelectedItem is string picked) {
                _commandBox.Text = picked;
                _commandBox.CaretIndex = picked.Length;
                _commandBox.Focus();
            }
        };

        var send = Action(Primary("Run in my game"), SendAsync);
        _commandBox.KeyDown += async (_, e) => {
            if (e.Key == Avalonia.Input.Key.Enter) {
                e.Handled = true;
                await Guard(SendAsync);
            }
        };

        return Fold("terminal", "Commands", "Run a command",
            "Runs a command as your wizard, the same as typing it in chat.",
            MissingNote("the app can't run commands in its game. Type them in the game's chat instead.", () => !_manager.HasCommandBox),
            WithHint(new DockPanel {
                Children = {
                    WithDock(WithMargin(send, new Thickness(8, 0, 0, 0)), Dock.Right),
                    WithDock(WithMargin(_commandHistory, new Thickness(8, 0, 0, 0)), Dock.Right),
                    _commandBox,
                },
            }, "Runs a command as the wizard playing on this PC, the same as typing it in chat. "
               + "Friends playing from other PCs are never affected. The Lookup tab lists every command. "
               + "Previous commands lists the last ones you ran: pick one to run it again."));
    }

    private readonly TextBox _announcementBox = new() { Watermark = "Such as: Double XP weekend starts now!" };
    private readonly CheckBox _announceBox = new() { Content = "Box on screen", IsChecked = true };
    private readonly CheckBox _announceChat = new() { Content = "Chat line", IsChecked = true };

    /// <summary>
    /// Tells everyone playing on the server something (Niduus, 2026-10-23: "add a announcement to the manager so I can
    /// announce something to the whole server"): the game's message box in the middle of the screen, a chat line, or both.
    /// </summary>
    private Control AnnouncementCard() {
        async Task SendAsync() {
            var text = (_announcementBox.Text ?? "").Trim();
            _manager.Announce(text, _announceBox.IsChecked == true, _announceChat.IsChecked == true);
            _announcementBox.Text = "";
            Say($"Announced to everyone online: {text}");
            await Task.CompletedTask;
        }

        var send = Action(Primary("Announce"), SendAsync);
        _announcementBox.KeyDown += async (_, e) => {
            if (e.Key == Avalonia.Input.Key.Enter) {
                e.Handled = true;
                await Guard(SendAsync);
            }
        };

        return Fold("quote", "Play", "Announcement",
            "Sends a message to every player online on your server, friends included.",
            WithHint(new DockPanel {
                Children = {
                    WithDock(WithMargin(send, new Thickness(8, 0, 0, 0)), Dock.Right),
                    _announcementBox,
                },
            }, "Box on screen: the game's message box, which each player closes with OK. Chat line: \"Announcement: ...\" in the chat. "
               + "Only players online right now see it."),
            Row(_announceBox, _announceChat));
    }

    private Control PositionCard() {
        _positionTimer.Tick += (_, _) => RefreshPosition();

        // Folded shut at first: it is for placing things in custom areas, not for every visit (the timer still runs).
        return Shut(Fold("crosshair", "zone", "Position",
            "Where each wizard on your server is standing, updated live.",
            _positionBox,
            WithHint(Row(Action(Primary("Copy X, Y, Z"), CopyPositionAsync), Action(Plain("Copy Rotation, Angle, Width"), CopyRawAsync), Action(Plain("Copy all"), () => CopyAsync(_positionBox.Text ?? ""))),
                "Where each wizard on your server is standing, as X, Y and Z, updated twice a second while this tab is open. "
                + "Rotation is the way the wizard faces in radians, the way the game files store it, and Angle is the same in degrees. "
                + "Width is the wizard's size, which is always 1. "
                + "It reads what the game already tells your server; nothing is added to the game. The newest mover is on top. "
                + "Select any part of the text to copy it, or use the button. Positions are exact to about 4 units.")));
    }

    private Control ToolsTab() {
        return Page(
            Fold("download", "Settings", "Updates",
                $"{Edition.NAME} {SelfUpdate.CurrentVersion}.",
                Row(Action(_updateButton, UpdateAsync)),
                AutoUpdateBox(),
                AutoInstallSwitch()),
            ServerSourceCard(),
            AuroriumCard(),
            Fold("folder", "Tools", "Files and folders", $"Installed in {_manager.InstallFolder}.",
                new WrapPanel {
                    Children = {
                        Spaced(Action(Plain("Open log folder"), () => Open(_manager.LogFolder))),
                        Spaced(Action(Plain("Open settings folder"), () => Open(_manager.ConfigFolder))),
                        Spaced(Action(Plain("Open install folder"), () => Open(_manager.InstallFolder))),
                        Spaced(Danger(Action(Plain("Uninstall Imlight"), UninstallAsync))),
                    },
                }));
    }

    // ---------------------------------------------------------------------
    // Status and log
    // ---------------------------------------------------------------------

    // The 2-second check runs away from the window: finding the server's process, trying its login port (up to half a
    // second) and reading Imlight.ini once for every switch made the whole window skip each time it ran.
    private bool _refreshing;
    private bool _refreshAgain;

    private async void Refresh() {
        if (_refreshing) {
            _refreshAgain = true;

            return;
        }

        _refreshing = true;
        try {
            var manager = _manager;
            var config = manager.ConfigFile;
            var (status, ini, stamp) = await Task.Run(() => {
                var written = File.Exists(config) ? File.GetLastWriteTimeUtc(config) : DateTime.MinValue;

                return (manager.Status(), IniFile.ReadAll(config), written);
            });

            // A switch saved while it was reading: what it read is old, so read again.
            if ((File.Exists(config) ? File.GetLastWriteTimeUtc(config) : DateTime.MinValue) != stamp) {
                _refreshAgain = true;
            }
            else {
                ShowStatus(status, ini);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Imlight.ini is being written; the next check reads it.
        }
        finally {
            _refreshing = false;
        }

        RefreshLog();
        if (_refreshAgain) {
            _refreshAgain = false;
            Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Background);
        }
    }

    private void ShowStatus(ServerStatus status, Dictionary<(string Section, string Key), string> ini) {
        bool Has(string section, string key) => ini.ContainsKey((section, key));
        bool Switch(string section, string key) => ini.TryGetValue((section, key), out var value) && value.Equals("true", StringComparison.OrdinalIgnoreCase);
        _dot.Background = status.Ready ? GOOD : status.Running ? WARN : MUTED;
        _statusText.Text = status.Ready ? "Running, ready for players" : status.Running ? "Starting…" : "Stopped";
        ManagerTheme.PaintBadge(_statusBadge, _statusText, _dot.Background);
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
        _modesCard.IsVisible = _randomizerCard.IsVisible || _dieDeleteCard.IsVisible;
        _dieDelete.IsChecked = status.DieDelete == true;
        _trainersCard.IsVisible = Has("Trainers", "FreeTraining");
        foreach (var (key, _, toggle) in _trainerSwitches) {
            toggle.IsChecked = Switch("Trainers", key);
        }

        _battleCard.IsVisible = Has("Battle", "ManaOnFizzle");
        ShowTamedDropsSwitch(Has("TamedDrops", "Enabled"), Switch("TamedDrops", "Enabled"));
        foreach (var (section, key, _, toggle) in _battleSwitches) {
            toggle.IsChecked = Switch(section, key);
            toggle.IsEnabled = Has(section, key);
        }

        if (!_pointsPerLevel.IsFocused) {
            _pointsPerLevel.Value = int.TryParse(ini.GetValueOrDefault(("Trainers", "TrainingPointsPerLevel")), out var points) ? points : 1;
        }

        _updatingRandomizer = false;
    }

    // One shown line of the server log.
    private sealed record LogRow(string Text, IBrush Brush);

    // The log is read away from the window, only while the Logs tab shows, and only when the file has changed; the
    // lines already on screen are kept and only new ones are added (redrawing all 400 every 2 seconds made it skip).
    private (string? File, long Length, DateTime Written, bool HideDebug, bool HideWarnings) _logReadFrom;
    private bool _logTabShown;
    private bool _logReading;
    private bool _logAgain;

    private async void RefreshLog() {
        // Paused: keep what is on screen so it can be read or copied.
        if (_pauseLog.IsChecked == true || !_logTabShown) {
            return;
        }

        if (_logReading) {
            _logAgain = true;

            return;
        }

        var manager = _manager;
        var hideDebug = _hideDebug.IsChecked == true;
        var hideWarnings = _hideWarnings.IsChecked == true;
        var last = _logReadFrom;
        _logReading = true;
        try {
            var (from, raw, rows) = await Task.Run(() => {
                var file = manager.NewestLogFile();
                var stamp = (file?.FullName, file?.Length ?? 0, file?.LastWriteTimeUtc ?? DateTime.MinValue, hideDebug, hideWarnings);
                if (stamp == last) {
                    return (stamp, -1, (List<LogRow>?) null);
                }

                var lines = manager.ReadLog();
                var shown = lines
                    .Where(line => !((hideDebug && line.Level is "DBG" or "VRB") || (hideWarnings && line.Level == "WRN")))
                    .Select(line => new LogRow(line.Count > 1 ? $"{line.Text}   ×{line.Count}" : line.Text, line.Level switch {
                        "WRN" => WARN,
                        "ERR" or "FTL" => BAD,
                        "DBG" or "VRB" => MUTED,
                        _ => LOG_TEXT,
                    }))
                    .ToList();

                return (stamp, lines.Count, shown);
            });
            if (rows is not null && _pauseLog.IsChecked != true) {
                _logReadFrom = from;
                UiTiming.Measure("show server log", () => ShowLog(raw, rows));
            }
        }
        catch (Exception) {
            // Being rotated or written; the next check reads it.
        }
        finally {
            _logReading = false;
        }

        if (_logAgain) {
            _logAgain = false;
            RefreshLog();
        }
    }

    private void ShowLog(int raw, List<LogRow> rows) {
        var atBottom = _logScroll.Offset.Y + _logScroll.Viewport.Height >= _logScroll.Extent.Height - 30;
        _shownLog = rows.Select(row => row.Text).ToList();
        _logEmpty.IsVisible = raw == 0;
        if (raw == 0) {
            _logRows.Clear();

            return;
        }

        // The lines on screen that are still in the log's end: drop the oldest ones above them, update the last one
        // (its ×count can grow) and add the new ones under it.
        var start = 0;
        for (; start < _logRows.Count - 1; start++) {
            var kept = _logRows.Count - 1 - start;
            if (kept > rows.Count) {
                continue;
            }

            var same = true;
            for (var i = 0; i < kept && same; i++) {
                same = _logRows[start + i] == rows[i];
            }

            if (same) {
                break;
            }
        }

        if (_logRows.Count == 0 || start > 50) {
            // Mostly new (the first read, a filter changed): one new list instead of hundreds of changes.
            _logRows = new System.Collections.ObjectModel.ObservableCollection<LogRow>(rows);
            _logLines.ItemsSource = _logRows;
        }
        else {
            for (var i = 0; i < start; i++) {
                _logRows.RemoveAt(0);
            }

            for (var i = 0; i < rows.Count; i++) {
                if (i >= _logRows.Count) {
                    _logRows.Add(rows[i]);
                }
                else if (_logRows[i] != rows[i]) {
                    _logRows[i] = rows[i];
                }
            }

            while (_logRows.Count > rows.Count) {
                _logRows.RemoveAt(_logRows.Count - 1);
            }
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
            // A server saved without a name is named after its address, so the name is dotted out with it.
            string[] secrets = [server.Host, server.Port.ToString()];
            var hidden = HideIn(server.Name, secrets);
            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            var shown = false;
            void ShowLabel() => label.Text = shown
                ? $"{server.Name}    {server.Host}:{server.Port}"
                : $"{hidden}    {Reveal(server.Host, false)}:{Reveal(server.Port.ToString(), false)}";
            ShowLabel();
            var toggle = Plain("Show");
            toggle.Click += (_, _) => {
                shown = !shown;
                toggle.Content = ManagerTheme.ButtonContent(shown ? "Hide" : "Show");
                ShowLabel();
            };

            _serverRows.Children.Add(new DockPanel {
                Children = {
                    WithDock(Row(
                        toggle,
                        Action(Primary("Play"), async () => {
                            await GameClient.LaunchAsync(_manager.InstallFolder, server.Host, server.Port);
                            Say($"Starting Wizard101 on {hidden}.");
                        }),
                        Danger(Action(Plain("Remove"), async () => {
                            if (await ConfirmAsync($"Remove {hidden}?")) {
                                GameClient.RemoveServer(_manager.InstallFolder, server.Name);
                                RenderServers();
                                Say($"{hidden} was removed.");
                            }
                        }))), Dock.Right),
                    label,
                },
            });
        }
    }

    private async Task SaveServerAsync() {
        var port = int.TryParse(_newServerPort.Text, out var parsed) ? parsed : 0;
        var saved = await GameClient.AddServerAsync(_manager.InstallFolder, _newServerName.Text ?? "", _newServerHost.Text ?? "", port);
        _newServerName.Text = _newServerHost.Text = _newServerPort.Text = "";
        RenderServers();
        Say($"{HideIn(saved.Name, [saved.Host, saved.Port.ToString()])} was saved.");
    }

    // ---------------------------------------------------------------------
    // Lookup
    // ---------------------------------------------------------------------

    private async Task LoadLookupAsync() {
        var folder = _manager.IdLookupFolder;
        var changed = new[] { "ids.csv", "zones.txt", "quests.csv", "bundles.csv" }
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
        if (SpellMode) {
            RenderSpells();

            return;
        }

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
        if (entry.Kind == "Bundle" && entry.Typed is not null) {
            // Runs .bundle for the wizard playing on this PC (Niduus, 2026-10-21: bundles picked in the app).
            buttons.Children.Add(Action(Plain("Send to my gifts"), () => {
                _manager.SendCommand(entry.Typed);
                Say($"Sent {entry.Name} to your gifts. Click the gift button in the game to claim it.");

                return Task.CompletedTask;
            }));
        }

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

    private async Task SaveClientLogAsync() {
        var folder = _manager.InstallFolder;
        var log = await Task.Run(() => GameClient.ReadClientLog(folder));
        if (log.File is null) {
            throw new PanelException(log.Reason ?? "Wizard101 has not written a log yet. Play once, then look again.");
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop)) {
            desktop = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        var target = Path.Combine(desktop, $"Wizard101 game log {DateTime.Now:yyyy-MM-dd HH-mm-ss}.txt");
        await Task.Run(() => {
            using var source = new FileStream(log.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = File.Create(target);
            source.CopyTo(copy);
        });
        Say($"Saved the game log to your desktop as \"{Path.GetFileName(target)}\". Drag it into the chat to send it.");
    }

    /// <param name="said">What the message bar says instead of the text, for text that stays hidden (an address).</param>
    private async Task CopyAsync(string text, string? said = null) {
        var clipboard = GetTopLevel(this)?.Clipboard ?? throw new PanelException("The clipboard is not available.");
        await clipboard.SetTextAsync(text);
        var lines = text.Split('\n').Length;
        Say(said ?? (lines > 1 || text.Length > 80 ? $"Copied {lines} lines. Paste them with Ctrl+V." : $"Copied {text}"));
    }

    // ---------------------------------------------------------------------
    // Tools
    // ---------------------------------------------------------------------

    // The update that was downloaded and built in the background and waits for a restart: what it is ("Imlight 1.2.0", or
    // the server from another GitHub copy) and the program that installs it.
    private (string Name, string Path)? _readyUpdate;
    private bool _preparingUpdate;

    /// <summary>
    /// When the app opens, gets a newer Imlight ready in the background (unless turned off on the Tools tab), then
    /// asks to restart into it. The server keeps running meanwhile. Another GitHub copy (the Server source card) also
    /// updates when its branch has a newer commit; a folder on this PC never does by itself.
    /// </summary>
    private async Task AutoUpdateAsync() {
        if (!_manager.AutoUpdate) {
            return;
        }

        if (await SelfUpdate.NewerReleaseAsync() is not null) {
            await PrepareUpdateAsync();

            return;
        }

        if (await _manager.Source.NewerCommitAsync(_manager.InstallFolder) is not null) {
            await PrepareSourceUpdateAsync();
        }
    }

    private async Task UpdateAsync() {
        if (_readyUpdate is not null) {
            await OfferRestartAsync();

            return;
        }

        if (_preparingUpdate) {
            Say("The update is still downloading and building. The app asks to restart when it is ready.");

            return;
        }

        if (await SelfUpdate.NewerReleaseAsync() is not null) {
            await PrepareUpdateAsync();

            return;
        }

        var source = _manager.Source;
        if (source.Kind == ServerSourceKind.Folder) {
            Say($"You have the newest {Edition.NAME}. Your server comes from {source.Describe()}, so it only changes when you " +
                "click Rebuild from folder under Server source on the Tools tab.");

            return;
        }

        if (source.Kind == ServerSourceKind.GitHub && await source.NewerCommitAsync(_manager.InstallFolder) is not null) {
            await PrepareSourceUpdateAsync();

            return;
        }

        // Nothing newer: the old way rebuilds the same version, which repairs a broken install.
        var newest = source.Kind == ServerSourceKind.GitHub
            ? $"GitHub shows nothing newer for {source.Describe()}."
            : $"You already have the newest Imlight ({SelfUpdate.CurrentVersion}).";
        if (await ConfirmAsync($"{newest} Rebuild it anyway? " +
                "The server stops, this app closes, and the update opens in its own window. Your settings, accounts and characters are kept.")) {
            _manager.RunInstaller("--update");
            Close();
        }
    }

    /// <summary>
    /// Downloads the newest release and has it build the server in the Pending folder, hidden, while everything keeps
    /// running. Then asks to restart into it.
    /// </summary>
    private async Task PrepareUpdateAsync() {
        _preparingUpdate = true;
        try {
            var whenReady = _manager.AutoInstallUpdates ? "the app restarts into it by itself when it is ready" : "the app asks to restart when it is ready";
            Say("A new Imlight is out. It is downloading and building in the background, so keep playing. " +
                $"This can take a few minutes; {whenReady}. The Update Imlight button on the Tools tab shows how far along it is.");
            _updateButton.Content = "Downloading...";
            var progress = new Progress<int>(percent => _updateButton.Content = percent >= 0 ? $"Downloading {percent}%" : "Downloading...");
            if (await SelfUpdate.DownloadNewerAsync(Path.Combine(_manager.InstallFolder, "Pending"), progress) is not { } download) {
                Say("The new Imlight could not be downloaded. Check your internet connection, then click Update Imlight on the Tools tab.", true);

                return;
            }

            Say($"Imlight {download.Version} is downloaded and building in the background. Keep playing; {whenReady}.");
            await BuildPendingAsync(download.Path, $"Imlight {download.Version}");
        }
        finally {
            _preparingUpdate = false;
            _updateButton.Content = _readyUpdate is not null ? "Restart to update" : "Update Imlight";
        }
    }

    /// <summary>
    /// Builds the server from the Server source card's choice (another GitHub copy, or a source folder on this PC) in
    /// the Pending folder, hidden, with this version of the app. Then asks to restart into it, as a new release does.
    /// </summary>
    private async Task PrepareSourceUpdateAsync() {
        if (_readyUpdate is not null || _preparingUpdate) {
            await UpdateAsync();

            return;
        }

        var exe = Path.Combine(_manager.InstallFolder, InstalledCommands.INSTALLED_EXE);
        if (!File.Exists(exe)) {
            exe = Environment.ProcessPath ?? throw new PanelException($"{exe} is missing. Run {Edition.RELEASE_ASSET} again to repair Imlight.");
        }

        _preparingUpdate = true;
        try {
            var name = $"The server from {_manager.Source.Describe()}";
            var whenReady = _manager.AutoInstallUpdates ? "the app restarts into it by itself when it is ready" : "the app asks to restart when it is ready";
            Say($"{name} is building in the background, so keep playing. This can take a few minutes; {whenReady}.");
            await BuildPendingAsync(exe, name);
        }
        finally {
            _preparingUpdate = false;
            _updateButton.Content = _readyUpdate is not null ? "Restart to update" : "Update Imlight";
        }
    }

    /// <summary>Runs <paramref name="program"/> --prepare-update hidden, showing how far along it is, then offers to restart into it.</summary>
    private async Task BuildPendingAsync(string program, string name) {
        var start = new ProcessStartInfo(program) {
            WorkingDirectory = _manager.InstallFolder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("--prepare-update");
        start.ArgumentList.Add("--dir");
        start.ArgumentList.Add(_manager.InstallFolder);
        using var process = Process.Start(start)
            ?? throw new PanelException("The update could not be started.");
        _updateButton.Content = "Building...";
        var startedAt = DateTime.Now;
        process.OutputDataReceived += (_, e) => {
            // The build's own progress lines, shown under the button's "Building" so it never looks stuck.
            if (!string.IsNullOrWhiteSpace(e.Data)) {
                var line = e.Data.Trim();
                Dispatcher.UIThread.Post(() => {
                    var minutes = (int) (DateTime.Now - startedAt).TotalMinutes;
                    _updateButton.Content = $"Building... {minutes} min";
                    Say($"Building {name} in the background: {(line.Length > 140 ? line[..140] + "..." : line)}");
                });
            }
        };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) {
            Say($"{name} could not be built. The reason is in setup-log.txt in the install folder (Tools tab, Open install folder).", true);

            return;
        }

        _readyUpdate = (name, program);
        await OfferRestartAsync(_manager.AutoInstallUpdates);
    }

    /// <summary>
    /// Restarts into the ready update after asking, or straight away when <paramref name="withoutAsking"/> (the
    /// Install updates automatically switch on the Tools tab). Either way the server restarts with it if it was running.
    /// </summary>
    private async Task OfferRestartAsync(bool withoutAsking = false) {
        if (_readyUpdate is not { } update) {
            return;
        }

        var installFolder = _manager.InstallFolder;
        var running = await Task.Run(() => InstalledCommands.IsServerRunning(installFolder));
        if (withoutAsking) {
            Say($"{update.Name} is ready and installs by itself now" + (running ? "; the server restarts with it." : "."));
        }
        else if (!await ConfirmAsync($"{update.Name} is ready. Restart Imlight now to use it?" + (running
                ? " The server restarts with it, so anyone playing is disconnected for a moment."
                : ""))) {
            Say($"{update.Name} is ready. Click Update Imlight on the Tools tab when you want to restart into it.");

            return;
        }

        var start = new ProcessStartInfo(update.Path) { WorkingDirectory = _manager.InstallFolder, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "--apply-update", "--dir", _manager.InstallFolder, "--wait-pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture) }) {
            start.ArgumentList.Add(arg);
        }

        if (running) {
            start.ArgumentList.Add("--start-server");
        }

        // Closing the app stops the server; everyone playing is told why first.
        ServerManager.StopNotice = "The server is updating now. You will be disconnected; log in again in a few minutes.";
        Process.Start(start)?.Dispose();
        Close();
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
        // Each May Cast card's key gets what the card does, so it can be searched by kind (Niduus: typing Aura shows every aura).
        _settings = _manager.ReadSettings()
            .Select(group => (group.Section, group.Section != PET_CASTING ? group.Settings : group.Settings
                .Select(s => PetCastCards.Details.TryGetValue(s.Key, out var card)
                    ? s with { Help = s.Help.Length > 0 ? $"{card}\n{s.Help}" : card }
                    : s)
                .ToList()))
            .ToList();
        _pending.Clear();
        UpdateSaveBar();
        UiTiming.Measure("build settings", BuildSettings);
        RenderSettings();
    }

    // Every section and row of the Settings tab is built once (when Imlight.ini is read) and searching only shows or
    // hides them: building hundreds of rows again on every key typed froze the window for most of a second.
    private readonly List<(string Section, Section Fold, List<(Setting Setting, Control Row)> Rows)> _settingFolds = [];
    private readonly List<(Setting Setting, Control Row)> _petCastRowList = [];
    private readonly TextBlock _petCastNone = Sub("");
    private readonly DispatcherTimer _settingDelay = new() { Interval = TimeSpan.FromMilliseconds(200) };

    private void BuildSettings() {
        _settingsList.Children.Clear();
        _settingFolds.Clear();
        _petCastRowList.Clear();
        _petCastRows.Children.Clear();
        (_petCastSearch.Parent as Panel)?.Children.Remove(_petCastSearch);
        (_petCastRows.Parent as Panel)?.Children.Remove(_petCastRows);
        foreach (var (section, settings) in _settings) {
            var rows = new StackPanel();
            var list = new List<(Setting Setting, Control Row)>();
            if (section == PET_CASTING) {
                rows.Children.Add(WithMargin(_petCastSearch, new Thickness(0, 4, 0, 4)));
                rows.Children.Add(_petCastRows);
            }

            foreach (var setting in settings) {
                var row = SettingRow(setting);
                list.Add((setting, row));
                (section == PET_CASTING ? _petCastRows : rows).Children.Add(row);
            }

            if (section == PET_CASTING) {
                _petCastRowList.AddRange(list);
                _petCastRows.Children.Add(_petCastNone);
            }

            var (icon, accent) = SettingLook(section);
            var fold = Fold(icon, accent, section, "", rows);
            _settingFolds.Add((section, fold, list));
            _settingsList.Children.Add(fold);
        }
    }

    private void RenderSettings() {
        var query = _settingSearch.Text?.Trim() ?? "";
        foreach (var (section, fold, rows) in _settingFolds) {
            var matches = 0;
            var petMatches = new List<Setting>();
            foreach (var (setting, row) in rows) {
                var match = query.Length == 0 || $"{section} {setting.Key} {setting.Help}".Contains(query, StringComparison.OrdinalIgnoreCase);
                matches += match ? 1 : 0;
                if (section == PET_CASTING) {
                    if (match) {
                        petMatches.Add(setting);
                    }
                }
                else {
                    row.IsVisible = match;
                }
            }

            fold.IsVisible = matches > 0;
            if (matches == 0) {
                continue;
            }

            if (section == PET_CASTING) {
                RenderPetCastRows(petMatches);
            }

            fold.SetBadge(matches == 1 ? "1 setting" : $"{matches} settings");
            fold.SetOpen(query.Length > 0 || section == "Randomizer" || (section == PET_CASTING && _petCastSearch.Text?.Trim().Length > 0));
        }
    }

    // The [PetCasting] section's own search: only its rows change, so the search box keeps its focus.
    private void RenderPetCastRows(List<Setting> settings) {
        var query = _petCastSearch.Text?.Trim() ?? "";
        var allowed = settings.ToHashSet();
        var shown = 0;
        foreach (var (setting, row) in _petCastRowList) {
            row.IsVisible = allowed.Contains(setting)
                && (query.Length == 0 || $"{setting.Key} {setting.Help}".Contains(query, StringComparison.OrdinalIgnoreCase));
            shown += row.IsVisible ? 1 : 0;
        }

        _petCastNone.Text = $"No May Cast setting matches \"{query}\".";
        _petCastNone.IsVisible = shown == 0;
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
            if (IsAddressSetting(setting.Key)) {
                // Dotted out until shown, like the address on the Play tab; typing still works while hidden.
                box.PasswordChar = '•';
                var toggle = Plain("Show");
                toggle.Click += (_, _) => {
                    box.RevealPassword = !box.RevealPassword;
                    toggle.Content = ManagerTheme.ButtonContent(box.RevealPassword ? "Hide" : "Show");
                };
                input = new DockPanel { Children = { WithDock(WithMargin(toggle, new Thickness(8, 0, 0, 0)), Dock.Right), box } };
            }
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

    /// <summary>The settings that hold an address or a port: LoginServerPort, GameServerIP, PublicAddress, the database's URL...</summary>
    private static bool IsAddressSetting(string key)
        => key.EndsWith("Port", StringComparison.OrdinalIgnoreCase) || key.EndsWith("IP", StringComparison.Ordinal)
            || key.EndsWith("Address", StringComparison.OrdinalIgnoreCase)
            || key is "PatchServerInternalUrl" or "PlayerDatabaseUrl" or "SeqSinkUrl";

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
        BuildSettings();
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
        var rename = Action(Plain("Rename"), async () => {
            if (await AskAsync($"New username for {account.Username}", secret: false, account.Username) is { } text && text != account.Username) {
                using var accounts = _manager.Accounts();
                await accounts.RenameAsync(account.Username, text);
                Say($"{account.Username} is now {text.Trim()}. They log in with the new name; their characters stay with the account.");
                await LoadAccountsAsync();
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

        // Each account's id, the number the server knows it by (Niduus, 2026-10-23: "give them ID's as well").
        var copyId = Action(Plain("Copy ID"), () => CopyAsync(account.AccountId));
        copyId.Padding = new Thickness(6, 1);
        copyId.FontSize = 11;
        var idRow = new StackPanel {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 0, 0),
            Children = {
                WithCenter(new SelectableTextBlock { Text = $"ID {account.AccountId}", Foreground = MUTED, FontSize = 12 }),
                copyId,
            },
        };

        return ManagerTheme.Item(new DockPanel {
            Children = {
                WithDock(Row(level, rename, password, lockButton), Dock.Right),
                WithDock(WithMargin(WithCenter(ManagerTheme.Tile("user", account.IsLocked ? BAD : ManagerTheme.Accent("Accounts"), 30)), new Thickness(0, 0, 12, 0)), Dock.Left),
                new StackPanel {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { new TextBlock { Text = account.Username, FontWeight = FontWeight.SemiBold }, details, idRow },
                },
            },
        });
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

    private async Task<string?> AskAsync(string question, bool secret, string? initial = null) {
        var box = new TextBox { PasswordChar = secret ? '•' : '\0', Text = initial };
        var dialog = Dialog(question, box, out _);
        var confirmed = await dialog.ShowDialog<bool>(this);

        return confirmed ? box.Text : null;
    }

    private static Window Dialog(string question, Control? input, out Button ok) {
        var dialog = new Window {
            Title = Edition.NAME, Width = 420, SizeToContent = SizeToContent.Height, CanResize = false, Background = BACKGROUND,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        ManagerTheme.Apply(dialog);
        ok = Primary("OK");
        ok.Click += (_, _) => dialog.Close(true);
        var cancel = Plain("Cancel");
        cancel.Click += (_, _) => dialog.Close(false);
        var body = new StackPanel {
            Margin = new Thickness(22), Spacing = 16,
            Children = {
                new DockPanel {
                    Children = {
                        WithDock(WithMargin(new Border { VerticalAlignment = VerticalAlignment.Top, Child = ManagerTheme.Tile("info", ManagerTheme.Accent("accent"), 32) }, new Thickness(0, 0, 14, 0)), Dock.Left),
                        WithCenter(new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap, FontSize = ManagerTheme.FONT_HEADING }),
                    },
                },
            },
        };
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

    // Each tab's icon and the one line its header says it is for.
    private static readonly Dictionary<string, (string Icon, string Line)> s_tabs = new() {
        ["Play"] = ("play", "Play Wizard101 on your server, on a friend's, or with friends."),
        ["Commands"] = ("terminal", "Every chat command the server knows, ready to copy."),
        ["Lookup"] = ("search", "Find any command, item, spell, creature, zone or quest and its ID."),
        ["Quests"] = ("flag", "Which quests your wizard has done and which come next, with Start and Teleport buttons."),
        ["Phrases"] = ("quote", "Every line of the game's text, by number, for .ca and boss cheat lines."),
        ["Spells"] = ("cards", "Every spell card, as the game draws it."),
        ["Gear"] = ("shield", "Every hat, robe, wand, mount, pet and bundle, as the game's item tooltip shows it."),
        ["Recipes"] = ("flask", "Every crafting recipe: what it needs, what it makes, and recipe vendors of your own."),
        ["Bosses"] = ("crown", "Change a boss's health, stats, spells and cheats."),
        ["Customs"] = ("puzzle", "Your own creatures and areas, built from the game's."),
        ["Guides"] = ("info", "Make a custom, and step-by-step help for everything else."),
        ["Server"] = ("server", "Start and stop the server, and switch its modes."),
        ["Logs"] = ("log", "What the server and the game are writing down, as it happens."),
        ["Settings"] = ("gear", "Every setting of the server, with what each one does."),
        ["Accounts"] = ("users", "Who can log in to your server, and what they may do."),
        ["Game Files"] = ("folder", "Read the game's data files, without ever changing them."),
        ["Tools"] = ("wrench", "Update Imlight, open its folders, or remove it."),
    };

    /// <summary>A tab of the side bar: its icon and name there, and a header with its one line above its page.</summary>
    private static TabItem Tab(string header, Func<Control> build) {
        var content = UiTiming.Measure($"build {header} tab", build);
        var (icon, line) = s_tabs[header];
        var accent = ManagerTheme.Accent(header);

        return new TabItem {
            Tag = header,
            Classes = { "nav" },
            Header = ManagerTheme.TabHeader(icon, accent, header),
            Content = new DockPanel { Children = { WithDock(ManagerTheme.PageHeader(icon, accent, header, line), Dock.Top), content } },
        };
    }

    private static Control Page(params Control[] cards) {
        var stack = new StackPanel { Margin = new Thickness(24, 12, 24, 24), Spacing = ManagerTheme.GAP, MaxWidth = ManagerTheme.PAGE_MAX_WIDTH };
        stack.Children.AddRange(cards);

        return new ScrollViewer { Content = stack };
    }

    private static Border Card(Control content) => new() {
        Background = ManagerTheme.SURFACE_BRUSH, BorderBrush = LINE, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(ManagerTheme.RADIUS), Padding = new Thickness(ManagerTheme.PAD), Child = content,
    };

    /// <summary>
    /// A section of a tab, as the block editor's are: a card with an icon tile in the tab's colour (or another named
    /// colour), a title and what it is for, that folds shut when its header is clicked.
    /// </summary>
    private static Section Fold(string icon, string accent, string title, string help, params Control[] body) {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.AddRange(body);
        return new Section(icon, ManagerTheme.Accent(accent), title, help, stack);
    }

    /// <summary>A section that starts folded shut, for what is used now and then (its title still shows).</summary>
    private static Section Shut(Section section) {
        section.SetOpen(false);

        return section;
    }

    /// <summary>A row with a "?" at its end that shows the longer words, so they are not in the way.</summary>
    private static Control WithHint(Control row, string hint) => new DockPanel {
        Children = { WithDock(WithMargin(HintBubble(hint), new Thickness(10, 0, 0, 0)), Dock.Right), row },
    };

    /// <summary>A switch (or a number box) on a row of its own, with what it does on the left.</summary>
    private static Control SwitchRow(string name, Control input, string? help = null) {
        var text = new StackPanel {
            VerticalAlignment = VerticalAlignment.Center, Spacing = 2,
            Children = { new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap } },
        };
        if (help is not null) {
            text.Children.Add(Sub(help));
        }

        input.VerticalAlignment = VerticalAlignment.Center;
        if (input is ToggleSwitch toggle) {
            toggle.MinWidth = 0;
        }

        return new Border {
            Background = ManagerTheme.SURFACE_HIGH_BRUSH, CornerRadius = new CornerRadius(ManagerTheme.RADIUS_SMALL), Padding = new Thickness(14, 6, 10, 6),
            Child = new DockPanel { Children = { WithDock(WithMargin(input, new Thickness(16, 0, 0, 0)), Dock.Right), text } },
        };
    }

    /// <summary>A log's dark, rounded well.</summary>
    private static Control LogWell(Control log) => new Border {
        CornerRadius = new CornerRadius(ManagerTheme.RADIUS_SMALL), ClipToBounds = true, BorderBrush = LINE, BorderThickness = new Thickness(1), Child = log,
    };

    /// <summary>The icon of a section of Imlight.ini on the Settings tab, and the colour of its tile.</summary>
    private static (string Icon, string Accent) SettingLook(string section) => section switch {
        "Global Settings" => ("gear", "Settings"),
        "Logging" => ("log", "Logs"),
        "Locale" or "World" => ("globe", "Lookup"),
        "Combat" or "Battle" or "Archmastery" => ("sword", "Bosses"),
        "PvP" => ("swords", "Bosses"),
        "Die Delete" => ("skull", "bad"),
        "Character" => ("user", "Accounts"),
        "Pets" or "Hatching" or PET_CASTING => ("paw", "Play"),
        "Jewels" or "April Fools" => ("sparkle", "Customs"),
        "Minigames" => ("dice", "Commands"),
        "Randomizer" => ("dice", "Customs"),
        "Seamstress" or "CrownShop" or "Bazaar" or "HallVendors" or "Bank" => ("bag", "Game Files"),
        "Trainers" or "TrainerHall" => ("school", "Lookup"),
        "Housing" => ("home", "Play"),
        "Instance" => ("layers", "Customs"),
        "ID Lookup" => ("search", "Lookup"),
        "Database" => ("server", "Server"),
        _ when section.Contains("Server", StringComparison.OrdinalIgnoreCase) => ("server", "Server"),
        _ => ("sliders", "Tools"),
    };

    private static TextBlock Heading(string text) => new() { Text = text, FontSize = ManagerTheme.FONT_HEADING + 1, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) };

    private static TextBlock Sub(string text) => new() { Text = text, Foreground = MUTED, TextWrapping = TextWrapping.Wrap };

    // Buttons come in three kinds, the same on every tab: primary (the accent colour), plain and danger (red).
    private static Button Primary(string text) => ManagerTheme.MakeButton(text, primary: true);

    private static Button Plain(string text) => ManagerTheme.MakeButton(text, primary: false);

    private static Button Danger(Button button) {
        button.Classes.Add("danger");

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
