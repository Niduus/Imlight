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

    internal sealed record HeldQuest(string Id, string Title, string Kind, int Level, string World, bool Here, IReadOnlyList<Step> Steps, IReadOnlyList<string> Way);

    // ---------------------------------------------------------------------
    // Tracking an Instance or the Main quest (Niduus, 2026-11-15: "show Instance and able to track it via click (Auto unless
    // changed) while also keeping Main Quest as an option (Auto if no instance or ended)")
    // ---------------------------------------------------------------------

    // Per feed: "" = Auto (a dungeon quest when one is held, else the quest played), "main" = the quest played, or the id of
    // one dungeon quest clicked. Back to Auto once no dungeon quest is held.
    private static readonly Dictionary<long, string> s_track = [];

    internal const string TRACK_MAIN = "main";

    /// <summary>What the spectator tracks for this feed now: "" Auto, "main", or a dungeon quest's id.</summary>
    internal static string TrackChoice(long key) {
        lock (s_lock) {
            return s_track.GetValueOrDefault(Norm(key), "");
        }
    }

    /// <summary>A click on the quest box's Instance or Main line: "main", a dungeon quest's id, or "" for Auto.</summary>
    internal static void Track(long key, string choice) {
        lock (s_lock) {
            key = Norm(key);
            s_track[key] = choice ?? "";
            FeedOf(key).Version++;
        }
    }

    // Spectator boxes folded down to their top line (Niduus, 2026-11-19: the overlay "taking up a bit more space than
    // last"): "<feed>|quest" or "<feed>|spells", kept on this PC.
    private static readonly HashSet<string> s_folded = LoadFolded();

    private static string FoldName(long key, string box) => (Norm(key) switch { TEAM_KEY => "team", ANYONE_KEY => "anyone", _ => "ultimus" }) + "|" + box;

    /// <summary>Whether this spectator box ("quest" or "spells") is folded down to its top line.</summary>
    internal static bool Folded(long key, string box) {
        lock (s_lock) {
            return s_folded.Contains(FoldName(key, box));
        }
    }

    /// <summary>A click on a spectator box's top line: folds it down to that line, or opens it again.</summary>
    internal static void ToggleFold(long key, string box) {
        string[] kept;
        lock (s_lock) {
            var name = FoldName(key, box);
            if (!s_folded.Remove(name)) {
                s_folded.Add(name);
            }

            FeedOf(Norm(key)).Version++;
            kept = [.. s_folded];
        }

        try {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FoldedFile)!);
            System.IO.File.WriteAllLines(FoldedFile, kept);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) {
            // Only a convenience: the box opens again next time.
        }
    }

    private static string FoldedFile => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Edition.INSTALL_FOLDER_NAME, "overlay-folded.txt");

    private static HashSet<string> LoadFolded() {
        try {
            return System.IO.File.Exists(FoldedFile)
                ? [.. System.IO.File.ReadAllLines(FoldedFile).Select(l => l.Trim()).Where(l => l.Length is > 0 and < 40)]
                : [];
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    /// <summary>The dungeon quest tracked in this one's box and map, or null for the quest it plays.</summary>
    internal static HeldQuest? Tracked(long key, Info? info) {
        if (info is null) {
            return null;
        }

        var choice = TrackChoice(key);
        if (choice == TRACK_MAIN) {
            return null;
        }

        return info.Instances.FirstOrDefault(q => q.Id == choice) ?? info.Instances.FirstOrDefault(q => !q.Id.Equals(info.Quest, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The quest whose way the server draws on the spectator's map: the page's pick, else a tracked dungeon quest.</summary>
    internal static string RouteQuest(long key) {
        if (PageRoute(key) is { Length: > 0 } page) {
            return page;
        }

        Info? info;
        lock (s_lock) {
            info = FeedOf(key).Info;
        }

        return Tracked(key, info)?.Id ?? "";
    }

    internal sealed record Card(string Name, string School, int Rank, bool Castable, bool Picked);

    internal sealed record Fighter(string Name, int Health, int Most, bool Alive);

    internal sealed record Battle(int Round, int Health, int Most, int Pips, int Power, int Shadow, IReadOnlyList<Card> Hand, string Pick, string Why,
                                  IReadOnlyList<string> Discards, IReadOnlyList<string> Casts, IReadOnlyList<Fighter> Enemies, IReadOnlyList<Fighter> Allies,
                                  string Deck);

    internal sealed record Info(string Name, int Level, int Health, int Most, string Place, string Doing, string Quest, string QuestTitle, string Kind,
                                string NextStep, IReadOnlyList<Step> Steps, IReadOnlyList<string> Way, IReadOnlyList<string> Others, Battle? Fight,
                                string Note, DateTime At, int QuestLevel = 0, string World = "", Tracker? Detail = null, string Progress = "",
                                string Estimate = "", string WorldLine = "", string SlowLine = "") {

        /// <summary>The quests held, with a dungeon quest's steps and way (the spectator can track one).</summary>
        internal IReadOnlyList<HeldQuest> Held { get; init; } = [];

        /// <summary>The dungeon (instance) quests held: here first.</summary>
        internal IEnumerable<HeldQuest> Instances => Held.Where(q => q.Kind == "instance").OrderByDescending(q => q.Here);

        internal bool Stale => DateTime.UtcNow - At > TimeSpan.FromSeconds(8);

    }

    /// <summary>
    /// Where the Quest Team Spectator's map and quest way are kept (Niduus, 2026-11-15: "They should be their own separate
    /// plugins ... only to spectate their designation"): a feed of its own beside Ultimus's, so both add-ons can be on the
    /// overlay at once without touching each other.
    /// </summary>
    internal const long TEAM_KEY = long.MinValue + 78;

    /// <summary>Whether a map key is one of the spectators' (Ultimus's or the Quest Team's), not a game window.</summary>
    internal static bool IsSpectator(long key) => key is KEY or TEAM_KEY or ANYONE_KEY;

    /// <summary>The feed a spectator piece shows: the Spectate Anyone add-on's, the Quest Team Spectator's own, or Ultimus's.</summary>
    internal static long KeyOf(OverlayElement e) => e.WatchesAnyone ? ANYONE_KEY : e.WatchesTeam ? TEAM_KEY : KEY;

    /// <summary>Every spectator feed, Ultimus's first.</summary>
    internal static readonly long[] Keys = [KEY, TEAM_KEY, ANYONE_KEY];

    // A feed's own key (anything else is Ultimus's).
    private static long Norm(long key) => key is TEAM_KEY or ANYONE_KEY ? key : KEY;

    // ---------------------------------------------------------------------
    // Spectate Anyone (Niduus, 2026-11-16: "While installed and you're the local host, you can spectate anyone on the server,
    // including Bots like Ultimus, TCO7 and QT members")
    // ---------------------------------------------------------------------

    /// <summary>The Spectate Anyone add-on's feed: one wizard on the server at a time, anyone, picked from the server's list.</summary>
    internal const long ANYONE_KEY = long.MinValue + 79;

    /// <summary>How the server is asked for one wizard by Spectate Anyone ("anyone:" and its character id).</summary>
    internal const string ANYONE = "anyone:";

    /// <summary>Someone the host may watch: a player, Ultimus, a TCO7 member or a Quest Team member.</summary>
    internal sealed record Someone(string Id, string Name, string Kind, string School);

    private static IReadOnlyList<Someone> s_everyone = [];
    private static string s_anyoneId = LoadAnyone();

    private static string AnyoneFile => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Edition.INSTALL_FOLDER_NAME, "overlay-anyone-watch.txt");

    private static string LoadAnyone() {
        try {
            return System.IO.File.Exists(AnyoneFile) ? System.IO.File.ReadAllText(AnyoneFile).Trim() : "";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) {
            return "";
        }
    }

    /// <summary>Everyone in the game the server last listed (Ultimus and the TCO7 first, then the Quest Team, then players).</summary>
    internal static IReadOnlyList<Someone> Everyone {
        get {
            lock (s_lock) {
                return s_everyone;
            }
        }
    }

    /// <summary>The one Spectate Anyone watches now, or null before the server has answered.</summary>
    internal static Someone? AnyoneWatched {
        get {
            lock (s_lock) {
                return s_everyone.FirstOrDefault(p => p.Id == s_anyoneId);
            }
        }
    }

    /// <summary>
    /// Spectate Anyone's buttons: watch the one named (a name, part of a name, or a character id), the next one or the one
    /// before. Returns the name watched, or null when nobody fits.
    /// </summary>
    internal static string? WatchAnyone(string? meant) {
        var text = (meant ?? "").Trim();
        if (text.StartsWith("anyone:", StringComparison.OrdinalIgnoreCase)) {
            text = text["anyone:".Length..].Trim();
        }

        Someone? pick;
        lock (s_lock) {
            pick = s_everyone.FirstOrDefault(p => p.Id == text)
                   ?? s_everyone.FirstOrDefault(p => string.Equals(p.Name, text, StringComparison.OrdinalIgnoreCase))
                   ?? (text.Length > 0 ? s_everyone.FirstOrDefault(p => p.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase)) : null);
        }

        if (pick is null) {
            return null;
        }

        SwitchAnyone(pick);

        return pick.Name;
    }

    /// <summary>Spectate Anyone's Next (or Back) button: the next one in the server's list, round again at the end.</summary>
    internal static string? NextAnyone(int step = 1) {
        Someone? pick;
        lock (s_lock) {
            if (s_everyone.Count == 0) {
                return null;
            }

            var at = s_everyone.ToList().FindIndex(p => p.Id == s_anyoneId);
            pick = s_everyone[((at < 0 ? 0 : at + step) % s_everyone.Count + s_everyone.Count) % s_everyone.Count];
        }

        SwitchAnyone(pick);

        return pick.Name;
    }

    private static void SwitchAnyone(Someone pick) {
        lock (s_lock) {
            if (s_anyoneId == pick.Id) {
                return;
            }

            s_anyoneId = pick.Id;
            var feed = FeedOf(ANYONE_KEY);
            feed.Info = null;
            feed.Said = "";
            feed.Version++;
        }

        try {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(AnyoneFile)!);
            System.IO.File.WriteAllText(AnyoneFile, pick.Id);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) {
            // Only a convenience: the next start watches the first one listed.
        }

        OverlayMinimapData.Heard(ANYONE_KEY, new JsonObject { ["ok"] = false, ["error"] = $"Switching to {pick.Name}..." });
    }

    private static Color? ColorOfSchool(string school) => school.Trim().ToLowerInvariant() switch {
        "ice" => Color.FromArgb(0xF0, 0x6E, 0xC8, 0xFF),
        "life" => Color.FromArgb(0xF0, 0x5C, 0xD6, 0x5C),
        "death" => Color.FromArgb(0xF0, 0xA8, 0xAE, 0xB4),
        "storm" => Color.FromArgb(0xF0, 0xB4, 0x6E, 0xFF),
        "fire" => Color.FromArgb(0xF0, 0xFF, 0x7A, 0x3C),
        "myth" => Color.FromArgb(0xF0, 0xF0, 0xD0, 0x40),
        "balance" => Color.FromArgb(0xF0, 0xD8, 0xA0, 0x60),
        _ => null,
    };

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
    private static readonly Dictionary<long, Feed> s_feeds = new() { [KEY] = new Feed(), [TEAM_KEY] = new Feed(), [ANYONE_KEY] = new Feed() };

    private static Feed FeedOf(long key) => s_feeds[Norm(key)];

    /// <summary>The spectator feeds the overlay shows (with the Minimap they need): the link asks the server for each.</summary>
    internal static long[] WantedKeys {
        get {
            lock (s_lock) {
                return [.. s_feeds.Where(f => f.Value.Wanted).Select(f => f.Key)];
            }
        }
    }

    internal static bool Wanted => WantedKeys.Length > 0;

    // Ahziael's Ultimus and Quest Team pages ask for the feed while they show (Niduus, 2026-11-15: their quest's way on a
    // small map there), with the quest picked in the page's Quest Tracker, if any. Each ask lasts 15 seconds; the pages ask
    // again on every refresh.
    private static readonly Dictionary<long, (string Quest, DateTime At)> s_page = [];

    /// <summary>The page showing this feed now, with the quest picked there ("" for the one played).</summary>
    internal static void PageWants(long key, string routeQuest) {
        lock (s_lock) {
            s_page[Norm(key)] = (routeQuest ?? "", DateTime.UtcNow);
        }
    }

    /// <summary>Whether a page shows this feed now.</summary>
    internal static bool PageWanted(long key) {
        lock (s_lock) {
            return s_page.TryGetValue(key, out var asked) && DateTime.UtcNow - asked.At < TimeSpan.FromSeconds(15);
        }
    }

    /// <summary>The quest picked on the page, whose way the server draws instead ("" for the one played).</summary>
    internal static string PageRoute(long key) {
        lock (s_lock) {
            return s_page.TryGetValue(key, out var asked) && DateTime.UtcNow - asked.At < TimeSpan.FromSeconds(15) ? asked.Quest : "";
        }
    }

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
    internal static string Who(long key) => key == TEAM_KEY ? TeamRoles[Volatile.Read(ref s_teamIndex) % TeamRoles.Length]
        : key == ANYONE_KEY ? ANYONE + Volatile.Read(ref s_anyoneId) : "";

    /// <summary>The watched Quest Team member's school colour (for its map's border); null for Ultimus's feed.</summary>
    internal static Color? SchoolColor(long key) => key == TEAM_KEY ? ColorOfSchool(Who(key))
        : key == ANYONE_KEY ? ColorOfSchool(AnyoneWatched?.School ?? "") : null;

    /// <summary>A plain name of whom a feed watches, for its boxes' headings and notes.</summary>
    internal static string Watched(long key) => key == TEAM_KEY ? $"Quest Team: {MemberName(Who(key))} ({Who(key)})"
        : key == ANYONE_KEY ? AnyoneWatched is { } one ? $"Watching: {one.Name} ({(one.Kind == "Player" ? one.School : one.Kind)})" : "Spectate Anyone"
        : "Ultimus";

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
            // The designers show him as he is now in the game when the feed is coming, so the box looks exactly as it does
            // over the game (Niduus, 2026-11-15); otherwise a made-up one.
            lock (s_lock) {
                if (FeedOf(key).Info is { Stale: false, Note.Length: 0 } live) {
                    return live;
                }
            }

            return SampleOf(key);
        }

        lock (s_lock) {
            return FeedOf(key).Info;
        }
    }

    /// <summary>The server's answer to op "ultimus" for one feed.</summary>
    internal static void Heard(long key, JsonObject answer) {
        key = Norm(key);
        // The map (with the quest's way) goes where the minimap keeps maps, under the feed's own key.
        // Spectate Anyone: the server's list of everyone to pick from comes with every answer, and an answer for someone no
        // longer watched (a pick was made meanwhile) is dropped.
        if (key == ANYONE_KEY) {
            if (answer["everyone"] is JsonArray listed) {
                var everyone = listed.OfType<JsonObject>().Select(p => new Someone(Short((string?) p["id"], 30), Short((string?) p["name"], 40),
                    Short((string?) p["kind"], 20), Short((string?) p["school"], 20))).Where(p => p.Id.Length > 0).Take(500).ToList();
                lock (s_lock) {
                    s_everyone = everyone;
                }
            }

            var watching = (string?) answer["watching"] ?? "";
            lock (s_lock) {
                if (s_anyoneId.Length == 0 || s_everyone.All(p => p.Id != s_anyoneId)) {
                    s_anyoneId = watching;
                }
                else if (watching.Length > 0 && watching != s_anyoneId) {
                    return;
                }
            }
        }

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
            // No dungeon quest any more (or the one clicked ended): back to Auto, so the next dungeon is tracked by itself.
            lock (s_lock) {
                if (s_track.TryGetValue(key, out var choice) && choice.Length > 0
                    && (!info.Instances.Any() || (choice != TRACK_MAIN && info.Instances.All(q => q.Id != choice)))) {
                    s_track.Remove(key);
                }
            }

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

    /// <summary>
    /// One wizard of a status.json (Ultimus's, or a Quest Team member's) as the spectator's quest box reads it, so Ahziael's
    /// pages draw it exactly as the game does (Niduus, 2026-11-15). The same words the overlay gets, put together from what
    /// the status already carries: the member's "questTracker" and "tracker", and the status's quest, doing and progress.
    /// </summary>
    internal static Info FromStatus(JsonObject status, JsonObject member, bool team) {
        var code = (string?) status["questCode"] ?? "";
        var title = (string?) member["questNow"] ?? "";
        // A Quest Team wizard that doesn't hold the team's quest shows none.
        if (team && title.Length == 0) {
            code = "";
        }

        var rows = (member["questTracker"]?["quests"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var row = rows.FirstOrDefault(q => string.Equals((string?) q["id"], code, StringComparison.OrdinalIgnoreCase));
        var health = ((string?) member["health"] ?? "").Split('/');
        var role = (string?) member["role"] ?? "";
        var name = (string?) member["name"] ?? "";
        var sent = new JsonObject {
            ["name"] = team && role.Length > 0 ? $"{name} ({role})" : name,
            ["level"] = member["level"]?.DeepClone(),
            ["hp"] = health.Length == 2 && int.TryParse(health[0], out var hp) ? hp : 0,
            ["max"] = health.Length == 2 && int.TryParse(health[1], out var max) ? max : 0,
            ["place"] = member["zone"]?.DeepClone(),
            ["doing"] = (bool?) member["battle"] == true ? "in a battle" : (string?) status["doing"] ?? "",
            ["quest"] = code,
            ["questTitle"] = code.Length == 0 ? "" : (string?) row?["t"] ?? (title.Length > 0 ? title : (string?) status["quest"] ?? code),
            ["step"] = member["stepNow"]?.DeepClone(),
            ["steps"] = row?["steps"]?.DeepClone(),
            ["way"] = row?["way"]?.DeepClone(),
            ["kind"] = row?["k"]?.DeepClone(),
            ["questLevel"] = row?["l"]?.DeepClone(),
            // The quest's world by its full name (Niduus, 2026-11-15: the Quest Team's boxes too).
            ["world"] = (string?) member["tracker"]?["questWorld"] is { Length: > 0 } named ? named : (string?) row?["wn"] ?? "",
            // The team's stuck line and read-ahead warning only when there is one, as the game's box gets them.
            ["detail"] = !team ? member["tracker"]?.DeepClone()
                : ((string?) status["stuck"] ?? "").Length > 0 || ((string?) status["readAhead"] ?? "").Length > 0
                    ? new JsonObject { ["stuck"] = status["stuck"]?.DeepClone(), ["readAhead"] = status["readAhead"]?.DeepClone() }
                    : null,
            ["progress"] = status["progress"]?.DeepClone(),
            ["quests"] = new JsonArray([.. rows.Take(40).Select(q => (JsonNode) new JsonObject {
                ["id"] = q["id"]?.DeepClone(), ["t"] = q["t"]?.DeepClone(), ["k"] = q["k"]?.DeepClone(), ["l"] = q["l"]?.DeepClone(),
                ["w"] = q["wn"]?.DeepClone(), ["steps"] = q["steps"]?.DeepClone(), ["way"] = q["way"]?.DeepClone(), ["here"] = q["here"]?.DeepClone(),
            })]),
        };

        // Read as the overlay reads it: from the words, the same way they come over the link (numbers made here in code
        // would not read as numbers otherwise).
        var asSent = JsonNode.Parse(sent.ToJsonString()) as JsonObject ?? sent;

        return Read(asSent, team ? TEAM_KEY : KEY) with { At = DateTime.MaxValue };
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
        var held = (sent["quests"] as JsonArray ?? []).OfType<JsonObject>().Take(40).Select(q => new HeldQuest(
            Short((string?) q["id"], 120), Short((string?) q["t"], 120), Short((string?) q["k"], 10), (int) Number(q["l"]), Short((string?) q["w"], 40),
            q["here"]?.GetValueKind() == System.Text.Json.JsonValueKind.True,
            [.. (q["steps"] as JsonArray ?? []).OfType<JsonObject>().Take(6).Select(st => new Step(Short((string?) st["s"], 120), Short((string?) st["where"], 80),
                st["here"]?.GetValueKind() == System.Text.Json.JsonValueKind.True))],
            Texts(q["way"], 12, 80))).Where(q => q.Id.Length > 0).ToList();
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
            sent["progress"] is JsonObject slow ? Short((string?) slow["slowLine"], 160) : "") { Held = held };
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

    /// <summary>The made-up one the designers show for this feed.</summary>
    internal static Info SampleOf(long key) => key == TEAM_KEY ? TeamSample : key == ANYONE_KEY ? AnyoneSample : Sample;

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
        "", DateTime.MaxValue, 5, "Wizard City", null, "Quest 14/52 in Wizard City", "about 3 h 20 min to Krokotopia at his pace (about 5 min a quest)", "World 1 of 19") {
        // A dungeon quest, so the designers show the Track line (Niduus, 2026-11-15).
        Held = [new HeldQuest("sample-instance", "Cleanse the Tower", "instance", 5, "Wizard City", false,
            [new Step("Defeat Rattlebones", "Wizard City / Rattlebones's Tower", false)], ["Ravenwood", "Rattlebones's Tower"])],
    };

    // A made-up Quest Team wizard for the designers.
    private static readonly Info TeamSample = Sample with {
        Name = "Freya (Ice)", Doing = "walking to Lost Souls with the team", Fight = null, Estimate = "about 4 h to Krokotopia at their pace (about 6 min a quest)",
    };

    // A made-up wizard for Spectate Anyone's designers.
    private static readonly Info AnyoneSample = Sample with { Name = "Tulkar", Doing = "walking to Lost Souls", Fight = null };

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
        if (key == OverlayUltimusData.ANYONE_KEY) {
            // Spectate Anyone: everyone on the server, a click watches that one (bots first, then players).
            var everyone = OverlayUltimusData.Everyone;
            if (everyone.Count == 0) {
                return;
            }

            var watchedId = OverlayUltimusData.AnyoneWatched?.Id;
            var anyone = new WrapPanel { Orientation = Orientation.Horizontal };
            anyone.Children.Add(Line("Watch: ", look.Size - 2, look.Faint));
            foreach (var one in everyone.Take(30)) {
                var watched = one.Id == watchedId;
                var name = Line(one.Name, look.Size - 2, watched ? look.Gold : look.Text, bold: watched);
                name.Margin = new Thickness(0, 0, 8, 0);
                name.Tag = new MemberPick(OverlayUltimusData.ANYONE + one.Id);
                anyone.Children.Add(name);
            }

            if (everyone.Count > 30) {
                anyone.Children.Add(Line($"+{everyone.Count - 30} more (Next button)", look.Size - 2, look.Faint));
            }

            panel.Children.Add(anyone);

            return;
        }

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

        return QuestBody(e, OverlayUltimusData.For(window, key), key, names: true);
    }

    /// <summary>
    /// The quest box's insides for this one (Ultimus, or a Quest Team wizard), as the game shows it: also drawn on Ahziael's
    /// Ultimus and Quest Team pages (Niduus, 2026-11-15: "portray exactly how they do in game"), where the names under it
    /// are left out (<paramref name="names"/> false), since they are clicked on the game.
    /// </summary>
    internal static Control QuestBody(OverlayElement e, OverlayUltimusData.Info? info, long key, bool names) {
        var panel = new StackPanel { Spacing = 2 };
        var look = Look(e);
        if (Waiting(info, key) is { } waiting) {
            panel.Children.Add(Line(OverlayUltimusData.Watched(key), look.Size + 2, look.Gold, bold: true));
            panel.Children.Add(Line(waiting, look.Size - 1, look.Faint, wrap: true));
            if (names) {
                AddNames(panel, key, look);
            }

            return panel;
        }

        // On the game, a click on the top line folds the box down to two lines (Niduus, 2026-11-19: the overlay "taking up
        // a bit more space than last").
        var folded = names && OverlayUltimusData.Folded(key, BoxFold.QUEST);
        var top = Line((names ? folded ? "▸ " : "▾ " : "") + $"{info!.Name}" + (info.Level > 0 ? $"  ·  level {info.Level}" : ""), look.Size + 2, look.Gold, bold: true);
        if (names) {
            top.Tag = new BoxFold(key, BoxFold.QUEST);
        }

        panel.Children.Add(top);
        if (folded) {
            // Folded: just the quest it plays now (or the dungeon quest tracked), on one line.
            var held = OverlayUltimusData.Tracked(key, info);
            var title = held is not null ? held.Title : info.QuestTitle.Length > 0 ? info.QuestTitle : info.Quest;
            var now = info.Steps.FirstOrDefault(s => s.Here) ?? info.Steps.FirstOrDefault();
            panel.Children.Add(Line(title.Length > 0 ? title : "No quest picked yet.", look.Size - 1, look.Text));
            if (held is null && now is not null) {
                panel.Children.Add(Line("• " + now.Text, look.Size - 2, look.Faint));
            }

            return panel;
        }

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
        if (names) {
            AddNames(panel, key, look);
        }

        panel.Children.Add(new Border { Height = 4 });
        // A dungeon quest held: a Track line to pick it or the quest played; Auto tracks the dungeon (Niduus, 2026-11-15).
        var tracked = OverlayUltimusData.Tracked(key, info);
        if (info.Instances.Any()) {
            AddTrackLine(panel, key, info, tracked, look);
        }

        if (tracked is not null) {
            AddInstance(panel, info, tracked, look);
        }
        else {
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

    /// <summary>A click on a spectator box's top line: folds the box (Box "quest" or "spells") down to it (OverlayHost.PlaceHits).</summary>
    internal sealed record BoxFold(long Key, string Box) {

        internal const string QUEST = "quest";
        internal const string SPELLS = "spells";

    }

    /// <summary>A click on the quest box's Track line: "main", a dungeon quest's id, or "" for Auto (OverlayHost.PlaceHits).</summary>
    internal sealed record TrackPick(long Key, string Choice);

    // "Track: Instance <name> · Main <name>": the one tracked lit; a click on the other tracks it.
    private static void AddTrackLine(Panel panel, long key, OverlayUltimusData.Info info, OverlayUltimusData.HeldQuest? tracked, Looks look) {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        var auto = OverlayUltimusData.TrackChoice(key).Length == 0;
        row.Children.Add(Line(auto ? "Track (auto): " : "Track: ", look.Size - 2, look.Faint));
        var instanceColor = OverlayRenderer.Parse(new OverlayQuestList().InstanceColor) is { } c ? new SolidColorBrush(c) : look.Gold;
        foreach (var dungeon in info.Instances.Take(2)) {
            var on = tracked?.Id == dungeon.Id;
            var chip = Line((on ? "◉ " : "○ ") + "Instance: " + dungeon.Title, look.Size - 2, on ? instanceColor : look.Text, bold: on);
            chip.Margin = new Thickness(0, 0, 10, 0);
            chip.Tag = new TrackPick(key, dungeon.Id);
            row.Children.Add(chip);
        }

        var main = tracked is null;
        var mainChip = Line((main ? "◉ " : "○ ") + "Main: " + (info.QuestTitle.Length > 0 ? info.QuestTitle : info.Quest.Length > 0 ? info.Quest : "none"),
            look.Size - 2, main ? look.Gold : look.Text, bold: main);
        mainChip.Tag = new TrackPick(key, OverlayUltimusData.TRACK_MAIN);
        row.Children.Add(mainChip);
        panel.Children.Add(row);
    }

    // The tracked dungeon quest in full, then the quest played in one line.
    private static void AddInstance(Panel panel, OverlayUltimusData.Info info, OverlayUltimusData.HeldQuest dungeon, Looks look) {
        var color = OverlayRenderer.Parse(new OverlayQuestList().InstanceColor) is { } c ? new SolidColorBrush(c) : look.Gold;
        panel.Children.Add(Line(dungeon.Title, look.Size + 1, color, bold: true, wrap: true));
        var about = string.Join("  ·  ", new[] { "Dungeon quest", dungeon.Level > 0 ? $"level {dungeon.Level}" : "", dungeon.World }.Where(t => t.Length > 0));
        panel.Children.Add(Line(about, look.Size - 2, look.Faint));
        foreach (var step in dungeon.Steps.Take(6)) {
            var where = step.Where.Length > 0 && !step.Here ? "  (" + step.Where + ")" : "";
            panel.Children.Add(Line("• " + step.Text + where, look.Size - 1, look.Text, wrap: true));
        }

        if (dungeon.Steps.Count == 0) {
            panel.Children.Add(Line("No step open here right now.", look.Size - 2, look.Faint));
        }

        if (dungeon.Way.Count > 0) {
            panel.Children.Add(Line("Way: " + string.Join(" > ", dungeon.Way), look.Size - 2, look.Faint, wrap: true));
        }

        if (info.Quest.Length > 0) {
            panel.Children.Add(new Border { Height = 3 });
            panel.Children.Add(Line("Main: " + (info.QuestTitle.Length > 0 ? info.QuestTitle : info.Quest), look.Size - 2, look.Gold, wrap: true));
            if (info.Steps.FirstOrDefault() is { } first) {
                panel.Children.Add(Line("• " + first.Text, look.Size - 3, look.Faint, wrap: true));
            }
        }
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
        // The designers keep their made-up battle while the one watched isn't fighting, so the box can still be placed.
        if (window == 0 && info?.Fight is null) {
            info = OverlayUltimusData.SampleOf(OverlayUltimusData.KeyOf(e));
        }

        if (info is null || info.Stale || info.Note.Length > 0 || info.Fight is not { } fight) {
            return null;
        }

        var key = OverlayUltimusData.KeyOf(e);
        var folded = OverlayUltimusData.Folded(key, BoxFold.SPELLS);
        var top = Line((folded ? "▸ " : "▾ ") + $"{info.Name} in battle" + (fight.Round > 0 ? $"  ·  round {fight.Round}" : ""), look.Size + 2, look.Gold, bold: true);
        top.Tag = new BoxFold(key, BoxFold.SPELLS);
        panel.Children.Add(top);
        if (folded) {
            // Folded: pips and what is cast, nothing more.
            panel.Children.Add(Line(Pips(fight) + (fight.Pick.Length > 0 ? "  ·  " + (fight.Pick == "passes" ? "Passes" : "Casts " + fight.Pick) : ""),
                look.Size - 1, look.Text));

            return panel;
        }

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
            ? OverlayHost.Link.SendsUltimus ? key == OverlayUltimusData.TEAM_KEY ? $"Looking for the Quest Team's {OverlayUltimusData.Who(key)} wizard..."
                : key == OverlayUltimusData.ANYONE_KEY ? "Looking for everyone in the game..." : "Looking for Ultimus..."
                : "This server's Imlight is too old to watch Ultimus. The host needs to update it."
            : OverlayHost.Link.NotLinkedNote
        : info.Note.Length > 0 ? info.Note
        : info.Stale ? key == OverlayUltimusData.TEAM_KEY ? "The Quest Team stopped answering for now..."
            : key == OverlayUltimusData.ANYONE_KEY ? "The server stopped answering for now..." : "Ultimus stopped answering for now..." : null;

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
