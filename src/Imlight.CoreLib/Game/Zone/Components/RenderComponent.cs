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
 * RENDER COMPONENT
 * ========================================================================
 * 
 * PURPOSE:
 * Manages object rendering and visibility mechanics for game entities, 
 * handling player-specific object creation, spawning, and despawning.
 * 
 * USAGE EXAMPLE:
 * 
 * NOTE:
 * Supports dynamic object rendering based on player proximity.
 * MSG_NEWOBJECT creates an object in the client's world and MSG_REMOVEOBJECT removes it. An object that
 * comes back into range is sent again with MSG_NEWOBJECT, as live servers do: this client ignores
 * MSG_ADDOBJECT.
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 09/27/2026
 */

using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.CoreObject;
using Imcodec.Cryptography;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed class RenderComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IComponentFactory, IWithTimers {

    public ITimerScheduler Timers { get; set; }

    // How long an object that moved stays away before it is sent at its new place, so the game has let the old one go.
    private const int SHOW_AGAIN_DELAY_IN_MS = 400;
    private const string SHOW_AGAIN_TIMER = "ShowAgainAfterMove";

    private const string SPAWN_STATE_NAME = "On";
    private const string DESPAWN_STATE_NAME = "Off";

    private readonly CoreObjectSerializer _serializer = new(
        versionable: false,
        behaviors: SerializerFlags.None
    );
    private readonly PropertyFlags _propertyFlags = PropertyFlags.Prop_Public
                                                  | PropertyFlags.Prop_Transmit
                                                  | PropertyFlags.Prop_AuthorityTransmit;
    // Keyed by the player's actor: CoreObject is a record, so its hash follows its location and a move replaces it.
    private readonly HashSet<IActorRef> _playersInRange = [];
    private readonly Dictionary<Wizard, IActorRef> _playersWithRequirementsMet = [];
    private readonly Dictionary<IActorRef, Wizard> _playerIgnoreBecauseDynamod = [];
    private float _renderDistance;
    private bool _doesDistanceCheck = false;
    // Players this object has seen join. One that was in the zone before the object was made (added while the zone
    // runs, or brought back by a trigger) got it from OnStart and is counted on their first move.
    private readonly HashSet<IActorRef> _joinedPlayers = [];
    // A creature in a battle: it stays sent to everyone in it however far they are (MSG_SHOWDUELPARTICIPANT).
    private bool _heldByDuel;
    // Players a quest-only object is kept from until their quest (or the zone's script) calls for it, and when each was
    // last checked again.
    private readonly Dictionary<IActorRef, Wizard> _hiddenUntilQuest = [];
    private readonly Dictionary<IActorRef, System.DateTime> _questCheckedAt = [];
    private static readonly System.TimeSpan QUEST_CHECK_INTERVAL = System.TimeSpan.FromSeconds(2);

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate gameObjectTemplate
        && gameObjectTemplate.m_behaviors.Any(x => x is RenderBehaviorTemplate)
        || template is CombatSigilTemplate // bug fix: combat sigil templtaes don't have any behaviors
        // A pet game's object (PetGameMorph in the pet morph zone) has no model, but the game runs the pet game's
        // messages through its ClientPetGameBehavior, so the client must be sent it (Niduus, 2026-10-20: no hatch window).
        || template is GameObjectTemplate { m_behaviors: var behaviors } && behaviors.Any(x => x is PetGameBehaviorTemplate)
        // The solo hatchery sigil (Teleport Pet Morphing 1P) has no model either, but the game only shows its press-X
        // prompt for an object it was sent (Niduus, 2026-10-20: the solo sigil had no prompt, the duo one did).
        || template is GameObjectTemplate { m_templateID: SOLO_HATCH_SIGIL_TEMPLATE_ID };

    private const uint SOLO_HATCH_SIGIL_TEMPLATE_ID = 266075;

    public override void OnStart() {
        // A combat minion never distance-culls; it lives only for the duel. A summoned pet
        // follows its owner, so the spawn location it would be culled by goes stale.
        _doesDistanceCheck = !Entity.IsCombatOnlyMinion
            && Entity.ActiveGameObject is not WizClientPet
            && Entity.Template.m_behaviors
                .OfType<AnimationBehaviorTemplate>()
                .Any(anim => anim.m_bFadesIn || anim.m_bFadesOut);

        _renderDistance = Entity.Zone.ZoneData.m_farClip;

        // Trigger requirements on an object's state ("Snake obelisk is Idle_On") start from the state it is placed in.
        Entity.Zone.ScriptState.SeedObjectDefault(Entity.Info?.m_zoneTag, Entity.Info?.m_startState);

        // Players already in the zone are sorted out on their first move (see OnPlayerMove).
        if (!IsQuestOnly) {
            CreateObjectForAllPlayers();
        }
    }

    public override void OnEnabled() {
        if (IsQuestOnly) {
            foreach (var player in _playersWithRequirementsMet.Values.Where(p => !_playerIgnoreBecauseDynamod.ContainsKey(p))) {
                CreateObjectForPlayer(player);
            }
        }
        else {
            CreateObjectForAllPlayers();
        }

        // Everyone tracked as able to see the object was just sent it, so the next move can cull or restore them.
        foreach (var player in _playersWithRequirementsMet.Values) {
            if (!_playerIgnoreBecauseDynamod.ContainsKey(player)) {
                _playersInRange.Add(player);
            }
        }
    }

    public override void OnDisabled() {
        // Broadcast the removal of the object to all players.
        PlayerBroadcast(new GAME_5_PROTOCOL.MSG_REMOVEOBJECT {
            GameObjectID = Entity.ActiveGameObject.m_globalID
        });

        // Nobody has the object any more. The requirements record stays so a re-enable can send it again.
        _playersInRange.Clear();
    }

    public override void OnPlayerJoin(CoreObject player, IActorRef suspect, Wizard wizard) {
        _joinedPlayers.Add(suspect);

        // Check to see if dynamods would enable/disable this object.
        // We don't need to check for spawns, only despawns.
        var relevantDynaMods = wizard?.DynamodSet?.Dynamods?
            .Where(d => d.ClientTag.Equals(Entity.Info?.m_zoneTag, System.StringComparison.OrdinalIgnoreCase))
            .Where(d => string.IsNullOrEmpty(d.ZoneName) 
                     || d.ZoneName.Equals(Entity.Zone.ZoneData.m_zoneName, System.StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];
        string persistedState = null;
        foreach (var mod in relevantDynaMods) {
            // If the player has a dynamod that disables this object, do not spawn it for them.
            if (IsDespawnState(mod.ModState)) {
                _playerIgnoreBecauseDynamod[suspect] = wizard;

                return;
            }

            if (!mod.ModState.Equals(SPAWN_STATE_NAME, System.StringComparison.OrdinalIgnoreCase)) {
                persistedState = mod.ModState;
            }
        }

        // A quest-only object stays away until a mod shows it or the player's quest calls for it.
        if (IsQuestOnly && relevantDynaMods.Count == 0 && !QuestCallsFor(wizard)) {
            _hiddenUntilQuest[suspect] = wizard;
            _questCheckedAt[suspect] = System.DateTime.UtcNow;

            return;
        }

        // Determine if this player meets the requirements to see the object.
        var requirementsMet = true;
        if (Entity.Info is not null && Entity.Info.m_spawnRequirements is not null) {
            var requirements = Entity.Info.m_spawnRequirements;
            requirementsMet = RequirementDispatcher.EvaluateRequirements(
                requirements,
                new ZoneRequirementContext(requirements, suspect, null, wizard, Entity.ZoneRef)
            );
        }

        if (requirementsMet) {
            _playersWithRequirementsMet[wizard] = suspect;

            // Always send MSG_NEWOBJECT so the client registers this object,
            // even if the player is outside the render distance. Without this,
            // the client will ignore subsequent MSG_ADDOBJECT messages.
            // See: "You cannot send MSG_ADDOBJECT in regards to an object if
            // the client has not been told about it with MSG_NEWOBJECT."
            CreateObjectForPlayer(suspect);

            // A dynamod state such as "IdleOpen" is only ever sent as a state change, so a player
            // arriving in the zone has to be told again or the object reverts to its default.
            if (persistedState is not null) {
                Entity.ChangeStateExclusiveSender(persistedState, suspect);
            }

            // Always add the player to the in-range list so the next
            // OnPlayerMove tick can correctly evaluate distance and send
            // MSG_REMOVEOBJECT if the player is outside the render radius.
            // The client needs the ~250ms gap between MSG_NEWOBJECT and
            // any MSG_REMOVEOBJECT to register the object properly.
            _playersInRange.Add(suspect);

            return;
        }

        // If the player doesn't meet spawn requirements, nothing to do.
        if (!_doesDistanceCheck) {
            return;
        }

        if (!IsInRadius(player, _renderDistance)) {
            DespawnObjectForPlayer(suspect);
        }
        else {
            _playersInRange.Add(suspect);
        }
    }

    public override void OnPlayerLeave(IActorRef suspect, ulong id) {
        _joinedPlayers.Remove(suspect);
        var wizard = _playersWithRequirementsMet.FirstOrDefault(x => x.Value == suspect).Key;
        if (wizard != null) {
            _playersWithRequirementsMet.Remove(wizard);
        }

        // Remove the player from the list of players in range.
        _playersInRange.Remove(suspect);
        _questCheckedAt.Remove(suspect);

        if (_playerIgnoreBecauseDynamod.Remove(suspect) | _hiddenUntilQuest.Remove(suspect)) {
            return;
        }

        DespawnObjectForPlayer(suspect);
    }

    public override void OnPlayerMove(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (playerWizard is not null && Entity.Info?.m_spawnRequirements is null && _joinedPlayers.Add(playerActor)) {
            if (IsQuestOnly) {
                // This player was in the zone before the object was made, so OnStart did not send it to them.
                _hiddenUntilQuest[playerActor] = playerWizard;
                _questCheckedAt[playerActor] = System.DateTime.MinValue;
            }
            else {
                _playersWithRequirementsMet[playerWizard] = playerActor;
                _playersInRange.Add(playerActor);
            }
        }

        if (_hiddenUntilQuest.ContainsKey(playerActor)) {
            ShowIfQuestCallsFor(playerActor, playerWizard);

            return;
        }

        if (!_doesDistanceCheck || _heldByDuel) {
            return;
        }

        // If this player is ignoring the object due to a dynamod, do nothing.
        if (_playerIgnoreBecauseDynamod.ContainsKey(playerActor)) {
            return;
        }

        // Check if the player is now in range of the object.
        if (IsInRadius(playerObj, _renderDistance) && !_playersInRange.Contains(playerActor)) {
            // Respawn the object if the player is in range and we've determined they meet the requirements.
            if (playerWizard is not null && _playersWithRequirementsMet.ContainsKey(playerWizard)) {
                CreateObjectForPlayer(playerActor);
            }

            _playersInRange.Add(playerActor);
        }
        else if (!IsInRadius(playerObj, _renderDistance) && _playersInRange.Contains(playerActor)) {
            // If the player is out of range, despawn the object for them.
            DespawnObjectForPlayer(playerActor);
            _playersInRange.Remove(playerActor);
        }
    }

    // An object from spiraldb-extra/ZoneObjects that the Customs tab moved, turned or resized while the zone runs: it takes
    // its new placement, is taken away from the players and is sent again at the new place. A battle circle is left
    // until the zone loads again (its supervisor keeps it by its old place).
    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONEOBJECTSCHANGED))]
    private void ReceiveZoneObjectsChanged(ZONE_102_PROTOCOL.MSG_ZONEOBJECTSCHANGED message) {
        var obj = Entity.ActiveGameObject;
        if (obj is null || Entity.Info is CombatSigilObjectInfo
            || !message.Moved.TryGetValue(obj.m_globalID.Full, out var moved)) {
            return;
        }

        // A sigil pad stands a little higher than its file says, as when the zone loads (ZoneSigilSupervisor).
        var lift = Entity.Info is MinigameSigilInfo ? 5 : 0;
        obj.m_location = new Imcodec.Math.Vector3(moved.m_location.X, moved.m_location.Y, moved.m_location.Z + lift);
        obj.m_orientation = moved.m_orientation;
        obj.m_fScale = moved.m_fScale;
        if (Entity.Info is { } info) {
            info.m_location = moved.m_location;
            info.m_orientation = moved.m_orientation;
            info.m_fScale = moved.m_fScale;
            if (info is MinigameSigilInfo pad && moved is MinigameSigilInfo movedPad) {
                pad.m_radius = movedPad.m_radius;
                pad.m_sigilType = movedPad.m_sigilType;
                pad.m_zoneTag2 = movedPad.m_zoneTag2;
            }

            Core.ZoneObjects.Rebind(info, moved);
        }

        OnDisabled();
        Timers.StartSingleTimer(SHOW_AGAIN_TIMER, new ZONE_102_PROTOCOL.MSG_ZONEOBJECTSHOWAGAIN(),
            System.TimeSpan.FromMilliseconds(SHOW_AGAIN_DELAY_IN_MS));
    }

    // A wizard came into the battle this creature is in: their game must have the creature, or the circle stays empty.
    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_SHOWDUELPARTICIPANT))]
    private void ReceiveShowDuelParticipant(ZONE_102_PROTOCOL.MSG_SHOWDUELPARTICIPANT message) {
        _heldByDuel = true;
        var player = message.PlayerActor;
        if (player is null || Entity.ActiveGameObject is null) {
            return;
        }

        // Neither a dynamod nor the spawn requirements hide a creature the wizard is fighting.
        if ((_playerIgnoreBecauseDynamod.Remove(player, out var hiddenWizard) | _hiddenUntilQuest.Remove(player, out var questWizard))
            && (hiddenWizard ?? questWizard) is { } fighter) {
            _playersWithRequirementsMet[fighter] = player;
        }

        CreateObjectForPlayer(player);
        _playersInRange.Add(player);
        _joinedPlayers.Add(player);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ZONEOBJECTSHOWAGAIN))]
    private void ReceiveShowAgain() => OnEnabled();

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ENTERSTATE))]
    public void ReceiveEnterState(ZONE_102_PROTOCOL.MSG_ENTERSTATE msg) {
        var isDespawn = IsDespawnState(msg.StateName);
        var isSpawn = string.Equals(msg.StateName, SPAWN_STATE_NAME, System.StringComparison.OrdinalIgnoreCase);

        // If the tag matches, spawn or despawn the object for the sender.
        var zoneTag = msg.ObjectName;
        if (Entity.Info is not null && string.Equals(Entity.Info.m_zoneTag, zoneTag, System.StringComparison.OrdinalIgnoreCase)) {
            // A state for the whole zone (a trigger result) reaches every player, not just one.
            if (!msg.ExclusiveToSender && msg.Sender is null) {
                Entity.ChangeState(msg.StateName);

                // The zone's script brought a quest-only object out: everyone in the zone gets it now.
                if (IsQuestOnly && !isDespawn) {
                    foreach (var (hiddenPlayer, hiddenWizard) in _hiddenUntilQuest.ToList()) {
                        Reveal(hiddenPlayer, hiddenWizard);
                    }
                }

                return;
            }

            var player = msg.Sender;
            if (player is null) {
                return;
            }

            // Any other state names an object state such as "IdleOpen". Only the client can act
            // on it, and it never affects whether the object is spawned.
            if (!isDespawn && !isSpawn) {
                Entity.ChangeStateExclusiveSender(msg.StateName, player);

                return;
            }

            if (isDespawn) {
                var wizard = _playersWithRequirementsMet.FirstOrDefault(x => x.Value == player).Key;
                if (_hiddenUntilQuest.Remove(player, out var questWizard)) {
                    wizard ??= questWizard;
                }

                DespawnObjectForPlayer(player);
                _playerIgnoreBecauseDynamod[player] = wizard;
            }
            else if (isSpawn) {
                // Respawn the object for the sender. This must also work for
                // players who joined while a dynamod hid the object: they
                // never received MSG_NEWOBJECT and are not in
                // _playersWithRequirementsMet, only in the ignore list.
                var wizard = _playersWithRequirementsMet.FirstOrDefault(x => x.Value == player).Key;
                if (wizard is null && _playerIgnoreBecauseDynamod.TryGetValue(player, out var hiddenWizard)) {
                    wizard = hiddenWizard;
                }

                if (wizard is null && _hiddenUntilQuest.TryGetValue(player, out var questWizard)) {
                    wizard = questWizard;
                }

                _playerIgnoreBecauseDynamod.Remove(player);
                _hiddenUntilQuest.Remove(player);

                // Mirror the join-time spawn requirements check.
                var requirementsMet = true;
                if (wizard is not null && Entity.Info is not null && Entity.Info.m_spawnRequirements is not null) {
                    requirementsMet = RequirementDispatcher.EvaluateRequirements(
                        Entity.Info.m_spawnRequirements,
                        new ZoneRequirementContext(Entity.Info.m_spawnRequirements, player, null, wizard, Entity.ZoneRef)
                    );
                }

                if (!requirementsMet) {
                    return;
                }

                if (wizard is not null) {
                    _playersWithRequirementsMet[wizard] = player;
                }

                CreateObjectForPlayer(player);
            }
        }
    }

    /// <summary>
    /// An object the game placed as undetectable (m_bUndetectable, such as the Commons' Primal Magic Book): the live
    /// server shows it only to players whose quest calls for it, through a dynamod that shows it, a zone script, or as
    /// the NPC of the player's quest step or of a quest it offers them. An object with spawn requirements is decided by
    /// those instead. Only characters (NPC behavior) are held back: teleporters and doors placed the same way are opened
    /// on live by quest data we do not have, and hiding them would cut players off. A character with a service of its
    /// own (a shop, a trainer, the stitcher Eloise Merryweather) is placed the same way yet always stands there, so it is
    /// never held back. Niduus 2026-10-25: quest objects stood in the world for everyone; then "Where did the stitcher go?".
    /// </summary>
    private bool IsQuestOnly
        => _isQuestOnly ??= Entity.Info is { m_bUndetectable: true, m_spawnRequirements: null } info
           && info.GetType() == typeof(CoreObjectInfo)
           && Entity.ActiveGameObject is not WizClientPet
           && Entity.Template.m_behaviors.Any(b => b is NPCBehaviorTemplate)
           && !Entity.Template.m_behaviors.Any(b => b is WizShoppingBehaviorTemplate)
           && Entity.GetComponentsOfType<IServiceComponent>().All(IsQuestService);

    // The services a quest character offers; any other (a shop, a trainer, a bank) means the character always stands there.
    private static bool IsQuestService(IServiceComponent service)
        => service is InteractPersonaGoalComponent or InteractQuestOfferComponent or InteractQuestUnderwayComponent
           or InteractQuestSelectComponent;

    private bool? _isQuestOnly;

    private bool QuestCallsFor(Wizard wizard) {
        // A zone script put the object in another state than it was placed in (shown it).
        var state = Entity.Zone.ScriptState.GetObjectState(Entity.Info?.m_zoneTag);
        if (!string.IsNullOrEmpty(state) && !IsDespawnState(state)
            && !string.Equals(state, Entity.Info?.m_startState, System.StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        if (wizard is null) {
            return false;
        }

        // A dynamod of the player's shows it (only the ones that do not hide it reach here).
        var tag = Entity.Info?.m_zoneTag;
        if (wizard.DynamodSet?.Dynamods?.Any(d => !IsDespawnState(d.ModState)
                && string.Equals(d.ClientTag, tag, System.StringComparison.OrdinalIgnoreCase)) == true) {
            return true;
        }

        return InteractPersonaGoalComponent.HasPendingGoalFor(Entity, wizard)
            || Entity.GetComponentOfType<InteractQuestOfferComponent>()?.GetServiceOptions(wizard).Any() == true;
    }

    // Looked at again every few seconds while the player moves, so a quest step that starts brings its NPC out.
    private void ShowIfQuestCallsFor(IActorRef player, Wizard wizard) {
        var now = System.DateTime.UtcNow;
        if (_questCheckedAt.TryGetValue(player, out var last) && now - last < QUEST_CHECK_INTERVAL) {
            return;
        }

        _questCheckedAt[player] = now;
        wizard ??= _hiddenUntilQuest[player];
        if (QuestCallsFor(wizard)) {
            Reveal(player, wizard);
        }
    }

    private void Reveal(IActorRef player, Wizard wizard) {
        _hiddenUntilQuest.Remove(player);
        _questCheckedAt.Remove(player);
        _joinedPlayers.Add(player);
        if (wizard is not null) {
            _playersWithRequirementsMet[wizard] = player;
        }

        CreateObjectForPlayer(player);
        _playersInRange.Add(player);
    }

    private static bool IsDespawnState(string stateName) {
        // A dynamod with no state hides its object for the player, as "Off" does (client triggers use both).
        return string.IsNullOrEmpty(stateName)
            || stateName.Equals(DESPAWN_STATE_NAME, System.StringComparison.OrdinalIgnoreCase);
    }

    private void CreateObjectForPlayer(IActorRef player) {
        // Serialize the client object.
        var clientObj = Entity.GetClientObject();
        if (!_serializer.Serialize(clientObj, _propertyFlags, out var serializedData)) {
            Logger.Error("Failed to serialize object data.");

            return;
        }

        // Send object data to the player.
        var newObjectMsg = new GAME_5_PROTOCOL.MSG_NEWOBJECT {
            Data = serializedData
        };
        player.Tell(newObjectMsg);

        foreach (var stateMsg in CreateCurrentStateMessages()) {
            player.Tell(stateMsg);
        }
    }

    private void CreateObjectForAllPlayers() {
        // Serialize the client object.
        var clientObj = Entity.GetClientObject();
        if (!_serializer.Serialize(clientObj, _propertyFlags, out var serializedData)) {
            Logger.Error("Failed to serialize object data.");

            return;
        }

        // Send object data to all players.
        PlayerBroadcast(new GAME_5_PROTOCOL.MSG_NEWOBJECT {
            Data = serializedData
        });

        foreach (var stateMsg in CreateCurrentStateMessages()) {
            PlayerBroadcast(stateMsg);
        }
    }

    // The client builds an object in its template's default state, so the state the zone holds for it
    // (the placement's start state, or what a player or trigger changed since) is sent right after it. A dungeon sigil
    // pad from spiraldb-extra/ZoneObjects is sent its file's look as it is now (the Customs tab can change it while the
    // zone runs), through Glowy for the red Advanced Mode look: the client's new pad is Off, and Red is reached only
    // from Glowy.
    private List<GAME_5_PROTOCOL.MSG_ENTERSTATE> CreateCurrentStateMessages() {
        var look = Entity.Info is MinigameSigilInfo ? Core.ZoneObjects.LitStateOf(Entity.Info) : null;
        var state = look ?? Entity.Zone.ScriptState.GetObjectState(Entity.Info?.m_zoneTag);
        // A duel circle (a PvP battle circle, such as a house's) is built Off, so the game shows nothing on the floor;
        // with no state of its own it is sent lit, the same blue glow as a sigil pad (Niduus, 2026-10-23).
        if (string.IsNullOrEmpty(state) && IsDuelCircle()) {
            state = Core.ZoneObjects.SIGIL_LIT_STATE;
        }

        if (IsDespawnState(state)) {
            return [];
        }

        string[] states = look is null ? [state] : Core.ZoneObjects.SigilStatePath(look);
        if (look is not null) {
            Logger.Debug("Dungeon sigil {0} (gid {1}): sent with state {2}.",
                Logger.Args(Entity.Info.m_zoneTag, Entity.ActiveGameObject.m_globalID.Full, string.Join(" then ", states)));
        }

        return [.. states.Select(s => new GAME_5_PROTOCOL.MSG_ENTERSTATE {
            GameObjectID = Entity.ActiveGameObject.m_globalID,
            State = StringHash.Compute(s),
            Data = new ByteString(),
        })];
    }

    private bool IsDuelCircle()
        => Entity.Info is CombatSigilObjectInfo circle
           && Sigils.SigilFactory.GetSigilTemplate(circle.m_sigilType) is PvPCombatSigilTemplate;

    private void DespawnObjectForPlayer(IActorRef player) {
        // Send object data to the player
        var despawnObjectMsg = new GAME_5_PROTOCOL.MSG_REMOVEOBJECT {
            GameObjectID = Entity.ActiveGameObject.m_globalID
        };
        player.Tell(despawnObjectMsg);
    }

}