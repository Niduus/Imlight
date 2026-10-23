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
 * Last Updated: 10/18/2026
 */

using System;
using Imlight.Common;

namespace Imlight.CoreLib.Game.PvP.Data;

internal static class PvPSettings {

    public static bool IsEnabled
        => ConfigurationManager.GetValue("PvP.Enabled", true);

    public static byte PracticePlanningSeconds
        => (byte) Math.Clamp(ConfigurationManager.GetValue("PvP.Practice.PlanningSeconds", 20), 1, byte.MaxValue);

    public static bool RankedEnabled
        => ConfigurationManager.GetValue("PvP.RankedEnabled", true);

    // Duel circles (Niduus, 2026-10-23): a house's own PvP circle, and a PvP battle circle added in the Customs tab,
    // start a duel between the wizards standing on its two sides.
    public static bool DuelCircles
        => ConfigurationManager.GetValue("PvP.DuelCircles", true);

    // A house's own duel circle takes turns (the PvP way); false: both sides act at once (the monster battle way).
    public static bool HouseDuelTakesTurns
        => ConfigurationManager.GetValue("PvP.HouseDuelTakesTurns", true);

    // Seconds both sides must stay on the circle before the duel starts.
    public static int DuelCircleCountdown
        => Math.Clamp(ConfigurationManager.GetValue("PvP.DuelCircleCountdown", 5), 1, 60);

    // The wizards' teleport effect plays as they are moved onto their places.
    public static bool DuelCircleTeleportEffect
        => ConfigurationManager.GetValue("PvP.DuelCircleTeleportEffect", true);

}
