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

using System;
using System.Collections.Generic;

namespace Imlight.CoreLib.WizardData.Models.Player;

/// <summary>
/// A saved outfit from the backpack's Equipment Sets window: the gear that was worn when it was made.
/// </summary>
[Serializable]
public class GearSet {

    /// <summary>The name the wizard picked from the game's list of set names, as the client sent it.</summary>
    public uint Name { get; set; }

    public List<GearSetItem> Items { get; set; } = [];

}

[Serializable]
public class GearSetItem {

    public ulong ItemId { get; set; }
    public EquipmentSlotType SlotType { get; set; }

}
