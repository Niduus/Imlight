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
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.BotPlayers;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Commands;
using Imlight.CoreLib.Game.QuestTeam;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Ultimus;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game;

/// <summary>
/// The bots' side of the doorway (BotDoor): what the server asks, answered by Ultimus, the TCO7, the Quest Team and the Bot
/// Players. The server's bot loader (Imlight.Director/BotLoader.cs) finds this class in the bots' build and puts it in.
/// </summary>
internal sealed class BotsSide : BotSide {

    // ---------------------------------------------------------------- start and stop

    internal override void Start(ActorSystem system) {
        // The bots only ever play on Niduus's own Imlight server: anywhere else nothing of them starts (BotHomeGuard).
        if (!BotShared.BotHomeGuard.Home) {
            return;
        }

        // The kill switch (bots-killswitch.txt in Niduus/Imlight) beats everything: on means no bot plays.
        if (!BotShared.BotKillSwitch.AllowsPlay()) {
            return;
        }

        BotShared.BotKillSwitch.StartWatching();

        // The signed Hive Mind file is checked and its result logged (stage 1: nothing about play changes yet).
        _ = BotShared.BotHive.Valid;

        // The Ahziael signal is listened for and logged (phase 1: nothing about play changes yet).
        BotShared.BotAhziaelSignal.StartListening();

        // Zoom's and True Form's leftovers in the shared books go first (Hive Mind audit, 2026-11-22).
        BotShared.BotMistakes.ForgetKeptApart();
        BotBodies.Start(system);
    }

    internal override void GameServerReady(IActorRef server, ActorSystem system, string realmName) {
        if (!BotShared.BotHomeGuard.Home || BotShared.BotKillSwitch.On) {
            return;
        }

        QuestTeamHost.GameServerReady(server, system, realmName);
    }

    internal override void StartWatching() {
        if (!BotShared.BotHomeGuard.Home || BotShared.BotKillSwitch.On) {
            return;
        }

        // Bot Players: their mini server runs only while [BotPlayers] Enabled is on, and is stopped when it is turned off.
        BotLink.StartWatching();
        // The Quest Team: its mini server and its four wizards run only while [QuestTeam] Enabled is on.
        QuestTeamLink.StartWatching();
        // Ultimus and the TCO7: their mini servers and wizards run only while [Ultimus] Enabled is on.
        UltimusLink.StartWatching();
    }

    internal override bool RunMiniServer(string[] args) {
        // A bots mini server outside Imlight's own server does nothing and ends (it still counts as handled).
        if (Array.Exists(args, a => a is "--bots" or "--questteam" or "--ultimus") && (!BotShared.BotHomeGuard.Home || !BotShared.BotKillSwitch.AllowsPlay())) {
            return true;
        }

        if (Array.IndexOf(args, "--bots") >= 0) {
            BotBrain.Run(args);

            return true;
        }

        if (Array.IndexOf(args, "--questteam") >= 0) {
            QuestTeamBrain.Run(args);

            return true;
        }

        if (Array.IndexOf(args, "--ultimus") >= 0) {
            UltimusBrain.Run(args);

            return true;
        }

        return false;
    }

    internal override async Task StopAsync(string why, TimeSpan most) {
        // Nothing of these bots starts again: their watch loops end (each loop finishes the look it is in, up to 3 s).
        BotLife.End();
        await Task.Delay(TimeSpan.FromSeconds(4));

        BotLink.StopForSwap(why);
        QuestTeamLink.StopForSwap(why);
        UltimusLink.StopForSwap(why);

        // Every bot wizard logs out (Ultimus's and the team's write their end reports first), then their directors stop.
        var until = DateTime.UtcNow + most;
        while (DateTime.UtcNow < until && (UltimusDirector.AnyPlaying || QuestTeamDirector.Members.Count > 0 || BotBodies.Count > 0)) {
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        await Task.Delay(TimeSpan.FromSeconds(3));
    }

    /// <summary>A bot update waits while any bot wizard (Ultimus, TCO7, Quest Team or a street bot) is in a battle,
    /// so nobody's fight is cut off by the swap.</summary>
    internal override string BusyWhy() {
        var bodies = new HashSet<ulong>(BotBodies.All.Select(o => o.GlobalId));
        var fighting = 0;
        foreach (var (gid, claim) in CombatDuelComponent.s_playerBattles) {
            if (!claim.Battle.HoldsPlayer(gid)) {
                continue;
            }

            if (bodies.Contains(gid) || (gid > 2 && IsRosterWizard(gid - 2))) {
                fighting++;
            }
        }

        return fighting == 0 ? null : $"{fighting} bot wizard{(fighting == 1 ? " is" : "s are")} in a battle";
    }

    internal override void Poll() {
        UltimusAiTrickTest.Poll();
        QuestTeam.QuestTeamAiCompare.Poll();
    }

    // ---------------------------------------------------------------- who is who

    internal override bool IsRosterWizard(ulong charId) => UltimusRoster.IsMember(charId) || QuestTeamRoster.IsMember(charId);

    internal override IEnumerable<CommandProtocol> Commands()
        => typeof(BotsSide).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(CommandProtocol)) && !t.IsAbstract)
            .Select(t => Activator.CreateInstance(t) as CommandProtocol)
            .Where(p => p is not null);

    // ---------------------------------------------------------------- what the bots hear and see

    internal override void HeardTyped(IActorRef zone, float x, float y, Wizard speaker, string typed) {
        // Ultimus hears typed chat near him too (Niduus, 2026-11-14: he is free to use text chat, and answers it).
        // The TCO7 hear players the same way (their talk with each other is UltimusTco7's).
        if (UltimusDirector.AnyPlaying) {
            UltimusVoice.PlayerSaid(zone, x, y, speaker.CharId, speaker.PlayerNameBehavior?.GetWizardName(), typed, typed: true);
        }

        // The Quest Team hears typed chat near it too (Niduus, 2026-11-15: "Allow for QT to have AI chats like Ultimus"). It
        // still never takes a player's orders; Ultimus and the team never hear each other.
        if (QuestTeamDirector.ActorOf("Ice") is not null && !UltimusRoster.IsMember(speaker.CharId) && !QuestTeamRoster.IsMember(speaker.CharId)) {
            QuestTeamVoice.PlayerSaid(zone, x, y, speaker.CharId, speaker.PlayerNameBehavior?.GetWizardName(), 0, typed, typed: true);
        }
    }

    // The Quest Team hears Menu Chat said near it and may answer (it never takes a player's orders).
    // Ultimus hears it too (it welcomes players into its battle, never takes their orders). Ultimus and the Quest Team
    // never hear each other: they keep apart.
    // typed: the player wrote the words (the game's word-list chat sends typed words this way), not a Menu Chat phrase.
    internal override void HeardMenuChat(IActorRef zone, float x, float y, Wizard speaker, uint phraseId, string text, bool typed) {
        var team = QuestTeamDirector.ActorOf("Ice") is not null;
        var ultimus = UltimusDirector.AnyPlaying;
        if (!team && !ultimus) {
            return;
        }

        text ??= MenuChatBook.ById(phraseId)?.Text;
        if (string.IsNullOrEmpty(text)) {
            return;
        }

        if (ultimus) {
            UltimusVoice.PlayerSaid(zone, x, y, speaker.CharId, speaker.PlayerNameBehavior?.GetWizardName(), text, typed: typed);
        }

        if (!team || UltimusRoster.IsMember(speaker.CharId)) {
            return;
        }

        QuestTeamVoice.PlayerSaid(zone, x, y, speaker.CharId, speaker.PlayerNameBehavior?.GetWizardName(), phraseId, text, typed);
    }

    // A player whispered to Ultimus: it may answer in Menu Chat, whispered back (Niduus, 2026-11-13: "Able to reply to
    // Whispers via Menu Chat (in context)").
    internal override void HeardWhisper(ulong targetCharId, Wizard sender, uint phraseId, string text, bool typed) {
        // A whisper to a Quest Team wizard: that wizard may answer, whispered back (Niduus, 2026-11-15, the team's AI chat).
        if (sender is not null && QuestTeamRoster.IsMember(targetCharId) && !QuestTeamRoster.IsMember(sender.CharId)
            && !UltimusRoster.IsMember(sender.CharId)) {
            var heard = text ?? (phraseId == 0 ? null : MenuChatBook.ById(phraseId)?.Text);
            if (!string.IsNullOrWhiteSpace(heard)) {
                QuestTeamVoice.PlayerWhispered(targetCharId, sender.CharId, sender.PlayerNameBehavior?.GetWizardName(), phraseId, heard, typed);
            }

            return;
        }

        if (sender is null || !UltimusRoster.IsMember(targetCharId) || UltimusRoster.IsMember(sender.CharId)
            || QuestTeamRoster.IsMember(sender.CharId)) {
            return;
        }

        text ??= phraseId == 0 ? null : MenuChatBook.ById(phraseId)?.Text;
        if (!string.IsNullOrWhiteSpace(text)) {
            UltimusVoice.PlayerWhispered(sender.CharId, sender.PlayerNameBehavior?.GetWizardName(), text, typed, targetCharId);
        }
    }

    // Ultimus hears his group's chat, in battle too (Niduus, 2026-11-15: "acknowledge group members in battle talking").
    internal override void HeardGroup(Wizard speaker, string text) {
        if (UltimusDirector.AnyPlaying) {
            UltimusVoice.GroupSaid(speaker.CharId, speaker.PlayerNameBehavior?.GetWizardName(), text);
        }
    }

    // Ultimus learns the wheel's animation names from real players (Ultimus/UltimusEmotes.cs).
    internal override void SawEmote(ulong charId, string animation) => UltimusEmotes.Seen(charId, animation);

    // Server-played wizards never hatch (Niduus, 2026-11-13: "Have Ultimus also reject Hatching Requests").
    internal override bool RefusesHatching(Wizard wizard) {
        if (!UltimusRoster.IsMember(wizard.CharId) && !QuestTeamRoster.IsMember(wizard.CharId)) {
            return false;
        }

        if (UltimusSeat.OfCharacter(wizard.CharId) is { } seat) {
            using (UltimusSeat.Use(seat)) {
                UltimusLink.Note(UltimusRoster.NAME, "turns down a pet hatching request (it does not hatch pets).");
            }
        }

        return true;
    }

    // ---------------------------------------------------------------- areas, doors and maps

    internal override void PlayerEnteredArea(IActorRef zone, IActorRef player, string zonePath, bool instance)
        => BotBodies.PlayerEntered(zone, player, zonePath, instance);

    internal override void PlayerLeftArea(IActorRef zone, IActorRef player) => BotBodies.PlayerLeft(zone, player);

    internal override bool GateNamesQuests(Trigger trigger) => BotGates.QuestsNamed(trigger.m_requirements).Count > 0;

    internal override void GateRefused(Wizard wizard, string zonePath, Trigger trigger) => BotGates.Refused(wizard, zonePath, trigger);

    internal override void NoteDoors(string zonePath, IEnumerable<MinimapIndex.Mark> live) => UltimusPads.NoteDoors(zonePath, live);

    private static BotPadHop Hop(UltimusPads.Pad pad) => pad is null ? null : new BotPadHop(pad.X, pad.Y, pad.ToX, pad.ToY, pad);

    private static List<UltimusPads.Pad> Pads(IReadOnlyCollection<BotPadHop> hops)
        => hops is null ? null : [.. hops.Select(h => h.Pad).OfType<UltimusPads.Pad>()];

    internal override BotPadHop PadToward(string zonePath, float x, float y, float toX, float toY, IReadOnlyCollection<BotPadHop> skip = null)
        => Hop(UltimusPads.Toward(zonePath, x, y, toX, toY, Pads(skip)));

    internal override BotPadHop PadAnother(string zonePath, float x, float y, float toX, float toY, IReadOnlyCollection<BotPadHop> skip)
        => Hop(UltimusPads.Another(zonePath, x, y, toX, toY, Pads(skip) ?? []));

    internal override BotPadHop PadFaster(string zonePath, float x, float y, float toX, float toY)
        => Hop(UltimusPads.Faster(zonePath, x, y, toX, toY));

    internal override int MostPads => UltimusPads.MOST_PADS;

    // ---------------------------------------------------------------- the overlay's spectators

    internal override Task<JsonObject> SpectateAsync(long link, JsonObject header, string address)
        => OverlaySpectate.SpectateAsync(link, header, address);

    internal override IDisposable UseSeatOf(ulong charId) => UltimusSeat.Use(UltimusSeat.OfCharacter(charId) ?? UltimusSeat.Current);

    // Ultimus's own game: his quest's way, drawn like the player's Quest Tracker line (the shared route finder). A Quest
    // Team wizard (the Quest Team Spectator): the team's quest, drawn the same way. A quest picked on Ahziael's Ultimus or
    // Quest Team page (Niduus, 2026-11-15): its way instead, when the wizard holds it.
    internal override JsonObject SpectateRoute(Wizard wizard, IActorRef zone, string quest, string routeQuest, bool team, out bool picked) {
        var seat = UltimusSeat.OfCharacter(wizard.CharId);
        var chosen = string.IsNullOrEmpty(routeQuest) ? null : UltimusSpectate.RouteFor(wizard, zone, routeQuest);
        picked = chosen is not null;
        var own = team || seat is null ? UltimusSpectate.RouteFor(wizard, zone, quest) : UltimusSpectate.Route(wizard, zone);

        return chosen ?? own;
    }

    // ---------------------------------------------------------------- battles

    internal override void PlanningBegins(CombatDuelComponent duel) => DuelBots.Of(duel).PlanningBegins();

    internal override void NoticeMove(CombatDuelComponent duel, CombatDuelSubCircle caster) => DuelBots.Of(duel).NoticeMove(caster);

    internal override void JoinedPlanning(CombatDuelComponent duel, CombatDuelSubCircle circle, double secondsLeft)
        => DuelBots.Of(duel).JoinedPlanning(circle, secondsLeft);

    internal override bool RefusesJoiner(CombatDuelComponent duel, CoreObject joiner) => DuelBots.Of(duel).RefusesJoiner(joiner);

    internal override CombatDuelSubCircle OwnCircle(CombatDuelComponent duel, CoreObject joiner) => DuelBots.Of(duel).OwnCircle(joiner);

    internal override void DuelMessage(CombatDuelComponent duel, object payload) => DuelBots.Of(duel).Receive(payload);

    internal override object BeforeMove(QueuedCombatAction action, IEnumerable<CombatDuelSubCircle> circles) => BotKnowledge.Before(action, circles);

    internal override void AfterMove(QueuedCombatAction action, object watched, ulong duelId, int round)
        => BotKnowledge.After(action, watched as (CombatDuelSubCircle Bot, int Health)?, duelId, round);

}
