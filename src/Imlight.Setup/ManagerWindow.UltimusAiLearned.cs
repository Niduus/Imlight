using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

// "What Ultimus learned from the AI" on Ahziael's Ultimus AI Chat page (Niduus, 2026-11-15: "I want him to learn from his AI
// answers so we can be more accurate in an offline setting"): the two switches ([Ultimus] AILearn, AILearnedChat) and the
// learned answers in logs\ultimus\ai-learned.json, each with a Remove button. The server reads the file again when it
// changes, so a removed answer is never said again.
internal sealed partial class ManagerWindow {

    private const string AI_LEARNED_FILE = "ai-learned.json";

    private readonly ToggleSwitch _ultAiLearn = new() { OnContent = "On", OffContent = "Off" };
    private readonly ToggleSwitch _ultAiLearnedChat = new() { OnContent = "On", OffContent = "Off" };
    private readonly TextBox _ultLearnedFilter = new() { Width = 260, Watermark = "Find a word, like Tower Shield" };
    private readonly TextBlock _ultLearnedState = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _ultLearnedList = new() { Spacing = 6 };
    private DateTime _ultLearnedRead = DateTime.MinValue;
    private bool _showingUltLearned;

    private Control UltimusAiLearnedFold() {
        _ultAiLearn.IsCheckedChanged += (_, _) => {
            if (!_showingUltLearned) {
                _ = Guard(() => SaveUltimusLearnSwitchAsync("AILearn", _ultAiLearn.IsChecked == true));
            }
        };
        _ultAiLearnedChat.IsCheckedChanged += (_, _) => {
            if (!_showingUltLearned) {
                _ = Guard(() => SaveUltimusLearnSwitchAsync("AILearnedChat", _ultAiLearnedChat.IsChecked == true));
            }
        };
        _ultLearnedFilter.TextChanged += (_, _) => {
            _ultLearnedRead = DateTime.MinValue;
            ShowUltimusAiLearned();
        };

        _showingUltLearned = true;
        try {
            _ultAiLearn.IsChecked = !string.Equals(_manager.GetSetting("Ultimus", "AILearn"), "false", StringComparison.OrdinalIgnoreCase);
            _ultAiLearnedChat.IsChecked = !string.Equals(_manager.GetSetting("Ultimus", "AILearnedChat"), "false", StringComparison.OrdinalIgnoreCase);
        }
        finally {
            _showingUltLearned = false;
        }

        ShowUltimusAiLearned();

        return Fold("info", ULTIMUS_TAB, "What Ultimus learned from the AI",
            "When the AI answers a player's Wizard101 question (a spell, a school, a world, gear, a creature, where to get something), Ultimus keeps the " +
            "answer, so his normal chat can answer the same question later without the AI: with the AI off, paused, used up or on a PC without internet. " +
            "He keeps only game knowledge: never his quest or place of that moment, never the player's name, and only what passed the safety check. " +
            "Every answer is checked again before he says it. He also keeps how a group leader worded a command (like \"Stay here\" or \"Let me go first\") " +
            "once the AI worked out which command it was, so he understands the same wording offline. Updates by itself.",
            SwitchRow("Learn from AI answers", _ultAiLearn, "On by default. Off keeps what he learned but adds nothing new."),
            SwitchRow("Use what he learned in normal chat", _ultAiLearnedChat,
                "On by default. When his normal chat has no answer, he answers from what he learned. In Thoughts these answers show as [learned from the AI]."),
            Row(_ultLearnedFilter, Action(Danger(Plain("Forget everything learned")), ForgetAllUltimusLearnedAsync)),
            _ultLearnedState,
            _ultLearnedList);
    }

    private Task SaveUltimusLearnSwitchAsync(string key, bool on) {
        if (!_manager.HasSetting("Ultimus", key)) {
            throw new PanelException($"This server's Imlight.ini has no [Ultimus] {key} yet. Click \"Update Ahziael\" once, then try again.");
        }

        _manager.SaveSettings([new Setting("Ultimus", key, on ? "true" : "false", "")]);
        _settings = [];
        Say(key == "AILearn"
            ? on ? "Ultimus learns from his AI answers again." : "Ultimus keeps what he learned but learns nothing new."
            : on ? "Ultimus answers from what he learned when his normal chat has no answer." : "Ultimus no longer answers from what he learned.");

        return Task.CompletedTask;
    }

    private Task ForgetAllUltimusLearnedAsync() {
        var path = Path.Combine(UltimusFolder, AI_LEARNED_FILE);
        if (File.Exists(path)) {
            File.Delete(path);
        }

        _ultLearnedRead = DateTime.MinValue;
        ShowUltimusAiLearned();
        Say("Ultimus forgot everything he learned from the AI.");

        return Task.CompletedTask;
    }

    private Task ForgetUltimusLearnedAsync(string question) {
        var path = Path.Combine(UltimusFolder, AI_LEARNED_FILE);
        var rows = ReadUltimusLearned(path);
        var kept = new JsonArray();
        foreach (var row in rows.Where(r => !string.Equals((string?) r["Question"], question, StringComparison.Ordinal))) {
            kept.Add(row.DeepClone());
        }

        File.WriteAllText(path, kept.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        _ultLearnedRead = DateTime.MinValue;
        ShowUltimusAiLearned();
        Say("Removed. Ultimus won't say that answer again.");

        return Task.CompletedTask;
    }

    private static List<JsonObject> ReadUltimusLearned(string path) {
        try {
            if (!File.Exists(path)) {
                return [];
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            return JsonNode.Parse(reader.ReadToEnd()) is JsonArray array ? [.. array.OfType<JsonObject>()] : [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
            return [];
        }
    }

    // The learned answers, newest first (read again when the file changes or the filter does).
    private void ShowUltimusAiLearned() {
        var path = Path.Combine(UltimusFolder, AI_LEARNED_FILE);
        var changed = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        if (changed == _ultLearnedRead && _ultLearnedState.Text?.Length > 0) {
            return;
        }

        _ultLearnedRead = changed;
        var rows = ReadUltimusLearned(path);
        var filter = (_ultLearnedFilter.Text ?? "").Trim();
        var shown = rows.Where(r => filter.Length == 0 || ((string?) r["Question"] ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase)
                                                       || ((string?) r["Answer"] ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => (string?) r["At"]).ToList();
        _ultLearnedState.Text = rows.Count == 0
            ? "Nothing learned yet. Turn AI chat on and ask Ultimus a Wizard101 question, like \"Where do I get Tower Shield?\"."
            : filter.Length > 0 ? $"{shown.Count} of {rows.Count} learned answers match." : $"{rows.Count} learned answers. Newest first.";
        _ultLearnedState.Foreground = MUTED;
        _ultLearnedList.Children.Clear();
        foreach (var r in shown.Take(100)) {
            var question = (string?) r["Question"] ?? "";
            var used = (int?) r["Used"] ?? 0;
            var at = DateTime.TryParse((string?) r["At"], System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal, out var when) ? when.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
            var text = new SelectableTextBlock {
                TextWrapping = TextWrapping.Wrap,
                Text = ((string?) r["Command"] is { Length: > 0 } command
                           ? $"Command: \"{question}\" means {command}"
                           : $"Q: {question}\nA: {(string?) r["Answer"]}") +
                       $"\n    from {(string?) r["By"]}, {at}{(used > 0 ? $", used {used} time(s) since" : "")}",
            };
            var remove = Action(Plain("Remove"), () => ForgetUltimusLearnedAsync(question));
            remove.VerticalAlignment = VerticalAlignment.Top;
            var row = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(text);
            _ultLearnedList.Children.Add(row);
        }
    }

}
