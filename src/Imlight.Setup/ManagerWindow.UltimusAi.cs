using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

// Ultimus's optional local AI for chat (Niduus, 2026-11-14, "Both honestly"; "Give a in-detail tutorial on how to link the
// LM to Ultimus in that section of the tab. Also giving recommendations"). [Ultimus] LocalAI, LocalAIUrl, LocalAIModel,
// all live. The keyword chat stays and answers whenever the AI is off, slow or down.
internal sealed partial class ManagerWindow {

    private readonly ToggleSwitch _ultAi = new() { OnContent = "On", OffContent = "Off" };
    private readonly TextBox _ultAiUrl = new() { Width = 320, Watermark = "http://localhost:11434/v1" };
    private readonly TextBox _ultAiModel = new() { Width = 220, Watermark = "llama3.2" };
    private readonly TextBlock _ultAiTest = new() { TextWrapping = TextWrapping.Wrap };
    private readonly SelectableTextBlock _ultAiScan = new() { TextWrapping = TextWrapping.Wrap };
    private bool _showingUltAi;

    private Control UltimusAiTab() => Page(UltimusAiFold());

    private Control UltimusAiFold() {
        _ultAi.IsCheckedChanged += (_, _) => {
            if (!_showingUltAi) {
                _ = Guard(() => SaveUltimusAiAsync("LocalAI", _ultAi.IsChecked == true ? "true" : "false",
                    _ultAi.IsChecked == true ? "Ultimus's AI chat is on. If the AI app isn't running, he answers with his normal chat." : "Ultimus's AI chat is off: he answers with his normal chat."));
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

        LoadUltimusAi();

        return Fold("users", ULTIMUS_TAB, "AI chat (optional, on your PC)",
            "Ultimus can use a language model running on your own PC to chat more naturally and keep up with a conversation. " +
            "Only his real facts (level, area, quest, step, health, his latest memories) and the talk with that player are given to it. " +
            "Private details are always blocked, whatever the AI says. It is never used for things that make him act (follow, potions, gold), " +
            "and when it is off, slow or not running he answers with his normal chat. Nothing goes to the internet: only this PC or your home network.",
            SwitchRow("AI chat", _ultAi, "Off by default. Changes count right away, no restart."),
            SwitchRow("AI address", _ultAiUrl, "Ollama: http://localhost:11434/v1   LM Studio: http://localhost:1234/v1"),
            SwitchRow("Model name", _ultAiModel, "Exactly as the AI app names it, for example llama3.2 or qwen2.5:7b."),
            Row(Action(Plain("Test connection"), TestUltimusAiAsync), Action(Plain("Scan my PC for a recommendation"), ScanForAiAsync)),
            _ultAiTest,
            _ultAiScan,
            AiGuideStep("Which app should I use?",
                "Ollama is the easiest: install it once and it runs quietly in the background. LM Studio has a window where you can browse and try models; pick it if you like seeing things. " +
                "Both are free and work offline. You only need one."),
            AiGuideStep("Which model fits my PC? (without scanning)",
                "Read this table and pick, or press \"Scan my PC for a recommendation\" above to have Ahziael pick for you. The scan only reads your memory and graphics card on this PC; nothing is sent anywhere.\n\n" +
                "Weak PC or laptop (8 GB memory, no gaming graphics card): llama3.2:1b (about 1.3 GB). Answers can be simple.\n" +
                "Normal PC (16 GB memory, or a graphics card with 4 to 6 GB): llama3.2 (the 3B model, about 2 GB). Recommended for most.\n" +
                "Gaming PC (graphics card with 8 GB or more): qwen2.5:7b or llama3.1:8b (about 4.5 to 5 GB). The best talk.\n" +
                "Very strong PC (16 GB or more on the graphics card): qwen2.5:14b (about 9 GB).\n" +
                "Downloads take a few minutes on fast internet (a 2 GB model is about 3 minutes at 100 Mbps). A bigger model talks better but answers slower and uses more of your PC while you play. " +
                "If replies come late or the game stutters, pick a smaller one."),
            AiGuideStep("Set it up with Ollama (step by step)",
                "1. In your web browser go to ollama.com and click Download, then Download for Windows.\n" +
                "2. Open the file you downloaded (OllamaSetup.exe) and click Install. When it finishes, a small llama icon sits by your clock: it is running.\n" +
                "3. Open the Start menu, type cmd and press Enter. A black window opens.\n" +
                "4. Type: ollama pull llama3.2   and press Enter. Wait until it says success (this is the download).\n" +
                "5. Close the black window. Ollama keeps running by itself, also after a restart of the PC.\n" +
                "6. Here: AI address http://localhost:11434/v1, Model name llama3.2. Click Test connection, then turn AI chat on."),
            AiGuideStep("Set it up with LM Studio (step by step)",
                "1. In your web browser go to lmstudio.ai and click Download for Windows. Open the file and install it.\n" +
                "2. Open LM Studio. Click the magnifying glass (Discover) on the left, search for Llama 3.2 3B Instruct and click Download.\n" +
                "3. Click the Developer tab (the green terminal icon) on the left. At the top, pick the model you downloaded so it loads.\n" +
                "4. Turn on Start Server (the switch at the top left of that tab). It says it is running on port 1234.\n" +
                "5. Copy the model's name shown there (for example llama-3.2-3b-instruct).\n" +
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
        var (model, size, why) = room >= 16 ? ("qwen2.5:14b", "about 9 GB", "your graphics card has plenty of memory")
            : room >= 8 ? ("qwen2.5:7b", "about 4.7 GB", "your graphics card has 8 GB or more")
            : room >= 4 || ramGb >= 15 ? ("llama3.2", "about 2 GB", room >= 4 ? "your graphics card has 4 GB or more" : "you have 16 GB of memory")
            : ("llama3.2:1b", "about 1.3 GB", "this PC has little memory to spare while the game runs");
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
            _ultAi.IsChecked = _manager.GetSwitch("Ultimus", "LocalAI");
            _ultAiUrl.Text = _manager.GetSetting("Ultimus", "LocalAIUrl") is { Length: > 0 } url ? url : "http://localhost:11434/v1";
            _ultAiModel.Text = _manager.GetSetting("Ultimus", "LocalAIModel") is { Length: > 0 } model ? model : "llama3.2";
        }
        finally {
            _showingUltAi = false;
        }
    }

    private Task SaveUltimusAiAsync(string key, string value, string said) {
        if (!_manager.HasSetting("Ultimus", "LocalAI")) {
            LoadUltimusAi();
            throw new PanelException("This server's Imlight.ini has no [Ultimus] LocalAI yet. Click \"Update Ahziael\" once, then try again.");
        }

        _manager.SaveSettings([new Setting("Ultimus", key, value, "")]);
        _settings = [];
        Say(said);

        return Task.CompletedTask;
    }

    // Asks the AI app for its models, then for one short answer, and says in plain words what worked.
    private async Task TestUltimusAiAsync() {
        var url = ((_ultAiUrl.Text ?? "").Trim().TrimEnd('/')) is { Length: > 0 } u ? u : "http://localhost:11434/v1";
        var model = (_ultAiModel.Text ?? "").Trim() is { Length: > 0 } m ? m : "llama3.2";
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
