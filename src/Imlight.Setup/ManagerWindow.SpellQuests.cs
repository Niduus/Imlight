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
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// The Spell Quests tab on the Quests page (Niduus, 2026-10-28: "a lot of side quests are missing dialog outright. It's
/// so bad that I don't know if I got sent down a spell rabbit hole or not. Can you make this viewable in Ahziael for spell
/// quests"): every chain of side quests that ends in a spell, quest by quest in the order they are played, with who
/// gives each one, its steps, every line its NPCs say, the spell, and which quest and step the picked wizard is on.
/// The server writes the lines and spells (quest-dialog.json in the ID lookup folder) next to quest-details.json, and
/// each wizard's held steps in quest-progress/&lt;id&gt;.txt.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string QUEST_DIALOG_FILE = "quest-dialog.json";

    private sealed record SqLine(int Step, string When, string Speaker, string Text);

    private sealed record SqSpell(uint Id, string Name, string School);

    private sealed record SqQuestLines(string Code, List<SqSpell> Spells, List<SqLine> Lines);

    private sealed record SqFile(List<SqQuestLines> Quests);

    /// <summary>One chain: its quests in play order (one list per school when each school has its own copy), and what they teach.</summary>
    private sealed class SqChain {
        public required List<List<QeQuest>> Variants { get; init; }
        public List<QeQuest> Quests => Variants[0];
        public QeQuest? OpenedBy { get; init; }
        public string World { get; init; } = "";
        public string Haystack { get; set; } = "";
    }

    private enum SqState { NotStarted, CanStart, OnIt, Done }

    private static readonly string[] s_sqShows = ["Every spell chain", "In my quest book now", "Started, not finished", "Not started", "Finished"];

    private readonly TextBox _sqSearch = new() { Watermark = "Search a quest you were given, a spell, an NPC, or words someone says" };
    private readonly ComboBox _sqWizard = new() { MinWidth = 200, PlaceholderText = "Pick a wizard" };
    private readonly ComboBox _sqShow = FilterCombo(s_sqShows, 190);
    private readonly ComboBox _sqWorld = FilterCombo([ANY_FILTER], 150);
    private readonly CheckBox _sqSingles = new() { Content = "Include single quests (no chain)", IsChecked = true };
    private readonly TextBlock _sqCount = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _sqList = new() { Background = Brushes.Transparent };
    private readonly StackPanel _sqDetails = new() { Spacing = 6 };
    private readonly Avalonia.Threading.DispatcherTimer _sqTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    private Dictionary<string, SqQuestLines> _sqLines = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _sqLinesWritten = DateTime.MinValue;
    private DateTime _sqBuiltFrom = DateTime.MinValue;
    private List<SqChain> _sqChains = [];
    private HashSet<string> _sqDone = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _sqNow = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, HashSet<string>> _sqSteps = new(StringComparer.OrdinalIgnoreCase);
    private string _sqWizardName = "";
    private DateTime _sqProgressWritten;
    private bool _sqWizardsFilling;
    private SqChain? _sqPicked;

    private string QuestDialogPath => Path.Combine(_manager.IdLookupFolder, QUEST_DIALOG_FILE);

    private Control SpellQuestsTab() {
        _sqList.ItemTemplate = new FuncDataTemplate<SqChain>((chain, _) => chain is null ? new Panel() : SpellChainRowView(chain));
        _sqList.SelectionChanged += (_, e) => {
            if (e.Source == _sqList && _sqList.SelectedItem is SqChain chain && chain != _sqPicked) {
                ShowSpellChain(chain);
            }
        };
        _sqWizard.SelectionChanged += (_, _) => {
            if (!_sqWizardsFilling) {
                _sqProgressWritten = default;
                LoadSpellQuestProgress();
                RenderSpellQuests();
            }
        };
        WhenFiltersChange(RenderSpellQuests, _sqSearch, _sqShow, _sqWorld, _sqSingles);
        // While the tab shows, a step done in the game shows here within a few seconds.
        _sqTimer.Tick += (_, _) => LoadSpellQuests();

        var details = new ScrollViewer { Width = 520, Margin = new Thickness(14, 0, 0, 0), Content = Card(_sqDetails) };
        ShowSpellChain(null);

        return new DockPanel {
            Margin = new Thickness(24, 12, 24, 16),
            Children = {
                WithDock(details, Dock.Right),
                WithDock(Fold("star", "Quests", "Spell quests",
                    "Every quest chain that ends in a spell, in the order you play it, with what each NPC says. Pick a chain to read all of it.",
                    WithHint(new WrapPanel {
                        Children = {
                            Spaced(_sqWizard),
                            Spaced(Action(Plain("Refresh"), () => {
                                _sqLinesWritten = default;
                                _sqProgressWritten = default;
                                LoadSpellQuests();

                                return Task.CompletedTask;
                            })),
                        },
                    }, "Pick your wizard to see which quest of each chain they are on, and which step of it. " +
                       "In my quest book now: only chains with a quest your wizard has right now, so you can tell at once " +
                       "whether a quest you were handed leads to a spell. Type the name of any quest to find the chain it is part of. " +
                       "A chain is the side quests that lead into each other, from the first one to the one that teaches the spell. " +
                       "The lines are what this server's quests say, step by step; a step with no lines says so."),
                    _sqSearch,
                    SearchFilters(
                        SearchFilter("Show", _sqShow),
                        SearchFilter("World", _sqWorld),
                        SearchTick(_sqSingles)),
                    _sqCount), Dock.Top),
                WithMargin(_sqList, new Thickness(0, 14, 0, 0)),
            },
        };
    }

    /// <summary>Reads the server's quests, lines and the picked wizard's progress again when any was written since. Runs when the tab shows.</summary>
    private void LoadSpellQuests() {
        LoadQuestDetails();
        LoadQuestGuide();
        var changed = LoadSpellQuestLines();
        if (changed || _sqBuiltFrom != _qeDataWritten) {
            _sqBuiltFrom = _qeDataWritten;
            BuildSpellChains();
            changed = true;
        }

        changed |= FillSpellQuestWizards();
        changed |= LoadSpellQuestProgress();
        if (changed || _sqList.ItemsSource is null) {
            RenderSpellQuests();
            if (_sqPicked is not null) {
                ShowSpellChain(_sqChains.FirstOrDefault(c => c.Quests[^1].Code == _sqPicked.Quests[^1].Code));
            }
        }
    }

    private bool LoadSpellQuestLines() {
        var path = QuestDialogPath;
        try {
            var written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MaxValue;
            if (written == _sqLinesWritten) {
                return false;
            }

            var file = written == DateTime.MaxValue ? null : JsonSerializer.Deserialize<SqFile>(File.ReadAllText(path), s_qeJson);
            _sqLines = (file?.Quests ?? []).Where(q => q.Code is { Length: > 0 }).GroupBy(q => q.Code, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            _sqLinesWritten = written;

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            // Being written right now: read on the next look.
            return false;
        }
    }

    private bool FillSpellQuestWizards() {
        var wizards = ReadQuestWizards();
        var current = _sqWizard.ItemsSource as List<QuestWizard> ?? [];
        if (current.Select(w => (w.File, w.Name, w.OnThisPc)).SequenceEqual(wizards.Select(w => (w.File, w.Name, w.OnThisPc)))) {
            return false;
        }

        var picked = (_sqWizard.SelectedItem as QuestWizard)?.File ?? (_questWizard.SelectedItem as QuestWizard)?.File;
        _sqWizardsFilling = true;
        try {
            _sqWizard.ItemsSource = wizards;
            _sqWizard.SelectedItem = wizards.FirstOrDefault(w => w.File == picked) ?? wizards.FirstOrDefault();
        }
        finally {
            _sqWizardsFilling = false;
        }

        _sqProgressWritten = default;

        return true;
    }

    /// <summary>Reads the picked wizard's finished quests, held quests and the steps they are on. True when they changed.</summary>
    private bool LoadSpellQuestProgress() {
        if (_sqWizard.SelectedItem is not QuestWizard wizard) {
            var had = _sqDone.Count + _sqNow.Count > 0;
            (_sqDone, _sqNow, _sqSteps, _sqWizardName) = (new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase),
                new(StringComparer.OrdinalIgnoreCase), "");

            return had;
        }

        try {
            var written = File.GetLastWriteTimeUtc(wizard.File);
            if (written == _sqProgressWritten) {
                return false;
            }

            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var now = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var steps = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(wizard.File)) {
                var parts = line.Split('\t');
                if (parts.Length >= 2 && parts[0] == "done") {
                    done.Add(parts[1]);
                }
                else if (parts.Length >= 2 && parts[0] == "now") {
                    now.Add(parts[1]);
                }
                else if (parts.Length >= 3 && parts[0] == "step") {
                    if (!steps.TryGetValue(parts[1], out var set)) {
                        steps[parts[1]] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    }

                    set.Add(parts[2]);
                }
            }

            (_sqDone, _sqNow, _sqSteps, _sqWizardName, _sqProgressWritten) = (done, now, steps, wizard.Name, written);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    private List<SqSpell> SpellsOfQuest(string code) => _sqLines.TryGetValue(code, out var lines) ? lines.Spells : [];

    /// <summary>
    /// Finds every chain: from each quest that teaches a spell, back through the side quests it needs to the first one
    /// (the main story quest that opens it is noted, not part of the chain). A spell quest that is part of a longer
    /// chain shows in that chain. Chains that are the same quests for each school are one chain.
    /// </summary>
    private void BuildSpellChains() {
        var guide = _quests.GroupBy(q => q.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var paths = new Dictionary<string, List<QeQuest>>(StringComparer.OrdinalIgnoreCase);
        var openers = new Dictionary<string, QeQuest?>(StringComparer.OrdinalIgnoreCase);

        // The side quests from the first one up to this one, longest way back.
        List<QeQuest> PathTo(QeQuest quest, HashSet<string> visiting) {
            if (paths.TryGetValue(quest.Code, out var known)) {
                return known;
            }

            visiting.Add(quest.Code);
            List<QeQuest> best = [];
            QeQuest? opener = null;
            foreach (var need in quest.Needs) {
                if (!_qeByCode.TryGetValue(need, out var before) || visiting.Contains(before.Code)) {
                    continue;
                }

                if (before.Main) {
                    opener ??= before;
                    continue;
                }

                var path = PathTo(before, visiting);
                if (path.Count > best.Count) {
                    best = path;
                }
            }

            visiting.Remove(quest.Code);
            var result = new List<QeQuest>(best) { quest };
            paths[quest.Code] = result;
            openers[quest.Code] = best.Count > 0 ? openers.GetValueOrDefault(best[0].Code) : opener;

            return result;
        }

        var spellQuests = _qeData.Quests.Where(q => SpellsOfQuest(q.Code).Count > 0).ToList();
        foreach (var quest in spellQuests) {
            PathTo(quest, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        // A spell quest inside another spell quest's chain is shown in that one.
        var inside = spellQuests.SelectMany(q => paths[q.Code].Take(paths[q.Code].Count - 1)).Select(q => q.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ends = spellQuests.Where(q => !inside.Contains(q.Code)).ToList();

        var chains = new List<SqChain>();
        foreach (var group in ends.GroupBy(q => string.Join('|', paths[q.Code].Select(p => SearchText.Normalize(p.Title))))) {
            var variants = group.Select(q => paths[q.Code]).ToList();
            var first = variants[0];
            var world = QuestWorldOf(first[0].Code, guide.GetValueOrDefault(first[0].Code)?.Zone ?? "");
            var chain = new SqChain { Variants = variants, OpenedBy = openers.GetValueOrDefault(first[0].Code), World = world };
            chain.Haystack = SearchText.Normalize(string.Join(' ', variants.SelectMany(v => v).Distinct().Select(q => string.Join(' ', [
                q.Code, q.Title, q.GiverName, string.Join(' ', q.Goals.Select(g => $"{g.Text} {g.NpcName} {g.Place}")),
                string.Join(' ', SpellsOfQuest(q.Code).Select(s => $"{s.Name} {s.School}")),
                string.Join(' ', (_sqLines.GetValueOrDefault(q.Code)?.Lines ?? []).Select(l => $"{l.Speaker} {l.Text}")),
            ])).Append(world).Append(chain.OpenedBy?.Title ?? "")));
            chains.Add(chain);
        }

        // In story order: by world, then by where the chain's first quest comes in the quest list.
        _sqChains = [.. chains.OrderBy(c => WorldOrder(c.World)).ThenBy(c => guide.GetValueOrDefault(c.Quests[0].Code)?.Order ?? int.MaxValue)];
        FillFilterCombo(_sqWorld, _sqChains.Select(c => c.World).Distinct().OrderBy(WorldOrder));
    }

    private SqState QuestStateOf(QeQuest quest) {
        if (_sqNow.Contains(quest.Code)) {
            return SqState.OnIt;
        }

        if (_sqDone.Contains(quest.Code)) {
            return SqState.Done;
        }

        return quest.Needs.All(n => _sqDone.Contains(n)) ? SqState.CanStart : SqState.NotStarted;
    }

    // The school's copy of the chain the wizard has touched, else the first.
    private List<QeQuest> VariantOf(SqChain chain)
        => chain.Variants.FirstOrDefault(v => v.Any(q => _sqNow.Contains(q.Code) || _sqDone.Contains(q.Code))) ?? chain.Quests;

    private SqState ChainStateOf(SqChain chain, out int at) {
        var quests = VariantOf(chain);
        at = quests.FindIndex(q => _sqNow.Contains(q.Code));
        if (at >= 0) {
            return SqState.OnIt;
        }

        if (_sqDone.Contains(quests[^1].Code)) {
            at = quests.Count - 1;
            return SqState.Done;
        }

        var lastDone = quests.FindLastIndex(q => _sqDone.Contains(q.Code));
        at = lastDone + 1;
        if (lastDone >= 0) {
            return SqState.CanStart;
        }

        return QuestStateOf(quests[0]) == SqState.CanStart ? SqState.CanStart : SqState.NotStarted;
    }

    private void RenderSpellQuests() {
        if (_qeData.Quests.Count == 0 || _sqLinesWritten == DateTime.MaxValue) {
            _sqList.ItemsSource = null;
            _sqCount.Text = _qeData.Quests.Count == 0
                ? "No quests yet. Start the server once: it writes its quests a little after it starts (with the ID lookup on)."
                : "This server hasn't written its quest lines yet. Update, then start the server once: it writes them as it starts.";

            return;
        }

        var words = SearchText.Words(_sqSearch.Text);
        var world = FilterPick(_sqWorld);
        var show = _sqShow.SelectedIndex;
        var singles = _sqSingles.IsChecked == true;
        var shown = new List<SqChain>();
        foreach (var chain in _sqChains) {
            if (!singles && chain.Quests.Count == 1) {
                continue;
            }

            var state = ChainStateOf(chain, out _);
            var started = chain.Variants.SelectMany(v => v).Any(q => _sqNow.Contains(q.Code) || _sqDone.Contains(q.Code));
            if ((world is not null && chain.World != world)
                || !words.All(w => chain.Haystack.Contains(w, StringComparison.Ordinal))
                || show switch {
                    1 => state != SqState.OnIt,
                    2 => !started || state == SqState.Done,
                    3 => started,
                    4 => state != SqState.Done,
                    _ => false,
                }) {
                continue;
            }

            shown.Add(chain);
        }

        _sqList.ItemsSource = shown;
        var total = _sqChains.Count(c => singles || c.Quests.Count > 1);
        var head = FilterCount(shown.Count, total, "spell chains");
        var mine = _sqWizard.SelectedItem is QuestWizard
            ? $" In {(_sqWizardName.Length > 0 ? _sqWizardName + "'s" : "your")} quest book now: {_sqChains.Count(c => ChainStateOf(c, out _) == SqState.OnIt):N0}; " +
              $"{_sqChains.Count(c => ChainStateOf(c, out _) == SqState.Done):N0} finished."
            : " Pick a wizard to see where they are.";
        if (_sqWizard.SelectedItem is QuestWizard) {
            // The question this tab answers (Niduus, 2026-10-28): does a side quest I was handed lead to a spell?
            var chained = _sqChains.SelectMany(c => c.Variants.SelectMany(v => v)).Select(q => q.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var held = _sqNow.Where(c => _qeByCode.TryGetValue(c, out var q) && !q.Main).ToList();
            if (held.Count > 0) {
                var onChain = held.Count(chained.Contains);
                mine += onChain == 0
                    ? $" None of the {held.Count:N0} side quests in the quest book leads to a spell."
                    : $" {onChain:N0} of the {held.Count:N0} side quests in the quest book lead{(onChain == 1 ? "s" : "")} to a spell (Show: In my quest book now).";
            }
        }

        _sqCount.Text = head + mine;
    }

    private string SpellsText(SqChain chain) {
        var spells = chain.Variants.SelectMany(v => v).Distinct().SelectMany(q => SpellsOfQuest(q.Code))
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        var shown = spells.Take(4).Select(s => s.School.Length > 0 ? $"{s.Name} ({s.School})" : s.Name);

        return string.Join(", ", shown) + (spells.Count > 4 ? $" and {spells.Count - 4} more" : "");
    }

    private Control SpellChainRowView(SqChain chain) {
        var quests = VariantOf(chain);
        var state = ChainStateOf(chain, out var at);
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(new TextBlock {
            Text = quests.Count == 1 ? quests[0].Title : $"{quests[0].Title}  →  {quests[^1].Title}",
            FontWeight = FontWeight.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
        });
        title.Children.Add(state switch {
            SqState.OnIt => QtTag($"On quest {at + 1} of {quests.Count}", WARN),
            SqState.Done => QtTag("Finished", GOOD),
            SqState.CanStart when at > 0 => QtTag($"Quest {at + 1} of {quests.Count} is next", ManagerTheme.Accent("Quests")),
            SqState.CanStart => QtTag("Can start", ManagerTheme.Accent("Quests")),
            _ => QtTag("Not started", MUTED),
        });

        var bits = new List<string> {
            quests.Count == 1 ? "1 quest" : $"{quests.Count} quests",
            $"teaches {SpellsText(chain)}",
            chain.World,
        };
        if (chain.Variants.Count > 1) {
            bits.Add($"one copy per school ({chain.Variants.Count})");
        }

        if (chain.OpenedBy is { } opener) {
            bits.Add($"opened by {opener.Title}");
        }

        return new Border {
            Padding = new Thickness(4, 6, 4, 6), BorderBrush = LINE, BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel { Spacing = 2, Children = { title, Sub(string.Join("  ·  ", bits)) } },
        };
    }

    // "<ITALICS>The eyes of Mortis glow</ITALICS>, $PLAYER_FIRSTNAME$" as plain words with the wizard's name.
    private string CleanLine(string text) {
        var name = _sqWizardName.Length > 0 ? _sqWizardName : "Wizard";
        var first = name.Split(' ')[0];
        text = text.Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("$PLAYER_FIRSTNAME$", first, StringComparison.OrdinalIgnoreCase)
            .Replace("$PLAYER_LASTNAME$", name.Contains(' ') ? name[(name.IndexOf(' ') + 1)..] : "", StringComparison.OrdinalIgnoreCase)
            .Replace("$PLAYER_NAME$", name, StringComparison.OrdinalIgnoreCase);
        text = Regex.Replace(text, @"#\d+:\$GOAL_[A-Z]+\$", "them");
        text = Regex.Replace(text, @"<\s*/?\s*(italics?|i|bold|b|br)\s*>", "", RegexOptions.IgnoreCase);

        return text.Trim();
    }

    private static string WhenShown(int step, string when) => (step, when) switch {
        (0, "Prep") => "When it is offered",
        (0, "Underway") => "If you talk to the giver again before it is done",
        (0, "Complete") => "When the quest is done",
        (0, _) => when,
        (_, "Prep") => "When this step starts",
        (_, "Underway") => "While you are on this step",
        (_, "Completion" or "Hyperlink") => "When you do this step",
        (_, "QuestInfo") => "Quest book",
        _ => when,
    };

    private void ShowSpellChain(SqChain? chain) {
        _sqPicked = chain;
        _sqDetails.Children.Clear();
        if (chain is null) {
            _sqDetails.Children.Add(Sub("Pick a chain on the left to see each of its quests in order: who gives it, its steps, everything " +
                                        "its NPCs say, the spell it teaches, and where your wizard is."));

            return;
        }

        var quests = VariantOf(chain);
        var state = ChainStateOf(chain, out var at);
        var guide = _quests.GroupBy(q => q.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        void Heading(string text, IBrush? brush = null) => _sqDetails.Children.Add(new TextBlock {
            Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = brush ?? ManagerTheme.Accent("Quests"),
            Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap,
        });

        void Small(string text) => _sqDetails.Children.Add(new TextBlock {
            Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.Bold, Foreground = MUTED, Margin = new Thickness(0, 8, 0, 0),
        });

        void Lines(IEnumerable<string> lines) => _sqDetails.Children.Add(new SelectableTextBlock {
            Text = string.Join('\n', lines), TextWrapping = TextWrapping.Wrap, FontSize = 13,
        });

        _sqDetails.Children.Add(new SelectableTextBlock {
            Text = quests.Count == 1 ? quests[0].Title : $"{quests[0].Title} → {quests[^1].Title}",
            FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
        });
        _sqDetails.Children.Add(Sub($"{chain.World}. {(quests.Count == 1 ? "A single quest" : $"A chain of {quests.Count} quests")} that teaches " +
                                    $"{SpellsText(chain)}." + (chain.OpenedBy is { } by ? $" It opens after the main story quest {by.Title}." : "")));
        if (chain.Variants.Count > 1) {
            _sqDetails.Children.Add(Sub($"Each school has its own copy of these quests ({chain.Variants.Count}); this shows " +
                                        (ReferenceEquals(quests, chain.Quests) ? "the first one" : "the one your wizard is doing") + "."));
        }

        _sqDetails.Children.Add(new TextBlock {
            Text = _sqWizard.SelectedItem is not QuestWizard ? "Pick a wizard above to see where they are in this chain."
                : state switch {
                    SqState.OnIt => $"{_sqWizardName} is on quest {at + 1} of {quests.Count}: {quests[at].Title}.",
                    SqState.Done => $"{_sqWizardName} has finished this chain.",
                    SqState.CanStart when at > 0 => $"{_sqWizardName} finished {at} of {quests.Count}. Next: {quests[at].Title}.",
                    SqState.CanStart => $"{_sqWizardName} can start this chain now.",
                    _ => $"{_sqWizardName} hasn't started it, and can't yet: an earlier quest is needed first.",
                },
            Foreground = state == SqState.Done ? GOOD : state == SqState.OnIt ? WARN : MUTED, TextWrapping = TextWrapping.Wrap,
        });

        for (var i = 0; i < quests.Count; i++) {
            var quest = quests[i];
            var questState = QuestStateOf(quest);
            var lines = _sqLines.GetValueOrDefault(quest.Code)?.Lines ?? [];
            var spells = SpellsOfQuest(quest.Code);
            var mark = questState switch {
                SqState.OnIt => "  (in your quest book)",
                SqState.Done => "  (done)",
                _ => "",
            };
            Heading($"{i + 1}. {quest.Title}{mark}", questState == SqState.Done ? GOOD : questState == SqState.OnIt ? WARN : null);

            var about = new List<string>();
            var zone = guide.GetValueOrDefault(quest.Code)?.Zone ?? "";
            about.Add(quest.StartsByItself ? "Starts by itself when the quest before it is done."
                : quest.GiverName.Length > 0 ? $"Given by {quest.GiverName}{(zone.Length > 0 ? $", in {zone}" : "")}." : "Who gives it is not known.");
            if (quest.Level > 0) {
                about.Add($"Level {quest.Level}. Code {quest.Code}.");
            }
            else {
                about.Add($"Code {quest.Code}.");
            }

            if (spells.Count > 0) {
                about.Add("Teaches: " + string.Join(", ", spells.Select(s => s.School.Length > 0 ? $"{s.Name} (only {s.School} wizards)" : s.Name)) + ".");
            }

            Lines(about);

            var buttons = new WrapPanel();
            var code = quest.Code;
            var start = Plain("Start");
            ToolTip.SetTip(start, "Gives this quest now (.quest forcegrant), even if the quests before it are not done. Start the server and log in first.");
            buttons.Children.Add(Spaced(Action(start, () => RunSpellQuestCommand($".quest forcegrant {code}", $"Started {quest.Title}."))));
            if (zone.Length > 0) {
                var teleport = Plain("Teleport");
                ToolTip.SetTip(teleport, $"Takes you to {zone}, where it is given (.tp).");
                buttons.Children.Add(Spaced(Action(teleport, () => RunSpellQuestCommand($".tp {zone}", $"Teleporting to {zone}."))));
            }

            buttons.Children.Add(Spaced(Action(Plain("Copy code"), () => CopyAsync(code))));
            _sqDetails.Children.Add(buttons);

            // The offer and what the giver says while you have it.
            var questLines = lines.Where(l => l.Step == 0).ToList();
            foreach (var group in questLines.GroupBy(l => l.When)) {
                Small(WhenShown(0, group.Key));
                Lines(group.Select(l => (l.Speaker.Length > 0 ? $"{l.Speaker}: " : "") + CleanLine(l.Text)));
            }

            if (questLines.All(l => l.When != "Prep")) {
                Small("When it is offered");
                Lines(["No lines: the quest's files have nothing for its offer."]);
            }

            var held = _sqSteps.GetValueOrDefault(quest.Code);
            for (var s = 0; s < quest.Goals.Count; s++) {
                var goal = quest.Goals[s];
                var here = questState == SqState.OnIt && held?.Contains(goal.Name) == true;
                var stepTitle = new TextBlock {
                    Text = $"Step {s + 1}: {goal.Text}{(goal.Place.Length > 0 && !goal.Text.Contains(goal.Place, StringComparison.OrdinalIgnoreCase) ? $" ({goal.Place})" : "")}" +
                           (here ? "   ◀ you are here" : ""),
                    FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
                };
                if (here) {
                    stepTitle.Foreground = WARN;
                }

                _sqDetails.Children.Add(stepTitle);
                var stepLines = lines.Where(l => l.Step == s + 1).ToList();
                if (stepLines.Count == 0) {
                    _sqDetails.Children.Add(Sub(goal.Kind == "Talk" ? "No lines: nobody says anything on this step." : "No lines on this step."));
                    continue;
                }

                foreach (var group in stepLines.GroupBy(l => WhenShown(s + 1, l.When))) {
                    Small(group.Key);
                    Lines(group.Select(l => (l.Speaker.Length > 0 ? $"{l.Speaker}: " : "") + CleanLine(l.Text)));
                }
            }
        }
    }

    // Only the wizard picked on this tab gets it, like the Quests tab's buttons.
    private Task RunSpellQuestCommand(string command, string said) {
        var wizard = _sqWizard.SelectedItem as QuestWizard;
        _manager.SendCommand(command, wizard is null ? null : Path.GetFileNameWithoutExtension(wizard.File));
        Say(said + (wizard is { Name.Length: > 0 } ? $" Only {wizard.Name} gets it." : "") + " The game's chat says if it worked.");

        return Task.CompletedTask;
    }

}
