/*
 * Imlight
 * Copyright (C) 2026 Revive101
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
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// Game Rules > Animations (Niduus, 2026-11-02: "Implement in the manager a Animations tab so players can see the total
/// animation seconds for config customization in great detail of each spell"). The game never tells the server it has
/// played a spell, so each round waits the server's estimate of its animations. This tab lists every spell with the
/// seconds a round waits for it, and, picked, the animation's stages from the game files and how the seconds add up.
/// Any animation's time, and the waits that are not one spell (a pass, a charm used up, a boss's line...), can be set
/// here. The server writes the list (spell-animations.json in the ID lookup folder); the times set here are kept in
/// spiraldb-extra/Customs/AnimationTimes.json, which the server's AnimationTimes.cs watches, so they count from the next
/// round with no restart.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string SPELL_ANIMATIONS_FILE = "spell-animations.json";
    private const string ANIMATION_TIMES_FILE = "AnimationTimes.json";
    // The most seconds the boxes take (the server stops there too).
    private const double ANIMATION_MAX_SECONDS = 60;

    private sealed record AnimStage(string Name, string Kind, float Seconds);
    private sealed record AnimPart(string What, float Seconds);
    private sealed record AnimInfo(string Name, float Fast, float Old, List<AnimPart> Parts, List<AnimPart> OldParts, List<AnimStage> Stages, bool Found);
    private sealed record AnimSpell(uint Id, string Name, string Shown, string School, int Pips, bool XPips, bool Treasure, string Animation) {
        internal string Title => Shown.Length > 0 ? Shown : Name;
    }

    private sealed record AnimNormal(string Name, float Fast, float Old, string What);
    private sealed record AnimReport(string Written, bool FastTiming, float ExtraSeconds, List<AnimNormal> General, List<AnimInfo> Animations, List<AnimSpell> Spells);

    private sealed class AnimationTimesFile {
        public Dictionary<string, float> Spells { get; set; } = [];
        public Dictionary<string, float> General { get; set; } = [];
        public Dictionary<string, string> Names { get; set; } = [];
    }

    private sealed record AnimRow(AnimSpell Spell, AnimInfo? Animation, int Sharing, string Haystack);

    private static readonly string[] s_animShow = ["Every spell", "Only changed times", "Only unchanged times", "Only ones with no animation in the game files"];
    private static readonly string[] s_animSorts = ["Name", "Longest wait first", "Shortest wait first", "Animation name", "Most spells sharing it first", "School"];
    private static readonly string[] s_animKinds = [ANY_FILTER, "Spells", "Treasure cards"];

    private static readonly JsonSerializerOptions s_animJson = new() {
        WriteIndented = true, PropertyNameCaseInsensitive = true, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly TextBox _animSearch = new() { Watermark = "Search a spell, its animation or its ID, such as Fire Cat, Meteor or Minotaur" };
    private readonly ComboBox _animSchool = FilterCombo([ANY_FILTER], 140);
    private readonly ComboBox _animKind = FilterCombo(s_animKinds, 140);
    private readonly ComboBox _animShowCombo = FilterCombo(s_animShow, 230);
    private readonly ComboBox _animSort = FilterCombo(s_animSorts, 190);
    private readonly NumericUpDown _animMin = FilterNumber();
    private readonly NumericUpDown _animMax = FilterNumber();
    private readonly NumericUpDown _animPipsMin = FilterNumber();
    private readonly NumericUpDown _animPipsMax = FilterNumber();
    private readonly CheckBox _animHideTests = new() { Content = "Hide test and unnamed spells", IsChecked = true };
    private readonly TextBlock _animCount = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _animSummary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _animGeneral = new() { Spacing = 4 };
    private readonly ListBox _animList = new() { Background = Brushes.Transparent, Classes = { "compact" } };
    private readonly StackPanel _animDetails = new() { Spacing = 6, Margin = new Thickness(0, 0, 18, 0) };
    private DetailsPopup? _animPopup;
    private AnimSpell? _animPicked;

    private AnimReport? _animReport;
    private Dictionary<string, AnimInfo> _animByName = new(StringComparer.OrdinalIgnoreCase);
    private List<AnimRow> _animRows = [];
    private DateTime _animWritten;
    private AnimationTimesFile _animTimes = new();

    private string SpellAnimationsPath => Path.Combine(_manager.IdLookupFolder, SPELL_ANIMATIONS_FILE);
    private string AnimationTimesPath => Path.Combine(_manager.CustomsFolder, ANIMATION_TIMES_FILE);

    private Control AnimationsTab() {
        _animList.ItemTemplate = new FuncDataTemplate<AnimRow>((row, _) => row is null ? new Panel() : AnimRowView(row));
        _animList.SelectionChanged += (_, e) => {
            if (e.Source == _animList && _animList.SelectedItem is AnimRow row && row.Spell != _animPicked) {
                ShowAnimation(row.Spell);
            }
        };
        WhenFiltersChange(RenderAnimations, _animSearch, _animSchool, _animKind, _animShowCombo, _animSort, _animMin, _animMax,
            _animPipsMin, _animPipsMax, _animHideTests);

        var filters = SearchFilters(
            SearchFilter("School", _animSchool),
            SearchFilter("Kind", _animKind),
            SearchFilter("Show", _animShowCombo),
            SearchFilter("Wait from (seconds)", _animMin),
            SearchFilter("Wait to (seconds)", _animMax),
            SearchFilter("Pips from", _animPipsMin),
            SearchFilter("Pips to", _animPipsMax),
            SearchFilter("Sort by", _animSort),
            SearchTick(_animHideTests));

        var details = new ScrollViewer { Width = 680, Content = _animDetails };
        _animPopup = new DetailsPopup("Spell animation", details, () => { _animList.SelectedItem = null; ShowAnimation(null); },
            by => StepList(_animList, by));

        var resetAll = Plain("Put every time back");
        ToolTip.SetTip(resetAll, "Takes away every time set on this page, so the server works them all out from the game files again.");
        var openFile = Plain("Open the times file");
        ToolTip.SetTip(openFile, "Opens AnimationTimes.json, where this page keeps the times you set.");

        var general = new Expander {
            Header = new TextBlock { Text = "Waits that are not one spell's animation", FontWeight = FontWeight.SemiBold },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel {
                Spacing = 8,
                Children = {
                    Sub("A round waits each spell's animation, plus these for what happens around it. Set one to change it everywhere. " +
                        "Empty means the server's own time."),
                    _animGeneral,
                },
            },
        };

        return new Panel {
            Children = {
                new DockPanel {
                    Margin = new Thickness(24, 12, 24, 16),
                    Children = {
                        WithDock(Fold("clock", "Animations", "How long a round waits for each spell",
                            "The game never tells the server it has finished playing a spell, so the server waits its own estimate of each " +
                            "animation before the next turn. Here is every spell's wait in detail. Click one to see how it adds up and to set your own.",
                            WithHint(_animSearch,
                                "Type a spell's name, its animation's name or its ID. With nothing typed, every spell shows. Spells that look the " +
                                "same share one animation, so a time you set counts for all of them. Times you set count from the next round, " +
                                "with no restart. The game's files are never changed."),
                            WithCount(filters, _animCount),
                            new WrapPanel {
                                Children = { Spaced(WithCenter(_animSummary)), Spaced(Action(openFile, OpenAnimationTimesFileAsync)), Spaced(Action(resetAll, ResetAllAnimationTimesAsync)) },
                            },
                            general), Dock.Top),
                        WithMargin(_animList, new Thickness(0, 14, 0, 0)),
                    },
                },
                _animPopup,
            },
        };
    }

    /// <summary>Reads the list again when the server has written it since, and the times set here. Runs when the tab shows.</summary>
    private async Task LoadAnimationsAsync() {
        _animTimes = ReadAnimationTimes();
        var path = SpellAnimationsPath;
        try {
            if (!File.Exists(path)) {
                (_animReport, _animRows, _animByName, _animWritten) = (null, [], new(StringComparer.OrdinalIgnoreCase), default);
            }
            else {
                var written = File.GetLastWriteTimeUtc(path);
                if (written != _animWritten) {
                    _animCount.Text = "Loading…";
                    var (report, rows, byName) = await Task.Run(() => ReadSpellAnimations(path));
                    (_animReport, _animRows, _animByName, _animWritten) = (report, rows, byName, written);
                    FillFilterCombo(_animSchool, rows.Select(r => r.Spell.School).Where(s => s.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            _animCount.Text = $"Could not read {SPELL_ANIMATIONS_FILE}: {ex.Message}";

            return;
        }

        RenderAnimationGeneral();
        RenderAnimations();
    }

    private static (AnimReport Report, List<AnimRow> Rows, Dictionary<string, AnimInfo> ByName) ReadSpellAnimations(string path) {
        var report = JsonSerializer.Deserialize<AnimReport>(File.ReadAllText(path), s_animJson)
                     ?? throw new JsonException("The file is empty.");
        var byName = new Dictionary<string, AnimInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in report.Animations ?? []) {
            if (raw?.Name is { Length: > 0 } name) {
                byName[name] = raw with { Parts = raw.Parts ?? [], OldParts = raw.OldParts ?? [], Stages = raw.Stages ?? [] };
            }
        }

        var spells = (report.Spells ?? []).Where(s => s is not null)
            .Select(s => s with { Name = s.Name ?? "", Shown = s.Shown ?? "", School = s.School ?? "", Animation = s.Animation ?? "" })
            .ToList();
        var sharing = spells.Where(s => s.Animation.Length > 0).GroupBy(s => s.Animation, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var rows = spells.Select(s => new AnimRow(s, byName.GetValueOrDefault(s.Animation), sharing.GetValueOrDefault(s.Animation),
                SearchText.Normalize($"{s.Shown} {s.Name} {s.Animation} {s.Id} {s.School}")))
            .ToList();

        return (report with { General = report.General ?? [] }, rows, byName);
    }

    private AnimationTimesFile ReadAnimationTimes() {
        try {
            if (File.Exists(AnimationTimesPath)) {
                var read = JsonSerializer.Deserialize<AnimationTimesFile>(File.ReadAllText(AnimationTimesPath), s_animJson) ?? new AnimationTimesFile();

                return new AnimationTimesFile {
                    Spells = new Dictionary<string, float>(read.Spells ?? [], StringComparer.OrdinalIgnoreCase),
                    General = new Dictionary<string, float>(read.General ?? [], StringComparer.OrdinalIgnoreCase),
                    Names = new Dictionary<string, string>(read.Names ?? [], StringComparer.OrdinalIgnoreCase),
                };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            Say($"Could not read {ANIMATION_TIMES_FILE}: {ex.Message}", isError: true);
        }

        return new AnimationTimesFile {
            Spells = new(StringComparer.OrdinalIgnoreCase), General = new(StringComparer.OrdinalIgnoreCase), Names = new(StringComparer.OrdinalIgnoreCase),
        };
    }

    private void WriteAnimationTimes(AnimationTimesFile times) {
        Directory.CreateDirectory(_manager.CustomsFolder);
        var path = AnimationTimesPath;
        File.WriteAllText(path + ".saving", JsonSerializer.Serialize(times, s_animJson));
        File.Move(path + ".saving", path, overwrite: true);
    }

    // Whether the server uses its shorter, measured times now ([Battle] FastAnimationTiming, on unless set off).
    private bool AnimFastTiming => !string.Equals(_manager.GetSetting("Battle", "FastAnimationTiming")?.Trim(), "false", StringComparison.OrdinalIgnoreCase);

    // The seconds of extra wait on every round ([Battle] AnimationExtraSeconds, 1 unless set).
    private double AnimExtraSeconds => double.TryParse(_manager.GetSetting("Battle", "AnimationExtraSeconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
        ? Math.Clamp(s, 0, 10)
        : 1;

    /// <summary>The seconds a round waits for an animation now: the time set here, or the server's own.</summary>
    private float AnimSeconds(AnimInfo? animation, string name, out bool set) {
        set = name.Length > 0 && _animTimes.Spells.ContainsKey(name);

        return set ? _animTimes.Spells[name] : AnimServerSeconds(animation);
    }

    // The seconds the server works out for an animation, without a time set for it here (the Casting wait set here counts:
    // it is the usual casting animation the server adds when an animation has none of its own).
    private float AnimServerSeconds(AnimInfo? animation) {
        var casting = _animTimes.General.TryGetValue("Casting", out var c) ? c : 5f;
        if (animation is null) {
            return casting;
        }

        var fast = AnimFastTiming;
        var normal = fast ? animation.Fast : animation.Old;
        var usual = (fast ? animation.Parts : animation.OldParts).Where(p => p.What.StartsWith("Casting animation", StringComparison.Ordinal)).Sum(p => p.Seconds);

        return usual > 0 ? normal - usual + casting : normal;
    }

    private void RenderAnimations() {
        var report = _animReport;
        var setCount = _animTimes.Spells.Count + _animTimes.General.Count;
        _animSummary.Text = (setCount == 0 ? "Every wait is the server's own." : $"You set {setCount} time{(setCount == 1 ? "" : "s")}.")
                            + $" Timing: {(AnimFastTiming ? "measured (Fast animation timing on)" : "the old, longer times (Fast animation timing off)")}."
                            + (AnimExtraSeconds > 0 ? $" Every round also waits {AnimExtraSeconds:0.#} s more (Settings, Battle, AnimationExtraSeconds)." : "");
        _animSummary.Foreground = setCount == 0 ? MUTED : WARN;

        if (report is null || _animRows.Count == 0) {
            _animList.ItemsSource = null;
            _animCount.Text = File.Exists(SpellAnimationsPath)
                ? "The spell animation list is empty."
                : "No spell animation list yet. Start the server once: it writes the list about two minutes after it starts (with the ID lookup on), " +
                  "and it shows here then.";

            return;
        }

        var words = SearchText.Words(_animSearch.Text);
        var school = FilterPick(_animSchool);
        var kind = FilterPick(_animKind);
        var show = _animShowCombo.SelectedIndex;
        var hideTests = _animHideTests.IsChecked == true;
        var rows = _animRows.Where(row => {
            var s = row.Spell;
            if (!words.All(w => row.Haystack.Contains(w, StringComparison.Ordinal))
                || (school is not null && !string.Equals(s.School, school, StringComparison.OrdinalIgnoreCase))
                || (kind == "Spells" && s.Treasure) || (kind == "Treasure cards" && !s.Treasure)
                || !InFilterRange(s.Pips, _animPipsMin, _animPipsMax)
                || (hideTests && (s.Shown.Length == 0 || s.Name.Contains("test", StringComparison.OrdinalIgnoreCase)))) {
                return false;
            }

            var seconds = AnimSeconds(row.Animation, s.Animation, out var set);

            return InFilterRange((long) Math.Floor(seconds), _animMin, _animMax) && show switch {
                1 => set,
                2 => !set,
                3 => row.Animation is not { Found: true },
                _ => true,
            };
        }).ToList();

        var byName = StringComparer.OrdinalIgnoreCase;
        rows = _animSort.SelectedIndex switch {
            1 => [.. rows.OrderByDescending(r => AnimSeconds(r.Animation, r.Spell.Animation, out _)).ThenBy(r => r.Spell.Title, byName)],
            2 => [.. rows.OrderBy(r => AnimSeconds(r.Animation, r.Spell.Animation, out _)).ThenBy(r => r.Spell.Title, byName)],
            3 => [.. rows.OrderBy(r => r.Spell.Animation, byName).ThenBy(r => r.Spell.Title, byName)],
            4 => [.. rows.OrderByDescending(r => r.Sharing).ThenBy(r => r.Spell.Animation, byName).ThenBy(r => r.Spell.Title, byName)],
            5 => [.. rows.OrderBy(r => r.Spell.School, byName).ThenBy(r => r.Spell.Title, byName)],
            _ => rows,
        };

        _animList.ItemsSource = rows;
        _animCount.Text = $"{FilterCount(rows.Count, _animRows.Count, "spells")} {_animByName.Count:N0} animations in the game files. List made {report.Written}.";
    }

    private Control AnimRowView(AnimRow row) {
        var s = row.Spell;
        var seconds = AnimSeconds(row.Animation, s.Animation, out var set);
        var tags = new[] {
            s.School,
            s.XPips ? "X pips" : s.Pips > 0 ? $"{s.Pips} pip{(s.Pips == 1 ? "" : "s")}" : "",
            s.Treasure ? "treasure card" : "",
            s.Animation.Length > 0 ? $"animation {s.Animation}" : "no animation",
            row.Sharing > 1 ? $"shared by {row.Sharing:N0} spells" : "",
            // Many spells show the same name (a treasure card, a creature's copy); the name in the files tells them apart.
            s.Shown.Length > 0 && !s.Shown.Equals(s.Name, StringComparison.OrdinalIgnoreCase) ? s.Name : "",
        }.Where(t => t.Length > 0);

        return new Border {
            Padding = new Thickness(4, 6),
            BorderBrush = LINE,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new DockPanel {
                Children = {
                    WithDock(WithCenter(new StackPanel {
                        Spacing = 0, MinWidth = 110,
                        Children = {
                            new TextBlock {
                                Text = $"{seconds:0.0#} s", FontSize = 17, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Right,
                                Foreground = set ? WARN : row.Animation is { Found: true } ? Brushes.White : MUTED,
                            },
                            new TextBlock {
                                Text = set ? "you set it" : row.Animation is { Found: true } ? "from the game files" : "usual cast only",
                                FontSize = ManagerTheme.FONT_SMALL, Foreground = MUTED, HorizontalAlignment = HorizontalAlignment.Right,
                            },
                        },
                    }), Dock.Right),
                    new StackPanel {
                        Spacing = 2,
                        Children = {
                            new TextBlock { Text = s.Title, FontWeight = FontWeight.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap },
                            new TextBlock { Text = string.Join("  ·  ", tags), Foreground = MUTED, TextWrapping = TextWrapping.Wrap },
                        },
                    },
                },
            },
        };
    }

    private void ShowAnimation(AnimSpell? spell) {
        var shownRow = spell is null ? null : (_animList.ItemsSource as List<AnimRow>)?.FirstOrDefault(r => r.Spell == spell);
        _animPopup?.ShowFor(_animList, shownRow);
        _animPicked = spell;
        _animDetails.Children.Clear();
        if (spell is null) {
            return;
        }

        var details = _animDetails;
        var animation = spell.Animation.Length > 0 ? _animByName.GetValueOrDefault(spell.Animation) : null;
        var fast = AnimFastTiming;
        var seconds = AnimSeconds(animation, spell.Animation, out var set);

        void Head(string text) => details.Children.Add(new TextBlock {
            Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.Bold, Foreground = MUTED, Margin = new Thickness(0, 12, 0, 0),
        });

        void Line(string left, string right, IBrush? brush = null, bool bold = false) => details.Children.Add(new DockPanel {
            Children = {
                WithDock(new TextBlock {
                    Text = right, FontFamily = MONO, Foreground = brush ?? Brushes.White, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
                    Margin = new Thickness(16, 0, 0, 0),
                }, Dock.Right),
                new SelectableTextBlock { Text = left, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal },
            },
        });

        details.Children.Add(new SelectableTextBlock { Text = spell.Title, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        details.Children.Add(Sub(string.Join(". ", new[] {
            spell.School,
            spell.XPips ? "X pips" : spell.Pips > 0 ? $"{spell.Pips} pip{(spell.Pips == 1 ? "" : "s")}" : "",
            spell.Treasure ? "Treasure card" : "",
            $"Spell ID {spell.Id}",
            spell.Name,
        }.Where(t => t.Length > 0)) + "."));

        details.Children.Add(new Border {
            Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(14, 10), CornerRadius = new CornerRadius(10),
            Background = ManagerTheme.Brush(ManagerTheme.ACCENT, 40),
            Child = new StackPanel {
                Spacing = 2,
                Children = {
                    new TextBlock { Text = $"A round waits {seconds:0.0#} s for this spell's animation", FontSize = 16, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap },
                    Sub(set
                        ? $"You set this time. The server's own is {AnimServerSeconds(animation):0.0#} s."
                        : animation is { Found: true }
                            ? $"Worked out from the game files, with {(fast ? "the measured times (Fast animation timing on)" : "the old, longer times (Fast animation timing off)")}."
                            : "The game files have no animation of this name, so it waits the usual casting animation."),
                },
            },
        });

        // Set a time of its own.
        Head(spell.Animation.Length > 0 ? $"Set the time for the animation {spell.Animation}" : "Set a time");
        if (spell.Animation.Length == 0) {
            details.Children.Add(Sub("This spell has no animation name, so the round waits the usual casting animation. Change that under " +
                                     "\"Waits that are not one spell's animation\", Casting."));
        }
        else {
            var box = new NumericUpDown {
                Minimum = 0, Maximum = (decimal) ANIMATION_MAX_SECONDS, Increment = 0.5m, FormatString = "0.0#", Width = 150,
                Value = (decimal) Math.Round(seconds, 2),
            };
            var save = Action(Primary("Use this time"), () => {
                SetAnimationTime(spell.Animation, box.Value is { } v ? (float) v : null, spell.Title);

                return Task.CompletedTask;
            });
            var reset = Action(Plain("Use the game's time"), () => {
                SetAnimationTime(spell.Animation, null, spell.Title);

                return Task.CompletedTask;
            });
            reset.IsEnabled = set;
            details.Children.Add(new WrapPanel { Children = { Spaced(WithCenter(box)), Spaced(WithCenter(new TextBlock { Text = "seconds" })), Spaced(save), Spaced(reset) } });
            var sharing = _animRows.Count(r => string.Equals(r.Spell.Animation, spell.Animation, StringComparison.OrdinalIgnoreCase));
            details.Children.Add(Sub((sharing > 1 ? $"Counts for all {sharing:N0} spells that play this animation (listed below). " : "") +
                                     "Lower it if the next turn starts late, raise it if spells get cut off. It counts from the next round, with no restart. " +
                                     "On the host's own PC the server also watches the game play the round, so a round can end a little sooner than this."));
        }

        // How the seconds add up.
        if (animation is not null) {
            var parts = fast ? animation.Parts : animation.OldParts;
            var casting = _animTimes.General.TryGetValue("Casting", out var c) ? c : (float?) null;
            Head(set ? "How the server's own time adds up" : "How the time adds up");
            foreach (var part in parts) {
                var usual = casting is not null && part.What.StartsWith("Casting animation", StringComparison.Ordinal);
                Line(part.What + (usual ? " (you set Casting)" : ""), $"{(usual ? casting!.Value : part.Seconds):0.00} s", usual ? WARN : null);
            }

            Line("Total", $"{AnimServerSeconds(animation):0.00} s", GOOD, bold: true);
            Head(fast ? "With Fast animation timing off it would be" : "With Fast animation timing on it would be");
            Line(fast ? "The old, longer estimate" : "The measured estimate", $"{(fast ? animation.Old : animation.Fast):0.00} s", MUTED);

            Head("The animation's stages in the game files");
            if (animation.Stages.Count == 0) {
                details.Children.Add(Sub("None: the game files have no animation of this name."));
            }

            foreach (var stage in animation.Stages) {
                Line($"{(stage.Name.Length > 0 ? stage.Name : "(no name)")}  ·  {stage.Kind}", $"{stage.Seconds:0.00} s", MUTED);
            }
        }

        // What else the round waits for.
        Head("What else the round waits for around it");
        foreach (var normal in _animReport?.General ?? []) {
            var mine = _animTimes.General.TryGetValue(normal.Name, out var v);
            Line($"{AnimGeneralTitle(normal.Name)}: {normal.What}", $"{(mine ? v : fast ? normal.Fast : normal.Old):0.0#} s", mine ? WARN : MUTED);
        }

        if (AnimExtraSeconds > 0) {
            Line("Every round, once: AnimationExtraSeconds (Settings, Battle)", $"{AnimExtraSeconds:0.0#} s", MUTED);
        }

        // The spells that share it.
        if (spell.Animation.Length > 0) {
            var others = _animRows.Where(r => string.Equals(r.Spell.Animation, spell.Animation, StringComparison.OrdinalIgnoreCase) && r.Spell != spell)
                .Select(r => r.Spell.Title).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
            if (others.Count > 0) {
                Head($"Other spells with this animation ({others.Count:N0})");
                details.Children.Add(new SelectableTextBlock {
                    Text = string.Join(", ", others.Take(200)) + (others.Count > 200 ? $" and {others.Count - 200:N0} more" : ""),
                    TextWrapping = TextWrapping.Wrap, FontSize = 13,
                });
            }
        }
    }

    private static string AnimGeneralTitle(string name) => name switch {
        "Casting" => "Casting",
        "Pass" => "Pass",
        "Defeat" => "Defeat",
        "Announcement" => "Boss line",
        "Charm" => "Charm or ward used",
        "OverTime" => "Over time",
        _ => name,
    };

    private void RenderAnimationGeneral() {
        _animGeneral.Children.Clear();
        var general = _animReport?.General ?? [];
        if (general.Count == 0) {
            _animGeneral.Children.Add(Sub("These show once the server has written the spell animation list."));

            return;
        }

        var fast = AnimFastTiming;
        foreach (var normal in general) {
            var mine = _animTimes.General.TryGetValue(normal.Name, out var v);
            var serverTime = fast ? normal.Fast : normal.Old;
            var box = new NumericUpDown {
                Minimum = 0, Maximum = (decimal) ANIMATION_MAX_SECONDS, Increment = 0.25m, FormatString = "0.0#", Width = 140,
                Value = mine ? (decimal) v : null, Watermark = $"{serverTime:0.0#}",
            };
            var name = normal.Name;
            box.ValueChanged += (_, _) => SetGeneralTime(name, box.Value is { } value ? (float) value : null);
            ToolTip.SetTip(box, $"Empty: the server's own {serverTime:0.0#} s.");
            _animGeneral.Children.Add(new DockPanel {
                Margin = new Thickness(0, 2),
                Children = {
                    WithDock(WithCenter(box), Dock.Right),
                    WithDock(WithCenter(new TextBlock {
                        Text = mine ? $"server's own {serverTime:0.0#} s" : $"{serverTime:0.0#} s", Foreground = MUTED, Margin = new Thickness(12, 0),
                    }), Dock.Right),
                    new StackPanel {
                        Children = {
                            new TextBlock { Text = AnimGeneralTitle(name), FontWeight = FontWeight.SemiBold, Foreground = mine ? WARN : Brushes.White },
                            new TextBlock { Text = normal.What, Foreground = MUTED, FontSize = ManagerTheme.FONT_SMALL, TextWrapping = TextWrapping.Wrap },
                        },
                    },
                },
            });
        }
    }

    private void SetAnimationTime(string animation, float? seconds, string spellTitle) {
        try {
            // Read again first, so a change made in the file (or on another PC's Remote) is kept.
            var times = ReadAnimationTimes();
            if (seconds is { } s) {
                times.Spells[animation] = (float) Math.Round(Math.Clamp(s, 0, ANIMATION_MAX_SECONDS), 2);
                times.Names[animation] = spellTitle;
            }
            else {
                times.Spells.Remove(animation);
                times.Names.Remove(animation);
            }

            WriteAnimationTimes(times);
            _animTimes = times;
            Say(seconds is { } set
                ? $"Spells with the animation {animation} now wait {set:0.0#} s. It counts from the next round if the server is running."
                : $"Spells with the animation {animation} wait the game's own time again.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            Say($"Could not save the time: {ex.Message}", isError: true);
        }

        var picked = _animPicked;
        RenderAnimations();
        if (picked is not null) {
            _animList.SelectedItem = (_animList.ItemsSource as List<AnimRow>)?.FirstOrDefault(r => r.Spell == picked);
            ShowAnimation(picked);
        }
    }

    private void SetGeneralTime(string name, float? seconds) {
        try {
            var times = ReadAnimationTimes();
            var had = times.General.TryGetValue(name, out var before);
            if (seconds is { } s) {
                var rounded = (float) Math.Round(Math.Clamp(s, 0, ANIMATION_MAX_SECONDS), 2);
                if (had && Math.Abs(before - rounded) < 0.001f) {
                    return;
                }

                times.General[name] = rounded;
            }
            else if (!times.General.Remove(name)) {
                return;
            }

            WriteAnimationTimes(times);
            _animTimes = times;
            Say(seconds is { } set ? $"{AnimGeneralTitle(name)} now waits {set:0.0#} s. It counts from the next round."
                : $"{AnimGeneralTitle(name)} waits the server's own time again.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            Say($"Could not save the time: {ex.Message}", isError: true);
        }

        RenderAnimations();
    }

    private async Task ResetAllAnimationTimesAsync() {
        var times = ReadAnimationTimes();
        var count = times.Spells.Count + times.General.Count;
        if (count == 0) {
            Say("Every wait is the server's own already.");

            return;
        }

        if (!await ConfirmAsync($"Take away all {count} times you set, so the server works them all out again?")) {
            return;
        }

        try {
            WriteAnimationTimes(new AnimationTimesFile());
            _animTimes = ReadAnimationTimes();
            Say("Every wait is the server's own again.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Say($"Could not save: {ex.Message}", isError: true);
        }

        RenderAnimationGeneral();
        RenderAnimations();
        ShowAnimation(null);
    }

    private Task OpenAnimationTimesFileAsync() {
        try {
            if (!File.Exists(AnimationTimesPath)) {
                WriteAnimationTimes(new AnimationTimesFile());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Say($"Could not make {ANIMATION_TIMES_FILE}: {ex.Message}", isError: true);

            return Task.CompletedTask;
        }

        return Open(AnimationTimesPath);
    }

}
