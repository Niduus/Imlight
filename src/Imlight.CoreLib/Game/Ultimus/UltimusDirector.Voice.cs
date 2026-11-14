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

    internal sealed record Heard(IActorRef Zone, float X, float Y, ulong SpeakerCharId, string SpeakerName, string Text, bool Whisper = false, bool Typed = false);

    private static readonly ConcurrentQueue<Heard> s_heard = new();

    /// <summary>A player (not Ultimus, not the Quest Team) used Menu Chat (ChatService); Ultimus may answer.</summary>
    internal static void PlayerSaid(IActorRef zone, float x, float y, ulong charId, string name, string text, bool typed = false) {
        if (zone is null || UltimusRoster.IsMember(charId) || QuestTeam.QuestTeamRoster.IsMember(charId) || s_heard.Count > 20) {
            return;
        }

        s_heard.Enqueue(new Heard(zone, x, y, charId, name ?? "a player", text ?? "", Typed: typed));
    }

    /// <summary>A player whispered to Ultimus (Menu Chat or typed): it answers in Menu Chat, whispered back, wherever they are.</summary>
    internal static void PlayerWhispered(ulong charId, string name, string text, bool typed = false) {
        if (s_heard.Count > 20) {
            return;
        }

        s_heard.Enqueue(new Heard(null, 0, 0, charId, name ?? "a player", text ?? "", Whisper: true, Typed: typed));
    }

    internal static bool TryTake(out Heard heard) => s_heard.TryDequeue(out heard);

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
    private static readonly string[] s_notDoing = ["fighting", "battle", "pet", "pets", "fishing", "garden", "gardening", "crafting", "shopping", "farming", "dueling", "PvP", "house", "brb", "afk"];


    /// <summary>Says a moment's line through Ultimus's Menu Chat, if it has not spoken lately and the dice allow.</summary>
    private void Speak(string moment, double chance, bool force = false, ulong whisperTo = 0) {
        if (_actors.Count == 0 || (!force && (DateTime.UtcNow < _nextLine || _voiceRandom.NextDouble() > chance))) {
            return;
        }

        var me = _team.Count > 0 ? _team[0] : null;
        var fighting = me?.Wizard is not null && CombatDuelComponent.IsInBattle(me.GlobalId);
        var leaveOut = s_notDoing.Where(w => !(fighting && w is "fighting" or "battle")).ToArray();
        if (MenuChatBook.Find(_voiceRandom, leaveOut, s_moments.GetValueOrDefault(moment) ?? []) is not { } phrase) {
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
            if (me is null || (!heard.Whisper && (me.ZoneActor is null || !me.ZoneActor.Equals(heard.Zone) || UltimusPlanner.Far(me.X, me.Y, heard.X, heard.Y) > 700))) {
                continue;
            }

            // Players he met are kept in players.json too (UltimusDirector.Life.cs).
            Remember(heard.SpeakerCharId, heard.SpeakerName, heard.Whisper);
            var how = (heard.Whisper ? "whispered" : "said") + (heard.Typed ? "" : " in Menu Chat");
            var reply = BotSocial.Answer(SocialSelf(me), heard.SpeakerCharId, heard.SpeakerName, heard.Text, heard.Typed, heard.Whisper);
            if (reply is null) {
                Note("Ultimus", $"{heard.SpeakerName} {how}: \"{heard.Text}\"; Ultimus lets it pass (just answered them, or they are muted).");
                continue;
            }

            Think($"{heard.SpeakerName} {how} \"{UltimusText.Short(heard.Text, 80)}\" ({reply.Intent}). {reply.Why}", "social|" + heard.SpeakerCharId + "|" + heard.Text);
            if (reply.Text.Length == 0) {
                continue;
            }

            _nextLine = DateTime.UtcNow + s_lineGap;
            _answeredAt[heard.SpeakerCharId] = DateTime.UtcNow;
            var whisperTo = heard.Whisper ? heard.SpeakerCharId : 0;
            if (!heard.Typed && reply.MenuMoment is { } moment && s_moments.ContainsKey(moment)) {
                Speak(moment, 1, force: true, whisperTo: whisperTo);
                continue;
            }

            Note("Ultimus", $"{heard.SpeakerName} {how}: \"{heard.Text}\"; Ultimus {(heard.Whisper ? "whispers back" : "says")}: \"{reply.Text}\"");
            _actors[0].Tell(heard.Whisper ? new UltimusMember.WhisperText(reply.Text, whisperTo) : new UltimusMember.SayText(reply.Text));
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
        var goal = quest?.m_goals?.FirstOrDefault(g => g is not null && target is not null && string.Equals(target.Quest, questName, StringComparison.OrdinalIgnoreCase)
            && g.m_goalName == target.Goal);
        var step = goal is null ? "" : QuestGuide.StepText(goal);
        if (string.IsNullOrWhiteSpace(step) && target is not null && string.Equals(target.Quest, questName, StringComparison.OrdinalIgnoreCase)) {
            step = target.What ?? "";
        }

        var title = string.IsNullOrEmpty(questName) ? null : UltimusText.Title(questName).Trim().Trim('"').Trim();
        var mood = UltimusMood.Now switch {
            UltimusMood.Level.Bold => "proud",
            UltimusMood.Level.Careful => "frustrated",
            _ => "steady",
        };

        return new BotSocial.Self(UltimusRoster.NAME, me.Wizard?.PlayerNameBehavior?.GetWizardName() ?? UltimusRoster.NAME, "Balance",
            me.Wizard?.MagicSchoolBehavior?.Level ?? 1, UltimusText.Zone(me.Zone), WorldOf(me.Zone), title, step.Trim().TrimEnd('.'),
            me.Wizard is not null && CombatDuelComponent.IsInBattle(me.GlobalId), mood, NpcTip, FoeTip);
    }

    // "Where is Headmaster Ambrose?": from his NPC book.
    private static string NpcTip(string who) {
        if (UltimusNpcBook.Find(who) is not { } entry || entry.Places.Count == 0) {
            return null;
        }

        var place = entry.Places[0];

        return $"{entry.Name} stands in {UltimusText.Zone(place.Zone)}, {WorldOf(place.Zone)}" + (entry.Places.Count > 1 ? " (and a few other spots)." : ".");
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
