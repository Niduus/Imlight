/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// Ways that open only for a quest (Niduus, 2026-11-15: "make sure all situations like this are also future-proof for
/// later worlds/quests/scenarios"). The Temple of Storms' teleporter stayed shut until its own quest's tests were done, and
/// the Tomb of Storms' way on until its three obelisks were lit; the general lesson is "a way that won't take me opens with
/// a quest I hold: do that quest first". Two sources, by situation, not by spot:
/// <list type="bullet">
/// <item>The game's own refusal: a door or teleporter that refuses a wizard is written down with the quests its needs name
/// (ZoneTriggerSupervisor), so Ultimus, the Quest Team and Bot Players learn it the moment it happens, anywhere.</item>
/// <item>Read ahead from the game's zone files: every story area's doors and teleporters whose needs name a quest, in
/// every story world, written to logs/bots/gates-ahead.txt so the bots know them before they get there.</item>
/// </list>
/// </summary>
internal static class BotGates {

    internal sealed record Gate(string Zone, string Trigger, string To, IReadOnlyList<string> Quests);

    // Refusals per wizard: zone -> (when, quests named).
    private static readonly ConcurrentDictionary<ulong, (string Zone, string To, IReadOnlyList<string> Quests, DateTime At)> s_refused = new();
    private static readonly TimeSpan REFUSAL_COUNTS = TimeSpan.FromMinutes(15);

    // Read ahead: zone -> its gated ways.
    private static volatile Dictionary<string, List<Gate>> s_ahead;
    private static int s_reading;

    /// <summary>The quests whose steps a door's needs name (not "must not have"), with the game's names for rebuilt quests resolved.</summary>
    internal static List<string> QuestsNamed(RequirementList list) {
        var names = new List<string>();
        Walk(list, names);

        return [.. names.Distinct(StringComparer.OrdinalIgnoreCase)];

        static void Walk(RequirementList list, List<string> names) {
            if (list?.m_requirements is null || list.m_applyNOT) {
                return;
            }

            foreach (var requirement in list.m_requirements) {
                switch (requirement) {
                    case RequirementList nested:
                        Walk(nested, names);
                        break;
                    // Only needs a quest's progress can meet: a step done, or a step reached. "Has the quest" is met by
                    // holding it, so a quest held is never the reason such a way stays shut.
                    case ReqHasGoal { m_applyNOT: false } goal when !string.IsNullOrEmpty(goal.m_questName)
                                                                  && goal.m_requiredStatus != GoalStatusRequirement.DontCare:
                        names.Add(QuestCodeAliases.Resolve(goal.m_questName));
                        break;
                }
            }
        }
    }

    /// <summary>A door or teleporter refused this wizard (its needs not met): written down with the quests it names.</summary>
    internal static void Refused(Wizard wizard, string zone, Trigger trigger) {
        if (wizard is null || string.IsNullOrEmpty(zone) || trigger is null) {
            return;
        }

        var quests = QuestsNamed(trigger.m_requirements);
        if (quests.Count == 0) {
            return;
        }

        var to = trigger.m_results?.m_results?.OfType<ResTeleport>().Select(t => (string) t.m_destinationZone).FirstOrDefault(z => !string.IsNullOrEmpty(z)) ?? "";
        s_refused[wizard.CharId] = (zone, to, quests, DateTime.UtcNow);
    }

    /// <summary>
    /// The quest that opens a way in this area which this wizard needs now: one it holds (not <paramref name="main"/>) that a
    /// way here names, when that way refused it a moment ago or leads where its main step is (<paramref name="mainTo"/>).
    /// Null when there is none. <paramref name="why"/>: in plain words.
    /// </summary>
    internal static string OpensTheWay(Wizard wizard, string zone, string main, string mainTo, out string why) {
        why = "";
        if (wizard?.QuestBehavior?.CurrentQuestInstances is not { } held || string.IsNullOrEmpty(zone)) {
            return null;
        }

        var holds = held.Where(q => q is not null && !string.Equals(q.QuestName, main, StringComparison.OrdinalIgnoreCase))
            .Select(q => q.QuestName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (holds.Count == 0) {
            return null;
        }

        if (s_refused.TryGetValue(wizard.CharId, out var refused) && DateTime.UtcNow - refused.At < REFUSAL_COUNTS
            && string.Equals(refused.Zone, zone, StringComparison.OrdinalIgnoreCase)
            && refused.Quests.FirstOrDefault(holds.Contains) is { } opener) {
            why = "the way here would not take me, and the game ties it to that quest";
            return opener;
        }

        if (string.IsNullOrEmpty(mainTo) || Ahead() is not { } ahead || !ahead.TryGetValue(zone, out var gates)) {
            return null;
        }

        foreach (var gate in gates.Where(g => string.Equals(g.To, mainTo, StringComparison.OrdinalIgnoreCase))) {
            if (gate.Quests.FirstOrDefault(holds.Contains) is { } named) {
                why = $"the way from here to {mainTo} opens with that quest (read ahead from the game's files)";
                return named;
            }
        }

        return null;
    }

    /// <summary>The gated ways read ahead (null until read; the first call starts the read in the background).</summary>
    internal static Dictionary<string, List<Gate>> Ahead() {
        if (s_ahead is null && System.Threading.Interlocked.Exchange(ref s_reading, 1) == 0) {
            _ = Task.Run(ReadAhead);
        }

        return s_ahead;
    }

    /// <summary>How many gated ways are known ahead in a world's areas (for the read-ahead note), and a few examples.</summary>
    internal static (int Count, List<string> Examples) InWorld(string worldFolder) {
        if (Ahead() is not { } ahead || string.IsNullOrEmpty(worldFolder)) {
            return (0, []);
        }

        var gates = ahead.Where(z => z.Key.StartsWith(worldFolder + "/", StringComparison.OrdinalIgnoreCase)).SelectMany(z => z.Value).ToList();

        return (gates.Count, [.. gates.Take(3).Select(g => $"{ZoneNames.Shown(g.Zone)} to {ZoneNames.Shown(g.To)} opens with {string.Join(" or ", g.Quests.Take(2))}")]);
    }

    private static void ReadAhead() {
        var found = new Dictionary<string, List<Gate>>(StringComparer.OrdinalIgnoreCase);
        try {
            var zones = WizardData.SpiralDB.GetAllZoneData().Select(z => z?.ZoneName).Where(z => !string.IsNullOrWhiteSpace(z) && OverlayQuestRoute.OnStory(z))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var zone in zones) {
                try {
                    if (!ResourceManager.TryLoadArchive(zone, out var wad)) {
                        continue;
                    }

                    var bytes = wad.OpenFile("triggers.xml")?.ToArray();
                    var triggers = bytes is null ? [] : UltimusRead<WizZoneTriggers>(bytes)?.m_triggers?.Where(t => t is not null).ToList() ?? [];
                    foreach (var trigger in triggers) {
                        var teleport = trigger.m_results?.m_results?.OfType<ResTeleport>().FirstOrDefault();
                        if (teleport is null) {
                            continue;
                        }

                        var quests = QuestsNamed(trigger.m_requirements);
                        if (quests.Count == 0) {
                            continue;
                        }

                        string to = teleport.m_destinationZone;
                        if (!found.TryGetValue(zone, out var list)) {
                            found[zone] = list = [];
                        }

                        list.Add(new Gate(zone, trigger.m_triggerName, string.IsNullOrWhiteSpace(to) ? zone : to.Trim(), quests));
                    }
                }
                catch (Exception e) {
                    Logger.Debug("Bots: the ways of {0} could not be read ahead: {1}", Logger.Args(zone, e.Message));
                }
            }

            s_ahead = found;
            Write(found);
            Logger.Information("Bots: read ahead {0} way(s) that open with a quest, in {1} story areas (logs/bots/gates-ahead.txt).",
                Logger.Args(found.Sum(z => z.Value.Count), found.Count));
        }
        catch (Exception e) {
            Logger.Debug("Bots: the gated ways could not be read ahead: {0}", Logger.Args(e.Message));
            s_ahead = found;
        }
    }

    private static T UltimusRead<T>(byte[] data) where T : PropertyClass
        => data is null ? null : new BindSerializer().Deserialize<T>(data, 1, out var result) ? result : null;

    private static void Write(Dictionary<string, List<Gate>> found) {
        try {
            var text = new StringBuilder();
            text.AppendLine($"WAYS THAT OPEN WITH A QUEST (read ahead from the game's zone files, {DateTime.Now:yyyy-MM-dd HH:mm})");
            text.AppendLine("A bot whose way on is one of these does the named quest first when it holds it (a dungeon's own quest, a test, a key step).");
            foreach (var (zone, gates) in found.OrderBy(z => z.Key)) {
                text.AppendLine();
                text.AppendLine(zone);
                foreach (var gate in gates) {
                    text.AppendLine($"    to {gate.To}: opens with {string.Join(", ", gate.Quests)}");
                }
            }

            var folder = Path.GetFullPath("logs/bots");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "gates-ahead.txt"), text.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Logger.Debug("Bots: gates-ahead.txt could not be written: {0}", Logger.Args(e.Message));
        }
    }

}
