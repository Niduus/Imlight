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
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Imlight.Setup;

/// <summary>
/// The game's own pictures for the boss and custom editors: school icons, spell cards (as the Lookup tab paints them)
/// and a mob's portrait. They come from the game files (SpellBook); until those are read, or when the game is not
/// found, a drawn stand-in in the school's colour shows instead.
/// </summary>
internal sealed partial class ManagerWindow {

    private readonly Dictionary<string, Bitmap?> _schoolIcons = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<uint, SpellCard>? _spellCardsById;
    private Task? _gameIconsLoading;

    /// <summary>Reads the game's spells in the background once, then runs <paramref name="ready"/> on the window.</summary>
    private void EnsureGameIcons(Action ready) {
        if (_spellBook is not null || _spellBookProblem is not null) {
            return;
        }

        _gameIconsLoading ??= LoadAsync();

        async Task LoadAsync() {
            try {
                var folder = new[] { _gameFiles.GameDataFolder, _gameFiles.CopyFolder }
                    .FirstOrDefault(f => f is not null && File.Exists(Path.Combine(f, "Root.wad")));
                if (folder is null) {
                    return;
                }

                var book = await Task.Run(() => SpellBook.Load(folder));
                _spellBook ??= book;
                ready();
            }
            catch (Exception) {
                // The drawn stand-ins stay.
            }
        }
    }

    private SpellCard? CardOf(uint spellId) {
        if (_spellBook is null || spellId == 0) {
            return null;
        }

        _spellCardsById ??= _spellBook.Spells.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());

        return _spellCardsById.GetValueOrDefault(spellId);
    }

    private static IBrush SchoolBrush(string school) => new SolidColorBrush(Color.FromUInt32(CardPainter.SchoolColor(school)));

    /// <summary>A school's icon from the game, or a coloured disc with its first letter.</summary>
    private Control SchoolBadge(string school, double size = 20) {
        school ??= "";
        if (_spellBook is not null && school.Length > 0) {
            if (!_schoolIcons.TryGetValue(school, out var bitmap)) {
                try {
                    bitmap = _spellBook.Image(_spellBook.SchoolIcon(school)) is { } image ? ToBitmap(image) : null;
                }
                catch (Exception) {
                    bitmap = null;
                }

                _schoolIcons[school] = bitmap;
            }

            if (bitmap is not null) {
                var picture = new Image { Source = bitmap, Width = size, Height = size, Stretch = Stretch.Uniform };
                ToolTip.SetTip(picture, school);

                return picture;
            }
        }

        var disc = new Border {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
            Background = school.Length > 0 ? SchoolBrush(school) : new SolidColorBrush(Color.FromArgb(120, 128, 128, 128)),
            BorderBrush = Brushes.White, BorderThickness = new Thickness(1),
            Child = new TextBlock {
                Text = school.Length > 0 ? school[..1] : "?", Foreground = Brushes.White, FontSize = size * 0.55, FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        ToolTip.SetTip(disc, school.Length > 0 ? school : "No school");

        return disc;
    }

    /// <summary>A spell's card as the game draws it, small; painted in the background, a school disc until then.</summary>
    private Control SpellThumb(uint spellId, string school, double width = 46) {
        var height = width * CardPainter.CARD_HEIGHT / CardPainter.CARD_WIDTH;
        var host = new Border { Width = width, Height = height, CornerRadius = new CornerRadius(4), ClipToBounds = true };
        var card = CardOf(spellId);
        if (card is null || _spellBook is null) {
            host.Child = new Border {
                Background = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)), CornerRadius = new CornerRadius(4),
                Child = new Border { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = SchoolBadge(school, width * 0.5) },
            };

            return host;
        }

        if (_spellPictures.TryGetValue(card.Id, out var cached)) {
            host.Child = new Image { Source = cached, Stretch = Stretch.Uniform };

            return host;
        }

        host.Child = SchoolBadge(school, width * 0.5);
        var book = _spellBook;
        _ = Task.Run(() => CardPainter.Paint(book, card)).ContinueWith(t => Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            if (t.Status != TaskStatus.RanToCompletion) {
                return;
            }

            if (_spellPictures.Count >= SPELL_CACHE) {
                _spellPictures.Clear();
            }

            var bitmap = ToBitmap(t.Result);
            _spellPictures[card.Id] = bitmap;
            host.Child = new Image { Source = bitmap, Stretch = Stretch.Uniform };
        }), TaskScheduler.Default);

        return host;
    }

    /// <summary>A mob's portrait: a round frame in its school's colour with its school's icon and its initials.</summary>
    private Control MobPortrait(string name, string school, double size = 72) {
        var initials = string.Concat((name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetter(w[0])).Take(2).Select(w => char.ToUpperInvariant(w[0])));

        return new Panel {
            Width = size, Height = size,
            Children = {
                new Border {
                    CornerRadius = new CornerRadius(size / 2), BorderBrush = SchoolBrush(school), BorderThickness = new Thickness(3),
                    Background = new LinearGradientBrush {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                        GradientStops = { new GradientStop(Color.FromArgb(200, 40, 46, 58), 0), new GradientStop(Color.FromUInt32(CardPainter.SchoolColor(school)), 1) },
                    },
                    Child = new TextBlock {
                        Text = initials.Length > 0 ? initials : "?", FontSize = size * 0.34, FontWeight = FontWeight.Bold, Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    },
                },
                new Border { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Child = SchoolBadge(school, size * 0.34) },
            },
        };
    }

}
