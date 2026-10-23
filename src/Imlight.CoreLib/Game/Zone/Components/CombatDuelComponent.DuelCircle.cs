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

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// Duel circles (Niduus, 2026-10-23: "we need duel circles enabled in houses"): a house's own PvP circle, and a PvP
/// battle circle (PvPSigil8Actor2Sides) added in the Customs tab, start a duel between the wizards standing on its two
/// sides, as on live. Once both sides have someone and stay for [PvP] DuelCircleCountdown seconds, everyone on it
/// plays their teleport effect, the PvPDirector makes the match and the circle seats them. The duel takes turns (the
/// PvP way) or lets both sides act at once: the Customs tab's Combat for an added circle, [PvP] HouseDuelTakesTurns
/// for a house's own. A wizard who fought on the circle steps off before it counts them again.
/// </summary>
internal sealed partial class CombatDuelComponent {

    private const string DUEL_CIRCLE_TIMER_KEY = "DuelCircle";
    private const string TOGETHER = "Together";
    // How far past the outermost place a wizard still counts as standing on the circle.
    private const float DUEL_CIRCLE_MARGIN = 60f;
    private const int MAX_PER_SIDE = 4;
    private static readonly TimeSpan s_teleportEffectTime = TimeSpan.FromSeconds(1.5);

    private sealed record DuelCircleWizard(ulong CharId, string Name, CombatTeam Side, GID GameObjectId);

    private readonly Dictionary<IActorRef, DuelCircleWizard> _onDuelCircle = [];
    // The wizards of the last duel here, until they step off.
    private readonly HashSet<IActorRef> _mustStepOff = [];
    private (Vector3 Place, CombatTeam Side)[] _duelCirclePlaces;
    private float _duelCircleReach;
    private int _duelCountdown = -1;
    private bool _duelCircleStarting;

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

    // Called for every move of a wizard in the zone while no duel runs here.
    private void DuelCircleMove(CoreObject playerObj, IActorRef playerActor, Wizard playerWizard) {
        if (_duelCircleStarting || !IsDuelCircle || playerWizard is null) {
            return;
        }

        var side = SideStoodOn(playerObj);
        if (side is null) {
            _mustStepOff.Remove(playerActor);
            if (_onDuelCircle.Remove(playerActor, out var left)) {
                TellDuelCircle($"{left.Name} stepped off the duel circle.");
                DuelCircleChanged();
            }

            return;
        }

        if (_mustStepOff.Contains(playerActor) || playerWizard.IsInDuel) {
            return;
        }

        if (_onDuelCircle.TryGetValue(playerActor, out var was) && was.Side == side) {
            return;
        }

        if (_onDuelCircle.Values.Count(w => w.Side == side) >= MAX_PER_SIDE) {
            return;
        }

        var name = playerWizard.PlayerNameBehavior?.GetWizardName() ?? "A wizard";
        _onDuelCircle[playerActor] = new DuelCircleWizard(playerWizard.CharId, name, side.Value, playerObj.m_globalID);
        TellDuelCircle($"{name} stands on the {(side == CombatTeam.Player ? "blue" : "red")} side of the duel circle.");
        DuelCircleChanged();
    }

    public override void OnPlayerLeave(IActorRef playerActor, ulong id)
        => DuelCircleLeave(playerActor);

    private void DuelCircleLeave(IActorRef playerActor) {
        _mustStepOff.Remove(playerActor);
        if (!_duelCircleStarting && _onDuelCircle.Remove(playerActor)) {
            DuelCircleChanged();
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

    private void DuelCircleChanged() {
        var blue = _onDuelCircle.Values.Count(w => w.Side == CombatTeam.Player);
        var red = _onDuelCircle.Values.Count(w => w.Side == CombatTeam.Monster);
        if (blue == 0 || red == 0) {
            if (_duelCountdown >= 0) {
                Timers.Cancel(DUEL_CIRCLE_TIMER_KEY);
                _duelCountdown = -1;
                TellDuelCircle("The duel is off: one side is empty.");
            }

            return;
        }

        // A new wizard starts the count again, so everyone sees how long is left.
        _duelCountdown = PvPSettings.DuelCircleCountdown;
        var style = DuelCircleRules.AlternateTurns ? "taking turns" : "both sides at once";
        TellDuelCircle($"Duel: {blue} against {red}, {style}. It starts in {_duelCountdown} seconds; step off to stay out.");
        Timers.StartPeriodicTimer(DUEL_CIRCLE_TIMER_KEY, new ZONE_102_PROTOCOL.MSG_DUELCIRCLETICK(), TimeSpan.FromSeconds(1));
    }

    [MessageHandler(typeof(ZONE_102_PROTOCOL.MSG_DUELCIRCLETICK))]
    private void ReceiveDuelCircleTick(ZONE_102_PROTOCOL.MSG_DUELCIRCLETICK message) {
        if (message.Go) {
            AskForCircleDuel();

            return;
        }

        if (_duelCountdown < 0 || _isActive) {
            Timers.Cancel(DUEL_CIRCLE_TIMER_KEY);

            return;
        }

        _duelCountdown--;
        if (_duelCountdown is > 0 and <= 3) {
            TellDuelCircle($"{_duelCountdown}...");
        }

        if (_duelCountdown > 0) {
            return;
        }

        Timers.Cancel(DUEL_CIRCLE_TIMER_KEY);
        _duelCountdown = -1;
        _duelCircleStarting = true;

        // Everyone on the circle teleports onto their place; the effect plays before they are moved.
        if (PvPSettings.DuelCircleTeleportEffect) {
            foreach (var wizard in _onDuelCircle.Values) {
                SetWizardState(wizard.GameObjectId, "Teleport");
            }

            Timers.StartSingleTimer(DUEL_CIRCLE_TIMER_KEY, new ZONE_102_PROTOCOL.MSG_DUELCIRCLETICK { Go = true }, s_teleportEffectTime);

            return;
        }

        AskForCircleDuel();
    }

    private void AskForCircleDuel() {
        var teams = new List<List<ulong>> {
            _onDuelCircle.Values.Where(w => w.Side == CombatTeam.Player).Select(w => w.CharId).ToList(),
            _onDuelCircle.Values.Where(w => w.Side == CombatTeam.Monster).Select(w => w.CharId).ToList(),
        };
        if (teams.Any(team => team.Count == 0) || PvPDirector.Instance is null) {
            DuelCircleFailed("one side is empty");

            return;
        }

        PvPDirector.Instance.Tell(new PVP_111_PROTOCOL.MSG_STARTLOCALMATCH { Circle = Self, Teams = teams, Rules = DuelCircleRules });
    }

    // The director could not make the duel.
    [MessageHandler(typeof(PVP_111_PROTOCOL.MSG_MATCHCANCELLED))]
    private void ReceiveCircleDuelCancelled(PVP_111_PROTOCOL.MSG_MATCHCANCELLED message) {
        if (_duelCircleStarting && !_isActive) {
            DuelCircleFailed("someone is in a match, a queue or not in the world");
        }
    }

    private void DuelCircleFailed(string why) {
        TellDuelCircle($"The duel could not start: {why}.");
        if (PvPSettings.DuelCircleTeleportEffect) {
            foreach (var wizard in _onDuelCircle.Values) {
                SetWizardState(wizard.GameObjectId, "InWorld");
            }
        }

        foreach (var actor in _onDuelCircle.Keys) {
            _mustStepOff.Add(actor);
        }

        _onDuelCircle.Clear();
        _duelCircleStarting = false;
    }

    // The circle's duel has its players on their places.
    private void DuelCircleSeated(IReadOnlyList<PvPRosterEntry> roster) {
        if (PvPSettings.DuelCircleTeleportEffect) {
            foreach (var entry in roster) {
                SetWizardState(entry.PlayerObject.m_globalID, "InWorld");
            }
        }

        foreach (var entry in roster) {
            _mustStepOff.Add(entry.Session);
        }

        foreach (var actor in _onDuelCircle.Keys) {
            _mustStepOff.Add(actor);
        }

        _onDuelCircle.Clear();
        _duelCircleStarting = false;
    }

    private void SetWizardState(GID gameObjectId, string state)
        => ZoneBroadcast(new GAME_5_PROTOCOL.MSG_ENTERSTATE { GameObjectID = gameObjectId, State = StringHash.Compute(state) });

    private void TellDuelCircle(string text) {
        Logger.Information("Duel circle {0}: {1}", Logger.Args(SigilId, text));
        foreach (var actor in _onDuelCircle.Keys) {
            actor.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = text, Modal = 0 });
        }
    }

}
