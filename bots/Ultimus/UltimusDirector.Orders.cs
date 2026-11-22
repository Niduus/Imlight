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
using Akka.Actor;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Groups;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Components;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// His group leader's orders, heard in chat (UltimusOrders reads the line). Only the leader of the group he is in is listened
/// to (Niduus, 2026-11-15); anyone else hears who is. Out of battle he stays, follows, comes to them or leaves the group;
/// battle orders wait for his next pick (CombatDuelComponent.UltimusOrders.cs). A line his own reading can't place is put to
/// the AI (online) or to the wordings it learned before (offline), which only ever pick one order from a fixed list.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    private sealed record OrderUnderstood(UltimusVoice.Heard Heard, int Pick);

    private readonly Dictionary<ulong, DateTime> _notLeaderToldAt = [];

    /// <summary>True when the line was taken as orders (answered here); false: it is talk, answered as usual.</summary>
    private bool LeaderOrders(UltimusVoice.Heard heard, UltimusMember.Status me) {
        // "Use it" after "do you have Hex?": Hex (UltimusDirector.SpellTalk.cs). A card named back to him that way is meant: cast it.
        // "Kill yourself" and the like are never taken as an order, by anyone (his log, 2026-11-19: read as "go after yourself").
        if (BotShared.BotOrderWords.NotAnOrder(heard.Text)) {
            return false;
        }

        var text = WithCardTalked(heard, out var namedBack);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^\s*(?:ok(?:ay)?|alright|all right)?[,\s]*(?:get|be)\s+ready\s+to\s+", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // His own name in front ("Garm, heal me", "Ultimus: stay") is who it is for, not part of the order.
        text = WithoutMyName(text, me);
        var orders = UltimusOrders.Parse(text, part => CardSaid(me, part));
        if (namedBack) {
            orders = [.. orders.Select(o => o.Kind == UltimusOrders.Kind.Suggest ? o with { Kind = UltimusOrders.Kind.Cast } : o)];
        }

        var grouped = GroupRegistry.TryGetGroup(me.CharId, out var group);
        // When he leads the group himself (a player can make the bot its leader by mistake), anyone in it may give him orders
        // (his log, 2026-11-20: "Could you please attack the golem?" got "I only take orders from my group leader, True Form").
        var leader = grouped && ((group.LeaderCharId == heard.SpeakerCharId && group.LeaderCharId != me.CharId)
                                 || (group.LeaderCharId == me.CharId && group.Members.Any(m => m.CharId == heard.SpeakerCharId)));

        // A short line read by what came before: the answer to his question, "again", "Walter too" (UltimusDirector.OrderContext.cs).
        if (leader && OrderInContext(heard, me, text, orders)) {
            return true;
        }

        if (orders.Count == 0) {
            if (!leader || !heard.Typed || BotShared.BotOrderWords.AskOf(heard.Text) is null || heard.Text.Length > 80) {
                return false;
            }

            // "Use it" with no card talked about: he asks which, rather than guessing one (the AI once took it for a blade).
            if (s_itCard.IsMatch(heard.Text)) {
                SendReply(heard, "Which card do you mean? Say its name, like \"use Hex\".", "leader commands");
                return true;
            }

            // A word two letters off an order ("shleid me"): he asks "Did you mean "shield me"?" instead of guessing.
            if (AskWhatTheyMeant(heard, me, text)) {
                return true;
            }

            // Worded his own way: a wording learned before (offline), else the AI picks one order from the list (online).
            var known = UltimusAiLearned.CommandFor(heard.Text, UltimusOrders.PlainNames);
            if (known >= 0) {
                ApplyOrders(heard, me, [new UltimusOrders.Order(UltimusOrders.Plain[known].Kind)], "a wording I learned");
                return true;
            }

            if (!UltimusLocalAI.Ready) {
                return false;
            }

            UltimusAiLearned.UnderstandAsync(heard.Text, UltimusOrders.PlainNames)
                .ContinueWith(t => new OrderUnderstood(heard, t.IsCompletedSuccessfully ? t.Result : -1))
                .PipeTo(Self);
            return true;
        }

        if (!leader) {
            // Not his leader: he says who he listens to (once a minute each), and does nothing.
            if (!_notLeaderToldAt.TryGetValue(heard.SpeakerCharId, out var told) || DateTime.UtcNow - told > TimeSpan.FromMinutes(1)) {
                _notLeaderToldAt[heard.SpeakerCharId] = DateTime.UtcNow;
                var who = grouped && PlayerCheats.LiveName(group.LeaderCharId) is { Length: > 0 } name ? name : "";
                SendReply(heard, !grouped ? "Sorry, I only take orders from the leader of my group. Invite me to a group and lead it!"
                    : who.Length > 0 ? $"Sorry, I only take orders from my group leader, {who}." : "Sorry, I only take orders from my group leader.",
                    "leader commands");
            }

            Note("Ultimus", $"{heard.SpeakerName} gave him orders (\"{UltimusText.Short(heard.Text, 60)}\"), but is not his group leader: he does not follow them.");
            return true;
        }

        ApplyOrders(heard, me, orders, "my own reading");
        return true;
    }

    private void OnOrderUnderstood(OrderUnderstood m) {
        var me = _team.Count > 0 ? _team[0] : null;
        if (me is null || m.Pick < 0 || m.Pick >= UltimusOrders.Plain.Length) {
            UltimusVoice.Again(m.Heard);
            return;
        }

        ApplyOrders(m.Heard, me, [new UltimusOrders.Order(UltimusOrders.Plain[m.Pick].Kind)], "the AI's reading");
    }

    private void ApplyOrders(UltimusVoice.Heard heard, UltimusMember.Status me, List<UltimusOrders.Order> orders, string how) {
        var fighting = me.Wizard is not null && CombatDuelComponent.IsInBattle(me.GlobalId);
        // An order that could mean more than one person is asked about, not guessed ("Blade who: you, me or Walter?").
        orders = WithoutUnclear(heard, me, orders, fighting);
        if (orders.Count == 0) {
            return;
        }

        RememberOrders(heard, orders);
        var said = new List<string>();
        var wentIn = false;
        foreach (var order in orders) {
            switch (order.Kind) {
                case UltimusOrders.Kind.Stay:
                    UltimusOrders.SetStay(me.Zone, me.X, me.Y);
                    said.Add("I'll stay right here");
                    break;
                case UltimusOrders.Kind.Follow:
                case UltimusOrders.Kind.ComeToMe:
                    UltimusOrders.EndStay();
                    if (!fighting && GroupRegistry.TryGetGroup(me.CharId, out var g)
                                  && g.Members.FirstOrDefault(x => x.CharId == heard.SpeakerCharId) is { } lead
                                  && !OverlayQuestTracker.SameZone(lead.ZonePath ?? "", me.Zone ?? "")) {
                        // In another area: the group's "go to" button, as a player clicks their name.
                        _actors[0].Tell(new UltimusMember.GoToTeammate(heard.SpeakerCharId));
                    }

                    said.Add(order.Kind == UltimusOrders.Kind.Follow ? "Right behind you" : "On my way");
                    break;
                case UltimusOrders.Kind.GoTo:
                    said.Add(GoToNamed(heard, me, order.Target, fighting));
                    break;
                case UltimusOrders.Kind.LeaveGroup:
                    said.Clear();
                    LeaveParty($"{heard.SpeakerName} (the group leader) told him to", "Okay! Back to my own story. Thanks for the group!");
                    Note("Ultimus", $"ORDER from {heard.SpeakerName}: \"{UltimusText.Short(heard.Text, 60)}\" ({how}): leaves the group.");
                    return;
                case UltimusOrders.Kind.OwnWay:
                    UltimusOrders.Clear();
                    RememberBattleOrder(heard.SpeakerCharId, heard.SpeakerName, "");
                    said.Add("Okay, I'll play my own way again");
                    break;
                case UltimusOrders.Kind.LeaderFirst:
                    UltimusOrders.SetLeaderFirst(heard.SpeakerCharId);
                    RememberBattleOrder(heard.SpeakerCharId, heard.SpeakerName, "leader-first");
                    said.Add("Okay, you pick first in battle and I'll go after you");
                    break;
                case UltimusOrders.Kind.GoFirst:
                    UltimusOrders.SetGoFirst();
                    RememberBattleOrder(heard.SpeakerCharId, heard.SpeakerName, "go-first");
                    // Out of battle with an enemy right by them, "go first" means in first: he walks in and picks first.
                    said.Add(!fighting && EnterFightNow(me) ? "Okay, I'll go in first and pick first" : "Okay, I'll pick first");
                    break;
                case UltimusOrders.Kind.Defensive:
                    UltimusOrders.SetDefensive(true);
                    said.Add("Okay, I'll play defensive: heals and shields first");
                    break;
                case UltimusOrders.Kind.Offensive:
                    UltimusOrders.SetDefensive(false);
                    said.Add("Okay, I'll go on the attack");
                    break;
                case UltimusOrders.Kind.EnterFight:
                    if (!fighting && _partyStrongWarned != 0 && CombatDuelComponent.IsInBattle(_partyStrongWarned)) {
                        // Told again after he said a group mate's fight looked too strong: he goes in anyway.
                        _partyStrongOkUntil = DateTime.UtcNow.AddMinutes(5);
                        said.Add("Okay, I'm coming in, even though it looks too strong for me");
                    }
                    else if (fighting) {
                        said.Add("I'm already in the fight");
                    }
                    else if (EnterFightNow(me)) {
                        said.Add("Going in");
                    }
                    else {
                        said.Add(_partyMate == 0 ? "I can only go into a fight with you when we're in a group" : "I don't see an enemy near you to start a fight with");
                    }

                    break;
                case UltimusOrders.Kind.Focus:
                    UltimusOrders.SetFocus(order.Target);
                    UltimusOrders.AddRound(order, heard.SpeakerCharId, heard.SpeakerName);
                    // Out of battle with an enemy right by the leader, "kill the outlaw" means start the fight now.
                    wentIn = !fighting && EnterFightNow(me);
                    said.Add(wentIn ? $"Going in after {On(order.Target)}" : $"Going after {On(order.Target)}");
                    break;
                case UltimusOrders.Kind.Attack when !fighting && EnterFightNow(me):
                    UltimusOrders.AddRound(order, heard.SpeakerCharId, heard.SpeakerName);
                    wentIn = true;
                    said.Add(order.Target == UltimusOrders.ALL_ENEMIES ? "Going in to hit them all" : "Going in");
                    break;
                default:
                    UltimusOrders.AddRound(order, heard.SpeakerCharId, heard.SpeakerName);
                    said.Add(Words(order));
                    break;
            }
        }

        var battleOrder = orders.Any(o => o.Kind is not (UltimusOrders.Kind.Stay or UltimusOrders.Kind.Follow or UltimusOrders.Kind.ComeToMe or UltimusOrders.Kind.GoTo
            or UltimusOrders.Kind.OwnWay or UltimusOrders.Kind.LeaderFirst or UltimusOrders.Kind.GoFirst or UltimusOrders.Kind.EnterFight
            or UltimusOrders.Kind.Defensive or UltimusOrders.Kind.Offensive));
        var line = string.Join(", then ", said.Where(s => s.Length > 0));
        if (battleOrder && !fighting && !wentIn) {
            line += $" in our next battle (within {(int) UltimusOrders.Keep.TotalMinutes} minutes)";
        }

        Note("Ultimus", $"ORDER from {heard.SpeakerName} (group leader): \"{UltimusText.Short(heard.Text, 80)}\" ({how}): " +
                        string.Join("; ", orders.Select(o => $"{o.Kind}{(o.Card.Length > 0 ? " " + o.Card : "")}{(o.Target.Length > 0 ? " on " + o.Target : "")}")));
        Think($"{heard.SpeakerName}, my group leader, told me: \"{UltimusText.Short(heard.Text, 80)}\". {line}.", "order|" + heard.Text);
        // In battle only a short "Okay!", once a round at most (the battle says the rest when it acts); out of battle, the whole line.
        // In battle a short yes that says what he understood ("Okay, I'll blade you!"), so a misread order shows at once
        // (Niduus, 2026-11-19: bots should "better identify them and their meaning"); one line a round at most.
        if (fighting) {
            var yes = line.Length == 0 ? "" : "Okay, " + UltimusText.Short(line.StartsWith("I ") || line.StartsWith("I'") ? line : char.ToLowerInvariant(line[0]) + line[1..], 70) + "!";
            if (yes.Length > 0 && UltimusOrders.MaySayThisRound(yes)) {
                SendReply(heard, BotShared.BotSocial.Scrub(yes), "leader commands");
            }
        }
        else if (line.Length > 0) {
            SendReply(heard, BotShared.BotSocial.Scrub(line + "!"), "leader commands");
        }
    }

    // How he says yes to a battle order.
    private static string Words(UltimusOrders.Order order) => order.Kind switch {
        UltimusOrders.Kind.Pass => "I'll pass",
        UltimusOrders.Kind.Flee => "I'll flee",
        UltimusOrders.Kind.Draw => "I'll draw a treasure card",
        UltimusOrders.Kind.Discard => $"I'll discard {order.Card}",
        UltimusOrders.Kind.Cast => order.Target.Length > 0 ? $"{Cap(order.Card)} on {On(order.Target)}, if it's in my hand" : $"I'll cast {Cap(order.Card)} as soon as I can",
        UltimusOrders.Kind.Suggest => $"I'll think about {order.Card}",
        UltimusOrders.Kind.Heal => order.Target.Length > 0 ? $"I'll heal {On(order.Target)}" : "I'll heal",
        UltimusOrders.Kind.Shield => order.Target.Length > 0 ? $"I'll shield {On(order.Target)}" : "I'll shield",
        UltimusOrders.Kind.Blade => order.Target.Length > 0 ? $"I'll blade {On(order.Target)}" : "I'll blade up",
        UltimusOrders.Kind.Trap => order.Target.Length > 0 ? $"I'll trap {On(order.Target)}" : "I'll put a trap up",
        UltimusOrders.Kind.Attack => order.Target == UltimusOrders.ALL_ENEMIES ? "I'll hit them all" : "I'll attack",
        _ => "",
    };

    private static string On(string target) => target switch {
        "me" or "myself" => "you",
        "you" or "yourself" or "urself" or "self" => "myself",
        "us" or "team" or "the team" or "everyone" or "all of us" or "everybody" => "the team",
        _ => System.Text.RegularExpressions.Regex.Replace(target, @"\b[a-z]", m => m.Value.ToUpperInvariant()).Replace("The ", "the ").Replace("A ", "a "),
    };

    // "Garm, heal me" / "Ultimus: stay" / "hey Idunn heal me": his own name in front taken off.
    private static string WithoutMyName(string text, UltimusMember.Status me) {
        var names = new List<string> { UltimusRoster.NAME };
        if (me.Wizard?.PlayerNameBehavior?.GetWizardName() is { Length: > 0 } full) {
            names.Add(full);
            names.Add(full.Split(' ')[0]);
        }

        // A Kitharas Family member's own word ("Moon, heal me").
        if (UltimusSeat.Current.IsKitharas) {
            names.Add(UltimusSeat.Current.CallName);
        }

        if (me.Name is { Length: > 0 } seatName) {
            names.Add(seatName);
        }

        foreach (var name in names.Where(n => n.Length >= 2).Distinct().OrderByDescending(n => n.Length)) {
            var cut = System.Text.RegularExpressions.Regex.Replace(text, $@"^\s*(?:hey|yo|ok|okay|hi)?[,\s]*{System.Text.RegularExpressions.Regex.Escape(name)}\b[\s,:!.-]*", "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (cut.Length != text.Length) {
                return cut;
            }
        }

        return text;
    }

    /// <summary>
    /// A card he can cast named at the start of a part, without "cast" ("Legion Shield us", "dragonblade me"): its name and
    /// what follows (Niduus, 2026-11-19: "Legion Shield us" set True Form to defend instead of casting it). His deck, or
    /// for True Form every spell he can be dealt.
    /// </summary>
    private (string Card, string After)? CardSaid(UltimusMember.Status me, string part) {
        var words = (part ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) {
            return null;
        }

        var known = new Dictionary<string, string>();
        try {
            if (_seat.IsTrueForm) {
                if (TrueFormPool.Ready) {
                    foreach (var e in TrueFormPool.At(TrueForm.SpellLevel(), TrueForm.DevSpellsNow)) {
                        known.TryAdd(BotShared.BotOrderWords.Flat(e.Shown), e.Shown);
                    }
                }
            }
            else {
                foreach (var spell in (me.Wizard?.SpellbookBehavior?.SpellList ?? []).Where(s => s is not null)) {
                    if (BotShared.BotCards.CardOf((uint) spell.m_templateID)?.Shown is { Length: > 0 } shown) {
                        known.TryAdd(BotShared.BotOrderWords.Flat(shown), shown);
                    }
                }
            }
        }
        catch (Exception) {
            return null;
        }

        for (var n = Math.Min(5, words.Length); n >= 1; n--) {
            var flat = BotShared.BotOrderWords.Flat(string.Concat(words.Take(n)));
            if (flat.Length >= 4 && known.TryGetValue(flat, out var card)) {
                return (card, string.Join(' ', words.Skip(n)));
            }
        }

        return null;
    }

    // "Go find Walter", "tp to Mindy": the group's "go to" button on that group mate, as a player clicks their name.
    private string GoToNamed(UltimusVoice.Heard heard, UltimusMember.Status me, string said, bool fighting) {
        if (!GroupRegistry.TryGetGroup(me.CharId, out var g)) {
            return "I can only go to someone in our group";
        }

        var mate = g.Members.Where(m => m.CharId != me.CharId)
            .Select(m => (m.CharId, m.ZonePath, Name: PlayerCheats.LiveName(m.CharId) ?? ""))
            .FirstOrDefault(m => BotShared.BotOrderWords.NameMatches(said, m.Name));
        if (mate.CharId == 0) {
            return $"I don't see anyone called {On(said)} in our group, so I'll stay with you";
        }

        if (fighting) {
            return $"I'll go to {mate.Name} once this battle is over";
        }

        UltimusOrders.EndStay();
        _actors[0].Tell(new UltimusMember.GoToTeammate(mate.CharId));

        Note("Ultimus", $"ORDER from {heard.SpeakerName}: go to {mate.Name}.");
        return $"On my way to {mate.Name}";
    }

}
