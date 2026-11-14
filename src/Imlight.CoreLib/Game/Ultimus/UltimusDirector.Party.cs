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
using System.Linq;
using Akka.Actor;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Groups;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Party mode (Niduus, 2026-11-14, approved): when a real player groups with him ([Ultimus] PartyMode, on by default), Ultimus
/// goes along with them as a group mate does. He teleports to them from the group like any player (the game's own "go to
/// friend" button), joins their battles, does the quest steps they both have next to them, and otherwise stays close. He
/// skips nothing and nobody moves him. When the group ends, the player leaves or goes away for a long while, he goes back
/// to his own story.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    private const string PARTY_KEY = "party|";
    private static readonly TimeSpan s_partyAway = TimeSpan.FromMinutes(15);

    private ulong _partyMate;
    private string _partyMateName = "";
    private DateTime _partyGotoAt = DateTime.MinValue;
    private DateTime _partyMateMovedAt = DateTime.UtcNow;
    private (float X, float Y, string Zone) _partyMateLast;

    /// <summary>Whom he goes along with now (party mode), or "".</summary>
    private string PartyMateName => _partyMate == 0 ? "" : _partyMateName;

    /// <summary>His move in party mode, or null when he is in no group with a real player (then his own story goes on).</summary>
    private UltimusPlanner.Target PartyTarget(UltimusMember.Status leader) {
        if (leader.Wizard is null || !GroupRegistry.TryGetGroup(leader.CharId, out var group)) {
            EndParty("the group ended");
            return null;
        }

        // Party mode switched off while in a group: he says so and leaves it (all settings apply at once).
        if (!UltimusWire.PartyMode) {
            LeaveParty("party mode was switched off in Ahziael", "I have to get back to my own story now. Thanks for the company!");
            return null;
        }

        var mate = group.Members.Where(m => m.CharId != leader.CharId && m.IsOnline && !UltimusRoster.IsMember(m.CharId)
                                              && !QuestTeam.QuestTeamRoster.IsMember(m.CharId))
            .OrderByDescending(m => m.CharId == group.LeaderCharId).FirstOrDefault();
        if (mate is null) {
            LeaveParty("no player of the group is online", null);
            return null;
        }

        var wizard = PlayerCheats.LiveWizard(mate.CharId);
        var name = PlayerCheats.LiveName(mate.CharId) is { Length: > 0 } n ? n : "my group mate";
        if (_partyMate != mate.CharId) {
            _partyMate = mate.CharId;
            _partyMateName = name;
            _partyMateMovedAt = DateTime.UtcNow;
            Note("Ultimus", $"PARTY: in a group with {name}: goes along with them (their battles, the steps they both have) until the group ends.");
            Think($"{name} and I are in a group now. I go where they go, help in their battles and do the quest steps we both have. " +
                  "My own story waits until the group ends.", "party|" + mate.CharId);
            UltimusJournal.Write(UltimusJournal.PARTY, $"Grouped up with {name} in {UltimusText.Zone(leader.Zone)}.", WorldOf(leader.Zone),
                UltimusText.Zone(leader.Zone), name, 1);
            Count("groups joined");
        }

        // Going along with a player is what he is doing: no off-track or no-progress alarm for it.
        StoryProgress();
        _lastProgress = DateTime.UtcNow;

        var mateGid = Wizard.GetGameObjectId(mate.CharId);
        var fighting = CombatDuelComponent.IsInBattle(mateGid);
        var at = wizard is null ? (X: float.NaN, Y: float.NaN) : (wizard.Location.X, wizard.Location.Y);
        if (fighting || mate.ZonePath != _partyMateLast.Zone || float.IsNaN(at.X)
            || UltimusPlanner.Far(at.X, at.Y, _partyMateLast.X, _partyMateLast.Y) > 30) {
            _partyMateMovedAt = DateTime.UtcNow;
            _partyMateLast = (at.X, at.Y, mate.ZonePath);
        }

        // Away a long while: he says goodbye, leaves the group and goes back to his story.
        if (DateTime.UtcNow - _partyMateMovedAt > s_partyAway) {
            LeaveParty($"{name} has not moved for {(int) s_partyAway.TotalMinutes} minutes",
                "Looks like you stepped away, so I'm heading back to my story. Invite me again any time!");
            return null;
        }

        // In another area: he goes to them from the group, as a player clicks their name and "go to".
        if (!OverlayQuestTracker.SameZone(mate.ZonePath ?? "", leader.Zone ?? "")) {
            if (!fighting && DateTime.UtcNow - _partyGotoAt > TimeSpan.FromSeconds(30) && !CombatDuelComponent.IsInBattle(leader.GlobalId)) {
                _partyGotoAt = DateTime.UtcNow;
                Note("Ultimus", $"PARTY: {name} is in {UltimusText.Zone(mate.ZonePath)}: goes to them from the group, as a player does.");
                _actors[0].Tell(new UltimusMember.GoToTeammate(mate.CharId));
            }

            return new UltimusPlanner.Target(UltimusPlanner.SPOT, leader.X, leader.Y,
                fighting ? $"waiting for {name} (my group) to finish their battle, then going to them" : $"going to {name} (my group)",
                Key: PARTY_KEY + "goto|" + mate.CharId);
        }

        if (float.IsNaN(at.X)) {
            return new UltimusPlanner.Target(UltimusPlanner.SPOT, leader.X, leader.Y, $"with {name} (my group)", Key: PARTY_KEY + "with|" + mate.CharId);
        }

        // Their battle: he walks in, as a player joins a group mate's fight (when there is room).
        if (fighting) {
            return CombatDuelComponent.UltimusMayJoin(mateGid, out _)
                ? new UltimusPlanner.Target(UltimusPlanner.SPOT, at.X, at.Y, $"joining {name}'s battle (my group)", Key: PARTY_KEY + "fight|" + mate.CharId)
                : new UltimusPlanner.Target(UltimusPlanner.SPOT, leader.X, leader.Y, $"watching {name}'s battle (my group; it is full)", Key: PARTY_KEY + "watch|" + mate.CharId);
        }

        // A quest step they both have, done together next to them.
        foreach (var quest in leader.Wizard.QuestBehavior?.CurrentQuestInstances?.Select(q => q?.QuestName).Where(q => !string.IsNullOrEmpty(q)).ToList() ?? []) {
            if (wizard?.QuestBehavior?.HasQuest(quest) != true || UltimusPlanner.ForQuest([leader], quest) is not { } step
                || step.Problem.Length > 0 || UltimusPlanner.Far(step.X, step.Y, at.X, at.Y) > 900) {
                continue;
            }

            Think($"{name} and I both have {UltimusText.Title(quest)}, and its step is right by them, so we do it together.", "party-quest|" + quest);
            return step with { What = $"{step.What} (with {name}, my group)", Key = PARTY_KEY + "quest|" + step.Key };
        }

        // Otherwise close by them, not on top of them.
        return UltimusPlanner.Far(leader.X, leader.Y, at.X, at.Y) > 150
            ? new UltimusPlanner.Target(UltimusPlanner.SPOT, at.X + 40, at.Y + 40, $"going along with {name} (my group)", Key: PARTY_KEY + "follow|" + mate.CharId)
            : new UltimusPlanner.Target(UltimusPlanner.SPOT, leader.X, leader.Y, $"with {name} (my group)", Key: PARTY_KEY + "with|" + mate.CharId);
    }

    // He leaves the group himself (a whisper first when there is something to say) and goes back to his story.
    private void LeaveParty(string why, string goodbye) {
        if (_partyMate != 0 && goodbye is not null) {
            _actors[0].Tell(new UltimusMember.WhisperText(goodbye, _partyMate));
        }

        if (DateTime.UtcNow - _partyGotoAt > TimeSpan.FromSeconds(5)) {
            _partyGotoAt = DateTime.UtcNow;
            _actors[0].Tell(new UltimusMember.LeaveGroup());
        }

        EndParty(why);
    }

    private void EndParty(string why) {
        if (_partyMate == 0) {
            return;
        }

        Note("Ultimus", $"PARTY: no longer with {_partyMateName} ({why}): back to his own story.");
        Think($"I'm done going along with {_partyMateName} ({why}). Back to my own story.", "party-end|" + _partyMate);
        _partyMate = 0;
        _partyMateName = "";
    }

}
