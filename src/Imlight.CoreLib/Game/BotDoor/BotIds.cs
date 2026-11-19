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
 */

using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.BotPlayers;

/// <summary>
/// The character ids of Bot Players wizards: a range of their own, far above any real character's. Kept with the server
/// (not the bots folder) because the server's own saving reads it even while the bots are being swapped (BotDoor).
/// </summary>
public static class BotIds {

    internal const ulong BASE = 0x7E00_0000_0000_0000UL;
    private const ulong RANGE = 0x0000_0000_FFFF_FFFFUL;

    public static ulong CharIdOf(int botId) => BASE + (ulong) botId;

    public static bool IsBot(ulong charId) => charId >= BASE && charId <= BASE + RANGE;

    /// <summary>For a wizard's object id (its character id + 2, Wizard.GetGameObjectId).</summary>
    public static bool IsBotObject(ulong globalId) => Wizard.TryGetCharacterId(globalId, out var charId) && IsBot(charId);

}
