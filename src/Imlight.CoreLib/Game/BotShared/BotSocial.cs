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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// The bots' shared social core (Niduus, 2026-11-14: "Improve the bots ability to socialize. Give it what you would for
/// something like me and you talking"; "He is free to use text chat"). Any server-played wizard can use it; Ultimus is the
/// first. It reads what a player said (typed or Menu Chat, said or whispered), works out what they mean, and answers the
/// actual question with the bot's real data, in a voice of its own:
///   Memory          per player: name, when first met, how often talked, what they said last, their school and world,
///                   battles fought together. Brought up naturally ("Welcome back! Still in Krokotopia?").
///   Intent          greeting, "what quest", "where", level, school, a tip (where an NPC stands, how a creature fights),
///                   help, group/follow, trade/gold, teleport, compliment, joke, thanks, goodbye, "are you a bot", rude.
///   Turn-taking     sometimes a short question back, the answer waited for and kept; one reply per message, a few
///                   seconds between replies to the same player, a limit per minute.
///   Personality     a friendly, slightly nerdy Balance wizard; his mood (proud, steady, frustrated but upbeat) colours
///                   the words; the same line is never said twice in a row.
///   Boundaries      solo (no groups), no gold or item handouts, never server or private info, never claims to be human
///                   when sincerely asked; rude or spamming players are muted for a while.
/// Each answer comes back with the reason for it, for the bot's Thoughts.
/// </summary>
internal static class BotSocial {

    /// <summary>What the bot knows of itself right now, to answer with.</summary>
    internal sealed record Self(string Bot, string Name, string School, int Level, string Area, string World, string Quest, string Step,
        bool InBattle, string Mood, Func<string, string> FindNpc = null, Func<string, string> FoeTip = null);

    /// <summary>An answer: the line, the intent it answers, why (for Thoughts), and whether it fits a Menu Chat moment.</summary>
    internal sealed record Reply(string Text, string Intent, string Why, string MenuMoment = null);

    internal sealed class Friend {
        public string Name { get; set; } = "";
        public DateTime First { get; set; } = DateTime.UtcNow;
        public DateTime Last { get; set; } = DateTime.MinValue;
        public int Talks { get; set; }
        public int BattlesTogether { get; set; }
        public string School { get; set; } = "";
        public string World { get; set; } = "";
        public List<string> Heard { get; set; } = [];
        public string Pending { get; set; } = "";
        public DateTime PendingAt { get; set; }
        public DateTime MutedUntil { get; set; }
        public DateTime RepliedAt { get; set; }
        public List<DateTime> Lines { get; set; } = [];
    }

    private const string FOLDER = "logs/bots";
    private static readonly object s_lock = new();
    private static readonly Dictionary<string, Dictionary<ulong, Friend>> s_friends = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Queue<DateTime>> s_said = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> s_lastLine = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Random s_random = new();

    private const int MOST_PER_MINUTE = 8;
    private static readonly TimeSpan s_perPlayerGap = TimeSpan.FromSeconds(3);

    // ---------------------------------------------------------------------
    // Intents
    // ---------------------------------------------------------------------

    internal const string GREETING = "greeting", HOW_ARE_YOU = "how are you", QUEST = "his quest", WHERE = "where he is", LEVEL = "his level",
        SCHOOL = "his school", TIP = "a tip", HELP = "help", GROUP = "group or follow", TRADE = "trade or gold", TELEPORT = "teleport",
        COMPLIMENT = "compliment", JOKE = "joke", THANKS = "thanks", BYE = "goodbye", BOT = "are you a bot", PRIVATE = "private info",
        RUDE = "rude", ANSWER = "answer to his question", CHAT = "small talk", NAME = "his name";

    private static readonly string[] s_schools = ["fire", "ice", "storm", "myth", "life", "death", "balance"];

    private static readonly string[] s_rude = ["stupid", "idiot", "noob", "shut up", "stfu", "trash", "dumb", "loser", "hate you", "you suck", "kys", "ugly", "moron"];

    private static bool Has(string text, params string[] words) => words.Any(w => MenuChatBook.Says(text, w));

    /// <summary>What a line means (the first intent that fits, most specific first).</summary>
    internal static string Classify(string text, Friend friend) {
        var t = (text ?? "").Trim();
        if (t.Length == 0) {
            return null;
        }

        if (Has(t, s_rude)) {
            return RUDE;
        }

        if (Has(t, "are you a bot", "are you real", "are you human", "you a bot", "is this a bot", "bot?", "are you ai", "real person", "a robot")) {
            return BOT;
        }

        if (Has(t, "password", "ip", "server ip", "admin", "address", "where do you live", "phone", "email", "owner", "who made you", "who runs")) {
            return PRIVATE;
        }

        // An answer to the question he asked (a school or a world), within two minutes.
        if (friend is { Pending.Length: > 0 } && DateTime.UtcNow - friend.PendingAt < TimeSpan.FromMinutes(2)
            && (friend.Pending == SCHOOL && s_schools.Any(s => Has(t, s)) || friend.Pending == WHERE && t.Split(' ').Length <= 6 && !t.Contains('?'))) {
            return ANSWER;
        }

        if (Has(t, "what quest", "which quest", "quest are you", "working on", "your quest", "what are you doing", "what you doing", "whatcha doing",
                "what r u doing", "wyd", "what are you up to", "up to", "doing what", "on what quest", "what step", "quest?")) {
            return QUEST;
        }

        if (Has(t, "where is", "where's", "wheres", "how do i beat", "how to beat", "how do you beat", "tips for", "tip for", "any tips", "how do i get to")) {
            return TIP;
        }

        if (Has(t, "where are you", "where r u", "what world", "which world", "where you at", "where are you going", "where to", "location")) {
            return WHERE;
        }

        if (Has(t, "what level", "your level", "how high", "lvl", "level?")) {
            return LEVEL;
        }

        if (Has(t, "what school", "your school", "which school", "school?")) {
            return SCHOOL;
        }

        if (Has(t, "your name", "who are you", "whats your name", "what's your name")) {
            return NAME;
        }

        if (Has(t, "group", "party", "join me", "join us", "team up", "follow me", "come with", "come here", "follow", "duo", "carry me")) {
            return GROUP;
        }

        if (Has(t, "gold", "trade", "give me", "gift", "items", "money", "crowns", "free stuff", "can i have")) {
            return TRADE;
        }

        if (Has(t, "teleport", "port to", "tp to", "port me", "tp me")) {
            return TELEPORT;
        }

        if (Has(t, "help", "help me", "need help", "can you help", "stuck")) {
            return HELP;
        }

        if (Has(t, "thanks", "thank you", "thx", "ty")) {
            return THANKS;
        }

        if (Has(t, "bye", "goodbye", "see you", "see ya", "cya", "later", "gtg", "gotta go", "good night", "gn")) {
            return BYE;
        }

        if (Has(t, "how are you", "how r u", "how's it going", "hows it going", "how are things", "how you doing", "sup", "what's up", "whats up")) {
            return HOW_ARE_YOU;
        }

        if (Has(t, "cool", "nice", "awesome", "great job", "good job", "gj", "gg", "love your", "amazing", "you're good", "well done")) {
            return COMPLIMENT;
        }

        if (Has(t, "lol", "haha", "lmao", "joke", "funny", "xd", "rofl")) {
            return JOKE;
        }

        if (Has(t, "hello", "hi", "hey", "greetings", "yo", "hiya", "howdy", "good morning", "good evening")) {
            return GREETING;
        }

        return CHAT;
    }

    // ---------------------------------------------------------------------
    // Answering
    // ---------------------------------------------------------------------

    /// <summary>
    /// The bot's answer to <paramref name="text"/> from a player, or null to stay quiet (muted, too soon, nothing to say).
    /// <paramref name="typed"/>: the player typed it (else Menu Chat).
    /// </summary>
    internal static Reply Answer(Self self, ulong charId, string name, string text, bool typed, bool whisper) {
        lock (s_lock) {
            var friends = FriendsOf(self.Bot);
            var known = friends.TryGetValue(charId, out var friend);
            if (!known) {
                friends[charId] = friend = new Friend { Name = name ?? "" };
            }

            var now = DateTime.UtcNow;
            var away = friend.Last == DateTime.MinValue ? TimeSpan.Zero : now - friend.Last;
            friend.Name = string.IsNullOrWhiteSpace(name) ? friend.Name : name;
            friend.Last = now;
            friend.Lines.RemoveAll(l => now - l > TimeSpan.FromSeconds(60));
            friend.Lines.Add(now);
            // The same line again counts only within a couple of minutes ("Hello" each day is not spam).
            if (away > TimeSpan.FromMinutes(2)) {
                friend.Heard.Clear();
            }

            var repeat = friend.Heard.Count(h => string.Equals(h, text, StringComparison.OrdinalIgnoreCase));
            friend.Heard.Add(text ?? "");
            if (friend.Heard.Count > 8) {
                friend.Heard.RemoveAt(0);
            }

            if (now < friend.MutedUntil) {
                Save(self.Bot);
                return null;
            }

            // Spam: more than 5 lines in 20 seconds, or the same line a third time: muted for 10 minutes, no answer.
            if (friend.Lines.Count(l => now - l < TimeSpan.FromSeconds(20)) > 5 || repeat >= 2) {
                friend.MutedUntil = now.AddMinutes(10);
                Save(self.Bot);
                return new Reply("", "spam", $"{friend.Name} sent the same thing again and again, so I mute them for 10 minutes and say nothing.");
            }

            var intent = Classify(text, friend);
            if (intent is null) {
                return null;
            }

            // Too soon after the last answer to them (one answer per message, a moment between): a question still gets one.
            var question = intent is QUEST or WHERE or LEVEL or SCHOOL or TIP or BOT or NAME;
            if (now - friend.RepliedAt < s_perPlayerGap && !question || !RoomToSpeak(self.Bot)) {
                Save(self.Bot);
                return null;
            }

            friend.Talks++;
            var reply = Compose(self, friend, intent, text, typed, whisper, known && away > TimeSpan.FromHours(1));
            if (reply is not null && reply.Text.Length > 0) {
                friend.RepliedAt = now;
                NoteSaid(self.Bot);
            }

            Save(self.Bot);

            return reply;
        }
    }

    /// <summary>A battle shared with this player (counted for the memory).</summary>
    internal static void FoughtWith(string bot, ulong charId, string name) {
        lock (s_lock) {
            var friends = FriendsOf(bot);
            if (!friends.TryGetValue(charId, out var friend)) {
                friends[charId] = friend = new Friend { Name = name ?? "" };
            }

            friend.BattlesTogether++;
            friend.Last = DateTime.UtcNow;
            Save(bot);
        }
    }

    private static Reply Compose(Self self, Friend friend, string intent, string text, bool typed, bool whisper, bool welcomeBack) {
        var who = string.IsNullOrWhiteSpace(friend.Name) ? "friend" : friend.Name.Split(' ')[0];
        var quest = string.IsNullOrWhiteSpace(self.Quest) ? null : self.Quest;
        var doing = quest is null ? $"exploring {self.Area}" : $"\"{quest}\"" + (string.IsNullOrWhiteSpace(self.Step) ? "" : $": {self.Step}");
        var moodWord = self.Mood switch {
            "proud" => Pick(self.Bot, "mood-proud", "Feeling great, the story is going well!", "Pretty proud of my progress today!", "On a roll lately!"),
            "frustrated" => Pick(self.Bot, "mood-frust", "A bit stuck lately, but I'll get there!", "Some tricky spots today, but I'm not giving up.", "Had a rough patch, still smiling though."),
            _ => Pick(self.Bot, "mood-steady", "Doing well!", "Pretty good, thanks!", "All good here!"),
        };

        // In a battle: short, and he says why.
        // Asked to help or join while he is fighting, nearby: yes, jump in (a battle is open to anyone who walks in).
        if (self.InBattle && !whisper && intent is HELP or GROUP) {
            return new Reply(Pick(self.Bot, "battle-join", "Sure, jump in!", "Yes, come help!", "Join in, the more the merrier!"), intent,
                $"{friend.Name} asked to help while I am in a battle, so I welcome them in.", "yes");
        }

        if (self.InBattle && intent is not (QUEST or BOT or RUDE)) {
            return intent is BYE or THANKS
                ? new Reply(Pick(self.Bot, "battle-short", "brb, fighting!", "One sec, mid-battle!", "In a fight, back soon!"), intent,
                    $"{friend.Name} said something while I am in a battle, so I keep it short.", intent == BYE ? "bye" : "thanks")
                : new Reply(Pick(self.Bot, "battle-short", "brb, fighting!", "One sec, mid-battle!", "In a fight, back soon!"), intent,
                    $"{friend.Name} talked to me mid-battle, so I say I'm fighting and answer after.");
        }

        string line;
        string why;
        string moment = null;
        switch (intent) {
            case RUDE:
                friend.MutedUntil = DateTime.UtcNow.AddMinutes(30);
                line = Pick(self.Bot, "rude", "Okay, I'll leave you be.", "Alright, take care.", "No worries, I'll get back to my quest.");
                why = $"{friend.Name} was rude, so I answer once, calmly, and then don't answer them for 30 minutes.";
                break;
            case BOT:
                line = Pick(self.Bot, "bot", $"I'm a bot! I'm {self.Name}, a server-played {self.School} wizard questing through the main story. Happy to chat though!",
                    $"Yep, I'm a bot, {self.Name}. I quest the main story on my own. Still glad you said hi!");
                why = $"{friend.Name} asked if I'm a bot. I never pretend to be human, so I say so plainly.";
                break;
            case PRIVATE:
                line = Pick(self.Bot, "private", "Sorry, I can't share that.", "That's not something I can talk about, sorry!", "I keep that private, sorry.");
                why = $"{friend.Name} asked for server or private information, which I never share.";
                break;
            case QUEST:
                line = quest is null
                    ? Pick(self.Bot, "quest-none", $"Between quests right now, about to pick up the next one in {self.Area}.", $"Looking for my next quest in {self.Area}.")
                    : Pick(self.Bot, "quest", $"I'm on {doing}.", $"Working on {doing}!", $"Right now: {doing}.");
                why = $"{friend.Name} asked what I'm doing, so I tell them my real quest" + (quest is null ? " (I have none this moment)." : $", {quest}.");
                break;
            case WHERE:
                line = Pick(self.Bot, "where", $"I'm in {self.Area}, {self.World}.", $"{self.Area} in {self.World}, on {doing}.", $"Over in {self.Area}!");
                why = $"{friend.Name} asked where I am, so I tell them my area and world.";
                break;
            case LEVEL:
                line = Pick(self.Bot, "level", $"Level {self.Level} {self.School}!", $"I'm level {self.Level}.", $"Level {self.Level} so far, climbing!");
                why = $"{friend.Name} asked my level: {self.Level}.";
                break;
            case SCHOOL:
                line = Pick(self.Bot, "school", $"{self.School}! Balance of all things.", $"I'm a {self.School} wizard.", $"{self.School}, the best of every school, if you ask me.");
                why = $"{friend.Name} asked my school.";
                break;
            case NAME:
                line = Pick(self.Bot, "name", $"I'm {self.Name}!", $"{self.Name}, nice to meet you!", $"Call me {self.Name}.");
                why = $"{friend.Name} asked my name.";
                break;
            case TIP: {
                var tip = TipFor(self, text);
                line = tip ?? Pick(self.Bot, "tip-none", "Not sure about that one, sorry!", "I haven't been there yet, so I don't know.", "No idea yet, I'm still learning!");
                why = tip is null ? $"{friend.Name} asked for a tip I have no data for, so I say I don't know rather than guess."
                    : $"{friend.Name} asked for a tip, so I answer from what I know: {tip}";
                break;
            }
            case HELP:
                line = Pick(self.Bot, "help", "I quest solo, but I can share tips! Ask me where an NPC is or how a creature fights.",
                    "I can't group up, but ask me where someone is and I'll tell you what I know!", "Solo questing for me, but happy to give tips!");
                why = $"{friend.Name} asked for help. I quest alone, but I offer what I can: tips from my books.";
                break;
            case GROUP:
                line = Pick(self.Bot, "group", "Thanks, but I quest solo! Good luck though!", "I'm a solo quester, sorry! Have fun!", "Kind of you, but I go it alone.");
                why = $"{friend.Name} asked me to group or follow. I quest solo, so I say no kindly.";
                moment = "no";
                break;
            case TRADE:
                line = Pick(self.Bot, "trade", "Sorry, I don't give out gold or items.", "No trades or gifts from me, sorry!", "I keep my gold for my own gear, sorry!");
                why = $"{friend.Name} asked for gold, items or a trade. I never hand those out.";
                moment = "no";
                break;
            case TELEPORT:
                line = Pick(self.Bot, "teleport", "Thanks, but I'll get there on my own!", "No teleports for me, I'm on my quest path.", "Sorry, I stick to my own route.");
                why = $"{friend.Name} asked about teleporting. My quest comes first, so I say no.";
                moment = "no";
                break;
            case THANKS:
                line = Pick(self.Bot, "thanks", "You're welcome!", "Anytime!", "No problem!");
                why = $"{friend.Name} thanked me.";
                moment = "thanks";
                break;
            case BYE:
                line = Pick(self.Bot, "bye", $"Bye {who}! Good luck out there!", "See you around!", $"Take care, {who}!");
                why = $"{friend.Name} said goodbye.";
                moment = "bye";
                break;
            case COMPLIMENT:
                line = Pick(self.Bot, "compliment", "Thanks, that means a lot!", "Aw, thank you!", "Thanks! You too!");
                why = $"{friend.Name} was kind, so I thank them.";
                break;
            case JOKE:
                line = Pick(self.Bot, "joke", "Haha!", "Ha, good one!", "Why did the Balance wizard cross the road? To even things out!",
                    "I'd tell a Krokotopia joke, but it's a bit dry.");
                why = $"{friend.Name} laughed or joked, so I laugh along.";
                break;
            case HOW_ARE_YOU:
                line = moodWord + (quest is null ? "" : $" Working on {doing}.");
                why = $"{friend.Name} asked how I am, so I answer with my real mood ({self.Mood}).";
                break;
            case ANSWER:
                line = Answered(self, friend, text);
                why = $"{friend.Name} answered the question I asked, so I remember it and reply.";
                break;
            case GREETING:
                line = welcomeBack
                    ? Pick(self.Bot, "welcome-back", $"Welcome back, {who}!" + (friend.World.Length > 0 ? $" Still in {friend.World}?" : ""),
                        $"Hey {who}, good to see you again!" + (friend.BattlesTogether > 0 ? $" We've fought {friend.BattlesTogether} battles together now." : ""),
                        $"{who}! Back again! We've talked {friend.Talks} times now.")
                    : Pick(self.Bot, "hello", $"Hi {who}!", "Hello there!", $"Hey {who}! I'm {self.Name}.", "Greetings, fellow wizard!");
                why = welcomeBack ? $"{friend.Name} is someone I've met before (first {friend.First.ToLocalTime():d MMMM}), so I welcome them back."
                    : $"{friend.Name} said hello, so I greet them.";
                moment = welcomeBack ? null : "hello";
                break;
            default:
                // Small talk he has no question in: typed lines get a friendly word now and then; Menu Chat ones a nod.
                if (s_random.NextDouble() > (whisper ? 0.9 : 0.4)) {
                    return null;
                }

                line = Pick(self.Bot, "chat", "Ha, true!", "Interesting!", "Oh nice!", "I hear you!", "Fair enough!");
                why = $"{friend.Name} chatted, so I answer in a friendly word.";
                break;
        }

        // Sometimes a short question back (never twice in a row, never when they're leaving or rude).
        if (intent is GREETING or HOW_ARE_YOU or QUEST or COMPLIMENT && s_random.NextDouble() < 0.35 && friend.Pending.Length == 0) {
            if (friend.School.Length == 0) {
                line += " " + Pick(self.Bot, "ask-school", "What school are you?", "What school are you, by the way?");
                friend.Pending = SCHOOL;
                friend.PendingAt = DateTime.UtcNow;
                why += " I ask their school back, to know them better.";
                moment = null;
            }
            else if (friend.World.Length == 0) {
                line += " " + Pick(self.Bot, "ask-world", "Where are you questing these days?", "What world are you in?");
                friend.Pending = WHERE;
                friend.PendingAt = DateTime.UtcNow;
                why += " I ask where they quest, to know them better.";
                moment = null;
            }
        }

        // A typed line gets a typed answer; Menu Chat gets a Menu Chat moment when one fits the answer.
        return new Reply(line, intent, why, typed ? null : moment);
    }

    private static string Answered(Self self, Friend friend, string text) {
        var pending = friend.Pending;
        friend.Pending = "";
        if (pending == SCHOOL && s_schools.FirstOrDefault(s => Has(text, s)) is { } school) {
            friend.School = char.ToUpperInvariant(school[0]) + school[1..];
            return string.Equals(friend.School, self.School, StringComparison.OrdinalIgnoreCase)
                ? Pick(self.Bot, "same-school", $"A fellow {friend.School} wizard! Nice!", $"{friend.School} too! Great choice.")
                : Pick(self.Bot, "school-answer", $"{friend.School}, nice! I'd love your spells some days.", $"Ooh, {friend.School}! Good pick.");
        }

        friend.World = (text ?? "").Trim().TrimEnd('.', '!');
        if (friend.World.Length > 40) {
            friend.World = friend.World[..40];
        }

        return Pick(self.Bot, "world-answer", $"{friend.World}, nice! Good luck there!", $"Oh, {friend.World}! Have fun!");
    }

    // A tip from the bot's books: where an NPC stands, or how a creature fights.
    private static string TipFor(Self self, string text) {
        var t = (text ?? "").Trim().TrimEnd('?', '!', '.');
        foreach (var lead in new[] { "how do i beat", "how to beat", "how do you beat", "tips for", "tip for", "any tips for", "where is", "where's", "wheres", "how do i get to" }) {
            var at = t.IndexOf(lead, StringComparison.OrdinalIgnoreCase);
            if (at < 0) {
                continue;
            }

            var subject = t[(at + lead.Length)..].Trim();
            if (subject.Length < 3) {
                return null;
            }

            return lead.StartsWith("where", StringComparison.Ordinal) || lead.StartsWith("how do i get", StringComparison.Ordinal)
                ? self.FindNpc?.Invoke(subject)
                : self.FoeTip?.Invoke(subject);
        }

        return null;
    }

    // ---------------------------------------------------------------------
    // Wording, limits and memory
    // ---------------------------------------------------------------------

    /// <summary>One of the lines, never the same one the bot said last for this kind of answer.</summary>
    private static string Pick(string bot, string kind, params string[] lines) {
        var key = bot + "|" + kind;
        var last = s_lastLine.GetValueOrDefault(key);
        var choices = lines.Length > 1 ? lines.Where(l => l != last).ToArray() : lines;
        var line = choices[s_random.Next(choices.Length)];
        s_lastLine[key] = line;

        return line;
    }

    private static bool RoomToSpeak(string bot) {
        if (!s_said.TryGetValue(bot, out var said)) {
            s_said[bot] = said = new Queue<DateTime>();
        }

        while (said.Count > 0 && DateTime.UtcNow - said.Peek() > TimeSpan.FromMinutes(1)) {
            said.Dequeue();
        }

        return said.Count < MOST_PER_MINUTE;
    }

    private static void NoteSaid(string bot) {
        if (!s_said.TryGetValue(bot, out var said)) {
            s_said[bot] = said = new Queue<DateTime>();
        }

        said.Enqueue(DateTime.UtcNow);
    }

    /// <summary>What the bot remembers of a player (null when they never talked).</summary>
    internal static Friend Of(string bot, ulong charId) {
        lock (s_lock) {
            return FriendsOf(bot).GetValueOrDefault(charId);
        }
    }

    private static string FileOf(string bot) => Path.Combine(Path.GetFullPath(FOLDER), $"social-{bot.ToLowerInvariant()}.json");

    private static Dictionary<ulong, Friend> FriendsOf(string bot) {
        if (s_friends.TryGetValue(bot, out var friends)) {
            return friends;
        }

        friends = [];
        try {
            if (File.Exists(FileOf(bot)) && JsonSerializer.Deserialize<Dictionary<ulong, Friend>>(File.ReadAllText(FileOf(bot))) is { } read) {
                friends = read;
            }
        }
        catch (Exception ex) {
            Logger.Debug("Bots: the social memory of {0} could not be read: {1}", Logger.Args(bot, ex.Message));
        }

        s_friends[bot] = friends;

        return friends;
    }

    private static void Save(string bot) {
        try {
            Directory.CreateDirectory(Path.GetFullPath(FOLDER));
            File.WriteAllText(FileOf(bot), JsonSerializer.Serialize(FriendsOf(bot), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) {
            Logger.Debug("Bots: the social memory of {0} could not be written: {1}", Logger.Args(bot, ex.Message));
        }
    }

}
