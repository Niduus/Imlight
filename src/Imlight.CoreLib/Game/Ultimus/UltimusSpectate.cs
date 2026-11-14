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
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Zone.Components;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// What the overlay's Ultimus Spectator add-on shows (Niduus, 2026-11-13: "Spectate Ultimus, able to view his location and
/// quests via Minimap ... and when in battle, able to see his spells in a new UI module"). Read only: Ultimus's Director and
/// his battle write here what they already know (his quest and step, his hand and his pick each round), and the overlay
/// link reads it. Nothing here changes what Ultimus does.
/// </summary>
internal static class UltimusSpectate {

    /// <summary>[Overlay] UltimusSpectator: overlays may watch Ultimus on this server. On unless turned off; live.</summary>
    internal static bool On => ConfigurationManager.GetValue("Overlay.UltimusSpectator", true);

    private const int MAX_CASTS = 8;

    private sealed record Now(string Quest, string Step, string Doing);

    private static volatile Now s_now = new("", "", "");

    /// <summary>The quest Ultimus plays now (its code), the step he is on and what he is doing (from the Director's status).</summary>
    internal static void NoteStatus(string quest, string step, string doing) => s_now = new(quest ?? "", step ?? "", doing ?? "");

    internal static string Quest => s_now.Quest;

    internal static string Step => s_now.Step;

    internal static string Doing => s_now.Doing;

    private static readonly object s_lock = new();
    private static JsonObject s_round;
    private static ulong s_battle;
    private static readonly LinkedList<string> s_casts = new();

    /// <summary>
    /// One of Ultimus's rounds: his hand as he saw it, his pips and health, the enemies, what he threw away and what he
    /// picked to cast (and on whom).
    /// </summary>
    internal static void NoteRound(ulong duel, int round, CombatDuelSubCircle me, IReadOnlyList<CombatDuelSubCircle> circles,
                                   UltimusTactics.Choice choice, IReadOnlyList<string> discards) {
        try {
            var hand = new JsonArray();
            var picked = "";
            for (byte i = 0; i < 24; i++) {
                if (me.GetSpellFromLastHand(i) is not { } spell || RewrittenAI.Read(spell) is not { } card) {
                    continue;
                }

                var name = card.Shown.Length > 0 ? card.Shown : card.Name;
                hand.Add(new JsonObject {
                    ["n"] = Short(name, 60), ["s"] = Short(card.School, 12), ["r"] = card.Rank, ["ok"] = me.HasPipsForSpell(spell),
                    ["pick"] = choice.Move == UltimusTactics.Move.Cast && choice.Card == i,
                });
                if (choice.Move == UltimusTactics.Move.Cast && choice.Card == i) {
                    picked = name;
                }
            }

            var pips = me.CombatParticipant?.m_pipCount;
            var enemies = new JsonArray();
            var allies = new JsonArray();
            foreach (var circle in circles.Where(c => c.Occupied && c != me)) {
                var row = new JsonObject {
                    ["n"] = Short(NameOf(circle), 60), ["hp"] = RewrittenAI.Health(circle), ["max"] = RewrittenAI.MostHealth(circle), ["alive"] = circle.IsAlive,
                };
                (circle.OccupiedTeam == me.OccupiedTeam ? allies : enemies).Add(row);
            }

            var target = choice.Target < 0 ? "" : circles.FirstOrDefault(c => c.SlotIndex == choice.Target) is { } on ? NameOf(on) : "";
            var pick = choice.Move == UltimusTactics.Move.Cast
                ? picked.Length == 0 ? "a spell" : picked + (target.Length > 0 ? " on " + target : choice.Target < 0 ? " on every enemy" : "")
                : "passes";
            var json = new JsonObject {
                ["round"] = round,
                ["hp"] = RewrittenAI.Health(me), ["max"] = RewrittenAI.MostHealth(me),
                ["pips"] = pips?.m_genericPips ?? 0, ["power"] = pips?.m_powerPips ?? 0, ["shadow"] = pips?.m_shadowPips ?? 0,
                ["hand"] = hand,
                ["pick"] = Short(pick, 120),
                ["why"] = Short(choice.Why ?? "", 160),
                ["discards"] = new JsonArray([.. discards.Take(8).Select(d => (JsonNode) Short(d, 60))]),
                ["enemies"] = enemies,
                ["allies"] = allies,
                ["at"] = DateTime.UtcNow.ToString("o"),
            };

            lock (s_lock) {
                if (s_battle != duel) {
                    s_battle = duel;
                    s_casts.Clear();
                }

                // The same round looked at again (a player changed their pick) replaces that round's line, and keeps
                // what was thrown away at its first look.
                if (s_round is not null && (int?) s_round["round"] == round) {
                    if (s_casts.Count > 0) {
                        s_casts.RemoveFirst();
                    }

                    if (discards.Count == 0 && s_round["discards"] is JsonArray thrown) {
                        json["discards"] = thrown.DeepClone();
                    }
                }

                s_casts.AddFirst($"Round {round}: {(choice.Move == UltimusTactics.Move.Cast ? "casts " : "")}{pick}");
                while (s_casts.Count > MAX_CASTS) {
                    s_casts.RemoveLast();
                }

                s_round = json;
            }
        }
        catch (Exception e) {
            Logger.Debug("Ultimus spectator: the round could not be noted: {0}", Logger.Args(e.Message));
        }
    }

    /// <summary>Ultimus's battle as the overlay shows it, or null while he is not in one.</summary>
    internal static JsonObject Battle(ulong globalId) {
        if (globalId == 0 || !CombatDuelComponent.IsInBattle(globalId)) {
            lock (s_lock) {
                s_round = null;
                s_battle = 0;
                s_casts.Clear();
            }

            return null;
        }

        lock (s_lock) {
            var battle = s_round?.DeepClone() as JsonObject ?? new JsonObject { ["round"] = 0, ["hand"] = new JsonArray() };
            battle["casts"] = new JsonArray([.. s_casts.Select(c => (JsonNode) c)]);

            return battle;
        }
    }

    private static string NameOf(CombatDuelSubCircle circle) {
        if (circle.IsHumanPlayer) {
            return UltimusDirector.RoleOf(circle.ParticipantObject?.m_globalID.Full ?? 0) is not null ? "himself"
                : circle.ParticipantObject?.m_debugName is { Length: > 0 } wizard ? wizard : $"slot {circle.SlotIndex + 1}";
        }

        return CombatEffectApplicator.TargetName(circle);
    }

    private static string Short(string text, int max) {
        text = (text ?? "").ReplaceLineEndings(" ").Trim();

        return text.Length > max ? text[..max] : text;
    }

}
