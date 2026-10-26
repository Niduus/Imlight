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

using System.Collections.Generic;
using System.Linq;
using Imcodec.CoreObject;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Effects;

/// <summary>
/// A wizard's speed (mount, speed gear) for the other players' games. Each game walks a player toward the spot their
/// last move named at the speed it knows for them; told only base speed, a friend on a mount fell behind on the other
/// screen and then jumped ahead (Niduus, 2026-10-26: players weren't fully in sync with movement).
/// </summary>
internal static class SharedSpeed {

    // [Advanced] ShareSpeedWithOthers, read on every use so a change applies without a restart.
    private static bool Enabled => ConfigurationManager.GetValue("Advanced.ShareSpeedWithOthers", true);

    private static readonly CoreObjectSerializer s_effectSerializer = new(versionable: false, behaviors: SerializerFlags.None);

    /// <summary>MSG_ADDEFFECT for each speed effect in the list, on the wizard's object.</summary>
    internal static List<IMessage> AddMessages(GID gameObjectId, IEnumerable<GameEffectBase> effects) {
        var messages = new List<IMessage>();
        if (!Enabled || effects is null) {
            return messages;
        }

        foreach (var effect in effects.OfType<SpeedEffect>()) {
            if (!s_effectSerializer.Serialize(effect, PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit, out var data)) {
                Logger.Error("Failed to serialize speed effect {0} for the zone.", Logger.Args(effect.m_effectNameID));

                continue;
            }

            messages.Add(new GAME_5_PROTOCOL.MSG_ADDEFFECT { GameObjectID = gameObjectId, EffectData = data });
        }

        return messages;
    }

    /// <summary>MSG_REMOVEEFFECT for each speed effect in the list, on the wizard's object.</summary>
    internal static List<IMessage> RemoveMessages(GID gameObjectId, IEnumerable<GameEffectBase> effects) {
        if (!Enabled || effects is null) {
            return [];
        }

        return [.. effects.OfType<SpeedEffect>().Select(effect => (IMessage) new GAME_5_PROTOCOL.MSG_REMOVEEFFECT {
            GameObjectID = gameObjectId,
            EffectNameID = effect.m_effectNameID,
            InternalID = effect.m_internalID,
        })];
    }

    /// <summary>The speed effects the wizard has now, for a player who is shown them.</summary>
    internal static List<IMessage> CurrentMessages(Wizard wizard)
        => AddMessages(wizard.GameObjectID, wizard.GameEffects);

}
