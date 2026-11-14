/*
 * Imlight
 * Copyright (C) 2026 Revive101
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
using System.Linq;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Game.Battlegrounds;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;
using BG = Imlight.CoreLib.Game.Battlegrounds.Battlegrounds;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.bg` for Beastmoon Hunt (Game/Battlegrounds), for testing while the queue and match are not built: go into an arena
/// as a beast on a team, change form, team and the arena's effects, and push an event of the arena's script.
/// </summary>
internal class CommandBattlegroundProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    private const string DEFAULT_MATCH = "Beastmoon_PVP_02";
    // Where Beastmoon sends a wizard back to (ClassProjects/Beastmoon_PVP_Event_NN m_eventTeleportZone / Location).
    private const string RETURN_ZONE = "WizardCity/WC_Duel_Arena";
    private const string RETURN_LOCATION = "From_BG";

    private const string HELP = ".bg shows your team, form and effects. "
        + ".bg enter [match or arena 2-5, or PVE_01] [red|blue] [form] goes into a fresh arena of your own (default Beastmoon_PVP_02, red). "
        + ".bg form <name> [poly|look|both] / .bg form off. .bg forms [filter]. .bg matches. .bg team red|blue|none. "
        + ".bg effect DeathEffect|BG-PlanningPhase on|off. .bg points (the match and its control points). .bg start "
        + "(skips the countdown). .bg reset. "
        + ".bg coins [n] (Battle Coins, 10 by default; negative takes). .bg event <EventName>. .bg leave.";

    [Command("bg")]
    [Alias("beastmoon", "battleground")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void BattlegroundCommand([Remainder] string text = "") {
        if (!BG.Enabled) {
            InformSenderClient("Beastmoon is turned off on this server ([Beastmoon] Enabled).");

            return;
        }

        try {
            var words = (text ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var first = words.Length > 0 ? words[0].ToLowerInvariant() : "";
            var rest = words.Skip(1).ToArray();
            switch (first) {
                case "":
                case "status":
                    Status();

                    return;
                case "enter":
                case "go":
                    Enter(rest);

                    return;
                case "form":
                    Form(rest);

                    return;
                case "forms":
                    ListForms(rest.FirstOrDefault());

                    return;
                case "matches":
                    ListMatches();

                    return;
                case "team":
                    Team(rest.FirstOrDefault());

                    return;
                case "effect":
                    Effect(rest);

                    return;
                case "event":
                    PostEvent(string.Join(' ', rest));

                    return;
                case "leave":
                case "out":
                    Leave();

                    return;
                case "coins":
                    var amount = rest.Length > 0 && int.TryParse(rest[0], out var n) ? n : 10;
                    var added = BeastmoonProgress.AddBattleCoins(Context.Character, Context.SessionActor, amount);
                    InformSenderClient($"{added:+#;-#;0} Battle Coins: you have {BeastmoonProgress.BattleCoinsOf(Context.Character)} of {BeastmoonProgress.BattleCoinCap}.");

                    return;
                case "points":
                case "cp":
                    Points();

                    return;
                case "start":
                    Context.ZoneActor.Tell(new ZONE_102_PROTOCOL.MSG_BATTLEGROUNDCONTROL { Kind = BattlegroundControlKind.Start });
                    InformSenderClient("The match starts now (if it was counting down).");

                    return;
                case "reset":
                    Context.ZoneActor.Tell(new ZONE_102_PROTOCOL.MSG_BATTLEGROUNDCONTROL { Kind = BattlegroundControlKind.Reset });
                    InformSenderClient("Every control point here is nobody's again (the flags keep their color until taken).");

                    return;
            }

            InformSenderClient(HELP);
        }
        catch (Exception ex) {
            Logger.Error("Beastmoon | .bg failed: {0}", Logger.Args(ex));
            InformSenderClient("That did not work: " + ex.Message);
        }
    }

    private void Status() {
        var me = Context.Character;
        var player = BG.Of(me.CharId);
        string effects;
        if (player is null) {
            effects = "none";
        }
        else {
            lock (player) {
                effects = player.Effects.Count == 0 ? "none" : string.Join(", ", player.Effects);
            }
        }

        InformSenderClient($"Zone {me.Zone}{(BG.IsBattlegroundZone(me.Zone) ? " (battleground)" : "")}. "
            + $"Team: {player?.Team ?? BG.Team.None}. Form: {player?.FormName ?? "none"}"
            + $"{(player?.FormName is null ? "" : $" ({player.FormLook})")}. Effects: {effects}. {HELP}");
    }

    private void Enter(string[] args) {
        var me = Context.Character;
        var matchName = DEFAULT_MATCH;
        var team = BG.Team.Red;
        string formName = null;
        foreach (var arg in args) {
            if (arg.Equals("red", StringComparison.OrdinalIgnoreCase) || arg.Equals("a", StringComparison.OrdinalIgnoreCase)) {
                team = BG.Team.Red;
            }
            else if (arg.Equals("blue", StringComparison.OrdinalIgnoreCase) || arg.Equals("b", StringComparison.OrdinalIgnoreCase)) {
                team = BG.Team.Blue;
            }
            else if (FindMatch(arg) is { } found) {
                matchName = found;
            }
            else {
                formName = arg;
            }
        }

        if (BattlegroundEntry.SendIntoMatch(me, Context.SessionActor, matchName, team, formName) is { } error) {
            InformSenderClient(error);

            return;
        }

        InformSenderClient($"Going into {matchName} on the {BG.Of(me.CharId)?.Team} team as {BG.Of(me.CharId)?.FormName}.");
    }

    // "Beastmoon_PVP_02", "PVP_02", or an arena number 2-5 (its first PvP match).
    private static string FindMatch(string arg) {
        if (int.TryParse(arg, out var arena) && arena is >= 1 and <= 25) {
            arg = $"Beastmoon_PVP_{arena:00}";
        }

        foreach (var name in new[] { arg, "Beastmoon_" + arg }) {
            if (BG.Template(name) is not null) {
                return name;
            }
        }

        return null;
    }

    private void Form(string[] args) {
        var me = Context.Character;
        if (args.Length == 0) {
            InformSenderClient("Which form? .bg form Cyclops-Life-01 [poly|look|both], or .bg form off. .bg forms lists them.");

            return;
        }

        if (args[0].Equals("off", StringComparison.OrdinalIgnoreCase)) {
            InformSenderClient(BeastForm.End(me, Context.SessionActor, Context.ZoneActor) ? "You are yourself again." : "You are in no form.");

            return;
        }

        var look = BeastForm.Look.Both;
        if (args.Length > 1) {
            look = args[1].ToLowerInvariant() switch {
                "poly" or "polymorph" => BeastForm.Look.Polymorph,
                "look" or "trans" or "transformation" => BeastForm.Look.Transformation,
                _ => BeastForm.Look.Both,
            };
        }

        var error = BeastForm.Start(me, Context.SessionActor, Context.ZoneActor, args[0], look);
        BattleCards.SyncBattleBook(me, Context.SessionActor);
        InformSenderClient(error ?? $"You are now {BG.Of(me.CharId)?.FormName} ({look}).");
    }

    private void ListForms(string filter) {
        var forms = BG.FormNames()
            .Select(f => f[BG.FORM_PREFIX.Length..])
            .Where(f => string.IsNullOrEmpty(filter) || f.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (forms.Count == 0) {
            InformSenderClient("No forms match that.");

            return;
        }

        // The first ranks only, unless a filter narrows it down.
        var shown = string.IsNullOrEmpty(filter) ? forms.Where(f => f.EndsWith("-01")).ToList() : forms;
        InformSenderClient($"{forms.Count} forms{(string.IsNullOrEmpty(filter) ? " (rank 01 shown)" : "")}: "
            + string.Join(", ", shown.Take(80)) + (shown.Count > 80 ? ", ..." : ""));
    }

    private void ListMatches() {
        var lines = BG.BeastmoonTemplateNames()
            .Select(n => (Name: n, Template: BG.Template(n)))
            .Where(m => m.Template is not null)
            .GroupBy(m => (string) m.Template.m_battlegroundZone)
            .Select(g => $"{g.Key}: {string.Join(" ", g.Select(m => m.Name.Replace("Beastmoon_", "")))}");
        InformSenderClient(string.Join(" | ", lines));
    }

    private void Team(string which) {
        var me = Context.Character;
        var team = (which ?? "").ToLowerInvariant() switch {
            "red" or "a" => BG.Team.Red,
            "blue" or "b" => BG.Team.Blue,
            "none" or "off" => BG.Team.None,
            _ => (BG.Team?) null,
        };
        if (team is null) {
            InformSenderClient(".bg team red|blue|none");

            return;
        }

        BG.SetTeam(me.CharId, team.Value);
        if (BG.IsBattlegroundZone(me.Zone)) {
            BeastForm.ShowTeam(me, Context.SessionActor, Context.ZoneActor);
        }

        InformSenderClient($"You are on the {team} team.");
    }

    private void Effect(string[] args) {
        if (args.Length < 2 || !BG.IsTracked(args[0])) {
            InformSenderClient($".bg effect <{BG.DEATH_EFFECT}|{BG.PLANNING_EFFECT}|{BG.TEAM_A_EFFECT}|{BG.TEAM_B_EFFECT}> on|off");

            return;
        }

        var on = args[1].Equals("on", StringComparison.OrdinalIgnoreCase) || args[1] == "1";
        BG.SetEffect(Context.Character.CharId, args[0], on);
        InformSenderClient($"{args[0]} is {(on ? "on" : "off")}.");
    }

    private void PostEvent(string name) {
        if (string.IsNullOrWhiteSpace(name)) {
            InformSenderClient(".bg event EVENT_CTF_Red_01 (any event of the arena's script).");

            return;
        }

        Context.ZoneActor.Tell(new ZONE_102_PROTOCOL.MSG_POSTEVENT {
            EventName = name,
            PlayerActor = Context.SessionActor,
            PlayerGameObject = Context.CharacterObject,
        });
        InformSenderClient($"Posted {name} in {Context.Character.Zone}.");
    }

    private void Points() {
        var answer = Context.ZoneActor.Ask<ZONE_102_PROTOCOL.MSG_BATTLEGROUNDCONTROL>(
            new ZONE_102_PROTOCOL.MSG_BATTLEGROUNDCONTROL { Kind = BattlegroundControlKind.Status }, TimeSpan.FromSeconds(5)).Result;
        InformSenderClient(answer?.Text ?? "The zone did not answer.");
    }

    private void Leave() {
        var me = Context.Character;
        BeastForm.End(me, Context.SessionActor, Context.ZoneActor);
        BG.Forget(me.CharId);
        InformSenderClient("Leaving Beastmoon.");
        Context.SessionActor.Tell(new CHARACTER_103_PROTOCOL.MSG_DOTELEPORTEFFECTS());
        Context.SessionActor.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = RETURN_ZONE,
            DestinationLocation = RETURN_LOCATION,
            SendToClient = true,
        });
    }

}
