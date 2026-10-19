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
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Housing;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.World;

/// <summary>
/// The trainers standing in the trainer hall. Placing any object in the hall puts a trainer on that spot instead; the
/// spots are kept in trainer-hall.txt next to the server, one per line, and every wizard's copy of the hall shows them.
/// "Pick up all" in the hall clears them.
/// </summary>
internal static class TrainerHallTrainers {

    /// <summary>The zone tag of a trainer that stands in the trainer hall.</summary>
    internal const string ZONE_TAG = "TrainerHallTrainer";

    private const string SPOTS_FILE = "trainer-hall.txt";

    // The Deckathalon Tome, the book on a stand.
    private static readonly uint s_trainerTemplateId = ConfigurationManager.GetValue("TrainerHall.TrainerTemplateId", 1452184u);

    private static readonly object s_lock = new();

    internal readonly record struct Spot(uint TemplateId, float X, float Y, float Z, float Yaw);

    /// <summary>
    /// Checks whether a house is the trainer hall.
    /// </summary>
    internal static bool IsHall(HouseState house)
        => TrainerHallDoor.Enabled && house is not null && HouseTemplates.TryGet(TrainerHallDoor.HouseTemplateId, out var hall)
           && string.Equals(house.ZoneName, hall.LotZone, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Checks whether a zone is the trainer hall's zone.
    /// </summary>
    internal static bool IsHallZone(string zoneName)
        => TrainerHallDoor.Enabled && HouseTemplates.TryGet(TrainerHallDoor.HouseTemplateId, out var hall)
           && string.Equals(zoneName, hall.LotZone, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the trainer spots.
    /// </summary>
    internal static List<Spot> Load() {
        lock (s_lock) {
            if (!File.Exists(SPOTS_FILE)) {
                return [];
            }

            var spots = new List<Spot>();
            foreach (var line in File.ReadAllLines(SPOTS_FILE)) {
                var parts = line.Split('\t', StringSplitOptions.TrimEntries);
                if (line.StartsWith('#') || parts.Length < 5
                    || !uint.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var templateId)
                    || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                    || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                    || !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)
                    || !float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var yaw)) {
                    continue;
                }

                spots.Add(new Spot(templateId, x, y, z, yaw));
            }

            return spots;
        }
    }

    /// <summary>
    /// Adds a trainer on a spot.
    /// </summary>
    internal static Spot Add(float x, float y, float z, float yaw) {
        var spot = new Spot(s_trainerTemplateId, x, y, z, yaw);
        lock (s_lock) {
            if (!File.Exists(SPOTS_FILE)) {
                File.WriteAllText(SPOTS_FILE, "# Trainer hall trainers: template id, X, Y, Z, facing. Delete a line to remove that trainer.\n");
            }

            File.AppendAllText(SPOTS_FILE, string.Create(CultureInfo.InvariantCulture,
                $"{spot.TemplateId}\t{spot.X:0.###}\t{spot.Y:0.###}\t{spot.Z:0.###}\t{spot.Yaw:0.####}\n"));
        }

        return spot;
    }

    /// <summary>
    /// Removes every trainer spot.
    /// </summary>
    /// <returns>How many there were.</returns>
    internal static int Clear() {
        lock (s_lock) {
            var count = Load().Count;
            if (File.Exists(SPOTS_FILE)) {
                File.Delete(SPOTS_FILE);
            }

            return count;
        }
    }

    /// <summary>
    /// Builds the trainer that stands on a spot.
    /// </summary>
    internal static bool TryCreate(Spot spot, out CoreObjectInfo info, out CoreObject coreObject, out GameObjectTemplate template) {
        info = null;
        coreObject = null;
        template = CoreObjectFactory.GetCoreTemplate(spot.TemplateId) as GameObjectTemplate;
        if (template is null) {
            Logger.Warning("The trainer hall trainer template {0} was not found.", Logger.Args(spot.TemplateId));

            return false;
        }

        info = new CoreObjectInfo {
            m_templateID = spot.TemplateId,
            m_fScale = 1.0f,
            m_zoneTag = ZONE_TAG,
            m_location = new Vector3(spot.X, spot.Y, spot.Z),
            m_orientation = new Vector3(0, 0, spot.Yaw),
        };
        coreObject = CoreObjectFactory.FinalizeCoreObject(info, template);
        if (coreObject is null) {
            return false;
        }

        coreObject = CoreObjectFactory.InitializeCoreObjectBehaviors(coreObject, template);

        return coreObject is not null;
    }

}
