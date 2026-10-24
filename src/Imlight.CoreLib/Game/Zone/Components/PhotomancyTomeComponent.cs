using System.Collections.Generic;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Photomancy;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// The Photomancy Tome in the Golem Tower (Niduus, 2026-10-24): pressing X opens the camera tome
/// (MSG_PHOTOMANCYUSETOME). Same shape as the hatching sigils' plain Interact option.
/// </summary>
internal sealed class PhotomancyTomeComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    private const float INTERACTION_TOLERANCE = 2.0f;

    public string ServiceName     => "Interact";
    public string NpcIcon         => "";
    public string NpcNameKey      => "";
    public string NpcTextKey      => "";
    public WizBangs WizBang       => WizBangs.None;
    public string StateName       => null;
    public string InteractWizBang => null;
    public string DisplayKey      => null;

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate go
        && (go.m_templateID == Photomancy.Photomancy.TOME_TEMPLATE_ID
            || go.m_objectName.ToString() == Photomancy.Photomancy.TOME_OBJECT_NAME);

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard _)
        => [new InteractableOption { m_serviceName = ServiceName }];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        if (!IsInHorizontalRadius(playerObject, ((IServiceComponent) this).DEFAULT_INTERACTION_RADIUS * INTERACTION_TOLERANCE)) {
            Logger.Warning("Photomancy: {0} used the tome from too far away.", Logger.Args(playerCharacter?.CharId));

            return;
        }

        Photomancy.Photomancy.OpenTome(playerActor, playerCharacter);
    }

}
