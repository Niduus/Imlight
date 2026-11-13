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
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Imlight.Setup;

/// <summary>
/// Play > Overlay (Niduus, 2026-11-11): what Ahziael draws over the Wizard101 window (<see cref="OverlayHost"/>), a
/// no-code designer for it ("No code programming with full customization including font, color, gradients and so on.
/// The works, all of it"), and the server's switch that turns away players without it ([Overlay] RequiredToPlay).
/// </summary>
internal sealed partial class ManagerWindow {

    private const string OVERLAY = "Overlay";
    private const string OVERLAY_SECTION = "Overlay";

    private OverlayEditor? _ovEditorPage;
    private readonly TextBlock _ovStatus = Sub("");
    private readonly DispatcherTimer _ovTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    // The server's rule.
    private readonly ToggleSwitch _ovRequired = new() { OnContent = "On", OffContent = "Off" };
    private readonly NumericUpDown _ovGrace = new() { Minimum = 3, Maximum = 300, Increment = 5, FormatString = "0", Width = 120 };
    private readonly TextBox _ovKick = new() { AcceptsReturn = false, TextWrapping = TextWrapping.Wrap, Watermark = OVERLAY_DEFAULT_KICK };
    private readonly TextBlock _ovRuleNote = Sub("");

    private const string OVERLAY_DEFAULT_KICK =
        "This server needs the Ahziael overlay. Get Ahziael from github.com/Niduus/Imlight/releases (ImlightManager.exe), add this server under Play, click Play in Ahziael, and log in again.";

    private static string[]? s_systemFonts;

    private Control OverlayTab() {
        _ovEditorPage = new OverlayEditor(this, (text, error) => Say(text, error), () => _manager.IsInstalled || RemoteSession.Current is not null ? Path.Combine(_manager.CustomsFolder, OverlayDesign.SERVER_FILE) : null);
        _ovTimer.Tick += (_, _) => _ovStatus.Text = OverlayStatus();
        _ovTimer.Start();
        _ovStatus.Text = OverlayStatus();
        // The button on the game opens this tab.
        OverlayHost.DesignRequested += () => {
            if (OverlayHost.Standalone) {
                return;
            }

            // On the game first; its "Full designer" button brings this tab up.
            if (OverlayGameEditor.Open(ShowOverlayDesigner)) {
                return;
            }

            ShowOverlayDesigner();
        };

        // An overlay button that opens Ahziael.
        OverlayButtons.AhziaelRequested += () => {
            if (WindowState == WindowState.Minimized) {
                WindowState = WindowState.Normal;
            }

            Show();
            Activate();
        };

        void ShowOverlayDesigner() {

            if (WindowState == WindowState.Minimized) {
                WindowState = WindowState.Normal;
            }

            Show();
            Activate();
            SelectTab(OVERLAY);
        }

        // The server's switch.
        _ovRequired.IsEnabled = false;
        _ovRequired.IsCheckedChanged += async (_, _) => {
            if (!_updatingRandomizer) {
                await Guard(() => SetSwitchAsync(OVERLAY_SECTION, "RequiredToPlay", "Require the Ahziael overlay", _ovRequired.IsChecked == true));
            }
        };
        _ovGrace.ValueChanged += (_, _) => {
            if (!_updatingRandomizer && _ovGrace.Value is { } v && _manager.HasSetting(OVERLAY_SECTION, "GraceSeconds")
                && _manager.GetSetting(OVERLAY_SECTION, "GraceSeconds") != ((int) v).ToString()) {
                _manager.SaveSettings([new Setting(OVERLAY_SECTION, "GraceSeconds", ((int) v).ToString(), "")]);
                _settings = [];
                Say($"Players without the overlay now get {(int) v} seconds before they are disconnected. It applies right away.");
            }
        };

        return WidePage(
            Fold("sparkle", "Play", "Game overlay", "What Ahziael draws over the Wizard101 window.",
                _ovEditorPage.EnabledRow(),
                _ovStatus,
                Sub("It sticks to the game window: it moves and resizes with it, stays through loading screens and when you click " +
                    "another window, and only goes away while the game is minimized. Click the small sparkle button in the game's " +
                    "bottom-right corner to open the designer below. Wizard101 in true full screen hides every other window, so play in a " +
                    "window (or the game's windowed full-screen mode) to see it. Nothing inside the game is changed.")),
            Fold("play", "Play", "Overlay without Ahziael open",
                "Run just the overlay, on its own, with Ahziael closed.",
                WithHint(Row(Action(Primary("Start the overlay on its own"), StartStandaloneOverlayAsync)),
                    "\"Ahziael Overlay\" is a small program that only draws the overlay, with the same design and the same sparkle button " +
                    "(which opens a designer window). It shows a sparkle icon by the clock, where you can open the designer or stop it. " +
                    "Only one of them draws at a time: with both open, whichever started first draws."),
                Sub("Servers that don't require the overlay don't care whether you have it, with or without Ahziael.")),
            Fold("sliders", "Customs", "Design it", "Fonts, colours, gradients, boxes, shadows, see-through and position. No code: everything is a box to fill in.",
                _ovEditorPage.Build()),
            Fold("shield", "Server", "Require the overlay on my server",
                "Turn away players whose Ahziael overlay isn't running.",
                SwitchRow("Require the Ahziael overlay", _ovRequired,
                    "On: a player whose PC has no Ahziael overlay running (in Ahziael or on its own) is disconnected with the message below. " +
                    "Off (the start): anyone can play. Works right away, no restart, and players already in the game are checked too."),
                SwitchRow("Seconds before the kick", _ovGrace, "How long a player may be in the game without the overlay first."),
                new StackPanel {
                    Spacing = 4,
                    Children = { new TextBlock { Text = "What they see" }, _ovKick, WithMargin(Row(Action(Plain("Save message"), SaveKickMessageAsync)), new Thickness(0, 4, 0, 0)) },
                },
                Sub("How it knows: the overlay on the player's PC keeps a small connection to your server (the same port as Remote Admin, " +
                    "12410 unless you changed it) and says every 5 seconds how many game windows it draws on. That many game " +
                    "connections from that internet address are let in. It sends no account, password or files. " +
                    "Friends on the internet need that port open too: click \"Let friends connect\" on the Play tab once more after turning this on."),
                Sub("It's a friendly check, not anti-cheat: someone who writes their own program could pretend to be the overlay."),
                _ovRuleNote));
    }

    internal static string OverlayStatus() {
        if (!OperatingSystem.IsWindows()) {
            return "The overlay works on Windows only.";
        }

        if (!OverlayHost.IsDrawing) {
            return OverlayHost.Standalone ? "Ahziael is open and drawing the overlay, so this one waits. Close Ahziael and this one takes over."
                : "Ahziael Overlay (on its own) is drawing the overlay. Changes made here show there too.";
        }

        var games = OverlayHost.GameWindows;
        var drawing = games == 0 ? "Wizard101 is not open, so there is nothing to draw on yet."
            : $"Drawing on {games} Wizard101 window{(games == 1 ? "" : "s")}.";

        return drawing + " " + OverlayHost.Link.Status;
    }

    private Task StartStandaloneOverlayAsync() {
        var exe = OverlayStandalone.Exe(_manager.InstallFolder);
        Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "--overlay", "--dir", _manager.InstallFolder }, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = _manager.InstallFolder })?.Dispose();
        Say(OverlayHost.IsDrawing
            ? "Ahziael Overlay started. Ahziael is drawing for now; when you close Ahziael, Ahziael Overlay takes over by itself."
            : "Ahziael Overlay started.");

        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------------
    // The server's rule
    // ---------------------------------------------------------------------

    private void ShowOverlayRule(Dictionary<(string Section, string Key), string> ini) {
        var has = ini.TryGetValue((OVERLAY_SECTION, "RequiredToPlay"), out var value);
        _ovRequired.IsEnabled = has;
        _ovRequired.IsChecked = has && value!.Equals("true", StringComparison.OrdinalIgnoreCase);
        _ovGrace.IsEnabled = ini.ContainsKey((OVERLAY_SECTION, "GraceSeconds"));
        _ovGrace.Value = int.TryParse(ini.GetValueOrDefault((OVERLAY_SECTION, "GraceSeconds")), out var grace) ? grace : 15;
        _ovKick.IsEnabled = ini.ContainsKey((OVERLAY_SECTION, "KickMessage"));
        if (!_ovKick.IsFocused) {
            _ovKick.Text = ini.GetValueOrDefault((OVERLAY_SECTION, "KickMessage")) ?? "";
        }

        _ovRuleNote.Text = has ? "" : "Click \"Update Ahziael\" at the top of the app to get this switch.";
        _ovRuleNote.IsVisible = !has;
    }

    private Task SaveKickMessageAsync() {
        if (!_manager.HasSetting(OVERLAY_SECTION, "KickMessage")) {
            throw new PanelException("Click \"Update Ahziael\" at the top of the app first, so the server has this setting.");
        }

        // One line in Imlight.ini.
        var text = (_ovKick.Text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        _manager.SaveSettings([new Setting(OVERLAY_SECTION, "KickMessage", text, "")]);
        _settings = [];
        Say(text.Length == 0 ? "Players without the overlay now see the standard message. It applies right away."
            : "Saved the message players without the overlay see. It applies right away.");

        return Task.CompletedTask;
    }

    /// <summary>
    /// The no-code designer: the picture, the list of texts and boxes, and the boxes to fill in for the picked one. The
    /// same on Ahziael's Overlay tab and in the window of the overlay on its own (<see cref="OverlayDesignerWindow"/>).
    /// </summary>
    internal sealed class OverlayEditor {

        // The picture is the game window as the design sees it (Niduus, 2026-11-11: "only be able to be placed within the
        // regions of the client UI size ... multi clients too with odd res"): a running game window's own shape, or a
        // size picked from the list, in design pixels (OverlayDesign.Fit).
        private double PREVIEW_W = OverlayDesign.REFERENCE_W;
        private double PREVIEW_H = OverlayDesign.REFERENCE_H;
        private readonly ComboBox _ovSize = new() { MinWidth = 260 };
        private List<(string Name, double W, double H, bool Game)> _ovSizes = [];
        private string _ovSizesKey = "";
        private bool _ovSizePicked;

        private readonly Window _owner;
        private readonly Action<string, bool> _say;

        /// <summary>The file of this PC's server's own overlay, or null when this PC has no server.</summary>
        private readonly Func<string?> _serverFile;

        // Niduus, 2026-11-11: "create 2 seperate sections in the UI overlay: Server and Client". The player's own design
        // (overlay.json) and the server's (ServerOverlay.json, sent to everyone who plays there); _ovDesign is the one
        // being changed. Both share the one Ahziael line.
        private OverlayDesign _ovClient;
        private OverlayDesign? _ovServer;
        private bool _ovServerPart;
        private readonly StackPanel _ovParts = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        private readonly TextBlock _ovPartNote = Sub("");
        private readonly DispatcherTimer _ovLockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private OverlayDesign _ovDesign;
        private OverlayElement? _ovSelected;
        private readonly Border _ovPreview = new() {
            CornerRadius = new CornerRadius(8), ClipToBounds = true, BorderThickness = new Thickness(1),
        };
        private readonly ListBox _ovList = new() { MinHeight = 180, MaxHeight = 320 };
        private readonly StackPanel _ovEditor = new() { Spacing = 10 };
        private readonly ToggleSwitch _ovEnabled = new() { OnContent = "On", OffContent = "Off" };
        private bool _ovFilling;
        private Panel? _ovCanvas;
        private Drag? _ovDrag;
        private int _ovListPressed = -1;

        private enum DragMode { Move, Right, Bottom, Corner }

        /// <summary>A drag on the picture: what, how, and where it started.</summary>
        private sealed record Drag(OverlayElement Element, DragMode Mode, Point Start, double X, double Y, double Width, double Height, Rect Bounds, IReadOnlyList<Rect> Others);

        private OverlayGrid _ovGrid = OverlayGrid.Load(OverlayHost.InstallFolder);
        private readonly List<(Point From, Point To)> _ovGuides = [];

        /// <summary>How near an edge (in picture pixels) a press takes the size instead of the place.</summary>
        private const double EDGE = 12;

        /// <summary>True for the editor right on the game (<see cref="OverlayGameEditor"/>): no stand-in picture, no lists.</summary>
        private readonly bool _inGame;
        private int _ovSeenStamp;
        private DateTime _ovServerFileTime;
        private Rect? _ovDragRect;
        private readonly TextBox _ovQuickWords = new() { Width = 220, Watermark = "Words of the picked text" };
        private readonly ContentControl _ovQuickParent = new();

        internal OverlayEditor(Window owner, Action<string, bool> say, Func<string?> serverFile, bool inGame = false) {
            _inGame = inGame;
            _ovSeenStamp = OverlayHost.DesignStamp;
            _owner = owner;
            _say = say;
            _serverFile = serverFile;
            _ovClient = OverlayHost.Design.Copy();
            _ovDesign = _ovClient;
            _ovPreview.BorderBrush = LINE;
            _ovEnabled.IsChecked = OverlayHost.ShowsMine(_ovClient);
            _ovEnabled.IsCheckedChanged += (_, _) => {
                if (!_ovFilling) {
                    _ovClient.Enabled = _ovEnabled.IsChecked == true;
                    OverlayChanged(false);
                    Say(_ovClient.Enabled ? "Your texts and boxes show over Wizard101 again."
                        : "Your texts and boxes are hidden. The Ahziael line, the server's texts and boxes and the sparkle button always show.");
                }
            };
            // Niduus, 2026-11-11: "if this is mandatory, it's counter productive to turn it off". While the server you play
            // on needs the overlay, the switch stays on and can't be changed.
            _ovLockTimer.Tick += (_, _) => ShowLock();
            _ovSize.SelectionChanged += (_, _) => {
                if (!_ovFilling) {
                    _ovSizePicked = true;
                    UseSize();
                }
            };
            _ovLockTimer.Start();
            _owner.Closed += (_, _) => _ovLockTimer.Stop();
            ShowLock();
            _ovList.SelectionChanged += (_, _) => {
                if (!_ovFilling && _ovList.SelectedIndex >= 0) {
                    _ovSelected = _ovList.SelectedIndex == 0 ? _ovDesign.Credit : _ovDesign.Elements[_ovList.SelectedIndex - 1];
                    RenderOverlayEditor();
                    RenderOverlayPreview();
                }
            };
            _ovSelected = _ovDesign.Credit;
            if (_inGame) {
                _ovPreview.CornerRadius = new CornerRadius(0);
                _ovPreview.BorderThickness = new Thickness(0);
            }

            _ovQuickWords.TextChanged += (_, _) => {
                if (!_ovFilling && _ovSelected is { IsBox: false } picked && picked != _ovDesign.Credit && picked.Text != (_ovQuickWords.Text ?? "")) {
                    picked.Text = _ovQuickWords.Text ?? "";
                    OverlayChanged();
                }
            };
            _ovPreview.Focusable = true;
            _ovPreview.KeyDown += PreviewKey;
            _ovPreview.PointerPressed += PreviewPressed;
            _ovPreview.PointerMoved += PreviewMoved;
            _ovPreview.PointerReleased += PreviewReleased;
            _ovPreview.PointerCaptureLost += (_, _) => EndPreviewDrag();
            _ovList.AddHandler(InputElement.PointerPressedEvent, ListPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
            _ovList.AddHandler(InputElement.PointerReleasedEvent, ListReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
            ShowParts();
            FillOverlayList();
            RenderOverlayEditor();
            RenderOverlayPreview();
        }

        private int _ovServerSeen = -1;

        /// <summary>
        /// The design changed somewhere else (the other designer, on the game or in Ahziael, or the other copy): this one
        /// shows it too, keeping the same one picked.
        /// </summary>
        private void Follow() {
            if (_ovDrag is not null) {
                return;
            }

            if (!_ovServerPart && OverlayHost.DesignStamp != _ovSeenStamp) {
                _ovSeenStamp = OverlayHost.DesignStamp;
                var at = _ovSelected is null ? -1 : _ovClient.Elements.IndexOf(_ovSelected);
                _ovClient = OverlayHost.Design.Copy();
                _ovDesign = _ovClient;
                _ovSelected = at >= 0 && at < _ovClient.Elements.Count ? _ovClient.Elements[at] : _ovClient.Credit;
                FillOverlayList();
                RenderOverlayEditor();
                RenderOverlayPreview();
            }
            else if (_ovServerPart && _serverFile() is { } path) {
                var time = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                if (time != _ovServerFileTime && _ovServerFileTime != DateTime.MinValue) {
                    _ovServerFileTime = time;
                    var at = _ovSelected is null || _ovServer is null ? -1 : _ovServer.Elements.IndexOf(_ovSelected);
                    _ovServer = OverlayDesign.LoadFile(path);
                    _ovServer.Credit = _ovClient.Credit;
                    _ovDesign = _ovServer;
                    _ovSelected = at >= 0 && at < _ovServer.Elements.Count ? _ovServer.Elements[at] : _ovClient.Credit;
                    FillOverlayList();
                    RenderOverlayEditor();
                    RenderOverlayPreview();
                }
            }
        }

        // The sizes to show the picture at: each open game window first, then common ones.
        private void FillSizes() {
            List<(string Name, double W, double H, bool Game)> sizes = OverlayHost.GameSizes.Select((g, i) => {
                var fit = OverlayDesign.Fit(g.Width, g.Height);
                return (Name: $"Game window {i + 1}: {g.Width:0} x {g.Height:0}", W: Math.Round(g.Width / fit), H: Math.Round(g.Height / fit), Game: true);
            }).ToList();
            sizes.Add(("1280 x 720 (wide)", 1280, 720, false));
            sizes.Add(("1920 x 1080 (wide)", 1280, 720, false));
            sizes.Add(("1024 x 768 (square-ish)", 1024 / OverlayDesign.Fit(1024, 768), 768 / OverlayDesign.Fit(1024, 768), false));
            sizes.Add(("800 x 600 (small)", 800 / OverlayDesign.Fit(800, 600), 600 / OverlayDesign.Fit(800, 600), false));
            sizes.Add(("2560 x 1080 (extra wide)", 2560 / OverlayDesign.Fit(2560, 1080), 1080 / OverlayDesign.Fit(2560, 1080), false));
            var key = string.Join("|", sizes.Select(x => x.Item1));
            if (key == _ovSizesKey) {
                return;
            }

            var old = _ovSize.SelectedIndex >= 0 && _ovSize.SelectedIndex < _ovSizes.Count ? _ovSizes[_ovSize.SelectedIndex].Name : null;
            _ovSizesKey = key;
            _ovSizes = sizes;
            _ovFilling = true;
            try {
                _ovSize.ItemsSource = sizes.Select(x => x.Item1).ToList();
                var keep = old is null ? -1 : sizes.FindIndex(x => x.Name == old);
                // A game window that opens is shown at once, unless a size was picked by hand.
                _ovSize.SelectedIndex = keep >= 0 && (_ovSizePicked || !sizes[0].Game) ? keep : 0;
            }
            finally {
                _ovFilling = false;
            }

            UseSize();
        }

        private void UseSize() {
            if (_ovSize.SelectedIndex < 0 || _ovSize.SelectedIndex >= _ovSizes.Count) {
                return;
            }

            var (_, w, h, _) = _ovSizes[_ovSize.SelectedIndex];
            if (w == PREVIEW_W && h == PREVIEW_H) {
                return;
            }

            PREVIEW_W = w;
            PREVIEW_H = h;
            if (_ovDrag is null) {
                RenderOverlayPreview();
            }
        }

        private void ShowLock() {
            if (!_inGame) {
                FillSizes();
            }

            Follow();
            var locked = OverlayHost.ServerRequires;
            _ovFilling = true;
            try {
                _ovEnabled.IsEnabled = !locked;
                _ovEnabled.IsChecked = OverlayHost.ShowsMine(_ovClient);
            }
            finally {
                _ovFilling = false;
            }

            // What the linked server sends changed: the picture shows it.
            var seen = OverlayHost.ServerElements.GetHashCode();
            if (seen != _ovServerSeen) {
                _ovServerSeen = seen;
                if (_ovDrag is null) {
                    RenderOverlayPreview();
                }
            }
        }

        // ---------------------------------------------------------------------
        // Server and Client
        // ---------------------------------------------------------------------

        private void ShowParts() {
            _ovParts.Children.Clear();
            var mine = ManagerTheme.MakeButton("Client: my own", primary: !_ovServerPart);
            mine.Click += (_, _) => UsePart(false);
            var server = ManagerTheme.MakeButton("Server: everyone on my server", primary: _ovServerPart);
            server.Click += (_, _) => UsePart(true);
            _ovParts.Children.Add(mine);
            _ovParts.Children.Add(server);
            _ovPartNote.Text = _ovServerPart
                ? "Server: what every player on your server sees, on everyone's game, whatever they made themselves. It is placed " +
                  "right after the Ahziael line, and players' own texts and boxes are moved off it, so they never cover it. " +
                  "Players can't hide it. It reaches them within a few seconds, no restart."
                : "Client: your own texts and boxes, only on your game. They are placed last: the Ahziael line and the server's " +
                  "texts and boxes come first, and yours are moved off them.";
        }

        private void UsePart(bool server) {
            if (server == _ovServerPart) {
                return;
            }

            if (server) {
                if (_serverFile() is not { } path) {
                    Say("This PC has no Imlight server, so there is no server overlay to design here. The server overlay is " +
                        "made in Ahziael on the PC that runs the server.", true);
                    return;
                }

                _ovServer = OverlayDesign.LoadFile(path);
                _ovServer.Credit = _ovClient.Credit;
                _ovDesign = _ovServer;
                _ovServerFileTime = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.UnixEpoch;
            }
            else {
                _ovDesign = _ovClient;
            }

            _ovServerPart = server;
            _ovSelected = _ovDesign.Credit;
            ShowParts();
            FillOverlayList();
            RenderOverlayEditor();
            RenderOverlayPreview();
        }

        /// <summary>A whole new design for the part being changed (Start over, Open a design file).</summary>
        private void UseDesign(OverlayDesign design) {
            if (_ovServerPart) {
                design.Credit = _ovClient.Credit;
                _ovServer = design;
            }
            else {
                design.Enabled = _ovClient.Enabled;
                _ovClient = design;
            }

            _ovDesign = design;
            _ovSelected = _ovDesign.Credit;
        }

        /// <summary>This PC's server's overlay file, from its install folder; null when it has no server.</summary>
        internal static string? ServerFileIn(string installFolder) {
            var ini = InstalledCommands.ConfigFile(installFolder);
            if (!File.Exists(ini)) {
                return null;
            }

            var extra = IniFile.Get(ini, "Database", "SpiralDBExtraPath") ?? "./spiraldb-extra";

            return Path.GetFullPath(Path.Combine(InstalledCommands.ServerFolder(installFolder), extra, "Customs", OverlayDesign.SERVER_FILE));
        }

        private void Say(string text, bool isError = false) => _say(text, isError);

        private Task<bool> ConfirmAsync(string question) => Dialog(question, null, out _).ShowDialog<bool>(_owner);

        private IStorageProvider StorageProvider => _owner.StorageProvider;

        private Button Action(Button button, Func<Task> action) {
            button.Click += async (_, _) => {
                button.IsEnabled = false;
                try {
                    await action();
                }
                catch (Exception ex) when (ex is PanelException) {
                    Say(ex.Message, true);
                }
                catch (Exception ex) {
                    Say($"Something went wrong: {ex.Message}", true);
                }
                finally {
                    button.IsEnabled = true;
                }
            };

            return button;
        }

        internal Control EnabledRow() => SwitchRow("Show my texts and boxes", _ovEnabled,
            "\"Ahziael made by Niduus | " + OverlayHost.Version + "\" (always bottom left), the server's texts and boxes and the sparkle button always show on the game. " +
            "This switch shows or hides only what you made, and stays on while the server you play on needs the overlay. " +
            "Clicks go straight through to the game (only the sparkle button takes them).");

        /// <summary>The game window's inside, in device-independent pixels: the picture takes its shape (on the game).</summary>
        internal void SetGameArea(double width, double height) {
            var fit = OverlayDesign.Fit(width, height);
            var (w, h) = (Math.Round(width / fit), Math.Round(height / fit));
            if (w == PREVIEW_W && h == PREVIEW_H) {
                return;
            }

            PREVIEW_W = w;
            PREVIEW_H = h;
            if (_ovDrag is null) {
                RenderOverlayPreview();
            }
        }

        /// <summary>
        /// The editor right on the game (Niduus, 2026-11-11: "I was also hoping to click the edit button over the game to
        /// make the UI in the game ... I want both methods to work"): the design over the game, pick, drag and size there,
        /// and a small bar to add, remove, change words, or open the full designer.
        /// </summary>
        internal Control BuildInGame(Action fullDesigner, Action done) {
            var remove = Danger(Action(Plain("Remove"), RemoveOverlayElement));
            var full = Plain("Full designer");
            full.Click += (_, _) => fullDesigner();
            var finish = ManagerTheme.MakeButton("Done", primary: true);
            finish.Click += (_, _) => done();
            var bar = new Border {
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 0, 0),
                Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(10), BorderBrush = LINE, BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x14, 0x16, 0x22)),
                Child = new StackPanel {
                    Spacing = 6,
                    Children = {
                        new WrapPanel {
                            Children = {
                                Spaced(_ovParts), Spaced(Block(OverlayElement.TEXT)), Spaced(Block(OverlayElement.BOX)), Spaced(Block(OverlayElement.BUTTON)),
                                Spaced(Action(Plain("Picture"), AddPictureAsync)), Spaced(Action(Plain("Minimap"), () => AddOverlayElement(OverlayElement.MINIMAP))),
                                Spaced(Action(Plain("Quest Tracker"), () => AddOverlayElement(OverlayElement.QUEST_TRACKER))),
                                Spaced(_ovQuickWords), Spaced(_ovQuickParent),
                                Spaced(OvCheck("Grid", _ovGrid.Show, v => { _ovGrid.Show = v; GridChanged(); })),
                                Spaced(OvCheck("Snap to grid", _ovGrid.SnapToGrid, v => { _ovGrid.SnapToGrid = v; GridChanged(); })),
                                Spaced(OvCheck("Snap to pieces", _ovGrid.SnapToPieces, v => { _ovGrid.SnapToPieces = v; GridChanged(); })),
                                Spaced(GridSettingsButton()),
                                Spaced(remove), Spaced(full), Spaced(finish),
                            },
                        },
                        Sub("Click a text or box to pick it, drag to move, drag its right or bottom edge to size it. Drag Text or Box onto the game to add one."),
                    },
                },
            };
            bar.MaxWidth = 1100;

            return new Panel {
                Children = { new Viewbox { Stretch = Stretch.Fill, Child = _ovPreview }, bar },
            };
        }

        private void GridChanged() {
            _ovGrid.Clean();
            _ovGrid.Save(OverlayHost.InstallFolder);
            RenderOverlayPreview();
        }

        /// <summary>Grid and snapping: every choice there is, for the designer in Ahziael.</summary>
        private Control GridControls() {
            var grid = new WrapPanel();
            grid.Children.Add(OvField("Grid", OvCheck("Show the grid", _ovGrid.Show, v => { _ovGrid.Show = v; GridChanged(); })));
            grid.Children.Add(OvField("Square size", OvNumber(_ovGrid.Size, 2, 400, 1, v => { _ovGrid.Size = v; GridChanged(); })));
            grid.Children.Add(OvField("Stronger line every", OvNumber(_ovGrid.MajorEvery, 0, 50, 1, v => { _ovGrid.MajorEvery = (int) v; GridChanged(); })));
            grid.Children.Add(OvField("Grid colour", OvColor(_ovGrid.Color, v => { _ovGrid.Color = v; GridChanged(); })));
            grid.Children.Add(OvField("Grid see-through", OvSlider(_ovGrid.Opacity, 0, 1, v => { _ovGrid.Opacity = v; GridChanged(); })));
            grid.Children.Add(OvField("Snap", Row(
                OvCheck("To the grid", _ovGrid.SnapToGrid, v => { _ovGrid.SnapToGrid = v; GridChanged(); }),
                OvCheck("To other pieces", _ovGrid.SnapToPieces, v => { _ovGrid.SnapToPieces = v; GridChanged(); }),
                OvCheck("Guide lines", _ovGrid.Guides, v => { _ovGrid.Guides = v; GridChanged(); }))));
            grid.Children.Add(OvField("Snap from (pixels)", OvNumber(_ovGrid.SnapDistance, 1, 40, 1, v => { _ovGrid.SnapDistance = v; GridChanged(); })));
            grid.Children.Add(OvField("Guide line colour", OvColor(_ovGrid.GuideColor, v => { _ovGrid.GuideColor = v; GridChanged(); })));

            return new StackPanel {
                Spacing = 4,
                Children = {
                    OvGroup("Grid and snapping", grid),
                    Sub("While dragging, a piece snaps to the window's edges and middle, to other pieces' edges and middles (a guide line shows which), " +
                        "or else to the grid. Hold Alt to drag freely. Arrow keys move the picked one a pixel; with Shift, a grid square. " +
                        "The grid only shows while designing, never on the game."),
                },
            };
        }

        // Everything about the grid, from the bar on the game.
        private Button GridSettingsButton() {
            var button = Plain("Grid settings");
            button.Click += (_, _) => {
                button.Flyout = new Flyout {
                    Placement = PlacementMode.Bottom,
                    Content = new ScrollViewer { MaxHeight = 460, Width = 560, Content = GridControls() },
                };
                button.Flyout.ShowAt(button);
            };

            return button;
        }

        /// <summary>A picture from a file on this PC: PNG, JPEG, GIF or BMP, up to 200 KB.</summary>
        private async Task<string?> PickPictureAsync() {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
                Title = "Pick a picture", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Pictures") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp"] }],
            });
            if (files.Count == 0) {
                return null;
            }

            var path = files[0].TryGetLocalPath() ?? throw new PanelException("Pick a picture that is on this computer.");
            if (new FileInfo(path).Length > OverlayElement.MAX_PICTURE_BYTES) {
                throw new PanelException($"That picture is too big. Pictures can be up to {OverlayElement.MAX_PICTURE_BYTES / 1024} KB; save a smaller copy first.");
            }

            var base64 = OverlayElement.CleanPicture(Convert.ToBase64String(await File.ReadAllBytesAsync(path)));

            return base64.Length > 0 ? base64 : throw new PanelException("That file isn't a PNG, JPEG, GIF or BMP picture.");
        }

        private async Task AddPictureAsync() {
            if (await PickPictureAsync() is not { } picture) {
                return;
            }

            await AddOverlayElement(OverlayElement.PICTURE, null);
            if (_ovSelected is { IsPicture: true } added) {
                added.Picture = picture;
                OverlayChanged();
                RenderOverlayEditor();
            }
        }

        // ---------------------------------------------------------------------
        // Add-ons (OverlayAddon)
        // ---------------------------------------------------------------------

        private readonly StackPanel _ovAddons = new() { Spacing = 8 };

        private Control AddonsSection() {
            FillAddons();

            return OvGroup("Add-ons", new StackPanel {
                Spacing = 8,
                Children = {
                    Sub("Add-ons are ready-made widgets (texts, boxes and pictures that go together) and styles (a look to put on any text or box) " +
                        "that people share as " + OverlayAddon.EXTENSION + " files. Like design files they hold only words, colours, numbers and " +
                        "pictures, so they can't run anything. Make one from your own design to share it."),
                    ShelfSection(),
                    new TextBlock { Text = "Your add-ons", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                    Row(Action(Primary("Add an add-on file"), InstallAddonAsync), Action(Plain("Make an add-on from my design"), MakeAddonAsync)),
                    _ovAddons,
                },
            });
        }

        // Niduus's own add-ons, from his GitHub repo (OverlayAddonShelf).
        private readonly StackPanel _ovShelf = new() { Spacing = 8 };

        private Control ShelfSection() {
            _ = FillShelfAsync(false);

            return new Border {
                BorderBrush = LINE, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8),
                Child = new StackPanel {
                    Spacing = 6,
                    Children = {
                        new TextBlock { Text = "Niduus's add-ons", FontWeight = FontWeight.SemiBold },
                        Sub("Overlay add-ons made by Niduus, ready to download. Ahziael gets them from Niduus/Imlight on GitHub " +
                            "(the " + OverlayAddonShelf.REPO_FOLDER + " folder). Each one is checked the same way as an add-on file, so it can't run anything. " +
                            "After you download one, its pieces show under Your add-ons: click one to put it on your overlay."),
                        Row(Action(Plain("Look again"), () => FillShelfAsync(true)), Action(Plain("Download all"), DownloadAllShelfAsync)),
                        _ovShelf,
                    },
                },
            };
        }

        private async Task FillShelfAsync(bool again) {
            _ovShelf.Children.Clear();
            _ovShelf.Children.Add(Sub("Looking for Niduus's add-ons..."));
            List<OverlayAddonShelf.Item> items;
            try {
                items = await OverlayAddonShelf.ListAsync(again);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) {
                _ovShelf.Children.Clear();
                _ovShelf.Children.Add(Sub("Couldn't reach GitHub just now (no internet, or GitHub asked Ahziael to wait a while). Click Look again in a minute."));
                return;
            }

            ShowShelf(items);
        }

        private void ShowShelf(List<OverlayAddonShelf.Item> items) {
            _ovShelf.Children.Clear();
            if (items.Count == 0) {
                _ovShelf.Children.Add(Sub("Niduus hasn't put any add-ons up yet."));
                return;
            }

            var installed = OverlayAddon.All(OverlayHost.InstallFolder);
            foreach (var item in items) {
                var addon = item.Addon;
                var state = OverlayAddonShelf.StateOf(addon, installed);
                Control button = state == OverlayAddonShelf.State.Have
                    ? new TextBlock { Text = "Downloaded", Foreground = MUTED, VerticalAlignment = VerticalAlignment.Center }
                    : Action(Primary(state == OverlayAddonShelf.State.Newer ? "Get the newer one" : "Download"), () => DownloadShelfAsync(item));
                var what = string.Join(", ", addon.Widgets.Select(w => w.Name.Length > 0 ? w.Name : "Widget")
                    .Concat(addon.Styles.Select(st => "Style: " + (st.Name.Length > 0 ? st.Name : "Look"))));
                _ovShelf.Children.Add(new Border {
                    BorderBrush = LINE, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8),
                    Child = new StackPanel {
                        Spacing = 4,
                        Children = {
                            new DockPanel {
                                Children = {
                                    WithDock(button, Dock.Right),
                                    new TextBlock {
                                        Text = addon.Name + (addon.Author.Length > 0 ? "  by " + addon.Author : "") + (addon.Version.Length > 0 ? "  (" + addon.Version + ")" : ""),
                                        FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                                    },
                                },
                            },
                            Sub(addon.Description.Length > 0 ? addon.Description : $"{addon.Widgets.Count} widget(s), {addon.Styles.Count} style(s)."),
                            Sub("Has: " + what),
                        },
                    },
                });
            }
        }

        private async Task DownloadShelfAsync(OverlayAddonShelf.Item item) {
            InstallFromShelf(item.Addon);
            FillAddons();
            ShowShelf(await OverlayAddonShelf.ListAsync());
            Say($"Downloaded \"{item.Addon.Name}\". Its pieces are under Your add-ons: click one to put it on your overlay.");
        }

        private async Task DownloadAllShelfAsync() {
            var items = await OverlayAddonShelf.ListAsync();
            var installed = OverlayAddon.All(OverlayHost.InstallFolder);
            var wanted = items.Where(i => OverlayAddonShelf.StateOf(i.Addon, installed) != OverlayAddonShelf.State.Have).ToList();
            if (wanted.Count == 0) {
                Say(items.Count == 0 ? "Niduus hasn't put any add-ons up yet." : "You already have all of Niduus's add-ons.");
                return;
            }

            foreach (var item in wanted) {
                InstallFromShelf(item.Addon);
            }

            FillAddons();
            ShowShelf(items);
            Say($"Downloaded {wanted.Count} add-on(s). Their pieces are under Your add-ons.");
        }

        /// <summary>A fresh checked copy each time, so the shelf's own stays as it came.</summary>
        private static void InstallFromShelf(OverlayAddon addon) =>
            OverlayAddon.Install(OverlayHost.InstallFolder, OverlayAddon.FromJson(addon.ToJson()) ?? throw new PanelException("That add-on didn't pass the check."));

        // The add-ons on this PC, read from their folder by FillAddons; ShowAddons draws them with what is on the overlay now.
        private List<OverlayAddon> _ovAddonList = [];

        private void FillAddons() {
            _ovAddonList = OverlayAddon.All(OverlayHost.InstallFolder);
            ShowAddons();
        }

        /// <summary>
        /// The add-ons, each widget with a tick when it is on the overlay being changed now (Niduus, 2026-11-13: "Overlay UI
        /// is not saying what I have active"). Ticking puts it on, unticking takes it off.
        /// </summary>
        private void ShowAddons() {
            _ovAddons.Children.Clear();
            if (_ovAddonList.Count == 0) {
                _ovAddons.Children.Add(Sub("No add-ons yet."));
                return;
            }

            var where = _ovServerPart ? "on the server's overlay" : "on your overlay";
            _ovAddons.Children.Add(Sub($"A ticked widget is {where} now. Tick one to put it on, untick it to take it off."));
            foreach (var addon in _ovAddonList) {
                var pieces = new WrapPanel();
                foreach (var widget in addon.Widgets) {
                    var on = OverlayAddon.OnOverlay(addon, widget, _ovDesign.Elements);
                    var name = widget.Name.Length > 0 ? widget.Name : "Widget";
                    var tick = new CheckBox {
                        Content = on.Count > 0 ? name + "  (on)" : name, IsChecked = on.Count > 0, Margin = new Thickness(0, 0, 14, 4),
                        FontWeight = on.Count > 0 ? FontWeight.SemiBold : FontWeight.Normal,
                    };
                    tick.IsCheckedChanged += async (_, _) => {
                        try {
                            if (tick.IsChecked == true) {
                                AddWidget(addon, widget);
                            }
                            else {
                                await TakeOffWidgetAsync(addon, widget);
                            }
                        }
                        catch (PanelException ex) {
                            Say(ex.Message, true);
                        }

                        ShowAddons();
                    };
                    pieces.Children.Add(tick);
                }

                foreach (var style in addon.Styles) {
                    pieces.Children.Add(Spaced(Action(Plain("Style: " + (style.Name.Length > 0 ? style.Name : "Look")), () => UseStyleAsync(style))));
                }

                var remove = Danger(Action(Plain("Remove"), () => RemoveAddonAsync(addon)));
                _ovAddons.Children.Add(new Border {
                    BorderBrush = LINE, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8),
                    Child = new StackPanel {
                        Spacing = 4,
                        Children = {
                            new DockPanel {
                                Children = {
                                    WithDock(remove, Dock.Right),
                                    new TextBlock {
                                        Text = addon.Name + (addon.Author.Length > 0 ? "  by " + addon.Author : "") + (addon.Version.Length > 0 ? "  (" + addon.Version + ")" : ""),
                                        FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                                    },
                                },
                            },
                            Sub(addon.Description.Length > 0 ? addon.Description : $"{addon.Widgets.Count} widget(s), {addon.Styles.Count} style(s)."),
                            pieces,
                        },
                    },
                });
            }
        }

        private void AddWidget(OverlayAddon addon, OverlayWidget widget) {
            if (_ovDesign.Elements.Count + widget.Elements.Count > OverlayDesign.MAX_ELEMENTS) {
                throw new PanelException($"The overlay holds up to {OverlayDesign.MAX_ELEMENTS} texts, boxes and pictures; that widget doesn't fit.");
            }

            var copies = OverlayAddon.CopyFamily(widget.Elements);
            foreach (var copy in copies) {
                copy.From = OverlayAddon.FromKey(addon, widget);
            }

            _ovDesign.Elements.AddRange(copies);
            _ovSelected = copies.FirstOrDefault(c => c.Parent.Length == 0) ?? copies.LastOrDefault() ?? _ovSelected;
            OverlayChanged();
            RenderOverlayEditor();
            Say($"Put \"{(widget.Name.Length > 0 ? widget.Name : "the widget")}\" on the overlay.");
        }

        private async Task TakeOffWidgetAsync(OverlayAddon addon, OverlayWidget widget) {
            var on = OverlayAddon.OnOverlay(addon, widget, _ovDesign.Elements);
            if (on.Count == 0) {
                return;
            }

            var name = widget.Name.Length > 0 ? widget.Name : "the widget";
            if (!await ConfirmAsync($"Take \"{name}\" off the overlay? Any changes you made to it go with it (ticking it again puts on a fresh one).")) {
                return;
            }

            _ovDesign.Elements.RemoveAll(on.Contains);
            if (_ovSelected is not null && on.Contains(_ovSelected)) {
                _ovSelected = _ovDesign.Credit;
            }

            OverlayChanged();
            RenderOverlayEditor();
            Say($"Took \"{name}\" off the overlay.");
        }

        private Task UseStyleAsync(OverlayStyle style) {
            if (_ovSelected is not { } picked || picked == _ovDesign.Credit) {
                throw new PanelException("Pick one of your texts or boxes first, then click the style.");
            }

            OverlayAddon.ApplyStyle(picked, style.Look);
            OverlayChanged();
            RenderOverlayEditor();

            return Task.CompletedTask;
        }

        private async Task RemoveAddonAsync(OverlayAddon addon) {
            if (!await ConfirmAsync($"Remove the add-on \"{addon.Name}\"? What you already added from it stays on your overlay.")) {
                return;
            }

            if (File.Exists(addon.Path)) {
                File.Delete(addon.Path);
            }

            FillAddons();
        }

        private async Task InstallAddonAsync() {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
                Title = "Add an overlay add-on", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Ahziael overlay add-on") { Patterns = ["*" + OverlayAddon.EXTENSION] }, FilePickerFileTypes.All],
            });
            if (files.Count == 0) {
                return;
            }

            var path = files[0].TryGetLocalPath() ?? throw new PanelException("Ahziael can only open an add-on file that is on this computer.");
            if (new FileInfo(path).Length > OverlayAddon.MAX_BYTES) {
                throw new PanelException("That file is too big to be an overlay add-on.");
            }

            var addon = OverlayAddon.FromJson(await File.ReadAllTextAsync(path))
                ?? throw new PanelException("That file is not an Ahziael overlay add-on.");
            if (!await ConfirmAsync($"Add \"{addon.Name}\"{(addon.Author.Length > 0 ? " by " + addon.Author : "")}? It has {addon.Widgets.Count} widget(s) " +
                                    $"and {addon.Styles.Count} style(s). It can only add texts, boxes and pictures; it can't run anything.")) {
                return;
            }

            OverlayAddon.Install(OverlayHost.InstallFolder, addon);
            FillAddons();
            Say($"Added \"{addon.Name}\". Its widgets and styles are under Add-ons.");
        }

        private async Task MakeAddonAsync() {
            var name = new TextBox { Watermark = "Name", MaxLength = 60 };
            var author = new TextBox { Watermark = "Made by", MaxLength = 60 };
            var about = new TextBox { Watermark = "What it is (optional)", MaxLength = 400, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60 };
            var widget = new CheckBox { Content = "My texts, boxes and pictures, as one widget", IsChecked = _ovDesign.Elements.Count > 0 };
            var picked = _ovSelected is { } p && p != _ovDesign.Credit ? p : null;
            var style = new CheckBox { Content = "The look of the picked one, as a style", IsChecked = picked is not null, IsEnabled = picked is not null };
            var dialog = Dialog("Make an add-on from your design", new StackPanel { Spacing = 8, Children = { name, author, about, widget, style } }, out _);
            if (!await dialog.ShowDialog<bool>(_owner)) {
                return;
            }

            var addon = new OverlayAddon { Name = name.Text ?? "", Author = author.Text ?? "", Description = about.Text ?? "" };
            if (widget.IsChecked == true && _ovDesign.Elements.Count > 0) {
                addon.Widgets.Add(new OverlayWidget { Name = addon.Name, Elements = _ovDesign.Elements.Select(OverlayAddon.Copy).ToList() });
            }

            if (style.IsChecked == true && picked is not null) {
                addon.Styles.Add(new OverlayStyle { Name = picked.Name.Length > 0 ? picked.Name : addon.Name, Look = OverlayAddon.Copy(picked) });
            }

            addon.Clean();
            if (addon.Widgets.Count == 0 && addon.Styles.Count == 0) {
                throw new PanelException("There is nothing to put in it: add some texts or boxes, or pick one for its style.");
            }

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
                Title = "Save the add-on", SuggestedFileName = addon.Name + OverlayAddon.EXTENSION,
                DefaultExtension = OverlayAddon.EXTENSION.TrimStart('.'), ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType("Ahziael overlay add-on") { Patterns = ["*" + OverlayAddon.EXTENSION] }],
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(OverlayDesign.DesignsFolder(OverlayHost.InstallFolder)),
            });
            if (file is null) {
                return;
            }

            await using (var stream = await file.OpenWriteAsync()) {
                stream.SetLength(0);
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(addon.ToJson());
            }

            OverlayAddon.Install(OverlayHost.InstallFolder, addon);
            FillAddons();
            Say($"Saved the add-on as \"{file.Name}\". Send that file to a friend: they add it with \"Add an add-on file\".");
        }

        internal Control Build() {
            var editor = new Grid {
                ColumnDefinitions = new ColumnDefinitions("260,*"), ColumnSpacing = 16,
                Children = {
                    new StackPanel {
                        Spacing = 8,
                        Children = {
                            new TextBlock { Text = "What it shows", FontWeight = FontWeight.SemiBold },
                            _ovList,
                            new WrapPanel {
                                Children = {
                                    Spaced(Action(Plain("Add text"), () => AddOverlayElement(OverlayElement.TEXT))),
                                    Spaced(Action(Plain("Add box"), () => AddOverlayElement(OverlayElement.BOX))),
                                    Spaced(Action(Plain("Add picture"), AddPictureAsync)),
                                    Spaced(Action(Plain("Add button"), () => AddOverlayElement(OverlayElement.BUTTON))),
                                    Spaced(Action(Plain("Add minimap"), () => AddOverlayElement(OverlayElement.MINIMAP))),
                                    Spaced(Action(Plain("Add Quest Tracker"), () => AddOverlayElement(OverlayElement.QUEST_TRACKER))),
                                    Spaced(Action(Plain("Copy"), DuplicateOverlayElement)),
                                    Spaced(Action(Plain("Up"), () => MoveOverlayElement(-1))),
                                    Spaced(Action(Plain("Down"), () => MoveOverlayElement(1))),
                                    Spaced(Danger(Action(Plain("Remove"), RemoveOverlayElement))),
                                },
                            },
                            new TextBlock { Text = "Drag onto the picture", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                            new WrapPanel { Children = { Spaced(Block(OverlayElement.TEXT)), Spaced(Block(OverlayElement.BOX)), Spaced(Block(OverlayElement.BUTTON)) } },
                            Sub("Drag a block onto the picture to put a new text, box or button right there (or just click it). A button can be clicked in the game to run a command, use a cheat, or open the designer or Ahziael; everything else lets clicks through to the game. Drag a line in the list above onto another to change the order."),
                            Sub("A higher Layer is drawn on top; on the same layer, lower in the list is on top. With Spacing above 0, one that laps over another is moved off it, that far away. The Ahziael line is always on top, in full, in the bottom-left corner."),
                        },
                    },
                    WithColumn(_ovEditor, 1),
                },
            };

            return new StackPanel {
                Spacing = 10,
                Children = {
                    _ovParts,
                    _ovPartNote,
                    WithHint(Row(new TextBlock { Text = "Picture size", VerticalAlignment = VerticalAlignment.Center }, _ovSize),
                        "The picture takes the shape of your game window while Wizard101 is open (each open game window is in the list). " +
                        "Texts and boxes stay inside the window. On a bigger or smaller game window everything grows or shrinks with it and " +
                        "keeps to its corner or side, so the design looks as you set it up on every game window, even several at once."),
                    WithMargin(new Viewbox { Stretch = Stretch.Uniform, MaxHeight = 400, Child = _ovPreview }, new Thickness(0, 0, 0, 6)),
                    Sub("The picture is a stand-in for the game window. Click a text or box to pick it, drag it to move it, and drag its right or bottom edge (or the corner) to size it. The Ahziael line always sits in the bottom-left corner and can't be moved or changed. The outlined one is the one you are changing. Changes show on the game at once."),
                    GridControls(),
                    editor,
                    AddonsSection(),
                    WithHint(Row(Action(Plain("Save design to a file"), ExportOverlayAsync), Action(Plain("Open a design file"), ImportOverlayAsync),
                            Danger(Action(Plain("Start over"), ResetOverlayAsync))),
                        "A design file (" + OverlayDesign.SHARE_EXTENSION + ") holds only words, colours and numbers, so it is safe to share: it can't run anything. " +
                        "Opening one replaces your design (Start over brings back the plain one)."),
                },
            };
        }

        // ---------------------------------------------------------------------
        // The list and the preview
        // ---------------------------------------------------------------------

        private void FillOverlayList() {
            _ovFilling = true;
            try {
                var names = new List<string> { "Ahziael line (fixed, always shown)" };
                names.AddRange(_ovDesign.Elements.Select(e => {
                    var name = ListName(e);
                    var depth = Depth(e);

                    return (depth > 0 ? new string(' ', depth * 3) + "↳ " : "") + (e.IsMinimap ? "◎ " : e.IsQuestTracker ? "☰ " : e.IsPicture ? "▣ " : e.IsBox ? "▭ " : e.IsButton ? "◉ " : "T  ") + name + (e.Locked ? " (locked)" : "") + (e.Visible ? "" : " (hidden)");
                }));
                _ovList.ItemsSource = names;
                _ovList.SelectedIndex = _ovSelected == _ovDesign.Credit || _ovSelected is null ? 0 : _ovDesign.Elements.IndexOf(_ovSelected) + 1;
            }
            finally {
                _ovFilling = false;
            }

            // The add-ons' ticks follow what is on the overlay (added, removed, the other part picked, or changed on the game).
            ShowAddons();
        }

        private void RenderOverlayPreview() {
            var words = new OverlayRenderer.Words(OverlayHost.Version, string.IsNullOrEmpty(OverlayHost.Link.ServerLabel) ? "127.0.0.1" : OverlayHost.Link.ServerLabel);
            if (_inGame) {
                // Right on the game: the game itself is the picture, a little darker so it is plain this is editing.
                _ovPreview.Child = new Panel {
                    Width = PREVIEW_W, Height = PREVIEW_H,
                    Children = {
                        new Border { Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)) },
                        (_ovCanvas = (Panel) (_ovServerPart
                            ? OverlayRenderer.Build(_ovClient, words, _ovSelected, showMine: false, server: _ovServer?.Elements)
                            : OverlayRenderer.Build(_ovClient, words, _ovSelected, OverlayHost.ShowsMine(_ovClient), PreviewServer()))),
                        new OverlayGridLayer(_ovGrid, _ovGuides.ToList()),
                    },
                };

                return;
            }

            _ovPreview.Child = new Panel {
                Width = PREVIEW_W, Height = PREVIEW_H,
                Children = {
                    // A stand-in for the game: sky, hills and a little sun.
                    new Border {
                        Background = new LinearGradientBrush {
                            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                            GradientStops = { new GradientStop(Color.FromRgb(0x3A, 0x6F, 0xC4), 0), new GradientStop(Color.FromRgb(0x9C, 0xC8, 0xF0), 0.62),
                                new GradientStop(Color.FromRgb(0x4E, 0x8A, 0x3C), 0.63), new GradientStop(Color.FromRgb(0x2D, 0x5A, 0x24), 1) },
                        },
                    },
                    new Border {
                        Width = 120, Height = 120, CornerRadius = new CornerRadius(60), Margin = new Thickness(0, 80, 160, 0),
                        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                        Background = new RadialGradientBrush { GradientStops = { new GradientStop(Color.FromRgb(0xFF, 0xF2, 0xA8), 0), new GradientStop(Color.FromArgb(0, 0xFF, 0xE0, 0x70), 1) } },
                    },
                    (_ovCanvas = (Panel) (_ovServerPart
                        ? OverlayRenderer.Build(_ovClient, words, _ovSelected, showMine: false, server: _ovServer?.Elements)
                        : OverlayRenderer.Build(_ovClient, words, _ovSelected, OverlayHost.ShowsMine(_ovClient), PreviewServer()))),
                    new OverlayGridLayer(_ovGrid, _ovGuides.ToList()),
                },
            };
        }

        // ---------------------------------------------------------------------
        // Click and drag on the picture
        // ---------------------------------------------------------------------

        /// <summary>The text or box under this point of the picture (the one drawn on top first), and where on it.</summary>
        private (OverlayElement Element, Rect Bounds)? HitTest(Point point) {
            if (_ovCanvas is null) {
                return null;
            }

            for (var i = _ovCanvas.Children.Count - 1; i >= 0; i--) {
                var child = _ovCanvas.Children[i];
                var part = OverlayRenderer.PartOf(child);
                // Only the part being changed; the Ahziael line can't be moved (pick it in the list to read about it).
                if (part == OverlayRenderer.OverlayPanel.CREDIT || (part == OverlayRenderer.OverlayPanel.SERVER) != _ovServerPart) {
                    continue;
                }

                if (child.Tag is OverlayElement element && child.Bounds.Inflate(EDGE / 2).Contains(point)) {
                    return (element, child.Bounds);
                }
            }

            return null;
        }

        private static DragMode ModeAt(Rect bounds, Point point, bool credit) {
            if (credit) {
                return DragMode.Move;
            }

            var right = Math.Abs(point.X - bounds.Right) <= EDGE;
            var bottom = Math.Abs(point.Y - bounds.Bottom) <= EDGE;

            return right && bottom ? DragMode.Corner : right ? DragMode.Right : bottom ? DragMode.Bottom : DragMode.Move;
        }

        private static Cursor CursorFor(DragMode mode) => new(mode switch {
            DragMode.Right => StandardCursorType.SizeWestEast,
            DragMode.Bottom => StandardCursorType.SizeNorthSouth,
            DragMode.Corner => StandardCursorType.BottomRightCorner,
            _ => StandardCursorType.SizeAll,
        });

        private void PreviewPressed(object? sender, PointerPressedEventArgs e) {
            if (_ovCanvas is null || !e.GetCurrentPoint(_ovPreview).Properties.IsLeftButtonPressed) {
                return;
            }

            var point = e.GetPosition(_ovCanvas);
            _ovPreview.Focus();
            if (HitTest(point) is not var (element, bounds)) {
                return;
            }

            var credit = element == _ovDesign.Credit;
            // Everything else on the picture, to snap to.
            // (not its own children, which move with it).
            var family = Descendants(element);
            var others = _ovCanvas.Children.Where(c => c.Tag != element && c.Tag is OverlayElement o && !family.Contains(o) && c.Bounds.Width > 0).Select(c => c.Bounds).ToList();
            if (_ovSelected != element) {
                _ovSelected = element;
                FillOverlayList();
                RenderOverlayEditor();
                RenderOverlayPreview();
            }

            if (element.Locked) {
                Say("That one is locked in place. Untick \"Locked\" in its boxes to move it.");
                e.Handled = true;
                return;
            }

            _ovDrag = new Drag(element, ModeAt(bounds, point, credit), point, element.X, element.Y,
                element.Width > 0 ? element.Width : bounds.Width, element.Height > 0 ? element.Height : bounds.Height, bounds, others);
            _ovPreview.Cursor = CursorFor(_ovDrag.Mode);
            e.Pointer.Capture(_ovPreview);
            e.Handled = true;
        }

        private void PreviewMoved(object? sender, PointerEventArgs e) {
            if (_ovCanvas is null) {
                return;
            }

            var point = e.GetPosition(_ovCanvas);
            if (_ovDrag is not { } drag) {
                // Only showing what a press would do there.
                _ovPreview.Cursor = HitTest(point) is var (element, bounds)
                    ? CursorFor(ModeAt(bounds, point, element == _ovDesign.Credit))
                    : Cursor.Default;
                return;
            }

            var dx = Math.Round(point.X - drag.Start.X);
            var dy = Math.Round(point.Y - drag.Start.Y);
            var element2 = drag.Element;
            // Which way X and Y count from the corner or side it is pinned to.
            var (sx, sy) = Signs(element2.Anchor);
            _ovGuides.Clear();
            var free = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            if (drag.Mode == DragMode.Move) {
                // Never off the game window: it stops at the edge.
                dx = Math.Clamp(dx, -drag.Bounds.Left, Math.Max(0, PREVIEW_W - drag.Bounds.Right));
                dy = Math.Clamp(dy, -drag.Bounds.Top, Math.Max(0, PREVIEW_H - drag.Bounds.Bottom));
                if (!free) {
                    var moved = drag.Bounds.Translate(new Vector(dx, dy));
                    dx += SnapAxis(moved.Left, moved.Right, moved.Center.X, true, drag.Others);
                    dy += SnapAxis(moved.Top, moved.Bottom, moved.Center.Y, false, drag.Others);
                    dx = Math.Clamp(dx, -drag.Bounds.Left, Math.Max(0, PREVIEW_W - drag.Bounds.Right));
                    dy = Math.Clamp(dy, -drag.Bounds.Top, Math.Max(0, PREVIEW_H - drag.Bounds.Bottom));
                }
                element2.X = drag.X + sx * dx;
                element2.Y = drag.Y + sy * dy;
                _ovDragRect = drag.Bounds.Translate(new Vector(dx, dy));
            }
            else {
                // Sizing from the right or bottom edge keeps the left and top where they are, up to the window's edge.
                dx = Math.Min(dx, Math.Max(0, PREVIEW_W - drag.Bounds.Right));
                dy = Math.Min(dy, Math.Max(0, PREVIEW_H - drag.Bounds.Bottom));
                if (!free) {
                    // Only the edge being dragged snaps.
                    if (drag.Mode is DragMode.Right or DragMode.Corner) {
                        dx += SnapEdge(drag.Bounds.Right + dx, true, drag.Others);
                    }

                    if (drag.Mode is DragMode.Bottom or DragMode.Corner) {
                        dy += SnapEdge(drag.Bounds.Bottom + dy, false, drag.Others);
                    }

                    dx = Math.Min(dx, Math.Max(0, PREVIEW_W - drag.Bounds.Right));
                    dy = Math.Min(dy, Math.Max(0, PREVIEW_H - drag.Bounds.Bottom));
                }
                if (drag.Mode is DragMode.Right or DragMode.Corner) {
                    var width = Math.Max(10, drag.Width + dx);
                    element2.Width = width;
                    element2.X = drag.X + KeepStart(element2.Anchor, true) * (width - drag.Width);
                }

                if (drag.Mode is DragMode.Bottom or DragMode.Corner) {
                    var height = Math.Max(10, drag.Height + dy);
                    element2.Height = height;
                    element2.Y = drag.Y + KeepStart(element2.Anchor, false) * (height - drag.Height);
                }
            }

            _ovDesign.Clean();
            RenderOverlayPreview();
            e.Handled = true;
        }

        private void PreviewReleased(object? sender, PointerReleasedEventArgs e) {
            if (_ovDrag is null) {
                return;
            }

            e.Pointer.Capture(null);
            EndPreviewDrag();
            e.Handled = true;
        }

        private void EndPreviewDrag() {
            if (_ovDrag is null) {
                return;
            }

            var drag = _ovDrag;
            _ovDrag = null;
            if (drag.Mode == DragMode.Move && _ovDragRect is { } rect) {
                Repin(drag.Element, rect);
            }

            _ovDragRect = null;
            _ovGuides.Clear();
            OverlayChanged(false);
            RenderOverlayEditor();
        }

        // The lines on one axis a piece can snap to: the window's edges and middle, and every other piece's edges and middle.
        private List<double> Targets(bool across, IReadOnlyList<Rect> others) {
            var size = across ? PREVIEW_W : PREVIEW_H;
            var lines = new List<double> { 0, size / 2, size };
            if (_ovGrid.SnapToPieces) {
                foreach (var r in others) {
                    lines.AddRange(across ? [r.Left, r.Center.X, r.Right] : [r.Top, r.Center.Y, r.Bottom]);
                }
            }

            return lines;
        }

        /// <summary>How far to move a dragged piece on one axis so an edge or its middle lands on a line, or on the grid.</summary>
        private double SnapAxis(double start, double end, double middle, bool across, IReadOnlyList<Rect> others) {
            var best = double.MaxValue;
            var line = 0.0;
            foreach (var target in Targets(across, others)) {
                foreach (var edge in new[] { start, end, middle }) {
                    var by = target - edge;
                    if (Math.Abs(by) <= _ovGrid.SnapDistance && Math.Abs(by) < Math.Abs(best)) {
                        (best, line) = (by, target);
                    }
                }
            }

            if (best != double.MaxValue) {
                Guide(line, across);
                return best;
            }

            return _ovGrid.SnapToGrid ? _ovGrid.ToGrid(start) - start : 0;
        }

        /// <summary>How far to move the edge being sized so it lands on a line, or on the grid.</summary>
        private double SnapEdge(double edge, bool across, IReadOnlyList<Rect> others) {
            var best = double.MaxValue;
            var line = 0.0;
            foreach (var target in Targets(across, others)) {
                var by = target - edge;
                if (Math.Abs(by) <= _ovGrid.SnapDistance && Math.Abs(by) < Math.Abs(best)) {
                    (best, line) = (by, target);
                }
            }

            if (best != double.MaxValue) {
                Guide(line, across);
                return best;
            }

            return _ovGrid.SnapToGrid ? _ovGrid.ToGrid(edge) - edge : 0;
        }

        private void Guide(double at, bool across) => _ovGuides.Add(across
            ? (new Point(at, 0), new Point(at, PREVIEW_H))
            : (new Point(0, at), new Point(PREVIEW_W, at)));

        // Arrow keys move the picked one by a pixel (with Shift, by a grid square).
        private void PreviewKey(object? sender, KeyEventArgs e) {
            if (_ovSelected is not { } picked || picked == _ovDesign.Credit || picked.Locked) {
                return;
            }

            var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? _ovGrid.Size : 1;
            var (dx, dy) = e.Key switch {
                Key.Left => (-step, 0.0), Key.Right => (step, 0.0), Key.Up => (0.0, -step), Key.Down => (0.0, step), _ => (0.0, 0.0),
            };
            if (dx == 0 && dy == 0) {
                return;
            }

            var (sx, sy) = Signs(picked.Anchor);
            picked.X += sx * dx;
            picked.Y += sy * dy;
            OverlayChanged(false);
            RenderOverlayEditor();
            e.Handled = true;
        }

        /// <summary>
        /// Pinned to the corner or side it was dropped nearest to, at the same place, so it stays there on a game window
        /// of another shape (a wider one moves right-hand pieces right, not the left-hand ones).
        /// </summary>
        private void Repin(OverlayElement e, Rect rect) => Repin(e, rect, AreaFor(e));

        // A child is pinned in its parent, the rest in the window.
        private static void Repin(OverlayElement e, Rect rect, Rect area) {
            var (cx, cy) = (rect.Center.X - area.X, rect.Center.Y - area.Y);
            var column = cx < area.Width / 3 ? 0 : cx > area.Width * 2 / 3 ? 2 : 1;
            var row = cy < area.Height / 3 ? 0 : cy > area.Height * 2 / 3 ? 2 : 1;
            e.Anchor = (OverlayAnchor) (row * 3 + column);
            e.X = Math.Round(column switch { 0 => rect.Left - area.Left, 2 => area.Right - rect.Right, _ => rect.Left - area.Left - (area.Width - rect.Width) / 2 });
            e.Y = Math.Round(row switch { 0 => rect.Top - area.Top, 2 => area.Bottom - rect.Bottom, _ => rect.Top - area.Top - (area.Height - rect.Height) / 2 });
        }

        // ---------------------------------------------------------------------
        // Parents and children
        // ---------------------------------------------------------------------

        /// <summary>Where a piece is drawn on the picture now, or null.</summary>
        private Rect? DrawnAt(OverlayElement e)
            => _ovCanvas?.Children.FirstOrDefault(c => c.Tag == e) is { } child && child.Bounds.Width > 0 ? child.Bounds : null;

        private OverlayElement? ParentOf(OverlayElement e)
            => e.Parent.Length == 0 ? null : _ovDesign.Elements.FirstOrDefault(p => p.Id == e.Parent);

        /// <summary>What a piece is placed in: its parent's place, or the whole window.</summary>
        private Rect AreaFor(OverlayElement e)
            => ParentOf(e) is { } parent && DrawnAt(parent) is { } area ? area : new Rect(0, 0, PREVIEW_W, PREVIEW_H);

        /// <summary>Its children, their children, and so on.</summary>
        private List<OverlayElement> Descendants(OverlayElement e) {
            var found = new List<OverlayElement>();
            var ids = new HashSet<string> { e.Id };
            for (var grew = true; grew;) {
                grew = false;
                foreach (var other in _ovDesign.Elements) {
                    if (other.Parent.Length > 0 && ids.Contains(other.Parent) && !found.Contains(other) && other != e) {
                        found.Add(other);
                        ids.Add(other.Id);
                        grew = true;
                    }
                }
            }

            return found;
        }

        private int Depth(OverlayElement e) {
            var depth = 0;
            for (var at = ParentOf(e); at is not null && depth < OverlayDesign.MAX_ELEMENTS; at = ParentOf(at)) {
                depth++;
            }

            return depth;
        }

        /// <summary>The pieces this one can belong to: any but itself and its own children.</summary>
        private List<OverlayElement> ParentChoices(OverlayElement e) {
            var family = Descendants(e);

            return _ovDesign.Elements.Where(o => o != e && !family.Contains(o)).ToList();
        }

        /// <summary>Makes a piece a child of another (or of the window), keeping it where it is on the picture.</summary>
        private void SetParent(OverlayElement e, OverlayElement? parent) {
            var at = DrawnAt(e);
            e.Parent = parent?.Id ?? "";
            if (at is { } rect) {
                var area = parent is not null && DrawnAt(parent) is { } p ? p : new Rect(0, 0, PREVIEW_W, PREVIEW_H);
                Repin(e, rect, area);
            }

            OverlayChanged();
            RenderOverlayEditor();
            Say(parent is null ? "It belongs to the game window again." : $"It now belongs to \"{ListName(parent)}\" and moves with it.");
        }

        private string ListName(OverlayElement e) {
            var i = _ovDesign.Elements.IndexOf(e);
            var name = e.Name.Length > 0 ? e.Name : e.IsMinimap ? $"Minimap {i + 1}" : e.IsQuestTracker ? $"Quest Tracker {i + 1}" : e.IsPicture ? $"Picture {i + 1}" : e.IsBox ? $"Box {i + 1}" : e.Text.Length > 0 ? e.Text.Split('\n')[0] : e.IsButton ? $"Button {i + 1}" : $"Text {i + 1}";

            return name.Length > 34 ? name[..34] + "…" : name;
        }

        /// <summary>A pick list of who it belongs to: the game window, or another piece.</summary>
        private ComboBox ParentPicker(OverlayElement e, double width) {
            var choices = ParentChoices(e);
            var names = new List<string> { "Nothing: the game window" };
            names.AddRange(choices.Select(ListName));
            var index = e.Parent.Length == 0 ? 0 : choices.FindIndex(c => c.Id == e.Parent) + 1;

            return OvCombo(names.ToArray(), Math.Max(0, index), i => SetParent(e, i == 0 ? null : choices[i - 1]), width);
        }

        /// <summary>+1 where X (or Y) grows to the right (or down), -1 where it grows the other way.</summary>
        private static (double X, double Y) Signs(OverlayAnchor anchor) => (
            anchor is OverlayAnchor.TopRight or OverlayAnchor.Right or OverlayAnchor.BottomRight ? -1 : 1,
            anchor is OverlayAnchor.BottomLeft or OverlayAnchor.Bottom or OverlayAnchor.BottomRight ? -1 : 1);

        // How far X moves per pixel of extra size, so the left (top) edge stays put: none from the left, back by all of it
        // from the right, half from the middle.
        private static double KeepStart(OverlayAnchor anchor, bool across) {
            var (sx, sy) = Signs(anchor);
            var middle = across
                ? anchor is OverlayAnchor.Top or OverlayAnchor.Center or OverlayAnchor.Bottom
                : anchor is OverlayAnchor.Left or OverlayAnchor.Center or OverlayAnchor.Right;

            return middle ? 0.5 : (across ? sx : sy) < 0 ? -1 : 0;
        }

        // ---------------------------------------------------------------------
        // Building blocks: drag a new text or box onto the picture, drag the list to change the order
        // ---------------------------------------------------------------------

        private Control Block(string kind) {
            var box = kind == OverlayElement.BOX;
            var button = kind == OverlayElement.BUTTON;
            var block = new Border {
                Padding = new Thickness(14, 8), CornerRadius = new CornerRadius(6), BorderBrush = LINE, BorderThickness = new Thickness(1),
                Background = ManagerTheme.SURFACE_HIGH_BRUSH, Cursor = new Cursor(StandardCursorType.DragMove),
                Child = new TextBlock { Text = box ? "▭  Box" : button ? "◉  Button" : "T  Text", FontWeight = FontWeight.SemiBold },
            };
            ToolTip.SetTip(block, box ? "Drag onto the picture to add a box there"
                : button ? "Drag onto the picture to add a button there (it can be clicked in the game)" : "Drag onto the picture to add a text there");
            block.PointerPressed += (_, e) => {
                if (e.GetCurrentPoint(block).Properties.IsLeftButtonPressed) {
                    e.Pointer.Capture(block);
                    block.Opacity = 0.6;
                    e.Handled = true;
                }
            };
            block.PointerReleased += (_, e) => {
                block.Opacity = 1;
                e.Pointer.Capture(null);
                e.Handled = true;
                Point? at = null;
                if (_ovCanvas is not null) {
                    var point = e.GetPosition(_ovCanvas);
                    if (new Rect(0, 0, PREVIEW_W, PREVIEW_H).Contains(point)) {
                        at = point;
                    }
                }

                try {
                    AddOverlayElement(kind, at);
                }
                catch (PanelException ex) {
                    Say(ex.Message, true);
                }
            };

            return block;
        }

        private int ListIndexAt(PointerEventArgs e) {
            var item = (_ovList.InputHitTest(e.GetPosition(_ovList)) as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);

            return item is null ? -1 : _ovList.IndexFromContainer(item);
        }

        private void ListPressed(object? sender, PointerPressedEventArgs e) {
            if (e.Route == RoutingStrategies.Tunnel) {
                _ovListPressed = e.GetCurrentPoint(_ovList).Properties.IsLeftButtonPressed ? ListIndexAt(e) : -1;
            }
        }

        private void ListReleased(object? sender, PointerReleasedEventArgs e) {
            if (e.Route != RoutingStrategies.Bubble) {
                return;
            }

            var from = _ovListPressed;
            _ovListPressed = -1;
            var to = ListIndexAt(e);
            // Line 0 is the Ahziael line: it stays first. Dropping on it puts the item first.
            if (from < 1 || to < 0 || to == from || from > _ovDesign.Elements.Count) {
                return;
            }

            var element = _ovDesign.Elements[from - 1];
            _ovDesign.Elements.RemoveAt(from - 1);
            _ovDesign.Elements.Insert(Math.Clamp(to - 1, 0, _ovDesign.Elements.Count), element);
            _ovSelected = element;
            OverlayChanged();
            RenderOverlayEditor();
        }

        // The server's texts and boxes on the picture of your own: what the server you play on sent, or else this PC's own.
        private IReadOnlyList<OverlayElement>? PreviewServer() {
            if (OverlayHost.ServerElements.Count > 0) {
                return OverlayHost.ServerElements;
            }

            if (_ovServer is null && _serverFile() is { } path && File.Exists(path)) {
                _ovServer = OverlayDesign.LoadFile(path);
                _ovServer.Credit = _ovClient.Credit;
            }

            return _ovServer?.Elements;
        }

        /// <summary>A change to the design: the picture, the list and the overlay over the game (or the server's file) follow at once.</summary>
        private void OverlayChanged(bool list = true) {
            _ovDesign.Clean();
            if (list) {
                FillOverlayList();
            }

            RenderOverlayPreview();
            if (!_ovServerPart) {
                OverlayHost.Apply(_ovDesign);
                _ovSeenStamp = OverlayHost.DesignStamp;
                return;
            }

            try {
                if (_serverFile() is { } path) {
                    _ovDesign.SaveFile(path);
                    _ovServerFileTime = File.GetLastWriteTimeUtc(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                Say($"Couldn't save the server overlay: {ex.Message}", true);
            }
        }

        private Task AddOverlayElement(string kind) => AddOverlayElement(kind, null);

        /// <summary>A new text or box; with <paramref name="at"/>, its top left corner there on the picture.</summary>
        private Task AddOverlayElement(string kind, Point? at) {
            if (_ovDesign.Elements.Count >= OverlayDesign.MAX_ELEMENTS) {
                throw new PanelException($"The overlay holds up to {OverlayDesign.MAX_ELEMENTS} texts and boxes.");
            }

            var element = kind == OverlayElement.MINIMAP
                ? new OverlayElement {
                    Kind = OverlayElement.MINIMAP, Name = "Minimap", Anchor = OverlayAnchor.TopRight, X = 20, Y = 20, Width = 200, Height = 200,
                    Background = new OverlayPaint { Kind = OverlayPaint.RADIAL, Color1 = "#C8202A44", Color2 = "#C80A0E1C" },
                    BorderColor = "#D0FFD24A", BorderWidth = 2, CornerRadius = 16, Shadow = true, ShadowBlur = 8, Map = new OverlayMap(),
                }
                : kind == OverlayElement.QUEST_TRACKER
                ? new OverlayElement {
                    Kind = OverlayElement.QUEST_TRACKER, Name = "Quest Tracker", Anchor = OverlayAnchor.Right, X = 20, Y = 0, Width = 260, Height = 0,
                    Background = new OverlayPaint { Kind = OverlayPaint.SOLID, Color1 = "#B40A0E1C" }, Padding = 10,
                    BorderColor = "#A0FFD24A", BorderWidth = 1, CornerRadius = 12, Shadow = true, ShadowBlur = 8, Quests = new OverlayQuestList(),
                }
                : kind == OverlayElement.PICTURE
                ? new OverlayElement { Kind = OverlayElement.PICTURE, Anchor = OverlayAnchor.TopLeft, X = 20, Y = 20, Background = new OverlayPaint() }
                : kind == OverlayElement.BUTTON
                ? new OverlayElement {
                    Kind = OverlayElement.BUTTON, Text = "Heal", Anchor = OverlayAnchor.Right, X = 20, Y = 0, Size = 16, Bold = true,
                    Padding = 10, CornerRadius = 10, BorderColor = "#A0FFFFFF", BorderWidth = 1, Shadow = true, Spacing = 4,
                    Fill = new OverlayPaint { Kind = OverlayPaint.SOLID, Color1 = "#FFFFFFFF" },
                    Background = new OverlayPaint { Kind = OverlayPaint.LINEAR, Color1 = "#E07A5CFF", Color2 = "#E0601E70", Angle = 90 },
                    Action = OverlayAction.COMMAND, Command = ".heal",
                }
                : kind == OverlayElement.BOX
                ? new OverlayElement {
                    Kind = OverlayElement.BOX, Name = "", Anchor = OverlayAnchor.TopRight, X = 20, Y = 20, Width = 220, Height = 80, CornerRadius = 12,
                    Background = new OverlayPaint { Kind = OverlayPaint.LINEAR, Color1 = "#B0281B4D", Color2 = "#B0601E70", Angle = 45 },
                    BorderColor = "#80FFFFFF", BorderWidth = 1,
                }
                : new OverlayElement {
                    Text = "My server", Anchor = OverlayAnchor.TopLeft, X = 20, Y = 20, Size = 22, Bold = true, Shadow = true,
                    Fill = new OverlayPaint { Kind = OverlayPaint.SOLID, Color1 = "#FFFFFFFF" },
                };
            if (at is { } point) {
                element.Anchor = OverlayAnchor.TopLeft;
                var (w, h) = element.IsBox ? (element.Width, element.Height) : element.IsButton ? (90, 44) : (160, 40);
                element.X = Math.Round(Math.Clamp(point.X, 0, Math.Max(0, PREVIEW_W - w)));
                element.Y = Math.Round(Math.Clamp(point.Y, 0, Math.Max(0, PREVIEW_H - h)));
            }

            _ovDesign.Elements.Add(element);
            _ovSelected = element;
            OverlayChanged();
            RenderOverlayEditor();

            return Task.CompletedTask;
        }

        private Task DuplicateOverlayElement() {
            if (_ovSelected is null || _ovSelected == _ovDesign.Credit) {
                throw new PanelException("Pick a text or box you added to copy it. The Ahziael line can't be copied.");
            }

            if (_ovDesign.Elements.Count >= OverlayDesign.MAX_ELEMENTS) {
                throw new PanelException($"The overlay holds up to {OverlayDesign.MAX_ELEMENTS} texts and boxes.");
            }

            // With its children: the copy is a whole family of its own, beside the first.
            var family = new List<OverlayElement> { _ovSelected };
            family.AddRange(Descendants(_ovSelected));
            if (_ovDesign.Elements.Count + family.Count > OverlayDesign.MAX_ELEMENTS) {
                throw new PanelException($"The overlay holds up to {OverlayDesign.MAX_ELEMENTS} texts and boxes; the copy and its children don't fit.");
            }

            var copies = OverlayAddon.CopyFamily(family);
            var copy = copies[0];
            copy.Parent = _ovSelected.Parent;
            copy.X += 16;
            copy.Y += 16;
            _ovDesign.Elements.InsertRange(_ovDesign.Elements.IndexOf(_ovSelected) + 1, copies);
            _ovSelected = copy;
            OverlayChanged();
            RenderOverlayEditor();

            return Task.CompletedTask;
        }

        private Task MoveOverlayElement(int by) {
            if (_ovSelected is null || _ovSelected == _ovDesign.Credit) {
                return Task.CompletedTask;
            }

            var at = _ovDesign.Elements.IndexOf(_ovSelected);
            var to = Math.Clamp(at + by, 0, _ovDesign.Elements.Count - 1);
            if (to != at) {
                _ovDesign.Elements.RemoveAt(at);
                _ovDesign.Elements.Insert(to, _ovSelected);
                OverlayChanged();
            }

            return Task.CompletedTask;
        }

        private async Task RemoveOverlayElement() {
            if (_ovSelected is null || _ovSelected == _ovDesign.Credit) {
                throw new PanelException("The Ahziael line is always shown, so it can't be removed. Pick one you added.");
            }

            var children = Descendants(_ovSelected);
            if (!await ConfirmAsync(children.Count == 0 ? "Remove this from the overlay?"
                    : $"Remove this and the {children.Count} piece{(children.Count == 1 ? "" : "s")} that belong to it?")) {
                return;
            }

            _ovDesign.Elements.Remove(_ovSelected);
            _ovDesign.Elements.RemoveAll(children.Contains);
            _ovSelected = _ovDesign.Credit;
            OverlayChanged();
            RenderOverlayEditor();
        }

        private async Task ResetOverlayAsync() {
            if (!await ConfirmAsync("Start the overlay over? Everything you added is removed and the Ahziael line gets its first look back.")) {
                return;
            }

            UseDesign(new OverlayDesign());
            OverlayChanged();
            RenderOverlayEditor();
            Say("The overlay is back to how it started.");
        }

        private async Task ExportOverlayAsync() {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
                Title = "Save the overlay design", SuggestedFileName = "My overlay" + OverlayDesign.SHARE_EXTENSION,
                DefaultExtension = OverlayDesign.SHARE_EXTENSION.TrimStart('.'), ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType("Ahziael overlay design") { Patterns = ["*" + OverlayDesign.SHARE_EXTENSION] }],
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(OverlayDesign.DesignsFolder(OverlayHost.InstallFolder)),
            });
            if (file is null) {
                return;
            }

            await using (var stream = await file.OpenWriteAsync()) {
                stream.SetLength(0);
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(_ovDesign.ToJson());
            }

            Say($"Saved the overlay design as \"{file.Name}\". A friend opens it here with \"Open a design file\".");
        }

        private async Task ImportOverlayAsync() {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
                Title = "Open an overlay design", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Ahziael overlay design") { Patterns = ["*" + OverlayDesign.SHARE_EXTENSION, "*.json"] }, FilePickerFileTypes.All],
            });
            if (files.Count == 0) {
                return;
            }

            var path = files[0].TryGetLocalPath() ?? throw new PanelException("Ahziael can only open a design file that is on this computer.");
            if (new FileInfo(path).Length > 12 * 1024 * 1024) {
                throw new PanelException("That file is too big to be an overlay design.");
            }

            var design = OverlayDesign.FromJson(await File.ReadAllTextAsync(path))
                ?? throw new PanelException("That file is not an Ahziael overlay design.");
            if (!await ConfirmAsync($"Use the design in \"{files[0].Name}\"? It replaces yours ({design.Elements.Count} text{(design.Elements.Count == 1 ? "" : "s")} and boxes).")) {
                return;
            }

            UseDesign(design);
            OverlayChanged();
            RenderOverlayEditor();
            Say("The overlay now uses that design.");
        }

        // ---------------------------------------------------------------------
        // The editor of the picked one
        // ---------------------------------------------------------------------

        /// <summary>
        /// What a button does (Niduus, 2026-11-11): one of a few fixed choices, filled in with words, never code. A command
        /// or cheat runs on the server the game is linked to, as whoever clicks it, with their own rights.
        /// </summary>
        private Control ButtonActionEditor(OverlayElement e) {
            var panel = new StackPanel { Spacing = 8 };
            var kinds = OverlayAction.KINDS;
            panel.Children.Add(OvField("It does", OvCombo(OverlayAction.LABELS, Math.Max(0, Array.IndexOf(kinds, e.Action)), i => {
                e.Action = kinds[i];
                OverlayChanged(false);
                RenderOverlayEditor();
            }, 300)));

            switch (e.Action) {
                case OverlayAction.COMMAND: {
                    var typed = OvText(e.Command, v => { e.Command = v; OverlayChanged(false); }, 300);
                    // Every Imlight and Ahziael chat command, by topic: picking one puts it in the box to finish off.
                    var entries = Imlight.CoreLib.Game.Commands.CommandGuide.All.Where(c => c.Typed.StartsWith('.')).ToList();
                    var pickCommand = new ComboBox {
                        MinWidth = 300, MaxDropDownHeight = 420, PlaceholderText = "Pick an Imlight or Ahziael command",
                        ItemsSource = entries.Select(c => $"{c.Section}: {c.Name}  ({c.Typed.Split("   ")[0]})").ToList(),
                    };
                    pickCommand.SelectionChanged += (_, _) => {
                        if (pickCommand.SelectedIndex >= 0 && pickCommand.SelectedIndex < entries.Count) {
                            var entry = entries[pickCommand.SelectedIndex];
                            typed.Text = entry.Typed.Split("   ")[0].Trim();
                            Say($"{entry.Name}: {entry.Help} Change any <...> part in the box to what you want.");
                        }
                    };
                    panel.Children.Add(new WrapPanel { Children = { OvField("Chat command", typed), OvField("Or pick", pickCommand) } });
                    panel.Children.Add(Sub("Any chat command, as you would type it in the game, for example .heal, .tp Ravenwood or " +
                        ".joinme Malistaire the Undying. It runs on the server the game was started on, only for the wizard in the game window the " +
                        "button is on, with that account's own rights: a command a player can't type, they can't click either. Nothing shows in chat. " +
                        "No password: with one game window open it links by itself; with several, the window shows a code once (type .link and the code in its chat)."));
                    break;
                }
                case OverlayAction.MOB_CHEAT: {
                    var name = OvText(e.Command, v => { e.Command = v; OverlayChanged(false); }, 240);
                    var found = new List<OverlayCommands.CheatInfo>();
                    var pick = new ComboBox { MinWidth = 300, MaxDropDownHeight = 420, PlaceholderText = "Pick a mob cheat" };
                    pick.SelectionChanged += (_, _) => {
                        if (pick.SelectedIndex >= 0 && pick.SelectedIndex < found.Count) {
                            name.Text = found[pick.SelectedIndex].Name;
                        }
                    };
                    var load = Plain("Load mob cheats");
                    load.Click += async (_, _) => {
                        var result = await OverlayCommands.SendAsync("mobcheats", "");
                        if (!result.Ok) {
                            Say(result.Text, true);
                            return;
                        }

                        found = (result.Cheats ?? []).Where(c => c.Name.Length > 0).ToList();
                        pick.ItemsSource = found.Select(c => c.Owner.Length > 0 ? $"{c.Name}  ({c.Owner})" : c.Name).ToList();
                        Say(found.Count == 0 ? result.Text : "The server's mob cheats you can set off are in the list.");
                    };
                    panel.Children.Add(new WrapPanel { Children = { OvField("Mob cheat (its name)", name), OvField("Or pick", Row(pick, load)) } });
                    panel.Children.Add(Sub("Sets off a creature's cheat whose When is \"it is pressed\" (Customs > Bosses: give a creature a cheat with that When " +
                        "and a name). It goes off in the battle of the wizard in that game window, at the next move, only when a creature " +
                        "there has a cheat by that name. Nobody else's battle is touched. Load mob cheats needs the game open."));
                    break;
                }
                case OverlayAction.CHEAT: {
                    var name = OvText(e.Command, v => { e.Command = v; OverlayChanged(false); }, 240);
                    var hows = OverlayAction.HOWS;
                    var pick = new ComboBox { MinWidth = 240, PlaceholderText = "Pick one of my cheats" };
                    pick.SelectionChanged += (_, _) => {
                        if (pick.SelectedItem is string chosen && chosen.Length > 0) {
                            name.Text = chosen;
                        }
                    };
                    var load = Plain("Load my cheats");
                    load.Click += async (_, _) => {
                        try {
                            var result = await OverlayCommands.SendAsync("cheats", "");
                            if (!result.Ok) {
                                Say(result.Text, true);
                                return;
                            }

                            pick.ItemsSource = (result.Cheats ?? []).Select(c => c.Name).Where(n => n.Length > 0).Distinct().ToList();
                            Say(pick.ItemCount == 0 ? "This server has no cheats for you yet." : "Your cheats on this server are in the list.");
                        }
                        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException) {
                            Say("The server didn't answer: " + ex.Message, true);
                        }
                    };
                    panel.Children.Add(new WrapPanel {
                        Children = {
                            OvField("Cheat (name or number)", name),
                            OvField("Or pick", Row(pick, load)),
                            OvField("Clicking it will", OvCombo(OverlayAction.HOW_LABELS, Math.Max(0, Array.IndexOf(hows, e.CheatHow)),
                                i => { e.CheatHow = hows[i]; OverlayChanged(false); }, 240)),
                        },
                    });
                    panel.Children.Add(Sub("The cheat is the own cheat of the wizard in that game window (type .cheats in the game to see yours). " +
                        "The host's settings decide which cheats each player may use, so a button can't get around them. Load my cheats needs the game open."));
                    break;
                }
                case OverlayAction.DESIGNER:
                    panel.Children.Add(Sub("Opens this designer right on the game, like the sparkle button."));
                    break;
                case OverlayAction.TOGGLE_MINE:
                    panel.Children.Add(Sub("Hides or shows your own texts, boxes and buttons. This button itself always stays, so you can bring " +
                        "them back. The server's pieces and the Ahziael line always show."));
                    break;
                case OverlayAction.OPEN_AHZIAEL:
                    panel.Children.Add(Sub("Brings Ahziael to the front (or starts it, when only the overlay is running)."));
                    break;
            }

            if (_ovServerPart) {
                panel.Children.Add(Sub("This is a server button: every player on your server gets it, and it runs as the player who clicks it."));
            }

            panel.Children.Add(Row(Action(Plain("Try it now"), () => {
                OverlayButtons.Click(e, _ovServerPart, IntPtr.Zero, ok => Say(OverlayButtons.LastResult.Length > 0 ? OverlayButtons.LastResult
                    : ok ? "Done." : "That didn't work.", !ok));
                return Task.CompletedTask;
            })));

            return panel;
        }

        // The minimap's own choices (Niduus, 2026-11-12: "mini map that is very detailed"): how it turns and zooms, what it
        // shows, and each kind's colour.
        private void MinimapEditor(OverlayElement e, Action changed) {
            var map = e.Map ??= new OverlayMap();
            void Set(Action change) {
                change();
                changed();
            }

            var how = new WrapPanel();
            // One choice for how much it shows; every part can still be changed by itself below (Niduus, 2026-11-12:
            // "Get in detail, then get in more detail").
            how.Children.Add(OvField("How much it shows (sets the choices below)", OvCombo(["Just the map", "Normal", "Detailed", "Everything"], -1,
                i => { if (i >= 0) { Set(() => map.Detail(i)); } }, 160)));
            how.Children.Add(OvField("Turns with me", OvCheck("The way I face is up", map.Turn, v => Set(() => map.Turn = v))));
            how.Children.Add(OvField("Shape", OvCombo(["Round", "Square"], map.Round ? 0 : 1, i => Set(() => map.Round = i == 0), 110)));
            how.Children.Add(OvField("How far it shows (smaller is closer)", OvNumber(map.Zoom, 300, 12000, 100, v => Set(() => map.Zoom = v))));
            how.Children.Add(OvField("Icon size", OvNumber(map.IconSize, 4, 30, 1, v => Set(() => map.IconSize = v))));
            how.Children.Add(OvField("Name size", OvNumber(map.NameSize, 6, 30, 1, v => Set(() => map.NameSize = v))));
            how.Children.Add(OvField("Words", Row(
                OvCheck("Names", map.Names, v => Set(() => map.Names = v)),
                OvCheck("Place's name", map.Place, v => Set(() => map.Place = v)))));
            how.Children.Add(OvField("If left and right look swapped", OvCheck("Swap left and right", map.Mirror, v => Set(() => map.Mirror = v))));
            _ovEditor.Children.Add(OvGroup("Minimap", new StackPanel {
                Spacing = 6,
                Children = {
                    how,
                    Sub("It shows what is around your wizard in this game window, from the server, several times a second. Width and height above set its size; " +
                        "Fill and Edge below set its background and rim. With one game open it links by itself; with more, it shows a .link code to type " +
                        "in that game's chat once. Here in the designer it shows a made-up map."),
                },
            }));

            var shows = new WrapPanel();
            void Kind(string label, bool on, Action<bool> set, string color, Action<string> setColor) {
                shows.Children.Add(OvField(label, Row(OvCheck("Show", on, v => Set(() => set(v))), OvColor(color, v => Set(() => setColor(v))))));
            }

            shows.Children.Add(OvField("Streets, grass, water and walls", Row(OvCheck("Show", map.Streets, v => Set(() => map.Streets = v)),
            OvSlider(map.StreetsOpacity, 0, 1, v => Set(() => map.StreetsOpacity = v)))));
        shows.Children.Add(OvField("The game's own map page (lines up roughly)", Row(OvCheck("Show", map.GamePicture, v => Set(() => map.GamePicture = v)),
            OvSlider(map.GamePictureOpacity, 0, 1, v => Set(() => map.GamePictureOpacity = v)))));
        shows.Children.Add(OvField("Streets", OvColor(map.PathColor, v => Set(() => map.PathColor = v))));
        shows.Children.Add(OvField("Grass", OvColor(map.GrassColor, v => Set(() => map.GrassColor = v))));
        shows.Children.Add(OvField("Water", OvColor(map.WaterColor, v => Set(() => map.WaterColor = v))));
        shows.Children.Add(OvField("Docks, decks and plank bridges", OvColor(map.WoodColor, v => Set(() => map.WoodColor = v))));
        shows.Children.Add(OvField("Walls, fences and buildings", Row(OvCheck("Show", map.Walls, v => Set(() => map.Walls = v)), OvColor(map.WallColor, v => Set(() => map.WallColor = v)))));
        shows.Children.Add(OvField("Trees, posts and pillars", Row(OvCheck("Show", map.Trees, v => Set(() => map.Trees = v)), OvColor(map.TreeColor, v => Set(() => map.TreeColor = v)))));
        Kind("NPCs", map.Npcs, v => map.Npcs = v, map.NpcColor, v => map.NpcColor = v);
            Kind("Mobs", map.Mobs, v => map.Mobs = v, map.MobColor, v => map.MobColor = v);
            Kind("Bosses (skull)", map.Bosses, v => map.Bosses = v, map.BossColor, v => map.BossColor = v);
            Kind("Sigils", map.Sigils, v => map.Sigils = v, map.SigilColor, v => map.SigilColor = v);
            Kind("Doors and teleporters", map.Doors, v => map.Doors = v, map.DoorColor, v => map.DoorColor = v);
            Kind("Chests", map.Chests, v => map.Chests = v, map.ChestColor, v => map.ChestColor = v);
            Kind("Other wizards", map.Wizards, v => map.Wizards = v, map.WizardColor, v => map.WizardColor = v);
            shows.Children.Add(OvField("Teammates", OvColor(map.TeamColor, v => Set(() => map.TeamColor = v))));
            shows.Children.Add(OvField("You (arrow)", OvColor(map.YouColor, v => Set(() => map.YouColor = v))));
            shows.Children.Add(OvField("Words", OvColor(map.TextColor, v => Set(() => map.TextColor = v))));
            _ovEditor.Children.Add(OvGroup("What the minimap shows", new StackPanel {
                Spacing = 6,
                Children = {
                    shows,
                    Sub("Quests show on the minimap only as the way to the quest picked in a Quest Tracker."),
                },
            }));

            // Everything else about its look (Niduus, 2026-11-12: "Allow for the Mini Map UI to also be customizable to any and all extent").
            var look = new WrapPanel();
            look.Children.Add(OvField("Movement", OvCheck("Glide smoothly between updates", map.Glide, v => Set(() => map.Glide = v))));
            look.Children.Add(OvField("North marker", Row(OvCheck("Show", map.ShowNorth, v => Set(() => map.ShowNorth = v)), OvColor(map.NorthColor, v => Set(() => map.NorthColor = v)))));
            look.Children.Add(OvField("Half way ring", Row(OvCheck("Show", map.ShowRing, v => Set(() => map.ShowRing = v)), OvColor(map.RingColor, v => Set(() => map.RingColor = v)))));
            look.Children.Add(OvField("Place's name at", OvCombo(["Bottom", "Top"], map.PlaceAt == "Top" ? 1 : 0, i => Set(() => map.PlaceAt = i == 1 ? "Top" : "Bottom"), 110)));
            look.Children.Add(OvField("More names", Row(
                OvCheck("Mobs", map.MobNames, v => Set(() => map.MobNames = v)),
                OvCheck("NPCs", map.NpcNames, v => Set(() => map.NpcNames = v)),
                OvCheck("Levels", map.Levels, v => Set(() => map.Levels = v)),
                OvCheck("Dark band under names", map.NameBackground, v => Set(() => map.NameBackground = v)))));
            look.Children.Add(OvField("Mob areas", Row(
                OvCheck("Show", map.MobAreas, v => Set(() => map.MobAreas = v)),
                OvColor(map.MobAreaColor, v => Set(() => map.MobAreaColor = v)),
                OvCheck("How many", map.MobAreaCount, v => Set(() => map.MobAreaCount = v)))));
            look.Children.Add(OvField("Mob area reach", OvNumber(map.MobAreaSize, 100, 3000, 50, v => Set(() => map.MobAreaSize = v))));
            look.Children.Add(OvField("Mob spawn areas", Row(
                OvCheck("Show", map.SpawnAreas, v => Set(() => map.SpawnAreas = v)),
                OvColor(map.SpawnAreaColor, v => Set(() => map.SpawnAreaColor = v)),
                OvCheck("Who spawns", map.SpawnNames, v => Set(() => map.SpawnNames = v)))));
            look.Children.Add(OvField("Spawn area edge", Row(
                OvCheck("Show where they pull", map.SpawnEdge, v => Set(() => map.SpawnEdge = v)),
                OvColor(map.SpawnEdgeColor, v => Set(() => map.SpawnEdgeColor = v)),
                OvColor(map.SpawnBossColor, v => Set(() => map.SpawnBossColor = v)))));
            look.Children.Add(OvField("Mob walks", Row(
                OvCheck("Show the walk each mob takes", map.SpawnWalks, v => Set(() => map.SpawnWalks = v)),
                OvColor(map.SpawnWalkColor, v => Set(() => map.SpawnWalkColor = v)))));
            look.Children.Add(OvField("Bosses", OvCheck("Ring round each boss", map.BossGlow, v => Set(() => map.BossGlow = v))));
            look.Children.Add(OvField("Chests", OvCheck("Say wooden or silver", map.ChestNames, v => Set(() => map.ChestNames = v))));
            look.Children.Add(OvField("Crowded names", OvCheck("Move or hide names that would cover others", map.TidyNames, v => Set(() => map.TidyNames = v))));
            look.Children.Add(OvField("Icon outline", OvColor(map.OutlineColor, v => Set(() => map.OutlineColor = v))));
            look.Children.Add(OvField("Teammates off the map", OvCheck("Arrow on the edge", map.EdgeArrows, v => Set(() => map.EdgeArrows = v))));
            look.Children.Add(OvField("Your arrow size", OvNumber(map.YouSize, 0.3, 5, 0.1, v => Set(() => map.YouSize = v))));
            look.Children.Add(OvField("Mob size", OvNumber(map.MobSize, 0.2, 5, 0.1, v => Set(() => map.MobSize = v))));
            look.Children.Add(OvField("Boss size", OvNumber(map.BossSize, 0.2, 5, 0.1, v => Set(() => map.BossSize = v))));
            look.Children.Add(OvField("NPC size", OvNumber(map.NpcSize, 0.2, 5, 0.1, v => Set(() => map.NpcSize = v))));
            look.Children.Add(OvField("Sigil size", OvNumber(map.SigilSize, 0.2, 5, 0.1, v => Set(() => map.SigilSize = v))));
            look.Children.Add(OvField("Door size", OvNumber(map.DoorSize, 0.2, 5, 0.1, v => Set(() => map.DoorSize = v))));
            look.Children.Add(OvField("Chest size", OvNumber(map.ChestSize, 0.2, 5, 0.1, v => Set(() => map.ChestSize = v))));
            look.Children.Add(OvField("Wizard size", OvNumber(map.WizardSize, 0.2, 5, 0.1, v => Set(() => map.WizardSize = v))));
            _ovEditor.Children.Add(OvGroup("Minimap look", new StackPanel { Spacing = 6, Children = { look, Sub("Sizes are times the icon size above (1 = the same).") } }));

            var full = new WrapPanel();
            var zoom = new WrapPanel();
            zoom.Children.Add(OvField("Zoom buttons", OvCheck("+ and - on the minimap's corner", map.ZoomButtons, v => Set(() => map.ZoomButtons = v))));
            zoom.Children.Add(OvField("Zoom close (presets)", OvCombo(["Very close", "Close", "Normal", "Far", "Very far"], -1,
                i => { if (i >= 0) { Set(() => map.Zoom = new[] { 700.0, 1300, 2200, 4000, 7000 }[i]); } }, 130)));
            zoom.Children.Add(OvField("One press zooms (times)", OvNumber(map.ZoomStep, 1.05, 4, 0.05, v => Set(() => map.ZoomStep = v))));
            zoom.Children.Add(OvField("Closest it can zoom", OvNumber(map.ZoomMin, 100, 30000, 50, v => Set(() => map.ZoomMin = v))));
            zoom.Children.Add(OvField("Farthest it can zoom", OvNumber(map.ZoomMax, 100, 60000, 500, v => Set(() => map.ZoomMax = v))));
            _ovEditor.Children.Add(OvGroup("Minimap zoom", new StackPanel { Spacing = 6, Children = { zoom,
                Sub("In the game, + and - on the minimap (or on the full screen map) zoom it, and how close you left it is kept on this PC.") } }));

            var extra = new WrapPanel();
            extra.Children.Add(OvField("The way you face", Row(OvCheck("Soft wedge", map.ViewCone, v => Set(() => map.ViewCone = v)), OvColor(map.ViewConeColor, v => Set(() => map.ViewConeColor = v)))));
            extra.Children.Add(OvField("Grid lines", Row(OvCheck("Show", map.Grid, v => Set(() => map.Grid = v)), OvColor(map.GridColor, v => Set(() => map.GridColor = v)))));
            extra.Children.Add(OvField("Grid every (game units)", OvNumber(map.GridSize, 100, 20000, 100, v => Set(() => map.GridSize = v))));
            extra.Children.Add(OvField("Edge of the ground", Row(OvCheck("Thin line", map.GroundEdge, v => Set(() => map.GroundEdge = v)), OvColor(map.GroundEdgeColor, v => Set(() => map.GroundEdgeColor = v)))));
            extra.Children.Add(OvField("Name colours", OvCheck("Same colour as their icon", map.NamesInKindColor, v => Set(() => map.NamesInKindColor = v))));
            _ovEditor.Children.Add(OvGroup("Minimap extras", new StackPanel { Spacing = 6, Children = { extra } }));

            full.Children.Add(OvField("Full screen button", OvCheck("On the minimap's corner", map.FullButton, v => Set(() => map.FullButton = v))));
            full.Children.Add(OvField("Space round it", OvNumber(map.FullMargin, 0, 250, 2, v => Set(() => map.FullMargin = v))));
            full.Children.Add(OvField("Background", OvColor(map.FullBackground, v => Set(() => map.FullBackground = v))));
            full.Children.Add(OvField("NPC names", OvCheck("Show on the full screen map", map.FullNames, v => Set(() => map.FullNames = v))));
            full.Children.Add(OvField("How far it shows (0 = 2.5 times the minimap)", OvNumber(map.FullZoom, 0, 30000, 100, v => Set(() => map.FullZoom = v))));
            _ovEditor.Children.Add(OvGroup("Full screen map", new StackPanel {
                Spacing = 6,
                Children = {
                    full,
                    Sub("In the game, the small button on the minimap's corner opens it full screen, north up, with + and - to see closer or farther " +
                        "and X to close it. How far it shows is kept as you zoom it."),
                },
            }));
        }

        // The Quest Tracker's own choices (Niduus, 2026-11-12: "show all your quest in catagory in the UI. Story, Main and Side
        // quests ... click the quest and it will visually draw lines of where you have to go").
        private void QuestTrackerEditor(OverlayElement e, Action changed) {
            var list = e.Quests ??= new OverlayQuestList();
            void Set(Action change) {
                change();
                changed();
            }

            var shows = new WrapPanel();
            shows.Children.Add(OvField("Words at the top (empty for none)", OvText(list.Title, v => Set(() => list.Title = v), 160)));
            shows.Children.Add(OvField("Instance (dungeon) quests", Row(OvCheck("Show", list.Instance, v => Set(() => list.Instance = v)), OvColor(list.InstanceColor, v => Set(() => list.InstanceColor = v)))));
            shows.Children.Add(OvField("Story quests", Row(OvCheck("Show", list.Story, v => Set(() => list.Story = v)), OvColor(list.StoryColor, v => Set(() => list.StoryColor = v)))));
            shows.Children.Add(OvField("Main quests", Row(OvCheck("Show", list.Main, v => Set(() => list.Main = v)), OvColor(list.MainColor, v => Set(() => list.MainColor = v)))));
            shows.Children.Add(OvField("Side quests", Row(OvCheck("Show", list.Side, v => Set(() => list.Side = v)), OvColor(list.SideColor, v => Set(() => list.SideColor = v)))));
            shows.Children.Add(OvField("Story quests under Main too", OvCheck("Show them twice", list.StoryInMain, v => Set(() => list.StoryInMain = v))));
            shows.Children.Add(OvField("Each quest's step now", OvCheck("Show", list.Steps, v => Set(() => list.Steps = v))));
            shows.Children.Add(OvField("Quest name size", OvNumber(list.TitleSize, 6, 30, 1, v => Set(() => list.TitleSize = v))));
            shows.Children.Add(OvField("Step size", OvNumber(list.StepSize, 6, 30, 1, v => Set(() => list.StepSize = v))));
            shows.Children.Add(OvField("Tallest before it scrolls", OvNumber(list.MaxHeight, 80, 1200, 10, v => Set(() => list.MaxHeight = v))));
            shows.Children.Add(OvField("Quest names", OvColor(list.TextColor, v => Set(() => list.TextColor = v))));
            shows.Children.Add(OvField("Steps", OvColor(list.StepColor, v => Set(() => list.StepColor = v))));
            _ovEditor.Children.Add(OvGroup("Quest Tracker", new StackPanel {
                Spacing = 6,
                Children = {
                    shows,
                    Sub("It lists your wizard's quests in this game window, from the server: Instance (a dungeon's own quests), Story (the main quests you need to finish the world), " +
                        "Main (the other main quests) and Side. Click a list's name in the game to fold it away. Fill and Edge below set its background " +
                        "and rim; a height of 0 makes it grow to fit. Here in the designer it shows made-up quests."),
                },
            }));

            var way = new WrapPanel();
            way.Children.Add(OvField("Draw the picked quest's way on the Minimap", OvCheck("Draw it", list.Route, v => Set(() => list.Route = v))));
            way.Children.Add(OvField("Line width", OvNumber(list.RouteWidth, 1, 10, 0.5, v => Set(() => list.RouteWidth = v))));
            _ovEditor.Children.Add(OvGroup("The way to a quest", new StackPanel {
                Spacing = 6,
                Children = {
                    way,
                    Sub("Click a quest in the game to pick it (click it again to let it go). Its way shows ONLY on the Minimap: a line along the " +
                        "streets to its step, in the quest's colour, and a ring on each step spot. Without a Minimap there are no lines. When the " +
                        "step is in another area, the Minimap says where to go and points at the door that leads there."),
                },
            }));
        }

        private void RenderOverlayEditor() {
            _ovEditor.Children.Clear();
            var e = _ovSelected ?? _ovDesign.Credit;
            _ovFilling = true;
            try {
                var words = !e.IsBox && e != _ovDesign.Credit;
                _ovQuickWords.IsEnabled = words;
                _ovQuickWords.Text = words ? e.Text : "";
                _ovQuickParent.Content = e == _ovDesign.Credit ? null : ParentPicker(e, 200);
            }
            finally {
                _ovFilling = false;
            }

            var credit = e == _ovDesign.Credit;

            void Changed() {
                OverlayChanged(false);
                if (credit && _ovServerPart) {
                    // The Ahziael line's look is the player's own (overlay.json), even when it is changed from the server part.
                    _ovClient.Clean();
                    OverlayHost.Apply(_ovClient);
                    _ovSeenStamp = OverlayHost.DesignStamp;
                }
            }

            // What and where.
            var what = new WrapPanel();
            if (credit) {
                // Niduus, 2026-11-11: "It can not be repositioned or disabled or moved in anyway", then "allow customization of
                // the Ahziael UI but not the ability to modify it's text. It stays the same no matter what".
                _ovEditor.Children.Add(Sub("The Ahziael line: \"" + OverlayDesign.CreditLine(OverlayHost.Version) + "\". You can change how it " +
                    "looks (font, size, colours, background, edges, shadow), but its words never change, and it always sits in the bottom-left " +
                    "corner, in full, on top of everything: it can't be moved or turned off. The server's texts and boxes, then yours, are " +
                    "placed around it."));
                var reset = Plain("Put its look back to Ahziael's");
                reset.Click += (_, _) => {
                    _ovDesign.ResetCreditLook();
                    Changed();
                    RenderOverlayEditor();
                };
                _ovEditor.Children.Add(OvGroup("The Ahziael line", Row(
                    OvField("See-through (stays at least 60% solid)", OvSlider(e.Opacity, 0.6, 1, v => { e.Opacity = v; Changed(); })),
                    reset)));
            }
            else {
                what.Children.Add(OvField("Name in the list", OvText(e.Name, v => { e.Name = v; OverlayChanged(); }, 180)));
                what.Children.Add(OvField("Shown", OvCheck("Shown", e.Visible, v => { e.Visible = v; OverlayChanged(); })));
                what.Children.Add(OvField("Belongs to (moves with it)", ParentPicker(e, 220)));
                what.Children.Add(OvField(e.Parent.Length > 0 ? "Pinned to (its parent's)" : "Pinned to", OvCombo(Enum.GetNames<OverlayAnchor>().Select(SpacedName).ToArray(), (int) e.Anchor, i => { e.Anchor = (OverlayAnchor) i; Changed(); }, 150)));
                what.Children.Add(OvField("In from the side", OvNumber(e.X, -4000, 4000, 1, v => { e.X = v; Changed(); })));
                what.Children.Add(OvField("In from the top or bottom", OvNumber(e.Y, -4000, 4000, 1, v => { e.Y = v; Changed(); })));
                if (!credit) {
                    what.Children.Add(OvField("Layer (higher is on top)", OvNumber(e.Layer, 0, 99, 1, v => { e.Layer = (int) v; Changed(); })));
                    what.Children.Add(OvField("Spacing (0 = may lap over)", OvNumber(e.Spacing, 0, 500, 1, v => { e.Spacing = v; Changed(); })));
                    what.Children.Add(OvField(e.IsBox ? "Width" : "Width (0 = fit)", OvNumber(e.Width, 0, 4000, 5, v => { e.Width = v; Changed(); })));
                    what.Children.Add(OvField(e.IsBox ? "Height" : "Height (0 = fit)", OvNumber(e.Height, 0, 4000, 5, v => { e.Height = v; Changed(); })));
                    what.Children.Add(OvField("Turn (degrees)", OvNumber(e.Rotation, -360, 360, 5, v => { e.Rotation = v; Changed(); })));
                }

                what.Children.Add(OvField("See-through", OvSlider(e.Opacity, 0, 1, v => { e.Opacity = v; Changed(); })));
                what.Children.Add(OvField("Locked", OvCheck("Can't be dragged", e.Locked, v => { e.Locked = v; Changed(); })));
                _ovEditor.Children.Add(OvGroup("What and where", what));
                if (e.IsButton) {
                    _ovEditor.Children.Add(OvGroup("When it is clicked in the game", ButtonActionEditor(e)));
                }

                // Put it on a side or the middle of the window in one click (it stays pinned there on any window size).
                Button AlignButton(string label, int? column, int? row) {
                    var button = Plain(label);
                    button.Click += (_, _) => {
                        var col = column ?? (int) e.Anchor % 3;
                        var rw = row ?? (int) e.Anchor / 3;
                        e.Anchor = (OverlayAnchor) (rw * 3 + col);
                        if (column is { } c) {
                            e.X = c == 1 ? 0 : 10;
                        }

                        if (row is { } r) {
                            e.Y = r == 1 ? 0 : 10;
                        }

                        OverlayChanged(false);
                        RenderOverlayEditor();
                    };

                    return button;
                }

                _ovEditor.Children.Add(OvGroup(e.Parent.Length > 0 ? "Line it up with its parent" : "Line it up with the window", new WrapPanel {
                    Children = {
                        Spaced(AlignButton("Left", 0, null)), Spaced(AlignButton("Centre", 1, null)), Spaced(AlignButton("Right", 2, null)),
                        Spaced(AlignButton("Top", null, 0)), Spaced(AlignButton("Middle", null, 1)), Spaced(AlignButton("Bottom", null, 2)),
                    },
                }));

                if (e.IsMinimap) {
                    MinimapEditor(e, Changed);
                }

                if (e.IsQuestTracker) {
                    QuestTrackerEditor(e, Changed);
                }

                if (e.IsPicture) {
                    var change = Action(Plain("Change picture"), async () => {
                        if (await PickPictureAsync() is { } picture) {
                            e.Picture = picture;
                            OverlayChanged();
                        }
                    });
                    _ovEditor.Children.Add(OvGroup("Picture", Row(change,
                        OvField("How it fills its box", OvCombo(["Whole picture", "Cover the box", "Stretch"], Array.IndexOf(OverlayElement.PICTURE_FITS, e.PictureFit),
                            i => { e.PictureFit = OverlayElement.PICTURE_FITS[i]; Changed(); }, 160)))));
                }

            }

            if (!e.IsBox) {
                if (!credit) {
                    var words = new TextBox { Text = e.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, MaxLength = OverlayElement.MAX_TEXT };
                    words.TextChanged += (_, _) => { e.Text = words.Text ?? ""; OverlayChanged(); };
                    _ovEditor.Children.Add(OvGroup("Words", new StackPanel {
                        Spacing = 4,
                        Children = { words, Sub("Fill-ins: {version} Ahziael's version, {server} the server's address, {time} the time, {date} the date.") },
                    }));
                }

                var font = new WrapPanel();
                var fonts = SystemFonts();
                var fontIndex = Math.Max(0, Array.IndexOf(fonts, e.Font.Length == 0 ? fonts[0] : e.Font));
                font.Children.Add(OvField("Font", OvCombo(fonts, fontIndex, i => { e.Font = i == 0 ? "" : fonts[i]; Changed(); }, 220)));
                font.Children.Add(OvField("Size", OvNumber(e.Size, credit ? 10 : 4, 200, 1, v => { e.Size = v; Changed(); })));
                font.Children.Add(OvField("Letter spacing", OvNumber(e.LetterSpacing, -10, 50, 0.5, v => { e.LetterSpacing = v; Changed(); })));
                font.Children.Add(OvField("Line height (0 = normal)", OvNumber(e.LineHeight, 0, 300, 1, v => { e.LineHeight = v; Changed(); })));
                font.Children.Add(OvField("Lines line up", OvCombo(["Left", "Center", "Right"], Array.IndexOf(new[] { "Left", "Center", "Right" }, e.Align), i => { e.Align = new[] { "Left", "Center", "Right" }[i]; Changed(); }, 110)));
                font.Children.Add(OvField("Style", Row(
                    OvCheck("Bold", e.Bold, v => { e.Bold = v; Changed(); }),
                    OvCheck("Italic", e.Italic, v => { e.Italic = v; Changed(); }),
                    OvCheck("Underline", e.Underline, v => { e.Underline = v; Changed(); }),
                    OvCheck("Strike", e.Strikethrough, v => { e.Strikethrough = v; Changed(); }))));
                _ovEditor.Children.Add(OvGroup("Font", font));
                _ovEditor.Children.Add(OvGroup("Letter colour", OvPaint(e.Fill, Changed, allowNone: false)));
            }

            _ovEditor.Children.Add(OvGroup(e.IsBox ? "Fill" : "Background behind the words", OvPaint(e.Background, Changed, allowNone: true)));

            var edge = new WrapPanel();
            edge.Children.Add(OvField("Edge colour", OvColor(e.BorderColor, v => { e.BorderColor = v; Changed(); })));
            edge.Children.Add(OvField("Edge width", OvNumber(e.BorderWidth, 0, 40, 0.5, v => { e.BorderWidth = v; Changed(); })));
            edge.Children.Add(OvField("Round corners", OvNumber(e.CornerRadius, 0, 500, 1, v => { e.CornerRadius = v; Changed(); })));
            edge.Children.Add(OvField("Space inside", OvNumber(e.Padding, 0, 200, 1, v => { e.Padding = v; Changed(); })));
            _ovEditor.Children.Add(OvGroup("Edge and corners", edge));

            var shadow = new WrapPanel();
            shadow.Children.Add(OvField("Shadow", OvCheck("Glow / shadow", e.Shadow, v => { e.Shadow = v; Changed(); })));
            shadow.Children.Add(OvField("Colour", OvColor(e.ShadowColor, v => { e.ShadowColor = v; Changed(); })));
            shadow.Children.Add(OvField("Softness", OvNumber(e.ShadowBlur, 0, 50, 1, v => { e.ShadowBlur = v; Changed(); })));
            shadow.Children.Add(OvField("Across", OvNumber(e.ShadowX, -50, 50, 1, v => { e.ShadowX = v; Changed(); })));
            shadow.Children.Add(OvField("Down", OvNumber(e.ShadowY, -50, 50, 1, v => { e.ShadowY = v; Changed(); })));
            _ovEditor.Children.Add(OvGroup("Shadow or glow", shadow));
        }


    }

    private static string SpacedName(string name) => string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString()));

    private static string[] SystemFonts() {
        if (s_systemFonts is null) {
            var names = new List<string> { "Default (Inter)" };
            try {
                names.AddRange(FontManager.Current.SystemFonts.Select(f => f.Name).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception) {
                // No font list on this system: the default font still works.
            }

            s_systemFonts = [.. names];
        }

        return s_systemFonts;
    }

    // A colour or gradient: what kind, its colours and, for a straight gradient, its direction.
    private static Control OvPaint(OverlayPaint paint, Action changed, bool allowNone) {
        var panel = new WrapPanel();
        string[] kinds = allowNone ? ["None", "One colour", "Straight gradient", "Round gradient"] : ["One colour", "Straight gradient", "Round gradient"];
        var offset = allowNone ? 0 : 1;
        var index = Array.IndexOf(OverlayPaint.KINDS, paint.Kind) - offset;
        void Fill() {
            panel.Children.Clear();
            panel.Children.Add(OvField("Kind", OvCombo(kinds, Math.Max(0, index), i => {
                index = i;
                paint.Kind = OverlayPaint.KINDS[i + offset];
                changed();
                Fill();
            }, 170)));
            if (paint.IsNone) {
                return;
            }

            var gradient = paint.Kind is OverlayPaint.LINEAR or OverlayPaint.RADIAL;
            panel.Children.Add(OvField(gradient ? "From" : "Colour", OvColor(paint.Color1, v => { paint.Color1 = v; changed(); })));
            if (!gradient) {
                return;
            }

            panel.Children.Add(OvField("Middle (optional)", OvColor(paint.Color3, v => { paint.Color3 = v; changed(); }, optional: true)));
            panel.Children.Add(OvField("To", OvColor(paint.Color2, v => { paint.Color2 = v; changed(); })));
            if (paint.Kind == OverlayPaint.LINEAR) {
                panel.Children.Add(OvField("Direction (0 = left to right, 90 = top to bottom)", OvNumber(paint.Angle, -360, 360, 15, v => { paint.Angle = v; changed(); })));
            }
        }

        if (!allowNone && paint.IsNone) {
            paint.Kind = OverlayPaint.SOLID;
            index = 0;
        }

        Fill();

        return panel;
    }

    private static Control OvGroup(string title, Control body) => new Border {
        Background = ManagerTheme.SURFACE_HIGH_BRUSH, CornerRadius = new CornerRadius(ManagerTheme.RADIUS_SMALL), Padding = new Thickness(12, 8),
        Child = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, body } },
    };

    private static Control OvField(string label, Control input) => new StackPanel {
        Spacing = 2, Margin = new Thickness(0, 0, 14, 6),
        Children = { new TextBlock { Text = label, FontSize = ManagerTheme.FONT_SMALL, Foreground = MUTED }, input },
    };

    private static TextBox OvText(string value, Action<string> set, double width) {
        var box = new TextBox { Text = value, Width = width };
        box.TextChanged += (_, _) => set(box.Text ?? "");

        return box;
    }

    private static CheckBox OvCheck(string label, bool value, Action<bool> set) {
        var box = new CheckBox { Content = label, IsChecked = value };
        box.IsCheckedChanged += (_, _) => set(box.IsChecked == true);

        return box;
    }

    private static ComboBox OvCombo(string[] items, int index, Action<int> set, double width) {
        var box = new ComboBox { ItemsSource = items, SelectedIndex = Math.Clamp(index, 0, items.Length - 1), Width = width, MaxDropDownHeight = 360 };
        box.SelectionChanged += (_, _) => {
            if (box.SelectedIndex >= 0) {
                set(box.SelectedIndex);
            }
        };

        return box;
    }

    private static NumericUpDown OvNumber(double value, double min, double max, double step, Action<double> set) {
        var box = new NumericUpDown {
            Minimum = (decimal) min, Maximum = (decimal) max, Increment = (decimal) step, Value = (decimal) Math.Clamp(value, min, max),
            FormatString = step < 1 ? "0.0" : "0", Width = 120,
        };
        box.ValueChanged += (_, _) => {
            if (box.Value is { } v) {
                set((double) v);
            }
        };

        return box;
    }

    private static Control OvSlider(double value, double min, double max, Action<double> set) {
        var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Width = 160, VerticalAlignment = VerticalAlignment.Center };
        var shown = new TextBlock { Width = 44, VerticalAlignment = VerticalAlignment.Center, Text = $"{slider.Value * 100:0}%" };
        slider.ValueChanged += (_, _) => {
            shown.Text = $"{slider.Value * 100:0}%";
            set(slider.Value);
        };

        return Row(slider, shown);
    }

    // A colour: a swatch that opens a picker (ready colours, and red, green, blue and see-through sliders) and the
    // #AARRGGBB code to type or paste.
    private static readonly string[] s_palette = [
        "#FFFFFFFF", "#FFD9D9D9", "#FF8C8C8C", "#FF3A3A3A", "#FF000000", "#FFFF4D4D", "#FFFF9A3C", "#FFFFD24A",
        "#FF7CD65A", "#FF2FBF71", "#FF3CC8E0", "#FF4A8CFF", "#FF7A5CFF", "#FFC15CFF", "#FFFF5CB8", "#FFB07A4A",
        "#FFFFE7A3", "#FFC9B6FF", "#FF140F28", "#FF601E70", "#80000000", "#80FFFFFF", "#40000000", "#00000000",
    ];

    private static Control OvColor(string value, Action<string> set, bool optional = false) {
        var swatch = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(4), BorderBrush = LINE, BorderThickness = new Thickness(1) };
        var code = new TextBox { Width = 112, Watermark = optional ? "none" : "#AARRGGBB", Text = value };
        var updating = false;
        var current = OverlayRenderer.Parse(value) ?? Colors.Transparent;
        var sliders = new Slider[4];
        string[] names = ["Red", "Green", "Blue", "Solid"];

        void Show(Color color, bool fromBox) {
            updating = true;
            try {
                current = color;
                swatch.Background = new SolidColorBrush(color);
                var hex = $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
                if (!fromBox) {
                    code.Text = hex;
                }

                sliders[0].Value = color.R;
                sliders[1].Value = color.G;
                sliders[2].Value = color.B;
                sliders[3].Value = color.A;
                set(hex);
            }
            finally {
                updating = false;
            }
        }

        var palette = new WrapPanel { Width = 8 * 30 };
        foreach (var hex in s_palette) {
            var chip = new Button {
                Width = 26, Height = 26, Margin = new Thickness(2), Padding = new Thickness(0), Background = new SolidColorBrush(Color.Parse(hex)),
                BorderBrush = LINE, BorderThickness = new Thickness(1),
            };
            ToolTip.SetTip(chip, hex);
            chip.Click += (_, _) => Show(Color.Parse(hex), false);
            palette.Children.Add(chip);
        }

        var pickers = new StackPanel { Spacing = 2, Children = { palette } };
        for (var i = 0; i < 4; i++) {
            var slider = sliders[i] = new Slider { Minimum = 0, Maximum = 255, Width = 200 };
            slider.ValueChanged += (_, _) => {
                if (!updating) {
                    Show(Color.FromArgb((byte) sliders[3].Value, (byte) sliders[0].Value, (byte) sliders[1].Value, (byte) sliders[2].Value), false);
                }
            };
            pickers.Children.Add(Row(new TextBlock { Text = names[i], Width = 48, VerticalAlignment = VerticalAlignment.Center }, slider));
        }

        var open = new Button { Padding = new Thickness(4), Content = swatch, Flyout = new Flyout { Content = pickers, Placement = PlacementMode.Bottom } };
        ToolTip.SetTip(open, "Pick a colour");
        updating = true;
        swatch.Background = new SolidColorBrush(current);
        sliders[0].Value = current.R;
        sliders[1].Value = current.G;
        sliders[2].Value = current.B;
        sliders[3].Value = current.A;
        updating = false;

        code.TextChanged += (_, _) => {
            if (updating) {
                return;
            }

            var text = (code.Text ?? "").Trim();
            if (optional && text.Length == 0) {
                set("");

                return;
            }

            var clean = OverlayPaint.CleanColor(text, "");
            if (clean.Length > 0) {
                Show(Color.Parse(clean), true);
            }
        };

        return Row(open, code);
    }

}
