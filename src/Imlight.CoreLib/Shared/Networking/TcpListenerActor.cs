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
 * TCP LISTENER ACTOR
 * ========================================================================
 * 
 * PURPOSE:
 * Manages asynchronous TCP socket listening and new connection 
 * allocation for network servers, handling incoming socket connections.
 * 
 * USAGE EXAMPLE:
 * // TCP listener is typically created by a server instance
 * // Handles incoming network connection acceptance
 * 
 * NOTE:
 * 
 * TODO:
 * 
 * Created by: Jooty
 * Version: KALI 1.0
 * Last Updated: 10/10/2026
 */

using System;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Shared.Networking;

public class TcpListenerActor : ReceiveActor {

    public string Name { get; }
    public int Port { get; }
    public bool Listening { get; private set; }
    public TcpListener Listener { get; }

    private readonly CancellationTokenSource _tokenSource;
    private readonly IActorRef _serverRef;

    public TcpListenerActor(string name, int port, IActorRef serverRef, bool startListening = true) {
        this.Name = name;
        this.Port = port;
        this.Listener = new TcpListener(IPAddress.Parse("0.0.0.0"), port);
        this._tokenSource = CancellationTokenSource.CreateLinkedTokenSource(new CancellationToken());
        this._serverRef = serverRef;

        Receive<SERVER_100_PROTOCOL.MSG_STARTLISTENING>(_ => {
            Start();
            Sender.Tell(new SERVER_100_PROTOCOL.MSG_STARTLISTENING_COMPLETE());
        });

        if (startListening) {
            Start();
        }
    }

    public static Props Props(string name, int port, IActorRef serverRef, bool startListening = true) 
        => Akka.Actor.Props.Create(() => new TcpListenerActor(name, port, serverRef, startListening));

    public async void Start() {
        if (Listening) {
            return;
        }

        try {
            this.Listener.Start();
            this.Listening = true;

            Logger.Information("TcpListener for {Name} bound to port {Port} — listening.",
                Logger.Args(Name, Port));

            var token = this._tokenSource.Token;
            await ListenAsync(token);
        }
        catch (Exception ex) {
            Logger.Fatal("TcpListener for {Name} on port {Port} failed: {Exception}",
                Logger.Args(Name, Port, ex));
        }
    }

    public void Stop() {
        this.Listening = false;
        this._tokenSource.Cancel();
        Listener.Stop();

        Logger.Debug("TcpListener for {Name} on port {Port} stopped.",
            Logger.Args(Name, Port));
    }

    /// <summary>
    /// Asynchronously listen for incoming connections.
    /// </summary>
    protected async Task ListenAsync(CancellationToken token) {
        while (!token.IsCancellationRequested) {
            try {
                var socket = await Listener.AcceptSocketAsync(token);
                AllocateNewSocket(socket);
            }
            catch (OperationCanceledException) {
                // Normal shutdown — token was cancelled.
                break;
            }
            catch (ObjectDisposedException) {
                // Listener was disposed — normal during shutdown.
                break;
            }
            catch (Exception ex) {
                Logger.Error("TcpListener for {Name} on port {Port} accept error: {Exception}",
                    Logger.Args(Name, Port, ex));
            }
        }
    }

    private void AllocateNewSocket(Socket socket) {
        Logger.Debug("TcpListener for {Name} accepted connection from {RemoteEndPoint}.",
            Logger.Args(Name, socket.RemoteEndPoint?.ToString()));

        try {
            socket.NoDelay = true;
        } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) {
            Logger.Debug("TcpListener for {Name} could not set NoDelay on a socket. {Exception}",
                Logger.Args(Name, ex.Message));
        }

        // The game waits for the server to speak first; Ahziael's overlay speaks first (TLS). A short look tells them
        // apart, off the accept loop, so the overlay links on the same port the game plays on (no other port to open).
        _ = Task.Run(async () => {
            if (await IsOverlayAsync(socket)) {
                Logger.Debug("TcpListener for {Name}: an Ahziael overlay from {RemoteEndPoint}.", Logger.Args(Name, socket.RemoteEndPoint?.ToString()));
                Game.Tools.RemoteAdmin.RemoteAdminServer.ServeOverlayFromGamePort(socket);

                return;
            }

            _serverRef.Tell(new SERVER_100_PROTOCOL.MSG_ALLOCATESOCKET() { Socket = socket });
        });
    }

    // How long a new connection may stay quiet before it is taken as the game (which never speaks first).
    private static readonly TimeSpan s_overlayLook = TimeSpan.FromMilliseconds(150);

    /// <summary>True when the connection opened with a TLS hello (0x16 0x03): Ahziael's overlay, never the game.</summary>
    private static async Task<bool> IsOverlayAsync(Socket socket) {
        try {
            var until = DateTime.UtcNow + s_overlayLook;
            while (DateTime.UtcNow < until) {
                if (socket.Available >= 2) {
                    var first = new byte[2];
                    var read = socket.Receive(first, 0, 2, SocketFlags.Peek);

                    return read == 2 && first[0] == 0x16 && first[1] == 0x03;
                }

                await Task.Delay(15);
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) {
        }

        return false;
    }

}
