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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Imlight.Setup;

/// <summary>
/// The Lookup tab's spell view (Niduus, 2026-10-21: "a list on the left with the card on the right of the line I am
/// hovering over, defaulting to the one I clicked on"): picking Spell in the Lookup tab shows every spell with its
/// school, cost and what it does, and a big picture of its card as the game draws it, read from the game files.
/// </summary>
internal sealed partial class ManagerWindow {

    private const int SPELL_MAX = 300;
    private const int SPELL_CACHE = 300;

    private readonly ListBox _spellResults = new() { Background = Brushes.Transparent };
    private readonly Image _spellImage = new() {
        Width = CardPainter.CARD_WIDTH * 2, Height = CardPainter.CARD_HEIGHT * 2, Stretch = Stretch.Uniform,
    };
    private readonly TextBlock _spellImageNote = new() {
        Foreground = MUTED, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20),
    };
    private readonly StackPanel _spellDetails = new() { Spacing = 4 };
    private readonly Dictionary<uint, Bitmap> _spellPictures = [];
    private Grid? _spellView;
    private SpellBook? _spellBook;
    private Task? _spellBookLoading;
    private string? _spellBookProblem;
    private volatile SpellCard? _spellShown;

    private bool SpellMode => (_lookupKind.SelectedItem as string) == "Spell";

    private Control SpellView() {
        RenderOptions.SetBitmapInterpolationMode(_spellImage, BitmapInterpolationMode.HighQuality);
        _spellResults.ItemTemplate = new FuncDataTemplate<SpellCard>((spell, _) => spell is null ? new Panel() : SpellRow(spell));
        _spellResults.SelectionChanged += (_, _) => ShowSpell(_spellResults.SelectedItem as SpellCard);

        // Off the list, the card goes back to the spell last clicked.
        _spellResults.PointerExited += (_, _) => ShowSpell(_spellResults.SelectedItem as SpellCard);

        var picture = new Border {
            Width = _spellImage.Width, Height = _spellImage.Height, HorizontalAlignment = HorizontalAlignment.Center,
            Child = new Panel { Children = { _spellImageNote, _spellImage } },
        };
        _spellView = new Grid {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            IsVisible = false,
            Children = {
                new DockPanel {
                    Children = {
                        WithDock(WithMargin(HideBrokenBox(RenderSpells), new Thickness(4, 0, 0, 6)), Dock.Top),
                        _spellResults,
                    },
                },
                WithColumn(new ScrollViewer {
                    Margin = new Thickness(16, 0, 0, 0),
                    Width = _spellImage.Width + 36,
                    Content = new StackPanel {
                        Spacing = 12,
                        Children = { picture, Card(_spellDetails) },
                    },
                }, 1),
            },
        };
        ShowSpell(null);

        return _spellView;
    }

    private static T WithColumn<T>(T control, int column) where T : Control {
        Grid.SetColumn(control, column);

        return control;
    }

    /// <summary>Switches the Lookup tab between its plain list and the spell view.</summary>
    private void ShowSpellMode() {
        if (_spellView is null) {
            return;
        }

        _spellView.IsVisible = SpellMode;
        _lookupResults.IsVisible = !SpellMode;
        if (SpellMode) {
            _ = Guard(LoadSpellBookAsync);
        }
    }

    private Task LoadSpellBookAsync() {
        if (_spellBook is not null || _spellBookProblem is not null) {
            RenderSpells();

            return Task.CompletedTask;
        }

        return _spellBookLoading ??= LoadAsync();

        async Task LoadAsync() {
            // The game's own folder first; the Game Files tab's copy when the game has moved.
            var folder = new[] { _gameFiles.GameDataFolder, _gameFiles.CopyFolder }
                .FirstOrDefault(f => f is not null && File.Exists(Path.Combine(f, "Root.wad")));
            _lookupCount.Text = "Reading the spells and their cards from your game files…";
            try {
                if (folder is null) {
                    throw new PanelException("Wizard101's game files could not be found, so the spell cards can't be shown. " +
                        "Click \"Update Imlight\" on the Tools tab once so the app can find Wizard101.");
                }

                _spellBook = await Task.Run(() => SpellBook.Load(folder));
            }
            catch (Exception ex) {
                _spellBookProblem = ex is PanelException ? ex.Message : $"The spells could not be read from the game files: {ex.Message}";
            }
            finally {
                _spellBookLoading = null;
            }

            RenderSpells();
        }
    }

    private void RenderSpells() {
        if (!SpellMode) {
            return;
        }

        if (_spellBook is null) {
            _lookupCount.Text = _spellBookProblem ?? "Reading the spells and their cards from your game files…";
            _spellResults.ItemsSource = null;

            return;
        }

        var query = _lookupSearch.Text ?? "";
        var results = _spellBook.Search(query, SPELL_MAX, HideBroken, out var total, out var brokenHidden);
        var selected = _spellResults.SelectedItem as SpellCard;
        _spellResults.ItemsSource = results;
        if (selected is not null && results.Contains(selected)) {
            _spellResults.SelectedItem = selected;
        }

        _lookupCount.Text = (total == 0 ? "Nothing matches." : total > SPELL_MAX
                ? $"{total:N0} spells match. Showing the best {SPELL_MAX}; add words to narrow it down."
                : total == 1 ? "1 spell matches." : $"{total:N0} spells match.")
            + " Point at a spell to see its card; click one to keep it showing." + BrokenHiddenNote(brokenHidden);
        ShowSpell(_spellResults.SelectedItem as SpellCard);
    }

    private Control SpellRow(SpellCard spell) {
        var facts = new List<string> { spell.School.Length > 0 ? spell.School : "No school", spell.Cost, $"{spell.Accuracy}%" };
        if (spell.Type.Length > 0) {
            facts.Add(spell.Type.Replace('_', ' '));
        }

        if (spell.Treasure) {
            facts.Add("Treasure card");
        }

        var title = new StackPanel {
            Orientation = Orientation.Horizontal, Spacing = 10,
            Children = { new TextBlock { Text = spell.Name, FontWeight = FontWeight.SemiBold, FontSize = 15 } },
        };
        if (SpellStatusBadge(spell) is { } badge) {
            title.Children.Add(badge);
        }

        title.Children.Add(WithCenter(new TextBlock { Text = string.Join("  ·  ", facts), Foreground = MUTED }));
        var does = spell.Does.Length > 0
            ? SpellDoesText(spell, maxLines: 2)
            : new TextBlock { Text = "No card text in the game files.", Foreground = MUTED };

        var row = new Border {
            Padding = new Thickness(4, 6),
            Background = Brushes.Transparent,
            BorderBrush = LINE,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new DockPanel {
                Children = {
                    WithDock(new Border {
                        Width = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 2, 10, 2),
                        Background = new SolidColorBrush(Color.FromUInt32(CardPainter.SchoolColor(spell.School))),
                    }, Dock.Left),
                    new StackPanel {
                        Spacing = 2,
                        Children = {
                            title,
                            does,
                            new TextBlock { Text = $"ID {spell.Id}  ·  {spell.Code}", Foreground = MUTED, FontSize = 12 },
                        },
                    },
                },
            },
        };
        row.PointerEntered += (_, _) => ShowSpell(spell);

        return row;
    }

    /// <summary>Shows a spell's card and details on the right, or a hint when there is none to show.</summary>
    private void ShowSpell(SpellCard? spell) {
        if (spell is not null && spell == _spellShown && _spellImage.Source is not null) {
            return;
        }

        _spellShown = spell;
        RenderSpellDetails(spell);
        if (spell is null || _spellBook is null) {
            _spellImage.Source = null;
            _spellImageNote.Text = _spellBookProblem ?? "Point at a spell in the list to see its card here.";

            return;
        }

        if (_spellPictures.TryGetValue(spell.Id, out var cached)) {
            _spellImage.Source = cached;

            return;
        }

        _ = PaintSpellAsync(_spellBook, spell);
    }

    private async Task PaintSpellAsync(SpellBook book, SpellCard spell) {
        GameImage? image;
        try {
            // Moving down the list quickly skips the cards already passed.
            image = await Task.Run(() => _spellShown == spell ? CardPainter.Paint(book, spell) : null);
        }
        catch (Exception ex) {
            _spellImage.Source = null;
            _spellImageNote.Text = $"This card could not be drawn: {ex.Message}";

            return;
        }

        if (image is null) {
            return;
        }

        if (_spellPictures.Count >= SPELL_CACHE) {
            _spellPictures.Clear();
        }

        var bitmap = ToBitmap(image);
        _spellPictures[spell.Id] = bitmap;
        if (_spellShown == spell) {
            _spellImage.Source = bitmap;
        }
    }

    /// <summary>A painted card as a picture Avalonia can show.</summary>
    internal static Bitmap ToBitmap(GameImage image) {
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = bitmap.Lock();
        var row = new byte[image.Width * 4];
        for (var y = 0; y < image.Height; y++) {
            for (var x = 0; x < image.Width; x++) {
                var o = (y * image.Width + x) * 4;
                var a = image.Pixels[o + 3];
                row[x * 4] = (byte) (image.Pixels[o + 2] * a / 255);
                row[x * 4 + 1] = (byte) (image.Pixels[o + 1] * a / 255);
                row[x * 4 + 2] = (byte) (image.Pixels[o] * a / 255);
                row[x * 4 + 3] = a;
            }

            Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, row.Length);
        }

        return bitmap;
    }

    private void RenderSpellDetails(SpellCard? spell) {
        _spellDetails.Children.Clear();
        if (spell is null) {
            _spellDetails.Children.Add(Sub("Point at a spell to see its card. Click one to keep it here, with its ID and the command that teaches it."));

            return;
        }

        _spellDetails.Children.Add(new SelectableTextBlock { Text = spell.Name, FontSize = 17, FontWeight = FontWeight.SemiBold });
        if (SpellStatusLine(spell) is { } statusLine) {
            _spellDetails.Children.Add(statusLine);
        }

        void Fact(string label, string value) {
            if (value.Length > 0) {
                _spellDetails.Children.Add(new DockPanel {
                    Children = {
                        WithDock(new TextBlock { Text = label, Width = 110, Foreground = MUTED }, Dock.Left),
                        new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap },
                    },
                });
            }
        }

        _spellDetails.Children.Add(WithMargin(new SelectableTextBlock { Text = spell.Command, FontFamily = MONO, Foreground = GOOD },
            new Thickness(0, 2, 0, 4)));
        _spellDetails.Children.Add(new WrapPanel {
            Children = {
                Spaced(Action(Primary("Learn on my wizard"), () => {
                    // Runs .sb learn for the wizard playing on this PC, like the bundle list's Send to my gifts.
                    _manager.SendCommand(spell.Command);
                    Say($"Sent {spell.Command} ({spell.Name}, {spell.School}). Open your spellbook in the game to see it.");

                    return Task.CompletedTask;
                })),
                Spaced(Action(Plain("Copy command"), () => CopyAsync(spell.Command))),
                Spaced(Action(Plain("Copy ID"), () => CopyAsync(spell.Id.ToString(CultureInfo.InvariantCulture)))),
            },
        });
        _spellDetails.Children.Add(WithMargin(new Border { Height = 1, Background = LINE }, new Thickness(0, 4, 0, 6)));
        Fact("ID", spell.Id.ToString(CultureInfo.InvariantCulture));
        Fact("School", spell.School);
        Fact("Pip cost", spell.Cost);
        Fact("Accuracy", $"{spell.Accuracy}%");
        Fact("Type", spell.Type.Replace('_', ' '));
        Fact("Kind", spell.Treasure ? "Treasure card" : "Normal spell");
        void FactWith(string label, Control? value) {
            if (value is not null) {
                _spellDetails.Children.Add(new DockPanel {
                    Children = { WithDock(new TextBlock { Text = label, Width = 110, Foreground = MUTED }, Dock.Left), value },
                });
            }
        }

        FactWith("Does", spell.Does.Length > 0 ? SpellDoesText(spell) : null);
        Fact("Level", spell.Level > 0 ? $"{spell.Level} or higher" : "");
        Fact("Game's name", spell.Code);
    }

}
