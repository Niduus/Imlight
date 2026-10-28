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

/*
 * ========================================================================
 * INTERACT LEVEL SCALES COMPONENT
 * ========================================================================
 *
 * PURPOSE:
 * The Scales of Knowledge's two buttons (Game/LevelScaling/LevelScales.cs): "Apply Level Scaling (LV 70)" gives the
 * area's Level Scaling Elixir (EquipmentService.LevelScaling), "Level Scaling 101" shows the scales' explanation.
 *
 * NOTE:
 * Each button shows while the wizard has the badge its option needs, as live. Without either badge the scales offer
 * nothing, and a use says which badge is missing (logged).
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/28/2026
 */

using System;
using System.Collections.Generic;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.LevelScaling;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractLevelScalesComponent(ZoneEntity entity)
    : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    private enum Choice {

        Apply,
        Info

    }

    public string ServiceName => "Interact";
    public string NpcIcon => LevelScales.ICON;
    public string NpcNameKey => (Entity.Template as GameObjectTemplate)?.m_displayName;
    // "Press X or click to Interact".
    public string NpcTextKey => "GUI_ObjectInteract";
    public WizBangs WizBang => WizBangs.None;
    public string StateName => null;
    public string InteractWizBang => null;
    public string DisplayKey => null;

    private LevelScales.Scales Scales
        => LevelScales.TryGet((Entity.Template as GameObjectTemplate)?.m_objectName.ToString(), out var scales) ? scales : null;

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate go && !go.m_objectName.IsEmpty && LevelScales.TryGet(go.m_objectName.ToString(), out _);

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter) {
        var scales = Scales;
        if (scales is null) {
            yield break;
        }

        foreach (var choice in Offered(scales, playerCharacter)) {
            yield return new InteractableOption {
                m_serviceName = ServiceName,
                m_displayKey = choice == Choice.Apply ? scales.ApplyKey : LevelScales.INFO_KEY,
                m_iconKey = LevelScales.ICON,
            };
        }
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        var scales = Scales;
        if (scales is null || playerCharacter is null) {
            return;
        }

        try {
            var offered = Offered(scales, playerCharacter);
            if (serviceOptionIndex >= offered.Count) {
                Logger.Information("Scales of Knowledge {0}: {1} has neither badge {2} nor {3}.",
                    Logger.Args(Name, playerCharacter.CharId, scales.Badge, scales.InfoBadge));

                return;
            }

            if (offered[(int) serviceOptionIndex] == Choice.Info) {
                ShowInfo(playerActor);

                return;
            }

            var elixir = LevelScales.ElixirId(scales);
            if (elixir == 0) {
                Logger.Warning("Scales of Knowledge {0}: the game files have no {1}.", Logger.Args(Name, scales.Elixir));

                return;
            }

            Logger.Information("Scales of Knowledge {0}: {1} applies {2}.", Logger.Args(Name, playerCharacter.CharId, scales.Elixir));
            playerActor.Tell(new CHARACTER_103_PROTOCOL.MSG_APPLYLEVELSCALING {
                ElixirTemplateId = elixir,
                AppliedTextKey = scales.AppliedKey,
            });
        }
        catch (Exception e) {
            Logger.Error("Scales of Knowledge {0}: using them failed: {1}", Logger.Args(Name, e));
        }
    }

    private string Name => (Entity.Template as GameObjectTemplate)?.m_objectName.ToString();

    // The buttons in the order they are shown: apply, then the explanation, each while its badge is earned.
    private static List<Choice> Offered(LevelScales.Scales scales, Wizard wizard) {
        var offered = new List<Choice>();
        if (LevelScales.HasBadge(wizard, scales.Badge)) {
            offered.Add(Choice.Apply);
        }

        if (LevelScales.HasBadge(wizard, scales.InfoBadge)) {
            offered.Add(Choice.Info);
        }

        return offered;
    }

    private void ShowInfo(IActorRef playerActor) {
        if (Entity.Template is not GameObjectTemplate template) {
            return;
        }

        var data = TemplateFileDialog.Read((uint) template.m_templateID, "Scales of Knowledge");
        if (data is null) {
            Logger.Warning("Scales of Knowledge {0}: its template has no explanation to read.", Logger.Args(Name));

            return;
        }

        playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_ACTORDIALOG { ActorDialog = data });
    }

}
