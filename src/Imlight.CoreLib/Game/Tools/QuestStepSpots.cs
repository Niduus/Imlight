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
using System.Linq;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Madlibs;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>
/// Where a quest step happens, as exactly as the game's files say: the spot the game's quest arrow points at (poi.dat),
/// else where the NPC of a "talk to" step stands in its zone, else the step's zone. The Manager's step buttons
/// (.tpstep) teleport there (Niduus, 2026-10-28: "This button will TP me to EXACTLY the place I need to TP to ... directly
/// to 'Place Gem in Celestial Bow' instead of the general area").
/// </summary>
internal static class QuestStepSpots {

    internal sealed record Spot(string Zone, string Location, string How);

    // Each zone's placed objects (template ID and spot), read once.
    private static readonly ConcurrentDictionary<string, (ulong Template, Imcodec.Math.Vector3 Spot)[]> s_zoneObjects
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The spot of this step (1 is the quest's first step), or null when not even its zone is known.</summary>
    internal static Spot Find(QuestTemplate quest, int step) {
        var goals = quest?.m_goals ?? [];
        if (step < 1 || step > goals.Count || goals[step - 1] is not { } goal) {
            return null;
        }

        foreach (var (zone, spot) in Services.QuestArrowGoals.GetSpots(quest, goal)) {
            if (TeleportZone(zone) is { Length: > 0 } known) {
                return new Spot(known, Location(spot), "where the game's quest arrow points");
            }
        }

        var stepZone = TeleportZone(goal is WaypointGoalTemplate { m_zoneTag.IsEmpty: false } waypoint
            ? (string) waypoint.m_zoneTag
            : (string) goal.m_destinationZone);

        if (goal is PersonaGoalTemplate persona && stepZone.Length > 0) {
            var npc = QuestMadlibs.SpeakerTemplateOf(persona.m_personaName, 0);
            var placed = ObjectsOf(stepZone).Where(o => o.Template == npc).Select(o => o.Spot).ToList();
            if (npc != 0 && placed.Count > 0) {
                return new Spot(stepZone, Location(placed[0]), "where that NPC stands");
            }
        }

        return stepZone.Length > 0 ? new Spot(stepZone, "Start", "the start of the step's zone") : null;
    }

    private static string TeleportZone(string zone)
        => string.IsNullOrWhiteSpace(zone) ? "" : AccessPassManager.GetContainedZoneName(zone.Trim()) ?? "";

    // A little to the side of the target, so you land next to an NPC or object rather than inside it.
    private static string Location(Imcodec.Math.Vector3 spot)
        => Util.GetCompactStringFromVector(new Imcodec.Math.Vector3(spot.X + 60, spot.Y, spot.Z + 10), new Imcodec.Math.Vector3(0, 0, 0));

    private static (ulong Template, Imcodec.Math.Vector3 Spot)[] ObjectsOf(string zone) => s_zoneObjects.GetOrAdd(zone, name => {
        try {
            if (!ResourceManager.TryLoadArchive(name, out var wad) || ResourceManager.OpenZoneData(wad)?.ToArray() is not { } data
                || !new BindSerializer().Deserialize<WizZoneData>(data, 1, out var zoneData)) {
                return [];
            }

            return [.. (zoneData?.m_objectList ?? []).Where(o => o is not null).Select(o => ((ulong) o.m_templateID, o.m_location))];
        }
        catch (Exception ex) {
            Logger.Debug("Quest step spots: zone {0} could not be read: {1}", Logger.Args(name, ex.Message));

            return [];
        }
    });

}
