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
using Imlight.CoreLib.Game.Commands.Protocols;
using Imlight.CoreLib.Game.Zone.Components;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// His spells in talk, from his real spellbook and deck (Niduus, 2026-11-16: "Do you have hex?" got "Yes, I have Hex learned
/// since it is a Balance school spell", a guess, and then "okay, get ready to use it" got "I'll blade up"). "Do you have X?",
/// "do you know X?", "is X in your deck?" are answered from what he has learned and what is in his deck; the card talked about
/// is kept a few minutes, so "use it" / "cast that" from his group leader means that card.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    private static readonly Regex s_spellAsk = new(
        @"^\s*(?:ultimus[,\s]+)?(?:do\s+(?:you|u)\s+(?:have|know|got|own|carry)|have\s+(?:you|u)\s+(?:got|learned|learnt)|can\s+(?:you|u)\s+(?:cast|use)|is|are)\s+" +
        @"(?:a\s+|an\s+|the\s+|any\s+)?(?<card>[a-z][a-z0-9' :-]{1,40}?)(?:\s+(?:spell|card|cards|in\s+your\s+deck|in\s+ur\s+deck|learned|trained))?\s*\??\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_itCard = new(@"\b(use|cast|play|throw)\s+(it|that|this|that\s+one|this\s+one|that\s+spell|that\s+card|this\s+spell|this\s+card)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex s_handAsk = new(
        @"\b(what(?:'?s|\s+is|\s+do\s+(?:you|u)\s+have|\s+cards?\s+(?:do\s+)?(?:you|u)\s+(?:have|got))\s+in\s+(?:your|ur)\s+hand|what(?:'?s|\s+is)\s+(?:your|ur)\s+hand" +
        @"|show\s+me\s+(?:your|ur)\s+(?:hand|cards)|what\s+cards\s+(?:do\s+)?(?:you|u)\s+(?:have|got)|what\s+can\s+(?:you|u)\s+cast)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly TimeSpan s_cardTalkKept = TimeSpan.FromMinutes(5);

    private readonly Dictionary<ulong, (string Card, DateTime At)> _cardTalked = [];

    private static string FlatName(string s) => new((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>"Do you have Hex?": answered from his own spellbook and deck. True when it named a real spell.</summary>
    private bool SpellAsk(UltimusVoice.Heard heard, UltimusMember.Status me) {
        var m = s_spellAsk.Match(heard.Text ?? "");
        if (!m.Success || me.Wizard?.SpellbookBehavior is not { } book) {
            return false;
        }

        var said = Regex.Replace(m.Groups["card"].Value.Trim(), @"\s+(in\s+your\s+deck|learned|trained)$", "", RegexOptions.IgnoreCase);
        var want = FlatName(said);
        if (want.Length < 3) {
            return false;
        }

        string Shown(uint id) => BotCards.CardOf(id)?.Shown ?? "";
        var learned = (book.LearnedSpellTemplateIds ?? []).Select(id => Shown(id)).FirstOrDefault(n => FlatName(n) == want);
        var deckCount = 0;
        var deckName = "";
        foreach (var spell in (book.SpellList ?? []).Where(s => s is not null)) {
            var name = Shown((uint) spell.m_templateID);
            if (FlatName(name) == want) {
                deckCount += Math.Max(1, (int) spell.m_quantity);
                deckName = name;
            }
        }

        var card = learned ?? (deckName.Length > 0 ? deckName : null);
        if (card is null) {
            // Not his: only answered when it is a real card of the game, by its exact name ("do you have a pet?" is not).
            if (!CommandDrawProtocol.TryFindSpell(said, out _, out var real) || FlatName(real) != want) {
                return false;
            }

            card = real;
        }

        _cardTalked[heard.SpeakerCharId] = (card, DateTime.UtcNow);
        var answer = learned is not null && deckCount > 0 ? $"Yes, I know {card} and I have {deckCount} in my deck."
            : learned is not null ? $"I know {card}, but it isn't in my deck right now."
            : deckCount > 0 ? $"I have {deckCount} {card} in my deck."
            : $"No, I haven't learned {card}.";
        Note("Ultimus", $"SPELLS: {heard.SpeakerName} asked about {card}: learned {(learned is not null ? "yes" : "no")}, {deckCount} in his deck: \"{answer}\"");
        SendReply(heard, answer, "his spells");
        return true;
    }

    /// <summary>"What's in your hand?": his real hand at his last pick in battle, or that he holds none out of battle.</summary>
    private bool HandAsk(UltimusVoice.Heard heard, UltimusMember.Status me) {
        if (!s_handAsk.IsMatch(heard.Text ?? "")) {
            return false;
        }

        var (cards, at) = UltimusOrders.Hand;
        var fighting = CombatDuelComponent.IsInBattle(me.GlobalId);
        string answer;
        if (fighting && DateTime.UtcNow - at < TimeSpan.FromMinutes(2) && cards.Count > 0) {
            var ready = cards.Where(c => c.Castable).Select(c => c.Card).ToList();
            var later = cards.Where(c => !c.Castable).Select(c => c.Card).ToList();
            answer = "In my hand: " + (ready.Count > 0 ? string.Join(", ", ready) : "nothing I have the pips for yet") +
                     (later.Count > 0 ? $". Not enough pips yet for {string.Join(", ", later)}." : ".");
        }
        else if (fighting) {
            answer = "I haven't been dealt a hand yet this round, ask me again in a moment.";
        }
        else {
            var deck = (me.Wizard?.SpellbookBehavior?.SpellList ?? []).Where(s => s is not null)
                .GroupBy(s => BotCards.CardOf((uint) s.m_templateID)?.Shown ?? "").Where(g => g.Key.Length > 0)
                .Select(g => (Name: g.Key, Count: g.Sum(s => Math.Max(1, (int) s.m_quantity)))).OrderByDescending(g => g.Count).ToList();
            answer = deck.Count == 0 ? "I'm not in a battle, so I have no hand right now."
                : $"I'm not in a battle, so no hand yet. My deck has {string.Join(", ", deck.Take(8).Select(d => $"{d.Count} {d.Name}"))}{(deck.Count > 8 ? " and more" : "")}.";
        }

        Note("Ultimus", $"SPELLS: {heard.SpeakerName} asked what is in his hand: \"{answer}\"");
        SendReply(heard, answer, "his spells");
        return true;
    }

    /// <summary>"Use it" / "cast that": the card talked about a moment ago in its place, or the line as said.</summary>
    private string WithCardTalked(UltimusVoice.Heard heard, out bool resolved) {
        resolved = false;
        var text = heard.Text ?? "";
        if (!s_itCard.IsMatch(text) || !_cardTalked.TryGetValue(heard.SpeakerCharId, out var talked) || DateTime.UtcNow - talked.At > s_cardTalkKept) {
            return text;
        }

        resolved = true;
        return s_itCard.Replace(text, mm => $"{mm.Groups[1].Value} {talked.Card}", 1);
    }

}
