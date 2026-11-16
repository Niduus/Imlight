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
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// How far Ultimus or the Quest Team is in the story of the world they are in (Niduus, 2026-11-15: "for Quests with
/// QT/Ultimus, have them show (CURR/END) of the quests. This way, I can see what quest there are on out of whatever to go
/// to the next world. Maybe a time estimation too"). END is the story quests of the world that lead to the next world's
/// first story quest, and the world's own story to its last quest (an "any one of these" choice, such as Wizard City's
/// school quests, counts once); CURR is the one
/// they are on (done + 1). The time is their own pace: the gaps between their last quests done, kept in pace.json in
/// their logs folder (Ultimus's journal fills it the first time). Read only: nothing here changes what the bots do.
/// </summary>
internal static class BotProgress {

    /// <summary>Ultimus's and the Quest Team's pace books.</summary>
    internal const string ULTIMUS = "ultimus", TEAM = "questteam";

    private const int MAX_TIMES = 40;
    private const int GAPS_USED = 10;
    // A gap longer than this was a break (the server off, a long stuck): not counted in the pace.
    private static readonly TimeSpan s_longestGap = TimeSpan.FromMinutes(90);

    internal sealed record Count(string World, int Done, int Total, string Next, double MinutesLeft) {
        internal int Now => Math.Min(Total, Done + 1);
    }

    // The world codes of quest names, for a quest whose giver's area is not known.
    private static readonly Dictionary<string, string> s_codes = new(StringComparer.OrdinalIgnoreCase) {
        ["WC"] = "WizardCity", ["KT"] = "Krokotopia", ["MB"] = "Marleybone", ["MS"] = "MooShu", ["DS"] = "DragonSpire", ["CL"] = "Celestia",
        ["ZF"] = "Zafaria", ["AV"] = "Avalon", ["AZ"] = "Azteca", ["KR"] = "Khrysalis", ["KH"] = "Khrysalis", ["PL"] = "Polaris", ["MR"] = "Mirage",
        ["EM"] = "Empyrea", ["KM"] = "Karamelle", ["LM"] = "Lemuria", ["NV"] = "Novus", ["WL"] = "Wallaru", ["DM"] = "Darkmoor",
    };

    private static readonly Lock s_lock = new();
    private static Dictionary<string, List<HashSet<string>>> s_plans;
    private static Dictionary<string, string> s_worldOf;
    private static int s_version = -1;

    /// <summary>The story world (zone folder of the ladder, as "Marleybone") a quest belongs to, or "".</summary>
    internal static string WorldOf(QuestTemplate quest) {
        if (quest is null) {
            return "";
        }

        var ladder = MainWorlds.Folders;
        string InLadder(string zone) {
            var folder = (zone ?? "").Split('/')[0].Trim();
            return ladder.FirstOrDefault(w => string.Equals(w, folder, StringComparison.OrdinalIgnoreCase)) ?? "";
        }

        if (InLadder(QuestGuide.GiverZone(quest)) is { Length: > 0 } giver) {
            return giver;
        }

        if (s_codes.TryGetValue(QuestStory.WorldOf(quest), out var coded)) {
            return coded;
        }

        return InLadder((quest.m_goals ?? []).Where(g => g is not null).Select(g => (string) g.m_destinationZone).FirstOrDefault(z => !string.IsNullOrWhiteSpace(z)));
    }

    /// <summary>A world's name as players say it ("Wizard City").</summary>
    internal static string Shown(string world) => MainWorlds.Number(world) > 0 ? MainWorlds.Name(world) : OverlayQuestTracker.WorldName(world);

    /// <summary>
    /// Where <paramref name="wizard"/> is in its world's story: the world of the quest it plays (when a story quest), else
    /// the world it stands in, else the first world of the ladder it has not finished. Null when nothing is known.
    /// </summary>
    internal static Count Of(Wizard wizard, string quest) {
        if (wizard is null) {
            return null;
        }

        try {
            var (plans, worldOf) = Plans();
            var ladder = MainWorlds.Folders;
            bool Has(string world) => plans.TryGetValue(world, out var groups) && groups.Count > 0;
            int DoneIn(string world) => plans[world].Count(g => g.Any(wizard.HasCompletedQuest));

            var world = !string.IsNullOrEmpty(quest) && worldOf.TryGetValue(quest, out var questWorld) ? questWorld : "";
            if (!Has(world)) {
                var standing = (wizard.Zone ?? "").Split('/')[0];
                world = ladder.FirstOrDefault(w => string.Equals(w, standing, StringComparison.OrdinalIgnoreCase)) ?? "";
            }

            if (!Has(world)) {
                world = ladder.FirstOrDefault(w => Has(w) && DoneIn(w) < plans[w].Count) ?? "";
            }

            if (!Has(world)) {
                return null;
            }

            var at = ladder.ToList().FindIndex(w => string.Equals(w, world, StringComparison.OrdinalIgnoreCase));
            var next = ladder.Skip(at + 1).FirstOrDefault(Has) ?? "";

            // The story goes on past the quests the game's data marks as the world's story: Marleybone's Big Ben
            // ("Metal Madness", "Stolen Away" after Katz's lab) is not marked as the main story, so it showed "Marleybone
            // story: 32/32 done, on to MooShu" while Ultimus was still in Big Ben (Niduus, 2026-11-15: "inaccurate").
            // A quest of this world that the bot plays and the count lacks belongs to the world's story: it is learned
            // (kept in story-extras.json) and counted from then on, done or not.
            Learn(world, quest, wizard, plans[world]);
            var extras = ExtrasOf(world);
            var done = DoneIn(world) + extras.Count(wizard.HasCompletedQuest);

            return new Count(world, done, plans[world].Count + extras.Count, next, 0);
        }
        catch (Exception ex) {
            Logger.Debug("Bots: story progress could not be worked out: {0}", Logger.Args(ex.Message));
            return null;
        }
    }

    // Quests learned to be part of a world's story past the ones the game's data marks: world -> quest codes.
    private static Dictionary<string, HashSet<string>> s_extras;

    private static string ExtrasFile => Path.Combine(Ultimus.UltimusWire.SharedFolder, "story-extras.json");

    private static HashSet<string> ExtrasOf(string world) {
        lock (s_lock) {
            LoadExtras();
            return s_extras.TryGetValue(world, out var set) ? [.. set] : [];
        }
    }

    private static void LoadExtras() {
        if (s_extras is not null) {
            return;
        }

        s_extras = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        try {
            if (File.Exists(ExtrasFile)
                && JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(ExtrasFile)) is { } saved) {
                foreach (var (w, codes) in saved) {
                    s_extras[w] = new HashSet<string>(codes ?? [], StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception ex) {
            Logger.Debug("Bots: the learned story quests could not be read: {0}", Logger.Args(ex.Message));
        }
    }

    // The bot is on a quest of this world, not done, that no counted group holds: it is the world's story too.
    private static void Learn(string world, string quest, Wizard wizard, List<HashSet<string>> plan) {
        if (string.IsNullOrEmpty(quest) || wizard.HasCompletedQuest(quest) || plan.Any(g => g.Contains(quest))
            || QuestTemplateCollection.GetQuestByName(quest) is not { } template
            || !string.Equals(WorldOf(template), world, StringComparison.OrdinalIgnoreCase)
            || DungeonQuestIndex.IsDungeonQuest(quest)) {
            return;
        }

        lock (s_lock) {
            LoadExtras();
            if (!s_extras.TryGetValue(world, out var set)) {
                s_extras[world] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            if (!set.Add(quest)) {
                return;
            }

            Logger.Information("Bots: {0} ({1}) is part of {2}'s story past the quests the game marks; it is counted from now on.",
                Logger.Args(Tools.OverlayQuestTracker.English(template.m_questTitle, quest), quest, world));
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(ExtrasFile)!);
                File.WriteAllText(ExtrasFile, JsonSerializer.Serialize(s_extras.ToDictionary(p => p.Key, p => p.Value.Order().ToList())));
            }
            catch (Exception ex) {
                Logger.Debug("Bots: the learned story quests could not be written: {0}", Logger.Args(ex.Message));
            }
        }
    }

    /// <summary>
    /// The progress line for the Spectators and Ahziael, and the pace's time left. <paramref name="book"/>: whose pace
    /// (<see cref="ULTIMUS"/> or <see cref="TEAM"/>); <paramref name="note"/>: true for the bot's own status (every few
    /// seconds), which writes a quest done into the pace book.
    /// </summary>
    internal static JsonObject Json(string book, Wizard wizard, string quest, bool note) {
        if (Of(wizard, quest) is not { } count) {
            return null;
        }

        if (note) {
            Noted(book, count);
        }

        var left = Math.Max(0, count.Total - count.Done);
        var gap = Pace(book);
        var minutes = gap is { } each ? left * each : 0;
        var name = Shown(count.World);
        var to = count.Next.Length > 0 ? Shown(count.Next) : "";
        string estimate;
        if (left == 0) {
            estimate = to.Length > 0 ? $"story of {name} done, on to {to}" : $"story of {name} done";
        }
        else if (gap is null) {
            estimate = "time to the next world shows after a few more quests";
        }
        else {
            estimate = $"about {Duration(minutes)} to {(to.Length > 0 ? to : "the end of " + name)} at {Pronoun(book)} pace (about {Duration(gap.Value)} a quest)";
        }

        var line = left == 0 ? $"{name} story: {count.Done}/{count.Total} done" : $"Quest {count.Now}/{count.Total} in {name}";
        // Its place in the main worlds (Niduus, 2026-11-15: "World X of 19").
        var number = MainWorlds.Number(count.World);
        var worlds = number > 0 ? $"World {number} of {MainWorlds.All.Length}" : "";
        // The quest now taking much longer than their usual pace (twice a quest's time, 20 min at least): shown in red.
        var since = SinceLast(book);
        var slow = gap is { } usual && since is { } now && now > Math.Max(20, usual * 2);
        var slowLine = slow ? $"this quest is taking longer than usual ({Duration(since!.Value)} so far, about {Duration(gap!.Value)} is usual)" : "";

        return new JsonObject {
            ["world"] = name,
            ["done"] = count.Done,
            ["now"] = count.Now,
            ["total"] = count.Total,
            ["next"] = to,
            ["minutesLeft"] = Math.Round(minutes, 1),
            ["minutesEach"] = gap is null ? 0 : Math.Round(gap.Value, 1),
            ["estimate"] = estimate,
            ["line"] = line,
            ["worldNumber"] = number,
            ["worlds"] = MainWorlds.All.Length,
            ["worldLine"] = worlds,
            ["slow"] = slow,
            ["slowLine"] = slowLine,
        };
    }

    private static string Pronoun(string book) => book == ULTIMUS ? "his" : "their";

    /// <summary>"45 min", "2 h 10 min", "1 day 3 h".</summary>
    internal static string Duration(double minutes) {
        var span = TimeSpan.FromMinutes(Math.Max(1, minutes));
        return span.TotalDays >= 1 ? $"{(int) span.TotalDays} day{((int) span.TotalDays == 1 ? "" : "s")} {span.Hours} h"
            : span.TotalHours >= 1 ? $"{(int) span.TotalHours} h {span.Minutes} min"
            : $"{Math.Max(1, (int) Math.Round(span.TotalMinutes))} min";
    }

    // Each world's story to the next world, as groups (a choice is one group of its options), and every story quest's world.
    private static (Dictionary<string, List<HashSet<string>>> Plans, Dictionary<string, string> WorldOf) Plans() {
        var version = Quests.QuestOptions.Version;
        lock (s_lock) {
            if (s_plans is not null && s_version == version) {
                return (s_plans, s_worldOf);
            }
        }

        var worldOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in QuestStory.Story()) {
            if (QuestTemplateCollection.GetQuestByName(name) is { } quest && WorldOf(quest) is { Length: > 0 } world) {
                worldOf[name] = world;
            }
        }

        var plans = new Dictionary<string, List<HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
        var ladder = MainWorlds.Folders;
        var withStory = ladder.Where(w => worldOf.Values.Contains(w, StringComparer.OrdinalIgnoreCase)).ToList();
        for (var i = 0; i < withStory.Count; i++) {
            plans[withStory[i]] = PlanOf(withStory[i], i + 1 < withStory.Count ? withStory[i + 1] : "", worldOf);
        }

        Logger.Debug("Bots: story progress worked out for {0} worlds.", Logger.Args(plans.Count));
        lock (s_lock) {
            (s_plans, s_worldOf, s_version) = (plans, worldOf, version);
        }

        return (plans, worldOf);
    }

    private static List<HashSet<string>> PlanOf(string world, string next, Dictionary<string, string> storyWorld) {
        string WorldOfName(string name) => storyWorld.TryGetValue(name, out var w) ? w
            : QuestTemplateCollection.GetQuestByName(name) is { } q ? WorldOf(q) : "";
        bool Is(string name, string w) => string.Equals(WorldOfName(name), w, StringComparison.OrdinalIgnoreCase);

        var inWorld = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (next.Length > 0) {
            // The next world's first story quests (none of its own story before them), and back from them: every quest of
            // this world they need, at any depth (side quests the story needs too).
            var nextStory = storyWorld.Where(p => string.Equals(p.Value, next, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToList();
            var gates = nextStory.Where(n => QuestTemplateCollection.GetQuestByName(n) is { } q
                && !QuestStory.ChainQuests(q.m_requirements).Any(need => Is(need, next))).ToList();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var waiting = new Stack<string>(gates);
            while (waiting.Count > 0) {
                var name = waiting.Pop();
                if (!seen.Add(name) || QuestTemplateCollection.GetQuestByName(name) is not { } quest) {
                    continue;
                }

                var here = Is(name, world);
                if (here) {
                    inWorld.Add(name);
                }
                else if (!Is(name, next)) {
                    continue;
                }

                foreach (var need in QuestStory.ChainQuests(quest.m_requirements)) {
                    waiting.Push(need);
                }
            }
        }

        // And the world's own story to its end: the deepest story quest of the world (counting only the world's own quests,
        // as the quest tracker's story does) and every quest of the world it needs first. The next world can open before
        // the world's story is over (Niduus, 2026-11-15: Ultimus showed "Quest 32/32 in Marleybone" in Katz's lab, "the
        // game mistakenly marked that as the finale. The end quest is the Big Ben one").
        if (next.Length > 0) {
            inWorld.UnionWith(WorldEndChain(world, storyWorld, Is).Chain);
        }

        // The last world (or one whose next world needs none of it): its whole story.
        if (inWorld.Count == 0) {
            inWorld.UnionWith(storyWorld.Where(p => string.Equals(p.Value, world, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key));
        }

        // "Any one of these" choices among them: one group.
        var parent = inWorld.ToDictionary(n => n, n => n, StringComparer.OrdinalIgnoreCase);
        string Root(string n) {
            while (!string.Equals(parent[n], n, StringComparison.OrdinalIgnoreCase)) {
                n = parent[n] = parent[parent[n]];
            }

            return n;
        }

        var askers = inWorld.Concat(storyWorld.Where(p => string.Equals(p.Value, next, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key));
        foreach (var asker in askers) {
            if (QuestTemplateCollection.GetQuestByName(asker) is not { } quest) {
                continue;
            }

            foreach (var options in Choices(quest.m_requirements)) {
                var inside = options.Where(inWorld.Contains).ToList();
                for (var i = 1; i < inside.Count; i++) {
                    parent[Root(inside[i])] = Root(inside[0]);
                }
            }
        }

        return [.. inWorld.GroupBy(Root, StringComparer.OrdinalIgnoreCase).Select(g => new HashSet<string>(g, StringComparer.OrdinalIgnoreCase))];
    }

    // The world's last story quest (deepest by its own world's quests; between equals, the latest code) and every quest of
    // that world it needs first, at any depth.
    private static (string End, HashSet<string> Chain) WorldEndChain(string world, Dictionary<string, string> storyWorld, Func<string, string, bool> isIn) {
        var mine = storyWorld.Where(p => string.Equals(p.Value, world, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToList();
        var chain = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (mine.Count == 0) {
            return ("", chain);
        }

        var depth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int Depth(string name) {
            if (depth.TryGetValue(name, out var known)) {
                return known;
            }

            if (!visiting.Add(name) || QuestTemplateCollection.GetQuestByName(name) is not { } quest) {
                return 0;
            }

            var deepest = QuestStory.ChainQuests(quest.m_requirements).Where(need => isIn(need, world)).Select(Depth).DefaultIfEmpty(0).Max();
            visiting.Remove(name);
            depth[name] = deepest + 1;

            return deepest + 1;
        }

        var end = mine.OrderByDescending(Depth).ThenByDescending(n => n, StringComparer.OrdinalIgnoreCase).First();
        var waiting = new Stack<string>([end]);
        while (waiting.Count > 0) {
            var name = waiting.Pop();
            if (!isIn(name, world) || !chain.Add(name) || QuestTemplateCollection.GetQuestByName(name) is not { } quest) {
                continue;
            }

            foreach (var need in QuestStory.ChainQuests(quest.m_requirements)) {
                waiting.Push(need);
            }
        }

        return (end, chain);
    }

    /// <summary>
    /// Each main world's story as the progress count uses it (the quests to the next world and to the world's own last
    /// quest, a choice as one group), the world's last story quest, and every story quest's world. For the Quest Tree.
    /// </summary>
    internal static (IReadOnlyDictionary<string, List<HashSet<string>>> Plans, IReadOnlyDictionary<string, string> Ends, IReadOnlyDictionary<string, string> WorldOf) Tree() {
        var (plans, worldOf) = Plans();
        var ends = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string WorldOfName(string name) => worldOf.TryGetValue(name, out var w) ? w : QuestTemplateCollection.GetQuestByName(name) is { } q ? WorldOf(q) : "";
        foreach (var world in plans.Keys) {
            ends[world] = WorldEndChain(world, worldOf, (n, w) => string.Equals(WorldOfName(n), w, StringComparison.OrdinalIgnoreCase)).End;
        }

        return (plans, ends, worldOf);
    }

    // The quests of each "any one of these" list in a requirement (each option's quests, at any depth).
    private static IEnumerable<List<string>> Choices(Requirement requirement) {
        if (requirement is not RequirementList { m_applyNOT: false } list) {
            yield break;
        }

        var inners = list.m_requirements ?? [];
        if (inners.Take(inners.Count - 1).Any(r => r?.m_operator == Operator.ROP_OR)) {
            yield return [.. inners.SelectMany(QuestStory.ChainQuests)];
        }

        foreach (var inner in inners) {
            foreach (var deeper in Choices(inner)) {
                yield return deeper;
            }
        }
    }

    // The pace books: when each quest of the story was seen done, per book.
    private static readonly Dictionary<string, List<DateTime>> s_times = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (string World, int Done)> s_last = new(StringComparer.Ordinal);

    // Ultimus's book is kept per seat: each TCO7 member has its own pace (UltimusSeat).
    private static string BookKey(string book) => book == ULTIMUS ? ULTIMUS + "|" + Ultimus.UltimusSeat.Current.Key : book;

    private static string FileOf(string book) => Path.Combine(book == ULTIMUS ? Ultimus.UltimusWire.Folder : QuestTeam.QuestTeamWire.Folder, "pace.json");

    private static List<DateTime> TimesOf(string book) {
        if (s_times.TryGetValue(BookKey(book), out var times)) {
            return times;
        }

        times = [];
        try {
            var path = FileOf(book);
            if (File.Exists(path) && JsonSerializer.Deserialize<List<DateTime>>(File.ReadAllText(path)) is { } read) {
                times = [.. read.Order()];
            }
            else if (book == ULTIMUS) {
                // The first time: Ultimus's own journal of quests done.
                times = [.. Ultimus.UltimusJournal.Recent(400).Where(e => e.Kind == Ultimus.UltimusJournal.QUEST).Select(e => e.At).Order().TakeLast(MAX_TIMES)];
            }
        }
        catch (Exception ex) {
            Logger.Debug("Bots: the pace book could not be read: {0}", Logger.Args(ex.Message));
        }

        return s_times[BookKey(book)] = times;
    }

    // A quest done since the last look (the same world, more done): its time goes in the book.
    private static void Noted(string book, Count count) {
        lock (s_lock) {
            var had = s_last.TryGetValue(BookKey(book), out var last);
            s_last[BookKey(book)] = (count.World, count.Done);
            if (!had || !string.Equals(last.World, count.World, StringComparison.OrdinalIgnoreCase) || count.Done <= last.Done) {
                return;
            }

            var times = TimesOf(book);
            // A jump of many at once (a fast-forward, a restart) is one moment, not many quests.
            times.Add(DateTime.UtcNow);
            while (times.Count > MAX_TIMES) {
                times.RemoveAt(0);
            }

            try {
                var path = FileOf(book);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(times));
            }
            catch (Exception ex) {
                Logger.Debug("Bots: the pace book could not be written: {0}", Logger.Args(ex.Message));
            }
        }
    }

    private static readonly DateTime s_started = DateTime.UtcNow;

    /// <summary>Minutes since their last quest done, or null when none is in the book.</summary>
    internal static double? SinceLast(string book) {
        lock (s_lock) {
            var times = TimesOf(book);
            // Time the server was off does not count: from this start at the latest.
            return times.Count == 0 ? null : (DateTime.UtcNow - (times[^1] > s_started ? times[^1] : s_started)).TotalMinutes;
        }
    }

    /// <summary>Minutes a quest takes them lately (the last gaps, breaks left out), or null with fewer than 3 gaps.</summary>
    internal static double? Pace(string book) {
        lock (s_lock) {
            var times = TimesOf(book);
            var gaps = new List<double>();
            for (var i = times.Count - 1; i > 0 && gaps.Count < GAPS_USED; i--) {
                var gap = times[i] - times[i - 1];
                if (gap > TimeSpan.Zero && gap <= s_longestGap && DateTime.UtcNow - times[i] < TimeSpan.FromDays(14)) {
                    gaps.Add(gap.TotalMinutes);
                }
            }

            return gaps.Count < 3 ? null : gaps.Average();
        }
    }

}
