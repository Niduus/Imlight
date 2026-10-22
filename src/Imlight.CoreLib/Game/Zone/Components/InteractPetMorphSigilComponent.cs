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
 * pair the two players standing on them into the in-person hatch. Niduus, 2026-10-20: the solo pads
 * were lit but had no prompt, the duo pads were dark with no prompt.
 *
 * USAGE EXAMPLE:
 * Attaches automatically to the two sigil templates (ShouldAttachToEntity). Pressing X tells the
 * player's session a MSG_HATCHSIGIL (with the pad's position, sigil type and radius for the sigil
 * countdown); HatchService sends the player, or both players of a duo pad, to the pet morph zone.
 * Two players on a duo pad go together only once both have pressed X; the first is told to wait for the other.
 *
 * NOTE:
 * Both templates are MinigameSigilInfo pads (DynamicTriggerTemplate with BasicNPCServiceBehavior and
 * CountdownBehavior), spawned by ZoneSigilSupervisor. The solo pads' fiery ring is the separate
 * KioskSigilGraphicOn object (266073); the zone trigger ActivateHatchingSigilsTrigger only gives the
 * duo pads the dynamod state "On", which is sent here to each joining player as well. Every step is
 * logged starting "Hatching sigil".
 *
 * The solo pads have no model of their own, so the game was never sent them and showed no prompt; RenderComponent
 * now sends them. Pressing X there starts the same trip as the duo pad used alone.
 *
 * TODO:
 * - Whether live offered a MinigameSigilOption (m_successZone ThePhantomZoneWorld/PetGameMorph) instead
 *   of a plain Interact option is not known; Interact is what the game showed a prompt for.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/21/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Pet;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.Game.Sigils;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class InteractPetMorphSigilComponent(ZoneEntity entity)
    : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory, IWithTimers {

    public ITimerScheduler Timers { get; set; }

    private const string RELIGHT_TIMER_KEY = "SigilRelight";
    // The game sets the pad to a blank look when its countdown runs out (ClientCountdownBehavior), a moment after the
    // players have gone, so the lit look is sent again once that has happened (Niduus, 2026-10-21: sigil missing on both).
    private static readonly TimeSpan s_relightDelay = TimeSpan.FromSeconds(2.5);

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
    private long _nextOrder;
    // The players of a pair just sent off, until when: when both press X the second press must not pair them again
    // (Niduus, 2026-10-20: both pressed and the trips crossed).
    private readonly Dictionary<ulong, DateTime> _pairedUntil = [];
    private static readonly TimeSpan PAIRED_FOR = TimeSpan.FromSeconds(15);
    // When each player on the pad pressed X: a pair goes only once both have, so pressing never pulls in a player who
    // is only standing there (Niduus, 2026-10-21: player 1 pressed and dragged player 2 in).
    private readonly Dictionary<ulong, DateTime> _pressedAt = [];

    // The circle each player on the pad stands on. A new player takes the lowest free one and nobody else is moved
    // (Niduus, 2026-10-21: "If 1 is empty, they go to that").
    private readonly Dictionary<ulong, int> _slots = [];

    // The pad's one countdown: when it ends, and whether a second player joining has already restarted it once.
    // Hopping off and back on joins it instead of starting it over (Niduus, 2026-10-21).
    private const float COUNTDOWN_SECONDS = 10.0f;
    private const float MIN_COUNTDOWN_SECONDS = 1.0f;
    private DateTime? _countdownEnds;
    private bool _countdownRestarted;
    private static readonly TimeSpan PRESS_KEPT_FOR = TimeSpan.FromSeconds(30);
    // The players this pad has been lit for. The game shows no prompt on a pad still in its Off state: both state
    // sets (PetGameSigilStates, PetMorphFor1SigilStates) start Off (Niduus, 2026-10-20: the solo pads gave no prompt).
    private readonly HashSet<IActorRef> _litFor = [];
    // The players who stepped off the pad, and when to light it for them again: a stopped countdown leaves the pad
    // dark for that player (Niduus, 2026-10-21: the sigil went missing after stepping off).
    private readonly Dictionary<IActorRef, DateTime> _relightAt = [];
    private static readonly TimeSpan RELIGHT_AFTER = TimeSpan.FromSeconds(2);
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
        _litFor.Remove(playerActor);
        // A player coming back into the zone has a new session; an old entry would send the pair's note to the old
        // one (Niduus, 2026-10-21: one player went in alone and the other's presses were ignored).
        if (playerWizard is not null && _occupants.Remove(playerWizard.CharId)) {
            _pressedAt.Remove(playerWizard.CharId);
            _slots.Remove(playerWizard.CharId);
            ClearCountdownIfEmpty();
            Logger.Information("Hatching sigil: {0} came back into the zone; their old place on {1} is cleared.",
                Logger.Args(playerWizard.CharId, PadName));
        }

        if (PetHatching.Enabled) {
            Entity.ChangeStateExclusiveSender(LIT_STATE, playerActor);
        }
    }

    // Sent at join and again on the player's first move: a state sent while the game is still loading the zone can be
    // lost, which left the duo pads dark (Niduus, 2026-10-20).
    private void Light(IActorRef playerActor) {
        if (PetHatching.Enabled && playerActor is not null && _litFor.Add(playerActor)) {
            Entity.ChangeStateExclusiveSender(LIT_STATE, playerActor);
        }
    }

    public override void OnPlayerLeave(IActorRef playerActor, ulong id) {
        _litFor.Remove(playerActor);
        _relightAt.Remove(playerActor);
        foreach (var charId in _occupants.Where(x => x.Value.Actor.Equals(playerActor)).Select(x => x.Key).ToList()) {
            _occupants.Remove(charId);
            _pairedUntil.Remove(charId);
            _pressedAt.Remove(charId);
            _slots.Remove(charId);
            ClearCountdownIfEmpty();
            Logger.Information("Hatching sigil: {0} left the zone, so is off {1}.", Logger.Args(charId, PadName));
            RelightSoon();
        }
    }

    public override void OnPlayerMove(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (playerWizard is null) {
            return;
        }

        // The join-time state may have been sent before the game had the pad; it goes once more on the first move.
        Light(playerActor);
        // Not while the countdown draws its numbers on the pad.
        if (_relightAt.TryGetValue(playerActor, out var relightAt) && DateTime.UtcNow >= relightAt
            && !(_countdownEnds is { } running && running > DateTime.UtcNow)) {
            _relightAt.Remove(playerActor);
            if (PetHatching.Enabled) {
                Entity.ChangeStateExclusiveSender(LIT_STATE, playerActor);
            }
        }

        var charId = playerWizard.CharId;
        var delta = playerObj.m_location - Entity.ActiveGameObject.m_location;
        var onPad = _occupants.ContainsKey(playerWizard.CharId)
            ? !SigilPadCheck.IsOff(delta.X, delta.Y, Radius)
            : SigilPadCheck.IsOn(delta.X, delta.Y, Radius);
        if (onPad && _occupants.TryGetValue(charId, out var known)) {
            // Keep the session and object current: both change when the player changes zones.
            if (!known.Actor.Equals(playerActor) || known.GameObjectId != playerObj.m_globalID.Full) {
                _occupants[charId] = known with { Actor = playerActor, GameObjectId = playerObj.m_globalID.Full };
            }
        }
        else if (onPad) {
            _occupants[charId] = new Occupant(playerActor, playerObj.m_globalID.Full, Name(playerWizard), _nextOrder++);
            Logger.Information("Hatching sigil: {0} ({1}) stepped on {2} ({3}, {4} on it now).",
                Logger.Args(Name(playerWizard), charId, PadName, IsSolo ? "solo" : "duo", _occupants.Count));
        }
        else if (!onPad && _occupants.Remove(charId)) {
            _pressedAt.Remove(charId);
            _slots.Remove(charId);
            ClearCountdownIfEmpty();
            // Stepping off stops the countdown, so pressing again when back on must start a new one.
            _pairedUntil.Remove(charId);
            _relightAt[playerActor] = DateTime.UtcNow + RELIGHT_AFTER;
            // The countdown runs while anyone is on the pad; it goes off the pad for everyone once the last one leaves
            // (Niduus, 2026-10-21).
            if (_occupants.Count == 0) {
                PlayerBroadcast(new WIZARD_12_PROTOCOL.MSG_MINIGAMETIMEREND { SigilGID = Entity.ActiveGameObject.m_globalID });
            }

            RelightSoon();
            Logger.Information("Hatching sigil: {0} ({1}) stepped off {2} ({3} on it now).",
                Logger.Args(Name(playerWizard), charId, PadName, _occupants.Count));
        }
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        Logger.Information("Hatching sigil: {0} ({1}) pressed {2} ({3}).",
            Logger.Args(Name(playerCharacter), playerCharacter?.CharId, PadName, IsSolo ? "solo" : "duo"));

        if (playerCharacter is null || !IsInHorizontalRadius(playerObject, Radius * INTERACTION_TOLERANCE)) {
            Logger.Warning("Hatching sigil: {0} used {1} from too far away; ignored.", Logger.Args(playerCharacter?.CharId, PadName));

            return;
        }

        if (IsSolo) {
            playerActor.Tell(HatchSigil(CHARACTER_103_PROTOCOL.HatchSigilRole.Solo));

            return;
        }

        var now = DateTime.UtcNow;
        if (_pairedUntil.TryGetValue(playerCharacter.CharId, out var until) && now < until) {
            Logger.Information("Hatching sigil: {0} is already on the way with their partner; the press is ignored.",
                Logger.Args(playerCharacter.CharId));

            return;
        }

        // The other player on the pad (the first to step on, if somehow there are more): a duo pad holds two.
        var partner = _occupants
            .Where(x => x.Key != playerCharacter.CharId)
            .OrderBy(x => x.Value.Order)
            .Select(x => (CharId: x.Key, Occupant: x.Value))
            .FirstOrDefault();
        _pressedAt[playerCharacter.CharId] = now;
        if (partner.Occupant is null) {
            Logger.Information("Hatching sigil: nobody else is on {0}.", Logger.Args(PadName));
            var alone = HatchSigil(CHARACTER_103_PROTOCOL.HatchSigilRole.Alone);
            alone.FaceSlot = SlotOf(playerCharacter.CharId);
            alone.CountdownSeconds = CountdownForNewTrip(now);
            playerActor.Tell(alone);

            return;
        }

        // The other player is only standing there: they are not taken along until they press X too.
        if (!_pressedAt.TryGetValue(partner.CharId, out var pressed) || now - pressed > PRESS_KEPT_FOR) {
            Logger.Information("Hatching sigil: {0} pressed with {1} on {2}, who has not pressed; waiting for them.",
                Logger.Args(playerCharacter.CharId, partner.CharId, PadName));
            var wait = HatchSigil(CHARACTER_103_PROTOCOL.HatchSigilRole.WaitingForPartner);
            wait.PartnerCharId = partner.CharId;
            wait.PartnerName = partner.Occupant.Name;
            wait.FaceSlot = SlotOf(playerCharacter.CharId);
            wait.CountdownSeconds = CountdownForNewTrip(now);
            playerActor.Tell(wait);

            return;
        }

        // Both stay marked as pressed while on the pad: if one steps off and back on, pressing X again joins the
        // countdown the other is still on instead of starting a second trip (Niduus, 2026-10-21).
        _pressedAt[partner.CharId] = now;
        _pairedUntil[playerCharacter.CharId] = now + PAIRED_FOR;
        _pairedUntil[partner.CharId] = now + PAIRED_FOR;
        Logger.Information("Hatching sigil: pairing {0} ({1}) with {2} ({3}) on {4}.",
            Logger.Args(Name(playerCharacter), playerCharacter.CharId, partner.Occupant.Name, partner.CharId, PadName));

        // The partner hears first, so their note comes before the hatch request it explains.
        var toPartner = HatchSigil(CHARACTER_103_PROTOCOL.HatchSigilRole.Partner);
        toPartner.PartnerCharId = playerCharacter.CharId;
        toPartner.PartnerGameObjectId = playerObject.m_globalID.Full;
        toPartner.PartnerName = Name(playerCharacter);
        // Circles go by who pressed X first: the partner pressed before this player (Niduus, 2026-10-21: the first
        // player takes the first circle, the second player the next).
        var (seconds, keep) = CountdownForPair(now);
        toPartner.FaceSlot = SlotOf(partner.CharId);
        toPartner.CountdownSeconds = seconds;
        toPartner.KeepCountdown = keep;
        partner.Occupant.Actor.Tell(toPartner);

        var toRequester = HatchSigil(CHARACTER_103_PROTOCOL.HatchSigilRole.Requester);
        toRequester.PartnerCharId = partner.CharId;
        toRequester.PartnerGameObjectId = partner.Occupant.GameObjectId;
        toRequester.PartnerName = partner.Occupant.Name;
        toRequester.FaceSlot = SlotOf(playerCharacter.CharId);
        toRequester.CountdownSeconds = seconds;
        toRequester.KeepCountdown = keep;
        playerActor.Tell(toRequester);
    }

    private void RelightSoon() {
        if (!PetHatching.Enabled) {
            return;
        }

        Timers.StartSingleTimer(RELIGHT_TIMER_KEY, new ZONE_102_PROTOCOL.MSG_SIGILRELIGHT(), s_relightDelay);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_SIGILRELIGHT))]
    private void ReceiveRelight(ZONE_102_PROTOCOL.MSG_SIGILRELIGHT message) {
        // Lighting the pad while its countdown runs fights the countdown's numbers, which looked like a distorted
        // fade when one player stepped off (Niduus, 2026-10-21); wait until the countdown is over.
        if (_countdownEnds is { } ends && ends > DateTime.UtcNow) {
            Timers.StartSingleTimer(RELIGHT_TIMER_KEY, new ZONE_102_PROTOCOL.MSG_SIGILRELIGHT(),
                ends - DateTime.UtcNow + s_relightDelay);

            return;
        }

        Entity.ChangeState(LIT_STATE);
    }

    // What the session needs for the sigil countdown (as the dungeon sigils send it) and the way back.
    private CHARACTER_103_PROTOCOL.MSG_HATCHSIGIL HatchSigil(CHARACTER_103_PROTOCOL.HatchSigilRole role) {
        var obj = Entity.ActiveGameObject;
        var pad = obj.m_location;

        return new CHARACTER_103_PROTOCOL.MSG_HATCHSIGIL {
            Role = role,
            SigilGID = obj.m_globalID.Full,
            SigilName = PadName,
            SigilLoc = Util.GetCompactStringFromVector(new Vector4(pad.X, pad.Y, pad.Z, obj.m_orientation.Z)),
            SigilType = SigilInfo?.m_sigilType.ToString() ?? "",
            Radius = SigilInfo is not null && SigilInfo.m_radius > 0 ? SigilInfo.m_radius : Radius,
            ZoneName = Entity.Zone?.ZonePath,
        };
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

    // 0 for the first player to press X, 1 when another player on the pad pressed before them.
    private int SlotOf(ulong charId) {
        if (_slots.TryGetValue(charId, out var slot)) {
            return slot;
        }

        var taken = _slots.Where(x => x.Key != charId && _occupants.ContainsKey(x.Key)).Select(x => x.Value).ToHashSet();
        slot = 0;
        while (taken.Contains(slot)) {
            slot++;
        }

        _slots[charId] = slot;

        return slot;
    }

    private bool CountdownRunning(DateTime now, out float secondsLeft) {
        secondsLeft = _countdownEnds is { } ends ? (float) (ends - now).TotalSeconds : 0;

        return secondsLeft > 0;
    }

    // A player starting on their own: the pad's countdown if one is running, else a new one.
    private float CountdownForNewTrip(DateTime now) {
        if (CountdownRunning(now, out var left)) {
            return Math.Max(left, MIN_COUNTDOWN_SECONDS);
        }

        _countdownEnds = now.AddSeconds(COUNTDOWN_SECONDS);
        _countdownRestarted = false;

        return COUNTDOWN_SECONDS;
    }

    // A pair forming: the first time a second player joins, the countdown starts over for both; after that it goes on.
    private (float Seconds, bool Keep) CountdownForPair(DateTime now) {
        if (CountdownRunning(now, out var left) && _countdownRestarted) {
            return (Math.Max(left, MIN_COUNTDOWN_SECONDS), true);
        }

        _countdownEnds = now.AddSeconds(COUNTDOWN_SECONDS);
        _countdownRestarted = true;

        return (COUNTDOWN_SECONDS, false);
    }

    private void ClearCountdownIfEmpty() {
        if (_occupants.Count == 0) {
            _countdownEnds = null;
            _countdownRestarted = false;
        }
    }

    private sealed record Occupant(IActorRef Actor, ulong GameObjectId, string Name, long Order);

}
