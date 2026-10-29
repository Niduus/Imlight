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
/// what it taught to &lt;wizard id&gt;-spells-result.txt, shown here.
/// </summary>
internal sealed partial class ManagerWindow {

    private readonly ComboBox _csWizard = new() { MinWidth = 220, PlaceholderText = "Pick a wizard" };
    private readonly CheckBox _csFinished = new() { Content = "Spells from quests I finished (main and side)", IsChecked = true };
    private readonly CheckBox _csSide = new() { Content = "Spells from side quests at or below my level that I skipped", IsChecked = true };
    private readonly CheckBox _csTrainers = new() { Content = "Trainer spells my level and school allow (the free ones)", IsChecked = true };
    private readonly StackPanel _csResult = new() { Spacing = 4 };
    private readonly DispatcherTimer _csTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private DateTime _csSent;
    private DateTime _csResultRead;
    private string _csResultFile = "";
    private bool _csFilling;

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
                    Children = { SearchTick(_csFinished), SearchTick(_csSide), SearchTick(_csTrainers) },
                }, "Quests: every spell a quest gives, for your school only. Side quests you skipped: their spells too, but not " +
                   "the quests themselves, and only side quests of your level or lower. Trainers: the spells of every game " +
                   "trainer that your level, school and earlier spells allow and that cost no training points (your own " +
                   "school's). Other schools' spells cost training points, so train those yourself at the trainer. " +
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

        var path = Path.Combine(_manager.ServerFolder, FF_FOLDER, wizard.Id + "-spells-result.txt");
        try {
            var written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (path == _csResultFile && written == _csResultRead) {
                // Nothing yet: after half a minute, the wizard was probably not logged in.
                if (_csSent != default && written < _csSent && DateTime.UtcNow - _csSent > TimeSpan.FromSeconds(30)) {
                    _csTimer.Stop();
                    _csSent = default;
                    _csResult.Children.Clear();
                    _csResult.Children.Add(new TextBlock {
                        Text = $"Nothing happened yet. Is {(wizard.Name.Length > 0 ? wizard.Name : "the wizard")} logged in on this PC, " +
                               "with the server running? Log in and press Collect spells again.",
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

            if (written >= _csSent) {
                _csTimer.Stop();
                _csSent = default;
            }

            var when = "";
            var spells = new List<(string Spell, string From)>();
            foreach (var line in File.ReadLines(path)) {
                var parts = line.Split('\t');
                if (parts[0] == "written" && parts.Length > 1) {
                    when = parts[1];
                }
                else if (parts[0] == "spell" && parts.Length > 2) {
                    spells.Add((parts[1], parts[2]));
                }
            }

            _csResult.Children.Add(new TextBlock {
                Text = spells.Count == 0 ? "No new spells: this wizard already had every spell they should have." : $"{spells.Count} spells learned.",
                FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
            });
            if (when.Length > 0) {
                _csResult.Children.Add(Sub($"Written {when}."));
            }

            foreach (var (spell, from) in spells) {
                _csResult.Children.Add(new TextBlock { Text = $"{spell}  ·  from {from}", TextWrapping = TextWrapping.Wrap });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        }
    }

}
