using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Imlight.CoreLib.Shared.Character;

namespace Imlight.Setup;

/// <summary>
/// Training points from leveling, on the Spell trainers card (Niduus, 2026-10-27: "add natural training point gain to
/// config and able to have a modify such as 'Gain X training point after Z level'"). [Trainers] NaturalTrainingPoints
/// is the game's own gain; TrainingPointRules is a list of rules that add points on top of it, written in the words
/// TrainingPointRules.cs reads (the server's own file). Both apply live, from the next level a wizard reaches.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string TP_SECTION = "Trainers";
    private const string TP_NATURAL = "NaturalTrainingPoints";
    private const string TP_RULES = "TrainingPointRules";
    private const int TP_TOP_LEVEL = 180;

    private readonly ToggleSwitch _naturalTp = new() { OnContent = "On", OffContent = "Off" };
    private readonly StackPanel _tpRuleRows = new() { Spacing = 4 };
    private readonly TextBlock _tpPreview = Sub("");
    private readonly TextBlock _tpNote = Sub("");
    private readonly List<TrainingPointRules.Rule> _tpRules = [];
    // The setting's text the rows show, so a refresh only redraws them when the file changed elsewhere.
    private string? _tpRulesShown;
    private bool _tpRulesSupported;

    /// <summary>The game's own training points for reaching a level (Root.wad MagicXPConfig, read 2026-10-27), for the preview.</summary>
    private static int GameTrainingPoints(int level)
        => level is 4 or 8 or 12 or 16 or 20 || level is >= 25 and <= TP_TOP_LEVEL && level % 5 == 0 ? 1 : 0;

    private Control TrainingPointRows() {
        _naturalTp.IsCheckedChanged += async (_, _) => {
            ShowTpPreview();
            if (!_updatingRandomizer) {
                await Guard(() => SetSwitchAsync(TP_SECTION, TP_NATURAL, "The game's own training points from leveling", _naturalTp.IsChecked == true));
            }
        };

        var add = Action(Plain("Add a rule"), () => {
            _tpRules.Add(new TrainingPointRules.Rule(1, 10));
            DrawTpRules();

            return SaveTpRulesAsync();
        });

        return new StackPanel {
            Spacing = 4,
            Children = {
                SwitchRow("Training points from leveling, like the real game", _naturalTp,
                    "1 point at levels 4, 8, 12, 16 and 20, then 1 every 5 levels (25, 30 ... 180). " +
                    "Off: leveling only gives what your rules below give."),
                WithMargin(new TextBlock { Text = "Extra training points for leveling", FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    new Thickness(0, 8, 0, 0)),
                Sub("Each rule adds points on top of the game's own. To have your rules replace the game's, turn the switch above off. " +
                    "Changes count right away, from the next level a wizard reaches."),
                _tpRuleRows,
                Row(add),
                _tpPreview,
                _tpNote,
            },
        };
    }

    private void DrawTpRules() {
        _tpRuleRows.Children.Clear();
        if (_tpRules.Count == 0) {
            _tpRuleRows.Children.Add(Sub("No rules yet: leveling gives only the game's own points (if the switch is on)."));
        }

        for (var i = 0; i < _tpRules.Count; i++) {
            _tpRuleRows.Children.Add(TpRuleRow(i));
        }

        ShowTpPreview();
    }

    private Control TpRuleRow(int index) {
        var rule = _tpRules[index];
        var words = Sub(TrainingPointRules.Describe(rule));
        NumericUpDown Box(int value, int min, int width) => new() {
            Minimum = min, Maximum = TrainingPointRules.MAX_LEVEL, Increment = 1, FormatString = "0", Width = width, Value = value,
            VerticalAlignment = VerticalAlignment.Center,
        };
        TextBlock Word(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 0) };

        var points = Box(rule.Points, 1, 120);
        var every = Box(rule.Every, 1, 120);
        var after = Box(rule.After, 0, 120);
        var until = Box(rule.Until, 0, 120);
        ToolTip.SetTip(until, "0 = no end");
        var remove = Action(Danger(Plain("Remove")), () => {
            if (index < _tpRules.Count) {
                _tpRules.RemoveAt(index);
            }

            DrawTpRules();

            return SaveTpRulesAsync();
        });

        foreach (var box in new[] { points, every, after, until }) {
            box.ValueChanged += async (_, _) => {
                if (index >= _tpRules.Count) {
                    return;
                }

                var changed = new TrainingPointRules.Rule((int) (points.Value ?? 1), (int) (after.Value ?? 0),
                    Math.Max(1, (int) (every.Value ?? 1)), (int) (until.Value ?? 0));
                if (changed.Points <= 0 || changed == _tpRules[index]) {
                    return;
                }

                _tpRules[index] = changed;
                words.Text = TrainingPointRules.Describe(changed);
                ShowTpPreview();
                await Guard(SaveTpRulesAsync);
            };
        }

        var line = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 4 };
        line.Children.AddRange([
            Word("Gain"), points, Word("training points every"), every, Word("level(s) after level"), after,
            Word("up to level"), until, Word("(0 = no end)"), remove,
        ]);

        return new Border {
            Background = ManagerTheme.SURFACE_HIGH_BRUSH, CornerRadius = new CornerRadius(ManagerTheme.RADIUS_SMALL), Padding = new Thickness(14, 6, 10, 6),
            Child = new StackPanel { Spacing = 4, Children = { line, words } },
        };
    }

    private Task SaveTpRulesAsync() {
        var text = TrainingPointRules.Format(_tpRules);
        if (!_tpRulesSupported) {
            _tpNote.Text = "Click \"Update Ahziael\" at the top of the app once so the server knows training point rules; then they save.";

            return Task.CompletedTask;
        }

        if (text == _tpRulesShown) {
            return Task.CompletedTask;
        }

        _manager.SaveSettings([new Setting(TP_SECTION, TP_RULES, text, "")]);
        _tpRulesShown = text;
        _settings = [];
        _tpNote.Text = _tpRules.Count == 0 ? "No extra training point rules. It applies right away."
            : "Training point rules saved. They count from the next level a wizard reaches.";

        return Task.CompletedTask;
    }

    private void ShowTpPreview() {
        int Total(int toLevel) {
            var points = 0;
            for (var level = 2; level <= toLevel; level++) {
                points += (_naturalTp.IsChecked == true ? GameTrainingPoints(level) : 0) + TrainingPointRules.For(_tpRules, level);
            }

            return points;
        }

        int Game(int toLevel) => Enumerable.Range(2, toLevel - 1).Sum(GameTrainingPoints);

        _tpPreview.Text = $"From leveling alone, a new wizard has {Total(50)} training points at level 50, {Total(100)} at level 100 " +
            $"and {Total(TP_TOP_LEVEL)} at level {TP_TOP_LEVEL} (the real game: {Game(50)}, {Game(100)} and {Game(TP_TOP_LEVEL)}). " +
            "Quests and drops that give training points come on top.";
    }

    /// <summary>Shows the two settings from Imlight.ini, and turns the older "points for every level" switch into a rule.</summary>
    private void ShowTrainingPoints(Dictionary<(string Section, string Key), string> ini) {
        bool Has(string key) => ini.ContainsKey((TP_SECTION, key));
        bool On(string key) => ini.TryGetValue((TP_SECTION, key), out var value) && value.Equals("true", StringComparison.OrdinalIgnoreCase);

        // A server without the key still gives the game's points, so the switch shows on.
        _naturalTp.IsEnabled = Has(TP_NATURAL);
        _naturalTp.IsChecked = !Has(TP_NATURAL) || On(TP_NATURAL);
        _tpRulesSupported = Has(TP_RULES);

        var text = ini.GetValueOrDefault((TP_SECTION, TP_RULES)) ?? "";
        if (_tpRulesSupported && On("TrainingPointPerLevel")) {
            var each = int.TryParse(ini.GetValueOrDefault((TP_SECTION, "TrainingPointsPerLevel")), out var n) ? Math.Clamp(n, 0, 100) : 1;
            var rules = TrainingPointRules.Parse(text);
            if (each > 0) {
                rules.Add(new TrainingPointRules.Rule(each, 0));
            }

            text = TrainingPointRules.Format(rules);
            try {
                _manager.SaveSettings([
                    new Setting(TP_SECTION, TP_RULES, text, ""),
                    new Setting(TP_SECTION, "TrainingPointPerLevel", "false", ""),
                ]);
                _settings = [];
                _tpNote.Text = $"Your \"{each} training points for every level\" switch is now a rule below (the same points as before).";
            }
            catch (Exception) {
                // Shown again on the next refresh.
            }
        }

        if (text == _tpRulesShown || _tpRuleRows.Children.OfType<Control>().Any(row => row.IsKeyboardFocusWithin)) {
            return;
        }

        _tpRulesShown = text;
        _tpRules.Clear();
        _tpRules.AddRange(TrainingPointRules.Parse(text));
        DrawTpRules();
    }

}
