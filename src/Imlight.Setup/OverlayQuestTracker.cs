/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// What a Quest Tracker shows and how (Niduus, 2026-11-12: "It will show all your quest in catagory in the UI. Story, Main
/// and Side quests. In the UI, you can click the quest and it will visually draw lines of where you have to go"). Plain
/// choices and colours only.
/// </summary>
internal sealed class OverlayQuestList {

    /// <summary>The words at the top ("Quests"); empty for none.</summary>
    public string Title { get; set; } = "Quests";

    /// <summary>Dungeon quests (they only count inside their dungeon).</summary>
    public bool Instance { get; set; } = true;

    public bool Story { get; set; } = true;
    public bool Main { get; set; } = true;
    public bool Side { get; set; } = true;

    /// <summary>Story quests are main quests too: shown again under Main when on.</summary>
    public bool StoryInMain { get; set; }

    /// <summary>Each quest's step now, under its name.</summary>
    public bool Steps { get; set; } = true;

    /// <summary>The picked quest's way is drawn on the Minimap (only there; with no Minimap, no lines).</summary>
    public bool Route { get; set; } = true;

    public double RouteWidth { get; set; } = 3;

    public double TitleSize { get; set; } = 13;
    public double StepSize { get; set; } = 11;

    /// <summary>How tall the list gets before it scrolls (mouse wheel over it, or its arrows).</summary>
    public double MaxHeight { get; set; } = 320;

    public string InstanceColor { get; set; } = "#FFC07CFF";
    public string StoryColor { get; set; } = "#FFFFC83C";
    public string MainColor { get; set; } = "#FFFF9A4A";
    public string SideColor { get; set; } = "#FF4AB4FF";
    public string TextColor { get; set; } = "#FFFFFFFF";
    public string StepColor { get; set; } = "#C8E6E6F0";

    internal OverlayQuestList Clean() {
        Title = new string((Title ?? "").Where(c => !char.IsControl(c)).Take(40).ToArray());
        RouteWidth = OverlayElement.Range(RouteWidth, 1, 10, 3);
        TitleSize = OverlayElement.Range(TitleSize, 6, 30, 13);
        StepSize = OverlayElement.Range(StepSize, 6, 30, 11);
        MaxHeight = OverlayElement.Range(MaxHeight, 80, 1200, 320);
        InstanceColor = OverlayPaint.CleanColor(InstanceColor, "#FFC07CFF");
        StoryColor = OverlayPaint.CleanColor(StoryColor, "#FFFFC83C");
        MainColor = OverlayPaint.CleanColor(MainColor, "#FFFF9A4A");
        SideColor = OverlayPaint.CleanColor(SideColor, "#FF4AB4FF");
        TextColor = OverlayPaint.CleanColor(TextColor, "#FFFFFFFF");
        StepColor = OverlayPaint.CleanColor(StepColor, "#C8E6E6F0");

        return this;
    }

    internal string ColorOf(string kind) => kind switch { "instance" => InstanceColor, "story" => StoryColor, "main" => MainColor, _ => SideColor };

}

/// <summary>
/// The quests each game window's Quest Tracker shows, as the server last sent them (op "quests" on the overlay link,
/// every two seconds while a tracker shows), the quest picked in each window, and the picked quest's way as the minimap's
/// map last brought it. The designers (no game window) get made-up quests.
/// </summary>
internal static class OverlayQuestData {

    internal sealed record Step(string Text, string Where, bool Here);

    internal sealed record Quest(string Id, string Title, string Kind, string World, int Level, bool Here, IReadOnlyList<Step> Steps) {

        /// <summary>For the picked quest with nothing to do here: the areas to go through, in order.</summary>
        internal IReadOnlyList<string> Way { get; init; } = [];

    }

    internal sealed record Snapshot(string Place, IReadOnlyList<Quest> Quests, string Note, DateTime At) {

        internal bool Stale => DateTime.UtcNow - At > TimeSpan.FromSeconds(10);

    }

    /// <summary>A picked quest's way on the minimap: the step spots (nearest first) and the walk to the nearest.</summary>
    internal sealed record Route(string Quest, string Title, string Kind, IReadOnlyList<(double X, double Y, string Name)> Points,
                                 IReadOnlyList<(double X, double Y)> Path, string To, string Via, IReadOnlySet<int>? Pads = null) {

        /// <summary>Faded trails to the step's other spots (the nearest by the walk gets <see cref="Path"/>).</summary>
        internal IReadOnlyList<Trail> Trails { get; init; } = [];

    }

    /// <summary>The walk to one of the step's other spots; Pads are the indexes of teleporter spots, as in Route.</summary>
    internal sealed record Trail(IReadOnlyList<(double X, double Y)> Path, IReadOnlySet<int> Pads);

    private static readonly object s_lock = new();
    private static readonly Dictionary<long, Snapshot> s_lists = [];
    private static readonly Dictionary<long, string> s_picks = [];
    private static readonly Dictionary<long, HashSet<string>> s_folded = [];
    private static readonly Dictionary<long, Route> s_routes = [];
    private static readonly Dictionary<long, OverlayQuestList> s_looks = [];
    private static readonly Dictionary<long, (string Quest, DateTime At)> s_lastPick = [];
    private static int s_version;
    private static bool s_askSoon;

    /// <summary>True once after a quest was picked or let go (the link then asks for the quests at once).</summary>
    internal static bool TakeAskSoon() {
        lock (s_lock) {
            var soon = s_askSoon;
            s_askSoon = false;

            return soon;
        }
    }
    private static long[] s_wanted = [];

    /// <summary>The game windows whose overlay shows a Quest Tracker now; set by the overlay, read by the link.</summary>
    internal static long[] Wanted {
        get => System.Threading.Volatile.Read(ref s_wanted);
        set => System.Threading.Volatile.Write(ref s_wanted, value);
    }

    /// <summary>Changes each time any tracker's quests, pick or folding change (the overlay is laid out again then).</summary>
    internal static int Version {
        get {
            lock (s_lock) {
                return s_version;
            }
        }
    }

    /// <summary>Whether the design drawn in the designers has a Quest Tracker (its made-up pick then shows on the made-up minimap).</summary>
    internal static bool DesignHasTracker { get; set; }

    internal static Snapshot? For(long window) {
        if (window == 0) {
            return Sample;
        }

        lock (s_lock) {
            return s_lists.GetValueOrDefault(window);
        }
    }

    /// <summary>The quest picked in this window ("" for none).</summary>
    internal static string Pick(long window) {
        if (window == 0) {
            return DesignHasTracker ? SamplePick : "";
        }

        lock (s_lock) {
            return s_picks.GetValueOrDefault(window, "");
        }
    }

    /// <summary>Picks this quest in this window, or lets it go when it was picked already.</summary>
    internal static void TogglePick(long window, string quest) {
        lock (s_lock) {
            // A double click is one click (the tracker is laid out again between the two).
            if (s_lastPick.TryGetValue(window, out var last) && last.Quest == quest && DateTime.UtcNow - last.At < TimeSpan.FromMilliseconds(500)) {
                return;
            }

            s_lastPick[window] = (quest, DateTime.UtcNow);
            if (s_picks.GetValueOrDefault(window) == quest || string.IsNullOrEmpty(quest)) {
                s_picks.Remove(window);
            }
            else {
                s_picks[window] = quest;
            }

            s_routes.Remove(window);
            s_version++;
            s_askSoon = true;
        }

        OverlayMinimapData.Redraw();
    }

    /// <summary>
    /// Sets the quest whose way is drawn for this key, with no click (the Ultimus Spectator's map always draws Ultimus's own
    /// quest, OverlayUltimusData.KEY); empty for none.
    /// </summary>
    internal static void Follow(long window, string quest) {
        lock (s_lock) {
            if (string.IsNullOrEmpty(quest)) {
                s_picks.Remove(window);
                s_routes.Remove(window);
            }
            else {
                s_picks[window] = quest;
            }
        }
    }

    internal static bool Folded(long window, string kind) {
        lock (s_lock) {
            return s_folded.TryGetValue(window, out var folded) && folded.Contains(kind);
        }
    }

    /// <summary>Folds a kind's list away, or opens it again.</summary>
    internal static void ToggleFold(long window, string kind) {
        lock (s_lock) {
            if (!s_folded.TryGetValue(window, out var folded)) {
                s_folded[window] = folded = [];
            }

            if (!folded.Remove(kind)) {
                folded.Add(kind);
            }

            s_version++;
        }
    }

    private static readonly Dictionary<long, double> s_scroll = [];
    private static readonly Dictionary<long, double> s_scrollMax = [];

    /// <summary>How far this window's list is scrolled down, in overlay units.</summary>
    internal static double Scroll(long window) {
        lock (s_lock) {
            return Math.Clamp(s_scroll.GetValueOrDefault(window), 0, s_scrollMax.GetValueOrDefault(window));
        }
    }

    /// <summary>How far the list can scroll (set by the tracker each time it is drawn; 0 when it all fits).</summary>
    internal static void ScrollRoom(long window, double max) {
        lock (s_lock) {
            s_scrollMax[window] = Math.Max(0, max);
        }
    }

    /// <summary>Scrolls this window's list (down for more than 0).</summary>
    internal static void ScrollBy(long window, double by) {
        lock (s_lock) {
            var max = s_scrollMax.GetValueOrDefault(window);
            var now = Math.Clamp(s_scroll.GetValueOrDefault(window), 0, max);
            var next = Math.Clamp(now + by, 0, max);
            if (Math.Abs(next - now) < 0.5) {
                return;
            }

            s_scroll[window] = next;
            s_version++;
        }
    }

    /// <summary>How this window's tracker looks (its line colours and width are used by the minimap).</summary>
    internal static void Look(long window, OverlayQuestList look) {
        lock (s_lock) {
            s_looks[window] = look;
        }
    }

    /// <summary>A Quest Tracker's look on this overlay, if any shows.</summary>
    internal static OverlayQuestList? AnyLook() {
        lock (s_lock) {
            return s_looks.Values.FirstOrDefault();
        }
    }

    internal static OverlayQuestList? LookOf(long window) {
        lock (s_lock) {
            return s_looks.GetValueOrDefault(window);
        }
    }

    /// <summary>The picked quest's way for this window's minimap, when the line is wanted; null otherwise.</summary>
    internal static Route? RouteFor(long window) {
        if (window == 0) {
            return DesignHasTracker && (LookOf(0)?.Route ?? true) ? SampleRoute : null;
        }

        lock (s_lock) {
            if (!s_picks.TryGetValue(window, out var pick) || !s_routes.TryGetValue(window, out var route) || route.Quest != pick) {
                return null;
            }

            return s_looks.TryGetValue(window, out var look) && !look.Route ? null : route;
        }
    }

    /// <summary>The server's answer to op "quests" for one window.</summary>
    internal static void Heard(long window, JsonObject answer) {
        Snapshot snapshot;
        if (answer["ok"]?.GetValue<bool>() == true && answer["quests"] is JsonObject sent) {
            snapshot = Read(sent);
        }
        else {
            var error = ((string?) answer["error"] ?? "").Trim();
            if (error == "busy") {
                return;
            }

            snapshot = new Snapshot("", [], error.Length > 200 ? error[..200] : error, DateTime.UtcNow);
        }

        lock (s_lock) {
            var before = s_lists.GetValueOrDefault(window);
            s_lists[window] = snapshot;
            // A picked quest that is done (or dropped) is let go.
            if (snapshot.Note.Length == 0 && s_picks.TryGetValue(window, out var pick) && snapshot.Quests.All(q => q.Id != pick)) {
                s_picks.Remove(window);
                s_routes.Remove(window);
                s_version++;
            }

            if (before is null || !Same(before, snapshot)) {
                s_version++;
            }
        }
    }

    /// <summary>The map the minimap got for this window: its "route", if any (OverlayMinimapData.Heard passes it on).</summary>
    internal static void HeardMap(long window, JsonObject? map) {
        lock (s_lock) {
            if (map?["route"] is not JsonObject sent) {
                s_routes.Remove(window);

                return;
            }

            var points = new List<(double, double, string)>();
            foreach (var item in (sent["points"] as JsonArray ?? []).OfType<JsonObject>().Take(12)) {
                points.Add((Number(item["x"]), Number(item["y"]), Short((string?) item["n"], 60)));
            }

            // A spot with a third number 1 is a teleporter: the line jumps from it to the next spot.
            static (List<(double, double)> Path, HashSet<int> Pads) Walk(JsonArray? sentPath) {
                var path = new List<(double, double)>();
                var pads = new HashSet<int>();
                foreach (var item in (sentPath ?? []).OfType<JsonArray>().Take(120)) {
                    if (item.Count >= 2) {
                        if (item.Count >= 3 && Number(item[2]) == 1) {
                            pads.Add(path.Count);
                        }

                        path.Add((Number(item[0]), Number(item[1])));
                    }
                }

                return (path, pads);
            }

            var (path, pads) = Walk(sent["path"] as JsonArray);
            var trails = new List<Trail>();
            foreach (var item in (sent["trails"] as JsonArray ?? []).OfType<JsonObject>().Take(8)) {
                var (trail, trailPads) = Walk(item["path"] as JsonArray);
                if (trail.Count > 0) {
                    trails.Add(new Trail(trail, trailPads));
                }
            }

            s_routes[window] = new Route(Short((string?) sent["quest"], 120), Short((string?) sent["t"], 120), Short((string?) sent["k"], 10),
                points, path, Short((string?) sent["to"], 80), Short((string?) sent["via"], 80), pads) { Trails = trails };
        }
    }

    /// <summary>Once a second: windows that no longer show a tracker are forgotten.</summary>
    internal static void Tick() {
        lock (s_lock) {
            var wanted = Wanted;
            foreach (var gone in s_lists.Keys.Where(w => !wanted.Contains(w)).ToList()) {
                s_lists.Remove(gone);
            }
        }
    }

    private static bool Same(Snapshot a, Snapshot b) {
        if (a.Note != b.Note || a.Place != b.Place || a.Quests.Count != b.Quests.Count) {
            return false;
        }

        for (var i = 0; i < a.Quests.Count; i++) {
            var (qa, qb) = (a.Quests[i], b.Quests[i]);
            if (qa.Id != qb.Id || qa.Kind != qb.Kind || qa.Here != qb.Here || !qa.Steps.SequenceEqual(qb.Steps) || !qa.Way.SequenceEqual(qb.Way)) {
                return false;
            }
        }

        return true;
    }

    private static Snapshot Read(JsonObject sent) {
        var quests = new List<Quest>();
        foreach (var item in (sent["quests"] as JsonArray ?? []).OfType<JsonObject>().Take(150)) {
            var steps = new List<Step>();
            foreach (var step in (item["steps"] as JsonArray ?? []).OfType<JsonObject>().Take(6)) {
                steps.Add(new Step(Short((string?) step["s"], 120), Short((string?) step["where"], 80), step["here"]?.GetValueKind() == System.Text.Json.JsonValueKind.True));
            }

            var kind = Short((string?) item["k"], 10) is "instance" or "story" or "main" ? (string) item["k"]! : "side";
            quests.Add(new Quest(Short((string?) item["id"], 120), Short((string?) item["t"], 120), kind, Short((string?) item["w"], 8),
                (int) Number(item["l"]), item["here"]?.GetValueKind() == System.Text.Json.JsonValueKind.True, steps) {
                Way = [.. (item["way"] as JsonArray ?? []).Take(12).Select(w => Short(w?.GetValueKind() == System.Text.Json.JsonValueKind.String ? (string?) w : "", 80)).Where(w => w.Length > 0)],
            });
        }

        return new Snapshot(Short((string?) sent["place"], 80), quests, "", DateTime.UtcNow);
    }

    private static double Number(JsonNode? node) {
        try {
            var value = node?.GetValue<double>() ?? 0;

            return double.IsFinite(value) ? value : 0;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException) {
            return 0;
        }
    }

    private static string Short(string? text, int max) {
        text = new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();

        return text.Length > max ? text[..max] : text;
    }

    // Made-up quests for the designers, matching the made-up minimap (Unicorn Way, the wizard at 0, 0).
    private const string SamplePick = "sample-main";

    private static readonly Snapshot Sample = new("Unicorn Way", [
        new Quest("sample-instance", "Cleanse the Tower", "instance", "WC", 5, false, [new Step("Defeat Rattlebones", "Wizard City / Rattlebones's Tower", false)]),
        new Quest("sample-story", "A Familiar Face", "story", "WC", 3, true, [new Step("Talk to Headmaster Ambrose", "Wizard City / Unicorn Way", true)]),
        new Quest(SamplePick, "Unicorn Way Ghosts", "main", "WC", 4, false, [new Step("Defeat 3 Lost Souls", "Wizard City / Triton Avenue", false)]) {
            Way = ["Ravenwood", "Triton Avenue"],
        },
        new Quest("sample-side-1", "Lost Souls", "side", "WC", 2, true, [new Step("Defeat 3 Lost Souls", "Wizard City / Unicorn Way", true)]),
        new Quest("sample-side-2", "Trouble in the Commons", "side", "WC", 2, false, [new Step("Talk to Mindy Pixiecrumb", "Wizard City / The Commons", false)]),
    ], "", DateTime.MaxValue);

    private static readonly Route SampleRoute = new(SamplePick, "Unicorn Way Ghosts", "main", [(0, -1800, "To Ravenwood")],
        [(0, -700), (-150, -1200), (0, -1800)], "Triton Avenue", "");

}

/// <summary>
/// Draws one Quest Tracker: its title, then Story, Main and Side, each with its quests (name, and the step now), and
/// tells the overlay where each line is so a click on it picks that quest (or folds a list away). Only drawing and
/// telling: the quests come from the server and the choices from <see cref="OverlayQuestList"/>.
/// </summary>
internal sealed class QuestTrackerView : Control {

    /// <summary>One clickable line: what a click on it does (Action "QuestPick" or "QuestFold", and the quest or the kind).</summary>
    internal sealed record Line(Rect Area, string Action, string Value);

    private readonly OverlayElement _element;
    private readonly OverlayQuestList _look;
    private readonly long _window;
    private readonly bool _hasMap;
    private readonly List<Line> _lines = [];
    private readonly List<Action<DrawingContext>> _drawing = [];
    private readonly List<Action<DrawingContext>> _body = [];
    private readonly List<Line> _bodyLines = [];
    private double _laidOutFor = -1;
    private double _height;
    private double _bodyTop;

    /// <summary>How far one wheel notch or arrow click scrolls.</summary>
    internal const double SCROLL_STEP = 48;

    private static readonly (string Kind, string Label)[] s_kinds = [("instance", "Instance"), ("story", "Story"), ("main", "Main"), ("side", "Side")];

    internal QuestTrackerView(OverlayElement element, long window, bool hasMap) {
        _element = element;
        _look = element.Quests ?? new OverlayQuestList();
        _window = window;
        _hasMap = hasMap;
        OverlayQuestData.Look(window, _look);
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    /// <summary>Where each clickable line is, on <paramref name="picture"/> (for the overlay's click windows).</summary>
    internal IEnumerable<(Rect Area, Line Line)> Lines(Visual picture) {
        if (this.TranslatePoint(new Point(0, 0), picture) is not { } at) {
            yield break;
        }

        var shown = new Rect(Bounds.Size);
        var body = BodyArea();
        var scroll = ScrollNow();
        var all = _lines.Select(l => (Area: l.Area.Intersect(shown), Line: l))
            .Concat(_bodyLines.Select(l => (Area: l.Area.Translate(new Vector(0, -scroll)).Intersect(body), Line: l)))
            .Concat(Arrows().Select(l => (Area: l.Area.Intersect(shown), Line: l)));
        foreach (var (seen, line) in all) {
            // Only what is seen (the list scrolls; what is above or below isn't clickable).
            if (seen.Width > 2 && seen.Height > 2) {
                yield return (seen.Translate(new Vector(at.X, at.Y)), line);
            }
        }
    }

    // The part of the tracker the list scrolls in (under the title).
    private Rect BodyArea() => new(0, _bodyTop, Bounds.Width, Math.Max(0, Bounds.Height - _bodyTop));

    private double Room() => Math.Max(0, _height - Bounds.Height);

    private double ScrollNow() {
        OverlayQuestData.ScrollRoom(_window, Room());

        return OverlayQuestData.Scroll(_window);
    }

    // The scroll arrows at the right, when the list doesn't fit: up at the top of the list, down at the bottom.
    private IEnumerable<Line> Arrows() {
        if (Room() < 1 || Bounds.Height - _bodyTop < 40) {
            yield break;
        }

        var size = Math.Max(16, _look.StepSize + 8);
        var x = Bounds.Width - size;
        yield return new Line(new Rect(x, _bodyTop, size, size), OverlayAction.QUEST_SCROLL, "up");
        yield return new Line(new Rect(x, Bounds.Height - size, size, size), OverlayAction.QUEST_SCROLL, "down");
    }

    protected override Size MeasureOverride(Size availableSize) {
        var width = double.IsFinite(availableSize.Width) && availableSize.Width > 0 ? availableSize.Width : 260;
        LayOut(width);

        var tallest = double.IsFinite(availableSize.Height) && _element.Height > 0 ? availableSize.Height : _look.MaxHeight;

        return new Size(width, Math.Min(_height, Math.Max(40, tallest)));
    }

    protected override Size ArrangeOverride(Size finalSize) {
        LayOut(finalSize.Width);

        return finalSize;
    }

    public override void Render(DrawingContext context) {
        foreach (var draw in _drawing) {
            draw(context);
        }

        var scroll = ScrollNow();
        using (context.PushClip(BodyArea())) {
            using (context.PushTransform(Matrix.CreateTranslation(0, -scroll))) {
                foreach (var draw in _body) {
                    draw(context);
                }
            }
        }

        // When the list doesn't all fit: a thin bar showing where you are, and the two arrows.
        var room = Room();
        var body = BodyArea();
        if (room < 1 || body.Height < 40) {
            return;
        }

        var color = OverlayRenderer.Parse(_look.StepColor) ?? Colors.White;
        var size = Math.Max(16, _look.StepSize + 8);
        var track = new Rect(Bounds.Width - size / 2 - 1.5, body.Top + size + 2, 3, Math.Max(4, body.Height - (2 * size) - 4));
        context.DrawRectangle(new SolidColorBrush(color, 0.18), null, track, 1.5, 1.5);
        var thumbHeight = Math.Max(10, track.Height * body.Height / _height);
        var thumbTop = track.Top + ((track.Height - thumbHeight) * (scroll / room));
        context.DrawRectangle(new SolidColorBrush(color, 0.75), null, new Rect(track.X, thumbTop, track.Width, thumbHeight), 1.5, 1.5);
        foreach (var arrow in Arrows()) {
            var up = arrow.Value == "up";
            var can = up ? scroll > 0.5 : scroll < room - 0.5;
            var box = arrow.Area.Deflate(1);
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xB0, 0x10, 0x14, 0x22)), new Pen(new SolidColorBrush(color, can ? 0.8 : 0.3), 1), box, 4, 4);
            var (cx, cy, r) = (box.Center.X, box.Center.Y, box.Width * 0.22);
            var geometry = new StreamGeometry();
            using (var g = geometry.Open()) {
                g.BeginFigure(new Point(cx - r, up ? cy + (r / 2) : cy - (r / 2)), true);
                g.LineTo(new Point(cx + r, up ? cy + (r / 2) : cy - (r / 2)));
                g.LineTo(new Point(cx, up ? cy - (r / 2) - 1 : cy + (r / 2) + 1));
                g.EndFigure(true);
            }

            context.DrawGeometry(new SolidColorBrush(color, can ? 1 : 0.35), null, geometry);
        }
    }

    // Works out every line and what is drawn where, for this width.
    private void LayOut(double width) {
        if (Math.Abs(width - _laidOutFor) < 0.5) {
            return;
        }

        _laidOutFor = width;
        Fill(width);
        // Too tall: laid out again, narrower, so the scroll arrows don't cover the words.
        if (_height > _look.MaxHeight || (_element.Height > 0 && _height > _element.Height)) {
            Fill(Math.Max(60, width - Math.Max(16, _look.StepSize + 8) - 4));
        }
    }

    private void Fill(double width) {
        _lines.Clear();
        _drawing.Clear();
        _body.Clear();
        _bodyLines.Clear();
        var y = 0.0;
        var text = Brush(_look.TextColor);
        var stepBrush = Brush(_look.StepColor);
        if (_look.Title.Length > 0) {
            var title = Text(_look.Title, _look.TitleSize + 2, text, true, width);
            var at = y;
            _drawing.Add(c => Words(c, title, new Point(0, at), text));
            y += title.Height + 4;
        }

        _bodyTop = y;

        var data = OverlayQuestData.For(_window);
        var note = data is null || (data.Stale && data.Note.Length == 0)
            ? OverlayHost.Link.Connected ? OverlayHost.Link.SendsQuests ? "Waiting for your quests..." : "This server's Imlight is too old for the Quest Tracker. The host needs to update it."
                : "Not linked to a server yet. Start the game with Play in Ahziael."
            : data.Note;
        if (note.Length > 0 || data is null) {
            var words = Text(note, _look.StepSize, stepBrush, false, width);
            var at = y;
            _body.Add(c => Words(c, words, new Point(0, at), stepBrush));
            _height = y + words.Height + 2;

            return;
        }

        var pick = OverlayQuestData.Pick(_window);
        var any = false;
        foreach (var (kind, label) in s_kinds) {
            var on = kind switch { "instance" => _look.Instance, "story" => _look.Story, "main" => _look.Main, _ => _look.Side };
            if (!on) {
                continue;
            }

            var quests = data.Quests.Where(q => q.Kind == kind || (kind == "main" && _look.StoryInMain && q.Kind == "story")).ToList();
            if (quests.Count == 0) {
                continue;
            }

            any = true;
            var color = Brush(_look.ColorOf(kind));
            var folded = OverlayQuestData.Folded(_window, kind);
            var header = Text($"{(folded ? "▸" : "▾")} {label} ({quests.Count})", _look.TitleSize, color, true, width);
            var headerAt = y;
            _body.Add(c => Words(c, header, new Point(0, headerAt), color));
            _bodyLines.Add(new Line(new Rect(0, y - 1, width, header.Height + 2), OverlayAction.QUEST_FOLD, kind));
            y += header.Height + 3;
            if (folded) {
                continue;
            }

            foreach (var quest in quests) {
                var top = y;
                var picked = quest.Id == pick;
                var name = Text(quest.Title, _look.TitleSize, text, picked, width - 14);
                name.MaxLineCount = 2;
                var nameAt = y + 2;
                y = nameAt + name.Height;
                var below = new List<(FormattedText Text, double At, IBrush Brush)>();
                void Below(string words, double size, IBrush brush, bool bold = false) {
                    var line = Text(words, size, brush, bold, width - 14);
                    line.MaxLineCount = 2;
                    below.Add((line, y + 1, brush));
                    y += 1 + line.Height;
                }

                if (_look.Steps && quest.Steps.Count > 0) {
                    var first = quest.Steps[0];
                    var more = quest.Steps.Count > 1 ? $" (+{quest.Steps.Count - 1} more)" : "";
                    Below(first.Text + more, _look.StepSize, stepBrush);
                    // Where it is, as the game's quest book says it ("Azteca / Xibalba").
                    if (first.Where.Length > 0) {
                        Below(first.Here ? $"{first.Where} (you're here)" : first.Where, _look.StepSize - 1, stepBrush);
                    }
                }

                // The picked quest's step is in another area: the areas to go through, door by door.
                if (picked && quest.Way.Count > 0) {
                    var kindBrush = Brush(_look.ColorOf(quest.Kind));
                    Below("Way: " + string.Join("  >  ", quest.Way), _look.StepSize, kindBrush, true);
                }

                y += 4;
                var bottom = y;
                var area = new Rect(0, top, width, bottom - top);
                var kindColor = OverlayRenderer.Parse(_look.ColorOf(quest.Kind)) ?? Colors.White;
                _body.Add(c => {
                    if (picked) {
                        c.DrawRectangle(new SolidColorBrush(kindColor, 0.22), new Pen(new SolidColorBrush(kindColor, 0.8), 1), area.Deflate(new Thickness(0, 0, 0, 1)), 5, 5);
                    }

                    // The quest's kind as a small bar at its left.
                    c.DrawRectangle(new SolidColorBrush(kindColor), null, new Rect(3, top + 4, 3, Math.Max(4, bottom - top - 8)), 1.5, 1.5);
                    Words(c, name, new Point(11, nameAt), text);
                    foreach (var (line, at, brush) in below) {
                        Words(c, line, new Point(11, at), brush);
                    }
                });
                _bodyLines.Add(new Line(area, OverlayAction.QUEST_PICK, quest.Id));
            }

            y += 2;
        }

        if (!any) {
            var none = Text("No quests right now.", _look.StepSize, stepBrush, false, width);
            var at = y;
            _body.Add(c => Words(c, none, new Point(0, at), stepBrush));
            y += none.Height + 2;
        }

        // A picked quest with no minimap to show its way.
        if (pick.Length > 0 && _look.Route && !_hasMap) {
            var hint = Text("Add the Minimap to see the way to the picked quest.", _look.StepSize, stepBrush, false, width);
            var at = y + 2;
            _body.Add(c => Words(c, hint, new Point(0, at), stepBrush));
            y = at + hint.Height + 2;
        }

        _height = y;
    }

    // Words drawn as their letters' outlines (drawn as text they come out blank on the overlay over the game on Windows).
    private static void Words(DrawingContext context, FormattedText text, Point at, IBrush brush) {
        if (text.BuildGeometry(at) is { } outline) {
            context.DrawGeometry(brush, null, outline);
        }
    }

    private FormattedText Text(string words, double size, IBrush brush, bool bold, double width) {
        var family = string.IsNullOrWhiteSpace(_element.Font) ? FontFamily.Default : new FontFamily(_element.Font);
        var text = new FormattedText(words, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.SemiBold), Math.Max(4, size), brush) {
            MaxTextWidth = Math.Max(20, width), Trimming = TextTrimming.CharacterEllipsis,
        };

        return text;
    }

    private static IBrush Brush(string color) => OverlayRenderer.Parse(color) is { } c ? new SolidColorBrush(c) : Brushes.White;

}

/// <summary>
/// The picked quest's way, drawn on the Minimap (and only there): a line along the streets from the wizard to the
/// nearest step, a ring on each step spot here, and, when the step is in another area, a note saying where to go.
/// </summary>
internal static class OverlayQuestRouteDrawing {

    internal static void Draw(DrawingContext context, long window, Func<double, double, Point> project, Point wizard, double mapW, double mapH, double iconSize) {
        if (OverlayQuestData.RouteFor(window) is not { } route) {
            return;
        }

        // Ultimus's own map draws his line in the same colours and width as the player's Quest Tracker line.
        var look = OverlayQuestData.LookOf(window) ?? (window == OverlayUltimusData.KEY ? OverlayQuestData.AnyLook() : null) ?? new OverlayQuestList();
        var color = OverlayRenderer.Parse(look.ColorOf(route.Kind)) ?? Colors.Gold;
        var width = look.RouteWidth;
        if (route.Points.Count > 0) {
            // First, under the line: faded trails to the step's other spots (Niduus, 2026-11-14: "draw faded trails for
            // the other targets that are not the closes, with the closes not being faded at all").
            var faded = Color.FromArgb((byte) (color.A * 0.38), color.R, color.G, color.B);
            foreach (var trail in route.Trails) {
                var trailLine = new StreamGeometry();
                using (var g = trailLine.Open()) {
                    g.BeginFigure(wizard, false);
                    for (var i = 0; i < trail.Path.Count; i++) {
                        var at = project(trail.Path[i].X, trail.Path[i].Y);
                        if (i > 0 && trail.Pads.Contains(i - 1)) {
                            g.EndFigure(false);
                            g.BeginFigure(at, false);
                            continue;
                        }

                        g.LineTo(at);
                    }

                    g.EndFigure(false);
                }

                context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)), width * 0.8 + 1.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), trailLine);
                context.DrawGeometry(null, new Pen(new SolidColorBrush(faded), width * 0.8, new DashStyle([1.4, 2.2], 0), PenLineCap.Round, PenLineJoin.Round), trailLine);
            }

            // The walk, broken at each teleporter: the line stops on the pad and goes on from where it lands (the
            // teleporter itself drawn as a small ring at both ends).
            var line = new StreamGeometry();
            var jumps = new List<(Point Pad, Point Lands)>();
            using (var g = line.Open()) {
                g.BeginFigure(wizard, false);
                var path = route.Path.Count > 0 ? route.Path : [(route.Points[0].X, route.Points[0].Y)];
                for (var i = 0; i < path.Count; i++) {
                    var at = project(path[i].X, path[i].Y);
                    if (i > 0 && route.Pads?.Contains(i - 1) == true) {
                        g.EndFigure(false);
                        jumps.Add((project(path[i - 1].X, path[i - 1].Y), at));
                        g.BeginFigure(at, false);
                        continue;
                    }

                    g.LineTo(at);
                }

                g.EndFigure(false);
            }

            // A dark edge under the line, so it reads on streets and grass alike.
            context.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), width + 2.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);
            context.DrawGeometry(null, new Pen(new SolidColorBrush(color), width, new DashStyle([2.2, 1.6], 0), PenLineCap.Round, PenLineJoin.Round), line);
            foreach (var (pad, lands) in jumps) {
                foreach (var spot in new[] { pad, lands }) {
                    context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), 3.5), spot, iconSize * 0.55, iconSize * 0.55);
                    context.DrawEllipse(null, new Pen(new SolidColorBrush(color), 1.8, new DashStyle([1.2, 1.0], 0)), spot, iconSize * 0.55, iconSize * 0.55);
                }
            }

            // The nearest spot's ring full colour, the others faded like their trails.
            for (var i = route.Points.Count - 1; i >= 0; i--) {
                var at = project(route.Points[i].X, route.Points[i].Y);
                var nearest = i == 0;
                context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(nearest ? (byte) 0xB0 : (byte) 0x40, 0, 0, 0)), 4), at, iconSize, iconSize);
                context.DrawEllipse(null, new Pen(new SolidColorBrush(nearest ? color : faded), 2.2), at, iconSize, iconSize);
            }
        }
    }

}
