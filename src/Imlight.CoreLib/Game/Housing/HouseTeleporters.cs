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
 * HOUSING SYSTEM
 * ========================================================================
 *
 * PURPOSE:
 * The teleporters of a house. The owner picks a teleporter's destination in the client's targeting mode, which sends
 * MSG_SETHOUSETELEPORTER; the destination is the custom data of the teleporter's blob entry, so it is stored with the
 * placed item. Whoever walks onto a teleporter that has a destination is moved there.
 *
 * USAGE EXAMPLE:
 * if (HouseTeleporters.TryLink(house, x, y, z, yaw, out var data)) {
 *     // store PlacedItem.EncodeCustom(data) on the teleporter and patch it
 * }
 * if (HouseTeleporters.OnMove(house, charId, location) is { } pad) {
 *     // teleport the player to pad.Target
 * }
 *
 * NOTE:
 * - The custom data is the client's HousingTeleporterBehavior, written with a versionable binary serializer and the
 *   property mask 0x10000, which the client loads in ClientBlobPatcher::MSG_PatchHousingTeleporterObject (0x140f1a6e0).
 * - Furniture is not a zone object, so the server finds a step onto a teleporter from the player's position, like the
 *   trainer hall door. A player who arrives on a teleporter has to step off every teleporter before one sends them again.
 * - Only a destination in the same house is used. The client's other zone messages (MSG_REQUESTTELEPORTERHOUSINGZONE,
 *   MSG_REQUESTTELEPORTERHOUSINGCONFIRM) are for teleporters into another house of the residence and for Castle Tours.
 * - A pet teleporter only keeps its destination: the pets that use it walk in the client.
 *
 * TODO:
 * - A destination in the other house of a residence (exterior to interior) is not used yet.
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/23/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Imcodec.Math;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Housing;

/// <summary>
/// A teleporter of a house with its destination.
/// </summary>
/// <param name="GidId">The GIDID of the placed teleporter.</param>
/// <param name="Position">Where the teleporter stands.</param>
/// <param name="IsPet">Whether only pets use it.</param>
/// <param name="TargetLot">The lot of the destination.</param>
/// <param name="Target">The destination.</param>
/// <param name="TargetYaw">The direction the player faces at the destination.</param>
internal sealed record HouseTeleporter(uint GidId, Vector3 Position, bool IsPet, ulong TargetLot, Vector3 Target, float TargetYaw);

internal static class HouseTeleporters {

    private static readonly float s_radius = ConfigurationManager.GetValue("Housing.TeleporterRadius", 50f);
    private static readonly float s_height = ConfigurationManager.GetValue("Housing.TeleporterHeight", 100f);

    // Template id to whether it is a teleporter (null for none) and whether it is a pet teleporter.
    private static readonly ConcurrentDictionary<uint, bool?> s_templates = new();

    // The teleporters of each house, read again when the house's revision moves.
    private static readonly ConcurrentDictionary<ulong, (uint Revision, uint GidBase, IReadOnlyList<HouseTeleporter> Pads)> s_houses = new();

    // The teleporter each character stands on, and whether they have to step off every teleporter first.
    private static readonly ConcurrentDictionary<ulong, (uint GidId, bool Blocked)> s_standing = new();

    /// <summary>
    /// Whether a template is a teleporter.
    /// </summary>
    /// <param name="templateId">The template of a placed item.</param>
    /// <param name="isPet">Whether it is a pet teleporter.</param>
    /// <returns>True for a template that carries a HousingTeleporterBehaviorTemplate.</returns>
    internal static bool IsTeleporter(uint templateId, out bool isPet) {
        var kind = templateId == 0 ? null : s_templates.GetOrAdd(templateId, id => CoreObjectFactory.GetCoreTemplate(id) is WizItemTemplate template
            && (template.m_behaviors ?? []).OfType<HousingTeleporterBehaviorTemplate>().FirstOrDefault() is { } teleporter
                ? teleporter.m_isPetTeleporter
                : null);
        isPet = kind == true;

        return kind is not null;
    }

    /// <summary>
    /// Builds the custom data of a teleporter that sends to a spot of a house.
    /// </summary>
    /// <param name="house">The house of the destination.</param>
    /// <param name="x">The destination X.</param>
    /// <param name="y">The destination Y.</param>
    /// <param name="z">The destination Z.</param>
    /// <param name="yaw">The direction the player faces there.</param>
    /// <param name="data">The bytes the client reads when this returns <c>true</c>.</param>
    /// <returns><c>false</c> when the data cannot be serialized.</returns>
    internal static bool TryLink(HouseState house, float x, float y, float z, float yaw, out byte[] data) {
        data = [];
        var template = house.Template;
        var behavior = new HousingTeleporterBehavior {
            m_lotInstanceGID = new GID(house.LotGid),
            m_x = x,
            m_y = y,
            m_z = z,
            m_yaw = yaw,
            m_lotTemplateID = new GID(template.TemplateId),
            m_clusterGID = new GID(house.LotGid),
            m_structureInstanceGID = new GID(template.IsStructure ? house.LotGid : 0),
            m_structureTemplateID = new GID(template.IsStructure ? template.TemplateId : template.StructureTemplateId),
            m_isOutside = !template.IsStructure,
        };
        try {
            if (!NewSerializer().Serialize(behavior, HouseMusic.BlobPropertyMask, out var serialized)) {
                return false;
            }

            data = serialized;

            return data.Length > 0;
        } catch (Exception exception) {
            Logger.Error("Teleporter: the destination could not be serialized: {Reason}", Logger.Args(exception.Message));

            return false;
        }
    }

    /// <summary>
    /// Reads the destination of a placed teleporter.
    /// </summary>
    /// <param name="item">The placed teleporter.</param>
    /// <returns>The destination, or <c>null</c> when it has none or it cannot be read.</returns>
    internal static HousingTeleporterBehavior ReadTarget(PlacedItem item) {
        var data = PlacedItem.DecodeCustom(item?.Custom);
        if (data.Length == 0) {
            return null;
        }

        try {
            if (NewSerializer().Deserialize<HousingTeleporterBehavior>(data, HouseMusic.BlobPropertyMask, out var target)) {
                return target;
            }
        } catch (Exception exception) {
            Logger.Error("Teleporter: the destination of teleporter {TemplateId} (item {ItemId}) could not be read: {Reason}",
                Logger.Args(item.TemplateId, item.Id, exception.Message));

            return null;
        }

        Logger.Warning("Teleporter: the destination of teleporter {TemplateId} (item {ItemId}) could not be read.",
            Logger.Args(item.TemplateId, item.Id));

        return null;
    }

    /// <summary>
    /// Checks a character's position in a house and finds the teleporter they just stepped onto.
    /// </summary>
    /// <param name="house">The house the character is in.</param>
    /// <param name="charId">The character.</param>
    /// <param name="location">Where the character stands.</param>
    /// <returns>The teleporter to use, or <c>null</c> when the character did not step onto one that sends them.</returns>
    internal static HouseTeleporter OnMove(HouseState house, ulong charId, Vector3 location) {
        var pads = PadsOf(house);
        var pad = pads.Count == 0 ? null : pads.FirstOrDefault(candidate => IsOn(candidate.Position, location));
        if (pad is null) {
            s_standing.TryRemove(charId, out _);

            return null;
        }

        var standing = s_standing.GetValueOrDefault(charId);
        if (standing.Blocked || standing.GidId == pad.GidId) {
            return null;
        }

        s_standing[charId] = (pad.GidId, false);
        if (pad.IsPet || pad.TargetLot == 0) {
            return null;
        }

        if (pad.TargetLot != house.LotGid) {
            Logger.Information("Teleporter: house object {GidId} of lot {LotGid} sends to lot {TargetLot}, which is not used yet.",
                Logger.Args(pad.GidId, house.LotGid, pad.TargetLot));

            return null;
        }

        // The player arrives on or near another teleporter; that one waits until they have stepped off.
        s_standing[charId] = (pad.GidId, true);

        return pad;
    }

    /// <summary>
    /// Forgets where a character stands, when they leave the house.
    /// </summary>
    internal static void Forget(ulong charId) => s_standing.TryRemove(charId, out _);

    /// <summary>
    /// Forgets the teleporters of a house, so the next move reads them again.
    /// </summary>
    internal static void Invalidate(ulong lotGid) => s_houses.TryRemove(lotGid, out _);

    private static bool IsOn(Vector3 pad, Vector3 location) {
        var dx = location.X - pad.X;
        var dy = location.Y - pad.Y;

        return dx * dx + dy * dy <= s_radius * s_radius && Math.Abs(location.Z - pad.Z) <= s_height;
    }

    private static IReadOnlyList<HouseTeleporter> PadsOf(HouseState house) {
        var revision = house.Revision;
        var gidBase = house.GidBase;
        if (s_houses.TryGetValue(house.LotGid, out var cached) && cached.Revision == revision && cached.GidBase == gidBase) {
            return cached.Pads;
        }

        var snapshot = house.Snapshot();
        var pads = new List<HouseTeleporter>();
        for (var slot = 0; slot < snapshot.Slots.Count; slot++) {
            if (snapshot.Slots[slot] is not { } item || !IsTeleporter(item.TemplateId, out var isPet)) {
                continue;
            }

            var target = ReadTarget(item);
            pads.Add(new HouseTeleporter(
                unchecked(snapshot.GidBase + (uint) slot),
                new Vector3(item.X, item.Y, item.Z),
                isPet,
                target is null ? 0 : (ulong) target.m_lotInstanceGID,
                target is null ? default : new Vector3(target.m_x, target.m_y, target.m_z),
                target?.m_yaw ?? 0));
        }

        s_houses[house.LotGid] = (snapshot.Revision, snapshot.GidBase, pads);

        return pads;
    }

    private static ObjectSerializer NewSerializer()
        => new(Versionable: true, Behaviors: SerializerFlags.None);

}
