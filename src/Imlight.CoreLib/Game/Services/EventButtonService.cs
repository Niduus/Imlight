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
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Game.Events;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// The game's event button for one session (Game/Events/EventButton.cs): shown on arrival while an event is on, its
/// window's info and leaderboard, and its Go button, which goes to the Library on the way to the Deckathalon.
/// </summary>
internal class EventButtonService(SessionActor sessionActor) : MessageService(sessionActor) {

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new EventButtonService(parentActor));

    // Where the event window's Go button sends a wizard: the Library, as the game's event template says, in front of
    // its event portal (WC-Teleport-LibToArena stands at -1.37, 1315.35, 1.50), facing it, instead of at the doors.
    private const string LIBRARY_ZONE = "WizardCity/Interiors/WC_Library";
    private const string LIBRARY_LOCATION = "-1.37,1150,1.5,3.1416";

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE))]
    private void ReceiveAttachComplete(SERVICE_101_PROTOCOL.MSG_ATTACHCOMPLETE message) {
        try {
            SendToSocket(EventButton.ButtonMessage());
        }
        catch (Exception e) {
            Logger.Error("Event button: login update failed: {0}", Logger.Args(e));
        }
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_REQUESTCLASSPROJECTINFO))]
    private void ReceiveRequestInfo(WIZARD2_53_PROTOCOL.MSG_REQUESTCLASSPROJECTINFO message) {
        try {
            // The button's request carries no DisplayInfo of its own (OnOpenClassProjectWindow sends the message empty),
            // and the client opens the window only when the answer's DisplayInfo is set.
            var wizard = GetActiveWizard();
            SendToSocket(EventButton.InfoMessage(wizard, 1));
            Logger.Information("Event button: sent the event window to {0} (event {1}).", Logger.Args(wizard.CharId, EventButton.ProjectId(GameEvents.Current)));
        }
        catch (Exception e) {
            Logger.Error("Event button: MSG_REQUESTCLASSPROJECTINFO failed: {0}", Logger.Args(e));
        }
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_CLASSPROJECTREQUESTLEADERBOARD))]
    private void ReceiveRequestLeaderboard(WIZARD2_53_PROTOCOL.MSG_CLASSPROJECTREQUESTLEADERBOARD message) {
        try {
            SendToSocket(EventButton.LeaderboardMessage(message.ProjectID));
        }
        catch (Exception e) {
            Logger.Error("Event button: MSG_CLASSPROJECTREQUESTLEADERBOARD failed: {0}", Logger.Args(e));
        }
    }

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_CLASSPROJECTREQUESTTELEPORT))]
    private void ReceiveRequestTeleport(WIZARD2_53_PROTOCOL.MSG_CLASSPROJECTREQUESTTELEPORT message)
        => Climb(message.ProjectID);

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_CLASSPROJECTUSETOKEN))]
    private void ReceiveUseToken(WIZARD2_53_PROTOCOL.MSG_CLASSPROJECTUSETOKEN message)
        => Enter(message.ProjectID, message.Level);

    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_CLASSPROJECTUSECROWNS))]
    private void ReceiveUseCrowns(WIZARD2_53_PROTOCOL.MSG_CLASSPROJECTUSECROWNS message)
        => Enter(message.ProjectID, message.Level);

    // The tower portal window's Use button (InteractDeckathalonPortalComponent opened it): starts the climb from the
    // checkpoint picked (its Level: 0 is floor 1, 1 floor 4, ...). Without that window, it is the Go button.
    private void Enter(uint projectId, uint level) {
        try {
            var wizard = GetActiveWizard();
            if (Deckathalon.TakePortalOffer(wizard.CharId) is not { } school) {
                Climb(projectId);

                return;
            }

            Deckathalon.TryStart(SessionActor.ActorRef, wizard, school, false, out var reply, (int) Math.Min(level, Deckathalon.MAX_CHECKPOINT));
            Logger.Information("Deckathalon | {0} enters the {1} tower from checkpoint {2}: {3}", Logger.Args(wizard.CharId, school, level, reply));
            SendToSocket(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = reply, Modal = 0 });
        }
        catch (Exception e) {
            Logger.Error("Event button: the tower portal's Use failed: {0}", Logger.Args(e));
        }
    }

    // The window's Go button: as in the game, it takes the wizard to the Library (the event's m_eventTeleportZone), where
    // the event's portal to the Duel Arena stands while it is on; the arena's tower portal starts the climb.
    private void Climb(uint projectId) {
        try {
            var wizard = GetActiveWizard();
            var school = EventButton.SchoolOf(projectId) ?? Deckathalon.CurrentEvent?.School ?? "";
            if (Deckathalon.CurrentEvent is null || wizard.IsInDuel) {
                var why = wizard.IsInDuel ? "Finish your battle first." : "No Deckathalon is on right now. The Events card of the Imlight app starts one.";
                SendToSocket(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = why, Modal = 0 });

                return;
            }

            Logger.Information("Event button: {0} goes to the Library for event {1} ({2}).", Logger.Args(wizard.CharId, projectId, school));
            SessionActor.ActorRef.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
                DestinationZone = LIBRARY_ZONE,
                DestinationLocation = LIBRARY_LOCATION,
                SendToClient = true,
                OwnerCharId = wizard.CharId,
            });
            SendToSocket(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE {
                Message = $"The {Deckathalon.CurrentEvent.School} Deckathalon: take the portal in the Library to the Duel Arena, then the tower portal there.",
                Modal = 0,
            });
        }
        catch (Exception e) {
            Logger.Error("Event button: Go failed: {0}", Logger.Args(e));
        }
    }

}
