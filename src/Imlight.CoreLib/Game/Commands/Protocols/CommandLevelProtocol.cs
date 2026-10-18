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
using Imlight.Common;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.Shared.Character;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// <c>.level MAX</c> or <c>.level 50</c>: sets your wizard's level, without the <c>.mod</c> group.
/// </summary>
internal class CommandLevelProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("level")]
    [Alias("lvl")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void LevelCommand(string level) {
        if (!TryParseLevel(level, out var newLevel, out var error)) {
            InformSenderClient(error);

            return;
        }

        Context.SessionActor.Tell(new CHARACTER_103_PROTOCOL.MSG_LEVELUP { NewLevel = newLevel }, null);
        InformSenderClient($"Level set to {newLevel}.");
    }

    /// <summary>
    /// Reads a level from "max" (any case) or a number from 1 to the max level.
    /// </summary>
    internal static bool TryParseLevel(string level, out byte newLevel, out string error) {
        newLevel = 0;
        error = null;
        var maxLevel = MagicLevelsConfig.MaxLevel;

        if (string.Equals(level?.Trim(), "max", System.StringComparison.OrdinalIgnoreCase)) {
            newLevel = (byte) maxLevel;

            return true;
        }

        if (!int.TryParse(level?.Trim(), out var number) || number < 1) {
            error = $"Type .level MAX or a level from 1 to {maxLevel}.";

            return false;
        }

        if (number > maxLevel) {
            error = $"You cannot set level higher than the max level ({maxLevel}).";

            return false;
        }

        newLevel = (byte) number;

        return true;
    }

}
