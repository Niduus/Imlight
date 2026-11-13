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
using System.Linq;
using Akka.Actor;
using Imlight.CoreLib.Game.BotShared;

namespace Imlight.CoreLib.Game.QuestTeam;

/// <summary>
/// The team's voices (Niduus, 2026-11-13): Menu Chat only, to plan with each other, kept tame (a line now and then, never
/// a stream). They answer a player's Menu Chat, but a player's ask never changes what they do: they turn it down, take
/// orders only from each other and never help a player.
/// </summary>
internal static class QuestTeamVoice {

    internal sealed record Heard(IActorRef Zone, float X, float Y, ulong SpeakerCharId, string SpeakerName, uint PhraseId, string Text);

    private static readonly ConcurrentQueue<Heard> s_heard = new();

    /// <summary>A player (not one of the team) used Menu Chat (ChatService); the team may answer.</summary>
    internal static void PlayerSaid(IActorRef zone, float x, float y, ulong charId, string name, uint phraseId, string text) {
        if (zone is null || QuestTeamRoster.IsMember(charId) || s_heard.Count > 20) {
            return;
        }

        s_heard.Enqueue(new Heard(zone, x, y, charId, name ?? "a player", phraseId, text ?? ""));
    }

    internal static bool TryTake(out Heard heard) => s_heard.TryDequeue(out heard);

}

internal sealed partial class QuestTeamDirectorActor {

    // Kept tame: one team line every 40 seconds at most, and most moments pass without one.
    private static readonly TimeSpan s_teamLineGap = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan s_answerGap = TimeSpan.FromSeconds(30);
    private DateTime _nextTeamLine = DateTime.MinValue;
    private readonly Dictionary<ulong, DateTime> _answeredAt = [];
    private readonly Random _voiceRandom = new();

    // What they say, by moment: the first words a Menu Chat phrase has win.
    private static readonly Dictionary<string, string[]> s_moments = new(StringComparer.Ordinal) {
        ["go"] = ["Let's go", "Follow me", "This way", "Come on"],
        ["wait"] = ["Wait for me", "Wait", "Hold on", "One moment", "Be right back"],
        ["fight"] = ["Ready", "Let's fight", "Attack", "Let's do this"],
        ["won"] = ["Good job", "Well done", "Nice", "Great", "Yay"],
        ["heal"] = ["I need healing", "Heal me", "Heal"],
        ["hello"] = ["Hello", "Hi", "Hey", "Greetings"],
        ["no"] = ["No thanks", "No, thank you", "Sorry", "Not now", "No"],
        ["thanks"] = ["You're welcome", "No problem", "Thanks", "Thank you"],
        ["bye"] = ["Goodbye", "Bye", "See you", "Later"],
    };

    // A player's phrase that asks something of them (they turn it down).
    private static readonly string[] s_asks = [
        "follow", "help", "come", "can you", "could you", "join", "trade", "friend", "group", "teleport", "go to", "let's",
        "need", "want to", "please", "will you", "duel", "battle", "fight", "carry", "port",
    ];

    /// <summary>Says a moment's line through one wizard's Menu Chat, if the team has not spoken lately and the dice allow.</summary>
    private void Speak(int index, string moment, double chance, bool force = false) {
        if (index < 0 || index >= _actors.Count || (!force && (DateTime.UtcNow < _nextTeamLine || _voiceRandom.NextDouble() > chance))) {
            return;
        }

        if (MenuChatBook.Find(_voiceRandom, s_moments.GetValueOrDefault(moment) ?? []) is not { } phrase) {
            return;
        }

        _nextTeamLine = DateTime.UtcNow + s_teamLineGap;
        Note(_team[index].Role, $"{_team[index].Name} says in Menu Chat: \"{phrase.Text}\"");
        _actors[index].Tell(new QuestTeamMember.SayMenuChat(phrase.Id));
    }

    /// <summary>Players' Menu Chat near the team: a greeting back, a no to any ask. Nothing they say changes the team's plan.</summary>
    private void AnswerPlayers() {
        while (QuestTeamVoice.TryTake(out var heard)) {
            var near = _team.Select((m, i) => (Member: m, Index: i))
                .Where(x => x.Member.ZoneActor is not null && x.Member.ZoneActor.Equals(heard.Zone)
                            && QuestTeamPlanner.Far(x.Member.X, x.Member.Y, heard.X, heard.Y) < 700)
                .OrderBy(x => QuestTeamPlanner.Far(x.Member.X, x.Member.Y, heard.X, heard.Y)).ToList();
            if (near.Count == 0) {
                continue;
            }

            if (_answeredAt.TryGetValue(heard.SpeakerCharId, out var last) && DateTime.UtcNow - last < s_answerGap) {
                continue;
            }

            var text = heard.Text;
            var moment = s_asks.Any(a => MenuChatBook.Says(text, a)) ? "no"
                : MenuChatBook.Says(text, "thank") || MenuChatBook.Says(text, "thanks") ? "thanks"
                : new[] { "bye", "goodbye", "see you", "later" }.Any(w => MenuChatBook.Says(text, w)) ? "bye"
                : new[] { "hello", "hi", "hey", "greetings" }.Any(w => MenuChatBook.Says(text, w)) ? "hello"
                : null;
            Note("Team", $"{heard.SpeakerName} said in Menu Chat: \"{text}\"" +
                         (moment == "no" ? "; the team turns it down (it takes orders only from each other)." : moment is null ? "; the team carries on." : "."));
            if (moment is null) {
                continue;
            }

            _answeredAt[heard.SpeakerCharId] = DateTime.UtcNow;
            Speak(near[0].Index, moment, 1, force: true);
        }
    }

}
