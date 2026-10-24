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
using Imlight.CoreLib.Game.TeamUp;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Utilities;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Services;

/// <summary>
/// Team Up for one player (Game/TeamUp/TeamUpBoard): the sigil window's Team Up! button, the Join a Team window (the
/// kiosk or .teamup), joining a team, Ready and Cancel in the Team Up! window. A player who logs out leaves their team.
/// </summary>
internal class TeamUpService(SessionActor sessionActor) : MessageService(sessionActor) {

    // The world tab the Join a Team window shows, for its refresh.
    private uint _tab;
    // The player who used Team Up, so logging out takes them off their team.
    private ulong _charId;

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new TeamUpService(parentActor));

    // The Team Up! button of the sigil window (TeamUpConfirmationWindow's Team Up button), or its options with Edit.
    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_REQUESTTEAMUP))]
    private void ReceiveRequestTeamUp(WIZARD2_53_PROTOCOL.MSG_REQUESTTEAMUP message) {
        try {
            if (!TeamUpBoard.IsEnabled || MakeMember() is not { } member) {
                return;
            }

            if (!TeamUpSigils.TryGet(message.NPCID, out var sigil)) {
                Logger.Warning("Team Up: {0} asked for a team at {1}, which is not a known dungeon sigil.",
                    Logger.Args(member.CharId, message.NPCID));
                Note("This sigil can't make a team. Walk off the sigil and back on, then try again.");

                return;
            }

            TeamUpBoard.Request(member, sigil, message.TeamSize, message.IsFarming != 0, message.RequestedSigilMode,
                message.MinimumLevel, message.MaximumLevel, message.Edit != 0);
        }
        catch (Exception ex) {
            Logger.Error("Team Up: asking for a team failed. {0}", Logger.Args(ex.Message));
        }
    }

    // The options button of the Team Up! window: the game opens its team options again when the server sends this.
    [MessageHandler(typeof(WIZARD_12_PROTOCOL.MSG_REQUESTTEAMUPINFO))]
    private void ReceiveRequestTeamUpInfo(WIZARD_12_PROTOCOL.MSG_REQUESTTEAMUPINFO message) {
        try {
            SendToSocket(message);
        }
        catch (Exception ex) {
            Logger.Error("Team Up: opening the team options failed. {0}", Logger.Args(ex.Message));
        }
    }

    // The Join a Team window asks for its teams again.
    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_REQUESTTEAMHELPINFO))]
    private void ReceiveRequestTeamHelpInfo(WIZARD2_53_PROTOCOL.MSG_REQUESTTEAMHELPINFO message) {
        try {
            var wizard = GetActiveWizard();
            if (wizard is null) {
                return;
            }

            SendHelp(_tab != 0 ? _tab : TeamUpWorlds.WorldOf(wizard.Zone), wizard);
        }
        catch (Exception ex) {
            Logger.Error("Team Up: refreshing the Join a Team window failed. {0}", Logger.Args(ex.Message));
        }
    }

    // A world tab of the Join a Team window.
    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_CLIENTREQUESTTEAMHELPWORLDINFO))]
    private void ReceiveTeamHelpWorld(WIZARD2_53_PROTOCOL.MSG_CLIENTREQUESTTEAMHELPWORLDINFO message) {
        try {
            if (GetActiveWizard() is { } wizard) {
                SendHelp(message.WorldID, wizard);
            }
        }
        catch (Exception ex) {
            Logger.Error("Team Up: showing a world of the Join a Team window failed. {0}", Logger.Args(ex.Message));
        }
    }

    // A team picked in the Join a Team window.
    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_REQUESTTEAMHELPJOIN))]
    private void ReceiveTeamHelpJoin(WIZARD2_53_PROTOCOL.MSG_REQUESTTEAMHELPJOIN message) {
        try {
            if (!TeamUpBoard.IsEnabled || MakeMember() is not { } member) {
                return;
            }

            if (!TeamUpBoard.Join(member, message.SigilID, out var why)) {
                SendToSocket(new WIZARD2_53_PROTOCOL.MSG_REQUESTTEAMHELPJOINFAIL { PlayerGID = member.Gid });
                Note(why);
                if (GetActiveWizard() is { } wizard) {
                    SendHelp(message.WorldID != 0 ? message.WorldID : _tab, wizard);
                }
            }
        }
        catch (Exception ex) {
            Logger.Error("Team Up: joining a team failed. {0}", Logger.Args(ex.Message));
        }
    }

    // Cancel in the Team Up! window.
    [MessageHandler(typeof(WIZARD2_53_PROTOCOL.MSG_TEAMUPREMOVEPLAYER))]
    private void ReceiveRemovePlayer(WIZARD2_53_PROTOCOL.MSG_TEAMUPREMOVEPLAYER message) {
        try {
            var charId = GetActiveWizard()?.CharId ?? _charId;
            if (charId != 0) {
                TeamUpBoard.Leave(charId, "cancelled");
            }
        }
        catch (Exception ex) {
            Logger.Error("Team Up: leaving a team failed. {0}", Logger.Args(ex.Message));
        }
    }

    // Ready in the Team Up! window.
    [MessageHandler(typeof(WIZARD3_56_PROTOCOL.MSG_UPDATETEAMUPREADY))]
    private void ReceiveReady(WIZARD3_56_PROTOCOL.MSG_UPDATETEAMUPREADY message) {
        try {
            if (GetActiveWizard()?.CharId is { } charId and not 0) {
                TeamUpBoard.MarkReady(charId);
            }
        }
        catch (Exception ex) {
            Logger.Error("Team Up: marking ready failed. {0}", Logger.Args(ex.Message));
        }
    }

    // `.teamup` and the Join a Team kiosk open the window.
    [InternalMessageHandler(typeof(OpenTeamHelp))]
    private void ReceiveOpenTeamHelp(OpenTeamHelp message) {
        try {
            if (!TeamUpBoard.IsEnabled) {
                Note("Team Up is turned off on this server.");

                return;
            }

            if (GetActiveWizard() is not { } wizard) {
                return;
            }

            _tab = 0;
            SendHelp(TeamUpWorlds.WorldOf(wizard.Zone), wizard);
        }
        catch (Exception ex) {
            Logger.Error("Team Up: opening the Join a Team window failed. {0}", Logger.Args(ex.Message));
        }
    }

    // `.teamup ask`: a team asked for at the sigil the player stands at, without the sigil window.
    [InternalMessageHandler(typeof(AskTeamUp))]
    private void ReceiveAskTeamUp(AskTeamUp message) {
        try {
            if (!TeamUpBoard.IsEnabled || MakeMember() is not { } member) {
                return;
            }

            TeamUpBoard.Request(member, message.Sigil, message.Size, false, 0, 0, 0, false);
        }
        catch (Exception ex) {
            Logger.Error("Team Up: asking for a team with .teamup failed. {0}", Logger.Args(ex.Message));
        }
    }

    protected override void OnDispose() {
        base.OnDispose();
        try {
            if (_charId != 0) {
                TeamUpBoard.Leave(_charId, "logged out");
            }
        }
        catch (Exception ex) {
            Logger.Error("Team Up: taking a player who logged out off their team failed. {0}", Logger.Args(ex.Message));
        }
    }

    private void SendHelp(uint worldId, Wizard wizard) {
        if (TeamUpWorlds.ById(worldId) is null) {
            worldId = TeamUpWorlds.First.Id;
        }

        _tab = worldId;
        SendToSocket(TeamUpBoard.HelpInfo(worldId, (uint) wizard.MagicSchoolBehavior.Level));
    }

    private TeamUpBoard.Member MakeMember() {
        var wizard = GetActiveWizard();
        if (wizard is null) {
            return null;
        }

        _charId = wizard.CharId;

        return new TeamUpBoard.Member {
            CharId = wizard.CharId,
            Gid = wizard.GameObjectID,
            Session = SessionActor.ActorRef,
            NameBlob = DataManipulation.SpacedHexStringToBytes(wizard.PlayerNameBehavior.GetWizardNameAsByteHexString()),
            School = wizard.MagicSchoolBehavior.MagicSchool.ToString(),
            Level = (uint) wizard.MagicSchoolBehavior.Level,
        };
    }

    private void Note(string text)
        => SendToSocket(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = text, Modal = 0 });

}

/// <summary>Tells a session's TeamUpService to open the Join a Team window (`.teamup`, the kiosk).</summary>
internal sealed class OpenTeamHelp : IServerMessage {

    public byte MessageOrder { get; } = 1;
    public byte ServiceID { get; } = 104;

}

/// <summary>Tells a session's TeamUpService to ask for a team at a sigil (`.teamup ask`).</summary>
internal sealed class AskTeamUp : IServerMessage {

    public byte MessageOrder { get; } = 1;
    public byte ServiceID { get; } = 104;

    public TeamUpSigils.Sigil Sigil { get; init; }
    public byte Size { get; init; } = 4;

}
