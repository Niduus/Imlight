using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// The Guild Museum's curators (Niduus, 2026-10-24): every exhibit stand of a gallery, its Curator's Podium and its
/// curator NPC carry a GuildMuseumCuratorBehavior naming their gallery and exhibit ("Podium" and "Gallery" for the
/// whole gallery). Pressing X opens the game's Museum Curator window (MSG_REQUESTMUSEUMCURATOR) with the wizard's
/// essence for those artifacts, and the same is written in chat ([GuildMuseum] ChatLines) in case the window does not
/// show it. See Game/GuildMuseum.
/// </summary>
internal sealed class GuildMuseumCuratorComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    private const float INTERACTION_TOLERANCE = 2.0f;

    public string ServiceName     => "GuildMuseumCurator";
    public string NpcIcon         => "";
    public string NpcNameKey      => Entity.Template is GameObjectTemplate template ? (string) template.m_displayName : null;
    public string NpcTextKey      => "";
    public WizBangs WizBang       => WizBangs.GuildMuseumCurator;
    public string StateName       => null;
    public string InteractWizBang => null;
    public string DisplayKey      => null;

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => GuildMuseum.GuildMuseum.Enabled && CuratorOf(template) is not null;

    private static GuildMuseumCuratorTemplate CuratorOf(CoreTemplate template)
        => (template as GameObjectTemplate)?.m_behaviors?.OfType<GuildMuseumCuratorTemplate>().FirstOrDefault();

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard _)
        => [new GuildMuseumCuratorOption { m_serviceName = ServiceName }];

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        try {
            if (!IsInHorizontalRadius(playerObject, ((IServiceComponent) this).DEFAULT_INTERACTION_RADIUS * INTERACTION_TOLERANCE)) {
                Logger.Warning("Guild Museum | {0} used a curator from too far away.", Logger.Args(playerCharacter?.CharId));

                return;
            }

            var curator = CuratorOf(Entity.Template);
            var gallery = GuildMuseum.GuildMuseum.GalleryOf(curator?.m_galleryWorld.ToString());
            if (gallery is null) {
                Logger.Warning("Guild Museum | {0} names gallery {1}, which the museum does not have.",
                    Logger.Args(Entity.ActiveGameObject.m_debugName, curator?.m_galleryWorld.ToString()));

                return;
            }

            // An exhibit stand names its exhibit; the podium and the curator name the whole gallery.
            var tag = curator.m_exhibit.ToString();
            var exhibit = GuildMuseum.GuildMuseum.ExhibitOf(gallery, tag);
            var charId = playerCharacter.CharId;
            if (GuildMuseum.GuildMuseum.TryWriteEssenceList(charId, gallery, exhibit, out var data)) {
                playerActor.Tell(new WIZARD3_56_PROTOCOL.MSG_REQUESTMUSEUMCURATOR {
                    GlobalID = Entity.ActiveGameObject.m_globalID.Full,
                    WorldID = GuildMuseum.GuildMuseum.WorldId(gallery),
                    ExhibitID = exhibit is null ? 0 : GuildMuseum.GuildMuseum.ExhibitId(gallery, exhibit),
                    Buffer = data,
                });
            }

            Logger.Information("Guild Museum | {0} opened the curator of {1} {2} (ids {3}).",
                Logger.Args(charId, gallery.World, tag, GuildMuseum.GuildMuseum.Ids));

            if (exhibit is not null) {
                // The stand shows what the wizard has, in case the window was not told before.
                playerActor.Tell(new WIZARD3_56_PROTOCOL.MSG_UPDATEMUSEUMEXHIBIT {
                    WorldID = GuildMuseum.GuildMuseum.WorldId(gallery),
                    ExhibitID = GuildMuseum.GuildMuseum.ExhibitId(gallery, exhibit),
                    State = (uint) GuildMuseum.GuildMuseum.TierOf(charId, exhibit),
                });
            }

            if (GuildMuseum.GuildMuseum.ChatOn) {
                List<string> lines = exhibit is null
                    ? [GuildMuseum.GuildMuseum.GalleryLine(charId, gallery), $"Type .museum {gallery.World.ToLowerInvariant()} list to see every exhibit."]
                    : GuildMuseum.GuildMuseum.ExhibitLines(charId, exhibit);
                foreach (var line in lines) {
                    playerActor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = line, Modal = 0 });
                }
            }
        }
        catch (Exception ex) {
            Logger.Error("Guild Museum | Opening a curator failed: {0}", Logger.Args(ex));
        }
    }

}
