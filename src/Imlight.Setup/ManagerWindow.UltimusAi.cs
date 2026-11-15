using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Imlight.CoreLib.Game.Ultimus;

namespace Imlight.Setup;

// Ultimus's optional local AI for chat (Niduus, 2026-11-14, "Both honestly"; "Give a in-detail tutorial on how to link the
// LM to Ultimus in that section of the tab. Also giving recommendations"). [Ultimus] LocalAI, LocalAIUrl, LocalAIModel,
// all live. The keyword chat stays and answers whenever the AI is off, slow or down.
// Niduus, 2026-11-15: "allow for Ultimus to also have API keys for it's chat. Citing free and accurate ones for those who
// don't want to pay": a picker of AI services ([Ultimus] AIProvider, AIModel; UltimusAiProviders, shared with the server)
// with a plain cost note each, and a key box. The key is kept in Config\ultimus-ai-keys.json only, never shown again, never
// in Imlight.ini, a log, a log zip or a backup. The local AI stays the default. No Microsoft services.
internal sealed partial class ManagerWindow {

    private readonly ToggleSwitch _ultAi = new() { OnContent = "On", OffContent = "Off" };
    private readonly ToggleSwitch _ultAiStuck = new() { OnContent = "On", OffContent = "Off" };
    private readonly TextBox _ultAiUrl = new() { Width = 320, Watermark = "http://localhost:11434/v1" };
    private readonly TextBox _ultAiModel = new() { Width = 220, Watermark = "qwen3.5:4b" };
    private readonly TextBlock _ultAiTest = new() { TextWrapping = TextWrapping.Wrap };
    private readonly SelectableTextBlock _ultAiScan = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _ultAiProvider = new() { Width = 360, ItemsSource = UltimusAiProviders.All.Select(p => p.Name).ToList() };
    private readonly TextBox _ultAiKey = new() { Width = 320, PasswordChar = '•', Watermark = "Paste your key here" };
    private readonly TextBox _ultAiOnlineModel = new() { Width = 320 };
    private readonly SelectableTextBlock _ultAiCost = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _ultAiLocalRows = new() { Spacing = 8 };
    private readonly StackPanel _ultAiOnlineRows = new() { Spacing = 8 };
    private bool _showingUltAi;

    private Control UltimusAiTab() => Page(SpaceNotice("", "Notice: the local AI app (Ollama or LM Studio) and its model take up extra space on this computer (a few GB per model) " +
        "and use a lot of extra memory (RAM, or the graphics card's memory) and processor while they run. Ultimus also keeps his " +
        "own logs and notes, which grow while he plays. Close the AI app when you don't need it."), UltimusAiFold(), UltimusAiSafetyFold(), UltimusAiLearnedFold());

    private Control UltimusAiFold() {
        _ultAi.IsCheckedChanged += (_, _) => {
            if (!_showingUltAi) {
                _ = Guard(() => SaveUltimusAiAsync("LocalAI", _ultAi.IsChecked == true ? "true" : "false",
                    _ultAi.IsChecked == true ? "Ultimus's AI chat is on. If the AI app isn't running, he answers with his normal chat." : "Ultimus's AI chat is off: he answers with his normal chat."));
            }
        };
        _ultAiStuck.IsCheckedChanged += (_, _) => {
            if (!_showingUltAi) {
                _ = Guard(() => SaveUltimusAiAsync("StuckThinking", _ultAiStuck.IsChecked == true ? "true" : "false",
                    _ultAiStuck.IsChecked == true
                        ? "Ultimus thinks it through when he is stuck (with the AI app running), and always uses what worked before."
                        : "Stuck thinking is off: Ultimus uses only his usual fixes when he is stuck.", "StuckThinking"));
            }
        };
        _ultAiUrl.LostFocus += (_, _) => {
            if (!_showingUltAi) {
                _ = Guard(() => SaveUltimusAiAsync("LocalAIUrl", (_ultAiUrl.Text ?? "").Trim(), "Saved the AI address."));
            }
        };
        _ultAiModel.LostFocus += (_, _) => {
            if (!_showingUltAi) {
                _ = Guard(() => SaveUltimusAiAsync("LocalAIModel", (_ultAiModel.Text ?? "").Trim(), "Saved the AI model name."));
            }
        };

        _ultAiProvider.SelectionChanged += (_, _) => {
            if (!_showingUltAi && _ultAiProvider.SelectedIndex >= 0) {
                _ = Guard(SaveUltimusAiProviderAsync);
            }
        };
        _ultAiKey.LostFocus += (_, _) => {
            if (!_showingUltAi && !string.IsNullOrWhiteSpace(_ultAiKey.Text)) {
                _ = Guard(SaveUltimusAiKeyAsync);
            }
        };
        _ultAiOnlineModel.LostFocus += (_, _) => {
            if (!_showingUltAi) {
                _ = Guard(() => SaveUltimusAiAsync("AIModel", (_ultAiOnlineModel.Text ?? "").Trim(),
                    (_ultAiOnlineModel.Text ?? "").Trim().Length > 0 ? "Saved the model name." : "Ultimus uses the service's own default model.", "AIModel"));
            }
        };

        _ultAiLocalRows.Children.Add(SwitchRow("AI address", _ultAiUrl, "Ollama: http://localhost:11434/v1   LM Studio: http://localhost:1234/v1"));
        _ultAiLocalRows.Children.Add(SwitchRow("Model name", _ultAiModel, "Exactly as the AI app names it, for example qwen3.5:4b or gemma4:e4b."));
        _ultAiOnlineRows.Children.Add(SwitchRow("API key", _ultAiKey,
            "Paste the key and click anywhere else: it is saved on this PC only (Server\\Config\\ultimus-ai-keys.json) and never shown again, " +
            "never written in Imlight.ini, the logs, a log zip or a backup. Each service keeps its own key."));
        _ultAiOnlineRows.Children.Add(Row(
            Action(Plain("Get a key"), () => { OpenInBrowser(SelectedUltAiProvider().KeyPage); return Task.CompletedTask; }),
            Action(Plain("Save key"), SaveUltimusAiKeyAsync),
            Action(Plain("Forget key"), ForgetUltimusAiKeyAsync)));
        _ultAiOnlineRows.Children.Add(SwitchRow("Model name", _ultAiOnlineModel,
            "Leave it empty to use the service's default (shown greyed out). Click Test connection to see every model your key can use."));

        LoadUltimusAi();

        return Fold("users", ULTIMUS_TAB, "AI chat (optional)",
            "Ultimus can use an AI language model to chat more naturally and keep up with a conversation: one running on your own PC (free, the default), " +
            "or an online one with an API key (several have free tiers). " +
            "Only his real facts (level, area, quest, step, health, his latest memories) and the talk with that player are given to it. " +
            "A safety check stands around every AI: private details (server address, accounts, passwords, keys, emails, file paths, real names) are blanked out " +
            "before anything is sent, lines that try to trick the AI are never sent, and any answer that leaks something is thrown away. " +
            "In chat it is never used for things that make him act (follow, potions, gold), and when it is off, slow or not running he answers with his normal chat.",
            SwitchRow("AI chat", _ultAi, "Off by default. Changes count right away, no restart."),
            SwitchRow("Think it through when stuck", _ultAiStuck,
                "On by default. When Ultimus is stuck on the same step again, he first does what worked there before; if nothing has, the AI " +
                "guesses why and picks one of his normal unstuck moves (step aside, walk away, another route, Commons, fight what's around, " +
                "click what's offered, or set the step aside). He never skips a step. His reasoning shows in Ultimus Thoughts and his log. " +
                "Uses the address and model below; the AI chat switch can stay off. What worked is shared with the Quest Team."),
            SwitchRow("Which AI", _ultAiProvider, "On this PC is the default. The others run online and need a key. Changes count right away."),
            _ultAiCost,
            _ultAiLocalRows,
            _ultAiOnlineRows,
            Row(Action(Plain("Test connection"), TestUltimusAiAsync), Action(Plain("Scan my PC for a recommendation"), ScanForAiAsync)),
            _ultAiTest,
            _ultAiScan,
            AiGuideStep("Which online AI should I pick? (free and paid)",
                "If you don't want to pay: Google Gemini, Groq, Cerebras and Mistral have free tiers, and OpenRouter has free models (their names end in :free). " +
                "None of them needs a card for the free use. The free limits are per minute and per day; when one is reached, Ultimus simply uses his normal chat " +
                "until it resets. Free limits change often, so each service's own page shows your current ones.\n\n" +
                "Good free picks: Groq or Cerebras for the fastest answers, Google Gemini for good answers. " +
                "Note: on free tiers Google and Mistral may use what is sent to improve their models, and some OpenRouter free models may keep it too. " +
                "Only Ultimus's facts and the chat lines go out, with private details blanked out first.\n\n" +
                "Paid (pay per use, you buy credit first): Anthropic Claude and OpenAI. A chat line costs a tiny fraction of a cent with their small models. " +
                "They don't train on what is sent through a key by default.\n\n" +
                "Want nothing to leave your PC at all? Keep \"On this PC\" and use Ollama or LM Studio (free)."),
            AiGuideStep("Use an online AI with a key (step by step)",
                "1. In \"Which AI\" above, pick the service you want.\n" +
                "2. Click \"Get a key\". Your web browser opens the service's key page. Sign up or sign in there.\n" +
                "3. On that page click Create key (or Create API key). Copy the key it shows (it is a long line of letters and numbers).\n" +
                "4. Back here, click in the API key box, paste the key (Ctrl+V), then click \"Save key\". The box empties: that is right, the key is saved and hidden.\n" +
                "5. Leave Model name empty for the default, then click Test connection. It should say Works!\n" +
                "6. Turn AI chat on. Think it through when stuck uses the same AI.\n" +
                "Treat a key like a password: never post it or show it on stream. If one gets out, delete it on the service's key page and make a new one."),
            AiGuideStep("Which app should I use?",
                "Ollama is the easiest: install it once and it runs quietly in the background. LM Studio has a window where you can browse and try models; pick it if you like seeing things. " +
                "Both are free and work offline. You only need one."),
            AiGuideStep("Which model fits my PC? (without scanning)",
                "Read this table and pick, or press \"Scan my PC for a recommendation\" above to have Ahziael pick for you. The scan only reads your memory and graphics card on this PC; nothing is sent anywhere.\n\n" +
                "These are 2026 models (Qwen 3.5 from Alibaba, Gemma 4 from Google). Update Ollama first if it says it can't load them.\n" +
                "Weak PC or laptop (8 GB memory, no gaming graphics card): qwen3.5:0.8b (about 1 GB). Answers can be simple.\n" +
                "Normal PC (16 GB memory, or a graphics card with 4 to 6 GB): qwen3.5:4b (about 3.5 GB). Recommended for most.\n" +
                "Gaming PC (graphics card with 8 GB or more): qwen3.5:9b (about 6.5 GB) or gemma4:e4b (about 7 GB). The best talk.\n" +
                "Very strong PC (20 GB or more on the graphics card): gemma4:26b (a big download, well over 10 GB). Fast for its size.\n" +
                "Downloads take a few minutes on fast internet (a 3.5 GB model is about 5 minutes at 100 Mbps). A bigger model talks better but answers slower and uses more of your PC while you play. " +
                "If replies come late or the game stutters, pick a smaller one."),
            AiGuideStep("Set it up with Ollama (step by step)",
                "1. In your web browser go to ollama.com and click Download, then Download for Windows.\n" +
                "2. Open the file you downloaded (OllamaSetup.exe) and click Install. When it finishes, a small llama icon sits by your clock: it is running.\n" +
                "3. Open the Start menu, type cmd and press Enter. A black window opens.\n" +
                "4. Type: ollama pull qwen3.5:4b   and press Enter. Wait until it says success (this is the download).\n" +
                "5. Close the black window. Ollama keeps running by itself, also after a restart of the PC.\n" +
                "6. Here: AI address http://localhost:11434/v1, Model name qwen3.5:4b. Click Test connection, then turn AI chat on."),
            AiGuideStep("Set it up with LM Studio (step by step)",
                "1. In your web browser go to lmstudio.ai and click Download for Windows. Open the file and install it.\n" +
                "2. Open LM Studio. Click the magnifying glass (Discover) on the left, search for Qwen3.5 4B and click Download.\n" +
                "3. Click the Developer tab (the green terminal icon) on the left. At the top, pick the model you downloaded so it loads.\n" +
                "4. Turn on Start Server (the switch at the top left of that tab). It says it is running on port 1234.\n" +
                "5. Copy the model's name shown there (for example qwen3.5-4b).\n" +
                "6. Here: AI address http://localhost:1234/v1, Model name: paste it. Click Test connection, then turn AI chat on.\n" +
                "LM Studio must be open with the server started whenever you want the AI chat."),
            AiGuideStep("If the test fails",
                "\"Nothing answers\": the AI app isn't running. Start Ollama from the Start menu, or open LM Studio and start its server.\n" +
                "\"The model isn't there\": the name doesn't match. Click Test connection: the names it has are listed; copy one into Model name.\n" +
                "\"Not on this PC\": the address must be this PC (localhost) or your home network, never an internet address."));
    }

    // "Scan my PC" (only when pressed): the PC's memory and graphics card, read here, to pick an app and a model. Nothing leaves the PC.
    private Task ScanForAiAsync() {
        var ramGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024 / 1024;
        var (gpu, vramGb) = GraphicsCard();
        var room = Math.Max(vramGb, 0);
        // 2026 models (Niduus, 2026-11-15: "recommend 2026 models not models from 2024"): Qwen 3.5 and Gemma 4, by room.
        var (model, size, why) = room >= 20 ? ("gemma4:26b", "a big download, well over 10 GB", "your graphics card has plenty of memory")
            : room >= 8 ? ("qwen3.5:9b", "about 6.5 GB", "your graphics card has 8 GB or more")
            : room >= 4 || ramGb >= 15 ? ("qwen3.5:4b", "about 3.5 GB", room >= 4 ? "your graphics card has 4 GB or more" : "you have 16 GB of memory")
            : ("qwen3.5:0.8b", "about 1 GB", "this PC has little memory to spare while the game runs");
        _ultAiScan.Text = $"Scan of this PC: {ramGb:0} GB memory, graphics card {(gpu.Length > 0 ? gpu : "not found")}{(vramGb > 0 ? $" with {vramGb:0} GB" : "")}.\n" +
                          $"Recommended: Ollama with the model {model} ({size}), because {why}. In the black window type: ollama pull {model}\n" +
                          "Then put that name in Model name above and click Test connection. Nothing from this scan was sent anywhere.";
        return Task.CompletedTask;
    }

    // The biggest graphics card's name and memory, from Windows's own driver list (no extra programs).
    private static (string Name, double Gb) GraphicsCard() {
        if (!OperatingSystem.IsWindows()) {
            return ("", 0);
        }

        try {
            using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            var best = ("", 0d);
            foreach (var sub in cls?.GetSubKeyNames() ?? []) {
                if (!sub.All(char.IsDigit)) {
                    continue;
                }

                using var key = cls!.OpenSubKey(sub);
                var name = key?.GetValue("DriverDesc") as string ?? "";
                var raw = key?.GetValue("HardwareInformation.qwMemorySize") ?? key?.GetValue("HardwareInformation.MemorySize");
                double bytes = raw switch {
                    long l => l, int i => (uint) i, byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0), byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0), _ => 0,
                };
                var gb = bytes / 1024 / 1024 / 1024;
                if (gb > best.Item2 || best.Item1.Length == 0) {
                    best = (name, gb);
                }
            }

            return best;
        }
        catch (Exception) {
            return ("", 0);
        }
    }

    private static Control AiGuideStep(string title, string text) => new Expander {
        Header = title, HorizontalAlignment = HorizontalAlignment.Stretch,
        Content = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 2) },
    };

    private void LoadUltimusAi() {
        _showingUltAi = true;
        try {
            var provider = UltimusAiProviders.Find(_manager.GetSetting("Ultimus", "AIProvider"));
            _ultAiProvider.SelectedIndex = Array.IndexOf(UltimusAiProviders.All, provider);
            _ultAiOnlineModel.Text = _manager.GetSetting("Ultimus", "AIModel") ?? "";
            ShowUltimusAiProvider(provider);
            _ultAi.IsChecked = _manager.GetSwitch("Ultimus", "LocalAI");
            _ultAiStuck.IsChecked = !_manager.HasSetting("Ultimus", "StuckThinking") || _manager.GetSwitch("Ultimus", "StuckThinking");
            _ultAiUrl.Text = _manager.GetSetting("Ultimus", "LocalAIUrl") is { Length: > 0 } url ? url : "http://localhost:11434/v1";
            _ultAiModel.Text = _manager.GetSetting("Ultimus", "LocalAIModel") is { Length: > 0 } model ? model : "qwen3.5:4b";
        }
        finally {
            _showingUltAi = false;
        }
    }

    private Task SaveUltimusAiAsync(string key, string value, string said, string needs = "LocalAI") {
        if (!_manager.HasSetting("Ultimus", needs)) {
            LoadUltimusAi();
            throw new PanelException($"This server's Imlight.ini has no [Ultimus] {needs} yet. Click \"Update Ahziael\" once, then try again.");
        }

        _manager.SaveSettings([new Setting("Ultimus", key, value, "")]);
        _settings = [];
        Say(said);

        return Task.CompletedTask;
    }

    private UltimusAiProvider SelectedUltAiProvider()
        => _ultAiProvider.SelectedIndex >= 0 && _ultAiProvider.SelectedIndex < UltimusAiProviders.All.Length
            ? UltimusAiProviders.All[_ultAiProvider.SelectedIndex]
            : UltimusAiProviders.All[0];

    private string UltAiConfigFolder => Path.GetDirectoryName(_manager.ConfigFile) ?? _manager.ServerFolder;

    // The rows and the cost note for the picked service.
    private void ShowUltimusAiProvider(UltimusAiProvider provider) {
        _ultAiLocalRows.IsVisible = provider.Local;
        _ultAiOnlineRows.IsVisible = !provider.Local;
        var name = provider.Name.Split(" (")[0];
        _ultAiCost.Text = (provider.Free ? "Free to use. " : "Paid. ") + provider.Cost +
                          (provider.Local ? "" : $"\nWith {name}, Ultimus's facts and the chat lines are sent to {name} over the internet, with private details blanked out first.");
        var hasKey = !provider.Local && UltimusAiProviders.ReadKey(UltAiConfigFolder, provider.Id) is not null;
        _ultAiKey.Text = "";
        _ultAiKey.Watermark = hasKey ? "A key is saved (hidden). Paste a new one to replace it." : "Paste your key here";
        _ultAiOnlineModel.Watermark = provider.Model;
    }

    // A new service: saved with an empty model name, so its own default model is used.
    private Task SaveUltimusAiProviderAsync() {
        var provider = SelectedUltAiProvider();
        if (!_manager.HasSetting("Ultimus", "AIProvider")) {
            LoadUltimusAi();
            throw new PanelException("This server's Imlight.ini has no [Ultimus] AIProvider yet. Click \"Update Ahziael\" once, then try again.");
        }

        _manager.SaveSettings([new Setting("Ultimus", "AIProvider", provider.Id, ""), new Setting("Ultimus", "AIModel", "", "")]);
        _settings = [];
        _showingUltAi = true;
        try {
            _ultAiOnlineModel.Text = "";
            ShowUltimusAiProvider(provider);
        }
        finally {
            _showingUltAi = false;
        }

        var hasKey = provider.Local || UltimusAiProviders.ReadKey(UltAiConfigFolder, provider.Id) is not null;
        Say(provider.Local
            ? "Ultimus uses the AI on this PC (Ollama or LM Studio)."
            : $"Ultimus uses {provider.Name.Split(" (")[0]}." + (hasKey ? " Its saved key is used." : " Click \"Get a key\", paste the key and click Save key."));

        return Task.CompletedTask;
    }

    // The pasted key, saved beside Imlight.ini only. It is never shown again and never written to a log.
    private Task SaveUltimusAiKeyAsync() {
        var provider = SelectedUltAiProvider();
        var key = (_ultAiKey.Text ?? "").Trim();
        if (provider.Local) {
            return Task.CompletedTask;
        }

        if (key.Length == 0) {
            // Clicking Save key right after pasting: leaving the box already saved it.
            if (UltimusAiProviders.ReadKey(UltAiConfigFolder, provider.Id) is not null) {
                Say($"The key for {provider.Name.Split(" (")[0]} is saved. Click Test connection to check it.");
                return Task.CompletedTask;
            }

            throw new PanelException("Paste the key in the API key box first.");
        }

        if (key.Length < 16 || key.Any(char.IsWhiteSpace)) {
            throw new PanelException("That doesn't look like a whole key: it should be one long line of letters and numbers with no spaces. Copy it again from the key page.");
        }

        UltimusAiProviders.SaveKey(UltAiConfigFolder, provider.Id, key);
        _ultAiKey.Text = "";
        _ultAiKey.Watermark = "A key is saved (hidden). Paste a new one to replace it.";
        Say($"Saved the key for {provider.Name.Split(" (")[0]}. It stays on this PC only. Click Test connection to check it.");

        return Task.CompletedTask;
    }

    private Task ForgetUltimusAiKeyAsync() {
        var provider = SelectedUltAiProvider();
        if (!provider.Local) {
            UltimusAiProviders.SaveKey(UltAiConfigFolder, provider.Id, null);
            _ultAiKey.Text = "";
            _ultAiKey.Watermark = "Paste your key here";
            Say($"Forgot the key for {provider.Name.Split(" (")[0]}. Ultimus uses his normal chat until a key is saved.");
        }

        return Task.CompletedTask;
    }

    // An online service: its models (when it lists them), then one short answer, in plain words. The key is never shown.
    private async Task TestOnlineUltimusAiAsync(UltimusAiProvider provider) {
        var key = UltimusAiProviders.ReadKey(UltAiConfigFolder, provider.Id);
        var name = provider.Name.Split(" (")[0];
        if (key is null) {
            _ultAiTest.Text = $"No key saved for {name} yet. Click \"Get a key\", paste the key in the API key box and click Save key.";
            return;
        }

        if (UltimusAiProviders.Blocked(provider.Url)) {
            _ultAiTest.Text = "That address is not allowed.";
            return;
        }

        var model = (_ultAiOnlineModel.Text ?? "").Trim() is { Length: > 0 } m ? m : provider.Model;
        _ultAiTest.Text = "Testing...";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + key);
        if (provider.Id == "anthropic") {
            http.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", key);
            http.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }

        string[] names = [];
        try {
            using var list = await http.GetAsync(provider.Url + "/models");
            if ((int) list.StatusCode is 401 or 403) {
                _ultAiTest.Text = $"{name} did not accept the key. Copy it again from the key page (the whole line), paste it and click Save key.";
                return;
            }

            if (list.IsSuccessStatusCode) {
                var json = JsonNode.Parse(await list.Content.ReadAsStringAsync());
                names = (json?["data"] as JsonArray ?? []).Select(n => ((string?) n?["id"] ?? "").Replace("models/", "")).Where(n => n.Length > 0)
                    .OrderBy(n => n.EndsWith(":free", StringComparison.Ordinal) ? 0 : 1).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }
        catch (Exception) {
            // Not every service lists its models; the answer below still tells.
        }

        if (names.Length > 0 && !names.Any(n => n.Equals(model, StringComparison.OrdinalIgnoreCase))) {
            _ultAiTest.Text = $"The key works, but {name} has no model \"{model}\" for it. Some it has: {string.Join(", ", names.Take(30))}" +
                              (names.Length > 30 ? $" (and {names.Length - 30} more)" : "") + ". Copy one into Model name.";
            return;
        }

        try {
            var started = DateTime.UtcNow;
            var body = new JsonObject {
                ["model"] = model, ["max_tokens"] = 40, ["stream"] = false,
                ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "Say hello to a Wizard101 player in one short sentence." } },
            };
            using var response = await http.PostAsync(provider.Url + "/chat/completions", new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
            var code = (int) response.StatusCode;
            var text = "";
            try {
                text = (string?) JsonNode.Parse(await response.Content.ReadAsStringAsync())?["choices"]?[0]?["message"]?["content"] ?? "";
            }
            catch (Exception) {
                text = "";
            }

            var seconds = (DateTime.UtcNow - started).TotalSeconds;
            _ultAiTest.Text = response.IsSuccessStatusCode && text.Trim().Length > 0
                ? $"Works! {name} answered in {seconds:0.0} seconds: \"{text.Trim()}\"" + (seconds > 8 ? " That is slow for chat; a smaller model would answer faster." : "")
                : code switch {
                    401 or 403 => $"{name} did not accept the key. Copy it again from the key page (the whole line), paste it and click Save key.",
                    402 => $"{name} needs credit on your account first (this one is paid), or pick a free one in Which AI.",
                    404 => $"{name} has no model \"{model}\". Leave Model name empty for the default, or copy a name from the service's model list.",
                    429 => $"The key works, but {name}'s limit is reached for now (free tiers have limits per minute and per day). Try again later.",
                    _ when response.IsSuccessStatusCode => $"{name} answered, but with no words. Try another model.",
                    _ => $"{name} answered with an error ({code}). Check the model name, or try again in a minute.",
                };
        }
        catch (Exception) {
            _ultAiTest.Text = $"{name} could not be reached or took too long. Check your internet connection and try again.";
        }
    }

    // Asks the AI app for its models, then for one short answer, and says in plain words what worked.
    private async Task TestUltimusAiAsync() {
        if (SelectedUltAiProvider() is { Local: false } online) {
            await TestOnlineUltimusAiAsync(online);
            return;
        }

        var url = ((_ultAiUrl.Text ?? "").Trim().TrimEnd('/')) is { Length: > 0 } u ? u : "http://localhost:11434/v1";
        var model = (_ultAiModel.Text ?? "").Trim() is { Length: > 0 } m ? m : "qwen3.5:4b";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !(uri.IsLoopback || uri.Host.StartsWith("192.168.", StringComparison.Ordinal)
                || uri.Host.StartsWith("10.", StringComparison.Ordinal) || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))) {
            _ultAiTest.Text = "Not on this PC: the address must start with http://localhost (or a home network address like http://192.168...).";
            return;
        }

        _ultAiTest.Text = "Testing...";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        string[] names;
        try {
            var json = JsonNode.Parse(await http.GetStringAsync(url + "/models"));
            names = (json?["data"] as JsonArray ?? []).Select(n => (string?) n?["id"] ?? "").Where(n => n.Length > 0).ToArray();
        }
        catch (Exception) {
            _ultAiTest.Text = $"Nothing answers at {url}. Is Ollama running (llama icon by the clock), or is LM Studio open with its server started?";
            return;
        }

        if (names.Length == 0) {
            _ultAiTest.Text = "The AI app answers but has no models yet. Download one first (see the steps below).";
            return;
        }

        if (!names.Any(n => n.Equals(model, StringComparison.OrdinalIgnoreCase) || n.Equals(model + ":latest", StringComparison.OrdinalIgnoreCase))) {
            _ultAiTest.Text = $"The AI app answers, but the model \"{model}\" isn't there. It has: {string.Join(", ", names)}. Copy one into Model name.";
            return;
        }

        try {
            var started = DateTime.UtcNow;
            var body = new JsonObject {
                ["model"] = model, ["max_tokens"] = 30, ["stream"] = false,
                ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "Say hello to a Wizard101 player in one short sentence." } },
            };
            var response = await http.PostAsync(url + "/chat/completions", new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
            var text = (string?) JsonNode.Parse(await response.Content.ReadAsStringAsync())?["choices"]?[0]?["message"]?["content"] ?? "";
            var seconds = (DateTime.UtcNow - started).TotalSeconds;
            _ultAiTest.Text = response.IsSuccessStatusCode && text.Length > 0
                ? $"Works! The model answered in {seconds:0.0} seconds: \"{text.Trim()}\"" + (seconds > 8 ? " That is slow for chat; a smaller model would answer faster." : "")
                : "The AI app answered with an error. Try loading the model again in the app, or pick a smaller model.";
        }
        catch (Exception) {
            _ultAiTest.Text = "The model took too long or stopped. The first answer after loading can take a while: click Test connection again. If it keeps failing, pick a smaller model.";
        }
    }

}
