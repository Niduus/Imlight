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
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Groups;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.Game.Tools;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Ultimus's voice. He hears players near him and whispers to him, typed or Menu Chat, and answers through the bots' shared
/// social core (BotSocial): the real answer to what they asked (his quest and its step, where he is, his level, where an
/// NPC stands, how a creature fights), friendly small talk, and a polite no to anything that would change his plan
/// (grouping, following, trading, teleporting): the Main Story comes first (Niduus, 2026-11-12). He talks typed or in Menu
/// Chat (Niduus, 2026-11-14). He never talks with the Quest Team, and the Quest Team never hears him.
/// </summary>
internal static class UltimusVoice {

    internal sealed record Heard(IActorRef Zone, float X, float Y, ulong SpeakerCharId, string SpeakerName, string Text, bool Whisper = false, bool Typed = false,
        bool OrdersChecked = false, bool Group = false);

    // Kept per seat (Ultimus and each TCO7 member has its own; UltimusSeat).
    private static readonly SeatLocal<ConcurrentQueue<Heard>> s_heardSeat = new(() => new ConcurrentQueue<Heard>());
    private static ConcurrentQueue<Heard> s_heard { get => s_heardSeat.Value; set => s_heardSeat.Value = value; }

    // Every seat that is playing hears the players near it (Ultimus and each TCO7 member, each by its own ears).
    private static IEnumerable<UltimusSeat> Listening => UltimusSeat.All.Where(seat => UltimusDirector.MembersOf(seat).Count > 0);

    /// <summary>A player (not Ultimus, a TCO7 member or the Quest Team) used Menu Chat (ChatService); each seat near may answer.</summary>
    internal static void PlayerSaid(IActorRef zone, float x, float y, ulong charId, string name, string text, bool typed = false) {
        if (zone is null || UltimusRoster.IsMember(charId) || QuestTeam.QuestTeamRoster.IsMember(charId)) {
            return;
        }

        foreach (var seat in Listening) {
            var heard = s_heardSeat.Of(seat);
            if (heard.Count <= 20) {
                heard.Enqueue(new Heard(zone, x, y, charId, name ?? "a player", text ?? "", Typed: typed));
            }
        }
    }

    /// <summary>
    /// A player whispered to Ultimus or a TCO7 member (Menu Chat or typed): it answers in Menu Chat, whispered back, wherever
    /// they are.
    /// </summary>
    internal static void PlayerWhispered(ulong charId, string name, string text, bool typed = false, ulong toCharId = 0) {
        var seat = UltimusSeat.OfCharacter(toCharId) ?? UltimusSeat.Ultimus;
        var heard = s_heardSeat.Of(seat);
        if (heard.Count > 20) {
            return;
        }

        heard.Enqueue(new Heard(null, 0, 0, charId, name ?? "a player", text ?? "", Whisper: true, Typed: typed));
    }

    /// <summary>
    /// A player wrote in group chat (the /party line): Ultimus (and each TCO7 member) hears it when he is in that group,
    /// wherever they are, so his group's talk and his leader's orders reach him in battle too (Niduus, 2026-11-15:
    /// "acknowledge group members in battle talking so he knows when a command is given").
    /// </summary>
    internal static void GroupSaid(ulong charId, string name, string text, bool typed = true) {
        if (UltimusRoster.IsMember(charId) || QuestTeam.QuestTeamRoster.IsMember(charId)) {
            return;
        }

        foreach (var seat in Listening) {
            var heard = s_heardSeat.Of(seat);
            if (heard.Count <= 20 && UltimusDirector.MembersOf(seat).Any(m => Groups.GroupRegistry.AreGrouped(m.CharId, charId))) {
                heard.Enqueue(new Heard(null, 0, 0, charId, name ?? "a player", text ?? "", Typed: typed, Group: true));
            }
        }
    }

    internal static bool TryTake(out Heard heard) => s_heard.TryDequeue(out heard);

    /// <summary>A line looked at as a leader's order and found none (the AI said so): it is answered as talk.</summary>
    internal static void Again(Heard heard) => s_heard.Enqueue(heard with { OrdersChecked = true });

}

internal sealed partial class UltimusDirectorActor {

    // Kept tame: one line every 40 seconds at most, and most moments pass without one.
    private static readonly TimeSpan s_lineGap = TimeSpan.FromSeconds(40);
    private DateTime _nextLine = DateTime.MinValue;
    private readonly Dictionary<ulong, DateTime> _answeredAt = [];
    private readonly Random _voiceRandom = new();

    // What it says, by moment: the first words a Menu Chat phrase has win.
    private static readonly Dictionary<string, string[]> s_moments = new(StringComparer.Ordinal) {
        ["welcome"] = ["Welcome", "Glad to have you", "Hello", "Hi", "Let's do this"],
        ["join"] = ["I'll help", "Let me help", "Need help", "Here I come", "Let's fight"],
        ["yes"] = ["Sure", "Okay", "Yes", "Come on", "Let's go"],
        ["won"] = ["Good job", "Well done", "Nice", "Great", "Yay"],
        ["hello"] = ["Hello", "Hi", "Hey", "Greetings"],
        ["no"] = ["No thanks", "No, thank you", "Sorry", "Not now", "No"],
        ["thanks"] = ["You're welcome", "No problem", "Thanks", "Thank you"],
        ["bye"] = ["Goodbye", "Bye", "See you", "Later"],
    };

    // Busy with something it is not doing is never said (it answered "One minute, I'm busy fighting" and "Sorry I'm busy
    // training my pet" while it walked to a quest): phrases with these words are left out unless they are true.
    // Phrases that hold a moment's words but mean something else there (coach review 2026-11-14: "You're welcome" and
    // "Welcome to my home! Have a nice Stay!" said when a player joined his battle, "That quest has a nice reward" after a win).
    private static readonly Dictionary<string, string[]> s_wrongFor = new(StringComparer.Ordinal) {
        ["welcome"] = ["you're welcome", "you are welcome", "home", "stay", "house"],
        ["won"] = ["quest", "reward", "home", "house", "pet", "outfit", "hat", "robe"],
        ["join"] = ["guild", "group", "home", "house"],
        ["hello"] = ["home", "house"],
    };

    private static readonly string[] s_notDoing = ["fighting", "battle", "pet", "pets", "fishing", "garden", "gardening", "crafting", "shopping", "farming", "dueling", "PvP", "house", "brb", "afk",
        "hatch", "hatching", "train", "training", "busy", "someone else", "sleep", "sleeping", "eat", "eating", "dinner", "homework", "mom", "dad", "work", "school work"];


    /// <summary>Says a moment's line through Ultimus's Menu Chat, if it has not spoken lately and the dice allow.</summary>
    /// <remarks>
    /// A phrase that is exactly one of the moment's words comes first; only when <paramref name="exactOnly"/> is false
    /// does a phrase that merely holds them do, never a question and never one that claims something he isn't doing.
    /// An answer to a player is always exact (a looser phrase answered "Follow me" with "Sorry, I am hatching with someone
    /// else"). False when nothing was said.
    /// </remarks>
    private bool Speak(string moment, double chance, bool force = false, ulong whisperTo = 0, bool exactOnly = false) {
        if (_actors.Count == 0 || (!force && (DateTime.UtcNow < _nextLine || _voiceRandom.NextDouble() > chance))) {
            return false;
        }

        var me = _team.Count > 0 ? _team[0] : null;
        var fighting = me?.Wizard is not null && CombatDuelComponent.IsInBattle(me.GlobalId);
        var words = s_moments.GetValueOrDefault(moment) ?? [];
        var phrase = MenuChatBook.Exact(_voiceRandom, words);
        if (phrase is null && !exactOnly) {
            var leaveOut = s_notDoing.Where(w => !(fighting && w is "fighting" or "battle")).Concat(s_wrongFor.GetValueOrDefault(moment) ?? []).ToArray();
            phrase = MenuChatBook.Find(_voiceRandom, leaveOut, words);
            if (phrase is not null && phrase.Text.TrimEnd().EndsWith('?')) {
                phrase = null;
            }
        }

        if (phrase is null) {
            return false;
        }

        _nextLine = DateTime.UtcNow + s_lineGap;
        if (whisperTo != 0) {
            Note("Ultimus", $"whispers back in Menu Chat: \"{phrase.Text}\"");
            _actors[0].Tell(new UltimusMember.WhisperMenuChat(phrase.Id, whisperTo));

            return true;
        }

        Note("Ultimus", $"says in Menu Chat: \"{phrase.Text}\"");
        _actors[0].Tell(new UltimusMember.SayMenuChat(phrase.Id));

        return true;
    }

    /// <summary>
    /// Players talking near Ultimus or whispering to him, typed or Menu Chat (Niduus, 2026-11-14: "Improve the bots ability to
    /// socialize", "He is free to use text chat", "to ask what quest he's doing, he will respond with it"). The shared social
    /// core (BotSocial) works out what they mean and answers with his real data; he answers in their way of talking: Menu
    /// Chat when they used it and a Menu Chat line fits, typed otherwise. "What quest" always gets his quest and its step,
    /// never "busy".
    /// </summary>
    private void AnswerPlayers() {
        while (UltimusVoice.TryTake(out var heard)) {
            var me = _team.Count > 0 ? _team[0] : null;
            // Said around him: heard out to his hearing radius (UltimusPlaybook Chat.HearRadius, live), and always from a
            // player in his own battle. Group chat: heard when he is in that group.
            if (me is null) {
                continue;
            }

            if (heard.Group ? !GroupRegistry.AreGrouped(me.CharId, heard.SpeakerCharId)
                : !heard.Whisper && (me.ZoneActor is null || !me.ZoneActor.Equals(heard.Zone)
                                     || (UltimusPlanner.Far(me.X, me.Y, heard.X, heard.Y) > UltimusPlaybook.Value("Chat.HearRadius") && !InMyBattle(me, heard.SpeakerCharId)))) {
                continue;
            }

            // Only what is said to him (approved 2026-11-14: "don't reply to chat not aimed at him").
            if (!AimedAtMe(heard, me, out var notMine)) {
                Note("Ultimus", $"{heard.SpeakerName} said \"{UltimusText.Short(heard.Text, 60)}\" near him; he stays out of it ({notMine}).");
                continue;
            }

            // Players he met are kept in players.json too (UltimusDirector.Life.cs).
            var cameBack = Remember(heard.SpeakerCharId, heard.SpeakerName, heard.Whisper);
            // What they are working on, and where they left off when they come back (UltimusDirector.People.cs).
            NotePerson(heard, cameBack);
            // "Stop" / "got it", "help me fight", "walk me through this quest", "do you remember me?"
            // (UltimusDirector.Teach.cs, UltimusDirector.Helper.cs, UltimusDirector.People.cs).
            if (!heard.OrdersChecked && (StopAsk(heard) || HelpAsk(heard, me) || TeachAsk(heard) || RememberMeAsk(heard)
                                         || HandAsk(heard, me) || SpellAsk(heard, me))) {
                _answeredAt[heard.SpeakerCharId] = DateTime.UtcNow;
                continue;
            }

            // His group leader's orders ("stay here", "let me go first", "cast Dragonblade on Mitsu"): UltimusDirector.Orders.cs.
            if (!heard.OrdersChecked && LeaderOrders(heard, me)) {
                _answeredAt[heard.SpeakerCharId] = DateTime.UtcNow;
                continue;
            }

            // Advice while he is stuck is kept on the step's issue, and he thanks them (UltimusDirector.Break.cs).
            if (heard.Typed && MaybeTip(heard.SpeakerName, heard.SpeakerCharId, heard.Text, heard.Whisper)) {
                _nextLine = DateTime.UtcNow + s_lineGap;
                _answeredAt[heard.SpeakerCharId] = DateTime.UtcNow;
                continue;
            }

            // "How do I train Tower Shield?", "where do I get this hat?": from the real game's data, never the AI's guess
            // (Niduus, 2026-11-16: "He will answer what he finds in the game's VANILLA files").
            if (heard.Typed && UltimusLookup.Answer(heard.SpeakerCharId, heard.Text) is { Length: > 0 } looked) {
                Think($"{heard.SpeakerName} asked \"{UltimusText.Short(heard.Text, 80)}\", so I look it up in the real game's data.",
                    "lookup|" + heard.SpeakerCharId + "|" + heard.Text);
                _answeredAt[heard.SpeakerCharId] = DateTime.UtcNow;
                SendReply(heard, looked, "game data");
                continue;
            }

            var how = (heard.Whisper ? "whispered" : "said") + (heard.Typed ? "" : " in Menu Chat");
            var reply = BotSocial.Answer(SocialSelf(me), heard.SpeakerCharId, heard.SpeakerName, heard.Text, heard.Typed, heard.Whisper);
            if (reply is null) {
                Note("Ultimus", $"{heard.SpeakerName} {how}: \"{heard.Text}\"; Ultimus lets it pass (just answered them, or they are muted).");
                continue;
            }

            Think($"{heard.SpeakerName} {how} \"{UltimusText.Short(heard.Text, 80)}\" ({reply.Intent}). {reply.Why}", "social|" + heard.SpeakerCharId + "|" + heard.Text);
            // A hello from a player near him: a wave back from the emote wheel (UltimusDirector.Emotes.cs).
            if (reply.Intent == BotSocial.GREETING && !heard.Whisper) {
                WaveAt(heard.SpeakerCharId, heard.SpeakerName);
            }

            if (reply.Text.Length == 0) {
                continue;
            }

            _nextLine = DateTime.UtcNow + s_lineGap;
            _answeredAt[heard.SpeakerCharId] = DateTime.UtcNow;
            var whisperTo = heard.Whisper ? heard.SpeakerCharId : 0;
            // A Menu Chat line gets a Menu Chat answer only when a phrase says exactly that; otherwise he types.
            if (!heard.Typed && reply.MenuMoment is { } moment && s_moments.ContainsKey(moment) && Speak(moment, 1, force: true, whisperTo: whisperTo, exactOnly: true)) {
                continue;
            }

            // The optional local AI words it (UltimusLocalAI): typed talk only, never for what makes him do something; the
            // keyword answer is the fallback and its facts are given to the model.
            if (heard.Typed && UltimusLocalAI.Ready && UltimusLocalAI.PlayerUnder(heard.SpeakerCharId) && reply.Intent is not (BotSocial.ORDER or BotSocial.PERMISSION or BotSocial.GROUP
                    or BotSocial.HELP or BotSocial.TRADE or BotSocial.PRIVATE or BotSocial.RUDE or "spam")) {
                var facts = AiFacts(me);
                var history = TalkWith(heard.SpeakerCharId).ToList();
                var keyword = reply.Text;
                // A keyword answer that only says he doesn't know is no fact to keep: the AI may answer from its own Wizard101
                // knowledge instead (Niduus's chat, 2026-11-15: "Where do I get Tower Shield?" got "I am not sure about that").
                var hint = KeywordKnowsNothing(reply) ? "" : keyword;
                var learned = UltimusAiLearned.Find(heard.Text);
                _nextLine = DateTime.UtcNow + TimeSpan.FromSeconds(15);
                UltimusLocalAI.Ask(facts, history, heard.Text, hint.Length > 0 ? hint : learned ?? "", heard.SpeakerCharId)
                    .ContinueWith(t => new AiWorded(heard, keyword, t.IsCompletedSuccessfully ? t.Result : null, learned, KeywordKnowsNothing(reply)))
                    .PipeTo(Self);
                continue;
            }

            // Without the AI: an answer he learned from it earlier, when his keyword chat knows nothing (UltimusAiLearned).
            if (heard.Typed && KeywordKnowsNothing(reply) && UltimusAiLearned.Find(heard.Text) is { } known) {
                SendReply(heard, known, LEARNED);
                continue;
            }

            SendReply(heard, reply.Text, "keyword chat");
        }
    }

    private const string LEARNED = "learned from the AI";

    // "Niduus made me" tacked onto an answer that wasn't about him (his PC log, 2026-11-15: "where are you?" got "...working on
    // my quest Internal Investigation. Niduus made me."): that sentence is left out unless the player asked who made him.
    internal static string WithoutMaker(string heard, string answer) {
        if (BotSocialWords.AsksAboutMe(heard)) {
            return answer;
        }

        var sentences = System.Text.RegularExpressions.Regex.Split(answer.Trim(), @"(?<=[.!?])\s+").Where(s => s.Trim().Length > 0).ToList();
        var kept = sentences.Where(s => !System.Text.RegularExpressions.Regex.IsMatch(s,
            @"\b(niduus|(made|created|built|coded) (me|by)|i am a bot|i'm a bot|i am just a bot|i'm just a bot)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();

        return kept.Count == 0 || kept.Count == sentences.Count ? answer : string.Join(" ", kept);
    }

    // Whether the keyword answer is only "I don't know" (a question he can't place, a tip he has no data for).
    private static bool KeywordKnowsNothing(BotSocial.Reply reply)
        => reply.Intent == BotSocial.QUESTION
           || reply.Intent == BotSocial.TIP && new[] { "not sure", "don't know", "haven't been there", "no idea", "no data" }.Any(w => reply.Text.Contains(w, StringComparison.OrdinalIgnoreCase));

    // The local AI's wording came back (or didn't): what passes the private-details block is said, else what he learned
    // from the AI before, else the keyword answer. A Wizard101 answer is kept to learn from (UltimusAiLearned).
    private sealed record AiWorded(UltimusVoice.Heard Heard, string Keyword, string Worded, string Learned, bool KeywordEmpty);

    private void OnAiWorded(AiWorded m) {
        var worded = m.Worded;
        if (worded is not null && !ReferenceEquals(BotSocial.Scrub(worded), worded)) {
            Note("Ultimus", $"the {UltimusLocalAI.Who}'s answer had a private detail in it; he says the keyword answer instead.");
            worded = null;
        }

        if (worded is not null) {
            worded = WithoutMaker(m.Heard.Text, worded);
            var self = _team.Count > 0 ? SocialSelf(_team[0]) : null;
            UltimusAiLearned.Learn(m.Heard.Text, worded, UltimusLocalAI.Who, m.Heard.SpeakerName, [self?.Quest, self?.Area, self?.World, self?.Step, self?.Doing]);
            SendReply(m.Heard, worded, UltimusLocalAI.Who);
            return;
        }

        if (m.KeywordEmpty && m.Learned is { Length: > 0 } learned) {
            SendReply(m.Heard, learned, LEARNED);
            return;
        }

        SendReply(m.Heard, m.Keyword, "keyword chat");
    }

    // The talk with each player (theirs and his), for the local AI to follow the conversation.
    private readonly Dictionary<ulong, List<(string Who, string Text, DateTime At)>> _talks = [];

    private IEnumerable<(string Who, string Text)> TalkWith(ulong charId) {
        if (!_talks.TryGetValue(charId, out var talk)) {
            return [];
        }

        // A talk older than 10 minutes is over: a new one starts fresh.
        talk.RemoveAll(l => DateTime.UtcNow - l.At > TimeSpan.FromMinutes(10));
        return talk.Select(l => (l.Who, l.Text));
    }

    private void NoteTalk(ulong charId, string who, string text) {
        if (!_talks.TryGetValue(charId, out var talk)) {
            _talks[charId] = talk = [];
        }

        talk.Add((who, text, DateTime.UtcNow));
        if (talk.Count > 12) {
            talk.RemoveRange(0, talk.Count - 12);
        }
    }

    // His real facts for the local AI: who and where he is, what he does, and his latest memories.
    private string AiFacts(UltimusMember.Status me) {
        var self = SocialSelf(me);
        var lines = new List<string> {
            $"Name: {self.Name}. School: {self.School}. Level: {self.Level}.",
            $"Where: {self.Area}, {self.World}.",
            $"Quest: {(string.IsNullOrWhiteSpace(self.Quest) ? "none right now" : self.Quest)}. Step: {BotSocial.Tidy(self.Step) ?? "none"}.",
            // Where the quest itself is, which is not always where he stands (Niduus's chat, 2026-11-15: grouped up and taken
            // to Aquila, the AI said his Marleybone quest "Internal Investigation" was "right here in Aquila").
            QuestWhereFact(me),
            $"Doing now: {BotSocial.Tidy(self.Doing) ?? "questing"}. In a battle: {(self.InBattle ? "yes" : "no")}. Health: {(self.Health >= 0 ? self.Health + "%" : "unknown")}. Mood: {self.Mood}.",
            $"Worlds been to: {string.Join(", ", self.Worlds ?? [])}.",
        };
        var memories = UltimusJournal.Recent(6);
        if (memories.Count > 0) {
            lines.Add("Latest memories: " + string.Join(" | ", memories.Select(e => $"{e.Text} ({UltimusJournal.Ago(e.At)})")));
        }

        return string.Join("\n", lines.Where(l => l.Length > 0));
    }

    // "Quest is in: The Ironworks, Marleybone." plus, away from it, why he is where he is; "" when the quest names no area.
    private string QuestWhereFact(UltimusMember.Status me) {
        var questName = _currentQuest ?? (_target ?? _leaderLastTarget)?.Quest;
        if (string.IsNullOrEmpty(questName) || QuestTemplateCollection.GetQuestByName(questName) is not { } quest) {
            return "";
        }

        var zone = (quest.m_goals ?? []).Where(g => g is not null).Select(OverlayQuestTracker.StepZone)
            .Concat((quest.m_goals ?? []).Where(g => g is not null).Select(g => (string) g.m_destinationZone))
            .FirstOrDefault(z => !string.IsNullOrWhiteSpace(z));
        if (string.IsNullOrWhiteSpace(zone)) {
            return "";
        }

        var questWorld = WorldOf(zone);
        var fact = $"Quest is in: {UltimusText.Zone(zone)}, {questWorld}.";
        if (!string.Equals(questWorld, WorldOf(me.Zone), StringComparison.OrdinalIgnoreCase)) {
            fact += PartyMateName.Length > 0
                ? $" I am in {WorldOf(me.Zone)} only because I am in a group with {PartyMateName}; my quest waits in {questWorld}."
                : $" I am not in {questWorld} right now; my quest is there, not here.";
        }

        return fact;
    }

    private void SendReply(UltimusVoice.Heard heard, string text, string by) {
        var whisperTo = heard.Whisper ? heard.SpeakerCharId : 0;
        var how = (heard.Whisper ? "whispered" : heard.Group ? "said in group chat" : "said") + (heard.Typed ? "" : " in Menu Chat");
        // Group chat is answered in group chat (the group's channel), so the whole group sees it, in battle too.
        var channel = heard.Group && _team.Count > 0 && GroupRegistry.TryGetGroup(_team[0].CharId, out var group) ? group.ChannelId : 0;
        Note("Ultimus", $"{heard.SpeakerName} {how}: \"{heard.Text}\"; Ultimus {(heard.Whisper ? "whispers back" : channel != 0 ? "answers in group chat" : "says")} ({by}): \"{text}\"");
        NoteTalk(heard.SpeakerCharId, "player", heard.Text);
        NoteTalk(heard.SpeakerCharId, "me", text);
        // Who answered, marked in his Thoughts (Niduus, 2026-11-15, "all"): the AI or his normal chat.
        Think($"[{(by == "keyword chat" ? "normal chat" : by)}] I answered {heard.SpeakerName}: \"{UltimusText.Short(text, 120)}\"",
            "answered|" + heard.SpeakerCharId + "|" + text);
        // A long answer goes in a few messages, each within the chat's length, a moment apart (Niduus, 2026-11-14: "allow for him
        // to send in multiple messages. Aware of the chat limits"). The whole answer still counts as one line for his
        // spam and repeat guards (BotSocial), and the next line waits until the last part is out.
        var parts = ChatParts(text);
        for (var i = 0; i < parts.Count; i++) {
            IActorRef actor = _actors[0];
            object message = heard.Whisper ? new UltimusMember.WhisperText(parts[i], whisperTo)
                : channel != 0 ? new UltimusMember.WhisperText(parts[i], channel)
                : new UltimusMember.SayText(parts[i]);
            if (i == 0) {
                actor.Tell(message);
            }
            else {
                Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(PART_GAP_SECONDS * i), actor, message, Self);
            }
        }

        _nextLine = DateTime.UtcNow + s_lineGap + TimeSpan.FromSeconds(PART_GAP_SECONDS * (parts.Count - 1));
    }

    // One chat message's most characters, the most messages for one answer, and the gap between them.
    private const int PART_LENGTH = 120;
    private const int MOST_PARTS = 3;
    private const double PART_GAP_SECONDS = 1.6;

    /// <summary>
    /// An answer cut into chat messages: at sentence ends where it can, else between words; never more than three. Every
    /// part is checked for sensitive details again (BotSocial.Scrub), whatever made it.
    /// </summary>
    internal static List<string> ChatParts(string text) {
        var parts = new List<string>();
        var rest = (text ?? "").Trim();
        while (rest.Length > 0 && parts.Count < MOST_PARTS) {
            if (rest.Length <= PART_LENGTH) {
                parts.Add(rest);
                break;
            }

            // The last sentence end, else the last space, within the length; a word longer than that is cut.
            var window = rest[..PART_LENGTH];
            var cut = new[] { window.LastIndexOf(". ", StringComparison.Ordinal), window.LastIndexOf("! ", StringComparison.Ordinal),
                window.LastIndexOf("? ", StringComparison.Ordinal) }.Max();
            cut = cut >= PART_LENGTH / 3 ? cut + 1 : window.LastIndexOf(' ');
            cut = cut <= 0 ? PART_LENGTH : cut;
            var part = rest[..cut].Trim();
            rest = rest[cut..].Trim();
            // The last allowed part ends the answer cleanly rather than mid-thought.
            if (parts.Count == MOST_PARTS - 1 && rest.Length > 0) {
                part = part.TrimEnd(',', ';', ':') + (part.EndsWith('.') || part.EndsWith('!') || part.EndsWith('?') ? "" : "...");
                rest = "";
            }

            parts.Add(part);
        }

        return [.. parts.Select(BotSocial.Scrub).Where(p => p.Length > 0).Distinct()];
    }

    // Who spoke near him lately (not to him), to tell a talk between players from a line for him.
    private readonly List<(ulong CharId, DateTime At)> _talkNear = [];
    private readonly Dictionary<ulong, DateTime> _namedBy = [];

    /// <summary>
    /// Whether a line is for him: whispered; his name in it; a talk he is already in with them (answered in the last 90
    /// seconds); or said close to him (within 300) while no other player is talking nearby. Anything else is players
    /// talking among themselves, and he stays out of it.
    /// </summary>
    private bool AimedAtMe(UltimusVoice.Heard heard, UltimusMember.Status me, out string why) {
        why = "";
        var now = DateTime.UtcNow;
        _talkNear.RemoveAll(t => now - t.At > TimeSpan.FromSeconds(60));
        var othersTalking = !heard.Whisper && _talkNear.Any(t => t.CharId != heard.SpeakerCharId);
        if (!heard.Whisper) {
            _talkNear.Add((heard.SpeakerCharId, now));
        }

        if (heard.Whisper || heard.Group) {
            return true;
        }

        // A player in his own battle talking: his team in the fight, so it is for him (orders like "heal me" or "wait"
        // come this way; Niduus, 2026-11-15).
        if (InMyBattle(me, heard.SpeakerCharId)) {
            return true;
        }

        var name = me.Wizard?.PlayerNameBehavior?.GetWizardName() ?? UltimusRoster.NAME;
        if (MenuChatBook.Says(heard.Text, UltimusRoster.NAME) || MenuChatBook.Says(heard.Text, name.Split(' ')[0])) {
            _namedBy[heard.SpeakerCharId] = now;
            return true;
        }

        // They called him by name a moment ago: what follows is for him too ("hey Ultimus" then "You stuck again?").
        if (_namedBy.TryGetValue(heard.SpeakerCharId, out var named) && now - named < TimeSpan.FromMinutes(2)) {
            return true;
        }

        if (BotSocial.Of(UltimusRoster.NAME, heard.SpeakerCharId) is { } friend && now - friend.RepliedAt < TimeSpan.FromSeconds(90)) {
            return true;
        }

        if (othersTalking) {
            why = "other players are talking here and it doesn't name him";
            return false;
        }

        if (UltimusPlanner.Far(me.X, me.Y, heard.X, heard.Y) > UltimusPlaybook.Value("Chat.NearRadius")) {
            why = "said away from him and not to him";
            return false;
        }

        return true;
    }

    // Whether this player is in Ultimus's battle right now.
    private static bool InMyBattle(UltimusMember.Status me, ulong charId) {
        try {
            return me.Wizard is not null && CombatDuelComponent.IsInBattle(me.GlobalId)
                   && CombatDuelComponent.UltimusBattlePlayers(me.GlobalId).Any(p => p.CharId == charId);
        }
        catch (Exception) {
            return false;
        }
    }

    /// <summary>What Ultimus knows of himself right now, for the social core: his real quest and step, area, level, mood.</summary>
    private BotSocial.Self SocialSelf(UltimusMember.Status me) {
        var target = _target ?? _leaderLastTarget;
        var questName = _currentQuest ?? target?.Quest;
        if (string.IsNullOrEmpty(questName)) {
            questName = me.Wizard?.QuestBehavior?.CurrentQuestInstances?.Select(q => q?.QuestName)
                .FirstOrDefault(n => n is not null && QuestTemplateCollection.GetQuestByName(n) is { } t && UltimusPlanner.IsMainStoryWorld(t));
        }

        var quest = string.IsNullOrEmpty(questName) ? null : QuestTemplateCollection.GetQuestByName(questName);
        var progress = "";
        var goal = quest?.m_goals?.FirstOrDefault(g => g is not null && target is not null && string.Equals(target.Quest, questName, StringComparison.OrdinalIgnoreCase)
            && g.m_goalName == target.Goal);
        // The step he is on: the one he is heading for, else the active step his quest book holds (2026-11-14: "what step"
        // got only the quest name while he was off healing).
        if (goal is null && quest is not null && me.Wizard?.QuestBehavior?.CurrentQuestInstances?
                .FirstOrDefault(q => q is not null && string.Equals(q.QuestName, questName, StringComparison.OrdinalIgnoreCase)) is { } held) {
            var active = held.GoalProgress?.FirstOrDefault(g => g is not null && held.IsGoalActive(g.GoalName));
            goal = active is null ? null : quest.m_goals?.FirstOrDefault(g => g is not null && g.m_goalName == active.GoalName);
            if (goal is not null && active.CurrentProgress is > 0 and < int.MaxValue && StepTotal(goal) is > 1 and var total) {
                progress = $" ({Math.Min(active.CurrentProgress, total)} of {total})";
            }
        }

        var step = goal is null ? "" : QuestGuide.StepText(goal);
        if (string.IsNullOrWhiteSpace(step) && target is not null && string.Equals(target.Quest, questName, StringComparison.OrdinalIgnoreCase)) {
            step = target.What ?? "";
        }

        step = step.Trim().TrimEnd('.') + progress;

        var title = string.IsNullOrEmpty(questName) ? null : UltimusText.Title(questName).Trim().Trim('"').Trim();
        var mood = UltimusMood.Now switch {
            UltimusMood.Level.Bold => "proud",
            UltimusMood.Level.Careful => "frustrated",
            _ => "steady",
        };

        return new BotSocial.Self(UltimusRoster.NAME, me.Wizard?.PlayerNameBehavior?.GetWizardName() ?? UltimusRoster.NAME, UltimusSeat.Current.Role,
            me.Wizard?.MagicSchoolBehavior?.Level ?? 1, UltimusText.Zone(me.Zone), WorldOf(me.Zone), title, step,
            me.Wizard is not null && CombatDuelComponent.IsInBattle(me.GlobalId), mood, who => NpcTip(me, who, _currentQuest ?? _target?.Quest), FoeTip, _doing,
            me.Wizard?.GameStats is { } hp ? (int) (hp.m_currentHitpoints * 100L / Math.Max(1, hp.m_baseHitpoints + hp.m_bonusHitpoints)) : -1,
            what => what switch {
                "potions" => AskedForPotions(me).Line,
                "potions-status" => DateTime.UtcNow < _potionRunUntil ? "Yes, I'm on my way to the potion shop now!" : AskedForPotions(me).Line,
                _ when what.StartsWith("recall:", StringComparison.Ordinal) => Recall(what[7..]),
                _ when what.StartsWith("fact:", StringComparison.Ordinal) => Fact(me, what[5..]),
                _ when what.StartsWith("asker:", StringComparison.Ordinal) => Asker(what[6..]),
                _ => null,
            }, Worlds().Select(w => w.World).ToList(), Opinion, Groups: UltimusWire.PartyMode, PartyWith: PartyMateName,
            StuckNow: StuckReport(me, title, step), Progress: $"My last step done or XP gained was {Minutes((DateTime.UtcNow - _progressAt).TotalSeconds)} ago");
    }

    // "Stuck?" answered from his real state (Niduus, 2026-11-15): where he is, what he is trying, for how long, and what he
    // does about it. Null when nothing says he is stuck (then the usual "No, I'm fine" answer).
    private string StuckReport(UltimusMember.Status me, string quest, string step) {
        var idle = DateTime.UtcNow - _progressAt;
        var trips = _zoneTrail.Count >= 3 && _zoneTrail.Select(z => z.Zone).Distinct(StringComparer.OrdinalIgnoreCase).Count() <= 3;
        var stuckWord = _doing?.StartsWith("stuck", StringComparison.Ordinal) == true || _doing?.StartsWith("looking around", StringComparison.Ordinal) == true;
        if (!stuckWord && !trips && idle < TimeSpan.FromMinutes(10)) {
            return null;
        }

        var what = string.IsNullOrWhiteSpace(step) ? _doing ?? "my next step" : step;
        var areas = trips ? string.Join(" and ", _zoneTrail.Select(z => UltimusText.Zone(z.Zone)).Distinct(StringComparer.OrdinalIgnoreCase)) : "";

        return $"Yes, I think so. I'm in {UltimusText.Zone(me.Zone)}" + (quest is { Length: > 0 } ? $" on {quest}" : "") + $", trying to: {what}. " +
               $"My last step done or XP gained was {Minutes(idle.TotalSeconds)} ago" +
               (trips ? $", and I keep going back and forth between {areas}" : "") + ". I'm trying another way, the way a player would.";
    }

    private static int StepTotal(GoalTemplate goal) => goal switch {
        BountyGoalTemplate { m_bountyTotal: > 0 } bounty => bounty.m_bountyTotal,
        ScavengeGoalTemplate { m_itemTotal: > 0 } scavenge => scavenge.m_itemTotal,
        _ => goal.m_tallyCounter?.m_count ?? 0,
    };

    // "Where is Headmaster Ambrose?": from his NPC book (every person the story worlds' quests name, from the game's own
    // area files, so also in places he hasn't been). "Where is <a friend>?": the way a player knows it, from the friends
    // list (Niduus, 2026-11-15: "If he's friends with someone, he should be able to say where they are at from natural means").
    private static string NpcTip(UltimusMember.Status me, string who, string quest = null) {
        if (FriendWhere(me, who) is { } friendAt) {
            return friendAt;
        }

        var (name, places) = UltimusNpcBook.FindAll(who, me?.Zone ?? "");
        if (places.Count == 0) {
            return null;
        }

        // Only what the game's files say (Niduus, 2026-11-15: "only accurate data"): every area someone by that name stands
        // in, not just the first one found, and the one his own quest needs said first, from the Quest Atlas.
        var forQuest = UltimusAtlas.OfQuest(quest)
            .Where(s => string.Equals(s.Person, name, StringComparison.OrdinalIgnoreCase))
            .Select(s => s.RealZone is { Length: > 0 } real ? real : s.StepZone)
            .FirstOrDefault(z => !string.IsNullOrEmpty(z) && places.Any(p => OverlayQuestTracker.SameZone(p.Zone, z) || OverlayQuestTracker.SameZone(z, p.Zone)));
        var text = forQuest is not null ? $"For my quest \"{UltimusText.Title(quest)}\", {name} is in {UltimusText.Zone(forQuest)}, {WorldOf(forQuest)}." : "";
        var others = places.Where(p => forQuest is null || !(OverlayQuestTracker.SameZone(p.Zone, forQuest) || OverlayQuestTracker.SameZone(forQuest, p.Zone))).ToList();
        if (others.Count == 0) {
            return text;
        }

        var shown = others.Take(3).Select(p => $"{UltimusText.Zone(p.Zone)} ({WorldOf(p.Zone)})").ToList();
        var list = shown.Count == 1 ? shown[0] : string.Join(", ", shown.Take(shown.Count - 1)) + " and " + shown[^1];
        var more = others.Count > 3 ? $", and {others.Count - 3} more place(s)" : "";

        return text.Length > 0
            ? $"{text} There's also a {name} in {list}{more}."
            : $"{name} stands in {list}{more}.";
    }

    // A player or bot by wizard name: where they are when they are his friend and online (as his friends list shows it);
    // otherwise null, or a plain "not on my friends list" when they are online but not his friend.
    private static string FriendWhere(UltimusMember.Status me, string who) {
        var name = (who ?? "").Trim().TrimEnd('?', '!', '.').Trim();
        if (name.Length < 3 || me?.Wizard is not { } mine) {
            return null;
        }

        foreach (var online in WizardData.Collections.OnlinePlayerCollection.GetOnlinePlayers()) {
            var wizard = WizardData.Collections.WizardCollection.GetCharacterUnloaded(online.CharacterId);
            var shown = wizard?.PlayerNameBehavior?.GetWizardName();
            if (string.IsNullOrEmpty(shown) || !string.Equals(shown, name, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            if (online.CharacterId == mine.CharId) {
                return $"That's me! I'm in {UltimusText.Zone(me.Zone)}.";
            }

            if (!WizardData.Collections.BuddyRelationshipCollection.AreFriends(mine.CharId, online.CharacterId)) {
                return $"{shown} isn't on my friends list, so I can't see where they are.";
            }

            var zone = string.IsNullOrEmpty(online.CurrentZone) ? "" : online.CurrentZone;

            return zone.Length == 0 ? $"{shown} is online, but my friends list doesn't say where."
                : $"My friends list says {shown} is in {UltimusText.Zone(zone)}, {WorldOf(zone)}.";
        }

        return null;
    }

    // "How do I beat Rattlebones?": from what the bots have seen that creature do.
    private static string FoeTip(string who) {
        if (BotKnowledge.ByName(who) is not { Casts: > 0 } habit) {
            return null;
        }

        var kinds = new[] { BotKnowledge.HIT, BotKnowledge.HEAL, BotKnowledge.SHIELD, BotKnowledge.BLADE, BotKnowledge.TRAP }
            .Where(k => habit.Times(k) > 0).OrderByDescending(habit.Times).Take(2).ToList();
        var tip = $"From {habit.Fights} fight(s) with {habit.Name}: it mostly casts {string.Join(" and ", kinds.Select(k => k + "s"))}";
        if (habit.Schools.Count > 0) {
            var school = habit.Schools.OrderByDescending(p => p.Value).First().Key;
            tip += $", hits with {school}, so bring a {school} shield";
        }

        if (habit.Chance(BotKnowledge.HEAL) > 0.2) {
            tip += "; it heals a lot, so hit hard all at once";
        }

        return tip + ".";
    }

}
