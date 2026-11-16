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
/// What the group helper asked of Ultimus now, for his battle brain (UltimusTactics): whom he helps and in what role.
/// </summary>
internal static class UltimusHelp {

    internal sealed record Ask(ulong CharId, ulong GlobalId, string Name, BotAsks.HelpRole Role);

    private static volatile Ask s_now;

    internal static Ask Now => s_now;

    internal static void Set(Ask ask) => s_now = ask;

}

/// <summary>
/// Group helper (Niduus, 2026-11-16: "he joins a real player's fight when asked and plays a role like healer or hitter").
/// [Ultimus] Helper (on by default, live). A player near him says "help me fight", "join my battle", "heal me" or "be my
/// hitter": he walks to them and into their battle (as a player joins a fight, when there is room) and plays the role they
/// asked for: as healer he keeps them up first (heals, then shields), as hitter he hits what they hit. "Help me" with no
/// role: healer when they are hurt, else hitter. Not in a battle yet: he stays by them for 3 minutes, waiting for it. One in
/// another area is told to invite him to a group (party mode brings him). It ends when their fight is over, or they say
/// "thanks" or "stop". Nobody moves him: he walks.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    private static bool HelperOn => ConfigurationManager.GetValue("Ultimus.Helper", true);

    private static readonly TimeSpan s_helpWait = TimeSpan.FromMinutes(3);

    private ulong _helpCharId;
    private string _helpName = "";
    private BotAsks.HelpRole _helpRole;
    private DateTime _helpUntil;
    private bool _helpSawFight;
    private bool _helpWhisper;

    /// <summary>"Help me fight" / "heal me": he comes to help. True when the ask was his to answer.</summary>
    private bool HelpAsk(UltimusVoice.Heard heard, UltimusMember.Status me) {
        if (!BotAsks.IsHelpAsk(heard.Text, out var role)) {
            return false;
        }

        if (!HelperOn) {
            SendReply(heard, "Sorry, I can't join fights on request right now.", "group helper");
            return true;
        }

        if (_helpCharId != 0 && _helpCharId != heard.SpeakerCharId) {
            SendReply(heard, $"I'm helping {_helpName} right now. Ask me again after!", "group helper");
            return true;
        }

        var wizard = PlayerCheats.LiveWizard(heard.SpeakerCharId);
        if (wizard is null || !OverlayQuestTracker.SameZone(wizard.Zone ?? "", me.Zone ?? "")) {
            SendReply(heard, "I'm not in your area. Invite me to a group and I'll come to you!", "group helper");
            return true;
        }

        var gid = Wizard.GetGameObjectId(heard.SpeakerCharId);
        if (role == BotAsks.HelpRole.Any) {
            role = wizard.GameStats is { } stats && stats.m_currentHitpoints < Math.Max(1, stats.m_baseHitpoints + stats.m_bonusHitpoints) * 0.6
                ? BotAsks.HelpRole.Healer : BotAsks.HelpRole.Hitter;
        }

        _helpCharId = heard.SpeakerCharId;
        _helpName = heard.SpeakerName ?? "";
        _helpRole = role;
        _helpWhisper = heard.Whisper;
        _helpSawFight = false;
        _helpUntil = DateTime.UtcNow + s_helpWait;
        UltimusHelp.Set(new UltimusHelp.Ask(heard.SpeakerCharId, gid, _helpName, role));
        var roleWords = role == BotAsks.HelpRole.Healer ? "your healer: I keep you up first" : "your hitter: I hit what you hit";
        var fighting = CombatDuelComponent.IsInBattle(gid);
        Note("Ultimus", $"HELPER: {_helpName} asked for help ({role}); {(fighting ? "walks into their battle" : "waits by them for their fight")}.");
        Think($"{_helpName} asked me to help in a fight. I'll be {roleWords}. My own story waits until their fight is over.", "help|" + heard.SpeakerCharId);
        NoteTogether(heard.SpeakerCharId, $"helped them in a fight as their {(role == BotAsks.HelpRole.Healer ? "healer" : "hitter")}");
        Count("fights helped");
        UltimusJournal.Write("helped", $"Helped {_helpName} in a fight as their {(role == BotAsks.HelpRole.Healer ? "healer" : "hitter")}.", "", UltimusText.Zone(me.Zone ?? ""), _helpName, 1);
        SendReply(heard, fighting ? $"On my way! I'll be {roleWords}." : $"Sure! Start the fight and I'll jump in. I'll be {roleWords}.", "group helper");
        return true;
    }

    /// <summary>While helping: walking into their battle, or waiting by them. Null when not helping.</summary>
    private UltimusPlanner.Target HelperTarget(UltimusMember.Status leader) {
        if (_helpCharId == 0) {
            return null;
        }

        if (!HelperOn) {
            EndHelping("the helper was switched off in Ahziael", "I have to go, sorry!");
            return null;
        }

        var wizard = PlayerCheats.LiveWizard(_helpCharId);
        if (wizard is null || !OverlayQuestTracker.SameZone(wizard.Zone ?? "", leader.Zone ?? "")) {
            EndHelping(wizard is null ? "they went offline" : "they left the area", wizard is null ? null : "You left the area, so I'm heading back to my story.");
            return null;
        }

        var gid = Wizard.GetGameObjectId(_helpCharId);
        var theyFight = CombatDuelComponent.IsInBattle(gid);
        var iFight = CombatDuelComponent.IsInBattle(leader.GlobalId);
        if (theyFight || iFight) {
            _helpSawFight = true;
            _helpUntil = DateTime.UtcNow + s_helpWait;
        }
        else if (_helpSawFight) {
            EndHelping("their fight is over", "Good fight! Back to my story. Ask me any time.");
            return null;
        }
        else if (DateTime.UtcNow > _helpUntil) {
            EndHelping("no fight started", "Looks like you don't need me after all. Ask again any time!");
            return null;
        }

        StoryProgress(real: false);
        _lastProgress = DateTime.UtcNow;
        var (x, y) = (wizard.Location.X, wizard.Location.Y);
        if (theyFight && !iFight) {
            return CombatDuelComponent.UltimusMayJoin(gid, out _)
                ? new UltimusPlanner.Target(UltimusPlanner.SPOT, x, y, $"joining {_helpName}'s battle (they asked for help)", Key: "help|fight|" + _helpCharId)
                : new UltimusPlanner.Target(UltimusPlanner.WAIT, leader.X, leader.Y, $"watching {_helpName}'s battle (it is full)", Key: "help|watch|" + _helpCharId);
        }

        var key = "help|near|" + _helpCharId;
        return UltimusPlanner.Far(leader.X, leader.Y, x, y) > (_target?.Key == key ? 90 : 220)
            ? new UltimusPlanner.Target(UltimusPlanner.SPOT, x + 40, y + 40, $"going to {_helpName}, who asked for help in a fight", Key: key)
            : new UltimusPlanner.Target(UltimusPlanner.WAIT, leader.X, leader.Y, $"waiting by {_helpName} for their fight", Key: "help|wait|" + _helpCharId);
    }

    private void EndHelping(string why, string goodbye) {
        if (_helpCharId == 0) {
            return;
        }

        if (goodbye is not null) {
            SayLater(_helpCharId, _helpWhisper, goodbye, 0.5);
        }

        Note("Ultimus", $"HELPER: done helping {_helpName} ({why}): back to his own story.");
        Think($"I'm done helping {_helpName} ({why}). Back to my own story.", "help-end|" + _helpCharId + "|" + why);
        UltimusHelp.Set(null);
        _helpCharId = 0;
        _helpName = "";
    }

}
