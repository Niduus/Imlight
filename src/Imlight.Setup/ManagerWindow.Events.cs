using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Imlight.Setup;

/// <summary>
/// The Events card on the Server tab (Niduus, 2026-10-24: "Have events be controlled by the manager with able to queue
/// new ones up as old ones expire"): a queue of game events, each lasting so many days and hours. The first one starts
/// when Start is pressed and each next one starts as the one before it ends; with Repeat the queue goes round again.
/// The app writes events.json next to the server, which works out from it which event is on and tells everyone online
/// when one starts or ends (the server's GameEvents.cs). An event that is over leaves the queue.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string EVENTS_FILE = "events.json";
    private const string DECKATHALON_SCORES_FILE = "deckathalon.json";

    private static readonly string[] s_eventSchools = ["Fire", "Ice", "Storm", "Myth", "Life", "Death", "Balance"];

    // The game's events the queue can run (the server's ClassProjects.Queueable): the kind, and the choices of the second
    // box (a Deckathalon's school, a Monthly Gauntlet's gauntlet). The Scroll of Fortune needs no queue: it is always on.
    private static readonly string[] s_eventKinds = ["Deckathalon", "Side Activity", "Pet Activity", "Castle Tours", "Wizard City Day", "Monthly Gauntlet"];
    private static readonly string[] s_monthlyGauntlets = ["Sinbad", "Spiral Cup", "Great Clock", "Grand Tourney", "Fantastic Voyage", "Accursed Play"];

    private static string[] EventChoices(string kind) => kind switch {
        "Deckathalon" => s_eventSchools,
        "Monthly Gauntlet" => s_monthlyGauntlets,
        _ => [],
    };

    private sealed class QueuedEvent {
        internal string Kind = "Deckathalon";
        internal string School = "Fire";
        internal int Days;
        internal int Hours;

        internal TimeSpan Length => TimeSpan.FromDays(Math.Max(0, Days)) + TimeSpan.FromHours(Math.Max(0, Hours));

        internal string Name => Kind == "Deckathalon" ? $"{School} Deckathalon"
            : string.IsNullOrWhiteSpace(School) ? Kind : $"{School} {Kind}";
    }

    private readonly List<QueuedEvent> _eventQueue = [];
    private DateTime? _eventsStartedAt;
    private bool _showingEvents;

    private readonly ComboBox _eventKind = new() { Width = 180, ItemsSource = s_eventKinds, SelectedIndex = 0 };
    private readonly ComboBox _eventSchool = new() { Width = 170, ItemsSource = s_eventSchools, SelectedIndex = 0 };
    private readonly NumericUpDown _eventDays = new() { Minimum = 0, Maximum = 365, Increment = 1, FormatString = "0", Width = 110, Value = 7 };
    private readonly NumericUpDown _eventHours = new() { Minimum = 0, Maximum = 23, Increment = 1, FormatString = "0", Width = 110, Value = 0 };
    private readonly ToggleSwitch _eventsRepeat = new() { OnContent = "On", OffContent = "Off" };
    private readonly TextBlock _eventNow = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly StackPanel _eventList = new() { Spacing = 4 };
    private readonly TextBlock _eventTop = Sub("");
    private readonly Button _eventsStart = Plain("Start the queue");
    private string _eventListShown = "";

    private string EventsPath => Path.Combine(_manager.ServerFolder, EVENTS_FILE);

    private Control EventsCard() {
        LoadEvents();
        _eventsRepeat.IsCheckedChanged += async (_, _) => {
            if (!_showingEvents) {
                await Guard(SaveEventsAsync);
            }
        };

        // The time left, counting down while the window is open; an event that is over leaves the queue.
        var ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        ticker.Tick += async (_, _) => await Guard(TickEventsAsync);
        ticker.Start();
        ShowEvents();

        // The second box is the Deckathalon's school or the Monthly Gauntlet's gauntlet; the other events have none.
        _eventKind.SelectionChanged += (_, _) => {
            var choices = EventChoices(s_eventKinds[Math.Max(0, _eventKind.SelectedIndex)]);
            _eventSchool.ItemsSource = choices;
            _eventSchool.SelectedIndex = choices.Length > 0 ? 0 : -1;
            _eventSchool.IsVisible = choices.Length > 0;
        };

        var add = Row(_eventKind, _eventSchool,
            new TextBlock { Text = "for", VerticalAlignment = VerticalAlignment.Center }, _eventDays,
            new TextBlock { Text = "days", VerticalAlignment = VerticalAlignment.Center }, _eventHours,
            new TextBlock { Text = "hours", VerticalAlignment = VerticalAlignment.Center });

        return Fold("clock", "Server", "Events",
            "Game events, one after another. Add events to the queue, then press Start the queue: the first one starts at " +
            "once and each next one starts by itself when the one before it ends. Everyone online is told when an event " +
            "starts or ends. The events are the game's own: the Deckathalon, the Side Activity (fishing, pet snacks, " +
            "crafting, Daily Assignments, Team Up), the Pet Activity (pet games, snacks, hatching), Castle Tours, " +
            "Wizard City Day and the six Monthly Gauntlets. The Scroll of Fortune is always on, like the live game. " +
            "In the game, the event button at the top of the screen shows them, .events lists them and " +
            ".deckathalon climbs the Deckathalon that is on.",
            _eventNow,
            Row(Action(_eventsStart, StartEventsAsync), Action(Plain("End the current event now"), EndCurrentEventAsync),
                Action(Danger(Plain("Clear the queue")), ClearEventsAsync)),
            Heading("Queue"),
            _eventList,
            Heading("Add an event"),
            add,
            Row(Action(Primary("Add to the queue"), AddEventAsync)),
            SwitchRow("Repeat", _eventsRepeat, "After the last event, start again from the first, so events never run out."),
            Heading("Deckathalon: best wizards"),
            _eventTop);
    }

    // When each event of the queue starts and ends, from the start of the queue (with Repeat, the round we are in).
    private List<(QueuedEvent Event, DateTime Start, DateTime End)> EventTimes(DateTime now) {
        var times = new List<(QueuedEvent, DateTime, DateTime)>();
        if (_eventsStartedAt is not { } startedAt || _eventQueue.Count == 0) {
            return times;
        }

        var start = startedAt;
        var total = TimeSpan.FromTicks(_eventQueue.Sum(e => e.Length.Ticks));
        if (_eventsRepeat.IsChecked == true && total > TimeSpan.Zero && now - start >= total) {
            start += TimeSpan.FromTicks(total.Ticks * ((now - start).Ticks / total.Ticks));
        }

        foreach (var queued in _eventQueue) {
            times.Add((queued, start, start + queued.Length));
            start += queued.Length;
        }

        return times;
    }

    private async Task TickEventsAsync() {
        // Without Repeat, an event that is over leaves the queue, and the queue then starts at the next one.
        var now = DateTime.UtcNow;
        if (_eventsRepeat.IsChecked != true && _eventsStartedAt is not null) {
            var over = EventTimes(now).TakeWhile(t => t.End <= now).ToList();
            if (over.Count > 0) {
                _eventQueue.RemoveRange(0, over.Count);
                _eventsStartedAt = _eventQueue.Count == 0 ? null : over[^1].End;
                await SaveEventsAsync();
            }
        }

        ShowEvents();
    }

    private async Task AddEventAsync() {
        var days = (int) (_eventDays.Value ?? 0);
        var hours = (int) (_eventHours.Value ?? 0);
        if (days == 0 && hours == 0) {
            throw new PanelException("Give the event a length: at least 1 hour.");
        }

        var kind = s_eventKinds[Math.Max(0, _eventKind.SelectedIndex)];
        var choices = EventChoices(kind);
        _eventQueue.Add(new QueuedEvent {
            Kind = kind,
            School = choices.Length > 0 ? choices[Math.Clamp(_eventSchool.SelectedIndex, 0, choices.Length - 1)] : "",
            Days = days, Hours = hours,
        });
        await SaveEventsAsync();
        Say($"Added the {_eventQueue[^1].Name} to the queue.");
    }

    private async Task StartEventsAsync() {
        if (_eventQueue.Count == 0) {
            throw new PanelException("Add an event to the queue first.");
        }

        if (_eventsStartedAt is not null) {
            throw new PanelException("The queue is already running.");
        }

        _eventsStartedAt = DateTime.UtcNow;
        await SaveEventsAsync();
        Say($"The {_eventQueue[0].Name} is on. Everyone online is told.");
    }

    private async Task EndCurrentEventAsync() {
        var now = DateTime.UtcNow;
        var times = EventTimes(now);
        var index = times.FindIndex(t => t.Start <= now && now < t.End);
        if (index < 0) {
            throw new PanelException("No event is on right now.");
        }

        // The events before it went already; the next one starts now.
        var name = times[index].Event.Name;
        _eventQueue.RemoveRange(0, index + 1);
        _eventsStartedAt = _eventQueue.Count == 0 ? null : now;
        await SaveEventsAsync();
        Say(_eventQueue.Count == 0 ? $"Ended the {name}. The queue is empty." : $"Ended the {name}; the {_eventQueue[0].Name} is on now.");
    }

    private async Task ClearEventsAsync() {
        if (!await ConfirmAsync("Clear the whole queue? The event that is on ends too.")) {
            return;
        }

        _eventQueue.Clear();
        _eventsStartedAt = null;
        await SaveEventsAsync();
    }

    private async Task MoveEventAsync(int index, int by) {
        var to = index + by;
        if (index < 0 || to < 0 || index >= _eventQueue.Count || to >= _eventQueue.Count) {
            return;
        }

        var now = DateTime.UtcNow;
        var running = EventTimes(now).FindIndex(t => t.Start <= now && now < t.End);
        if (running >= 0 && (index == running || to == running)) {
            throw new PanelException("The event that is on stays where it is. End it first, or move the others.");
        }

        (_eventQueue[index], _eventQueue[to]) = (_eventQueue[to], _eventQueue[index]);
        await SaveEventsAsync();
    }

    private async Task RemoveEventAsync(int index) {
        if (index < 0 || index >= _eventQueue.Count) {
            return;
        }

        var now = DateTime.UtcNow;
        var times = EventTimes(now);
        if (times.Count > index && times[index].Start <= now && now < times[index].End) {
            await EndCurrentEventAsync();

            return;
        }

        // An event before the one on now is over already (Repeat keeps them); one after it just goes.
        if (times.Count > index && times[index].End <= now && _eventsStartedAt is { } started) {
            _eventsStartedAt = started + _eventQueue[index].Length;
        }

        _eventQueue.RemoveAt(index);
        if (_eventQueue.Count == 0) {
            _eventsStartedAt = null;
        }

        await SaveEventsAsync();
    }

    private void ShowEvents() {
        var now = DateTime.UtcNow;
        var times = EventTimes(now);
        var on = times.FirstOrDefault(t => t.Start <= now && now < t.End);
        _eventNow.Text = on.Event is not null
            ? $"On now: {on.Event.Name}, {Left(on.End - now)} left."
            : _eventQueue.Count == 0 ? "Nothing is on. Add an event below, then press Start the queue."
            : _eventsStartedAt is null ? "Nothing is on yet. Press Start the queue to start the first event."
            : "Nothing is on.";
        _eventsStart.IsEnabled = _eventsStartedAt is null && _eventQueue.Count > 0;

        // The rows are only built again when the queue changed, so their buttons stay pressable.
        var shown = string.Join("|", times.Select(t => $"{t.Event.Name}{t.Event.Length}{t.Start:o}"))
            + string.Join("|", _eventQueue.Select(e => e.Name + e.Length)) + _eventsStartedAt;
        if (shown != _eventListShown) {
            _eventListShown = shown;
            _eventList.Children.Clear();
            if (_eventQueue.Count == 0) {
                _eventList.Children.Add(Sub("The queue is empty."));
            }

            for (var i = 0; i < _eventQueue.Count; i++) {
                var index = i;
                var queued = _eventQueue[i];
                var when = times.Count > i
                    ? (times[i].Start <= now && now < times[i].End ? $"on now, ends {Local(times[i].End)}"
                        : times[i].End <= now ? "over" : $"starts {Local(times[i].Start)}")
                    : "waits for Start";
                var label = new TextBlock {
                    Text = $"{i + 1}. {queued.Name}, {Length(queued)} ({when})",
                    VerticalAlignment = VerticalAlignment.Center, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                };
                _eventList.Children.Add(new DockPanel {
                    Children = {
                        WithDock(Row(Action(Plain("Up"), () => MoveEventAsync(index, -1)), Action(Plain("Down"), () => MoveEventAsync(index, 1)),
                            Action(Plain("Remove"), () => RemoveEventAsync(index))), Dock.Right),
                        label,
                    },
                });
            }
        }

        _eventTop.Text = DeckathalonTop(on.Event is { Kind: "Deckathalon" } ? $"Deckathalon-{on.Event.School}@{on.Start:yyyy-MM-ddTHH:mm}" : null);
    }

    private static string Left(TimeSpan left)
        => left.TotalDays >= 1 ? $"{(int) left.TotalDays}d {left.Hours}h {left.Minutes}m"
            : left.TotalHours >= 1 ? $"{left.Hours}h {left.Minutes}m {left.Seconds}s"
            : $"{left.Minutes}m {left.Seconds}s";

    private static string Length(QueuedEvent queued)
        => queued.Days > 0 && queued.Hours > 0 ? $"{queued.Days} days {queued.Hours} hours"
            : queued.Days > 0 ? $"{queued.Days} day{(queued.Days == 1 ? "" : "s")}"
            : $"{queued.Hours} hour{(queued.Hours == 1 ? "" : "s")}";

    private static string Local(DateTime utc) => utc.ToLocalTime().ToString("ddd MMM d, h:mm tt", CultureInfo.CurrentCulture);

    // The best 10 of the Deckathalon on now, from the server's deckathalon.json.
    private string DeckathalonTop(string? eventKey) {
        if (eventKey is null) {
            return "No Deckathalon is on.";
        }

        try {
            var path = Path.Combine(_manager.ServerFolder, DECKATHALON_SCORES_FILE);
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path))?["Events"]?[eventKey] is not JsonObject byWizard) {
                return "Nobody has cleared a floor yet.";
            }

            var top = byWizard.Select(p => p.Value).OfType<JsonObject>()
                .Select(s => (Name: s["Name"]?.GetValue<string>() ?? "?", Floor: s["BestFloor"]?.GetValue<int>() ?? 0, Points: s["Points"]?.GetValue<int>() ?? 0))
                .Where(s => s.Floor > 0)
                .OrderByDescending(s => s.Floor).ThenByDescending(s => s.Points)
                .Take(10)
                .Select((s, i) => $"{i + 1}. {s.Name}: floor {s.Floor}, {s.Points} points")
                .ToList();

            return top.Count == 0 ? "Nobody has cleared a floor yet." : string.Join(Environment.NewLine, top);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) {
            return "The scores could not be read just now.";
        }
    }

    private void LoadEvents() {
        _showingEvents = true;
        try {
            _eventQueue.Clear();
            _eventsStartedAt = null;
            if (!File.Exists(EventsPath) || JsonNode.Parse(File.ReadAllText(EventsPath)) is not JsonObject json) {
                return;
            }

            foreach (var item in (json["Queue"] as JsonArray ?? []).OfType<JsonObject>()) {
                var kind = item["Kind"]?.GetValue<string>() ?? "Deckathalon";
                var queued = new QueuedEvent {
                    Kind = kind,
                    School = item["School"]?.GetValue<string>() ?? (kind == "Deckathalon" ? "Fire" : ""),
                    Days = item["Days"]?.GetValue<int>() ?? 0,
                    Hours = item["Hours"]?.GetValue<int>() ?? 0,
                };
                if (queued.Length > TimeSpan.Zero) {
                    _eventQueue.Add(queued);
                }
            }

            _eventsStartedAt = json["StartedAt"]?.GetValue<string>() is { } started
                && DateTime.TryParse(started, null, DateTimeStyles.RoundtripKind, out var at) ? at.ToUniversalTime() : null;
            _eventsRepeat.IsChecked = json["Repeat"]?.GetValue<bool>() == true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) {
            Say($"Could not read the events: {ex.Message}", isError: true);
        }
        finally {
            _showingEvents = false;
        }
    }

    private async Task SaveEventsAsync() {
        var queue = new JsonArray();
        foreach (var queued in _eventQueue) {
            queue.Add(new JsonObject {
                ["Kind"] = queued.Kind,
                ["School"] = queued.School,
                ["Days"] = queued.Days,
                ["Hours"] = queued.Hours,
            });
        }

        var json = new JsonObject {
            ["Queue"] = queue,
            ["StartedAt"] = _eventsStartedAt?.ToString("o"),
            ["Repeat"] = _eventsRepeat.IsChecked == true,
        };
        Directory.CreateDirectory(_manager.ServerFolder);
        var temp = $"{EventsPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temp, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, EventsPath, overwrite: true);
        ShowEvents();
    }

}
