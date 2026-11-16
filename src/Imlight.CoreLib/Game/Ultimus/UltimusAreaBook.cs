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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// The area book: what an area's own files say about getting round it, laid out for Ultimus, the Quest Team and the Bot
/// Players (Niduus, 2026-11-15: "World mapping did help our boy out. Any more to add?"; 2026-11-16: "Add more to this
/// update around Ultimus"). Only what the files or the bots' own moves show, nothing guessed:
/// every teleporter with where it lands (or that its landing is not known yet), the gates and doors that open later and
/// what opens them (a creature beaten, a spot stepped on, a lever's state, a quest step), the lifts and moving floors and
/// their stops, and the grounds (floors cut off by walls or water) with how many teleporters lead from one to another.
/// Written to logs/ultimus/maps/&lt;area&gt;-book.txt, and its facts near the wizard go into the stuck thinking.
/// </summary>
internal static class UltimusAreaBook {

    /// <summary>A gate, door or moving part that a trigger changes, and what makes it change.</summary>
    /// <param name="Object">The object's name in the area's files.</param>
    /// <param name="State">The state the trigger puts it in (OpenIdle, Raise2, ...).</param>
    /// <param name="By">What does it, in plain words.</param>
    /// <param name="Lift">A lift or moving floor (else a gate, door or barrier).</param>
    /// <param name="X">Where it stands, when the files say (else NaN).</param>
    internal sealed record Change(string Object, string State, string By, bool Lift, float X, float Y, float Z) {
        public bool HasSpot => !float.IsNaN(X);
    }

    private static readonly ConcurrentDictionary<string, List<Change>> s_changes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The gates, doors and lifts of the area that open or move later, and what does it.</summary>
    internal static List<Change> ChangesIn(string zonePath) {
        if (string.IsNullOrEmpty(zonePath)) {
            return [];
        }

        return s_changes.GetOrAdd(zonePath, path => {
            try {
                if (!ResourceManager.TryLoadArchive(path, out var wad)) {
                    return [];
                }

                return Changes(UltimusPads.Read<WizZoneTriggers>(wad.OpenFile("triggers.xml")?.ToArray()),
                    UltimusPads.Read<WizZoneVolumes>(wad.OpenFile("volumes.xml")?.ToArray()),
                    UltimusPads.Read<WizZoneData>(ResourceManager.OpenZoneData(wad)?.ToArray()));
            }
            catch (Exception ex) {
                Logger.Debug("Ultimus: the gates of {0} could not be read: {1}", Logger.Args(path, ex.Message));
                return [];
            }
        });
    }

    /// <summary>
    /// From the area's triggers: each trigger that sets an object to an open state (a gate or door) or a lift state, with
    /// what fires it and what it needs.
    /// </summary>
    internal static List<Change> Changes(WizZoneTriggers triggerFile, WizZoneVolumes volumeFile, WizZoneData zoneData) {
        var triggers = triggerFile?.m_triggers?.Where(t => t is not null).ToList() ?? [];
        var volumes = volumeFile?.m_volumes?.Where(v => v is not null).ToList() ?? [];
        var found = new List<Change>();
        foreach (var trigger in triggers) {
            var sets = trigger.m_results?.m_results?.OfType<ResModifyTriggerObject>().Where(r => r is not null).ToList() ?? [];
            foreach (var set in sets) {
                string obj = set.m_triggerObjName;
                string state = set.m_triggerObjState;
                if (string.IsNullOrEmpty(obj) || string.IsNullOrEmpty(state)) {
                    continue;
                }

                var lift = OverlayQuestPath.IsLiftState(state);
                if (!lift && !Opens(state)) {
                    continue;
                }

                var (x, y, z) = SpotOf(obj, triggers, zoneData);
                found.Add(new Change(obj, state, ByWords(trigger, volumes, triggers), lift, x, y, z));
            }
        }

        return [.. found.DistinctBy(c => (c.Object.ToLowerInvariant(), c.State.ToLowerInvariant(), c.By))];
    }

    // A gate or door that opens (Open, OpenIdle, Opening, Unlock, Lowered barrier ...).
    private static bool Opens(string state)
        => state.Contains("Open", StringComparison.OrdinalIgnoreCase) || state.Contains("Unlock", StringComparison.OrdinalIgnoreCase);

    // Where the object stands: a trigger's own object, else the area's object list (by its tag). NaN when the files don't say.
    private static (float X, float Y, float Z) SpotOf(string obj, List<Trigger> triggers, WizZoneData zoneData) {
        foreach (var trigger in triggers) {
            if (trigger.m_triggerObjInfo is { } info && info.m_templateID != 0
                && (string.Equals(info.m_zoneTag, obj, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(info.m_triggerObjName, obj, StringComparison.OrdinalIgnoreCase))) {
                return (info.m_location.X, info.m_location.Y, info.m_location.Z);
            }
        }

        var listed = zoneData?.m_objectList?.FirstOrDefault(o => o is not null && string.Equals(o.m_zoneTag, obj, StringComparison.OrdinalIgnoreCase));

        return listed is null ? (float.NaN, float.NaN, float.NaN) : (listed.m_location.X, listed.m_location.Y, listed.m_location.Z);
    }

    // What fires the trigger and what it needs, in plain words.
    private static string ByWords(Trigger trigger, List<Volume> volumes, List<Trigger> triggers) {
        var parts = new List<string>();
        var needs = trigger.m_requirements?.m_requirements?.Where(r => r is not null).ToList() ?? [];
        foreach (var fire in trigger.m_fireEvents?.Select(e => e.ToString()).Where(e => !string.IsNullOrEmpty(e)) ?? []) {
            if (string.Equals(fire, "Monster_Killed", StringComparison.OrdinalIgnoreCase)) {
                var marks = needs.OfType<ReqMonsterKilled>().SelectMany(r => (r.m_monsterAdjectives ?? []).Select(a => a.ToString()))
                    .Select(Creature).Where(a => a.Length > 0).Distinct().ToList();
                parts.Add(marks.Count > 0 ? $"beating {string.Join(" or ", marks)}" : "beating a creature here");
                continue;
            }

            var volume = volumes.FirstOrDefault(v => v.m_enterEvents?.Any(e => (string) e == fire) == true || "Enter_" + (string) v.m_volumeName == fire);
            if (volume is not null) {
                parts.Add($"stepping on \"{(string) volume.m_volumeName}\" at ({volume.m_locationX:0}, {volume.m_locationY:0})");
                continue;
            }

            // "Lever.Down.EnterState": another object reaching a state (a lever pulled, a button pressed).
            var bits = fire.Split('.');
            if (bits.Length == 3 && bits[2].Equals("EnterState", StringComparison.OrdinalIgnoreCase)) {
                parts.Add($"\"{bits[0]}\" turning to {bits[1]}");
                continue;
            }

            // Another trigger's event, said by its name.
            parts.Add($"the event \"{fire}\"");
        }

        foreach (var need in needs) {
            switch (need) {
                case ReqTriggerObjectState state:
                    parts.Add($"while \"{(string) state.m_triggerObjName}\" is {(string) state.m_triggerObjState}");
                    break;
                case ReqHasEntry entry when !string.IsNullOrEmpty(entry.m_entryName):
                    parts.Add($"once {(entry.m_applyNOT ? "without" : "with")} the game's note \"{(string) entry.m_entryName}\"");
                    break;
                case ReqHasBadge:
                    parts.Add("with a badge");
                    break;
            }
        }

        return parts.Count == 0 ? "something in the area" : string.Join(", ", parts);
    }

    // "Golem-Steel-Elite-L39.AdjRef" to "a Golem Steel Elite (level 39)".
    private static string Creature(string adjective) {
        var name = (adjective ?? "").Replace(".AdjRef", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (name.Length == 0) {
            return "";
        }

        var bits = name.Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
        var level = bits.Count > 0 && bits[^1].Length > 1 && bits[^1][0] is 'L' or 'l' && int.TryParse(bits[^1][1..], out var l) ? l : 0;
        if (level > 0) {
            bits.RemoveAt(bits.Count - 1);
        }

        return $"a {string.Join(' ', bits)}" + (level > 0 ? $" (level {level})" : "");
    }

    // ---- The book, written out, and its facts for thinking. ----

    /// <summary>The area book as text: grounds, teleporters (with landings or not known yet), gates and lifts.</summary>
    internal static string Text(string zonePath) {
        var text = new StringBuilder();
        text.AppendLine($"AREA BOOK of {zonePath} ({DateTime.Now:yyyy-MM-dd HH:mm}): what the area's own files and the bots' own moves show.");
        text.AppendLine("A ground is floor walls or water cut off from the rest: only a teleporter, lift or door joins grounds.");
        text.AppendLine();
        var pads = UltimusPads.In(zonePath);
        text.AppendLine($"TELEPORTERS whose landing is known ({pads.Count}):");
        foreach (var pad in pads.OrderBy(p => OverlayQuestPath.IslandOf(zonePath, p.X, p.Y)).ThenBy(p => p.X)) {
            text.AppendLine($"  ground {Ground(zonePath, pad.X, pad.Y)} ({pad.X:0}, {pad.Y:0}) lands on ground {Ground(zonePath, pad.ToX, pad.ToY)} " +
                            $"({pad.ToX:0}, {pad.ToY:0}){(pad.Learned ? ", learned by walking into it" : pad.Click != 0 ? ", a door or stone to press" : ", from the files")}");
        }

        var untried = UltimusPads.UntriedIn(zonePath);
        text.AppendLine();
        text.AppendLine($"TELEPORTERS whose landing is not known yet ({untried.Count}; the game's copy of the area leaves it out, a bot learns it by walking in):");
        foreach (var u in untried.OrderBy(u => OverlayQuestPath.IslandOf(zonePath, u.X, u.Y)).ThenBy(u => u.X)) {
            text.AppendLine($"  ground {Ground(zonePath, u.X, u.Y)} ({u.X:0}, {u.Y:0}) \"{u.Name}\"");
        }

        var changes = ChangesIn(zonePath);
        text.AppendLine();
        text.AppendLine($"GATES AND DOORS that open later ({changes.Count(c => !c.Lift)}):");
        foreach (var c in changes.Where(c => !c.Lift)) {
            text.AppendLine($"  \"{c.Object}\" {(c.HasSpot ? $"at ({c.X:0}, {c.Y:0}, height {c.Z:0}) on ground {Ground(zonePath, c.X, c.Y)}" : "(its spot is not in the area's files)")} " +
                            $"goes {c.State} on {c.By}");
        }

        text.AppendLine();
        text.AppendLine($"LIFTS AND MOVING FLOORS ({changes.Count(c => c.Lift)} stops):");
        foreach (var group in changes.Where(c => c.Lift).GroupBy(c => c.Object, StringComparer.OrdinalIgnoreCase)) {
            var first = group.First();
            text.AppendLine($"  \"{group.Key}\" {(first.HasSpot ? $"at ({first.X:0}, {first.Y:0}, height {first.Z:0})" : "(its spot is not in the area's files)")}:");
            foreach (var stop in group) {
                text.AppendLine($"    {stop.State} on {stop.By}");
            }
        }

        return text.ToString();
    }

    private static string Ground(string zonePath, float x, float y) {
        var island = OverlayQuestPath.IslandOf(zonePath, x, y);
        return island < 0 ? "?" : island.ToString();
    }

    /// <summary>Writes the book to logs/ultimus/maps/&lt;area&gt;-book.txt (again whenever a bot learns a teleporter there).</summary>
    internal static void Save(string zonePath) {
        if (string.IsNullOrEmpty(zonePath)) {
            return;
        }

        try {
            var folder = Path.Combine(UltimusWire.Folder, "maps");
            Directory.CreateDirectory(folder);
            var name = new string(zonePath.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
            File.WriteAllText(Path.Combine(folder, $"{name}-book.txt"), Text(zonePath));
        }
        catch (Exception) {
            // Only a help for reading what he knows.
        }
    }

    /// <summary>
    /// The book's facts about getting from (x, y) to a step at (toX, toY), for a stuck bot's thinking: which ground each is
    /// on and how many known teleporters join them, the unknown teleporters he can reach, and the gates and lifts near
    /// him with what opens or moves them. Empty when the area's files say nothing.
    /// </summary>
    internal static List<string> Facts(string zonePath, float x, float y, float toX, float toY) {
        var facts = new List<string>();
        try {
            var reach = UltimusPads.Reach(zonePath, x, y);
            var (here, there) = (OverlayQuestPath.IslandOf(zonePath, x, y), OverlayQuestPath.IslandOf(zonePath, toX, toY));
            if (here >= 0 && there >= 0) {
                facts.Add(here == there
                    ? "Area book: the step is on the same ground as him (he can walk there)."
                    : reach.TryGetValue(there, out var hop)
                        ? $"Area book: the step is on another ground; known teleporters lead there in {hop.Hops} teleporter(s)."
                        : "Area book: the step is on another ground and no known teleporter leads there yet.");
            }

            var untried = UltimusPads.UntriedIn(zonePath).Where(u => reach.ContainsKey(OverlayQuestPath.IslandOf(zonePath, u.X, u.Y))).ToList();
            if (untried.Count > 0) {
                facts.Add($"Area book: {untried.Count} teleporter(s) he can reach have a landing nobody knows yet; walking into one shows where it goes.");
            }

            foreach (var c in ChangesIn(zonePath).Where(c => c.HasSpot && UltimusPlanner.Far(x, y, c.X, c.Y) < NEAR).Take(4)) {
                facts.Add($"Area book: {(c.Lift ? "a lift" : "a gate or door")} \"{c.Object}\" {UltimusPlanner.Far(x, y, c.X, c.Y):0} away goes {c.State} on {c.By}.");
            }
        }
        catch (Exception) {
            // Only a help for thinking.
        }

        return facts;
    }

    /// <summary>
    /// The nearest gate or door within <paramref name="most"/> of (x, y) that opens on beating a creature (Big Ben: each
    /// floor's door by the teleporter up opens once the floor's elite is beaten), or null.
    /// </summary>
    internal static Change OpensOnFight(string zonePath, float x, float y, float most) {
        try {
            return ChangesIn(zonePath).Where(c => !c.Lift && c.HasSpot && c.By.StartsWith("beating", StringComparison.Ordinal))
                .Select(c => (C: c, Far: UltimusPlanner.Far(x, y, c.X, c.Y)))
                .Where(c => c.Far <= most)
                .OrderBy(c => c.Far)
                .Select(c => c.C)
                .FirstOrDefault();
        }
        catch (Exception) {
            return null;
        }
    }

    // How near a gate or lift is told when stuck.
    private const float NEAR = 2500f;

}
