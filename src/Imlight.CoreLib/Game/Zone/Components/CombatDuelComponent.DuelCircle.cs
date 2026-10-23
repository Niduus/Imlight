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
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.Math;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.PvP;
using Imlight.CoreLib.Game.PvP.Arena;
using Imlight.CoreLib.Game.PvP.Data;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// Duel circles (Niduus, 2026-10-23: "we need duel circles enabled in houses"): a house's own PvP circle, and a PvP
/// battle circle (PvPSigil8Actor2Sides) added in the Customs tab. A wizard who steps on goes straight into the battle,
/// as with a monster ("enter it like they would a battle"): the first on team A (blue), the next on team B (red), and
/// so on. The first one starts a [PvP] DuelCircleWait (15 s) countdown; the rounds begin when it ends or the circle is
/// full, or the duel is called off when one side is still empty. The duel takes turns (the PvP way) or lets both
/// sides act at once: the Customs tab's Combat for an added circle, [PvP] HouseDuelTakesTurns for a house's own. It
/// ends like a battle, not a PvP arena match: everyone stays where they are, with their health back as before. A
/// wizard who fought on the circle steps off before it counts them again.
/// </summary>
internal sealed partial class CombatDuelComponent {

    private const string DUEL_CIRCLE_TIMER_KEY = "DuelCircle";
    private const string DUEL_CIRCLE_SHOW_KEY = "DuelCircleShow";
    private static readonly TimeSpan s_showAgainDelay = TimeSpan.FromMilliseconds(500);
    private const string TOGETHER = "Together";
    // How far past the outermost place a wizard still counts as standing on the circle.
    private const float DUEL_CIRCLE_MARGIN = 60f;
    private const int MAX_PER_SIDE = 4;

    private sealed record DuelCircleWizard(IActorRef Session, string Name, CombatTeam Side);

    // Wizards who stepped on and wait for the director to put them in the duel, by character id.
    private readonly Dictionary<ulong, DuelCircleWizard> _circleJoining = [];
    // The wizards of the last duel here, and the ones already asked for, until they step off.
    private readonly HashSet<IActorRef> _mustStepOff = [];
    // Each seated wizard's health when they stepped on; it is theirs again when they leave the duel.
    private readonly Dictionary<ulong, (Wizard Wizard, IActorRef Session, int Health)> _circleHealth = [];
    private (Vector3 Place, CombatTeam Side)[] _duelCirclePlaces;
    private float _duelCircleReach;
    private int _duelCountdown = -1;
    // The director is making the duel for the first wizards.
    private bool _duelCircleStarting;
    // The duel is made and waits for its countdown: wizards who step on join it.
    private bool _circleWaiting;
    // The running PvP duel started from this duel circle (its wizards stay in the zone when it ends).
    private bool _isCircleDuel;

    private bool IsDuelCircle
        => PvPSettings.DuelCircles && _sigilTemplate is PvPCombatSigilTemplate && _combatSigilObjectInfo is not null
           && (ZoneObjects.CombatSigilOf(_combatSigilObjectInfo) is not null
               || (Entity.Zone?.ZonePath ?? "").StartsWith("Housing", StringComparison.OrdinalIgnoreCase));

    private PvPDuelRules DuelCircleRules {
        get {
            var combat = ZoneObjects.CombatSigilOf(_combatSigilObjectInfo)?.Combat;
            var takeTurns = combat is null || combat.Length == 0
                ? ZoneObjects.CombatSigilOf(_combatSigilObjectInfo) is not null || PvPSettings.HouseDuelTakesTurns
                : !combat.Equals(TOGETHER, StringComparison.OrdinalIgnoreCase);

            return new PvPDuelRules(takeTurns, takeTurns);
        }
    }

    // Called for every move of a wizard in the zone.
    private void DuelCircleMove(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (!IsDuelCircle || playerWizard is null) {
            return;
        }

        if (SideStoodOn(playerObj) is null) {
            _mustStepOff.Remove(playerActor);

            return;
        }

        // Rounds running, or a duel that is not this circle's own: nobody walks in.
        if (_mustStepOff.Contains(playerActor) || playerWizard.IsInDuel || (_isActive && (!_isCircleDuel || !_circleWaiting))) {
            return;
        }

        var charId = playerWizard.CharId;
        if (IsSeatedOnCircle(charId)) {
            return;
        }

        // A reconnect gives the wizard a new session; the old one is gone.
        if (_circleJoining.TryGetValue(charId, out var asked)) {
            if (asked.Session.Equals(playerActor)) {
                return;
            }

            _circleJoining.Remove(charId);
        }

        // Whoever steps on is given a side in turn, wherever they stand (Niduus, 2026-10-23): the first to team A
        // (blue), the next to team B (red), and so on, until the circle is full or the countdown ends.
        var blue = CircleSideCount(CombatTeam.Player);
        var red = CircleSideCount(CombatTeam.Monster);
        if (blue >= MAX_PER_SIDE && red >= MAX_PER_SIDE) {
            return;
        }

        var side = (blue <= red && blue < MAX_PER_SIDE) || red >= MAX_PER_SIDE ? CombatTeam.Player : CombatTeam.Monster;
        var name = playerWizard.PlayerNameBehavior?.GetWizardName() ?? "A wizard";
        _circleJoining[charId] = new DuelCircleWizard(playerActor, name, side);
        _mustStepOff.Add(playerActor);

        if (_isActive) {
            AskToJoinCircleDuel(charId, side);
        }
        else if (!_duelCircleStarting) {
            _duelCircleStarting = true;
            AskForCircleDuel();
        }
    }

    private bool IsSeatedOnCircle(ulong charId)
        => _isActive && SubCircles is not null && SubCircles.Any(c => c is { Occupied: true } && c._wizard?.CharId == charId);

    private int CircleSideCount(CombatTeam side)
        => _circleJoining.Values.Count(w => w.Side == side)
           + (_isActive && SubCircles is not null ? SubCircles.Count(c => c is { Occupied: true, IsHumanPlayer: true } && c.OccupiedTeam == side) : 0);

    public override void OnPlayerLeave(IActorRef playerActor, ulong id)
        => DuelCircleLeave(playerActor);

    private void DuelCircleLeave(IActorRef playerActor) {
        _mustStepOff.Remove(playerActor);
        foreach (var charId in _circleJoining.Where(w => w.Value.Session.Equals(playerActor)).Select(w => w.Key).ToList()) {
            _circleJoining.Remove(charId);
        }
    }

    // The side whose place is nearest, when the wizard stands on the circle.
    private CombatTeam? SideStoodOn(CoreObject playerObj) {
        if (_duelCirclePlaces is null) {
            var circles = CreateDuelActorSubCircles(_sigilTemplate).Where(c => c is not null).ToList();
            _duelCirclePlaces = [.. circles.Select(c => (c.WorldPosition, c.SlotType == CombatSlotType.Creature ? CombatTeam.Monster : CombatTeam.Player))];
            _duelCircleReach = (_sigilTemplate.m_subCircles?.Count > 0 ? _sigilTemplate.m_subCircles.Max(c => c.m_radius) : 0) + DUEL_CIRCLE_MARGIN;
        }

        if (_duelCirclePlaces.Length == 0 || !IsInHorizontalRadius(playerObj, _duelCircleReach)) {
            return null;
        }

        var at = playerObj.m_location;

        return _duelCirclePlaces
            .OrderBy(p => ((p.Place.X - at.X) * (p.Place.X - at.X)) + ((p.Place.Y - at.Y) * (p.Place.Y - at.Y)))
            .First().Side;
    }

    private void ShowDuelCircle() {
        if (IsDuelCircle && _renderComponent is { IsEnabled: false }) {
            _renderComponent.Enable();
        }
    }

    private void AskForCircleDuel() {
        var teams = new List<List<ulong>> {
            _circleJoining.Where(w => w.Value.Side == CombatTeam.Player).Select(w => w.Key).ToList(),
            _circleJoining.Where(w => w.Value.Side == CombatTeam.Monster).Select(w => w.Key).ToList(),
        };
        if (PvPDirector.Instance is null) {
            DuelCircleFailed("PvP is not running");

            return;
        }

        PvPDirector.Instance.Tell(new PVP_111_PROTOCOL.MSG_STARTLOCALMATCH { Circle = Self, Teams = teams, Rules = DuelCircleRules });
    }

    private void AskToJoinCircleDuel(ulong charId, CombatTeam side)
        => PvPDirector.Instance?.Tell(new PVP_111_PROTOCOL.MSG_JOINLOCALMATCH {
            Circle = Self, MatchId = _pvp.MatchId, CharId = charId, Side = (int) side,
        });

    // The director could not make the duel.
    [MessageHandler(typeof(PVP_111_PROTOCOL.MSG_MATCHCANCELLED))]
    private void ReceiveCircleDuelCancelled(PVP_111_PROTOCOL.MSG_MATCHCANCELLED message) {
        if (_duelCircleStarting && !_isActive) {
            DuelCircleFailed("someone is in a match, a queue or not in the world");
        }
    }

    private void DuelCircleFailed(string why) {
        foreach (var wizard in _circleJoining.Values) {
            wizard.Session.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = $"The duel could not start: {why}.", Modal = 0 });
        }

        Logger.Information("Duel circle {0}: the duel could not start: {1}.", Logger.Args(SigilId, why));
        _circleJoining.Clear();
        _duelCircleStarting = false;
    }

    // The director made the duel and its first wizards sit on their places: show them the battle and start the countdown.
    private void DuelCircleBegin(IReadOnlyList<PvPRosterEntry> roster) {
        _duelCircleStarting = false;
        _isCircleDuel = true;
        _circleWaiting = true;
        _circleHealth.Clear();

        foreach (var entry in roster) {
            _circleJoining.Remove(entry.CharId);
            ShowCircleJoiner(entry);
        }

        // Wizards who stepped on while the duel was being made join it now.
        foreach (var (charId, wizard) in _circleJoining.ToList()) {
            AskToJoinCircleDuel(charId, wizard.Side);
        }

        _duelCountdown = PvPSettings.DuelCircleWait;
        TellCircleDuel($"The duel starts in {_duelCountdown} seconds. More wizards can step on to join.");
        ShowCircleCountdown();
        Timers.StartPeriodicTimer(DUEL_CIRCLE_TIMER_KEY, new ZONE_102_PROTOCOL.MSG_DUELCIRCLETICK(), TimeSpan.FromSeconds(1));
        StartCircleRoundsIfFull();
    }

    // The wait shows as the countdown in the middle of the screen that a dungeon sigil shows (Niduus, 2026-10-23: "the
    // same count down in the center that are on Sigils"). The battle's planning timer sent here showed nothing.
    private void ShowCircleCountdown() {
        if (_duelCountdown <= 0) {
            return;
        }

        ShowToCircleAndZone(new WIZARD_12_PROTOCOL.MSG_MINIGAMETIMERSTART {
            Time = _duelCountdown,
            SigilGID = SigilId,
            Teleport = 0,
        });
    }

    private void EndCircleCountdown()
        => ShowToCircleAndZone(new WIZARD_12_PROTOCOL.MSG_MINIGAMETIMEREND { SigilGID = SigilId });

    // Everyone in the zone sees the countdown, not only the wizards on the circle (Niduus, 2026-10-23). The wizards on
    // it get theirs directly: a copy through the zone could reach them after the battle began, and in v0.10.191 that
    // kept the battle from ever showing.
    private void ShowToCircleAndZone(IMessage message) {
        var onCircle = SubCircles.Where(c => c is { Occupied: true, IsAiCreature: false } && c.ParticipantActor is not null)
            .Select(c => c.ParticipantActor).ToArray();
        foreach (var actor in onCircle) {
            actor.Tell(message);
        }

        Entity.ZoneRef.Tell(new ZONE_102_PROTOCOL.MSG_ZONEBROADCAST {
            Sender = Self,
            Message = message,
            Except = onCircle,
            Targets = ZoneBroadcastTarget.Players,
        });
    }

    [MessageHandler(typeof(PVP_111_PROTOCOL.MSG_LOCALMATCHJOINED))]
    private void ReceiveLocalMatchJoined(PVP_111_PROTOCOL.MSG_LOCALMATCHJOINED message) {
        _circleJoining.Remove(message.CharId, out var asked);
        if (message.Entry is null || !_isActive || !IsPvPDuel || _pvp.MatchId != message.MatchId) {
            if (message.Entry is null && asked is not null) {
                asked.Session.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = "You cannot join this duel now.", Modal = 0 });
            }

            return;
        }

        // Too late: the rounds began, or the side filled up.
        if (!_circleWaiting || GetAvailableSubCircle((CombatTeam) message.Entry.TeamIndex) is null) {
            message.Entry.Session.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = "This duel is full or has begun.", Modal = 0 });

            return;
        }

        SeatPvPEntry(message.Entry, moveThere: false);
        _pvp.AddToRoster(message.Entry);
        ShowCircleJoiner(message.Entry);
        ShowCircleCountdown();
        StartCircleRoundsIfFull();
    }

    // The wizard sits on their place: the game shows them the battle at once, as with a monster.
    private void ShowCircleJoiner(PvPRosterEntry entry) {
        var circle = SubCircles.FirstOrDefault(c => c is { Occupied: true } && c.ParticipantActor?.Equals(entry.Session) == true);
        if (circle is null) {
            return;
        }

        if (circle._wizard is { } wizard) {
            _circleHealth[entry.CharId] = (wizard, entry.Session, wizard.GameStats.m_currentHitpoints);
        }

        AddCombatParticipant(circle);
        var name = circle._wizard?.PlayerNameBehavior?.GetWizardName() ?? "A wizard";
        TellCircleDuel($"{name} joins team {(circle.OccupiedTeam == CombatTeam.Player ? "A (blue)" : "B (red)")}.");
    }

    private void StartCircleRoundsIfFull() {
        if (_circleWaiting && CircleSideCount(CombatTeam.Player) >= MAX_PER_SIDE && CircleSideCount(CombatTeam.Monster) >= MAX_PER_SIDE
            && _circleJoining.Count == 0) {
            TellCircleDuel("The circle is full.");
            StartCircleRounds();
        }
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_DUELCIRCLETICK))]
    private void ReceiveDuelCircleTick(ZONE_102_PROTOCOL.MSG_DUELCIRCLETICK message) {
        if (message.Show) {
            if (!_isActive) {
                ShowDuelCircle();
            }

            return;
        }

        if (_duelCountdown < 0 || !_isActive || !_circleWaiting) {
            Timers.Cancel(DUEL_CIRCLE_TIMER_KEY);

            return;
        }

        _duelCountdown--;
        if (_duelCountdown <= 0) {
            StartCircleRounds();
        }
    }

    private void StartCircleRounds() {
        Timers.Cancel(DUEL_CIRCLE_TIMER_KEY);
        _duelCountdown = -1;
        _circleWaiting = false;
        EndCircleCountdown();

        var blue = SubCircles.Count(c => c is { Occupied: true, IsHumanPlayer: true } && c.OccupiedTeam == CombatTeam.Player);
        var red = SubCircles.Count(c => c is { Occupied: true, IsHumanPlayer: true } && c.OccupiedTeam == CombatTeam.Monster);
        if (blue == 0 || red == 0) {
            TellCircleDuel("Nobody stepped on for the other team, so the duel is off.");
            if (_pvp.BeginEnding(-1, [])) {
                CloseCircleDuel(-1);
            }

            return;
        }

        var style = DuelCircleRules.AlternateTurns ? "taking turns" : "both sides at once";
        TellCircleDuel($"Duel: {blue} against {red}, {style}. Begin!");
        Timers.StartSingleTimer(GRACE_TIME_KEY, new COMBAT_106_PROTOCOL.MSG_NEWROUND(), TimeSpan.Zero);
    }

    // The circle's duel is over (won, or called off): everyone leaves the battle where they stand, as after a battle
    // with monsters, not a PvP arena match (that left the spellbook hidden, Niduus 2026-10-23).
    private void CloseCircleDuel(int winningTeam) {
        Timers.Cancel(DUEL_CIRCLE_TIMER_KEY);
        if (winningTeam >= 0) {
            TellCircleDuel($"Team {(winningTeam == (int) CombatTeam.Player ? "A (blue)" : "B (red)")} wins the duel!");
            ZoneBroadcast(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_COMBATMATCHRESULT { DuelID = SigilId, WinningTeam = (byte) winningTeam });
        }

        EnactActionOnSubCircles(circle => {
            if (circle.IsHumanPlayer && circle.ParticipantActor is not null) {
                _mustStepOff.Add(circle.ParticipantActor);
                circle.ParticipantActor.Tell(new GAME_5_PROTOCOL.MSG_ENTERSTATE {
                    GameObjectID = circle.ParticipantObject.m_globalID,
                    State = (uint) NPCStates.Idle,
                });
            }
        });

        // A wizard defeated in the duel: the game keeps them "defeated", with no spellbook or buttons, even in the
        // next duel (Niduus, 2026-10-23). After a lost monster battle the trip to the hub resets that; here the
        // wizard reloads the zone on the spot they stand on.
        var defeated = new List<(IActorRef Session, Wizard Wizard)>();
        EnactActionOnSubCircles(circle => {
            if (circle.IsHumanPlayer && !circle.IsAlive && circle.ParticipantActor is not null && circle._wizard is { } wizard) {
                defeated.Add((circle.ParticipantActor, wizard));
            }
        });

        _pvp.TryFinish();
        // As after a monster battle: the victory phase, then the end.
        Duel.m_duelPhase = kDuelPhase.kPhase_Victory;
        SendCombatPhase((byte) Duel.m_duelPhase);
        Duel.m_duelPhase = kDuelPhase.kPhase_Ended;
        SendCombatPhase((byte) Duel.m_duelPhase);
        ZoneBroadcast(new DOODLEDOUG_MESSAGES_51_PROTOCOL.MSG_ENDDUEL { DuelID = SigilId });

        ReportPvPDuelEnded(winningTeam);
        foreach (var charId in _circleHealth.Keys.ToList()) {
            RestoreCircleHealth(charId);
        }

        foreach (var (session, wizard) in defeated) {
            // A house is entered by its lot; reloading one here could land in the wizard's own house instead.
            if (wizard.AttachedLotGid != 0 || string.IsNullOrEmpty(wizard.Zone)) {
                continue;
            }

            Logger.Information("Duel circle {0}: {1} was defeated and reloads the zone where they stand.",
                Logger.Args(SigilId, wizard.PlayerNameBehavior?.GetWizardName()));
            session.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
                DestinationZone = wizard.Zone,
                DestinationLocation = Shared.Resources.Util.GetCompactStringFromVector(wizard.Location, wizard.Orientation),
                SendToClient = true,
                ForceReload = true,
            });
        }

        DespawnDuel();
        _isActive = false;
        _isCircleDuel = false;
        _circleWaiting = false;
        _circleJoining.Clear();
        EnactActionOnSubCircles(circle => circle.RemoveParticipant());
    }

    // A duel circle is practice: a wizard leaves it with the health they stepped on with.
    private void RestoreCircleHealth(ulong charId) {
        if (!_circleHealth.Remove(charId, out var held)) {
            return;
        }

        held.Wizard.UpdateHealth(held.Health);
        held.Session.Tell(new WIZARD_12_PROTOCOL.MSG_UPDATEHEALTH {
            CharacterID = held.Wizard.GameObjectID,
            NewHealth = held.Health,
            NewHealthMax = held.Wizard.GameStats.GetClientTypeAlternative().m_baseHitpoints,
            DisplayDiff = 0,
        });
    }

    private void TellCircleDuel(string text) {
        Logger.Information("Duel circle {0}: {1}", Logger.Args(SigilId, text));
        var sessions = new HashSet<IActorRef>(_circleJoining.Values.Select(w => w.Session));
        if (_isActive && SubCircles is not null) {
            foreach (var circle in SubCircles.Where(c => c is { Occupied: true, IsHumanPlayer: true } && c.ParticipantActor is not null)) {
                sessions.Add(circle.ParticipantActor);
            }
        }

        foreach (var actor in sessions) {
            actor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = text, Modal = 0 });
        }
    }

}
