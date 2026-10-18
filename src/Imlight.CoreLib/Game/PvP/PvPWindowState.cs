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
 * PVP
 * ========================================================================
 * 
 * PURPOSE:
 * What one session remembers of the PvP windows it opened: the kiosk it used and the tournament, league and
 * season of the PvP window. The client sends neither league nor season after it opens the window.
 * 
 * USAGE EXAMPLE:
 * var replies = _window.OpenWindow(message, SessionActor.ActorRef, playerObject, wizard, DateTime.UtcNow);
 * 
 * NOTE:
 * Each method answers with the wire messages to send. A request that did not come from a kiosk of this session
 * or names a league or season the player may not use is refused, never trusted.
 * 
 * TODO:
 * 
 * 
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/14/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.IO;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.PvP;

internal sealed class PvPWindowState {

    private const byte RankedMatchType = 1;
    private const byte PracticeMatchType = 2;
    private const sbyte JoinCommand = 3;
    private const sbyte LeaveCommand = 4;
    private const string PracticeTournament = "PvPPractice";

    private uint _kioskLeagueId;

    public uint TournamentNameId { get; private set; }
    public uint LeagueId { get; private set; }
    public uint SeasonId { get; private set; }

    public bool HasWindow => TournamentNameId != 0;

    public void KioskUsed(uint leagueId) {
        _kioskLeagueId = leagueId;
        TournamentNameId = 0;
        LeagueId = 0;
        SeasonId = 0;
    }

    public IReadOnlyList<IMessage> OpenWindow(WIZARD3_56_PROTOCOL.MSG_PVP5THAGEOPENPVPWINDOW message, IActorRef playerActor,
                                              CoreObject playerObject, Wizard wizard, DateTime utcNow) {
        if (!PvPSettings.IsEnabled) {
            return [new WIZARD3_56_PROTOCOL.MSG_PVPDISABLED()];
        }

        if (message.MatchType != PracticeMatchType) {
            if (message.MatchType != RankedMatchType) {
                Logger.Warning("{CharId} opened a PvP window of unknown match type {MatchType}.",
                    Logger.Args(wizard.CharId, message.MatchType));
            }

            return [PvPArenaErrors.Message(PvPArenaErrors.INVALID_TOURNAMENT_TYPE)];
        }

        var refusal = GetOpenRefusal(message.LeagueID, message.SeasonID, playerActor, playerObject, wizard, utcNow);
        if (refusal is not null) {
            return [PvPArenaErrors.Message(refusal)];
        }

        if (!PvPTournamentFactory.TryGetTournament(PracticeTournament, out var tournament)) {
            Logger.Error("The tournament {Tournament} is missing from the Root.wad.", Logger.Args(PracticeTournament));

            return [PvPArenaErrors.Message(PvPArenaErrors.INVALID_TOURNAMENT_TYPE)];
        }

        TournamentNameId = StringHash.Compute(tournament.m_tournamentName);
        LeagueId = message.LeagueID;
        SeasonId = message.SeasonID;

        return [new WIZARD_12_PROTOCOL.MSG_PREPVPKIOSK {
            TournamentNameID = TournamentNameId,
            LeagueID = LeagueId,
            SeasonID = SeasonId,
            Patching = 0
        }];
    }

    public IReadOnlyList<IMessage> CanJoin(WIZARD3_56_PROTOCOL.MSG_PVP5THAGECANJOINMATCH message, IActorRef playerActor,
                                           CoreObject playerObject, Wizard wizard, DateTime utcNow) {
        var canJoin = HasWindow
                      && message.LeagueID == LeagueId
                      && message.SeasonID == SeasonId
                      && GetOpenRefusal(LeagueId, SeasonId, playerActor, playerObject, wizard, utcNow, requireKiosk: false) is null;

        return [new WIZARD3_56_PROTOCOL.MSG_PVP5THAGECANJOINMATCHRESPONSE { CanJoinQueue = (byte) (canJoin ? 1 : 0) }];
    }

    /// <summary>
    /// Answers the client echo of MSG_PREPVPKIOSK: it asks for the full tournament info once the window exists.
    /// </summary>
    public IReadOnlyList<IMessage> PrePvPKioskEcho(WIZARD_12_PROTOCOL.MSG_PREPVPKIOSK message, Wizard wizard) {
        // Patching means the client is still downloading arena zone packages; it echoes again when it is done.
        if (message.Patching != 0) {
            return [];
        }

        if (!HasWindow || message.TournamentNameID != TournamentNameId || message.LeagueID != LeagueId || message.SeasonID != SeasonId) {
            Logger.Warning("{CharId} echoed a PvP kiosk window that is not the one opened.", Logger.Args(wizard.CharId));

            return [];
        }

        return [GetUpdateInfo(wizard)];
    }

    public IReadOnlyList<IMessage> UpdateRequest(WIZARD_12_PROTOCOL.MSG_PVPUPDATEREQUEST message, Wizard wizard) {
        if (!TryReadRequest(message.InfoRequest, out var request)) {
            Logger.Warning("{CharId} sent a PvP update request that does not deserialize.", Logger.Args(wizard.CharId));

            return [];
        }

        if (!HasWindow || request.m_tournamentNameID != TournamentNameId) {
            Logger.Warning("{CharId} asked for the PvP tournament {TournamentId}, which is not the one opened.",
                Logger.Args(wizard.CharId, request.m_tournamentNameID));

            return [];
        }

        return [GetUpdateInfo(wizard)];
    }

    private static bool TryReadRequest(ByteString data, out TournamentInfoRequest request) {
        request = null;
        try {
            var serializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);

            return serializer.Deserialize(data, 4, out request) && request is not null;
        } catch (Exception) {
            return false;
        }
    }

    /// <summary>
    /// Answers the join and leave commands of the PvP window: the wire messages to send back and the message
    /// for the PvPDirector, if the command is in order.
    /// </summary>
    public (IReadOnlyList<IMessage> Replies, IServerMessage ToDirector) Intent(WIZARD_12_PROTOCOL.MSG_PVPINTENT message, IActorRef playerActor,
                                                                              CoreObject playerObject, Wizard wizard, DateTime utcNow) {
        switch (message.Command) {
            case JoinCommand:
                return Join(message, playerActor, playerObject, wizard, utcNow);
            case LeaveCommand:
                return ([], new PVP_111_PROTOCOL.MSG_LEAVEQUEUE { CharId = wizard.CharId });
            default:
                Logger.Warning("{CharId} sent PvP intent command {Command}, which practice does not offer.",
                    Logger.Args(wizard.CharId, message.Command));

                return ([PvPArenaErrors.Message(PvPArenaErrors.INVALID_TOURNAMENT_TYPE)], null);
        }
    }

    /// <summary>
    /// Answers the Try All Opponents choice. The client also sends it unprompted right after it receives the
    /// first queued match actor.
    /// </summary>
    public PVP_111_PROTOCOL.MSG_SETEXPANDSEARCH ExpandSearch(WIZARD_12_PROTOCOL.MSG_EXPANDPVPSEARCH message, Wizard wizard) {
        if (!HasWindow || message.TournamentNameID != TournamentNameId || !PvPRegistry.IsQueued(wizard.CharId)) {
            return null;
        }

        return new PVP_111_PROTOCOL.MSG_SETEXPANDSEARCH { CharId = wizard.CharId, Expand = message.ExpandMatchSearch != 0 };
    }

    private (IReadOnlyList<IMessage> Replies, IServerMessage ToDirector) Join(WIZARD_12_PROTOCOL.MSG_PVPINTENT message, IActorRef playerActor,
                                                                             CoreObject playerObject, Wizard wizard, DateTime utcNow) {
        if (!PvPSettings.IsEnabled) {
            return ([new WIZARD3_56_PROTOCOL.MSG_PVPDISABLED()], null);
        }

        if (!HasWindow || message.TournamentNameID != TournamentNameId || message.MatchID != 0
            || !PvPTournamentFactory.TryGetTournament(TournamentNameId, out var tournament)) {
            Logger.Warning("{CharId} tried to join tournament {TournamentId} match {MatchId} without that window.",
                Logger.Args(wizard.CharId, message.TournamentNameID, message.MatchID));

            return ([], null);
        }

        if (!PvPMatchFactory.TryGetMatch(message.MatchNameID, out var match)) {
            return ([PvPArenaErrors.Message(PvPArenaErrors.INVALID_MATCH_NAME_ID)], null);
        }

        string matchName = match.m_matchName;
        if (!tournament.m_allowedMatches.Any(name => (string) name == matchName)) {
            return ([PvPArenaErrors.Message(PvPArenaErrors.MATCH_NOT_ALLOWED)], null);
        }

        // Teams of more than one player come with the later phases.
        if (match.m_teamSize != 1 || match.m_teamCount != 2) {
            return ([PvPArenaErrors.Message(PvPArenaErrors.INVALID_MATCH_NAME_ID)], null);
        }

        var refusal = GetOpenRefusal(LeagueId, SeasonId, playerActor, playerObject, wizard, utcNow, requireKiosk: false);
        if (refusal is not null) {
            return ([PvPArenaErrors.Message(refusal)], null);
        }

        return ([], new PVP_111_PROTOCOL.MSG_JOINQUEUE {
            CharId = wizard.CharId,
            TournamentNameId = TournamentNameId,
            MatchNameId = message.MatchNameID,
            LeagueId = LeagueId,
            SeasonId = SeasonId,
            ExpandSearch = message.AllowExtMatchSearch != 0
        });
    }

    private GAME_5_PROTOCOL.MSG_PVPUPDATEINFO GetUpdateInfo(Wizard wizard) {
        PvPTournamentFactory.TryGetTournament(TournamentNameId, out var tournament);

        return new GAME_5_PROTOCOL.MSG_PVPUPDATEINFO {
            TournamentInfo = PvPBlobs.TournamentList(tournament?.m_tournamentName ?? string.Empty, TournamentNameId),
            CharacterID = wizard.CharId,
            PromptMsg = 0,
            DiffType = 0,
            IsPvPQueue = 0,
            IsPlayerAccountAlreadyHosting = 0
        };
    }

    private string GetOpenRefusal(uint leagueId, uint seasonId, IActorRef playerActor, CoreObject playerObject, Wizard wizard,
                                  DateTime utcNow, bool requireKiosk = true) {
        if (requireKiosk && (_kioskLeagueId == 0 || leagueId != _kioskLeagueId)) {
            Logger.Warning("{CharId} opened a PvP window without using a kiosk of that league.", Logger.Args(wizard.CharId));

            return PvPArenaErrors.PLAYER_INELIGIBLE;
        }

        if (!PvPLeagueFactory.TryGetLeague(leagueId, out var league) || !PvPSeasonClock.IsOpen(league, seasonId, utcNow)) {
            return PvPArenaErrors.PLAYER_INELIGIBLE;
        }

        if (!PvPEligibility.MeetsLeagueRequirements(league, playerActor, playerObject, wizard)) {
            return PvPArenaErrors.NOT_IN_LEVEL_RANGE;
        }

        return PvPEligibility.GetQueueRefusal(wizard);
    }

}
