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
 * COMBAT DUEL SYSTEM
 * ========================================================================
 * 
 * PURPOSE:
 * Manages turn-based combat encounters between players and creatures.
 * This component is responsible for handling duel initiation, combat moves,
 * and duel resolution. It also manages the duel state and participant data.
 * 
 * USAGE EXAMPLE:
 * This component may activate from an NpcComponent on a dueling creature.
 * It may also activate as a result of `ResStartDuel` from a trigger.
 * 
 * NOTE:
 * This class is merely the director of the duel. The actual combat logic is
 * all handled within the `Imlight.CoreLib.Game.Combat` namespace.
 * Start at the `CombatResolver` class and work your way down.
 * Tutorial duels are scripted by the `TutorialDuelDirector` in that namespace.
 * Combat positions are determined by sigil templates, with specific subcircle positions.
 * 
 * TODO:
 * - Implementation of creature stunning functionality
 * - Calculate the correct damage percent max as a limit function
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.Math;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.PvP;
using Imlight.CoreLib.Game.PvP.Arena;
using Imlight.CoreLib.Game.PvP.Data;
using Imlight.CoreLib.Game.Sigils;
using Imlight.CoreLib.Game.Social;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;
using Imcodec.Cryptography;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// Manages combat duels between players and creatures in the game world.
/// Controls the full duel lifecycle including initialization, participant management,
/// turn sequencing, spell casting, and resolution of combat.
/// </summary>
/// <remarks>
/// Acts as the central orchestrator for the combat system, handling messaging between
/// all participants, managing combat state transitions, and coordinating the various
/// phases of combat (planning, execution, resolution). The component creates and 
/// positions subcircles according to sigil templates and maintains combat state
/// including team assignments, turn order, and participant status.
/// </remarks>
internal sealed partial class CombatDuelComponent(ZoneEntity entity)
    : ZoneEntityComponent(entity), IComponentFactory, IWithTimers, IClientBehaviorProvider<WizardClientDuelBehavior> {

    private const byte PLANNING_TIME = 30;
    private const float DUEL_GRACE_PERIOD_IN_SECONDS = 3.75f;
    private const float DUEL_NEW_ROUND_DELAY = 2.5f;
    private const float YAW_ERROR_COMPENSATION = 1.58f;
    private const string GRACE_TIME_KEY = "GracePeriod";
    private const string PLANNING_TIME_KEY = "PlanningPhase";
    private const string PREPLANNING_TIME_KEY = "PrePlanningPhase";
    private const string RESOUTION_TIME_KEY = "ResolutionPhase";
    private const double MINION_SUMMON_ANIMATION_DELAY = 5.5;
    private const string GROUP_JOIN_PROMPT_KEY = "GUI_GotoFriendQuestion";
    private const string PVP_RETURN_GRACE_KEY = "PvPReturnGrace";
    private const string ENCOUNTER_MINIONS_KEY = "EncounterMinions";
    // How long a battle against an encounter's boss waits for the zone's own minions to answer before it spawns the
    // missing ones; well inside the grace period, so all of them fight from the first round.
    private const float ENCOUNTER_JOIN_WAIT_IN_SECONDS = 1.0f;
    private const double PVP_RETURN_GRACE_SECONDS = 0; // todo: measure in game

    public bool NoTransfer { get; set; } = false;
    public ITimerScheduler Timers { get; set; }
    public Duel Duel { get; private set; }
    internal bool IsDuelActive => _isActive;
    internal bool IsAwaitingMoves => _awaitingCombatMoves;
    public Combat.CombatResolver CombatResolver { get; private set; }
    public CombatDuelSubCircle[] SubCircles { get; private set; }
    public CombatDuelSubCircle[] ActiveSubCircles => [.. SubCircles.Where(x => x.Occupied)];
    // Human players only; a minion must not let an extra enemy scale into the fight.
    public byte PlayerCount => (byte) SubCircles.Count(x => x.Occupied && x.OccupiedTeam == CombatTeam.Player
                                                            && !x.IsSummonedMinion);
    public byte CreatureCount => (byte) SubCircles.Count(x => x.Occupied && x.OccupiedTeam == CombatTeam.Monster);
    // Exclude minions from the loss condition; they must not hold a fight open.
    public byte AlivePlayerCount
        => (byte) SubCircles.Count(x => x.Occupied && x.OccupiedTeam == CombatTeam.Player && x.IsAlive
                                        && !x.IsSummonedMinion);
    public byte AliveCreatureCount
        => (byte) SubCircles.Count(x => x.Occupied && x.OccupiedTeam == CombatTeam.Monster && x.IsAlive);
    public byte PlayersInDuel
        => (byte) SubCircles.Count(x => x.Occupied && x.OccupiedTeam == CombatTeam.Player && x.AddedToDuel);
    public byte CreaturesInDuel
        => (byte) SubCircles.Count(x => x.Occupied && x.OccupiedTeam == CombatTeam.Monster && x.AddedToDuel);
    public byte AliveAndInDuelPlayerCount
        => (byte) SubCircles.Count(x => x.Occupied && x.OccupiedTeam == CombatTeam.Player && x.IsAlive && x.AddedToDuel
                                        && !x.IsSummonedMinion);
    public byte AliveAndInDuelCreatureCount
        => (byte) SubCircles.Count(x => x.Occupied && x.OccupiedTeam == CombatTeam.Monster && x.IsAlive && x.AddedToDuel);
    public ulong SigilId => Entity.ActiveGameObject.m_globalID;
    internal bool IsPvPDuel => _pvp is not null;
    internal uint PvPLeagueId => _pvp?.LeagueId ?? 0;
    private byte PlanningSeconds => IsPvPDuel ? PvPSettings.PracticePlanningSeconds : PLANNING_TIME;

    /// <summary>
    /// Counts the wizards of one side that fight on, whichever side that is.
    /// </summary>
    public byte AliveHumanCount(CombatTeam team)
        => (byte) SubCircles.Count(x => x.Occupied && x.IsHumanPlayer && x.OccupiedTeam == team && x.IsAlive);

    // Keyed by the actor: a CoreObject is a record whose hash follows its location.
    private readonly HashSet<IActorRef> _entitiesInRange = [];
    private readonly CombatGroupReservations _groupReservations = new();
    private readonly ObjectSerializer _serializer = new(
        Versionable: false,
        Behaviors: SerializerFlags.None
    );
    private readonly PropertyFlags _combatParticipantFlags = (PropertyFlags) 4;
    private readonly PropertyFlags _combatParticipantStatFlags = (PropertyFlags) 5;
    private readonly PropertyFlags _combatParticipantHandFlags = (PropertyFlags) 5;
    private readonly PropertyFlags _upFirstFlags = PropertyFlags.Prop_Transmit
                                                 | PropertyFlags.Prop_AuthorityTransmit
                                                 | PropertyFlags.Prop_Public;

    private CombatSigilObjectInfo _combatSigilObjectInfo;
    private RenderComponent _renderComponent;
    private CombatSigilTemplate _sigilTemplate;
    private PvPDuelState _pvp;
    private bool _isActive;
    private bool _awaitingCombatMoves;

    // Tamed Drops (TamedDrops.cs): the rounds this battle counts, and how many rounds in a row everyone chose to pass.
    private int _countedRounds;
    private int _passStreak;
    private int _lastRoundCounted = -1;
    // When this round's planning began, so a wizard who joins during it can be given the time left.
    private DateTime _planningStartedAt;
    // Enemy-side minions (.joinme creatures, a boss's second form) that were defeated. Their circle is freed when they
    // die, so the win would not see them; their drops come from this list.
    private readonly List<(ulong TemplateId, ulong GlobalId)> _defeatedEnemyMinions = [];
    // The ZoneObjects encounter of this battle while its minions gather, and the templates that joined from the zone.
    private ZoneObjects.ZoneEncounter _encounter;
    private CombatDuelSubCircle _encounterBossCircle;
    private readonly HashSet<ulong> _encounterJoined = [];
    // Global ids of the room's own creatures that joined this battle's encounter (alive or defeated since), so a lost
    // or fled battle puts them back in the room for the next try (ZonePath, MSG_ENCOUNTERRESET).
    private readonly HashSet<ulong> _encounterZoneMinions = [];
    // Whether this battle counts in its zone's EncounterBattles (a distinct spawner waits while it runs).
    private bool _encounterBattleCounted;
    // The room's minions a lost encounter battle gives back, sent to the zone when the duel is torn down.
    private HashSet<ulong> _encounterResetIds = [];
    private int _battle;
    private TutorialDuelDirector _tutorialDirector;
    // Seconds of cinematics before a caster's cast; SummonMinion adds the animation delay to it.
    internal float CurrentActionCinematicOffsetSeconds;
    internal bool CheatInstantCinematics { get; set; }

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate gameObjectTemplate
        && gameObjectTemplate.m_behaviors.Any(x => x is not null && x.m_behaviorName == "DuelBehavior");

    public override void OnStart() {
        // Disable the RenderComponent. We'll activate it when the sigil is activated.
        _renderComponent = Entity.GetComponentOfType<RenderComponent>();
        _renderComponent?.Disable();

        _tutorialDirector = new TutorialDuelDirector(this, Entity.Zone?.ZonePath ?? "");
    }

    // A duel circle is on the floor before its first duel. Sent without a duel, the game's circle never takes one later
    // ("has a NULL duel", then every MSG_DUEL and MSG_COMBATADD ignored), so the first duel after a restart never showed.
    private Duel _restingDuel;

    public WizardClientDuelBehavior GetClientBehaviorInstance() => new() {
        m_pDuel = Duel ?? (IsDuelCircle ? _restingDuel ??= CreateDuelWithDefaults() : null),
        // todo: this is the sigil template itself, not for the game object
        m_sigilTemplateID = IsPvPDuel ? StringHash.Compute(_sigilTemplate.m_sigilName) : 1901671683,
    };

    public override void OnPlayerJoin(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (!_isActive) {
            return;
        }

        // Check if this player is in the duel. If they are, remove them from the duel.
        var subCircle = SubCircles.FirstOrDefault(x => x is not null && x.ParticipantActor == playerActor);
        if (subCircle is null) {
            return;
        }

        // Handle this as if it were the flee action, without its mana cost: the wizard did not flee.
        HandleFleeAction(subCircle, drainMana: false);
    }

    public override void OnPlayerMove(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        DuelCircleMove(playerObj, playerActor, playerWizard);

        // The roster of a PvP duel is fixed; nobody joins by walking in.
        if (!_isActive || IsPvPDuel) {
            return;
        }

        // Check if the player is now in range of the object.
        // If there's a slot available, add the player to the duel.
        if (IsInRadius(playerObj, _combatSigilObjectInfo.m_radius) && !_entitiesInRange.Contains(playerActor)) {
            _entitiesInRange.Add(playerActor);

            if (IsSlotAvailable(CombatTeam.Player)) {
                AddParticipant(playerObj, playerActor);
            }
        }
        else if (!IsInRadius(playerObj, _combatSigilObjectInfo.m_radius) && _entitiesInRange.Contains(playerActor)) {
            _entitiesInRange.Remove(playerActor);
        }
    }

    public override void OnCreatureMove(CoreObject creature, IActorRef suspect, ZoneEntity entity) {
        if (!_isActive || IsPvPDuel) {
            return;
        }

        try {
            // Check if the creature is now in range of the object.
            // If there's a slot available, add the creature to the duel.
            if (IsInRadius(creature, _combatSigilObjectInfo.m_radius) && !_entitiesInRange.Contains(suspect)) {
                _entitiesInRange.Add(suspect);
                JoinLateMob(creature, suspect, entity);
            }
            else if (!IsInRadius(creature, _combatSigilObjectInfo.m_radius) && _entitiesInRange.Contains(suspect)) {
                _entitiesInRange.Remove(suspect);
            }
        }
        catch (Exception ex) {
            Logger.Error("Duel {0} | Late mob join of {1} failed: {2}",
                Logger.Args(Duel?.m_duelID.Full ?? SigilId, creature?.m_debugName, ex));
        }
    }

    // [Battle] MobsJoinLate (Niduus, 2026-10-22: "Can we add the 'late' feature to mobs? Have it on by default"):
    // as on live, a monster that walks (or spawns) into a running battle's circle joins it on a free enemy circle,
    // within the usual creature cap, and fights from the next round. Off: monsters walk past running battles.
    private static readonly bool s_mobsJoinLate = ConfigurationManager.GetValue("Battle.MobsJoinLate", true);

    private void JoinLateMob(CoreObject creature, IActorRef suspect, ZoneEntity entity) {
        if (!s_mobsJoinLate || creature is null || suspect is null || entity is null || entity.IsCombatOnlyMinion) {
            return;
        }

        // Only a world monster that is not fighting (or dying) already.
        var npc = entity.GetComponentOfType<NpcComponent>();
        var ai = entity.GetComponentOfType<CombatCreatureAIComponent>();
        if (npc is null || !npc.IsMonster || ai is null || ai.IsInDuel) {
            return;
        }

        // Each refusal is logged, so a monster that walked in and did not join says why (Niduus, 2026-10-22: "Mobs
        // join late isn't working").
        string name = npc.NameOverride ?? creature.m_debugName;
        void Refused(string why) => Logger.Information("Late mob join: {0} ({1}) walked into duel {2} but did not join: {3}",
            Logger.Args(name, creature.m_templateID.Full, Duel?.m_duelID.Full ?? SigilId, why));

        // Battles whose enemies are set (tutorial and scripted fights, a ZoneObjects encounter that gathers its own
        // minions) take nobody who wanders by, and a wandering boss does not join someone else's fight. A plain boss
        // fight does take a monster that walks in, as in the game (Niduus, 2026-10-23: a Rotted Fodder walked into the
        // Harvest Lord's battle and stood there).
        // A boss standing in the circle as the battle is made is part of the same fight and joins it before the first
        // round (Niduus, 2026-10-23: the Fairy Queen stood beside Rattlebones in the Rattlebones Exalted Challenge and
        // never came in, "it is a boss"). Only once the rounds have begun is a boss that walks in turned away.
        var bossOfThisFight = npc.IsBossMonster && Duel.m_roundNum == 0;
        if (_tutorialDirector.IsActive || _encounter is not null || _encounterBattleCounted || (npc.IsBossMonster && !bossOfThisFight)) {
            Refused(_tutorialDirector.IsActive ? "tutorial battle" : npc.IsBossMonster ? "it is a boss"
                : "the battle has its own minions");

            return;
        }

        if (SubCircles.Any(x => x?.ParticipantObject?.m_globalID == creature.m_globalID)) {
            return;
        }

        // Any free enemy circle, up to 4 enemies, even where the usual cap (one monster per wizard in starter areas
        // such as Wizard City) would say no (Niduus, 2026-10-22: walk-ins should join in Wizard City too). A monster
        // that finds no room vanishes, as before.
        if (GetAvailableSubCircleTeamCreature() is null) {
            Refused($"the enemy side is full ({CreatureCount} monsters)");
            suspect.Tell(new COMBAT_106_PROTOCOL.MSG_COMBATDEATH());

            return;
        }

        // Two battles side by side both see the same step; only one of them gets the monster.
        if (!entity.TryClaimLateDuelJoin()) {
            Refused("another battle took it first");

            return;
        }

        // It takes the free enemy circle nearest to where it stands (the Fairy Queen, placed on circle 4, takes circle 4).
        AddParticipant(creature, suspect, NearestFreeCreatureCircle(creature));
        var circle = SubCircles.FirstOrDefault(x => x?.ParticipantObject?.m_globalID == creature.m_globalID);
        if (circle is null) {
            entity.ReleaseLateDuelJoin();
            Refused("no circle could be given to it");

            return;
        }

        var now = JoinLateCreatureNow(circle);
        Logger.Information("Late mob join: {0} ({1}) joined duel {2} slot {3}{4}",
            Logger.Args(name, creature.m_templateID.Full, Duel.m_duelID.Full,
                circle.SlotIndex, now ? " and plays this round" : Duel.m_roundNum == 0 ? ", from the first round" : ", from the next round"));
    }

    /// <summary>
    /// A monster that joins a running battle (walking in, or by .joinme) plays the round it joins in, as late wizards
    /// do (Niduus, 2026-10-22, with [Battle] MobsJoinLate on). Before or during planning its circle is sent to everyone
    /// and its AI picks a move now; once the spells play, it comes in with the next round.
    /// </summary>
    /// <returns>Whether it plays this round.</returns>
    private bool JoinLateCreatureNow(CombatDuelSubCircle circle) {
        if (!s_mobsJoinLate || !_isActive || IsPvPDuel || _tutorialDirector.IsActive || circle is null
            || circle.AddedToDuel || circle.IsHumanPlayer || circle.ParticipantActor is null || Duel.m_roundNum == 0
            || Duel.m_duelPhase is not (kDuelPhase.kPhase_PrePlanning or kDuelPhase.kPhase_Planning)) {
            return false;
        }

        try {
            AddCombatParticipant(circle);
            if (!circle.AddedToDuel) {
                return false;
            }

            if (Duel.m_duelPhase == kDuelPhase.kPhase_Planning) {
                SendCombatStats();
                SendCombatPips();
                SendCombatHealth();
            }

            // Its move is taken even if everyone else has moved already (the round then ends a second later).
            _awaitingCombatMoves = true;
            circle.ParticipantActor.Tell(new COMBAT_106_PROTOCOL.MSG_NEWROUND());

            return true;
        }
        catch (Exception ex) {
            Logger.Error("Duel {0} | Slot {1} | Late creature join failed: {2}", Logger.Args(Duel.m_duelID.Full, circle.SlotIndex, ex));

            return false;
        }
    }

    private const string MONSTER_KILLED_EVENT = "Monster_Killed";

    internal void ZoneBroadcast(IMessage message) => Entity.ZoneRef.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
        Selfless = false,
        Sender = Self,
        Message = message
    });

    internal void DuelBroadcast(IMessage message)
        => EnactActionOnSubCircles(circle => circle.ParticipantActor.Tell(message));

    internal void CreatureBroadcast(IMessage message) => EnactActionOnSubCircles(circle => {
        if (circle.OccupiedTeam == CombatTeam.Monster) {
            circle.ParticipantActor.Tell(message);
        }
    });

    internal new void PlayerBroadcast(IMessage message) => EnactActionOnSubCircles(circle => {
        if (circle.OccupiedTeam == CombatTeam.Player) {
            circle.ParticipantActor.Tell(message);
        }
    });

    /// <summary>
    /// Sends the message to everyone with a client: the wizards of either side and their minions, not the
    /// creatures of the AI.
    /// </summary>
    internal void HumanBroadcast(IMessage message) => EnactActionOnSubCircles(circle => {
        if (!circle.IsAiCreature) {
            circle.ParticipantActor.Tell(message);
        }
    });

    internal void TeamBroadcast(CombatTeam team, IMessage message) => EnactActionOnSubCircles(circle => {
        if (circle.OccupiedTeam == team) {
            circle.ParticipantActor.Tell(message);
        }
    });

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_SIGILDETAILS))]
    private void ReceiveSigilDetails(ZONE_102_PROTOCOL.MSG_SIGILDETAILS message) {
        _combatSigilObjectInfo = message.CombatSigilObjectInfo;

        // Get the sigil template.
        _sigilTemplate = (CombatSigilTemplate) SigilFactory.GetSigilTemplate(_combatSigilObjectInfo.m_sigilType);

        // A duel circle is always on the floor, not only during a duel (Niduus, 2026-10-23).
        ShowDuelCircle();
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_REQUESTCOMBATSIGIL))]
    private void ReceiveDuelStart(ZONE_102_PROTOCOL.MSG_REQUESTCOMBATSIGIL message) {
        if (_isActive) {
            return;
        }

        // A new battle on this sigil cuts the last one's victory scene short.
        if (_victorySceneBattle >= 0) {
            FinishEndDuel();
        }

        if (_renderComponent is null) {
            Logger.Error("RenderComponent is null for duel {0}! Deleting sigil.",
                Logger.Args(SigilId));

            Entity.DespawnObject();

            return;
        }

        InitializeDuel(message.StartingParticipants);
        ActivateSigil();
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_STARTPVPMATCH))]
    private void ReceiveStartPvPMatch(ZONE_102_PROTOCOL.MSG_STARTPVPMATCH message) {
        if (_isActive) {
            Logger.Warning("Match {MatchId} cannot start on sigil {SigilId}: a duel is already running.",
                Logger.Args(message.MatchId, SigilId));

            return;
        }

        if (_sigilTemplate is not PvPCombatSigilTemplate || _renderComponent is null) {
            Logger.Error("Sigil {SigilId} cannot host PvP match {MatchId}.", Logger.Args(SigilId, message.MatchId));
            CancelPvPMatch(message.MatchId, PvPCancelReason.NoArena);

            return;
        }

        // A duel circle's duel brings its own rules.
        var rules = message.Rules;
        if (rules is null && (!PvPTournamentFactory.TryGetTournament(message.TournamentNameId, out var tournament)
            || !PvPGameTypes.TryResolve(tournament.m_pvpDuelModifiers.Select(name => (string) name), out rules))) {
            Logger.Error("Match {MatchId} has rules the duel does not support.", Logger.Args(message.MatchId));
            CancelPvPMatch(message.MatchId, PvPCancelReason.UnsupportedRules);

            return;
        }

        try {
            InitializePvPDuel(message, rules);
        } catch (Exception ex) {
            Logger.Error("Match {MatchId} could not seat its players: {Exception}", Logger.Args(message.MatchId, ex));
            AbortPvPDuel(message.MatchId, PvPCancelReason.NoArena);
            if (message.Rules is not null) {
                DuelCircleFailed("the circle could not seat everyone");
            }

            return;
        }

        // A duel circle's duel waits for its countdown before the rounds begin.
        _circleWaiting = message.Rules is not null;
        ActivateSigil();
        if (message.Rules is not null) {
            DuelCircleBegin(message.Roster);
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_FORFEITPVPPARTICIPANT))]
    private void ReceiveForfeitPvPParticipant(ZONE_102_PROTOCOL.MSG_FORFEITPVPPARTICIPANT message) {
        if (!IsPvPDuel || !_isActive || _pvp.MatchId != message.MatchId) {
            return;
        }

        var subCircle = SubCircles.FirstOrDefault(circle => circle.Occupied && circle._wizard?.CharId == message.CharId);
        if (subCircle is null) {
            return;
        }

        ForfeitPvPDuel(subCircle);
    }

    private void ActivateSigil() {
        // A duel circle is on the floor already.
        if (!_renderComponent.IsEnabled) {
            _renderComponent.Enable();
        }

        // Broadcast MSG_DUEL to inform all clients a duel is now active.
        // The live server sends this to enable 3D combat targeting.
        BroadcastDuel();

        // Fire a message to self to start the duel after the grace period has ended.
        if (_circleWaiting) {
            return;
        }

        var delay = TimeSpan.FromSeconds(DUEL_GRACE_PERIOD_IN_SECONDS);
        Timers.StartSingleTimer(GRACE_TIME_KEY, new COMBAT_106_PROTOCOL.MSG_NEWROUND(), delay);
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_NEWROUND))]
    private void ReceiveNewRound(COMBAT_106_PROTOCOL.MSG_NEWROUND message) {
        if (!_isActive) {
            return;
        }

        Logger.Debug("Duel {0} | New round {1} at {2}",
            Logger.Args(Duel.m_duelID.Full, Duel.m_roundNum, DateTime.Now.ToString("HH:mm:ss")));

        _groupReservations.DropStale(ZoneActor, SubCircles);
        if (Duel.m_roundNum == 0) {
            ApplyFullGroupInitiative();
        }

        if (IsPvPDuel) {
            _pvp.BeginRound(Duel.m_roundNum + 1);
            Duel.m_firstTeamToAct = (int) _pvp.RoundStarter;
        }

        // Add the circles to combat if they are not already.
        AddWaitingCombatParticipants();
        RememberRecentWizards();
        CombatResolver.Reset();
        _awaitingCombatMoves = true;

        // Determine the power pip gain. Alternating turns gain in the opponent's phase: the non-starter now.
        DoPipGain(IsAlternatingPvP ? PvPDuelState.Opposite(_pvp.RoundStarter) : null);

        if (IsPvPDuel) {
            BeginPvPHalfTurn();
        } else {
            // Echo the new round message to all actors.
            EnactActionOnSubCircles(circle => circle.ParticipantActor.Tell(message));
        }

        // Pre-planning phase just wants to send who is up first.
        Duel.m_duelPhase = kDuelPhase.kPhase_PrePlanning;
        Duel.m_roundNum++;
        SendCombatPhase((byte) Duel.m_duelPhase);
        SendUpFirst(Duel.m_roundNum);

        _tutorialDirector.OnNewRound(Duel.m_roundNum);

        var delay = TimeSpan.FromSeconds(DUEL_NEW_ROUND_DELAY);
        Timers.StartSingleTimer(PREPLANNING_TIME_KEY, new COMBAT_106_PROTOCOL.MSG_PLANNINGPHASEBEGIN(), delay);
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_PLANNINGPHASEBEGIN))]
    private void ReceivePlanningPhaseBegin() {
        // Planning phase is when each participant notices their new stats and "plans" accordingly.
        Duel.m_duelPhase = kDuelPhase.kPhase_Planning;
        SendCombatPhase((byte) Duel.m_duelPhase);

        SendCombatStats();
        SendCombatHand();
        SendCombatPips();
        SendCombatHealth();
        SendCombatUI(PlanningSeconds);
        LogRoundPipsAndHealth();
        _planningStartedAt = DateTime.UtcNow;

        // Re-telegraph minion AI moves now that the planning HUD exists.
        ResendMinionMoveSelections();

        // Tutorial duels flush queued card grants and re-script the golems before planning.
        _tutorialDirector.OnPlanningPhaseBegin();

        // Tutorial fights have no planning countdown (m_disableTimer): the client drives pacing. Planning still
        // ends normally once every participant has enqueued a move (ReceiveCombatMove).
        if (!_tutorialDirector.IsActive) {
            var delay = TimeSpan.FromSeconds(PlanningSeconds);
            Timers.StartSingleTimer(PLANNING_TIME_KEY, new COMBAT_106_PROTOCOL.MSG_PLANNINGPHASEOVER(), delay);
        }
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_ACTORCOMBATDRAW))]
    private void ReceiveCombatDraw(COMBAT_106_PROTOCOL.MSG_ACTORCOMBATDRAW message) {
        // Find which sub circle this draw request is for.
        var caster = SubCircles.FirstOrDefault(x => x.ParticipantActor == message.Actor);
        if (caster is null) {
            Logger.Warning("Duel {0} | Combat draw received from an actor that is not in the duel.",
                Logger.Args(Duel.m_duelID.Full));

            return;
        }

        // Draw a random treasure card from the vault.
        var drawnSpell = caster.DrawFromVault();
        if (drawnSpell != null) {
            Logger.Debug("Duel {0} | Slot {1} | Drew treasure card {2} from vault.",
                Logger.Args(Duel.m_duelID.Full, caster.SlotIndex, drawnSpell.m_templateID));
        }

        // Send the hand as-is: a refilling send would also pull real deck cards into the open slots.
        SendCurrentCombatHand(caster);
    }

    [MessageHandler(typeof(TUTORIAL_108_PROTOCOL.MSG_TUTORIALREBUILDDUELHAND))]
    private void ReceiveTutorialRebuildDuelHand(TUTORIAL_108_PROTOCOL.MSG_TUTORIALREBUILDDUELHAND message) {
        if (!_isActive) {
            return;
        }

        _tutorialDirector.ReceiveRebuildDuelHand(Sender, message);
    }

    [MessageHandler(typeof(TUTORIAL_108_PROTOCOL.MSG_TUTORIALGRANTPIPS))]
    private void ReceiveTutorialGrantPips(TUTORIAL_108_PROTOCOL.MSG_TUTORIALGRANTPIPS message) {
        if (!_isActive) {
            return;
        }

        _tutorialDirector.ReceiveGrantPips(Sender, message);
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE))]
    private void ReceiveCombatMove(COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE message) {
        if (!_isActive) {
            return;
        }

        // Find which sub circle this is.
        var caster = SubCircles.FirstOrDefault(x => x.ParticipantActor == message.Actor)
            ?? throw new Exception("Combat move received from an actor that is not in the duel.");

        // Tutorial duels script the golems' moves server-side; drop their own AI moves so a pass cannot
        // overwrite the scripted attack. Player moves still flow through normally.
        if (_tutorialDirector.IsActive && caster.OccupiedTeam == CombatTeam.Monster) {
            return;
        }

        // Conceding a PvP duel is the Flee button, and it counts at any time: on the other side's turn too, or
        // outside of the planning phase (Niduus, 2026-10-23: concede did nothing).
        if (IsPvPDuel && (CombatMoveType) message.MoveType == CombatMoveType.Flee) {
            HandleFleeAction(caster);

            return;
        }

        if (!_awaitingCombatMoves) {
            Logger.Warning("Duel {0} | Slot {1} | Received combat move while not expecting it.",
                Logger.Args(Duel.m_duelID.Full, caster.SlotIndex));

            return;
        }

        if (IsPvPDuel && !IsPvPMoveAllowed(caster)) {
            return;
        }

        var moveType = (CombatMoveType) message.MoveType;

        // A defeated wizard sits the round out; only fleeing is still theirs to choose.
        if (!caster.IsAlive && moveType != CombatMoveType.Flee) {
            return;
        }

        switch (moveType) {
            case CombatMoveType.Discard:
                var discardCandidate = caster.GetSpellFromLastHand(message.SpellSelection);
                if (IsEnchantment(discardCandidate) && TryFindEnchantTarget(caster, discardCandidate, message.SpellSelection, message.SpellTarget, message.RawSpellTarget, out var enchantTarget1)) {
                    HandleEnchantMove(caster, discardCandidate, enchantTarget1);
                }
                else {
                    HandleDiscardMove(caster, message.SpellSelection);
                }
                break;
            case CombatMoveType.Pass:
                HandlePassMove(caster);
                break;
            case CombatMoveType.Attack:
                HandleAttackMove(caster, message.SpellSelection, message.SpellTarget, message.RawSpellTarget);
                break;
            case CombatMoveType.Flee:
                HandleFleeAction(caster);
                break;
            case CombatMoveType.ChangeMind:
                HandleChangeMindAction(caster);
                break;
            default:
                // An enchantment played onto a card may come as its own move type.
                if (TryEnchantMove(caster, message.SpellSelection, message.SpellTarget, message.RawSpellTarget)) {
                    return;
                }

                Logger.Warning("Duel {0} | Slot {1} | Invalid combat move type: {2} (card {3}, target {4})",
                    Logger.Args(Duel.m_duelID.Full, caster.SlotIndex, moveType, message.SpellSelection, message.RawSpellTarget));
                break;
        }

        _tutorialDirector.OnCombatMoveQueued();

        // If by this point all participants have inputted their moves, we can start the next phase.
        var participantCount = AlivePlayerCount + AliveCreatureCount;
        if (CombatResolver.HaveAllParticipantsEnqueuedActions()) {
            _awaitingCombatMoves = false;

            // Adding a new timer will cancel the old one.
            var delay = TimeSpan.FromSeconds(1);
            Timers.StartSingleTimer(PLANNING_TIME_KEY, new COMBAT_106_PROTOCOL.MSG_PLANNINGPHASEOVER(), delay);
        }
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_PLANNINGPHASEOVER))]
    private void ReceivePlanningPhaseOver(COMBAT_106_PROTOCOL.MSG_PLANNINGPHASEOVER message) {
        if (!_isActive) {
            return;
        }

        // A repeated timer must not execute a half turn twice.
        if (IsPvPDuel && Duel.m_duelPhase != kDuelPhase.kPhase_Planning) {
            return;
        }

        Logger.Debug("Duel {0} | Round {1} over at {2}",
            Logger.Args(Duel.m_duelID.Full, Duel.m_roundNum, DateTime.Now.ToString("HH:mm:ss")));

        CountRoundForTamedDrops();

        // The execution phase begins. This is where combat actions take place and we actually see spell cinematics.
        _awaitingCombatMoves = false;
        Duel.m_duelPhase = kDuelPhase.kPhase_Execution;
        SendCombatPhase((byte) Duel.m_duelPhase);

        // The hands the game holds now, before the round uses up its cards (see SendHandWith).
        var handsBeforeRound = SnapshotHands();

        // Determine how long the cinematics will take.
        var cinematicTimeInSeconds = CombatResolver.ApplyQueuedCombatActions(out var actions);
        // [Battle] AnimationExtraSeconds: more time on top of the estimate, for a slow PC (none with instant cinematics).
        var waitSeconds = cinematicTimeInSeconds > 0 ? Math.Max(0f, cinematicTimeInSeconds + s_animationExtraSeconds) : 0f;
        Logger.Information("Duel {0} | Round {1} | Waiting {2:0.0} s for {3} actions to play.",
            Logger.Args(SigilId, Duel.m_roundNum, waitSeconds, actions?.m_actionList?.Count ?? 0));
        _roundWait = (Duel.m_roundNum, cinematicTimeInSeconds, waitSeconds, DateTime.UtcNow);
        var actionExecutionTime = TimeSpan.FromSeconds(waitSeconds);
        Duel.m_executionPhaseTimer = (float) actionExecutionTime.TotalSeconds;

        // Pet May Cast cards go into their owner's hand first, or the game skips them.
        foreach (var (owner, cards) in CombatResolver.PetCastCards) {
            SendHandWith(owner, cards, handsBeforeRound.GetValueOrDefault(owner));
        }

        // Serialize the combat actions and send them to the clients.
        if (!_serializer.Serialize(actions, _combatParticipantHandFlags, out var buffer)) {
            Logger.Error("Failed to serialize combat actions for duel {0}",
                Logger.Args(SigilId));

            return;
        }

        var msg = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATACTIONS {
            DuelID = SigilId,
            ActionData = buffer,
        };
        ZoneBroadcast(msg);
        ScheduleHealthGlobes();

        Timers.StartSingleTimer(RESOUTION_TIME_KEY, new COMBAT_106_PROTOCOL.MSG_ROUNDRESOLUTION(), actionExecutionTime);
    }

    // [Battle] AnimationExtraSeconds: seconds added to every round's wait for its animations (0 to 10).
    private static readonly float s_animationExtraSeconds
        = Math.Clamp(ConfigurationManager.GetValue("Battle.AnimationExtraSeconds", 1f), 0f, 10f);

    // The round whose animations are playing: its estimate, what the duel waits, and when the actions went out.
    private (int Round, float Estimate, float Wait, DateTime Sent)? _roundWait;

    /// <summary>
    /// One line per round on how long its animations were given. The game sends nothing when it has played a round
    /// (no such message in its protocol, and none in Niduus's logs), so the duel always waits the estimate.
    /// </summary>
    private void LogRoundWaitOver() {
        try {
            if (_roundWait is not { } wait) {
                return;
            }

            _roundWait = null;
            Logger.Information("Duel {0} | Round {1} | animations done after {2:0.0} s (estimate {3:0.0} s, by timer{4}).",
                Logger.Args(SigilId, wait.Round, (DateTime.UtcNow - wait.Sent).TotalSeconds, wait.Estimate,
                    s_animationExtraSeconds > 0 ? $", +{s_animationExtraSeconds:0.#} s extra" : ""));
        }
        catch (Exception e) {
            Logger.Error("Duel {0} | Round wait log failed: {1}", Logger.Args(SigilId, e));
        }
    }

    // Keys of the health globe timers still waiting, so a duel that ends early can drop them.
    private readonly List<string> _healthGlobeKeys = [];

    /// <summary>
    /// Moves each wizard's health globe as the round plays: one MSG_UPDATEHEALTH per step that changed their health,
    /// when that step's animation is over. The globe only moved on the next round's combat health (Niduus,
    /// 2026-10-22: "when it became the next turn, it did sync, but want it live and dynamic").
    /// </summary>
    private void ScheduleHealthGlobes() {
        CancelHealthGlobes();
        foreach (var beat in CombatResolver.HealthBeats) {
            if (beat.Circle._wizard is not { } wizard || beat.Circle.ParticipantActor is not { } actor) {
                continue;
            }

            var due = new COMBAT_106_PROTOCOL.MSG_HEALTHGLOBEDUE { Actor = actor, Wizard = wizard, Health = beat.Health };
            if (beat.Seconds <= 0) {
                SendHealthGlobe(due);

                continue;
            }

            var key = $"healthGlobe_{Guid.NewGuid():N}";
            _healthGlobeKeys.Add(key);
            Timers.StartSingleTimer(key, due, TimeSpan.FromSeconds(beat.Seconds));
        }

        if (CombatResolver.HealthBeats.Count > 0) {
            Logger.Debug("Duel {0} | Round {1} | Health globes: {2}",
                Logger.Args(SigilId, Duel.m_roundNum, string.Join(", ", CombatResolver.HealthBeats
                    .Select(b => $"slot {b.Circle.SlotIndex} {b.Health} at {b.Seconds:0.0} s"))));
        }
    }

    private void CancelHealthGlobes() {
        foreach (var key in _healthGlobeKeys) {
            Timers.Cancel(key);
        }
        _healthGlobeKeys.Clear();
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_HEALTHGLOBEDUE))]
    private void ReceiveHealthGlobeDue(COMBAT_106_PROTOCOL.MSG_HEALTHGLOBEDUE message) => SendHealthGlobe(message);

    private static void SendHealthGlobe(COMBAT_106_PROTOCOL.MSG_HEALTHGLOBEDUE due)
        => due.Actor.Tell(NewHealthGlobe(due.Wizard, due.Health));

    // The globe's health message. Its maximum is the client's own (the game adds its gear bonus itself).
    private static WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH NewHealthGlobe(Wizard wizard, int health) => new() {
        CharacterID = wizard.GameObjectID,
        NewHealth = Math.Max(0, health),
        NewHealthMax = wizard.GameStats.GetClientTypeAlternative().m_baseHitpoints,
        DisplayDiff = 0,
    };

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_ROUNDRESOLUTION))]
    private void ReceiveRoundResolution(COMBAT_106_PROTOCOL.MSG_ROUNDRESOLUTION message) {
        if (!_isActive) {
            return;
        }

        LogRoundWaitOver();

        // Iterate through dead creature participants and remove them from the duel.
        // Players can be healed and therefore don't need to be removed.
        EnactActionOnSubCircles(circle => {
            // A dead minion can't be revived; remove it like an enemy.
            if (!circle.IsHumanPlayer && !circle.IsAlive) {
                NoteDefeatedEnemyMinion(circle);
                var removeMsg = new COMBAT_106_PROTOCOL.MSG_COMBATDEATH();
                circle.ParticipantActor.Tell(removeMsg);
            }
        });

        // All spells have been called. Inform the client whether this duel continues or ends.
        Duel.m_duelPhase = kDuelPhase.kPhase_Resolution;
        SendCombatPhase((byte) Duel.m_duelPhase);

        if (IsPvPDuel) {
            ContinuePvPDuel();

            return;
        }

        var playersWin = AliveCreatureCount <= 0;
        var creaturesWin = AlivePlayerCount <= 0;
        if (!playersWin && !creaturesWin) {
            // Continue. Start a new round.
            Self.Tell(new COMBAT_106_PROTOCOL.MSG_NEWROUND());

            return;
        }

        EndDuel();
    }

    private void ContinuePvPDuel() {
        if (IsPvPSideWiped()) {
            EndDuel();

            return;
        }

        if (_pvp.TryBeginSecondHalfTurn()) {
            // The other team acts next in the same round. The starter team gains its pips as phase two begins.
            DoPipGain(_pvp.RoundStarter);
            BeginPvPHalfTurn();
            SendUpFirst(Duel.m_roundNum);

            var delay = TimeSpan.FromSeconds(DUEL_NEW_ROUND_DELAY);
            Timers.StartSingleTimer(PREPLANNING_TIME_KEY, new COMBAT_106_PROTOCOL.MSG_PLANNINGPHASEBEGIN(), delay);

            return;
        }

        Self.Tell(new COMBAT_106_PROTOCOL.MSG_NEWROUND());
    }

    private void BeginPvPHalfTurn() {
        // Only the members of the acting team may queue moves, and its minions plan now.
        var acting = _pvp.ActingTeam;
        CombatResolver.Reset();
        CombatResolver.ActingTeam = acting;
        _awaitingCombatMoves = true;

        if (acting is { } actingTeam) {
            Duel.m_firstTeamToAct = (int) actingTeam;
            Duel.m_altTurnCounter = _pvp.HalfTurn;
            Duel.m_originalFirstTeamToAct = (int) _pvp.OriginalFirstTeam;
        }

        EnactActionOnSubCircles(circle => {
            if (acting is { } team) {
                circle.CombatParticipant.m_myTeamTurn = circle.OccupiedTeam == team;
            }

            // The AI of a minion plans when its team may act.
            if (circle.IsSummonedMinion && (acting is null || circle.OccupiedTeam == acting)) {
                circle.ParticipantActor.Tell(new COMBAT_106_PROTOCOL.MSG_NEWROUND());
            }
        });

        // A participant's turn flag only reaches the game with the duel object; without it the waiting
        // team's cards never fade.
        if (acting is not null) {
            BroadcastDuel();
        }
    }

    private bool IsPvPMoveAllowed(CombatDuelSubCircle caster) {
        if (_pvp.ActingTeam is { } acting && caster.OccupiedTeam != acting) {
            Logger.Debug("Duel {0} | Slot {1} | Move dropped, the other team is acting.",
                Logger.Args(Duel.m_duelID.Full, caster.SlotIndex));

            return false;
        }

        // The AI of a minion plans ahead; a wizard may only move once the planning phase has begun.
        if (caster.IsHumanPlayer && Duel.m_duelPhase != kDuelPhase.kPhase_Planning) {
            Logger.Debug("Duel {0} | Slot {1} | Move dropped, the duel is not in its planning phase.",
                Logger.Args(Duel.m_duelID.Full, caster.SlotIndex));

            return false;
        }

        return true;
    }

    [MessageHandler(typeof(DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_ENDDUEL))]
    private void DespawnDuel() {
        _isActive = false;

        // Minions are children of this sigil entity, which persists between fights; MSG_COMBATDEATH
        // deletes them outright.
        EnactActionOnSubCircles(circle => {
            if (circle.IsSummonedMinion && circle.ParticipantActor is not null) {
                circle.ParticipantActor.Tell(new COMBAT_106_PROTOCOL.MSG_COMBATDEATH());
            }
        });

        _renderComponent?.Disable();
        Entity.DespawnObject();
        if (IsDuelCircle) {
            // Back on the floor once the game has let the battle's circle go.
            Timers.StartSingleTimer(DUEL_CIRCLE_SHOW_KEY, new ZONE_102_PROTOCOL.MSG_DUELCIRCLETICK { Show = true }, s_showAgainDelay);
        }

        _entitiesInRange.Clear();
        _groupReservations.Clear();
        _recentWizardsMet.Clear();
        EndEncounterBattle();
    }

    // The friends window's Recent tab: every wizard in this battle met the others once (players who join late too).
    private readonly HashSet<(ulong, ulong)> _recentWizardsMet = [];

    private void RememberRecentWizards() {
        try {
            var wizards = ActiveSubCircles.Where(c => c.IsHumanPlayer && c._wizard is not null)
                .Select(c => c._wizard.CharId).Distinct().Order().ToArray();
            for (var i = 0; i < wizards.Length; i++) {
                for (var j = i + 1; j < wizards.Length; j++) {
                    if (_recentWizardsMet.Add((wizards[i], wizards[j]))) {
                        RecentWizards.Met([wizards[i], wizards[j]], IsPvPDuel ? RecentWizardKind.PvP : RecentWizardKind.Combat);
                    }
                }
            }
        }
        catch (Exception ex) {
            Logger.Error("Recent wizards | Battle {0}: {1}", Logger.Args(Duel?.m_duelID.Full, ex.Message));
        }
    }

    // The zone's distinct spawners wait while an encounter battle runs; this lets them go and, after a lost battle,
    // gives the room back the minions that fought.
    private void EndEncounterBattle() {
        if (!_encounterBattleCounted) {
            return;
        }

        _encounterBattleCounted = false;
        if (Entity.Zone is { } zone) {
            System.Threading.Interlocked.Decrement(ref zone.EncounterBattles);
        }

        Entity.ZoneRef.Tell(new ZONE_102_PROTOCOL.MSG_ENCOUNTERBATTLEENDED { ResetIds = _encounterResetIds });
        _encounterResetIds = [];
        _encounterZoneMinions.Clear();
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_CLIENT_DISCONNECT))]
    private void ReceiveClientDisconnect(GAME_5_PROTOCOL.MSG_CLIENT_DISCONNECT message) {
        if (!_isActive) {
            return;
        }

        // Find the sub circle that the client was in and remove them from the duel.
        var subCircle = SubCircles.FirstOrDefault(x => x.ParticipantActor == Sender);
        if (subCircle is null) {
            return;
        }

        _pvp?.MarkDisconnected(Sender);

        // Handle this as if it were the flee action, without its mana cost: the wizard did not flee.
        HandleFleeAction(subCircle, drainMana: false);
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_QUERY_LOGOUT))]
    private void ReceiveQueryLogout(GAME_5_PROTOCOL.MSG_QUERY_LOGOUT message) {
        if (!_isActive) {
            return;
        }

        // Find the sub circle that the client was in and remove them from the duel.
        var subCircle = SubCircles.FirstOrDefault(x => x.ParticipantActor == Sender);
        if (subCircle is null) {
            return;
        }

        _pvp?.MarkDisconnected(Sender);

        // Handle this as if it were the flee action, without its mana cost: the wizard did not flee.
        HandleFleeAction(subCircle, drainMana: false);
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_DEFERREDMINIONSUMMON))]
    private void ReceiveDeferredMinionSummon(ZONE_102_PROTOCOL.MSG_DEFERREDMINIONSUMMON message) {
        // Drop if the duel ended or restarted during the summon animation.
        if (!_isActive || message.Caster is null || Array.IndexOf(SubCircles, message.Caster) < 0) {
            Logger.Information("Duel {0} | deferred minion summon (tid {1}) dropped, duel no longer active.",
                Logger.Args(Duel.m_duelID.Full, message.CreatureTid));

            return;
        }

        // A creature's summon (the Fairy Queen's fairies) brings its minion in on its own side, not the wizards'.
        SpawnAndAssignMinion(message.CreatureTid, message.Caster,
            forEnemies: !Duel.m_bPVP && message.Caster.OccupiedTeam == CombatTeam.Monster);
    }

    private void InitializeDuel(Dictionary<IActorRef, CoreObject> startingParticipants) {
        Duel = CreateDuelWithDefaults();
        SubCircles = CreateDuelActorSubCircles(_sigilTemplate);
        CombatResolver = new Combat.CombatResolver(Duel, SubCircles);
        _defeatedEnemyMinions.Clear();

        // Determine which team goes up first. Tutorial duels override this with the golems first.
        Duel.m_firstTeamToAct = (int) DetermineFirstTeam();
        _tutorialDirector.OnDuelCreated(Duel);

        // When the duel is created, it must be created by two suspects: the player and the creature.
        // The creatures will always be team A and the players will always be team B. Assign the first
        // participants to their respective sub circles.
        var startingCreatureActor = startingParticipants.First(x => x.Value.m_templateID != 1);
        var startingPlayerActor = startingParticipants.First(x => x.Value.m_templateID == 1);
        var startingCreatureObject = startingCreatureActor.Value;
        var startingPlayerObject = startingPlayerActor.Value;

        var availableCreatureSubCircle = PassiveCircle(true, PassiveCircleOf(startingCreatureObject))
            ?? GetAvailableSubCircleTeamCreature();
        var availablePlayerSubCircle = ChoosePlayerSubCircle(startingPlayerObject);

        if (availableCreatureSubCircle == null || availablePlayerSubCircle == null) {
            Logger.Error("Failed to find available sub circles for duel {0}", Logger.Args(SigilId));

            return;
        }

        AssignParticipantToSubCircle(availableCreatureSubCircle, startingCreatureActor.Key, startingCreatureObject);
        AssignParticipantToSubCircle(availablePlayerSubCircle, startingPlayerActor.Key, startingPlayerObject);

        _isActive = true;
        ReserveCirclesForGroupOf(startingPlayerObject);
        AddEncounterMinions(availableCreatureSubCircle, startingCreatureObject);
        BringPassiveMinions(availablePlayerSubCircle, startingPlayerObject);
        BringMinionsForPlayers();

        Logger.Debug("Duel {0} | Created. Grace period over in {1}",
            Logger.Args(Duel.m_duelID.Full, DUEL_GRACE_PERIOD_IN_SECONDS));
    }

    // A spiraldb-extra/ZoneObjects encounter (Niduus, 2026-10-21: Malistaire the Undying with 3 random Draconians):
    // a battle started against its boss gets MinionCount different minions on the boss's side. The zone's own
    // creatures of the encounter's templates that are walking the room come first, as on live where the minions are
    // there before the battle (Niduus, 2026-10-22); a short wait later, the ones still missing are spawned, picked at
    // random among the templates not already in. They take the free enemy circles before the first round.
    private void AddEncounterMinions(CombatDuelSubCircle bossCircle, CoreObject boss) {
        _battle++;
        _encounter = null;
        _encounterJoined.Clear();
        _encounterZoneMinions.Clear();
        // A zone file's encounter, else the boss's passive "Minions" cheat (the creatures it comes into a battle with).
        var encounter = Entity.Zone?.Extras.EncounterFor(boss.m_templateID) ?? PassiveEncounter(boss);
        _brought.Add((_battle, boss.m_globalID.Full));
        if (encounter is null || encounter.MinionCount <= 0 || encounter.Minions.Count == 0) {
            return;
        }

        _encounter = encounter;
        if (!_encounterBattleCounted && Entity.Zone is { } zone) {
            System.Threading.Interlocked.Increment(ref zone.EncounterBattles);
            _encounterBattleCounted = true;
        }
        _encounterBossCircle = bossCircle;
        Entity.ZoneRef.Tell(new ZONE_102_PROTOCOL.MSG_QUERYENCOUNTERMINIONS {
            TemplateIds = [.. encounter.Minions],
            Center = Entity.ActiveGameObject.m_location,
            Radius = encounter.JoinRadius,
        }, Self);
        Timers.StartSingleTimer(ENCOUNTER_MINIONS_KEY, new ZONE_102_PROTOCOL.MSG_ENCOUNTERMINIONSDUE { Battle = _battle },
            TimeSpan.FromSeconds(ENCOUNTER_JOIN_WAIT_IN_SECONDS));
    }

    // Who already brought their passive minions, by battle and global id.
    private readonly HashSet<(int Battle, ulong Who)> _brought = [];

    // The passive "Minions" cheat of whoever's cheats they are (BossCheats.cs): a creature's file, or the player cheats
    // for the wizard played on this PC. Null when none.
    private static BossCheats.BossCheat PassiveMinionsOf(CoreObject who) {
        if (who is null) {
            return null;
        }

        if (who.m_templateID == 1) {
            return Wizard.TryGetCharacterId(who.m_globalID, out var charId) && PlayerCheats.IsLocalChar(charId)
                ? BossCheats.MinionsOf(PlayerCheats.Current()) : null;
        }

        return BossCheats.MinionsOf(BossCheats.For(Customs.KeyOf(who)));
    }

    // Every passive "Minions" cheat of whoever's cheats they are that waits for a number of players. Empty when none.
    private static List<BossCheats.BossCheat> PassiveMinionsForPlayersOf(CoreObject who) {
        if (who is null) {
            return [];
        }

        if (who.m_templateID == 1) {
            return Wizard.TryGetCharacterId(who.m_globalID, out var charId) && PlayerCheats.IsLocalChar(charId)
                ? BossCheats.MinionsForPlayersOf(PlayerCheats.Current()) : [];
        }

        return BossCheats.MinionsForPlayersOf(BossCheats.For(Customs.KeyOf(who)));
    }

    // Which players-count "Minions" cheats already brought their creatures, by battle, global id and cheat.
    private readonly HashSet<(int Battle, ulong Who, int Cheat)> _broughtForPlayers = [];

    // A passive "Minions" cheat with a players count (Wizard404's player counts: Dr. Neaux's extra pool minion and the
    // Gussied-Up Gator's two more with 3 wizards): once that many stand on the other side, while the battle is still in
    // its first round, its creatures are summoned onto its side, leaving out those templates already standing there.
    // Waits for a zone encounter still gathering its minions, so those take their circles first.
    private void BringMinionsForPlayers() {
        if (!_isActive || _encounter is not null || Duel.m_roundNum > 1) {
            return;
        }

        foreach (var circle in SubCircles.Where(c => c is { Occupied: true, IsAlive: true, IsSummonedMinion: false }).ToList()) {
            if (circle.ParticipantObject is not { } who) {
                continue;
            }

            var cheats = PassiveMinionsForPlayersOf(who);
            if (cheats.Count == 0) {
                continue;
            }

            var players = SubCircles.Count(c => c is not null && c.Occupied && c.IsAlive && c.OccupiedTeam != circle.OccupiedTeam);
            for (var i = 0; i < cheats.Count; i++) {
                if (!BossCheats.PlayersFit(cheats[i], players) || !_broughtForPlayers.Add((_battle, who.m_globalID.Full, i))) {
                    continue;
                }

                var spawned = new List<uint>();
                foreach (var tid in BossCheats.BroughtBy(cheats[i]).Distinct()) {
                    if (SubCircles.Any(c => c is not null && c.Occupied && c.IsAlive && c.OccupiedTeam == circle.OccupiedTeam
                                            && c.ParticipantObject is { } o && o.m_templateID.Full == tid)) {
                        continue;
                    }

                    if (SpawnAndAssignMinion(tid, circle, forEnemies: circle.OccupiedTeam == CombatTeam.Monster) is not null) {
                        spawned.Add(tid);
                    }
                }

                Logger.Information("Duel {0} | {1} comes with more for {2} players: {3} spawned ({4}).",
                    Logger.Args(Duel.m_duelID.Full, who.m_templateID.Full, players, spawned.Count, string.Join(", ", spawned)));
            }
        }
    }

    // A battle started against a creature with a passive "Minions" cheat: those creatures join it, as a zone file's
    // encounter does (the zone's own ones of those templates first, then spawned ones). Null when it has none.
    private static ZoneObjects.ZoneEncounter PassiveEncounter(CoreObject boss) {
        var brought = BossCheats.BroughtBy(PassiveMinionsOf(boss)).Distinct().ToList();

        return brought.Count == 0 ? null : new ZoneObjects.ZoneEncounter {
            Boss = boss.m_templateID.Full,
            Notes = "passive Minions cheat",
            Minions = [.. brought.Select(id => (ulong) id)],
            MinionCount = brought.Count,
            JoinRadius = 0,
        };
    }

    // Anyone else with a passive "Minions" cheat (a wizard, a boss that walks in): its creatures are summoned onto its
    // side as it joins, once a battle, leaving out any of those templates already standing there.
    private void BringPassiveMinions(CombatDuelSubCircle circle, CoreObject who) {
        if (circle is null || who is null || circle.IsSummonedMinion || !_brought.Add((_battle, who.m_globalID.Full))) {
            return;
        }

        var cheat = PassiveMinionsOf(who);
        if (cheat is null) {
            return;
        }

        foreach (var tid in BossCheats.BroughtBy(cheat)) {
            if (SubCircles.Any(c => c is not null && c.Occupied && c.IsAlive && c.OccupiedTeam == circle.OccupiedTeam
                                    && c.ParticipantObject is { } o && o.m_templateID.Full == tid)) {
                continue;
            }

            SpawnAndAssignMinion(tid, circle, forEnemies: circle.OccupiedTeam == CombatTeam.Monster);
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_QUERYENCOUNTERMINIONSRSP))]
    private void ReceiveEncounterMinion(ZONE_102_PROTOCOL.MSG_QUERYENCOUNTERMINIONSRSP message) {
        var creature = message.CreatureObject;
        if (!_isActive || _encounter is null || creature is null || message.CreatureActor is null
            || _encounterJoined.Count >= _encounter.MinionCount || _encounterJoined.Contains(creature.m_templateID.Full)
            || SubCircles.Any(x => x?.ParticipantObject?.m_globalID == creature.m_globalID)
            // The encounter says how many minions fight (MinionCount), so the usual cap of 2 creatures plus one per
            // wizard does not apply: with one wizard in, only 1 of the room's Draconians joined and the other 2 stayed
            // walking the room while 2 more were spawned into the circle (Niduus, 2026-10-22). A free circle is enough.
            || GetAvailableSubCircleTeamCreature() is null) {
            return;
        }

        AddParticipant(creature, message.CreatureActor);
        if (SubCircles.Any(x => x?.ParticipantObject?.m_globalID == creature.m_globalID)) {
            _encounterJoined.Add(creature.m_templateID.Full);
            _encounterZoneMinions.Add(creature.m_globalID.Full);
            _entitiesInRange.Add(message.CreatureActor);
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_ENCOUNTERMINIONSDUE))]
    private void ReceiveEncounterMinionsDue(ZONE_102_PROTOCOL.MSG_ENCOUNTERMINIONSDUE message) {
        var encounter = _encounter;
        _encounter = null;
        if (!_isActive || encounter is null || message.Battle != _battle) {
            return;
        }

        var fromZone = _encounterJoined.Count;
        var picked = encounter.Minions.Where(m => !_encounterJoined.Contains(m))
            .OrderBy(_ => Random.Shared.Next()).Take(Math.Max(0, encounter.MinionCount - fromZone)).ToList();
        var spawned = 0;
        foreach (var minion in picked) {
            if (SpawnAndAssignMinion((uint) minion, _encounterBossCircle, forEnemies: true) is not null) {
                spawned++;
            }
        }

        Logger.Information("Duel {0} | encounter for {1}: {2} of the zone's minions joined, {3} of {4} spawned ({5}).",
            Logger.Args(Duel.m_duelID.Full, encounter.Boss, fromZone, spawned, picked.Count, string.Join(", ", picked)));
        BringMinionsForPlayers();
    }

    private Duel CreateDuelWithDefaults() => new() {
        m_duelID = SigilId,
        m_planningTimer = PLANNING_TIME,

        m_scalarDamage = _sigilTemplate.m_scalarDamagePvE,
        m_scalarResist = _sigilTemplate.m_scalarResistPvE,
        m_scalarPierce = _sigilTemplate.m_scalarPiercePvE,
        m_damageLimit = _sigilTemplate.m_damageLimitPvE,
        m_dK0 = _sigilTemplate.m_dK0PvE,
        m_dN0 = _sigilTemplate.m_dN0PvE,
        m_resistLimit = _sigilTemplate.m_resistLimitPvE,
        m_rK0 = _sigilTemplate.m_rK0PvE,
        m_rN0 = _sigilTemplate.m_rN0PvE,
        m_flatParticipantList = [],
        // The school pip gauge only shows when the duel says how full it can get.
        m_maxArchmasteryTeam0 = CombatDuelSubCircle.SchoolGaugeOn ? 100f : 0f,
        m_maxArchmasteryTeam1 = CombatDuelSubCircle.SchoolGaugeOn ? 100f : 0f,
        // TODO: boss cheats. No boss has scripted behavior yet: combat triggers are always empty and
        // NPCBehaviorTemplate.m_triggerList is never read. Needs a trigger hook (round start, spell cast, health
        // thresholds) and a script per boss. Not to be taken from Wizard101 Central (Niduus, 2026-10-18).
        m_duelModifier = new DuelModifier() {
            m_battlefieldEffects = [],
            m_combatTriggers = [],
            m_gameEffects = [],
        }
    };

    private Duel CreateDuelWithPvPDefaults(PvPDuelRules rules, uint leagueId) => new() {
        m_duelID = SigilId,
        m_bPVP = true,
        m_planningTimer = PvPSettings.PracticePlanningSeconds,

        m_scalarDamage = _sigilTemplate.m_scalarDamagePvP,
        m_scalarResist = _sigilTemplate.m_scalarResistPvP,
        m_scalarPierce = _sigilTemplate.m_scalarPiercePvP,
        m_damageLimit = _sigilTemplate.m_damageLimitPvP,
        m_dK0 = _sigilTemplate.m_dK0PvP,
        m_dN0 = _sigilTemplate.m_dN0PvP,
        m_resistLimit = _sigilTemplate.m_resistLimitPvP,
        m_rK0 = _sigilTemplate.m_rK0PvP,
        m_rN0 = _sigilTemplate.m_rN0PvP,
        m_shadowThresholdFactor = _sigilTemplate.m_shadowThresholdFactor,
        m_shadowPipRatingFactor = _sigilTemplate.m_shadowPipRatingFactor,
        m_alternateTurnCombatRule = PvPGameTypes.ToCombatRule(rules),
        m_combatRules = [PvPGameTypes.ToCombatRule(rules)],
        m_flatParticipantList = [],
        m_duelModifier = new DuelModifier() {
            m_battlefieldEffects = [],
            m_combatTriggers = [],
            m_gameEffects = PvPLeagueScaling.CreateClientEffects(leagueId),
        }
    };

    private void InitializePvPDuel(ZONE_102_PROTOCOL.MSG_STARTPVPMATCH message, PvPDuelRules rules) {
        Duel = CreateDuelWithPvPDefaults(rules, message.LeagueId);
        SubCircles = CreateDuelActorSubCircles(_sigilTemplate);
        CombatResolver = new Combat.CombatResolver(Duel, SubCircles);

        var firstTeam = DetermineFirstTeam();
        Duel.m_firstTeamToAct = (int) firstTeam;
        Duel.m_originalFirstTeamToAct = (int) firstTeam;
        _pvp = new PvPDuelState(message.MatchId, message.TournamentNameId, message.LeagueId, message.Roster, rules, firstTeam);

        // The roster is fixed: each player sits on a circle of their side and is moved onto it first, because
        // they stand wherever they arrived in the zone.
        // A duel circle's wizards walk onto their places, as into a battle with monsters; an arena moves them.
        foreach (var entry in message.Roster) {
            SeatPvPEntry(entry, moveThere: message.Rules is null);
        }

        _isActive = true;

        Logger.Debug("Duel {0} | PvP match {1} created with {2} players. Grace period over in {3}",
            Logger.Args(Duel.m_duelID.Full, message.MatchId, message.Roster.Count, DUEL_GRACE_PERIOD_IN_SECONDS));
    }

    private void SeatPvPEntry(PvPRosterEntry entry, bool moveThere) {
        var subCircle = GetAvailableSubCircle((CombatTeam) entry.TeamIndex)
            ?? throw new InvalidOperationException($"No free circle on side {entry.TeamIndex}.");

        if (moveThere) {
            ZoneBroadcast(new GAME_5_PROTOCOL.MSG_SERVERTELEPORT {
                LocationX = CompressTeleportCoordinate(subCircle.WorldPosition.X),
                LocationY = CompressTeleportCoordinate(subCircle.WorldPosition.Y),
                LocationZ = CompressTeleportCoordinate(subCircle.WorldPosition.Z),
                Direction = (byte) Math.Round(subCircle.WorldRotation / (2 * Math.PI) * 250),
                MobileID = entry.PlayerObject.m_nMobileID,
            });
        }

        AssignParticipantToSubCircle(subCircle, entry.Session, entry.PlayerObject);
    }

    private void CancelPvPMatch(ulong matchId, PvPCancelReason reason)
        => PvPDirector.Instance?.Tell(new PVP_111_PROTOCOL.MSG_MATCHCANCELLED { MatchId = matchId, Reason = reason });

    private void AbortPvPDuel(ulong matchId, PvPCancelReason reason) {
        // Seating failed halfway: let the seated players go, then ask the director to send everyone back.
        _isActive = false;
        if (_pvp is not null) {
            foreach (var entry in _pvp.Roster) {
                entry.Session.Tell(new COMBAT_106_PROTOCOL.MSG_PVPDUELENDED { MatchId = matchId, WinningTeam = -1 });
            }

            _pvp = null;
        }

        CancelPvPMatch(matchId, reason);
    }

    private CombatDuelSubCircle[] CreateDuelActorSubCircles(CombatSigilTemplate template) {
        var subCircles = template.m_subCircles;
        var subCircleObjs = new CombatDuelSubCircle[8];
        var sigilOrientation = Entity.ActiveGameObject.m_orientation;
        var sigilLocation = Entity.ActiveGameObject.m_location;

        // The sigil rotation is stored between -pi and pi. We need it to be between 0 and 2pi.
        var sigilRotation = sigilOrientation.Z;
        if (sigilRotation < 0) {
            sigilRotation = (2 * MathF.PI) + sigilRotation;
        }

        for (int i = 0; i < subCircles.Count; i++) {
            var rotation = subCircles[i].m_rotation;
            var radius = subCircles[i].m_radius;
            var color = subCircles[i].m_color;
            var rotationRadians = rotation * (MathF.PI / 180f);

            // The sigil is rotated by some degree. We need to find our x and y coordinates based on this rotation.
            var rotatedX = radius * MathF.Cos(rotationRadians - sigilRotation);
            var rotatedY = radius * MathF.Sin(rotationRadians - sigilRotation);
            var x = sigilLocation.X + rotatedX;
            var y = sigilLocation.Y + rotatedY;
            var rotatedSigilPos = new Vector3(x, y, sigilLocation.Z);

            // Now we know where the sigil is located, we need to calculate the facing direction of the sub circle.
            // Calculate the direction vector towards the center of the duel (only Z-axis in radians)
            var duelCenter = new Vector3(sigilLocation.X - x, sigilLocation.Y - y, 0);
            var faceTowardsYaw = MathF.Atan2(duelCenter.Y, duelCenter.X);
            // The yaw must be between 0 and 2PI. It must also be reversed as the client rotates clockwise.
            // The translation isn't perfect because of Gamebyro engine bullshit. We need to compensate for this.
            faceTowardsYaw = (2 * MathF.PI) - faceTowardsYaw - YAW_ERROR_COMPENSATION;
            if (faceTowardsYaw < 0) {
                faceTowardsYaw += 2 * MathF.PI;
            }

            // Cretae the sub circle object and add it to the array.
            var subCircle = new CombatDuelSubCircle(this, radius, rotation, color, i) {
                WorldPosition = rotatedSigilPos,
                WorldRotation = faceTowardsYaw,
                SlotName = subCircles[i].m_locationPreference,
                SlotType = IsCreatureSide(subCircles[i].m_locationType) ? CombatSlotType.Creature : CombatSlotType.Player
            };
            subCircleObjs[i] = subCircle;
        }

        return subCircleObjs;
    }

    private static bool IsCreatureSide(string locationType) {
        // The red circles of a PvP sigil are the first four, like the monster circles of a combat sigil.
        return locationType is "MonsterCircle" or "RedCircle";
    }

    private void EnactActionOnSubCircles(Action<CombatDuelSubCircle> action) {
        foreach (var subCircle in ActiveSubCircles) {
            action(subCircle);
        }
    }

    private CombatDuelSubCircle GetAvailableSubCircleTeamCreature() {
        for (int i = 0; i < 4; i++) {
            if (!SubCircles[i].Occupied) {
                return SubCircles[i];
            }
        }

        return null;
    }

    private CombatDuelSubCircle GetAvailableSubCircle(CombatTeam team)
        => team == CombatTeam.Monster ? GetAvailableSubCircleTeamCreature() : GetAvailableSubCircleTeamPlayer();

    private CombatDuelSubCircle GetAvailableSubCircleTeamPlayer() {
        for (int i = 4; i < 8; i++) {
            if (!SubCircles[i].Occupied) {
                return SubCircles[i];
            }
        }

        return null;
    }

    private bool AssignParticipantToSubCircle(CombatDuelSubCircle subCircle, IActorRef actorRef, CoreObject coreObject,
                                              bool isSummonedMinion = false, int minionOwnerSubCircle = 0) {
        if (subCircle.ParticipantActor != null) {
            return false;
        }

        subCircle.AssignParticipant(actorRef, coreObject, isSummonedMinion, minionOwnerSubCircle);

        return true;
    }

    internal void SummonMinion(uint creatureTid, CombatDuelSubCircle caster) {
        if (CoreObjectFactory.GetCoreTemplate(creatureTid) is null) {
            Logger.Warning("Duel {0} | minion summon: no template for creature tid {1}.",
                Logger.Args(Duel.m_duelID.Full, creatureTid));

            return;
        }

        // Cinematics before this cast plus the summon animation: the minion appears mid-cast.
        var castOffset = CurrentActionCinematicOffsetSeconds;
        var spawnDelay = castOffset + MINION_SUMMON_ANIMATION_DELAY;
        Timers.StartSingleTimer(
            $"minionSummon_{Guid.NewGuid():N}",
            new ZONE_102_PROTOCOL.MSG_DEFERREDMINIONSUMMON { CreatureTid = creatureTid, Caster = caster },
            TimeSpan.FromSeconds(spawnDelay));
    }

    internal static void OnMinionRemoved(CombatDuelSubCircle circle)
        => circle.RemoveParticipant();

    private void NoteDefeatedEnemyMinion(CombatDuelSubCircle circle) {
        if (circle.IsSummonedMinion && circle.OccupiedTeam == CombatTeam.Monster && circle.ParticipantObject is { } minion
            && _defeatedEnemyMinions.All(m => m.GlobalId != minion.m_globalID)) {
            _defeatedEnemyMinions.Add((minion.m_templateID, minion.m_globalID));
        }
    }

    /// <summary>
    /// A boss's second form (boss cheat Spawn): the creature joins on the boss's side and fights from the next round.
    /// </summary>
    /// <returns>The circle it took, or null when none was free.</returns>
    internal CombatDuelSubCircle SpawnBossForm(uint creatureTid, CombatDuelSubCircle boss)
        // A custom's second form is the custom its Forms names for that creature (Customs.cs), if any.
        => SpawnAndAssignMinion(creatureTid, boss, forEnemies: boss.OccupiedTeam == CombatTeam.Monster,
            customId: Customs.IdOf(Customs.FormKey(boss.ParticipantObject, creatureTid)));

    /// <summary>
    /// A cheat's minion (or a custom's form): a creature, as that custom when customId is set, on the side of whoever's
    /// cheat it is (a player cheat's minion fights for the wizard).
    /// </summary>
    internal CombatDuelSubCircle SpawnBossMinion(uint creatureTid, CombatDuelSubCircle boss, int customId)
        => SpawnAndAssignMinion(creatureTid, boss, forEnemies: boss.OccupiedTeam == CombatTeam.Monster, customId: customId);

    // forEnemies: .joinme puts the creature on the enemy side (it fights like a boss's minion).
    // customId: it comes in as that custom of the Customs tab (Customs.cs), 0 for the game's creature.
    // Returns the circle it took, or null.
    private CombatDuelSubCircle SpawnAndAssignMinion(uint creatureTid, CombatDuelSubCircle caster, bool forEnemies = false,
                                                     int customId = 0) {
        // A PvE minion fights for the wizards whoever summoned it; in PvP it fights for its summoner.
        var creatureSide = forEnemies || (Duel.m_bPVP && caster?.OccupiedTeam == CombatTeam.Monster);
        var slot = PassiveCircle(creatureSide,
                       BossCheats.CircleOf(BossCheats.For(customId > 0 ? Customs.Key(customId) : creatureTid)))
            ?? (forEnemies ? GetAvailableSubCircleTeamCreature()
                : Duel.m_bPVP ? GetAvailableSubCircle(caster.OccupiedTeam) : GetAvailableSubCircleTeamPlayer());
        if (slot is null) {
            Logger.Information("Duel {0} | minion summon (tid {1}) skipped, no free slot on the side of the caster.",
                Logger.Args(Duel.m_duelID.Full, creatureTid));

            return null;
        }

        var template = CoreObjectFactory.GetCoreTemplate(creatureTid);
        if (template is null) {
            Logger.Warning("Duel {0} | minion summon: no template for creature tid {1}.",
                Logger.Args(Duel.m_duelID.Full, creatureTid));

            return null;
        }

        var centre = Entity.ActiveGameObject?.m_location ?? (caster?.ParticipantObject?.m_location ?? default);
        var info = new CoreObjectInfo {
            m_templateID = creatureTid,
            m_location = centre,
            m_fScale = 1.0f,
        };
        var minionObj = CoreObjectFactory.FinalizeCoreObject(info, template);
        minionObj = CoreObjectFactory.InitializeCoreObjectBehaviors(minionObj, template);
        // Marked before its actor is made, so its name (NpcComponent) is the custom's.
        Customs.Mark(minionObj.m_globalID.Full, customId);

        var minionActor = Entity.SpawnCombatMinionActor(minionObj, template);
        if (minionActor is null) {
            return null;
        }

        // An enemy minion belongs to the creature that brought it in, or (.joinme, from a wizard) the first enemy in the fight.
        var owner = forEnemies && caster?.OccupiedTeam != CombatTeam.Monster
            ? SubCircles.FirstOrDefault(c => c.Occupied && c.OccupiedTeam == CombatTeam.Monster && c != slot)?.SlotIndex ?? slot.SlotIndex
            : caster?.SlotIndex ?? slot.SlotIndex;
        try {
            AssignParticipantToSubCircle(slot, minionActor, minionObj, isSummonedMinion: true,
                                         minionOwnerSubCircle: owner);
        }
        catch (Exception ex) {
            Logger.Error("Duel {0} | minion summon: failed to assign tid {1} to slot {2}: {3}",
                Logger.Args(Duel.m_duelID.Full, creatureTid, slot.SlotIndex, ex));
            slot.RemoveParticipant();
            minionActor.Tell(PoisonPill.Instance);

            return null;
        }

        Logger.Information("Duel {0} | summoned minion tid {1} into {2}-team slot {3} (caught up next round).",
            Logger.Args(Duel.m_duelID.Full, creatureTid, forEnemies ? "enemy" : "player", slot.SlotIndex));

        return slot;
    }

    // The free enemy circle nearest to where a creature stands, or null when none is free.
    private CombatDuelSubCircle NearestFreeCreatureCircle(CoreObject creature)
        => SubCircles.Take(4).Where(c => c is not null && !c.Occupied)
            .OrderBy(c => (c.WorldPosition - creature.m_location).LengthSquared())
            .FirstOrDefault();

    // Passive "Circle" (BossCheats.cs; Niduus, 2026-10-24): the circle, 1-4 of its side, a creature's cheat file (or,
    // for the wizard played on this PC, the player cheats) says it always takes, or 0.
    private static int PassiveCircleOf(CoreObject participant) {
        if (participant is null) {
            return 0;
        }

        if (participant.m_templateID == 1) {
            return Wizard.TryGetCharacterId(participant.m_globalID, out var charId) && PlayerCheats.IsLocalChar(charId)
                ? BossCheats.CircleOf(PlayerCheats.Current()) : 0;
        }

        return BossCheats.CircleOf(BossCheats.For(Customs.KeyOf(participant)));
    }

    // Circle number (1-4) of a side, counted in slot order; if it is taken (or not allowed), the free allowed circle of
    // that side nearest to it. Null when number is 0 or nothing on that side is free.
    private CombatDuelSubCircle PassiveCircle(bool creatureSide, int number, Func<CombatDuelSubCircle, bool> allowed = null) {
        if (number is < 1 or > 4) {
            return null;
        }

        var side = SubCircles.Skip(creatureSide ? 0 : 4).Take(4).Where(c => c is not null).OrderBy(c => c.SlotIndex).ToList();
        var wanted = side.ElementAtOrDefault(number - 1);
        if (wanted is null) {
            return null;
        }

        bool Free(CombatDuelSubCircle c) => !c.Occupied && (allowed?.Invoke(c) ?? true);

        return Free(wanted) ? wanted
            : side.Where(Free).OrderBy(c => (c.WorldPosition - wanted.WorldPosition).LengthSquared())
                .ThenBy(c => Math.Abs(c.SlotIndex - wanted.SlotIndex)).FirstOrDefault();
    }

    /// <param name="preferred">A free enemy circle for a creature to take, or null for the first free one.</param>
    private void AddParticipant(CoreObject participantObject, IActorRef participantActor, CombatDuelSubCircle preferred = null) {
        var isPlayer = participantObject.m_templateID == 1;

        var alreadyInDuel = SubCircles.Any(x => x?.ParticipantObject?.m_globalID == participantObject.m_globalID);
        if (alreadyInDuel) {
            return;
        }

        // A passive circle wins over where it stands (preferred).
        var subCircle = isPlayer ? ChoosePlayerSubCircle(participantObject)
            : PassiveCircle(true, PassiveCircleOf(participantObject))
              ?? (preferred is { Occupied: false } ? preferred : GetAvailableSubCircleTeamCreature());
        if (subCircle is null) {
            if (isPlayer && _groupReservations.Count > 0) {
                Logger.Debug("Duel {DuelId} | Every free circle is held for a group; {Participant} cannot join.",
                    Logger.Args(Duel.m_duelID.Full, participantObject.m_debugName));

                return;
            }

            Logger.Warning("Duel {0} | No available sub circles for participant {1}",
                Logger.Args(Duel.m_duelID.Full, participantObject.m_globalID));

            return;
        }

        AssignParticipantToSubCircle(subCircle, participantActor, participantObject);

        Logger.Debug("Duel {0} | Slot {1} | Participant {2} joined",
            Logger.Args(Duel.m_duelID.Full, subCircle.SlotIndex, participantObject.m_debugName));

        ShowCreaturesOfDuel(subCircle);
        BringPassiveMinions(subCircle, participantObject);
        BringMinionsForPlayers();

        if (isPlayer) {
            ReserveCirclesForGroupOf(participantObject);
            GiveBackPipsOfLeaver(subCircle);
            JoinLateWizardNow(subCircle);
        }
    }

    // The pips a wizard had when they logged out or dropped from this battle, by character.
    private readonly Dictionary<ulong, PipCount> _pipsOfLeavers = new();

    private void GiveBackPipsOfLeaver(CombatDuelSubCircle circle) {
        if (circle?._wizard is not { } wizard || circle.CombatParticipant is not { } participant
            || !_pipsOfLeavers.Remove(wizard.CharId, out var pips)) {
            return;
        }

        participant.m_pipCount = pips;
        Logger.Information("Duel {0} | Slot {1} | {2} came back into the battle with their pips ({3} pips, {4} power) and {5} health",
            Logger.Args(Duel.m_duelID.Full, circle.SlotIndex, wizard.CharId, pips.m_genericPips, pips.m_powerPips,
                wizard.GameStats.m_currentHitpoints));
    }

    // A wizard who joins a battle that is running must see every creature in it, and a creature that joins must be seen
    // by every wizard in it (Niduus, 2026-10-22: after coming back into a battle the enemy circles were empty while the
    // battle went on; the game had no object for the creature, so it drew nothing and its name was blank). Each creature
    // sends its object to the wizard again; a game that has it already ignores the copy.
    private void ShowCreaturesOfDuel(CombatDuelSubCircle joined) {
        if (joined is null || !joined.Occupied) {
            return;
        }

        var placeOnCircles = new List<WIZARD_12_PROTOCOL.MSG_AGGRO>();
        foreach (var other in ActiveSubCircles) {
            if (other == joined || other.ParticipantActor is null) {
                continue;
            }

            if (joined.IsHumanPlayer && !other.IsHumanPlayer && !other.IsSummonedMinion) {
                other.ParticipantActor.Tell(new ZONE_102_PROTOCOL.MSG_SHOWDUELPARTICIPANT { PlayerActor = joined.ParticipantActor });
            }
            else if (!joined.IsHumanPlayer && !joined.IsSummonedMinion && other.IsHumanPlayer) {
                joined.ParticipantActor.Tell(new ZONE_102_PROTOCOL.MSG_SHOWDUELPARTICIPANT { PlayerActor = other.ParticipantActor });
            }

            // The wizard's game makes every object of the battle where the zone left it; each one is sent onto its
            // circle, as everyone else saw it walk in (Niduus, 2026-10-23: after a relog the returning wizard saw two of
            // four enemies on their circles and his friend in the wrong place).
            if (joined.IsHumanPlayer && other.ParticipantObject is { } otherObject) {
                placeOnCircles.Add(new WIZARD_12_PROTOCOL.MSG_AGGRO {
                    GlobalID = otherObject.m_globalID,
                    LocX = other.WorldPosition.X,
                    LocY = other.WorldPosition.Y,
                    LocZ = other.WorldPosition.Z,
                    Yaw = other.WorldRotation,
                    SigilGID = SigilId,
                });
            }
        }

        // A moment later, once the objects have reached the game.
        if (placeOnCircles.Count > 0 && joined.ParticipantActor is { } wizardActor) {
            _ = System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(1.5)).ContinueWith(_ => {
                foreach (var aggro in placeOnCircles) {
                    wizardActor.Tell(aggro);
                }
            });
        }
    }

    // The duel object the game gets when it comes into the zone lists the battle's participants. One that left (fled,
    // logged out, was disconnected) must leave that list too: a wizard who logged out and back in was still listed,
    // so his game put him on his circle twice ("Subcircle 5 is already in use"), showed the stale copy's pips (none)
    // and counted him twice in the battle.
    private void ForgetParticipant(GID ownerId) {
        var owner = ownerId.Full;
        Duel.m_flatParticipantList?.RemoveAll(p => p is not null && p.m_ownerID.Full == owner);
    }

    [MessageHandler(typeof(GROUP_109_PROTOCOL.MSG_JOINRESERVEDDUEL))]
    private void ReceiveJoinReservedDuel(GROUP_109_PROTOCOL.MSG_JOINRESERVEDDUEL message) {
        if (!_isActive || !_groupReservations.IsReservedFor(message.CharId)) {
            return;
        }

        var subCircle = _groupReservations.ChoosePlayerCircle(message.CharId, SubCircles);
        if (subCircle is null) {
            return;
        }

        // The group mate may be anywhere in the zone; put them on their circle before they join.
        ZoneBroadcast(new GAME_5_PROTOCOL.MSG_SERVERTELEPORT {
            LocationX = CompressTeleportCoordinate(subCircle.WorldPosition.X),
            LocationY = CompressTeleportCoordinate(subCircle.WorldPosition.Y),
            LocationZ = CompressTeleportCoordinate(subCircle.WorldPosition.Z),
            Direction = (byte) Math.Round(subCircle.WorldRotation / (2 * Math.PI) * 250),
            MobileID = message.PlayerObject.m_nMobileID,
        });

        AddParticipant(message.PlayerObject, message.PlayerActor);
    }

    private CombatDuelSubCircle ChoosePlayerSubCircle(CoreObject playerObject) {
        if (!Wizard.TryGetCharacterId(playerObject.m_globalID, out var charId)) {
            return GetAvailableSubCircleTeamPlayer();
        }

        // A passive circle (player cheats), unless a group already holds a circle for this wizard; circles held for
        // someone else are left to them.
        var subCircle = _groupReservations.IsReservedFor(charId) ? null
            : PassiveCircle(false, PassiveCircleOf(playerObject), c => !_groupReservations.IsHeldForSomeoneElse(c, charId));
        subCircle ??= _groupReservations.ChoosePlayerCircle(charId, SubCircles);
        if (subCircle is not null) {
            _groupReservations.Release(charId);
        }

        return subCircle;
    }

    private void ReserveCirclesForGroupOf(CoreObject playerObject) {
        if (IsScriptedDuel() || IsPvPDuel || !Wizard.TryGetCharacterId(playerObject.m_globalID, out var charId)) {
            return;
        }

        foreach (var groupMate in _groupReservations.ReserveForGroupOf(charId, ZoneActor, SubCircles)) {
            Logger.Debug("Duel {DuelId} | Holding a circle for group mate {GroupMate} of {CharId}.",
                Logger.Args(Duel.m_duelID.Full, groupMate.CharId, charId));

            // Accepting sends MSG_GotoPlayerConfirm back, which GroupService turns into MSG_JOINRESERVEDDUEL.
            groupMate.Session?.Tell(new WIZARD2_53_PROTOCOL.MSG_GotoPlayerConfirm {
                PromptKey = GROUP_JOIN_PROMPT_KEY,
                TargetCharacterID = charId,
                DisableCrossPlay = 0,
                ClientPlatform = 0
            });
        }
    }

    private void ApplyFullGroupInitiative() {
        if (IsScriptedDuel() || IsPvPDuel || !CombatGroupReservations.IsFullGroup(SubCircles)) {
            return;
        }

        Duel.m_fullPartyGroup = true;
        Duel.m_firstTeamToAct = (int) CombatTeam.Player;

        // The client reads the full group flag from the duel object when its combat intro starts.
        BroadcastDuel();

        Logger.Debug("Duel {DuelId} | A full group takes the first turn.", Logger.Args(Duel.m_duelID.Full));
    }

    private void BroadcastDuel() {
        var duelBehavior = GetClientBehaviorInstance();
        if (_serializer.Serialize(duelBehavior, _combatParticipantFlags, out var duelData)) {
            ZoneBroadcast(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_DUEL {
                Data = duelData,
            });
        }
    }

    private static ushort CompressTeleportCoordinate(float coordinate)
        => unchecked((ushort) (short) MathF.Round(coordinate / 4));

    private void SendCombatPhase(byte phase) {
        if (NewCombatPhase(phase) is { } msg) {
            DuelBroadcast(msg);
        }
    }

    private DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATPHASE NewCombatPhase(byte phase) {
        // Determine which sigil slot the client should point its turn indicator at.
        var upFirstSigilSlot = GetUpFirstSigilSlot();

        // Serialize the up first data and send it to all the combat participants.
        // This is the one instance where the client sends a versionable object to the client.
        var versionableSerializer = new ObjectSerializer(
            Versionable: true,
            Behaviors: SerializerFlags.None
        );

        var upFirst = new UpFirstData {
            m_resultType = 122, // Always recorded as 122, per packet captures.
            m_upFirst = upFirstSigilSlot,
            m_roundNum = Duel.m_roundNum,
        };
        if (!versionableSerializer.Serialize(upFirst, _upFirstFlags, out var upFirstData)) {
            Logger.Error("Failed to serialize up first data for duel {0}",
                Logger.Args(SigilId));

            return null;
        }

        return new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATPHASE() {
            DuelID = SigilId,
            NewPhase = phase,
            PlayerID = 0, // Always recorded as 0
            Data = phase == 1 ? upFirstData : "",
        };
    }

    private void SendUpFirst(int roundNum) {
        var upFirstSigilSlot = GetUpFirstSigilSlot();

        var upFirstMsg = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATUPFIRST {
            DuelID = SigilId,
            RoundNum = (ushort) roundNum,
            FirstTeamToAct = (byte) Duel.m_firstTeamToAct,
            UpFirst = upFirstSigilSlot,
        };
        ZoneBroadcast(upFirstMsg);
    }

    private void SendCombatUI(byte planningPhaseTimer) {
        // When the teams take turns, every client is told whose turn it is and only that team gets the countdown.
        var acting = IsPvPDuel ? _pvp.ActingTeam : null;
        var combatUiMsg = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_SHOWCOMBATUI {
            DuelID = SigilId,
            AltTurn = acting is null ? 0 : 1,
            AltTurnTeam = (int?) acting ?? 0,
        };
        var planningMsg = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_SETPLANNINGPHASETIMER {
            DuelID = SigilId,
            Time = planningPhaseTimer,
        };

        HumanBroadcast(combatUiMsg);
        if (acting is { } actingTeam) {
            TeamBroadcast(actingTeam, planningMsg);
        } else {
            HumanBroadcast(planningMsg);
        }
    }

    private void SendCombatStats() => EnactActionOnSubCircles(circle => {
        // Serialize participant stats and send them to the participant, locally.
        var participantCombatsStats = circle.ClientGameStats;
        if (participantCombatsStats is null) {
            Logger.Error("Failed to get combat stats for duel {0}",
                Logger.Args(SigilId));

            return;
        }
        circle.ApplyPipGauges(participantCombatsStats);

        if (!_serializer.Serialize(participantCombatsStats, _combatParticipantStatFlags, out var buffer)) {
            Logger.Error("Failed to serialize combat stats for duel {0}",
                Logger.Args(SigilId));

            return;
        }

        var msg = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATSTATS {
            DuelID = SigilId,
            PartID = circle.ParticipantObject.m_globalID,
            StatsData = buffer,
        };

        DuelBroadcast(msg);
    });

    private void SendCombatHand() => EnactActionOnSubCircles(SendHandTo);

    // Draws the circle's hand for the round and sends it to that participant. Creatures are skipped.
    private void SendHandTo(CombatDuelSubCircle circle) {
        {
            if (circle.IsAiCreature) {
                return;
            }

            var newHand = circle.DrawHand();

            if (!_serializer.Serialize(newHand, _combatParticipantHandFlags, out var buffer)) {
                Logger.Error("Failed to serialize combat hand for duel {0}",
                    Logger.Args(SigilId));

                return;
            }

            var participantActor = circle.ParticipantActor;
            var msg = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATHAND {
                DeckCount = (byte) circle.AvailableSpells,
                TotalDeckCount = (ushort) circle.TotalSpells,
                TreasureCardCount = (ushort) circle.VaultRemainingCount,
                ParticipantID = circle.ParticipantObject.m_globalID,
                HandData = buffer,
            };

            participantActor.Tell(msg);
        }
    }

    // Each player's hand as the game has it, taken before the round's actions are worked out.
    private Dictionary<CombatDuelSubCircle, List<Spell>> SnapshotHands() {
        var hands = new Dictionary<CombatDuelSubCircle, List<Spell>>();
        try {
            foreach (var circle in SubCircles) {
                if (circle is null || circle.IsAiCreature || circle.ParticipantActor is null) {
                    continue;
                }

                hands[circle] = [.. circle.GetCurrentHand().m_spellList ?? []];
            }
        }
        catch (Exception ex) {
            Logger.Error("Duel {0} | Could not note the hands before the round: {1}", Logger.Args(SigilId, ex));
        }

        return hands;
    }

    /// <summary>
    /// The hand the game held this round plus extra cards (pet May Casts, Daybreaker/.c1/.c2 free casts), sent to the
    /// game only; the server's hand stays as it is. It goes out before the round's actions, and the game casts only
    /// cards it finds in the caster's hand, so it must still hold the cards played this round: the server has already
    /// discarded them by now. Sending the hand after the round (the cards gone) made the game skip the player's own
    /// card whenever a pet or a free cast went off in the same round ("Participant (sc 4) does not spell in hand",
    /// Niduus's log 2026-10-22: Aberrant - Shadow Global and Meteor Strike with Pet - Sleet Storm, Headless Horseman,
    /// Tower Shield with a Daybreaker). Cards the round added to the hand are kept too.
    /// </summary>
    private void SendHandWith(CombatDuelSubCircle circle, List<Spell> extra, List<Spell> handBeforeRound = null) {
        if (circle is null || circle.IsAiCreature || circle.ParticipantActor is null) {
            return;
        }

        var current = circle.GetCurrentHand().m_spellList ?? [];
        List<Spell> cards = [.. handBeforeRound ?? current];
        if (handBeforeRound is not null) {
            cards.AddRange(current.Where(c => !handBeforeRound.Any(b => ReferenceEquals(b, c))));
            Logger.Debug("Duel {0} | Slot {1} | Hand for the round's free casts: {2} cards as at the start of the round "
                         + "({3} played this round kept), {4} added by the round, {5} free cast cards.",
                Logger.Args(SigilId, circle.SlotIndex, handBeforeRound.Count,
                    handBeforeRound.Count(b => !current.Any(c => ReferenceEquals(b, c))),
                    cards.Count - handBeforeRound.Count, extra.Count));
        }

        var hand = new Hand { m_spellList = [.. cards, .. extra] };
        if (!_serializer.Serialize(hand, _combatParticipantHandFlags, out var buffer)) {
            Logger.Error("Failed to serialize pet cast hand for duel {0}", Logger.Args(SigilId));

            return;
        }

        circle.ParticipantActor.Tell(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATHAND {
            DeckCount = (byte) circle.AvailableSpells,
            TotalDeckCount = (ushort) circle.TotalSpells,
            TreasureCardCount = (ushort) circle.VaultRemainingCount,
            ParticipantID = circle.ParticipantObject.m_globalID,
            HandData = buffer,
        });
    }

    internal void SendCurrentCombatHand(CombatDuelSubCircle circle) {
        // As-is, no draw or refill, so a discarded slot stays visibly open for the vault draw.
        if (circle is null || circle.IsAiCreature) {
            return;
        }

        var hand = circle.GetCurrentHand();
        if (!_serializer.Serialize(hand, _combatParticipantHandFlags, out var buffer)) {
            Logger.Error("Failed to serialize current combat hand for duel {0}", Logger.Args(SigilId));

            return;
        }

        var msg = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATHAND {
            DeckCount = (byte) circle.AvailableSpells,
            TotalDeckCount = (ushort) circle.TotalSpells,
            TreasureCardCount = (ushort) circle.VaultRemainingCount,
            ParticipantID = circle.ParticipantObject.m_globalID,
            HandData = buffer,
        };

        circle.ParticipantActor.Tell(msg);
    }

    internal void SendCombatPips() {
        var pips = new CombatPipListObj {
            m_pipList = new List<ParticipantPipData>(),
            m_duelID = SigilId
        };

        EnactActionOnSubCircles(circle => {
            if (!circle.AddedToDuel || !circle.IsAlive) {
                return;
            }

            var current = circle.CombatParticipant.m_pipCount;
            var participantPipData = new ParticipantPipData {
                // PipAquiredByEnum: 2 for a power pip this round, else 1 (a pip, or none with every slot full).
                m_acq = circle.PipAcquired == 2 ? 2u : 1u,
                m_partID = (GID) circle.ParticipantObject.m_globalID,
                m_arch = circle.ArchmasterySchool,
                m_archPoints = circle.ArchmasteryPoints,
                m_pips = new PipCount() {
                    m_genericPips = current.m_genericPips,
                    m_powerPips = current.m_powerPips,
                    m_balancePips = current.m_balancePips,
                    m_deathPips = current.m_deathPips,
                    m_firePips = current.m_firePips,
                    m_icePips = current.m_icePips,
                    m_lifePips = current.m_lifePips,
                    m_mythPips = current.m_mythPips,
                    m_stormPips = current.m_stormPips,
                    m_shadowPips = current.m_shadowPips,
                }
            };
            pips.m_pipList.Add(participantPipData);
        });

        // Serialize the combat pips and send it to each participant.
        if (!_serializer.Serialize(pips, _combatParticipantStatFlags, out var buffer)) {
            Logger.Error("Failed to serialize combat pips for duel {0}",
                Logger.Args(SigilId));

            return;
        }

        ZoneBroadcast(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATPIPS {
            DuelID = SigilId,
            PipData = buffer,
        });
    }

    private void SendCombatHealth() {
        var healthList = new CombatHealthListObj {
            m_healthList = new List<ParticipantParameter>(),
            m_duelID = SigilId
        };

        // Iterate through each sub circle and add the participant's health to the list. A defeated one is sent too
        // (0), so no game keeps showing the health it had before it fell.
        EnactActionOnSubCircles(circle => {
            if (!circle.AddedToDuel || circle.ParticipantGameStats is null || circle.ParticipantObject is null) {
                return;
            }

            circle.SyncParticipantHealth();
            var participantHealth = new ParticipantParameter {
                m_data = (uint) Math.Max(0, circle.ParticipantGameStats.m_currentHitpoints),
                m_partID = (GID) circle.ParticipantObject.m_globalID,
            };
            healthList.m_healthList.Add(participantHealth);
        });

        // Serialize the combat health and send it to each participant.
        if (!_serializer.Serialize(healthList, _combatParticipantStatFlags, out var healthBuffer)) {
            Logger.Error("Failed to serialize combat health for duel {0}",
                Logger.Args(SigilId));

            return;
        }

        var msg = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATHEALTH {
            DuelID = SigilId,
            HealthData = healthBuffer,
        };
        ZoneBroadcast(msg);
    }

    // One line a round with every slot's pips and the server's health, to compare with what the game shows.
    private void LogRoundPipsAndHealth() {
        var slots = new List<string>();
        EnactActionOnSubCircles(circle => {
            if (circle.CombatParticipant?.m_pipCount is not { } pips || circle.ParticipantGameStats is not { } stats) {
                return;
            }

            var school = CombatDuelSubCircle.TotalPips(pips) - pips.m_genericPips - pips.m_powerPips;
            slots.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{circle.SlotIndex} {circle.ParticipantObject?.m_debugName}: pips {pips.m_genericPips}+{pips.m_powerPips}P"
                + $"+{school}S+{pips.m_shadowPips}Sh (acq {circle.PipAcquired}), health {stats.m_currentHitpoints}/"
                + $"{circle.CombatParticipant.m_maxPlayerHealth}{(circle.AddedToDuel ? "" : " (not in yet)")}"));
        });

        Logger.Debug("Duel {0} | Round {1} | {2}", Logger.Args(Duel.m_duelID.Full, Duel.m_roundNum, string.Join("; ", slots)));
    }

    private void SendCombatMoveSelection(CombatTeam team, ulong participantId, byte moveType, Spell spell, uint targetIndex,
        uint? targetBits = null) {
        byte isItemCard = (byte) (spell?.m_itemCard ?? false ? 1 : 0);
        byte isTreasureCard = (byte) (spell?.m_treasureCard ?? false ? 1 : 0);
        byte isBattleCard = (byte) (spell?.m_battleCard ?? false ? 1 : 0);

        // One bit per targeted circle (bit n = circle n), the way the client sends it; a player's own pick is echoed
        // as the client sent it (targetBits).
        var actualIndex = targetBits ?? (targetIndex < 32 ? 1u << (int) targetIndex : targetIndex);

        var msg = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMOVESELECTION {
            DuelID = SigilId,
            ParticipantID = participantId,
            MoveType = moveType,
            SpellID = (int) (spell?.m_templateID ?? 0),
            SpellTargetIndex = actualIndex,
            EnchantmentID = (int) (spell?.m_enchantment ?? 0),
            IsItemCard = isItemCard,
            IsTreasureCard = isTreasureCard,
            IsBattleCard = isBattleCard,
            // 0 would name circle 0 as a shadow pact target: the client then drew the shadow icon next to every
            // picked card instead of the target circle's symbol (Niduus, 2026-10-25).
            ShadowPactTarget = -1,
        };
        // The chosen card is for the side of the caster only; the other side sees it when it is cast.
        TeamBroadcast(team, msg);
    }

    // Determines if a card is an enchantment instead of a playable combat cast.
    private static bool IsEnchantment(Spell spell) {
        if (spell is null) {
            return false;
        }

        var template = CoreObjectFactory.GetCoreTemplate(spell.m_templateID) as SpellTemplate;
        if (template is null) {
            return false;
        }

        if (string.Equals(template.m_sTypeName, "Enchantment", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        if (template.m_effects != null) {
            foreach (var effect in template.m_effects) {
                if (effect.m_effectTarget is kEffectTarget.kSpell or kEffectTarget.kSpecificSpells) {
                    return true;
                }

                if (effect.m_effectType is kSpellEffects.kModifyCardDamage 
                    or kSpellEffects.kModifyCardHeal 
                    or kSpellEffects.kModifyCardAccuracy 
                    or kSpellEffects.kModifyCardMutation 
                    or kSpellEffects.kModifyCardArmorPiercing 
                    or kSpellEffects.kModifyCardCloak
                    or kSpellEffects.kModifyCardOutgoingDamage
                    or kSpellEffects.kModifyCardIncomingDamage
                    or kSpellEffects.kProtectCardBeneficial
                    or kSpellEffects.kProtectCardHarmful) {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsValidEnchantTarget(Spell enchantSpell, Spell targetSpell) {
        if (targetSpell is null || enchantSpell is null) {
            return false;
        }

        // Cannot enchant an already enchanted card
        if (targetSpell.m_enchantment != 0) {
            return false;
        }

        // Cannot enchant an enchantment card
        if (IsEnchantment(targetSpell)) {
            return false;
        }

        var targetTemplate = CoreObjectFactory.GetCoreTemplate(targetSpell.m_templateID) as SpellTemplate;
        if (targetTemplate is null) {
            return false;
        }

        // Enchantments cannot be applied to Treasure Cards
        if (targetSpell.m_treasureCard || targetTemplate.m_Treasure) {
            return false;
        }

        var enchantTemplate = CoreObjectFactory.GetCoreTemplate(enchantSpell.m_templateID) as SpellTemplate;
        if (enchantTemplate?.m_effects is null) {
            return true;
        }

        foreach (var effect in enchantTemplate.m_effects) {
            // Damage enchants  can only be applied to spells that deal damage (including drains like Vampire)
            if (effect.m_effectType is kSpellEffects.kModifyCardDamage or kSpellEffects.kModifyCardArmorPiercing
                or kSpellEffects.kCollectEssence) {
                if (!HasDamageEffect(targetTemplate)) {
                    return false;
                }
            }

            // Healing enchants can only be applied to healing spells
            if (effect.m_effectType is kSpellEffects.kModifyCardHeal) {
                if (!HasHealingEffect(targetTemplate)) {
                    return false;
                }
            }

            // Accuracy enchants can only be applied to spells that are not 100% accuracy by default
            if (effect.m_effectType is kSpellEffects.kModifyCardAccuracy) {
                if (targetTemplate.m_accuracy >= 100) {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool HasDamageEffect(SpellTemplate template) =>
        template.m_effects != null && template.m_effects.Any(IsDamageEffect);

    private static bool IsDamageEffect(SpellEffect effect) {
        if (effect is null) {
            return false;
        }

        if (effect is RandomSpellEffect rse && rse.m_effectList != null) {
            return rse.m_effectList.Any(IsDamageEffect);
        }

        if (effect is VariableSpellEffect vse && vse.m_effectList != null) {
            return vse.m_effectList.Any(IsDamageEffect);
        }

        if (effect is EffectListSpellEffect elsee && elsee.m_effectList != null) {
            return elsee.m_effectList.Any(IsDamageEffect);
        }

        return effect.m_effectType is kSpellEffects.kDamage
            or kSpellEffects.kDamageOverTime
            or kSpellEffects.kDamageNoCrit
            or kSpellEffects.kDamagePerTotalPipPower
            or kSpellEffects.kStealHealth;
    }

    private static bool HasHealingEffect(SpellTemplate template) =>
        template.m_effects != null && template.m_effects.Any(IsHealingEffect);

    private static bool IsHealingEffect(SpellEffect effect) {
        if (effect is null) {
            return false;
        }

        if (effect is RandomSpellEffect rse && rse.m_effectList != null) {
            return rse.m_effectList.Any(IsHealingEffect);
        }

        if (effect is VariableSpellEffect vse && vse.m_effectList != null) {
            return vse.m_effectList.Any(IsHealingEffect);
        }

        if (effect is EffectListSpellEffect elsee && elsee.m_effectList != null) {
            return elsee.m_effectList.Any(IsHealingEffect);
        }

        return effect.m_effectType is kSpellEffects.kHeal
            or kSpellEffects.kHealOverTime
            or kSpellEffects.kHealByWard
            or kSpellEffects.kHealPercent
            or kSpellEffects.kMaxHealthHeal
            or kSpellEffects.kSetHealPercent;
    }

    private static bool TryFindEnchantTarget(CombatDuelSubCircle caster, Spell enchantSpell, int enchantSlot, uint spellTarget, uint rawTarget, out Spell targetSpell) {
        targetSpell = null;
        var hand = caster.GetCurrentHand()?.m_spellList;
        if (hand is null || hand.Count == 0) {
            return false;
        }

        int bitmaskSlot = (rawTarget > 0 && (rawTarget & (rawTarget - 1)) == 0) ? (int) Math.Log2(rawTarget) : -1;
        int[] candidateSlots = [(int) rawTarget, (int) spellTarget, bitmaskSlot];

        foreach (var slot in candidateSlots) {
            if (slot < 0 || slot >= hand.Count || slot == enchantSlot) {
                continue;
            }

            var card = hand[slot];
            if (card != null && IsValidEnchantTarget(enchantSpell, card)) {
                targetSpell = card;
                return true;
            }
        }

        return false;
    }

    private void HandleEnchantMove(CombatDuelSubCircle caster, Spell enchantSpell, Spell targetSpell) {
        if (caster is null || enchantSpell is null || targetSpell is null) {
            return;
        }

        Logger.Debug("Duel {0} | Slot {1} | Enchanting spell {2} with enchantment {3}",
            Logger.Args(Duel.m_duelID.Full, caster.SlotIndex, targetSpell.m_templateID, enchantSpell.m_templateID));

        // Mark enchantment properties on targetSpell
        targetSpell.m_enchantment = enchantSpell.m_templateID;
        targetSpell.m_enchantedThisCombat = true;
        targetSpell.m_enchantmentSpellIsItemCard = enchantSpell.m_itemCard;

        var enchantTemplate = CoreObjectFactory.GetCoreTemplate(enchantSpell.m_templateID) as SpellTemplate;
        if (enchantTemplate?.m_effects != null) {
            foreach (var effect in enchantTemplate.m_effects) {
                switch (effect.m_effectType) {
                    case kSpellEffects.kModifyCardDamage:
                    case kSpellEffects.kModifyCardHeal:
                    case kSpellEffects.kModifyCardOutgoingDamage:
                    case kSpellEffects.kModifyCardIncomingDamage:
                        targetSpell.m_regularAdjust += effect.m_effectParam;
                        break;
                    case kSpellEffects.kModifyCardAccuracy:
                        targetSpell.m_accuracy = (byte) Math.Max(0, targetSpell.m_accuracy + effect.m_effectParam);
                        break;
                    case kSpellEffects.kModifyCardCloak:
                        targetSpell.m_cloaked = true;
                        break;
                    case kSpellEffects.kModifyCardMutation:
                        targetSpell.m_premutationSpellID = targetSpell.m_templateID;
                        targetSpell.m_templateID = (uint) effect.m_effectParam;
                        break;
                    case kSpellEffects.kProtectCardBeneficial:
                    case kSpellEffects.kProtectCardHarmful:
                        targetSpell.m_delayEnchantment = true;
                        break;
                    default:
                        break;
                }
            }
        }

        // Consume enchantment card from hand and vault or deck
        if (enchantSpell.m_treasureCard) {
            var consumedId = caster.ConsumeFromVault(enchantSpell);
            if (consumedId != 0 && caster._wizard != null) {
                caster._wizard.SpellbookBehavior?.RemoveTreasureCard(consumedId);
                WizardCollection.RemoveTreasureCard(caster._wizard, consumedId);

                var deckSlot = caster._wizard.EquipmentBehavior?.SlotList?
                    .FirstOrDefault(s => s.SlotType == EquipmentSlotType.Deck);
                if (deckSlot?.ItemId != null) {
                    caster._wizard.RemoveSpellFromDeck(consumedId, deckSlot.ItemId.Value);
                    if (enchantTemplate != null) {
                        var spellHash = StringHash.Compute(enchantTemplate.m_name);
                        caster.ParticipantActor?.Tell(
                            new WIZARD_12_PROTOCOL.MSG_REMOVETREASURESPELLFROMDECK {
                                SpellID = (int) spellHash,
                                EnchantmentID = 0,
                                DeckID = deckSlot.ItemId.Value,
                                Success = 1,
                                Destroy = 0
                            }, ActorRefs.NoSender);
                    }
                }
            }
        }
        else {
            caster.ConsumeCard(enchantSpell);
        }

        SendCurrentCombatHand(caster);

        Logger.Debug("Duel {0} | Slot {1} | Enchantment applied successfully. Target spell {2} now has regularAdjust {3}, accuracy {4}.",
            Logger.Args(Duel.m_duelID.Full, caster.SlotIndex, targetSpell.m_templateID, targetSpell.m_regularAdjust, targetSpell.m_accuracy));
    }

    private void HandleDiscardMove(CombatDuelSubCircle caster, int spellSelection) {
        var spell = caster.GetSpellFromLastHand((byte) spellSelection);
        if (spell is null) {
            Logger.Warning("Duel {0} | Slot {1} | Discard received for an empty hand slot ({2}).",
                Logger.Args(Duel.m_duelID.Full, caster.SlotIndex, spellSelection));

            return;
        }

        caster.DiscardCard(spell);

        Logger.Debug("Duel {0} | Slot {1} | Discarded a card: {2}",
            Logger.Args(Duel.m_duelID.Full, caster.SlotIndex, spell.m_templateID.ToString() ?? "None"));

        // Re-send as-is so the freed slot stays open; SendCombatHand would refill it from the deck.
        SendCurrentCombatHand(caster);
    }

    private void HandlePassMove(CombatDuelSubCircle caster) {
        // If the participant passes, we don't need to know what spell they were casting.
        CombatResolver.AddCombatMove(CombatMoveType.Pass, caster, null, null);

        if (!caster.IsAiCreature) {
            SendCombatMoveSelection(caster.OccupiedTeam, caster.ParticipantObject.m_globalID, (byte) CombatMoveType.Pass, null, 0);
        }
    }

    // The circles named by a target sent as bits, in circle order; empty when it is not a set of bits (no target is all ones).
    private List<CombatDuelSubCircle> PickedCircles(uint raw) {
        var circles = new List<CombatDuelSubCircle>();
        if (raw == 0 || raw == uint.MaxValue) {
            return circles;
        }

        for (var i = 0; i < SubCircles.Length && i < 32; i++) {
            if ((raw & (1u << i)) == 0) {
                continue;
            }

            if (SubCircles[i] is not { } circle) {
                return [];
            }

            circles.Add(circle);
        }

        return (raw >> Math.Min(SubCircles.Length, 31)) == 0 ? circles : [];
    }

    private void HandleAttackMove(CombatDuelSubCircle caster, int spellSelection, uint spellTarget, uint rawSpellTarget) {
        var spell = caster.GetSpellFromLastHand((byte) spellSelection);
        if (spell is null) {
            Logger.Warning("Duel {0} | Slot {1} | Attack received for an empty hand slot ({2}).",
                Logger.Args(Duel.m_duelID.Full, caster.SlotIndex, spellSelection));

            CombatResolver.AddCombatMove(CombatMoveType.Pass, caster, null, null);

            return;
        }

        // An enchantment is played onto another card in the hand, not cast.
        if (CoreObjectFactory.GetCoreTemplate(spell.m_templateID) is SpellTemplate template
                && CombatEnchantments.IsEnchantment(template)) {
            TryEnchantMove(caster, spellSelection, spellTarget, rawSpellTarget);

            return;
        }

        if (!caster.HasPipsForSpell(spell)) {
            Logger.Warning("Duel {0} | Slot {1} | Participant does not have enough pips or mana for spell {2}",
                Logger.Args(Duel.m_duelID.Full, caster.SlotIndex, spell.m_templateID));

            CombatResolver.AddCombatMove(CombatMoveType.Pass, caster, null, null);

            return;
        }

        // Find the target sub circle. Valid targets are 1-8.
        // If the spell doesn't have a target like for AoE spells or self-heals,
        // the value will be the integer cap.
        var target = caster;
        // A pick-your-enemies card sends one bit per enemy picked (bit n = circle n), several bits for several
        // enemies; one bit is the usual single target. Several bits used to be read as one circle number, so picking
        // circles 1 and 2 (6) aimed the card at circle 6.
        var picked = CoreObjectFactory.GetCoreTemplate(spell.m_templateID) is SpellTemplate cardTemplate && DivideCards.IsPickCard(cardTemplate)
            ? PickedCircles(rawSpellTarget) : [];
        if (picked.Count > 1) {
            target = picked[0];
            spellTarget = (uint) target.SlotIndex;
            Logger.Information("Duel {0} | Slot {1} | Picked {2} enemies (sent as {3}): {4}.",
                Logger.Args(Duel.m_duelID.Full, caster.SlotIndex, picked.Count, rawSpellTarget,
                    string.Join(", ", picked.Select(p => p.SlotIndex))));
        }
        else if (spellTarget < SubCircles.Length) {
            // The client sends raw sigil slots for spell targets (see GetUpFirstSigilSlot).
            target = SubCircles[spellTarget];
        }

        CombatResolver.AddCombatMove(CombatMoveType.Attack, caster, target, spell,
            picked.Count > 1 ? picked : target != caster ? [target] : null);

        // Minions are AI-driven; their telegraph comes from ResendMinionMoveSelections, not an echo.
        if (!caster.IsSummonedMinion && !caster.IsAiCreature) {
            SendCombatMoveSelection(caster.OccupiedTeam, caster.ParticipantObject.m_globalID, (byte) CombatMoveType.Attack, spell, spellTarget,
                rawSpellTarget);
        }
    }

    /// <summary>
    /// Plays an enchantment card onto another card in the caster's hand. The target card may come as a bit
    /// (1 << index, like spell targets) or as a plain index; whichever names another card is used. The turn
    /// is not used up: the player picks a card to cast afterwards.
    /// </summary>
    private bool TryEnchantMove(CombatDuelSubCircle caster, int spellSelection, uint spellTarget, uint rawSpellTarget) {
        var enchantment = caster.GetSpellFromLastHand((byte) spellSelection);
        if (enchantment is null
            || CoreObjectFactory.GetCoreTemplate(enchantment.m_templateID) is not SpellTemplate enchantTemplate
            || !CombatEnchantments.IsEnchantment(enchantTemplate)) {
            return false;
        }

        // The target card comes as a plain index: reading it as a bit first (0.9.24 to 0.9.71) put Niduus's
        // Daybreaker for Tower Shield (raw 2) on Sun Serpent (index 1). The bit reading is only a fallback.
        var isBit = rawSpellTarget != 0 && (rawSpellTarget & (rawSpellTarget - 1)) == 0;
        var candidates = isBit ? new[] { rawSpellTarget, spellTarget } : new[] { rawSpellTarget };

        Spell target = null;
        foreach (var index in candidates) {
            if (index != (uint) spellSelection && index < byte.MaxValue
                && caster.GetSpellFromLastHand((byte) index) is { } card && card != enchantment) {
                target = card;
                break;
            }
        }

        Logger.Information("Duel {0} | Slot {1} | Enchant: card {2} ({3}) onto target {4} (sent as {5}): {6}",
            Logger.Args(Duel.m_duelID.Full, caster.SlotIndex, spellSelection, enchantTemplate.m_name.ToString(),
                        spellTarget, rawSpellTarget, target is null ? "no such card" : target.m_templateID.ToString()));

        bool enchanted;
        try {
            enchanted = target is not null && CombatEnchantments.Enchant(target, enchantment);
        }
        catch (Exception ex) {
            Logger.Error("Duel {0} | Slot {1} | {2} failed while enchanting: {3}",
                Logger.Args(Duel.m_duelID.Full, caster.SlotIndex, enchantTemplate.m_name.ToString(), ex));
            enchanted = false;
        }

        if (!enchanted) {
            try {
                var targetTemplate = target is null ? null : CoreObjectFactory.GetCoreTemplate(target.m_templateID) as SpellTemplate;
                Logger.Information("Duel {0} | Slot {1} | {2} did not enchant {3}. Enchantment effects: {4}. Card effects: {5}.",
                    Logger.Args(Duel.m_duelID.Full, caster.SlotIndex, enchantTemplate.m_name.ToString(),
                                targetTemplate?.m_name.ToString() ?? "no card",
                                CombatEnchantments.Describe(enchantTemplate.m_effects),
                                CombatEnchantments.Describe(targetTemplate?.m_effects)));
            }
            catch (Exception ex) {
                Logger.Error("Duel {0} | Slot {1} | could not describe the failed enchantment: {2}",
                    Logger.Args(Duel.m_duelID.Full, caster.SlotIndex, ex));
            }
            SendCurrentCombatHand(caster);

            return true;
        }

        Logger.Information("Duel {0} | Slot {1} | Enchanted with {2}: pip cost now {3} (adjust {4}), accuracy {5}.",
            Logger.Args(Duel.m_duelID.Full, caster.SlotIndex,
                        string.Join(", ", (enchantTemplate.m_effects ?? []).Select(e => $"{e.m_effectType} {e.m_effectParam}")),
                        target.m_pipCost?.m_spellRank, target.m_regularAdjust, target.m_accuracy));

        Imlight.CoreLib.Game.Combat.CombatResolver.UseUpCard(caster, enchantment);
        SendCurrentCombatHand(caster);

        return true;
    }

    private void HandleChangeMindAction(CombatDuelSubCircle caster) {
        // Send the action director a null spell to indicate that the participant has changed their mind.
        CombatResolver.AddCombatMove(CombatMoveType.ChangeMind, caster, null, null);

        // Echo the change mind action to each player participant
        if (!caster.IsAiCreature) {
            SendCombatMoveSelection(caster.OccupiedTeam, caster.ParticipantObject.m_globalID, (byte) CombatMoveType.ChangeMind, null, 0);
        }
    }

    // drainMana: only the Flee button costs mana. Logging out, a dropped connection or leaving the zone took it all as
    // well (Niduus, 2026-10-22: after logging out of a battle and back in to rejoin it, his mana was gone).
    private void HandleFleeAction(CombatDuelSubCircle caster, bool drainMana = true) {
        if (!_isActive) {
            return;
        }

        if (IsPvPDuel) {
            ForfeitPvPDuel(caster);

            return;
        }

        var actor = caster.ParticipantActor;
        var participantObjId = caster.ParticipantObject.m_globalID;

        // A wizard who logs out or drops keeps their pips for when they come back into this battle (Niduus asked: a
        // relog mid-battle must not hand out fresh pips). Health already lives on the wizard and carries over.
        if (!drainMana && caster._wizard is not null && caster.CombatParticipant?.m_pipCount is { } leftPips) {
            _pipsOfLeavers[caster._wizard.CharId] = leftPips;
        }

        // Fleeing drains the player's mana; creature participants have no wizard.
        if (drainMana && caster._wizard is not null) {
            var clientMaxMana = caster._wizard.GameStats.GetClientTypeAlternative().m_baseMana;
            caster._wizard.UpdateMana(0);
            actor.Tell(new WIZARD_12_PROTOCOL.MSG_UPDATEMANA {
                Mana = 0,
                MaxMana = clientMaxMana,
            });
        }

        // A boss's PlayerFlees cheat answers it at the start of the next round.
        if (drainMana) {
            CombatResolver?.NotePlayerFled(caster);
        }

        // Inform the client that they've been removed from this duel.
        var defeatMsg = NewDefeat(diedInDuel: false);
        actor.Tell(defeatMsg);

        ForgetParticipant(participantObjId);
        caster.RemoveParticipant();

        Logger.Debug("Duel {0} | Slot {1} | Participant fled",
            Logger.Args(Duel.m_duelID.Full, caster.SlotIndex));

        ZoneBroadcast(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATREMOVE {
            DuelID = SigilId,
            ParticipantID = participantObjId,
        });

        // If no more players are left in the duel because of this, end the duel.
        if (AliveAndInDuelPlayerCount <= 0) {
            EndDuel();
        }
    }

    /// <summary>
    /// The defeat for a player leaving this duel by fleeing or losing. A copy of the zone with a spiraldb-extra/ZoneObjects
    /// exit (an added sigil's private room, such as Malistaire's from the Ravenwood sigil) sends them to that exit, the
    /// same place its doors go, not to the world's hub (which for G14_DM is Castle Darkmoor).
    /// </summary>
    private COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT NewDefeat(bool diedInDuel) {
        var defeat = new COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT { DiedInDuel = diedInDuel };
        var zone = Entity.Zone;
        if (string.IsNullOrEmpty(zone?.ZonePath)
            || Imlight.CoreLib.Game.World.WorldHubZones.GetHubForZone(zone.ZonePath) is not { } hub) {
            return defeat;
        }

        // Going to the hub is a teleport out of the zone like any other, so the exit takes it when it takes those.
        if (zone.Extras.TryGetExit(null, hub.m_hubZone, out var exitZone, out var exitLoc)) {
            defeat.ExitZone = exitZone;
            defeat.ExitLocation = exitLoc;
        }

        return defeat;
    }

    private static CombatTeam DetermineFirstTeam()
        => (CombatTeam) new Random().Next(0, 2);

    internal bool IsScriptedDuel()
        => _tutorialDirector.IsActive;

    private byte GetUpFirstSigilSlot() {
        // Prefer the acting team's first living participant, else any living participant.
        var upFirst = SubCircles.FirstOrDefault(s => s is not null && s.AddedToDuel && s.IsAlive
            && s.SlotType == (Duel.m_firstTeamToAct == (int) CombatTeam.Player
                ? CombatSlotType.Player
                : CombatSlotType.Creature))
            ?? SubCircles.FirstOrDefault(s => s is not null && s.AddedToDuel && s.IsAlive);
        if (upFirst is null) {
            return 0;
        }

        // The modern client resolves the indicator against its sigil slots, not a list index.
        return (byte) upFirst.SlotIndex;
    }

    private void AddWaitingCombatParticipants() => EnactActionOnSubCircles(AddCombatParticipant);

    private void AddCombatParticipant(CombatDuelSubCircle circle) {
        if (circle.AddedToDuel || !circle.Occupied) {
            return;
        }

        // Serialize the combat participant and send it to every client in the zone.
        circle.SyncParticipantHealth();
        var participant = circle.CombatParticipant;
        if (!_serializer.Serialize(participant, _combatParticipantFlags, out var buffer)) {
            Logger.Error("Failed to serialize combat participant for duel {0}",
                Logger.Args(SigilId));

            return;
        }

        var msg = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATADD {
            DuelID = SigilId,
            ParticipantData = buffer,
        };
        ZoneBroadcast(msg);

        Logger.Debug("Duel {0} | Slot {1} | Serialized participant sent",
            Logger.Args(Duel.m_duelID.Full, circle.SlotIndex));

        circle.AddedToDuel = true;
        ForgetParticipant(participant.m_ownerID);
        Duel.m_flatParticipantList.Add(participant);
    }

    // [Battle] LateJoinersJoinImmediately (Niduus, 2026-10-22: "joining late should immediately put that wizard in"):
    // a wizard who steps in after the round began plays this round, not the next one.
    private static readonly bool s_lateJoinersJoinImmediately
        = ConfigurationManager.GetValue("Battle.LateJoinersJoinImmediately", true);
    // The least planning time a wizard who joins mid-planning gets; the countdown is made longer for everyone if less is left.
    private const double LATE_JOIN_MIN_PLANNING_SECONDS = 15;

    private void JoinLateWizardNow(CombatDuelSubCircle circle) {
        if (!s_lateJoinersJoinImmediately || !_isActive || IsPvPDuel || _tutorialDirector.IsActive || circle is null
            || circle.AddedToDuel || !circle.IsHumanPlayer || Duel.m_roundNum == 0) {
            return;
        }

        // Before planning (the round's opening): planning, which starts in a moment, deals the wizard in with everyone.
        if (Duel.m_duelPhase == kDuelPhase.kPhase_PrePlanning) {
            AddCombatParticipant(circle);
            Logger.Debug("Duel {0} | Slot {1} | Late joiner is in for this round.", Logger.Args(Duel.m_duelID.Full, circle.SlotIndex));

            return;
        }

        // Mid-execution the spells are already playing: the wizard comes in with the next round, as before.
        if (Duel.m_duelPhase != kDuelPhase.kPhase_Planning) {
            return;
        }

        AddCombatParticipant(circle);

        // The planning phase as everyone else got it, for this wizard only.
        var actor = circle.ParticipantActor;
        var phase = NewCombatPhase((byte) kDuelPhase.kPhase_Planning);
        if (phase is not null) {
            actor.Tell(phase);
        }

        SendCombatStats();
        SendHandTo(circle);
        SendCombatPips();
        SendCombatHealth();

        // The time left, or more when little is left (then for everyone).
        var left = PlanningSeconds - (DateTime.UtcNow - _planningStartedAt).TotalSeconds;
        if (left < LATE_JOIN_MIN_PLANNING_SECONDS) {
            left = LATE_JOIN_MIN_PLANNING_SECONDS;
            _planningStartedAt = DateTime.UtcNow - TimeSpan.FromSeconds(PlanningSeconds - left);
            HumanBroadcast(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_SETPLANNINGPHASETIMER {
                DuelID = SigilId,
                Time = (byte) Math.Ceiling(left),
            });
        }

        actor.Tell(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_SHOWCOMBATUI { DuelID = SigilId, AltTurn = 0, AltTurnTeam = 0 });
        actor.Tell(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_SETPLANNINGPHASETIMER { DuelID = SigilId, Time = (byte) Math.Ceiling(left) });

        // The new wizard has not moved yet, so the round waits for them (a 1 s end timer for the others is replaced).
        _awaitingCombatMoves = true;
        Timers.StartSingleTimer(PLANNING_TIME_KEY, new COMBAT_106_PROTOCOL.MSG_PLANNINGPHASEOVER(), TimeSpan.FromSeconds(left));

        Logger.Debug("Duel {0} | Slot {1} | Late joiner plays this round ({2:0} s of planning left).",
            Logger.Args(Duel.m_duelID.Full, circle.SlotIndex, left));
    }

    private bool IsAlternatingPvP => IsPvPDuel && _pvp.Rules.AlternateTurns;

    private void DoPipGain(CombatTeam? onlyTeam = null) {
        // Archmastery fills fastest for whoever has the most of it in the battle.
        var highestArchmastery = 0f;
        EnactActionOnSubCircles(circle => {
            if (circle.AddedToDuel && circle.IsAlive) {
                highestArchmastery = Math.Max(highestArchmastery, circle.ArchmasteryRating);
            }
        });

        EnactActionOnSubCircles(circle => {
            if (!circle.AddedToDuel || !circle.IsAlive) {
                return;
            }

            if (onlyTeam is { } team && circle.OccupiedTeam != team) {
                return;
            }

            circle.DoPipGain(highestArchmastery);
        });
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_CHEATINSTAWIN))]
    private void ReceiveCheatInstaWin(COMBAT_106_PROTOCOL.MSG_CHEATINSTAWIN message) {
        // The cheat kills the creatures; the red side of a PvP duel is made of wizards.
        if (!_isActive || IsPvPDuel) {
            return;
        }

        // EndDuel derives the winner from the alive creature count, so zero them first.
        Timers.CancelAll();
        EnactActionOnSubCircles(circle => {
            if (circle.OccupiedTeam == CombatTeam.Monster) {
                circle.DamageParticipant(int.MaxValue);
            }
        });

        EndDuel();
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_CHEATDRAWCARDS))]
    private void ReceiveCheatDrawCards(COMBAT_106_PROTOCOL.MSG_CHEATDRAWCARDS message) {
        var subCircle = SubCircles.FirstOrDefault(x => x is not null && x.ParticipantActor == message.Actor);
        if (!_isActive || subCircle is null || !subCircle.IsAlive) {
            return;
        }

        void Inform(string text) => message.Actor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = text });

        // Cards only go in while cards are being picked; mid-round the client would not show them right.
        if (Duel.m_duelPhase != kDuelPhase.kPhase_Planning) {
            Inform("Wait until it is your turn to pick a card, then try .draw again.");

            return;
        }

        if (subCircle.HandRoom <= 0) {
            Inform("Your hand is full. Discard a card first, then try .draw again.");

            return;
        }

        var added = subCircle.DrawExtraCards(message.SpellTemplateId, message.Amount);
        if (added == 0) {
            Inform($"Could not make a {message.SpellName} card.");

            return;
        }

        SendCurrentCombatHand(subCircle);
        Inform(added < message.Amount
            ? $"Drew {added} {message.SpellName} (your hand had room for {added})."
            : $"Drew {added} {message.SpellName}.");
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_CHEATDELAYCAST))]
    private void ReceiveCheatDelayCast(COMBAT_106_PROTOCOL.MSG_CHEATDELAYCAST message) {
        var subCircle = SubCircles.FirstOrDefault(x => x is not null && x.ParticipantActor == message.Actor);
        if (!_isActive || subCircle is null || !subCircle.IsAlive) {
            return;
        }

        void Inform(string text) => message.Actor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = text });
        // .cf / .cl (Niduus, 2026-10-22) are the f and l queues; .c1 / .c2 the ones around the caster's card.
        var name = (message.When switch { 'f' => "cf", 'l' => "cl", _ => message.AtStart ? "c1" : "c2" }) + (message.SkipCast ? "s" : "");
        if (Duel.m_duelPhase != kDuelPhase.kPhase_Planning) {
            Inform($"Wait until it is your turn to pick a card, then try .{name} again.");

            return;
        }

        if (message.Announcement is not null) {
            CombatResolver.AddDelayedAnnouncement(subCircle, message.Announcement, message.AtStart, message.When);
            Inform($".{name} ca: \"{message.AnnouncementShown ?? message.Announcement}\" shows before the next spell in that queue.");

            return;
        }

        // pN / eN on an empty circle or a defeated participant (Niduus, 2026-10-22): say who stands where instead of
        // casting it on someone else. A repeated spell sends one message per copy, so it is said once.
        if (TargetProblem(subCircle, message) is { } problem) {
            if (_lastTargetProblem != (message.Actor, (long) Duel.m_roundNum, problem)) {
                _lastTargetProblem = (message.Actor, (long) Duel.m_roundNum, problem);
                Inform(problem);
            }

            return;
        }

        try {
            var count = CombatResolver.AddFreeDelayedCast(subCircle, message.SpellTemplateId, message.AtStart, out var enchantment,
                                                          message.Target, message.SkipCast, message.When, message.Enchant);
            var when = message.When switch {
                'f' => "before anyone's turn",
                'l' => "after everyone's turn",
                _ => $"{(message.AtStart ? "before" : "after")} your card",
            };
            var on = message.Target switch {
                null => "",
                "all" => " on every target",
                var code => $" on {code}",
            };
            Inform(count == 0
                ? $"Could not cast {message.SpellName}."
                : $".{name}: {message.SpellName}{(enchantment is null ? message.Enchant ? " (no enchantment fits it)" : "" : $" with {enchantment}")}{on} casts {when} this round{(message.SkipCast ? ", without the casting animation" : "")} ({count} queued).");
        }
        catch (Exception ex) {
            Logger.Error("Duel {0} | .{1} failed: {2}", Logger.Args(SigilId, name, ex));
        }
    }

    // The last target problem told to a caster, so a repeated spell (.c1 e4 Fire Cat 3) says it once.
    private (IActorRef Actor, long Round, string Problem) _lastTargetProblem;

    /// <summary>
    /// Why a .c1/.c2/.cf/.cl pN or eN target can't be hit (an empty circle, or someone defeated that the spell does
    /// not revive), with who stands on each circle of that side; null when it can.
    /// </summary>
    private string TargetProblem(CombatDuelSubCircle caster, COMBAT_106_PROTOCOL.MSG_CHEATDELAYCAST message) {
        if (Game.Combat.CombatResolver.FreeTargetCircle(caster, message.Target) is not { } circle) {
            return null;
        }

        var revive = circle.SlotType == caster.SlotType
                     && CoreObjectFactory.GetCoreTemplate(message.SpellTemplateId) is SpellTemplate template
                     && Game.Combat.CombatResolver.IsRevive(template);
        if (circle.Occupied && circle.AddedToDuel && (circle.IsAlive || revive)) {
            return null;
        }

        var letter = message.Target[0];
        var side = SubCircles.Where(c => c is not null && (c.SlotType == caster.SlotType) == (letter == 'p'))
            .OrderBy(c => c.SlotIndex).Take(4).ToList();
        var roster = string.Join(", ", side.Select((c, i) => $"{letter}{i + 1} is " + (!c.Occupied ? "empty"
            : (c == caster ? "you" : CircleName(c)) + (c.IsAlive ? "" : " (defeated)"))));

        return $"{message.Target} is {(circle.Occupied ? "defeated" : "an empty circle")}, so {message.SpellName} was not queued. {roster}. Use me for yourself.";
    }

    // Who stands on a circle, for messages.
    private static string CircleName(CombatDuelSubCircle circle)
        => circle._wizard?.PlayerNameBehavior?.GetWizardName() is { Length: > 0 } wizard ? wizard
            : circle.ParticipantObject?.m_debugName is { Length: > 0 } debugName ? debugName : "someone";

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_CHEATANNOUNCE))]
    private void ReceiveCheatAnnounce(COMBAT_106_PROTOCOL.MSG_CHEATANNOUNCE message) {
        var subCircle = SubCircles.FirstOrDefault(x => x is not null && x.ParticipantActor == message.Actor);
        if (!_isActive || subCircle is null) {
            return;
        }

        CombatResolver.AddAnnouncement(message.Text, subCircle.SlotIndex);
        message.Actor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE {
            Message = $".ca: \"{message.Shown ?? message.Text}\" shows before the next round's first spell.",
        });
    }

    // .wegofirst / .theygofirst (Niduus, 2026-10-22): which side acts first, from this round's spells on.
    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_CHEATTURNORDER))]
    private void ReceiveCheatTurnOrder(COMBAT_106_PROTOCOL.MSG_CHEATTURNORDER message) {
        var subCircle = SubCircles.FirstOrDefault(x => x is not null && x.ParticipantActor == message.Actor);
        void Inform(string text) => message.Actor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = text });
        if (!_isActive || subCircle is null || IsPvPDuel) {
            Inform("Use .wegofirst or .theygofirst in a battle against creatures.");

            return;
        }

        CombatResolver.SetFirstSide(subCircle, message.WeFirst);
        Inform(message.WeFirst ? "Your side goes first, from this round's spells on." : "The other side goes first, from this round's spells on.");
    }

    /// <summary>Shows the sun on the side that now acts first.</summary>
    internal void ShowFirstSide() {
        SendUpFirst(Duel.m_roundNum);
        Logger.Information("Duel {0} | Up first sent again: team {1}, phase {2}.", Logger.Args(SigilId, Duel.m_firstTeamToAct, Duel.m_duelPhase));
    }

    // .joinme (Niduus, 2026-10-20): a creature joins this battle on the enemy side and fights from the next round.
    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_CHEATJOINME))]
    private void ReceiveCheatJoinMe(COMBAT_106_PROTOCOL.MSG_CHEATJOINME message) {
        var subCircle = SubCircles.FirstOrDefault(x => x is not null && x.ParticipantActor == message.Actor);
        void Inform(string text) => message.Actor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = text });
        if (!_isActive || subCircle is null || IsPvPDuel) {
            Inform("Use .joinme in a battle against creatures.");

            return;
        }

        if (GetAvailableSubCircleTeamCreature() is null) {
            Inform("The enemy side is full (4 enemies).");

            return;
        }

        try {
            var slot = SpawnAndAssignMinion(message.CreatureTemplateId, subCircle, forEnemies: true, customId: message.CustomId);
            Inform(slot is null
                ? $"{message.CreatureName} could not join the battle."
                : JoinLateCreatureNow(slot)
                    ? $"{message.CreatureName} joins the battle and fights this round."
                    : $"{message.CreatureName} joins the battle and fights from the next round.");
        }
        catch (Exception ex) {
            Logger.Error("Duel {0} | .joinme failed: {1}", Logger.Args(SigilId, ex));
        }
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_CHEATGIVEPIPS))]
    private void ReceiveCheatGivePips(COMBAT_106_PROTOCOL.MSG_CHEATGIVEPIPS message) {
        var subCircle = SubCircles.FirstOrDefault(x => x is not null && x.ParticipantActor == message.Actor);
        if (!_isActive || subCircle is null || !subCircle.IsAlive) {
            return;
        }

        try {
            var result = subCircle.GivePips(message.Kind, message.Amount);
            // The stats first: they tell the game how many shadow slots to draw.
            SendCombatStats();
            SendCombatPips();
            message.Actor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = result });
        }
        catch (Exception ex) {
            Logger.Error("Duel {0} | .pip give failed: {1}", Logger.Args(SigilId, ex));
        }
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_SETARCHMASTERYSCHOOL))]
    private void ReceiveSetArchmasterySchool(COMBAT_106_PROTOCOL.MSG_SETARCHMASTERYSCHOOL message) {
        var subCircle = SubCircles.FirstOrDefault(x => x is not null && x.ParticipantActor == message.Actor);
        if (!_isActive || subCircle is null) {
            return;
        }

        try {
            // Clicking the Power Orb: the next school pip comes in the new school, the orb keeps its power.
            subCircle.SetArchmasterySchool(message.School);
            SendCombatPips();
        }
        catch (Exception ex) {
            Logger.Error("Duel {0} | Archmastery school change failed: {1}", Logger.Args(SigilId, ex));
        }
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_CHEATMASTERY))]
    private void ReceiveCheatMastery(COMBAT_106_PROTOCOL.MSG_CHEATMASTERY message) {
        var subCircle = SubCircles.FirstOrDefault(x => x is not null && x.ParticipantActor == message.Actor);
        if (!_isActive || subCircle is null) {
            return;
        }

        foreach (var school in message.Schools ?? []) {
            subCircle.GrantMastery((MagicSchool) school);
        }
        SendCombatStats();

        var names = string.Join(", ", (message.Schools ?? []).Select(school => ((MagicSchool) school).ToString()));
        message.Actor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE {
            Message = $"You have {names} school mastery (not Archmastery) for the rest of this battle: power pips count as 2 for those spells.",
        });
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_CHEATINSTANTCINEMATICS))]
    private void ReceiveCheatInstantCinematics(COMBAT_106_PROTOCOL.MSG_CHEATINSTANTCINEMATICS message)
        => CheatInstantCinematics = message.Enabled;

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_CHEATNOFIZZLE))]
    private void ReceiveCheatNoFizzle(COMBAT_106_PROTOCOL.MSG_CHEATNOFIZZLE message) {
        var subCircle = SubCircles.FirstOrDefault(x => x.ParticipantActor == message.Actor);
        if (subCircle is null) {
            return;
        }

        subCircle.CheatNoFizzle = message.Enabled;
    }

    private void EndDuel() {
        if (IsPvPDuel) {
            EndPvPDuel();

            return;
        }

        // The duel has ended. Inform the clients of the result.
        var playersWin = AliveAndInDuelCreatureCount <= 0;
        var creaturesWin = AliveAndInDuelPlayerCount <= 0;

        // A queued tutorial card grant must not leak into the next duel on this sigil.
        _tutorialDirector.OnDuelEnded();

        CancelHealthGlobes();
        RemovePlayersFromDuel(playersWin);

        if (playersWin) {
            PlayerWin();
        }
        else if (creaturesWin) {
            CreatureWin();
        }

        // Broadcast to the zone of the result.
        var combatMatchResult = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMATCHRESULT {
            DuelID = SigilId,
            WinningTeam = playersWin ? (byte) CombatTeam.Player : (byte) CombatTeam.Monster,
        };
        ZoneBroadcast(combatMatchResult);

        // A won battle keeps its victory scene a moment, as the real game does: in a packet capture (Niduus,
        // 2026-10-22) the game asks for the Victory Dance right after the victory phase and the battle ends about
        // 0.8 s later. Ending at once (phase 7 straight after 6) meant the game never asked and put the wizard
        // straight back on their mount.
        _victoryWinners.Clear();
        if (playersWin && s_victorySceneSeconds > 0) {
            EnactActionOnSubCircles(circle => {
                if (circle.IsHumanPlayer && circle.IsAlive && circle.ParticipantActor is not null && circle.ParticipantObject is not null) {
                    _victoryWinners.Add((circle.ParticipantActor, circle.ParticipantObject.m_globalID));
                }
            });
        }

        // DespawnDuel deletes the summoned minions by their circles, which are emptied below; it gets them from here.
        _victoryMinions.Clear();
        EnactActionOnSubCircles(circle => {
            if (circle.IsSummonedMinion && circle.ParticipantActor is not null) {
                _victoryMinions.Add(circle.ParticipantActor);
            }
        });

        _isActive = false;

        // Nobody is in this duel any more; a later disconnect or logout must not find a circle.
        EnactActionOnSubCircles(circle => circle.RemoveParticipant());

        if (_victoryWinners.Count > 0) {
            _victorySceneBattle = ++_battle;
            Timers.StartSingleTimer(VICTORY_SCENE_KEY, new ZONE_102_PROTOCOL.MSG_VICTORYSCENEOVER { Battle = _victorySceneBattle },
                TimeSpan.FromSeconds(s_victorySceneSeconds));

            return;
        }

        FinishEndDuel();
    }

    // [Battle] VictorySceneDelay: how long a won battle's victory scene lasts before the battle ends for the game
    // (0: at once). Off by default: in 0.10.150 (as VictorySceneSeconds = 0.8) Niduus was stuck in place after a won
    // battle (2026-10-22), so it is a test switch until that is understood.
    private static readonly double s_victorySceneSeconds
        = Math.Clamp(ConfigurationManager.GetValue("Battle.VictorySceneDelay", 0.0), 0, 5);

    private const string VICTORY_SCENE_KEY = "VictoryScene";
    private const uint VICTORY_STATE = 523160964;     // the state the real server puts a winner in during the scene
    private const uint NORMAL_STATE = 1674067506;     // StringHash("Normal"), sent just before the battle ends

    private readonly List<(IActorRef Actor, ulong ObjectId)> _victoryWinners = [];
    private readonly List<IActorRef> _victoryMinions = [];
    private int _victorySceneBattle = -1;

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_VICTORYSCENEOVER))]
    private void ReceiveVictorySceneOver(ZONE_102_PROTOCOL.MSG_VICTORYSCENEOVER message) {
        if (message.Battle != _victorySceneBattle) {
            return;
        }

        FinishEndDuel();
    }

    /// <summary>Tells the game the battle is over (phase 7, ENDDUEL) and takes the sigil down.</summary>
    private void FinishEndDuel() {
        _pipsOfLeavers.Clear();
        _victorySceneBattle = -1;
        Timers.Cancel(VICTORY_SCENE_KEY);
        foreach (var (actor, objectId) in _victoryWinners) {
            actor.Tell(new GAME_5_PROTOCOL.MSG_ENTERSTATE { GameObjectID = objectId, State = NORMAL_STATE });
        }

        _victoryWinners.Clear();
        foreach (var minion in _victoryMinions) {
            minion.Tell(new COMBAT_106_PROTOCOL.MSG_COMBATDEATH());
        }

        _victoryMinions.Clear();

        // Inform the zone of the final phase, and the end of the duel.
        Duel.m_duelPhase = kDuelPhase.kPhase_Ended;
        SendCombatPhase((byte) Duel.m_duelPhase);

        var duelEndedMsg = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_ENDDUEL { DuelID = SigilId };
        ZoneBroadcast(duelEndedMsg);

        DespawnDuel();
        _isActive = false;
    }

    private bool IsPvPSideWiped()
        => AliveHumanCount(CombatTeam.Player) <= 0 || AliveHumanCount(CombatTeam.Monster) <= 0;

    private int GetPvPWinningTeam() {
        // The side with a wizard still standing wins; with none standing on either side it is a draw (-1).
        var blueStands = AliveHumanCount(CombatTeam.Player) > 0;
        var redStands = AliveHumanCount(CombatTeam.Monster) > 0;

        return blueStands == redStands ? -1 : blueStands ? (int) CombatTeam.Player : (int) CombatTeam.Monster;
    }

    private void ForfeitPvPDuel(CombatDuelSubCircle caster) {
        var participantObjId = caster.ParticipantObject.m_globalID;
        var session = caster.ParticipantActor;
        var matchWasDecided = _pvp.IsEnding;
        if (caster._wizard is not null && !matchWasDecided) {
            _pvp.MarkForfeited(caster._wizard.CharId);
        }

        // A forfeit costs the wizard nothing outside of the duel: no mana is drained and nobody is sent to a hub.
        // A duel circle's wizard stays in the zone, so the game is told it left the battle, as after fleeing;
        // otherwise it stayed in a battle that no longer had it (Niduus, 2026-10-23).
        if (_isCircleDuel && session is not null) {
            if (caster._wizard is not null) {
                RestoreCircleHealth(caster._wizard.CharId);
            }

            session.Tell(new COMBAT_106_PROTOCOL.MSG_COMBATDEFEAT { DiedInDuel = false });
            session.Tell(new GAME_5_PROTOCOL.MSG_ENTERSTATE { GameObjectID = participantObjId, State = (uint) NPCStates.Idle });
        }

        ForgetParticipant(participantObjId);
        caster.RemoveParticipant();

        Logger.Debug("Duel {0} | Slot {1} | Participant left{2}",
            Logger.Args(Duel.m_duelID.Full, caster.SlotIndex, matchWasDecided ? " after the match was decided" : " and forfeits"));

        ZoneBroadcast(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATREMOVE {
            DuelID = SigilId,
            ParticipantID = participantObjId,
        });

        if (matchWasDecided) {
            // Somebody who leaves during the victory scene is not waited for.
            if (_pvp.StopWaitingFor(session)) {
                FinishPvPDuel();
            }

            return;
        }

        if (IsPvPSideWiped()) {
            EndDuel();
        }
    }

    private void EndPvPDuel() {
        var winningTeam = GetPvPWinningTeam();

        // Two forfeits, or a forfeit and a knockout, can both reach for the end; the match ends once.
        var waitingFor = SubCircles.Where(circle => circle.Occupied && circle.IsHumanPlayer && !_pvp.IsDisconnected(circle.ParticipantActor))
            .Select(circle => circle.ParticipantActor)
            .ToList();
        if (!_pvp.BeginEnding(winningTeam, waitingFor)) {
            return;
        }

        Logger.Debug("Duel {0} | PvP match ended. Winning team {1}.", Logger.Args(Duel.m_duelID.Full, winningTeam));

        Timers.CancelAll();
        if (_isCircleDuel) {
            CloseCircleDuel(winningTeam);

            return;
        }

        // The wizards can move again. Nobody gains experience or loot, or is sent to a hub.
        EnactActionOnSubCircles(circle => {
            if (circle.IsHumanPlayer) {
                circle.ParticipantActor.Tell(new GAME_5_PROTOCOL.MSG_ENTERSTATE {
                    GameObjectID = circle.ParticipantObject.m_globalID,
                    State = (uint) NPCStates.Idle
                });
            }
        });

        ZoneBroadcast(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMATCHRESULT {
            DuelID = SigilId,
            WinningTeam = winningTeam,
        });
        HumanBroadcast(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_ALLOWLEAVEPVP());

        Duel.m_duelPhase = kDuelPhase.kPhase_Victory;
        SendCombatPhase((byte) Duel.m_duelPhase);

        // Each client reports when its victory scene is over; a draw plays none and reports nothing, so the wait is
        // bounded by the grace period either way.
        var grace = TimeSpan.FromSeconds(PVP_RETURN_GRACE_SECONDS);
        if (grace > TimeSpan.Zero && _pvp.HasSessionsToWaitFor) {
            Timers.StartSingleTimer(PVP_RETURN_GRACE_KEY, new COMBAT_106_PROTOCOL.MSG_PVPENDGRACEOVER(), grace);

            return;
        }

        FinishPvPDuel();
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_PVPVICTORYSEEN))]
    private void ReceivePvPVictorySeen(COMBAT_106_PROTOCOL.MSG_PVPVICTORYSEEN message) {
        if (IsPvPDuel && _isActive && _pvp.IsEnding && _pvp.StopWaitingFor(message.Actor)) {
            FinishPvPDuel();
        }
    }

    [MessageHandler(typeof(COMBAT_106_PROTOCOL.MSG_PVPENDGRACEOVER))]
    private void ReceivePvPEndGraceOver(COMBAT_106_PROTOCOL.MSG_PVPENDGRACEOVER message) {
        if (IsPvPDuel && _isActive) {
            FinishPvPDuel();
        }
    }

    private void FinishPvPDuel() {
        if (!_pvp.TryFinish()) {
            return;
        }

        Timers.Cancel(PVP_RETURN_GRACE_KEY);

        Duel.m_duelPhase = kDuelPhase.kPhase_Ended;
        SendCombatPhase((byte) Duel.m_duelPhase);

        ZoneBroadcast(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_ENDDUEL { DuelID = SigilId });

        ReportPvPDuelEnded(_pvp.WinningTeam);

        DespawnDuel();
        _isActive = false;
        _isCircleDuel = false;
        EnactActionOnSubCircles(circle => circle.RemoveParticipant());
    }

    private void ReportPvPDuelEnded(int winningTeam) {
        // Each player's session lets go of the duel; the director sends everyone back and closes the match.
        foreach (var entry in _pvp.Roster) {
            if (_pvp.IsDisconnected(entry.Session)) {
                continue;
            }

            entry.Session.Tell(new COMBAT_106_PROTOCOL.MSG_PVPDUELENDED {
                MatchId = _pvp.MatchId,
                WinningTeam = winningTeam,
                Forfeited = _pvp.HasForfeited(entry.CharId)
            });
        }

        PvPDirector.Instance?.Tell(new PVP_111_PROTOCOL.MSG_MATCHENDED {
            MatchId = _pvp.MatchId,
            WinningTeam = winningTeam,
            ForfeitedCharIds = _pvp.ForfeitedCharIds
        });
    }

    private void PlayerWin() {
        Logger.Debug("Duel {0} | Duel ended. Players win.", Logger.Args(Duel.m_duelID.Full));

        Duel.m_duelPhase = kDuelPhase.kPhase_Victory;
        SendCombatPhase((byte) Duel.m_duelPhase);

        var adjectivesOfDefeatedMobs = new List<string>();
        var adjectivesPerDefeatedMob = new List<List<string>>();
        var templateIdsOfDefeatedMobs = new List<ulong>();
        // What each one drops by: its custom's key (Customs.cs) or its template id.
        var lootKeysOfDefeatedMobs = new List<ulong>();
        // A defeated minion on the enemy side (a .joinme boss, a boss's second form) may have left its circle already.
        var defeatedMobs = new List<(ulong TemplateId, ulong GlobalId)>();
        EnactActionOnSubCircles(circle => {
            if (circle.OccupiedTeam != CombatTeam.Monster) {
                return;
            }

            if (circle.IsSummonedMinion) {
                NoteDefeatedEnemyMinion(circle);
            }
            else {
                defeatedMobs.Add((circle.ParticipantObject.m_templateID, circle.ParticipantObject.m_globalID));
            }
        });
        defeatedMobs.AddRange(_defeatedEnemyMinions);
        _defeatedEnemyMinions.Clear();
        foreach (var (mobTemplateId, mobGlobalId) in defeatedMobs) {
            var mobTemplate = CoreObjectFactory.GetCoreTemplate(mobTemplateId);
            if (mobTemplate is null) {
                continue;
            }

            if (mobTemplate is not GameObjectTemplate gameObjectTemplate) {
                continue;
            }

            var mobAdjectives = gameObjectTemplate.m_adjectiveList.Select(a => (string) a).ToList();

            // A creature the randomizer swapped in counts for quests as the creature it replaced, too.
            if (Imlight.CoreLib.Game.Randomization.Randomizer.TryTakeReplacedCreature(mobGlobalId, out var replacedTemplateId)
                && CoreObjectFactory.GetCoreTemplate(replacedTemplateId) is GameObjectTemplate replacedTemplate) {
                mobAdjectives = [.. mobAdjectives.Union(replacedTemplate.m_adjectiveList.Select(a => (string) a))];
            }

            adjectivesOfDefeatedMobs.AddRange(mobAdjectives);
            adjectivesPerDefeatedMob.Add(mobAdjectives);
            templateIdsOfDefeatedMobs.Add(gameObjectTemplate.m_templateID);
            lootKeysOfDefeatedMobs.Add(Customs.Of(mobGlobalId) is var custom and > 0
                ? Customs.Key(custom) : gameObjectTemplate.m_templateID);
            Customs.Forget(mobGlobalId);
        }

        var allyCharIds = new List<ulong>();
        EnactActionOnSubCircles(circle => {
            if (CombatGroupReservations.TryGetCharId(circle, out var charId)) {
                allyCharIds.Add(charId);
            }
        });

        // Send the final messages to the participants.
        var combatVictoryMsg = new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATVICTORY();
        EnactActionOnSubCircles(circle => {
            if (circle.OccupiedTeam == CombatTeam.Monster) {
                return;
            }

            circle.ParticipantActor.Tell(combatVictoryMsg);

            var victoryMsg = new COMBAT_106_PROTOCOL.MSG_COMBATWIN() {
                UsedPips = circle._usedPipsForExperienceGain,
                MobAdjectives = [.. adjectivesOfDefeatedMobs],
                MobTemplateIds = [.. templateIdsOfDefeatedMobs],
                MobLootKeys = [.. lootKeysOfDefeatedMobs],
                AllyCharIds = [.. allyCharIds],
                CountedRounds = _countedRounds,
                Animus = CombatResolver?.TakeAnimus(circle),
            };
            circle.ParticipantActor.Tell(victoryMsg);
        });

        PostMonsterKilledEvents(adjectivesPerDefeatedMob);

        // A won battle (not one everybody fled) leaves health, mana and gold wisps behind.
        BattleDrops.Spawn(Entity.ZoneRef, Entity.ActiveGameObject.m_location);
    }

    private void CountRoundForTamedDrops() {
        // Run once per round, before the queued moves are used up. By default the third round in a row that every wizard
        // chose to pass (none stunned or confused) and every one after it does not count; a round with a spell cast
        // counts again. The Manager's Tamed Drops card sets how many passes count and whether forced ones always do.
        if (IsPvPDuel || _lastRoundCounted == Duel.m_roundNum) {
            return;
        }

        _lastRoundCounted = Duel.m_roundNum;
        try {
            var settings = TamedDropsSettings.Current;
            var wizards = 0;
            var everyoneChosePass = true;
            EnactActionOnSubCircles(circle => {
                if (!circle.IsHumanPlayer || !circle.AddedToDuel || !circle.IsAlive || circle.OccupiedTeam != CombatTeam.Player) {
                    return;
                }

                wizards++;
                var forced = circle.CombatParticipant is { } participant && (participant.m_stunned > 0 || participant.m_confused > 0);
                var passed = CombatResolver.GetQueuedAction(circle)?.Spell is null;
                if (!passed || (forced && settings.ForcedPassesCount)) {
                    everyoneChosePass = false;
                }
            });

            _passStreak = wizards > 0 && everyoneChosePass ? _passStreak + 1 : 0;
            if (!settings.PassesStopCounting || _passStreak <= settings.PassesThatCount) {
                _countedRounds++;
            }
        }
        catch (Exception e) {
            Logger.Error("Duel {0} | Tamed Drops round count failed: {1}", Logger.Args(SigilId, e));
        }
    }

    private void PostMonsterKilledEvents(List<List<string>> adjectivesPerDefeatedMob) {
        // Triggers match the killed monster's adjectives (a boss's ".AdjRef") and act for the first winning player.
        IActorRef winnerActor = null;
        CoreObject winnerObject = null;
        EnactActionOnSubCircles(circle => {
            if (winnerActor is null && circle.OccupiedTeam == CombatTeam.Player && !circle.IsSummonedMinion
                && circle.ParticipantActor is not null) {
                winnerActor = circle.ParticipantActor;
                winnerObject = circle.ParticipantObject;
            }
        });

        if (winnerActor is null) {
            return;
        }

        foreach (var adjectives in adjectivesPerDefeatedMob) {
            Entity.ZoneRef.Tell(new ZONE_102_PROTOCOL.MSG_POSTEVENT {
                EventName = MONSTER_KILLED_EVENT,
                PlayerActor = winnerActor,
                PlayerGameObject = winnerObject,
                Adjectives = adjectives,
            });
        }
    }

    private void CreatureWin() {
        Logger.Debug("Duel {0} | Duel ended. Creatures win.", Logger.Args(Duel.m_duelID.Full));

        // Send combat death to all creatures anyways. This will get rid of their game object. The ones still
        // standing were not defeated, so their spawn points spawn again, even in a private instance.
        var deathMsg = new COMBAT_106_PROTOCOL.MSG_COMBATDEATH();
        EnactActionOnSubCircles(circle => {
            if (circle.OccupiedTeam == CombatTeam.Monster && !circle.IsSummonedMinion && circle.IsAlive
                    && circle.ParticipantObject is { } creature) {
                ZonePath.Spared[creature.m_globalID] = 0;
            }

            circle.ParticipantActor.Tell(deathMsg);
        });

        // The room's minions that joined an encounter and were defeated in it come back too, so a new try after a flee
        // finds them walking the room again rather than spawned twice (ZonePath, MSG_ENCOUNTERBATTLEENDED).
        if (Entity.Zone?.IsInstance == true) {
            foreach (var id in _encounterZoneMinions) {
                ZonePath.Spared[id] = 0;
            }
        }
        _encounterResetIds = [.. _encounterZoneMinions];

        // Inform each player that they've been defeated.
        var defeatMsg = NewDefeat(diedInDuel: false);
        EnactActionOnSubCircles(circle => {
            if (!circle.AddedToDuel) {
                return;
            }

            circle.ParticipantActor.Tell(defeatMsg);
        });
    }

    private void RemovePlayersFromDuel(bool playersWin) => EnactActionOnSubCircles(circle => {
        // Minions have no hub to return to; DespawnDuel tears them down.
        if (!circle.IsHumanPlayer) {
            return;
        }

        // A wizard defeated in a battle their team won stays where they are and gets up with a little health, as on
        // live (Niduus, 2026-10-22: his friend was sent out of the Malistaire room after they won). Only a lost battle
        // or a flee sends a wizard to the hub or the room's exit. Die = Delete keeps its rule: still dead at the end.
        if (!circle.IsAlive && playersWin && !Duel.m_bPVP && !s_dieDelete) {
            ReviveAfterWin(circle);

            return;
        }

        // The globe shows the health the battle left, even if a step's timer had not gone off yet.
        if (circle._wizard is { } wizard && !circle.IsSandboxed) {
            circle.ParticipantActor.Tell(NewHealthGlobe(wizard, wizard.GameStats.m_currentHitpoints));
        }

        // Send any dead players back to the hub. Scripted tutorial duels never count as a death.
        if (!circle.IsAlive) {
            var defeatMsg = NewDefeat(diedInDuel: !_tutorialDirector.IsActive && !Duel.m_bPVP);
            circle.ParticipantActor.Tell(defeatMsg);

            return;
        }

        // Get the players back into the idle state, so they can move around again; after a won battle with a victory
        // scene, the state the real server uses for it (the game's own "Normal" follows when the scene ends).
        var stateMsg = new GAME_5_PROTOCOL.MSG_ENTERSTATE {
            GameObjectID = circle.ParticipantObject.m_globalID,
            State = playersWin && !Duel.m_bPVP && s_victorySceneSeconds > 0 ? VICTORY_STATE : (uint) NPCStates.Idle
        };
        circle.ParticipantActor.Tell(stateMsg);
    });

    private static readonly bool s_dieDelete = ConfigurationManager.GetValue("Die Delete.Enabled", false);
    // The health a wizard defeated in a won battle gets up with.
    private const int REVIVE_AFTER_WIN_HEALTH = 1;

    private void ReviveAfterWin(CombatDuelSubCircle circle) {
        circle.SetHealthTo(REVIVE_AFTER_WIN_HEALTH);
        if (circle._wizard is { } wizard) {
            circle.ParticipantActor.Tell(new WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH {
                CharacterID = wizard.GameObjectID,
                NewHealth = REVIVE_AFTER_WIN_HEALTH,
                NewHealthMax = wizard.GameStats.GetClientTypeAlternative().m_baseHitpoints,
                DisplayDiff = 1,
            });
        }

        circle.ParticipantActor.Tell(new GAME_5_PROTOCOL.MSG_ENTERSTATE {
            GameObjectID = circle.ParticipantObject.m_globalID,
            State = (uint) NPCStates.Idle
        });

        Logger.Debug("Duel {0} | Slot {1} | Defeated in a won battle: stays, back with {2} health.",
            Logger.Args(Duel.m_duelID.Full, circle.SlotIndex, REVIVE_AFTER_WIN_HEALTH));
    }

    private bool IsSlotAvailable(CombatTeam team) {
        var IsNewbieZone = _combatSigilObjectInfo is { m_unknown_uint_4: not 0 };
        var IsDangerousZone = false;

        // Newbie sigils (marked in the sigil info) can only have 1 creature as a base.
        // Dangerous zones (like Sunken City) can have 3 creatures as a base.
        // Every player in the duel also allocates 1 more creature slot.
        var baseCreatureCount = IsNewbieZone ? 1 : IsDangerousZone ? 3 : 2;
        var maxCreatures = baseCreatureCount + (PlayerCount - 1);

        var slotAvailable = (team == CombatTeam.Player)
            ? PlayerCount < 4
            : CreatureCount < 4 && (CreatureCount < maxCreatures);

        return slotAvailable;
    }

    private void ResendMinionMoveSelections() {
        EnactActionOnSubCircles(circle => {
            if (!circle.IsSummonedMinion || !circle.IsAlive || circle.ParticipantObject is null) {
                return;
            }

            if (IsPvPDuel && _pvp.ActingTeam is { } acting && circle.OccupiedTeam != acting) {
                return;
            }

            var action = CombatResolver.GetQueuedAction(circle);
            if (action is null || action.Spell is null || action.SelectedTarget is null) {
                SendCombatMoveSelection(circle.OccupiedTeam, circle.ParticipantObject.m_globalID, (byte) CombatMoveType.Pass, null, 0);

                return;
            }

            SendCombatMoveSelection(circle.OccupiedTeam, circle.ParticipantObject.m_globalID, (byte) CombatMoveType.Attack,
                action.Spell, (uint) action.SelectedTarget.SlotIndex);
        });
        var delay = TimeSpan.FromSeconds(PlanningSeconds);
        Timers.StartSingleTimer(PLANNING_TIME_KEY, new COMBAT_106_PROTOCOL.MSG_PLANNINGPHASEOVER(), delay);
    }

}
