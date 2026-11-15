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
        var orders = UltimusOrders.Parse(heard.Text);
        var grouped = GroupRegistry.TryGetGroup(me.CharId, out var group);
        var leader = grouped && group.LeaderCharId == heard.SpeakerCharId && group.LeaderCharId != me.CharId;

        if (orders.Count == 0) {
            if (!leader || !heard.Typed || heard.Text.TrimEnd().EndsWith('?') || heard.Text.Length > 80) {
                return false;
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
        var said = new List<string>();
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
                    said.Add("Okay, I'll pick first");
                    break;
                case UltimusOrders.Kind.Focus:
                    UltimusOrders.SetFocus(order.Target);
                    UltimusOrders.AddRound(order, heard.SpeakerCharId, heard.SpeakerName);
                    said.Add($"Going after {order.Target}");
                    break;
                default:
                    UltimusOrders.AddRound(order, heard.SpeakerCharId, heard.SpeakerName);
                    said.Add(Words(order));
                    break;
            }
        }

        var battleOrder = orders.Any(o => o.Kind is not (UltimusOrders.Kind.Stay or UltimusOrders.Kind.Follow or UltimusOrders.Kind.ComeToMe
            or UltimusOrders.Kind.OwnWay or UltimusOrders.Kind.LeaderFirst or UltimusOrders.Kind.GoFirst));
        var line = string.Join(", then ", said.Where(s => s.Length > 0));
        if (battleOrder && !fighting) {
            line += $" in our next battle (within {(int) UltimusOrders.Keep.TotalMinutes} minutes)";
        }

        Note("Ultimus", $"ORDER from {heard.SpeakerName} (group leader): \"{UltimusText.Short(heard.Text, 80)}\" ({how}): " +
                        string.Join("; ", orders.Select(o => $"{o.Kind}{(o.Card.Length > 0 ? " " + o.Card : "")}{(o.Target.Length > 0 ? " on " + o.Target : "")}")));
        Think($"{heard.SpeakerName}, my group leader, told me: \"{UltimusText.Short(heard.Text, 80)}\". {line}.", "order|" + heard.Text);
        // In battle only a short "Okay!", once a round at most (the battle says the rest when it acts); out of battle, the whole line.
        if (fighting) {
            if (line.Length > 0 && UltimusOrders.MaySayThisRound()) {
                SendReply(heard, "Okay!", "leader commands");
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
        UltimusOrders.Kind.Cast => $"{Cap(order.Card)} on {On(order.Target)}, if it's in my hand",
        UltimusOrders.Kind.Suggest => $"I'll think about {order.Card}",
        UltimusOrders.Kind.Heal => order.Target.Length > 0 ? $"I'll heal {On(order.Target)}" : "I'll heal",
        UltimusOrders.Kind.Shield => order.Target.Length > 0 ? $"I'll shield {On(order.Target)}" : "I'll shield",
        UltimusOrders.Kind.Blade => order.Target.Length > 0 ? $"I'll blade {On(order.Target)}" : "I'll blade up",
        UltimusOrders.Kind.Trap => order.Target.Length > 0 ? $"I'll trap {On(order.Target)}" : "I'll put a trap up",
        UltimusOrders.Kind.Attack => "Attacking",
        _ => "",
    };

    private static string On(string target) => target switch {
        "me" or "myself" => "you",
        "you" or "yourself" or "urself" or "self" => "myself",
        _ => target,
    };

}
