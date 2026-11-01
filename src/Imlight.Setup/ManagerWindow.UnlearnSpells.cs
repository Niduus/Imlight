using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// Progress > Unlearn Spells (Niduus, 2026-10-31: "make a UI that allows for players to UNLEARN spells that are on their
/// wizard"). Sends '.unlearn list' to the picked wizard; the server writes their spellbook to
/// fast-forward/&lt;wizard id&gt;-spellbook.txt (each spell with its school and the training points unlearning it gives
/// back), listed here with ticks. "Unlearn the ticked spells" writes &lt;wizard id&gt;-unlearn-pick.txt and sends
/// '.unlearn picked yes'; the server (SpellUnlearn) takes them out of the spellbook and decks, gives the points back and
/// writes the spellbook again. In the game, '.unlearn &lt;spell name&gt;' does the same for one spell on any server.
/// </summary>
internal sealed partial class ManagerWindow {

    private readonly ComboBox _usWizard = new() { MinWidth = 220, PlaceholderText = "Pick a wizard" };
    private readonly TextBox _usSearch = new() { Watermark = "Search your spells, such as Fire Cat, Ice or Myth", MinWidth = 320 };
    private readonly TextBlock _usStatus = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _usCount = new() { TextWrapping = TextWrapping.Wrap, Foreground = MUTED };
    private readonly StackPanel _usList = new() { Spacing = 2 };
    private readonly DispatcherTimer _usTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly HashSet<uint> _usTicked = [];
    private List<UsSpell> _usBook = [];
    private string _usBookFile = "";
    private DateTime _usBookRead;
    private DateTime _usSent;
    private DateTime _usUnlearnSent;
    private string _usLastResult = "";
    private bool _usFilling;
    private readonly Button _usGiveBack = ManagerTheme.MakeButton("Give back unlearned spells", primary: false);

    private sealed record UsSpell(string Name, uint Id, string School, int Points, string Kind);

    private Control UnlearnSpellsTab() {
        _usTimer.Tick += (_, _) => ReadUnlearnSpells();
        _usWizard.SelectionChanged += (_, _) => {
            if (!_usFilling) {
                _usTicked.Clear();
                _usLastResult = "";
                _usBookRead = default;
                ReadUnlearnSpells();
                AskForSpellbook(quiet: true);
            }
        };
        _usSearch.TextChanged += (_, _) => RenderUnlearnSpells();

        var read = Plain("Read my spellbook");
        ToolTip.SetTip(read, "Asks the server for the picked wizard's spellbook. Log in with that wizard on this PC first.");
        var tickShown = Plain("Tick all shown");
        var untick = Plain("Untick all");
        var go = Primary("Unlearn the ticked spells");
        ToolTip.SetTip(go, "Takes the ticked spells out of the wizard's spellbook and every deck, and gives back the training points they cost.");

        ToolTip.SetTip(_usGiveBack, "Teaches the picked wizard every spell they unlearned again (also .unlearn undo in the game), " +
                                    "and takes back the training points unlearning gave for them, as far as the wizard still has them.");
        _usGiveBack.IsVisible = false;

        return Page(
            Fold("undo", "Unlearn Spells", "Unlearn Spells",
                "Take any spell your wizard knows out of their spellbook and decks, and get back the training points it cost.",
                WithHint(new WrapPanel {
                    Children = { Spaced(_usWizard), Spaced(Action(read, () => { AskForSpellbook(quiet: false); return Task.CompletedTask; })) },
                }, "Pick your wizard and log in with them on this PC, with the server running. Then press Read my spellbook. " +
                   "Training points come back the way the game's own reset gives them: spells you bought at a trainer (another school, " +
                   "astral, shadow) give their points back; your own school's spells, quest spells and dropped spells were free, so they " +
                   "give nothing. A quest spell does not come back by itself: Progress > Collect Spells can give it again. " +
                   "On any server, the chat command .unlearn <spell name> does the same for one spell."),
                _usStatus,
                new WrapPanel { Children = { Spaced(Action(_usGiveBack, GiveBackUnlearnedAsync)) } }),
            Fold("list", "Unlearn Spells", "Your spellbook", "Tick the spells to unlearn, then press Unlearn the ticked spells.",
                new WrapPanel {
                    Children = {
                        Spaced(_usSearch),
                        Spaced(Action(tickShown, () => { TickShownSpells(true); return Task.CompletedTask; })),
                        Spaced(Action(untick, () => { _usTicked.Clear(); RenderUnlearnSpells(); return Task.CompletedTask; })),
                    },
                },
                _usCount,
                new WrapPanel { Children = { Spaced(Action(go, UnlearnTickedSpellsAsync)) } },
                _usList));
    }

    /// <summary>Fills the wizards and asks for the picked one's spellbook. Runs when the tab shows.</summary>
    private void LoadUnlearnSpells() {
        FillFastForwardWizards();
        var wizards = (_ffWizard.ItemsSource as List<FfWizard>) ?? [];
        var picked = (_usWizard.SelectedItem as FfWizard)?.Id;
        _usFilling = true;
        _usWizard.ItemsSource = wizards;
        _usWizard.SelectedItem = wizards.FirstOrDefault(w => w.Id == picked)
                                 ?? wizards.FirstOrDefault(w => w.OnThisPc) ?? (wizards.Count == 1 ? wizards[0] : null);
        _usFilling = false;
        _usBookRead = default;
        ReadUnlearnSpells();
        AskForSpellbook(quiet: true);
    }

    // '.unlearn list': the server writes the spellbook. Quiet: when the tab opens or a wizard is picked, without a warning
    // if nothing comes back (the wizard may simply not be logged in).
    private void AskForSpellbook(bool quiet) {
        if (_usWizard.SelectedItem is not FfWizard wizard) {
            if (!quiet) {
                throw new PanelException("Pick a wizard first. The list fills once that wizard has played on your server.");
            }

            return;
        }

        try {
            _manager.SendCommand(".unlearn list", wizard.Id);
        }
        catch (PanelException) when (quiet) {
            // The server is not running (or has no command box): the last spellbook read still shows.
            return;
        }

        // Look for a little while for the server's answer.
        _usSent = DateTime.UtcNow;
        _usTimer.Start();
        if (!quiet) {
            Say($"Asked the server for {UsName(wizard)}'s spellbook.");
        }

        ReadUnlearnSpells();
    }

    private async Task UnlearnTickedSpellsAsync() {
        if (_usWizard.SelectedItem is not FfWizard wizard) {
            throw new PanelException("Pick a wizard first.");
        }

        var ids = _usBook.Where(s => _usTicked.Contains(s.Id)).ToList();
        if (ids.Count == 0) {
            throw new PanelException("Tick at least one spell to unlearn.");
        }

        var name = UsName(wizard);
        var points = ids.Sum(s => s.Points);
        var shown = string.Join(", ", ids.Take(8).Select(s => s.Name)) + (ids.Count > 8 ? $" and {ids.Count - 8} more" : "");
        if (!await ConfirmAsync($"Unlearn {ids.Count} spells from {name}: {shown}? They leave the spellbook and every deck" +
                                (points > 0 ? $", and {points} training points come back." : ". They were free, so no training points come back.") +
                                $" {name} must be logged in on this PC." +
                                (ids.Count > 30 ? " That is a lot of spells: Give back unlearned spells undoes it if you change your mind." : ""))) {
            return;
        }

        var folder = Path.Combine(_manager.ServerFolder, FF_FOLDER);
        Directory.CreateDirectory(folder);
        TryDeleteFile(Path.Combine(folder, wizard.Id + "-unlearn-picked-result.txt"));
        await File.WriteAllTextAsync(Path.Combine(folder, wizard.Id + "-unlearn-pick.txt"),
            string.Join('\n', ids.Select(s => s.Id.ToString(CultureInfo.InvariantCulture))) + "\n");
        _manager.SendCommand(".unlearn picked yes", wizard.Id);
        _usUnlearnSent = DateTime.UtcNow;
        _usSent = DateTime.UtcNow;
        _usTimer.Start();
        _usStatus.Text = $"Sent. Unlearning {ids.Count} spells from {name}...";
        _usStatus.Foreground = null;
        Say($"Unlearn sent for {ids.Count} spells from {name}.");
    }

    // '.unlearn undo': every spell the wizard unlearned comes back.
    private async Task GiveBackUnlearnedAsync() {
        if (_usWizard.SelectedItem is not FfWizard wizard) {
            throw new PanelException("Pick a wizard first.");
        }

        var name = UsName(wizard);
        if (!await ConfirmAsync($"Give {name} back every spell they unlearned? The training points unlearning gave for them are taken " +
                                $"back, as far as {name} still has them. {name} must be logged in on this PC.")) {
            return;
        }

        _manager.SendCommand(".unlearn undo", wizard.Id);
        _usSent = DateTime.UtcNow;
        _usLastResult = "Gave back the unlearned spells.";
        _usTimer.Start();
        _usStatus.Text = $"Sent. Giving {name} their unlearned spells back...";
        _usStatus.Foreground = null;
        Say($"Give back unlearned spells sent for {name}.");
    }

    private void TickShownSpells(bool tick) {
        foreach (var spell in ShownSpells()) {
            if (tick) {
                _usTicked.Add(spell.Id);
            }
            else {
                _usTicked.Remove(spell.Id);
            }
        }

        RenderUnlearnSpells();
    }

    private IEnumerable<UsSpell> ShownSpells() {
        var words = (_usSearch.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return _usBook.Where(s => words.All(w => s.Name.Contains(w, StringComparison.OrdinalIgnoreCase)
                                                  || s.School.Contains(w, StringComparison.OrdinalIgnoreCase)
                                                  || s.Kind.Contains(w, StringComparison.OrdinalIgnoreCase)
                                                  || s.Id.ToString(CultureInfo.InvariantCulture) == w));
    }

    // Reads the spellbook and the last unlearn's result when they change.
    private void ReadUnlearnSpells() {
        if (_usWizard.SelectedItem is not FfWizard wizard) {
            _usTimer.Stop();
            _usBook = [];
            _usStatus.Text = "Pick a wizard.";
            _usStatus.Foreground = null;
            RenderUnlearnSpells();
            return;
        }

        var folder = Path.Combine(_manager.ServerFolder, FF_FOLDER);
        ReadUnlearnResult(wizard, Path.Combine(folder, wizard.Id + "-unlearn-picked-result.txt"));
        var path = Path.Combine(folder, wizard.Id + "-spellbook.txt");
        try {
            var written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (path == _usBookFile && written == _usBookRead) {
                if (_usSent != default && DateTime.UtcNow - _usSent > TimeSpan.FromSeconds(30)) {
                    _usTimer.Stop();
                    _usSent = default;
                    if (written == DateTime.MinValue || _usUnlearnSent != default) {
                        _usStatus.Text = $"Nothing back from the server. Is {UsName(wizard)} logged in on this PC, with the server running? " +
                                         "Log in and press Read my spellbook.";
                        _usStatus.Foreground = WARN;
                        _usUnlearnSent = default;
                    }
                }

                return;
            }

            (_usBookFile, _usBookRead) = (path, written);
            if (written == DateTime.MinValue) {
                _usBook = [];
                _usStatus.Text = $"No spellbook read yet. Log in with {UsName(wizard)} on this PC and press Read my spellbook.";
                _usStatus.Foreground = null;
                RenderUnlearnSpells();
                return;
            }

            if (_usSent != default && written >= _usSent && _usUnlearnSent == default) {
                _usTimer.Stop();
                _usSent = default;
            }

            var when = "";
            var points = 0;
            var unlearned = 0;
            var book = new List<UsSpell>();
            foreach (var line in File.ReadLines(path)) {
                var parts = line.Split('\t');
                switch (parts[0]) {
                    case "written" when parts.Length > 1:
                        when = parts[1];
                        break;
                    case "points" when parts.Length > 1:
                        int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out points);
                        break;
                    case "unlearned" when parts.Length > 1:
                        int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out unlearned);
                        break;
                    case "spell" when parts.Length > 4 && uint.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id):
                        int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var back);
                        book.Add(new UsSpell(parts[1], id, parts[3], back, parts.Length > 5 ? parts[5] : ""));
                        break;
                }
            }

            _usBook = book;
            _usGiveBack.IsVisible = unlearned > 0;
            _usGiveBack.Content = $"Give back {unlearned} unlearned spell{(unlearned == 1 ? "" : "s")}";
            _usTicked.IntersectWith(book.Select(s => s.Id));
            _usStatus.Text = (_usLastResult.Length > 0 ? _usLastResult + "  " : "") +
                             $"{UsName(wizard)} knows {book.Count} spells and has {points} training points" +
                             (when.Length > 0 ? $"  ·  read {when}." : ".");
            _usStatus.Foreground = null;
            RenderUnlearnSpells();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Being written: read on the next look.
        }
    }

    private void ReadUnlearnResult(FfWizard wizard, string path) {
        if (_usUnlearnSent == default) {
            return;
        }

        try {
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < _usUnlearnSent) {
                return;
            }

            var removed = new List<string>();
            var points = 0;
            foreach (var line in File.ReadLines(path)) {
                var parts = line.Split('\t');
                if (parts[0] == "removed" && parts.Length > 1) {
                    removed.Add(parts[1]);
                }
                else if (parts[0] == "points" && parts.Length > 1) {
                    int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out points);
                }
            }

            _usUnlearnSent = default;
            _usTicked.Clear();
            _usLastResult = removed.Count == 0
                ? "Nothing was unlearned (the wizard no longer knew those spells)."
                : $"Unlearned {removed.Count} spells" + (points > 0 ? $" and gave back {points} training points." : ".");
            Say(_usLastResult + (removed.Count > 0 ? " " + string.Join(", ", removed.Take(12)) + (removed.Count > 12 ? ", ..." : "") : ""));
            _usBookRead = default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        }
    }

    private void RenderUnlearnSpells() {
        _usList.Children.Clear();
        var shown = ShownSpells().ToList();
        var ticked = _usBook.Where(s => _usTicked.Contains(s.Id)).ToList();
        _usCount.Text = _usBook.Count == 0
            ? "No spells to show yet."
            : $"{shown.Count} of {_usBook.Count} spells shown  ·  {ticked.Count} ticked" +
              (ticked.Sum(s => s.Points) > 0 ? $" ({ticked.Sum(s => s.Points)} training points back)" : "");

        foreach (var spell in shown) {
            var extra = new List<string>();
            if (spell.School.Length > 0) {
                extra.Add(spell.School);
            }

            extra.Add(spell.Points > 0 ? $"{spell.Points} training point{(spell.Points == 1 ? "" : "s")} back" : "free, no points back");
            if (spell.Kind.Length > 0) {
                extra.Add(spell.Kind == "Boss" ? "boss spell" : "dev spell");
            }

            var box = new CheckBox {
                Content = new TextBlock {
                    Inlines = {
                        new Avalonia.Controls.Documents.Run(spell.Name) { FontWeight = FontWeight.SemiBold },
                        new Avalonia.Controls.Documents.Run("   " + string.Join("  ·  ", extra)) { Foreground = MUTED },
                    },
                    TextWrapping = TextWrapping.Wrap,
                },
                IsChecked = _usTicked.Contains(spell.Id),
            };
            ToolTip.SetTip(box, $"Template ID {spell.Id}");
            var id = spell.Id;
            box.IsCheckedChanged += (_, _) => {
                if (box.IsChecked == true) {
                    _usTicked.Add(id);
                }
                else {
                    _usTicked.Remove(id);
                }

                var now = _usBook.Where(s => _usTicked.Contains(s.Id)).ToList();
                _usCount.Text = $"{shown.Count} of {_usBook.Count} spells shown  ·  {now.Count} ticked" +
                                (now.Sum(s => s.Points) > 0 ? $" ({now.Sum(s => s.Points)} training points back)" : "");
            };
            _usList.Children.Add(box);
        }
    }

    private static string UsName(FfWizard wizard) => wizard.Name.Length > 0 ? wizard.Name : "this wizard";

}
