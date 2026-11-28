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
 *
 * ========================================================================
 * DYNAMOD SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages dynamic character state modifications and state transitions 
 * within the game server session.
 * 
 * USAGE EXAMPLE:
 * Internal service handling character state changes and dynamic 
 * modification management.
 * 
 * NOTE:
 * - Manages addition and removal of dynamic modifications
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

/// <summary>Asks the session's DynaModService to send the wizard's mods again and give these objects (tag, look) their first look.</summary>
internal sealed class RestoreQuestObjectLooks : IServerMessage {
    public byte MessageOrder { get; } = 7;
    public byte ServiceID { get; } = 105;
    public List<(string Tag, string State)> Looks { get; init; } = [];
}

internal sealed record DeferPersonaDynamodHide(string Zone, string Tag, bool Defer) : IServerMessage {
    public byte MessageOrder { get; } = 8;
    public byte ServiceID { get; } = 105;
}

/// <summary>Timer message: an object's animation on the way to a quest's state has played (States/StateTransitions).</summary>
internal sealed class DynaModPassDone {
    public string ZoneName { get; init; }
    public string ClientTag { get; init; }
    public string StateName { get; init; }
}

internal class DynaModService(SessionActor sessionActor) : MessageService(sessionActor) {

    // The game reads the list with the owner's fields, Transmit and AuthorityTransmit (ClientDynaModBehavior::
    // MSG_DynaModBehavior_UpdateMods deserializes AllMods with mask 0x18). With Transmit alone each mod lacked m_zoneID and
    // the game read every mod after the empty opening one out of step (found 2026-11-26).
    private const PropertyFlags WIRE_FLAGS = PropertyFlags.Prop_Transmit | PropertyFlags.Prop_AuthorityTransmit;

    // Live opens the full list with an empty mod at index 1, then numbers the mods after it.
    private const int SENTINEL_INDEX = 1;

    // The state RenderComponent reads as "show this object to the player".
    private const string SHOW_STATE_NAME = "On";

    private readonly Dictionary<string, HashSet<string>> _deferredPersonaHides = new(StringComparer.OrdinalIgnoreCase);

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new DynaModService(parentActor));

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceiveAttachComplete(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        var wizard = GetActiveWizard();
        SendCurrentMods(wizard);
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_ENTERSTATE))]
    private void ReceiveEnterState(CHARACTER_103_PROTOCOL.MSG_ENTERSTATE message) {
        var activeWizard = GetActiveWizard();
        var activeWizardGameObject = GetActiveGameObject();

        var objState = activeWizard.EnterState(message.StateName);
        if (objState is null) {
            Logger.Error("Failed to enter state {0} for wizard {1}", Logger.Args(message.StateName, activeWizard.CharId));

            return;
        }

        // Echo the state change to the client.
        var stateMsg = new GAME_5_PROTOCOL.MSG_ENTERSTATE {
            GameObjectID = activeWizardGameObject.m_globalID,
            State = StringHash.Compute(message.StateName)
        };

        var isPublicStateChange = !objState.m_privateState;
        if (isPublicStateChange) {
            ZoneBroadcast(stateMsg, false);
        }
        else {
            SendToSocket(stateMsg);
        }
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_ADDDYNAMOD))]
    private void ReceiveAddDynaMod(CHARACTER_103_PROTOCOL.MSG_ADDDYNAMOD message) {
        var wizard = GetActiveWizard();

        if (message.DynaMod is null) {
            Logger.Error("Received MSG_ADDDYNAMOD with null DynaMod data for wizard {0}",
                Logger.Args(wizard.CharId));
                
            return;
        }

        var zoneName = message.DynaMod.m_zoneName;
        var dynaModClientTag = message.DynaMod.m_dynaModClientTag;
        var dynaModState = message.DynaMod.m_dynaModState;

        // The object as the zone places it ("... instance" or not), so the mod reaches it (ZoneObjectTags).
        var placedTag = Imlight.CoreLib.Game.Zone.Core.ZoneObjectTags.Placed(
            string.IsNullOrEmpty(zoneName) ? wizard.Zone : (string) zoneName, (string) dynaModClientTag);
        if (!string.Equals(placedTag, (string) dynaModClientTag, StringComparison.Ordinal)) {
            Logger.Debug("Dynamod for {0}: {1} is placed as {2}; the mod goes to that.",
                Logger.Args(wizard.CharId, (string) dynaModClientTag, placedTag));
            dynaModClientTag = placedTag;
        }
        // A mod that shows its object (m_bShowsObject) and names no state brings a quest-only object out; with no state
        // it would read as a hide (Niduus 2026-10-25: quest objects shown at the wrong times).
        if (string.IsNullOrEmpty(dynaModState) && message.DynaMod.m_bShowsObject) {
            dynaModState = SHOW_STATE_NAME;
        }

        // The look the object had for this wizard before (null: the one it starts in), for the animation on the way.
        var previousState = wizard.DynamodSet?.Dynamods?.LastOrDefault(d => d is not null
            && string.Equals(d.ClientTag, dynaModClientTag, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrEmpty(zoneName) || string.IsNullOrEmpty(d.ZoneName)
                || string.Equals(d.ZoneName, zoneName, StringComparison.OrdinalIgnoreCase)))?.ModState;

        // The same mod again (a zone trigger that re-runs each time the player walks into it, a quest result run twice): the
        // game already has it, so nothing is sent.
        var unchanged = wizard.DynamodSet?.Dynamods?.Any(d => d is not null
            && string.Equals(d.ClientTag, dynaModClientTag, StringComparison.OrdinalIgnoreCase)
            && DynamodSet.ZonesOverlap(d.ZoneName, zoneName)
            && string.Equals(d.ModState ?? "", (string) dynaModState ?? "", StringComparison.OrdinalIgnoreCase)) == true;

        wizard.AddDynamod(zoneName, dynaModClientTag, dynaModState);
        Logger.Debug("Dynamod for {0}: {1} in {2} set to '{3}'{4}.",
            Logger.Args(wizard.CharId, (string) dynaModClientTag, string.IsNullOrEmpty(zoneName) ? "any zone" : (string) zoneName,
                (string) dynaModState ?? "", message.DynaMod.m_dynaModRemove ? " (removes it)" : ""));

        // A used quest object's used look is remembered with its object, so the quest safety guards can bring the
        // object back for a later step that needs it again (Game/Quests/QuestSafety).
        if (message.UsedLookTemplateId != 0 && !string.IsNullOrEmpty(dynaModClientTag)) {
            Imlight.CoreLib.Game.Quests.QuestSafety.NoteUsedLook(wizard, zoneName, dynaModClientTag, message.UsedLookTemplateId, dynaModState);
        }

        // A quest that opens a door or pops a bubble names only the state the object ends in. The object's state set says
        // what it plays on the way (Bubble_On_IDLE to Bubble_Off_END through Bubble_Xplod_PASS, Bartleby's mouth IdleUp to
        // IdleOpen through Opening): that goes first, and the end state once it has played (States/StateTransitions).
        // Sending the end state alone made the object jump there with no animation (Niduus 2026-11-13).
        var firstState = dynaModState;
        if (message.ContextActor is not null && !string.IsNullOrEmpty(dynaModState)
            && Imlight.CoreLib.Game.States.StateTransitions.TryFind(zoneName, dynaModClientTag, previousState, dynaModState,
                out var passState, out var wait)) {
            Logger.Debug("Wizard {0}: {1} plays {2} on its way to {3} ({4:0.#} s).",
                Logger.Args(wizard.CharId, dynaModClientTag, passState, dynaModState, wait.TotalSeconds));
            firstState = passState;
            Timers.StartSingleTimer($"dynamod-pass-{dynaModClientTag}",
                new DynaModPassDone { ZoneName = zoneName, ClientTag = dynaModClientTag, StateName = dynaModState }, wait);
        }
        else if (!string.IsNullOrEmpty(dynaModClientTag)) {
            Timers.Cancel($"dynamod-pass-{dynaModClientTag}");
        }

        if (unchanged && string.Equals((string) firstState ?? "", (string) dynaModState ?? "", StringComparison.OrdinalIgnoreCase)) {
            Logger.Debug("Dynamod for {0}: {1} already '{2}'; not sent again.",
                Logger.Args(wizard.CharId, (string) dynaModClientTag, (string) dynaModState ?? ""));
            SendObjectLook(zoneName, dynaModClientTag, firstState, toClient: false);

            return;
        }

        SendObjectLook(zoneName, dynaModClientTag, firstState, toClient: message.ContextActor is not null);
    }

    // A mod added by a quest result (not echoed from the client) must reach the client as well: mods on client-only
    // objects such as the arena gates are otherwise never applied. Live sends it to the owner. The zone's objects that
    // carry the tag take the state for this player too.
    private void SendObjectLook(string zoneName, string clientTag, string state, bool toClient) {
        if (toClient && !IsDeferredHide(clientTag, zoneName, state, GetActiveWizard().Zone)) {
            SendCurrentMods(GetActiveWizard(), new Dynamod { ZoneName = zoneName, ClientTag = clientTag, ModState = state });
        }

        // Broadcast the state change to the zone.
        // Objects that match the client tag will apply the state change.
        var stateChangeMsg = new ZONE_102_PROTOCOL.MSG_ENTERSTATE {
            ObjectName = clientTag,
            StateName = state,
            ExclusiveToSender = true,
            Sender = SessionActor.ActorRef
        };
        ZoneBroadcastNoPlayers(stateChangeMsg);
    }

    // The animation on the way has played: the object settles in the state the quest gave it, unless the wizard's mod
    // for it changed since or the wizard left the zone.
    [InternalMessageHandler(typeof(DynaModPassDone))]
    private void ReceiveDynaModPassDone(DynaModPassDone message) {
        try {
            var wizard = GetActiveWizard();
            var mod = wizard?.DynamodSet?.Dynamods?.LastOrDefault(d => d is not null
                && string.Equals(d.ClientTag, message.ClientTag, StringComparison.OrdinalIgnoreCase));
            if (mod is null || !string.Equals(mod.ModState, message.StateName, StringComparison.OrdinalIgnoreCase)) {
                return;
            }

            // Gone to another zone meanwhile: the mod is saved and comes with the next visit. Dynamods sometimes name only
            // the start of the zone's path ("WizardCity/Interiors"), so that still counts as the same place.
            if (!string.IsNullOrEmpty(message.ZoneName) && !string.IsNullOrEmpty(wizard.Zone)
                && !wizard.Zone.StartsWith(message.ZoneName, StringComparison.OrdinalIgnoreCase)
                && !message.ZoneName.StartsWith(wizard.Zone, StringComparison.OrdinalIgnoreCase)) {
                return;
            }

            SendObjectLook(message.ZoneName, message.ClientTag, message.StateName, toClient: true);
        }
        catch (Exception ex) {
            Logger.Error("Could not finish the animation of {0}: {1}", Logger.Args(message.ClientTag, ex.Message));
        }
    }

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_SENDDYNAMODSTATE))]
    private void ReceiveSendDynaModState(CHARACTER_103_PROTOCOL.MSG_SENDDYNAMODSTATE message) {
        // A zone-wide state a trigger gave a named object: the client applies it to the object with that tag.
        // It is not saved; it belongs to this zone instance, and goes with every list sent while the player is here.
        if (string.IsNullOrEmpty(message.ObjectName)) {
            return;
        }

        var wizard = GetActiveWizard();
        if (!string.Equals(_zoneStatesZone, wizard?.Zone, StringComparison.OrdinalIgnoreCase)) {
            _zoneStates.Clear();
            _zoneStatesZone = wizard?.Zone;
        }

        if (_zoneStates.TryGetValue(message.ObjectName, out var had)
            && string.Equals(had ?? "", message.StateName ?? "", StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        _zoneStates[message.ObjectName] = message.StateName;
        SendCurrentMods(wizard);
    }

    // States zone triggers gave named objects in the zone the player is in (MSG_SENDDYNAMODSTATE): not saved, but part of
    // every list the game gets while the player stays.
    private readonly Dictionary<string, string> _zoneStates = new(StringComparer.OrdinalIgnoreCase);
    private string _zoneStatesZone;

    [MessageHandler(typeof(CHARACTER_103_PROTOCOL.MSG_REMOVEDYNAMOD))]
    private void ReceiveRemoveDynaMod(CHARACTER_103_PROTOCOL.MSG_REMOVEDYNAMOD message) {
        var wizard = GetActiveWizard();
        string dynaModClientTag = message.DynaMod is null ? null : (string) message.DynaMod.m_dynaModClientTag;
        if (wizard is null || string.IsNullOrEmpty(dynaModClientTag)) {
            return;
        }

        var removed = wizard.DynamodSet?.Dynamods?.FirstOrDefault(d => d is not null
            && string.Equals(d.ClientTag, dynaModClientTag, StringComparison.OrdinalIgnoreCase));
        if (!wizard.RemoveDynamod(dynaModClientTag) || removed is null) {
            return;
        }

        // A mod that hid its object (Watson leaving the Scotland Yard roof in Weird Science): taking it off brings the
        // object back now, for this player, instead of only after the next zone change. The client gets its mods again.
        var hid = string.IsNullOrEmpty(removed.ModState)
            || string.Equals(removed.ModState, "Off", StringComparison.OrdinalIgnoreCase);
        if (!hid || message.ContextActor is null) {
            return;
        }

        SendCurrentMods(wizard);

        ZoneBroadcastNoPlayers(new ZONE_102_PROTOCOL.MSG_ENTERSTATE {
            ObjectName = dynaModClientTag,
            StateName = SHOW_STATE_NAME,
            ExclusiveToSender = true,
            Sender = SessionActor.ActorRef,
        });
    }

    // The quest safety guards took a used look off: the client gets the wizard's mods again and the object its first look.
    [InternalMessageHandler(typeof(RestoreQuestObjectLooks))]
    private void ReceiveRestoreQuestObjectLooks(RestoreQuestObjectLooks message) {
        try {
            var wizard = GetActiveWizard();
            SendCurrentMods(wizard);

            foreach (var (tag, state) in message.Looks ?? []) {
                if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(state)) {
                    continue;
                }

                ZoneBroadcastNoPlayers(new ZONE_102_PROTOCOL.MSG_ENTERSTATE {
                    ObjectName = tag,
                    StateName = state,
                    ExclusiveToSender = true,
                    Sender = SessionActor.ActorRef,
                });
            }
        }
        catch (Exception ex) {
            Logger.Error("Could not bring quest objects back: {0}", Logger.Args(ex.Message));
        }
    }

    [InternalMessageHandler(typeof(DeferPersonaDynamodHide))]
    private void ReceiveDeferPersonaDynamodHide(DeferPersonaDynamodHide message) {
        if (message.Defer) {
            if (!_deferredPersonaHides.TryGetValue(message.Zone, out var tags)) {
                _deferredPersonaHides[message.Zone] = tags = new(StringComparer.OrdinalIgnoreCase);
            }

            tags.Add(message.Tag);
        }
        else if (_deferredPersonaHides.TryGetValue(message.Zone, out var tags) && tags.Remove(message.Tag) && tags.Count == 0) {
            _deferredPersonaHides.Remove(message.Zone);
        }

        var wizard = GetActiveWizard();
        if (wizard is not null && string.Equals(wizard.Zone, message.Zone, StringComparison.OrdinalIgnoreCase)) {
            SendCurrentMods(wizard);
        }
    }

    private bool IsDeferredHide(string tag, string zone, string state, string currentZone)
        => !string.IsNullOrEmpty(currentZone)
            && (string.IsNullOrEmpty(state) || string.Equals(state, "Off", StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrEmpty(zone) || string.Equals(zone, currentZone, StringComparison.OrdinalIgnoreCase))
            && _deferredPersonaHides.TryGetValue(currentZone, out var tags) && tags.Contains(tag);

    // Every change goes to the game as the whole list (with <paramref name="look"/>, a look on the way to a quest's state,
    // in place of its object's saved mod), never as a single added mod, so the game's list always matches the wizard's mods
    // and the zone's trigger states.
    private void SendCurrentMods(Wizard wizard, Dynamod look = null) {
        if (wizard is null) {
            return;
        }

        if (!string.Equals(_zoneStatesZone, wizard.Zone, StringComparison.OrdinalIgnoreCase)) {
            _zoneStates.Clear();
            _zoneStatesZone = wizard.Zone;
        }

        var mods = (wizard.DynamodSet?.Dynamods ?? []).Where(mod => mod is not null).ToList();
        foreach (var (tag, state) in _zoneStates) {
            mods.Add(new Dynamod { ZoneName = wizard.Zone, ClientTag = tag, ModState = state });
        }

        if (look is not null) {
            mods.Add(look);
        }

        var sent = OnePerObject(mods.Where(mod => !IsDeferredHide(mod.ClientTag, mod.ZoneName, mod.ModState, wizard.Zone)),
            wizard.Zone, preferLater: true);
        var message = BuildUpdateAll(wizard.GameObjectID, sent);
        if (message is not null) {
            SendToSocket(message);
        }
    }

    // The wire carries no zone, and the game applies the mods in order: two mods for one tag fight, and the later wins.
    // Saved sets may hold both a zone's mod and an older "any zone" one for the same object. Each tag goes once, from the
    // mods that apply to the current zone: the latest (saved mods are kept in the order they were set, and this zone's
    // trigger states and a look on the way come after them). Mods for other zones are not sent: the game gets the whole list
    // again each time one mod changes, and a wizard far into the game carries hundreds (MoMi, 2026-11-28: 551, most of them
    // in other worlds, sent after every quest that changed one object, and the game lagged each time). They are sent with
    // the rest when the wizard comes to their zone.
    private static List<Dynamod> OnePerObject(IEnumerable<Dynamod> mods, string zone, bool preferLater) {
        var list = mods.ToList();
        var chosen = new Dictionary<string, Dynamod>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in list) {
            if (string.IsNullOrEmpty(mod.ClientTag) || !AppliesHere(mod.ZoneName, zone)) {
                continue;
            }

            if (!chosen.TryGetValue(mod.ClientTag, out var best) || preferLater
                || (mod.ZoneName?.Length ?? 0) >= (best.ZoneName?.Length ?? 0)) {
                chosen[mod.ClientTag] = mod;
            }
        }

        return list.Where(mod => AppliesHere(mod.ZoneName, zone))
            .Where(mod => string.IsNullOrEmpty(mod.ClientTag)
            || !chosen.TryGetValue(mod.ClientTag, out var best) || ReferenceEquals(best, mod)).ToList();
    }

    private static bool AppliesHere(string modZone, string zone)
        => string.IsNullOrEmpty(modZone) || string.IsNullOrEmpty(zone)
            || zone.StartsWith(modZone, StringComparison.OrdinalIgnoreCase)
            || modZone.StartsWith(zone, StringComparison.OrdinalIgnoreCase);

    private static DynaMod ToWireMod(Dynamod mod, int index) {
        // A mod with no state takes its object away: the plain DynaMod, as the game's own classes split it (DynaModShowObject
        // shows an object, DynaModDelta gives it a state).
        if (string.IsNullOrEmpty(mod.ModState)) {
            return new DynaMod { m_clientTag = mod.ClientTag ?? string.Empty, m_index = index };
        }

        return new DynaModDelta {
            m_clientTag = mod.ClientTag ?? string.Empty,
            m_index = index,
            m_stateID = StringHash.Compute(mod.ModState),
        };
    }

    private static bool TrySerialize(PropertyClass obj, out ByteString data)
        => new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None)
            .Serialize(obj, WIRE_FLAGS, out data);

    private static GAME_5_PROTOCOL.MSG_DYNAMODBEHAVIOR_UPDATEMODS BuildUpdateAll(ulong globalId, IEnumerable<Dynamod> mods) {
        var list = new DynaModList { m_allMods = [new DynaMod { m_clientTag = string.Empty, m_index = SENTINEL_INDEX }] };
        var index = SENTINEL_INDEX;
        foreach (var mod in mods.Where(m => m is not null)) {
            list.m_allMods.Add(ToWireMod(mod, ++index));
        }

        return TrySerialize(list, out var data)
            ? new GAME_5_PROTOCOL.MSG_DYNAMODBEHAVIOR_UPDATEMODS { GlobalID = globalId, UpdateAll = 1, AllMods = data }
            : null;
    }

}
