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
 * COMMAND SERVICE
 * ========================================================================
 * 
 * PURPOSE:
 * Manages in-game command processing and player selection mechanisms 
 * for administrative and interactive game functions.
 * 
 * USAGE EXAMPLE:
 * Internal service handling command routing and player context 
 * management within the game server session.
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 3/18/2025
 */

using System;
using System.Linq;
using Imlight.Common;
using Akka.Actor;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Game.Commands;
using Imlight.CoreLib.WizardData.Implementations;
using Imlight.CoreLib.WizardData.Models.Player;
using Imcodec.MessageLayer.Generated;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Services;

internal class CommandService(SessionActor sessionActor) : MessageService(sessionActor) {

    private readonly IActorRef _dispatcherRef = CommandDispatcher.Instance;

    private Wizard _selectedWizard;
    private Account _selectedAccount;

    protected static Props Props(SessionActor parentActor)
        => Akka.Actor.Props.Create(() => new CommandService(parentActor));

    protected override void PreStart() {
        AgentBridge.Register(Self, Context.System);
        base.PreStart();
    }

    protected override void PostStop() {
        AgentBridge.Unregister(Self);
        base.PostStop();
    }

    [InternalMessageHandler(typeof(AgentDescribe))]
    private void ReceiveAgentDescribe(AgentDescribe message) {
        var wizard = GetActiveWizard();
        if (wizard is null || GetActiveGameObject() is null) {
            message.Reply.TrySetResult(null);
            return;
        }

        message.Reply.TrySetResult(new System.Collections.Generic.Dictionary<string, object> {
            ["name"] = wizard.PlayerNameBehavior?.GetWizardName() ?? "",
            ["charId"] = wizard.CharId.ToString(),
            ["account"] = GetActiveAccount()?.Username ?? "",
            ["zone"] = wizard.Zone ?? "",
            ["level"] = wizard.MagicSchoolBehavior?.Level ?? 0,
        });
    }

    [InternalMessageHandler(typeof(AgentGrab))]
    private void ReceiveAgentGrab(AgentGrab message) {
        var wizard = GetActiveWizard();
        var coreObject = GetActiveGameObject();
        if (wizard is null || coreObject is null) {
            message.Reply.TrySetResult(null);
            return;
        }

        message.Reply.TrySetResult(new LivePlayer(wizard.PlayerNameBehavior?.GetWizardName() ?? "", wizard,
            GetActiveAccount(), coreObject, SessionActor.ActorRef, Self));
    }

    [InternalMessageHandler(typeof(AgentRunCommand))]
    private void ReceiveAgentRunCommand(AgentRunCommand message) {
        if (GetActiveWizard() is null || GetActiveGameObject() is null || GetActiveAccount() is null) {
            message.Output.Enqueue("[bridge] That player is not in the game.");
            return;
        }

        var capture = Context.ActorOf(Akka.Actor.Props.Create(() => new AgentCaptureActor(SessionActor.ActorRef, message.Output)));
        Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(30), capture, PoisonPill.Instance, Self);

        _dispatcherRef.Tell(new SERVER_100_PROTOCOL.MSG_COMMAND() {
            CommandText = message.Text,
            ActorRef = capture,
            CoreObject = GetActiveGameObject(),
            Wizard = GetActiveWizard(),
            Account = GetActiveAccount(),
            ZoneActor = SessionActor.GetZoneActor(),
            ServerActor = SessionActor.ServerRef,
            SelectedWizard = _selectedWizard,
            SelectedAccount = _selectedAccount
        });
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_COMMAND))]
    private void ReceiveCommand(GAME_5_PROTOCOL.MSG_COMMAND message) {
        var coreObject = GetActiveGameObject();
        var wizard = GetActiveWizard();
        var account = GetActiveAccount();

        _dispatcherRef.Tell(new SERVER_100_PROTOCOL.MSG_COMMAND() {
            CommandText = message.Command,
            ActorRef = SessionActor.ActorRef,
            CoreObject = coreObject,
            Wizard = wizard,
            Account = account,
            ZoneActor = SessionActor.GetZoneActor(),
            ServerActor = SessionActor.ServerRef,
            SelectedWizard = _selectedWizard,
            SelectedAccount = _selectedAccount
        });
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> s_thisPc = new();

    /// <summary>Whether a session's game runs on the PC the server runs on (remembered per address).</summary>
    internal static bool IsThisPcCached(string remoteIp) => s_thisPc.GetOrAdd(remoteIp ?? "", IsThisPc);

    private static bool IsThisPc(string remoteIp) {
        // RemoteIp may carry a port or an IPv4-mapped IPv6 form.
        var text = remoteIp ?? "";
        if (!System.Net.IPAddress.TryParse(text, out var address)) {
            var colon = text.LastIndexOf(':');
            if (colon <= 0 || !System.Net.IPAddress.TryParse(text[..colon].Trim('[', ']'), out address)) {
                return false;
            }
        }

        if (address.IsIPv4MappedToIPv6) {
            address = address.MapToIPv4();
        }

        if (System.Net.IPAddress.IsLoopback(address)) {
            return true;
        }

        try {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Any(unicast => unicast.Address.Equals(address));
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) {
            return false;
        }
    }

    [MessageHandler(typeof(SERVICE_101_PROTOCOL.MSG_MANAGERCOMMAND))]
    private void ReceiveManagerCommand(SERVICE_101_PROTOCOL.MSG_MANAGERCOMMAND message) {
        if (GetActiveWizard() is null || GetActiveGameObject() is null || GetActiveAccount() is null) {
            return;
        }

        // Only the game on the server's own PC runs commands typed in the Imlight app. That game may connect
        // through the PC's network address (the one given out for friends), not only 127.0.0.1.
        if (!IsThisPc(SessionActor.RemoteIp)) {
            Logger.Information("Imlight app command not run for {0}: the game at {1} is not on this PC.",
                Logger.Args(GetActiveWizard().PlayerNameBehavior?.GetWizardName(), SessionActor.RemoteIp));

            return;
        }

        Logger.Information("Running the Imlight app command \"{0}\" for {1}.",
            Logger.Args(message.Command, GetActiveWizard().PlayerNameBehavior?.GetWizardName()));

        _dispatcherRef.Tell(new SERVER_100_PROTOCOL.MSG_COMMAND() {
            CommandText = message.Command,
            ActorRef = SessionActor.ActorRef,
            CoreObject = GetActiveGameObject(),
            Wizard = GetActiveWizard(),
            Account = GetActiveAccount(),
            ZoneActor = SessionActor.GetZoneActor(),
            ServerActor = SessionActor.ServerRef,
            SelectedWizard = _selectedWizard,
            SelectedAccount = _selectedAccount
        });
    }

    [InternalMessageHandler(typeof(RemoteAdminCommand))]
    private void ReceiveRemoteAdminCommand(RemoteAdminCommand message) {
        var wizard = GetActiveWizard();
        var account = GetActiveAccount();
        if (wizard is null || GetActiveGameObject() is null || account is null || account.AccountId != message.AccountId
            || message.WizardId is { } only && wizard.CharId != only) {
            return;
        }

        // Remote Admin let in only an Administrator, but the rank may have changed since.
        if (account.AuthLevel < AuthLevel.Administrator) {
            return;
        }

        var name = wizard.PlayerNameBehavior?.GetWizardName() ?? "";
        Logger.Information("Running the remote admin command \"{0}\" for {1}.", Logger.Args(message.Text, name));
        message.Ran(name);
        _dispatcherRef.Tell(new SERVER_100_PROTOCOL.MSG_COMMAND() {
            CommandText = message.Text,
            ActorRef = SessionActor.ActorRef,
            CoreObject = GetActiveGameObject(),
            Wizard = wizard,
            Account = account,
            ZoneActor = SessionActor.GetZoneActor(),
            ServerActor = SessionActor.ServerRef,
            SelectedWizard = _selectedWizard,
            SelectedAccount = _selectedAccount
        });
    }

    [MessageHandler(typeof(GAME_5_PROTOCOL.MSG_BUDDYSTATS))]
    private void ReceivePlayerSelect(GAME_5_PROTOCOL.MSG_BUDDYSTATS message) {
        // We only care about the ID sent here. It's the ID of the core object, but Imlight serialized
        // it using the character ID.
        var id = message.BuddyID;

        var persistentWizard = WizardCollection.GetCharacterUnloaded(id);
        if (persistentWizard is null) {
            return;
        }

        var account = AccountCollection.GetAccount(persistentWizard.AccountId);
        if (account is null) {
            return;
        }

        // Cache for our next command.
        _selectedWizard = persistentWizard;
        _selectedAccount = account;
    }

}
