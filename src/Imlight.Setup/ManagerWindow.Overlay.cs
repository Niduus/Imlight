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
        private sealed record Drag(OverlayElement Element, DragMode Mode, Point Start, double X, double Y, double Width, double Height, Rect Bounds);

        /// <summary>How near an edge (in picture pixels) a press takes the size instead of the place.</summary>
        private const double EDGE = 12;

        /// <summary>True for the editor right on the game (<see cref="OverlayGameEditor"/>): no stand-in picture, no lists.</summary>
        private readonly bool _inGame;
        private int _ovSeenStamp;
        private DateTime _ovServerFileTime;
        private Rect? _ovDragRect;
        private readonly TextBox _ovQuickWords = new() { Width = 220, Watermark = "Words of the picked text" };

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
                                Spaced(_ovParts), Spaced(Block(OverlayElement.TEXT)), Spaced(Block(OverlayElement.BOX)), Spaced(_ovQuickWords),
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
                                    Spaced(Action(Plain("Copy"), DuplicateOverlayElement)),
                                    Spaced(Action(Plain("Up"), () => MoveOverlayElement(-1))),
                                    Spaced(Action(Plain("Down"), () => MoveOverlayElement(1))),
                                    Spaced(Danger(Action(Plain("Remove"), RemoveOverlayElement))),
                                },
                            },
                            new TextBlock { Text = "Drag onto the picture", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                            new WrapPanel { Children = { Spaced(Block(OverlayElement.TEXT)), Spaced(Block(OverlayElement.BOX)) } },
                            Sub("Drag a block onto the picture to put a new text or box right there (or just click it). Drag a line in the list above onto another to change the order."),
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
                    editor,
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
                names.AddRange(_ovDesign.Elements.Select((e, i) => {
                    var name = e.Name.Length > 0 ? e.Name : e.IsBox ? $"Box {i + 1}" : e.Text.Length > 0 ? e.Text.Split('\n')[0] : $"Text {i + 1}";
                    if (name.Length > 34) {
                        name = name[..34] + "…";
                    }

                    return (e.IsBox ? "▭ " : "T  ") + name + (e.Visible ? "" : " (hidden)");
                }));
                _ovList.ItemsSource = names;
                _ovList.SelectedIndex = _ovSelected == _ovDesign.Credit || _ovSelected is null ? 0 : _ovDesign.Elements.IndexOf(_ovSelected) + 1;
            }
            finally {
                _ovFilling = false;
            }
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
            if (HitTest(point) is not var (element, bounds)) {
                return;
            }

            var credit = element == _ovDesign.Credit;
            if (_ovSelected != element) {
                _ovSelected = element;
                FillOverlayList();
                RenderOverlayEditor();
                RenderOverlayPreview();
            }

            _ovDrag = new Drag(element, ModeAt(bounds, point, credit), point, element.X, element.Y,
                element.Width > 0 ? element.Width : bounds.Width, element.Height > 0 ? element.Height : bounds.Height, bounds);
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
            if (drag.Mode == DragMode.Move) {
                // Never off the game window: it stops at the edge.
                dx = Math.Clamp(dx, -drag.Bounds.Left, Math.Max(0, PREVIEW_W - drag.Bounds.Right));
                dy = Math.Clamp(dy, -drag.Bounds.Top, Math.Max(0, PREVIEW_H - drag.Bounds.Bottom));
                element2.X = drag.X + sx * dx;
                element2.Y = drag.Y + sy * dy;
                _ovDragRect = drag.Bounds.Translate(new Vector(dx, dy));
            }
            else {
                // Sizing from the right or bottom edge keeps the left and top where they are, up to the window's edge.
                dx = Math.Min(dx, Math.Max(0, PREVIEW_W - drag.Bounds.Right));
                dy = Math.Min(dy, Math.Max(0, PREVIEW_H - drag.Bounds.Bottom));
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
            OverlayChanged(false);
            RenderOverlayEditor();
        }

        /// <summary>
        /// Pinned to the corner or side it was dropped nearest to, at the same place, so it stays there on a game window
        /// of another shape (a wider one moves right-hand pieces right, not the left-hand ones).
        /// </summary>
        private void Repin(OverlayElement e, Rect rect) {
            var column = rect.Center.X < PREVIEW_W / 3 ? 0 : rect.Center.X > PREVIEW_W * 2 / 3 ? 2 : 1;
            var row = rect.Center.Y < PREVIEW_H / 3 ? 0 : rect.Center.Y > PREVIEW_H * 2 / 3 ? 2 : 1;
            e.Anchor = (OverlayAnchor) (row * 3 + column);
            e.X = Math.Round(column switch { 0 => rect.Left, 2 => PREVIEW_W - rect.Right, _ => rect.Left - (PREVIEW_W - rect.Width) / 2 });
            e.Y = Math.Round(row switch { 0 => rect.Top, 2 => PREVIEW_H - rect.Bottom, _ => rect.Top - (PREVIEW_H - rect.Height) / 2 });
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
            var block = new Border {
                Padding = new Thickness(14, 8), CornerRadius = new CornerRadius(6), BorderBrush = LINE, BorderThickness = new Thickness(1),
                Background = ManagerTheme.SURFACE_HIGH_BRUSH, Cursor = new Cursor(StandardCursorType.DragMove),
                Child = new TextBlock { Text = box ? "▭  Box" : "T  Text", FontWeight = FontWeight.SemiBold },
            };
            ToolTip.SetTip(block, box ? "Drag onto the picture to add a box there" : "Drag onto the picture to add a text there");
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

            var element = kind == OverlayElement.BOX
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
                var (w, h) = element.IsBox ? (element.Width, element.Height) : (160, 40);
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

            var copy = OverlayDesign.FromJson(new OverlayDesign { Elements = [_ovSelected] }.ToJson())!.Elements[0];
            copy.X += 16;
            copy.Y += 16;
            _ovDesign.Elements.Insert(_ovDesign.Elements.IndexOf(_ovSelected) + 1, copy);
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

            if (!await ConfirmAsync("Remove this from the overlay?")) {
                return;
            }

            _ovDesign.Elements.Remove(_ovSelected);
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
            if (new FileInfo(path).Length > 512 * 1024) {
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

        private void RenderOverlayEditor() {
            _ovEditor.Children.Clear();
            var e = _ovSelected ?? _ovDesign.Credit;
            _ovFilling = true;
            try {
                var words = !e.IsBox && e != _ovDesign.Credit;
                _ovQuickWords.IsEnabled = words;
                _ovQuickWords.Text = words ? e.Text : "";
            }
            finally {
                _ovFilling = false;
            }

            var credit = e == _ovDesign.Credit;

            void Changed() => OverlayChanged(false);

            // What and where.
            var what = new WrapPanel();
            if (credit) {
                // Niduus, 2026-11-11: "It can not be repositioned or disabled or moved in anyway".
                _ovEditor.Children.Add(Sub("The Ahziael line: \"" + OverlayDesign.CreditLine(OverlayHost.Version) + "\". It always sits in the " +
                    "bottom-left corner, in full, on top of everything, and only Ahziael sets how it looks: it can't be moved, changed or " +
                    "turned off. The server's texts and boxes, then yours, are placed around it."));

                return;
            }

            what.Children.Add(OvField("Name in the list", OvText(e.Name, v => { e.Name = v; OverlayChanged(); }, 180)));
            what.Children.Add(OvField("Shown", OvCheck("Shown", e.Visible, v => { e.Visible = v; OverlayChanged(); })));
            what.Children.Add(OvField("Pinned to", OvCombo(Enum.GetNames<OverlayAnchor>().Select(SpacedName).ToArray(), (int) e.Anchor, i => { e.Anchor = (OverlayAnchor) i; Changed(); }, 150)));
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
            _ovEditor.Children.Add(OvGroup("What and where", what));

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
