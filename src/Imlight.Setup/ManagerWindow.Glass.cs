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
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// Glass and glow (Niduus, 2026-10-26: "Push glass and glow but make it more organized ... most importantly make
/// updates not hidden. Have it be nearly front and center. Keep name and created by in the same visual section"):
/// soft colours drifting behind see-through panels, a pulsing server light, and the header: the name with "Created by
/// Niduus" in one panel, and the update panel in the middle of the window on every page.
/// </summary>
internal sealed partial class ManagerWindow {

    /// <summary>When this file is in the install folder the colours stay still and the server light does not pulse.</summary>
    private const string MOVING_BACKGROUND_OFF = "moving-background-off.txt";

    // The drifting colours: where each sits (a share of the window's width and height), its size, colour and how it moves.
    private static readonly (double X, double Y, double Size, Color Color, double Speed, double Phase)[] s_blobs = [
        (0.30, -0.10, 760, ManagerTheme.ACCENT, 0.07, 0.0),
        (0.85, 0.45, 680, ManagerTheme.ACCENT_GLOW, 0.05, 2.1),
        (0.55, 0.95, 560, Color.FromRgb(194, 91, 224), 0.06, 4.2),
        (-0.05, 0.70, 520, Color.FromRgb(70, 120, 245), 0.045, 1.3),
    ];

    private readonly Canvas _backdrop = new() { IsHitTestVisible = false };
    private readonly DispatcherTimer _driftTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly ToggleSwitch _movingBackground = new() { OnContent = "On", OffContent = "Off" };
    private readonly DropShadowEffect _dotGlow = new() { OffsetX = 0, OffsetY = 0, BlurRadius = 6, Opacity = 0.9 };
    private double _driftTime;

    // The update panel in the header.
    private readonly TextBlock _updateLine = new() { FontSize = ManagerTheme.FONT_SMALL, Foreground = MUTED, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 };
    private readonly TextBlock _updateTitle = new() { FontWeight = FontWeight.SemiBold };
    private Border? _updatePanel;
    private Version? _newerRelease;
    private bool _updateChecked;
    private readonly DispatcherTimer _updateCheckTimer = new() { Interval = TimeSpan.FromMinutes(30) };

    private bool MovingBackgroundOff => File.Exists(Path.Combine(_manager.InstallFolder, MOVING_BACKGROUND_OFF));

    /// <summary>The colours behind everything, and what keeps them drifting while this window is the one in front.</summary>
    private Control Backdrop() {
        foreach (var blob in s_blobs) {
            _backdrop.Children.Add(new Ellipse {
                Width = blob.Size, Height = blob.Size,
                Fill = new RadialGradientBrush {
                    GradientStops = {
                        new GradientStop(Color.FromArgb(110, blob.Color.R, blob.Color.G, blob.Color.B), 0),
                        new GradientStop(Color.FromArgb(44, blob.Color.R, blob.Color.G, blob.Color.B), 0.45),
                        new GradientStop(Color.FromArgb(0, blob.Color.R, blob.Color.G, blob.Color.B), 1),
                    },
                },
            });
        }

        _backdrop.SizeChanged += (_, _) => PlaceBlobs();
        _dot.Effect = _dotGlow;
        _driftTimer.Tick += (_, _) => {
            _driftTime += _driftTimer.Interval.TotalSeconds;
            PlaceBlobs();
        };
        // It only moves while this window is in front, so it costs nothing while you play.
        Activated += (_, _) => StartDrift();
        Opened += (_, _) => {
            if (IsActive) {
                StartDrift();
            }
        };
        Deactivated += (_, _) => _driftTimer.Stop();
        Closed += (_, _) => {
            _driftTimer.Stop();
            _updateCheckTimer.Stop();
        };

        _movingBackground.IsChecked = !MovingBackgroundOff;
        _movingBackground.IsCheckedChanged += (_, _) => {
            var path = Path.Combine(_manager.InstallFolder, MOVING_BACKGROUND_OFF);
            try {
                if (_movingBackground.IsChecked == true) {
                    File.Delete(path);
                }
                else {
                    File.WriteAllText(path, "Delete this file to make Ahziael's background colours drift again.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // Not saved for next time; it still changes now.
            }

            if (_movingBackground.IsChecked == true) {
                StartDrift();
            }
            else {
                _driftTimer.Stop();
                _dotGlow.BlurRadius = 6;
            }
        };

        return _backdrop;
    }

    private void StartDrift() {
        if (_movingBackground.IsChecked == true) {
            _driftTimer.Start();
        }
    }

    private void PlaceBlobs() {
        var width = _backdrop.Bounds.Width;
        var height = _backdrop.Bounds.Height;
        for (var i = 0; i < s_blobs.Length && i < _backdrop.Children.Count; i++) {
            var blob = s_blobs[i];
            var angle = (_driftTime * blob.Speed) + blob.Phase;
            Canvas.SetLeft(_backdrop.Children[i], (blob.X * width) - (blob.Size / 2) + (Math.Sin(angle) * 90));
            Canvas.SetTop(_backdrop.Children[i], (blob.Y * height) - (blob.Size / 2) + (Math.Cos(angle * 1.3) * 60));
        }

        // The server light breathes, in its own colour.
        _dotGlow.Color = (_dot.Background as ISolidColorBrush)?.Color ?? ManagerTheme.MUTED;
        _dotGlow.BlurRadius = 5 + (9 * (0.5 + (0.5 * Math.Sin(_driftTime * 3))));
    }

    /// <summary>
    /// The header: Ahziael's name with "Created by Niduus" and the version in one panel on the left, the update panel
    /// in the middle, and the server's state on the right.
    /// </summary>
    private Control Header() {
        // A slim bar (Niduus, 2026-10-26, mockup L), so the pages under it have the room.
        var brand = new StackPanel {
            Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0),
            Children = {
                ManagerTheme.Tile("sparkle", ManagerTheme.Gradient(), 34),
                new StackPanel {
                    VerticalAlignment = VerticalAlignment.Center, Spacing = 2,
                    Children = {
                        new TextBlock {
                            Text = Edition.NAME, FontSize = 19, FontWeight = FontWeight.Bold, Foreground = ManagerTheme.Gradient(),
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                        CreditChip(),
                    },
                },
            },
        };

        var whatsNew = Action(Plain("What's new"), () => {
            SelectTab(WHATS_NEW);

            return Task.CompletedTask;
        });
        _updatePanel = ManagerTheme.GlassPanel(new DockPanel {
            Children = {
                WithDock(new StackPanel {
                    Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0),
                    Children = { Action(_updateButton, UpdateAsync), whatsNew },
                }, Dock.Right),
                WithDock(WithMargin(ManagerTheme.Tile("download", ManagerTheme.Accent("What's New"), 28), new Thickness(0, 0, 10, 0)), Dock.Left),
                new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2, Children = { _updateTitle, _updateLine } },
            },
        });
        _updatePanel.Padding = new Thickness(10, 6);
        _updatePanel.HorizontalAlignment = HorizontalAlignment.Center;
        _updatePanel.Margin = new Thickness(16, 0);
        ToolTip.SetTip(_updatePanel, "Updates and what changed in each version. Turn automatic updates on or off on the Tools tab.");
        _updateButton.PropertyChanged += (_, e) => {
            if (e.Property == ContentControl.ContentProperty) {
                RefreshUpdatePanel();
            }
        };
        _updateCheckTimer.Tick += async (_, _) => await Guard(CheckForUpdateAsync);
        _updateCheckTimer.Start();
        RefreshUpdatePanel();

        var bar = ManagerTheme.GlassPanel(new DockPanel {
            Children = {
                WithDock(_statusBadge, Dock.Right),
                WithDock(brand, Dock.Left),
                _updatePanel,
            },
        }, 18);
        bar.Padding = new Thickness(10, 6);
        bar.Margin = new Thickness(12, 10, 12, 0);

        return bar;
    }

    /// <summary>Asks GitHub whether a newer Ahziael is out, for the update panel.</summary>
    private async Task CheckForUpdateAsync() {
        _newerRelease = await SelfUpdate.NewerReleaseAsync();
        _updateChecked = true;
        RefreshUpdatePanel();
    }

    /// <summary>The update panel's words, lit up in the accent when there is something new to get.</summary>
    private void RefreshUpdatePanel() {
        if (_updatePanel is null) {
            return;
        }

        var current = SelfUpdate.CurrentVersion;
        var busy = _updateButton.Content is string label && (label.StartsWith("Downloading", StringComparison.Ordinal) || label.StartsWith("Building", StringComparison.Ordinal));
        string title;
        string line;
        var lit = true;
        if (_readyUpdate is { } ready) {
            title = "Update ready";
            line = $"{ready.Name} is ready. Click Restart to update to start using it.";
        }
        else if (busy || _preparingUpdate) {
            title = "Updating in the background";
            line = "Keep playing. It asks to restart when it is ready.";
        }
        else if (_newerRelease is { } newer) {
            title = $"Version {newer} is out";
            line = $"You have {current}. Click Update Ahziael to get it.";
        }
        else {
            lit = false;
            title = $"Version {current}";
            line = _updateChecked ? "You have the newest version." : "Checking for updates...";
        }

        _updateTitle.Text = title;
        _updateLine.Text = line;
        _updatePanel.BorderBrush = lit ? ManagerTheme.Brush(ManagerTheme.ACCENT_LIGHT, 200) : ManagerTheme.Glass(34);
        _updatePanel.Background = lit ? ManagerTheme.Brush(ManagerTheme.ACCENT, 60) : ManagerTheme.Glass(12);
        _updatePanel.Effect = lit ? ManagerTheme.Glow(ManagerTheme.ACCENT_LIGHT, 28, 0.7) : null;
    }

    /// <summary>What's new in this version, near the top of Home: its changes, and a button to every version's.</summary>
    private Control HomeWhatsNew() {
        var current = SelfUpdate.CurrentVersion;
        var notes = BuiltInNotes().Where(n => n.Version <= current).OrderByDescending(n => n.Version).FirstOrDefault();
        var lines = new StackPanel { Spacing = 6 };
        if (notes is null || notes.Changes.Count == 0) {
            lines.Children.Add(Sub("The notes for this version show on the What's New page."));
        }
        else {
            foreach (var change in notes.Changes.Take(5)) {
                lines.Children.Add(new DockPanel {
                    Children = {
                        WithDock(new Border {
                            Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Margin = new Thickness(2, 7, 10, 0), VerticalAlignment = VerticalAlignment.Top,
                            Background = ManagerTheme.Accent("What's New"),
                        }, Dock.Left),
                        new TextBlock { Text = change, TextWrapping = TextWrapping.Wrap },
                    },
                });
            }

            if (notes.Changes.Count > 5) {
                lines.Children.Add(Sub($"And {notes.Changes.Count - 5} more."));
            }
        }

        return Fold("sparkle", "What's New", notes is null ? "What's new" : $"What's new in {notes.Version}",
            "The newest changes. Every version's are on the What's New page.",
            lines,
            Row(Action(Plain("See every version"), () => {
                SelectTab(WHATS_NEW);

                return Task.CompletedTask;
            })));
    }

}
