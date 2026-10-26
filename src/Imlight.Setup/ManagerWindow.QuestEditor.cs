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
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The Quest Editor tab (Niduus, 2026-10-25: "add custom options to quests (In detail)"): for any game quest, its
/// rewards (experience, gold, training points, items), each step's count and NPC, who gives it and who it is handed in
/// to, the level it needs, the quests it needs first, turning it off, skipping it or making it repeatable; and quests of
/// your own, each a copy of a game quest with its own changes. Saved as spiraldb-extra/QuestOptions.json, which the
/// server reads again within two seconds (Game/Quests/QuestOptions.cs), so no restart. The game's files never change.
/// The server writes what the game's quests are (quest-details.json in the ID lookup folder) for this tab to show.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string QUEST_DETAILS_FILE = "quest-details.json";
    private const string QUEST_OPTIONS_FILE = "QuestOptions.json";
    private const int QUEST_EDITOR_SHOWN = 400;

    // What the server wrote: each game quest as the game has it.
    private sealed record QeGoal(string Name, string Kind, string Text, int Count, ulong Npc, string NpcName, string Place);

    private sealed record QeRewards(int Xp, int GoldMin, int GoldMax, int TrainingPoints, List<string> Items);

    private sealed record QeQuest(string Code, string Title, int Level, bool Main, ulong Giver, string GiverName,
        bool StartsByItself, List<string> Needs, List<QeGoal> Goals, QeRewards Rewards);

    private sealed record QeNpc(ulong Id, string Name, string Code, string Zone);

    private sealed record QeFile(List<QeQuest> Quests, List<QeNpc> Npcs);

    // One quest's changes, as the server reads them (the same names as QuestOptions.QuestChange).
    private sealed class QeChange {
        public string Quest { get; set; } = "";
        public string CopyOf { get; set; } = "";
        public bool Off { get; set; }
        public bool Skip { get; set; }
        public bool Repeatable { get; set; }
        public int? Level { get; set; }
        public List<string> Needs { get; set; } = [];
        public bool NeedsOnly { get; set; }
        public ulong Giver { get; set; }
        public bool StartsByItself { get; set; }
        public bool? Mainline { get; set; }
        public bool KeepGameRewards { get; set; } = true;
        public int Xp { get; set; }
        public int Gold { get; set; }
        public int TrainingPoints { get; set; }
        public List<QeItem> Items { get; set; } = [];
        public List<QeGoalChange> Goals { get; set; } = [];
        // The Manager's own note of what it is, not read by the server.
        public string Note { get; set; } = "";

        internal QeChange Copy() => JsonSerializer.Deserialize<QeChange>(JsonSerializer.Serialize(this, s_qeJson), s_qeJson)!;

        internal bool ChangesNothing => CopyOf.Length == 0 && !Off && !Skip && !Repeatable && Level is null && Needs.Count == 0
            && !NeedsOnly && Giver == 0 && !StartsByItself && Mainline is null && KeepGameRewards && Xp == 0 && Gold == 0
            && TrainingPoints == 0 && Items.Count == 0 && Goals.All(g => g.ChangesNothing);
    }

    private sealed class QeItem {
        public ulong Id { get; set; }
        public int Count { get; set; } = 1;
    }

    private sealed class QeGoalChange {
        public string Goal { get; set; } = "";
        public int? Count { get; set; }
        public ulong Npc { get; set; }
        public bool Skip { get; set; }
        public bool AnyCreature { get; set; }

        internal bool ChangesNothing => Count is null && Npc == 0 && !Skip && !AnyCreature;
    }

    private sealed class QeOptionFile {
        public List<QeChange> Quests { get; set; } = [];
    }

    private sealed record QeRow(QeQuest Quest, QeChange? Change, int Order);

    private static readonly JsonSerializerOptions s_qeJson = new() {
        WriteIndented = true, PropertyNameCaseInsensitive = true, AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] s_qeShows = ["Every quest", "Changed by me", "My own quests", "Off or skipped", "Not changed"];
    private static readonly string[] s_qeKinds = [ANY_FILTER, "Talk", "Defeat", "Collect", "Go", "Use", "Other"];
    private static readonly string[] s_qeSorts = ["Story order", "Level (low first)", "Level (high first)", "Name", "Code", "Giver"];

    private readonly TextBox _qeSearch = new() { Watermark = "Search a quest's name, code, giver, step or reward" };
    private readonly ComboBox _qeShow = FilterCombo(s_qeShows, 150);
    private readonly ComboBox _qeWorld = FilterCombo([ANY_FILTER], 150);
    private readonly ComboBox _qeKind = FilterCombo(s_qeKinds, 120);
    private readonly ComboBox _qeGiver = FilterCombo([ANY_FILTER], 180);
    private readonly ComboBox _qeSort = FilterCombo(s_qeSorts, 150);
    private readonly NumericUpDown _qeLevelMin = FilterNumber(5);
    private readonly NumericUpDown _qeLevelMax = FilterNumber(5);
    private readonly NumericUpDown _qeStepsMin = FilterNumber(1);
    private readonly NumericUpDown _qeStepsMax = FilterNumber(1);
    private readonly CheckBox _qeMainOnly = new() { Content = "Main story only" };
    private readonly CheckBox _qeRewardItems = new() { Content = "Gives items" };
    private readonly TextBlock _qeCount = Sub("");
    private readonly ListBox _qeResults = new() { Background = Brushes.Transparent, MaxHeight = 420 };
    private readonly StackPanel _qeEditor = new() { Spacing = 10 };

    private QeFile _qeData = new([], []);
    private Dictionary<string, QeQuest> _qeByCode = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<ulong, QeNpc> _qeNpcs = [];
    private List<QeChange> _qeChanges = [];
    private DateTime _qeDataWritten;
    private QeQuest? _qeQuest;
    private QeChange _qeEdit = new();

    private string QuestDetailsPath => Path.Combine(_manager.IdLookupFolder, QUEST_DETAILS_FILE);

    private string QuestOptionsPath => Path.Combine(_manager.ExtraFolder, QUEST_OPTIONS_FILE);

    private Control QuestEditorTab() {
        _qeResults.ItemTemplate = new FuncDataTemplate<QeRow>((row, _) => row is null ? new Panel() : QeRowView(row));
        _qeResults.SelectionChanged += (_, _) => {
            if (_qeResults.SelectedItem is QeRow row) {
                EditQuest(row.Quest, row.Change);
            }
        };
        WhenFiltersChange(RenderQuestEditor, _qeSearch, _qeShow, _qeWorld, _qeKind, _qeGiver, _qeSort, _qeLevelMin, _qeLevelMax,
            _qeStepsMin, _qeStepsMax, _qeMainOnly, _qeRewardItems);
        _qeEditor.Children.Add(Sub("Pick a quest in the list above to change it."));

        return Page(
            MissingNote("it doesn't write the quest details this page shows.", () => !_manager.HasQuestEditor),
            Fold("flag", "Customs", "Quests",
                "Find any quest of the game, or one of your own. Pick it to change it below.",
                WithHint(_qeSearch,
                    "Every quest of the game shows when the box is empty. Type words of its name, its code (WC-MAIN-C01-011), " +
                    "the NPC who gives it, a step (Defeat 3 Lost Souls) or a reward. The filters narrow it down. Changed by me: " +
                    "quests with changes of yours. My own quests: the ones you made from a game quest."),
                SearchFilters(
                    SearchFilter("Show", _qeShow),
                    SearchFilter("World", _qeWorld),
                    SearchFilter("Level from", _qeLevelMin),
                    SearchFilter("Level to", _qeLevelMax),
                    SearchFilter("Given by", _qeGiver),
                    SearchFilter("Has a step to", _qeKind),
                    SearchFilter("Steps, from", _qeStepsMin),
                    SearchFilter("Steps, to", _qeStepsMax),
                    SearchFilter("Sort by", _qeSort),
                    SearchTick(_qeMainOnly),
                    SearchTick(_qeRewardItems)),
                _qeCount,
                Card(_qeResults)),
            Fold("target", "Customs", "Change the quest",
                "Everything you can change about the picked quest. Save changes and the server uses them within two seconds, " +
                "with no restart. The game's own files are never changed.",
                _qeEditor));
    }

    /// <summary>Reads the server's quest details and your changes again when either was saved since. Runs when the tab shows.</summary>
    private void LoadQuestEditor() {
        var changed = LoadQuestDetails();
        _qeChanges = ReadQuestChanges();
        // The story order of the Quests tab's list.
        LoadQuestGuide();
        if (changed || _qeResults.ItemsSource is null) {
            RenderQuestEditor();
        }
    }

    /// <summary>Reads quest-details.json again when the server wrote it since (the Quest Tree tab reads it too). True when it changed.</summary>
    private bool LoadQuestDetails() {
        var changed = false;
        try {
            var written = File.Exists(QuestDetailsPath) ? File.GetLastWriteTimeUtc(QuestDetailsPath) : DateTime.MinValue;
            if (written != _qeDataWritten) {
                _qeDataWritten = written;
                _qeData = written == DateTime.MinValue ? new QeFile([], [])
                    : JsonSerializer.Deserialize<QeFile>(File.ReadAllText(QuestDetailsPath), s_qeJson) ?? new QeFile([], []);
                _qeByCode = _qeData.Quests.GroupBy(q => q.Code, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                _qeNpcs = _qeData.Npcs.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
                FillFilterCombo(_qeWorld, _qeData.Quests.Select(q => QuestWorldOf(q.Code, "")).OrderBy(WorldOrder)
                    .ThenBy(w => w, StringComparer.OrdinalIgnoreCase));
                FillFilterCombo(_qeGiver, _qeData.Quests.Select(q => q.GiverName).OrderBy(g => g, StringComparer.OrdinalIgnoreCase));
                changed = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            _qeCount.Text = $"Could not read {QUEST_DETAILS_FILE}: {ex.Message}";
        }

        return changed;
    }

    private List<QeChange> ReadQuestChanges() {
        try {
            return File.Exists(QuestOptionsPath)
                ? JsonSerializer.Deserialize<QeOptionFile>(File.ReadAllText(QuestOptionsPath), s_qeJson)?.Quests?.Where(c => c is not null).ToList() ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            Say($"Could not read your quest changes ({QUEST_OPTIONS_FILE}): {ex.Message}", isError: true);

            return _qeChanges;
        }
    }

    // A quest of your own: the game quest it copies, under its own code and title.
    private QeQuest? BaseOf(QeChange change)
        => _qeByCode.GetValueOrDefault(change.CopyOf.Length > 0 ? change.CopyOf : change.Quest);

    private IEnumerable<QeRow> AllQuestRows() {
        var order = _quests.Select((q, i) => (q.Code, i)).GroupBy(p => p.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);
        var changes = _qeChanges.Where(c => c.CopyOf.Length == 0).GroupBy(c => c.Quest, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var copy in _qeChanges.Where(c => c.CopyOf.Length > 0)) {
            if (BaseOf(copy) is { } original) {
                yield return new QeRow(original with { Code = copy.Quest }, copy, order.GetValueOrDefault(copy.CopyOf, int.MaxValue - 1));
            }
        }

        foreach (var quest in _qeData.Quests) {
            yield return new QeRow(quest, changes.GetValueOrDefault(quest.Code), order.GetValueOrDefault(quest.Code, int.MaxValue));
        }
    }

    private void RenderQuestEditor() {
        var query = _qeSearch.Text ?? "";
        var show = _qeShow.SelectedIndex;
        var world = FilterPick(_qeWorld);
        var kind = FilterPick(_qeKind);
        var giver = FilterPick(_qeGiver);
        var all = AllQuestRows().ToList();
        var rows = all.Where(row => {
            var q = row.Quest;
            var c = row.Change;
            return show switch {
                       1 => c is not null,
                       2 => c is { CopyOf.Length: > 0 },
                       3 => c is not null && (c.Off || c.Skip),
                       4 => c is null,
                       _ => true,
                   }
                   && (world is null || QuestWorldOf(q.Code, "") == world)
                   && (giver is null || string.Equals(q.GiverName, giver, StringComparison.OrdinalIgnoreCase))
                   && (kind is null || q.Goals.Any(g => g.Kind == kind))
                   && InFilterRange(c?.Level ?? q.Level, _qeLevelMin, _qeLevelMax)
                   && InFilterRange(q.Goals.Count, _qeStepsMin, _qeStepsMax)
                   && (_qeMainOnly.IsChecked != true || q.Main)
                   && (_qeRewardItems.IsChecked != true || q.Rewards.Items.Count > 0 || c?.Items.Count > 0)
                   && (query.Length == 0 || SearchText.Matches(string.Join(' ', q.Title, q.Code, q.GiverName,
                       string.Join(' ', q.Goals.Select(g => g.Text + " " + g.Place)), string.Join(' ', q.Rewards.Items), c?.Note), query));
        });
        rows = _qeSort.SelectedIndex switch {
            1 => rows.OrderBy(r => r.Change?.Level ?? r.Quest.Level).ThenBy(r => r.Order),
            2 => rows.OrderByDescending(r => r.Change?.Level ?? r.Quest.Level).ThenBy(r => r.Order),
            3 => rows.OrderBy(r => r.Quest.Title, StringComparer.OrdinalIgnoreCase),
            4 => rows.OrderBy(r => r.Quest.Code, StringComparer.OrdinalIgnoreCase),
            5 => rows.OrderBy(r => r.Quest.GiverName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Order),
            _ => rows.OrderBy(r => r.Order).ThenBy(r => r.Quest.Code, StringComparer.OrdinalIgnoreCase),
        };
        var list = rows.ToList();
        _qeCount.Text = _qeData.Quests.Count == 0
            ? "The quest list isn't here yet: start the server once (it writes the list as it starts), then come back."
            : FilterCount(list.Count, all.Count, "quests", QUEST_EDITOR_SHOWN);
        _qeResults.ItemsSource = list.Take(QUEST_EDITOR_SHOWN).ToList();
    }

    private Control QeRowView(QeRow row) {
        var q = row.Quest;
        var c = row.Change;
        var tags = new List<string>();
        if (c is { CopyOf.Length: > 0 }) {
            tags.Add("My own quest");
        }
        else if (c is not null) {
            tags.Add("Changed");
        }

        if (c?.Off == true) {
            tags.Add("Off");
        }

        if (c?.Skip == true) {
            tags.Add("Skipped");
        }

        if (c?.Repeatable == true) {
            tags.Add("Repeatable");
        }

        var line = $"{q.Code}  ·  level {c?.Level ?? q.Level}  ·  {(q.GiverName.Length > 0 ? "from " + q.GiverName : q.StartsByItself ? "starts by itself" : "giver not known")}  ·  " +
                   $"{q.Goals.Count} step{(q.Goals.Count == 1 ? "" : "s")}";
        return new StackPanel {
            Margin = new Thickness(4, 3),
            Children = {
                new TextBlock {
                    Text = tags.Count > 0 ? $"{q.Title}   [{string.Join(", ", tags)}]" : q.Title,
                    FontWeight = c is null ? FontWeight.Normal : FontWeight.SemiBold,
                },
                Sub(line),
            },
        };
    }

    // ---------------------------------------------------------------------
    // The editor
    // ---------------------------------------------------------------------

    private void EditQuest(QeQuest quest, QeChange? change) {
        _qeQuest = quest;
        _qeEdit = change?.Copy() ?? new QeChange { Quest = quest.Code };
        RenderQuestForm();
    }

    private string NpcName(ulong id) => _qeNpcs.TryGetValue(id, out var npc) ? $"{npc.Name}{(npc.Zone.Length > 0 ? $" ({npc.Zone})" : "")}" : $"NPC {id}";

    private string QuestName(string code) => _qeByCode.TryGetValue(code, out var q) ? $"{q.Title} ({code})"
        : _qeChanges.FirstOrDefault(c => c.Quest.Equals(code, StringComparison.OrdinalIgnoreCase)) is { } own && BaseOf(own) is { } b
            ? $"{b.Title} ({code}, yours)" : code;

    private static NumericUpDown QeNumber(int? value, int max, decimal step, string watermark, double width = 130) => new() {
        Minimum = 0, Maximum = max, Increment = step, FormatString = "0", Width = width, Watermark = watermark,
        Value = value,
    };

    private static TextBlock QeLabel(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };

    private void RenderQuestForm() {
        _qeEditor.Children.Clear();
        if (_qeQuest is not { } quest) {
            _qeEditor.Children.Add(Sub("Pick a quest in the list above to change it."));

            return;
        }

        var edit = _qeEdit;
        var isCopy = edit.CopyOf.Length > 0;
        _qeEditor.Children.Add(Heading(quest.Title));
        _qeEditor.Children.Add(Sub(isCopy
            ? $"Your own quest {edit.Quest}, made from {QuestName(edit.CopyOf)}. It has the game quest's words and steps, with your changes."
            : $"{quest.Code}  ·  level {quest.Level}  ·  {(quest.Main ? "main story" : "side quest")}  ·  " +
              $"{(quest.GiverName.Length > 0 ? "given by " + quest.GiverName : quest.StartsByItself ? "starts by itself" : "giver not known")}"));

        // On or off
        var status = FilterCombo(["As in the game", "Off: nobody is offered it", "Skip: it counts as done"], 260);
        status.SelectedIndex = edit.Skip ? 2 : edit.Off ? 1 : 0;
        status.SelectionChanged += (_, _) => {
            edit.Off = status.SelectedIndex == 1;
            edit.Skip = status.SelectedIndex == 2;
        };
        var repeat = new CheckBox { Content = "Can be done again after it is done", IsChecked = edit.Repeatable };
        repeat.IsCheckedChanged += (_, _) => edit.Repeatable = repeat.IsChecked == true;
        var story = FilterCombo(["As in the game", "Main story", "Side quest"], 160);
        story.SelectedIndex = edit.Mainline switch { true => 1, false => 2, _ => 0 };
        story.SelectionChanged += (_, _) => edit.Mainline = story.SelectedIndex switch { 1 => true, 2 => false, _ => null };
        _qeEditor.Children.Add(QeSection("On or off",
            "Off: no NPC offers it and it never starts by itself (a wizard who has it keeps it). Skip: every quest, door and NPC " +
            "that waits for it acts as if it were done, so the story goes on without it. Main story or side quest changes how " +
            "the quest book marks it.",
            Row(QeLabel("The quest is"), status), repeat, Row(QeLabel("Quest book marks it as"), story)));

        // Level
        var level = QeNumber(edit.Level, 300, 1, $"{quest.Level} (game)", 180);
        level.ValueChanged += (_, _) => edit.Level = level.Value is { } v && v > 0 ? (int) v : null;
        _qeEditor.Children.Add(QeSection("Level",
            $"The wizard must be at least this level to be offered it, and the quest book shows it. Empty: as in the game " +
            $"(level {quest.Level}, which the game only shows).",
            Row(QeLabel("Needs level"), level)));

        // Who gives it
        var giverNow = Sub("");
        void ShowGiver() => giverNow.Text = edit.StartsByItself ? "Nobody gives it: it starts by itself as soon as the wizard may have it."
            : edit.Giver != 0 ? $"Given by {NpcName(edit.Giver)} (yours)."
            : quest.GiverName.Length > 0 ? $"Given by {quest.GiverName} (as in the game)." : "The game names no giver for it.";
        ShowGiver();
        var auto = new CheckBox { Content = "Starts by itself (no NPC gives it)", IsChecked = edit.StartsByItself };
        auto.IsCheckedChanged += (_, _) => {
            edit.StartsByItself = auto.IsChecked == true;
            ShowGiver();
        };
        _qeEditor.Children.Add(QeSection("Who gives it",
            "Pick any NPC who gives a quest or that a quest talks to. The NPC offers it with the yellow !, and the offer's " +
            "lines show their name and picture.",
            giverNow,
            QeNpcPicker("Search an NPC to give it, by name or zone", npc => {
                edit.Giver = npc.Id;
                edit.StartsByItself = false;
                auto.IsChecked = false;
                ShowGiver();
            }),
            Row(auto, Action(Plain("Use the game's giver"), () => {
                edit.Giver = 0;
                edit.StartsByItself = false;
                auto.IsChecked = false;
                ShowGiver();

                return Task.CompletedTask;
            }))));

        // Quests needed first
        var needsRows = new StackPanel { Spacing = 4 };
        void RenderNeeds() {
            needsRows.Children.Clear();
            foreach (var code in edit.Needs.ToList()) {
                needsRows.Children.Add(Row(QeLabel(QuestName(code)), Danger(Action(Plain("Remove"), () => {
                    edit.Needs.Remove(code);
                    RenderNeeds();

                    return Task.CompletedTask;
                }))));
            }
        }

        RenderNeeds();
        var needsOnly = new CheckBox { Content = "Only the quests below (forget everything the game itself asks for)", IsChecked = edit.NeedsOnly };
        needsOnly.IsCheckedChanged += (_, _) => edit.NeedsOnly = needsOnly.IsChecked == true;
        _qeEditor.Children.Add(QeSection("Quests needed first",
            "The game asks for: " + (quest.Needs.Count > 0 ? string.Join(", ", quest.Needs.Select(QuestName)) : "no quest") +
            ". Quests you add here must be done too. With the tick and no quest added, it needs nothing at all.",
            needsOnly, needsRows,
            QeQuestPicker("Search a quest it needs, by name or code", code => {
                if (!edit.Needs.Contains(code, StringComparer.OrdinalIgnoreCase)) {
                    edit.Needs.Add(code);
                }

                RenderNeeds();
            })));

        // Steps
        var steps = new StackPanel { Spacing = 8 };
        var lastTalk = quest.Goals.FindLastIndex(g => g.Kind == "Talk");
        for (var i = 0; i < quest.Goals.Count; i++) {
            steps.Children.Add(QeStep(quest.Goals[i], i, i == lastTalk && i == quest.Goals.Count - 1, edit));
        }

        if (quest.Goals.Count == 0) {
            steps.Children.Add(Sub("The server knows no steps for this quest."));
        }

        _qeEditor.Children.Add(QeSection("Steps",
            "Each step in the order the game has them. Change how many creatures to defeat or things to collect, who to talk " +
            "to (the last step's NPC is who you hand it in to), let any won battle count, or skip a step (done the moment it starts).",
            steps));

        // Rewards
        var r = quest.Rewards;
        var keep = new CheckBox { Content = "Give the game's rewards too", IsChecked = edit.KeepGameRewards };
        keep.IsCheckedChanged += (_, _) => edit.KeepGameRewards = keep.IsChecked == true;
        var xp = QeNumber(edit.Xp == 0 ? null : edit.Xp, 100_000_000, 100, "0");
        xp.ValueChanged += (_, _) => edit.Xp = (int) (xp.Value ?? 0);
        var gold = QeNumber(edit.Gold == 0 ? null : edit.Gold, 100_000_000, 100, "0");
        gold.ValueChanged += (_, _) => edit.Gold = (int) (gold.Value ?? 0);
        var tp = QeNumber(edit.TrainingPoints == 0 ? null : edit.TrainingPoints, 1000, 1, "0", 110);
        tp.ValueChanged += (_, _) => edit.TrainingPoints = (int) (tp.Value ?? 0);
        var itemRows = new StackPanel { Spacing = 4 };
        void RenderItems() {
            itemRows.Children.Clear();
            foreach (var item in edit.Items.ToList()) {
                var count = QeNumber(item.Count, 999, 1, "1", 100);
                count.ValueChanged += (_, _) => item.Count = Math.Max(1, (int) (count.Value ?? 1));
                var id = item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                itemRows.Children.Add(Row(DropIcon(id, 26), QeLabel(CrownItemName(id)), QeLabel("how many"), count,
                    Danger(Action(Plain("Remove"), () => {
                        edit.Items.Remove(item);
                        RenderItems();

                        return Task.CompletedTask;
                    }))));
            }
        }

        RenderItems();
        var game = new List<string>();
        if (r.Xp > 0) {
            game.Add($"{r.Xp:N0} experience");
        }

        if (r.GoldMax > 0) {
            game.Add(r.GoldMin == r.GoldMax ? $"{r.GoldMax:N0} gold" : $"{r.GoldMin:N0} to {r.GoldMax:N0} gold");
        }

        if (r.TrainingPoints > 0) {
            game.Add($"{r.TrainingPoints} training point{(r.TrainingPoints == 1 ? "" : "s")}");
        }

        if (r.Items.Count > 0) {
            game.Add((r.Items.Count > 1 ? "one of: " : "") + string.Join(", ", r.Items));
        }

        _qeEditor.Children.Add(QeSection("Rewards",
            "The game gives: " + (game.Count > 0 ? string.Join("; ", game) : "nothing the server knows of") +
            ". Spells it teaches are always given. What you set here is given on top when the quest is done, and shows in " +
            "the quest's offer and quest book.",
            keep,
            Row(QeLabel("Experience"), xp, QeLabel("Gold"), gold, QeLabel("Training points"), tp),
            itemRows,
            CrownItemSearch("Search an item to give, by name or ID", id => {
                if (ulong.TryParse(id, out var itemId)) {
                    edit.Items.Add(new QeItem { Id = itemId, Count = 1 });
                    RenderItems();
                }

                return Task.CompletedTask;
            })));

        // Note and buttons
        var note = new TextBox { Watermark = "A note for yourself (shows in the search)", Text = edit.Note, Width = 520, HorizontalAlignment = HorizontalAlignment.Left };
        note.TextChanged += (_, _) => edit.Note = note.Text ?? "";
        _qeEditor.Children.Add(note);
        _qeEditor.Children.Add(new WrapPanel {
            Children = {
                Spaced(Action(Primary("Save changes"), SaveQuestEditAsync)),
                Spaced(Danger(Action(Plain(isCopy ? "Delete my quest" : "Undo all my changes"), RemoveQuestEditAsync))),
                Spaced(Action(Plain("Make my own quest from this one"), CopyQuestAsync)),
            },
        });
    }

    private static Control QeSection(string title, string help, params Control[] body) {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold });
        stack.Children.Add(Sub(help));
        stack.Children.AddRange(body);

        return Card(stack);
    }

    private Control QeStep(QeGoal goal, int index, bool handIn, QeChange edit) {
        QeGoalChange Change() {
            var found = edit.Goals.FirstOrDefault(g => g.Goal == goal.Name);
            if (found is null) {
                found = new QeGoalChange { Goal = goal.Name };
                edit.Goals.Add(found);
            }

            return found;
        }

        var had = edit.Goals.FirstOrDefault(g => g.Goal == goal.Name);
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock {
            Text = $"{index + 1}. {goal.Text}{(goal.Place.Length > 0 ? $"  ({goal.Place})" : "")}{(handIn ? "  (hand in)" : "")}",
            TextWrapping = TextWrapping.Wrap,
        });

        var row = new WrapPanel();
        if (goal.Kind is "Defeat" or "Collect" or "Use") {
            var count = QeNumber(had?.Count, 10_000, 1, $"{Math.Max(goal.Count, 1)} (game)", 180);
            count.ValueChanged += (_, _) => Change().Count = count.Value is { } v && v > 0 ? (int) v : null;
            row.Children.Add(Spaced(Row(QeLabel(goal.Kind == "Use" ? "Times" : "How many"), count)));
        }

        if (goal.Kind == "Defeat") {
            var any = new CheckBox { Content = "Any won battle counts", IsChecked = had?.AnyCreature == true };
            any.IsCheckedChanged += (_, _) => Change().AnyCreature = any.IsChecked == true;
            row.Children.Add(Spaced(any));
        }

        var skip = new CheckBox { Content = "Skip this step", IsChecked = had?.Skip == true };
        skip.IsCheckedChanged += (_, _) => Change().Skip = skip.IsChecked == true;
        row.Children.Add(Spaced(skip));
        stack.Children.Add(row);

        if (goal.Kind == "Talk") {
            var who = Sub("");
            void ShowWho() => who.Text = (had = edit.Goals.FirstOrDefault(g => g.Goal == goal.Name)) is { Npc: > 0 } c
                ? $"Talk to {NpcName(c.Npc)} (yours)."
                : $"Talk to {(goal.NpcName.Length > 0 ? goal.NpcName : "the game's NPC")} (as in the game).";
            ShowWho();
            stack.Children.Add(who);
            stack.Children.Add(Row(QeNpcPicker(handIn ? "Search an NPC to hand it in to" : "Search another NPC to talk to", npc => {
                Change().Npc = npc.Id;
                ShowWho();
            }), Action(Plain("Game's NPC"), () => {
                Change().Npc = 0;
                ShowWho();

                return Task.CompletedTask;
            })));
        }

        return new Border {
            BorderBrush = LINE, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 6), Child = stack,
        };
    }

    /// <summary>A search box over the NPCs the server listed, each match with a button that picks it. Lists them all when empty.</summary>
    private Control QeNpcPicker(string watermark, Action<QeNpc> picked) {
        var search = new TextBox { Watermark = watermark, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        var results = new StackPanel { Spacing = 2 };
        void Render() {
            results.Children.Clear();
            var query = search.Text ?? "";
            var matches = _qeData.Npcs.Where(n => SearchText.Matches($"{n.Name} {n.Code} {n.Zone} {n.Id}", query)).Take(12).ToList();
            if (matches.Count == 0) {
                results.Children.Add(Sub(_qeData.Npcs.Count == 0 ? "The NPC list isn't here yet: start the server once." : "No NPC matches."));
            }

            foreach (var npc in matches) {
                results.Children.Add(Row(QeLabel($"{npc.Name}{(npc.Zone.Length > 0 ? $"  ·  {npc.Zone}" : "")}"),
                    Action(Plain("Pick"), () => {
                        search.Text = "";
                        results.Children.Clear();
                        picked(npc);

                        return Task.CompletedTask;
                    })));
            }
        }

        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        delay.Tick += (_, _) => {
            delay.Stop();
            Render();
        };
        search.TextChanged += (_, _) => {
            delay.Stop();
            delay.Start();
        };
        search.GotFocus += (_, _) => Render();

        return new StackPanel { Spacing = 4, Children = { search, results } };
    }

    /// <summary>A search box over every quest (the game's and your own), each match with a button that picks it.</summary>
    private Control QeQuestPicker(string watermark, Action<string> picked) {
        var search = new TextBox { Watermark = watermark, Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        var results = new StackPanel { Spacing = 2 };
        void Render() {
            results.Children.Clear();
            var query = search.Text ?? "";
            var matches = AllQuestRows().Where(r => SearchText.Matches($"{r.Quest.Title} {r.Quest.Code}", query))
                .OrderBy(r => r.Order).Take(12).ToList();
            foreach (var row in matches) {
                results.Children.Add(Row(QeLabel($"{row.Quest.Title} ({row.Quest.Code})"), Action(Plain("Add"), () => {
                    search.Text = "";
                    results.Children.Clear();
                    picked(row.Quest.Code);

                    return Task.CompletedTask;
                })));
            }
        }

        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        delay.Tick += (_, _) => {
            delay.Stop();
            Render();
        };
        search.TextChanged += (_, _) => {
            delay.Stop();
            delay.Start();
        };
        search.GotFocus += (_, _) => Render();

        return new StackPanel { Spacing = 4, Children = { search, results } };
    }

    // ---------------------------------------------------------------------
    // Saving
    // ---------------------------------------------------------------------

    private async Task WriteQuestChangesAsync(List<QeChange> changes) {
        Directory.CreateDirectory(_manager.ExtraFolder);
        var file = new QeOptionFile { Quests = changes };
        // Written beside the old file and then moved over it, so the server never reads half a file.
        var temp = $"{QuestOptionsPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(file, s_qeJson));
        File.Move(temp, QuestOptionsPath, overwrite: true);
        _qeChanges = changes;
        RenderQuestEditor();
    }

    private async Task SaveQuestEditAsync() {
        var edit = _qeEdit.Copy();
        edit.Goals = [.. edit.Goals.Where(g => !g.ChangesNothing)];
        edit.Items = [.. edit.Items.Where(i => i.Id != 0 && i.Count > 0)];
        var changes = ReadQuestChanges().Where(c => !c.Quest.Equals(edit.Quest, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!edit.ChangesNothing) {
            changes.Add(edit);
        }

        await WriteQuestChangesAsync(changes);
        Say(edit.ChangesNothing
            ? $"{edit.Quest} has no changes now: it is as in the game."
            : $"Saved {edit.Quest}. The server uses it within two seconds. A wizard who has the quest now sees step changes " +
              "after the next zone change; new offers and rewards count at once.");
    }

    private async Task RemoveQuestEditAsync() {
        var code = _qeEdit.Quest;
        var changes = ReadQuestChanges().Where(c => !c.Quest.Equals(code, StringComparison.OrdinalIgnoreCase)).ToList();
        await WriteQuestChangesAsync(changes);
        if (_qeEdit.CopyOf.Length > 0) {
            _qeQuest = null;
            RenderQuestForm();
            Say($"Deleted your quest {code}.");

            return;
        }

        _qeEdit = new QeChange { Quest = code };
        RenderQuestForm();
        Say($"{code} is as in the game again.");
    }

    private async Task CopyQuestAsync() {
        if (_qeQuest is not { } quest) {
            return;
        }

        var source = _qeEdit.CopyOf.Length > 0 ? _qeEdit.CopyOf : quest.Code;
        var changes = ReadQuestChanges();
        var number = 1;
        string code;
        do {
            code = $"MY-{number:000}-{source}";
            number++;
        } while (_qeByCode.ContainsKey(code) || changes.Any(c => c.Quest.Equals(code, StringComparison.OrdinalIgnoreCase)));

        var copy = _qeEdit.Copy();
        copy.Quest = code;
        copy.CopyOf = source;
        copy.Note = $"My copy of {quest.Title}";
        changes.Add(copy);
        await WriteQuestChangesAsync(changes);
        _qeQuest = quest with { Code = code };
        _qeEdit = copy.Copy();
        RenderQuestForm();
        Say($"Made your own quest {code} from {quest.Title}. Pick who gives it and change it as you like, then Save changes. " +
            "Until you pick another giver, the game quest's NPC offers it (after the game quest, when it needs that one).");
    }

}
