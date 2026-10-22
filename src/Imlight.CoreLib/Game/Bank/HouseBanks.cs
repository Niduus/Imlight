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
 *
 * ========================================================================
 * HOUSE BANKS
 * ========================================================================
 *
 * PURPOSE:
 * Makes a bank chest placed in a house open the bank. The chests (HOUSE_BankChest_01 to _06: Bank Lockbox,
 * Strongbox, Chest, Trunk, Vault and Box) are furniture, which the client draws from the house blob, so the
 * server has no object to offer the bank from. Wherever one stands, the dorm bank (WC_RVW-Bank, an invisible NPC
 * that stands on the dorm's chest) is put on it, the way HouseTrainers puts a trainer on a trainer tome.
 *
 * USAGE EXAMPLE:
 * HouseBanks.SpawnAll(zoneActor, house);           // a player enters the house
 * HouseBanks.Spawn(zoneActor, gidId, placedItem);   // a chest is placed or moved
 * HouseBanks.Remove(zoneActor, gidId);             // a chest is moved or picked up
 *
 * NOTE:
 * A chest is any placed item whose template's object name is in BankConfig.ObjectNames and which is furniture,
 * not an NPC. Every bank opens the bank of the player who uses it, also in another player's house, like live.
 * Nothing is spawned while the bank is off (BankConfig.IsEnabled).
 *
 * TODO:
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/21/2026
 */

using System;
using System.Collections.Concurrent;
using Akka.Actor;
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Housing;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Bank;

/// <summary>
/// The invisible banks that stand on the bank chests of a house.
/// </summary>
internal static class HouseBanks {

    private const string TagPrefix = "HouseBank_";

    // Template id to object name of the furniture templates seen, so a house with many items reads each template once.
    private static readonly ConcurrentDictionary<uint, string> s_furnitureNames = new();

    /// <summary>
    /// Whether a placed item is a bank chest.
    /// </summary>
    /// <param name="templateId">The template of the placed item.</param>
    /// <returns>True when its object name is a bank name and the template is furniture.</returns>
    internal static bool IsBankItem(uint templateId) {
        if (templateId == 0) {
            return false;
        }

        var name = s_furnitureNames.GetOrAdd(templateId, id => CoreObjectFactory.GetCoreTemplate(id) is WizItemTemplate item && !item.m_objectName.IsEmpty
            ? item.m_objectName.ToString()
            : string.Empty);

        return name.Length > 0 && BankConfig.ObjectNames.Contains(name);
    }

    /// <summary>
    /// Whether a zone tag is the tag of a bank on a chest.
    /// </summary>
    internal static bool IsBankTag(string zoneTag) => zoneTag?.StartsWith(TagPrefix, StringComparison.Ordinal) == true;

    private static string Tag(uint gidId) => TagPrefix + gidId;

    /// <summary>
    /// Puts the bank on a placed bank chest. A bank already standing there is kept.
    /// </summary>
    /// <param name="zone">The house zone.</param>
    /// <param name="gidId">The GIDID of the placed item.</param>
    /// <param name="item">The placed item.</param>
    internal static void Spawn(IActorRef zone, uint gidId, PlacedItem item) {
        if (zone is null || item is null || !IsBankItem(item.TemplateId) || !BankConfig.IsEnabled) {
            return;
        }

        Logger.Information("Bank: chest {TemplateId} (house object {GidId}) gets a bank at {X}, {Y}, {Z}.",
            Logger.Args(item.TemplateId, gidId, item.X, item.Y, item.Z));
        zone.Tell(new SERVICE_101_PROTOCOL.MSG_SPAWNHOUSETRAINER {
            Tag = Tag(gidId), X = item.X, Y = item.Y, Z = item.Z, Yaw = item.Yaw,
        }, ActorRefs.NoSender);
    }

    /// <summary>
    /// Takes away the bank of a chest that was moved or picked up. Nothing happens when none stands there.
    /// </summary>
    /// <param name="zone">The house zone.</param>
    /// <param name="gidId">The GIDID of the placed item.</param>
    internal static void Remove(IActorRef zone, uint gidId)
        => zone?.Tell(new ZONE_102_PROTOCOL.MSG_REMOVETRIGGEROBJECT { ObjectName = Tag(gidId) }, ActorRefs.NoSender);

    /// <summary>
    /// Puts a bank on every bank chest of a house.
    /// </summary>
    /// <param name="zone">The house zone.</param>
    /// <param name="house">The house.</param>
    internal static void SpawnAll(IActorRef zone, HouseState house) {
        var snapshot = house.Snapshot();
        for (var slot = 0; slot < snapshot.Slots.Count; slot++) {
            if (snapshot.Slots[slot] is { } item) {
                Spawn(zone, unchecked(snapshot.GidBase + (uint) slot), item);
            }
        }
    }

    /// <summary>
    /// Builds the bank for a spawn request.
    /// </summary>
    internal static bool TryCreate(SERVICE_101_PROTOCOL.MSG_SPAWNHOUSETRAINER spawn, out CoreObjectInfo info, out CoreObject coreObject,
                                   out GameObjectTemplate template) {
        info = null;
        coreObject = null;
        var templateId = BankConfig.ChestNpcTemplateId;
        template = CoreObjectFactory.GetCoreTemplate(templateId) as GameObjectTemplate;
        if (template is null) {
            Logger.Warning("Bank: the bank template {TemplateId} for a house bank chest was not found, see [Bank] ChestNpc.",
                Logger.Args(templateId));

            return false;
        }

        info = new CoreObjectInfo {
            m_templateID = templateId,
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
