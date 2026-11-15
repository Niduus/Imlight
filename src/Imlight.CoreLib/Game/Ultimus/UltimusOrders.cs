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

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// What the leader of Ultimus's group told him to do (Niduus, 2026-11-15: "allow for him to take commands such as "Let me go
/// first" for battles or "Stay here". He will only accept commands him the person that is the leader of his group", and
/// "Cast Dragonblade" makes him think, "Cast Dragonblade on me" or "on Mitsu" casts that card from his hand on that wizard).
/// Only the group's leader is listened to (UltimusDirector.Orders.cs); the battle reads the orders here
/// (CombatDuelComponent.UltimusOrders.cs). Everything is still a click a player makes: his own hand, his own pips.
/// </summary>
internal static class UltimusOrders {

    internal enum Kind {
        Stay, Follow, ComeToMe, LeaveGroup, OwnWay, LeaderFirst, GoFirst,
        Pass, Flee, Draw, Discard, Cast, Suggest, Heal, Shield, Blade, Trap, Attack, Focus,
    }

    /// <summary>One order: what, which card (by name, may be empty) and on whom (as said: "me", "Mitsu", "the boss"; may be empty).</summary>
    internal sealed record Order(Kind Kind, string Card = "", string Target = "");

    /// <summary>A battle order waiting for his pick, with who gave it and how many rounds it has waited (for pips).</summary>
    internal sealed class Pending {
        internal Order Order;
        internal ulong By;
        internal string ByName = "";
        internal DateTime At;
        internal int Rounds;
        internal bool ToldNoPips;
        internal int LastRound = -1;
    }

    /// <summary>The orders without a card or a name, as their names are given to the AI to pick from ("stay here", ...).</summary>
    internal static readonly (string Name, Kind Kind)[] Plain = [
        ("stay here", Kind.Stay), ("follow me", Kind.Follow), ("come to me", Kind.ComeToMe), ("leave the group", Kind.LeaveGroup),
        ("play your own way again", Kind.OwnWay), ("let me go first in battle", Kind.LeaderFirst), ("you go first in battle", Kind.GoFirst),
        ("pass this round", Kind.Pass), ("flee the battle", Kind.Flee), ("draw a treasure card", Kind.Draw), ("heal", Kind.Heal),
        ("shield", Kind.Shield), ("blade", Kind.Blade), ("attack", Kind.Attack),
    ];

    internal static IReadOnlyList<string> PlainNames { get; } = [.. Plain.Select(p => p.Name)];

    /// <summary>Battle orders are only kept this long (said before a battle, they count for the next one).</summary>
    internal static readonly TimeSpan Keep = TimeSpan.FromMinutes(3);

    private static readonly object s_lock = new();
    private static readonly List<Pending> s_round = [];

    /// <summary>Whom he waits for in battle (the leader picks first), or 0. Off with "you go first".</summary>
    internal static ulong LeaderFirst { get; private set; }

    /// <summary>He picks without waiting for the players before him (until told otherwise).</summary>
    internal static bool GoFirst { get; private set; }

    /// <summary>Where he was told to stay (party mode stays there until told to follow), or null.</summary>
    internal static (string Zone, float X, float Y)? Stay { get; private set; }

    /// <summary>The enemy the leader said to go after (by name), kept 10 minutes, or "".</summary>
    internal static string Focus {
        get { lock (s_lock) { return DateTime.UtcNow - s_focusAt < TimeSpan.FromMinutes(10) ? s_focus : ""; } }
    }

    private static string s_focus = "";
    private static DateTime s_focusAt = DateTime.MinValue;

    // Battle chat stays short (Niduus, 2026-11-15, "all"): one line a round at most from him, his "Okay!" or what he can't do.
    private static long s_roundCount;
    private static long s_saidRound = -1;

    /// <summary>A new battle round began (any battle he is in): he may say one line in it.</summary>
    internal static void NewRound() => System.Threading.Interlocked.Increment(ref s_roundCount);

    /// <summary>True once a round: the one line he may say in battle chat this round.</summary>
    internal static bool MaySayThisRound() {
        lock (s_lock) {
            if (s_saidRound == s_roundCount) {
                return false;
            }

            s_saidRound = s_roundCount;
            return true;
        }
    }

    internal static void SetStay(string zone, float x, float y) => Stay = (zone ?? "", x, y);
    internal static void EndStay() => Stay = null;

    internal static void SetLeaderFirst(ulong leader) {
        LeaderFirst = leader;
        GoFirst = false;
    }

    internal static void SetGoFirst() {
        LeaderFirst = 0;
        GoFirst = true;
    }

    internal static void SetFocus(string name) {
        lock (s_lock) {
            s_focus = name ?? "";
            s_focusAt = DateTime.UtcNow;
        }
    }

    /// <summary>Every order dropped ("do your own thing", or the group ended).</summary>
    internal static void Clear() {
        lock (s_lock) {
            s_round.Clear();
            s_focus = "";
        }

        LeaderFirst = 0;
        GoFirst = false;
        Stay = null;
    }

    internal static void AddRound(Order order, ulong by, string byName) {
        lock (s_lock) {
            // The same order again replaces the old one; a pass or a flee replaces every card order. Two of a kind ("heal me
            // and Mitsu") both wait: one a round.
            s_round.RemoveAll(p => p.Order == order || order.Kind is Kind.Pass or Kind.Flee);
            s_round.Add(new Pending { Order = order, By = by, ByName = byName ?? "", At = DateTime.UtcNow });
        }
    }

    /// <summary>The battle orders still waiting, oldest first (the old ones dropped).</summary>
    internal static List<Pending> Waiting() {
        lock (s_lock) {
            s_round.RemoveAll(p => DateTime.UtcNow - p.At > Keep || p.Rounds > 3);
            return [.. s_round];
        }
    }

    internal static void Done(Pending pending) {
        lock (s_lock) {
            s_round.Remove(pending);
        }
    }

    internal static bool HasWaiting {
        get { lock (s_lock) { return s_round.Count > 0; } }
    }

    // ---------------------------------------------------------------------
    // Reading a line
    // ---------------------------------------------------------------------

    private static readonly Regex s_split = new(@"\s*(?:,|;|&|\+|\.(?=\s|$)|!|\band then\b|\bthen\b|\band\b|\bafter that\b|\balso\b)\s*", RegexOptions.Compiled);

    private static readonly string[] s_fillers = ["ultimus", "ult", "please", "pls", "plz", "can you", "could you", "would you", "will you",
        "hey", "ok", "okay", "now", "right now", "quick", "quickly", "asap", "for me", "go ahead and", "i want you to", "i need you to", "you should", "try to"];

    /// <summary>
    /// The orders in a line, in the order said ("let me go first then heal me", "cast Dragonblade on Mitsu and attack the boss").
    /// Empty when the line holds none. A part that is only a name after a targeted order ("heal me and Mitsu") repeats it.
    /// </summary>
    internal static List<Order> Parse(string text) {
        var orders = new List<Order>();
        if (string.IsNullOrWhiteSpace(text) || text.TrimEnd().EndsWith('?') || text.Length > 160) {
            return orders;
        }

        var low = " " + text.ToLowerInvariant().Replace("’", "").Replace("'", "") + " ";
        // "go ahead and heal me", "try and draw": the "and" there is not a second order.
        low = Regex.Replace(low, @"\b(go ahead|try|be sure|make sure) and\b", " ");
        Order last = null;
        foreach (var raw in s_split.Split(low)) {
            var part = Clean(raw);
            if (part.Length == 0) {
                continue;
            }

            if (One(part) is { } order) {
                orders.Add(order);
                last = order;
            }
            else if (last is { Kind: Kind.Heal or Kind.Shield or Kind.Blade or Kind.Trap or Kind.Focus or Kind.Cast } && part.Split(' ').Length <= 3) {
                // "heal me and Mitsu": the same order on the next name.
                var again = last with { Target = part };
                orders.Add(again);
            }
        }

        return orders;
    }

    private static string Clean(string part) {
        var t = " " + Regex.Replace(part, @"[^a-z0-9 ]", " ") + " ";
        foreach (var filler in s_fillers) {
            t = Regex.Replace(t, $@"(?<= ){Regex.Escape(filler)}(?= )", " ");
        }

        return Regex.Replace(t, @"\s+", " ").Trim();
    }

    private static bool Has(string t, params string[] phrases)
        => phrases.Any(p => Regex.IsMatch(t, $@"(^| ){Regex.Escape(p)}( |$)"));

    private static bool Starts(string t, out string rest, params string[] words) {
        foreach (var word in words) {
            if (t == word) {
                rest = "";
                return true;
            }

            if (t.StartsWith(word + " ", StringComparison.Ordinal)) {
                rest = t[(word.Length + 1)..].Trim();
                return true;
            }
        }

        rest = "";
        return false;
    }

    private static Order One(string t) {
        if (Has(t, "do your own thing", "your own thing", "play your own way", "your own way", "as you like", "never mind", "nevermind", "forget it",
                "cancel", "at ease", "free will", "fight how you want", "play how you want", "do what you want", "stop taking orders")) {
            return new Order(Kind.OwnWay);
        }

        if (Has(t, "dont wait", "do not wait", "you go first", "you first", "you start", "you pick first", "dont wait for me")) {
            return new Order(Kind.GoFirst);
        }

        if (Has(t, "let me go first", "me first", "i go first", "ill go first", "i will go first", "i am going first", "im going first",
                "wait for me to pick", "wait for my turn", "wait for my pick", "wait for me to go", "go after me", "after me", "let me pick first",
                "let me start", "i start", "i pick first", "my turn first")) {
            return new Order(Kind.LeaderFirst);
        }

        if (Has(t, "go first", "go ahead", "you can go first")) {
            return new Order(Kind.GoFirst);
        }

        if (Has(t, "draw", "treasure card", "treasure cards", "tc", "tcs")) {
            return new Order(Kind.Draw);
        }

        if (Has(t, "flee", "run away", "run", "get out of the battle", "get out of here", "leave the battle", "leave battle", "leave the fight", "escape")) {
            return new Order(Kind.Flee);
        }

        if (Has(t, "pass", "skip this round", "skip this turn", "skip", "save your pips", "save pips", "hold your pips", "keep your pips", "dont cast",
                "dont cast anything", "do nothing", "dont do anything")) {
            return new Order(Kind.Pass);
        }

        if (Has(t, "leave the group", "leave group", "leave my group", "quit the group", "go back to your quest", "go do your quest", "do your quest",
                "go quest", "back to your story", "go back to your story", "you can go now", "you can leave")) {
            return new Order(Kind.LeaveGroup);
        }

        if (Has(t, "tp to me", "teleport to me", "port to me", "go to me", "come to me", "get over here", "over here")) {
            return new Order(Kind.ComeToMe);
        }

        if (Has(t, "follow me", "follow", "come with me", "come here", "come on", "come", "lets go", "with me", "keep up", "move on", "keep going",
                "go on", "lead the way", "move with me")) {
            return new Order(Kind.Follow);
        }

        if (Has(t, "stay here", "stay", "wait here", "wait", "hold on", "hold here", "hold position", "dont move", "stop", "stand still", "stay put",
                "wait for me", "stop following")) {
            return new Order(Kind.Stay);
        }

        if (Starts(t, out var rest, "heal", "heal up")) {
            return new Order(Kind.Heal, "", Target(rest));
        }

        if (Starts(t, out rest, "shield", "ward", "protect", "guard")) {
            return new Order(Kind.Shield, "", Target(rest));
        }

        if (Starts(t, out rest, "blade", "buff", "boost")) {
            return new Order(Kind.Blade, "", Target(rest));
        }

        if (Starts(t, out rest, "trap")) {
            return new Order(Kind.Trap, "", Target(rest));
        }

        if (Starts(t, out rest, "attack", "hit", "kill", "focus", "focus on", "target", "go for", "nuke", "hit on", "damage")) {
            rest = Target(rest);
            return rest.Length == 0 ? new Order(Kind.Attack) : new Order(Kind.Focus, "", rest);
        }

        if (Starts(t, out rest, "discard", "throw away", "get rid of", "toss", "drop")) {
            return rest.Length == 0 ? null : new Order(Kind.Discard, rest);
        }

        if (Starts(t, out rest, "cast", "use", "play", "throw")) {
            if (rest.Length == 0) {
                return null;
            }

            // "Dragonblade on Mitsu", "Fire Cat at the boss": the card and on whom; no "on": only a suggestion he thinks about.
            var at = Regex.Match(rest, @"^(?<card>.+?) (?:on|at|onto|to|for) (?<who>.+)$");
            return at.Success
                ? new Order(Kind.Cast, at.Groups["card"].Value.Trim(), Target(at.Groups["who"].Value))
                : new Order(Kind.Suggest, rest);
        }

        return null;
    }

    // "on me" → "me"; "the boss" stays; "him"/"her" kept as said.
    private static string Target(string rest) {
        rest = Regex.Replace(rest ?? "", @"^(on|at|onto|to|for) ", "").Trim();
        return rest is "up" ? "" : rest;
    }

}
