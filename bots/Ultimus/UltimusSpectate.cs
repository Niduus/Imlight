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
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Akka.Actor;

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

    private sealed record Now(string Quest, string Step, string Doing, string Zone, float X, float Y, bool HasSpot);

    // Kept per seat (Ultimus and each TCO7 member has its own; UltimusSeat).
    private static readonly SeatLocal<Now> s_nowSeat = new(() => new Now("", "", "", "", 0, 0, false));
    private static Now s_now { get => s_nowSeat.Value; set => s_nowSeat.Value = value; }

    /// <summary>
    /// The quest Ultimus plays now (its code), the step he is on and what he is doing (from the Director's status), and
    /// the spot in his area he is heading to for it (when he has one).
    /// </summary>
    internal static void NoteStatus(string quest, string step, string doing, string zone = "", float? x = null, float? y = null)
        => s_now = new(quest ?? "", step ?? "", doing ?? "", zone ?? "", x ?? 0, y ?? 0, x is not null && y is not null);

    internal static string Quest => s_now.Quest;

    // Kept per seat (Ultimus and each TCO7 member has its own; UltimusSeat).
    private static readonly SeatLocal<JsonObject> s_detailSeat = new();
    private static JsonObject s_detail { get => s_detailSeat.Value; set => s_detailSeat.Value = value; }

    /// <summary>His quest tracker's details (UltimusDirector.Tracker.cs), for the Spectator's quest box.</summary>
    internal static void NoteDetail(JsonObject detail) => s_detail = (JsonObject) detail?.DeepClone();

    /// <summary>A copy of his quest tracker's details, or null before the first status.</summary>
    internal static JsonObject Detail => (JsonObject) s_detail?.DeepClone();

    internal static string Step => s_now.Step;

    internal static string Doing => s_now.Doing;

    /// <summary>
    /// The way drawn on Ultimus's own minimap (Niduus, 2026-11-14: "have it draw to the quest like how it does for the
    /// player in the normal UI"): the Quest Tracker's own route of his quest (the shared route finder: legal walk,
    /// teleporters, doors and exits toward the step's area), from where he stands. When that finds no spot for the step
    /// here, the spot Ultimus himself is heading to for it, along the same legal walk.
    /// </summary>
    internal static JsonObject Route(Wizard wizard, IActorRef zone) {
        var now = s_now;
        var held = wizard.QuestBehavior?.CurrentQuestInstances?.FirstOrDefault(q => q is not null
            && string.Equals(q.QuestName, now.Quest, StringComparison.OrdinalIgnoreCase));
        var quest = held?.QuestName ?? now.Quest;
        if (held is not null && Tools.OverlayQuestRoute.Build(wizard, zone, quest) is { } route
            && route["points"] is JsonArray { Count: > 0 }) {
            return route;
        }

        string zonePath = wizard.Zone ?? "";
        if (!now.HasSpot || !Tools.OverlayQuestTracker.SameZone(now.Zone, zonePath)) {
            return null;
        }

        var here = wizard.Location;
        var walk = new JsonArray();
        foreach (var spot in Tools.OverlayQuestPath.Route(wizard.CharId, zonePath, here.X, here.Y, now.X, now.Y, Tools.RouteMobs.Avoid(zone, now.X, now.Y), here.Z)) {
            walk.Add(spot.Pad ? new JsonArray(Round(spot.X), Round(spot.Y), 1) : new JsonArray(Round(spot.X), Round(spot.Y)));
        }

        var template = held is null ? null : QuestTemplateCollection.GetQuestByName(held.QuestName);
        return new JsonObject {
            ["quest"] = quest.Length > 0 ? quest : "ultimus-spot",
            ["t"] = template is null ? Short(now.Step, 120) : Tools.OverlayQuestTracker.English(template.m_questTitle, quest),
            ["k"] = template is null ? "main" : Tools.QuestStory.KindOf(template),
            ["points"] = new JsonArray(new JsonObject { ["x"] = Round(now.X), ["y"] = Round(now.Y), ["n"] = Short(now.Step, 60) }),
            ["path"] = walk,
        };
    }

    /// <summary>
    /// The way of <paramref name="quest"/> for another bot (the Quest Team Spectator): the Quest Tracker's own route from
    /// where it stands (the shared route finder), or null when it does not hold the quest or no spot is found here.
    /// </summary>
    internal static JsonObject RouteFor(Wizard wizard, IActorRef zone, string quest) {
        if (string.IsNullOrEmpty(quest) || wizard.QuestBehavior?.CurrentQuestInstances?.Any(q => q is not null
                && string.Equals(q.QuestName, quest, StringComparison.OrdinalIgnoreCase)) != true) {
            return null;
        }

        return Tools.OverlayQuestRoute.Build(wizard, zone, quest) is { } route && route["points"] is JsonArray { Count: > 0 } ? route : null;
    }

    private static int Round(float value) => float.IsFinite(value) ? (int) MathF.Round(value) : 0;

    private static readonly object s_lock = new();

    // One battle book per wizard watched (Niduus, 2026-11-15: the Quest Team Spectator gets a battle box too): "" is
    // Ultimus, a role (Ice, Life, Death, Storm) a Quest Team wizard.
    private sealed class Book {
        public JsonObject Round;
        public ulong Battle;
        public readonly LinkedList<string> Casts = new();
    }

    // Kept per seat (Ultimus and each TCO7 member has its own; UltimusSeat).
    private static readonly SeatLocal<Dictionary<string, Book>> s_booksSeat = new(() => new Dictionary<string, Book>(StringComparer.OrdinalIgnoreCase));
    private static Dictionary<string, Book> s_books { get => s_booksSeat.Value; set => s_booksSeat.Value = value; }

    private static Book BookOf(string who) => s_books.TryGetValue(who ?? "", out var book) ? book : s_books[who ?? ""] = new Book();

    /// <summary>
    /// One of Ultimus's rounds: his hand as he saw it, his pips and health, the enemies, what he threw away and what he
    /// picked to cast (and on whom).
    /// </summary>
    internal static void NoteRound(ulong duel, int round, CombatDuelSubCircle me, IReadOnlyList<CombatDuelSubCircle> circles,
                                   UltimusTactics.Choice choice, IReadOnlyList<string> discards)
        => NoteRound("", duel, round, me, circles, choice.Move == UltimusTactics.Move.Cast, choice.Card, choice.Target, choice.Why, discards);

    /// <summary>
    /// One round of the wizard <paramref name="who"/> ("" Ultimus, else a Quest Team role), the same way: its hand, pips,
    /// health, the enemies and allies, what it threw away and what it cast. Read only.
    /// </summary>
    internal static void NoteRound(string who, ulong duel, int round, CombatDuelSubCircle me, IReadOnlyList<CombatDuelSubCircle> circles,
                                   bool cast, int card, int targetSlot, string why, IReadOnlyList<string> discards) {
        try {
            var hand = new JsonArray();
            var picked = "";
            for (byte i = 0; i < 24; i++) {
                if (me.GetSpellFromLastHand(i) is not { } spell || RewrittenAI.Read(spell) is not { } read) {
                    continue;
                }

                var name = read.Shown.Length > 0 ? read.Shown : read.Name;
                hand.Add(new JsonObject {
                    ["n"] = Short(name, 60), ["s"] = Short(read.School, 12), ["r"] = read.Rank, ["ok"] = me.HasPipsForSpell(spell),
                    ["pick"] = cast && card == i,
                });
                if (cast && card == i) {
                    picked = name;
                }
            }

            var pips = me.CombatParticipant?.m_pipCount;
            var enemies = new JsonArray();
            var allies = new JsonArray();
            foreach (var circle in circles.Where(c => c.Occupied && c != me)) {
                var row = new JsonObject {
                    ["n"] = Short(NameOf(circle, me, who), 60), ["hp"] = RewrittenAI.Health(circle), ["max"] = RewrittenAI.MostHealth(circle), ["alive"] = circle.IsAlive,
                };
                (circle.OccupiedTeam == me.OccupiedTeam ? allies : enemies).Add(row);
            }

            var target = targetSlot < 0 ? "" : circles.FirstOrDefault(c => c.SlotIndex == targetSlot) is { } on ? NameOf(on, me, who) : "";
            var pick = cast
                ? picked.Length == 0 ? "a spell" : picked + (target.Length > 0 ? " on " + target : targetSlot < 0 ? " on every enemy" : "")
                : "passes";
            var json = new JsonObject {
                ["round"] = round,
                ["hp"] = RewrittenAI.Health(me), ["max"] = RewrittenAI.MostHealth(me),
                ["pips"] = pips?.m_genericPips ?? 0, ["power"] = pips?.m_powerPips ?? 0, ["shadow"] = pips?.m_shadowPips ?? 0,
                ["hand"] = hand,
                ["pick"] = Short(pick, 120),
                ["why"] = Short(why ?? "", 160),
                ["discards"] = new JsonArray([.. discards.Take(8).Select(d => (JsonNode) Short(d, 60))]),
                ["enemies"] = enemies,
                ["allies"] = allies,
                ["at"] = DateTime.UtcNow.ToString("o"),
            };

            lock (s_lock) {
                var book = BookOf(who);
                if (book.Battle != duel) {
                    book.Battle = duel;
                    book.Casts.Clear();
                }

                // The same round looked at again (a player changed their pick) replaces that round's line, and keeps
                // what was thrown away at its first look.
                if (book.Round is not null && (int?) book.Round["round"] == round) {
                    if (book.Casts.Count > 0) {
                        book.Casts.RemoveFirst();
                    }

                    if (discards.Count == 0 && book.Round["discards"] is JsonArray thrown) {
                        json["discards"] = thrown.DeepClone();
                    }
                }

                book.Casts.AddFirst($"Round {round}: {(cast ? "casts " : "")}{pick}");
                while (book.Casts.Count > MAX_CASTS) {
                    book.Casts.RemoveLast();
                }

                book.Round = json;
            }
        }
        catch (Exception e) {
            Logger.Debug("Spectator: the round could not be noted: {0}", Logger.Args(e.Message));
        }
    }

    /// <summary>The battle of <paramref name="who"/> ("" Ultimus) as the overlay shows it, or null while it is not in one.</summary>
    internal static JsonObject Battle(ulong globalId, string who = "") {
        if (globalId == 0 || !CombatDuelComponent.IsInBattle(globalId)) {
            lock (s_lock) {
                s_books.Remove(who ?? "");
            }

            return null;
        }

        lock (s_lock) {
            var book = BookOf(who);
            var battle = book.Round?.DeepClone() as JsonObject ?? new JsonObject { ["round"] = 0, ["hand"] = new JsonArray() };
            battle["casts"] = new JsonArray([.. book.Casts.Select(c => (JsonNode) c)]);

            return battle;
        }
    }

    private static string NameOf(CombatDuelSubCircle circle, CombatDuelSubCircle me, string who) {
        if (who.Length > 0 && circle == me) {
            return "itself";
        }

        if (who.Length > 0 && circle.IsHumanPlayer) {
            // A Quest Team teammate by its locked name.
            return QuestTeam.QuestTeamDirector.Members.FirstOrDefault(m => m.Wizard is not null && m.GlobalId == (circle.ParticipantObject?.m_globalID.Full ?? 0))?.Name
                   ?? (circle.ParticipantObject?.m_debugName is { Length: > 0 } player ? player : $"slot {circle.SlotIndex + 1}");
        }

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
