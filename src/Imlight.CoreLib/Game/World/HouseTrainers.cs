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

using Akka.Actor;
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Housing;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.World;

/// <summary>
/// The trainer tome: a piece of furniture (the Burrower Lectern, sold by Abner) that brings the Deckathalon Tome
/// trainer with it. Wherever one is placed in a house the trainer stands on it; moving or picking up the furniture
/// moves or removes the trainer. Each trainer is named after its furniture's GIDID, so it can be found again.
/// </summary>
internal static class HouseTrainers {

    private const string TAG_PREFIX = "HouseTrainer_";

    private static readonly uint s_itemTemplateId = ConfigurationManager.GetValue("TrainerHall.TrainerItemId", 1292128u);
    private static readonly uint s_trainerTemplateId = ConfigurationManager.GetValue("TrainerHall.TrainerTemplateId", 1452184u);

    // [TrainerHall] TrainerName: the name shown over the trainer's head; empty keeps the game's own name.
    private static readonly string s_trainerName = ConfigurationManager.GetValue("TrainerHall.TrainerName", "")?.Trim() ?? "";

    /// <summary>
    /// The overhead name for an object with this zone tag: its set trainer part ("Fire Trainer 2"), else the
    /// TrainerName setting; null when it is not a house trainer or keeps its own.
    /// </summary>
    internal static string NameFor(string zoneTag)
        => zoneTag?.StartsWith(TAG_PREFIX, System.StringComparison.Ordinal) != true ? null
            : HouseTrainerSets.SetFor(zoneTag)?.Name ?? (s_trainerName.Length > 0 ? s_trainerName : null);

    /// <summary>Whether an object with this zone tag is a house trainer (a trainer tome brought by its furniture).</summary>
    internal static bool IsHouseTrainer(string zoneTag) => zoneTag?.StartsWith(TAG_PREFIX, System.StringComparison.Ordinal) == true;

    /// <summary>The furniture that brings a trainer.</summary>
    internal static uint ItemTemplateId => s_itemTemplateId;

    internal static bool IsTrainerItem(uint templateId) => s_itemTemplateId != 0 && templateId == s_itemTemplateId;

    /// <summary>
    /// Removes the trainer spots that 0.9.57 kept for the trainer hall; trainers now come with their furniture.
    /// </summary>
    internal static void ForgetOldSpots() {
        try {
            System.IO.File.Delete("trainer-hall.txt");
        }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.UnauthorizedAccessException) {
        }
    }

    private static string Tag(uint gidId) => TAG_PREFIX + gidId;

    /// <summary>
    /// Puts the trainer on a placed trainer tome. A trainer already standing there is kept.
    /// </summary>
    internal static void Spawn(IActorRef zone, HouseState house, uint gidId, PlacedItem item) {
        if (zone is null || item is null || !IsTrainerItem(item.TemplateId)) {
            return;
        }

        HouseTrainerSets.Assign(house, Tag);

        zone.Tell(new SERVICE_101_PROTOCOL.MSG_SPAWNHOUSETRAINER {
            Tag = Tag(gidId), X = item.X, Y = item.Y, Z = item.Z, Yaw = item.Yaw,
        }, ActorRefs.NoSender);
    }

    /// <summary>
    /// Takes away the trainer of a trainer tome that was moved or picked up.
    /// </summary>
    internal static void Remove(IActorRef zone, uint gidId)
        => zone?.Tell(new ZONE_102_PROTOCOL.MSG_REMOVETRIGGEROBJECT { ObjectName = Tag(gidId) }, ActorRefs.NoSender);

    /// <summary>
    /// Puts a trainer on every trainer tome of a house.
    /// </summary>
    internal static void SpawnAll(IActorRef zone, HouseState house) {
        var snapshot = house.Snapshot();
        HouseTrainerSets.Assign(house, Tag);
        for (var slot = 0; slot < snapshot.Slots.Count; slot++) {
            if (snapshot.Slots[slot] is { } item && IsTrainerItem(item.TemplateId)) {
                zone?.Tell(new SERVICE_101_PROTOCOL.MSG_SPAWNHOUSETRAINER {
                    Tag = Tag(unchecked(snapshot.GidBase + (uint) slot)), X = item.X, Y = item.Y, Z = item.Z, Yaw = item.Yaw,
                }, ActorRefs.NoSender);
            }
        }
    }

    /// <summary>
    /// Builds the trainer for a spawn request.
    /// </summary>
    internal static bool TryCreate(SERVICE_101_PROTOCOL.MSG_SPAWNHOUSETRAINER spawn, out CoreObjectInfo info, out CoreObject coreObject,
                                   out GameObjectTemplate template) {
        info = null;
        coreObject = null;
        template = CoreObjectFactory.GetCoreTemplate(s_trainerTemplateId) as GameObjectTemplate;
        if (template is null) {
            Logger.Warning("The trainer tome's trainer template {0} was not found.", Logger.Args(s_trainerTemplateId));

            return false;
        }

        info = new CoreObjectInfo {
            m_templateID = s_trainerTemplateId,
            m_fScale = 1.0f,
            m_zoneTag = spawn.Tag,
            m_location = new Vector3(spawn.X, spawn.Y, spawn.Z),
            m_orientation = new Vector3(0, 0, spawn.Yaw),
        };
        coreObject = CoreObjectFactory.FinalizeCoreObject(info, template);
        if (coreObject is null) {
            return false;
        }

        coreObject = CoreObjectFactory.InitializeCoreObjectBehaviors(coreObject, template);

        return coreObject is not null;
    }

}
