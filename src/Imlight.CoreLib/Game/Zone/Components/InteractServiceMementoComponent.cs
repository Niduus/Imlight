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

        var updateInterval = TimeSpan.FromSeconds(WIZBANG_UPDATE_INTERVAL_SECONDS);
        var updateMsg = new ZONE_102_PROTOCOL.MSG_WIZBANGUPDATEINTERVAL();
        Timers.StartPeriodicTimer(WIZBANG_UPDATE_TIMER_KEY, updateMsg, updateInterval);
    }

    public override void OnPlayerLeave(IActorRef playerActor, ulong id) {
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

        // Effective interaction radius = the SMALLEST any attached service asks for (default 300).
        var interactionRadius = _serviceComponents.Count > 0
            ? _serviceComponents.Min(c => c.DEFAULT_INTERACTION_RADIUS)
            : _interactionRadius;

        // Handle interaction range (for service options).
        if (IsInHorizontalRadius(playerObj, interactionRadius)
            && !_playersInInteractionRange.ContainsKey(playerId)) {
            _playersInInteractionRange.Add(playerId, playerActor);
            SendActorServiceOptions(playerActor, nextSection: true);
        }
        else if (!IsInHorizontalRadius(playerObj, interactionRadius)
                 && _playersInInteractionRange.ContainsKey(playerId)) {
            _playersInInteractionRange.Remove(playerId);
            SendLeaveServiceRange(playerActor);
        }

        // Wizbangs go to every player in the zone, near or far (Niduus, 2026-10-25: vendors and trainers lost the
        // symbol over their heads at times). The zone's far clip is no sure measure of what the client draws, and a
        // player who has not moved yet since arriving was never counted.
        // The newest session of a wizard replaces an old one (a relog or a trip back into the zone keeps the same
        // object id): the old session no longer answers, and asking it held up this NPC's symbol for everyone.
        _playersInRenderRange[playerId] = playerActor;
    }

    public override void OnPlayerJoin(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (_serviceComponents.Count <= 0 || playerObj is null) {
            return;
        }

        _playersInRenderRange[playerObj.m_globalID.Full] = playerActor;
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

        if (serviceComponent is null) {
            Logger.Warning("No component owns service index {0} for NPC {1} with service name {2}",
                Logger.Args(serviceIndex, Entity.ActiveGameObject.m_debugName, serviceName));

            return;
        }

        // Call the service component's interaction method with the index of its own option.
        var localIndex = _componentFirstOption.TryGetValue(serviceComponent, out var first)
            ? serviceIndex - (uint) first
            : serviceIndex;
        serviceComponent.OnServiceInteraction(playerActor, playerCharacter, playerObject, localIndex);
    }

    // A click without a service name (or a component without one) is taken as meant for that component.
    private static bool NameMatches(IServiceComponent component, string serviceName)
        => component is not null
           && (string.IsNullOrEmpty(serviceName) || string.IsNullOrEmpty(component.ServiceName)
               || string.Equals(component.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase));

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

    internal void ShowServiceOptions(IActorRef playerActor, int reinteract = 0) {
        SendLeaveServiceRange(playerActor);
        SendActorServiceOptions(playerActor, reinteract);
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

        // If we have no service options, do not send anything.
        if (_serviceMemento.m_serviceOptions.Count <= 0) {
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
        foreach (var component in _serviceComponents) {
            var componentOptions = OptionsFor(component, playerCharacter);
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
        if ((Imlight.CoreLib.Game.World.CustomNpcs.NameFor(Entity.Info?.m_zoneTag)
             ?? Imlight.CoreLib.Game.World.CustomTeleporters.For(Entity.Info?.m_zoneTag)?.Name) is { } customName) {
            madlibList = [new MadlibArgT_WideByteString { m_madlibArgument = customName, m_madlibToken = "NAME" }];
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
