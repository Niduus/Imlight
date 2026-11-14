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
using System.Linq;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// The bots' chat checks: real lines players said to Ultimus (Niduus's screenshots and the chat audit, 2026-11-14) and what
/// the answer must, and must never, say. Run by Ultimus's self-test after an update, and by hand in development. Each
/// check talks as a new player (no memory, no rate limit between them) and nothing is written to the memory file.
/// </summary>
internal static class BotSocialChecks {

    internal sealed record Check(string Line, bool Typed, bool InBattle, string Intent, string[] Has, string[] Never, string Before = null);

    // Fillers that must never answer a question or a rule (audit C4).
    private static readonly string[] s_fillers = ["Ha, true!", "Interesting!", "Fair enough!", "I hear you!", "Okay!", "Got it!", "Ha!", "Fair point."];

    internal static readonly Check[] All = [
        new("Hi", false, false, BotSocial.GREETING, [], ["Hello?"]),
        new("Can I Fish next to you?", false, false, BotSocial.PERMISSION, ["go ahead", "Of course", "Sure"], s_fillers),
        new("what quest are you on?", false, false, BotSocial.QUEST, ["Payback"], ["...", "Right now:"]),
        new("what quest are you on?", true, false, BotSocial.QUEST, ["Payback"], ["..."]),
        new("what step", true, false, BotSocial.STEP, ["Defeat 3 Nirini Warrior (1 of 3)"], s_fillers),
        new("what step are you on?", true, false, BotSocial.STEP, ["Defeat 3 Nirini Warrior"], s_fillers),
        new("no, the step of the quest", true, false, BotSocial.STEP, ["Defeat 3 Nirini Warrior"], s_fillers, Before: "what quest are you on?"),
        new("how far along are you?", true, false, BotSocial.STEP, ["Defeat 3 Nirini Warrior"], s_fillers),
        new("Wanna do a dungeon?", true, false, BotSocial.GROUP, ["solo", "good luck", "Good luck"], s_fillers),
        new("Follow me", false, false, BotSocial.GROUP, ["can't follow", "own quest path"], ["hatch", "Sure"]),
        new("follow me", true, true, BotSocial.GROUP, ["battle", "fight", "One sec", "brb"], ["Sure", "jump in"]),
        new("You're not allowed to hatch!", true, false, BotSocial.RULES, ["never hatch", "don't do that"], s_fillers),
        new("wanna hatch pets?", true, false, BotSocial.GROUP, ["solo", "Kind of you", "sorry", "Sorry"], s_fillers),
        new("can I join your battle?", true, true, BotSocial.GROUP, ["jump in", "come help", "Join in"], s_fillers),
        new("want to duel?", true, false, BotSocial.GROUP, ["duel"], s_fillers),
        new("are you hurt?", true, false, BotSocial.STATUS, ["52%"], s_fillers),
        new("why are you standing still?", true, false, BotSocial.STATUS, ["waiting for wisps"], s_fillers),
        new("this quest is dumb lol", true, false, BotSocial.JOKE, [], ["leave you be", "take care"]),
        new("the trash mobs here hit hard", true, false, BotSocial.CHAT, [], ["leave you be", "take care"]),
        new("I'll do it later", true, false, BotSocial.CHAT, [], ["Bye", "See you"]),
        new("nice weather today", true, false, BotSocial.CHAT, [], ["thank you", "Thanks"]),
        new("thanks for the help", true, false, BotSocial.THANKS, [], ["quest solo"]),
        new("pet owner here", true, false, BotSocial.CHAT, [], ["can't share"]),
        new("how much gold do you have?", true, false, BotSocial.TRADE, ["Enough"], ["don't give"]),
        new("are you an NPC?", true, false, BotSocial.BOT, ["bot"], s_fillers),
        new("who owns this server", true, false, BotSocial.PRIVATE, [], s_fillers),
        new("Who made you?", true, false, BotSocial.MAKER, ["Niduus"], ["can't", "Not sure"]),
        new("What's your favorite world?", true, false, BotSocial.LIKE, ["Wizard City"], ["Not sure", "sand"]),
        new("What's your least favorite school?", true, false, BotSocial.LIKE, ["Myth"], ["I'm a Balance wizard", "Storm"]),
        new("whats your favorite spell", true, false, BotSocial.LIKE, ["haven't seen enough"], ["Not sure", "Scarab", "Hydra"]),
        new("do you like Krokotopia?", true, false, BotSocial.LIKE, ["lost about"], s_fillers),
        new("do you like Celestia?", true, false, BotSocial.LIKE, ["haven't seen enough"], ["I do", "fun"]),
        new("ARe you in combat?", true, false, BotSocial.STATUS, ["No, I'm not in a battle"], ["Not sure"]),
        new("are you walking?", true, false, BotSocial.STATUS, [], ["Not sure"]),
        new("are you questing?", true, false, BotSocial.STATUS, ["Yes"], ["Not sure"]),
        new("ok", true, false, null, [], []),
        new("brb", true, false, null, [], []),
        new("what level are you?", true, false, BotSocial.LEVEL, ["12"], s_fillers),
        // 2026-11-14 "oh boy" screenshot.
        new("hey Ultimus", true, false, BotSocial.GREETING, [], ["Not sure"]),
        new("ULTIMUS??", true, false, BotSocial.CALLED, ["here"], ["Not sure"]),
        new("You stuck again?", true, false, BotSocial.STATUS, ["waiting for wisps"], s_fillers),
        new("go get potions", true, false, BotSocial.ORDER, ["potion shop"], ["Got it!"]),
        new("like right now", true, false, BotSocial.AGAIN, ["on my way", "potion"], s_fillers, Before: "go get potions"),
        new("Can you fish?", true, false, BotSocial.ABILITY, ["No, I don't fish"], ["Not sure"]),
        new("can you hatch pets?", true, false, BotSocial.ABILITY, ["No"], ["Not sure"]),
        new("can you buy potions?", true, false, BotSocial.ORDER, ["potion"], ["Not sure"]),
        new("can you juggle?", true, false, BotSocial.ABILITY, ["don't think"], ["Not sure", "Yes"]),
        new("stop", true, false, BotSocial.ORDER, ["can't stop"], ["Got it!", "Okay"]),
        new("where are you?", true, false, BotSocial.WHERE, ["The Oasis"], s_fillers),
    ];

    /// <summary>The checks that failed, in plain words (empty when all pass).</summary>
    internal static List<string> Run() {
        var failed = new List<string>();
        var was = BotSocial.NoSave;
        BotSocial.NoSave = true;
        try {
            var self = new BotSocial.Self("selftest", "Ultimus", "Balance", 12, "The Oasis", "Krokotopia", "Payback", "Defeat 3 Nirini Warrior (1 of 3)",
                false, "steady", Doing: "waiting for wisps to come back before the fight", Health: 52,
                Worlds: ["Wizard City", "Krokotopia"],
                // Stands in for his records: Myth creatures beat him; Wizard City went smoothly; he's been to Krokotopia.
                Opinion: (topic, least) => topic switch {
                    "school" when least => "Myth. Myth creatures beat me 3 times so far!",
                    "world" when !least => "Wizard City! Things went smoothly there.",
                    "like:Krokotopia" => "It's okay! I lost about 40 minutes there being stuck, though.",
                    _ => null,
                },
                Act: what => what == "potions" ? "Good idea, heading to the potion shop now (2 areas away)!" : "Yes, I'm on my way to the potion shop now!");
            ulong id = 900_000;
            foreach (var check in All) {
                id++;
                var me = self with { InBattle = check.InBattle };
                // A few tries, as some lines are picked at random: every try must pass.
                for (var i = 0; i < 6; i++) {
                    id += 1000;
                    if (check.Before is not null) {
                        BotSocial.Answer(me, id, "Tester", check.Before, check.Typed, false);
                    }

                    var reply = BotSocial.Answer(me, id, "Tester", check.Line, check.Typed, false);
                    var intent = reply?.Intent;
                    var text = reply?.Text ?? "";
                    // Small talk may pass without a word (he answers it only now and then).
                    if (check.Intent == BotSocial.CHAT && reply is null) {
                        continue;
                    }

                    var problem = check.Intent is null
                        ? reply is null || text.Length == 0 ? null : $"should stay quiet, said \"{text}\""
                        : intent != check.Intent ? $"meant \"{intent ?? "nothing"}\", should be \"{check.Intent}\" (said \"{text}\")"
                        : check.Has.Length > 0 && !check.Has.Any(h => text.Contains(h, StringComparison.Ordinal)) ? $"said \"{text}\", which has none of: {string.Join(" / ", check.Has)}"
                        : check.Never.FirstOrDefault(n => text.Contains(n, StringComparison.Ordinal)) is { } bad ? $"said \"{text}\", which must never have \"{bad}\""
                        : null;
                    if (problem is not null) {
                        failed.Add($"\"{check.Line}\" ({(check.Typed ? "typed" : "Menu Chat")}{(check.InBattle ? ", in battle" : "")}): {problem}");
                        break;
                    }
                }
            }
        }
        finally {
            BotSocial.Forget("selftest");
            BotSocial.NoSave = was;
        }

        return failed;
    }

}
