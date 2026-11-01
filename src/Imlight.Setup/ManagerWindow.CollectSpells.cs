using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// Progress > Collect Spells (Niduus, 2026-10-29: "is there a 'Collect Spells' under Progress for that wizard to get all
/// their quest, main, side and progress spells?"). Writes which spells to collect to fast-forward/&lt;wizard id&gt;-spells.txt
/// beside the server and sends '.quest collectspells'; the server teaches them (QuestService.CollectSpells) and writes
/// what it taught to &lt;wizard id&gt;-spells-result.txt, shown here. With "Check my spellbook" it also lists the spells
/// in the book that no part of the real game gives (tick and remove them) and the real game's spells still missing, with
/// why (Niduus, 2026-10-30).
/// </summary>
internal sealed partial class ManagerWindow {

    private readonly ComboBox _csWizard = new() { MinWidth = 220, PlaceholderText = "Pick a wizard" };
    private readonly CheckBox _csFinished = new() { Content = "Spells from quests I finished (main and side)", IsChecked = true };
    private readonly CheckBox _csSide = new() { Content = "Spells from quests at or below my level I haven't done (main story and side)", IsChecked = true };
    private readonly CheckBox _csTrainers = new() { Content = "Trainer spells my level and school allow (the free ones)", IsChecked = true };
    private readonly CheckBox _csSkills = new() { Content = "Cantrips, and fishing, gardening and monster magic spells my ranks allow", IsChecked = true };
    private readonly CheckBox _csDrops = new() { Content = "Spells creatures at or below my level drop (lore spells such as Fire Dragon)", IsChecked = true };
    private readonly CheckBox _csPaid = new() { Content = "Also train spells that cost training points, while I have them", IsChecked = false };
    private readonly CheckBox _csCheck = new() { Content = "Check my spellbook (spells not from the real game, spells still missing)", IsChecked = true };
    private readonly StackPanel _csResult = new() { Spacing = 4 };
    private readonly DispatcherTimer _csTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private DateTime _csSent;
    private DateTime _csResultRead;
    private string _csResultFile = "";
    private bool _csFilling;
    private DateTime _csRemoveSent;
    private string _csRemoved = "";

    private Control CollectSpellsTab() {
        _csTimer.Tick += (_, _) => ReadCollectSpellsResult();
        _csWizard.SelectionChanged += (_, _) => {
            if (!_csFilling) {
                _csResultRead = default;
                ReadCollectSpellsResult();
            }
        };

        var go = Primary("Collect spells");
        ToolTip.SetTip(go, "Teaches the picked wizard every spell the ticked kinds should have given them by now. " +
                           "Start the server and log in with that wizard on this PC first.");
        var refresh = Plain("Refresh");
        ToolTip.SetTip(refresh, "Reads your wizards again.");

        return Page(
            Fold("sparkle", "Collect Spells", "Collect Spells",
                "Give a wizard every spell their quests and trainers should have given them by now.",
                WithHint(new WrapPanel {
                    Children = { Spaced(_csWizard), Spaced(Action(refresh, () => { LoadCollectSpells(); return Task.CompletedTask; })) },
                }, "Pick your wizard. Log in with that wizard on this PC before you press Collect spells."),
                WithHint(new WrapPanel {
                    Children = { SearchTick(_csFinished), SearchTick(_csSide), SearchTick(_csTrainers), SearchTick(_csSkills), SearchTick(_csDrops), SearchTick(_csPaid), SearchTick(_csCheck) },
                }, "Quests: every spell a quest gives, for your school only. Quests you haven't done: their spells too, but not " +
                   "the quests themselves, and only quests of your level or lower. Trainers: the spells of every game " +
                   "trainer that your level, school and earlier spells allow and that cost no training points (your own " +
                   "school's). Training points: also buys the spells that cost points (astral, shadow, your second school), " +
                   "paying like the trainer would. Cantrips and skills: every cantrip, and the fishing, gardening and monster " +
                   "magic spells your ranks allow (no gold taken). Drops: every spell a creature of your level or lower can " +
                   "drop, such as the lore spells. Check: lists the spells in your book that the real game never gives, so " +
                   "you can take them out, and the real game spells you're still missing, with what's in the way. " +
                   "Spells you already know are left alone."),
                new WrapPanel { Children = { Spaced(Action(go, StartCollectSpellsAsync)) } }),
            Fold("list", "Collect Spells", "What it gave", "The spells the last Collect Spells taught the picked wizard.", _csResult));
    }

    /// <summary>Fills the wizards. Runs when the tab shows.</summary>
    private void LoadCollectSpells() {
        FillFastForwardWizards();
        var wizards = (_ffWizard.ItemsSource as List<FfWizard>) ?? [];
        var picked = (_csWizard.SelectedItem as FfWizard)?.Id;
        _csFilling = true;
        _csWizard.ItemsSource = wizards;
        _csWizard.SelectedItem = wizards.FirstOrDefault(w => w.Id == picked) ?? (wizards.Count == 1 ? wizards[0] : null);
        _csFilling = false;
        _csResultRead = default;
        ReadCollectSpellsResult();
    }

    private async Task StartCollectSpellsAsync() {
        if (_csWizard.SelectedItem is not FfWizard wizard) {
            throw new PanelException("Pick a wizard first. The list fills once that wizard has played on your server.");
        }

        var kinds = new List<string>();
        if (_csFinished.IsChecked == true) {
            kinds.Add("finished");
        }

        if (_csSide.IsChecked == true) {
            kinds.Add("side");
        }

        if (_csTrainers.IsChecked == true) {
            kinds.Add("trainers");
        }

        if (_csSkills.IsChecked == true) {
            kinds.Add("skills");
        }

        if (_csDrops.IsChecked == true) {
            kinds.Add("drops");
        }

        if (_csPaid.IsChecked == true) {
            kinds.Add("paid");
        }

        if (_csCheck.IsChecked == true) {
            kinds.Add("check");
        }

        if (kinds.Count == 0) {
            throw new PanelException("Tick at least one kind of spell.");
        }

        var name = wizard.Name.Length > 0 ? wizard.Name : "this wizard";
        if (!await ConfirmAsync($"Teach {name} every spell the ticked kinds should have given them by now? " +
                                $"{name} must be logged in on this PC.")) {
            return;
        }

        var folder = Path.Combine(_manager.ServerFolder, FF_FOLDER);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, wizard.Id + "-spells.txt"), string.Join('\n', kinds) + "\n");
        TryDeleteFile(Path.Combine(folder, wizard.Id + "-spells-result.txt"));
        _manager.SendCommand(".quest collectspells", wizard.Id);
        _csSent = DateTime.UtcNow;
        _csResultRead = default;
        _csRemoved = "";
        _csResult.Children.Clear();
        _csResult.Children.Add(Sub("Sent. The game's chat says when it is done; this shows what it gave."));
        _csTimer.Start();
        Say($"Collect Spells sent for {name}.");
    }

    private void ReadCollectSpellsResult() {
        if (_csWizard.SelectedItem is not FfWizard wizard) {
            _csResult.Children.Clear();
            _csResult.Children.Add(Sub("Pick a wizard."));
            return;
        }

        ReadRemoveSpellsResult(wizard);
        var path = Path.Combine(_manager.ServerFolder, FF_FOLDER, wizard.Id + "-spells-result.txt");
        try {
            var written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (path == _csResultFile && written == _csResultRead) {
                // Nothing yet. The server runs commands one after another, so a big one sent before (such as
                // .spellements) can hold it up; after half a minute, say so and keep looking.
                if (_csSent != default && written < _csSent && DateTime.UtcNow - _csSent > TimeSpan.FromSeconds(30)
                    && _csResult.Children.Count < 2) {
                    _csResult.Children.Add(new TextBlock {
                        Text = $"Nothing back yet. If the game's chat never said \"Collect Spells: looking...\", check that " +
                               $"{(wizard.Name.Length > 0 ? wizard.Name : "the wizard")} is logged in on this PC with the server running. " +
                               "If another big command was sent just before, this waits for it to finish; this page keeps looking.",
                        TextWrapping = TextWrapping.Wrap, Foreground = WARN,
                    });
                }

                return;
            }

            (_csResultFile, _csResultRead) = (path, written);
            _csResult.Children.Clear();
            if (written == DateTime.MinValue) {
                _csResult.Children.Add(Sub(_csSent != default ? "Waiting for the server..." : "This wizard hasn't used Collect Spells yet."));
                return;
            }

            if (written >= _csSent && _csRemoveSent == default) {
                _csTimer.Stop();
                _csSent = default;
            }

            var when = "";
            var points = 0;
            var checkedBook = false;
            var spells = new List<(string Spell, string From)>();
            var extras = new List<(string Spell, uint Id)>();
            var missing = new List<(string Spell, string Why)>();
            foreach (var line in File.ReadLines(path)) {
                var parts = line.Split('\t');
                switch (parts[0]) {
                    case "written" when parts.Length > 1:
                        when = parts[1];
                        break;
                    case "points" when parts.Length > 1:
                        int.TryParse(parts[1], out points);
                        break;
                    case "checked" when parts.Length > 1:
                        checkedBook = parts[1] == "1";
                        break;
                    case "spell" when parts.Length > 2:
                        spells.Add((parts[1], parts[2]));
                        break;
                    case "extra" when parts.Length > 2 && uint.TryParse(parts[2], out var id):
                        extras.Add((parts[1], id));
                        break;
                    case "missing" when parts.Length > 2:
                        missing.Add((parts[1], parts[2]));
                        break;
                }
            }

            if (_csRemoved.Length > 0) {
                _csResult.Children.Add(new TextBlock { Text = _csRemoved, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            }

            _csResult.Children.Add(new TextBlock {
                Text = spells.Count == 0 ? "No new spells to give." : $"{spells.Count} spells learned" + (points > 0 ? $" ({points} training points spent)." : "."),
                FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
            });
            if (when.Length > 0) {
                _csResult.Children.Add(Sub($"Written {when}."));
            }

            foreach (var (spell, from) in spells) {
                _csResult.Children.Add(new TextBlock { Text = $"{spell}  ·  from {from}", TextWrapping = TextWrapping.Wrap });
            }

            if (!checkedBook) {
                return;
            }

            _csResult.Children.Add(new TextBlock {
                Text = extras.Count == 0
                    ? "Every spell in the spellbook comes from the real game."
                    : $"{extras.Count} spells in the spellbook that the real game never gives (no trainer, quest, upgrade or fusion teaches them):",
                FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 10, 0, 0),
            });
            if (extras.Count > 0) {
                var boxes = new List<(CheckBox Box, uint Id)>();
                foreach (var (spell, id) in extras) {
                    var box = new CheckBox { Content = spell, IsChecked = true };
                    boxes.Add((box, id));
                    _csResult.Children.Add(box);
                }

                var remove = Plain("Remove the ticked spells");
                ToolTip.SetTip(remove, "Takes the ticked spells out of the spellbook and decks, and gives back any training points they cost. " +
                                       "The wizard must be logged in on this PC.");
                _csResult.Children.Add(new WrapPanel {
                    Children = { Spaced(Action(remove, () => RemoveTickedSpellsAsync(wizard, [.. boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Id)]))) },
                });
            }

            _csResult.Children.Add(new TextBlock {
                Text = missing.Count == 0
                    ? "No real game trainer spells missing for your school, second school, astral schools or shadow magic."
                    : $"{missing.Count} real game spells still missing (your school, second school, astral schools and shadow magic, up to your level):",
                FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Avalonia.Thickness(0, 10, 0, 0),
            });
            foreach (var (spell, why) in missing) {
                _csResult.Children.Add(new TextBlock { Text = $"{spell}  ·  {why}", TextWrapping = TextWrapping.Wrap });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        }
    }

    private async Task RemoveTickedSpellsAsync(FfWizard wizard, List<uint> ids) {
        if (ids.Count == 0) {
            throw new PanelException("Tick at least one spell to remove.");
        }

        var name = wizard.Name.Length > 0 ? wizard.Name : "this wizard";
        if (!await ConfirmAsync($"Take {ids.Count} spells out of {name}'s spellbook and decks? Training points they cost come back. " +
                                $"{name} must be logged in on this PC.")) {
            return;
        }

        var folder = Path.Combine(_manager.ServerFolder, FF_FOLDER);
        Directory.CreateDirectory(folder);
        TryDeleteFile(Path.Combine(folder, wizard.Id + "-spells-remove-result.txt"));
        await File.WriteAllTextAsync(Path.Combine(folder, wizard.Id + "-spells-remove.txt"), string.Join('\n', ids) + "\n");
        _manager.SendCommand(".quest removespells", wizard.Id);
        _csRemoveSent = DateTime.UtcNow;
        _csTimer.Start();
        Say($"Asked the server to remove {ids.Count} spells from {name}.");
    }

    // When the removal is done: say so, and check the spellbook again so the lists are up to date.
    private void ReadRemoveSpellsResult(FfWizard wizard) {
        if (_csRemoveSent == default) {
            return;
        }

        var path = Path.Combine(_manager.ServerFolder, FF_FOLDER, wizard.Id + "-spells-remove-result.txt");
        try {
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < _csRemoveSent) {
                return;
            }

            var removed = 0;
            var points = 0;
            foreach (var line in File.ReadLines(path)) {
                var parts = line.Split('\t');
                if (parts[0] == "removed") {
                    removed++;
                }
                else if (parts[0] == "points" && parts.Length > 1) {
                    int.TryParse(parts[1], out points);
                }
            }

            _csRemoveSent = default;
            _csRemoved = $"Removed {removed} spells" + (points > 0 ? $" and gave back {points} training points." : ".");
            Say(_csRemoved);

            // A check only, so the lists show what is left.
            var folder = Path.Combine(_manager.ServerFolder, FF_FOLDER);
            File.WriteAllText(Path.Combine(folder, wizard.Id + "-spells.txt"), "check\n");
            _manager.SendCommand(".quest collectspells", wizard.Id);
            _csSent = DateTime.UtcNow;
            _csResultRead = default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        }
    }

}
