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
 * SERVER
 * ========================================================================
 * 
 * PURPOSE:
 * Provides a base implementation for network servers using Akka.NET 
 * actor model, managing session creation, connection handling, 
 * and server-level operations.
 * 
 * USAGE EXAMPLE:
 * // Inherit and implement a custom server
 * public class LoginServer : Server {
 *     public LoginServer(string name, int port) : base(name, port, factoryProps) { }
 * }
 * 
 * NOTE:
 * - Abstract base class for network server implementations
 * - Manages active sessions and TCP connections
 * - Provides unique session ID generation and connection handling
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 10/10/2026
 */

using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Structures;

namespace Imlight.CoreLib.Shared.Networking;

public abstract class Server : ReceiveProtocolDispatcher {

    public string Name { get; }
    public string Ip { get; }
    public int Port { get; }
    public virtual string RealmName { get; protected set; }

    protected readonly ObservableHashSet<SessionActor> ActiveSessions;

    public static readonly TimeSpan StartListeningTimeout = TimeSpan.FromSeconds(30);

    private readonly IActorRef _actorFactoryRef;
    private readonly IActorRef _tcpListenerRef;
    private readonly long _serverStartTime;
    private readonly Props _factoryProps;

    public Server(string name, int port, Props factoryProps, string ip = null, bool deferListening = false) {
        this.Name = name;
        this.Port = port;
        this.ActiveSessions = new ObservableHashSet<SessionActor>();
        this._serverStartTime = DateTimeOffset.Now.ToUnixTimeSeconds();
        this._factoryProps = factoryProps;

        // Use provided IP if set, otherwise auto-detect.
        if (!string.IsNullOrWhiteSpace(ip)) {
            this.Ip = ip;
        }
        else {
#if !DEBUG
            try {
                this.Ip = new HttpClient()
                    .GetStringAsync("https://api.ipify.org/")
                    .GetAwaiter()
                    .GetResult();
            }
            catch {
                // If the ipify call fails (offline, timeout, blocked), fall back
                // to loopback so the server can still start.
                Logger.Warning("Failed to resolve public IP from ipify.org — falling back to 127.0.0.1.");
                this.Ip = "127.0.0.1";
            }
#else
            this.Ip = "127.0.0.1";
#endif
        }

        _tcpListenerRef = CreateTcpListener(deferListening);
        _actorFactoryRef = CreateActorFactory();
    }

    [MessageHandler(typeof(SERVER_100_PROTOCOL.MSG_ALLOCATESOCKET))]
    protected virtual void ReceiveAllocateSocket(SERVER_100_PROTOCOL.MSG_ALLOCATESOCKET message) {
        // A connection that closes at once (the Manager checks every 2 s whether the server is up) can have its socket
        // gone before this runs: reading RemoteEndPoint then threw, which restarted the whole login server and took the
        // game servers with it, so the game could only reconnect in a loop (Niduus, 2026-10-23). Nothing here may throw.
        string remote;
        try {
            remote = message.Socket.RemoteEndPoint?.ToString();
        }
        catch (Exception) {
            remote = "(already closed)";
        }

        try {
            // Create a new child actor, which represents the active socket connection.
            var id = GetNewUniqueId();
            while (!Context.Child($"SessionActor.{id}").IsNobody()) {
                id = GetNewUniqueId();
            }

            var sessionProps = SessionActor.Props(message.Socket, id, Context.Self, _actorFactoryRef);
            Context.ActorOf(sessionProps, $"SessionActor.{id}");

            Logger.Debug("{Type} new connection from {RemoteEndPoint} given session ID {Id}",
                Logger.Args(GetType(), remote, id));
        }
        catch (Exception ex) {
            Logger.Error("{Name} could not take a connection from {RemoteEndPoint}: {Exception}", Logger.Args(Name, remote, ex));
        }
    }

    [MessageHandler(typeof(SERVER_100_PROTOCOL.MSG_DEALLOCATESOCKET))]
    protected virtual void ReceiveDeallocateSocket(SERVER_100_PROTOCOL.MSG_DEALLOCATESOCKET message) {
        if (!ActiveSessions.Remove(ActiveSessions.FirstOrDefault(x => x.SessionID == message.Id))) {
            // It's fine if no session was found. This is a common occurrence.
        }
        else {
            Logger.Information("{Name} lost connection to {Ip}", Logger.Args(Name, message.Ip, message.Id));
        }
    }

    [MessageHandler(typeof(SERVER_100_PROTOCOL.MSG_STARTLISTENING))]
    protected void ReceiveStartListening(SERVER_100_PROTOCOL.MSG_STARTLISTENING message) {
        StartListeningAsync(message).PipeTo(Sender);
    }

    [MessageHandler(typeof(SERVER_100_PROTOCOL.MSG_QUERYACTORFACTORY))]
    protected void ReceiveQueryActorFactory(SERVER_100_PROTOCOL.MSG_QUERYACTORFACTORY message) {
        var reply = new SERVER_100_PROTOCOL.MSG_ACTORFACTORYINFO() {
            Reference = _actorFactoryRef
        };

        Sender.Tell(reply);
    }

    [MessageHandler(typeof(SERVER_100_PROTOCOL.MSG_QUERYSERVER))]
    protected void ReceiveQueryServer(SERVER_100_PROTOCOL.MSG_QUERYSERVER message) {
        // Get a list of strings for the connected IPs.
        var ips = ActiveSessions.Select(x => x.RemoteIp).ToArray();
        var msg = new SERVER_100_PROTOCOL.MSG_SERVERINFO() {
            IP = this.Ip,
            Port = Port,
            PlayerCount = (ushort) ActiveSessions.Count,
            ActorRef = Context.Self,
            ConnectedIps = ips,
            RealmName = RealmName
        };

        Sender.Tell(msg);
    }

    protected override SupervisorStrategy SupervisorStrategy() => new ServerSupervisorStrategy(Name);

    /// <summary>
    /// Decides what happens to a child of the server that failed.
    /// </summary>
    private sealed class ServerSupervisorStrategy : OneForOneStrategy {

        private readonly string _serverName;

        public ServerSupervisorStrategy(string serverName)
            : base(maxNrOfRetries: 1, withinTimeRange: TimeSpan.FromSeconds(30), localOnlyDecider: _ => Directive.Stop) {
            _serverName = serverName;

            // Handle logs each failure itself.
            LoggingEnabled = false;
        }

        protected override Directive Handle(IActorRef child, Exception exception) {
            // There is no attempting to stabilize the connection server side. The client will attempt to
            // reconnect on any given failure. This is a good thing, as it allows us to simply stop the session actor
            // and let the client handle the rest.
            if (child.Path.Name.StartsWith("SessionActor", StringComparison.Ordinal)) {
                // Client regularly shuts down the socket. No need to log it.
                if (exception.Message?.Contains("shutdown", StringComparison.OrdinalIgnoreCase) != true) {
                    Logger.Error("{Child} has failed with exception {Exception}", Logger.Args(child.Path.Name, exception));
                }

                return Directive.Stop;
            }

            switch (exception) {
                case ActorKilledException or DeathPactException:
                    return Directive.Stop;
                case ActorInitializationException:
                    // There is nothing to keep from a child that could not even start.
                    Logger.Error("{Server}: {Child} could not start and starts again: {Exception}",
                        Logger.Args(_serverName, child.Path.Name, exception));

                    return Directive.Restart;
            }

            // The game world, the command dispatcher and the listener hold every player's zone and connection. This
            // used to read ex.InnerException.Source, which threw for an error without an inner one: the whole game
            // server then crashed and restarted, dropped everyone and forgot their login keys, so the game could not
            // reconnect (Niduus at the Unicorn Way gate, 2026-10-25). Now the one message that failed is skipped and
            // the rest keeps running.
            Logger.Error("{Server}: {Child} failed and skipped the message it was on: {Exception}",
                Logger.Args(_serverName, child.Path.Name, exception));

            return Directive.Resume;
        }

    }

    protected virtual ushort GetNewUniqueId() {
        ushort newId = 0;
        var isUniqueId = false;
        var random = new Random();

        while (!isUniqueId) {
            newId = (ushort) random.Next(ushort.MaxValue);

            if (ActiveSessions.All(s => s.SessionID != newId)) {
                isUniqueId = true;
            }
        }

        return newId;
    }

    /// <summary>
    /// Starts this server's TCP listener. Servers that own other servers also start those.
    /// </summary>
    protected virtual Task<SERVER_100_PROTOCOL.MSG_STARTLISTENING_COMPLETE> StartListeningAsync(
        SERVER_100_PROTOCOL.MSG_STARTLISTENING message)
        => _tcpListenerRef.Ask<SERVER_100_PROTOCOL.MSG_STARTLISTENING_COMPLETE>(message, StartListeningTimeout);

    private IActorRef CreateTcpListener(bool deferListening) {
        var actorName = $"{Name}.TcpListener.{Port}";
        var tcpProps = TcpListenerActor.Props(Name, Port, Context.Self, !deferListening);
        var tcpListenerRef = Context.ActorOf(tcpProps, actorName);

        Logger.Verbose("New actor created under {Path}: {ActorName}", Logger.Args(Context.Self.Path, actorName));

        return tcpListenerRef;
    }

    private IActorRef CreateActorFactory() {
        if (_factoryProps is null) {
            return null;
        }

        var actorName = $"{Name}.ActorFactory";

        Logger.Verbose("New actor created under {Path}: {ActorName}", Logger.Args(Context.Self.Path, actorName));

        return Context.ActorOf(_factoryProps, actorName);
    }

}
