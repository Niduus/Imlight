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
using Imlight.Common;
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Teaching mode (Niduus, 2026-11-16: "a player can say 'walk me through this quest', and he guides them step by step,
/// waiting for them"). [Ultimus] Teaching (on by default, live). The player's quest is read from their own quest book (the
/// one they name, else their first story quest), and he tells them its step and where it is in plain words, with the way
/// there when it is in another area. Then he waits: each time their step changes he tells them the next one, and progress
/// on a step ("3 of 5") gets a short word now and then. He stays by them when they share an area, and keeps telling them
/// by whisper when not. It ends when the quest is done, when they say "thanks, I got it" or "stop", or after 20 minutes
/// with no progress. His own story waits meanwhile, as a player's does while helping a friend.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    private static bool TeachingOn => ConfigurationManager.GetValue("Ultimus.Teaching", true);

    private static readonly TimeSpan s_teachIdle = TimeSpan.FromMinutes(20);

    private ulong _teachCharId;
    private string _teachName = "";
    private string _teachQuest = "";
    private string _teachTitle = "";
    private string _teachStepKey = "";
    private string _teachStep = "";
    private bool _teachWhisper;
    private DateTime _teachProgressAt;
    private DateTime _teachCountSaidAt;
    private DateTime _teachLookAt;
    private int _teachSteps;

    /// <summary>"Walk me through this quest": he starts guiding them. True when the ask was his to answer.</summary>
    private bool TeachAsk(UltimusVoice.Heard heard) {
        if (!BotAsks.IsTeachAsk(heard.Text)) {
            return false;
        }

        if (!TeachingOn) {
            SendReply(heard, "Sorry, I can't walk anyone through quests right now.", "teaching");
            return true;
        }

        if (_teachCharId != 0 && _teachCharId != heard.SpeakerCharId) {
            SendReply(heard, $"I'm walking {_teachName} through a quest right now. Ask me again in a bit!", "teaching");
            return true;
        }

        var wizard = PlayerCheats.LiveWizard(heard.SpeakerCharId);
        var quest = BotAsks.QuestOf(wizard, BotAsks.NamedQuest(heard.Text));
        if (quest is null) {
            SendReply(heard, "I can't see a quest in your book to walk you through. Pick one up and ask me again!", "teaching");
            return true;
        }

        _teachCharId = heard.SpeakerCharId;
        _teachName = heard.SpeakerName ?? "";
        _teachQuest = quest.Id;
        _teachTitle = quest.Title;
        _teachWhisper = heard.Whisper;
        _teachStepKey = BotAsks.StepKey(quest.Step);
        _teachStep = quest.Step;
        _teachProgressAt = DateTime.UtcNow;
        _teachCountSaidAt = DateTime.UtcNow;
        _teachSteps = 0;
        Note("Ultimus", $"TEACHING: walks {_teachName} through \"{quest.Title}\" ({quest.Id}), step by step.");
        Think($"{_teachName} asked me to walk them through \"{quest.Title}\". I tell them each step and wait for them; my own story waits.", "teach|" + quest.Id);
        UltimusJournal.Write(UltimusJournal.PARTY, $"Walked {_teachName} through {quest.Title}.", quest.World, "", _teachName, 1);
        NoteTogether(heard.SpeakerCharId, $"walked them through \"{quest.Title}\"");
        Count("quests taught");
        SendReply(heard, $"Sure! \"{quest.Title}\". {StepWords(quest)} Tell me when you're stuck, or say \"got it\" when you don't need me.", "teaching");
        return true;
    }

    private static string StepWords(BotAsks.QuestNow quest) {
        if (string.IsNullOrWhiteSpace(quest.Step)) {
            return "Its next step is hidden in your book; talk to the person who gave it to you.";
        }

        var text = $"Step: {quest.Step.TrimEnd('.')}" + (quest.Where.Length > 0 ? $" ({quest.Where})." : ".");
        if (!quest.Here && quest.Way.Length > 0) {
            text += $" To get there: {quest.Way.TrimEnd('.')}.";
        }
        else if (quest.Here) {
            text += " It's right here in this area.";
        }

        return text;
    }

    /// <summary>"Stop" or "thanks, I got it" from the player he is guiding or helping: he stops. True when it was theirs.</summary>
    private bool StopAsk(UltimusVoice.Heard heard) {
        if (!BotAsks.IsStop(heard.Text)) {
            return false;
        }

        var mine = false;
        if (_teachCharId != 0 && _teachCharId == heard.SpeakerCharId) {
            EndTeaching("they said they've got it", null);
            mine = true;
        }

        if (_helpCharId != 0 && _helpCharId == heard.SpeakerCharId) {
            EndHelping("they said they're fine", null);
            mine = true;
        }

        if (mine) {
            SendReply(heard, "You got it! Good luck, and ask me any time.", "teaching");
        }

        return mine;
    }

    /// <summary>While teaching: his move (by them, or waiting), and the next step told when theirs changes. Null when not teaching.</summary>
    private UltimusPlanner.Target TeachTarget(UltimusMember.Status leader) {
        if (_teachCharId == 0) {
            return null;
        }

        if (!TeachingOn) {
            EndTeaching("teaching was switched off in Ahziael", "I have to stop guiding you now, sorry!");
            return null;
        }

        var wizard = PlayerCheats.LiveWizard(_teachCharId);
        if (wizard is null) {
            EndTeaching("they went offline", null);
            return null;
        }

        if (DateTime.UtcNow >= _teachLookAt) {
            _teachLookAt = DateTime.UtcNow.AddSeconds(3);
            var now = BotAsks.QuestOf(wizard, "", _teachQuest);
            if (now is null) {
                var done = wizard.HasCompletedQuest(_teachQuest);
                EndTeaching(done ? "the quest is done" : "the quest left their book",
                    done ? $"You finished \"{_teachTitle}\"! Great job. Ask me again for the next one." : $"\"{_teachTitle}\" isn't in your book any more, so I'll stop here.");
                return null;
            }

            var key = BotAsks.StepKey(now.Step);
            if (key != _teachStepKey) {
                _teachStepKey = key;
                _teachStep = now.Step;
                _teachProgressAt = DateTime.UtcNow;
                _teachSteps++;
                Note("Ultimus", $"TEACHING: {_teachName}'s step changed: tells them the next one ({UltimusText.Short(now.Step, 60)}).");
                TellTaught($"Nice! Next: {StepWords(now)}");
            }
            else if (now.Step != _teachStep) {
                _teachStep = now.Step;
                _teachProgressAt = DateTime.UtcNow;
                if (DateTime.UtcNow - _teachCountSaidAt > TimeSpan.FromSeconds(45)) {
                    _teachCountSaidAt = DateTime.UtcNow;
                    TellTaught($"Good, keep going: {now.Step}.");
                }
            }
        }

        if (DateTime.UtcNow - _teachProgressAt > s_teachIdle) {
            EndTeaching("no progress for 20 minutes", "Looks like you stepped away, so I'm heading back to my story. Ask me again any time!");
            return null;
        }

        // Going along with a player is what he is doing: no off-track alarm for it.
        StoryProgress(real: false);
        _lastProgress = DateTime.UtcNow;
        if (OverlayQuestTracker.SameZone(wizard.Zone ?? "", leader.Zone ?? "") && !CombatDuelComponent.IsInBattle(leader.GlobalId)) {
            var (x, y) = (wizard.Location.X, wizard.Location.Y);
            var key = "teach|near|" + _teachCharId;
            return UltimusPlanner.Far(leader.X, leader.Y, x, y) > (_target?.Key == key ? 90 : 250)
                ? new UltimusPlanner.Target(UltimusPlanner.SPOT, x + 40, y + 40, $"staying by {_teachName} while I walk them through \"{_teachTitle}\"", Key: key)
                : new UltimusPlanner.Target(UltimusPlanner.WAIT, leader.X, leader.Y, $"waiting for {_teachName} (walking them through \"{_teachTitle}\")", Key: "teach|wait|" + _teachCharId);
        }

        return new UltimusPlanner.Target(UltimusPlanner.WAIT, leader.X, leader.Y,
            $"guiding {_teachName} through \"{_teachTitle}\" by whisper (they are in another area)", Key: "teach|far|" + _teachCharId);
    }

    private void TellTaught(string text) {
        var wizard = PlayerCheats.LiveWizard(_teachCharId);
        var near = wizard is not null && _team.Count > 0 && OverlayQuestTracker.SameZone(wizard.Zone ?? "", _team[0].Zone ?? "")
                   && UltimusPlanner.Far(_team[0].X, _team[0].Y, wizard.Location.X, wizard.Location.Y) < UltimusPlaybook.Value("Chat.NearRadius");
        SayLater(_teachCharId, _teachWhisper || !near, text, 0.5);
    }

    private void EndTeaching(string why, string goodbye) {
        if (_teachCharId == 0) {
            return;
        }

        if (goodbye is not null) {
            TellTaught(goodbye);
        }

        Note("Ultimus", $"TEACHING: done walking {_teachName} through \"{_teachTitle}\" ({why}; {_teachSteps} step(s) told): back to his own story.");
        Think($"I'm done guiding {_teachName} ({why}). Back to my own story.", "teach-end|" + _teachCharId + "|" + why);
        _teachCharId = 0;
        _teachName = "";
        _teachQuest = "";
    }

}
