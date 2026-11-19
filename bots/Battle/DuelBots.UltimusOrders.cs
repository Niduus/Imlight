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
using System.Text.RegularExpressions;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Ultimus;
using Imlight.CoreLib.WizardData.Models.Player;
using InHand = Imlight.CoreLib.Game.BotShared.BotCore.InHand;

namespace Imlight.CoreLib.Game.Zone.Components;

/// <summary>
/// His group leader's battle orders (UltimusOrders), played the way a player plays them: from his own hand, with his own
/// pips, on a target the card can really be cast on. "Cast Dragonblade" is a suggestion he weighs (a heal he needs comes
/// first); "Cast Dragonblade on Mitsu" is an order he follows when the card is in his hand. A card he lacks the pips for
/// waits up to three rounds; one he doesn't have, or a name not in the battle, he says so and plays his own way.
/// </summary>
internal sealed partial class DuelBots {

    private void UltimusSays(CombatDuelSubCircle member, string text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return;
        }

        // One line a round at most in battle chat; the rest goes to his Thoughts only.
        if (!UltimusOrders.MaySayThisRound(text)) {
            UltimusLink.Note(UltimusRoster.NAME, $"round {D.Duel.m_roundNum}: (would say \"{text}\", but already spoke this round)");
            return;
        }

        if (UltimusDirector.ActorOf(UltimusRole(member)) is { } wizard) {
            wizard.Tell(new UltimusMember.SayText(BotSocial.Scrub(text)), Akka.Actor.ActorRefs.NoSender);
        }
    }

    /// <summary>The leader is in this battle on his side and has not picked yet (he waits, as told).</summary>
    private bool UltimusLeaderStillPicking(CombatDuelSubCircle member, IReadOnlyList<CombatDuelSubCircle> circles) {
        if (UltimusOrders.LeaderFirst == 0) {
            return false;
        }

        var gid = Wizard.GetGameObjectId(UltimusOrders.LeaderFirst);
        return circles.Any(c => c.OccupiedTeam == member.OccupiedTeam && c.IsAlive && c.AddedToDuel && c.ParticipantObject?.m_globalID.Full == gid
                                && D.CombatResolver.GetQueuedAction(c) is null);
    }

    /// <summary>A flee or a draw ordered: done here (true: his pick waits for a later look, or there is none this round).</summary>
    private bool UltimusOrderedFleeOrDraw(CombatDuelSubCircle member, bool deadline) {
        foreach (var pending in UltimusOrders.Waiting()) {
            switch (pending.Order.Kind) {
                case UltimusOrders.Kind.Flee:
                    UltimusOrders.Done(pending);
                    UltimusLink.Note(UltimusRoster.NAME, $"round {D.Duel.m_roundNum}: {pending.ByName} (group leader) said to flee: presses Flee.");
                    UltimusSays(member, "Okay!");
                    // The leader's call, so he obeys; a flee in a dungeon still counts against the run (UltimusEffort).
                    var inDungeon = member._wizard is { } fleeing && Imlight.CoreLib.Game.World.DungeonRecall.IsInDungeon(fleeing.CharId);
                    UltimusEffort.Note(inDungeon ? UltimusEffort.Kind.DungeonLost : UltimusEffort.Kind.Fled,
                        $"{pending.ByName} (group leader) said to flee{(inDungeon ? " in a dungeon: the run starts over" : "")}");
                    UltimusDirector.ActorOf(UltimusRole(member))?.Tell(new UltimusMember.Flee(), Akka.Actor.ActorRefs.NoSender);
                    return true;
                case UltimusOrders.Kind.Draw when !deadline:
                    UltimusOrders.Done(pending);
                    if (member.VaultRemainingCount <= 0) {
                        UltimusSays(member, "I don't have any treasure cards left in my deck.");
                        continue;
                    }

                    if (member.HandCount >= 7) {
                        UltimusSays(member, "My hand is full, I'll draw once I've played a card.");
                        continue;
                    }

                    UltimusLink.Note(UltimusRoster.NAME, $"round {D.Duel.m_roundNum}: {pending.ByName} (group leader) said to draw: draws a treasure card.");
                    UltimusSays(member, "Okay!");
                    _ultimusDrew.Add((D.Duel.m_roundNum, member.SlotIndex));
                    UltimusDirector.ActorOf(UltimusRole(member))?.Tell(new UltimusMember.DrawTreasure(), Akka.Actor.ActorRefs.NoSender);
                    D.BotTimer(ULTIMUS_THINK_KEY, new UltimusThink(D.Duel.m_roundNum, false), TimeSpan.FromSeconds(1.5));
                    return true;
            }
        }

        return false;
    }

    /// <summary>His pick with the leader's orders laid over it (his own pick when there is none he can follow).</summary>
    private UltimusTactics.Choice UltimusOrdered(CombatDuelSubCircle member, IReadOnlyList<CombatDuelSubCircle> circles, UltimusTactics.Choice own,
                                                  List<BotBattle.Move> moves) {
        var hand = UltimusTactics.Hand(member);
        var enemies = circles.Where(c => c.OccupiedTeam != member.OccupiedTeam && c.IsAlive).ToList();
        var allies = circles.Where(c => c.OccupiedTeam == member.OccupiedTeam && c.IsAlive).ToList();
        UltimusTactics.Choice ordered = null;
        // His real hand, for "what's in your hand?" in chat (UltimusDirector.SpellTalk.cs).
        UltimusOrders.SetHand(hand.Select(h => (h.Card.Shown, h.Castable)));

        foreach (var pending in UltimusOrders.Waiting()) {
            var order = pending.Order;
            if (pending.LastRound != D.Duel.m_roundNum) {
                pending.LastRound = D.Duel.m_roundNum;
                pending.Rounds++;
            }

            if (order.Kind == UltimusOrders.Kind.Discard) {
                UltimusOrders.Done(pending);
                if (FindCard(hand, order.Card, false) is { } thrown) {
                    moves.Add(BotBattle.Move.Discard(thrown.Index, $"discards {thrown.Card.Shown}, as {pending.ByName} said"));
                }
                else {
                    UltimusSays(member, $"I don't have {order.Card} in my hand.");
                }

                continue;
            }

            if (ordered is not null || order.Kind is UltimusOrders.Kind.Flee or UltimusOrders.Kind.Draw) {
                continue;
            }

            if (order.Kind == UltimusOrders.Kind.Pass) {
                UltimusOrders.Done(pending);
                ordered = new(UltimusTactics.Move.Pass, 0, -1, $"passes, as {pending.ByName} (group leader) said");
                UltimusSays(member, "Okay!");
                continue;
            }

            // The card: named ("cast Dragonblade"), or the best of its kind ("heal me", "shield Mitsu", "attack").
            InHand card = order.Kind is UltimusOrders.Kind.Cast or UltimusOrders.Kind.Suggest
                ? FindCard(hand, order.Card, true) ?? FindCard(hand, order.Card, false)
                : BestFor(hand, order, member, pending.By, allies);
            var what = order.Card.Length > 0 ? order.Card : order.Kind is UltimusOrders.Kind.Attack or UltimusOrders.Kind.Focus ? "hit" : order.Kind.ToString().ToLowerInvariant();
            if (card is null) {
                // A named card still in his deck waits for him to draw it (three rounds at most), as a player's would; he says so
                // once (Niduus, 2026-11-16: he said he'd use Hex and never cast it, with no word why).
                if (order.Card.Length > 0 && InDeck(member, order.Card) is { } inDeck) {
                    UltimusLink.Note(UltimusRoster.NAME, $"round {D.Duel.m_roundNum}: {inDeck} ({pending.ByName}'s order) is in his deck but not in his hand yet: waits to draw it (round {pending.Rounds} of 3).");
                    if (!pending.ToldNoPips) {
                        pending.ToldNoPips = true;
                        UltimusSays(member, $"{inDeck} isn't in my hand yet, I'll cast it as soon as I draw it.");
                    }

                    continue;
                }

                // True Form's hand is dealt for the orders waiting (TrueFormBattle.Deal): the card comes with his next hand.
                if (UltimusSeat.Current.IsTrueForm && pending.Rounds <= 2 && TrueFormBattle.CanDealFor(order)) {
                    UltimusLink.Note(UltimusRoster.NAME, $"round {D.Duel.m_roundNum}: no {what} card in his hand for {pending.ByName}'s order yet: it comes with his next hand.");
                    if (!pending.ToldNoPips) {
                        pending.ToldNoPips = true;
                        UltimusSays(member, $"I'll have a {what} card for that in my next hand.");
                    }

                    continue;
                }

                UltimusOrders.Done(pending);
                UltimusLink.Note(UltimusRoster.NAME, $"round {D.Duel.m_roundNum}: can't do {pending.ByName}'s order ({order.Kind} {what}): " +
                                                     (order.Card.Length > 0 ? $"{order.Card} is not in his hand or his deck." : $"no {what} card in his hand."));
                if (order.Kind != UltimusOrders.Kind.Focus) {
                    UltimusSays(member, order.Card.Length > 0 ? $"I don't have {order.Card} in my deck." : $"I don't have a {what} card in my hand.");
                }

                continue;
            }

            if (!card.Castable) {
                // Not enough pips yet: it waits for a later round (three at most), and he says so once.
                UltimusLink.Note(UltimusRoster.NAME, $"round {D.Duel.m_roundNum}: {card.Card.Shown} ({pending.ByName}'s order) needs more pips: waits (round {pending.Rounds} of 3).");
                if (!pending.ToldNoPips) {
                    pending.ToldNoPips = true;
                    UltimusSays(member, $"Not enough pips for {card.Card.Shown} yet, I'll cast it when I can.");
                }

                continue;
            }

            // On whom: the name said, else the card's natural target.
            var target = UltimusOrderTarget(order.Target, pending.By, member, card.Card, enemies, allies, own, out var problem);
            if (problem is not null) {
                UltimusOrders.Done(pending);
                UltimusSays(member, problem);
                continue;
            }

            // A suggestion is weighed: a heal or a shield he picked himself comes first.
            if (order.Kind == UltimusOrders.Kind.Suggest && own.Move == UltimusTactics.Move.Cast
                && member.GetSpellFromLastHand((byte) own.Card) is { } mine && RewrittenAI.Read(mine) is { } minePick
                && (minePick.Heal || minePick.Shield) && !minePick.Hit && minePick.Id != card.Card.Id) {
                UltimusLink.Note(UltimusRoster.NAME, $"round {D.Duel.m_roundNum}: thinks about {card.Card.Shown} ({pending.ByName}'s idea), but {own.Why} comes first.");
                if (!pending.ToldNoPips) {
                    pending.ToldNoPips = true;
                    UltimusSays(member, $"Good idea, but I need to {minePick.Kind} first. {card.Card.Shown} next!");
                }

                continue;
            }

            UltimusOrders.Done(pending);
            UltimusSays(member, "Okay!");
            var on = target < 0 ? "" : $" on {UltimusTactics.Name(circles.First(c => c.SlotIndex == target))}";
            ordered = new(UltimusTactics.Move.Cast, card.Index, target,
                $"casts {card.Card.Shown}{on}, as {pending.ByName} (group leader) {(order.Kind == UltimusOrders.Kind.Suggest ? "suggested" : "said")}");
        }

        if (ordered is not null) {
            return ordered;
        }

        // Told to play defensive: a heal when anyone of his side is low, else a shield on himself every other round, before
        // his own hit (Niduus, 2026-11-16: "Play defensive" got an "Okay!" and nothing changed).
        if (UltimusOrders.Defensive && Defend(member, hand, allies, own) is { } safe) {
            return safe;
        }

        // A focus ("attack the boss") turns his own single hit onto that enemy.
        if (UltimusOrders.Focus is { Length: > 0 } focus && own.Move == UltimusTactics.Move.Cast
            && member.GetSpellFromLastHand((byte) own.Card) is { } spell && RewrittenAI.Read(spell) is { Hit: true, HitsAll: false }
            && FindCircle(focus, 0, member, enemies) is { } foe && foe.SlotIndex != own.Target) {
            return own with { Target = foe.SlotIndex, Why = $"{own.Why}, at {UltimusTactics.Name(foe)} (the group leader's target)" };
        }

        return own;
    }

    /// <summary>
    /// True Form only: an order waits whose card is not in the hand he was dealt (it came after the deal), so he is dealt again
    /// before he picks, with that card in it (TrueFormBattle.Deal reads the orders). Once a round at most.
    /// </summary>
    private bool TrueFormOrderMissing(CombatDuelSubCircle member, IReadOnlyList<CombatDuelSubCircle> circles) {
        if (!UltimusOrders.HasWaiting || D.CombatResolver.GetQueuedAction(member) is not null || !_trueFormRedealt.Add((D.Duel.m_roundNum, member.SlotIndex))) {
            return false;
        }

        var hand = UltimusTactics.Hand(member);
        var allies = circles.Where(c => c.OccupiedTeam == member.OccupiedTeam && c.IsAlive).ToList();
        foreach (var pending in UltimusOrders.Waiting()) {
            var order = pending.Order;
            if (!TrueFormBattle.CanDealFor(order)) {
                continue;
            }

            var card = order.Kind is UltimusOrders.Kind.Cast or UltimusOrders.Kind.Suggest
                ? FindCard(hand, order.Card, true)
                : BestFor(hand, order, member, pending.By, allies, true);
            if (card is null) {
                return true;
            }
        }

        return false;
    }

    private readonly HashSet<(int Round, int Slot)> _trueFormRedealt = [];

    private int _defendShieldRound = -10;

    private UltimusTactics.Choice Defend(CombatDuelSubCircle member, List<InHand> hand, List<CombatDuelSubCircle> allies, UltimusTactics.Choice own) {
        // His own pick already heals or shields: it stands.
        if (own.Move == UltimusTactics.Move.Cast && member.GetSpellFromLastHand((byte) own.Card) is { } mine && RewrittenAI.Read(mine) is { } minePick
            && (minePick.Heal || minePick.Shield)) {
            return null;
        }

        var hurt = allies.OrderBy(RewrittenAI.HealthShare).FirstOrDefault();
        if (hurt is not null && RewrittenAI.HealthShare(hurt) < 0.7f && BestOfKind(hand, UltimusOrders.Kind.Heal, true) is { } heal) {
            var on = heal.Card.HealForTeammate ? hurt : member;
            return new(UltimusTactics.Move.Cast, heal.Index, heal.Card.HealWholeTeam ? -1 : on.SlotIndex,
                $"casts {heal.Card.Shown} on {UltimusTactics.Name(on)} (playing defensive, as the group leader said)");
        }

        if (D.Duel.m_roundNum - _defendShieldRound >= 2 && BestOfKind(hand, UltimusOrders.Kind.Shield, true) is { } shield) {
            _defendShieldRound = D.Duel.m_roundNum;
            var on = shield.Card.ShieldForTeammate && hurt is not null ? hurt : member;
            return new(UltimusTactics.Move.Cast, shield.Index, shield.Card.ShieldWholeTeam ? -1 : on.SlotIndex,
                $"casts {shield.Card.Shown} on {UltimusTactics.Name(on)} (playing defensive, as the group leader said)");
        }

        return null;
    }

    // The shown name of a card of his deck that matches the name said, or null (his deck, not only this round's hand).
    private static string InDeck(CombatDuelSubCircle member, string name) {
        var want = Flat(Regex.Replace(name ?? "", @"^(the|a|an|my|your|tc|treasure card) ", "", RegexOptions.IgnoreCase));
        if (want.Length < 3 || member?._wizard?.SpellbookBehavior?.SpellList is not { } deck) {
            return null;
        }

        foreach (var spell in deck.Where(s => s is not null)) {
            var shown = BotShared.BotCards.CardOf((uint) spell.m_templateID)?.Shown ?? "";
            if (shown.Length > 0 && Flat(shown) == want) {
                return shown;
            }
        }

        return null;
    }

    private static string Flat(string text) => Regex.Replace((text ?? "").ToLowerInvariant(), "[^a-z0-9]", "");

    // A card of the hand by its name, as said ("dragonblade", "Fire Cat", "tc fire cat").
    private static InHand FindCard(List<InHand> hand, string name, bool castable) {
        var want = Flat(Regex.Replace(name ?? "", @"^(the|a|an|my|your|tc|treasure card) ", "", RegexOptions.IgnoreCase));
        if (want.Length < 3) {
            return null;
        }

        return hand.Where(h => !castable || h.Castable).FirstOrDefault(h => Flat(h.Card.Shown) == want || Flat(h.Card.Name) == want)
               ?? hand.Where(h => !castable || h.Castable).FirstOrDefault(h => Flat(h.Card.Shown).Contains(want) || (want.Contains(Flat(h.Card.Shown)) && Flat(h.Card.Shown).Length >= 4));
    }

    /// <summary>
    /// The best card of the order's kind for whom it is meant: a blade, heal or shield that can go on a teammate when it is for
    /// someone else (Niduus, 2026-11-19: "blade me" got Amplify, which only works on himself, and nothing was cast), one for
    /// the whole team for "us", and a hit-all for "kill them all". Castable first; else any, so the battle can say why not.
    /// </summary>
    private static InHand BestFor(List<InHand> hand, UltimusOrders.Order order, CombatDuelSubCircle member, ulong by, List<CombatDuelSubCircle> allies,
                                  bool strict = false) {
        var said = (order.Target ?? "").Trim().ToLowerInvariant();
        var team = said is "us" or "team" or "the team" or "everyone" or "all" or "all of us" or "everybody";
        var other = !team && said.Length > 0 && said is not ("you" or "yourself" or "urself" or "self")
                    && FindCircle(said, by, member, allies) is { } named && named != member;
        bool Fits(InHand h) => order.Kind switch {
            UltimusOrders.Kind.Heal => team ? h.Card.HealWholeTeam : !other || h.Card.HealForTeammate || h.Card.HealWholeTeam,
            UltimusOrders.Kind.Shield => team ? h.Card.ShieldWholeTeam : !other || h.Card.ShieldForTeammate || h.Card.ShieldWholeTeam,
            UltimusOrders.Kind.Blade => team ? h.Card.BladeWholeTeam
                : other ? h.Card.BladeForTeammate || h.Card.BladeWholeTeam || h.Card.BladeOnlyTeammate : !h.Card.BladeOnlyTeammate,
            UltimusOrders.Kind.Trap => order.Target != UltimusOrders.ALL_ENEMIES || h.Card.TrapAll,
            UltimusOrders.Kind.Attack => order.Target != UltimusOrders.ALL_ENEMIES || h.Card.HitsAll,
            _ => true,
        };

        var fitting = hand.Where(Fits).ToList();
        if (strict) {
            return BestOfKind(fitting, order.Kind, true);
        }

        return BestOfKind(fitting, order.Kind, true) ?? BestOfKind(hand, order.Kind, true) ?? BestOfKind(fitting, order.Kind, false)
               ?? BestOfKind(hand, order.Kind, false);
    }

    // The best card of a kind: the biggest heal, a shield, a blade, a trap, the hardest hit.
    private static InHand BestOfKind(List<InHand> hand, UltimusOrders.Kind kind, bool castable) {
        var cards = hand.Where(h => !castable || h.Castable);
        return kind switch {
            UltimusOrders.Kind.Heal => cards.Where(h => h.Card.Heal && !h.Card.Hit).OrderByDescending(h => h.Card.HealAmount).FirstOrDefault(),
            UltimusOrders.Kind.Shield => cards.FirstOrDefault(h => h.Card.Shield),
            UltimusOrders.Kind.Blade => cards.OrderByDescending(h => h.Card.BladePercent).FirstOrDefault(h => h.Card.Blade),
            UltimusOrders.Kind.Trap => cards.OrderByDescending(h => h.Card.TrapPercent).FirstOrDefault(h => h.Card.Trap),
            UltimusOrders.Kind.Attack or UltimusOrders.Kind.Focus => cards.Where(h => h.Card.Hit).OrderByDescending(h => h.Card.BaseDamage).FirstOrDefault(),
            _ => null,
        };
    }

    // An enemy or ally by name as said: "Mitsu", "momi", "the boss", "the cyclops"; "me" is the one who gave the order.
    private static CombatDuelSubCircle FindCircle(string said, ulong by, CombatDuelSubCircle member, IEnumerable<CombatDuelSubCircle> among) {
        var list = among.ToList();
        var t = (said ?? "").Trim().ToLowerInvariant();
        if (t is "me" or "myself") {
            var gid = Wizard.GetGameObjectId(by);
            return list.FirstOrDefault(c => c.ParticipantObject?.m_globalID.Full == gid);
        }

        if (t is "you" or "yourself" or "urself" or "self" or "ultimus") {
            return list.FirstOrDefault(c => c == member);
        }

        if (t is "boss" or "the boss") {
            return list.FirstOrDefault(RewrittenAI.IsBoss);
        }

        var want = Flat(Regex.Replace(t, @"^(the|a|an) ", ""));
        if (want.Length < 2) {
            return null;
        }

        return list.FirstOrDefault(c => Flat(UltimusTactics.Name(c)) == want || Flat(c.ParticipantObject?.m_debugName) == want)
               ?? list.FirstOrDefault(c => Flat(UltimusTactics.Name(c)).StartsWith(want, StringComparison.Ordinal)
                                           || Flat(c.ParticipantObject?.m_debugName).StartsWith(want, StringComparison.Ordinal))
               ?? list.FirstOrDefault(c => want.Length >= 3 && (Flat(UltimusTactics.Name(c)).Contains(want) || Flat(c.ParticipantObject?.m_debugName).Contains(want)))
               // A name typed one letter off ("mitzu", "mnidy"): still that wizard (BotShared.BotOrderWords).
               ?? list.FirstOrDefault(c => BotOrderWords.NameMatches(want, UltimusTactics.Name(c)));
    }

    // The circle a card goes on, as the game allows it: an enemy card on an enemy, a helping card on his side (one that
    // works only on himself, on himself); a card that hits all or helps the whole team needs no target (-1).
    private static int UltimusOrderTarget(string said, ulong by, CombatDuelSubCircle member, RewrittenAI.Card card, List<CombatDuelSubCircle> enemies,
                                          List<CombatDuelSubCircle> allies, UltimusTactics.Choice own, out string problem) {
        problem = null;
        var forEnemy = card.Hit || card.Trap || card.Weakness || card.Stun || card.RemovesCharms;
        var team = (card.Heal && card.HealWholeTeam) || (card.Blade && card.BladeWholeTeam) || (card.Shield && card.ShieldWholeTeam);
        if ((forEnemy && card.HitsAll) || (forEnemy && card.TrapAll) || (!forEnemy && team)) {
            return -1;
        }

        var wanted = string.IsNullOrWhiteSpace(said) ? null : said.Trim().ToLowerInvariant();
        if (wanted is "us" or "team" or "the team" or "everyone" or "all" or "all of us" or "everybody") {
            wanted = null;
        }

        if (forEnemy) {
            if (wanted is null or "them" or "enemy" or "the enemy" or "enemies") {
                var ownTarget = enemies.FirstOrDefault(e => e.SlotIndex == own.Target);
                var focus = UltimusOrders.Focus is { Length: > 0 } f ? FindCircle(f, by, member, enemies) : null;
                return (focus ?? ownTarget ?? enemies.OrderBy(RewrittenAI.Health).First()).SlotIndex;
            }

            if (FindCircle(wanted, by, member, enemies) is { } foe) {
                return foe.SlotIndex;
            }

            problem = FindCircle(wanted, by, member, allies) is not null
                ? $"{card.Shown} only goes on an enemy, not on {wanted}."
                : $"I can't see {said} in this battle.";
            return -1;
        }

        var selfOnly = (card.Heal && !card.HealForTeammate) || (card.Blade && !card.BladeForTeammate && !card.BladeOnlyTeammate)
                       || (card.Shield && !card.ShieldForTeammate);
        CombatDuelSubCircle ally;
        if (wanted is null) {
            ally = selfOnly || card.Blade || card.Shield ? member : allies.OrderBy(RewrittenAI.HealthShare).First();
        }
        else if (FindCircle(wanted, by, member, allies) is { } named) {
            ally = named;
        }
        else {
            problem = FindCircle(wanted, by, member, enemies) is not null
                ? $"{card.Shown} only goes on my side, not on {wanted}."
                : $"I can't see {said} in this battle.";
            return -1;
        }

        if (selfOnly && ally != member) {
            problem = $"{card.Shown} only works on me, sorry.";
            return -1;
        }

        if (card.Blade && card.BladeOnlyTeammate && ally == member) {
            ally = allies.FirstOrDefault(a => a != member);
            if (ally is null) {
                problem = $"{card.Shown} only goes on a teammate.";
                return -1;
            }
        }

        return ally.SlotIndex;
    }

}
