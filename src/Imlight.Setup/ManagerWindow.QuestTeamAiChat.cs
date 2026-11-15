using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;

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
            _qtAiState);
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
