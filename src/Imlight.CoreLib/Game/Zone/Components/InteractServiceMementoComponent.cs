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
 * INTERACT SERVICE MEMENTO
 * ========================================================================
 * 
 * PURPOSE:
 * Manages complex NPC interaction mechanics, tracking player proximity 
 * and service component interactions for zone entities.
 * 
 * USAGE EXAMPLE:
 * Always attached to an entity. Implement a service component to add functionality.
 * 
 * NOTE:
 * Keeps track of any components of type `IServiceComponent` attached to the entity.
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// Interface for service components that can be attached to a zone entity.
/// </summary>
public interface IServiceComponent {

    IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter);
    void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex);

    string ServiceName { get; }
    string NpcIcon { get; }
    string NpcNameKey { get; }
    string NpcTextKey { get; }
    WizBangs WizBang { get; }
    string StateName { get; }
    string InteractWizBang { get; }
    string DisplayKey { get; }

    float DEFAULT_INTERACTION_RADIUS => 300.0f;

}

internal sealed class InteractServiceMementoComponent(ZoneEntity entity) 
    : ZoneEntityComponent(entity), IComponentFactory, IWithTimers {

    private const string DEFAULT_NAME_KEY = "NPCFormats_Name";
    // Guild_Pedestal_GuildTome on the guild grounds (Housing_Guild/GuildStarter_Ext).
    private const uint GUILD_TOME_TEMPLATE_ID = 1529133;
    private const string DEFAULT_TEXT_KEY = "GUI_NPCInteractText";

    /// <summary>"Press X or click to Interact", for things that are not people.</summary>
    private const string OBJECT_TEXT_KEY = "GUI_ObjectInteract";
    private const string WIZBANG_UPDATE_TIMER_KEY = "WIZBANG_UPDATE_TIMER";
    private const double WIZBANG_UPDATE_INTERVAL_SECONDS = 1.0;
    private static readonly TimeSpan WizBangQueryTimeout = TimeSpan.FromSeconds(3);
    private bool _wizBangQueryPending;

    private readonly float _interactionRadius = 300.0f;
    private readonly Dictionary<ulong, IActorRef> _playersInInteractionRange = [];
    private readonly Dictionary<ulong, IActorRef> _playersInRenderRange = [];
    private List<IServiceComponent> _serviceComponents = [];
    private Dictionary<int, IServiceComponent> _optionIndexToComponent = [];
    private readonly Dictionary<IServiceComponent, int> _componentFirstOption = [];
    private ServiceMementoBase _serviceMemento;
    private MadlibBlock _madlibBlock;

    public ITimerScheduler Timers { get; set; }

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => true;

    public override void OnStart() {
        RefreshServiceMomento(null);

        // The component set is fixed once loaded, so an entity without services never has a wizbang.
        if (_serviceComponents.Count <= 0) {
            return;
        }

        _shape = QuestObjectShape();
        _offGroundReach = _shape is null ? OffGroundReach() : 0f;
        if (SizeFactor() > 1.5f && _serviceComponents.Any(c => c is InteractPersonaGoalComponent or InteractQuestOfferComponent)) {
            Logger.Information("Reach: {0} ({1}) in {2} is placed {3:0.#} times its size; it is reached from {4:0} away.",
                Logger.Args(Entity.Info?.m_zoneTag, (Entity.Template as GameObjectTemplate)?.m_objectName, Entity.Zone?.ZonePath,
                    Entity.Info?.m_fScale, _serviceComponents.Min(c => c.DEFAULT_INTERACTION_RADIUS) * SizeFactor()));
        }

        var updateInterval = TimeSpan.FromSeconds(WIZBANG_UPDATE_INTERVAL_SECONDS);
        var updateMsg = new ZONE_102_PROTOCOL.MSG_WIZBANGUPDATEINTERVAL();
        Timers.StartPeriodicTimer(WIZBANG_UPDATE_TIMER_KEY, updateMsg, updateInterval);
    }

    public override void OnPlayerLeave(IActorRef playerActor, ulong id) {
        _heldPrompts.Remove(playerActor);
        if (_playersInInteractionRange.Any(x => x.Value == playerActor)) {
            var playerObj = _playersInInteractionRange.First(x => x.Value == playerActor).Key;
            _playersInInteractionRange.Remove(playerObj);
        }
        
        if (_playersInRenderRange.Any(x => x.Value == playerActor)) {
            var playerObj = _playersInRenderRange.First(x => x.Value == playerActor).Key;
            _playersInRenderRange.Remove(playerObj);
        }

    }

    public override void OnPlayerMove(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (_serviceComponents.Count <= 0) {
            return;
        }

        var playerId = playerObj.m_globalID.Full;

        // A volume-held prompt (a ladder) is not the mesh reach. Moving must not open or close it.
        if (_heldPrompts.Contains(playerActor)) {
            _playersInInteractionRange[playerId] = playerActor;
            _playersInRenderRange[playerId] = playerActor;
            return;
        }

        // Effective interaction radius = the SMALLEST any attached service asks for (default 300).
        var interactionRadius = (_serviceComponents.Count > 0
            ? _serviceComponents.Min(c => c.DEFAULT_INTERACTION_RADIUS)
            : _interactionRadius) * SizeFactor();

        // Handle interaction range (for service options). A large quest object (Zafaria's fallen Zebra Scout Tower)
        // is reached at its edge too, not only near its center (ObjectReach).
        var inRange = IsInHorizontalRadius(playerObj, interactionRadius) || ReachesShape(playerObj);
        if (inRange
            && !_playersInInteractionRange.ContainsKey(playerId)) {
            _playersInInteractionRange.Add(playerId, playerActor);
            if (SizeFactor() > 1f) {
                // Said for big NPCs and objects, so a talk that is never offered can be traced (the Auroracle, 2026-11-28).
                var offered = _serviceComponents.Sum(c => {
                    try { return c.GetServiceOptions(playerWizard).Count(); } catch (Exception) { return 0; }
                });
                Logger.Information("Reach: {0} came within {1:0} of the big {2} ({3}); {4} option(s) to offer ({5}).",
                    Logger.Args(playerWizard?.CharId, interactionRadius, Entity.Info?.m_zoneTag, (Entity.Template as GameObjectTemplate)?.m_objectName,
                        offered, string.Join(", ", _serviceComponents.Select(c => c.GetType().Name))));
            }

            SendActorServiceOptions(playerActor, nextSection: true);
        }
        else if (!inRange
                 && _playersInInteractionRange.ContainsKey(playerId)) {
            _playersInInteractionRange.Remove(playerId);
            SendLeaveServiceRange(playerActor);
        }

        TryTalkToBigNpc(playerObj, playerActor, playerWizard);

        // Wizbangs go to every player in the zone, near or far (Niduus, 2026-10-25: vendors and trainers lost the
        // symbol over their heads at times). The zone's far clip is no sure measure of what the client draws, and a
        // player who has not moved yet since arriving was never counted.
        // The newest session of a wizard replaces an old one (a relog or a trip back into the zone keeps the same
        // object id): the old session no longer answers, and asking it held up this NPC's symbol for everyone.
        _playersInRenderRange[playerId] = playerActor;
    }

    // Talk steps already opened by walking up to a big NPC, by player and goal: once each.
    private readonly HashSet<(ulong Player, ulong Goal)> _bigTalksOpened = [];

    /// <summary>
    /// The game shows no talk prompt for an NPC placed much bigger than normal that stands out of walking reach (the
    /// Auroracle, PL-FROZ-NPC05, five times its size over the pool of its chamber: the server offered the talk, the game
    /// never showed it, PL-FROZ-MAIN-010, 2026-11-28). A wizard on a talk step for such an NPC who walks up to it (within
    /// the usual reach times the square root of its size) gets the talk opened as if X had been pressed, once a step.
    /// </summary>
    private void TryTalkToBigNpc(CoreObject playerObj, IActorRef playerActor, Wizard wizard) {
        try {
            var factor = SizeFactor();
            if (factor <= 1.5f || wizard is null || playerObj is null
                || _serviceComponents.OfType<InteractPersonaGoalComponent>().FirstOrDefault() is not { } talk
                || !IsInHorizontalRadius(playerObj, _interactionRadius * MathF.Sqrt(factor))) {
                return;
            }

            if (talk.GetServiceOptions(wizard).OfType<GoalEntry>().FirstOrDefault() is not { } goal
                || !_bigTalksOpened.Add((playerObj.m_globalID.Full, goal.m_goalID))) {
                return;
            }

            RefreshServiceMomento(wizard);
            var index = _componentFirstOption.TryGetValue(talk, out var first) ? (uint) first : 0u;
            Logger.Information("Reach: {0} walked up to the big {1}; its talk step opens by itself (the game shows no prompt for it).",
                Logger.Args(wizard.CharId, Entity.Info?.m_zoneTag));
            PlayerInteraction(new ZONE_102_PROTOCOL.MSG_ZONEINTERACTION {
                GlobalID = Entity.ActiveGameObject.m_globalID.Full,
                ServiceName = talk.ServiceName,
                ServiceIndex = index,
                PlayerActor = playerActor,
                PlayerCharacter = wizard,
                PlayerObject = playerObj,
            });
        }
        catch (Exception e) {
            Logger.Debug("Reach: opening the talk of {0} failed: {1}", Logger.Args(Entity.Info?.m_zoneTag, e.Message));
        }
    }

    // The largest reach a big placed object gets (its size times the usual reach).
    private const float MAX_SIZED_REACH = 1500f;

    /// <summary>
    /// How much bigger than usual the zone placed this object: its reach grows with it. The Auroracle (PL-FROZ-NPC05,
    /// PL-FROZ-MAIN-010) is placed five times its size; measured as a normal NPC, nobody could stand close enough to its
    /// centre to talk to it (2026-11-28).
    /// </summary>
    private float SizeFactor() {
        var scale = Entity.Info?.m_fScale ?? 1f;
        if (float.IsNaN(scale) || scale <= 1f) {
            return 1f;
        }

        return MathF.Min(scale, MAX_SIZED_REACH / MathF.Max(1f, _interactionRadius));
    }

    // The shape of a quest object that is not a character, when it is larger than the usual reach (ObjectReach).
    private ObjectReach.Shape _shape;

    private ObjectReach.Shape QuestObjectShape() {
        try {
            if (!_serviceComponents.Any(c => c is InteractQuestSelectComponent)
                || Entity.Template is not GameObjectTemplate template
                || template.m_behaviors?.Any(b => b is NPCBehaviorTemplate) == true) {
                return null;
            }

            return ObjectReach.For(template);
        }
        catch (Exception e) {
            Logger.Debug("Object reach: no shape for {0}: {1}", Logger.Args(Entity.Info?.m_zoneTag, e.Message));

            return null;
        }
    }

    // How far a player may stand from a quest object whose own spot is off the ground players walk on (0: the usual reach).
    private float _offGroundReach;

    // The furthest a quest object off the walk ground is reached from (its nearest ground further than this is no shore).
    private const float OFF_GROUND_REACH_MAX = 1500f;

    /// <summary>
    /// A quest object with no collision shape whose spot is not on the ground players walk on (Kondha's BlightedWater, the
    /// pool "Heal" of KR-KOND-C08-002 asks for: its spot lies in the pool, about 500 past the Water Bug Cave sigil and off
    /// the zone's walk ground) is reached from the nearest ground instead: from there plus the usual edge reach. Measured
    /// from its spot alone, nobody could stand close enough to be offered it (2026-11-27).
    /// </summary>
    private float OffGroundReach() {
        try {
            if (!_serviceComponents.Any(c => c is InteractQuestSelectComponent)
                || Entity.Template is not GameObjectTemplate template
                || template.m_behaviors?.Any(b => b is NPCBehaviorTemplate) == true
                || Entity.Zone?.ZonePath is not { Length: > 0 } zone
                || Entity.ActiveGameObject is not { } self) {
                return 0f;
            }

            var at = new System.Numerics.Vector3(self.m_location.X, self.m_location.Y, self.m_location.Z);
            if (Imlight.CoreLib.Game.Zone.Core.SafeLanding.Problem(zone, at) is not { } why
                || Imlight.CoreLib.Game.Zone.Core.SafeLanding.Check(zone, at) is not { Moved: true } ground) {
                return 0f;
            }

            var dx = ground.Spot.X - at.X;
            var dy = ground.Spot.Y - at.Y;
            var reach = MathF.Sqrt((dx * dx) + (dy * dy)) + ObjectReach.EDGE_REACH;
            if (reach <= _interactionRadius || reach > OFF_GROUND_REACH_MAX) {
                return 0f;
            }

            Logger.Information("Quest object {0} in {1}: its spot is {2}; it is reached from the nearest ground, {3:0} away (reach {4:0}).",
                Logger.Args(Entity.Info?.m_zoneTag, zone, why, reach - ObjectReach.EDGE_REACH, reach));

            return reach;
        }
        catch (Exception e) {
            Logger.Debug("Object reach: no ground reach for {0}: {1}", Logger.Args(Entity.Info?.m_zoneTag, e.Message));

            return 0f;
        }
    }

    private bool ReachesShape(CoreObject playerObj) {
        if (_offGroundReach > 0f && playerObj is not null) {
            var center = Entity.ActiveGameObject.m_location;
            var dx = playerObj.m_location.X - center.X;
            var dy = playerObj.m_location.Y - center.Y;
            if ((dx * dx) + (dy * dy) <= _offGroundReach * _offGroundReach) {
                return true;
            }
        }

        if (_shape is null || playerObj is null) {
            return false;
        }

        var self = Entity.ActiveGameObject;

        return _shape.Reaches(self.m_location, self.m_orientation.Z, Entity.Info?.m_fScale ?? 1f, playerObj.m_location);
    }

    public override void OnPlayerJoin(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (_serviceComponents.Count <= 0 || playerObj is null) {
            return;
        }

        var playerId = playerObj.m_globalID.Full;
        var interactionRadius = _serviceComponents.Min(c => c.DEFAULT_INTERACTION_RADIUS) * SizeFactor();
        if (IsInHorizontalRadius(playerObj, interactionRadius) || ReachesShape(playerObj)) {
            _playersInInteractionRange[playerId] = playerActor;
            SendActorServiceOptions(playerActor, nextSection: false);
        }

        _playersInRenderRange[playerId] = playerActor;
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONEINTERACTION))]
    private void PlayerInteraction(ZONE_102_PROTOCOL.MSG_ZONEINTERACTION message) {
        var playerActor = message.PlayerActor;
        var playerCharacter = message.PlayerCharacter;
        var playerObject = message.PlayerObject;
        var serviceName = message.ServiceName;
        var serviceIndex = message.ServiceIndex;
        var reinteract = message.Reinteract;

        Logger.Debug("Player {0} interacted with NPC {1} ({5}, template {6}) using service {2} at index {3} (Reinteract: {4})",
            Logger.Args(playerActor.Path.Name, Entity.ActiveGameObject.m_globalID.Full, serviceName, serviceIndex, reinteract,
                Entity.Info?.m_zoneTag is { Length: > 0 } tag ? tag : Entity.ActiveGameObject.m_debugName.ToString(),
                Entity.ActiveGameObject.m_templateID));

        // The Guild Tome on the guild grounds shows the guild; the live game opens the guild window there.
        if (Entity.ActiveGameObject.m_templateID == GUILD_TOME_TEMPLATE_ID && playerCharacter is not null) {
            var guild = Imlight.CoreLib.Game.Social.Guilds.For(playerCharacter.CharId);
            playerActor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE {
                Message = guild is null
                    ? "You are not in a guild yet. Type .guild create <name> to make one, or ask a guild member to .guild invite you. Type .guild for the guild commands."
                    : Imlight.CoreLib.Game.Commands.Protocols.CommandGuildProtocol.DescribeGuild(guild),
                Modal = 1,
            });

            return;
        }

        if (_serviceComponents.Count <= 0) {
            Logger.Warning("No service components found for NPC {0}",
                Logger.Args(Entity.ActiveGameObject.m_debugName));

            return;
        }

        // If this is a reinteract scenario, send service range messages first.
        if (reinteract == 2) {
            SendLeaveServiceRange(playerActor);
            SendActorServiceOptions(playerActor, reinteract);
        }

        // Route to the component that owns this option index.
        // Using FirstOrDefault by ServiceName is unsafe when multiple components share
        // the same service name (e.g. InteractQuestSelectComponent shadowing WoodenChestComponent).
        _optionIndexToComponent.TryGetValue((int) serviceIndex, out var serviceComponent);

        // The list the game clicked can be older than ours (a quest just finished and its "underway" option took
        // the offer's place): the click went to a service that showed nothing, and the dialog hung on an empty
        // Back/More bar (Niduus 2026-10-25, Lady Oriel). Build the list again for this player, and if the index
        // still names another service, go by the service name the game sent.
        if (!NameMatches(serviceComponent, serviceName) && playerCharacter is not null) {
            RefreshServiceMomento(playerCharacter);
            _optionIndexToComponent.TryGetValue((int) serviceIndex, out serviceComponent);
            if (!NameMatches(serviceComponent, serviceName)
                && _componentFirstOption.Keys.FirstOrDefault(c => NameMatches(c, serviceName)) is { } named) {
                serviceComponent = named;
                serviceIndex = (uint) _componentFirstOption[named];
            }
        }

        // The list can be one built for another wizard (a shared area): a door click from a wizard whose step says to use
        // this object counts for the step instead (see RefreshServiceMomento).
        if (serviceComponent is InteractDoorTeleportComponent && playerCharacter is not null
            && _serviceComponents.OfType<InteractQuestSelectComponent>().FirstOrDefault() is { } questUse
            && OptionsFor(questUse, playerCharacter).Count > 0) {
            serviceComponent = questUse;
            serviceIndex = _componentFirstOption.TryGetValue(questUse, out var questFirst) ? (uint) questFirst : 0;
        }

        // A collect/usage object can own both state and quest services. The active quest step wins over a stale
        // wooden-chest, vendor or door GUI even when the game sends an old flat index after quest progress changes.
        if (playerCharacter is not null
            && _serviceComponents.OfType<InteractQuestSelectComponent>().FirstOrDefault() is { } activeQuestUse
            && OptionsFor(activeQuestUse, playerCharacter).Count > 0) {
            serviceComponent = activeQuestUse;
            serviceIndex = _componentFirstOption.TryGetValue(activeQuestUse, out var questFirst) ? (uint) questFirst : 0;
        }

        if (serviceComponent is null) {
            Logger.Warning("No component owns service index {0} for NPC {1} with service name {2}",
                Logger.Args(serviceIndex, Entity.ActiveGameObject.m_debugName, serviceName));
            EndTalk(playerActor, $"nothing at option {serviceIndex} ({serviceName})");

            return;
        }

        // Call the service component's interaction method with the index of its own option.
        var localIndex = _componentFirstOption.TryGetValue(serviceComponent, out var first)
            ? serviceIndex - (uint) first
            : serviceIndex;
        if (serviceComponent is InteractDungeonSigilComponent sigil) {
            sigil.NextRequestedMode = message.RequestedSigilMode;
        }
        if (serviceComponent is InteractQuestOfferComponent offer) {
            offer.NextOfferChained = reinteract == 2;
        }

        serviceComponent.OnServiceInteraction(playerActor, playerCharacter, playerObject, localIndex);
    }

    // A click without a service name (or a component without one) is taken as meant for that component.
    private static bool NameMatches(IServiceComponent component, string serviceName)
        => component is not null
           && (string.IsNullOrEmpty(serviceName) || string.IsNullOrEmpty(component.ServiceName)
               || string.Equals(component.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A quest this NPC offered straight after the last one was answered. The game came into that offer through the
    /// server's "talk again" (Reinteract 2) and never left the talk by itself: after accepting The Cure from Gretta
    /// Darkkettle the camera stayed on her while the wizard walked away (Niduus 2026-11-02). Let the game out of the
    /// talk, as with an empty one, and give the NPC's options back.
    /// </summary>
    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_RELEASECHAINEDTALK))]
    private void ReleaseChainedTalk(ZONE_102_PROTOCOL.MSG_RELEASECHAINEDTALK message) {
        if (message.PlayerActor is null) {
            return;
        }

        Logger.Information("Letting {0} out of the talk with NPC {1}: the follow-on quest {2} was {3}.",
            Logger.Args(message.PlayerActor.Path.Name,
                Entity.Info?.m_zoneTag is { Length: > 0 } tag ? tag : Entity.ActiveGameObject.m_debugName.ToString(),
                message.QuestName, message.Accepted ? "accepted" : "declined"));
        ReleaseTalk(message.PlayerActor);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_REFRESHSERVICEOPTIONS))]
    private void RefreshServiceOptions(ZONE_102_PROTOCOL.MSG_REFRESHSERVICEOPTIONS message) {
        // The client drops an NPC's options once they are used. Live sends them again when the dialog
        // closes; without that the player has to leave the NPC's range and come back to talk again.
        if (_serviceComponents.Count <= 0 || !_playersInInteractionRange.ContainsValue(message.PlayerActor)) {
            return;
        }

        if (AskWizard(message.PlayerActor) is not { } wizard) {
            return;
        }
        foreach (var trainer in Entity.GetComponentsOfType<InteractTrainerComponent>()) {
            trainer.ResetSpellOptions(wizard);
        }

        SendLeaveServiceRange(message.PlayerActor);
        SendActorServiceOptions(message.PlayerActor);
    }

    // The player's wizard, or null when their session does not answer in time. Every component of an entity runs on
    // its one actor, so waiting on a session that closed (the game shut while next to the NPC) froze the whole NPC:
    // no one who came into the zone afterwards was sent it (Niduus 2026-10-25, Majid: "he gone").
    private Wizard AskWizard(IActorRef playerActor) {
        try {
            return playerActor.Ask<CHARACTER_103_PROTOCOL.MSG_CHARACTER>(new CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD(),
                WizBangQueryTimeout).Result?.Wizard;
        }
        catch (Exception ex) {
            Logger.Warning("{0} did not answer for {1}'s options: {2}",
                Logger.Args(playerActor?.Path.Name, Entity.ActiveGameObject?.m_debugName, ex.GetBaseException().Message));

            return null;
        }
    }

    private readonly HashSet<IActorRef> _heldPrompts = [];

    /// <summary>Shows Press-X because the player is inside this object's trigger volume, not because they are near the mesh.</summary>
    internal void HoldPrompt(IActorRef playerActor) {
        if (playerActor is null) {
            return;
        }

        _heldPrompts.Add(playerActor);
        SendActorServiceOptions(playerActor);
    }

    /// <summary>Clears a volume-held Press-X when the player leaves that trigger volume.</summary>
    internal void ReleasePrompt(IActorRef playerActor) {
        if (playerActor is null || !_heldPrompts.Remove(playerActor)) {
            return;
        }

        SendLeaveServiceRange(playerActor);
    }

    /// <summary>Sends a player this NPC's options again without closing what the game shows (a quest dialog).</summary>
    internal void SendServiceOptions(IActorRef playerActor)
        => SendActorServiceOptions(playerActor);

    internal void ShowServiceOptions(IActorRef playerActor, int reinteract = 0) {
        SendLeaveServiceRange(playerActor);
        SendActorServiceOptions(playerActor, reinteract);
    }

    /// <summary>
    /// Lets the game out of a talk that has nothing to show. A click (or the server's own step to the next quest)
    /// that sends no dialog left the camera on the NPC until the player logged out (Niduus 2026-10-27, Sergeant
    /// Muldoon after Firecat Alley's last talk). Clears the game's interactions, then gives the NPC's options back
    /// so it can still be talked to.
    /// </summary>
    internal void EndTalk(IActorRef playerActor, string why) {
        if (playerActor is null) {
            return;
        }

        Logger.Warning("Ending an empty talk with NPC {0} for {1}: {2}",
            Logger.Args(Entity.Info?.m_zoneTag is { Length: > 0 } tag ? tag : Entity.ActiveGameObject.m_debugName.ToString(),
                playerActor.Path.Name, why));
        ReleaseTalk(playerActor);
    }

    // Clears the game's interactions (which lets the camera go), then gives the NPC's options back.
    private void ReleaseTalk(IActorRef playerActor) {
        playerActor.Tell(new WIZARD2_53_PROTOCOL.MSG_CLEARALLINTERACTIONS());
        SendLeaveServiceRange(playerActor);
        if (_playersInInteractionRange.ContainsValue(playerActor)) {
            SendActorServiceOptions(playerActor);
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_WIZBANGUPDATEINTERVAL))]
    private void HandleWizBangUpdateInterval(ZONE_102_PROTOCOL.MSG_WIZBANGUPDATEINTERVAL message) {
        if (_serviceComponents.Count <= 0 || _playersInRenderRange.Count == 0 || _wizBangQueryPending) {
            return;
        }

        // Query every nearby player's wizard concurrently instead of
        // blocking on each one sequentially. A player who does not answer in time (a session that is gone) is
        // dropped, so it cannot hold up the symbol for the others.
        _wizBangQueryPending = true;
        var playerActors = _playersInRenderRange.Values.ToArray();
        var queryTasks = playerActors.Select(async playerActor => {
            try {
                var msg = new CHARACTER_103_PROTOCOL.MSG_QUERYACTIVEWIZARD();
                var rsp = await playerActor.Ask<CHARACTER_103_PROTOCOL.MSG_CHARACTER>(msg, WizBangQueryTimeout);
                return rsp.Wizard;
            }
            catch {
                return null;
            }
        });

        Task.WhenAll(queryTasks)
            .ContinueWith(t => {
                var wizards = t.Result.ToArray();
                return new ZONE_102_PROTOCOL.MSG_WIZBANG_UPDATE_RESULT {
                    PlayerActors = playerActors,
                    Wizards = wizards
                };
            })
            .PipeTo(Self);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_WIZBANG_UPDATE_RESULT))]
    private void HandleWizBangUpdateResult(ZONE_102_PROTOCOL.MSG_WIZBANG_UPDATE_RESULT result) {
        _wizBangQueryPending = false;
        for (int i = 0; i < result.PlayerActors.Length; i++) {
            var wizard = result.Wizards[i];
            if (wizard != null) {
                SendWizBang(result.PlayerActors[i], wizard);
                // A volume-held Press-X (a ladder) is sent once when the player steps in, as a door's is. Sending it
                // again on every wizbang tick was dropped (2026-11-25).

                continue;
            }

            var gone = _playersInRenderRange.Where(x => x.Value.Equals(result.PlayerActors[i])).Select(x => x.Key).ToList();
            foreach (var playerId in gone) {
                _playersInRenderRange.Remove(playerId);
            }
        }
    }

    private void SendActorServiceOptions(IActorRef playerActor, int reinteract = 0, bool nextSection = false) {
        if (AskWizard(playerActor) is not { } wizard) {
            return;
        }

        // Each time a player walks up to a test trainer with a long list, it shows the next section of it.
        if (nextSection) {
            foreach (var trainer in Entity.GetComponentsOfType<InteractTrainerComponent>()) {
                trainer.NextSection(wizard);
            }
        }

        RefreshServiceMomento(wizard);

        if (SizeFactor() > 1.5f) {
            Logger.Information("Reach: {0} ({1}) offers {2} option(s) to {3}: {4}.",
                Logger.Args(Entity.Info?.m_zoneTag, (Entity.Template as GameObjectTemplate)?.m_objectName,
                    _serviceMemento.m_serviceOptions.Count, wizard.CharId,
                    string.Join(", ", _serviceComponents.Select(c => {
                        try { return $"{c.GetType().Name} {c.GetServiceOptions(wizard).Count()}"; } catch (Exception e) { return $"{c.GetType().Name} failed ({e.Message})"; }
                    }))));
        }

        // If we have no service options, do not send anything.
        if (_serviceMemento.m_serviceOptions.Count <= 0) {
            if (Entity.Template is GameObjectTemplate transportTemplateWithoutOptions
                && InteractDoorTeleportComponent.HasTransportName(transportTemplateWithoutOptions.m_objectName.ToString())) {
                Logger.Information("Transport prompt: {0} has no service options for {1}.",
                    Logger.Args(Entity.Info?.m_zoneTag ?? transportTemplateWithoutOptions.m_objectName.ToString(), playerActor.Path.Name));
            }
            return;
        }

        // Serialize the service memento and send it to the player.
        var serializer = new ObjectSerializer(
            Versionable: false,
            Behaviors: SerializerFlags.None
        );
        if (!serializer.Serialize(_serviceMemento, 4, out var data)) {
            Logger.Error("Failed to serialize service memento for NPC {0}",
                Logger.Args(Entity.ActiveGameObject.m_debugName));

            return;
        }

        var npcOptionsMsg = new QUEST_MESSAGES_52_PROTOCOL.MSG_SENDNPCOPTIONS {
            // Do not let this property name fool you. 
            // The client incorrectly labels this property as "MobileID." It is in fact
            // the global ID of the NPC. It will not work if you set it to the mobile ID.
            MobileID = Entity.ActiveGameObject.m_globalID.Full,
            Options = data,
            Reinteract = reinteract
        };

        playerActor.Tell(npcOptionsMsg);
        if (Entity.Template is GameObjectTemplate transportTemplateWithOptions
            && InteractDoorTeleportComponent.HasTransportName(transportTemplateWithOptions.m_objectName.ToString())) {
            Logger.Information("Transport prompt: sent {0} option(s) for {1} to {2}.",
                Logger.Args(_serviceMemento.m_serviceOptions.Count,
                    Entity.Info?.m_zoneTag ?? transportTemplateWithOptions.m_objectName.ToString(),
                    playerActor.Path.Name));
        }
    }

    private void SendLeaveServiceRange(IActorRef playerActor) {
        var msg = new GAME_5_PROTOCOL.MSG_LEAVESERVICERANGE {
            // Do not let this property name fool you. 
            // The client incorrectly labels this property as "MobileID." It is in fact
            // the global ID of the NPC. It will not work if you set it to the mobile ID.
            MobileID = Entity.ActiveGameObject.m_globalID.Full
        };

        playerActor.Tell(msg);
    }

    private void RefreshServiceMomento(Wizard playerCharacter) {
        // A trainer lists one option per spell. Its options go last so an NPC that also runs a shop (Abner) keeps
        // the shop's option where the game shows it, instead of after hundreds of spells.
        _serviceComponents = [.. Entity.GetComponentsOfType<IServiceComponent>().OrderBy(c => c is InteractTrainerComponent ? 1 : 0)];
        if (_serviceComponents.Count <= 0) {
            return;
        }

        var gameObjTemplate = Entity.Template as GameObjectTemplate;

        // Get all service options and track which component owns each flat index.
        _optionIndexToComponent.Clear();
        _componentFirstOption.Clear();
        var allOptions = new List<ServiceOptionBase>();
        var componentsWithOptions = new List<IServiceComponent>();
        // An object that is both a door and a lit-up-and-go object (the Dragonspyre cathedral arches) offered two "Interact"
        // options, so pressing X opened a person's "What can I do for you?" menu (2026-11-20 report). It keeps one: the
        // object's own use when that sends the wizard through (it plays the arch lighting up too), else the door.
        var door = _serviceComponents.OfType<InteractDoorTeleportComponent>().FirstOrDefault();
        var objectState = _serviceComponents.OfType<InteractObjectStateComponent>().FirstOrDefault();
        var dropped = new HashSet<IServiceComponent>();
        if (door is not null && objectState is not null && playerCharacter is not null
            && OptionsFor(door, playerCharacter).Count > 0 && OptionsFor(objectState, playerCharacter).Count > 0) {
            dropped.Add(objectState.UseTeleports(playerCharacter) ? door : objectState);
        }

        // An object that is a door and also the thing a wizard's quest step says to use (Dragonspyre's Time Crystal in the
        // Hall of Time: "The Time is Right" says use it, and its other option sends a wizard to the Grand Chasm's past)
        // counts for the step first, as live. The door went first, so the step never counted and Ultimus went to the past
        // and back 31 times in 20 minutes (2026-11-21 23:10-23:30). Once the step is done, the door is the only option again.
        var questUse = _serviceComponents.OfType<InteractQuestSelectComponent>().FirstOrDefault();
        if (door is not null && questUse is not null && playerCharacter is not null
            && OptionsFor(door, playerCharacter).Count > 0 && OptionsFor(questUse, playerCharacter).Count > 0) {
            dropped.Add(door);
        }

        // The same for an object with a use of its own (Last Wood's fire pits: put out, and "set off the smoke signal" of
        // Planning the Attack). Two "Interact" options opened a person's "What can I do for you?" menu with two blank
        // buttons (2026-11-25). While the wizard's step needs the object, the step's use is the only option.
        if (objectState is not null && questUse is not null && playerCharacter is not null
            && OptionsFor(questUse, playerCharacter).Count > 0) {
            dropped.Add(objectState);
        }

        foreach (var component in _serviceComponents) {
            var componentOptions = dropped.Contains(component) ? [] : OptionsFor(component, playerCharacter);
            if (componentOptions.Count > 0) {
                componentsWithOptions.Add(component);
            }

            if (componentOptions.Count > 0) {
                _componentFirstOption[component] = allOptions.Count;
            }

            foreach (var option in componentOptions) {
                // The client echoes this index, so it has to be the flat index used to route it.
                option.m_serviceIndex = (uint) allOptions.Count;
                _optionIndexToComponent[allOptions.Count] = component;
                allOptions.Add(option);
            }
        }

        // Get UI overrides based on priority.
        // A component with nothing to offer must not set the dialog's icon, name and text (a quest
        // interactable that is currently unused would otherwise shadow an "Enter door" prompt).
        var sortedComponents = SortComponentsByPriority(componentsWithOptions.Count > 0 ? componentsWithOptions : _serviceComponents);
        var highestPriority = sortedComponents.FirstOrDefault();

        SetMadLibBlock();

        var npcIconOrDefault = string.IsNullOrEmpty(highestPriority?.NpcIcon)
            ? gameObjTemplate?.m_sIcon.ToString()
            : highestPriority.NpcIcon;
        var npcNameKeyOrDefault = string.IsNullOrEmpty(highestPriority?.NpcNameKey)
            ? DEFAULT_NAME_KEY
            : highestPriority.NpcNameKey;
        // Only a person is talked to: a lever, chest or portal with no text of its own says "to Interact", as live.
        var isPerson = gameObjTemplate?.m_behaviors?.OfType<NPCBehaviorTemplate>().Any() ?? true;
        var npcTextKeyOrDefault = string.IsNullOrEmpty(highestPriority?.NpcTextKey)
            ? (isPerson ? DEFAULT_TEXT_KEY : OBJECT_TEXT_KEY)
            : highestPriority.NpcTextKey;

        _serviceMemento = new ServiceMementoBase {
            m_npcIcon = npcIconOrDefault ?? string.Empty,
            m_npcNameKey = npcNameKeyOrDefault ?? DEFAULT_NAME_KEY,
            m_npcTextKey = npcTextKeyOrDefault ?? DEFAULT_TEXT_KEY,
            m_serviceOptions = allOptions,
            m_personaMadlibs = _madlibBlock
        };
    }

    private void SetMadLibBlock() {
        // NPCs normally have a madlib of first name, last name, and title.
        // To avoid hardcoding these values, we use the display name of the template.
        // We'll also set the madlib token to just "NAME" so the client displays the name as-is.
        if (Entity.Template is not GameObjectTemplate gameObjTemplate) {
            return;
        }

        var madlibList = new List<MadlibArg> {
            new MadlibArgT_ByteString() {
                m_madlibArgument = gameObjTemplate.m_displayName,
                m_madlibToken = "NAME"
            },
        };

        // A custom vendor or trainer's dialog shows the name it was given, as written (CustomNpcs), and so does a
        // teleporter hub's stone (CustomTeleporters).
        var teleporter = Imlight.CoreLib.Game.World.CustomTeleporters.For(Entity.Info?.m_zoneTag);
        if ((Imlight.CoreLib.Game.World.CustomNpcs.NameFor(Entity.Info?.m_zoneTag)
             ?? Imlight.CoreLib.Game.World.CustomTeleporters.NameOf(teleporter)) is { } customName) {
            madlibList = [new MadlibArgT_WideByteString { m_madlibArgument = customName, m_madlibToken = "NAME" }];
        }

        // A portal's Custom prompt line: its text key is the lang line that is only this block's FIRSTNAME.
        if (teleporter?.CustomText is { Length: > 0 } customText) {
            madlibList.Add(new MadlibArgT_WideByteString { m_madlibArgument = customText, m_madlibToken = "FIRSTNAME" });
        }

        _madlibBlock = new MadlibBlock() {
            m_blockToken = "NPC",
            m_madlibs = madlibList
        };
    }

    private readonly HashSet<string> _failedComponents = [];

    /// <summary>
    /// A component's options for this player. One component that throws must not take away the NPC's other
    /// services or its icon, so it counts as having none (and is logged once).
    /// </summary>
    private List<ServiceOptionBase> OptionsFor(IServiceComponent component, Wizard wizard) {
        try {
            return [.. component.GetServiceOptions(wizard) ?? []];
        }
        catch (Exception ex) {
            if (_failedComponents.Add(component.GetType().Name)) {
                Logger.Error("{0} on NPC {1} failed to list its services: {2}",
                    Logger.Args(component.GetType().Name, Entity.ActiveGameObject?.m_debugName, ex));
            }

            return [];
        }
    }

    private void SendWizBang(IActorRef playerActor, Wizard playerWizard) {
        if (_serviceComponents.Count <= 0) {
            return;
        }

        // Collect wizbangs from components that have service options for this player.
        var activeWizBangs = new List<WizBangs>();
        foreach (var component in _serviceComponents) {
            var serviceOptions = OptionsFor(component, playerWizard);
            if (serviceOptions.Count > 0) {
                activeWizBangs.Add(component.WizBang);
            }
        }

        // Get highest priority wizbang for this player, or None to clear.
        var wizBang = WizBangPriority.GetHighestPriorityWizBang(activeWizBangs);

        // Re-send every tick rather than once per change: a single send can be lost
        // while the client is still building the scene, and retail re-pushes each update.
        var wizBangMsg = new GAME_5_PROTOCOL.MSG_WIZBANG {
            WizBangID = (uint) wizBang,
            GameObjectID = Entity.ActiveGameObject.m_globalID.Full
        };
        playerActor.Tell(wizBangMsg);
    }

    private static IOrderedEnumerable<IServiceComponent> SortComponentsByPriority(IEnumerable<IServiceComponent> components) {
        // Sort by WizBang priority
        var wizBangs = components.Select(c => c.WizBang);
        var prioritySortedWizBangs = WizBangPriority.GetPrioritySortedWizBangs([.. wizBangs]);

        // If priority sorted WizBangs are empty, sort by default.
        if (prioritySortedWizBangs is null || prioritySortedWizBangs.Count <= 0) {
            return components.OrderBy(c => c.WizBang);
        }

        return components.OrderBy(c => prioritySortedWizBangs.IndexOf(c.WizBang));
    }

}
