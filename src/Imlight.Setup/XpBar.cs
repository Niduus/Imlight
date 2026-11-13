using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// A wizard's XP bar as the game draws it (Niduus, 2026-11-13: "a live updating XP bar ... with CURR/MAX displayed.
/// Animated as if in the real game"): a dark bar in a gold frame cut into ten parts, with round caps at both ends. When the
/// XP goes up the fill slides along to the new amount; on a level up it fills to the end, empties and slides on from the
/// start of the new level. The numbers in the middle count with the fill.
/// </summary>
internal sealed class XpBar : Border {

    private const int SEGMENTS = 10;
    private const double HEIGHT = 22;

    private static readonly IBrush s_gold = new SolidColorBrush(Color.FromRgb(0xF2, 0xD2, 0x2E));
    private static readonly IBrush s_goldDark = new SolidColorBrush(Color.FromRgb(0x8A, 0x6A, 0x10));
    private static readonly IBrush s_track = new LinearGradientBrush {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromRgb(0x50, 0x50, 0x50), 0), new GradientStop(Color.FromRgb(0x18, 0x18, 0x18), 0.55), new GradientStop(Color.FromRgb(0x30, 0x30, 0x30), 1) },
    };
    private static readonly IBrush s_fill = new LinearGradientBrush {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromRgb(0xB8, 0xF0, 0xFF), 0), new GradientStop(Color.FromRgb(0x3C, 0xA8, 0xE8), 0.5), new GradientStop(Color.FromRgb(0x1C, 0x5C, 0xB0), 1) },
    };
    private static readonly IBrush s_cap = new RadialGradientBrush {
        GradientStops = { new GradientStop(Color.FromRgb(0xFF, 0x9A, 0x30), 0), new GradientStop(Color.FromRgb(0xA0, 0x30, 0x10), 1) },
    };

    private readonly Border _fill;
    private readonly Grid _track;
    private readonly TextBlock _numbers;
    private readonly DispatcherTimer _timer;

    // Where the fill is now and where it is going, as parts of the bar (0 to 1); a level up first runs to 1 and wraps.
    private double _shown;
    private double _target;
    private bool _wrap;
    private int _level = -1;
    private int _now;
    private int _max;
    private double _shownNumber;

    internal XpBar() {
        Height = HEIGHT;
        MinWidth = 220;
        CornerRadius = new CornerRadius(HEIGHT / 2);
        Background = s_goldDark;
        Padding = new Thickness(2);
        _fill = new Border { Background = s_fill, HorizontalAlignment = HorizontalAlignment.Left, Width = 0, CornerRadius = new CornerRadius(3) };
        _track = new Grid { Background = s_track, ClipToBounds = true };
        _track.Children.Add(_fill);

        // The ten parts, with gold lines between them as the game's bar has.
        var lines = new Grid();
        for (var i = 0; i < SEGMENTS; i++) {
            lines.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            var part = new Border { BorderBrush = s_gold, BorderThickness = new Thickness(i == 0 ? 0 : 1, 0, 0, 0) };
            Grid.SetColumn(part, i);
            lines.Children.Add(part);
        }

        _track.Children.Add(lines);
        _numbers = new TextBlock {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
            FontWeight = FontWeight.Bold, Foreground = Brushes.White,
            Effect = new DropShadowEffect { BlurRadius = 2, OffsetX = 1, OffsetY = 1, Color = Colors.Black, Opacity = 1 },
        };
        _track.Children.Add(_numbers);

        var framed = new Border { BorderBrush = s_gold, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4), Child = _track, Margin = new Thickness(HEIGHT - 6, 0) };
        var whole = new Grid { Children = { framed, Cap(HorizontalAlignment.Left), Cap(HorizontalAlignment.Right) } };
        Child = whole;
        ToolTip.SetTip(this, "XP into this level / XP this level takes");

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Step());
        _track.SizeChanged += (_, _) => Paint();
    }

    private static Border Cap(HorizontalAlignment side) => new() {
        Width = HEIGHT - 4, Height = HEIGHT - 4, CornerRadius = new CornerRadius((HEIGHT - 4) / 2), Background = s_cap,
        BorderBrush = s_gold, BorderThickness = new Thickness(2), HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Forgets what it showed (another wizard is shown): the next numbers show at once, with no slide.</summary>
    internal void Reset() {
        _timer.Stop();
        _level = -1;
        _wrap = false;
    }

    /// <summary>The XP into this level, what the level takes, and the level (a higher level than before plays the level up).</summary>
    internal void Set(int now, int max, int level) {
        var part = max > 0 ? Math.Clamp((double) now / max, 0, 1) : 1;
        if (_level < 0) {
            // First showing: no slide from empty, as the game opens with the bar where it is.
            (_level, _now, _max, _shown, _target, _shownNumber) = (level, now, max, part, part, now);
            Paint();

            return;
        }

        _wrap |= level > _level;
        (_level, _now, _max, _target) = (level, now, max, part);
        if (!_timer.IsEnabled && (Math.Abs(_shown - _target) > 0.0005 || _wrap || Math.Abs(_shownNumber - _now) > 0.5)) {
            _timer.Start();
        }
    }

    private void Step() {
        // Eased: quick at first, slowing into place, about a second for a full bar.
        var goal = _wrap ? 1.0 : _target;
        var gap = goal - _shown;
        _shown += Math.Abs(gap) < 0.002 ? gap : Math.Sign(gap) * Math.Max(0.004, Math.Abs(gap) * 0.08);
        if (_wrap && _shown >= 1) {
            _wrap = false;
            _shown = 0;
            _shownNumber = 0;
        }

        var numberGap = _now - _shownNumber;
        _shownNumber += Math.Abs(numberGap) < 1 ? numberGap : numberGap * 0.12;
        Paint();
        if (!_wrap && Math.Abs(_shown - _target) < 0.0005 && Math.Abs(_shownNumber - _now) < 0.5) {
            _timer.Stop();
        }
    }

    private void Paint() {
        var width = _track.Bounds.Width;
        _fill.Width = Math.Max(0, width * Math.Clamp(_shown, 0, 1));
        _numbers.Text = _max > 0 ? $"{(int) Math.Round(_shownNumber):N0} / {_max:N0}" : "Max level";
    }

}
