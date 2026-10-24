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
 * Decides what a character may do in a house: be its owner, visit it, or stay out.
 *
 * USAGE EXAMPLE:
 * if (HouseAccess.Evaluate(visitor, house) == HouseAccessLevel.Denied) {
 *     // refuse the visit
 * }
 *
 * NOTE:
 * - The owner always has access. Anyone else is a guest only when the owner is a friend of theirs and has not hidden
 *   from friends (WizGameStats.m_invisibleToFriends). The protocol knows no finer setting.
 * - Only the owner decorates. A guest sees the house and never edits it.
 * - The owner's data comes from the store, so the owner can be offline.
 *
 * - A house listed on Castle Tours (CastleTourBoard) is open to everyone as a guest.
 *
 * TODO:
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using Imlight.CoreLib.Game.Social;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Housing;

/// <summary>
/// What a character may do in a house.
/// </summary>
internal enum HouseAccessLevel {
    Denied,
    Guest,
    Owner,
}

internal static class HouseAccess {

    /// <summary>
    /// Decides what a character may do in a house.
    /// </summary>
    /// <param name="visitor">The character who wants in.</param>
    /// <param name="house">The stored house.</param>
    /// <returns>The access level; <see cref="HouseAccessLevel.Denied"/> when the house is missing or its owner cannot be loaded.</returns>
    internal static HouseAccessLevel Evaluate(Wizard visitor, House house)
        => house is null ? HouseAccessLevel.Denied : Evaluate(visitor, house.OwnerCharId, house.TemplateId);

    /// <summary>
    /// Decides what a character may do in the houses of an owner.
    /// </summary>
    /// <param name="visitor">The character who wants in.</param>
    /// <param name="ownerCharId">The owner of the house.</param>
    /// <param name="templateId">The house's template, or 0 when it is not known. The guild house lets the owner's guild in.</param>
    /// <returns>The access level; <see cref="HouseAccessLevel.Denied"/> when the owner cannot be loaded.</returns>
    internal static HouseAccessLevel Evaluate(Wizard visitor, ulong ownerCharId, uint templateId = 0) {
        if (visitor is null) {
            return HouseAccessLevel.Denied;
        }

        if (visitor.CharId == ownerCharId) {
            return HouseAccessLevel.Owner;
        }

        // A guild house is the guild's: its leader's guild mates come in whether or not they are friends.
        if (Guilds.IsGuildHouseTemplate(templateId) && Guilds.SameGuild(visitor.CharId, ownerCharId)) {
            return HouseAccessLevel.Guest;
        }

        // A house on Castle Tours lets everyone in.
        if (templateId != 0 && CastleTourBoard.IsOnTour(ownerCharId, templateId)) {
            return HouseAccessLevel.Guest;
        }

        var owner = WizardCollection.GetCharacterUnloaded(ownerCharId);
        if (owner?.GameStats is null || owner.GameStats.m_invisibleToFriends) {
            return HouseAccessLevel.Denied;
        }

        return BuddyRelationshipCollection.AreFriends(ownerCharId, visitor.CharId) ? HouseAccessLevel.Guest : HouseAccessLevel.Denied;
    }

}
