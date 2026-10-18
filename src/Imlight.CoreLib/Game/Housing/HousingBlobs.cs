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
 * The pieces of the housing blob protocol that the house and the attic both use: parsing the client's blob request
 * and serializing a blob or a request with the generated types.
 *
 * USAGE EXAMPLE:
 * if (HousingBlobs.TryParseRequest(message.Data, out var request)) {
 *     SendToSocket(new MSG_SEND_BLOB { Data = HousingBlobs.Serialize(blob), UserData = gidBase });
 * }
 *
 * NOTE:
 * - A request is only accepted when it is exactly the bytes the serializer writes for what it parsed, so truncated,
 *   padded and malformed payloads are refused. A list of GIDs only has to parse: no capture shows the client's bytes for one.
 * - The version, count and user data of a request describe the client's cache. They never decide what is sent.
 *
 * TODO:
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/15/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Game.Housing;

internal static class HousingBlobs {

    /// <summary>
    /// The blob type of everything housing.
    /// </summary>
    internal const string BlobType = "Housing";

    private const int MaxRequestLength = 4096;
    private const int MaxSerializedLength = 65535;
    private const uint SerializeMask = 1;

    /// <summary>
    /// Counts the days the client stamps on a blob it caches, the way the client does.
    /// </summary>
    /// <returns>The epoch days of now.</returns>
    internal static uint EpochDaysNow()
        => unchecked((uint) (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60 * 1440));

    /// <summary>
    /// Serializes a blob or a blob request.
    /// </summary>
    /// <param name="value">The object to serialize.</param>
    /// <returns>The bytes the client reads.</returns>
    /// <exception cref="InvalidOperationException">The object cannot be serialized or does not fit a string field.</exception>
    internal static ByteString Serialize(PropertyClass value) {
        if (!NewSerializer().Serialize(value, SerializeMask, out var serialized)) {
            throw new InvalidOperationException($"Could not serialize {value.GetType().Name}.");
        }

        if (serialized.Length > MaxSerializedLength) {
            throw new InvalidOperationException(
                $"{value.GetType().Name} is {serialized.Length} bytes, past the {MaxSerializedLength} byte string limit.");
        }

        return serialized;
    }

    /// <summary>
    /// Parses the data of a client's blob request.
    /// </summary>
    /// <param name="data">The request's data field.</param>
    /// <param name="request">The request when this returns <c>true</c>.</param>
    /// <returns><c>false</c> when the data is not exactly a serialized <see cref="BlobRequest"/>.</returns>
    internal static bool TryParseRequest(ByteString data, out BlobRequest request) {
        request = null;
        byte[] bytes = data;
        if (bytes is null || bytes.Length == 0 || bytes.Length > MaxRequestLength) {
            return false;
        }

        try {
            var serializer = NewSerializer();
            if (!serializer.Deserialize<BlobRequest>(bytes, SerializeMask, out var parsed) || parsed is null
                || !serializer.Serialize(parsed, SerializeMask, out var canonical)
                || !((byte[]) canonical).AsSpan().SequenceEqual(bytes)) {
                return false;
            }

            request = parsed;

            return true;
        } catch (Exception) {
            return false;
        }
    }

    /// <summary>
    /// Parses a list of housing object GIDs the client sends, such as the objects "Pick Up All Items" leaves in place.
    /// </summary>
    /// <param name="data">The message's data field; empty for no GIDs.</param>
    /// <param name="gids">The GIDs when this returns <c>true</c>.</param>
    /// <returns><c>false</c> when the data is not a serialized <see cref="HousingItemList"/>.</returns>
    internal static bool TryParseItemList(ByteString data, out IReadOnlyList<ulong> gids) {
        gids = [];
        byte[] bytes = data;
        if (bytes is null || bytes.Length == 0) {
            return true;
        }

        try {
            if (bytes.Length > MaxSerializedLength || !NewSerializer().Deserialize<HousingItemList>(bytes, SerializeMask, out var parsed) || parsed is null) {
                return false;
            }

            gids = [.. (parsed.m_housingItemGIDList ?? []).Select(gid => (ulong) gid)];

            return true;
        } catch (Exception) {
            return false;
        }
    }

    private static ObjectSerializer NewSerializer()
        => new(Versionable: false, Behaviors: SerializerFlags.None);

}
