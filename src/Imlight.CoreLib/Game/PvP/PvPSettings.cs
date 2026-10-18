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
 * PVP
 * ========================================================================
 * 
 * PURPOSE:
 * Reads the [PvP] section of Imlight.ini.
 * 
 * USAGE EXAMPLE:
 * if (!PvPSettings.IsEnabled) { }
 * 
 * NOTE:
 * The values are read on every call, so a reloaded configuration takes effect without a restart.
 * 
 * TODO:
 * 
 * 
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using System;
using Imlight.Common;

namespace Imlight.CoreLib.Game.PvP;

internal static class PvPSettings {

    public static bool IsEnabled
        => ConfigurationManager.GetValue("PvP.Enabled", true);

    public static byte PracticePlanningSeconds
        => (byte) Math.Clamp(ConfigurationManager.GetValue("PvP.Practice.PlanningSeconds", 20), 1, byte.MaxValue);

    public static bool SwapFirstTeamEachRound
        => ConfigurationManager.GetValue("PvP.SwapFirstTeamEachRound", true);

    /// <summary>
    /// Gets how long a finished match waits for the clients to finish their victory scene. Unset means the match
    /// does not wait.
    /// </summary>
    public static TimeSpan ReturnGrace {
        get {
            var seconds = ConfigurationManager.GetValue("PvP.ReturnGraceSeconds", -1d);
            if (seconds < 0) {
                if (!s_warnedReturnGraceUnset) {
                    s_warnedReturnGraceUnset = true;
                    Logger.Warning("PvP.ReturnGraceSeconds is not set; finished matches do not wait for the victory scene.");
                }

                return TimeSpan.Zero;
            }

            return TimeSpan.FromSeconds(seconds);
        }
    }

    private static bool s_warnedReturnGraceUnset;

}
