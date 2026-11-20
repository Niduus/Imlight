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
using System.Collections.Concurrent;
using System.Collections.Generic;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Akka.Actor;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Commands;
using Imlight.CoreLib.Game.Commands.Protocols;
using Imlight.RemoteAdmin;
using Imlight.RemoteAdmin;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.Game.Quests;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>
/// The overlay's spectators of the server's own wizards (Ultimus, the TCO7, the Quest Team) and Spectate Anyone: moved here
/// from OverlayLinks with the bots, so a bot update changes them too. OverlayLinks.SpectateAsync asks the bots through
/// BotDoor.
/// </summary>
internal static class OverlaySpectate {

    /// <summary>
    /// Ultimus as the overlay's Ultimus Spectator shows him (Niduus, 2026-11-13: "Spectate Ultimus, able to view his
    /// location and quests via Minimap ... and when in battle, able to see his spells"): his own map with his quest's way on
    /// it, his quests, and his battle. The same for every game window, and for any overlay linked to this server; it only
    /// reads, so no ".link" is needed. Never the player's own map or quests.
    /// </summary>
    internal static async Task<JsonObject> SpectateAsync(long link, JsonObject header, string address = "") {
        // Spectate Anyone (Niduus, 2026-11-16): "who" is "anyone:" and the wizard picked (its character id, or empty for the
        // first one). Only the host, on the server's own PC.
        if (((string) header["who"] ?? "").StartsWith(ANYONE, StringComparison.OrdinalIgnoreCase)) {
            return await SpectateAnyoneAsync(link, header, address);
        }

        if (!Ultimus.UltimusSpectate.On) {
            return RemoteAdminWire.Error("The host has turned off watching Ultimus on this server.");
        }

        var times = OverlayLinks.s_spectateAsks.GetOrAdd(link, _ => new ConcurrentQueue<DateTime>());
        while (times.TryPeek(out var oldest) && DateTime.UtcNow - oldest > TimeSpan.FromSeconds(1)) {
            times.TryDequeue(out _);
        }

        if (times.Count >= 12) {
            return RemoteAdminWire.Error("busy");
        }

        times.Enqueue(DateTime.UtcNow);

        // The Quest Team Spectator (Niduus, 2026-11-15): "who" names the team member watched (Ice, Life, Death or Storm).
        if (((string) header["who"] ?? "") is { Length: > 0 } who) {
            return await SpectateTeamAsync(who, header);
        }

        return await SpectateSeatAsync(Ultimus.UltimusSeat.Ultimus, header);
    }

    /// <summary>
    /// Ultimus, or a TCO7 member (each is played by Ultimus's brain, UltimusSeat), as the spectators show him: his own map with
    /// his quest's way on it, his quests, and his battle.
    /// </summary>
    private static async Task<JsonObject> SpectateSeatAsync(Ultimus.UltimusSeat seat, JsonObject header) {
        using var seatScope = Ultimus.UltimusSeat.Use(seat);
        if (!Ultimus.UltimusWire.Enabled) {
            return RemoteAdminWire.Error(seat.IsUltimus ? "Ultimus is off on this server. Turn him on in Ahziael (Ultimus page) to watch him."
                : $"{seat.Name} is off on this server. Turn the TCO7 on in Ahziael (TCO7 page) to watch them.");
        }

        var member = Ultimus.UltimusDirector.Members.FirstOrDefault();
        if (member is null || member.State is "offline" or "logging in") {
            return RemoteAdminWire.Error($"{seat.Name} isn't in the game yet. {(seat.IsUltimus ? "He shows" : "They show")} here once logged in.");
        }

        var quest = Ultimus.UltimusSpectate.Quest;
        var reply = new TaskCompletionSource<(JsonObject Map, JsonObject Quests, IActorRef Zone, string Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var others = new ConcurrentQueue<(ulong CharId, IActorRef Zone, string Name, float X, float Y)>();
        var reach = header["reach"] is JsonValue far && far.TryGetValue<double>(out var wanted) && double.IsFinite(wanted) ? (float) wanted : 0f;
        var ask = new OverlaySpectateAsk(member.CharId, reply, others, reach, quest, RouteQuest: ((string) header["routeQuest"] ?? "").Trim());
        foreach (var service in AgentBridge.CommandServices) {
            service.Tell(ask);
        }

        if (await Task.WhenAny(reply.Task, Task.Delay(2000)) != reply.Task) {
            // Going to another area: his last map stays until he arrives.
            return RemoteAdminWire.Error(member.State is "zoning" or "arriving" ? "busy" : $"{seat.Name} isn't in the game right now.");
        }

        var (map, quests, zone, error) = reply.Task.Result;
        if (map is null) {
            return RemoteAdminWire.Error(error);
        }

        await Task.Delay(60);
        OverlayMinimap.AddWizards(map, member.CharId, zone, others);
        // His area's streets, sent once per area like the player's minimap (the overlay says which it has).
        var zonePath = (string) map["zone"] ?? "";
        var smoothing = OverlayMinimap.Smoothing;
        var groundKey = zonePath + "#" + smoothing.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (OverlayMinimap.Streets && zonePath.Length > 0 && !string.Equals((string) header["groundKey"] ?? "", groundKey, StringComparison.OrdinalIgnoreCase)) {
            var shapes = Zone.Core.ZoneGround.ShapesOf(zonePath, smoothing);
            map["ground"] = shapes is null || shapes.IsEmpty
                ? new JsonObject { ["zone"] = zonePath, ["key"] = groundKey }
                : new JsonObject {
                    ["zone"] = zonePath, ["key"] = groundKey, ["left"] = shapes.Left, ["bottom"] = shapes.Bottom, ["unit"] = shapes.Unit,
                    ["shapes"] = shapes.Data,
                };
        }

        var wizard = member.Wizard;
        var stats = wizard?.GameStats;
        var row = (quests?["quests"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(q => string.Equals((string) q["id"], quest, StringComparison.OrdinalIgnoreCase));
        var info = new JsonObject {
            ["name"] = member.Name,
            ["level"] = wizard?.MagicSchoolBehavior?.Level ?? 0,
            ["hp"] = stats?.m_currentHitpoints ?? 0,
            ["max"] = stats is null ? 0 : stats.m_baseHitpoints + stats.m_bonusHitpoints,
            ["place"] = (string) map["place"] ?? "",
            ["doing"] = Ultimus.UltimusSpectate.Doing,
            ["quest"] = quest,
            ["questTitle"] = quest.Length == 0 ? "" : (string) row?["t"] ?? Ultimus.UltimusText.Title(quest),
            ["step"] = Ultimus.UltimusSpectate.Step,
            ["steps"] = row?["steps"]?.DeepClone(),
            ["way"] = row?["way"]?.DeepClone(),
            ["kind"] = (string) row?["k"] ?? "",
            // Detail for his quest box (Niduus, 2026-11-14: "make sure his quest tracker is in detail"): its level and world.
            ["questLevel"] = row?["l"]?.DeepClone(),
            ["world"] = Ultimus.UltimusDirectorActor.WorldName((string) row?["w"] ?? ""),
            ["detail"] = Ultimus.UltimusSpectate.Detail,
            // Quest CURR/END in his world's story and a time to the next world (Niduus, 2026-11-15).
            ["progress"] = BotShared.BotProgress.Json(BotShared.BotProgress.ULTIMUS, wizard, quest, note: false),
            ["quests"] = new JsonArray([.. ((quests?["quests"] as JsonArray)?.OfType<JsonObject>() ?? [])
                .Take(40).Select(q => (JsonNode) new JsonObject {
                    ["id"] = q["id"]?.DeepClone(), ["t"] = q["t"]?.DeepClone(), ["k"] = q["k"]?.DeepClone(),
                    // A dungeon quest's steps and way, so the spectator can track it (Niduus, 2026-11-15: "also show Instance").
                    ["l"] = q["l"]?.DeepClone(), ["w"] = Ultimus.UltimusDirectorActor.WorldName((string) q["w"] ?? ""),
                    ["steps"] = (string) q["k"] == "instance" ? q["steps"]?.DeepClone() : null,
                    ["way"] = (string) q["k"] == "instance" ? q["way"]?.DeepClone() : null,
                    ["here"] = q["here"]?.DeepClone(),
                })]),
        };
        if (Ultimus.UltimusSpectate.Battle(member.GlobalId) is { } battle) {
            battle["deck"] = wizard?.SpellbookBehavior?.SpellList is { } deck
                ? BotShared.BotCards.Describe(deck.Where(d => d is not null).Select(d => ((uint) d.m_templateID, (int) d.m_quantity)))
                : "";
            info["battle"] = battle;
        }

        var answer = RemoteAdminWire.Ok();
        answer["map"] = map;
        answer["ultimus"] = info;

        return answer;
    }


    /// <summary>
    /// One Quest Team wizard as the Quest Team Spectator shows it (Niduus, 2026-11-15: "Make a spectator plugin for them,
    /// only able to cycle between each member"): its own map with the team's quest way, and the team's quest. Read only.
    /// </summary>
    private static async Task<JsonObject> SpectateTeamAsync(string who, JsonObject header) {
        if (!QuestTeam.QuestTeamWire.Enabled) {
            return RemoteAdminWire.Error("The Quest Team is off on this server. Turn it on in Ahziael (Quest Team page) to watch it.");
        }

        var members = QuestTeam.QuestTeamDirector.Members;
        var member = members.FirstOrDefault(m => string.Equals(m.Role, who, StringComparison.OrdinalIgnoreCase));
        if (member is null || member.State is "offline" or "logging in") {
            return RemoteAdminWire.Error($"The Quest Team's {who} wizard isn't in the game yet. It shows here once it has logged in.");
        }

        var now = QuestTeam.QuestTeamDirector.SpectateNow;
        var reply = new TaskCompletionSource<(JsonObject Map, JsonObject Quests, IActorRef Zone, string Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var others = new ConcurrentQueue<(ulong CharId, IActorRef Zone, string Name, float X, float Y)>();
        var reach = header["reach"] is JsonValue far && far.TryGetValue<double>(out var wanted) && double.IsFinite(wanted) ? (float) wanted : 0f;
        var ask = new OverlaySpectateAsk(member.CharId, reply, others, reach, now.Quest, Team: true, RouteQuest: ((string) header["routeQuest"] ?? "").Trim());
        foreach (var service in AgentBridge.CommandServices) {
            service.Tell(ask);
        }

        if (await Task.WhenAny(reply.Task, Task.Delay(2000)) != reply.Task) {
            return RemoteAdminWire.Error(member.State is "zoning" or "arriving" ? "busy" : $"The Quest Team's {who} wizard isn't in the game right now.");
        }

        var (map, quests, zone, error) = reply.Task.Result;
        if (map is null) {
            return RemoteAdminWire.Error(error);
        }

        await Task.Delay(60);
        OverlayMinimap.AddWizards(map, member.CharId, zone, others);
        var zonePath = (string) map["zone"] ?? "";
        var smoothing = OverlayMinimap.Smoothing;
        var groundKey = zonePath + "#" + smoothing.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (OverlayMinimap.Streets && zonePath.Length > 0 && !string.Equals((string) header["groundKey"] ?? "", groundKey, StringComparison.OrdinalIgnoreCase)) {
            var shapes = Zone.Core.ZoneGround.ShapesOf(zonePath, smoothing);
            map["ground"] = shapes is null || shapes.IsEmpty
                ? new JsonObject { ["zone"] = zonePath, ["key"] = groundKey }
                : new JsonObject {
                    ["zone"] = zonePath, ["key"] = groundKey, ["left"] = shapes.Left, ["bottom"] = shapes.Bottom, ["unit"] = shapes.Unit,
                    ["shapes"] = shapes.Data,
                };
        }

        var wizard = member.Wizard;
        var stats = wizard?.GameStats;
        var row = (quests?["quests"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(q => string.Equals((string) q["id"], now.Quest, StringComparison.OrdinalIgnoreCase));
        var info = new JsonObject {
            ["name"] = $"{member.Name} ({member.Role})",
            ["level"] = wizard?.MagicSchoolBehavior?.Level ?? 0,
            ["hp"] = stats?.m_currentHitpoints ?? 0,
            ["max"] = stats is null ? 0 : stats.m_baseHitpoints + stats.m_bonusHitpoints,
            ["place"] = (string) map["place"] ?? "",
            ["doing"] = CombatDuelComponentInBattle(member) ? "in a battle" : now.Doing,
            ["quest"] = now.Quest,
            ["questTitle"] = now.Quest.Length == 0 ? "" : (string) row?["t"] ?? QuestTeam.QuestTeamText.Title(now.Quest),
            ["step"] = now.Step,
            ["steps"] = row?["steps"]?.DeepClone(),
            ["way"] = row?["way"]?.DeepClone(),
            ["kind"] = (string) row?["k"] ?? "",
            ["questLevel"] = row?["l"]?.DeepClone(),
            ["world"] = Ultimus.UltimusDirectorActor.WorldName((string) row?["w"] ?? ""),
            ["role"] = member.Role,
            ["team"] = new JsonArray([.. members.Select(m => (JsonNode) m.Role)]),
            // Its school, for the Quest Team map's border colour (Niduus, 2026-11-15).
            ["school"] = member.Role,
            // Quest CURR/END in this wizard's world story and a time to the next world at the team's pace (Niduus, 2026-11-15).
            ["progress"] = BotShared.BotProgress.Json(BotShared.BotProgress.TEAM, wizard, now.Quest, note: false),
            ["quests"] = new JsonArray([.. ((quests?["quests"] as JsonArray)?.OfType<JsonObject>() ?? [])
                .Take(40).Select(q => (JsonNode) new JsonObject {
                    ["id"] = q["id"]?.DeepClone(), ["t"] = q["t"]?.DeepClone(), ["k"] = q["k"]?.DeepClone(),
                    // A dungeon quest's steps and way, so the spectator can track it (Niduus, 2026-11-15: "also show Instance").
                    ["l"] = q["l"]?.DeepClone(), ["w"] = Ultimus.UltimusDirectorActor.WorldName((string) q["w"] ?? ""),
                    ["steps"] = (string) q["k"] == "instance" ? q["steps"]?.DeepClone() : null,
                    ["way"] = (string) q["k"] == "instance" ? q["way"]?.DeepClone() : null,
                    ["here"] = q["here"]?.DeepClone(),
                })]),
        };

        // The team's stuck line and read-ahead warning, as Ultimus's tracker shows his (Niduus, 2026-11-15).
        if (now.Stuck.Length > 0 || now.ReadAhead.Length > 0) {
            info["detail"] = new JsonObject { ["stuck"] = now.Stuck, ["readAhead"] = now.ReadAhead };
        }

        // Its battle, as Ultimus's (Niduus, 2026-11-15: a battle box for the Quest Team too).
        if (member.Wizard is not null && Ultimus.UltimusSpectate.Battle(member.GlobalId, member.Role) is { } battle) {
            battle["deck"] = wizard?.SpellbookBehavior?.SpellList is { } deck
                ? BotShared.BotCards.Describe(deck.Where(d => d is not null).Select(d => ((uint) d.m_templateID, (int) d.m_quantity)))
                : "";
            info["battle"] = battle;
        }

        var answer = RemoteAdminWire.Ok();
        answer["map"] = map;
        answer["ultimus"] = info;

        return answer;
    }

    // ---- Spectate Anyone (op "ultimus" with who "anyone:<character id>") ----

    internal const string ANYONE = "anyone:";

    /// <summary>[Overlay] SpectateAnyone: the host may watch anyone on the server with the Spectate Anyone add-on. On; live.</summary>
    internal static bool SpectateAnyoneOn => ConfigurationManager.GetValue("Overlay.SpectateAnyone", true);

    /// <summary>
    /// Spectate Anyone (Niduus, 2026-11-16: "While installed and you're the local host, you can spectate anyone on the server,
    /// including Bots like Ultimus, TCO7 and QT members"): everyone in the game, players and the server's own wizards, to pick
    /// from, and the one picked as the spectators show a wizard (its map, quests, and for Ultimus, the TCO7 and the Quest
    /// Team their battle and plans too). Only for an overlay on the server's own PC.
    /// </summary>
    private static async Task<JsonObject> SpectateAnyoneAsync(long link, JsonObject header, string address) {
        if (!string.Equals(address, "127.0.0.1", StringComparison.Ordinal)) {
            return RemoteAdminWire.Error("Spectate Anyone is only for the host, on the PC that runs the server.");
        }

        if (!SpectateAnyoneOn) {
            return RemoteAdminWire.Error("Spectate Anyone is turned off in Ahziael (Overlay settings).");
        }

        var everyone = Everyone();
        var asked = ((string) header["who"] ?? "")[ANYONE.Length..].Trim();
        var pick = everyone.FirstOrDefault(e => e.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == asked)
                   ?? everyone.FirstOrDefault(e => string.Equals(e.Name, asked, StringComparison.OrdinalIgnoreCase))
                   ?? everyone.FirstOrDefault();
        JsonObject answer;
        if (pick is null) {
            answer = RemoteAdminWire.Error("Nobody is in the game right now.");
        }
        else if (Ultimus.UltimusSeat.OfCharacter(pick.Id) is { } seat) {
            answer = await SpectateSeatAsync(seat, header);
        }
        else if (QuestTeam.QuestTeamDirector.Members.FirstOrDefault(m => m.CharId == pick.Id) is { } teamMember) {
            answer = await SpectateTeamAsync(teamMember.Role, header);
        }
        else {
            answer = await SpectatePlayerAsync(pick.Id, pick.Name, header);
        }

        // The list to pick from, and whom this answer shows (the overlay drops an answer for someone it no longer watches).
        answer["everyone"] = new JsonArray([.. everyone.Select(e => (JsonNode) new JsonObject {
            ["id"] = e.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), ["name"] = e.Name, ["kind"] = e.Kind, ["school"] = e.School,
        })]);
        answer["watching"] = pick?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        if (answer["ultimus"] is JsonObject info && pick is not null) {
            info["role"] = ANYONE + answer["watching"];
            info["kind"] ??= "";
            info["who"] = pick.Kind;
            info["school"] ??= pick.School;
        }

        return answer;
    }

    private sealed record Someone(ulong Id, string Name, string Kind, string School);

    // Everyone in the game: Ultimus and the TCO7 first, then the Quest Team, then players by name.
    private static List<Someone> Everyone() {
        var list = new List<Someone>();
        try {
            foreach (var p in OnlinePlayerCollection.GetCachedOnlinePlayers().Where(p => p.CharacterId != 0)) {
                var seat = Ultimus.UltimusSeat.OfCharacter(p.CharacterId);
                var kind = seat is { IsUltimus: true } ? "Ultimus" : seat is { IsStreet: true } ? "Street bot" : seat is { IsTrueForm: true } ? "True Form" : seat is not null ? "TCO7"
                    : QuestTeam.QuestTeamRoster.IsMember(p.CharacterId) ? "Quest Team" : "Player";
                var name = PlayerCheats.LiveName(p.CharacterId) is { Length: > 0 } live ? live : p.WizardName ?? "?";
                list.Add(new Someone(p.CharacterId, name, kind, seat?.Role ?? p.School ?? ""));
            }
        }
        catch (InvalidOperationException) {
            // The list changed while read: read again next time.
        }

        static int Order(Someone s) => s.Kind switch { "Ultimus" => 0, "TCO7" => 1, "Quest Team" => 2, _ => 3 };
        return [.. list.DistinctBy(s => s.Id).OrderBy(Order).ThenBy(s => Ultimus.UltimusSeat.OfCharacter(s.Id)?.Index ?? 0).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>A player (or anyone without a bot's plans) as the spectators show a wizard: its map, its quests and its quest's way.</summary>
    private static async Task<JsonObject> SpectatePlayerAsync(ulong charId, string name, JsonObject header) {
        var wizard = PlayerCheats.LiveWizard(charId);
        var quest = wizard?.QuestBehavior?.CurrentQuestInstances?.Select(q => q?.QuestName)
            .FirstOrDefault(n => n is not null && QuestTemplateCollection.GetQuestByName(n) is { } t && Ultimus.UltimusPlanner.IsMainStoryWorld(t))
            ?? wizard?.QuestBehavior?.CurrentQuestInstances?.Select(q => q?.QuestName).FirstOrDefault(n => n is not null) ?? "";
        var reply = new TaskCompletionSource<(JsonObject Map, JsonObject Quests, IActorRef Zone, string Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var others = new ConcurrentQueue<(ulong CharId, IActorRef Zone, string Name, float X, float Y)>();
        var reach = header["reach"] is JsonValue far && far.TryGetValue<double>(out var wanted) && double.IsFinite(wanted) ? (float) wanted : 0f;
        var ask = new OverlaySpectateAsk(charId, reply, others, reach, quest, Team: true, RouteQuest: ((string) header["routeQuest"] ?? "").Trim());
        foreach (var service in AgentBridge.CommandServices) {
            service.Tell(ask);
        }

        if (await Task.WhenAny(reply.Task, Task.Delay(2000)) != reply.Task) {
            return RemoteAdminWire.Error("busy");
        }

        var (map, quests, zone, error) = reply.Task.Result;
        if (map is null) {
            return RemoteAdminWire.Error(error);
        }

        await Task.Delay(60);
        OverlayMinimap.AddWizards(map, charId, zone, others);
        var zonePath = (string) map["zone"] ?? "";
        var smoothing = OverlayMinimap.Smoothing;
        var groundKey = zonePath + "#" + smoothing.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (OverlayMinimap.Streets && zonePath.Length > 0 && !string.Equals((string) header["groundKey"] ?? "", groundKey, StringComparison.OrdinalIgnoreCase)) {
            var shapes = Zone.Core.ZoneGround.ShapesOf(zonePath, smoothing);
            map["ground"] = shapes is null || shapes.IsEmpty
                ? new JsonObject { ["zone"] = zonePath, ["key"] = groundKey }
                : new JsonObject {
                    ["zone"] = zonePath, ["key"] = groundKey, ["left"] = shapes.Left, ["bottom"] = shapes.Bottom, ["unit"] = shapes.Unit,
                    ["shapes"] = shapes.Data,
                };
        }

        var stats = wizard?.GameStats;
        var row = (quests?["quests"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(q => string.Equals((string) q["id"], quest, StringComparison.OrdinalIgnoreCase));
        var info = new JsonObject {
            ["name"] = name,
            ["level"] = wizard?.MagicSchoolBehavior?.Level ?? 0,
            ["hp"] = stats?.m_currentHitpoints ?? 0,
            ["max"] = stats is null ? 0 : stats.m_baseHitpoints + stats.m_bonusHitpoints,
            ["place"] = (string) map["place"] ?? "",
            ["doing"] = Zone.Components.CombatDuelComponent.IsInBattle(Wizard.GetGameObjectId(charId)) ? "in a battle" : "",
            ["quest"] = quest,
            ["questTitle"] = quest.Length == 0 ? "" : (string) row?["t"] ?? Ultimus.UltimusText.Title(quest),
            ["step"] = "",
            ["steps"] = row?["steps"]?.DeepClone(),
            ["way"] = row?["way"]?.DeepClone(),
            ["kind"] = (string) row?["k"] ?? "",
            ["questLevel"] = row?["l"]?.DeepClone(),
            ["world"] = Ultimus.UltimusDirectorActor.WorldName((string) row?["w"] ?? ""),
            ["quests"] = new JsonArray([.. ((quests?["quests"] as JsonArray)?.OfType<JsonObject>() ?? [])
                .Take(40).Select(q => (JsonNode) new JsonObject {
                    ["id"] = q["id"]?.DeepClone(), ["t"] = q["t"]?.DeepClone(), ["k"] = q["k"]?.DeepClone(),
                    ["l"] = q["l"]?.DeepClone(), ["w"] = Ultimus.UltimusDirectorActor.WorldName((string) q["w"] ?? ""),
                    ["steps"] = (string) q["k"] == "instance" ? q["steps"]?.DeepClone() : null,
                    ["way"] = (string) q["k"] == "instance" ? q["way"]?.DeepClone() : null,
                    ["here"] = q["here"]?.DeepClone(),
                })]),
        };

        var answer = RemoteAdminWire.Ok();
        answer["map"] = map;
        answer["ultimus"] = info;

        return answer;
    }

    private static bool CombatDuelComponentInBattle(QuestTeam.QuestTeamMember.Status member)
        => member.Wizard is not null && Zone.Components.CombatDuelComponent.IsInBattle(member.GlobalId);

}
