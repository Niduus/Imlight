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
using System.Reflection;
using System.Runtime.CompilerServices;
using Imcodec.ObjectProperty;
using Imcodec.IO;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Sigils;

/// <summary>
/// The battle circle of an Advanced Mode dungeon (Game/World/AdvancedMode): a combat sigil object under the newer class
/// hash 490043331, which the 43 Advanced copies in the game files use for every circle (Imcodec had no type for it, so
/// those rooms had no circle and no fight started). Its sigil is a Battleground one ("BattlegroundSigil" or
/// "AdvancedModeSigil": two teams of four, Red and Blue); here it is read as the ordinary circle of 8
/// (CombatSigil8Actor), the monsters' four places where the Red team stands, so the fight plays like any dungeon's.
/// </summary>
public record AdvancedCombatSigilObjectInfo : CombatSigilObjectInfo {

    internal const uint CLASS_HASH = 490043331;

    // The Battleground sigils the copies name, and the ordinary circle they become.
    private const string ORDINARY_SIGIL = "CombatSigil8Actor";

    public override uint GetHash() => CLASS_HASH;

    public override bool Decode(BitReader reader, ObjectSerializer serializer) {
        var decoded = base.Decode(reader, serializer);
        var sigil = m_sigilType.ToString() ?? "";
        if (sigil.Length == 0 || sigil.StartsWith("Battleground", StringComparison.OrdinalIgnoreCase)
            || sigil.Equals("AdvancedModeSigil", StringComparison.OrdinalIgnoreCase)) {
            m_sigilType = ORDINARY_SIGIL;
        }

        return decoded;
    }

    /// <summary>
    /// Adds the type to Imcodec's server types as the assembly loads, before any zone file is read. Imcodec keeps them in
    /// a private registry with no way in from outside, so it is reached by reflection.
    /// </summary>
    [ModuleInitializer]
    internal static void Register() {
        try {
            var field = typeof(ObjectSerializer).GetField("s_serverTypeRegistry", BindingFlags.NonPublic | BindingFlags.Static);
            if (field?.GetValue(null) is TypeRegistry registry) {
                registry.RegisterType(CLASS_HASH, typeof(AdvancedCombatSigilObjectInfo));
            }
            else {
                Logger.Warning("Advanced Mode: could not add the Advanced battle circle type, so Advanced dungeons have no circles.");
            }
        }
        catch (Exception e) {
            Logger.Warning("Advanced Mode: could not add the Advanced battle circle type: {0}", Logger.Args(e.Message));
        }
    }

}
