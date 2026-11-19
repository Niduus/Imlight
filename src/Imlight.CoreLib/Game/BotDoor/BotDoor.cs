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
 */

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Commands;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game;

/// <summary>
/// The doorway between the server and the bots (Ultimus, the TCO7, the Quest Team, the Bot Players and their shared brain).
/// The bots live in the repository's bots folder and are built apart from the server (bots/Imlight.Bots.csproj), so a bot
/// update needs no new Ahziael (Niduus, 2026-11-19: "Make the bots more update-able ... have them soft reset only
/// themselves"). The server never names a bot type: it only calls <see cref="Side"/>, which the bots fill in when they are
/// loaded (Imlight.Director/BotLoader.cs) and which is empty while they are being swapped, so every call does nothing then.
/// </summary>
internal static class BotDoor {

    private static volatile BotSide s_side;

    /// <summary>The bots now running, or null while none are loaded (being swapped, or they failed to build).</summary>
    internal static BotSide Side => s_side;

    /// <summary>Which bots run: "bots-v12" for a published bot update, "built in" for the ones made with the server.</summary>
    internal static string Version { get; private set; } = "";

    /// <summary>How many times bots were put in (each swap is one more), so a battle can tell a stale bot state apart.</summary>
    internal static int Generation { get; private set; }

    // What the server told the bots so far, told again to bots put in later (a swap): they start where the old ones were.
    // Each realm is a game server of its own, so each one's start is kept and told again in order.
    private static readonly List<ActorSystem> s_started = [];
    private static readonly List<(IActorRef Server, ActorSystem System, string Realm)> s_ready = [];
    private static bool s_watching;
    private static readonly object s_lock = new();

    /// <summary>The game server starts (GameServer.PreStart).</summary>
    internal static void ServerStarted(ActorSystem system) {
        lock (s_lock) {
            s_started.Add(system);
            Side?.Start(system);
        }
    }

    /// <summary>The game server is ready for sessions.</summary>
    internal static void ServerReady(IActorRef server, ActorSystem system, string realmName) {
        lock (s_lock) {
            s_ready.Add((server, system, realmName));
            Side?.GameServerReady(server, system, realmName);
        }
    }

    /// <summary>The server is up and the bots' mini servers may be looked after.</summary>
    internal static void StartWatching() {
        lock (s_lock) {
            s_watching = true;
            Side?.StartWatching();
        }
    }

    /// <summary>Puts bots in (called by the bot loader only), and tells them what the server already told the old ones.</summary>
    internal static void Install(BotSide side, string version) {
        lock (s_lock) {
            Version = version ?? "";
            Generation++;
            s_side = side;
            foreach (var system in s_started) {
                side.Start(system);
            }

            foreach (var ready in s_ready) {
                side.GameServerReady(ready.Server, ready.System, ready.Realm);
            }

            if (s_watching) {
                side.StartWatching();
            }
        }
    }

    /// <summary>Takes the bots out, so nothing calls them while they are swapped. Called by the bot loader only.</summary>
    internal static BotSide Remove() {
        lock (s_lock) {
            var side = s_side;
            s_side = null;

            return side;
        }
    }

}

/// <summary>A message to a battle (or another of the server's actors) that only the bots understand; the battle hands it on.</summary>
internal sealed record BotDoorMessage(object Payload, int Generation);

/// <summary>A teleporter pad the bots know, for the overlay's quest line (OverlayQuestPath).</summary>
internal sealed record BotPadHop(float X, float Y, float ToX, float ToY, object Pad);

/// <summary>
/// What the server asks of the bots. Every call is a hook: the server says what happened or asks a question, and the bots
/// answer. A new hook needs a new server (Ahziael update); what the bots do inside one does not.
/// </summary>
internal abstract class BotSide {

    // ---------------------------------------------------------------- start and stop

    /// <summary>The game server starts (GameServer.PreStart): the Bot Players' bodies get their actor system.</summary>
    internal virtual void Start(ActorSystem system) { }

    /// <summary>The game server is ready for sessions (the Quest Team opens its sessions on it).</summary>
    internal virtual void GameServerReady(IActorRef server, ActorSystem system, string realmName) { }

    /// <summary>The server is up: the bots' mini servers start being looked after (on while their switches are on).</summary>
    internal virtual void StartWatching() { }

    /// <summary>
    /// This program was started as one of the bots' mini servers (--botplayers, --questteam or --ultimus): runs it. False
    /// when the mode is not the bots'.
    /// </summary>
    internal virtual bool RunMiniServer(string[] args) => false;

    /// <summary>
    /// The bots stop for a swap (a bot update): every bot saves what it must and logs out, every loop and timer of theirs
    /// ends, and their mini servers are closed. Players and the server stay up. Waits at most the given time.
    /// </summary>
    internal virtual Task StopAsync(string why, TimeSpan most) => Task.CompletedTask;

    /// <summary>
    /// Why the bots should not be swapped right now (one of them is in a battle), or null when they may be. A swap waits for
    /// this, up to a while, so no fight is left half done.
    /// </summary>
    internal virtual string BusyWhy() => null;

    /// <summary>Called with Ahziael's command poll (about once a second).</summary>
    internal virtual void Poll() { }

    // ---------------------------------------------------------------- who is who

    /// <summary>Whether a character is one of the server's own named wizards (Ultimus, a TCO7 member or the Quest Team).</summary>
    internal virtual bool IsRosterWizard(ulong charId) => false;

    /// <summary>The commands the bots bring (.bot, .ultimus, .qt, ...).</summary>
    internal virtual IEnumerable<CommandProtocol> Commands() => [];

    // ---------------------------------------------------------------- what the bots hear and see

    /// <summary>A player typed in area chat near (x, y).</summary>
    internal virtual void HeardTyped(IActorRef zone, float x, float y, Wizard speaker, string typed) { }

    /// <summary>A player said a Menu Chat phrase (or typed words sent as one) near (x, y).</summary>
    internal virtual void HeardMenuChat(IActorRef zone, float x, float y, Wizard speaker, uint phraseId, string text, bool typed) { }

    /// <summary>A player whispered to someone (it may be a bot).</summary>
    internal virtual void HeardWhisper(ulong targetCharId, Wizard sender, uint phraseId, string text, bool typed) { }

    /// <summary>A player said something in group chat.</summary>
    internal virtual void HeardGroup(Wizard speaker, string text) { }

    /// <summary>A player played an emote from the wheel.</summary>
    internal virtual void SawEmote(ulong charId, string animation) { }

    /// <summary>Whether a wizard turns down every pet hatching request (the server's own wizards do).</summary>
    internal virtual bool RefusesHatching(Wizard wizard) => false;

    // ---------------------------------------------------------------- areas, doors and maps

    /// <summary>A player came into an area: the bots walking there show themselves.</summary>
    internal virtual void PlayerEnteredArea(IActorRef zone, IActorRef player, string zonePath, bool instance) { }

    /// <summary>A player left an area.</summary>
    internal virtual void PlayerLeftArea(IActorRef zone, IActorRef player) { }

    /// <summary>Whether a shut door's needs name quests the bots should learn about.</summary>
    internal virtual bool GateNamesQuests(Trigger trigger) => false;

    /// <summary>A door stayed shut for a wizard: the bots learn which quests open it.</summary>
    internal virtual void GateRefused(Wizard wizard, string zonePath, Trigger trigger) { }

    /// <summary>The pads in the area now (live doors), for the bots' teleporter book.</summary>
    internal virtual void NoteDoors(string zonePath, IEnumerable<MinimapIndex.Mark> live) { }

    /// <summary>A teleporter pad toward (toX, toY) from (x, y), not one of skip (the bots' teleporter book).</summary>
    internal virtual BotPadHop PadToward(string zonePath, float x, float y, float toX, float toY, IReadOnlyCollection<BotPadHop> skip = null) => null;

    /// <summary>Any pad here that lands nearer (toX, toY), as a player would try one.</summary>
    internal virtual BotPadHop PadAnother(string zonePath, float x, float y, float toX, float toY, IReadOnlyCollection<BotPadHop> skip) => null;

    /// <summary>A pad that gets to (toX, toY) much faster than walking.</summary>
    internal virtual BotPadHop PadFaster(string zonePath, float x, float y, float toX, float toY) => null;

    /// <summary>The most pads one route takes.</summary>
    internal virtual int MostPads => 0;

    // ---------------------------------------------------------------- the overlay's spectators

    /// <summary>The overlay's spectators of the server's own wizards and Spectate Anyone (op "ultimus").</summary>
    internal virtual Task<JsonObject> SpectateAsync(long link, JsonObject header, string address)
        => Task.FromResult(RemoteAdmin.RemoteAdminWire.Error("The bots are not loaded on this server."));

    /// <summary>Keeps the bot whose character this is as the one the calls inside are about (Ultimus or a TCO7 member).</summary>
    internal virtual IDisposable UseSeatOf(ulong charId) => null;

    /// <summary>The quest way the spectator draws for a wizard: the picked quest's when routeQuest is held, else its own.</summary>
    internal virtual JsonObject SpectateRoute(Wizard wizard, IActorRef zone, string quest, string routeQuest, bool team, out bool picked) {
        picked = false;

        return null;
    }

    // ---------------------------------------------------------------- battles

    /// <summary>A battle's planning begins: the bots plan (Bot Players, the Quest Team, Ultimus and the TCO7).</summary>
    internal virtual void PlanningBegins(CombatDuelComponent duel) { }

    /// <summary>Someone in the battle picked a card or changed their mind.</summary>
    internal virtual void NoticeMove(CombatDuelComponent duel, CombatDuelSubCircle caster) { }

    /// <summary>A wizard stepped into the battle while planning runs, with this many seconds left.</summary>
    internal virtual void JoinedPlanning(CombatDuelComponent duel, CombatDuelSubCircle circle, double secondsLeft) { }

    /// <summary>Whether this battle turns a joining wizard away (the Quest Team and Ultimus keep apart from others).</summary>
    internal virtual bool RefusesJoiner(CombatDuelComponent duel, CoreObject joiner) => false;

    /// <summary>The circle a joining wizard should take (a Quest Team member's own), or null for the usual one.</summary>
    internal virtual CombatDuelSubCircle OwnCircle(CombatDuelComponent duel, CoreObject joiner) => null;

    /// <summary>A new battle starts on this sigil (the bots forget what they knew of the last one there).</summary>
    internal virtual void DuelStarts(CombatDuelComponent duel) { }

    /// <summary>A message for the battle that only the bots understand (BotDoorMessage).</summary>
    internal virtual void DuelMessage(CombatDuelComponent duel, object payload) { }

    /// <summary>A move is about to be played: what the bots want to watch of it (BotKnowledge), handed back to <see cref="AfterMove"/>.</summary>
    internal virtual object BeforeMove(QueuedCombatAction action, IEnumerable<CombatDuelSubCircle> circles) => null;

    /// <summary>The move was played.</summary>
    internal virtual void AfterMove(QueuedCombatAction action, object watched, ulong duelId, int round) { }

}
