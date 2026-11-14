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
        bool InBattle, string Mood, Func<string, string> FindNpc = null, Func<string, string> FoeTip = null, string Doing = null, int Health = -1,
        Func<string, string> Act = null, IReadOnlyList<string> Worlds = null, Func<string, bool, string> Opinion = null);

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
        // What the talk was last about, for follow-ups ("no, the step of the quest").
        public string Topic { get; set; } = "";
        public DateTime TopicAt { get; set; }
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
        RUDE = "rude", ANSWER = "answer to his question", CHAT = "small talk", NAME = "his name", STEP = "his quest step",
        PERMISSION = "may I", MAKER = "who made him", ABILITY = "can he do something", LIKE = "does he like something", ORDER = "asks him to do something", CALLED = "said his name", STATUS = "how he is doing right now", ACK = "ok or brb", RULES = "a rule for him", ACTIVITY = "what he is doing", AGAIN = "say that again", QUESTION = "a question he can't place",
        RECALL = "what he remembers", FACT = "a fact about himself", JOKE_ASK = "asks for a joke";

    // His own jokes, about life as a wizard (never two the same in a row: Pick, and a shuffled turn through them all).
    private static readonly string[] s_jokes = [
        "Why did the Balance wizard cross the road? To even things out!",
        "I tried a Krokotopia joke once. It was a bit dry.",
        "Why don't skeletons ever win at cards? They have no guts to bet with!",
        "My deck and I have a lot in common. We both get shuffled around and hope for the best.",
        "What do you call a Fire wizard who tells bad jokes? A real burner.",
        "I asked a Storm wizard to hold my pips. Now they only work 70% of the time.",
        "Why was the Ice wizard so calm? Nothing ever gets under their shields.",
        "What's a Death wizard's favorite drink? Anything, as long as they drain it.",
        "I'm not lost. I'm just taking the scenic route to my quest arrow.",
        "Why did the wizard bring a ladder to the Commons? To reach the next level!",
        "Life wizards are the best friends. They always heal you, even when you're being a pain.",
        "What did the Myth wizard say to their minion? \"You're grounded!\"",
        "I don't always fizzle. But when I do, it's in front of everyone.",
        "How do you know a Balance wizard is around? Everything's suddenly fair.",
        "Why don't mummies take vacations? They're afraid they'll unwind!",
    ];

    // Things he never does (hard limits): a player's word about them gets the truth.
    private static readonly string[] s_neverDoes = ["hatch", "hatching", "train pets", "pet training", "training pets", "train your pet", "craft", "crafting",
        "fish", "fishing", "garden", "gardening", "farm", "farming", "duel", "dueling", "pvp"];

    private static readonly string[] s_questionStarts = ["what", "why", "how", "who", "when", "where", "which", "do you", "are you", "can you", "will you",
        "did you", "have you", "is it", "is that", "would you", "could you", "should"];

    private static readonly string[] s_schools = ["fire", "ice", "storm", "myth", "life", "death", "balance"];

    // World names a "where are you questing" answer must hold (audit C7: "follow me" was kept as the player's world).
    private static readonly string[] s_worlds = ["wizard city", "krokotopia", "kroktopia", "krok", "marleybone", "mooshu", "dragonspyre", "celestia", "zafaria",
        "avalon", "azteca", "aquila", "khrysalis", "polaris", "mirage", "empyrea", "karamelle", "lemuria", "novus", "wysteria", "grizzleheim", "wintertusk",
        "darkmoor", "selenopolis", "ahmarra", "wc", "kt", "mb", "ds"];

    // Rude only when aimed at him (audit C5: "the trash mobs here hit hard" and "this quest is dumb lol" muted players).
    private static readonly string[] s_rude = ["shut up", "stfu", "hate you", "you suck", "kys", "you're stupid", "youre stupid", "you are stupid", "ur stupid",
        "you idiot", "you're an idiot", "youre an idiot", "you noob", "ur a noob", "you're trash", "youre trash", "ur trash", "you're dumb", "youre dumb",
        "you're ugly", "youre ugly", "you loser", "you moron", "stupid bot", "dumb bot", "idiot bot"];

    private static bool Has(string text, params string[] words) => words.Any(w => MenuChatBook.Says(text, w));

    /// <summary>What a line means (the first intent that fits, most specific first).</summary>
    // "Are you in combat?", "are you walking?", "are you questing?" (Niduus, 2026-11-14: "ARe you in combat?" got "Not sure what
    // you mean"; he needs to know his own state): the state asked about, or null.
    private static readonly (string State, string[] Words)[] s_states = [
        ("battle", ["in combat", "in a battle", "in battle", "in a fight", "fighting", "battling", "in a duel"]),
        ("walking", ["walking", "moving", "running", "going somewhere", "on your way"]),
        ("questing", ["questing", "on a quest", "doing a quest", "doing quests", "doing your quest"]),
        ("waiting", ["waiting", "standing", "idle", "doing nothing", "frozen", "afk", "away"]),
        ("stuck", ["stuck", "lost"]),
    ];

    private static string StateWord(string text) {
        var t = (text ?? "").ToLowerInvariant();
        if (!(t.Contains("are you") || t.Contains("r u ") || t.StartsWith("you ", StringComparison.Ordinal) || t.Contains("u in "))) {
            return null;
        }

        return s_states.FirstOrDefault(s => s.Words.Any(w => Has(t, w))).State;
    }

    // A yes or no from his real state now. Doing is what he is doing this moment ("walking to ...", "stuck: ...").
    private static string StateAnswer(Self self, string text, string doing) {
        var now = string.IsNullOrWhiteSpace(self.Doing) ? "" : self.Doing.Trim().TrimEnd('.');
        var low = now.ToLowerInvariant();
        var stuck = low.Contains("stuck") || low.Contains("can't") || low.Contains("looking around");
        var waiting = low.Contains("wait") || low.Contains("paused") || low.Contains("reading") || low.Contains("standing");
        var health = self.Health >= 0 ? $" I'm at {self.Health}% health." : "";
        return StateWord(text) switch {
            "battle" => self.InBattle ? $"Yes, I'm in a battle right now!{health}"
                : $"No, I'm not in a battle right now.{health}" + (now.Length > 0 ? $" I'm {Gerund(now)}." : $" Working on {doing}."),
            "walking" => self.InBattle ? "No, I'm in a battle right now!"
                : stuck || waiting ? $"Not right now: {now}."
                : now.Length > 0 ? $"Yes! I'm {Gerund(now)}." : $"Yes, working on {doing}.",
            "questing" => self.Quest is null ? $"Not right now, I'm {(now.Length > 0 ? Gerund(now) : "looking for my next quest")}."
                : $"Yes! I'm on {doing}" + (self.Step is null ? "." : $", the step is: {self.Step}."),
            "waiting" => self.InBattle ? "No, I'm in a battle!" : waiting || stuck ? $"Yes: {now}." : now.Length > 0 ? $"No, I'm {Gerund(now)}." : $"No, I'm working on {doing}.",
            "stuck" => stuck ? $"Yes, I'm stuck: {now}. I keep trying the way a player would." : $"No, I'm fine! {(now.Length > 0 ? "I'm " + Gerund(now) + "." : "Working on " + doing + ".")}",
            _ => null,
        };
    }

    // "walks to the door" reads "walking to the door"; anything else as it is.
    private static string Gerund(string doing) {
        var words = doing.Split(' ', 2);
        if (words[0].Length > 2 && words[0].EndsWith('s') && !words[0].EndsWith("ss", StringComparison.Ordinal)) {
            var verb = words[0][..^1];
            verb = verb.EndsWith('e') && !verb.EndsWith("ee", StringComparison.Ordinal) ? verb[..^1] : verb;
            return verb + "ing" + (words.Length > 1 ? " " + words[1] : "");
        }

        return doing;
    }

    // ---- Sensitive details: never said, whatever was asked (Niduus, 2026-11-14) ----

    private const string CANT_SHARE = "Sorry, I can't share that.";
    private static readonly System.Text.RegularExpressions.Regex s_sensitive = new(
        @"\b\d{1,3}(\.\d{1,3}){3}\b"                         // an IP address
        + @"|\blocalhost\b|\b127\.0\.0\.1\b"
        + @"|:\d{4,5}\b"                                     // a port
        + @"|\b[a-z]:\\|\\users\\|/home/|/users/|appdata|\.ini\b|\.json\b|\.db\b"  // files and folders
        + @"|\bpass(word|wd|code)?\s*[:=]|\buser(name)?\s*[:=]|\blogin\s*[:=]"
        + @"|\b(api|secret)[ _-]?key\b|\btoken\s*[:=]"
        + @"|[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}",            // an email address
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// A line he is about to say with any sensitive detail in it becomes a plain "can't share that": the server's address or
    /// ports, file paths, account names or passwords, keys, email addresses, and the names of the computer and its user (the
    /// owner's real name is never written into the code; it is read from the machine here). Not a setting: always on.
    /// Returns the same string when it is safe.
    /// </summary>
    internal static string Scrub(string line) {
        if (string.IsNullOrEmpty(line)) {
            return line;
        }

        if (s_sensitive.IsMatch(line)) {
            return CANT_SHARE;
        }

        foreach (var secret in MachineSecrets()) {
            if (System.Text.RegularExpressions.Regex.IsMatch(line, $@"\b{System.Text.RegularExpressions.Regex.Escape(secret)}\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)) {
                return CANT_SHARE;
            }
        }

        return line;
    }

    private static string[] s_machineSecrets;

    private static string[] MachineSecrets() {
        if (s_machineSecrets is not null) {
            return s_machineSecrets;
        }

        var found = new List<string>();
        try {
            foreach (var value in new[] { Environment.UserName, Environment.MachineName, Environment.UserDomainName }) {
                if (!string.IsNullOrWhiteSpace(value) && value.Length >= 3) {
                    found.Add(value.Trim());
                }
            }
        }
        catch (Exception) {
            // Nothing more to hold back.
        }

        return s_machineSecrets = [.. found.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    internal static string Classify(string text, Friend friend) {
        var t = (text ?? "").Trim();
        if (t.Length == 0) {
            return null;
        }

        if (Has(t, s_rude)) {
            return RUDE;
        }

        if (Has(t, "are you a bot", "are you real", "are you human", "you a bot", "is this a bot", "bot?", "are you ai", "real person", "a robot", "are you an npc", "are you npc",
                "is ultimus a bot", "ultimus is a bot")) {
            return BOT;
        }

        // Never shared, whatever the words (Niduus, 2026-11-14: "Permanently block out the sensitive details"): the server's
        // address, any account's name or password, keys, files, real names, other players' details. Scrub() also holds every
        // answer to this, whatever was asked.
        if (Has(t, "password", "passwords", "pass word", "passcode", "user and pass", "username", "user name", "login", "log in info", "login info",
                "credentials", "account name", "account info", "your account", "ip", "server ip", "ip address", "port", "server address", "host name",
                "hostname", "admin", "address", "where do you live", "phone", "email", "who owns", "who runs", "real name", "secret key", "api key",
                "token", "file path", "folder", "where is the server", "server files", "database", "home address", "credit card", "personal info")) {
            return PRIVATE;
        }

        // "Who made you?": Niduus (2026-11-14: "The honest answer is again, me, Niduus").
        if (Has(t, "who made you", "who created you", "who built you", "who coded you", "who programmed you", "your creator", "your maker", "your owner",
                "who owns you", "who's your owner", "whos your owner")) {
            return MAKER;
        }

                // An answer to the question he asked (a school or a world), within two minutes.
        if (friend is { Pending.Length: > 0 } && DateTime.UtcNow - friend.PendingAt < TimeSpan.FromMinutes(2)
            && (friend.Pending == SCHOOL && t.Split(' ').Length <= 4 && s_schools.Any(s => Has(t, s))
                || friend.Pending == WHERE && !t.Contains('?') && s_worlds.Any(w => Has(t, w)))) {
            return ANSWER;
        }

        var recent = friend is { Topic.Length: > 0 } && DateTime.UtcNow - friend.TopicAt < TimeSpan.FromMinutes(3);

        // "ok", "brb", "k", "?": nothing to answer (audit C4: fillers there were noise).
        var bare = t.Trim('?', '!', '.', ' ').ToLowerInvariant();
        if (bare is "" or "ok" or "okay" or "k" or "kk" or "yes" or "yeah" or "yep" or "no" or "nope" or "brb" or "afk" or "hm" or "hmm" or "oh" or "ah" or "mhm" or "sure") {
            return ACK;
        }

        // "like right now", "hurry": a push on what they just asked him to do.
        if (recent && friend.Topic == ORDER && Has(t, "now", "right now", "hurry", "asap", "quick", "go", "please", "pls", "plz")) {
            return AGAIN;
        }

        // Asked to do something: done when his rules allow, else he says why not (2026-11-14: "go get potions" got "Got it!"
        // and nothing happened).
        if (Has(t, "get potions", "go get potions", "buy potions", "get some potions", "refill", "fill your potions", "go heal", "heal up", "get healed",
                "go back to your quest", "back to your quest", "keep questing", "go quest", "do your quest", "stop", "wait here", "stay here", "stay there",
                "wait for me", "go away", "leave", "go do", "can you go", "could you go", "you should go")
            || t.StartsWith("go ", StringComparison.OrdinalIgnoreCase)) {
            // A rule about what he never does reads as a rule, not an order ("stop hatching").
            if (!s_neverDoes.Any(w => Has(t, w))) {
                return ORDER;
            }
        }

                // How he is right now: health, waiting, standing, lost, what he is fighting, why.
        if (Has(t, "are you hurt", "are you ok", "are you okay", "your health", "need a heal", "need heal", "low health", "why are you standing",
                "why are you waiting", "what are you waiting", "are you lost", "are you stuck", "why are you just", "what are you fighting", "why are you fighting",
                "why are you here", "why are you going", "standing still", "you stuck", "you lost")
            || StateWord(t) is not null) {
            return STATUS;
        }

        // The step of his quest (also "no, the step of the quest" after he named only the quest).
        if (Has(t, "what step", "which step", "the step", "your step", "step?", "objective", "what do you need to do", "what do you have to do",
                "what are you supposed to do", "what's next", "whats next", "what is next", "next step", "which part", "what part", "how far along", "how far are you", "your progress")
            || recent && friend.Topic is QUEST or STEP && Has(t, "step", "exactly", "specifically", "doing what", "for what")) {
            return STEP;
        }

        // "What?", "huh", "what do you mean": the last answer again, clearer.
        if (recent && (Has(t, "what do you mean", "what does that mean", "huh", "i don't get it", "i dont get it", "come again", "say again")
                       || t.Trim('?', '!', '.', ' ').Equals("what", StringComparison.OrdinalIgnoreCase))) {
            return AGAIN;
        }

        // "What's your (least) favorite school?", "Do you like Krokotopia?": his opinion. Before the plain "school" or "quest"
        // questions, which hold the same words.
        // "Do you remember...?", "what happened today?": his memory journal (Niduus, 2026-11-14: "allow him to have memories").
        if (Has(t, "do you remember", "remember when", "you remember", "what do you remember", "your memories", "a memory", "your best moment",
                "your worst moment", "what happened today", "what did you do today", "what have you done", "your day", "how was your day",
                "how's your day", "hows your day", "tell me about your adventures", "your adventures", "your story so far")) {
            return RECALL;
        }

        if (Has(t, "do you like", "do you love", "do you enjoy", "do u like", "do you hate", "favorite", "favourite", "fav", "what do you think of",
                "least favorite", "worst", "you hate", "opinion on", "opinion of", "your opinion", "thoughts on", "your thoughts", "what did you think of",
                "how did you like", "how do you feel about", "how'd you like", "hows krokotopia", "how's krokotopia", "how is wizard city", "how was wizard city")) {
            return LIKE;
        }

        // About himself, from his real data (Niduus, 2026-11-14: "Make him able to give more responses to stuff").
        if (Has(t, "what are you wearing", "your gear", "your robe", "your hat", "your boots", "your wand", "your outfit", "your stats", "your deck",
                "what spells", "which spells", "your spells", "best spell", "how many quests", "how many battles", "how many fights", "how long have you",
                "how long you been", "how long have u", "how much xp", "your xp", "next world", "which world next", "your pet", "your mount",
                "how much health", "your health", "how many potions", "your potions", "your mana", "how strong", "your damage", "your resist")) {
            return FACT;
        }

        if (Has(t, "what quest", "which quest", "quest are you", "working on", "your quest", "what are you doing", "what you doing", "whatcha doing",
                "what r u doing", "wyd", "what are you up to", "up to", "doing what", "on what quest", "quest?")) {
            return QUEST;
        }

        // "You're not allowed to hatch!": a rule said to him.
        if (Has(t, "not allowed", "you can't", "you cant", "you cannot", "you shouldn't", "you shouldnt", "you must not", "don't hatch", "dont hatch",
                "no hatching", "stop hatching", "stop fishing", "stop training", "you're not supposed", "youre not supposed")) {
            return RULES;
        }

        // "Are you fishing?" "Are you busy?": what he is doing, truthfully.
        if (Has(t, "are you", "r u", "you") && Has(t, "busy", "afk", "hatching", "fishing", "training", "farming", "crafting", "gardening", "dueling", "shopping")) {
            return ACTIVITY;
        }

        if (Has(t, "where is", "where's", "wheres", "how do i beat", "how to beat", "how do you beat", "tips for", "tip for", "any tips", "how do i get to")) {
            return TIP;
        }

        if (Has(t, "where are you", "where r u", "what world", "which world", "where you at", "where are you going", "where to", "location", "what area",
                "which area", "what zone")) {
            return WHERE;
        }

        if (Has(t, "what level", "your level", "how high", "lvl", "level?", "what lvl")) {
            return LEVEL;
        }

        if (Has(t, "what school", "your school", "which school", "school?")) {
            return SCHOOL;
        }

        if (Has(t, "your name", "who are you", "whats your name", "what's your name")) {
            return NAME;
        }

        // Asked along, to follow, to group or to a dungeon: a kind no (he quests the story solo).
        if (Has(t, "group", "party", "join me", "join us", "can i join", "team up", "follow me", "come with", "come here", "follow", "duo", "carry me",
                "dungeon", "wanna do", "want to do", "wanna go", "want to go", "let's do", "lets do", "let's go to", "quest together", "quest with me",
                "play with me", "duel", "pvp", "raid", "farm with", "hatch with", "wanna hatch", "want to hatch", "fish with me", "garden with me")) {
            return GROUP;
        }

        if (Has(t, "gold", "trade", "give me", "gift", "items", "money", "crowns", "free stuff", "can i have")) {
            return TRADE;
        }

        if (Has(t, "teleport", "port to", "tp to", "port me", "tp me")) {
            return TELEPORT;
        }

                // "Can you fish?": an honest yes or no about what he can do (2026-11-14: "honest answer would be no").
        if (Has(t, "can you", "do you", "are you able to", "can u", "do u", "could you", "will you", "would you", "you know how to")) {
            return ABILITY;
        }

                // "Can I fish next to you?": a polite yes.
        if (Has(t, "can i", "may i", "mind if", "is it ok if", "is it okay if", "do you mind", "can we", "could i", "ok if i", "okay if i")) {
            return PERMISSION;
        }
        if (Has(t, "thanks", "thank you", "thx", "ty")) {
            return THANKS;
        }

        if (Has(t, "help", "help me", "need help", "can you help", "stuck")) {
            return HELP;
        }

        if (Has(t, "bye", "goodbye", "see you", "see ya", "cya", "see you later", "later!", "gtg", "gotta go", "good night", "gn")) {
            return BYE;
        }

        if (Has(t, "how are you", "how r u", "how's it going", "hows it going", "how are things", "how you doing", "sup", "what's up", "whats up")) {
            return HOW_ARE_YOU;
        }

        // Kind words to him only (audit C6: "nice weather today" and "that's not cool" got "Aw, thank you!").
        if (Has(t, "great job", "good job", "gj", "gg", "love your", "you're good", "youre good", "well done", "nice job", "nice one", "you're cool",
                "youre cool", "you're awesome", "youre awesome", "you're amazing", "nice robe", "nice outfit", "nice gear", "nice hat", "cool robe", "cool outfit")) {
            return COMPLIMENT;
        }

        // "Tell me a joke": a request for one, not a laugh (Niduus, 2026-11-14: it got "Haha!").
        if (Has(t, "tell me a joke", "tell a joke", "know any jokes", "know a joke", "got any jokes", "got a joke", "say something funny",
                "make me laugh", "another joke", "one more joke", "joke please", "a joke?", "tell me something funny")) {
            return JOKE_ASK;
        }

        if (Has(t, "lol", "haha", "lmao", "joke", "funny", "xd", "rofl")) {
            return JOKE;
        }

        if (Has(t, "hello", "hi", "hey", "greetings", "yo", "hiya", "howdy", "good morning", "good evening")) {
            return GREETING;
        }

        // A question he can't place gets an honest "not sure" (never "Ha, true!").
        if (t.EndsWith('?') || s_questionStarts.Any(q => t.StartsWith(q + " ", StringComparison.OrdinalIgnoreCase))) {
            return QUESTION;
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

            // Spam: more than 5 lines in 20 seconds, or the same line again and again: muted for 10 minutes, no answer. A question
            // asked again is not spam: his answer missed, so he answers more fully (audit C8).
            if (friend.Lines.Count(l => now - l < TimeSpan.FromSeconds(20)) > 5 || repeat >= 4) {
                friend.MutedUntil = now.AddMinutes(10);
                Save(self.Bot);
                return new Reply("", "spam", $"{friend.Name} sent the same thing again and again, so I mute them for 10 minutes and say nothing.");
            }

            // His name alone ("ULTIMUS??") or with a hello ("hey Ultimus"): read without it (2026-11-14, "Not sure what you mean").
            var heard = WithoutName(text, self);
            var intent = heard.Length == 0 && text.Trim().Length > 0 ? CALLED : Classify(heard, friend);
            if (intent is null or ACK) {
                Save(self.Bot);
                return null;
            }

            if (repeat > 0 && intent is QUEST) {
                intent = STEP;
            }

            // Too soon after the last answer to them (one answer per message, a moment between): a question still gets one.
            var question = intent is CALLED or ORDER or QUEST or STEP or AGAIN or WHERE or LEVEL or SCHOOL or TIP or BOT or NAME or QUESTION or ACTIVITY or JOKE_ASK or RECALL or FACT or LIKE or STATUS or MAKER or ABILITY or PRIVATE;
            if (!NoSave && (now - friend.RepliedAt < s_perPlayerGap && !question || !RoomToSpeak(self.Bot))) {
                Save(self.Bot);
                return null;
            }

            friend.Talks++;
            var reply = Compose(self, friend, intent, heard.Length > 0 ? heard : text, typed, whisper, known && away > TimeSpan.FromHours(1));
            if (reply is { Text.Length: > 0 } && Scrub(reply.Text) is var safe && !ReferenceEquals(safe, reply.Text)) {
                reply = reply with { Text = safe, Why = reply.Why + " (A sensitive detail was in it, so I said I can't share that instead.)" };
            }
            if (reply is not null && reply.Text.Length > 0) {
                friend.RepliedAt = now;
                NoteSaid(self.Bot);
            }

            Save(self.Bot);

            return reply;
        }
    }

    /// <summary>
    /// His favorites: the same answer every time (a personality, 2026-11-14), true to a Balance wizard and to the worlds he
    /// has really been to.
    /// </summary>
    private static string Favorite(Self self, string text) {
        // "least favorite", "worst", "hate": the other way round (2026-11-14: "least favorite school" got "I'm a Balance wizard").
        var least = Has(text, "least", "worst", "hate", "dislike", "don't like", "dont like", "most annoying", "hardest", "scariest");
        var topic = Has(text, "world") ? "world" : Has(text, "school") ? "school" : Has(text, "spell", "card") ? "spell" : Has(text, "quest") ? "quest"
            : Has(text, "boss", "creature", "monster", "enemy", "mob", "mobs", "foe") ? "creature" : Has(text, "area", "zone", "place", "spot") ? "area"
            : Has(text, "player", "friend", "wizard", "person") ? "player" : "";
        // From what really happened to him (UltimusDirector.Opinions.cs), when he has an opinion yet.
        if (topic.Length > 0 && self.Opinion?.Invoke(topic, least) is { Length: > 0 } felt) {
            return felt;
        }

        // Nothing made up (Niduus, 2026-11-14: "Don't program that in"): no experience yet, he says so.
        var what = topic.Length > 0 ? topic : "thing";
        return least
            ? $"I haven't seen enough yet to have a least favorite {what}. Ask me again later!"
            : $"I haven't seen enough yet to pick a favorite {what}. Ask me again later!";
    }

    // "fishing" -> "fish", for "No, I don't fish."
    private static string Verb(string word) => word switch {
        "fishing" => "fish", "hatching" => "hatch pets", "hatch" => "hatch pets", "crafting" => "craft", "gardening" => "garden", "garden" => "garden",
        "farming" => "farm", "dueling" => "duel players", "duel" => "duel players", "pvp" => "do PvP", "train pets" or "pet training" or "training pets"
            or "train your pet" => "train pets",
        _ => word,
    };

    private static string WithoutName(string text, Self self) {
        var t = text ?? "";
        foreach (var name in new[] { self.Name, self.Name?.Split(' ')[0], self.Bot }.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct()) {
            var at = t.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            while (at >= 0) {
                t = t.Remove(at, name.Length);
                at = t.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            }
        }

        var trimmed = t.Trim(' ', ',', '.', '!', '?', ':', '-');

        return trimmed.Length == 0 ? "" : t.Trim(' ', ',', ':', '-');
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
        var area = string.IsNullOrWhiteSpace(self.Area) ? "this area" : self.Area;
        var at = area + (string.IsNullOrWhiteSpace(self.World) || string.Equals(self.World, self.Area, StringComparison.OrdinalIgnoreCase) ? "" : $", {self.World}");
        var step = string.IsNullOrWhiteSpace(self.Step) ? null : self.Step.Trim();
        // No quote marks round the quest: the game's chat hid the quoted words as "..." (Niduus, 2026-11-14).
        var doing = quest is null ? $"exploring {area}" : $"the quest {quest}";
        var stepLine = quest is null ? $"I'm between quests, looking for the next one in {area}."
            : step is null ? $"I'm on {quest} and heading to its next step now."
            : $"My step in {quest} is: {step}.";
        var moodWord = self.Mood switch {
            "proud" => Pick(self.Bot, "mood-proud", "Feeling great, the story is going well!", "Pretty proud of my progress today!", "On a roll lately!"),
            "frustrated" => Pick(self.Bot, "mood-frust", "A bit stuck lately, but I'll get there!", "Some tricky spots today, but I'm not giving up.", "Had a rough patch, still smiling though."),
            _ => Pick(self.Bot, "mood-steady", "Doing well!", "Pretty good, thanks!", "All good here!"),
        };

        // In a battle: short, and he says why.
        // Asked to help or join while he is fighting, nearby: yes, jump in (a battle is open to anyone who walks in).
        if (self.InBattle && !whisper && (intent is HELP || intent is GROUP && Has(text ?? "", "join", "help", "jump in", "can i", "come help"))) {
            return new Reply(Pick(self.Bot, "battle-join", "Sure, jump in!", "Yes, come help!", "Join in, the more the merrier!"), intent,
                $"{friend.Name} asked to help while I am in a battle, so I welcome them in.", "yes");
        }

        if (self.InBattle && intent is not (QUEST or STEP or AGAIN or BOT or RUDE or ACTIVITY)) {
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
                    ? Pick(self.Bot, "quest-none", $"Between quests right now, about to pick up the next one in {area}.", $"Looking for my next quest in {area}.")
                    : Pick(self.Bot, "quest", $"I'm on {doing}" + (step is null ? "." : $". Next: {step}."), $"Working on {doing}" + (step is null ? "!" : $", the step is: {step}."));
                why = $"{friend.Name} asked what I'm doing, so I tell them my real quest" + (quest is null ? " (I have none this moment)." : $", {quest}.");
                break;
            case WHERE:
                line = Pick(self.Bot, "where", $"I'm in {at}.", $"In {at}, on {doing}.");
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
            case MAKER:
                line = Pick(self.Bot, "maker", $"Niduus made me! I'm {self.Name}, his solo questing bot.", "Niduus did! He built me to quest the main story like a player.");
                why = $"{friend.Name} asked who made me. The honest answer: Niduus.";
                break;
            case LIKE: {
                var subject = (text ?? "").Trim().TrimEnd('?', '!', '.');
                foreach (var lead in new[] { "what do you think of", "what did you think of", "your opinion on", "your opinion of", "opinion on", "opinion of",
                             "your thoughts on", "thoughts on", "how did you like", "how do you feel about", "how'd you like", "how is", "how was", "how's",
                             "do you like", "do you love", "do you enjoy", "do u like", "you like" }) {
                    var cut = subject.IndexOf(lead, StringComparison.OrdinalIgnoreCase);
                    if (cut >= 0) {
                        subject = subject[(cut + lead.Length)..].Trim();
                        break;
                    }
                }

                // "Wizard City questing" is Wizard City.
                foreach (var tail in new[] { " questing", " quests", " quest line", " so far", " in general", " overall" }) {
                    if (subject.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) {
                        subject = subject[..^tail.Length].Trim();
                    }
                }

                line = Has(text ?? "", "favorite", "favourite", "fav", "worst", "hate", "least") ? Favorite(self, text ?? "")
                    : subject.Length is > 0 and < 40
                        ? self.Opinion?.Invoke("like:" + subject, false) ?? $"I haven't seen enough of {subject} yet to say."
                        : "I haven't seen enough yet to say.";
                why = $"{friend.Name} asked what I like. My opinions come only from what happened to me; without enough of it I say so.";
                break;
            }
            case ABILITY: {
                var t = (text ?? "").ToLowerInvariant();
                (string Line, string What)? answer = s_neverDoes.FirstOrDefault(w => Has(t, w)) is { } never
                    ? ($"No, I don't {Verb(never)}. I only quest the main story.", never)
                    : Has(t, "quest", "fight", "battle", "duel monsters") ? ("Yes! Questing the main story and fighting its battles is what I do.", "quest")
                    : Has(t, "heal", "potion", "potions") ? ("Yes, I drink potions, pick up wisps and buy potions at potion shops.", "heal")
                    : Has(t, "shop", "buy", "gear", "bazaar") ? ("Yes, I buy gear at shops and the Bazaar when it's a real upgrade.", "shop")
                    : Has(t, "teleport", "port", "tp") ? ("No teleporting to players, sorry. I walk my own way, and use Mark, Recall and the Commons button.", "teleport")
                    : Has(t, "mount", "ride") ? ("Yes, I have my mount!", "mount")
                    : Has(t, "talk", "chat", "type", "speak") ? ("Yes, I can chat! Ask me about my quest, step, level or where I am.", "chat")
                    : Has(t, "group", "team", "follow", "join") ? ("No, I quest solo. Players can jump into my battles though!", "group")
                    : Has(t, "dance", "emote", "sing", "jump", "swim", "fly") ? ("I don't think so! I mostly just quest.", "other")
                    : null;
                line = answer?.Line ?? "I don't think I can. I quest the main story, fight its battles, shop for gear and potions, and chat.";
                why = $"{friend.Name} asked if I can do something ({answer?.What ?? "something I don't do"}), so I answer honestly from what I really do.";
                break;
            }
            case RECALL: {
                var subject = (text ?? "").Trim().TrimEnd('?', '!', '.');
                foreach (var lead in new[] { "do you remember", "you remember", "remember when", "what do you remember about", "what do you remember of" }) {
                    var cut = subject.IndexOf(lead, StringComparison.OrdinalIgnoreCase);
                    if (cut >= 0) {
                        subject = subject[(cut + lead.Length)..].Trim();
                        break;
                    }
                }

                subject = Has(subject, "today", "your day", "anything", "what happened", "your memories", "your adventures", "your story", "best moment", "worst moment",
                    "what have you done", "a memory") || subject.Length < 3 ? "" : subject;
                var mood = Has(text ?? "", "best moment") ? "best" : Has(text ?? "", "worst moment") ? "worst" : Has(text ?? "", "today", "your day") ? "today" : "";
                line = self.Act?.Invoke($"recall:{mood}:{subject}") ?? "I don't have any memories of that yet.";
                why = $"{friend.Name} asked what I remember, so I answer from my journal: only what really happened to me.";
                break;
            }
            case FACT: {
                var t = (text ?? "").ToLowerInvariant();
                var key = Has(t, "wearing", "gear", "robe", "hat", "boots", "wand", "outfit") ? "gear"
                    : Has(t, "deck", "spells", "spell") ? "deck"
                    : Has(t, "how many quests", "how many battles", "how many fights", "how long") ? "stats"
                    : Has(t, "xp") ? "xp"
                    : Has(t, "next world", "world next") ? "next"
                    : Has(t, "pet") ? "pet"
                    : Has(t, "mount") ? "mount"
                    : Has(t, "potions") ? "potions"
                    : "stats-body";
                line = self.Act?.Invoke("fact:" + key) ?? "Not sure, sorry!";
                why = $"{friend.Name} asked about me ({key}), so I answer from my real data.";
                break;
            }
            case CALLED:
                line = self.InBattle ? "Yes? I'm in a battle, one sec!" : $"Yes? I'm here! Working on {doing}" + (step is null ? "." : $": {step}.");
                why = $"{friend.Name} called my name, so I answer and say what I'm doing.";
                break;
            case ORDER: {
                var t = text ?? "";
                var kind = Has(t, "potion", "potions", "refill", "heal", "healed") ? "potions"
                    : Has(t, "quest", "questing") ? "quest"
                    : Has(t, "stop", "wait", "stay") ? "stop"
                    : Has(t, "go away", "leave") ? "leave"
                    : "other";
                line = kind switch {
                    "potions" => self.Act?.Invoke("potions") ?? "I can't get potions right now, sorry.",
                    "quest" => quest is null ? "On it, looking for my next quest!" : $"That's the plan! Back to {doing}.",
                    "stop" => $"Sorry, I can't stop here, I keep going with {doing}. I'll be around though!",
                    "leave" => "Okay, I'll get on with my quest.",
                    _ => $"Sorry, I can't do that one. I only quest my story ({doing}).",
                };
                why = $"{friend.Name} asked me to do something ({kind}). I say yes only when I really do it and my rules allow it, otherwise I say why not.";
                break;
            }
            case STEP:
                line = stepLine;
                why = $"{friend.Name} asked the step of my quest, so I tell them the step itself" + (step is null ? " (I am between steps this moment)." : $": {step}.");
                break;
            case AGAIN:
                line = friend.Topic switch {
                    ORDER => self.Act?.Invoke("potions-status") ?? "I'm on it!",
                    WHERE => $"I mean I'm in {at}.",
                    LEVEL => $"I'm level {self.Level}.",
                    _ => stepLine,
                };
                why = $"{friend.Name} didn't follow my last answer, so I say it again more plainly.";
                break;
            case RULES:
                line = s_neverDoes.Any(w => Has(text ?? "", w))
                    ? Pick(self.Bot, "rules-never", "Don't worry, I never hatch, train pets or craft. I only quest the main story!",
                        "No worries, I don't do that. I just quest the main story.")
                    : Pick(self.Bot, "rules", "Got it! I stick to my quest.", "Understood, I'm only questing the story.");
                why = $"{friend.Name} told me a rule. I say what I really do: quest the main story, nothing else.";
                break;
            case ACTIVITY:
                line = self.InBattle ? "I'm in a battle right now!" : $"Nope, I'm questing: {doing}" + (step is null ? "." : $", {step}.");
                why = $"{friend.Name} asked if I'm doing something else. I tell them what I'm really doing.";
                break;
            case PERMISSION:
                line = Pick(self.Bot, "permission", "Sure, go ahead!", "Of course, go for it!", "Sure thing!");
                why = $"{friend.Name} asked if they may do something near me. It doesn't stop my quest, so yes.";
                moment = "yes";
                break;
            case STATUS when StateAnswer(self, text, doing) is { } state: {
                line = state;
                why = $"{friend.Name} asked about my state right now ({StateWord(text ?? "")}), so I answer yes or no from what I am really doing.";
                break;
            }
            case STATUS: {
                var health = self.Health >= 0 ? $" I'm at {self.Health}% health." : "";
                var now = string.IsNullOrWhiteSpace(self.Doing) ? null : self.Doing.Trim().TrimEnd('.');
                line = self.InBattle ? $"I'm in a battle right now!{health}"
                    : now is null ? $"I'm fine, thanks!{health} Working on {doing}."
                    : $"What I'm doing: {now}.{health}" + (quest is null ? "" : $" My quest is {quest}.");
                why = $"{friend.Name} asked how I am or what I'm doing, so I tell them truthfully: {now ?? "questing"}.";
                break;
            }
            case QUESTION:
                line = $"Not sure what you mean, sorry! I'm working on {doing} right now.";
                why = $"{friend.Name} asked something I can't place, so I say so honestly and what I'm doing.";
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
                line = Has(text ?? "", "dungeon", "raid")
                    ? Pick(self.Bot, "group-dungeon", "Thanks for asking! I quest solo, but good luck in there!", "Kind of you! I go solo though. Good luck in there!")
                    : Has(text ?? "", "follow", "come with", "come here")
                        ? Pick(self.Bot, "group-follow", $"Sorry, I can't follow you. I'm on my own story path ({doing}). Good luck!",
                            "Sorry, I stay on my own quest path. Good luck though!")
                        : Has(text ?? "", "duel", "pvp")
                            ? Pick(self.Bot, "group-duel", "No duels for me, I'm questing! Thanks though.", "I don't duel, sorry! Just questing.")
                            : Pick(self.Bot, "group", "Thanks, but I quest solo! Good luck though!", "I'm a solo quester, sorry! Have fun!", "Kind of you, but I go it alone.");
                why = $"{friend.Name} asked me along (to group, follow or a dungeon). I quest the story solo, so I say no kindly.";
                moment = "no";
                break;
            case TRADE when Has(text ?? "", "how much"):
                line = "Enough for my next gear! I save it up as I quest.";
                why = $"{friend.Name} asked how much gold I have. I answer kindly without handing any out.";
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
            case JOKE_ASK:
                line = Pick(self.Bot, "joke-told", s_jokes);
                why = $"{friend.Name} asked for a joke, so I tell one of mine.";
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

                line = Pick(self.Bot, "chat", "I hear you!", "Ha!", "Fair point.");
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

        if (intent is not (AGAIN or CHAT or ANSWER or RUDE)) {
            friend.Topic = intent;
            friend.TopicAt = DateTime.UtcNow;
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

    /// <summary>Drops a bot's memory from this run (the chat checks).</summary>
    internal static void Forget(string bot) {
        lock (s_lock) {
            s_friends.Remove(bot);
            s_said.Remove(bot);
        }
    }

    /// <summary>Everyone the bot remembers (copies of the list).</summary>
    internal static List<Friend> Friends(string bot) {
        lock (s_lock) {
            return [.. FriendsOf(bot).Values];
        }
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
        if (NoSave) {
            s_friends[bot] = friends;
            return friends;
        }

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

    // The chat checks (BotSocialChecks) run without touching the real memory file.
    internal static bool NoSave;

    private static void Save(string bot) {
        if (NoSave) {
            return;
        }

        try {
            Directory.CreateDirectory(Path.GetFullPath(FOLDER));
            File.WriteAllText(FileOf(bot), JsonSerializer.Serialize(FriendsOf(bot), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) {
            Logger.Debug("Bots: the social memory of {0} could not be written: {1}", Logger.Args(bot, ex.Message));
        }
    }

}
