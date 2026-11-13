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

    /// <summary>
    /// A spell's card as the game draws it, small; painted in the background, a school disc until then. A count above
    /// 0 shows as "xN" on the card, as the game's deck does. A small card shows the card big when pointed at.
    /// </summary>
    private Control SpellThumb(uint spellId, string school, double width = 46, int count = CardPainter.AUTO_COUNT) {
        var height = width * CardPainter.CARD_HEIGHT / CardPainter.CARD_WIDTH;
        var host = new Border { Width = width, Height = height, CornerRadius = new CornerRadius(4), ClipToBounds = true };
        var card = CardOf(spellId);
        if (card is null || _spellBook is not { } book) {
            host.Child = new Border {
                Background = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)), CornerRadius = new CornerRadius(4),
                Child = new Border { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = SchoolBadge(school, width * 0.5) },
            };

            return host;
        }

        host.Child = SchoolBadge(school, width * 0.5);

        // Painted about the size it shows (or twice that on a sharp screen), then scaled down smoothly.
        var scale = Math.Clamp((int) Math.Ceiling(width * 1.5 / CardPainter.CARD_WIDTH), 1, CardPainter.SCALE);
        RequestCard(book, card, count, scale, () => true, bitmap => {
            var image = new Image { Source = bitmap, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
            host.Child = image;
        });
        if (width < 100) {
            host.PointerEntered += (_, _) => {
                if (ToolTip.GetTip(host) is null) {
                    ToolTip.SetTip(host, SpellThumb(spellId, school, 200, count));
                }
            };
        }

        return host;
    }

    // Each gear slot's word in the game's picture names and the app's own icon when no picture is found.
    private static readonly Dictionary<string, (string[] Words, string StandIn)> s_slotIcons = new(StringComparer.OrdinalIgnoreCase) {
        ["Hat"] = (["Hat", "Hats"], "user"),
        ["Robe"] = (["Robe", "Robes"], "layers"),
        ["Boots"] = (["Boots", "Boot", "Shoes"], "up"),
        ["Wand"] = (["Wand", "Wands", "Weapon"], "sparkle"),
        ["Athame"] = (["Athame", "Athames"], "sword"),
        ["Amulet"] = (["Amulet", "Amulets"], "star"),
        ["Ring"] = (["Ring", "Rings"], "ring"),
        ["Pet"] = (["Pet", "Pets"], "paw"),
        ["Mount"] = (["Mount", "Mounts"], "wing"),
        ["Deck"] = (["Deck", "Decks"], "deck"),
    };

    private readonly Dictionary<string, string?> _slotPictureNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A gear slot's round icon as the game's backpack tabs show it (Niduus, 2026-11-13: "use the actual icons for gear"),
    /// read from the game's picture files; the app's own icon in a round frame until they are read or when none is found.
    /// </summary>
    private Control SlotIcon(string slot, double size) {
        var picture = GearPicture(SlotPictureName(slot));
        Control icon = picture is not null
            ? new Image { Source = picture, Width = size, Height = size, Stretch = Stretch.Uniform }
            : new Border {
                Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
                Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x6B, 0x3A)), BorderBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0x9A, 0x3A)),
                BorderThickness = new Thickness(2),
                Child = ManagerTheme.Icon(s_slotIcons.TryGetValue(slot, out var s) ? s.StandIn : "square", size * 0.55, Brushes.White),
            };
        ToolTip.SetTip(icon, slot);

        return icon;
    }

    /// <summary>
    /// The game's picture for a slot's backpack tab: the shortest picture name that has the slot's word as a whole word
    /// together with a tab or button word, or null.
    /// </summary>
    private string? SlotPictureName(string slot) {
        if (_spellBook is not { } book || !s_slotIcons.TryGetValue(slot, out var known)) {
            return null;
        }

        if (_slotPictureNames.TryGetValue(slot, out var cached)) {
            return cached;
        }

        static string[] Parts(string name)
            => System.Text.RegularExpressions.Regex.Split(Path.GetFileNameWithoutExtension(name), "[^A-Za-z]+|(?<=[a-z])(?=[A-Z])")
                .Where(p => p.Length > 0).ToArray();

        string[] places = ["Backpack", "Inventory", "Equip", "Equipment", "Tab", "Btn", "Button", "Filter", "Category", "Icon"];
        string? found = null;
        foreach (var place in places) {
            found = known.Words.SelectMany(word => book.FindImages(word, place))
                .Where(n => n.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                .Where(n => Parts(n) is var parts && parts.Any(p => known.Words.Contains(p, StringComparer.OrdinalIgnoreCase))
                            && parts.Any(p => p.Equals(place, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(n => n.Length).FirstOrDefault();
            if (found is not null) {
                break;
            }
        }

        _slotPictureNames[slot] = found;

        return found;
    }

    /// <summary>
    /// Lets a Quest Team or Ultimus wizard sheet draw its deck with the game's card pictures, like the game's deck window,
    /// and reads the game's spells if that has not happened yet (the sheet draws again once they are read).
    /// </summary>
    private void ShowCardsOn(WizardSheet sheet) {
        sheet.CardThumb ??= (id, school, width, count) => SpellThumb(id, school, width, count > 0 ? count : 0);
        sheet.StatIcon ??= (key, tip, size) => GearIcon(key, tip, size);
        sheet.SchoolIcon ??= (school, size) => SchoolBadge(school, size);
        sheet.SlotIcon ??= SlotIcon;
        EnsureGameIcons(() => Avalonia.Threading.Dispatcher.UIThread.Post(sheet.Redraw));
    }

    /// <summary>
    /// Pointing at <paramref name="target"/> (a search result row) shows the spell's card big, as a card in a deck does,
    /// so versions with the same name can be told apart before one is picked (player suggestion, 2026-10-27).
    /// </summary>
    private void SpellCardTip(Control target, uint spellId, string school) {
        target.PointerEntered += (_, _) => {
            if (ToolTip.GetTip(target) is not null) {
                return;
            }

            if (CardOf(spellId) is null) {
                EnsureGameIcons(() => { });

                return;
            }

            ToolTip.SetTip(target, SpellThumb(spellId, school, 200));
            ToolTip.SetShowDelay(target, 150);
        };
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
