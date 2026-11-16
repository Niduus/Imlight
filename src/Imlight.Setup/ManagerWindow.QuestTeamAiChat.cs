using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Imlight.CoreLib.Game.Ultimus;

namespace Imlight.Setup;

// "Quest Team AI chat" on Ahziael's Ultimus AI Chat page (Niduus, 2026-11-15: "Allow for QT to have AI chats like Ultimus
// with each member able to be different models or an option to unify them into one model for responses"): the switch
// ([QuestTeam] AIChat), one AI for all ([QuestTeam] AIOneModel, AIProvider, AIModel) or one per wizard (AIProviderIce,
// AIModelIce and so on), and a key box for any online service. Keys go only in Config\ultimus-ai-keys.json, like Ultimus's.
internal sealed partial class ManagerWindow {

    // The team's names are locked (Niduus, 2026-11-14).
    private static readonly (string Role, string Name)[] s_qtAiMembers = [("Ice", "Freya"), ("Death", "Anubis"), ("Life", "Terra"), ("Storm", "Chaac")];

    private readonly ToggleSwitch _qtAiOne = new() { OnContent = "One AI", OffContent = "Each their own" };
    private readonly ComboBox _qtAiProvider = new() { Width = 300 };
    private readonly TextBox _qtAiModel = new() { Width = 220, Watermark = "Usual model" };
    private readonly Dictionary<string, (ComboBox Provider, TextBox Model)> _qtAiPerMember = [];
    private readonly ComboBox _qtAiKeyFor = new() { Width = 300 };
    private readonly TextBox _qtAiKey = new() { Width = 300, PasswordChar = '*', Watermark = "Paste the key here" };
    private readonly TextBlock _qtAiState = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _qtAiPerPlayer = new() { Width = 120, Watermark = "40" };
    private readonly TextBox _qtAiTalkMinutes = new() { Width = 120, Watermark = "10" };
    private readonly TextBox _qtAiCompareQuestion = new() { Width = 420, Watermark = "Where do I get Tower Shield?", Text = "Where do I get Tower Shield?" };
    private readonly SelectableTextBlock _qtAiCompareResult = new() { TextWrapping = TextWrapping.Wrap };
    private DateTime _qtAiCompareRead = DateTime.MinValue;
    private const string QT_AI_COMPARE_REQUEST = "questteam-ai-compare-request.txt";
    private const string QT_AI_COMPARE_RESULT = "qt-ai-compare.txt";
    private bool _showingQtAi;

    private static List<string> QtAiChoices(string first) => [first, .. UltimusAiProviders.All.Select(p => p.Name)];

    private Control QuestTeamAiChatFold() {
        _qtAiProvider.ItemsSource = QtAiChoices("Same as Ultimus");
        _qtAiKeyFor.ItemsSource = UltimusAiProviders.All.Where(p => !p.Local).Select(p => p.Name).ToList();
        _qtAiKeyFor.SelectedIndex = 0;

        _ultQtAi.IsCheckedChanged += (_, _) => ShowQuestTeamAiChat();
        _qtAiOne.IsCheckedChanged += (_, _) => {
            if (!_showingQtAi) {
                _ = Guard(() => SaveQuestTeamAiSettingAsync([("AIOneModel", _qtAiOne.IsChecked == true ? "true" : "false")],
                    _qtAiOne.IsChecked == true ? "The whole Quest Team answers with one AI." : "Each Quest Team wizard answers with its own AI."));
            }
        };
        _qtAiProvider.SelectionChanged += (_, _) => {
            if (!_showingQtAi && _qtAiProvider.SelectedIndex >= 0) {
                _ = Guard(() => SaveQuestTeamAiSettingAsync([("AIProvider", QtAiProviderId(_qtAiProvider)), ("AIModel", "")],
                    _qtAiProvider.SelectedIndex == 0 ? "The Quest Team uses Ultimus's AI." : $"The Quest Team uses {QtAiShortName(_qtAiProvider)}."));
            }
        };
        _qtAiModel.LostFocus += (_, _) => {
            if (!_showingQtAi) {
                _ = Guard(() => SaveQuestTeamAiSettingAsync([("AIModel", (_qtAiModel.Text ?? "").Trim())], "Saved the Quest Team's model name."));
            }
        };

        _qtAiPerPlayer.LostFocus += (_, _) => {
            if (!_showingQtAi) {
                _ = Guard(() => SaveQuestTeamAiNumberAsync("AIPerPlayer", _qtAiPerPlayer, "AI answers per player per day",
                    n => n == 0 ? "No per-player limit for the Quest Team's AI answers." : $"Each player gets at most {n} Quest Team AI answers a day."));
            }
        };
        _qtAiTalkMinutes.LostFocus += (_, _) => {
            if (!_showingQtAi) {
                _ = Guard(() => SaveQuestTeamAiNumberAsync("AITeamTalkMinutes", _qtAiTalkMinutes, "minutes between team talks",
                    n => n == 0 ? "The team no longer chats among themselves." : $"The team chats among themselves at most once every {n} minutes."));
            }
        };

        var rows = new List<Control>();
        foreach (var (role, name) in s_qtAiMembers) {
            var provider = new ComboBox { Width = 300, ItemsSource = QtAiChoices("Same as the team") };
            var model = new TextBox { Width = 220, Watermark = "Usual model" };
            _qtAiPerMember[role] = (provider, model);
            provider.SelectionChanged += (_, _) => {
                if (!_showingQtAi && provider.SelectedIndex >= 0) {
                    _ = Guard(() => SaveQuestTeamAiSettingAsync([("AIProvider" + role, QtAiProviderId(provider)), ("AIModel" + role, "")],
                        provider.SelectedIndex == 0 ? $"{name} uses the team's AI." : $"{name} uses {QtAiShortName(provider)}."));
                }
            };
            model.LostFocus += (_, _) => {
                if (!_showingQtAi) {
                    _ = Guard(() => SaveQuestTeamAiSettingAsync([("AIModel" + role, (model.Text ?? "").Trim())], $"Saved {name}'s model name."));
                }
            };
            rows.Add(SwitchRow($"{role} {name}", Row(provider, model)));
        }

        LoadQuestTeamAiChat();

        return Fold("info", ULTIMUS_TAB, "Quest Team AI chat",
            "With this on, Freya, Anubis, Terra and Chaac answer players' typed chat and whispers with the AI, each in their own way of talking: Freya calm " +
            "and precise, Anubis dry and darkly funny, Terra warm and cheerful, Chaac loud and full of energy. They answer only what is said to them (their " +
            "name, \"team\", or said close by), still turn down every ask (follow, help, trade, join) and never change their plans. The same safety check, " +
            "daily limit, AI hours, pause and per-player limits as Ultimus apply, and what they learn about the game is kept for offline chat too. " +
            "Pick one AI for the whole team, or a different one for each wizard.",
            SwitchRow("Quest Team AI chat", _ultQtAi,
                "Off by default. On: typed chat and whispers get AI answers, and Menu Chat gets a Menu Chat answer the AI picks from a safe list. " +
                "Whenever the AI is off, paused or can't be reached, they give their usual answers."),
            SwitchRow("One AI for the whole team", _qtAiOne,
                "On by default: all four use the team's AI below. Off: each wizard uses the AI picked for it (one left on \"Same as the team\" uses the team's)."),
            SwitchRow("Team's AI", Row(_qtAiProvider, _qtAiModel),
                "\"Same as Ultimus\" uses the AI picked at the top of this page. The model box can stay empty for the service's usual model."),
            new TextBlock { Text = "Each wizard's own AI (used when \"One AI for the whole team\" is off)", FontWeight = FontWeight.SemiBold,
                Margin = new Avalonia.Thickness(0, 8, 0, 0) },
            new StackPanel { Spacing = 6, Children = { rows[0], rows[1], rows[2], rows[3] } },
            SwitchRow("Save a key for", Row(_qtAiKeyFor, _qtAiKey, Action(Plain("Save key"), SaveQuestTeamAiKeyAsync)),
                "An online service needs its key once (the same keys Ultimus uses). It stays on this PC only, never in the logs, backups or uploads."),
            _qtAiState,
            SwitchRow("Quest Team AI answers per player per day", _qtAiPerPlayer,
                "Counted apart from Ultimus's, so chatting with the team doesn't use up a player's answers from Ultimus. 0 = no limit. Past it, the team " +
                "gives that player its usual answers until midnight."),
            SwitchRow("Team talks among themselves every (minutes)", _qtAiTalkMinutes,
                "Now and then, when two of them stand together out of battle, one says a short line to the other in their own way and the other may answer. " +
                "At most once in this many minutes; 0 = never. Each line uses one AI answer from the daily limit (the AI on this PC is free)."),
            new TextBlock { Text = "Compare their AIs", FontWeight = FontWeight.SemiBold, Margin = new Avalonia.Thickness(0, 8, 0, 0) },
            Row(_qtAiCompareQuestion, Action(Plain("Ask all four"), AskQuestTeamAiCompareAsync)),
            Sub("Asks Freya, Anubis, Terra and Chaac the same question, each with the AI picked for them, and shows their answers side by side with " +
                "the service, model and how long it took. Nothing is said in the game. The server must be running; works while the team is offline. " +
                "Online AI: counts 4 answers."),
            _qtAiCompareResult);
    }

    private static string QtAiProviderId(ComboBox box) => box.SelectedIndex <= 0 ? "" : UltimusAiProviders.All[box.SelectedIndex - 1].Id;

    private static string QtAiShortName(ComboBox box) => box.SelectedIndex <= 0 ? "" : UltimusAiProviders.All[box.SelectedIndex - 1].Name.Split(" (")[0];

    private static int QtAiIndex(string? id) {
        if (string.IsNullOrWhiteSpace(id)) {
            return 0;
        }

        var index = Array.FindIndex(UltimusAiProviders.All, p => p.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));
        return index < 0 ? 0 : index + 1;
    }

    private void LoadQuestTeamAiChat() {
        _showingQtAi = true;
        try {
            _qtAiOne.IsChecked = !string.Equals(_manager.GetSetting("QuestTeam", "AIOneModel"), "false", StringComparison.OrdinalIgnoreCase);
            _qtAiProvider.SelectedIndex = QtAiIndex(_manager.GetSetting("QuestTeam", "AIProvider"));
            _qtAiModel.Text = _manager.GetSetting("QuestTeam", "AIModel") ?? "";
            _qtAiPerPlayer.Text = _manager.GetSetting("QuestTeam", "AIPerPlayer") is { Length: > 0 } perPlayer ? perPlayer : "40";
            _qtAiTalkMinutes.Text = _manager.GetSetting("QuestTeam", "AITeamTalkMinutes") is { Length: > 0 } talk ? talk : "10";
            foreach (var (role, (provider, model)) in _qtAiPerMember) {
                provider.SelectedIndex = QtAiIndex(_manager.GetSetting("QuestTeam", "AIProvider" + role));
                model.Text = _manager.GetSetting("QuestTeam", "AIModel" + role) ?? "";
            }
        }
        finally {
            _showingQtAi = false;
        }

        ShowQuestTeamAiChat();
    }

    private Task SaveQuestTeamAiSettingAsync((string Key, string Value)[] changes, string said) {
        if (changes.FirstOrDefault(c => !_manager.HasSetting("QuestTeam", c.Key)) is { Key: { } missing }) {
            LoadQuestTeamAiChat();
            throw new PanelException($"This server's Imlight.ini has no [QuestTeam] {missing} yet. Click \"Update Ahziael\" once, then try again.");
        }

        _manager.SaveSettings([.. changes.Select(c => new Setting("QuestTeam", c.Key, c.Value, ""))]);
        _settings = [];
        LoadQuestTeamAiChat();
        Say(said);

        return Task.CompletedTask;
    }

    private Task SaveQuestTeamAiNumberAsync(string key, TextBox box, string what, Func<int, string> said) {
        var text = (box.Text ?? "").Trim();
        if (!int.TryParse(text, out var number) || number < 0) {
            LoadQuestTeamAiChat();
            throw new PanelException($"Type a whole number for the {what}, like 10 (0 = {(key == "AIPerPlayer" ? "no limit" : "never")}).");
        }

        return SaveQuestTeamAiSettingAsync([(key, number.ToString(System.Globalization.CultureInfo.InvariantCulture))], said(number));
    }

    private Task AskQuestTeamAiCompareAsync() {
        if (!_manager.Status().Running) {
            throw new PanelException("Start the server first: the server asks the AIs.");
        }

        var question = (_qtAiCompareQuestion.Text ?? "").Trim();
        if (question.Length == 0) {
            throw new PanelException("Type a question first, like \"Where do I get Tower Shield?\".");
        }

        System.IO.File.WriteAllText(System.IO.Path.Combine(_manager.ServerFolder, QT_AI_COMPARE_REQUEST), question);
        _qtAiCompareResult.Text = "Asking all four...";
        _qtAiCompareRead = DateTime.MinValue;

        return Task.CompletedTask;
    }

    // The latest "Ask all four" answers, read again when the server writes them (Ultimus page timer).
    private void ShowQuestTeamAiCompare() {
        try {
            var path = System.IO.Path.Combine(UltimusSharedFolder, QT_AI_COMPARE_RESULT);
            var changed = System.IO.File.Exists(path) ? System.IO.File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (changed != _qtAiCompareRead && changed != DateTime.MinValue) {
                _qtAiCompareRead = changed;
                _qtAiCompareResult.Text = System.IO.File.ReadAllText(path);
            }
        }
        catch (System.IO.IOException) {
            // Shown next round.
        }
    }

    private Task SaveQuestTeamAiKeyAsync() {
        var online = UltimusAiProviders.All.Where(p => !p.Local).ToList();
        if (_qtAiKeyFor.SelectedIndex < 0 || _qtAiKeyFor.SelectedIndex >= online.Count) {
            throw new PanelException("Pick the service the key is for first.");
        }

        var provider = online[_qtAiKeyFor.SelectedIndex];
        var key = (_qtAiKey.Text ?? "").Trim();
        if (key.Length < 16 || key.Any(char.IsWhiteSpace)) {
            throw new PanelException("That doesn't look like a whole key: it should be one long line of letters and numbers with no spaces. Copy it again from the key page.");
        }

        UltimusAiProviders.SaveKey(UltAiConfigFolder, provider.Id, key);
        _qtAiKey.Text = "";
        ShowQuestTeamAiChat();
        Say($"Saved the key for {provider.Name.Split(" (")[0]}. It stays on this PC only.");

        return Task.CompletedTask;
    }

    // Which AI each wizard answers with right now, and whether its key is saved (the server picks the same way).
    private void ShowQuestTeamAiChat() {
        var lines = new List<string>();
        var problem = false;
        if (!_manager.HasSetting("QuestTeam", "AIOneModel")) {
            lines.Add("Click \"Update Ahziael\" once to add the Quest Team AI chat settings to this server.");
            problem = true;
        }

        if (_ultQtAi.IsChecked != true) {
            lines.Add("Quest Team AI chat is off: they give their usual Menu Chat answers.");
        }

        var ultimus = UltimusAiProviders.Find(_manager.GetSetting("Ultimus", "AIProvider"));
        var ultimusModel = ultimus.Local
            ? _manager.GetSetting("Ultimus", "LocalAIModel") is { Length: > 0 } local ? local : ultimus.Model
            : _manager.GetSetting("Ultimus", "AIModel") is { Length: > 0 } online ? online : ultimus.Model;
        var one = _qtAiOne.IsChecked == true;
        var teamId = _manager.GetSetting("QuestTeam", "AIProvider") ?? "";
        var teamModel = _manager.GetSetting("QuestTeam", "AIModel") ?? "";
        foreach (var (role, name) in s_qtAiMembers) {
            var id = teamId;
            var model = teamModel;
            if (!one) {
                var own = _manager.GetSetting("QuestTeam", "AIProvider" + role) ?? "";
                var ownModel = _manager.GetSetting("QuestTeam", "AIModel" + role) ?? "";
                if (own.Trim().Length > 0) {
                    (id, model) = (own, ownModel);
                }
                else if (ownModel.Trim().Length > 0) {
                    model = ownModel;
                }
            }

            var provider = id.Trim().Length == 0 ? ultimus : UltimusAiProviders.Find(id);
            var shown = model.Trim().Length > 0 ? model.Trim()
                : provider.Id == ultimus.Id ? ultimusModel
                : provider.Local ? _manager.GetSetting("Ultimus", "LocalAIModel") is { Length: > 0 } lm ? lm : provider.Model
                : provider.Model;
            var keyOk = provider.Local || UltimusAiProviders.ReadKey(UltAiConfigFolder, provider.Id) is not null;
            problem |= !keyOk;
            lines.Add($"{role} {name}: {provider.Name.Split(" (")[0]}, model {shown}" + (keyOk ? "." : ". No key saved for it yet: save one above."));
        }

        _qtAiState.Text = string.Join("\n", lines);
        _qtAiState.Foreground = problem ? WARN : MUTED;
        foreach (var (provider, model) in _qtAiPerMember.Values) {
            provider.IsEnabled = !one;
            model.IsEnabled = !one;
        }
    }

}
