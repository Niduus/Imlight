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
using Imlight.CoreLib.Game.Groups;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Components;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Orders read by what was just said (Niduus, 2026-11-19: "have bots improve interactions and commands by context and adding
/// additional thought for asking context"). An order that could mean two people ("blade" in a group of three, "heal him" with
/// two teammates) gets a short question ("Blade who: you, me or Walter?") and the answer finishes it; a word two letters off
/// an order gets "Did you mean ...?"; "again" repeats the last order; "Walter too" puts the last order on Walter as well.
/// Only for his group leader, as every order (UltimusDirector.Orders.cs).
/// </summary>
internal sealed partial class UltimusDirectorActor {

    // A question he asked his leader, waiting for its answer: the order it finishes, or the line he thinks they meant.
    private sealed record Asked(UltimusOrders.Order Order, string Meant, List<string> Choices, DateTime At);

    private readonly Dictionary<ulong, Asked> _askedLeader = [];

    // The last orders each leader gave him, for "again" and "Walter too".
    private readonly Dictionary<ulong, (List<UltimusOrders.Order> Orders, DateTime At)> _lastOrders = [];

    private static readonly TimeSpan s_askKept = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan s_againKept = TimeSpan.FromMinutes(3);

    private static readonly Regex s_again = new(@"^(again|do it again|do that again|same|same again|same thing|one more|once more|repeat|repeat that|again please|more|another)$",
        RegexOptions.Compiled);

    private static readonly Regex s_yes = new(@"^(yes|yeah|yea|ye|yep|yup|y|ya|sure|correct|right|exactly|that|that one|uh huh|mhm|ok|okay|yes please|yeah that)$",
        RegexOptions.Compiled);

    private static readonly Regex s_no = new(@"^(no|nope|nah|n|never mind|nevermind|forget it|cancel|neither|none)$", RegexOptions.Compiled);

    /// <summary>
    /// A short line read by what came before it: the answer to his question, "again", or "Walter too". True when it was one
    /// (acted on and answered here).
    /// </summary>
    private bool OrderInContext(UltimusVoice.Heard heard, UltimusMember.Status me, string text, List<UltimusOrders.Order> parsed) {
        var said = Regex.Replace(Regex.Replace((text ?? "").ToLowerInvariant().Replace("'", ""), @"[^a-z0-9 ]", " "), @"\s+", " ").Trim();
        said = Regex.Replace(said, @"^(please|pls|plz|ok|okay|um|uh|hmm|well) ", "").Trim();
        if (said.Length == 0) {
            return false;
        }

        // His question first: the answer finishes the order it held.
        if (_askedLeader.Remove(heard.SpeakerCharId, out var asked) && DateTime.UtcNow - asked.At < s_askKept) {
            if (asked.Meant.Length > 0) {
                if (s_yes.IsMatch(said)) {
                    var meant = UltimusOrders.Parse(asked.Meant, part => CardSaid(me, part));
                    if (meant.Count > 0) {
                        ApplyOrders(heard, me, meant, "what I asked back");
                        return true;
                    }
                }

                if (s_no.IsMatch(said)) {
                    SendReply(heard, "Okay, say it another way and I'll try again.", "leader commands");
                    return true;
                }
            }
            else if (parsed.Count == 0 || said.Split(' ').Length <= 3) {
                if (s_no.IsMatch(said)) {
                    SendReply(heard, "Okay, I'll leave it.", "leader commands");
                    return true;
                }

                if (WhoFromAnswer(said, heard, me, asked.Choices) is { } who) {
                    ApplyOrders(heard, me, [asked.Order with { Target = who }], "the answer to my question");
                    return true;
                }
            }
        }

        if (parsed.Count > 0) {
            return false;
        }

        // "Again", "same", "one more": the last orders once more.
        if (s_again.IsMatch(said) && _lastOrders.TryGetValue(heard.SpeakerCharId, out var last) && DateTime.UtcNow - last.At < s_againKept) {
            ApplyOrders(heard, me, [.. last.Orders], "the same as last time");
            return true;
        }

        // "Walter too", "and Mindy", "now Walter", "Walter as well": the last order that went on someone, on them.
        var too = Regex.Match(said, @"^(?:and |also |now |then |next |same (?:for|on) |(?:do |now do )?(?:the same )?(?:for|on) )?(?<who>[a-z0-9 ]{2,24}?)(?: too| as well| also| next)?$");
        if (too.Success && (Regex.IsMatch(said, @"( too| as well| also| next)$|^(and|also|now|then|next|same for|same on|for|on) "))
            && _lastOrders.TryGetValue(heard.SpeakerCharId, out var before) && DateTime.UtcNow - before.At < s_againKept
            && before.Orders.LastOrDefault(o => o.Target.Length > 0 && o.Target != UltimusOrders.ALL_ENEMIES) is { } targeted
            && WhoFromAnswer(too.Groups["who"].Value.Trim(), heard, me, null) is { } next) {
            ApplyOrders(heard, me, [targeted with { Target = next }], "the last order, on someone else");
            return true;
        }

        return false;
    }

    /// <summary>
    /// "Shleid me": a word two letters off an order. He asks "Did you mean "shield me"?" rather than guess or ignore it. True
    /// when he asked.
    /// </summary>
    private bool AskWhatTheyMeant(UltimusVoice.Heard heard, UltimusMember.Status me, string text) {
        var lower = Regex.Replace(Regex.Replace((text ?? "").ToLowerInvariant().Replace("'", ""), @"[^a-z0-9 ]", " "), @"\s+", " ").Trim();
        if (BotOrderWords.LikelyMeant(lower) is not { } meant || UltimusOrders.Parse(meant, part => CardSaid(me, part)).Count == 0) {
            return false;
        }

        _askedLeader[heard.SpeakerCharId] = new Asked(null, meant, [], DateTime.UtcNow);
        Note("Ultimus", $"ORDER from {heard.SpeakerName} unclear (\"{UltimusText.Short(heard.Text, 60)}\"): asks if they meant \"{meant}\".");
        SendReply(heard, $"Did you mean \"{meant}\"?", "leader commands");
        return true;
    }

    /// <summary>
    /// The orders he can follow as said; one that could mean more than one person is held back and asked about instead
    /// ("Blade who: you, me or Walter?"). Returns the orders to follow now.
    /// </summary>
    private List<UltimusOrders.Order> WithoutUnclear(UltimusVoice.Heard heard, UltimusMember.Status me, List<UltimusOrders.Order> orders, bool fighting) {
        var keep = new List<UltimusOrders.Order>();
        foreach (var order in orders) {
            if (_askedLeader.ContainsKey(heard.SpeakerCharId) || Unclear(order, heard, me, fighting) is not { } choices) {
                keep.Add(order);
                continue;
            }

            var verb = order.Kind switch {
                UltimusOrders.Kind.Heal => "Heal",
                UltimusOrders.Kind.Shield => "Shield",
                UltimusOrders.Kind.Blade => "Blade",
                _ => "Go to",
            };
            var shown = choices.Select(c => c switch { "me" => "you", "you" => "me", _ => c }).ToList();
            var list = shown.Count <= 2 ? string.Join(" or ", shown) : string.Join(", ", shown.Take(shown.Count - 1)) + " or " + shown[^1];
            _askedLeader[heard.SpeakerCharId] = new Asked(order, "", choices, DateTime.UtcNow);
            Note("Ultimus", $"ORDER from {heard.SpeakerName} could mean more than one person (\"{UltimusText.Short(heard.Text, 60)}\"): asks who.");
            SendReply(heard, $"{verb} who: {list}?", "leader commands");
        }

        return keep;
    }

    // The people an order could be for, when it could be for more than one; null when it is clear.
    private List<string> Unclear(UltimusOrders.Order order, UltimusVoice.Heard heard, UltimusMember.Status me, bool fighting) {
        var others = GroupMates(me).Where(m => m.CharId != heard.SpeakerCharId).Select(m => m.Name).Where(n => n.Length > 0).ToList();
        var said = (order.Target ?? "").Trim().ToLowerInvariant();
        var forAlly = order.Kind is UltimusOrders.Kind.Heal or UltimusOrders.Kind.Shield or UltimusOrders.Kind.Blade;
        if (order.Kind == UltimusOrders.Kind.GoTo) {
            var matches = others.Where(n => BotOrderWords.NameMatches(said, n)).ToList();
            return matches.Count > 1 ? matches : null;
        }

        if (!forAlly) {
            return null;
        }

        // "Heal him" / "blade her": clear only when one other teammate is there.
        if (said is "him" or "her" or "them" or "that guy" or "that one") {
            return others.Count > 1 ? others : null;
        }

        // A name that fits two teammates ("blade m" with Mitsu and Mindy).
        if (said.Length > 0 && said is not ("me" or "myself" or "you" or "yourself" or "urself" or "self" or "us" or "team" or "the team"
                or "everyone" or "all" or "all of us" or "everybody")) {
            var matches = others.Where(n => BotOrderWords.NameMatches(said, n)).ToList();
            return matches.Count > 1 ? matches : null;
        }

        // "Blade" / "heal" alone in a battle with other players on his side: the leader, himself, or someone else?
        if (said.Length == 0 && fighting && order.Kind is UltimusOrders.Kind.Blade or UltimusOrders.Kind.Heal && others.Count > 0) {
            return ["me", "you", .. others];
        }

        return null;
    }

    // Whom an answer names: "me" (the leader), "you" (himself), "both"/"everyone" (the team), or a group mate by name.
    private string WhoFromAnswer(string said, UltimusVoice.Heard heard, UltimusMember.Status me, List<string> choices) {
        said = Regex.Replace(said ?? "", @"^(on |to |for |at )", "").Trim();
        if (said is "me" or "myself" or "mine" or "on me") {
            return "me";
        }

        if (said is "you" or "yourself" or "urself" or "self" or "u") {
            return "you";
        }

        if (said is "both" or "both of us" or "us" or "everyone" or "everybody" or "all" or "all of us" or "the team" or "team") {
            return "us";
        }

        var names = (choices ?? []).Where(c => c is not ("me" or "you")).Concat(GroupMates(me).Select(m => m.Name)).Where(n => n.Length > 0).Distinct().ToList();
        var hits = names.Where(n => BotOrderWords.NameMatches(said, n)).ToList();
        if (hits.Count == 1) {
            return BotOrderWords.Flat(hits[0]) == BotOrderWords.Flat(heard.SpeakerName) ? "me" : hits[0];
        }

        return null;
    }

    // His group mates (not himself), with their names as the game shows them.
    private List<(ulong CharId, string Name)> GroupMates(UltimusMember.Status me)
        => GroupRegistry.TryGetGroup(me.CharId, out var g)
            ? [.. g.Members.Where(m => m.CharId != me.CharId).Select(m => (m.CharId, PlayerCheats.LiveName(m.CharId) ?? ""))]
            : [];

    /// <summary>The orders followed, kept for "again" and "Walter too".</summary>
    private void RememberOrders(UltimusVoice.Heard heard, List<UltimusOrders.Order> orders) {
        var kept = orders.Where(o => o.Kind is not (UltimusOrders.Kind.LeaveGroup or UltimusOrders.Kind.OwnWay)).ToList();
        if (kept.Count > 0) {
            _lastOrders[heard.SpeakerCharId] = (kept, DateTime.UtcNow);
        }
    }

}
