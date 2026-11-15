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
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// Quest people, for Ultimus and the Quest Team (Niduus, 2026-11-15, "all" of the log check's ideas):
/// <list type="bullet">
/// <item>A person a step names may stand in an area under another of the game's objects than the one the quest names
/// (an area's own copy of them). Matched by their name, as a player knows them.</item>
/// <item>A person a step names who is not where the quest says is written to logs/bots/people-missing.txt (the area the
/// quest says, where they were looked for, and where they usually stand), for the Ultimus Issues tab in Ahziael.</item>
/// </list>
/// </summary>
internal static class BotPeople {

    /// <summary>The nearest NPC here with the same name as the quest's person (template <paramref name="npc"/>), or null.</summary>
    internal static MinimapIndex.Mark ByName(IEnumerable<MinimapIndex.Mark> live, ulong npc, float x, float y, string stepKey = null) {
        if (npc == 0 || live is null || CoreObjectFactory.GetCoreTemplate(npc) is not GameObjectTemplate template
            || MinimapIndex.NameOf(template) is not { Length: > 2 } name) {
            return null;
        }

        return live.Where(m => m.Kind == MinimapIndex.NPC && m.Template != npc && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)
                               && !IsNotThisOne(stepKey, m.Object.m_globalID.Full))
            .OrderBy(m => MathF.Abs(m.Object.m_location.X - x) + MathF.Abs(m.Object.m_location.Y - y)).FirstOrDefault();
    }

    // Same-name people who offered nothing for a step when stood next to ("step key|object" -> when). A person of the same
    // name is not always the one a step means (his log, 2026-11-15: "Purloin the Plans" asks for another Baxter than the one
    // at the Ironworks gate, and he stood by the gate's Baxter for minutes). Kept 20 minutes.
    private static readonly ConcurrentDictionary<string, DateTime> s_notThisOne = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan s_notThisOneFor = TimeSpan.FromMinutes(20);

    /// <summary>This same-name person offered nothing for the step: not the one it means (for a while).</summary>
    internal static void NotThisOne(string stepKey, ulong globalId) {
        if (!string.IsNullOrEmpty(stepKey) && globalId != 0) {
            s_notThisOne[$"{stepKey}#{globalId}"] = DateTime.UtcNow;
        }
    }

    private static readonly ConcurrentDictionary<string, DateTime> s_noOfferSince = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Stood next to a "talk to" person who offers nothing for the step: when it is a same-name copy (not the step's own
    /// person) and it has offered nothing for 6 seconds, it is written down as not the one (true once, when written).
    /// The step is then looked for where the right person stands (the planners), as a player who reads the name tag does.
    /// </summary>
    internal static bool OfferedNothing(Akka.Actor.IActorRef zone, string stepKey, ulong template, ulong globalId, int who) {
        if (zone is null || string.IsNullOrEmpty(stepKey) || template == 0 || globalId == 0
            || MinimapIndex.In(zone).FirstOrDefault(m => m.Object.m_globalID.Full == globalId) is not { } mark || mark.Template == template) {
            return false;
        }

        var seen = $"{stepKey}#{globalId}#{who}";
        var first = s_noOfferSince.GetOrAdd(seen, DateTime.UtcNow);
        if (DateTime.UtcNow - first < TimeSpan.FromSeconds(6)) {
            return false;
        }

        if (DateTime.UtcNow - first > TimeSpan.FromMinutes(2)) {
            // An old look (he walked off and came back): it starts again.
            s_noOfferSince[seen] = DateTime.UtcNow;
            return false;
        }

        s_noOfferSince.TryRemove(seen, out _);
        NotThisOne(stepKey, globalId);
        return true;
    }

    internal static bool IsNotThisOne(string stepKey, ulong globalId)
        => !string.IsNullOrEmpty(stepKey) && s_notThisOne.TryGetValue($"{stepKey}#{globalId}", out var at) && DateTime.UtcNow - at < s_notThisOneFor;

    /// <summary>A same-name person here was found not to be this step's person lately: the right one is looked for elsewhere.</summary>
    internal static bool HadWrongCopy(string stepKey)
        => !string.IsNullOrEmpty(stepKey) && s_notThisOne.Any(e => e.Key.StartsWith(stepKey + "#", StringComparison.OrdinalIgnoreCase)
                                                                  && DateTime.UtcNow - e.Value < s_notThisOneFor);

    internal sealed record Missing(string Who, string Quest, string Step, string Zone, string UsuallyIn, DateTime At);

    private static readonly ConcurrentDictionary<string, Missing> s_missing = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime s_written = DateTime.MinValue;

    // Steps whose person was looked for in an area and not there: "quest|goal|area" -> the area, and when (an hour counts).
    private static readonly ConcurrentDictionary<string, (string Zone, DateTime At)> s_absent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether this step's person was looked for in <paramref name="zone"/> within the last hour and was not there.</summary>
    internal static bool Absent(string stepKey, string zone)
        => !string.IsNullOrEmpty(stepKey) && s_absent.Any(a => a.Key.StartsWith(stepKey + "|", StringComparison.OrdinalIgnoreCase)
                                                               && DateTime.UtcNow - a.Value.At < TimeSpan.FromHours(1)
                                                               && (OverlayQuestTracker.SameZone(a.Value.Zone, zone) || OverlayQuestTracker.SameZone(zone, a.Value.Zone)));

    /// <summary>A step's person was not in the area the step names: written down (once per quest step and area).</summary>
    internal static void NotThere(string who, string quest, string step, string zone, string stepKey, string usuallyIn) {
        if (string.IsNullOrEmpty(who) || string.IsNullOrEmpty(zone)) {
            return;
        }

        if (!string.IsNullOrEmpty(stepKey)) {
            s_absent[stepKey + "|" + zone] = (zone, DateTime.UtcNow);
        }

        var key = $"{quest}|{step}|{zone}";
        var isNew = !s_missing.ContainsKey(key);
        s_missing[key] = new Missing(who, quest ?? "", step ?? "", zone, usuallyIn ?? "", DateTime.Now);
        if (isNew || DateTime.UtcNow - s_written > TimeSpan.FromMinutes(5)) {
            Write();
        }
    }

    private static void Write() {
        s_written = DateTime.UtcNow;
        try {
            var text = new StringBuilder();
            text.AppendLine($"QUEST PEOPLE NOT WHERE THE QUEST SAYS (written by Ultimus and the Quest Team, {DateTime.Now:yyyy-MM-dd HH:mm})");
            text.AppendLine("Each line: who, the quest and its step, where the quest says they are, and where they usually stand.");
            foreach (var m in s_missing.Values.OrderByDescending(m => m.At)) {
                text.AppendLine();
                text.AppendLine(m.Zone);
                text.AppendLine($"    {m.Who}: \"{m.Step}\" of {m.Quest}, not found here" +
                                (m.UsuallyIn.Length > 0 ? $"; usually stands in {ZoneNames.Shown(m.UsuallyIn)}" : "") + $" (last seen missing {m.At:d MMM HH:mm})");
            }

            var folder = Path.GetFullPath("logs/bots");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "people-missing.txt"), text.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Logger.Debug("Bots: people-missing.txt could not be written: {0}", Logger.Args(e.Message));
        }
    }

}
