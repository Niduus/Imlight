#if !HELLHOUND
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// Server > Ahziael > Claude (Niduus, 2026-11-24: "Is there any way to do MCP with Ahziael since it does hook into the client?"):
/// Ahziael's own Claude connector (<see cref="ClaudeServer"/>, tools in <see cref="ClaudeTools"/>). Claude sees what
/// Ahziael sees: the server, who is online and where, its logs, the game's own log, the game windows on this PC (and
/// pictures of them) and what the overlay's minimap and Quest Tracker are told. Off by default, this PC only, and every
/// switch works at once. It never reads the game's memory or changes its files, and no key or password goes through it.
/// </summary>
internal sealed partial class ManagerWindow {

    private ClaudeServer? _claude;
    private ClaudeConnectorSettings? _claudeSettings;
    private readonly TextBlock _claudeState = new() { Foreground = MUTED, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _claudeActivity = new() { Foreground = MUTED, TextWrapping = TextWrapping.Wrap, FontFamily = MONO, FontSize = 12 };
    private readonly List<string> _claudeHeard = [];

    private ClaudeConnectorSettings ClaudeChoices => _claudeSettings ??= ClaudeConnectorSettings.Load(_manager.InstallFolder);

    private Control ClaudeCard() {
        var choices = ClaudeChoices;
        var on = new ToggleSwitch { OnContent = "On", OffContent = "Off", IsChecked = choices.On };
        var changes = new ToggleSwitch { OnContent = "On", OffContent = "Off", IsChecked = choices.Changes };
        var port = new NumericUpDown { Minimum = 1025, Maximum = 65535, Increment = 1, Value = choices.UsablePort, FormatString = "0", Width = 150 };
        var line = Sub(ClaudeSetup.ClaudeCodeLine(choices.UsablePort));

        on.IsCheckedChanged += (_, _) => _ = Guard(() => {
            ClaudeChoices.On = on.IsChecked == true;
            ClaudeChoices.Save(_manager.InstallFolder);
            ApplyClaude();
            Say(ClaudeChoices.On ? "Claude can use Ahziael now, on this PC only." : "Claude can't use Ahziael any more.");
            return Task.CompletedTask;
        });
        changes.IsCheckedChanged += (_, _) => _ = Guard(() => {
            ClaudeChoices.Changes = changes.IsChecked == true;
            ClaudeChoices.Save(_manager.InstallFolder);
            ShowClaudeState();
            Say(ClaudeChoices.Changes
                ? "Claude may run commands, announce and change settings now, while the connector is on."
                : "Claude can only look now.");
            return Task.CompletedTask;
        });
        port.ValueChanged += (_, _) => _ = Guard(() => {
            if (port.Value is not { } v || (int) v == ClaudeChoices.Port) {
                return Task.CompletedTask;
            }

            ClaudeChoices.Port = (int) v;
            ClaudeChoices.Save(_manager.InstallFolder);
            line.Text = ClaudeSetup.ClaudeCodeLine(ClaudeChoices.UsablePort);
            ApplyClaude();
            return Task.CompletedTask;
        });

        ShowClaudeState();
        ShowClaudeActivity();

        return Shut(Fold("link", "Settings", "Claude",
            "Let Claude look at Ahziael, the server and the Wizard101 windows on this PC through Ahziael's own connector. Off until you turn it on.",
            SwitchRow("Let Claude use Ahziael", on,
                "Claude can see the server's state, who is online and where, the server's and the game's logs, Ahziael's window, and take pictures of the game windows. Only programs on this PC can reach it."),
            SwitchRow("Let Claude make changes too", changes,
                "Claude may also run chat commands as your wizard, send announcements and change server settings. Off: Claude can only look."),
            _claudeState,
            WithHint(Row(
                    Action(Primary("Add Ahziael to Claude desktop"), async () => {
                        var folder = _manager.InstallFolder;
                        Say(await Task.Run(() => ClaudeSetup.AddToClaudeDesktop(folder)));
                    }),
                    Action(Plain("Copy the Claude Code line"), () => CopyAsync(ClaudeSetup.ClaudeCodeLine(ClaudeChoices.UsablePort)))),
                "\"Add Ahziael to Claude desktop\" puts Ahziael in Claude desktop's connectors (a copy of its settings file is kept as it was). "
                + "Quit Claude desktop fully and open it again after. Claude Code uses the line below instead."),
            line,
            SwitchRow("Port on this PC", port, "Only change it if another program already uses this port. It works at once."),
            Sub("What Claude did last (kept only while Ahziael is open):"),
            _claudeActivity,
            Sub("Ahziael never reads the game's memory or changes Wizard101's files: Claude sees the game windows the way a screenshot does, "
                + "and everything else comes from your server and the logs. API keys and passwords are never sent, and addresses of other PCs are blanked.")));
    }

    /// <summary>Starts or stops the connector to match the Claude card; called when Ahziael opens and on every change.</summary>
    private void ApplyClaude() {
        var choices = ClaudeChoices;
        var port = choices.UsablePort;
        if (choices.On && (_claude is null || _claude.Port != port)) {
            _claude?.Dispose();
            _claude = null;
            try {
                _claude = new ClaudeServer(port,
                    (name, args) => Dispatcher.UIThread.InvokeAsync(() => RunClaudeTool(name, args)),
                    said => Dispatcher.UIThread.Post(() => HeardClaude(said)));
            }
            catch (Exception ex) {
                _claude = null;
                ShowClaudeState($"Couldn't start on port {port}: {ex.Message}. Pick another port below.");
                return;
            }
        }
        else if (!choices.On && _claude is not null) {
            _claude.Dispose();
            _claude = null;
        }

        ShowClaudeState();
    }

    private void StopClaude() {
        _claude?.Dispose();
        _claude = null;
    }

    private void ShowClaudeState(string? problem = null) {
        _claudeState.Foreground = problem is null && _claude is not null ? GOOD : MUTED;
        _claudeState.Text = problem ?? (_claude is { } c
            ? $"● On, at {c.Address} (this PC only). " + (ClaudeChoices.Changes ? "Claude may make changes." : "Claude can only look.")
            : "● Off.");
    }

    private void HeardClaude(string said) {
        var line = $"{DateTime.Now:HH:mm:ss}  {(said.Length > 160 ? said[..160] + "…" : said)}";
        _claudeHeard.Insert(0, line);
        if (_claudeHeard.Count > 12) {
            _claudeHeard.RemoveAt(_claudeHeard.Count - 1);
        }

        ShowClaudeActivity();
    }

    private void ShowClaudeActivity()
        => _claudeActivity.Text = _claudeHeard.Count == 0 ? "Nothing yet." : string.Join("\n", _claudeHeard);

    // ---------------------------------------------------------------------
    // The tools
    // ---------------------------------------------------------------------

    private static string ClaudeN(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static double? ClaudeNum(JsonObject a, string name) {
        try {
            return a[name] is { } v ? v.GetValue<double>() : null;
        }
        catch (Exception) {
            return a[name]?.ToString() is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        }
    }

    private static string? ClaudeStr(JsonObject a, string name) {
        try {
            return a[name]?.GetValue<string>();
        }
        catch (Exception) {
            return a[name]?.ToString();
        }
    }

    private static bool ClaudeFlag(JsonObject a, string name) {
        try {
            return a[name]?.GetValue<bool>() == true;
        }
        catch (Exception) {
            return string.Equals(a[name]?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static JsonObject ClaudeSaid(string text, bool error = false) => ClaudeTools.Text(AppLogs.Scrub(text), error);

    private async Task<JsonObject> RunClaudeTool(string name, JsonObject a) {
        var tool = ClaudeTools.All.First(t => t.Name == name);
        if (tool.Changes && !ClaudeChoices.Changes) {
            return ClaudeSaid("Changes are off. Niduus can allow them in Ahziael: Server > Ahziael > Claude > \"Let Claude make changes too\".", error: true);
        }

        if (tool.Changes && IsRemote) {
            return ClaudeSaid("Ahziael is managing another PC's server right now; Claude's changes only work on this PC's own server.", error: true);
        }

        try {
            return await ClaudeTool(name, a);
        }
        catch (PanelException ex) {
            return ClaudeSaid(ex.Message, error: true);
        }
    }

    private async Task<JsonObject> ClaudeTool(string name, JsonObject a) {
        var manager = _manager;
        switch (name) {
            case "get_status":
                return ClaudeSaid(await ClaudeStatusAsync());
            case "list_players":
                return ClaudeSaid(ClaudePlayers());
            case "get_positions": {
                var file = PositionFile;
                var lines = await Task.Run(() => File.Exists(file) ? File.ReadAllLines(file) : []);
                return ClaudeSaid(ClaudePositions(lines));
            }
            case "list_game_windows": {
                var windows = await Task.Run(ClaudeGameWindows.List);
                return ClaudeSaid(windows.Count == 0
                    ? "No Wizard101 window is open on this PC."
                    : string.Join("\n", windows.Select(w =>
                        $"#{w.Number} \"{w.Title}\" (process {w.ProcessId}): " + (w.Minimized
                            ? "minimized"
                            : $"inside {w.Width} x {w.Height} pixels at ({w.X}, {w.Y}) on the screen, scale {ClaudeN(w.Scale)}"))));
            }
            case "take_game_picture": {
                var window = await ClaudeWindowAsync(a);
                var withOverlay = ClaudeFlag(a, "with_overlay");
                var widest = (int) (ClaudeNum(a, "max_width") ?? 1280);
                var (png, w, h, how) = await Task.Run(() => ClaudeGameWindows.Picture(window, withOverlay, widest));
                return ClaudeTools.Picture(png, $"Game window #{window.Number} \"{window.Title}\", {w} x {h} pixels (its inside is {window.Width} x {window.Height}), {how}.");
            }
            case "take_ahziael_picture": {
                var (png, w, h) = PictureOfAhziael((int) (ClaudeNum(a, "max_width") ?? 1280));
                return ClaudeTools.Picture(png, $"Ahziael's window, {w} x {h} pixels.");
            }
            case "get_minimap": {
                var window = await ClaudeWindowAsync(a);
                var limit = Math.Clamp((int) (ClaudeNum(a, "limit") ?? 80), 1, 500);
                return ClaudeSaid(ClaudeMinimap(window, limit));
            }
            case "get_quest_tracker": {
                var window = await ClaudeWindowAsync(a);
                return ClaudeSaid(ClaudeQuests(window));
            }
            case "read_server_log": {
                var count = Math.Clamp((int) (ClaudeNum(a, "lines") ?? 80), 1, 500);
                var words = SearchText.Words(ClaudeStr(a, "search"));
                var level = (ClaudeStr(a, "level") ?? "").Trim();
                var lines = await Task.Run(() => manager.ReadLog());
                var shown = lines.Where(l => (level.Length == 0 || l.Level.Equals(level, StringComparison.OrdinalIgnoreCase))
                                             && (words.Length == 0 || words.All(SearchText.Normalize(l.Text).Contains)))
                    .TakeLast(count).Select(l => l.Count > 1 ? $"{l.Text}  (x{l.Count})" : l.Text).ToList();
                return ClaudeSaid(shown.Count == 0 ? (lines.Count == 0 ? "The server hasn't written a log yet." : "No log line matches.") : string.Join("\n", shown));
            }
            case "read_game_log": {
                var count = Math.Clamp((int) (ClaudeNum(a, "lines") ?? 80), 1, 500);
                var words = SearchText.Words(ClaudeStr(a, "search"));
                var area = (ClaudeStr(a, "area") ?? "").Trim();
                var folder = manager.InstallFolder;
                var log = await Task.Run(() => GameClient.ReadClientLog(folder));
                if (log.File is null) {
                    return ClaudeSaid(log.Reason ?? "Wizard101 hasn't written a log yet. Play once first.", error: true);
                }

                var shown = log.Text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0
                        && (area.Length == 0 || InGameArea(l, area))
                        && (words.Length == 0 || words.All(SearchText.Normalize(l).Contains)))
                    .TakeLast(count).ToList();
                return ClaudeSaid(shown.Count == 0 ? "No line of the game log matches." : string.Join("\n", shown));
            }
            case "read_ahziael_events": {
                var count = Math.Clamp((int) (ClaudeNum(a, "count") ?? 20), 1, 200);
                var folder = manager.InstallFolder;
                var events = await Task.Run(() => AppLogs.ReadEvents(folder));
                return ClaudeSaid(events.Count == 0
                    ? "The Logs list is empty."
                    : string.Join("\n\n", events.Take(count).Select(e =>
                        $"{e.Time:yyyy-MM-dd HH:mm:ss} {e.Kind}: {e.What}" + (e.Version.Length > 0 ? $" (version {e.Version})" : "")
                        + (e.Details.Length > 0 ? "\n" + (e.Details.Length > 1500 ? e.Details[..1500] + "…" : e.Details) : ""))));
            }
            case "find_settings":
                return ClaudeSaid(await Task.Run(() => ClaudeSettings(manager, ClaudeStr(a, "search") ?? "", Math.Clamp((int) (ClaudeNum(a, "limit") ?? 40), 1, 300))));
            case "run_command": {
                var command = (ClaudeStr(a, "command") ?? "").Trim();
                var wizard = (ClaudeStr(a, "wizard_id") ?? "").Trim();
                manager.SendCommand(command, wizard.Length > 0 ? wizard : null);
                Say($"Claude ran {command}");
                return ClaudeSaid(manager.TypesIntoGame
                    ? $"Typed {command} into the game's chat (the game plays on another server)."
                    : $"Sent {command} to the server; it runs as {(wizard.Length > 0 ? "wizard " + wizard : "the wizard playing on this PC")}. read_server_log shows the answer.");
            }
            case "announce": {
                var where = (ClaudeStr(a, "where") ?? "both").Trim().ToLowerInvariant();
                manager.Announce(ClaudeStr(a, "text") ?? "", where is "box" or "both", where is "chat" or "both");
                Say("Claude sent an announcement.");
                return ClaudeSaid($"Announced ({where}).");
            }
            case "change_setting": {
                var section = (ClaudeStr(a, "section") ?? "").Trim();
                var key = (ClaudeStr(a, "key") ?? "").Trim();
                var value = (ClaudeStr(a, "value") ?? "").Trim();
                if (SecretSetting(key)) {
                    return ClaudeSaid("Claude can't change passwords or keys. Change that one yourself on the Settings tab.", error: true);
                }

                var old = manager.GetSetting(section, key);
                var restart = await Task.Run(() => manager.SaveSettings([new Setting(section, key, value, "")]));
                Say($"Claude changed {section} > {key}.");
                return ClaudeSaid($"{section} > {key}: {old} -> {value}. " + (restart
                    ? "The server reads this one only when it starts, so it waits for a restart."
                    : InstalledCommands.IsServerRunning(manager.InstallFolder) ? "It is in use now." : "Saved; the server uses it when it starts."));
            }
            default:
                return ClaudeSaid("Ahziael doesn't have a tool called " + name, error: true);
        }
    }

    // "11/23/26 20:40:58 [STAT] CORE_SEER ...": the fourth word is the area (cut to 15 letters by the game).
    private static bool InGameArea(string line, string area) {
        var parts = line.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 3 && (parts[3].StartsWith(area, StringComparison.OrdinalIgnoreCase) || area.StartsWith(parts[3], StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ClaudeGameWindows.Window> ClaudeWindowAsync(JsonObject a) {
        var windows = await Task.Run(ClaudeGameWindows.List);
        if (windows.Count == 0) {
            throw new PanelException("No Wizard101 window is open on this PC. Start the game first (Play tab).");
        }

        var number = (int) (ClaudeNum(a, "window") ?? 1);
        return windows.FirstOrDefault(w => w.Number == number)
               ?? throw new PanelException($"There is no game window #{number}: the open ones are #1 to #{windows.Count} (see list_game_windows).");
    }

    private async Task<string> ClaudeStatusAsync() {
        var manager = _manager;
        var (status, windows) = await Task.Run(() => (manager.Status(), ClaudeGameWindows.List()));
        var text = new StringBuilder();
        text.AppendLine($"{Edition.NAME} {SelfUpdate.CurrentVersion}.");
        text.AppendLine(RemoteSession.Current is { } remote
            ? $"Managing another PC's server ({remote.Label}): " + (status.Running ? "connected." : "not connected.")
            : "This PC's server is " + (status.Running ? status.Ready ? "on and ready." : "starting." : "off."));
        if (status.Randomizer is { } randomizer) {
            text.AppendLine("Randomizer: " + (randomizer ? "on." : "off."));
        }

        if (status.DieDelete is { } dieDelete) {
            text.AppendLine("Die Delete: " + (dieDelete ? "on." : "off."));
        }

        text.AppendLine(_onlineLatest.Count == 1 ? "1 player is online (list_players)." : $"{_onlineLatest.Count} players are online (list_players).");
        text.AppendLine(windows.Count == 0 ? "No Wizard101 window is open on this PC." : $"{windows.Count} Wizard101 window(s) open on this PC (list_game_windows).");
        text.AppendLine("Overlay: " + (OverlayHost.IsDrawing ? $"drawing on {OverlayHost.GameWindows} game window(s). " : "not drawing here. ") + OverlayHost.Link.Status);
        text.Append(ClaudeChoices.Changes ? "Claude may make changes." : "Claude can only look (changes are off).");
        return text.ToString();
    }

    private string ClaudePlayers() {
        if (_onlineLatest.Count == 0) {
            return "Nobody is online (or the server is off).";
        }

        return string.Join("\n", _onlineLatest.Select(p =>
            (p.Wizard.Length == 0 ? "(picking a wizard)" : p.Wizard)
            + $" | account {(p.Account.Length > 0 ? p.Account : "?")}"
            + (p.Level > 0 ? $" | level {p.Level}" : "") + (p.School.Length > 0 ? $" {p.School}" : "")
            + (p.Zone.Length > 0 ? $" | {PlaceInWords(p.Zone)} ({p.Zone})" : "")
            + (p.InBattle ? " | in a battle" : "")
            + (p.Since is { } since ? $" | online since {since:HH:mm}" : "")));
    }

    private static string ClaudePositions(string[] lines) {
        var shown = new List<string>();
        foreach (var line in lines) {
            var parts = line.Split('\t');
            if (parts.Length < 7) {
                continue;
            }

            var radians = double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 0;
            shown.Add($"{parts[0]} | zone {parts[1]} | X {parts[2]} Y {parts[3]} Z {parts[4]} | facing {parts[5]} rad ({ClaudeN(radians * 180 / Math.PI % 360)}°)"
                      + $" | last move {parts[6]}" + (parts.Length > 7 ? $" | {(parts[7] == "local" ? "plays on this PC" : "plays on another PC")}" : "")
                      + (parts.Length > 8 ? $" | wizard id {parts[8]}" : ""));
        }

        return shown.Count == 0 ? "No positions yet: nobody has moved since the server started (or it is off)." : string.Join("\n", shown);
    }

    private static string ClaudeMinimap(ClaudeGameWindows.Window window, int limit) {
        if (OverlayMinimapData.For(window.Handle.ToInt64()) is not { } map) {
            return $"Game window #{window.Number} has no minimap data. The overlay only asks the server while its design shows a minimap (Play > Overlay).";
        }

        var text = new StringBuilder();
        text.AppendLine($"{map.Place}{(map.Zone.Length > 0 ? $" ({map.Zone})" : "")}: the wizard is at X {ClaudeN(map.X)} Y {ClaudeN(map.Y)}, facing {ClaudeN(map.Yaw)} rad."
                        + (map.Stale ? " (Old: the server hasn't sent a new map for a while.)" : ""));
        if (map.Note.Length > 0) {
            text.AppendLine(map.Note);
        }

        foreach (var m in map.Marks.OrderBy(m => (m.X - map.X) * (m.X - map.X) + (m.Y - map.Y) * (m.Y - map.Y)).Take(limit)) {
            text.AppendLine($"{m.Kind} \"{m.Name}\"" + (m.Level > 0 ? $" level {m.Level}" : "") + (m.Job.Length > 0 ? $" ({m.Job})" : "")
                            + $" at X {ClaudeN(m.X)} Y {ClaudeN(m.Y)}" + (Math.Abs(m.Height) > 1 ? $", {ClaudeN(m.Height)} {(m.Height > 0 ? "above" : "below")}" : ""));
        }

        if (map.Marks.Count > limit) {
            text.AppendLine($"…and {map.Marks.Count - limit} more further away.");
        }

        return text.ToString().TrimEnd();
    }

    private static string ClaudeQuests(ClaudeGameWindows.Window window) {
        var key = window.Handle.ToInt64();
        if (OverlayQuestData.For(key) is not { } list) {
            return $"Game window #{window.Number} has no Quest Tracker data. The overlay only asks the server while its design shows a Quest Tracker (Play > Overlay).";
        }

        var text = new StringBuilder();
        text.AppendLine($"In {list.Place}." + (list.Stale ? " (Old: the server hasn't sent the quests for a while.)" : ""));
        if (list.Note.Length > 0) {
            text.AppendLine(list.Note);
        }

        var picked = OverlayQuestData.Pick(key);
        foreach (var q in list.Quests) {
            text.AppendLine($"{(q.Id == picked ? "[picked] " : "")}{q.Title} ({q.Kind}{(q.World.Length > 0 ? ", " + q.World : "")}{(q.Level > 0 ? $", level {q.Level}" : "")}){(q.Here ? ", something to do here" : "")}");
            foreach (var s in q.Steps) {
                text.AppendLine($"   - {s.Text}{(s.Where.Length > 0 ? $" ({s.Where})" : "")}{(s.Here ? " [here]" : "")}");
            }

            if (q.Way.Count > 0) {
                text.AppendLine("   way: " + string.Join(" > ", q.Way));
            }
        }

        if (OverlayQuestData.RouteFor(key) is { } route) {
            text.AppendLine($"Arrow: toward {route.To}{(route.Via.Length > 0 ? $" via {route.Via}" : "")} for {route.Title}; step spots: "
                            + string.Join(", ", route.Points.Take(8).Select(p => $"{p.Name} ({ClaudeN(p.X)}, {ClaudeN(p.Y)})")));
        }

        return text.ToString().TrimEnd();
    }

    private static readonly Regex s_secretKey = new("pass|secret|token|apikey|api_key|privatekey", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool SecretSetting(string key) => s_secretKey.IsMatch(key);

    private static string ClaudeSettings(ServerManager manager, string search, int limit) {
        if (!manager.IsInstalled) {
            throw new PanelException("Imlight.ini isn't there yet: the server isn't set up on this PC.");
        }

        var sections = manager.ReadSettings();
        var words = SearchText.Words(search);
        if (words.Length == 0) {
            return "Sections: " + string.Join(", ", sections.Select(s => $"{s.Section} ({s.Settings.Count})")) + ". Search for words to see settings.";
        }

        var found = sections.SelectMany(s => s.Settings)
            .Where(s => words.All(SearchText.Normalize($"{s.Section} {s.Key} {s.Value} {s.Help}").Contains)).ToList();
        if (found.Count == 0) {
            return "No setting matches.";
        }

        return string.Join("\n", found.Take(limit).Select(s =>
                   $"[{s.Section}] {s.Key} = {(SecretSetting(s.Key) ? "(hidden)" : s.Value)}" + (ServerManager.NeedsRestart(s.Section, s.Key) ? " (needs a restart)" : "")
                   + (s.Help.Length > 0 ? $"\n   {(s.Help.Length > 400 ? s.Help[..400] + "…" : s.Help)}" : "")))
               + (found.Count > limit ? $"\n…and {found.Count - limit} more." : "");
    }

    private (byte[] Png, int Width, int Height) PictureOfAhziael(int maxWidth) {
        var scale = RenderScaling;
        var size = new PixelSize(Math.Max(1, (int) (ClientSize.Width * scale)), Math.Max(1, (int) (ClientSize.Height * scale)));
        var fit = Math.Min(1, Math.Max(200, maxWidth) / (double) size.Width);
        var outSize = new PixelSize(Math.Max(1, (int) (size.Width * fit)), Math.Max(1, (int) (size.Height * fit)));
        // The window's content first, at full size, then drawn over its background at the size asked for.
        using var layers = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        layers.Render(Content as Visual ?? this);
        using var picture = new RenderTargetBitmap(outSize, new Vector(96 * scale * fit, 96 * scale * fit));
        using (var ctx = picture.CreateDrawingContext()) {
            var all = new Rect(0, 0, ClientSize.Width, ClientSize.Height);
            ctx.FillRectangle(Background ?? Brushes.Black, all);
            ctx.DrawImage(layers, new Rect(0, 0, size.Width, size.Height), all);
        }

        using var stream = new MemoryStream();
        picture.Save(stream);
        return (stream.ToArray(), outSize.Width, outSize.Height);
    }

}
#endif
