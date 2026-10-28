/*
 * Imlight
 * Copyright (C) 2026 Revive101
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
using System.Linq;
using Imcodec.CoreObject;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Game.LevelScaling;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// The Scales of Knowledge's Level Scaling Elixir (Game/LevelScaling/LevelScales.cs): a running one is taken off, then
/// the new one is given and started, with its timer on the screen. Throwing it away ends it as for any elixir.
/// </summary>
internal partial class EquipmentService {

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_APPLYLEVELSCALING))]
    private void ReceiveApplyLevelScaling(CHARACTER_103_PROTOCOL.MSG_APPLYLEVELSCALING message) {
        try {
            var wizard = GetActiveWizard();
            if (wizard?.EquipmentBehavior is null) {
                return;
            }

            // The new one replaces any level scaling already running, as the scales' ResRemoveElixir results do.
            var running = wizard.EquipmentBehavior.GetActiveElixirs()
                .Where(active => LevelScales.ScalingEffectOf(ItemHelper.GetItemTemplate(active.Item)) is not null)
                .Select(active => active.Slot)
                .ToList();
            foreach (var slot in running) {
                ExpireElixir(wizard, slot);
            }

            if (message.ElixirTemplateId == 0) {
                InformGameClient(running.Count > 0 ? "Level scaling is off." : "You have no level scaling running.");

                return;
            }

            if (CoreObjectFactory.GetCoreTemplate(message.ElixirTemplateId) is not WizItemTemplate template
                || template.m_behaviors?.OfType<ElixirBehaviorTemplate>().FirstOrDefault() is not { } elixirTemplate) {
                Logger.Warning("Level scaling: template {0} is no elixir.", Logger.Args(message.ElixirTemplateId));

                return;
            }

            if (!TryStartGivenElixir(wizard, message.ElixirTemplateId, template, elixirTemplate)) {
                InformGameClient("You have too many elixirs running for level scaling. Throw one away from your backpack's elixirs and try again.");

                return;
            }

            Logger.Information("Level scaling: {0} has {1} running ({2}).",
                Logger.Args(wizard.CharId, template.m_objectName, LevelScales.ScalingEffectOf(template)));
            if (!string.IsNullOrEmpty(message.AppliedTextKey)) {
                SendToSocket(new GAME_5_PROTOCOL.MSG_CLIENTNOTIFYTEXT { NotifyText = message.AppliedTextKey, Type = 0 });
            }
        }
        catch (Exception e) {
            Logger.Error("Level scaling: giving the elixir {0} failed: {1}", Logger.Args(message.ElixirTemplateId, e));
        }
    }

    // A new elixir of the template, put straight into an elixir slot and started (the crown shop's "use now" does the same).
    private bool TryStartGivenElixir(Wizard wizard, uint templateId, WizItemTemplate itemTemplate, ElixirBehaviorTemplate elixirTemplate) {
        uint durationSeconds = 0;
        if (!string.IsNullOrEmpty(elixirTemplate.m_expireTime)) {
            _ = uint.TryParse((string) elixirTemplate.m_expireTime, out durationSeconds);
        }

        var equipBehavior = wizard.EquipmentBehavior;
        if (durationSeconds == 0 || !equipBehavior.CanEquipElixir(elixirTemplate)) {
            return false;
        }

        var elixirItem = (WizClientObjectItem) CoreObjectFactory.FinalizeCoreObject(templateId);
        elixirItem.m_characterId = (GID) wizard.CharId;
        CoreObjectFactory.InitializeCoreObjectBehaviors(elixirItem, templateId);

        if (CoreObjectFactory.FindBehaviorInstance<ClientElixirBehavior>(elixirItem, out var clientElixir)) {
            clientElixir.m_expireTime = durationSeconds;
            clientElixir.m_statsApplied = true;
        }

        if (!equipBehavior.EquipElixir(elixirItem, durationSeconds)) {
            return false;
        }

        var addedEffects = CharacterEffectHelper.AddEffectsToWizard(wizard, itemTemplate);

        WizardItemCollection.AddItem(elixirItem);
        WizardCollection.UpdateCharacterItems(wizard);

        if (_itemSerializer.Serialize(elixirItem, 24, out var serializedItem)) {
            SendToSocket(new GAME_5_PROTOCOL.MSG_INVENTORYBEHAVIOR_ADDITEM {
                GlobalID = wizard.GameObjectID,
                SerializedItem = serializedItem
            });
        }

        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_ITEMACQUISITION {
            ItemGlobalID = elixirItem.m_globalID,
            ItemLocation = 1,
            ItemTemplateID = templateId
        });

        SendToSocket(new GAME_5_PROTOCOL.MSG_EQUIPITEM {
            ItemID = elixirItem.m_globalID,
            SlotName = "Elixir",
            IsEquip = 1
        });

        SendAddEffects(addedEffects);

        // Start the HUD countdown and turn its icon on.
        SendToSocket(new WIZARD2_53_PROTOCOL.MSG_SETELIXIRTIMER {
            GlobalID = elixirItem.m_globalID,
            TimerTime = durationSeconds
        });

        SendToSocket(new WIZARD_12_PROTOCOL.MSG_ELIXIRSTATECHANGE {
            parentID = elixirItem.m_globalID,
            EffectEnabled = 1
        });

        PlaySound(elixirTemplate.m_equipSound);
        SyncHealthAndMana(wizard);
        StartElixirPlaytimeTicker();

        return true;
    }

}
