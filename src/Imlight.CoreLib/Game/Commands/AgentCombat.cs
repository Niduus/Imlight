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
 * AGENT COMBAT
 * ========================================================================
 *
 * PURPOSE:
 * Lets the agent bridge (the Imlight MCP server) fight a battle as a logged-in player: list the creatures
 * near the player, start a duel with one, read the battle (circles, health, pips, hand), play a move and
 * let a simple strategy play the whole fight as a job.
 *
 * NOTE:
 * A move is sent to the duel exactly as the client's own combat move is (COMBAT_106 MSG_ACTORCOMBATMOVE),
 * and a duel is started with the same message a creature's aggro sends (ZONE_102 MSG_REQUESTCOMBATSIGIL),
 * so everything the client sees comes from the server's normal paths. The state is read from the duel
 * without changing it.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Commands;

public static class AgentCombat {

    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string PlanningPhase = "kPhase_Planning";

    private static readonly Dictionary<ulong, string> s_names = [];

    public static void Map() {
        AgentBridge.MapRoute("/combat/state", State);
        AgentBridge.MapRoute("/combat/creatures", Creatures);
        AgentBridge.MapRoute("/combat/start", Start);
        AgentBridge.MapRoute("/combat/move", Move);
        AgentBridge.MapRoute("/combat/auto", Auto);
    }

    // ---------------------------------------------------------------- arguments

    // An empty string counts as not given.
    private static string Str(JsonElement body, string name)
        => body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    private static int? Num(JsonElement body, string name)
        => body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static bool Flag(JsonElement body, string name, bool fallback)
        => body.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;

    private static async Task<LivePlayer> FindPlayer(JsonElement body) {
        var filter = Str(body, "player");
        var all = await AgentBridge.GetLivePlayers();
        if (!string.IsNullOrWhiteSpace(filter)) {
            all = [.. all.Where(p => $"{p.Name}|{p.Account?.Username}".Contains(filter, StringComparison.OrdinalIgnoreCase))];
        }

        return all.Count switch {
            0 => throw new InvalidOperationException("No matching player is logged in (see game_players)."),
            1 => all[0],
            _ => throw new InvalidOperationException("More than one player matches: " + string.Join(", ", all.Select(p => p.Name))),
        };
    }

    // ---------------------------------------------------------------- finding things in the live server

    private static IEnumerable<IActorRef> Children(IActorRef actor)
        => (actor?.GetType().GetProperty("Underlying", Flags)?.GetValue(actor) as ActorCell)?.GetChildren() ?? [];

    private static ZoneEntity EntityOf(IActorRef actor) {
        var cell = actor?.GetType().GetProperty("Underlying", Flags)?.GetValue(actor);

        return cell?.GetType().GetProperty("Actor", Flags)?.GetValue(cell) as ZoneEntity;
    }

    /// <summary>The actor of the zone the player stands in.</summary>
    private static async Task<IActorRef> ZoneOf(LivePlayer player) {
        var server = player.Session.Path.Parent;
        var serverName = server.Name;
        var world = serverName[..serverName.LastIndexOf('.')] + ".GameWorld";
        var zoneName = player.Wizard.Zone.Replace('/', '-');
        var path = $"{server.ToStringWithoutAddress()}/{world}/{zoneName}";
        try {
            return await AgentBridge.Actors.ActorSelection(path).ResolveOne(TimeSpan.FromSeconds(3));
        }
        catch (ActorNotFoundException) {
            // A private copy (a dungeon, a raid, a Beastmoon arena) runs inside its owner's InstanceContainer_<id>.
            try {
                return await AgentBridge.Actors.ActorSelection($"{server.ToStringWithoutAddress()}/{world}/InstanceContainer_*/{zoneName}")
                    .ResolveOne(TimeSpan.FromSeconds(3));
            }
            catch (ActorNotFoundException) {
                throw new InvalidOperationException($"The zone actor {path} is not running (the player is in {player.Wizard.Zone}).");
            }
        }
    }

    private static IActorRef Child(IActorRef parent, string name)
        => Children(parent).FirstOrDefault(c => c.Path.Name == name)
            ?? throw new InvalidOperationException($"{parent.Path.Name} has no child {name}.");

    private sealed record DuelHandle(IActorRef Sigil, CombatDuelComponent Duel);

    private static bool Fights(CombatDuelSubCircle circle, LivePlayer player)
        => circle.IsHumanPlayer
            && (circle.ParticipantActor?.Equals(player.Session) == true
                || (ulong) circle.ParticipantObject.m_globalID == (ulong) player.CoreObject.m_globalID);

    private static async Task<DuelHandle> FindDuel(LivePlayer player) {
        var zone = await ZoneOf(player);
        foreach (var sigil in Children(Child(zone, "ZoneSigilSupervisor"))) {
            var duel = EntityOf(sigil)?.GetComponentOfType<CombatDuelComponent>();
            if (duel is { IsDuelActive: true } && duel.ActiveSubCircles.Any(c => Fights(c, player))) {
                return new DuelHandle(sigil, duel);
            }
        }

        return null;
    }

    private static string NameOf(ulong templateId) {
        lock (s_names) {
            if (s_names.TryGetValue(templateId, out var known)) {
                return known;
            }

            var name = IdCatalog.IsReady ? IdCatalog.Search(null, templateId.ToString(), 1).FirstOrDefault()?.Name : null;
            if (name is not null) {
                s_names[templateId] = name;
            }

            return name ?? templateId.ToString();
        }
    }

    // ---------------------------------------------------------------- reading the battle

    private static bool Kind(Func<List<Spell>, List<Spell>> filter, Spell spell) => filter([spell]).Count > 0;

    /// <summary>Every pip the participant holds. School pips fill the same slots as normal ones but only pay school pip costs.</summary>
    private static object PipSummary(PipCount pips) => new {
        generic = (int) pips.m_genericPips,
        power = (int) pips.m_powerPips,
        school = new Dictionary<string, int> {
            ["balance"] = pips.m_balancePips, ["death"] = pips.m_deathPips, ["fire"] = pips.m_firePips, ["ice"] = pips.m_icePips,
            ["life"] = pips.m_lifePips, ["myth"] = pips.m_mythPips, ["storm"] = pips.m_stormPips,
        }.Where(x => x.Value > 0).ToDictionary(x => x.Key, x => x.Value),
        shadow = (int) pips.m_shadowPips,
    };

    private static string Cost(Spell spell) {
        var cost = spell.m_pipCost;

        return cost is null ? "?" : $"{cost.m_spellRank} pips" + (cost.m_shadowPips > 0 ? $" + {cost.m_shadowPips} shadow" : "");
    }

    /// <summary>Why the participant cannot cast the card right now, in words.</summary>
    private static string WhyNot(CombatDuelSubCircle me, Spell spell) {
        return !me.HasSchoolPipsForSpell(spell)
            ? "lacks the school or shadow pips this card costs"
            : "not enough pips or mana";
    }

    private static object Snapshot(LivePlayer player, DuelHandle handle) {
        var duel = handle.Duel;
        var me = duel.ActiveSubCircles.FirstOrDefault(c => Fights(c, player));
        var circles = duel.ActiveSubCircles.Select(c => new {
            slot = c.SlotIndex,
            team = c.OccupiedTeam.ToString(),
            name = c.IsHumanPlayer ? (Fights(c, player) ? player.Name : "another player") : NameOf(c.ParticipantObject.m_templateID.Full),
            you = c == me,
            alive = c.IsAlive,
            hp = c.ParticipantGameStats?.m_currentHitpoints,
            maxHp = c.ParticipantGameStats?.m_baseHitpoints,
            pips = c.CombatParticipant is null ? null : PipSummary(c.CombatParticipant.m_pipCount),
            minion = c.IsSummonedMinion,
        }).ToList();

        var hand = new List<object>();
        if (me is not null) {
            for (byte i = 0; i < 24; i++) {
                var spell = me.GetSpellFromLastHand(i);
                if (spell is null) {
                    continue;
                }

                var template = CoreObjectFactory.GetCoreTemplate(spell.m_templateID) as SpellTemplate;
                hand.Add(new {
                    card = (int) i,
                    id = spell.m_templateID,
                    name = NameOf(spell.m_templateID),
                    rank = (int) spell.m_pipCost.m_spellRank,
                    school = spell.m_magicSchoolID.ToString(),
                    castable = me.HasPipsForSpell(spell),
                    cost = Cost(spell),
                    whyNot = me.HasPipsForSpell(spell) ? null : WhyNot(me, spell),
                    damage = Kind(SpellEffectFilter.FilterSpellsByOutgoingDamage, spell),
                    heal = Kind(SpellEffectFilter.FilterSpellsByHealing, spell),
                    buff = Kind(SpellEffectFilter.FilterSpellsByBuff, spell),
                    debuff = Kind(SpellEffectFilter.FilterSpellsByDebuff, spell),
                    enchant = template is not null && CombatEnchantments.IsEnchantment(template),
                });
            }
        }

        return new {
            inDuel = true,
            sigil = handle.Sigil.Path.Name,
            round = duel.Duel.m_roundNum,
            phase = duel.Duel.m_duelPhase.ToString(),
            acceptingMoves = duel.IsAwaitingMoves && duel.Duel.m_duelPhase.ToString() == PlanningPhase,
            yourSlot = me?.SlotIndex,
            enemiesAlive = (int) duel.AliveCreatureCount,
            alliesAlive = (int) duel.AlivePlayerCount,
            circles,
            hand,
        };
    }

    private static async Task<object> State(JsonElement body) {
        var player = await FindPlayer(body);
        var handle = await FindDuel(player);
        if (handle is null) {
            return new { inDuel = false, player = player.Name, zone = player.Wizard.Zone, wizardIsInDuel = player.Wizard.IsInDuel };
        }

        return Snapshot(player, handle);
    }

    // ---------------------------------------------------------------- starting a battle

    private sealed record Creature(IActorRef Actor, ZoneEntity Entity, string Name, ulong TemplateId, float Distance);

    private static async Task<List<Creature>> CreaturesNear(LivePlayer player) {
        var zone = await ZoneOf(player);
        var here = player.CoreObject.m_location;
        var found = new List<Creature>();
        foreach (var supervisor in Children(zone).Where(c => c.Path.Name is "ZonePathSupervisor" or "ZoneObjectSupervisor")) {
            // Creatures stand under the supervisor itself or one level down (a path, itself an entity, holds the creatures that walk it).
            foreach (var child in Children(supervisor).SelectMany(c => Children(c).Prepend(c))) {
                var entity = EntityOf(child);
                if (entity?.ActiveGameObject is null || entity.GetComponentOfType<CombatCreatureAIComponent>() is null) {
                    continue;
                }

                var there = entity.ActiveGameObject.m_location;
                var distance = MathF.Sqrt(MathF.Pow(there.X - here.X, 2) + MathF.Pow(there.Y - here.Y, 2));
                var templateId = entity.ActiveGameObject.m_templateID.Full;
                found.Add(new Creature(child, entity, NameOf(templateId), templateId, distance));
            }
        }

        return [.. found.OrderBy(c => c.Distance)];
    }

    private static async Task<object> Creatures(JsonElement body) {
        var player = await FindPlayer(body);
        var filter = Str(body, "creature");
        var limit = Num(body, "limit") ?? 25;
        var list = (await CreaturesNear(player))
            .Where(c => string.IsNullOrWhiteSpace(filter) || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || c.TemplateId.ToString() == filter)
            .ToList();

        return new {
            zone = player.Wizard.Zone,
            total = list.Count,
            creatures = list.Take(limit).Select(c => new {
                actor = c.Actor.Path.Name,
                name = c.Name,
                templateId = c.TemplateId,
                distance = (int) c.Distance,
                x = (int) c.Entity.ActiveGameObject.m_location.X,
                y = (int) c.Entity.ActiveGameObject.m_location.Y,
            }),
        };
    }

    private static async Task<object> Start(JsonElement body) {
        var player = await FindPlayer(body);
        var existing = await FindDuel(player);
        if (existing is not null) {
            throw new InvalidOperationException("The player is already in a duel (see combat_state).");
        }

        var filter = Str(body, "creature");
        var pick = (await CreaturesNear(player))
            .FirstOrDefault(c => string.IsNullOrWhiteSpace(filter) || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || c.TemplateId.ToString() == filter || c.Actor.Path.Name == filter)
            ?? throw new InvalidOperationException(string.IsNullOrWhiteSpace(filter)
                ? $"No fightable creature in {player.Wizard.Zone}. Teleport to a zone with monsters first (game_command 'teleport <zone>')."
                : $"No creature matching '{filter}' in {player.Wizard.Zone} (see combat_creatures).");

        var zone = await ZoneOf(player);
        Child(zone, "ZoneSigilSupervisor").Tell(new ZONE_102_PROTOCOL.MSG_REQUESTCOMBATSIGIL {
            StartingParticipants = new Dictionary<IActorRef, CoreObject> {
                { pick.Actor, pick.Entity.ActiveGameObject },
                { player.Session, player.CoreObject },
            },
        });

        // The sigil answers by starting the duel; wait for it to show up.
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline) {
            await Task.Delay(150);
            if (await FindDuel(player) is { } handle) {
                return new { started = true, against = pick.Name, creatureActor = pick.Actor.Path.Name, state = Snapshot(player, handle) };
            }
        }

        throw new InvalidOperationException($"The duel with {pick.Name} did not start. The nearest sigil may be busy, or the creature is already fighting.");
    }

    // ---------------------------------------------------------------- playing a move

    private static async Task<bool> WaitForPlanning(LivePlayer player, double seconds, CancellationToken token) {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline && !token.IsCancellationRequested) {
            var handle = await FindDuel(player);
            if (handle is null) {
                return false;
            }

            if (handle.Duel.IsAwaitingMoves && handle.Duel.Duel.m_duelPhase.ToString() == PlanningPhase) {
                // The hand is sent right after the phase starts; give that a moment.
                await Task.Delay(250, token);

                return true;
            }

            await Task.Delay(100, token);
        }

        return false;
    }

    private static COMBAT_106_PROTOCOL.MSG_ACTORCOMBATMOVE MoveMessage(LivePlayer player, CombatMoveType type, int card, int target)
        => new() {
            Actor = player.Session,
            MoveType = (byte) type,
            SpellSelection = (byte) card,
            SpellTarget = target < 0 ? uint.MaxValue : (uint) target,
            RawSpellTarget = target < 0 ? uint.MaxValue : 1u << target,
            TimeLeft = 10,
            ShadowPactTarget = -1,
            SelectedTieredSpellID = 0,
        };

    /// <summary>Plays one move for the player; returns what was played. Throws when the move cannot be played.</summary>
    private static async Task<string> PlayMove(LivePlayer player, JsonElement body, CancellationToken token) {
        var action = (Str(body, "action") ?? "cast").ToLowerInvariant();
        var waitSeconds = Num(body, "waitSeconds") ?? 45;

        if (!await WaitForPlanning(player, waitSeconds, token)) {
            var gone = await FindDuel(player) is null;
            throw new InvalidOperationException(gone ? "The player is not in a duel." : $"The duel did not reach its planning phase within {waitSeconds}s.");
        }

        var handle = await FindDuel(player) ?? throw new InvalidOperationException("The duel ended.");
        var me = handle.Duel.ActiveSubCircles.First(c => Fights(c, player));
        var enemies = handle.Duel.ActiveSubCircles.Where(c => c.OccupiedTeam == CombatTeam.Monster && c.IsAlive).ToList();

        switch (action) {
            case "pass":
                handle.Sigil.Tell(MoveMessage(player, CombatMoveType.Pass, 0, -1));

                return "passed";
            case "flee":
                handle.Sigil.Tell(MoveMessage(player, CombatMoveType.Flee, 0, -1));

                return "fled";
            case "cast" or "discard": {
                var wanted = Str(body, "spell");
                var card = Num(body, "card");
                if (card is null && wanted is not null) {
                    card = FindCard(me, wanted) ?? throw new InvalidOperationException($"No castable card matches '{wanted}' in the hand.");
                }

                if (card is null) {
                    throw new InvalidOperationException("Give 'card' (hand index from combat_state) or 'spell' (name or id).");
                }

                var spell = me.GetSpellFromLastHand((byte) card.Value) ?? throw new InvalidOperationException($"Hand slot {card} is empty.");
                if (action == "discard") {
                    handle.Sigil.Tell(MoveMessage(player, CombatMoveType.Discard, card.Value, -1));

                    return $"discarded {NameOf(spell.m_templateID)}";
                }

                if (!me.HasPipsForSpell(spell)) {
                    throw new InvalidOperationException($"Not enough pips (or mana) for {NameOf(spell.m_templateID)}.");
                }

                var target = Target(body, me, enemies, spell);
                handle.Sigil.Tell(MoveMessage(player, CombatMoveType.Attack, card.Value, target));

                return $"cast {NameOf(spell.m_templateID)} (card {card}) at slot {target}";
            }
            default:
                throw new InvalidOperationException($"Unknown action '{action}'. Use cast, pass, flee or discard.");
        }
    }

    private static int? FindCard(CombatDuelSubCircle me, string wanted) {
        for (byte i = 0; i < 24; i++) {
            var spell = me.GetSpellFromLastHand(i);
            if (spell is not null && me.HasPipsForSpell(spell)
                && (spell.m_templateID.ToString() == wanted || NameOf(spell.m_templateID).Contains(wanted, StringComparison.OrdinalIgnoreCase))) {
                return i;
            }
        }

        return null;
    }

    /// <summary>A target slot for the spell: what the caller named, else an enemy for attacks and debuffs, else the caster.</summary>
    private static int Target(JsonElement body, CombatDuelSubCircle me, List<CombatDuelSubCircle> enemies, Spell spell) {
        if (Num(body, "target") is { } slot) {
            return slot;
        }

        var named = Str(body, "target")?.ToLowerInvariant();
        if (named == "self") {
            return me.SlotIndex;
        }

        var offensive = Kind(SpellEffectFilter.FilterSpellsByOutgoingDamage, spell) || Kind(SpellEffectFilter.FilterSpellsByDebuff, spell);
        if (named == "enemy" || (named is null && offensive)) {
            return (enemies.OrderBy(e => e.ParticipantGameStats.m_currentHitpoints).FirstOrDefault() ?? throw new InvalidOperationException("No living enemy to target.")).SlotIndex;
        }

        return me.SlotIndex;
    }

    private static async Task<object> Move(JsonElement body) {
        var player = await FindPlayer(body);
        var played = await PlayMove(player, body, CancellationToken.None);

        // Optionally hold until the round has played out (the next planning phase, or the end of the duel).
        if (Flag(body, "waitForRound", false)) {
            var round = (await FindDuel(player))?.Duel.Duel.m_roundNum;
            var deadline = DateTime.UtcNow.AddSeconds(Num(body, "waitSeconds") ?? 45);
            while (DateTime.UtcNow < deadline) {
                await Task.Delay(200);
                var handle = await FindDuel(player);
                if (handle is null || (handle.Duel.Duel.m_roundNum != round && handle.Duel.IsAwaitingMoves)) {
                    break;
                }
            }
        }
        else {
            await Task.Delay(400);
        }

        var after = await FindDuel(player);

        return new { played, state = after is null ? (object) new { inDuel = false } : Snapshot(player, after) };
    }

    // ---------------------------------------------------------------- playing the whole fight

    private static Task<object> Auto(JsonElement body) {
        var maxRounds = Num(body, "maxRounds") ?? 40;
        var fleeBelow = Num(body, "fleeBelowPercent") ?? 0;
        var healBelow = Num(body, "healBelowPercent") ?? 35;
        var refillPips = Flag(body, "refillPips", false);
        var filter = Str(body, "player");
        var job = AgentJobs.Start("combat_auto", async (job, token) => {
            var player = await FindPlayer(JsonDocument.Parse(JsonSerializer.Serialize(new { player = filter })).RootElement);
            var rounds = 0;
            var outcome = "stopped";
            while (!token.IsCancellationRequested && rounds < maxRounds) {
                if (!await WaitForPlanning(player, 60, token)) {
                    break;
                }

                var handle = await FindDuel(player);
                if (handle is null) {
                    break;
                }

                var me = handle.Duel.ActiveSubCircles.FirstOrDefault(c => Fights(c, player));
                if (me is null || !me.IsAlive) {
                    outcome = "lost";
                    break;
                }

                var enemies = handle.Duel.ActiveSubCircles.Where(c => c.OccupiedTeam == CombatTeam.Monster && c.IsAlive).ToList();
                if (enemies.Count == 0) {
                    outcome = "won";
                    break;
                }

                rounds++;
                if (refillPips) {
                    // The same helper as the in-game .pip give light: fills every free slot with normal pips.
                    me.GivePips("light", -1);
                }

                var hpPercent = 100 * me.ParticipantGameStats.m_currentHitpoints / Math.Max(1, me.ParticipantGameStats.m_baseHitpoints);
                var cards = Enumerable.Range(0, 24).Select(i => (Card: i, Spell: me.GetSpellFromLastHand((byte) i)))
                    .Where(x => x.Spell is not null && me.HasPipsForSpell(x.Spell)).ToList();
                var template = (Func<(int Card, Spell Spell), bool>) (x => CoreObjectFactory.GetCoreTemplate(x.Spell.m_templateID) is SpellTemplate t && CombatEnchantments.IsEnchantment(t));

                string played;
                if (fleeBelow > 0 && hpPercent < fleeBelow) {
                    handle.Sigil.Tell(MoveMessage(player, CombatMoveType.Flee, 0, -1));
                    played = "fled";
                    outcome = "fled";
                }
                else {
                    var heal = cards.Where(x => !template(x) && Kind(SpellEffectFilter.FilterSpellsByHealing, x.Spell)).OrderByDescending(x => x.Spell.m_pipCost.m_spellRank).FirstOrDefault();
                    var attack = cards.Where(x => !template(x) && Kind(SpellEffectFilter.FilterSpellsByOutgoingDamage, x.Spell)).OrderByDescending(x => x.Spell.m_pipCost.m_spellRank).FirstOrDefault();
                    var weakest = enemies.OrderBy(e => e.ParticipantGameStats.m_currentHitpoints).First();
                    if (hpPercent < healBelow && heal.Spell is not null) {
                        handle.Sigil.Tell(MoveMessage(player, CombatMoveType.Attack, heal.Card, me.SlotIndex));
                        played = $"heal {NameOf(heal.Spell.m_templateID)}";
                    }
                    else if (attack.Spell is not null) {
                        handle.Sigil.Tell(MoveMessage(player, CombatMoveType.Attack, attack.Card, weakest.SlotIndex));
                        played = $"cast {NameOf(attack.Spell.m_templateID)} at slot {weakest.SlotIndex}";
                    }
                    else {
                        handle.Sigil.Tell(MoveMessage(player, CombatMoveType.Pass, 0, -1));
                        var blocked = Enumerable.Range(0, 24).Select(i => (Card: i, Spell: me.GetSpellFromLastHand((byte) i)))
                            .Where(x => x.Spell is not null && Kind(SpellEffectFilter.FilterSpellsByOutgoingDamage, x.Spell))
                            .Select(x => $"{NameOf(x.Spell.m_templateID)}: {WhyNot(me, x.Spell)}").ToList();
                        played = "pass (no castable damage card" + (blocked.Count > 0 ? "; " + string.Join("; ", blocked) : "; the hand has no damage card") + ")";
                    }
                }

                job.Log($"round {handle.Duel.Duel.m_roundNum}: {played} | you {me.ParticipantGameStats.m_currentHitpoints}/{me.ParticipantGameStats.m_baseHitpoints} | " +
                    string.Join(", ", enemies.Select(e => $"s{e.SlotIndex} {NameOf(e.ParticipantObject.m_templateID.Full)} {e.ParticipantGameStats.m_currentHitpoints}/{e.ParticipantGameStats.m_baseHitpoints}")));

                if (outcome == "fled") {
                    break;
                }

                // Wait out the round: until the next planning phase, or the duel is over.
                var round = handle.Duel.Duel.m_roundNum;
                var deadline = DateTime.UtcNow.AddSeconds(90);
                while (DateTime.UtcNow < deadline && !token.IsCancellationRequested) {
                    await Task.Delay(250, token);
                    var now = await FindDuel(player);
                    if (now is null || now.Duel.Duel.m_roundNum != round) {
                        break;
                    }
                }

            }

            if (outcome == "stopped") {
                var wizard = player.Wizard;
                outcome = token.IsCancellationRequested ? "cancelled" : rounds >= maxRounds ? "round limit reached" : await FindDuel(player) is null ? "ended" : "stopped";
                job.Log($"finished: {outcome} after {rounds} rounds; wizard in duel: {wizard.IsInDuel}");
            }
            else {
                job.Log($"finished: {outcome} after {rounds} rounds");
            }

            return new { outcome, rounds };
        });

        return Task.FromResult<object>(new { jobId = job.Id, message = "Playing the fight in the background. Read it with job_get." });
    }

}
