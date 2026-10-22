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
 * AGENT BRIDGE
 * ========================================================================
 *
 * PURPOSE:
 * A local HTTP endpoint that lets tools (the Imlight MCP server) list the players online and run
 * in-game commands as one of them, reading back what the server answered.
 *
 * NOTE:
 * Off unless [Agent Bridge] Enabled = true and a Token is set. It only listens on 127.0.0.1 and every
 * request needs the token, because a command runs with the full rights of the player it runs as.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Commands;

/// <summary>Asks a player's command service to run a command as that player.</summary>
internal sealed record AgentRunCommand(string Text, ConcurrentQueue<string> Output);

/// <summary>Asks a player's command service who it belongs to.</summary>
internal sealed record AgentDescribe(TaskCompletionSource<Dictionary<string, object>> Reply);

/// <summary>Asks a player's command service for the live objects behind its player.</summary>
internal sealed record AgentGrab(TaskCompletionSource<LivePlayer> Reply);

/// <summary>The live objects behind a logged-in player, for scripts run by the agent bridge.</summary>
public sealed record LivePlayer(
    string Name,
    Imlight.CoreLib.WizardData.Models.Player.Wizard Wizard,
    Imlight.CoreLib.WizardData.Models.Player.Account Account,
    Imcodec.ObjectProperty.TypeCache.CoreObject CoreObject,
    IActorRef Session,
    IActorRef Service);

/// <summary>Stands in for the player's session while a command runs: keeps the server messages, passes everything on.</summary>
internal sealed class AgentCaptureActor : ReceiveActor {

    public AgentCaptureActor(IActorRef session, ConcurrentQueue<string> output) {
        ReceiveAny(message => {
            if (message is EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE serverMessage) {
                output.Enqueue(serverMessage.Message.ToString() ?? "");
            }

            session.Forward(message);
        });
    }

}

public static class AgentBridge {

    private static readonly ConcurrentDictionary<IActorRef, byte> s_services = new();
    private static readonly ConcurrentDictionary<string, Func<JsonElement, Task<object>>> s_routes = new();
    private static HttpListener s_listener;

    /// <summary>The server's actor system, known once the first player's session starts or the Director hands it over.</summary>
    public static ActorSystem Actors { get; set; }

    /// <summary>When the bridge started; used for uptime.</summary>
    public static DateTime StartedUtc { get; } = DateTime.UtcNow;

    /// <summary>Adds an endpoint (POST, JSON body in, JSON out). The Director uses this for what needs heavier libraries.</summary>
    public static void MapRoute(string path, Func<JsonElement, Task<object>> handler) => s_routes[path] = handler;

    /// <summary>Gives the live objects of every player in the game.</summary>
    public static async Task<List<LivePlayer>> GetLivePlayers() {
        var grabs = s_services.Keys.Select(async service => {
            var source = new TaskCompletionSource<LivePlayer>();
            service.Tell(new AgentGrab(source));

            return await Task.WhenAny(source.Task, Task.Delay(2000)) == source.Task ? source.Task.Result : null;
        });

        return [.. (await Task.WhenAll(grabs)).Where(x => x is not null)];
    }
    private static string s_token;

    public static void Register(IActorRef commandService, ActorSystem system) {
        Actors ??= system;
        s_services[commandService] = 0;
    }

    public static void Unregister(IActorRef commandService) => s_services.TryRemove(commandService, out _);

    public static void StartIfEnabled() {
        if (!ConfigurationManager.GetValue("Agent Bridge.Enabled", false)) {
            return;
        }

        // The IMLIGHT_BRIDGE_TOKEN environment variable wins, so the token never has to sit in a file that may be shared.
        s_token = Environment.GetEnvironmentVariable("IMLIGHT_BRIDGE_TOKEN") is { Length: > 0 } fromEnvironment
            ? fromEnvironment
            : ConfigurationManager.GetValue("Agent Bridge.Token", "");
        if (string.IsNullOrWhiteSpace(s_token)) {
            Logger.Warning("The agent bridge is enabled but has no Token, so it stays off.");

            return;
        }

        var port = ConfigurationManager.GetValue("Agent Bridge.Port", 12400);
        try {
            s_listener = new HttpListener();
            s_listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            s_listener.Start();
        }
        catch (Exception ex) {
            Logger.Error("The agent bridge could not listen on port {0}: {1}", Logger.Args(port, ex.Message));

            return;
        }

        AgentJobs.Map();
        AgentCombat.Map();

        Logger.Information("The agent bridge is listening on 127.0.0.1:{0}.", Logger.Args(port));
        _ = Task.Run(AcceptLoop);
    }

    private static async Task AcceptLoop() {
        while (s_listener.IsListening) {
            HttpListenerContext context;
            try {
                context = await s_listener.GetContextAsync();
            }
            catch {
                return;
            }

            _ = Task.Run(() => Handle(context));
        }
    }

    private static async Task Handle(HttpListenerContext context) {
        object result;
        var status = 200;
        try {
            var supplied = context.Request.Headers["X-Agent-Token"] ?? "";
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(s_token))) {
                status = 401;
                result = new { error = "Bad or missing X-Agent-Token." };
            }
            else {
                result = await Route(context.Request);
            }
        }
        catch (Exception ex) {
            status = 500;
            result = new { error = ex.GetBaseException().Message };
        }

        try {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(result);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(bytes);
        }
        catch (Exception ex) {
            // The caller gave up waiting (or the result could not be written); nothing more to send.
            Logger.Debug("The agent bridge could not answer a request: {0}", Logger.Args(ex.GetBaseException().Message));
        }
        finally {
            try { context.Response.Close(); } catch { /* already closed */ }
        }
    }

    private static async Task<object> Route(HttpListenerRequest request) {
        var path = request.Url!.AbsolutePath.TrimEnd('/');
        if (s_routes.TryGetValue(path, out var extra)) {
            using var extraReader = new System.IO.StreamReader(request.InputStream, request.ContentEncoding);
            var extraText = await extraReader.ReadToEndAsync();
            using var extraBody = JsonDocument.Parse(string.IsNullOrWhiteSpace(extraText) ? "{}" : extraText);

            return await extra(extraBody.RootElement);
        }

        switch (path) {
            case "/info": {
                var process = System.Diagnostics.Process.GetCurrentProcess();
                ThreadPool.GetAvailableThreads(out var freeWorkers, out var freeIo);
                ThreadPool.GetMaxThreads(out var maxWorkers, out _);

                return new {
                    pid = process.Id,
                    uptimeSeconds = (long) (DateTime.UtcNow - StartedUtc).TotalSeconds,
                    workingSetMb = process.WorkingSet64 / 1048576,
                    gcHeapMb = GC.GetTotalMemory(false) / 1048576,
                    gcCollections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) },
                    threads = process.Threads.Count,
                    threadPoolBusyWorkers = maxWorkers - freeWorkers,
                    threadPoolPending = ThreadPool.PendingWorkItemCount,
                    players = s_services.Count,
                    actorSystem = Actors?.Name,
                    dotnet = Environment.Version.ToString(),
                    directory = AppDomain.CurrentDomain.BaseDirectory,
                };
            }
            case "/shutdown":
                _ = Task.Run(async () => {
                    await Task.Delay(500);
                    Logger.Information("The agent bridge was asked to shut the server down.");
                    // Let the actors stop (and save) first, but never wait on them for more than 10 seconds.
                    if (Actors is { } actors) {
                        await Task.WhenAny(actors.Terminate(), Task.Delay(10000));
                    }

                    Environment.Exit(0);
                });

                return new { ok = true, message = "The server is exiting." };
            case "/health":
                return new { ok = true, players = s_services.Count };
            case "/players":
                return new { players = await Describe() };
            case "/command":
                using (var reader = new System.IO.StreamReader(request.InputStream, request.ContentEncoding)) {
                    using var body = JsonDocument.Parse(await reader.ReadToEndAsync());
                    var root = body.RootElement;
                    var text = root.GetProperty("command").GetString() ?? "";
                    var player = root.TryGetProperty("player", out var p) ? p.GetString() : null;
                    var waitMs = root.TryGetProperty("waitMs", out var w) ? w.GetInt32() : 1500;

                    return await RunCommand(player, text.TrimStart('.', '/'), Math.Clamp(waitMs, 100, 15000));
                }
            default:
                return new { error = "Unknown endpoint. Use /health, /players or /command." };
        }
    }

    private static async Task<List<Dictionary<string, object>>> Describe() {
        var replies = s_services.Keys.Select(async service => {
            var source = new TaskCompletionSource<Dictionary<string, object>>();
            service.Tell(new AgentDescribe(source));
            var done = await Task.WhenAny(source.Task, Task.Delay(2000));

            return done == source.Task ? source.Task.Result : null;
        });

        return [.. (await Task.WhenAll(replies)).Where(x => x is not null)];
    }

    private static async Task<object> RunCommand(string player, string text, int waitMs) {
        var candidates = new List<(IActorRef Service, Dictionary<string, object> Info)>();
        foreach (var service in s_services.Keys) {
            var source = new TaskCompletionSource<Dictionary<string, object>>();
            service.Tell(new AgentDescribe(source));
            if (await Task.WhenAny(source.Task, Task.Delay(2000)) == source.Task) {
                candidates.Add((service, source.Task.Result));
            }
        }

        if (!string.IsNullOrWhiteSpace(player)) {
            candidates = [.. candidates.Where(c =>
                $"{c.Info["name"]}|{c.Info["account"]}".Contains(player, StringComparison.OrdinalIgnoreCase))];
        }

        if (candidates.Count == 0) {
            return new { error = "No matching player is in the game. Log a character in first (see /players)." };
        }

        if (candidates.Count > 1) {
            return new { error = "More than one player matches; give a more specific player.", players = candidates.Select(c => c.Info) };
        }

        var output = new ConcurrentQueue<string>();
        candidates[0].Service.Tell(new AgentRunCommand(text, output));

        // Wait until the replies stop coming, up to the limit.
        var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
        var lastCount = -1;
        var quietSince = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline) {
            await Task.Delay(100);
            if (output.Count != lastCount) {
                lastCount = output.Count;
                quietSince = DateTime.UtcNow;
            }
            else if (lastCount > 0 && DateTime.UtcNow - quietSince > TimeSpan.FromMilliseconds(400)) {
                break;
            }
        }

        return new { player = candidates[0].Info["name"], command = text, replies = output.ToArray() };
    }

}
