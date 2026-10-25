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
 * CASTLE TOURS SERVICE
 * ========================================================================
 *
 * PURPOSE:
 * Answers the Castle Tours window (Myrella Windspar in the Castle Tours building, InteractCastleToursComponent):
 * adding and removing a castle, visiting one (New Today, Random by type, the leaderboards, Friends, Favorites),
 * My Castles, rating a house with stars, favorites, and the rating badges.
 *
 * USAGE EXAMPLE:
 * Internal service handling the Castle Tours messages within the game server session.
 *
 * NOTE:
 * Read from WizardGraphicalClient.exe (r806919), 2026-10-24:
 * - Add: the window asks MSG_CASTLETOURSREQUESTMYCASTLES (answered with a CastleTourHouseTemplateList of the houses on
 *   the Tour and the Days), lists the deeds the player holds, and sends MSG_CASTLETOURSPREADD for the one picked. The
 *   server's MSG_CASTLETOURSPREADD back opens the type window, or for HouseType 5 or 6 the confirm window at once
 *   (0x140efb740). The confirm sends MSG_CASTLETOURSADD, answered by MSG_CASTLETOURSADDRESULT.
 * - Remove: the same list, then MSG_CASTLETOURSREMOVE, answered by MSG_CASTLETOURSREMOVERESULT.
 * - Visit: MSG_CASTLETOURSVISITCASTLE VisitType 0x20 (New Today) or 0x21 (Random, HouseType 0-8). The server picks a
 *   house and sends MSG_CASTLETOURSTELEPORTPLAYER with a CastleTourTeleportInfo. The client sends the same message
 *   back (0x140f49db0), which the server then teleports. A server MSG_CASTLETOURSVISITCASTLE means no house was found.
 * - Leaderboards: MSG_CASTLETOURSREQUESTLEADERBOARD (PageNumber is the first line, 10 lines a page) is answered by
 *   MSG_CASTLETOURSLEADERBOARDRESPONSE with a CastleTourLeaderboardUpdate. Clicking a line sends
 *   MSG_CASTLETOURSTELEPORTPLAYER with LeaderboardTeleport 1. "Visit Another Castle" after a leaderboard visit sends
 *   MSG_CASTLETOURSREQUESTNEXTLEADERBOARD.
 * - In the house, MSG_CASTLETOURSENABLERATINGDISPLAY shows the rate, favorite and visit-another buttons. FirstDisplay
 *   is the house type in the high four bits (what "Visit Another" asks for) and the welcome note in the low four.
 *   The rating window sends MSG_CASTLETOURSSENDRATING 1-4; MSG_CASTLETOURSPOSTRATEHOUSE shows the daily badge credit.
 * - Only New Today and Random visits can be rated (Housing_00000636).
 *
 * TODO:
 * - Hall of Fame, Castle Games and reporting a house are not done.
 *
 * Created by: Jay
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Cryptography;
using Imlight.Common;
using Imlight.CoreLib.Game.Housing;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Utilities;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

internal class CastleToursService(SessionActor sessionActor) : MessageService(sessionActor) {

    private const sbyte NewTodayVisit = 0x20;
    private const sbyte RandomVisit = 0x21;
    private const int PageSize = 10;
    private const int RecentlySeenCount = 5;
    private const uint SerializeMask = 4;
    // MSG_CASTLETOURSPREADD: any other value opens the type window.
    private const sbyte PickType = -1;

    /// <summary>A house of the Tour the player is on the way to, or in.</summary>
    private sealed record TourVisit(ulong OwnerCharId, uint TemplateId, bool Rateable, CastleTourType VisitedType, bool NewToday, int Board) {
        internal bool WelcomeShown { get; set; }
    }

    // The house the Visit buttons offered; the client's teleport echo for it can be rated.
    private (ulong Owner, uint Template, CastleTourType Type, bool NewToday)? _offered;
    private TourVisit _visit;
    private readonly Queue<string> _recentlySeen = new();

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new CastleToursService(parentActor));

    // ---- Add and remove ------------------------------------------------------------------------------------------

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSREQUESTMYCASTLES))]
    private void ReceiveRequestMyCastles(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSREQUESTMYCASTLES message) {
        var wizard = GetActiveWizard();
        var templates = CastleTourBoard.TemplatesOf(wizard.CharId);
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSREQUESTMYCASTLES {
            GlobalID = message.GlobalID,
            Data = Serialize(new CastleTourHouseTemplateList { m_houseTemplateList = templates }),
            Days = (sbyte) Math.Clamp(CastleTourBoard.Settings.Days, 1, sbyte.MaxValue),
        });
        Logger.Information("Castle Tours | {0} opened the castle list: {1} on the Tour ({2}).",
            Logger.Args(wizard.CharId, templates.Count, string.Join(", ", templates)));
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSPREADD))]
    private void ReceivePreAdd(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSPREADD message) {
        var wizard = GetActiveWizard();
        if (!HouseDeeds.TryGetHeldTemplate(wizard, message.HouseGID, out var templateId)) {
            Logger.Warning("Castle Tours | {0} picked item {1} to add, which is not a house they hold.", Logger.Args(wizard.CharId, message.HouseGID));
            SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSADDRESULT { Status = (uint) CastleTourResult.Problem });

            return;
        }

        // A dorm goes straight to the confirm window as a dorm.
        var isDorm = HouseTemplates.Starters().Any(starter => starter.TemplateId == templateId);
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSPREADD {
            GlobalID = message.GlobalID,
            HouseGID = message.HouseGID,
            HouseType = isDorm ? (sbyte) CastleTourType.Dorm : PickType,
        });
        Logger.Information("Castle Tours | {0} wants to add house {1} (item {2}){3}.",
            Logger.Args(wizard.CharId, templateId, message.HouseGID, isDorm ? ", a dorm" : ""));
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSADD))]
    private void ReceiveAdd(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSADD message) {
        var wizard = GetActiveWizard();
        var result = HouseDeeds.TryGetHeldTemplate(wizard, message.HouseGID, out var templateId)
            ? CastleTourBoard.TryAdd(wizard, templateId, (CastleTourType) message.HouseType)
            : CastleTourResult.Problem;
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSADDRESULT { Status = (uint) result });
        Logger.Information("Castle Tours | {0} added house {1} (item {2}) as type {3}: {4}.",
            Logger.Args(wizard.CharId, templateId, message.HouseGID, (CastleTourType) message.HouseType, result));
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSREMOVE))]
    private void ReceiveRemove(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSREMOVE message) {
        var wizard = GetActiveWizard();
        var result = HouseDeeds.TryGetHeldTemplate(wizard, message.HouseGID, out var templateId)
            ? CastleTourBoard.TryRemove(wizard.CharId, templateId)
            : CastleTourResult.Problem;
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSREMOVERESULT { Status = (uint) result });
        Logger.Information("Castle Tours | {0} removed house {1} (item {2}): {3}.", Logger.Args(wizard.CharId, templateId, message.HouseGID, result));
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSREQUESTMYCASTLEDATA))]
    private void ReceiveRequestMyCastleData(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSREQUESTMYCASTLEDATA message) {
        var wizard = GetActiveWizard();
        var now = DateTime.UtcNow;
        var mine = CastleTourBoard.MyCastles(wizard.CharId);
        var list = new CastleTourMyCastleDataList {
            m_entryList = [.. mine.Select(pair => new CastleTourMyCastleData {
                m_houseTemplateID = pair.House.TemplateId,
                m_rank = (ushort) Math.Min(pair.Rank, ushort.MaxValue),
                m_visitCount = (ushort) Math.Clamp(pair.House.Visits, 0, ushort.MaxValue),
                m_timeRemaining = (uint) Math.Max(0, (pair.House.EndsUtc - now).TotalSeconds),
            })],
        };
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSREQUESTMYCASTLEDATA {
            GlobalID = message.GlobalID,
            Data = Serialize(list),
        });
        Logger.Information("Castle Tours | {0} opened My Castles: {1} on the Tour.", Logger.Args(wizard.CharId, mine.Count));
    }

    // ---- Visits --------------------------------------------------------------------------------------------------

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSVISITCASTLE))]
    private void ReceiveVisitCastle(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSVISITCASTLE message) {
        var wizard = GetActiveWizard();
        var newToday = message.VisitType != RandomVisit;
        var type = Enum.IsDefined((CastleTourType) message.HouseType) ? (CastleTourType) message.HouseType : CastleTourType.Any;
        var house = CastleTourBoard.PickVisit(wizard, newToday, newToday ? CastleTourType.Any : type, _recentlySeen);
        Logger.Information("Castle Tours | {0} asked to visit (VisitType 0x{1:X2}, HouseType {2}): {3}.",
            Logger.Args(wizard.CharId, message.VisitType, message.HouseType,
                house is null ? "no house" : $"{house.OwnerName}'s house {house.TemplateId} ({house.Type})"));
        if (house is null) {
            // The client says there was a problem finding a castle (Housing_00000600).
            SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSVISITCASTLE { VisitType = 0, HouseType = message.HouseType });

            return;
        }

        _offered = (house.OwnerCharId, house.TemplateId, newToday ? CastleTourType.Any : type, newToday);
        Offer(wizard, house, leaderboard: false);
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSREQUESTNEXTLEADERBOARD))]
    private void ReceiveRequestNextLeaderboard(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSREQUESTNEXTLEADERBOARD message) {
        var wizard = GetActiveWizard();
        var board = message.LeaderboardType & ~CastleTourBoard.MyCastlesFlag;
        var lines = CastleTourBoard.Leaderboard(wizard, board)
            .Where(line => line.Rank != CastleTourBoard.OffTourRank && line.House.OwnerCharId != wizard.CharId)
            .Select(line => line.House)
            .ToList();
        if (lines.Count == 0) {
            SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSVISITCASTLE { VisitType = 0, HouseType = 0 });

            return;
        }

        // The next house after the one the player is in, or the first.
        var at = _visit is null ? -1 : lines.FindIndex(line => line.OwnerCharId == _visit.OwnerCharId && line.TemplateId == _visit.TemplateId);
        var next = lines[(at + 1) % lines.Count];
        Logger.Information("Castle Tours | {0} goes to the next house of leaderboard {1}: {2}'s house {3}.",
            Logger.Args(wizard.CharId, message.LeaderboardType, next.OwnerName, next.TemplateId));
        _offered = null;
        Offer(wizard, next, leaderboard: true);
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSTELEPORTPLAYER))]
    private void ReceiveTeleportPlayer(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSTELEPORTPLAYER message) {
        var wizard = GetActiveWizard();
        if (!TryParse(message.TeleportData, out CastleTourTeleportInfo info) || info is null) {
            Logger.Warning("Castle Tours | {0} sent a teleport the server cannot read ({1} bytes).",
                Logger.Args(wizard.CharId, ((byte[]) message.TeleportData)?.Length ?? 0));
            SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSTELEPORTREJECTED());

            return;
        }

        var owner = (ulong) info.m_owningCharacterGID;
        var house = CastleTourBoard.Get(owner, info.m_houseTemplateID);
        if (house is null) {
            Logger.Information("Castle Tours | {0} wants to go to house {1} of {2}, which is not on the Tour.",
                Logger.Args(wizard.CharId, info.m_houseTemplateID, owner));
            SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSTELEPORTREJECTED());

            return;
        }

        var offered = _offered is { } o && o.Owner == house.OwnerCharId && o.Template == house.TemplateId ? o : default;
        var fromVisitButtons = _offered is not null && offered.Owner != 0 && message.LeaderboardTeleport == 0;
        _offered = null;
        var visit = new TourVisit(house.OwnerCharId, house.TemplateId,
            Rateable: fromVisitButtons && house.OwnerCharId != wizard.CharId,
            VisitedType: fromVisitButtons ? offered.Type : house.Type,
            NewToday: fromVisitButtons && offered.NewToday,
            Board: message.LeaderboardTeleport != 0 ? 1 : 0);

        if (!TryGo(wizard, house)) {
            SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSTELEPORTREJECTED());

            return;
        }

        _visit = visit;
        Remember(house);
        if (house.OwnerCharId != wizard.CharId) {
            CastleTourBoard.CountVisit(house.OwnerCharId, house.TemplateId);
            Imlight.CoreLib.Game.Events.ClassProjects.Record(SessionActor.ActorRef, wizard, Imcodec.ObjectProperty.TypeCache.ClassProjectActivityType.CPT_VisitCastleToursHouse);
        }
    }

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceiveAttachComplete(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        try {
            ShowRatingButtons();
        }
        catch (Exception ex) {
            Logger.Error("Castle Tours | Showing the rating buttons failed: {0}", Logger.Args(ex));
        }
    }

    // ---- Leaderboards and favorites ------------------------------------------------------------------------------

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSREQUESTLEADERBOARD))]
    private void ReceiveRequestLeaderboard(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSREQUESTLEADERBOARD message) {
        var wizard = GetActiveWizard();
        var board = message.LeaderboardType & 0xFF;
        var lines = CastleTourBoard.Leaderboard(wizard, board);
        var start = (int) Math.Min(message.PageNumber, (uint) Math.Max(0, lines.Count - 1));
        start -= start % PageSize;
        var owners = new Dictionary<ulong, Wizard>();
        var update = new CastleTourLeaderboardUpdate {
            m_leaderboardSize = lines.Count,
            m_entryList = [.. lines.Skip(start).Take(PageSize).Select(line => EntryOf(wizard, line.House, line.Rank, owners))],
        };
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSLEADERBOARDRESPONSE {
            CharacterID = wizard.CharId,
            LeaderboardType = message.LeaderboardType,
            SortType = message.SortType,
            PageNumber = (uint) start,
            LeaderboardData = Serialize(update),
        });
        Logger.Information("Castle Tours | {0} opened leaderboard {1} (sort {2}, from line {3}): {4} houses.",
            Logger.Args(wizard.CharId, board, message.SortType, start, lines.Count));
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSADDFAVORITE))]
    private void ReceiveAddFavorite(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSADDFAVORITE message) {
        var wizard = GetActiveWizard();
        var result = CastleTourFavoriteResult.Problem;
        var house = wizard.AttachedLotGid == 0 ? null : HouseCollection.Load(wizard.AttachedLotGid);
        if (house is not null) {
            result = CastleTourBoard.AddFavorite(wizard.CharId, house.OwnerCharId, house.TemplateId);
        }

        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSADDFAVORITE { GlobalID = message.GlobalID, Success = (sbyte) result });
        Logger.Information("Castle Tours | {0} made the house of lot {1} a favorite: {2}.", Logger.Args(wizard.CharId, wizard.AttachedLotGid, result));
    }

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSREMOVEFAVORITE))]
    private void ReceiveRemoveFavorite(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSREMOVEFAVORITE message) {
        var wizard = GetActiveWizard();
        var removed = CastleTourBoard.RemoveFavorite(wizard.CharId, message.OwningPlayerGID, message.HouseTemplateID);
        Logger.Information("Castle Tours | {0} removed favorite house {1} of {2}: {3}.",
            Logger.Args(wizard.CharId, message.HouseTemplateID, message.OwningPlayerGID, removed ? "done" : "it was not a favorite"));
    }

    // ---- Rating --------------------------------------------------------------------------------------------------

    [MessageHandler(typeof(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSSENDRATING))]
    private void ReceiveSendRating(WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSSENDRATING message) {
        var wizard = GetActiveWizard();
        if (_visit is null || !IsInTourHouse(wizard, _visit)) {
            Logger.Information("Castle Tours | {0} sent rating {1} outside a Castle Tours visit.", Logger.Args(wizard.CharId, message.Rating));

            return;
        }

        if (!_visit.Rateable) {
            Logger.Information("Castle Tours | {0} tried to rate a house they did not reach by New Today or Random.", Logger.Args(wizard.CharId));
            InformGameClient("You may only rate a house by selecting New Today or Random.");

            return;
        }

        if (!CastleTourBoard.TryRate(wizard, _visit.OwnerCharId, _visit.TemplateId, message.Rating, out var creditToday, out var earned)) {
            Logger.Warning("Castle Tours | {0} could not rate house {1} of {2} with {3}.",
                Logger.Args(wizard.CharId, _visit.TemplateId, _visit.OwnerCharId, message.Rating));

            return;
        }

        var settings = CastleTourBoard.Settings;
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSPOSTRATEHOUSE {
            CharacterID = wizard.CharId,
            BadgeCredit = (sbyte) Math.Clamp(creditToday, 0, sbyte.MaxValue),
            MaxBadgeCredit = (sbyte) Math.Clamp(settings.DailyBadgeCredit, 0, sbyte.MaxValue),
        });
        foreach (var badge in earned) {
            TellOtherServices(new CHARACTER_103_PROTOCOL.MSG_BADGEEVENT { Kind = BadgeEventKind.Award, BadgeId = StringHash.Compute(badge) });
        }

        Logger.Information("Castle Tours | {0} rated house {1} of {2}: {3} stars (badge credit today {4}/{5}{6}).",
            Logger.Args(wizard.CharId, _visit.TemplateId, _visit.OwnerCharId, message.Rating, creditToday, settings.DailyBadgeCredit,
                earned.Count == 0 ? "" : $", earned {string.Join(", ", earned)}"));

        Imlight.CoreLib.Game.Events.ClassProjects.Record(SessionActor.ActorRef, wizard, Imcodec.ObjectProperty.TypeCache.ClassProjectActivityType.CPT_RateCastleToursHouse);

        // The buttons show the new rating (Re-Rate).
        _visit.WelcomeShown = true;
        ShowRatingButtons();
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Sends the client the house to go to; the client answers with the same message, and the teleport follows.
    /// </summary>
    private void Offer(Wizard wizard, CastleTourHouse house, bool leaderboard) {
        var owner = OwnerOf(wizard, house.OwnerCharId);
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSTELEPORTPLAYER {
            CharacterID = wizard.CharId,
            TeleportData = Serialize(TeleportInfoOf(house, owner)),
            Source = 0,
            LeaderboardTeleport = (sbyte) (leaderboard ? 1 : 0),
            OwnerID = house.OwnerCharId,
        });
    }

    /// <summary>
    /// Teleports the player into the house: their own the way Home does, anyone else's as a visitor.
    /// </summary>
    private bool TryGo(Wizard wizard, CastleTourHouse house) {
        HouseEntry entry;
        if (house.OwnerCharId == wizard.CharId) {
            if (!HouseDeeds.TryPrepareHouse(wizard, house.TemplateId, out entry, out var failure)) {
                Logger.Warning("Castle Tours | {0} cannot go to their own house {1}: {2}.", Logger.Args(wizard.CharId, house.TemplateId, failure));

                return false;
            }
        } else {
            var owner = WizardCollection.GetCharacterUnloaded(house.OwnerCharId);
            if (owner is null || !HouseTemplates.TryGet(house.TemplateId, out var template)) {
                Logger.Warning("Castle Tours | The owner {0} of house {1} cannot be loaded.", Logger.Args(house.OwnerCharId, house.TemplateId));

                return false;
            }

            var state = HouseRegistry.GetOrCreate(owner, house.TemplateId, out _);
            if (state is null) {
                Logger.Warning("Castle Tours | House {0} of {1} could not be stored.", Logger.Args(house.TemplateId, house.OwnerCharId));

                return false;
            }

            entry = HouseEntry.ForTeleport(state.LotGid, owner.CharId, template);
        }

        Logger.Information("Castle Tours | {0} goes to {1}'s house {2} (lot {3}, {4}).",
            Logger.Args(wizard.CharId, house.OwnerName, house.TemplateId, entry.LotGid, entry.Zone));
        Teleport(entry.Zone, doTeleportEffects: true, houseEntry: entry);

        return true;
    }

    /// <summary>
    /// Shows the Castle Tours buttons when the player stands in the house of their visit (or its other side).
    /// </summary>
    private void ShowRatingButtons() {
        var wizard = GetActiveWizard();
        if (_visit is null || wizard is null) {
            return;
        }

        if (!IsInTourHouse(wizard, _visit)) {
            // They left the house.
            if (wizard.AttachedLotGid == 0 || !CastleTourBoard.IsOnTour(_visit.OwnerCharId, CurrentTemplate(wizard))) {
                _visit = null;
            }

            return;
        }

        var house = CastleTourBoard.Get(_visit.OwnerCharId, _visit.TemplateId);
        if (house is null) {
            return;
        }

        var owner = OwnerOf(wizard, house.OwnerCharId);
        var lot = owner?.HouseLots.GetValueOrDefault(house.TemplateId) ?? 0;
        var structure = house.StructureTemplateId == 0 ? 0 : owner?.HouseLots.GetValueOrDefault(house.StructureTemplateId) ?? 0;
        var hint = _visit.NewToday ? 0 : Math.Clamp((int) _visit.VisitedType, 0, 15);
        var welcome = _visit.Rateable && !_visit.WelcomeShown ? 1 : 0;
        _visit.WelcomeShown = true;
        SendToSocket(new WIZARDHOUSING_50_PROTOCOL.MSG_CASTLETOURSENABLERATINGDISPLAY {
            CastleType = (sbyte) house.Type,
            Rating = (sbyte) CastleTourBoard.RatingBy(wizard.CharId, house),
            FirstDisplay = (hint << 4) | welcome,
            TemplateID = house.TemplateId,
            LotGID = lot,
            StructureGID = structure,
        });
        Logger.Information("Castle Tours | {0} is in {1}'s house {2} (rateable {3}, lot {4}, inside {5}).",
            Logger.Args(wizard.CharId, house.OwnerName, house.TemplateId, _visit.Rateable, lot, structure));
    }

    private static bool IsInTourHouse(Wizard wizard, TourVisit visit) {
        if (wizard.AttachedLotGid == 0) {
            return false;
        }

        var house = HouseCollection.Load(wizard.AttachedLotGid);
        if (house is null || house.OwnerCharId != visit.OwnerCharId) {
            return false;
        }

        var tour = CastleTourBoard.Get(visit.OwnerCharId, visit.TemplateId);

        return tour is not null && tour.Names(house.OwnerCharId, house.TemplateId);
    }

    private static uint CurrentTemplate(Wizard wizard)
        => wizard.AttachedLotGid == 0 ? 0 : HouseCollection.Load(wizard.AttachedLotGid)?.TemplateId ?? 0;

    private void Remember(CastleTourHouse house) {
        _recentlySeen.Enqueue(CastleTourBoard.KeyOf(house));
        while (_recentlySeen.Count > RecentlySeenCount) {
            _recentlySeen.Dequeue();
        }
    }

    private static Wizard OwnerOf(Wizard viewer, ulong ownerCharId)
        => ownerCharId == viewer.CharId ? viewer : WizardCollection.GetCharacterUnloaded(ownerCharId);

    private static CastleTourTeleportInfo TeleportInfoOf(CastleTourHouse house, Wizard owner)
        => new() {
            m_houseTemplateID = house.TemplateId,
            m_structureTemplateID = house.StructureTemplateId,
            m_lotInstanceGID = owner?.HouseLots.GetValueOrDefault(house.TemplateId) ?? 0,
            m_structureInstanceGID = house.StructureTemplateId == 0 ? 0 : owner?.HouseLots.GetValueOrDefault(house.StructureTemplateId) ?? 0,
            m_owningCharacterGID = house.OwnerCharId,
        };

    private static CastleTourLeaderboardEntry EntryOf(Wizard viewer, CastleTourHouse house, ushort rank, Dictionary<ulong, Wizard> owners) {
        if (!owners.TryGetValue(house.OwnerCharId, out var owner)) {
            owner = OwnerOf(viewer, house.OwnerCharId);
            owners[house.OwnerCharId] = owner;
        }

        var nameHex = !string.IsNullOrEmpty(house.OwnerNameHex) ? house.OwnerNameHex : owner?.PlayerNameBehavior?.GetWizardNameAsByteHexString();

        return new CastleTourLeaderboardEntry {
            m_houseTemplateID = house.TemplateId,
            m_structureTemplateID = house.StructureTemplateId,
            m_lotInstanceGID = owner?.HouseLots.GetValueOrDefault(house.TemplateId) ?? 0,
            m_structureInstanceGID = house.StructureTemplateId == 0 ? 0 : owner?.HouseLots.GetValueOrDefault(house.StructureTemplateId) ?? 0,
            m_owningCharacterGID = house.OwnerCharId,
            m_type = (byte) house.Type,
            m_packedName = string.IsNullOrEmpty(nameHex) ? new ByteString([]) : new ByteString(DataManipulation.SpacedHexStringToBytes(nameHex)),
            m_rank = rank,
        };
    }

    private static ByteString Serialize(PropertyClass value) {
        if (!new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None).Serialize(value, SerializeMask, out var serialized)) {
            throw new InvalidOperationException($"Could not serialize {value.GetType().Name}.");
        }

        return serialized;
    }

    private static bool TryParse<T>(ByteString data, out T value) where T : PropertyClass {
        value = null;
        byte[] bytes = data;
        if (bytes is null || bytes.Length == 0) {
            return false;
        }

        try {
            return new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None).Deserialize(bytes, SerializeMask, out value);
        } catch (Exception) {
            return false;
        }
    }

}
