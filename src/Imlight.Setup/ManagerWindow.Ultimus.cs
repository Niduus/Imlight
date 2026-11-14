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
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The Ultimus section of the side bar (Niduus, 2026-11-12: "Can we create a Solo Quester called Ultimus? ... a fork of
/// current QT. It's own mini server, logs, logic and reasoning"). Forked from the Quest Team section
/// (ManagerWindow.QuestTeam.cs). One Balance wizard named Ultimus plays the Main Story alone; the server runs it only while
/// [Ultimus] Enabled is on, and its little program writes everything in Server\logs\ultimus, which these tabs read. Its
/// page shows its gear, stats, deck and quest (Niduus, 2026-11-13), and each issue has a debug report a quest can be fixed
/// from.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string ULTIMUS_FOLDER = "ultimus";
    private const string ULTIMUS_TAB = "Ultimus";
    private const string ULTIMUS_ISSUES_TAB = "Ultimus Issues";
    private const string ULTIMUS_LOGS_TAB = "Ultimus Logs";
    private const string ULTIMUS_AI_TAB = "Ultimus AI Chat";
    private const int ULTIMUS_LOG_LINES = 300;

    private readonly TextBlock _ultState = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    // A red note when a door did not take Ultimus (or the Quest Team) since you last looked: its tries are written down, ready
    // to send (Niduus, 2026-11-14).
    private readonly TextBlock _ultDoorText = new() {
        TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#E5484D")),
    };
    private StackPanel? _ultDoorNote;
    private const string DOOR_SEEN_FILE = "exit-tries-seen.txt";

    // The door tries files written since the last "Got it": Ultimus's and the Quest Team's, newest write time of each.
    private List<(string Who, string Path, DateTime At)> NewDoorTries() {
        var found = new List<(string Who, string Path, DateTime At)>();
        DateTime seen = DateTime.MinValue;
        try {
            var seenFile = Path.Combine(UltimusFolder, DOOR_SEEN_FILE);
            if (File.Exists(seenFile) && long.TryParse(File.ReadAllText(seenFile).Trim(), out var ticks)) {
                seen = new DateTime(ticks, DateTimeKind.Utc);
            }

            foreach (var (who, folder) in new[] { ("Ultimus", UltimusFolder), ("Quest Team", QuestTeamFolder) }) {
                var file = Path.Combine(folder, "exit-tries.txt");
                if (File.Exists(file) && File.GetLastWriteTimeUtc(file) is var at && at > seen) {
                    found.Add((who, file, at));
                }
            }
        }
        catch (Exception) {
            // Only a note: nothing shows when the files can't be read.
        }

        return found;
    }

    private Control UltimusDoorNote() {
        _ultDoorNote = new StackPanel { Spacing = 6, IsVisible = false, Margin = new Thickness(0, 4, 0, 4) };
        _ultDoorNote.Children.Add(_ultDoorText);
        _ultDoorNote.Children.Add(Row(
            Action(Plain("Copy the door tries"), () => CopyAsync(string.Join("\n\n", NewDoorTries().Select(d =>
                $"[{d.Who}] {d.Path}\n" + ReadAllOf(d.Path, 32 * 1024))), "The door tries were copied. Paste them in the chat.")),
            Action(Plain("Got it"), () => {
                try {
                    Directory.CreateDirectory(UltimusFolder);
                    File.WriteAllText(Path.Combine(UltimusFolder, DOOR_SEEN_FILE), DateTime.UtcNow.Ticks.ToString());
                }
                catch (Exception) {
                    // Shown again next time; nothing lost.
                }

                ShowUltimusDoorNote();

                return Task.CompletedTask;
            })));

        return _ultDoorNote;
    }

    private void ShowUltimusDoorNote() {
        if (_ultDoorNote is null) {
            return;
        }

        var doors = NewDoorTries();
        _ultDoorNote.IsVisible = doors.Count > 0;
        if (doors.Count > 0) {
            _ultDoorText.Text = $"A door did not take {string.Join(" or ", doors.Select(d => d.Who))} " +
                                $"(last at {doors.Max(d => d.At).ToLocalTime():HH:mm}). Each try is written down: press Copy the door tries " +
                                "and paste it in the chat so the door can be fixed. Got it hides this until the next one.";
        }
    }
    private readonly TextBlock _ultDoing = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _ultToday = new() { TextWrapping = TextWrapping.Wrap };

    // The friends page (Niduus, 2026-11-14, approved): the players Ultimus has met and talked with.
    private readonly SelectableTextBlock _ultFriends = new() { TextWrapping = TextWrapping.Wrap };
    private DateTime _ultFriendsRead = DateTime.MinValue;
    private readonly TextBlock _ultQuest = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _ultMembers = new() { Spacing = 6 };
    // Its stats, gear, deck and quest, the same sheet as the Quest Team's (Niduus, 2026-11-13); with one Ultimus the picker hides.
    private readonly WizardSheet _ultSheet = new("Turn Ultimus on and start the server; once it is playing, its stats, gear, deck and quest show here.");
    private readonly ToggleSwitch _ultEnabled = new() { OnContent = "On", OffContent = "Off" };
    // The nightly auto-coach (Niduus, 2026-11-14: a toggle, off by default): [Ultimus] AutoCoach.
    private readonly ToggleSwitch _ultAutoCoach = new() { OnContent = "On", OffContent = "Off" };
    private readonly ToggleSwitch _ultParty = new() { OnContent = "On", OffContent = "Off" };
    private readonly ToggleSwitch _ultEmotes = new() { OnContent = "On", OffContent = "Off" };

    // The guardrails' per-rule locks (Niduus, 2026-11-14): a locked rule is left alone by his daily review (self-tuning.txt).
    private static readonly (string Rule, string Name, string Help)[] s_ultTunable = [
        ("Quest.ReadyHealth", "Health he wants before a step", "How full his health must be before he starts a step (he heals first below it)."),
        ("Battle.HealAt", "When he heals in battle", "At or below this share of health he heals himself in a battle."),
        ("Confidence.Careful", "When he plays it careful", "A plan he trusts less than this is done the careful way: full health first, the map's way."),
    ];
    private readonly ToggleSwitch _ultTuneAuto = new() { OnContent = "On", OffContent = "Off" };
    private readonly Dictionary<string, ToggleSwitch> _ultLocks = [];
    private readonly NumericUpDown _ultSlow = new() { Minimum = 1.2m, Maximum = 20, Increment = 0.5m, FormatString = "0.0", Width = 140 };
    private readonly NumericUpDown _ultLosses = new() { Minimum = 1, Maximum = 20, Increment = 1, FormatString = "0", Width = 140 };
    private readonly NumericUpDown _ultQuestMinutes = new() { Minimum = 0, Maximum = 600, Increment = 5, FormatString = "0", Width = 140 };
    private readonly NumericUpDown _ultStuck = new() { Minimum = 2, Maximum = 120, Increment = 1, FormatString = "0", Width = 140 };
    private readonly NumericUpDown _ultMaxHours = new() { Minimum = 0, Maximum = 24, Increment = 1, FormatString = "0", Width = 140 };
    private readonly StackPanel _ultIssues = new() { Spacing = 10 };
    private readonly TextBlock _ultIssuesEmpty = new() { TextWrapping = TextWrapping.Wrap, Foreground = MUTED, Text = "No issues yet." };
    private readonly StackPanel _ultBroken = new() { Spacing = 6 };
    private readonly TextBlock _ultBrokenEmpty = new() { TextWrapping = TextWrapping.Wrap, Foreground = MUTED, Text = "No quest problems yet." };
    private readonly DispatcherTimer _ultTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Border _ultBanner = new() { IsVisible = false };
    private readonly TextBlock _ultBannerText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private SelectableTextBlock? _ultLogBox;
    private ScrollViewer? _ultLogScroll;
    private TextBox? _ultQuestLogBox;
    private TextBox? _ultProgressBox;
    private string _ultProgressText = "";
    private string _ultLogText = "";
    // Ultimus Thoughts (Niduus, 2026-11-13): only its THOUGHT lines, in brown; and the bots' mistakes book.
    private SelectableTextBlock? _ultThoughtsBox;
    private ScrollViewer? _ultThoughtsScroll;
    private string _ultThoughtsText = "";
    private TextBox? _ultMistakesBox;
    private string _ultMistakesText = "";
    private const string THOUGHT_COLOR = "#C48A52";
    private string _ultQuestLogText = "";
    private DateTime _ultIssuesWritten = DateTime.MinValue;
    private List<UltimusIssue> _ultIssueList = [];
    private int _ultIssuesShown = -1;
    private bool _showingUlt;
    private bool _ultTimerStarted;

    private sealed class UltimusIssue {
        public int Id { get; set; }
        public DateTime At { get; set; }
        public string Kind { get; set; } = "";
        public string Quest { get; set; } = "";
        public string Title { get; set; } = "";
        public string World { get; set; } = "";
        public string Text { get; set; } = "";
        public string Expected { get; set; } = "";
        public List<string> Seen { get; set; } = [];
        public string Step { get; set; } = "";
        public List<string> Debug { get; set; } = [];
        public string Report { get; set; } = "";
        public List<string> Log { get; set; } = [];
    }

    private string UltimusFolder => Path.Combine(_manager.LogFolder, ULTIMUS_FOLDER);

    private void StartUltimusTimer() {
        if (_ultTimerStarted) {
            return;
        }

        _ultTimerStarted = true;
        _ultTimer.Tick += (_, _) => RefreshUltimus();
        _ultTimer.Start();
        Dispatcher.UIThread.Post(RefreshUltimus, DispatcherPriority.Background);
    }

    // ---------------------------------------------------------------------
    // Ultimus tab: the switch, Ultimus itself (gear, stats, deck, quest), what it is doing, the settings and Restart
    // ---------------------------------------------------------------------

    private Control UltimusTab() {
        _ultEnabled.IsCheckedChanged += async (_, _) => {
            if (!_showingUlt) {
                await Guard(() => SetUltimusEnabledAsync(_ultEnabled.IsChecked == true));
            }
        };
        foreach (var (box, key) in new[] { (_ultSlow, "SlowFactor"), (_ultLosses, "LossesBeforeIssue"), (_ultStuck, "StuckMinutes"), (_ultQuestMinutes, "QuestMinutes"), (_ultMaxHours, "MaxHoursPerDay") }) {
            box.ValueChanged += (_, _) => {
                if (!_showingUlt && box.Value is { } value) {
                    _ = Guard(() => SaveUltimusNumberAsync(key, value));
                }
            };
        }
        _ultAutoCoach.IsCheckedChanged += async (_, _) => {
            if (!_showingUlt) {
                await Guard(() => SetUltimusAutoCoachAsync(_ultAutoCoach.IsChecked == true));
            }
        };
        _ultParty.IsCheckedChanged += async (_, _) => {
            if (!_showingUlt) {
                await Guard(() => SetUltimusOnByDefaultAsync("PartyMode", _ultParty.IsChecked == true,
                    "Party mode is on: Ultimus takes a player's group invite and goes along with them.",
                    "Party mode is off: Ultimus turns group invites down and quests alone."));
            }
        };
        _ultEmotes.IsCheckedChanged += async (_, _) => {
            if (!_showingUlt) {
                await Guard(() => SetUltimusOnByDefaultAsync("Emotes", _ultEmotes.IsChecked == true,
                    "Ultimus's emotes are on: he waves, cheers and sits.", "Ultimus's emotes are off."));
            }
        };

        LoadUltimusSettings();
        StartUltimusTimer();
        ShowCardsOn(_ultSheet);

        return Page(
            SpaceNotice("Ultimus"),
            Fold("users", ULTIMUS_TAB, "Ultimus",
                "Ultimus is the Solo Quester: one Balance wizard the server plays by itself, alone, through the Main Story, from " +
                "level 1 in Wizard City to the last world. Its goal is the Main Story above all. It plays like a player: it walks " +
                "everywhere, talks to the quest givers, fights, trains and shops with its own gold. Players are welcome in its " +
                "battles, and it joins a player's battle when the creature its quest needs is in it. It never joins the Quest " +
                "Team, and the Quest Team keeps out of its way. When a quest does not work, a banner at the top of Ahziael says so, " +
                "and Ultimus Issues shows what happened with a debug report you can copy (or hand to Claude) to fix the quest. " +
                "Something odd it notices shows in pink in its log. It runs in a little program of its own next to the server; " +
                "it never touches your own wizards.",
                _ultState,
                UltimusDoorNote(),
                SwitchRow("Ultimus", _ultEnabled, "On: Ultimus logs in a few seconds later and starts questing. Off: it logs out."),
                _ultDoing,
                _ultToday,
                _ultQuest,
                _ultMembers,
                _ultSheet.View,
                UltimusThanks(),
                Row(Action(Plain("Pause or resume"), PauseUltimusAsync), Action(Plain("Pause and explain"), ExplainUltimusAsync), Action(Plain("Back to story"), BackToStoryAsync), Action(Plain("Stop and save"), StopUltimus), Action(Plain("Restart with a new Ultimus"), RestartUltimusAsync), Action(Plain("Open its folder"), () => {
                    Directory.CreateDirectory(UltimusFolder);

                    return Open(UltimusFolder);
                }))),
            Fold("sliders", ULTIMUS_TAB, "When to tell you",
                "When Ultimus counts something as an issue. Changes count right away.",
                SwitchRow("A quest is too slow at (times the expected time)", _ultSlow,
                    "2 means a quest that takes twice as long as expected is reported. The first time a quest is played its time is a guess; after that Ultimus learns the real time."),
                SwitchRow("Lost fights before it is an issue", _ultLosses,
                    "When Ultimus loses the same fight this many times, the quest is reported."),
                SwitchRow("Minutes with no progress before it is stuck", _ultStuck,
                    "When nothing moves forward for this long, Ultimus reports that it is stuck."),
                SwitchRow("Time limit for one quest (minutes)", _ultQuestMinutes,
                    "A quest that takes longer than this is reported, however long it was expected to take. 0 means no limit."),
                SwitchRow("Most hours he plays a day", _ultMaxHours,
                    "When he has played this many hours today he rests somewhere safe until tomorrow. 0 means no limit."),
                SwitchRow("Party mode", _ultParty,
                    "On: when a player invites Ultimus to a group, he joins and goes along with them: he teleports to them from the group, " +
                    "joins their battles and does the quest steps they both have, until the group ends or they go away for 15 minutes. " +
                    "Then he goes back to his own story. Off: he turns every group invite down. On by default."),
                SwitchRow("Emotes", _ultEmotes,
                    "On: Ultimus waves at a player who says hello near him, cheers after a tough win and sits down when he has nothing " +
                    "to do for a minute. He learns the emote names from players' own emote wheels, so if you don't see one, play that emote " +
                    "once near anyone. On by default."),
                SwitchRow("Nightly auto-coach", _ultAutoCoach,
                    "On: each night (after 4 AM) Ultimus puts all his logs in a dated folder, logs\\ultimus\\coach, each file on its own plus one joint file, ready to hand to the coach. The last 14 nights are kept. Off by default.")),
            UltimusLocksFold(),
            Fold("users", ULTIMUS_TAB, "Friends he made",
                "The players Ultimus has met: how often you talked, battles fought together, your school and world if you told him, " +
                "and when he last saw you. Muted means he is not answering that player for a while (rude or spamming). Updates by itself.",
                _ultFriends));
    }

    private void LoadUltimusSettings() {
        _showingUlt = true;
        try {
            _ultEnabled.IsChecked = _manager.GetSwitch("Ultimus", "Enabled");
            _ultAutoCoach.IsChecked = _manager.GetSwitch("Ultimus", "AutoCoach");
            _ultParty.IsChecked = !string.Equals(_manager.GetSetting("Ultimus", "PartyMode")?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
            _ultEmotes.IsChecked = !string.Equals(_manager.GetSetting("Ultimus", "Emotes")?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
            _ultSlow.Value = (decimal) Number("SlowFactor", 2);
            _ultLosses.Value = (decimal) Number("LossesBeforeIssue", 3);
            _ultStuck.Value = (decimal) Number("StuckMinutes", 8);
            _ultQuestMinutes.Value = (decimal) Number("QuestMinutes", 45);
            _ultMaxHours.Value = (decimal) Number("MaxHoursPerDay", 0);
        }
        finally {
            _showingUlt = false;
        }

        double Number(string key, double fallback)
            => double.TryParse(_manager.GetSetting("Ultimus", key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    private Task SetUltimusEnabledAsync(bool on) {
        if (!_manager.HasSetting("Ultimus", "Enabled")) {
            LoadUltimusSettings();
            throw new PanelException("This server's Imlight.ini has no [Ultimus] section yet. Click \"Update Ahziael\" once, then try again.");
        }

        _manager.SaveSettings([new Setting("Ultimus", "Enabled", on ? "true" : "false", "")]);
        _settings = [];
        Say(on ? "Ultimus is on. With the server running, it logs in within a few seconds and starts questing."
            : "Ultimus is off. It logs out within a few seconds.");
        RefreshUltimus();

        return Task.CompletedTask;
    }

    // A switch that is on when the ini does not have it yet (the server's default): saved as the key, added if missing.
    private Task SetUltimusOnByDefaultAsync(string key, bool on, string saidOn, string saidOff) {
        _manager.SaveSettings([new Setting("Ultimus", key, on ? "true" : "false", "")]);
        _settings = [];
        Say(on ? saidOn : saidOff);

        return Task.CompletedTask;
    }

    private Task SetUltimusAutoCoachAsync(bool on) {
        if (!_manager.HasSetting("Ultimus", "AutoCoach")) {
            LoadUltimusSettings();
            throw new PanelException("This server's Imlight.ini has no [Ultimus] AutoCoach yet. Click \"Update Ahziael\" once, then try again.");
        }

        _manager.SaveSettings([new Setting("Ultimus", "AutoCoach", on ? "true" : "false", "")]);
        _settings = [];
        Say(on ? "The nightly auto-coach is on: each night Ultimus's logs go in a dated folder in logs\\ultimus\\coach."
            : "The nightly auto-coach is off.");

        return Task.CompletedTask;
    }

    private Task SaveUltimusNumberAsync(string key, decimal value) {
        if (!_manager.HasSetting("Ultimus", key)) {
            throw new PanelException("This server's Imlight.ini has no [Ultimus] " + key + " yet. Click \"Update Ahziael\" once, then try again.");
        }

        var text = key == "SlowFactor" ? ((double) value).ToString("0.0", CultureInfo.InvariantCulture) : ((int) value).ToString(CultureInfo.InvariantCulture);
        if (_manager.GetSetting("Ultimus", key) == text) {
            return Task.CompletedTask;
        }

        _manager.SaveSettings([new Setting("Ultimus", key, text, "")]);
        _settings = [];
        Say("Saved. Ultimus uses it right away.");

        return Task.CompletedTask;
    }

    // Pause: Ultimus stands still where it is, still logged in, so you can walk over and look; a battle it is in still plays
    // out. The file is there while paused.
    private async Task PauseUltimusAsync() {
        Directory.CreateDirectory(UltimusFolder);
        var file = Path.Combine(UltimusFolder, "paused.txt");
        if (File.Exists(file)) {
            File.Delete(file);
            Say("Resumed. Ultimus goes on with its quest.");

            return;
        }

        await File.WriteAllTextAsync(file, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        Say("Paused. Ultimus stands where it is and stays logged in (a battle it is in still finishes). Click again to resume.");
    }

    // Pause and explain (Niduus, 2026-11-14): the server pauses him and writes his whole plan (where he is, his goals, his
    // next move and how much he trusts it, his mood, his last thoughts) to explain.txt, which opens here.
    private async Task ExplainUltimusAsync() {
        Directory.CreateDirectory(UltimusFolder);
        var answer = Path.Combine(UltimusFolder, "explain.txt");
        var before = File.Exists(answer) ? File.GetLastWriteTimeUtc(answer) : DateTime.MinValue;
        await File.WriteAllTextAsync(Path.Combine(UltimusFolder, "explain-request.txt"), DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        Say("Asking Ultimus to pause and explain himself...");
        for (var i = 0; i < 20; i++) {
            await Task.Delay(500);
            if (File.Exists(answer) && File.GetLastWriteTimeUtc(answer) > before) {
                Say("Ultimus is paused and explained his plan (explain.txt). Click Pause or resume to let him go on.");
                await Open(answer);

                return;
            }
        }

        Say("Ultimus did not answer. Is the server running with Ultimus on and in the game?");
    }

    // The friends page: logs\bots\social-ultimus.json (his memory of each player he talked with) and logs\ultimus\players.json
    // (everyone he met), read again when either changes.
    private void ShowUltimusFriends() {
        var social = Path.Combine(_manager.LogFolder, "bots", "social-ultimus.json");
        var met = Path.Combine(UltimusFolder, "players.json");
        var changed = new[] { social, met }.Where(File.Exists).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
        if (changed <= _ultFriendsRead && _ultFriends.Text?.Length > 0) {
            return;
        }

        _ultFriendsRead = changed;
        var rows = new Dictionary<string, (string Name, int Talks, int Battles, string School, string World, DateTime Last, DateTime Muted, int Met)>();
        try {
            if (File.Exists(social) && JsonNode.Parse(File.ReadAllText(social)) is JsonObject friends) {
                foreach (var (id, node) in friends) {
                    if (node is not JsonObject f) {
                        continue;
                    }

                    rows[id] = ((string?) f["Name"] ?? "", (int?) f["Talks"] ?? 0, (int?) f["BattlesTogether"] ?? 0, (string?) f["School"] ?? "",
                        (string?) f["World"] ?? "", (DateTime?) f["Last"] ?? DateTime.MinValue, (DateTime?) f["MutedUntil"] ?? DateTime.MinValue, 0);
                }
            }

            if (File.Exists(met) && JsonNode.Parse(File.ReadAllText(met)) is JsonObject players) {
                foreach (var (id, node) in players) {
                    if (node is not JsonObject p) {
                        continue;
                    }

                    var times = (int?) p["Times"] ?? 0;
                    var last = (DateTime?) p["Last"] ?? DateTime.MinValue;
                    rows[id] = rows.TryGetValue(id, out var row)
                        ? row with { Met = times, Last = row.Last > last ? row.Last : last }
                        : ((string?) p["Name"] ?? "", 0, 0, "", "", last, DateTime.MinValue, times);
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or FormatException) {
            return;
        }

        if (rows.Count == 0) {
            _ultFriends.Text = "Nobody yet. Walk up to Ultimus in the game and say hi (typed or Menu Chat), or whisper to him.";
            return;
        }

        var text = new StringBuilder();
        foreach (var row in rows.Values.OrderByDescending(r => r.Last)) {
            var parts = new List<string> { $"talked {row.Talks} time(s)" };
            if (row.Met > 0) {
                parts.Add($"met {row.Met} time(s)");
            }

            if (row.Battles > 0) {
                parts.Add($"{row.Battles} battle(s) together");
            }

            if (row.School.Length > 0) {
                parts.Add(row.School);
            }

            if (row.World.Length > 0) {
                parts.Add("questing in " + row.World);
            }

            if (row.Muted > DateTime.UtcNow) {
                parts.Add($"muted until {row.Muted.ToLocalTime():HH:mm}");
            }

            var seen = row.Last == DateTime.MinValue ? "" : $" (last seen {row.Last.ToLocalTime():d MMM, HH:mm})";
            text.AppendLine($"{(row.Name.Length > 0 ? row.Name : "a player")}: {string.Join(", ", parts)}{seen}");
        }

        _ultFriends.Text = text.ToString().TrimEnd();
    }

    // Back to story (the guardrails, Niduus 2026-11-14): he drops any side trip, a pause is lifted, and he plans again toward
    // his story quest.
    private async Task BackToStoryAsync() {
        Directory.CreateDirectory(UltimusFolder);
        await File.WriteAllTextAsync(Path.Combine(UltimusFolder, "back-to-story-request.txt"), DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        Say("Sent. Ultimus drops any side trip (no shops or trainers for 30 minutes) and heads back to his story quest within a second or two.");
    }

    // ---------------------------------------------------------------------
    // His own numbers: self-tuning on or off, and a lock per rule (self-tuning.txt)
    // ---------------------------------------------------------------------

    private Control UltimusLocksFold() {
        var rows = new List<Control> {
            SwitchRow("He may tune his own numbers", _ultTuneAuto,
                "On: at his daily review he changes a few numbers by a little, only inside the ranges in self-tuning.txt, and writes why in learned-today.txt. Off: he leaves them all alone."),
        };
        foreach (var (rule, name, help) in s_ultTunable) {
            var toggle = new ToggleSwitch { OnContent = "Locked", OffContent = "He may tune it" };
            _ultLocks[rule] = toggle;
            rows.Add(SwitchRow($"{name} ({rule})", toggle, help + " Locked: he keeps it as it is."));
            toggle.IsCheckedChanged += (_, _) => {
                if (!_showingUlt) {
                    _ = Guard(SaveUltimusTuningAsync);
                }
            };
        }

        _ultTuneAuto.IsCheckedChanged += (_, _) => {
            if (!_showingUlt) {
                _ = Guard(SaveUltimusTuningAsync);
            }
        };
        LoadUltimusTuning();

        return Fold("lock", ULTIMUS_TAB, "His own numbers",
            "Ultimus changes a few of his own numbers at his daily review, by a little and only inside the limits you set. " +
            "His hard limits (no skipping, no free XP, gold or items, no crafting or pet training, staying in bounds, side areas " +
            "only when a quest names them) and his time and risk budgets are yours alone: he can never change them. Changes count right away.",
            [.. rows]);
    }

    private string UltimusTuningFile => Path.Combine(UltimusFolder, "self-tuning.txt");

    private void LoadUltimusTuning() {
        _showingUlt = true;
        try {
            var lines = File.Exists(UltimusTuningFile) ? File.ReadAllLines(UltimusTuningFile) : [];
            string Value(string key) => lines.Select(l => l.Trim()).Where(l => !l.StartsWith('#') && l.Contains('='))
                .Where(l => l[..l.IndexOf('=')].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                .Select(l => l[(l.IndexOf('=') + 1)..].Trim()).LastOrDefault() ?? "";
            var auto = Value("Auto");
            _ultTuneAuto.IsChecked = !(auto.StartsWith("off", StringComparison.OrdinalIgnoreCase) || auto.StartsWith("no", StringComparison.OrdinalIgnoreCase) || auto == "0");
            var locked = Value("Locked").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var (rule, toggle) in _ultLocks) {
                toggle.IsChecked = locked.Contains(rule, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (IOException) {
            // Shown as it was; the next change writes it again.
        }
        finally {
            _showingUlt = false;
        }
    }

    // Writes the Auto and Locked lines of self-tuning.txt, keeping everything else (his ranges) as it is.
    private async Task SaveUltimusTuningAsync() {
        Directory.CreateDirectory(UltimusFolder);
        var lines = File.Exists(UltimusTuningFile) ? (await File.ReadAllLinesAsync(UltimusTuningFile)).ToList() : [
            "# What Ultimus may change in his own playbook at his daily review, and how far.",
            "Quest.ReadyHealth = 0.6 to 0.95",
            "Battle.HealAt = 0.35 to 0.7",
            "Confidence.Careful = 30 to 70",
        ];
        static bool Is(string line, string key) => !line.TrimStart().StartsWith('#') && line.Contains('=')
                                                   && line[..line.IndexOf('=')].Trim().Equals(key, StringComparison.OrdinalIgnoreCase);
        lines.RemoveAll(l => Is(l, "Auto") || Is(l, "Locked"));
        var at = lines.FindIndex(l => !l.TrimStart().StartsWith('#'));
        at = at < 0 ? lines.Count : at;
        var locked = _ultLocks.Where(l => l.Value.IsChecked == true).Select(l => l.Key).ToList();
        lines.Insert(at, "Locked = " + string.Join(", ", locked));
        lines.Insert(at, "Auto = " + (_ultTuneAuto.IsChecked == true ? "on" : "off"));
        await File.WriteAllLinesAsync(UltimusTuningFile, lines);
        Say(_ultTuneAuto.IsChecked != true ? "Saved. Ultimus leaves all his numbers alone."
            : locked.Count == 0 ? "Saved. Ultimus may tune his numbers inside your limits."
            : $"Saved. Ultimus may tune his numbers inside your limits, except {string.Join(", ", locked)}.");
    }

    // Stop and save: Ultimus logs out cleanly where it is (its progress is its own, saved as it goes); turning it on again
    // picks up from the same step.
    private async Task StopUltimus() {
        _showingUlt = true;
        _ultEnabled.IsChecked = false;
        _showingUlt = false;
        await SetUltimusEnabledAsync(false);
        Say("Stopped. Ultimus logs out where it is, with its quests and steps kept. Turn Ultimus on to pick up from there.");
    }

    private async Task RestartUltimusAsync() {
        if (!await ConfirmAsync("Make a new level 1 Ultimus? The one playing now is deleted, and the new one " +
                                "starts again at the beginning of Wizard City. Its issues, reports and quest log stay.")) {
            return;
        }

        Directory.CreateDirectory(UltimusFolder);
        await File.WriteAllTextAsync(Path.Combine(UltimusFolder, "restart-request.txt"), DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        Say(InstalledCommands.IsServerRunning(_manager.InstallFolder) && _ultEnabled.IsChecked == true
            ? "Asked. Ultimus logs out, a new one is made, and it logs in again within half a minute."
            : "Asked. A new Ultimus is made the next time it runs (server started and Ultimus on).");
    }

    // ---------------------------------------------------------------------
    // Ultimus Issues tab
    // ---------------------------------------------------------------------

    private Control UltimusIssuesTab() {
        StartUltimusTimer();

        return Page(Fold("list", ULTIMUS_TAB, "Broken quests",
            "One line for each quest Ultimus found a problem with, with the latest reason and how many times it came up. " +
            "\"Found ahead\" means the quest check spotted it before he got there (he reads ahead at the start of each world; " +
            "Run read-ahead now checks every story world at once). " +
            "He never skips a step: he keeps trying it as a player would until it is fixed. The full reports are below.",
            Row(Action(Plain("Copy the list"), () => CopyAsync(UltimusBrokenText())), Action(Plain("Run read-ahead now"), async () => {
                // Every story world at once (Niduus, 2026-11-14): the server runs its quest check fresh, then files what it finds.
                Directory.CreateDirectory(UltimusFolder);
                await File.WriteAllTextAsync(Path.Combine(UltimusFolder, "read-ahead-request.txt"), DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                Say(InstalledCommands.IsServerRunning(_manager.InstallFolder) && _ultEnabled.IsChecked == true
                    ? "Asked. The quest check runs fresh (a few minutes), then every story world's problems show in this list and in read-ahead.txt."
                    : "Asked. It runs once the server is started and Ultimus is in the game.");
            })),
            _ultBrokenEmpty,
            _ultBroken),
            Fold("flag", ULTIMUS_TAB, "Ultimus Issues",
            "Everything Ultimus ran into, newest first: a quest that does not work, Ultimus being stuck, a fight lost again " +
            "and again, or a quest that took much longer than expected. Each one has a debug report: what went wrong, what a " +
            "player would expect, where Ultimus was, the quest's and step's codes and data file, and the log that led up to " +
            "it. Copy a report (or open the debug folder) and hand it to Claude to fix the quest.",
            Row(Action(Plain("Copy all"), () => CopyAsync(UltimusIssuesText(_ultIssueList))), Action(Plain("Open its folder"), () => {
                Directory.CreateDirectory(UltimusFolder);

                return Open(UltimusFolder);
            })),
            _ultIssuesEmpty,
            _ultIssues));
    }

    private static string UltimusIssueKind(string kind) => kind switch {
        "quest" => "Quest problem",
        "team" => "Ultimus stuck",
        "slow" => "Taking too long",
        _ => "Issue",
    };

    private static string UltimusIssuesText(IEnumerable<UltimusIssue> issues) {
        var text = new StringBuilder();
        foreach (var issue in issues) {
            text.AppendLine($"#{issue.Id}  {issue.At.ToLocalTime():yyyy-MM-dd HH:mm}  {UltimusIssueKind(issue.Kind)}  {issue.Title} {(issue.World.Length > 0 ? $"({issue.World})" : "")}");
            text.AppendLine(issue.Text);
            if (issue.Expected.Length > 0) {
                text.AppendLine("Expected (as a player): " + issue.Expected);
            }

            foreach (var line in issue.Seen ?? []) {
                text.AppendLine("  " + line);
            }

            foreach (var line in issue.Debug ?? []) {
                text.AppendLine("  " + line);
            }

            foreach (var line in issue.Log) {
                text.AppendLine("    " + line);
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    // The issue's own report file (debug/issue-0001.txt), or the same made from the issue when the file is not there.
    private string UltimusReportText(UltimusIssue issue) {
        try {
            if (issue.Report.Length > 0 && Path.Combine(UltimusFolder, issue.Report) is var path && File.Exists(path)) {
                return File.ReadAllText(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Made from the issue below.
        }

        return UltimusIssuesText([issue]);
    }

    // "Fixed it" (Niduus, 2026-11-14): tells Ultimus to try the quest again at once from a clean slate (fixed-request.txt),
    // and hides it from the list until it is reported again (fixed-quests.txt keeps when it was marked).
    private Control FixedItButton(string quest, string title) {
        var row = Row(Action(Plain("Fixed it"), async () => {
            Directory.CreateDirectory(UltimusFolder);
            var at = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            await File.AppendAllTextAsync(Path.Combine(UltimusFolder, "fixed-request.txt"), quest + Environment.NewLine);
            await File.AppendAllTextAsync(Path.Combine(UltimusFolder, "fixed-quests.txt"), $"{quest}\t{at}{Environment.NewLine}");
            _ultIssuesShown = -1;
            ShowUltimusIssues();
            Say(InstalledCommands.IsServerRunning(_manager.InstallFolder) && _ultEnabled.IsChecked == true
                ? $"Marked fixed: {title}. Ultimus tries it again within a few seconds, and it comes back here only if it still goes wrong."
                : $"Marked fixed: {title}. Ultimus tries it again once the server is on and he is in the game.");
        }));
        ToolTip.SetTip(row, "You fixed this quest: Ultimus forgets what went wrong and tries it again right away.");

        return row;
    }

    // When each quest was marked fixed (newest wins).
    private Dictionary<string, DateTime> UltimusFixedQuests() {
        var fixedAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try {
            var path = Path.Combine(UltimusFolder, "fixed-quests.txt");
            if (File.Exists(path)) {
                foreach (var parts in File.ReadAllLines(path).Select(l => l.Split('\t')).Where(p => p.Length == 2)) {
                    if (DateTime.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)) {
                        fixedAt[parts[0]] = at;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Nothing marked.
        }

        return fixedAt;
    }

    // One entry per quest with a "quest" issue: newest reason, how many times, found ahead or met while playing. A quest
    // marked fixed shows again only when it is reported after that.
    private List<(string Quest, string Title, string World, int Count, bool Ahead, UltimusIssue Latest)> UltimusBrokenList() {
        var fixedAt = UltimusFixedQuests();
        return [.. _ultIssueList.Where(i => i.Kind == "quest" && i.Quest.Length > 0
                                       && (!fixedAt.TryGetValue(i.Quest, out var at) || i.At.ToUniversalTime() > at.ToUniversalTime()))
            .GroupBy(i => i.Quest, StringComparer.OrdinalIgnoreCase)
            .Select(g => {
                var latest = g.OrderByDescending(i => i.Id).First();
                var met = g.Where(i => !i.Text.StartsWith("READ-AHEAD", StringComparison.Ordinal)).OrderByDescending(i => i.Id).FirstOrDefault();
                return (g.Key, latest.Title.Length > 0 ? latest.Title : g.Key, latest.World, g.Count(), met is null, met ?? latest);
            })
            .OrderByDescending(b => b.Item6.Id)];
    }

    private static string UltimusBrokenReason(string text) {
        var reason = text.StartsWith("READ-AHEAD", StringComparison.Ordinal) && text.IndexOf("): ", StringComparison.Ordinal) is var cut and > 0
            ? text[(cut + 3)..] : text;
        var end = reason.IndexOf(" Diagnosis", StringComparison.Ordinal);
        reason = end > 0 ? reason[..end] : reason;
        return reason.Length > 260 ? reason[..257] + "..." : reason;
    }

    private string UltimusBrokenText() {
        var text = new StringBuilder("QUESTS ULTIMUS FOUND A PROBLEM WITH\n");
        foreach (var b in UltimusBrokenList()) {
            text.AppendLine($"{b.Title} ({b.Quest}{(b.World.Length > 0 ? ", " + b.World : "")}): {b.Count} time(s){(b.Ahead ? ", found ahead" : "")}. {UltimusBrokenReason(b.Latest.Text)}");
        }

        return text.ToString();
    }

    private void ShowUltimusBroken() {
        _ultBroken.Children.Clear();
        var list = UltimusBrokenList();
        _ultBrokenEmpty.IsVisible = list.Count == 0;
        foreach (var b in list.Take(60)) {
            _ultBroken.Children.Add(new Border {
                Background = ManagerTheme.SURFACE_HIGH_BRUSH, CornerRadius = new CornerRadius(ManagerTheme.RADIUS_SMALL), Padding = new Thickness(12, 8),
                Child = new StackPanel {
                    Spacing = 2,
                    Children = {
                        new TextBlock {
                            Text = $"{b.Title}{(b.World.Length > 0 ? $" ({b.World})" : "")}", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
                            Foreground = b.Ahead ? ManagerTheme.Accent("Progress") : ManagerTheme.Accent("Create"),
                        },
                        new TextBlock {
                            Text = $"{(b.Ahead ? "Found ahead" : "Met while playing")}  ·  {b.Count} time(s)  ·  last {b.Latest.At.ToLocalTime():ddd d MMM, HH:mm}  ·  {b.Quest}",
                            Foreground = MUTED, FontSize = ManagerTheme.FONT_SMALL, TextWrapping = TextWrapping.Wrap,
                        },
                        new TextBlock { Text = UltimusBrokenReason(b.Latest.Text), TextWrapping = TextWrapping.Wrap },
                        FixedItButton(b.Quest, b.Title),
                    },
                },
            });
        }
    }

    private void ShowUltimusIssues() {
        if (_ultIssuesShown == (_ultIssueList.Count == 0 ? 0 : _ultIssueList.Max(i => i.Id))) {
            return;
        }

        _ultIssuesShown = _ultIssueList.Count == 0 ? 0 : _ultIssueList.Max(i => i.Id);
        _ultIssues.Children.Clear();
        _ultIssuesEmpty.IsVisible = _ultIssueList.Count == 0;
        ShowUltimusBroken();
        foreach (var issue in _ultIssueList.OrderByDescending(i => i.Id).Take(100)) {
            var head = new TextBlock {
                Text = $"{UltimusIssueKind(issue.Kind)}: {(issue.Title.Length > 0 ? issue.Title : "Ultimus")}{(issue.World.Length > 0 ? $" ({issue.World})" : "")}",
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
            var logFold = new Expander { Header = "What happened (Ultimus's log)", Content = LogWell(log), HorizontalAlignment = HorizontalAlignment.Stretch };
            var facts = new TextBox {
                IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = MONO, FontSize = 12,
                MaxHeight = 260, Text = (issue.Debug?.Count ?? 0) == 0 ? "(no debug facts)" : string.Join('\n', issue.Debug!),
            };
            var debugFold = new Expander { Header = "Debug facts (quest and step codes, game data)", Content = LogWell(facts), HorizontalAlignment = HorizontalAlignment.Stretch };
            var shown = issue;
            var copy = Row(Action(Plain("Copy debug report"), () => CopyAsync(UltimusReportText(shown))));
            ToolTip.SetTip(copy, "Copies everything about this issue in one go, ready to paste to Claude to fix the quest.");
            _ultIssues.Children.Add(new Border {
                Background = ManagerTheme.SURFACE_HIGH_BRUSH, CornerRadius = new CornerRadius(ManagerTheme.RADIUS_SMALL), Padding = new Thickness(14, 10),
                Child = new StackPanel { Spacing = 6, Children = { head, when, body, expected, seen, debugFold, logFold, copy } },
            });
        }
    }

    // ---------------------------------------------------------------------
    // Ultimus Logs tab
    // ---------------------------------------------------------------------

    private Control UltimusLogsTab() {
        StartUltimusTimer();
        _ultQuestLogBox = new TextBox {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = MONO, FontSize = 12.5,
            Height = 300, Text = "Turn Ultimus on and start the server; every quest it starts and finishes is written here.",
        };
        _ultLogBox = new SelectableTextBlock {
            TextWrapping = TextWrapping.Wrap, FontFamily = MONO, FontSize = 12.5, Margin = new Thickness(10, 8),
            Text = "Turn Ultimus on and start the server; what it does is written here.",
        };
        _ultLogScroll = new ScrollViewer { Height = 420, Content = _ultLogBox, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        _ultThoughtsBox = new SelectableTextBlock {
            TextWrapping = TextWrapping.Wrap, FontFamily = MONO, FontSize = 12.5, Margin = new Thickness(10, 8),
            Foreground = new SolidColorBrush(Color.Parse(THOUGHT_COLOR)),
            Text = "Turn Ultimus on and start the server; what it thinks, and why, is written here.",
        };
        _ultThoughtsScroll = new ScrollViewer { Height = 320, Content = _ultThoughtsBox, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        _ultMistakesBox = new TextBox {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = MONO, FontSize = 12.5,
            Height = 300, Text = "No mistakes written down yet.",
        };

        _ultProgressBox = new TextBox {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = MONO, FontSize = 12.5,
            Height = 220, Text = "Turn Ultimus on and start the server; how far he got is written here.",
        };

        return Page(
            Fold("clock", ULTIMUS_TAB, "Progress",
                "How far Ultimus got: his level and area, the story world he is on, how long each world took, how long since " +
                "he last finished a step or gained XP (his watchdog raises the alarm when that is too long), his mood and " +
                "today's counts. Under it, after an update, the story steps that ask to click things which he may have to search for " +
                "(use-steps.txt). Updated every 30 seconds (progress.txt in his folder).",
                LogWell(_ultProgressBox),
                Row(Action(Plain("Copy all"), () => CopyAsync(_ultProgressText)))),
            UltimusDiaryFold(),
            Fold("copy", ULTIMUS_TAB, "All of Ultimus's logs in one copy",
                "Copies every Ultimus log at once, ready to paste in one go: his log, his thoughts, the mistakes book, the " +
                "quest log, his abnormal fixes, the creatures he fought, his status, what he learned and his playbook. " +
                "Newest first, and his main log only the last 3 hours, so the part that matters fits in one paste. Each line " +
                "starts with a tag naming the log it came from, such as [Main], [Thoughts] or [Mistakes]. To share everything " +
                "in full, Save for coach puts every log in a folder you pick, each as its own file plus one joint file, ready to " +
                "attach in the chat; Save joint file saves just the one file with all of them.",
                Row(Action(Plain("Copy all logs"), () => CopyAsync(UltimusAllLogs(), "All of Ultimus's logs were copied.")),
                    Action(Plain("Save for coach (separate files)"), SaveUltimusForCoachAsync),
                    Action(Plain("Save joint file"), SaveUltimusJointFileAsync))),
            Fold("sparkle", ULTIMUS_TAB, "Ultimus Thoughts",
                "Ultimus thinking out loud: why it picks what it picks, what it remembers of a creature or a spot where it got " +
                "stuck, and what it will do differently next time, naming the playbook rule behind a choice. Also in its log, " +
                "in brown. Updated every 3 seconds. His playbook (the rules and numbers he plays by) can be changed: open it, " +
                "change a number, save.",
                LogWell(_ultThoughtsScroll),
                Row(Action(Plain("Copy all"), () => CopyAsync(_ultThoughtsText)), Action(Plain("Open his playbook"), () => {
                    // His rules and the reasons he gives (playbook.txt, written by the server the first time he plays):
                    // change a number and save, and he uses it within a few seconds.
                    var playbook = Path.Combine(UltimusFolder, "playbook.txt");

                    return Open(File.Exists(playbook) ? playbook : UltimusFolder);
                }), Action(Plain("Undo his self-tuning"), async () => {
                    // Every number he changed himself goes back to its start (a lesson that turned out bad).
                    Directory.CreateDirectory(UltimusFolder);
                    await File.WriteAllTextAsync(Path.Combine(UltimusFolder, "undo-tuning-request.txt"), DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                    Say("Sent. Every number Ultimus changed himself goes back to where it started within a few seconds.");
                }), Action(Plain("Open his self-tuning limits"), () => {
                    // How far he may change his own numbers at his daily review (self-tuning.txt; Auto = off stops it).
                    var limits = Path.Combine(UltimusFolder, "self-tuning.txt");

                    return Open(File.Exists(limits) ? limits : UltimusFolder);
                }), Action(Plain("Open today I learned"), () => {
                    var today = Path.Combine(UltimusFolder, "learned-today.txt");

                    return Open(File.Exists(today) ? today : UltimusFolder);
                }))),
            Fold("book", ULTIMUS_TAB, "Mistakes book",
                "Every mistake Ultimus made, newest first: what went wrong and where, its reasoning at the time, what it tried, " +
                "what worked and what prevents it. The Quest Team reads it when it gets stuck at the same spot. " +
                "Kept in the server's logs\\bots folder (mistakes.txt to read, mistakes.json for the bots).",
                LogWell(_ultMistakesBox),
                Row(Action(Plain("Copy all"), () => CopyAsync(_ultMistakesText)), Action(Plain("Open its folder"), () => {
                    var folder = Path.Combine(_manager.LogFolder, "bots");
                    Directory.CreateDirectory(folder);

                    return Open(folder);
                }))),
            Fold("flag", ULTIMUS_TAB, "Quest log",
                "Every quest Ultimus started and finished, each step, how long it took and how long it was expected to take.",
                LogWell(_ultQuestLogBox),
                Row(Action(Plain("Copy all"), () => CopyAsync(_ultQuestLogText)))),
            Fold("users", ULTIMUS_TAB, "Ultimus log",
                "What Ultimus does: where it walks, who it talks to, what it trains, each spell it casts in a battle and why, " +
                "and why it waits. Updated every 3 seconds.",
                UltimusLogColors(),
                LogWell(_ultLogScroll),
                Row(Action(Plain("Copy all"), () => CopyAsync(_ultLogText)), Action(Plain("Open its folder"), () => {
                    Directory.CreateDirectory(UltimusFolder);

                    return Open(UltimusFolder);
                }))));
    }

    // ---------------------------------------------------------------------
    // The banner at the top of Ahziael
    // ---------------------------------------------------------------------

    private Control UltimusBanner() {
        var bar = ManagerTheme.GlassPanel(new DockPanel {
            Children = {
                WithDock(new StackPanel {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = {
                        Action(Primary("Show what happened"), () => {
                            SelectTab(_ultBannerMilestone ? ULTIMUS_LOGS_TAB : ULTIMUS_ISSUES_TAB);
                            MarkUltimusIssuesSeen();

                            return Task.CompletedTask;
                        }),
                        Action(Plain("Dismiss"), () => {
                            MarkUltimusIssuesSeen();

                            return Task.CompletedTask;
                        }),
                    },
                }, Dock.Right),
                WithDock(WithMargin(ManagerTheme.Tile("flag", ManagerTheme.Accent("Create"), 26), new Thickness(0, 0, 10, 0)), Dock.Left),
                WithCenter(_ultBannerText),
            },
        }, 14);
        bar.Padding = new Thickness(12, 6);
        bar.Margin = new Thickness(12, 8, 12, 0);
        _ultBanner.Child = bar;
        StartUltimusTimer();

        return _ultBanner;
    }

    private int UltimusIssuesSeen() {
        try {
            var path = Path.Combine(UltimusFolder, "issues-seen.txt");

            return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var seen) ? seen : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return int.MaxValue;
        }
    }

    private bool _ultBannerMilestone;
    private int _ultMilestoneNewest;

    // The newest milestone not shown yet (milestones.jsonl, one JSON line each; milestones-seen.txt holds the last seen).
    private string? NewUltimusMilestone() {
        try {
            var path = Path.Combine(UltimusFolder, "milestones.jsonl");
            if (!File.Exists(path)) {
                return null;
            }

            var seenPath = Path.Combine(UltimusFolder, "milestones-seen.txt");
            var seen = File.Exists(seenPath) && int.TryParse(File.ReadAllText(seenPath).Trim(), out var s) ? s : 0;
            string? text = null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line) {
                if (line.Trim().Length == 0) {
                    continue;
                }

                using var json = JsonDocument.Parse(line);
                var id = json.RootElement.GetProperty("id").GetInt32();
                if (id > seen) {
                    _ultMilestoneNewest = id;
                    text = json.RootElement.GetProperty("text").GetString();
                }
            }

            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) {
            return null;
        }
    }

    private void MarkUltimusIssuesSeen() {
        var newest = _ultIssueList.Count == 0 ? 0 : _ultIssueList.Max(i => i.Id);
        try {
            Directory.CreateDirectory(UltimusFolder);
            if (_ultMilestoneNewest > 0) {
                File.WriteAllText(Path.Combine(UltimusFolder, "milestones-seen.txt"), _ultMilestoneNewest.ToString(CultureInfo.InvariantCulture));
            }

            File.WriteAllText(Path.Combine(UltimusFolder, "issues-seen.txt"), newest.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Shown again next time; nothing lost.
        }

        _ultBanner.IsVisible = false;
    }

    // ---------------------------------------------------------------------
    // Reading the team's files
    // ---------------------------------------------------------------------

    private void RefreshUltimus() {
        ReadUltimusIssues();

        // The banner: issues newer than the last one Ahziael showed.
        var seen = UltimusIssuesSeen();
        var fresh = _ultIssueList.Where(i => i.Id > seen).OrderBy(i => i.Id).ToList();
        if (_ultIssues.IsEffectivelyVisible) {
            ShowUltimusIssues();
            if (fresh.Count > 0) {
                MarkUltimusIssuesSeen();
                fresh.Clear();
            }
        }

        if (fresh.Count > 0) {
            var last = fresh[^1];
            _ultBannerText.Text = (fresh.Count == 1 ? "Ultimus found an issue: " : $"Ultimus found {fresh.Count} issues. Newest: ") +
                                   $"{UltimusIssueKind(last.Kind).ToLowerInvariant()}, {(last.Title.Length > 0 ? last.Title : "the Ultimus")}. {last.Text}";
        }

        // Milestones (a world finished, a level up, badly stuck, a self-test), shown when no issue is (Niduus, 2026-11-14).
        var milestone = fresh.Count == 0 ? NewUltimusMilestone() : null;
        _ultBannerMilestone = milestone is not null;
        if (milestone is not null) {
            _ultBannerText.Text = "Ultimus milestone: " + milestone;
        }

        _ultBanner.IsVisible = fresh.Count > 0 || milestone is not null;

        if (_ultFriends.IsEffectivelyVisible) {
            ShowUltimusFriends();
        }

        if (_ultState.IsEffectivelyVisible) {
            ShowUltimusStatus();
            ShowUltimusDoorNote();
        }

        if (_ultLogBox?.IsEffectivelyVisible == true) {
            ShowUltimusColoredLog(ReadUltimusNewest("ultimus*.txt", _ultLogText));
            ShowUltimusThoughts(_ultLogText);
            ShowUltimusMistakes();
            ShowLog(_ultQuestLogBox!, ReadUltimusNewest("quest-log.txt", _ultQuestLogText), ref _ultQuestLogText);
            var progress = ReadAllOf(Path.Combine(UltimusFolder, "progress.txt"), 32 * 1024);
            if (progress.Length > 0 && UseStepsTop() is { Length: > 0 } useSteps) {
                progress = progress.TrimEnd() + "\n\n" + useSteps;
            }

            if (progress.Length > 0 && progress != _ultProgressText) {
                _ultProgressText = progress;
                _ultProgressBox!.Text = progress;
            }

            ShowUltimusDiary();
        }

        // The top of his check of the story's "click these things" steps (written after an update): the steps worth a look,
        // shown under his progress so it is seen without opening files (Niduus, 2026-11-14).
        string UseStepsTop() {
            var text = ReadAllOf(Path.Combine(UltimusFolder, "use-steps.txt"), 64 * 1024);
            if (text.Length == 0) {
                return "";
            }

            var lines = text.Replace("\r", "").Split('\n').TakeWhile(l => l.Trim() != "PLACED").ToList();
            var shown = lines.Take(23).ToList();
            if (lines.Count > shown.Count) {
                shown.Add($"... and {lines.Count - shown.Count} more (use-steps.txt in his folder)");
            }

            return string.Join('\n', shown).TrimEnd();
        }

        static void ShowLog(TextBox box, string text, ref string shown) {
            if (text == shown || text.Length == 0) {
                return;
            }

            shown = text;
            box.Text = string.Join('\n', text.Split('\n').TakeLast(ULTIMUS_LOG_LINES));
            box.CaretIndex = box.Text.Length;
        }
    }

    // Log colours, the same as the Quest Team's (Niduus, 2026-11-12), plus pink for something abnormal it noticed: white normal; yellow minor (does not hold the quest back); orange notable (could
    // stop a quest finishing); red critical (stops or stopped the story, fix it now); cyan a command Ahziael ran for them,
    // with the reason. The server tags each line with the word (UltimusLink.Note), so a copied log still says it.
    private static readonly (string Tag, string Color, string Meaning)[] s_ultLogLevels = [
        ("CRITICAL", "#FF5A5A", "Red: critical, stops or stopped the story, fix it now"),
        ("NOTABLE", "#FFA040", "Orange: notable, could stop a quest from finishing"),
        ("MINOR", "#FFE45C", "Yellow: minor, does not hold the quest back"),
        ("COMMAND", "#5CE1FF", "Cyan: something Ahziael did for it, and why"),
        ("ABNORMAL", "#FF6EC7", "Pink: something abnormal it noticed"),
        ("THOUGHT", THOUGHT_COLOR, "Brown: Ultimus thinking out loud"),
    ];

    private static IBrush UltimusLogBrush(string line) {
        foreach (var (tag, color, _) in s_ultLogLevels) {
            if (line.Contains(": " + tag + ": ", StringComparison.Ordinal)) {
                return new SolidColorBrush(Color.Parse(color));
            }
        }

        return ManagerTheme.TEXT_BRUSH;
    }

    private static Control UltimusLogColors() {
        var row = new WrapPanel();
        row.Children.Add(new TextBlock { Text = "White: normal", Foreground = ManagerTheme.TEXT_BRUSH, Margin = new Thickness(0, 0, 16, 4), FontSize = ManagerTheme.FONT_SMALL });
        foreach (var (_, color, meaning) in s_ultLogLevels) {
            row.Children.Add(new TextBlock { Text = meaning, Foreground = new SolidColorBrush(Color.Parse(color)), Margin = new Thickness(0, 0, 16, 4), FontSize = ManagerTheme.FONT_SMALL });
        }

        return row;
    }

    private void ShowUltimusColoredLog(string text) {
        if (_ultLogBox is null || text == _ultLogText || text.Length == 0) {
            return;
        }

        _ultLogText = text;
        var atEnd = _ultLogScroll is null || _ultLogScroll.Offset.Y >= _ultLogScroll.Extent.Height - _ultLogScroll.Viewport.Height - 20;
        var inlines = new Avalonia.Controls.Documents.InlineCollection();
        foreach (var line in text.Split('\n').TakeLast(ULTIMUS_LOG_LINES)) {
            if (inlines.Count > 0) {
                inlines.Add(new Avalonia.Controls.Documents.LineBreak());
            }

            inlines.Add(new Avalonia.Controls.Documents.Run(line.TrimEnd('\r')) { Foreground = UltimusLogBrush(line) });
        }

        _ultLogBox.Inlines = inlines;
        if (atEnd) {
            _ultLogScroll?.ScrollToEnd();
        }
    }

    private void ShowUltimusThoughts(string log) {
        if (_ultThoughtsBox is null || log.Length == 0) {
            return;
        }

        var thoughts = string.Join('\n', log.Split('\n').Where(l => l.Contains(": THOUGHT: ", StringComparison.Ordinal))
            .Select(l => l.TrimEnd('\r').Replace("THOUGHT: ", "", StringComparison.Ordinal)).TakeLast(ULTIMUS_LOG_LINES));
        if (thoughts == _ultThoughtsText || thoughts.Length == 0) {
            return;
        }

        _ultThoughtsText = thoughts;
        var atEnd = _ultThoughtsScroll is null || _ultThoughtsScroll.Offset.Y >= _ultThoughtsScroll.Extent.Height - _ultThoughtsScroll.Viewport.Height - 20;
        _ultThoughtsBox.Text = thoughts;
        if (atEnd) {
            _ultThoughtsScroll?.ScrollToEnd();
        }
    }

    private void ShowUltimusMistakes() {
        if (_ultMistakesBox is null) {
            return;
        }

        try {
            var path = Path.Combine(_manager.LogFolder, "bots", "mistakes.txt");
            if (!File.Exists(path)) {
                return;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd().ReplaceLineEndings("\n").TrimEnd();
            if (text != _ultMistakesText) {
                _ultMistakesText = text;
                _ultMistakesBox.Text = text;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Read again on the next refresh.
        }
    }

    private void ReadUltimusIssues() {
        try {
            var path = Path.Combine(UltimusFolder, "issues.json");
            if (!File.Exists(path)) {
                _ultIssueList = [];

                return;
            }

            var written = File.GetLastWriteTimeUtc(path);
            if (written == _ultIssuesWritten) {
                return;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            _ultIssueList = JsonSerializer.Deserialize<List<UltimusIssue>>(stream) ?? [];
            _ultIssuesWritten = written;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            // Being written right now: read it on the next tick.
        }
    }

    private void ShowUltimusStatus() {
        JsonObject? status = null;
        try {
            var path = Path.Combine(UltimusFolder, "status.json");
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
        var on = _ultEnabled.IsChecked == true;

        _ultState.Text = !running ? "The server is not running, so Ultimus is not either."
            : !on ? "Off: Ultimus is not playing."
            : live ? $"Playing. {Capital((string?) status!["why"] ?? "")}".TrimEnd()
            : "Starting: the server starts Ultimus within a few seconds, then it logs in.";
        _ultState.Foreground = running && on && live ? ManagerTheme.Accent("good") : MUTED;

        _ultDoing.IsVisible = _ultToday.IsVisible = _ultQuest.IsVisible = _ultMembers.IsVisible = running && on && live;
        if (!_ultDoing.IsVisible) {
            _ultSheet.Show(null);
            return;
        }

        _ultDoing.Text = "Doing now: " + ((string?) status!["doing"] ?? "-");
        _ultToday.Text = "Today so far: " + ((string?) status["today"] ?? "nothing yet.");
        var quest = (string?) status["quest"] ?? "";
        var questFor = (int?) status["questFor"] ?? 0;
        var expected = (int?) status["questExpected"] ?? 0;
        _ultQuest.Text = quest.Length == 0 ? "Quest: none right now."
            : $"Quest: {quest}, for {Minutes(questFor)} so far" + (expected > 0 ? $" (expected about {Minutes(expected)})." : ".");
        _ultQuest.Foreground = expected > 0 && questFor > expected * (double) (_ultSlow.Value ?? 2) ? ManagerTheme.Accent("Create") : ManagerTheme.TEXT_BRUSH;

        foreach (var node in status["members"] as JsonArray ?? []) {
            if (node is not JsonObject member) {
                continue;
            }

            var role = (string?) member["role"] ?? "";
            var card = UltimusMemberCard(role);
            card.Line.Text = $"{role}  ·  {(string?) member["name"]}  ·  level {(int?) member["level"] ?? 0}  ·  {(string?) member["zone"]}  ·  " +
                             $"health {(string?) member["health"]}  ·  {(int?) member["gold"] ?? 0} gold  ·  {(int?) member["training"] ?? 0} training points" +
                             ((bool?) member["battle"] == true ? "  ·  in a battle" : "") +
                             ((string?) member["state"] is { } state && state != "playing" ? $"  ·  {state}" : "");
        }

        _ultSheet.Show(status["members"] as JsonArray);

        static string Capital(string text) => text.Length == 0 ? "" : char.ToUpperInvariant(text[0]) + text[1..];
        static string Minutes(int seconds) => seconds < 90 ? $"{seconds} seconds" : $"{seconds / 60} minutes";
    }

    private sealed record UltimusCard(TextBlock Line);

    private readonly Dictionary<string, UltimusCard> _ultCards = [];

    // One line per Ultimus wizard (one for now) with its TP button; the sheet under it has the details.
    private UltimusCard UltimusMemberCard(string role) {
        if (_ultCards.TryGetValue(role, out var known)) {
            return known;
        }

        TextBlock Text(bool strong = false) => new() { TextWrapping = TextWrapping.Wrap, FontWeight = strong ? FontWeight.SemiBold : FontWeight.Normal };
        var card = new UltimusCard(Text(true));
        var tp = Action(Plain("TP to Ultimus"), () => {
            if (!InstalledCommands.IsServerRunning(_manager.InstallFolder)) {
                throw new PanelException("Start the server and log in to your wizard first.");
            }

            _manager.SendCommand(".ultimus tp");
            Say("Sent: your wizard goes to Ultimus, wherever it is.");

            return Task.CompletedTask;
        });
        ToolTip.SetTip(tp, "Your wizard (logged in on this PC) goes right next to Ultimus, even inside its dungeon. In the game: .ultimus tp");
        _ultMembers.Children.Add(new Border {
            Background = ManagerTheme.SURFACE_HIGH_BRUSH, CornerRadius = new CornerRadius(ManagerTheme.RADIUS_SMALL), Padding = new Thickness(12, 8),
            Child = new StackPanel {
                Spacing = 6,
                Children = { new DockPanel { Children = { WithDock(tp, Dock.Right), WithCenter(card.Line) } } },
            },
        });
        _ultCards[role] = card;

        return card;
    }

    // Niduus, 2026-11-13: "Shout out to discord user gese11 for sharing to me the bot concept".
    private static Control UltimusThanks() => new TextBlock {
        Text = "Shout out to Discord user gese11 for sharing the bot concept with Niduus.",
        TextWrapping = TextWrapping.Wrap, FontStyle = FontStyle.Italic, Foreground = MUTED, FontSize = ManagerTheme.FONT_SMALL,
    };

    // Every Ultimus log in one text (Niduus, 2026-11-14: "get me a universal copy for all of Ultimus' logs with proper
    // indents in them to identify which logs are from which"): a heading per log, then each of its lines indented and
    // tagged with the log's short name.
    // Every log in full, oldest line first, for a coach to read from a saved file (no chat paste limit).
    private string UltimusJointText() {
        var bots = Path.Combine(_manager.LogFolder, "bots");
        var text = new StringBuilder();
        text.AppendLine($"All of Ultimus's logs in full, saved {DateTime.Now:yyyy-MM-dd HH:mm:ss}. Each line starts with the file it came from.");
        foreach (var path in UltimusCoachFiles()) {
            var tag = Path.GetFileName(path);
            text.AppendLine();
            text.AppendLine($"===== [{tag}] {path} =====");
            var body = ReadAllOf(path, 8 * 1024 * 1024);
            foreach (var line in (body.Length == 0 ? "(empty)" : body).Split('\n')) {
                text.AppendLine($"    [{tag}] {line.TrimEnd('\r')}");
            }
        }

        return text.ToString();
    }

    // The files a coach reads: his own folder's files (newest log first) and the bots' shared files.
    private List<string> UltimusCoachFiles() {
        var files = new List<string>();
        try {
            if (Directory.Exists(UltimusFolder)) {
                files.AddRange(new DirectoryInfo(UltimusFolder).EnumerateFiles()
                    .Where(f => f.Extension is ".txt" or ".json" or ".jsonl" && !f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.Name.StartsWith("ultimus", StringComparison.OrdinalIgnoreCase) ? 1 : 0).ThenByDescending(f => f.LastWriteTimeUtc)
                    .Select(f => f.FullName));
            }

            var bots = Path.Combine(_manager.LogFolder, "bots");
            if (Directory.Exists(bots)) {
                files.AddRange(new DirectoryInfo(bots).EnumerateFiles().Where(f => f.Extension is ".txt" or ".json")
                    .OrderBy(f => f.Name).Select(f => f.FullName));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // What could be listed.
        }

        return files;
    }

    // "Save for coach" (Niduus, 2026-11-14: "Have all the logs be able to be saved as independent files or as a joint
    // file"): a folder he picks gets a dated folder with every log as its own file, his thoughts on their own, and one
    // joint file, ready to attach in the chat.
    private async Task SaveUltimusForCoachAsync() {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions {
            Title = "Pick where to save Ultimus's logs", AllowMultiple = false,
            SuggestedStartLocation = await StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Desktop),
        });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is not { } where) {
            return;
        }

        var folder = Path.Combine(where, $"Ultimus logs {DateTime.Now:yyyy-MM-dd HH-mm}");
        Directory.CreateDirectory(folder);
        var saved = 0;
        foreach (var path in UltimusCoachFiles()) {
            var into = Path.Combine(folder, Path.GetFileName(path).Equals("mistakes.txt", StringComparison.OrdinalIgnoreCase)
                || Path.GetDirectoryName(path)?.EndsWith("bots", StringComparison.OrdinalIgnoreCase) == true ? "bots-" + Path.GetFileName(path) : Path.GetFileName(path));
            var body = ReadAllOf(path, 64 * 1024 * 1024);
            await File.WriteAllTextAsync(into, body);
            saved++;
        }

        var main = ReadAllOf(UltimusNewestFile("ultimus*.txt"), 64 * 1024 * 1024);
        await File.WriteAllTextAsync(Path.Combine(folder, "thoughts.txt"),
            string.Join('\n', main.Split('\n').Where(l => l.Contains(": THOUGHT: ", StringComparison.Ordinal))));
        await File.WriteAllTextAsync(Path.Combine(folder, "ALL LOGS (joint file).txt"), UltimusJointText());
        await File.WriteAllTextAsync(Path.Combine(folder, "README.txt"),
            "Ultimus's logs, saved by Ahziael for the coach. Attach this whole folder (or the joint file) in the chat.\n" +
            "ultimus*.txt: what he does, day by day. thoughts.txt: his reasons. bots-mistakes.txt: the mistakes book. " +
            "bots-knowledge.txt: the bots' shared brain (creature habits, lessons). learned-today.txt: his daily reviews. " +
            "playbook.txt and self-tuning.txt: his rules and the limits he may change them within.\n");
        Say($"Saved {saved + 3} files in {folder}.");
        await Open(folder);
    }

    private async Task SaveUltimusJointFileAsync() {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
            Title = "Save all of Ultimus's logs in one file", SuggestedFileName = $"Ultimus logs {DateTime.Now:yyyy-MM-dd HH-mm}.txt",
            DefaultExtension = "txt", ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType("Text") { Patterns = ["*.txt"] }],
            SuggestedStartLocation = await StorageProvider.TryGetWellKnownFolderAsync(WellKnownFolder.Desktop),
        });
        if (file is null) {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(UltimusJointText());
        await using (var stream = await file.OpenWriteAsync()) {
            stream.SetLength(0);
            await stream.WriteAsync(bytes);
        }

        Say($"Saved all of Ultimus's logs in {file.Name}.");
    }

    private string UltimusAllLogs() {
        var bots = Path.Combine(_manager.LogFolder, "bots");
        var main = ReadAllOf(UltimusNewestFile("ultimus*.txt"), 1024 * 1024);
        // Newest first, so the part that matters fits in a chat paste even when the paste gets cut (Niduus's paste of
        // v0.10.852 stopped in the morning's lines): the logs that change every second also keep only the last hours.
        var since = DateTime.Now.AddHours(-UltimusCopyHours);
        var recent = NewestFirst(main.Split('\n').Where(l => !LineTime(l, out var at) || at >= since), 2000);
        var thoughts = NewestFirst(main.Split('\n').Where(l => l.Contains(": THOUGHT: ", StringComparison.Ordinal)), 150);
        var quests = NewestFirst(ReadAllOf(Path.Combine(UltimusFolder, "quest-log.txt"), 256 * 1024).Split('\n'), 150);
        (string Tag, string Name, string Text)[] logs = [
            ("Progress", "His progress: level, world times, today (progress.txt)", ReadAllOf(Path.Combine(UltimusFolder, "progress.txt"), 32 * 1024)),
            ("Status", "His status now (status.txt)", ReadAllOf(Path.Combine(UltimusFolder, "status.txt"), 32 * 1024)),
            ("Steps", "What went wrong at each step before (step-memory.json)", ReadAllOf(Path.Combine(UltimusFolder, "step-memory.json"), 64 * 1024)),
            ("Explain", "His last Pause and explain (explain.txt)", ReadAllOf(Path.Combine(UltimusFolder, "explain.txt"), 32 * 1024)),
            ("UseSteps", "Story steps that ask to click things, checked after an update (use-steps.txt)", ReadAllOf(Path.Combine(UltimusFolder, "use-steps.txt"), 64 * 1024)),
            ("ExitTries", "Doors that did not take him: where he stood on each try (exit-tries.txt)", ReadAllOf(Path.Combine(UltimusFolder, "exit-tries.txt"), 32 * 1024)),
            ("SelfTest", "His last self-test after an update (self-test.txt)", ReadAllOf(Path.Combine(UltimusFolder, "self-test.txt"), 16 * 1024)),
            ("Training", "His training plan (training-plan.txt)", ReadAllOf(Path.Combine(UltimusFolder, "training-plan.txt"), 16 * 1024)),
            ("Pace", "His weekly pace report (pace-report.txt)", ReadAllOf(Path.Combine(UltimusFolder, "pace-report.txt"), 16 * 1024)),
            ("Thoughts", "Ultimus Thoughts, newest first (his reasons, also in the main log)", thoughts),
            ("Mistakes", "Mistakes book, newest first (logs\\bots\\mistakes.txt)", ReadAllOf(Path.Combine(bots, "mistakes.txt"), 64 * 1024)),
            ("Fixes", "Abnormal fixes, each stuck spot and what was tried (abnormal-fixes.json)", ReadAllOf(Path.Combine(UltimusFolder, "abnormal-fixes.json"), 64 * 1024)),
            ("Foes", "Creatures he fought (foes.json)", ReadAllOf(Path.Combine(UltimusFolder, "foes.json"), 64 * 1024)),
            ("Quests", "Quest log, newest first (quest-log.txt)", quests),
            ("Main", $"Ultimus log, newest first, the last {UltimusCopyHours} hours (what he does; issues are in it too)", recent),
            ("Today", "Today I learned, his daily reviews (learned-today.txt)", ReadAllOf(Path.Combine(UltimusFolder, "learned-today.txt"), 32 * 1024)),
            ("Brain", "The bots' shared brain: lessons and creature habits (logs\\bots\\knowledge.txt)", ReadAllOf(Path.Combine(bots, "knowledge.txt"), 64 * 1024)),
            ("Learned", "What he learned (learned.jsonl)", ReadAllOf(Path.Combine(UltimusFolder, "learned.jsonl"), 32 * 1024)),
            ("Playbook", "His playbook (playbook.txt)", ReadAllOf(Path.Combine(UltimusFolder, "playbook.txt"), 64 * 1024)),
        ];

        var text = new StringBuilder();
        text.AppendLine($"All of Ultimus's logs, copied {DateTime.Now:yyyy-MM-dd HH:mm:ss}, newest first. Each line starts with the log it came from:");
        foreach (var (tag, name, _) in logs) {
            text.AppendLine($"    [{tag}] {name}");
        }

        foreach (var (tag, name, body) in logs) {
            text.AppendLine();
            text.AppendLine($"===== [{tag}] {name} =====");
            if (body.Length == 0) {
                text.AppendLine($"    [{tag}] (empty)");
                continue;
            }

            foreach (var line in body.Split('\n')) {
                text.AppendLine($"    [{tag}] {line.TrimEnd('\r')}");
            }
        }

        return text.ToString();
    }

    // How many hours of his main log "Copy all logs" takes.
    private const int UltimusCopyHours = 3;

    private static string NewestFirst(IEnumerable<string> lines, int most)
        => string.Join('\n', lines.Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).Reverse().Take(most));

    // A log line's time ("2026-11-13 20:05:51.968 -06:00 [INF] ..."); false for a line without one.
    private static bool LineTime(string line, out DateTime at) {
        at = default;

        return line.Length >= 19 && DateTime.TryParseExact(line[..19], "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out at);
    }

    private string? UltimusNewestFile(string pattern) {
        try {
            return Directory.Exists(UltimusFolder)
                ? new DirectoryInfo(UltimusFolder).EnumerateFiles(pattern).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    // A file's text (its last part when it is big), read while the server may be writing it; "" when it can't be read.
    private static string ReadAllOf(string? path, int most) {
        try {
            if (path is null || !File.Exists(path)) {
                return "";
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(Math.Max(0, stream.Length - most), SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            return reader.ReadToEnd().ReplaceLineEndings("\n").TrimEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return "";
        }
    }

    private string ReadUltimusNewest(string pattern, string fallback) {
        try {
            var file = Directory.Exists(UltimusFolder)
                ? new DirectoryInfo(UltimusFolder).EnumerateFiles(pattern).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
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
