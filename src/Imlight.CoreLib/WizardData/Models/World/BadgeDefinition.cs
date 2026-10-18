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

namespace Imlight.CoreLib.WizardData.Models.World;

/// <summary>
/// One badge from SpiralDB's BadgeDefinitions. Id is the string hash of the badge's name, the same number the
/// game uses for a badge (BadgeInfo.m_badgeNameID).
/// </summary>
public class BadgeDefinition {

    public uint Id { get; set; }
    public string Name { get; set; }
    public string DisplayName { get; set; }
    public string Description { get; set; }
    // Locale key of the title shown over the wizard's head, such as Badges_00005095.
    public string Title { get; set; }
    public string Filter { get; set; }
    public bool ShowTitle { get; set; }
    public bool Retired { get; set; }

}
