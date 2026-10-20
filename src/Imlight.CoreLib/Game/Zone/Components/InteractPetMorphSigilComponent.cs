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
 *
 * ========================================================================
 * INTERACT PET MORPH SIGIL COMPONENT
 * ========================================================================
 *
 * PURPOSE:
 * The pet hatching sigils in the Pet Pavilion hatchery (WizardCity/Interiors/WC_Hatchery). The solo
 * sigils ("Teleport Pet Morphing 1P", tid 266075, PetMorphFor1Sigil01..04) hatch two of the player's
 * own pets; the duo sigils ("Teleport Pet Morphing", tid 112077, PetMorphSigil01..04, two circles)
 * pair the two players standing on them into the in-person hatch. Justin, 2026-10-20: the solo pads
 * were lit but had no prompt, the duo pads were dark with no prompt.
 *
 * USAGE EXAMPLE:
 * Attaches automatically to the two sigil templates (ShouldAttachToEntity). Pressing X tells the
 * player's session a MSG_HATCHSIGIL; HatchService does the rest.
 *
 * NOTE:
 * Both templates are MinigameSigilInfo pads (DynamicTriggerTemplate with BasicNPCServiceBehavior and
 * CountdownBehavior), spawned by ZoneSigilSupervisor. The solo pads' fiery ring is the separate
 * KioskSigilGraphicOn object (266073); the zone trigger ActivateHatchingSigilsTrigger only gives the
 * duo pads the dynamod state "On", which is sent here to each joining player as well. Every step is
 * logged starting "Hatching sigil".
 *
 * TODO:
 * - How the live server started the morph (countdown, then which window) is not known; see HatchService.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/20/2026
 */

using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractPetMorphSigilComponent(ZoneEntity entity)
    : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    private const uint SOLO_TEMPLATE_ID = 266075;
    private const uint DUO_TEMPLATE_ID = 112077;
    private const string SIGIL_NAME = "Teleport Pet Morphing";

    // The solo pads stand 320 apart, so their prompts must not reach each other's pad.
    private const float SOLO_RADIUS = 120.0f;
    private const float SOLO_MAX_RADIUS = 150.0f;
    // The duo pad is two circles side by side; this reaches both.
    private const float DUO_RADIUS = 200.0f;
    private const float INTERACTION_TOLERANCE = 2.0f;

    // The state ActivateHatchingSigilsTrigger gives the duo pads (ResAddDynaMod dynaModState=On).
    private const string LIT_STATE = "On";

    public string ServiceName     => "Interact";
    public string NpcIcon         => "";
    public string NpcNameKey      => "";
    public string NpcTextKey      => "";
    public WizBangs WizBang       => WizBangs.None;
    public string StateName       => null;
    public string InteractWizBang => null;
    public string DisplayKey      => null;

    public float DEFAULT_INTERACTION_RADIUS => Radius;

    // The players standing on the pad, by character id.
    private readonly Dictionary<ulong, Occupant> _occupants = [];
    private bool _logged;

    private MinigameSigilInfo SigilInfo
        => Entity.Info as MinigameSigilInfo;

    private bool IsSolo
        => Entity.Template is GameObjectTemplate go
        && (go.m_templateID == SOLO_TEMPLATE_ID || go.m_objectName.ToString().EndsWith("1P"));

    private float Radius {
        get {
            var given = SigilInfo?.m_radius ?? 0;
            if (IsSolo) {
                return given > 0 ? System.Math.Min(given, SOLO_MAX_RADIUS) : SOLO_RADIUS;
            }

            return given > 0 ? given : DUO_RADIUS;
        }
    }

    private string PadName
        => Entity.Info?.m_zoneTag.ToString() is { Length: > 0 } tag ? tag : Entity.ActiveGameObject?.m_debugName.ToString();

    public static bool ShouldAttachToEntity(CoreTemplate template) {
        if (template is not GameObjectTemplate go) {
            return false;
        }

        if (go.m_templateID == SOLO_TEMPLATE_ID || go.m_templateID == DUO_TEMPLATE_ID) {
            return true;
        }

        return !go.m_objectName.IsEmpty && go.m_objectName.ToString().StartsWith(SIGIL_NAME);
    }

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard _) {
        if (!PetHatching.Enabled) {
            return [];
        }

        return [
            new InteractableOption { m_serviceName = ServiceName }
        ];
    }

    public override void OnStart() {
        LogPadOnce();
    }

    public override void OnPlayerJoin(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        LogPadOnce();
        if (!IsSolo && PetHatching.Enabled) {
            Entity.ChangeStateExclusiveSender(LIT_STATE, playerActor);
        }
    }

    public override void OnPlayerLeave(IActorRef playerActor, ulong id) {
        foreach (var charId in _occupants.Where(x => x.Value.Actor.Equals(playerActor)).Select(x => x.Key).ToList()) {
            _occupants.Remove(charId);
            Logger.Information("Hatching sigil: {0} left the zone, so is off {1}.", Logger.Args(charId, PadName));
        }
    }

    public override void OnPlayerMove(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (playerWizard is null) {
            return;
        }

        var charId = playerWizard.CharId;
        var onPad = IsInHorizontalRadius(playerObj, Radius);
        if (onPad && !_occupants.ContainsKey(charId)) {
            _occupants[charId] = new Occupant(playerActor, playerObj.m_globalID.Full, Name(playerWizard));
            Logger.Information("Hatching sigil: {0} ({1}) stepped on {2} ({3}, {4} on it now).",
                Logger.Args(Name(playerWizard), charId, PadName, IsSolo ? "solo" : "duo", _occupants.Count));
        }
        else if (!onPad && _occupants.Remove(charId)) {
            Logger.Information("Hatching sigil: {0} ({1}) stepped off {2} ({3} on it now).",
                Logger.Args(Name(playerWizard), charId, PadName, _occupants.Count));
        }
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        var sigilGid = Entity.ActiveGameObject.m_globalID.Full;
        Logger.Information("Hatching sigil: {0} ({1}) pressed {2} ({3}).",
            Logger.Args(Name(playerCharacter), playerCharacter?.CharId, PadName, IsSolo ? "solo" : "duo"));

        if (playerCharacter is null || !IsInHorizontalRadius(playerObject, Radius * INTERACTION_TOLERANCE)) {
            Logger.Warning("Hatching sigil: {0} used {1} from too far away; ignored.", Logger.Args(playerCharacter?.CharId, PadName));

            return;
        }

        if (IsSolo) {
            playerActor.Tell(new CHARACTER_103_PROTOCOL.MSG_HATCHSIGIL {
                Role = CHARACTER_103_PROTOCOL.HatchSigilRole.Solo,
                SigilGID = sigilGid,
                SigilName = PadName,
            });

            return;
        }

        // The other player on the pad (the first to step on, if somehow there are more).
        var partner = _occupants
            .Where(x => x.Key != playerCharacter.CharId)
            .Select(x => (CharId: x.Key, Occupant: x.Value))
            .FirstOrDefault();
        if (partner.Occupant is null) {
            Logger.Information("Hatching sigil: nobody else is on {0}.", Logger.Args(PadName));
            playerActor.Tell(new CHARACTER_103_PROTOCOL.MSG_HATCHSIGIL {
                Role = CHARACTER_103_PROTOCOL.HatchSigilRole.Alone,
                SigilGID = sigilGid,
                SigilName = PadName,
            });

            return;
        }

        Logger.Information("Hatching sigil: pairing {0} ({1}) with {2} ({3}) on {4}.",
            Logger.Args(Name(playerCharacter), playerCharacter.CharId, partner.Occupant.Name, partner.CharId, PadName));

        // The partner hears first, so their note comes before the hatch request it explains.
        partner.Occupant.Actor.Tell(new CHARACTER_103_PROTOCOL.MSG_HATCHSIGIL {
            Role = CHARACTER_103_PROTOCOL.HatchSigilRole.Partner,
            SigilGID = sigilGid,
            SigilName = PadName,
            PartnerCharId = playerCharacter.CharId,
            PartnerGameObjectId = playerObject.m_globalID.Full,
            PartnerName = Name(playerCharacter),
        });
        playerActor.Tell(new CHARACTER_103_PROTOCOL.MSG_HATCHSIGIL {
            Role = CHARACTER_103_PROTOCOL.HatchSigilRole.Requester,
            SigilGID = sigilGid,
            SigilName = PadName,
            PartnerCharId = partner.CharId,
            PartnerGameObjectId = partner.Occupant.GameObjectId,
            PartnerName = partner.Occupant.Name,
        });
    }

    private void LogPadOnce() {
        if (_logged || SigilInfo is null) {
            return;
        }

        _logged = true;
        Logger.Information("Hatching sigil: {0} is a {1} pad (template {2}, sigil type \"{3}\", tag2 \"{4}\", radius {5}, used radius {6}).",
            Logger.Args(PadName, IsSolo ? "solo" : "duo", (Entity.Template as GameObjectTemplate)?.m_templateID,
                SigilInfo.m_sigilType.ToString(), SigilInfo.m_zoneTag2.ToString(), SigilInfo.m_radius, Radius));
    }

    private static string Name(Wizard wizard)
        => wizard?.PlayerNameBehavior?.GetWizardName() ?? wizard?.CharId.ToString() ?? "?";

    private sealed record Occupant(IActorRef Actor, ulong GameObjectId, string Name);

}
