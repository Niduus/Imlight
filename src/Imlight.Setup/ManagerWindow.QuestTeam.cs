using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The Quest Team section of the side bar (Niduus, 2026-11-12: "Make them their own section and server, using the same bot
/// features. Party of 4."). Four wizards (Ice, Life, Death, Storm) play the story by themselves on the server, walking,
/// training and fighting like players, and say which quests do not work. The server runs them only while [QuestTeam]
/// Enabled is on; its little program writes everything in Server\logs\quest-team, which these tabs read. A new issue shows a
/// banner at the top of Ahziael ("If a issue occures, it will announce it in Ahziael").
/// </summary>
internal sealed partial class ManagerWindow {

    private const string QUEST_TEAM_FOLDER = "quest-team";
    private const string QUEST_TEAM_TAB = "Quest Team";
    private const string QUEST_TEAM_ISSUES_TAB = "Team Issues";
    private const string QUEST_TEAM_LOGS_TAB = "Team Logs";
    private const string QUEST_TEAM_WIZARDS_TAB = "Team Wizards";
    private const string BOT_CONCEPT_SHOUT_OUT = "Shout out to Discord user gese11 for sharing the bot concept.";
    private readonly WizardSheet _teamSheet = new("Turn the Quest Team on and start the server; once the four wizards are playing, pick one here to see its stats, gear, deck and quest.") { Compact = true };
    private const int QUEST_TEAM_LOG_LINES = 300;

    private readonly TextBlock _teamState = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _teamDoing = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _teamToday = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _teamQuest = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _teamMembers = new() { Spacing = 6 };
    private readonly ToggleSwitch _teamEnabled = new() { OnContent = "On", OffContent = "Off" };
    private readonly NumericUpDown _teamSlow = new() { Minimum = 1.2m, Maximum = 20, Increment = 0.5m, FormatString = "0.0", Width = 140 };
    private readonly NumericUpDown _teamLosses = new() { Minimum = 1, Maximum = 20, Increment = 1, FormatString = "0", Width = 140 };
    private readonly NumericUpDown _teamQuestMinutes = new() { Minimum = 0, Maximum = 600, Increment = 5, FormatString = "0", Width = 140 };
    private readonly NumericUpDown _teamStuck = new() { Minimum = 2, Maximum = 120, Increment = 1, FormatString = "0", Width = 140 };
    private readonly StackPanel _teamIssues = new() { Spacing = 10 };
    private readonly TextBlock _teamIssuesEmpty = new() { TextWrapping = TextWrapping.Wrap, Foreground = MUTED, Text = "No issues yet." };
    private readonly DispatcherTimer _teamTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Border _teamBanner = new() { IsVisible = false };
    private readonly TextBlock _teamBannerText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private SelectableTextBlock? _teamLogBox;
    private ScrollViewer? _teamLogScroll;
    private TextBox? _teamQuestLogBox;
    private string _teamLogText = "";
    private string _teamQuestLogText = "";
    private DateTime _teamIssuesWritten = DateTime.MinValue;
    private List<QuestTeamIssue> _teamIssueList = [];
    private int _teamIssuesShown = -1;
    private bool _showingTeam;
    private bool _teamTimerStarted;

    private sealed class QuestTeamIssue {
        public int Id { get; set; }
        public DateTime At { get; set; }
        public string Kind { get; set; } = "";
        public string Quest { get; set; } = "";
        public string Title { get; set; } = "";
        public string World { get; set; } = "";
        public string Text { get; set; } = "";
        public string Expected { get; set; } = "";
        public List<string> Seen { get; set; } = [];
        public List<string> Log { get; set; } = [];
        public string Level { get; set; } = "";
    }

    // The newest issue already popped up (-1: not read yet, so issues from before Ahziael opened do not pop up).
    private int _teamPopped = -1;
    private Window? _teamPopup;

    // A CRITICAL issue (the story is stopped) pops up over Ahziael with an OK button (Niduus, 2026-11-13: "this should also
    // pop up in Ahziael for critical issues"), as well as the banner.
    private void PopUpCriticalIssues() {
        var newest = _teamIssueList.Count == 0 ? 0 : _teamIssueList.Max(i => i.Id);
        if (_teamPopped < 0) {
            _teamPopped = newest;

            return;
        }

        var critical = _teamIssueList.Where(i => i.Id > _teamPopped
                                                 && (i.Level == "CRITICAL" || (i.Level.Length == 0 && i.Kind == "team"))).OrderBy(i => i.Id).ToList();
        _teamPopped = Math.Max(_teamPopped, newest);
        if (critical.Count == 0) {
            return;
        }

        var last = critical[^1];
        var text = (critical.Count == 1 ? "Critical Quest Team issue" : $"{critical.Count} critical Quest Team issues. Newest") +
                   $" ({(last.Title.Length > 0 ? last.Title : "the Quest Team")}): {last.Text}";
        _teamPopup?.Close();
        var ok = Primary("OK");
        var issues = Plain("Open Team Issues");
        var dialog = new Window {
            Title = Edition.NAME + " - Quest Team", Width = 460, SizeToContent = SizeToContent.Height, CanResize = false, Background = BACKGROUND,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        ManagerTheme.Apply(dialog);
        ok.Click += (_, _) => dialog.Close();
        issues.Click += (_, _) => {
            dialog.Close();
            SelectTab(QUEST_TEAM_ISSUES_TAB);
        };
        dialog.Content = new StackPanel {
            Margin = new Thickness(22), Spacing = 16,
            Children = {
                new DockPanel {
                    Children = {
                        WithDock(WithMargin(new Border { VerticalAlignment = VerticalAlignment.Top, Child = ManagerTheme.Tile("flag", new SolidColorBrush(Color.Parse("#FF5A5A")), 32) }, new Thickness(0, 0, 14, 0)), Dock.Left),
                        WithCenter(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = ManagerTheme.FONT_HEADING - 2 }),
                    },
                },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { issues, ok } },
            },
        };
        dialog.Closed += (_, _) => {
            if (_teamPopup == dialog) {
                _teamPopup = null;
            }
        };
        _teamPopup = dialog;
        dialog.Show(this);
    }

    private string QuestTeamFolder => Path.Combine(_manager.LogFolder, QUEST_TEAM_FOLDER);

    private void StartQuestTeamTimer() {
        if (_teamTimerStarted) {
            return;
        }

        _teamTimerStarted = true;
        _teamTimer.Tick += (_, _) => RefreshQuestTeam();
        _teamTimer.Start();
        Dispatcher.UIThread.Post(RefreshQuestTeam, DispatcherPriority.Background);
    }

    // ---------------------------------------------------------------------
    // Quest Team tab: the switch, the four wizards, what they are doing, the settings and Restart
    // ---------------------------------------------------------------------

    private Control QuestTeamTab() {
        _teamEnabled.IsCheckedChanged += async (_, _) => {
            if (!_showingTeam) {
                await Guard(() => SetQuestTeamEnabledAsync(_teamEnabled.IsChecked == true));
            }
        };
        foreach (var (box, key) in new[] { (_teamSlow, "SlowFactor"), (_teamLosses, "LossesBeforeIssue"), (_teamStuck, "StuckMinutes"), (_teamQuestMinutes, "QuestMinutes") }) {
            box.ValueChanged += (_, _) => {
                if (!_showingTeam && box.Value is { } value) {
                    _ = Guard(() => SaveQuestTeamNumberAsync(key, value));
                }
            };
        }

        LoadQuestTeamSettings();
        StartQuestTeamTimer();

        return Page(
            SpaceNotice("The Quest Team"),
            Fold("users", QUEST_TEAM_TAB, "Quest Team",
                "Four wizards the server plays by itself, always together and always in this order: Ice (tanks and supports), " +
                "Life (heals and supports), Death (backup hitter and support) and Storm (hits and blades itself). They start at " +
                "level 1 in Wizard City and play the story like players do: they walk everywhere (no teleporting), talk to the " +
                "quest givers, fight as a team, and walk to the Ravenwood trainers to learn spells with their own training points " +
                "(each also learns the spells its team role needs plus a heal and a shield of its own, so it can fight alone). When a quest does not work, when they are stuck, or when a " +
                "quest takes much longer than it should, a banner at the top of Ahziael says so and Team Issues shows what happened. " +
                "They run in a little program of their own next to the server; they never touch your own wizards. " + BOT_CONCEPT_SHOUT_OUT,
                _teamState,
                SwitchRow("Quest Team", _teamEnabled, "On: the four wizards log in a few seconds later and start questing. Off: they log out."),
                _teamDoing,
                _teamToday,
                _teamQuest,
                _teamMembers,
                Row(Action(Plain("Pause or resume"), PauseQuestTeamAsync), Action(Plain("Stop and save"), StopQuestTeam), Action(Plain("Restart with four new wizards"), RestartQuestTeamAsync), Action(Plain("Open its folder"), () => {
                    Directory.CreateDirectory(QuestTeamFolder);

                    return Open(QuestTeamFolder);
                }))),
            Fold("sliders", QUEST_TEAM_TAB, "When to tell you",
                "When the Quest Team counts something as an issue. Changes count right away.",
                SwitchRow("A quest is too slow at (times the expected time)", _teamSlow,
                    "2 means a quest that takes twice as long as expected is reported. The first time a quest is played its time is a guess; after that the team learns the real time."),
                SwitchRow("Lost fights before it is an issue", _teamLosses,
                    "When the team loses the same fight this many times, the quest is reported."),
                SwitchRow("Minutes with no progress before it is stuck", _teamStuck,
                    "When nothing moves forward for this long, the team reports that it is stuck."),
                SwitchRow("Time limit for one quest (minutes)", _teamQuestMinutes,
                    "A quest that takes longer than this is reported, however long it was expected to take. 0 means no limit.")));
    }

    private void LoadQuestTeamSettings() {
        _showingTeam = true;
        try {
            _teamEnabled.IsChecked = _manager.GetSwitch("QuestTeam", "Enabled");
            _teamSlow.Value = (decimal) Number("SlowFactor", 2);
            _teamLosses.Value = (decimal) Number("LossesBeforeIssue", 2);
            _teamStuck.Value = (decimal) Number("StuckMinutes", 8);
            _teamQuestMinutes.Value = (decimal) Number("QuestMinutes", 45);
        }
        finally {
            _showingTeam = false;
        }

        double Number(string key, double fallback)
            => double.TryParse(_manager.GetSetting("QuestTeam", key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    private Task SetQuestTeamEnabledAsync(bool on) {
        if (!_manager.HasSetting("QuestTeam", "Enabled")) {
            LoadQuestTeamSettings();
            throw new PanelException("This server's Imlight.ini has no [QuestTeam] section yet. Click \"Update Ahziael\" once, then try again.");
        }

        _manager.SaveSettings([new Setting("QuestTeam", "Enabled", on ? "true" : "false", "")]);
        _settings = [];
        Say(on ? "The Quest Team is on. With the server running, the four wizards log in within a few seconds and start questing."
            : "The Quest Team is off. The four wizards log out within a few seconds.");
        RefreshQuestTeam();

        return Task.CompletedTask;
    }

    private Task SaveQuestTeamNumberAsync(string key, decimal value) {
        if (!_manager.HasSetting("QuestTeam", key)) {
            throw new PanelException("This server's Imlight.ini has no [QuestTeam] " + key + " yet. Click \"Update Ahziael\" once, then try again.");
        }

        var text = key == "SlowFactor" ? ((double) value).ToString("0.0", CultureInfo.InvariantCulture) : ((int) value).ToString(CultureInfo.InvariantCulture);
        if (_manager.GetSetting("QuestTeam", key) == text) {
            return Task.CompletedTask;
        }

        _manager.SaveSettings([new Setting("QuestTeam", key, text, "")]);
        _settings = [];
        Say("Saved. The Quest Team uses it right away.");

        return Task.CompletedTask;
    }

    // Pause (Niduus, 2026-11-12: "Please!"): the four stand still where they are, still logged in, so he can walk over and
    // look; a battle they are in still plays out. The file is there while paused.
    private async Task PauseQuestTeamAsync() {
        Directory.CreateDirectory(QuestTeamFolder);
        var file = Path.Combine(QuestTeamFolder, "paused.txt");
        if (File.Exists(file)) {
            File.Delete(file);
            Say("Resumed. The team goes on with its quest.");

            return;
        }

        await File.WriteAllTextAsync(file, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        Say("Paused. The four stand where they are and stay logged in (a battle they are in still finishes). Click again to resume.");
    }

    // Stop and save: the team logs out cleanly where it is (its progress is the wizards' own, saved as it goes); turning it
    // on again picks up from the same step.
    private async Task StopQuestTeam() {
        _showingTeam = true;
        _teamEnabled.IsChecked = false;
        _showingTeam = false;
        await SetQuestTeamEnabledAsync(false);
        Say("Stopped. The four log out where they are, with their quests and steps kept. Turn the Quest Team on to pick up from there.");
    }

    private async Task RestartQuestTeamAsync() {
        if (!await ConfirmAsync("Make four new level 1 wizards for the Quest Team? The four they play now are deleted, and the new ones " +
                                "start again at the beginning of Wizard City. Their issues and quest log stay.")) {
            return;
        }

        Directory.CreateDirectory(QuestTeamFolder);
        await File.WriteAllTextAsync(Path.Combine(QuestTeamFolder, "restart-request.txt"), DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        Say(InstalledCommands.IsServerRunning(_manager.InstallFolder) && _teamEnabled.IsChecked == true
            ? "Asked. The team logs out, four new wizards are made, and they log in again within half a minute."
            : "Asked. Four new wizards are made the next time the Quest Team runs (server started and the Quest Team on).");
    }

    // ---------------------------------------------------------------------
    // Team Issues tab
    // ---------------------------------------------------------------------

    private Control QuestTeamIssuesTab() {
        StartQuestTeamTimer();

        return Page(Fold("flag", QUEST_TEAM_TAB, "Team Issues",
            "Everything the Quest Team ran into, newest first: a quest that does not work, the team being stuck, a fight lost " +
            "again and again, or a quest that took much longer than expected. Each one shows the last lines of the team's log " +
            "from when it happened, so you can see what led up to it.",
            Row(Action(Plain("Copy all"), () => CopyAsync(IssuesText(_teamIssueList))), Action(Plain("Open its folder"), () => {
                Directory.CreateDirectory(QuestTeamFolder);

                return Open(QuestTeamFolder);
            })),
            _teamIssuesEmpty,
            _teamIssues));
    }

    private static string IssueKind(string kind) => kind switch {
        "quest" => "Quest problem",
        "team" => "Quest Team stuck",
        "slow" => "Taking too long",
        _ => "Issue",
    };

    private static string IssuesText(IEnumerable<QuestTeamIssue> issues) {
        var text = new StringBuilder();
        foreach (var issue in issues) {
            text.AppendLine($"#{issue.Id}  {issue.At.ToLocalTime():yyyy-MM-dd HH:mm}  {IssueKind(issue.Kind)}  {issue.Title} {(issue.World.Length > 0 ? $"({issue.World})" : "")}");
            text.AppendLine(issue.Text);
            if (issue.Expected.Length > 0) {
                text.AppendLine("Expected (as a player): " + issue.Expected);
            }

            foreach (var line in issue.Seen ?? []) {
                text.AppendLine("  " + line);
            }

            foreach (var line in issue.Log) {
                text.AppendLine("    " + line);
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    private void ShowQuestTeamIssues() {
        if (_teamIssuesShown == (_teamIssueList.Count == 0 ? 0 : _teamIssueList.Max(i => i.Id))) {
            return;
        }

        _teamIssuesShown = _teamIssueList.Count == 0 ? 0 : _teamIssueList.Max(i => i.Id);
        _teamIssues.Children.Clear();
        _teamIssuesEmpty.IsVisible = _teamIssueList.Count == 0;
        foreach (var issue in _teamIssueList.OrderByDescending(i => i.Id).Take(100)) {
            var head = new TextBlock {
                Text = $"{IssueKind(issue.Kind)}: {(issue.Title.Length > 0 ? issue.Title : "the Quest Team")}{(issue.World.Length > 0 ? $" ({issue.World})" : "")}",
                FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
                Foreground = issue.Kind == "slow" ? ManagerTheme.Accent("Progress") : ManagerTheme.Accent("Create"),
            };
            var when = new TextBlock {
                Text = $"{issue.At.ToLocalTime():dddd d MMMM, HH:mm}{(issue.Quest.Length > 0 ? $"  ·  {issue.Quest}" : "")}",
                Foreground = MUTED, FontSize = ManagerTheme.FONT_SMALL, TextWrapping = TextWrapping.Wrap,
            };
            var body = new TextBlock { Text = issue.Text, TextWrapping = TextWrapping.Wrap };
            var expected = new TextBlock {
                Text = issue.Expected.Length == 0 ? "" : "Expected, as a player: " + issue.Expected, TextWrapping = TextWrapping.Wrap,
                IsVisible = issue.Expected.Length > 0, FontStyle = FontStyle.Italic,
            };
            var seen = new TextBlock {
                Text = string.Join('\n', issue.Seen ?? []), TextWrapping = TextWrapping.Wrap, Foreground = MUTED, FontSize = ManagerTheme.FONT_SMALL,
                IsVisible = (issue.Seen?.Count ?? 0) > 0,
            };
            var log = new TextBox {
                IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = MONO, FontSize = 12,
                MaxHeight = 260, Text = issue.Log.Count == 0 ? "(no log lines)" : string.Join('\n', issue.Log),
            };
            var logFold = new Expander { Header = "What happened (the team's log)", Content = LogWell(log), HorizontalAlignment = HorizontalAlignment.Stretch };
            _teamIssues.Children.Add(new Border {
                Background = ManagerTheme.SURFACE_HIGH_BRUSH, CornerRadius = new CornerRadius(ManagerTheme.RADIUS_SMALL), Padding = new Thickness(14, 10),
                Child = new StackPanel { Spacing = 6, Children = { head, when, body, expected, seen, logFold } },
            });
        }
    }

    // ---------------------------------------------------------------------
    // Team Logs tab
    // ---------------------------------------------------------------------

    // Pick a wizard of the team and see its stats, gear, deck and quest (Niduus, 2026-11-13). The view is shared with Ultimus.
    private Control QuestTeamWizardsTab() {
        StartQuestTeamTimer();
        ShowCardsOn(_teamSheet);

        return Page(
            Fold("users", QUEST_TEAM_TAB, "The team's wizards",
                "Pick Ice, Life, Death or Storm to see that wizard's stats, the gear it wears, its deck right now, and the quest and " +
                "step it is on. Updated every 3 seconds.",
                _teamSheet.View));
    }

    private Control QuestTeamLogsTab() {
        StartQuestTeamTimer();
        _teamQuestLogBox = new TextBox {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = MONO, FontSize = 12.5,
            Height = 300, Text = "Turn the Quest Team on and start the server; every quest it starts and finishes is written here.",
        };
        _teamLogBox = new SelectableTextBlock {
            TextWrapping = TextWrapping.Wrap, FontFamily = MONO, FontSize = 12.5, Margin = new Thickness(10, 8),
            Text = "Turn the Quest Team on and start the server; what each wizard does is written here.",
        };
        _teamLogScroll = new ScrollViewer { Height = 420, Content = _teamLogBox, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        return Page(
            Fold("flag", QUEST_TEAM_TAB, "Quest log",
                "Every quest the team started and finished, each step, how long it took and how long it was expected to take.",
                LogWell(_teamQuestLogBox),
                Row(Action(Plain("Copy all"), () => CopyAsync(_teamQuestLogText)))),
            Fold("users", QUEST_TEAM_TAB, "Team log",
                "What each wizard does: where they walk, who they talk to, what they train, each spell they cast in a battle, and " +
                "why they wait. Updated every 3 seconds.",
                TeamLogColors(),
                LogWell(_teamLogScroll),
                Row(Action(Plain("Copy all"), () => CopyAsync(_teamLogText)), Action(Plain("Open its folder"), () => {
                    Directory.CreateDirectory(QuestTeamFolder);

                    return Open(QuestTeamFolder);
                }))));
    }

    // ---------------------------------------------------------------------
    // The banner at the top of Ahziael
    // ---------------------------------------------------------------------

    private Control QuestTeamBanner() {
        var bar = ManagerTheme.GlassPanel(new DockPanel {
            Children = {
                WithDock(new StackPanel {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = {
                        Action(Primary("Show what happened"), () => {
                            SelectTab(QUEST_TEAM_ISSUES_TAB);
                            MarkQuestTeamIssuesSeen();

                            return Task.CompletedTask;
                        }),
                        Action(Plain("Dismiss"), () => {
                            MarkQuestTeamIssuesSeen();

                            return Task.CompletedTask;
                        }),
                    },
                }, Dock.Right),
                WithDock(WithMargin(ManagerTheme.Tile("flag", ManagerTheme.Accent("Create"), 26), new Thickness(0, 0, 10, 0)), Dock.Left),
                WithCenter(_teamBannerText),
            },
        }, 14);
        bar.Padding = new Thickness(12, 6);
        bar.Margin = new Thickness(12, 8, 12, 0);
        _teamBanner.Child = bar;
        StartQuestTeamTimer();

        return _teamBanner;
    }

    private int QuestTeamIssuesSeen() {
        try {
            var path = Path.Combine(QuestTeamFolder, "issues-seen.txt");

            return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var seen) ? seen : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return int.MaxValue;
        }
    }

    private void MarkQuestTeamIssuesSeen() {
        var newest = _teamIssueList.Count == 0 ? 0 : _teamIssueList.Max(i => i.Id);
        try {
            Directory.CreateDirectory(QuestTeamFolder);
            File.WriteAllText(Path.Combine(QuestTeamFolder, "issues-seen.txt"), newest.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Shown again next time; nothing lost.
        }

        _teamBanner.IsVisible = false;
    }

    // ---------------------------------------------------------------------
    // Reading the team's files
    // ---------------------------------------------------------------------

    private void RefreshQuestTeam() {
        ReadQuestTeamIssues();
        PopUpCriticalIssues();

        // The banner: issues newer than the last one Ahziael showed.
        var seen = QuestTeamIssuesSeen();
        var fresh = _teamIssueList.Where(i => i.Id > seen).OrderBy(i => i.Id).ToList();
        if (_teamIssues.IsEffectivelyVisible) {
            ShowQuestTeamIssues();
            if (fresh.Count > 0) {
                MarkQuestTeamIssuesSeen();
                fresh.Clear();
            }
        }

        if (fresh.Count > 0) {
            var last = fresh[^1];
            _teamBannerText.Text = (fresh.Count == 1 ? "An issue with the Quest Team has occurred: " : $"{fresh.Count} issues with the Quest Team have occurred. Newest: ") +
                                   $"{IssueKind(last.Kind).ToLowerInvariant()}, {(last.Title.Length > 0 ? last.Title : "the Quest Team")}. {last.Text}";
        }

        _teamBanner.IsVisible = fresh.Count > 0;

        if (_teamState.IsEffectivelyVisible) {
            ShowQuestTeamStatus();
        }

        if (_teamSheet.View.IsEffectivelyVisible) {
            _teamSheet.Show(ReadQuestTeamStatus() is { } status && DateTime.TryParse((string?) status["at"], CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var written) && DateTime.UtcNow - written.ToUniversalTime() < TimeSpan.FromSeconds(30)
                ? status["members"] as JsonArray : null);
        }

        if (_teamLogBox?.IsEffectivelyVisible == true) {
            ShowColoredLog(ReadNewest("quest-team*.txt", _teamLogText));
            ShowLog(_teamQuestLogBox!, ReadNewest("quest-log.txt", _teamQuestLogText), ref _teamQuestLogText);
        }

        static void ShowLog(TextBox box, string text, ref string shown) {
            if (text == shown || text.Length == 0) {
                return;
            }

            shown = text;
            box.Text = string.Join('\n', text.Split('\n').TakeLast(QUEST_TEAM_LOG_LINES));
            box.CaretIndex = box.Text.Length;
        }
    }

    // Team log colours (Niduus, 2026-11-12): white normal; yellow minor (does not hold the quest back); orange notable (could
    // stop a quest finishing); red critical (stops or stopped the story, fix it now); cyan a command Ahziael ran for them,
    // with the reason. The server tags each line with the word (QuestTeamLink.Note), so a copied log still says it.
    private static readonly (string Tag, string Color, string Meaning)[] s_teamLogLevels = [
        ("CRITICAL", "#FF5A5A", "Red: critical, stops or stopped the story, fix it now"),
        ("NOTABLE", "#FFA040", "Orange: notable, could stop a quest from finishing"),
        ("MINOR", "#FFE45C", "Yellow: minor, does not hold the quest back"),
        ("COMMAND", "#5CE1FF", "Cyan: something Ahziael did for them, and why"),
        ("ABNORMAL", "#FF6EC7", "Pink: something abnormal they noticed"),
    ];

    private static IBrush TeamLogBrush(string line) {
        foreach (var (tag, color, _) in s_teamLogLevels) {
            if (line.Contains(": " + tag + ": ", StringComparison.Ordinal)) {
                return new SolidColorBrush(Color.Parse(color));
            }
        }

        return ManagerTheme.TEXT_BRUSH;
    }

    private static Control TeamLogColors() {
        var row = new WrapPanel();
        row.Children.Add(new TextBlock { Text = "White: normal", Foreground = ManagerTheme.TEXT_BRUSH, Margin = new Thickness(0, 0, 16, 4), FontSize = ManagerTheme.FONT_SMALL });
        foreach (var (_, color, meaning) in s_teamLogLevels) {
            row.Children.Add(new TextBlock { Text = meaning, Foreground = new SolidColorBrush(Color.Parse(color)), Margin = new Thickness(0, 0, 16, 4), FontSize = ManagerTheme.FONT_SMALL });
        }

        return row;
    }

    private void ShowColoredLog(string text) {
        if (_teamLogBox is null || text == _teamLogText || text.Length == 0) {
            return;
        }

        _teamLogText = text;
        var atEnd = _teamLogScroll is null || _teamLogScroll.Offset.Y >= _teamLogScroll.Extent.Height - _teamLogScroll.Viewport.Height - 20;
        var inlines = new Avalonia.Controls.Documents.InlineCollection();
        foreach (var line in text.Split('\n').TakeLast(QUEST_TEAM_LOG_LINES)) {
            if (inlines.Count > 0) {
                inlines.Add(new Avalonia.Controls.Documents.LineBreak());
            }

            inlines.Add(new Avalonia.Controls.Documents.Run(line.TrimEnd('\r')) { Foreground = TeamLogBrush(line) });
        }

        _teamLogBox.Inlines = inlines;
        if (atEnd) {
            _teamLogScroll?.ScrollToEnd();
        }
    }

    private void ReadQuestTeamIssues() {
        try {
            var path = Path.Combine(QuestTeamFolder, "issues.json");
            if (!File.Exists(path)) {
                _teamIssueList = [];

                return;
            }

            var written = File.GetLastWriteTimeUtc(path);
            if (written == _teamIssuesWritten) {
                return;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            _teamIssueList = JsonSerializer.Deserialize<List<QuestTeamIssue>>(stream) ?? [];
            _teamIssuesWritten = written;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            // Being written right now: read it on the next tick.
        }
    }

    private JsonObject? ReadQuestTeamStatus() {
        try {
            var path = Path.Combine(QuestTeamFolder, "status.json");
            if (!File.Exists(path)) {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            return JsonNode.Parse(stream) as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            return null;
        }
    }

    private void ShowQuestTeamStatus() {
        JsonObject? status = null;
        try {
            var path = Path.Combine(QuestTeamFolder, "status.json");
            if (File.Exists(path)) {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                status = JsonNode.Parse(stream) as JsonObject;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            return;
        }

        var at = DateTime.TryParse((string?) status?["at"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var written)
            ? written.ToUniversalTime() : DateTime.MinValue;
        var live = status is not null && DateTime.UtcNow - at < TimeSpan.FromSeconds(30);
        var running = InstalledCommands.IsServerRunning(_manager.InstallFolder);
        var on = _teamEnabled.IsChecked == true;

        _teamState.Text = !running ? "The server is not running, so the Quest Team is not either."
            : !on ? "Off: the Quest Team is not playing."
            : live ? $"Playing. {Capital((string?) status!["why"] ?? "")}".TrimEnd()
            : "Starting: the server starts the Quest Team within a few seconds, then the four wizards log in one after the other.";
        _teamState.Foreground = running && on && live ? ManagerTheme.Accent("good") : MUTED;

        _teamDoing.IsVisible = _teamToday.IsVisible = _teamQuest.IsVisible = _teamMembers.IsVisible = running && on && live;
        if (!_teamDoing.IsVisible) {
            return;
        }

        _teamDoing.Text = "Doing now: " + ((string?) status!["doing"] ?? "-");
        _teamToday.Text = "Today so far: " + ((string?) status["today"] ?? "nothing yet.");
        var quest = (string?) status["quest"] ?? "";
        var questFor = (int?) status["questFor"] ?? 0;
        var expected = (int?) status["questExpected"] ?? 0;
        _teamQuest.Text = quest.Length == 0 ? "Quest: none right now."
            : $"Quest: {quest}, for {Minutes(questFor)} so far" + (expected > 0 ? $" (expected about {Minutes(expected)})." : ".");
        _teamQuest.Foreground = expected > 0 && questFor > expected * (double) (_teamSlow.Value ?? 2) ? ManagerTheme.Accent("Create") : ManagerTheme.TEXT_BRUSH;

        foreach (var node in status["members"] as JsonArray ?? []) {
            if (node is not JsonObject member) {
                continue;
            }

            var role = (string?) member["role"] ?? "";
            var card = TeamMemberCard(role);
            card.Line.Text = $"{role}  ·  {(string?) member["name"]}  ·  level {(int?) member["level"] ?? 0}  ·  {(string?) member["zone"]}  ·  " +
                             $"health {(string?) member["health"]}  ·  {(int?) member["gold"] ?? 0} gold  ·  {(int?) member["training"] ?? 0} training points" +
                             ((bool?) member["battle"] == true ? "  ·  in a battle" : "") +
                             ((string?) member["state"] is { } state && state != "playing" ? $"  ·  {state}" : "");
            card.Quests.Text = "Quests: " + List(member["questList"], "none");
            card.Gear.Text = "Gear: " + List(member["gear"], "none yet");
            var deckFor = (string?) member["deckFor"] ?? "";
            card.Deck.Text = "Deck" + (deckFor.Length > 0 ? $" (made ready for {deckFor})" : "") + ": " +
                             ((string?) member["deck"] is { Length: > 0 } deck ? deck : "empty");
            card.Spells.Text = "Trained spells: " + List(member["spells"], "none");
        }

        static string List(JsonNode? node, string none)
            => node is JsonArray { Count: > 0 } array ? string.Join(", ", array.Select(n => (string?) n).Where(t => !string.IsNullOrEmpty(t))) : none;

        static string Capital(string text) => text.Length == 0 ? "" : char.ToUpperInvariant(text[0]) + text[1..];
        static string Minutes(int seconds) => seconds < 90 ? $"{seconds} seconds" : $"{seconds / 60} minutes";
    }

    private sealed record TeamCard(TextBlock Line, TextBlock Quests, TextBlock Gear, TextBlock Deck, TextBlock Spells);

    private readonly Dictionary<string, TeamCard> _teamCards = [];

    // One card per wizard, made once (so an opened "Quests, gear, deck and spells" stays open while it updates).
    private TeamCard TeamMemberCard(string role) {
        if (_teamCards.TryGetValue(role, out var known)) {
            return known;
        }

        TextBlock Text(bool strong = false) => new() { TextWrapping = TextWrapping.Wrap, FontWeight = strong ? FontWeight.SemiBold : FontWeight.Normal };
        var card = new TeamCard(Text(true), Text(), Text(), Text(), Text());
        var tp = Action(Plain("TP to " + role), () => {
            if (!InstalledCommands.IsServerRunning(_manager.InstallFolder)) {
                throw new PanelException("Start the server and log in to your wizard first.");
            }

            _manager.SendCommand(".questteam tp " + role);
            Say($"Sent: your wizard goes to the Quest Team's {role} wizard, wherever it is.");

            return Task.CompletedTask;
        });
        ToolTip.SetTip(tp, $"Your wizard (logged in on this PC) goes right next to {role}, even inside its dungeon. In the game: .questteam tp {role}");
        var details = new Expander {
            Header = "Quests, gear, deck and trained spells", HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel { Spacing = 6, Children = { card.Quests, card.Gear, card.Deck, card.Spells } },
        };
        _teamMembers.Children.Add(new Border {
            Background = ManagerTheme.SURFACE_HIGH_BRUSH, CornerRadius = new CornerRadius(ManagerTheme.RADIUS_SMALL), Padding = new Thickness(12, 8),
            Child = new StackPanel {
                Spacing = 6,
                Children = { new DockPanel { Children = { WithDock(tp, Dock.Right), WithCenter(card.Line) } }, details },
            },
        });
        _teamCards[role] = card;

        return card;
    }

    private string ReadNewest(string pattern, string fallback) {
        try {
            var file = Directory.Exists(QuestTeamFolder)
                ? new DirectoryInfo(QuestTeamFolder).EnumerateFiles(pattern).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
                : null;
            if (file is null) {
                return "";
            }

            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - 128 * 1024), SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            return reader.ReadToEnd().ReplaceLineEndings("\n").TrimEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return fallback;
        }
    }

}
