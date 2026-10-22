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
 * Reads the [Housing] section of Imlight.ini.
 * 
 * USAGE EXAMPLE:
 * var timeout = HousingSettings.DeedZoneTimeout;
 * 
 * NOTE:
 * - DeedZoneTimeoutSeconds: how long to wait for the client to confirm that a house zone archive is ready.
 * - StarterDeeds: the templates every character is given on the first Home click, as a comma-separated list.
 * - AtticLimit: how many items an attic holds.
 * - CastleSpace: how many castles a character has space for, sent as the character's m_maximumNumberOfIslands. It was
 *   never sent (so 0) while equipping a castle did nothing in the game (Niduus, 2026-10-22).
 *
 * TODO:
 * - Check with a capture whether the game reads m_maximumNumberOfIslands before it equips a castle.
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/22/2026
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Housing;

/// <summary>
/// The housing settings.
/// </summary>
internal static class HousingSettings {

    private const string DeedZoneTimeoutKey = "Housing.DeedZoneTimeoutSeconds";
    private const string StarterDeedsKey = "Housing.StarterDeeds";
    private const string AtticLimitKey = "Housing.AtticLimit";
    private const int DefaultDeedZoneTimeoutSeconds = 90;
    private const int DefaultAtticLimit = 100;
    private const string CastleSpaceKey = "Housing.CastleSpace";
    private const int DefaultCastleSpace = 100;

    /// <summary>
    /// How long to wait for the client to confirm that a house zone archive is ready:
    /// <c>Housing.DeedZoneTimeoutSeconds</c>, or 90 seconds when it is not a positive number.
    /// </summary>
    internal static TimeSpan DeedZoneTimeout {
        get {
            var configured = ConfigurationManager.Settings[DeedZoneTimeoutKey].AsInt();

            return TimeSpan.FromSeconds(configured > 0 ? configured : DefaultDeedZoneTimeoutSeconds);
        }
    }

    /// <summary>
    /// How many items an attic holds: <c>Housing.AtticLimit</c>, or 100 when it is not a positive number.
    /// </summary>
    internal static int AtticLimit {
        get {
            var configured = ConfigurationManager.Settings[AtticLimitKey].AsInt();

            return configured > 0 ? configured : DefaultAtticLimit;
        }
    }

    /// <summary>
    /// How many castles a character has space for: <c>Housing.CastleSpace</c>, or 100 when it is not a positive number.
    /// </summary>
    internal static int CastleSpace {
        get {
            var configured = ConfigurationManager.Settings[CastleSpaceKey].AsInt();

            return configured > 0 ? configured : DefaultCastleSpace;
        }
    }

    /// <summary>
    /// The templates of the houses every character is given: <c>Housing.StarterDeeds</c>, in order.
    /// An entry that is not a template id is logged and left out.
    /// </summary>
    internal static IReadOnlyList<uint> StarterDeeds {
        get {
            var starters = new List<uint>();
            foreach (var entry in ConfigurationManager.Settings[StarterDeedsKey].AsList()) {
                if (uint.TryParse(entry, NumberStyles.None, CultureInfo.InvariantCulture, out var templateId) && templateId != 0) {
                    starters.Add(templateId);
                } else {
                    Logger.Warning("{Key} has an entry that is not a template id: {Entry}", Logger.Args(StarterDeedsKey, entry));
                }
            }

            return starters;
        }
    }

}
