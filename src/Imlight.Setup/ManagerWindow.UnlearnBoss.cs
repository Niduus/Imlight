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
/// Spells > Unlearn dev/boss spells (Niduus, 2026-10-29: "add an 'Unlearn dev/boss' spells button to spells. This way,
/// people can test spells while also remaining legit to their desired characters"). Sends '.unlearnboss yes' to the
/// picked wizard; the server (BossDevSpells) takes Merle's boss spells and the dev spells out of their spellbook and decks,
/// gives back the training points they cost and writes fast-forward/&lt;wizard id&gt;-unlearn-result.txt, shown here.
/// </summary>
internal sealed partial class ManagerWindow {

    private readonly ComboBox _ubWizard = new() { MinWidth = 220, PlaceholderText = "Pick a wizard" };
    private readonly StackPanel _ubResult = new() { Spacing = 2 };
    private readonly DispatcherTimer _ubTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private DateTime _ubSent;
    private bool _ubFilling;

    private Control UnlearnBossRow() {
        _ubTimer.Tick += (_, _) => ReadUnlearnBossResult();
        _ubWizard.SelectionChanged += (_, _) => {
            if (!_ubFilling) {
                ReadUnlearnBossResult();
            }
        };

        var go = Primary("Unlearn dev/boss spells");
        ToolTip.SetTip(go, "Takes every boss spell (Merle Ambrose's list) and dev/test spell out of the picked wizard's spellbook " +
                           "and decks, and gives back the training points they cost. Start the server and log in with that wizard on this PC first.");

        var panel = new StackPanel {
            Spacing = 6,
            Children = {
                WithHint(new WrapPanel { Children = { Spaced(_ubWizard), Spaced(Action(go, StartUnlearnBossAsync)) } },
                    "Tested boss or dev spells and want your wizard legit again? Pick the wizard and press Unlearn dev/boss spells. " +
                    "Only boss and dev spells go: spells from quests, trainers and drops stay. In the game, .unlearnboss does the same."),
                _ubResult,
            },
        };
        panel.AttachedToVisualTree += (_, _) => LoadUnlearnBossWizards();

        return panel;
    }

    private void LoadUnlearnBossWizards() {
        FillFastForwardWizards();
        var wizards = (_ffWizard.ItemsSource as List<FfWizard>) ?? [];
        var picked = (_ubWizard.SelectedItem as FfWizard)?.Id;
        _ubFilling = true;
        _ubWizard.ItemsSource = wizards;
        _ubWizard.SelectedItem = wizards.FirstOrDefault(w => w.Id == picked) ?? (wizards.Count == 1 ? wizards[0] : null);
        _ubFilling = false;
        ReadUnlearnBossResult();
    }

    private async Task StartUnlearnBossAsync() {
        if (_ubWizard.SelectedItem is not FfWizard wizard) {
            throw new PanelException("Pick a wizard first. The list fills once that wizard has played on your server.");
        }

        var name = wizard.Name.Length > 0 ? wizard.Name : "this wizard";
        if (!await ConfirmAsync($"Take every boss and dev spell out of {name}'s spellbook and decks, and give back the training " +
                                $"points they cost? {name} must be logged in on this PC.")) {
            return;
        }

        var folder = Path.Combine(_manager.ServerFolder, FF_FOLDER);
        TryDeleteFile(Path.Combine(folder, wizard.Id + "-unlearn-result.txt"));
        _manager.SendCommand(".unlearnboss yes", wizard.Id);
        _ubSent = DateTime.UtcNow;
        _ubResult.Children.Clear();
        _ubResult.Children.Add(Sub("Sent. The game's chat says when it is done; this shows what it took."));
        _ubTimer.Start();
        Say($"Unlearn dev/boss spells sent for {name}.");
    }

    private void ReadUnlearnBossResult() {
        _ubResult.Children.Clear();
        if (_ubWizard.SelectedItem is not FfWizard wizard) {
            return;
        }

        var path = Path.Combine(_manager.ServerFolder, FF_FOLDER, wizard.Id + "-unlearn-result.txt");
        try {
            var written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (written == DateTime.MinValue) {
                if (_ubSent == default) {
                    return;
                }

                // Nothing after half a minute: the wizard was probably not logged in.
                if (DateTime.UtcNow - _ubSent > TimeSpan.FromSeconds(30)) {
                    _ubTimer.Stop();
                    _ubSent = default;
                    _ubResult.Children.Add(new TextBlock {
                        Text = $"Nothing happened yet. Is {(wizard.Name.Length > 0 ? wizard.Name : "the wizard")} logged in on this PC, " +
                               "with the server running? Log in and press Unlearn dev/boss spells again.",
                        TextWrapping = TextWrapping.Wrap, Foreground = WARN,
                    });
                }
                else {
                    _ubResult.Children.Add(Sub("Waiting for the server..."));
                }

                return;
            }

            if (written >= _ubSent) {
                _ubTimer.Stop();
                _ubSent = default;
            }

            var when = "";
            var spells = new List<(string Name, string Kind, int Points)>();
            foreach (var line in File.ReadLines(path)) {
                var parts = line.Split('\t');
                if (parts[0] == "written" && parts.Length > 1) {
                    when = parts[1];
                }
                else if (parts[0] == "spell" && parts.Length > 4) {
                    spells.Add((parts[1], parts[2], int.TryParse(parts[4], out var points) ? points : 0));
                }
            }

            var back = spells.Sum(s => s.Points);
            _ubResult.Children.Add(new TextBlock {
                Text = spells.Count == 0
                    ? "Last time: no boss or dev spells. This wizard's spellbook is legit."
                    : $"Last time: unlearned {spells.Count} spells ({spells.Count(s => s.Kind == "Boss")} boss, {spells.Count(s => s.Kind == "Dev")} dev)" +
                      $"{(back > 0 ? $", {back} training points back" : "")}{(when.Length > 0 ? $"  ·  {when}" : "")}.",
                FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
            });
            if (spells.Count > 0) {
                _ubResult.Children.Add(new TextBlock {
                    Text = string.Join(",  ", spells.Select(s => $"{s.Name} ({s.Kind}{(s.Points > 0 ? $", {s.Points} TP" : "")})")),
                    TextWrapping = TextWrapping.Wrap, Foreground = MUTED,
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        }
    }

}
