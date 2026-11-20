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
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Imlight.Setup;

/// <summary>
/// The Hive mind (Niduus, 2026-11-19: "while the server is loading, it collects unique data for the hive mind to present to
/// the player. This data is bullet proof and tells the user how the bots complete each task"). It reads what every bot
/// wrote down (Ultimus, the TCO7, the Quest Team) and keeps only what is certain:
/// <list type="bullet">
/// <item>a quest counts only when a bot finished it start to finish by itself: no step finished by Ahziael, no step
/// flagged broken, not picked up partway or after a restart;</item>
/// <item>a stuck fix counts only when it worked every time it was tried;</item>
/// <item>a fight style counts only after 5 fights of that kind (the bots' own rule).</item>
/// </list>
/// Everything left out is counted, so the page can say how much was left out and why.
/// </summary>
internal static class HiveMind {

    internal const string FOLDER = "hive-mind";
    internal const string BOOK_FILE = "hive-mind.json";

    /// <summary>How many fights of one kind before a style is shown (the same rule the bots use, fight-styles.txt).</summary>
    internal const int FIGHTS_TO_COUNT = 5;

    internal sealed class Book {
        public DateTime Made { get; set; }
        public List<string> Bots { get; set; } = [];
        public List<Quest> Quests { get; set; } = [];
        public List<Fight> Fights { get; set; } = [];
        public int RunsRead { get; set; }
        public int LeftOutSkipped { get; set; }
        public int LeftOutBroken { get; set; }
        public int LeftOutPartway { get; set; }
        public int LeftOutUnfinished { get; set; }
        public int FixesLeftOut { get; set; }
    }

    internal sealed class Quest {
        public string Code { get; set; } = "";
        public string Title { get; set; } = "";
        public string World { get; set; } = "";
        /// <summary>When a bot first finished it: the quests show in the order the bots played them, which is the story's.</summary>
        public DateTime First { get; set; } = DateTime.MaxValue;
        public List<Done> Done { get; set; } = [];
        public List<Fix> Fixes { get; set; } = [];
        /// <summary>What failed on this quest, so the bots know what to avoid (Niduus, 2026-11-20: "I don't want the Hive Mind to
        /// only mark success, I want it to also note failures, so they know what to avoid").</summary>
        public List<Miss> Misses { get; set; } = [];
        public List<Trouble> Troubles { get; set; } = [];
    }

    /// <summary>A stuck move that failed at a quest's step at least once (with how often it worked there too).</summary>
    internal sealed class Miss {
        public string Step { get; set; } = "";
        public string Place { get; set; } = "";
        public string Move { get; set; } = "";
        public int Worked { get; set; }
        public int Failed { get; set; }
    }

    /// <summary>A problem a bot reported on a quest (lost a fight again and again, got stuck, took far too long), and who.</summary>
    internal sealed class Trouble {
        public string Text { get; set; } = "";
        public int Times { get; set; }
        public List<string> Bots { get; set; } = [];
    }

    /// <summary>One bot's clean finishes of one quest.</summary>
    internal sealed class Done {
        public string Bot { get; set; } = "";
        public int Times { get; set; }
        public double BestSeconds { get; set; }
        public int Steps { get; set; }
        public int FightsWon { get; set; }
        public int Troubles { get; set; }
        public DateTime Last { get; set; }
    }

    /// <summary>A move that got a bot unstuck at a quest's step, every time it was tried.</summary>
    internal sealed class Fix {
        public string Step { get; set; } = "";
        public string Place { get; set; } = "";
        public string Move { get; set; } = "";
        public int Worked { get; set; }
        public string By { get; set; } = "";
    }

    internal sealed class Fight {
        public string Kind { get; set; } = "";
        public string Style { get; set; } = "";
        public int Fights { get; set; }
        public int Wins { get; set; }
        public double Rounds { get; set; }
        public double HealthLost { get; set; }
    }

    // The bots' folders inside the server's logs folder, and the name each one goes by.
    private static IEnumerable<(string Bot, string Folder)> BotFolders(string logs) {
        yield return ("Ultimus", Path.Combine(logs, "ultimus"));
        var tco7 = Path.Combine(logs, "tco7");
        if (Directory.Exists(tco7)) {
            foreach (var member in Directory.GetDirectories(tco7).OrderBy(d => d, StringComparer.OrdinalIgnoreCase)) {
                var name = Path.GetFileName(member);
                if (name.Length > 0) {
                    yield return (char.ToUpperInvariant(name[0]) + name[1..], member);
                }
            }
        }

        yield return ("Quest Team", Path.Combine(logs, "quest-team"));

        // The questing street bots (logs/street-questers/street01 and on), each by the name its wizard got (name.txt).
        var street = Path.Combine(logs, "street-questers");
        if (Directory.Exists(street)) {
            foreach (var bot in Directory.GetDirectories(street).OrderBy(d => d, StringComparer.OrdinalIgnoreCase)) {
                var name = "";
                try {
                    var file = Path.Combine(bot, "name.txt");
                    name = File.Exists(file) ? File.ReadAllText(file).Trim() : "";
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                    // The folder's own name below.
                }

                yield return ($"{(name.Length > 0 ? name : Path.GetFileName(bot))} (street bot)", bot);
            }
        }
    }

    /// <summary>Reads every bot's files in <paramref name="logs"/> (the server's logs folder) into one book.</summary>
    internal static Book Collect(string logs) {
        var book = new Book { Made = DateTime.Now };
        var quests = new Dictionary<string, Quest>(StringComparer.OrdinalIgnoreCase);
        foreach (var (bot, folder) in BotFolders(logs)) {
            var log = Path.Combine(folder, "quest-log.txt");
            if (!File.Exists(log)) {
                continue;
            }

            book.Bots.Add(bot);
            ReadQuestLog(bot, ReadLines(log), quests, book);
        }

        ReadFixes(Path.Combine(logs, "ultimus", "stuck-solutions.json"), quests, book);
        book.Fights = ReadFights(BotFolders(logs).Select(b => Path.Combine(b.Folder, "fight-styles.json")));
        book.Quests = quests.Values.Where(q => q.Done.Count > 0 || q.Misses.Count > 0 || q.Troubles.Count > 0)
            .OrderBy(q => WorldOrder(q.World)).ThenBy(q => q.First).ThenBy(q => q.Code, StringComparer.OrdinalIgnoreCase).ToList();

        return book;
    }

    private static IEnumerable<string> ReadLines(string path) {
        // Shared read: the server may be writing the file right now.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) {
            lines.Add(line);
        }

        return lines;
    }

    // 2026-11-12 19:11:00  STARTED  "Unicorn Way" (WC-COMMONS-MAIN-001), WC: 2 steps, ...
    private static readonly Regex s_line = new(@"^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)\s+(STARTED|FINISHED|SKIPPED|BROKEN|ISSUE|step)\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex s_titleCode = new(@"^(""[^""]*"") \(([^)]+)\)", RegexOptions.Compiled);
    private static readonly Regex s_title = new(@"^(""[^""]*"")", RegexOptions.Compiled);
    private static readonly Regex s_took = new(@"took (\d+) battle\(s\) won", RegexOptions.Compiled);
    private static readonly Regex s_steps = new(@"\), [^:]*: (\d+) steps", RegexOptions.Compiled);
    private static readonly Regex s_time = new(@"\) in ([\d.]+) (seconds|minutes|hours)", RegexOptions.Compiled);

    private sealed class Run {
        public string Code = "";
        public string Title = "";
        public DateTime Started;
        public int Steps;
        public int Fights;
        public int Troubles;
        public bool Tainted;
        public bool Skipped;
        public bool AfterRestart;
    }

    /// <summary>One bot's quest log: every run from STARTED to FINISHED, kept only when it is clean.</summary>
    internal static void ReadQuestLog(string bot, IEnumerable<string> lines, Dictionary<string, Quest> quests, Book book) {
        var open = new Dictionary<string, Run>(StringComparer.Ordinal);       // by title, as the step lines name it
        var restarted = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // codes started before and not finished
        foreach (var raw in lines) {
            var m = s_line.Match(raw);
            if (!m.Success) {
                continue;
            }

            var at = DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var when) ? when : DateTime.MinValue;
            var kind = m.Groups[2].Value;
            var rest = m.Groups[3].Value;
            switch (kind) {
                case "STARTED": {
                    if (s_titleCode.Match(rest) is not { Success: true } tc) {
                        break;
                    }

                    var title = tc.Groups[1].Value;
                    var code = tc.Groups[2].Value;
                    if (open.Remove(title, out var before)) {
                        // The same quest started again before it finished: the server restarted or the bot dropped it. What
                        // comes next picks up partway, so its time and steps are not the whole quest's.
                        book.LeftOutUnfinished++;
                        restarted.Add(before.Code);
                    }

                    var steps = s_steps.Match(rest) is { Success: true } sm ? int.Parse(sm.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                    open[title] = new Run { Code = code, Title = title, Started = at, Steps = steps, AfterRestart = restarted.Contains(code) };
                    break;
                }
                case "step": {
                    if (s_title.Match(rest) is { Success: true } t && open.TryGetValue(t.Groups[1].Value, out var run)) {
                        if (s_took.Match(rest) is { Success: true } took) {
                            run.Fights += int.Parse(took.Groups[1].Value, CultureInfo.InvariantCulture);
                        }
                    }

                    break;
                }
                case "SKIPPED":
                case "BROKEN": {
                    if (s_title.Match(rest) is { Success: true } t && open.TryGetValue(t.Groups[1].Value, out var run)) {
                        run.Tainted = true;
                        run.Skipped |= kind == "SKIPPED";
                    }

                    break;
                }
                case "ISSUE": {
                    if (s_title.Match(rest) is { Success: true } t && open.TryGetValue(t.Groups[1].Value, out var run)) {
                        run.Troubles++;
                        // What went wrong is kept too, so the bots know what to avoid on this quest.
                        var text = rest[t.Length..].TrimStart(':', ' ').Trim();
                        if (text.Length > 0 && run.Code.Length > 0) {
                            var key = OneForAllSchools(run.Code);
                            if (!quests.TryGetValue(key, out var quest)) {
                                quests[key] = quest = new Quest { Code = key, Title = run.Title.Trim('"').Trim(), World = WorldOf(run.Code), First = at };
                            }

                            text = text.Length > 240 ? text[..240].TrimEnd() + "..." : text;
                            var same = TroubleKey(text);
                            if (quest.Troubles.FirstOrDefault(x => TroubleKey(x.Text) == same) is not { } trouble) {
                                quest.Troubles.Add(trouble = new Trouble { Text = text });
                            }

                            trouble.Times++;
                            if (!trouble.Bots.Contains(bot)) {
                                trouble.Bots.Add(bot);
                            }
                        }
                    }

                    break;
                }
                case "FINISHED": {
                    if (s_titleCode.Match(rest) is not { Success: true } tc) {
                        break;
                    }

                    var title = tc.Groups[1].Value;
                    var code = tc.Groups[2].Value;
                    book.RunsRead++;
                    open.Remove(title, out var run);
                    restarted.Remove(code);
                    if (run is null || run.AfterRestart || rest.Contains("picked up partway", StringComparison.Ordinal)) {
                        book.LeftOutPartway++;
                        break;
                    }

                    if (run.Tainted) {
                        if (run.Skipped) {
                            book.LeftOutSkipped++;
                        }
                        else {
                            book.LeftOutBroken++;
                        }

                        break;
                    }

                    var seconds = s_time.Match(rest) is { Success: true } time ? Seconds(time.Groups[1].Value, time.Groups[2].Value) : 0;
                    if (seconds <= 0) {
                        seconds = Math.Max(1, (at - run.Started).TotalSeconds);
                    }

                    var key = OneForAllSchools(code);
                    if (!quests.TryGetValue(key, out var quest)) {
                        quests[key] = quest = new Quest { Code = key, Title = title.Trim('"').Trim(), World = WorldOf(code), First = at };
                    }

                    quest.First = at < quest.First ? at : quest.First;
                    var done = quest.Done.FirstOrDefault(d => d.Bot == bot);
                    if (done is null) {
                        quest.Done.Add(done = new Done { Bot = bot, BestSeconds = double.MaxValue });
                    }

                    done.Times++;
                    done.Last = at > done.Last ? at : done.Last;
                    if (seconds < done.BestSeconds) {
                        done.BestSeconds = seconds;
                        done.Steps = run.Steps;
                        done.FightsWon = run.Fights;
                        done.Troubles = run.Troubles;
                    }

                    break;
                }
            }
        }

        book.LeftOutUnfinished += open.Count;
    }

    private static double Seconds(string number, string unit) {
        var n = double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

        return unit switch { "hours" => n * 3600, "minutes" => n * 60, _ => n };
    }

    /// <summary>Stuck fixes that worked every time they were tried, under the quest they were for.</summary>
    private static void ReadFixes(string path, Dictionary<string, Quest> quests, Book book) {
        if (!File.Exists(path)) {
            return;
        }

        JsonArray? all;
        try {
            all = JsonNode.Parse(string.Join('\n', ReadLines(path))) as JsonArray;
        }
        catch (JsonException) {
            return;
        }

        foreach (var row in (all ?? []).OfType<JsonObject>()) {
            var code = (string?) row["Quest"] ?? "";
            var worked = (int?) row["Worked"] ?? 0;
            var failed = (int?) row["Failed"] ?? 0;
            if (code.Length == 0 || worked < 1 || failed > 0) {
                book.FixesLeftOut++;
                // A move that failed is not a sure fix, but it is worth knowing: the bots avoid it, or try it last.
                if (code.Length > 0 && failed > 0) {
                    AddMiss(quests, code, row, worked, failed);
                }

                continue;
            }

            code = OneForAllSchools(code);
            if (!quests.TryGetValue(code, out var quest)) {
                quests[code] = quest = new Quest { Code = code, Title = code, World = WorldOf(code), First = DateTime.MaxValue };
            }

            var (step, zone, move) = Where(row);
            // The same move at the same step and place, learned twice, is one line.
            if (quest.Fixes.FirstOrDefault(f => f.Step == step.Trim() && f.Place == zone && f.Move == move) is { } same) {
                same.Worked += worked;
                continue;
            }

            quest.Fixes.Add(new Fix { Step = step.Trim(), Place = zone, Move = move, Worked = worked, By = (string?) row["By"] ?? "" });
        }
    }

    // The step, area and move of a stuck-solutions row, in plain words.
    private static (string Step, string Zone, string Move) Where(JsonObject row)
        => ((((string?) row["Step"] ?? "").Split('|').LastOrDefault(s => s.Trim().Length > 0) ?? "").Trim(),
            ((string?) row["Zone"] ?? "").Split('/', '\\').LastOrDefault(z => z.Length > 0) ?? "",
            MoveWords((string?) row["Move"] ?? ""));

    private static void AddMiss(Dictionary<string, Quest> quests, string code, JsonObject row, int worked, int failed) {
        code = OneForAllSchools(code);
        if (!quests.TryGetValue(code, out var quest)) {
            quests[code] = quest = new Quest { Code = code, Title = code, World = WorldOf(code), First = DateTime.MaxValue };
        }

        var (step, zone, move) = Where(row);
        if (quest.Misses.FirstOrDefault(m => m.Step == step && m.Place == zone && m.Move == move) is { } same) {
            same.Worked += worked;
            same.Failed += failed;
            return;
        }

        quest.Misses.Add(new Miss { Step = step, Place = zone, Move = move, Worked = worked, Failed = failed });
    }

    // Two reports of the same trouble differ only in their numbers ("lost 3 times", "lost 4 times").
    private static string TroubleKey(string text) => Regex.Replace(text, @"[\d.,:]+", "#");

    internal static string MoveWords(string move) => move switch {
        "step_aside" => "stepped a little aside",
        "walk_away_and_back" => "walked well away and came back",
        "other_route" => "took another route",
        "commons_and_walk_back" => "pressed Commons and walked back",
        "fight_nearby" => "fought the creatures around",
        "click_offered" => "clicked what was offered",
        "set_step_aside" => "set the step aside for a while",
        _ => move.Replace('_', ' '),
    };

    /// <summary>Every bot's fight styles added up, kind by kind; only those with enough fights to count.</summary>
    private static List<Fight> ReadFights(IEnumerable<string> paths) {
        var sum = new Dictionary<(string Kind, string Style), Fight>();
        foreach (var path in paths.Where(File.Exists)) {
            JsonObject? kinds;
            try {
                kinds = JsonNode.Parse(string.Join('\n', ReadLines(path))) as JsonObject;
            }
            catch (JsonException) {
                continue;
            }

            foreach (var (kind, styles) in kinds ?? []) {
                foreach (var (style, node) in styles as JsonObject ?? []) {
                    if (node is not JsonObject s) {
                        continue;
                    }

                    if (!sum.TryGetValue((kind, style), out var f)) {
                        sum[(kind, style)] = f = new Fight { Kind = kind, Style = style };
                    }

                    f.Fights += (int?) s["Fights"] ?? 0;
                    f.Wins += (int?) s["Wins"] ?? 0;
                    f.Rounds += (double?) s["Rounds"] ?? 0;
                    f.HealthLost += (double?) s["HealthLost"] ?? 0;
                }
            }
        }

        return sum.Values.Where(f => f.Fights >= FIGHTS_TO_COUNT)
            .OrderBy(f => f.Kind, StringComparer.OrdinalIgnoreCase).ThenByDescending(f => f.Fights).ToList();
    }

    // ---------------------------------------------------------------------
    // Worlds
    // ---------------------------------------------------------------------

    private static readonly (string Code, string Name)[] s_worlds = [
        ("WC", "Wizard City"), ("KT", "Krokotopia"), ("MB", "Marleybone"), ("MS", "MooShu"), ("DS", "Dragonspyre"), ("CL", "Celestia"),
        ("ZF", "Zafaria"), ("AV", "Avalon"), ("AZ", "Azteca"), ("KR", "Khrysalis"), ("PL", "Polaris"), ("MR", "Mirage"), ("EM", "Empyrea"),
        ("KM", "Karamelle"), ("LM", "Lemuria"), ("NV", "Novus"), ("WR", "Wallaru"), ("DM", "Darkmoor"), ("AH", "Ahmarra"),
    ];

    private static readonly string[] s_schools = ["BALANCE", "FIRE", "ICE", "STORM", "MYTH", "LIFE", "DEATH"];

    /// <summary>A quest each school has its own copy of ("To Ravenwood!", WC-COMMONS-MAIN-002-FIRE) counts as one quest.</summary>
    internal static string OneForAllSchools(string code) {
        var dash = code.LastIndexOf('-');

        return dash > 0 && s_schools.Contains(code[(dash + 1)..], StringComparer.OrdinalIgnoreCase) ? code[..dash] : code;
    }

    internal static string WorldOf(string code) {
        var prefix = code.Split('-')[0];
        var hit = s_worlds.FirstOrDefault(w => w.Code.Equals(prefix, StringComparison.OrdinalIgnoreCase));

        return hit.Name ?? (prefix.Length > 0 ? prefix : "Other");
    }

    internal static int WorldOrder(string world) {
        var i = Array.FindIndex(s_worlds, w => w.Name == world);

        return i < 0 ? 99 : i;
    }

    // ---------------------------------------------------------------------
    // Words
    // ---------------------------------------------------------------------

    internal static string Time(double seconds) {
        if (seconds < 90) {
            return $"{Math.Max(1, Math.Round(seconds))} seconds";
        }

        var minutes = seconds / 60;

        return minutes < 90 ? $"{Math.Round(minutes)} minutes" : $"{(minutes / 60).ToString("0.#", CultureInfo.InvariantCulture)} hours";
    }

    /// <summary>One bot's finish of a quest, in a sentence.</summary>
    internal static string Line(Done d) {
        var text = new StringBuilder($"{d.Bot}: finished it by itself {Plural(d.Times, "time")}, best in {Time(d.BestSeconds)}");
        if (d.Steps > 0) {
            text.Append($", {Plural(d.Steps, "step")}");
        }

        if (d.FightsWon > 0) {
            text.Append($", {Plural(d.FightsWon, "fight")} won");
        }

        text.Append(d.Troubles > 0 ? $", with {Plural(d.Troubles, "hold-up")} on the way that it got past itself." : ".");

        return text.ToString();
    }

    internal static string Line(Fix f) =>
        $"Got stuck at \"{f.Step}\" in {f.Place}: {Who(f.By)} {f.Move}, and that worked every time it was tried ({Plural(f.Worked, "time")}).";

    internal static string Line(Miss m) => m.Worked == 0
        ? $"Avoid at \"{m.Step}\" in {m.Place}: {m.Move} failed {Plural(m.Failed, "time")} and never worked there."
        : $"Try last at \"{m.Step}\" in {m.Place}: {m.Move} worked {Plural(m.Worked, "time")} but failed {Plural(m.Failed, "time")}.";

    internal static string Line(Trouble t) =>
        $"Went wrong ({Plural(t.Times, "time")}, {string.Join(", ", t.Bots)}): {t.Text}";

    // Who found the move: the stuck-solutions file says "memory" (a move that worked there before) or "local AI" (thought it through).
    private static string Who(string by) => by switch {
        "memory" => "a bot remembered what worked there before and",
        "local AI" => "a bot thought it through and",
        "" => "a bot",
        _ => by,
    };

    internal static string Line(Fight f) {
        var won = f.Fights == 0 ? 0 : 100.0 * f.Wins / f.Fights;

        return $"\"{f.Style}\": {Plural(f.Fights, "fight")}, won {won:0}%, about {f.Rounds / Math.Max(1, f.Fights):0.#} rounds, " +
               $"{100 * f.HealthLost / Math.Max(1, f.Fights):0}% health lost on average.";
    }

    internal static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    /// <summary>The book as plain text pages, one per world, in the same simple layout as the GitHub folder's pages.</summary>
    internal static IEnumerable<(string Name, string Text)> Pages(Book book) {
        foreach (var world in book.Quests.GroupBy(q => q.World)) {
            var text = new StringBuilder();
            text.AppendLine($"# {world.Key}");
            text.AppendLine();
            text.AppendLine($"How the bots finished each quest of {world.Key}, from their own logs (collected {book.Made:MMMM d, yyyy}). " +
                            "Only runs a bot did start to finish by itself count as done. What failed is listed too (Avoid, Try last, Went wrong), so the bots know what to stay away from.");
            foreach (var q in world) {
                text.AppendLine();
                text.AppendLine($"## {q.Title}");
                foreach (var d in q.Done.OrderBy(d => d.BestSeconds)) {
                    text.AppendLine($"- {Line(d)}");
                }

                foreach (var f in q.Fixes) {
                    text.AppendLine($"- {Line(f)}");
                }

                // What failed, so it is avoided: the moves that did not help, then the problems the bots ran into.
                foreach (var m in q.Misses.OrderBy(m => m.Worked).ThenByDescending(m => m.Failed)) {
                    text.AppendLine($"- {Line(m)}");
                }

                foreach (var t in q.Troubles.OrderByDescending(t => t.Times).Take(8)) {
                    text.AppendLine($"- {Line(t)}");
                }
            }

            yield return (world.Key, text.ToString());
        }

        if (book.Fights.Count > 0) {
            var text = new StringBuilder();
            text.AppendLine("# How the bots fight");
            text.AppendLine();
            text.AppendLine($"Every bot's fights added up by kind of fight. \"build up\" is a blade or trap first, then one big hit. \"hit now\" is the best hit at once. " +
                            $"A style shows only after {FIGHTS_TO_COUNT} fights of that kind.");
            foreach (var kind in book.Fights.GroupBy(f => f.Kind)) {
                text.AppendLine();
                text.AppendLine($"## {kind.Key}");
                foreach (var f in kind) {
                    text.AppendLine($"- {Line(f)}");
                }
            }

            yield return ("How the bots fight", text.ToString());
        }
    }

    internal static void Save(Book book, string logs) {
        var folder = Path.Combine(logs, FOLDER);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, BOOK_FILE);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(book, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    internal static Book? Load(string logs) {
        try {
            var path = Path.Combine(logs, FOLDER, BOOK_FILE);

            return File.Exists(path) ? JsonSerializer.Deserialize<Book>(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
            return null;
        }
    }

}
