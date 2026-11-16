using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

// "Stuck solutions" (Niduus, 2026-11-15: see and clear what was learned): logs\ultimus\stuck-solutions.json, the moves that
// got Ultimus or the Quest Team going (or didn't) at a stuck spot, with Ultimus's guess at the cause. "Forget" writes the
// ids to stuck-solutions-forget.txt, which the server reads within a few seconds; with the server stopped the file itself is
// changed. Read again whenever the file changes.
internal sealed partial class ManagerWindow {

    private const string SOLUTIONS_FILE = "stuck-solutions.json";
    private const string SOLUTIONS_FORGET = "stuck-solutions-forget.txt";
    private readonly StackPanel _ultSolutions = new() { Spacing = 6 };
    private readonly TextBlock _ultSolutionsEmpty = new() { TextWrapping = TextWrapping.Wrap };
    private DateTime _ultSolutionsRead = DateTime.MinValue;

    private Control UltimusSolutionsFold() => Fold("list", ULTIMUS_TAB, "Stuck solutions",
        "What Ultimus and the Quest Team learned when they got stuck: the quest step, where, Ultimus's guess at why, the move " +
        "they made and how often it worked or failed. They try what worked first next time. Forget a line if it is wrong " +
        "(for example after you fixed that quest), or forget everything to start fresh. Updates by itself.",
        Row(Action(Plain("Forget everything"), () => ForgetSolutionsAsync(["all"]))),
        _ultSolutionsEmpty,
        _ultSolutions);

    private void ShowUltimusSolutions() {
        var path = Path.Combine(UltimusSharedFolder, SOLUTIONS_FILE);
        var changed = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        if (changed == _ultSolutionsRead && _ultSolutionsEmpty.Text?.Length > 0) {
            return;
        }

        _ultSolutionsRead = changed;
        var rows = new List<JsonObject>();
        try {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonArray all) {
                rows.AddRange(all.OfType<JsonObject>());
            }
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException) {
            return;
        }

        _ultSolutions.Children.Clear();
        _ultSolutionsEmpty.Text = rows.Count == 0
            ? "Nothing learned yet. Lines show up here after Ultimus or the Quest Team get stuck and try a move."
            : $"{rows.Count} line(s), {rows.Count(r => ((int?) r["Worked"] ?? 0) > ((int?) r["Failed"] ?? 0))} with a move that works. Newest first.";
        foreach (var r in rows.OrderByDescending(r => (DateTime?) r["Last"] ?? DateTime.MinValue).Take(200)) {
            var id = (string?) r["Id"] ?? "";
            var step = ((string?) r["Step"] ?? "").Split('|') is var parts && parts.Length >= 3 ? parts[^1] : (string?) r["Step"] ?? "";
            var worked = (int?) r["Worked"] ?? 0;
            var failed = (int?) r["Failed"] ?? 0;
            var cause = (string?) r["Cause"] ?? "";
            var text = $"{(worked > failed ? "Works" : worked + failed == 0 ? "Not checked yet" : "Does not work")}: " +
                       $"\"{SolutionMove((string?) r["Move"] ?? "")}\" for \"{step}\" ({SolutionQuest((string?) r["Quest"] ?? "")}), in {SolutionZone((string?) r["Zone"] ?? "")}. " +
                       $"Worked {worked}, failed {failed}. By {(string?) r["By"] ?? "?"}" +
                       ((DateTime?) r["Last"] is { } last ? $", last {last.ToLocalTime():MMM d HH:mm}." : ".") +
                       (cause.Length > 0 ? $" Ultimus's guess at why: {cause}" : "");
            var line = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            var forget = Action(Plain("Forget"), () => ForgetSolutionsAsync([id]));
            forget.IsEnabled = id.Length > 0;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(line);
            Grid.SetColumn(forget, 1);
            forget.Margin = new Thickness(8, 0, 0, 0);
            grid.Children.Add(forget);
            _ultSolutions.Children.Add(grid);
        }
    }

    private static string SolutionMove(string move) => move switch {
        "step_aside" => "step a little aside",
        "walk_away_and_back" => "walk well away and come back",
        "other_route" => "take another route",
        "commons_and_walk_back" => "press Commons and walk back",
        "fight_nearby" => "fight the creatures around",
        "click_offered" => "click what is offered",
        "set_step_aside" => "set the step aside for a while",
        _ => move,
    };

    private static string SolutionQuest(string quest) => quest.Length == 0 ? "no quest" : quest;

    private static string SolutionZone(string zone) => zone.Split('/', '\\').LastOrDefault(z => z.Length > 0) ?? zone;

    private async Task ForgetSolutionsAsync(string[] ids) {
        Directory.CreateDirectory(UltimusSharedFolder);
        if (InstalledCommands.IsServerRunning(_manager.InstallFolder)) {
            await File.AppendAllLinesAsync(Path.Combine(UltimusSharedFolder, SOLUTIONS_FORGET), ids);
            Say(ids.Contains("all") ? "Asked: everything learned is forgotten within a few seconds." : "Asked: that line is forgotten within a few seconds.");
            _ultSolutionsRead = DateTime.MinValue;
            return;
        }

        // Server stopped: the file is changed here.
        var path = Path.Combine(UltimusSharedFolder, SOLUTIONS_FILE);
        if (File.Exists(path) && JsonNode.Parse(await File.ReadAllTextAsync(path)) is JsonArray all) {
            var keep = new JsonArray();
            foreach (var node in all.ToList()) {
                if (node is JsonObject o && !ids.Contains("all") && !ids.Contains((string?) o["Id"] ?? "\0")) {
                    all.Remove(o);
                    keep.Add(o);
                }
            }

            await File.WriteAllTextAsync(path, keep.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        Say(ids.Contains("all") ? "Everything learned is forgotten." : "That line is forgotten.");
        _ultSolutionsRead = DateTime.MinValue;
        ShowUltimusSolutions();
    }

}
