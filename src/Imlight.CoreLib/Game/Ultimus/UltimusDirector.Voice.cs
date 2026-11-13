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
using Imlight.CoreLib.Game.Zone.Components;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Ultimus's voice, forked from the Quest Team's (QuestTeamDirector.Voice.cs): Menu Chat only, kept tame. Where the Quest
/// Team turns every ask down, Ultimus is friendly to players: it welcomes a player into its battle, says so when it walks
/// into theirs, and says yes to "help" or "join" while it is fighting. Nothing a player says changes its plan, though: the
/// Main Story comes first (Niduus, 2026-11-12), so it turns down following, trading, grouping and teleporting. It never
/// talks with the Quest Team, and the Quest Team never hears it.
/// </summary>
internal static class UltimusVoice {

    internal sealed record Heard(IActorRef Zone, float X, float Y, ulong SpeakerCharId, string SpeakerName, string Text, bool Whisper = false);

    private static readonly ConcurrentQueue<Heard> s_heard = new();

    /// <summary>A player (not Ultimus, not the Quest Team) used Menu Chat (ChatService); Ultimus may answer.</summary>
    internal static void PlayerSaid(IActorRef zone, float x, float y, ulong charId, string name, string text) {
        if (zone is null || UltimusRoster.IsMember(charId) || QuestTeam.QuestTeamRoster.IsMember(charId) || s_heard.Count > 20) {
            return;
        }

        s_heard.Enqueue(new Heard(zone, x, y, charId, name ?? "a player", text ?? ""));
    }

    /// <summary>A player whispered to Ultimus (Menu Chat or typed): it answers in Menu Chat, whispered back, wherever they are.</summary>
    internal static void PlayerWhispered(ulong charId, string name, string text) {
        if (s_heard.Count > 20) {
            return;
        }

        s_heard.Enqueue(new Heard(null, 0, 0, charId, name ?? "a player", text ?? "", Whisper: true));
    }

    internal static bool TryTake(out Heard heard) => s_heard.TryDequeue(out heard);

}

internal sealed partial class UltimusDirectorActor {

    // Kept tame: one line every 40 seconds at most, and most moments pass without one.
    private static readonly TimeSpan s_lineGap = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan s_answerGap = TimeSpan.FromSeconds(30);
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
        // A whisper it has no answer for: it is busy questing.
        ["busy"] = ["I'm busy", "Busy", "Sorry", "Not now", "Maybe later"],
        ["questing"] = ["Questing", "I'm questing", "On a quest", "Busy"],
    };

    // A player's phrase that asks to fight with it: yes while it is in a battle.
    private static readonly string[] s_fightAsks = ["help", "join", "battle", "fight", "need help", "can i"];

    // A player's phrase that asks it to change its plan: turned down, the story comes first.
    private static readonly string[] s_planAsks = [
        "follow", "come", "can you", "could you", "trade", "friend", "group", "teleport", "go to", "let's", "want to",
        "please", "will you", "duel", "carry", "port", "wait",
    ];

    /// <summary>Says a moment's line through Ultimus's Menu Chat, if it has not spoken lately and the dice allow.</summary>
    private void Speak(string moment, double chance, bool force = false, ulong whisperTo = 0) {
        if (_actors.Count == 0 || (!force && (DateTime.UtcNow < _nextLine || _voiceRandom.NextDouble() > chance))) {
            return;
        }

        if (MenuChatBook.Find(_voiceRandom, s_moments.GetValueOrDefault(moment) ?? []) is not { } phrase) {
            return;
        }

        _nextLine = DateTime.UtcNow + s_lineGap;
        if (whisperTo != 0) {
            Note("Ultimus", $"whispers back in Menu Chat: \"{phrase.Text}\"");
            _actors[0].Tell(new UltimusMember.WhisperMenuChat(phrase.Id, whisperTo));

            return;
        }

        Note("Ultimus", $"says in Menu Chat: \"{phrase.Text}\"");
        _actors[0].Tell(new UltimusMember.SayMenuChat(phrase.Id));
    }

    /// <summary>Players' Menu Chat near Ultimus: a greeting back, a yes to fighting together, a no to anything else asked.</summary>
    private void AnswerPlayers() {
        while (UltimusVoice.TryTake(out var heard)) {
            var me = _team.Count > 0 ? _team[0] : null;
            if (me is null || (!heard.Whisper && (me.ZoneActor is null || !me.ZoneActor.Equals(heard.Zone) || UltimusPlanner.Far(me.X, me.Y, heard.X, heard.Y) > 700))) {
                continue;
            }

            if (_answeredAt.TryGetValue(heard.SpeakerCharId, out var last) && DateTime.UtcNow - last < s_answerGap) {
                continue;
            }

            var text = heard.Text;
            var fighting = CombatDuelComponent.IsInBattle(me.GlobalId);
            var moment = fighting && s_fightAsks.Any(a => MenuChatBook.Says(text, a)) ? "yes"
                : s_planAsks.Any(a => MenuChatBook.Says(text, a)) || s_fightAsks.Any(a => MenuChatBook.Says(text, a)) ? "no"
                : MenuChatBook.Says(text, "thank") || MenuChatBook.Says(text, "thanks") ? "thanks"
                : new[] { "bye", "goodbye", "see you", "later" }.Any(w => MenuChatBook.Says(text, w)) ? "bye"
                : new[] { "hello", "hi", "hey", "greetings" }.Any(w => MenuChatBook.Says(text, w)) ? "hello"
                // Whispered: asked what it is doing, it is questing; anything else gets a polite "busy".
                : heard.Whisper && new[] { "what", "doing", "where", "how" }.Any(w => MenuChatBook.Says(text, w)) ? "questing"
                : heard.Whisper ? "busy"
                : null;
            Note("Ultimus", $"{heard.SpeakerName} {(heard.Whisper ? "whispered" : "said in Menu Chat")}: \"{text}\"" + moment switch {
                "yes" => "; Ultimus is fighting, so it welcomes them in.",
                "no" => "; Ultimus turns it down (its quest comes first).",
                null => "; Ultimus carries on.",
                _ => ".",
            });
            if (moment is null) {
                continue;
            }

            _answeredAt[heard.SpeakerCharId] = DateTime.UtcNow;
            Speak(moment, 1, force: true, whisperTo: heard.Whisper ? heard.SpeakerCharId : 0);
        }
    }

}
