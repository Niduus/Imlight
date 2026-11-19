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
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Ultimus;

namespace Imlight.CoreLib.Game.QuestTeam;

/// <summary>
/// The team's voices (Niduus, 2026-11-13): Menu Chat to plan with each other, kept tame (a line now and then, never a
/// stream). They answer a player's Menu Chat, but a player's ask never changes what they do: they turn it down, take
/// orders only from each other and never help a player.
/// Niduus, 2026-11-15 ("Allow for QT to have AI chats like Ultimus with each member able to be different models or an
/// option to unify them into one model"): with [QuestTeam] AIChat on, players' typed chat near them and whispers to them get
/// a typed answer worded by the AI, each wizard in its own way of talking and with its own model, or all with one.
/// </summary>
internal static class QuestTeamVoice {

    /// <param name="Typed">The player typed the words (not a Menu Chat phrase).</param>
    /// <param name="ToCharId">A whisper to this team wizard; 0 when said aloud.</param>
    internal sealed record Heard(IActorRef Zone, float X, float Y, ulong SpeakerCharId, string SpeakerName, uint PhraseId, string Text, bool Typed = false,
        ulong ToCharId = 0);

    private static readonly ConcurrentQueue<Heard> s_heard = new();

    /// <summary>A player (not one of the team) used Menu Chat or typed near the team (ChatService); the team may answer.</summary>
    internal static void PlayerSaid(IActorRef zone, float x, float y, ulong charId, string name, uint phraseId, string text, bool typed = false) {
        if (zone is null || QuestTeamRoster.IsMember(charId) || s_heard.Count > 20 || string.IsNullOrWhiteSpace(text)) {
            return;
        }

        s_heard.Enqueue(new Heard(zone, x, y, charId, name ?? "a player", phraseId, text, typed));
    }

    /// <summary>A player whispered to one of the team (ChatService); that wizard may whisper back.</summary>
    internal static void PlayerWhispered(ulong toCharId, ulong charId, string name, uint phraseId, string text, bool typed) {
        if (toCharId == 0 || QuestTeamRoster.IsMember(charId) || s_heard.Count > 20 || string.IsNullOrWhiteSpace(text)) {
            return;
        }

        s_heard.Enqueue(new Heard(null, 0, 0, charId, name ?? "a player", phraseId, text, typed, toCharId));
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

    // Words of a phrase that sends someone somewhere or after something: never a team line said on the way.
    private static readonly string[] s_placeWords = [
        "to", "look", "find", "MooShu", "Krokotopia", "Marleybone", "Dragonspyre", "Celestia", "Zafaria", "Avalon", "Azteca",
        "Khrysalis", "Polaris", "Mirage", "Empyrea", "Karamelle", "Lemuria", "Ahmarra", "Wizard City", "Grizzleheim", "Wysteria",
    ];

    // A player's phrase that asks something of them (they turn it down).
    private static readonly string[] s_asks = [
        "follow", "help", "come", "can you", "could you", "join", "trade", "friend", "group", "teleport", "go to", "let's",
        "need", "want to", "please", "will you", "duel", "battle", "fight", "carry", "port",
    ];

    /// <summary>Says a moment's line through one wizard's Menu Chat, if the team has not spoken lately and the dice allow.</summary>
    private void Speak(int index, string moment, double chance, bool force = false, ulong whisperTo = 0) {
        if (index < 0 || index >= _actors.Count || (!force && (DateTime.UtcNow < _nextTeamLine || _voiceRandom.NextDouble() > chance))) {
            return;
        }

        // The phrase that is just the words first: one that only starts with them names places the team is not going
        // (Niduus's log, 2026-11-15: level 1 Freya in Unicorn Way said "Let's go to MooShu" and "Let's go to Ahmarra").
        var words = s_moments.GetValueOrDefault(moment) ?? [];
        if ((MenuChatBook.Exact(_voiceRandom, words) ?? MenuChatBook.Find(_voiceRandom, s_placeWords, words)) is not { } phrase) {
            return;
        }

        _nextTeamLine = DateTime.UtcNow + s_teamLineGap;
        SayPhrase(index, phrase, whisperTo, "");
    }

    // A Menu Chat phrase said aloud, or whispered back to the player who whispered.
    private void SayPhrase(int index, MenuChatBook.Phrase phrase, ulong whisperTo, string by) {
        Note(_team[index].Role, $"{_team[index].Name} {(whisperTo != 0 ? "whispers back" : "says")} in Menu Chat: \"{phrase.Text}\"{by}");
        _actors[index].Tell(whisperTo != 0 ? (object) new QuestTeamMember.WhisperMenuChat(phrase.Id, whisperTo) : new QuestTeamMember.SayMenuChat(phrase.Id));
    }

    /// <summary>
    /// Players' chat near the team or whispered to one of them: a greeting back, a no to any ask, and with [QuestTeam] AIChat
    /// a typed answer worded by that wizard's AI. Nothing they say changes the team's plan.
    /// </summary>
    private void AnswerPlayers() {
        while (QuestTeamVoice.TryTake(out var heard)) {
            var whisperTo = heard.ToCharId != 0 ? heard.SpeakerCharId : 0;
            var near = (heard.ToCharId != 0
                    ? _team.Select((m, i) => (Member: m, Index: i)).Where(x => x.Member.CharId == heard.ToCharId)
                    : _team.Select((m, i) => (Member: m, Index: i))
                        .Where(x => x.Member.ZoneActor is not null && x.Member.ZoneActor.Equals(heard.Zone)
                                    && QuestTeamPlanner.Far(x.Member.X, x.Member.Y, heard.X, heard.Y) < 700)
                        .OrderBy(x => QuestTeamPlanner.Far(x.Member.X, x.Member.Y, heard.X, heard.Y)))
                .ToList();
            if (near.Count == 0) {
                continue;
            }

            // Typed talk is a conversation, so it may be answered sooner than Menu Chat.
            var talk = heard.Typed || whisperTo != 0;
            if (_answeredAt.TryGetValue(heard.SpeakerCharId, out var last) && DateTime.UtcNow - last < (talk ? s_typedGap : s_answerGap)) {
                continue;
            }

            var text = heard.Text;
            var speaker = near[0].Index;
            // Typed chat said aloud: only what is said to the team, like Ultimus (not players talking among themselves).
            if (heard.Typed && whisperTo == 0 && !AimedAtTeam(heard, near, out speaker, out var notOurs)) {
                Note("Team", $"{heard.SpeakerName} said \"{QuestTeamText.Short(text, 60)}\" near the team; it stays out of it ({notOurs}).");
                continue;
            }

            var moment = s_asks.Any(a => MenuChatBook.Says(text, a)) ? "no"
                : MenuChatBook.Says(text, "thank") || MenuChatBook.Says(text, "thanks") ? "thanks"
                : new[] { "bye", "goodbye", "see you", "later" }.Any(w => MenuChatBook.Says(text, w)) ? "bye"
                : new[] { "hello", "hi", "hey", "greetings" }.Any(w => MenuChatBook.Says(text, w)) ? "hello"
                : null;
            var how = (whisperTo != 0 ? "whispered" : "said") + (heard.Typed ? "" : " in Menu Chat");

            // "How do I train X?", "where do I get X?": the same real-game lookup Ultimus answers from (UltimusLookup).
            if (heard.Typed && UltimusLookup.Answer(heard.SpeakerCharId, text) is { Length: > 0 } looked) {
                _answeredAt[heard.SpeakerCharId] = DateTime.UtcNow;
                Note("Team", $"{heard.SpeakerName} {how}: \"{QuestTeamText.Short(text, 80)}\"; answered from the real game's data.");
                SayTyped(speaker, heard, looked, "game data");
                continue;
            }

            // Typed or whispered talk with [QuestTeam] AIChat: that wizard's own AI words a typed answer (an ask still gets a no,
            // in the wizard's own words; nothing said changes the plan).
            if (talk && heard.Typed && AiChatOn && AskTeamAi(speaker, heard, moment, how)) {
                continue;
            }

            // Without the AI: an answer learned from it earlier (shared with Ultimus, UltimusAiLearned).
            if (heard.Typed && moment is null && UltimusAiLearned.Find(text) is { } known) {
                _answeredAt[heard.SpeakerCharId] = DateTime.UtcNow;
                Note("Team", $"{heard.SpeakerName} {how}: \"{QuestTeamText.Short(text, 80)}\"");
                SayTyped(speaker, heard, known, LEARNED);
                continue;
            }

            // With [QuestTeam] AIChat, the AI picks the Menu Chat answer from a safe list (never for an ask: that stays a no).
            var target = TeamTarget(_team[speaker].Role);
            if (!heard.Typed && moment != "no" && AiChatOn && UltimusLocalAI.ReadyFor(target)) {
                var choices = AiChoices();
                if (choices.Count > 0) {
                    Note("Team", $"{heard.SpeakerName} {how}: \"{text}\"; {_team[speaker].Name}'s {UltimusLocalAI.WhoOf(target)} picks the answer.");
                    _answeredAt[heard.SpeakerCharId] = DateTime.UtcNow;
                    var fallback = moment;
                    UltimusLocalAI.Pick(text, choices.Select(c => c.Text).ToList(), target)
                        .ContinueWith(t => new TeamAiPick(speaker, fallback, choices, t.IsCompletedSuccessfully ? t.Result : -1, whisperTo, UltimusLocalAI.WhoOf(target)))
                        .PipeTo(Self);
                    continue;
                }
            }

            Note("Team", $"{heard.SpeakerName} {how}: \"{text}\"" +
                         (moment == "no" ? "; the team turns it down (it takes orders only from each other)." : moment is null ? "; the team carries on." : "."));
            if (moment is null) {
                continue;
            }

            _answeredAt[heard.SpeakerCharId] = DateTime.UtcNow;
            Speak(speaker, moment, 1, force: true, whisperTo: whisperTo);
        }
    }

    // ---- Menu Chat through the AI (Niduus, 2026-11-15, "all" to the ideas) ----

    /// <summary>
    /// [QuestTeam] AIChat: the team's AI chat. Typed chat and whispers get typed answers worded by each wizard's AI, and
    /// Menu Chat gets a Menu Chat answer the AI picks. Off by default, live.
    /// </summary>
    private static bool AiChatOn => ConfigurationManager.GetValue("QuestTeam.AIChat", false);

    /// <summary>The AI's pick came back: that phrase is said, or the usual answer when it found none.</summary>
    private sealed record TeamAiPick(int Speaker, string Fallback, List<MenuChatBook.Phrase> Choices, int Picked, ulong WhisperTo, string Who);

    // Only these may be picked: greetings, thanks, goodbyes, praise and polite noes. No phrase that agrees to anything or
    // names a place, so whatever the AI picks keeps the team's rules (and a Menu Chat phrase can't hold a private detail).
    private List<MenuChatBook.Phrase> AiChoices() {
        var list = new List<MenuChatBook.Phrase>();
        foreach (var moment in new[] { "hello", "thanks", "bye", "won", "no" }) {
            foreach (var words in s_moments[moment]) {
                if (MenuChatBook.Exact(_voiceRandom, words) is { } phrase && list.All(p => p.Id != phrase.Id)) {
                    list.Add(phrase);
                }
            }
        }

        return list;
    }

    private void OnTeamAiPick(TeamAiPick m) {
        if (m.Speaker < 0 || m.Speaker >= _actors.Count || m.Speaker >= _team.Count) {
            return;
        }

        if (m.Picked < 0 || m.Picked >= m.Choices.Count) {
            Note("Team", $"the {m.Who} found no answer" + (m.Fallback is null ? "; the team carries on." : "; the team gives its usual one."));
            if (m.Fallback is not null) {
                Speak(m.Speaker, m.Fallback, 1, force: true, whisperTo: m.WhisperTo);
            }

            return;
        }

        _nextTeamLine = DateTime.UtcNow + s_teamLineGap;
        SayPhrase(m.Speaker, m.Choices[m.Picked], m.WhisperTo, $" (picked by the {m.Who})");
    }

}
