/*
 * Quest reports: one zip beside the server that collects every quest found missing or broken (2026-11-28).
 *
 * Server\quest-reports.zip keeps itself up to date, so it can be sent as it is whenever someone is to fix quests:
 *   missing/<QUEST>.json  a quest code the server was asked for (a quest's requirements, an encounter, a zone trigger, an
 *                         object) but has no quest file for: when, how often, and what asked.
 *   broken/<QUEST>.json   a quest the quest checker (Ahziael's Quest Testing tab) failed, with its problems and the
 *                         server's quest file as it stands.
 *   stuck/<QUEST>.json    each time a wizard typed .stuck while on the quest: step, progress, zone and spot.
 *   index.txt             one line per report.
 * A quest that turns up is taken out of missing/ when quests load or the checker runs; one the checker passes is taken
 * out of broken/. Reports survive restarts (they are read back from the zip). Only quest codes, zones and steps are kept:
 * no account names, and a wizard only by its character number.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using Imlight.Common;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Tools;

internal static class QuestReports {

    internal const string FILE_NAME = "quest-reports.zip";
    private const int MAX_STUCK_REPORTS = 20;

    // A quest code: area letters, then parts joined by dashes (WC-ST04-C01-001, EM-AERI-J-ENC-001, KT-CRY-Palace).
    private static readonly Regex s_code = new(@"^[A-Za-z]{2,6}(-[A-Za-z0-9_]+){1,7}$", RegexOptions.Compiled);
    private static readonly object s_lock = new();
    private static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true };

    // Every entry of the zip by its path, as text. Read from the zip the first time.
    private static Dictionary<string, string> s_entries;
    private static readonly HashSet<string> s_missingThisRun = new(StringComparer.OrdinalIgnoreCase);
    private static int s_writeQueued;

    private static string ZipPath => Path.Combine(AppContext.BaseDirectory, FILE_NAME);

    /// <summary>
    /// The server was asked for a quest it has no file for (QuestTemplateCollection). Recorded once a run per code, with
    /// what asked for it.
    /// </summary>
    internal static void Missing(string questName) {
        try {
            if (string.IsNullOrWhiteSpace(questName) || questName.Length > 64 || !s_code.IsMatch(questName)
                || SpiralDB.QuestTemplates is not { Count: > 0 }) {
                return;
            }

            lock (s_lock) {
                if (!s_missingThisRun.Add(questName)) {
                    return;
                }
            }

            // Verified: a name the live game's arrow file has, or one the game's own data asked for. A made-up name the
            // server only probes for (a "_Hard" copy that does not exist) is not a missing quest.
            bool live;
            try {
                live = Imlight.CoreLib.Game.Services.QuestArrowGoals.IsLiveQuest(questName);
            }
            catch (Exception) {
                live = false;
            }

            var askedBy = AskedBy();
            if (!live && !FromGameData(askedBy)) {
                return;
            }

            Update($"missing/{questName}.json", node => {
                node["quest"] = questName;
                node["kind"] = "missing";
                node["inLiveGame"] = live;
                node["firstSeen"] ??= Now();
                node["lastSeen"] = Now();
                node["runsSeen"] = (node["runsSeen"]?.GetValue<int>() ?? 0) + 1;
                var asked = node["askedBy"] as JsonArray ?? [];
                if (askedBy.Length > 0 && !asked.Any(a => a?.GetValue<string>() == askedBy)) {
                    asked.Add(askedBy);
                }

                node["askedBy"] = asked;
                node["summary"] = "no quest file on the server; asked for by " + (askedBy.Length > 0 ? askedBy : "unknown");
            });
        }
        catch (Exception e) {
            Logger.Debug("Quest reports: could not note missing quest {0}: {1}", Logger.Args(questName, e.Message));
        }
    }

    /// <summary>The quest checker's results: failed quests go in broken/, passed ones come out.</summary>
    internal static void CheckerResults(IEnumerable<(string Code, bool Ok, List<string> Problems)> results) {
        try {
            var list = results.ToList();
            lock (s_lock) {
                var entries = Entries();
                foreach (var (code, ok, problems) in list) {
                    var path = $"broken/{code}.json";
                    if (ok) {
                        if (entries.TryGetValue(path, out var old) && JsonNode.Parse(old)?["source"]?.GetValue<string>() == "quest checker") {
                            entries.Remove(path);
                        }

                        continue;
                    }

                    var node = entries.TryGetValue(path, out var text) ? JsonNode.Parse(text) as JsonObject ?? [] : [];
                    node["quest"] = code;
                    node["kind"] = "broken";
                    node["source"] = "quest checker";
                    node["firstSeen"] ??= Now();
                    node["lastSeen"] = Now();
                    node["problems"] = new JsonArray([.. problems.Select(p => (JsonNode) JsonValue.Create(p))]);
                    node["summary"] = problems.FirstOrDefault() ?? "failed the quest checker";
                    node["questFile"] = TemplateJson(code);
                    entries[path] = node.ToJsonString(s_indented);
                }

                DropFoundMissing(entries);
            }

            QueueWrite();
        }
        catch (Exception e) {
            Logger.Warning("Quest reports: the quest checker's results could not be added: {0}", Logger.Args(e.Message));
        }
    }

    /// <summary>A wizard typed .stuck: a snapshot of each active quest's step.</summary>
    internal static void Stuck(Wizard wizard, IEnumerable<(string Quest, string Step, int Count, int Total)> steps, string zone,
        float x, float y, float z) {
        try {
            foreach (var (quest, step, count, total) in steps) {
                if (string.IsNullOrEmpty(quest)) {
                    continue;
                }

                Update($"stuck/{quest}.json", node => {
                    node["quest"] = quest;
                    node["kind"] = "stuck";
                    node["firstSeen"] ??= Now();
                    node["lastSeen"] = Now();
                    var reports = node["reports"] as JsonArray ?? [];
                    reports.Add(new JsonObject {
                        ["time"] = Now(),
                        ["character"] = wizard?.CharId.ToString(),
                        ["step"] = step,
                        ["progress"] = $"{count}/{total}",
                        ["zone"] = zone,
                        ["at"] = $"{x:0},{y:0},{z:0}",
                    });
                    while (reports.Count > MAX_STUCK_REPORTS) {
                        reports.RemoveAt(0);
                    }

                    node["reports"] = reports;
                    node["summary"] = $".stuck on step {step} ({count}/{total}) in {zone}, {reports.Count} time(s)";
                    node["questFile"] = TemplateJson(quest);
                });
            }
        }
        catch (Exception e) {
            Logger.Debug("Quest reports: could not note a .stuck: {0}", Logger.Args(e.Message));
        }
    }

    /// <summary>Quests loaded (again): codes that now have a file come out of missing/.</summary>
    internal static void QuestsLoaded() {
        try {
            lock (s_lock) {
                s_missingThisRun.Clear();
                if (DropFoundMissing(Entries()) == 0) {
                    return;
                }
            }

            QueueWrite();
        }
        catch (Exception e) {
            Logger.Debug("Quest reports: could not check missing quests: {0}", Logger.Args(e.Message));
        }
    }

    private static int DropFoundMissing(Dictionary<string, string> entries) {
        var found = entries.Keys.Where(k => k.StartsWith("missing/", StringComparison.Ordinal)
            && SpiralDB.QuestExists(Path.GetFileNameWithoutExtension(k))).ToList();
        foreach (var k in found) {
            entries.Remove(k);
        }

        return found.Count;
    }

    private static void Update(string path, Action<JsonObject> change) {
        lock (s_lock) {
            var entries = Entries();
            var node = entries.TryGetValue(path, out var text) ? JsonNode.Parse(text) as JsonObject ?? [] : [];
            change(node);
            entries[path] = node.ToJsonString(s_indented);
        }

        QueueWrite();
    }

    private static JsonNode TemplateJson(string code) {
        try {
            return QuestTemplateCollection.GetQuestByName(code) is { } quest
                ? JsonSerializer.SerializeToNode(quest, SpiralJsonOptions.Options)
                : null;
        }
        catch (Exception) {
            return null;
        }
    }

    // Asked for while running the game's own results or requirements (a trigger, an object, another quest), not by a
    // server rule guessing a name.
    private static bool FromGameData(string askedBy)
        => askedBy.Contains("Handler", StringComparison.Ordinal) || askedBy.Contains("Requirement", StringComparison.Ordinal)
            || askedBy.Contains("StartEncounter", StringComparison.Ordinal);

    // The first methods outside the quest lookup that asked: what to look at to see where the code comes from.
    private static string AskedBy() {
        var frames = new StackTrace(2, false).GetFrames() ?? [];
        var names = frames.Select(f => f.GetMethod())
            .Where(m => m?.DeclaringType is { } t && t != typeof(QuestReports) && t != typeof(QuestTemplateCollection)
                && t != typeof(SpiralDB) && t.Namespace?.StartsWith("Imlight", StringComparison.Ordinal) == true)
            .Select(m => Clean(m.DeclaringType) + "." + m.Name)
            .Distinct()
            .Take(3)
            .ToList();
        return string.Join(" < ", names);
    }

    private static string Clean(Type type) {
        var t = type;
        while (t.DeclaringType is not null && (t.Name.Contains('<') || t.Name.Contains("DisplayClass"))) {
            t = t.DeclaringType;
        }

        return t.Name;
    }

    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC";

    private static Dictionary<string, string> Entries() {
        if (s_entries is not null) {
            return s_entries;
        }

        s_entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try {
            if (File.Exists(ZipPath)) {
                using var zip = ZipFile.OpenRead(ZipPath);
                foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))) {
                    using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                    s_entries[entry.FullName] = reader.ReadToEnd();
                }
            }
        }
        catch (Exception e) {
            Logger.Warning("Quest reports: {0} could not be read back ({1}); it starts over.", Logger.Args(ZipPath, e.Message));
        }

        return s_entries;
    }

    // Writes run one at a time, a moment after the change, so a burst of reports writes the zip once.
    private static void QueueWrite() {
        if (Interlocked.Exchange(ref s_writeQueued, 1) == 1) {
            return;
        }

        _ = System.Threading.Tasks.Task.Delay(2000).ContinueWith(_ => {
            Interlocked.Exchange(ref s_writeQueued, 0);
            Write();
        });
    }

    private static void Write() {
        try {
            Dictionary<string, string> copy;
            lock (s_lock) {
                copy = new Dictionary<string, string>(Entries(), StringComparer.OrdinalIgnoreCase);
            }

            var tmp = ZipPath + ".tmp";
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create)) {
                var index = new StringBuilder();
                index.AppendLine($"Quest reports from this server, updated {Now()}.");
                foreach (var kind in new[] { "missing", "broken", "stuck" }) {
                    var mine = copy.Where(p => p.Key.StartsWith(kind + "/", StringComparison.Ordinal)).OrderBy(p => p.Key).ToList();
                    index.AppendLine();
                    index.AppendLine($"{kind}: {mine.Count}");
                    foreach (var (path, text) in mine) {
                        string summary;
                        try {
                            summary = JsonNode.Parse(text)?["summary"]?.GetValue<string>() ?? "";
                        }
                        catch (Exception) {
                            summary = "";
                        }

                        index.AppendLine($"  {Path.GetFileNameWithoutExtension(path)}: {summary}");
                    }
                }

                foreach (var (path, text) in copy.OrderBy(p => p.Key)) {
                    using var w = new StreamWriter(zip.CreateEntry(path, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                    w.Write(text);
                }

                using var iw = new StreamWriter(zip.CreateEntry("index.txt").Open(), new UTF8Encoding(false));
                iw.Write(index.ToString());
            }

            File.Move(tmp, ZipPath, overwrite: true);
        }
        catch (IOException e) {
            // The zip is open somewhere (being copied or sent): tried again shortly.
            Logger.Debug("Quest reports: {0} is in use ({1}); trying again.", Logger.Args(FILE_NAME, e.Message));
            _ = System.Threading.Tasks.Task.Delay(30000).ContinueWith(_ => QueueWrite());
        }
        catch (Exception e) {
            Logger.Warning("Quest reports: {0} could not be written: {1}", Logger.Args(FILE_NAME, e.Message));
        }
    }

}
