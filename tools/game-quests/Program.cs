/*
 * The game's own quest files, read into the server's quest types (2026-11-28).
 *
 * The client ships CSR.wad, which holds QuestData/<area>/<QUEST>.xml: every quest's full template (3,661 of them),
 * with its dialog, step volumes, step results and rewards. The quests rebuilt from Wizard404 lost what Wizard404 does not
 * record (dialog events, the volume a step is reached by, what a step's end does to the world), and 780 quests are not
 * on the server at all.
 *
 *   repair   For each quest in spiraldb-extra\QuestTemplates that the game has: fills in only what the rebuilt copy lacks,
 *            and keeps its step names, so progress players already have still matches. Writes changed files to out\repair.
 *   missing  Writes the quests the server lacks (those whose code matches --only, a regex) to out\missing\QuestTemplates,
 *            with their gold and experience as a reward table in out\missing\DropTables.
 *
 * Usage: dotnet run -c Release -- repair|missing [--csr <CSR.wad>] [--server <Server folder>] [--only <regex>]
 * Nothing is written into the server; out\report.txt says what changed.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.WizardData;
using Type = System.Type;

internal static class Program {

    private const string DEFAULT_CSR = @"C:\ProgramData\KingsIsle Entertainment\Wizard101\Data\GameData\CSR.wad";
    private static readonly string Here = AppContext.GetData("APP_CONTEXT_BASE_DIRECTORY") as string ?? ".";
    private static readonly List<string> Report = [];
    private static readonly Dictionary<string, int> Unknown = new(StringComparer.Ordinal);

    private static int Main(string[] args) {
        if (args.Length == 2 && args[0] == "fields") {
            var t = Types.GetType("Imcodec.ObjectProperty.TypeCache." + args[1]);
            for (var b = t; b is not null && b != typeof(object); b = b.BaseType) {
                Console.WriteLine(b.Name + ": " + string.Join(", ", b.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(f => f.Name + ":" + f.FieldType.Name))
                    + " | props: " + string.Join(", ", b.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(p => p.Name)));
            }
            return 0;
        }

        // tidy: the server's own quest files in a folder, with missing lists made empty (written to --out\tidy).
        if (args.Length >= 2 && args[0] == "tidy") {
            var outTidy = Path.Combine(args.Length >= 3 ? args[2] : ".", "tidy");
            Directory.CreateDirectory(outTidy);
            int seen = 0, changed = 0;
            foreach (var file in Directory.GetFiles(args[1], "*.json")) {
                try {
                    var quest = Load(file);
                    if (quest is null) continue;
                    seen++;
                    if (Tidy(quest, 0) > 0) {
                        changed++;
                        File.WriteAllText(Path.Combine(outTidy, Path.GetFileName(file)), Json(quest));
                    }
                }
                catch (Exception e) {
                    Console.WriteLine($"{Path.GetFileName(file)}: {e.Message}");
                }
            }

            Console.WriteLine($"{changed} of {seen} quest files had missing lists; written to {outTidy}");
            return 0;
        }

        if (args.Length == 0 || args[0] is not ("repair" or "missing")) {
            Console.WriteLine("Usage: GameQuests repair|missing [--csr <CSR.wad>] [--server <Server>] [--only <regex>] [--out <folder>]");
            return 1;
        }

        string Arg(string name, string fallback) {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }

        var csr = Arg("--csr", DEFAULT_CSR);
        var server = Arg("--server", Path.GetFullPath(Path.Combine(Here, "..", "..", "..", "..", "..", "..", "Server")));
        var only = new Regex(Arg("--only", "."), RegexOptions.IgnoreCase);
        var outDir = Arg("--out", Path.GetFullPath(Path.Combine(Here, "..", "..", "..", "out")));

        var game = ReadGameQuests(csr);
        Console.WriteLine($"{game.Count} quests read from {csr}.");

        var result = args[0] == "repair" ? Repair(game, server, Path.Combine(outDir, "repair"))
            : Missing(game, server, only, Path.Combine(outDir, "missing"));

        if (Unknown.Count > 0) {
            Report.Add("");
            Report.Add("Game fields the server's types do not have (left out): "
                + string.Join(", ", Unknown.OrderByDescending(p => p.Value).Take(40).Select(p => $"{p.Key} ({p.Value})")));
        }

        Directory.CreateDirectory(outDir);
        File.WriteAllLines(Path.Combine(outDir, $"report-{args[0]}.txt"), Report);
        Console.WriteLine(result);
        Console.WriteLine($"Details: {Path.Combine(outDir, $"report-{args[0]}.txt")}");
        return 0;
    }

    // ---------------------------------------------------------------- reading the game's files

    private static Dictionary<string, QuestTemplate> ReadGameQuests(string wadPath) {
        var quests = new Dictionary<string, QuestTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, data) in WadEntries(wadPath, n => n.StartsWith("QuestData/", StringComparison.Ordinal)
                     && n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))) {
            try {
                var doc = XDocument.Parse(DecodeText(data));
                var cls = doc.Root?.Elements("Class").FirstOrDefault();
                if (cls is null || FromXml(cls) is not QuestTemplate quest || string.IsNullOrEmpty((string) quest.m_questName)) {
                    continue;
                }

                // A quest can be in two files (an old copy under another name): the file named after it wins.
                var questName = (string) quest.m_questName;
                var ownFile = string.Equals(Path.GetFileNameWithoutExtension(name), questName, StringComparison.OrdinalIgnoreCase);
                if (!quests.ContainsKey(questName) || ownFile) {
                    quests[questName] = quest;
                }
            }
            catch (Exception e) {
                Report.Add($"{name}: could not be read ({e.Message})");
            }
        }

        return quests;
    }

    private static string DecodeText(byte[] data) {
        var text = Encoding.UTF8.GetString(data);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    private static IEnumerable<(string Name, byte[] Data)> WadEntries(string path, Func<string, bool> wanted) {
        using var f = File.OpenRead(path);
        using var r = new BinaryReader(f);
        if (Encoding.ASCII.GetString(r.ReadBytes(5)) != "KIWAD") {
            throw new InvalidDataException(path + " is not a KIWAD file.");
        }

        var version = r.ReadUInt32();
        var count = r.ReadUInt32();
        if (version >= 2) {
            r.ReadByte();
        }

        var entries = new List<(string Name, uint Offset, uint Size, uint ZSize, bool Compressed)>();
        for (var i = 0; i < count; i++) {
            var offset = r.ReadUInt32();
            var size = r.ReadUInt32();
            var zsize = r.ReadUInt32();
            var compressed = r.ReadByte() != 0;
            r.ReadUInt32();
            var nameLength = r.ReadInt32();
            var name = Encoding.Latin1.GetString(r.ReadBytes(nameLength)).TrimEnd('\0');
            entries.Add((name, offset, size, zsize, compressed));
        }

        foreach (var e in entries.Where(e => wanted(e.Name))) {
            f.Position = e.Offset;
            var raw = r.ReadBytes((int) (e.Compressed ? e.ZSize : e.Size));
            if (e.Compressed) {
                using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
                using var o = new MemoryStream();
                z.CopyTo(o);
                raw = o.ToArray();
            }

            yield return (e.Name, raw);
        }
    }

    private static readonly Assembly Types = typeof(PropertyClass).Assembly;

    /// <summary>An object of the server's types from a game XML class element; null for a class the types lack.</summary>
    private static object FromXml(XElement cls) {
        var className = ((string) cls.Attribute("Name") ?? "").Replace("class ", "").Trim();

        // The server's types know these two only by name (the game program's own type list has no fields for them):
        // an encounter's experience is experience, and starting an encounter is an event the server acts on
        // (ResPostEventHandler, "ImlightStartEncounter:<quest>").
        if (className == "ResAddEncounterXP") {
            return new ResAddMagicXP {
                m_experience = int.TryParse((string) cls.Element("m_experience"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : 0,
            };
        }

        if (className == "ResAddEncounter" && (string) cls.Element("m_encounterName") is { Length: > 0 } encounter) {
            return new ResPostEvent { m_eventName = "ImlightStartEncounter:" + encounter };
        }

        // An item reward's per-school choices (m_lootOptions) are not in the server's type: kept aside for the reward table.
        if (className == "ResItemLoot") {
            var options = new List<(ulong Item, RequirementList Needs)>();
            foreach (var option in cls.Elements("m_lootOptions").SelectMany(e => e.Elements("Class"))) {
                if (ulong.TryParse((string) option.Element("m_itemTemplateID"), out var item) && item != 0) {
                    var needs = option.Element("m_lootRequirements")?.Elements("Class").FirstOrDefault() is { } req
                        ? FromXml(req) as RequirementList : null;
                    options.Add((item, needs));
                }
            }

            var loot = new ResItemLoot {
                m_itemTemplateID = ulong.TryParse((string) cls.Element("m_itemTemplateID"), out var main) ? main : 0,
            };
            LootOptions[loot] = options;
            return loot;
        }

        var type = Types.GetType("Imcodec.ObjectProperty.TypeCache." + className);
        if (type is null || type.IsAbstract) {
            Note("class " + className);
            return null;
        }

        var obj = Activator.CreateInstance(type);
        foreach (var group in cls.Elements().GroupBy(e => e.Name.LocalName)) {
            var member = Member(type, group.Key);
            if (member is null) {
                Note(group.Key);
                continue;
            }

            var memberType = member is FieldInfo fi ? fi.FieldType : ((PropertyInfo) member).PropertyType;
            try {
                var value = Convert(memberType, group.ToList());
                if (value is not null) {
                    if (member is FieldInfo field) {
                        field.SetValue(obj, value);
                    }
                    else {
                        ((PropertyInfo) member).SetValue(obj, value);
                    }
                }
            }
            catch (Exception) {
                Note(className + "." + group.Key + " (value)");
            }
        }

        return obj;
    }

    private static readonly Dictionary<(Type, string), MemberInfo> Members = [];
    private static readonly Dictionary<ResItemLoot, List<(ulong Item, RequirementList Needs)>> LootOptions = new(ReferenceEqualityComparer.Instance);

    private static MemberInfo Member(Type type, string name) {
        if (Members.TryGetValue((type, name), out var known)) {
            return known;
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
        MemberInfo m = type.GetField(name, flags);
        if (m is null && type.GetProperty(name, flags) is { CanWrite: true } p) {
            m = p;
        }

        Members[(type, name)] = m;
        return m;
    }

    private static object Convert(Type type, List<XElement> elements) {
        if (type != typeof(string) && typeof(IList).IsAssignableFrom(type) && type.IsGenericType) {
            var itemType = type.GetGenericArguments()[0];
            var list = (IList) Activator.CreateInstance(type);
            foreach (var e in elements) {
                var item = One(itemType, e);
                if (item is not null) {
                    list.Add(item);
                }
            }

            return list;
        }

        return One(type, elements[^1]);
    }

    private static object One(Type type, XElement e) {
        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null) {
            type = nullable;
        }

        if (typeof(PropertyClass).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface) {
            var cls = e.Elements("Class").FirstOrDefault();
            return cls is null ? null : FromXml(cls);
        }

        var text = e.Value ?? "";
        if (type == typeof(string)) {
            return text;
        }

        if (type == typeof(bool)) {
            return text.Trim() is "1" or "true" or "True";
        }

        if (type.IsEnum) {
            var parts = text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.Contains("::") ? p[(p.LastIndexOf("::", StringComparison.Ordinal) + 2)..] : p).ToList();
            if (parts.Count == 0) {
                return Activator.CreateInstance(type);
            }

            long bits = 0;
            foreach (var part in parts) {
                bits |= Enum.TryParse(type, part, true, out var v) ? System.Convert.ToInt64(v)
                    : long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            }

            return Enum.ToObject(type, bits);
        }

        if (type.IsPrimitive || type == typeof(decimal)) {
            if (string.IsNullOrWhiteSpace(text)) {
                return Activator.CreateInstance(type);
            }

            return System.Convert.ChangeType(double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                && type != typeof(ulong) && type != typeof(long) ? d : (object) text, type, CultureInfo.InvariantCulture);
        }

        // Types with a conversion from a string (ByteString and the like).
        var fromString = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name is "op_Implicit" or "op_Explicit" && m.ReturnType == type
                && m.GetParameters() is [{ ParameterType: var pt }] && pt == typeof(string));
        if (fromString is not null) {
            return fromString.Invoke(null, [text]);
        }

        // Vectors and colours: numbers in order into the fields.
        var numbers = text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        var obj = Activator.CreateInstance(type);
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => f.FieldType.IsPrimitive).ToList();
        for (var i = 0; i < fields.Count && i < numbers.Length; i++) {
            fields[i].SetValue(obj, System.Convert.ChangeType(
                double.Parse(numbers[i], CultureInfo.InvariantCulture), fields[i].FieldType, CultureInfo.InvariantCulture));
        }

        return obj;
    }

    private static void Note(string what) => Unknown[what] = Unknown.GetValueOrDefault(what) + 1;

    private static string S(object v) => v?.ToString() ?? "";

    // ---------------------------------------------------------------- server files

    private static QuestTemplate Load(string file) {
        using var s = File.OpenRead(file);
        return JsonSerializer.Deserialize<QuestTemplate>(s, SpiralJsonOptions.Options);
    }

    private static string Json(QuestTemplate quest) {
        Tidy(quest, 0);
        return JsonSerializer.Serialize(quest, new JsonSerializerOptions(SpiralJsonOptions.Options) { WriteIndented = true });
    }

    /// <summary>
    /// The game's files leave out empty lists, and the server's code expects a list (empty or not): a quest with a "Prep"
    /// section and no lines crashed the server while it sent the quest (PL-URVI-ENC-002, 2026-11-28). Every missing list
    /// becomes an empty one, and dialog sections with no lines go.
    /// </summary>
    private static int Tidy(object obj, int depth) {
        if (obj is null || depth > 40) {
            return 0;
        }

        var changes = 0;
        if (obj is ActorDialogList adl && adl.m_dialogs is { } dialogs) {
            changes += dialogs.RemoveAll(d => d is null || d.m_dialogEntries is not { Count: > 0 });
        }

        var type = obj.GetType();
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance)) {
            changes += TidyMember(field.FieldType, () => field.GetValue(obj), v => field.SetValue(obj, v), depth);
        }

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
            if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0) {
                continue;
            }

            changes += TidyMember(prop.PropertyType, () => prop.GetValue(obj), v => prop.SetValue(obj, v), depth);
        }

        return changes;
    }

    private static int TidyMember(Type type, Func<object> get, Action<object> set, int depth) {
        object value;
        try {
            value = get();
        }
        catch (Exception) {
            return 0;
        }

        if (type != typeof(string) && type.IsGenericType && typeof(IList).IsAssignableFrom(type)) {
            if (value is null) {
                try {
                    set(Activator.CreateInstance(type));
                    return 1;
                }
                catch (Exception) {
                    return 0;
                }
            }

            var changes = 0;
            foreach (var item in (IList) value) {
                if (item is PropertyClass) {
                    changes += Tidy(item, depth + 1);
                }
            }

            return changes;
        }

        return value is PropertyClass ? Tidy(value, depth + 1) : 0;
    }

    private static readonly HashSet<string> RewardResults = new(StringComparer.Ordinal) {
        nameof(ResAddGold), nameof(ResAddMagicXP), "ResAddEncounterXP", nameof(ResAddTrainingPoints), nameof(ResDropTable),
    };

    // ---------------------------------------------------------------- repair

    private static string Repair(Dictionary<string, QuestTemplate> game, string server, string outDir) {
        var folder = Path.Combine(server, "spiraldb-extra", "QuestTemplates");
        Directory.CreateDirectory(outDir);
        int seen = 0, changed = 0;
        var kinds = new Dictionary<string, int>();
        void Count(string kind) => kinds[kind] = kinds.GetValueOrDefault(kind) + 1;

        foreach (var file in Directory.GetFiles(folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
            QuestTemplate ours;
            try {
                ours = Load(file);
            }
            catch (Exception e) {
                Report.Add($"{Path.GetFileName(file)}: not read ({e.Message})");
                continue;
            }

            if (ours is null || !game.TryGetValue((string) ours.m_questName ?? "", out var theirs)) {
                continue;
            }

            seen++;
            var notes = new List<string>();

            // Dialog lines: the game's dialog events and actions, matched by the line's sound file.
            var events = new Dictionary<string, (string Event, string Action)>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in Entries(theirs)) {
                var key = SoundKey(S(entry.m_soundFile));
                if (key.Length > 0 && (S(entry.m_dialogEvent).Length > 0 || S(entry.m_action).Length > 0)) {
                    events[key] = (S(entry.m_dialogEvent), S(entry.m_action));
                }
            }

            foreach (var entry in Entries(ours)) {
                if (!events.TryGetValue(SoundKey(S(entry.m_soundFile)), out var ev)) {
                    continue;
                }

                if (S(entry.m_dialogEvent).Length == 0 && ev.Event.Length > 0) {
                    entry.m_dialogEvent = ev.Event;
                    notes.Add($"line {SoundKey(S(entry.m_soundFile))}: event {ev.Event}");
                    Count("dialog event");
                }

                if (S(entry.m_action).Length == 0 && ev.Action.Length > 0) {
                    entry.m_action = ev.Action;
                    notes.Add($"line {SoundKey(S(entry.m_soundFile))}: action {ev.Action}");
                    Count("dialog action");
                }
            }

            var ourSounds = Entries(ours).Select(e => SoundKey(S(e.m_soundFile))).Where(k => k.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Steps, matched by place and checked by kind and title.
            var ourGoals = ours.m_goals ?? [];
            var theirGoals = theirs.m_goals ?? [];
            for (var i = 0; i < ourGoals.Count && i < theirGoals.Count; i++) {
                GoalTemplate a = ourGoals[i], b = theirGoals[i];
                if (a is null || b is null || a.GetType() != b.GetType()
                    || (S(a.m_goalTitle).Length > 0 && S(b.m_goalTitle).Length > 0 && S(a.m_goalTitle) != S(b.m_goalTitle))) {
                    if (a is not null && b is not null) {
                        notes.Add($"step {i + 1} ({S(a.m_goalName)}): not the game's step {S(b.m_goalName)} ({b.GetType().Name} {S(b.m_goalTitle)}); left alone");
                        Count("step left alone");
                    }

                    continue;
                }

                var stepName = S(a.m_goalName);
                if (a is WaypointGoalTemplate wa && b is WaypointGoalTemplate wb
                    && S(wa.m_proximityTag).Length == 0 && S(wb.m_proximityTag).Length > 0) {
                    wa.m_proximityTag = wb.m_proximityTag;
                    wa.m_zoneEntry = wb.m_zoneEntry;
                    wa.m_zoneExit = wb.m_zoneExit;
                    if (S(wb.m_zoneTag).Length > 0) {
                        wa.m_zoneTag = wb.m_zoneTag;
                    }

                    notes.Add($"step {stepName}: reached by {S(wb.m_proximityTag)} (was: enter the zone)");
                    Count("step volume");
                }

                // A dialog the copy already shows (as the step's completion lines) is not shown a second time.
                var gameResults = (b.m_completeResults?.m_results ?? []).Where(r => r is not null && !RewardResults.Contains(r.GetType().Name)
                    && !(r is ResActorDialog ad && (ad.m_dialog?.m_dialogEntries ?? []).OfType<NPCDialogEntry>()
                        .Any(e => ourSounds.Contains(SoundKey(S(e.m_soundFile)))))).ToList();
                if (gameResults.Count > 0 && (a.m_completeResults?.m_results ?? []).Count == 0) {
                    a.m_completeResults ??= new ResultList();
                    a.m_completeResults.m_results = [.. gameResults];
                    notes.Add($"step {stepName}: {gameResults.Count} results ({string.Join(", ", gameResults.Select(Describe))})");
                    Count("step results");
                }

                if (a is ScavengeGoalTemplate sa && b is ScavengeGoalTemplate sb) {
                    if ((sa.m_clientTags?.Count ?? 0) == 0 && (sb.m_clientTags?.Count ?? 0) > 0) {
                        sa.m_clientTags = sb.m_clientTags;
                        notes.Add($"step {stepName}: object tags {string.Join(", ", sb.m_clientTags.Select(x => S(x)))}");
                        Count("use step tags");
                    }

                    if ((sa.m_itemAdjectives?.Count ?? 0) == 0 && (sb.m_itemAdjectives?.Count ?? 0) > 0) {
                        sa.m_itemAdjectives = sb.m_itemAdjectives;
                        Count("use step adjectives");
                    }
                }

                if (a is BountyGoalTemplate ba && b is BountyGoalTemplate bb
                    && (ba.m_npcAdjectives?.Count ?? 0) == 0 && (bb.m_npcAdjectives?.Count ?? 0) > 0) {
                    ba.m_npcAdjectives = bb.m_npcAdjectives;
                    notes.Add($"step {stepName}: creatures {string.Join(", ", bb.m_npcAdjectives.Select(x => S(x)))}");
                    Count("defeat step creatures");
                }
            }

            if (ourGoals.Count != theirGoals.Count) {
                notes.Add($"has {ourGoals.Count} steps, the game {theirGoals.Count}");
            }

            // The quest's own start and end: what the game does there and the copy does not (rewards stay as they are).
            foreach (var (label, mine, gameList) in new[] {
                         ("start", ours.m_startResults ??= new ResultList(), theirs.m_startResults),
                         ("end", ours.m_endResults ??= new ResultList(), theirs.m_endResults) }) {
                mine.m_results ??= [];
                foreach (var r in gameList?.m_results ?? []) {
                    if (r is null || RewardResults.Contains(r.GetType().Name)
                        || (r is ResActorDialog ad && (ad.m_dialog?.m_dialogEntries ?? []).OfType<NPCDialogEntry>()
                            .Any(e => ourSounds.Contains(SoundKey(S(e.m_soundFile)))))) {
                        continue;
                    }

                    var id = Describe(r);
                    if (mine.m_results.Any(m => m is not null && Describe(m) == id)) {
                        continue;
                    }

                    mine.m_results.Add(r);
                    notes.Add($"{label}: {id}");
                    Count(label + " result");
                }
            }

            if (notes.Count(n => !n.Contains("left alone") && !n.StartsWith("has ", StringComparison.Ordinal)) == 0) {
                if (notes.Count > 0) {
                    Report.Add($"{S(ours.m_questName)}: (nothing filled in)");
                    Report.AddRange(notes.Select(n => "    " + n));
                }

                continue;
            }

            changed++;
            File.WriteAllText(Path.Combine(outDir, Path.GetFileName(file)), Json(ours));
            Report.Add($"{S(ours.m_questName)}:");
            Report.AddRange(notes.Select(n => "    " + n));
        }

        Report.Insert(0, $"{changed} of {seen} rebuilt quests the game has were filled in: "
            + string.Join(", ", kinds.OrderByDescending(k => k.Value).Select(k => $"{k.Value} {k.Key}")));
        Report.Insert(1, "");
        return Report[0];
    }

    private static string SoundKey(string sound) {
        var i = sound.LastIndexOfAny(['/', '|', '\\']);
        return (i >= 0 ? sound[(i + 1)..] : sound).Trim();
    }

    private static string Describe(Result r) => r switch {
        ResAddDynaMod d => $"{nameof(ResAddDynaMod)} {S(d.m_dynaModClientTag)} {S(d.m_dynaModState)}{(d.m_dynaModRemove ? " remove" : "")}",
        ResRemoveDynaMod d => $"{nameof(ResRemoveDynaMod)} {S(d.m_dynaModClientTag)}",
        ResPostEvent p => $"{nameof(ResPostEvent)} {S(p.m_eventName)}",
        _ => r.GetType().Name + " " + JsonSerializer.Serialize<Result>(r, SpiralJsonOptions.Options).GetHashCode().ToString("x"),
    };

    /// <summary>Every NPC dialog line of a quest: its own dialogs, its steps' dialogs and the dialogs its results show.</summary>
    private static IEnumerable<NPCDialogEntry> Entries(QuestTemplate quest) {
        IEnumerable<NPCDialogEntry> Of(ActorDialogListBase list)
            => (list as ActorDialogList)?.m_dialogs?.Where(d => d is not null)
                .SelectMany(d => d.m_dialogEntries ?? []).OfType<NPCDialogEntry>() ?? [];
        IEnumerable<NPCDialogEntry> Results(ResultList list)
            => (list?.m_results ?? []).OfType<ResActorDialog>().Where(r => r.m_dialog is not null)
                .SelectMany(r => r.m_dialog.m_dialogEntries ?? []).OfType<NPCDialogEntry>();

        foreach (var e in Of(quest.m_dialogList)) yield return e;
        foreach (var e in Results(quest.m_startResults)) yield return e;
        foreach (var e in Results(quest.m_endResults)) yield return e;
        foreach (var g in quest.m_goals ?? []) {
            if (g is null) continue;
            foreach (var e in Of(g.m_dialogList)) yield return e;
            foreach (var e in Results(g.m_completeResults)) yield return e;
        }
    }

    // ---------------------------------------------------------------- missing

    private static string Missing(Dictionary<string, QuestTemplate> game, string server, Regex only, string outDir) {
        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.GetDirectories(server, "spiraldb-*")) {
            var q = Path.Combine(dir, "QuestTemplates");
            if (!Directory.Exists(q)) continue;
            foreach (var f in Directory.GetFiles(q, "questtemplates_*.json")) {
                have.Add(Path.GetFileNameWithoutExtension(f)["questtemplates_".Length..]);
            }
        }

        var quests = Path.Combine(outDir, "QuestTemplates");
        var tables = Path.Combine(outDir, "DropTables");
        Directory.CreateDirectory(quests);
        Directory.CreateDirectory(tables);
        var written = 0;
        foreach (var (name, quest) in game.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)) {
            if (have.Contains(name) || !only.IsMatch(name)) {
                continue;
            }

            // Gold and experience come through a reward table, as for every other quest on the server.
            long gold = 0, xp = 0, training = 0;
            var items = new List<object>();
            var end = quest.m_endResults ??= new ResultList();
            end.m_results ??= [];
            foreach (var r in end.m_results.ToList()) {
                switch (r) {
                    case ResItemLoot loot:
                        // No server code gives a ResItemLoot: the item (or its per-school choice) goes in the reward table.
                        var options = LootOptions.GetValueOrDefault(loot) ?? [];
                        if (options.Count > 0) {
                            items.AddRange(options.Select(o => (object) new Dictionary<string, object> {
                                ["ItemId"] = o.Item.ToString(CultureInfo.InvariantCulture), ["ItemName"] = "", ["Notes"] = "",
                                ["Requirements"] = o.Needs is null ? null : JsonSerializer.SerializeToElement(o.Needs, SpiralJsonOptions.Options),
                            }));
                        }
                        else if (loot.m_itemTemplateID != 0) {
                            items.Add(new Dictionary<string, object> {
                                ["ItemId"] = loot.m_itemTemplateID.ToString(CultureInfo.InvariantCulture), ["ItemName"] = "", ["Notes"] = "",
                                ["Requirements"] = null,
                            });
                        }
                        end.m_results.Remove(r);
                        break;
                    case ResAddGold g: gold += g.m_gold; end.m_results.Remove(r); break;
                    case ResAddMagicXP m: xp += m.m_experience; end.m_results.Remove(r); break;
                    default:
                        if (r?.GetType().Name == "ResAddEncounterXP"
                            && r.GetType().GetField("m_experience")?.GetValue(r) is { } e) {
                            xp += System.Convert.ToInt64(e);
                            end.m_results.Remove(r);
                        }
                        break;
                }
            }

            end.m_results.Add(new ResDropTable { m_tableName = name });
            File.WriteAllText(Path.Combine(quests, $"questtemplates_{name}.json"), Json(quest));
            var table = new Dictionary<string, object> {
                ["Name"] = name, ["Description"] = $"{name} (from the game's own quest file)", ["RollChance"] = 1.0,
                ["Weight"] = 100, ["NoneChance"] = 0.0, ["PityCounter"] = 0.0, ["MinGold"] = gold, ["MaxGold"] = gold,
                ["ExperienceAmount"] = xp, ["TrainingPoints"] = training, ["Items"] = items,
            };
            File.WriteAllText(Path.Combine(tables, $"droptables_{name.ToLowerInvariant()}.json"),
                JsonSerializer.Serialize(table, new JsonSerializerOptions { WriteIndented = true }));
            written++;
            Report.Add($"{name}: {quest.GetType().Name}, {quest.m_goals?.Count ?? 0} steps, {gold} gold, {xp} experience, {items.Count} items");
        }

        Report.Insert(0, $"{written} quests the server lacks written (matching {only}).");
        Report.Insert(1, "");
        return Report[0];
    }

}
