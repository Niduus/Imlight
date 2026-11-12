using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// Progress > Monstrology (Niduus, 2026-11-12: "make a monstrology section in the exe where players can give themselves
/// monstrology TC's of what they want, granted within game limits", and "make sure these gives work as commands like
/// giving spells and gear does"). Lists the game's own Summon Monster and Expel Monster treasure cards
/// (Spells/MonsterMagicTC in Root.wad) by monster, and gives the picked one with '.monstercard &lt;card id&gt; &lt;how many&gt;'.
/// The server checks the card is a real Monstrology card and keeps the treasure card book's limit.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string MONSTER_CARD_FOLDER = "Spells/MonsterMagicTC/";
    private const int MONSTER_CARD_MAX = 400;

    private readonly TextBox _mcSearch = new() { Watermark = "Search a monster, such as Lost Soul, Dark Fairy or Krampus" };
    private readonly ComboBox _mcKind = new() { ItemsSource = new[] { "Summon and Expel", "Summon cards", "Expel cards" }, SelectedIndex = 0, MinWidth = 180 };
    private readonly NumericUpDown _mcAmount = new() { Minimum = 1, Maximum = 99, Value = 1, Increment = 1, FormatString = "0", Width = 120 };
    private readonly TextBlock _mcCount = new() { TextWrapping = TextWrapping.Wrap, Foreground = MUTED };
    private readonly ListBox _mcList = new() { Background = Brushes.Transparent, Classes = { "compact" }, MaxHeight = 520 };
    private readonly Image _mcPicture = new() { Height = 300, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel _mcDetails = new() { Spacing = 6 };
    private readonly DispatcherTimer _mcDelay = new() { Interval = TimeSpan.FromMilliseconds(220) };

    private List<MonsterCard>? _monsterCards;
    private MonsterCard? _mcPicked;

    /// <summary>One Monstrology treasure card: Summon or Expel, the monster it is for, and the game's card.</summary>
    private sealed record MonsterCard(SpellCard Card, bool Summon, string Monster, uint MonsterId) {
        internal string Title => $"{(Summon ? "Summon" : "Expel")} {Monster}";
        internal string Command => $".monstercard {Card.Id.ToString(CultureInfo.InvariantCulture)}";
    }

    private Control MonstrologyTab() {
        _mcDelay.Tick += (_, _) => {
            _mcDelay.Stop();
            RenderMonsterCards();
        };
        _mcSearch.TextChanged += (_, _) => {
            _mcDelay.Stop();
            _mcDelay.Start();
        };
        _mcKind.SelectionChanged += (_, _) => RenderMonsterCards();
        _mcList.ItemTemplate = new FuncDataTemplate<MonsterCard>((card, _) => card is null ? new Panel() : new TextBlock {
            Text = card.Title, Margin = new Thickness(4, 2),
        });
        _mcList.SelectionChanged += (_, e) => {
            if (e.Source == _mcList && _mcList.SelectedItem is MonsterCard card && card != _mcPicked) {
                ShowMonsterCard(card);
            }
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,*") };
        grid.Children.Add(new StackPanel { Spacing = 8, Children = { _mcCount, _mcList } });
        var details = new StackPanel { Spacing = 10, Children = { _mcPicture, _mcDetails } };
        Grid.SetColumn(details, 2);
        grid.Children.Add(details);

        return Page(
            Fold("sparkle", "Collect Spells", "Monstrology cards",
                "Give your wizard the game's own Summon Monster and Expel Monster treasure cards, for any monster that has a Monstrology page.",
                WithHint(new WrapPanel {
                    Children = { Spaced(_mcSearch), Spaced(_mcKind) },
                }, "Search by the monster's name. Summon cards call that monster to fight on your side, and they go into a " +
                   "Monstrodome's Set Monsters window. Expel cards make that monster leave a battle. Only cards the real game " +
                   "has are listed, and your treasure card book still holds at most 999 cards. Start the server and log in " +
                   "with your wizard first, or type the same command in the game's chat: .monstercard summon Lost Soul 3."),
                grid));
    }

    /// <summary>Reads the cards from the game files the first time the tab shows.</summary>
    private async Task LoadMonstrologyAsync() {
        if (_monsterCards is null) {
            _mcCount.Text = "Reading the Monstrology cards from your game files…";
            await LoadSpellBookAsync();
            if (_spellBook is { } book) {
                _monsterCards = MonsterCards(book);
            }
        }

        RenderMonsterCards();
    }

    private static List<MonsterCard> MonsterCards(SpellBook book) {
        var cards = new List<MonsterCard>();
        foreach (var card in book.Spells) {
            if (!card.File.StartsWith(MONSTER_CARD_FOLDER, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var match = MonsterCardFile().Match(card.File[MONSTER_CARD_FOLDER.Length..]);
            if (!match.Success || !uint.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) {
                continue;
            }

            var summon = match.Groups["kind"].Value == "Summon";
            cards.Add(new MonsterCard(card, summon, Words(match.Groups["name"].Value), id));
        }

        return [.. cards.OrderBy(c => c.Monster, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Summon ? 0 : 1).ThenBy(c => c.MonsterId)];

        // "AberrantDragonTitan" -> "Aberrant Dragon Titan".
        static string Words(string name) => Regex.Replace(name.Replace('_', ' '), "(?<=[a-z])(?=[A-Z0-9])|(?<=[A-Z])(?=[A-Z][a-z])", " ").Trim();
    }

    [GeneratedRegex(@"^(?<kind>Summon|Kill)(?<name>.+?)TC(?<id>\d+)\.xml$", RegexOptions.CultureInvariant)]
    private static partial Regex MonsterCardFile();

    private void RenderMonsterCards() {
        if (_monsterCards is not { } all) {
            _mcCount.Text = _spellBookProblem ?? "Reading the Monstrology cards from your game files…";
            _mcList.ItemsSource = null;

            return;
        }

        var query = (_mcSearch.Text ?? "").Trim();
        var kind = _mcKind.SelectedIndex;
        var found = all.Where(c => (kind == 0 || (kind == 1) == c.Summon)
                                   && (query.Length == 0 || c.Monster.Contains(query, StringComparison.OrdinalIgnoreCase)
                                       || c.MonsterId.ToString(CultureInfo.InvariantCulture) == query
                                       || c.Card.Id.ToString(CultureInfo.InvariantCulture) == query))
            .ToList();
        _mcCount.Text = found.Count > MONSTER_CARD_MAX
            ? $"{found.Count} cards match; the first {MONSTER_CARD_MAX} are shown. Search to narrow it down."
            : $"{found.Count} card{(found.Count == 1 ? "" : "s")}.";
        _mcList.ItemsSource = found.Take(MONSTER_CARD_MAX).ToList();
    }

    private void ShowMonsterCard(MonsterCard? card) {
        _mcPicked = card;
        _mcDetails.Children.Clear();
        _mcPicture.Source = null;
        if (card is null || _spellBook is not { } book) {
            return;
        }

        RequestCard(book, card.Card, CardPainter.AUTO_COUNT, SPELL_BIG_SCALE, () => _mcPicked == card, bitmap => {
            if (_mcPicked == card) {
                _mcPicture.Source = bitmap;
            }
        });
        _mcDetails.Children.Add(new SelectableTextBlock { Text = card.Title, FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        _mcDetails.Children.Add(Sub(card.Summon
            ? "A Summon Monster treasure card: in battle it calls this monster to your side. It also goes into a Monstrodome."
            : "An Expel Monster treasure card: in battle it makes this monster leave."));
        _mcDetails.Children.Add(new SelectableTextBlock {
            Text = $"Card {card.Card.Id} · monster {card.MonsterId}", FontFamily = MONO, Foreground = GOOD,
        });
        _mcDetails.Children.Add(new WrapPanel {
            Children = {
                Spaced(WithCenter(new TextBlock { Text = "How many", Foreground = MUTED })),
                Spaced(_mcAmount),
                Spaced(Action(Primary("Give to my wizard"), () => {
                    var times = (int) (_mcAmount.Value ?? 1);
                    var command = $"{card.Command} {times}";
                    _manager.SendCommand(command);
                    Say($"Sent {command} ({times} {card.Title}). Look in your spellbook's treasure cards in the game.");

                    return Task.CompletedTask;
                })),
                Spaced(Action(Plain("Copy command"), () => CopyAsync($"{card.Command} {(int) (_mcAmount.Value ?? 1)}"))),
            },
        });
    }

}
