/*
 * Imlight
 * Copyright (C) 2026 Revive101
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
using System.Linq;
using System.Threading;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// The Ultimus Spectator add-on's data (Niduus, 2026-11-13: "Spectate Ultimus, able to view his location and quests via
/// Minimap (Required) and when in battle, able to see his spells in a new UI module. Ultimus Spectator requires Minimap, but
/// doesn't Override PLAYER Minimap. It allows it's own Ultimus Minimap and will always show the Quest Tracking of Ultimus'
/// Quests and NOT the players"). Ultimus is the same on every game window, so his map is kept once, under its own key
/// (<see cref="KEY"/>) beside the player's maps; the player's minimap and Quest Tracker are never touched.
/// </summary>
internal static class OverlayUltimusData {

    /// <summary>Where Ultimus's map, and his quest's way, are kept in the minimap's and Quest Tracker's data (never a game window).</summary>
    internal const long KEY = long.MinValue + 77;

    internal sealed record Step(string Text, string Where, bool Here);

    /// <summary>His quest tracker's details (Niduus, 2026-11-14: "in detail"): world, why, what now, the step after, parked steps.</summary>
    internal sealed record Tracker(string World, string Why, string Target, string Next, IReadOnlyList<string> Parked, string ReadAhead, string Stuck = "");

    internal sealed record Card(string Name, string School, int Rank, bool Castable, bool Picked);

    internal sealed record Fighter(string Name, int Health, int Most, bool Alive);

    internal sealed record Battle(int Round, int Health, int Most, int Pips, int Power, int Shadow, IReadOnlyList<Card> Hand, string Pick, string Why,
                                  IReadOnlyList<string> Discards, IReadOnlyList<string> Casts, IReadOnlyList<Fighter> Enemies, IReadOnlyList<Fighter> Allies,
                                  string Deck);

    internal sealed record Info(string Name, int Level, int Health, int Most, string Place, string Doing, string Quest, string QuestTitle, string Kind,
                                string NextStep, IReadOnlyList<Step> Steps, IReadOnlyList<string> Way, IReadOnlyList<string> Others, Battle? Fight,
                                string Note, DateTime At, int QuestLevel = 0, string World = "", Tracker? Detail = null, string Progress = "",
                                string Estimate = "", string WorldLine = "", string SlowLine = "") {

        internal bool Stale => DateTime.UtcNow - At > TimeSpan.FromSeconds(8);

    }

    /// <summary>
    /// Where the Quest Team Spectator's map and quest way are kept (Niduus, 2026-11-15: "They should be their own separate
    /// plugins ... only to spectate their designation"): a feed of its own beside Ultimus's, so both add-ons can be on the
    /// overlay at once without touching each other.
    /// </summary>
    internal const long TEAM_KEY = long.MinValue + 78;

    /// <summary>Whether a map key is one of the spectators' (Ultimus's or the Quest Team's), not a game window.</summary>
    internal static bool IsSpectator(long key) => key is KEY or TEAM_KEY;

    /// <summary>The feed a spectator piece shows: the Quest Team Spectator's own, or Ultimus's.</summary>
    internal static long KeyOf(OverlayElement e) => e.WatchesTeam ? TEAM_KEY : KEY;

    /// <summary>The Quest Team's roles, in the team's order (the Quest Team Spectator cycles through them only).</summary>
    internal static readonly string[] TeamRoles = ["Ice", "Life", "Death", "Storm"];

    // The member watched, remembered across restarts of the overlay (Niduus, 2026-11-15).
    private static int s_teamIndex = LoadWatched();

    private static string WatchedFile => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Edition.INSTALL_FOLDER_NAME, "overlay-team-watch.txt");

    private static int LoadWatched() {
        try {
            var role = System.IO.File.Exists(WatchedFile) ? System.IO.File.ReadAllText(WatchedFile).Trim() : "";
            var at = Array.FindIndex(TeamRoles, r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));
            return Math.Max(0, at);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) {
            return 0;
        }
    }

    private static void SaveWatched(string role) {
        try {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(WatchedFile)!);
            System.IO.File.WriteAllText(WatchedFile, role);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) {
            // Only a convenience: the next start watches Freya (Ice) again.
        }
    }

    // One feed per spectator: what was last heard, and whether the overlay shows it.
    private sealed class Feed {
        public Info? Info;
        public string Said = "";
        public int Version;
        public bool Wanted;
    }

    private static readonly object s_lock = new();
    private static readonly Dictionary<long, Feed> s_feeds = new() { [KEY] = new Feed(), [TEAM_KEY] = new Feed() };

    private static Feed FeedOf(long key) => s_feeds[key == TEAM_KEY ? TEAM_KEY : KEY];

    /// <summary>The spectator feeds the overlay shows (with the Minimap they need): the link asks the server for each.</summary>
    internal static long[] WantedKeys {
        get {
            lock (s_lock) {
                return [.. s_feeds.Where(f => f.Value.Wanted).Select(f => f.Key)];
            }
        }
    }

    internal static bool Wanted => WantedKeys.Length > 0;

    internal static void SetWanted(long key, bool wanted) {
        lock (s_lock) {
            FeedOf(key).Wanted = wanted;
        }
    }

    internal static bool IsWanted(long key) {
        lock (s_lock) {
            return FeedOf(key).Wanted;
        }
    }

    /// <summary>Who the server is asked for: "" for Ultimus (KEY), else the Quest Team role watched now (TEAM_KEY).</summary>
    internal static string Who(long key) => key == TEAM_KEY ? TeamRoles[Volatile.Read(ref s_teamIndex) % TeamRoles.Length] : "";

    /// <summary>The watched Quest Team member's school colour (for its map's border); null for Ultimus's feed.</summary>
    internal static Color? SchoolColor(long key) => key != TEAM_KEY ? null : Who(key) switch {
        "Ice" => Color.FromArgb(0xF0, 0x6E, 0xC8, 0xFF),
        "Life" => Color.FromArgb(0xF0, 0x5C, 0xD6, 0x5C),
        "Death" => Color.FromArgb(0xF0, 0xA8, 0xAE, 0xB4),
        "Storm" => Color.FromArgb(0xF0, 0xB4, 0x6E, 0xFF),
        _ => null,
    };

    /// <summary>A plain name of whom a feed watches, for its boxes' headings and notes.</summary>
    internal static string Watched(long key) => key == TEAM_KEY ? $"Quest Team: {MemberName(Who(key))} ({Who(key)})" : "Ultimus";

    // The Quest Team's locked names (2026-11-14), by role.
    private static readonly string[] s_teamNames = ["Freya", "Terra", "Anubis", "Chaac"];

    /// <summary>A Quest Team role's locked name ("Freya" for Ice).</summary>
    internal static string MemberName(string role) {
        var at = Array.FindIndex(TeamRoles, r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));
        return at >= 0 ? s_teamNames[at] : role;
    }

    /// <summary>The role meant by a name or school ("Freya", "freya (ice)", "Ice"), or null.</summary>
    internal static string? RoleOf(string? meant) {
        var text = (meant ?? "").Trim();
        for (var i = 0; i < TeamRoles.Length; i++) {
            if (text.StartsWith(TeamRoles[i], StringComparison.OrdinalIgnoreCase) || text.StartsWith(s_teamNames[i], StringComparison.OrdinalIgnoreCase)) {
                return TeamRoles[i];
            }
        }

        return null;
    }

    /// <summary>
    /// The Quest Team Spectator's member buttons (Niduus, 2026-11-15: "spectate the desired"): watch the member named
    /// straight away. Returns its role, or null when the name is not one of the four.
    /// </summary>
    internal static string? WatchMember(string? meant) {
        if (RoleOf(meant) is not { } role) {
            return null;
        }

        var at = Array.IndexOf(TeamRoles, role);
        if (at != Volatile.Read(ref s_teamIndex) % TeamRoles.Length) {
            Switch(at);
        }

        return role;
    }

    /// <summary>The Quest Team Spectator's button: the next member (only the four, in the team's order). Ultimus's feed is untouched.</summary>
    internal static string NextMember() => Switch((Volatile.Read(ref s_teamIndex) + 1) % TeamRoles.Length);

    private static string Switch(int next) {
        Volatile.Write(ref s_teamIndex, next);
        SaveWatched(TeamRoles[next]);
        lock (s_lock) {
            var feed = FeedOf(TEAM_KEY);
            feed.Info = null;
            feed.Said = "";
            feed.Version++;
        }

        // The new member's map is drawn fresh.
        OverlayMinimapData.Heard(TEAM_KEY, new JsonObject { ["ok"] = false, ["error"] = $"Switching to the Quest Team's {TeamRoles[next]} wizard..." });

        return TeamRoles[next];
    }

    /// <summary>Changes when what any spectator's quest box or spells panel shows changes (the overlay is laid out again then).</summary>
    internal static int Version {
        get {
            lock (s_lock) {
                return s_feeds.Values.Sum(f => f.Version);
            }
        }
    }

    /// <summary>The feed's wizard as last heard, or (for the designers, window 0) a made-up one.</summary>
    internal static Info? For(long window, long key = KEY) {
        if (window == 0) {
            return key == TEAM_KEY ? TeamSample : Sample;
        }

        lock (s_lock) {
            return FeedOf(key).Info;
        }
    }

    /// <summary>The server's answer to op "ultimus" for one feed.</summary>
    internal static void Heard(long key, JsonObject answer) {
        key = key == TEAM_KEY ? TEAM_KEY : KEY;
        // The map (with the quest's way) goes where the minimap keeps maps, under the feed's own key.
        OverlayMinimapData.Heard(key, answer);
        Info info;
        string said;
        if (answer["ok"]?.GetValue<bool>() == true && answer["ultimus"] is JsonObject sent) {
            // A Quest Team answer for a member no longer watched (Next member was pressed meanwhile) is dropped.
            if (key == TEAM_KEY && !string.Equals((string?) sent["role"] ?? "", Who(key), StringComparison.OrdinalIgnoreCase)) {
                return;
            }

            info = Read(sent, key);
            said = sent.ToJsonString();
            // Its quest is always the one drawn on its map (never a quest the player picked): the way the server sent with the map.
            var drawn = answer["map"] is JsonObject map && map["route"] is JsonObject route ? (string?) route["quest"] : null;
            OverlayQuestData.Follow(key, string.IsNullOrEmpty(drawn) ? info.Quest : drawn);
        }
        else {
            var error = ((string?) answer["error"] ?? "").Trim();
            if (error == "busy") {
                return;
            }

            error = error.Length > 200 ? error[..200] : error;
            info = new Info(Watched(key), 0, 0, 0, "", "", "", "", "", "", [], [], [], null, error, DateTime.UtcNow);
            said = "error:" + error;
            OverlayQuestData.Follow(key, "");
        }

        lock (s_lock) {
            var feed = FeedOf(key);
            var wasStale = feed.Info?.Stale ?? true;
            feed.Info = info;
            if (said != feed.Said || wasStale) {
                feed.Said = said;
                feed.Version++;
            }
        }
    }

    /// <summary>Once a second: a feed that stopped coming is laid out again once, with a note.</summary>
    internal static void Tick() {
        lock (s_lock) {
            foreach (var feed in s_feeds.Values) {
                if (!feed.Wanted) {
                    feed.Info = null;
                    feed.Said = "";
                    continue;
                }

                if (feed.Info is { Stale: true } && feed.Said != "stale") {
                    feed.Said = "stale";
                    feed.Version++;
                }
            }
        }
    }

    private static Info Read(JsonObject sent, long key) {
        var steps = (sent["steps"] as JsonArray ?? []).OfType<JsonObject>().Take(6)
            .Select(s => new Step(Short((string?) s["s"], 120), Short((string?) s["where"], 80), s["here"]?.GetValueKind() == System.Text.Json.JsonValueKind.True)).ToList();
        Tracker? tracker = null;
        if (sent["detail"] is JsonObject detail) {
            // The tracker's own steps: the game's words with counts, and the one he is on now marked (shown with "here").
            if (detail["steps"] is JsonArray { Count: > 0 } open) {
                steps = [.. open.OfType<JsonObject>().Take(6).Select(s => new Step(
                    Short((string?) s["s"], 120) + (Short((string?) s["drops"], 100) is { Length: > 0 } drops ? $" [{drops}]" : ""),
                    Short((string?) s["where"], 80), s["now"]?.GetValueKind() == System.Text.Json.JsonValueKind.True))];
            }

            tracker = new Tracker(Short((string?) detail["questWorld"], 40) is { Length: > 0 } w ? w : Short((string?) detail["world"], 40),
                Short((string?) detail["why"], 160),
                // His X, Y and height and his step's, after what he is doing (Niduus, 2026-11-15: floors).
                Short((string?) detail["target"], 120) + (Short((string?) detail["position"], 140) is { Length: > 0 } position ? $" | {position}" : ""),
                Short((string?) detail["next"], 120),
                Texts(detail["parked"], 4, 320), Short((string?) detail["readAhead"], 160), Short((string?) detail["stuck"], 200));
        }

        var way = Texts(sent["way"], 12, 80);
        var others = (sent["quests"] as JsonArray ?? []).OfType<JsonObject>().Take(40)
            .Where(q => !string.Equals((string?) q["id"], (string?) sent["quest"], StringComparison.OrdinalIgnoreCase))
            .Select(q => Short((string?) q["t"], 120)).Where(t => t.Length > 0).ToList();
        Battle? battle = null;
        if (sent["battle"] is JsonObject fight) {
            var hand = (fight["hand"] as JsonArray ?? []).OfType<JsonObject>().Take(12)
                .Select(c => new Card(Short((string?) c["n"], 60), Short((string?) c["s"], 12), (int) Number(c["r"]),
                    c["ok"]?.GetValueKind() == System.Text.Json.JsonValueKind.True, c["pick"]?.GetValueKind() == System.Text.Json.JsonValueKind.True)).ToList();
            battle = new Battle((int) Number(fight["round"]), (int) Number(fight["hp"]), (int) Number(fight["max"]), (int) Number(fight["pips"]),
                (int) Number(fight["power"]), (int) Number(fight["shadow"]), hand, Short((string?) fight["pick"], 120), Short((string?) fight["why"], 160),
                Texts(fight["discards"], 8, 60), Texts(fight["casts"], 8, 140), Fighters(fight["enemies"]), Fighters(fight["allies"]),
                Short((string?) fight["deck"], 400));
        }

        return new Info(Short((string?) sent["name"], 40) is { Length: > 0 } name ? name : Watched(key), (int) Number(sent["level"]), (int) Number(sent["hp"]),
            (int) Number(sent["max"]), Short((string?) sent["place"], 80), Short((string?) sent["doing"], 120), Short((string?) sent["quest"], 120),
            Short((string?) sent["questTitle"], 120), Short((string?) sent["kind"], 10), Short((string?) sent["step"], 160), steps, way, others, battle, "",
            DateTime.UtcNow, (int) Number(sent["questLevel"]), Short((string?) sent["world"], 40), tracker,
            // Quest CURR/END in the world's story and the time to the next world (Niduus, 2026-11-15).
            sent["progress"] is JsonObject progress ? Short((string?) progress["line"], 80) : "",
            sent["progress"] is JsonObject paced ? Short((string?) paced["estimate"], 160) : "",
            // "World 3 of 19" (Niduus's main worlds) and the red line when this quest runs long.
            sent["progress"] is JsonObject numbered ? Short((string?) numbered["worldLine"], 40) : "",
            sent["progress"] is JsonObject slow ? Short((string?) slow["slowLine"], 160) : "");
    }

    private static List<Fighter> Fighters(JsonNode? node) => (node as JsonArray ?? []).OfType<JsonObject>().Take(8)
        .Select(f => new Fighter(Short((string?) f["n"], 60), (int) Number(f["hp"]), (int) Number(f["max"]), f["alive"]?.GetValueKind() != System.Text.Json.JsonValueKind.False))
        .ToList();

    private static List<string> Texts(JsonNode? node, int count, int max) => (node as JsonArray ?? []).Take(count)
        .Select(t => Short(t?.GetValueKind() == System.Text.Json.JsonValueKind.String ? (string?) t : "", max)).Where(t => t.Length > 0).ToList();

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

    // A made-up Ultimus for the designers, so the pieces can be placed and styled before he is watched.
    private static readonly Info Sample = new("Ultimus", 9, 612, 780, "Unicorn Way", "walking to Lost Souls", "sample-main", "Unicorn Way Ghosts", "main",
        "defeat 3 Lost Souls", [new Step("Defeat 3 Lost Souls", "Wizard City / Triton Avenue", false)], ["Ravenwood", "Triton Avenue"],
        ["Lost Souls", "Trouble in the Commons"],
        new Battle(3, 612, 780, 3, 1, 0, [
            new Card("Scarab", "Balance", 1, true, false), new Card("Scorpion", "Balance", 2, true, true), new Card("Sandstorm", "Balance", 3, false, false),
            new Card("Spirit Blade", "Balance", 0, true, false), new Card("Weakness", "Balance", 0, true, false), new Card("Fairy", "Life", 3, false, false),
        ], "Scorpion on Lost Soul", "the weakest enemy: Lost Soul with Scorpion (about 145)", ["Elemental Shield"],
            ["Round 3: casts Scorpion on Lost Soul", "Round 2: casts Spirit Blade on himself", "Round 1: passes"],
            [new Fighter("Lost Soul", 120, 300, true), new Fighter("Lost Soul", 300, 300, true)], [], "Scarab x4, Scorpion x3, Sandstorm x2"),
        "", DateTime.MaxValue, 5, "Wizard City", null, "Quest 14/52 in Wizard City", "about 3 h 20 min to Krokotopia at his pace (about 5 min a quest)", "World 1 of 19");

    // A made-up Quest Team wizard for the designers.
    private static readonly Info TeamSample = Sample with {
        Name = "Freya (Ice)", Doing = "walking to Lost Souls with the team", Fight = null, Estimate = "about 4 h to Krokotopia at their pace (about 6 min a quest)",
    };

}

/// <summary>
/// The Ultimus Spectator's pieces: Ultimus's own minimap (the player's Minimap's drawing, centred on him, with his quest's
/// way), his quest box, and his spells in battle. Only drawing: what they show comes from the server.
/// </summary>
internal static class UltimusViews {

    /// <summary>
    /// The Ultimus Spectator needs the Minimap add-on downloaded (Niduus, 2026-11-14: required "for install, for Spectate
    /// to work"); the player's own Minimap does not have to be on the overlay. Without the add-on, each piece says so.
    /// </summary>
    // The Quest Team's four names under its quest box; a click on one watches it (OverlayHost.PlaceHits).
    private static void AddNames(Panel panel, long key, Looks look) {
        if (key != OverlayUltimusData.TEAM_KEY) {
            return;
        }

        var names = new WrapPanel { Orientation = Orientation.Horizontal };
        names.Children.Add(Line("Watch: ", look.Size - 2, look.Faint));
        foreach (var role in OverlayUltimusData.TeamRoles) {
            var watched = string.Equals(role, OverlayUltimusData.Who(key), StringComparison.OrdinalIgnoreCase);
            var name = Line(OverlayUltimusData.MemberName(role), look.Size - 2, watched ? look.Gold : look.Text, bold: watched);
            name.Margin = new Thickness(0, 0, 8, 0);
            name.Tag = new MemberPick(role);
            names.Children.Add(name);
        }

        panel.Children.Add(names);
    }

    /// <summary>A Quest Team name in the quest box: a click on it watches that member (OverlayHost.PlaceHits).</summary>
    internal sealed record MemberPick(string Role);

    private static readonly IBrush s_slow = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));

    internal const string NEEDS_MINIMAP = "This spectator needs the Minimap add-on downloaded. Get it in Ahziael: Play > Overlay > Add-ons > Niduus's add-ons.";

    /// <summary>Ultimus's quest box: who he is, where, and his quest now with its step and way (never the player's quests).</summary>
    internal static Control Quest(OverlayElement e, long window, bool hasMap) {
        var panel = new StackPanel { Spacing = 2 };
        var look = Look(e);
        if (!hasMap) {
            panel.Children.Add(Line(NEEDS_MINIMAP, look.Size, look.Faint, wrap: true));

            return panel;
        }

        var key = OverlayUltimusData.KeyOf(e);
        var info = OverlayUltimusData.For(window, key);
        if (Waiting(info, key) is { } waiting) {
            panel.Children.Add(Line(OverlayUltimusData.Watched(key), look.Size + 2, look.Gold, bold: true));
            panel.Children.Add(Line(waiting, look.Size - 1, look.Faint, wrap: true));
            AddNames(panel, key, look);

            return panel;
        }

        panel.Children.Add(Line($"{info!.Name}" + (info.Level > 0 ? $"  ·  level {info.Level}" : ""), look.Size + 2, look.Gold, bold: true));
        if (info.Most > 0) {
            panel.Children.Add(Bar(info.Health, info.Most, look));
        }

        if (info.Place.Length > 0) {
            panel.Children.Add(Line("In " + info.Place + (info.Fight is not null ? "  (in battle)" : ""), look.Size - 1, look.Text));
        }

        if (info.Doing.Length > 0) {
            panel.Children.Add(Line(Capital(info.Doing), look.Size - 2, look.Faint, wrap: true));
        }

        // Quest CURR/END in the world's story, and the time to the next world at their pace (Niduus, 2026-11-15).
        if (info.Progress.Length > 0) {
            var numbered = info.WorldLine.Length > 0 ? $"{info.WorldLine}  ·  {info.Progress}" : info.Progress;
            panel.Children.Add(Line(numbered, look.Size - 1, look.Gold, bold: true, wrap: true));
            if (info.Estimate.Length > 0) {
                panel.Children.Add(Line(Capital(info.Estimate), look.Size - 3, look.Faint, wrap: true));
            }

            // Red when the quest now takes much longer than their usual pace (Niduus, 2026-11-15).
            if (info.SlowLine.Length > 0) {
                panel.Children.Add(Line(Capital(info.SlowLine), look.Size - 3, s_slow, bold: true, wrap: true));
            }
        }

        // The Quest Team Spectator: click a name to watch that member (Niduus, 2026-11-15).
        AddNames(panel, key, look);

        panel.Children.Add(new Border { Height = 4 });
        if (info.Quest.Length == 0) {
            panel.Children.Add(Line("No quest picked yet.", look.Size - 1, look.Faint));
        }
        else {
            var color = OverlayRenderer.Parse(new OverlayQuestList().ColorOf(info.Kind)) is { } kind ? new SolidColorBrush(kind) : look.Gold;
            panel.Children.Add(Line(info.QuestTitle.Length > 0 ? info.QuestTitle : info.Quest, look.Size + 1, color, bold: true, wrap: true));
            // In detail (Niduus, 2026-11-14): what kind of quest, its level and world, then every open step with its count.
            var about = string.Join("  ·  ", new[] {
                KindWord(info.Kind), info.QuestLevel > 0 ? $"level {info.QuestLevel}" : "", info.World,
            }.Where(t => t.Length > 0));
            if (about.Length > 0) {
                panel.Children.Add(Line(about, look.Size - 2, look.Faint));
            }

            foreach (var step in info.Steps.Take(6)) {
                // With the tracker's details, "Here" marks the step he works on now; without them, a step in this area.
                var mark = info.Detail is not null && step.Here ? "> " : "• ";
                var where = step.Where.Length > 0 && (info.Detail is not null || !step.Here) ? "  (" + step.Where + ")" : "";
                panel.Children.Add(Line(mark + step.Text + where, look.Size - 1, info.Detail is not null && step.Here ? look.Gold : look.Text, wrap: true));
            }

            if (info.Steps.Count == 0 && info.NextStep.Length > 0) {
                panel.Children.Add(Line("• " + Capital(info.NextStep), look.Size - 1, look.Text, wrap: true));
            }

            if (info.Way.Count > 0) {
                panel.Children.Add(Line("Way: " + string.Join(" > ", info.Way), look.Size - 2, look.Faint, wrap: true));
            }

            if (info.Detail is { } d) {
                // The Quest Team's stuck line (Niduus, 2026-11-15).
                if (d.Stuck.Length > 0) {
                    panel.Children.Add(Line("Team stuck: " + d.Stuck, look.Size - 2, look.Gold, bold: true, wrap: true));
                }

                if (d.Target.Length > 0) {
                    panel.Children.Add(Line("Now: " + d.Target, look.Size - 2, look.Text, wrap: true));
                }

                if (d.Why.Length > 0) {
                    panel.Children.Add(Line("Why: " + d.Why, look.Size - 3, look.Faint, wrap: true));
                }

                if (d.Next.Length > 0) {
                    panel.Children.Add(Line("After that: " + d.Next, look.Size - 3, look.Faint, wrap: true));
                }

                if (d.ReadAhead.Length > 0) {
                    panel.Children.Add(Line("Read-ahead warned: " + d.ReadAhead, look.Size - 3, look.Faint, wrap: true));
                }
            }
        }

        if (info.Detail is { Parked.Count: > 0 } help) {
            panel.Children.Add(Line("Needs help", look.Size - 1, look.Gold, bold: true));
            foreach (var parked in help.Parked) {
                panel.Children.Add(Line("• " + parked, look.Size - 3, look.Faint, wrap: true));
            }
        }

        if (info.Others.Count > 0) {
            var named = string.Join(", ", info.Others.Take(3));
            var more = info.Others.Count > 3 ? $" and {info.Others.Count - 3} more" : "";
            panel.Children.Add(Line($"Also holds: {named}{more}.", look.Size - 3, look.Faint, wrap: true));
        }

        return panel;
    }

    private static string KindWord(string kind) => kind switch {
        "story" => "Story quest",
        "main" => "Main quest",
        "instance" => "Dungeon quest",
        "side" => "Side quest",
        _ => "",
    };

    /// <summary>
    /// Ultimus's spells in battle: his hand (the card he picks lit up, the ones he can't cast yet faded), his pips and health,
    /// what he casts and why, what he threw away, his last rounds and the enemies. Over a game it shows only while he fights.
    /// </summary>
    internal static Control? Spells(OverlayElement e, long window, bool hasMap) {
        var panel = new StackPanel { Spacing = 2 };
        var look = Look(e);
        if (!hasMap) {
            panel.Children.Add(Line(NEEDS_MINIMAP, look.Size, look.Faint, wrap: true));

            return panel;
        }

        var info = OverlayUltimusData.For(window, OverlayUltimusData.KeyOf(e));
        if (info is null || info.Stale || info.Note.Length > 0 || info.Fight is not { } fight) {
            return null;
        }

        panel.Children.Add(Line($"{info.Name} in battle" + (fight.Round > 0 ? $"  ·  round {fight.Round}" : ""), look.Size + 2, look.Gold, bold: true));
        if (fight.Most > 0) {
            panel.Children.Add(Bar(fight.Health, fight.Most, look));
        }

        panel.Children.Add(Line(Pips(fight), look.Size - 1, look.Text));
        if (fight.Hand.Count > 0) {
            var hand = new WrapPanel { Margin = new Thickness(0, 3, 0, 3) };
            foreach (var card in fight.Hand) {
                hand.Children.Add(CardChip(card, look));
            }

            panel.Children.Add(hand);
        }
        else {
            panel.Children.Add(Line("Waiting for his hand...", look.Size - 1, look.Faint));
        }

        if (fight.Pick.Length > 0) {
            panel.Children.Add(Line((fight.Pick == "passes" ? "Passes" : "Casts " + fight.Pick), look.Size, look.Gold, bold: true, wrap: true));
        }

        if (fight.Why.Length > 0) {
            panel.Children.Add(Line("Why: " + fight.Why, look.Size - 2, look.Faint, wrap: true));
        }

        if (fight.Discards.Count > 0) {
            panel.Children.Add(Line("Threw away: " + string.Join(", ", fight.Discards.Select(d => d.StartsWith("discards ", StringComparison.Ordinal) ? d[9..] : d)),
                look.Size - 2, look.Faint, wrap: true));
        }

        if (fight.Enemies.Count > 0) {
            panel.Children.Add(new Border { Height = 3 });
            foreach (var enemy in fight.Enemies) {
                panel.Children.Add(Line($"{enemy.Name}: " + (enemy.Alive ? $"{enemy.Health:N0}/{enemy.Most:N0}" : "defeated"), look.Size - 2,
                    enemy.Alive ? look.Text : look.Faint));
            }
        }

        if (fight.Casts.Count > 1) {
            panel.Children.Add(new Border { Height = 3 });
            foreach (var cast in fight.Casts.Skip(1).Take(4)) {
                panel.Children.Add(Line(cast, look.Size - 3, look.Faint, wrap: true));
            }
        }

        return panel;
    }

    private sealed record Looks(double Size, IBrush Text, IBrush Faint, IBrush Gold);

    // The piece's own words colour and size; the gold and faint lines follow from them.
    private static Looks Look(OverlayElement e) {
        var size = Math.Clamp(e.Size > 0 ? e.Size : 13, 8, 40);
        var text = OverlayRenderer.Brush(e.Fill) ?? Brushes.White;
        var faint = OverlayRenderer.Parse(e.Fill.Color1) is { } c ? new SolidColorBrush(c, 0.7) : new SolidColorBrush(Colors.White, 0.7);

        return new Looks(size, text, faint, new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x4A)));
    }

    private static string? Waiting(OverlayUltimusData.Info? info, long key) => info is null
        ? OverlayHost.Link.Connected
            ? OverlayHost.Link.SendsUltimus ? key == OverlayUltimusData.TEAM_KEY ? $"Looking for the Quest Team's {OverlayUltimusData.Who(key)} wizard..." : "Looking for Ultimus..."
                : "This server's Imlight is too old to watch Ultimus. The host needs to update it."
            : "Not linked to a server yet. Start the game with Play in Ahziael."
        : info.Note.Length > 0 ? info.Note
        : info.Stale ? key == OverlayUltimusData.TEAM_KEY ? "The Quest Team stopped answering for now..." : "Ultimus stopped answering for now..." : null;

    private static string Pips(OverlayUltimusData.Battle fight) {
        var words = $"Pips: {fight.Pips}";
        if (fight.Power > 0) {
            words += $" + {fight.Power} power";
        }

        if (fight.Shadow > 0) {
            words += $" + {fight.Shadow} shadow";
        }

        return words;
    }

    private static Control CardChip(OverlayUltimusData.Card card, Looks look) {
        var school = SchoolColor(card.School);
        return new Border {
            Background = new SolidColorBrush(card.Picked ? Color.FromArgb(0xE0, 0x5A, 0x46, 0x10) : Color.FromArgb(0xB0, 0x14, 0x18, 0x2A)),
            BorderBrush = new SolidColorBrush(card.Picked ? Color.FromRgb(0xFF, 0xD2, 0x4A) : school),
            BorderThickness = new Thickness(card.Picked ? 2 : 1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(5, 2),
            Margin = new Thickness(0, 0, 4, 4),
            Opacity = card.Castable || card.Picked ? 1 : 0.45,
            Child = new TextBlock {
                Text = (card.Picked ? "▶ " : "") + card.Name + (card.Rank > 0 ? $" ({card.Rank})" : ""),
                FontSize = Math.Max(8, look.Size - 2), FontWeight = card.Picked ? FontWeight.Bold : FontWeight.Normal, Foreground = look.Text,
            },
        };
    }

    private static Color SchoolColor(string school) => school.ToLowerInvariant() switch {
        "fire" => Color.FromRgb(0xFF, 0x6A, 0x3A),
        "ice" => Color.FromRgb(0x7A, 0xD0, 0xFF),
        "storm" => Color.FromRgb(0xB0, 0x7A, 0xFF),
        "myth" => Color.FromRgb(0xFF, 0xD8, 0x4A),
        "life" => Color.FromRgb(0x6A, 0xE0, 0x6A),
        "death" => Color.FromRgb(0xB8, 0xB8, 0xB8),
        "balance" => Color.FromRgb(0xE0, 0x9A, 0x50),
        _ => Color.FromRgb(0x90, 0x90, 0xA0),
    };

    private static Control Bar(int health, int most, Looks look) {
        var share = Math.Clamp((double) health / Math.Max(1, most), 0, 1);
        var color = share > 0.6 ? Color.FromRgb(0x5A, 0xD0, 0x6A) : share > 0.3 ? Color.FromRgb(0xFF, 0xC8, 0x3C) : Color.FromRgb(0xFF, 0x50, 0x50);
        var grid = new Grid { Height = Math.Max(10, look.Size), Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Max(0.001, share), GridUnitType.Star)));
        grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Max(0.001, 1 - share), GridUnitType.Star)));
        var back = new Border { Background = new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), CornerRadius = new CornerRadius(4) };
        Grid.SetColumnSpan(back, 2);
        grid.Children.Add(back);
        grid.Children.Add(new Border { Background = new SolidColorBrush(color), CornerRadius = new CornerRadius(4) });
        var words = new TextBlock {
            Text = $"{health:N0} / {most:N0}", FontSize = Math.Max(8, look.Size - 3), Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumnSpan(words, 2);
        grid.Children.Add(words);

        return grid;
    }

    private static TextBlock Line(string text, double size, IBrush brush, bool bold = false, bool wrap = false) => new() {
        Text = text, FontSize = Math.Max(8, size), Foreground = brush, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
    };

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

}
