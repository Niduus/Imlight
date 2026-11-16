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
using System.Collections.Generic;
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

    // A group mate changing areas (a door, a sigil, a teleport) is offline to the group for a moment while their next area
    // loads. He waits this long for them before he counts them gone (Niduus, 2026-11-16: "he seems to lose the group after
    // a zone transfer": he left the group the moment his mate went through a door).
    private static readonly TimeSpan s_partyLoading = TimeSpan.FromMinutes(3);

    private DateTime _partyOfflineSince = DateTime.MaxValue;
    private DateTime _partyEnterFightUntil = DateTime.MinValue;

    private ulong _partyMate;
    private string _partyMateName = "";
    private DateTime _partyGotoAt = DateTime.MinValue;
    private DateTime _partyMateMovedAt = DateTime.UtcNow;
    private (float X, float Y, string Zone) _partyMateLast;

    /// <summary>
    /// Who counts as a group mate: a real player (not a bot); for a TCO7 member, also another TCO7 member. Ultimus himself
    /// never groups with the TCO7: his data stays his own.
    /// </summary>
    private bool IsPartyMate(ulong charId)
        => charId != 0 && !QuestTeam.QuestTeamRoster.IsMember(charId)
           && (!UltimusRoster.IsMember(charId) || (_seat.IsTco7 && UltimusSeat.OfCharacter(charId) is { IsTco7: true }));

    /// <summary>Whom he goes along with now (party mode), or "".</summary>
    private string PartyMateName => _partyMate == 0 ? "" : _partyMateName;

    /// <summary>His move in party mode, or null when he is in no group with a real player (then his own story goes on).</summary>
    private UltimusPlanner.Target PartyTarget(UltimusMember.Status leader) {
        // His own area loading: nothing is known of the group for that moment, and nothing ended.
        if (leader.Wizard is null) {
            return _partyMate == 0 ? null : new UltimusPlanner.Target(UltimusPlanner.WAIT, leader.X, leader.Y, $"changing areas with {_partyMateName} (my group)",
                Key: PARTY_KEY + "loading|" + _partyMate);
        }

        if (!GroupRegistry.TryGetGroup(leader.CharId, out var group)) {
            EndParty("the group ended");
            return null;
        }

        // Party mode switched off while in a group: he says so and leaves it (all settings apply at once).
        if (!UltimusWire.PartyMode) {
            LeaveParty("party mode was switched off in Ahziael", "I have to get back to my own story now. Thanks for the company!");
            return null;
        }

        // A TCO7 member's group mates may be other TCO7 members too (they team up, UltimusDirector.Tco7.cs). The one who
        // asked leads and plays his own story; the others go along with him as with a player.
        var players = group.Members.Where(m => m.CharId != leader.CharId && IsPartyMate(m.CharId)).ToList();
        if (_seat.IsTco7 && group.LeaderCharId == leader.CharId && players.Count > 0 && players.All(m => UltimusRoster.IsMember(m.CharId))) {
            return Tco7Leading(leader, players);
        }

        var mate = players.Where(m => m.IsOnline).OrderByDescending(m => m.CharId == group.LeaderCharId).FirstOrDefault();
        if (mate is null) {
            // Offline to the group is also a player loading their next area: he waits for them a while, still grouped.
            var loading = players.OrderByDescending(m => m.CharId == _partyMate).ThenByDescending(m => m.CharId == group.LeaderCharId).FirstOrDefault();
            if (loading is null) {
                LeaveParty("no player is in the group", null);
                return null;
            }

            if (_partyOfflineSince == DateTime.MaxValue) {
                _partyOfflineSince = DateTime.UtcNow;
                Note("Ultimus", $"PARTY: {(_partyMate != 0 ? _partyMateName : "my group mate")} is changing areas (offline to the group for now): he stays in the group and waits for them.");
            }

            if (DateTime.UtcNow - _partyOfflineSince > s_partyLoading) {
                _partyOfflineSince = DateTime.MaxValue;
                LeaveParty($"no player of the group came back online in {(int) s_partyLoading.TotalMinutes} minutes", null);
                return null;
            }

            StoryProgress(real: false);
            _lastProgress = DateTime.UtcNow;
            return new UltimusPlanner.Target(UltimusPlanner.WAIT, leader.X, leader.Y,
                $"waiting for {(_partyMate != 0 ? _partyMateName : "my group mate")} (my group), who is changing areas", Key: PARTY_KEY + "away|" + loading.CharId);
        }

        _partyOfflineSince = DateTime.MaxValue;

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
            _actors[0].Tell(new UltimusMember.WhisperText(BotShared.BotSocial.Scrub($"Thanks for the invite, {name}! I'll come along with you."), mate.CharId));
            // A leader's favorite battle order from an earlier group with them (UltimusDirector.Orders.cs).
            if (mate.CharId == group.LeaderCharId && RecallBattleOrder(mate.CharId) is { } remembered) {
                Note("Ultimus", $"PARTY: remembers how {name} likes battles run: {remembered}");
                Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds(2), _actors[0],
                    new UltimusMember.WhisperText(BotShared.BotSocial.Scrub(remembered), mate.CharId), Self);
            }
        }

        // Going along with a player is what he is doing: no off-track or no-progress alarm for it.
        StoryProgress(real: false);
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

        // Told to stay (his group leader, UltimusDirector.Orders.cs): he waits on that spot until told to follow.
        if (UltimusOrders.Stay is { } stay && OverlayQuestTracker.SameZone(stay.Zone, leader.Zone ?? "")) {
            return new UltimusPlanner.Target(UltimusPlanner.WAIT, stay.X, stay.Y, $"staying here, as {name} (my group leader) asked",
                Key: PARTY_KEY + "stay|" + mate.CharId);
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
                // Waiting out the mate's battle is standing still on purpose, like watching it: the "watch" key keeps the stuck
                // guard off (TCO7 Mythis was "stuck" in The Commons waiting for Kurrent's battle, 2026-11-16).
                Key: PARTY_KEY + (fighting ? "watch|" : "goto|") + mate.CharId);
        }

        if (float.IsNaN(at.X)) {
            return new UltimusPlanner.Target(UltimusPlanner.SPOT, leader.X, leader.Y, $"with {name} (my group)", Key: PARTY_KEY + "with|" + mate.CharId);
        }

        // On a sigil, counting down: he steps on it with them and presses it, as a player joins a friend's dungeon pad
        // (Niduus, 2026-11-16: "he doesn't get on sigil with me"). He stands still on it once his own countdown runs.
        if (Services.ZoneService.IsCountingDown(mateGid) && !Services.ZoneService.IsCountingDown(leader.GlobalId) && SigilTarget(leader, at, name) is { } sigil) {
            return sigil;
        }

        // Told to go in ("enter the fight", "go in first"): he walks onto the nearest enemy by them and starts the battle.
        if (!fighting && DateTime.UtcNow < _partyEnterFightUntil && !CombatDuelComponent.IsInBattle(leader.GlobalId)) {
            if (EnterFightTarget(leader, at, name) is { } foe) {
                return foe;
            }

            _partyEnterFightUntil = DateTime.MinValue;
            SayLater(mate.CharId, true, "I don't see an enemy near you to start a fight with.", 0.5);
        }

        if (fighting) {
            _partyEnterFightUntil = DateTime.MinValue;
        }
        else {
            _partyStrongWarned = 0;
        }

        // Their battle: he walks in, as a player joins a group mate's fight (when there is room). One far too strong for him
        // he asks about first, and goes in only when told again (Niduus's log, 2026-11-16 02:16: he walked into Walter
        // Blanco's fight with Malistaire the Undying at level 25 and lost).
        if (fighting) {
            if (CombatDuelComponent.UltimusMayJoin(mateGid, out _) && DateTime.UtcNow >= _partyStrongOkUntil
                && TooStrong(leader, CombatDuelComponent.UltimusFoesOf(mateGid), out var strong)) {
                if (_partyStrongWarned != mateGid) {
                    _partyStrongWarned = mateGid;
                    Note("Ultimus", $"PARTY: {name}'s battle looks far too strong for him ({strong}): he asks before going in.", UltimusLink.NOTABLE);
                    Think($"{name}'s fight looks far too strong for me ({strong}). I ask first instead of walking in and falling.", "party-strong|" + mateGid);
                    SayLater(mate.CharId, true, $"That fight looks far too strong for me ({strong}). Say \"enter the fight\" and I'll come in anyway.", 0.5);
                }

                return new UltimusPlanner.Target(UltimusPlanner.SPOT, leader.X, leader.Y,
                    $"watching {name}'s battle (it looks far too strong for me; I go in if they say so)", Key: PARTY_KEY + "watch|" + mate.CharId);
            }

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

        // Otherwise close by them, not on top of them. Once walking to them he keeps on until well inside, and once by them
        // he stays until they are well away: one edge made him flip between the two every second (Niduus's log, 2026-11-14:
        // "with Walter Blanco" and "going along with Walter Blanco" over and over while grouped).
        var following = _target?.Key == PARTY_KEY + "follow|" + mate.CharId;
        return UltimusPlanner.Far(leader.X, leader.Y, at.X, at.Y) > (following ? 90 : 220)
            ? new UltimusPlanner.Target(UltimusPlanner.SPOT, at.X + 40, at.Y + 40, $"going along with {name} (my group)", Key: PARTY_KEY + "follow|" + mate.CharId)
            : new UltimusPlanner.Target(UltimusPlanner.SPOT, leader.X, leader.Y, $"with {name} (my group)", Key: PARTY_KEY + "with|" + mate.CharId);
    }

    // The sigil his mate is counting down on: walked to, then pressed once he is on it ("Interact", as the X a player presses).
    private UltimusPlanner.Target SigilTarget(UltimusMember.Status leader, (float X, float Y) mateAt, string name) {
        var pad = Tools.MinimapIndex.In(leader.ZoneActor).Where(m => m.Kind == Tools.MinimapIndex.SIGIL && m.Object is not null)
            .Select(m => (Mark: m, Far: UltimusPlanner.Far(mateAt.X, mateAt.Y, m.Object.m_location.X, m.Object.m_location.Y)))
            .Where(m => m.Far < 400).OrderBy(m => m.Far).Select(m => m.Mark).FirstOrDefault();
        if (pad is null) {
            return null;
        }

        var (x, y) = (pad.Object.m_location.X, pad.Object.m_location.Y);
        var target = new UltimusPlanner.Target(UltimusPlanner.SPOT, x, y, $"stepping on the sigil with {name} (my group), to go in together",
            GlobalId: pad.Object.m_globalID.Full, Key: PARTY_KEY + "sigil|" + pad.Object.m_globalID.Full, Z: pad.Object.m_location.Z);
        if (UltimusPlanner.Far(leader.X, leader.Y, x, y) < 120 && leader.Options.ContainsKey(target.GlobalId)) {
            if (_doing != target.What) {
                Think($"{name} is on the sigil and its countdown runs: I hop on with them so we go in together.", "party-sigil|" + target.GlobalId);
            }

            ClickOnce(0, target, ["Interact"], $"steps on the sigil {pad.Name} with {name}");
        }

        return target;
    }

    // The enemy to start a fight with, when told to go in: the nearest creature by his mate (a boss counts), free to fight.
    private UltimusPlanner.Target EnterFightTarget(UltimusMember.Status leader, (float X, float Y) mateAt, string name) {
        var foe = Tools.MinimapIndex.In(leader.ZoneActor)
            .Where(m => m.Kind is Tools.MinimapIndex.MOB or Tools.MinimapIndex.BOSS && m.Object is not null
                        && BotShared.DudCreatures.Fightable(m.Object.m_globalID.Full)
                        && UltimusPlanner.Far(mateAt.X, mateAt.Y, m.Object.m_location.X, m.Object.m_location.Y) < 900)
            .OrderBy(m => UltimusPlanner.Far(mateAt.X, mateAt.Y, m.Object.m_location.X, m.Object.m_location.Y)).FirstOrDefault();
        return foe is null ? null : new UltimusPlanner.Target(UltimusPlanner.FIGHT, foe.Object.m_location.X, foe.Object.m_location.Y,
            $"going into the fight with {foe.Name}, as {name} (my group leader) asked", Template: foe.Template, GlobalId: foe.Object.m_globalID.Full,
            Key: PARTY_KEY + "enter|" + foe.Object.m_globalID.Full);
    }

    /// <summary>"Enter the fight", "go in first": he walks into the nearest enemy by his mate (for 2 minutes, or until a battle starts).</summary>
    // A battle's strongest creature with this many times his own full health, or all of them together with this many
    // times, is far too strong for him to walk into unasked.
    private const int STRONG_ONE = 5;
    private const int STRONG_ALL = 8;

    // The group mate whose too-strong battle he asked about, and until when he was told to go in anyway.
    private ulong _partyStrongWarned;
    private DateTime _partyStrongOkUntil = DateTime.MinValue;

    /// <summary>
    /// Whether creatures (by exact template) are far too strong for him, by their health in the game's data against his own
    /// full health; the words say who and how much.
    /// </summary>
    private static bool TooStrong(UltimusMember.Status me, IReadOnlyCollection<ulong> foes, out string words) {
        words = "";
        if (foes is not { Count: > 0 } || me.Wizard?.GameStats is not { } stats) {
            return false;
        }

        var mine = Math.Max(1, stats.m_baseHitpoints + stats.m_bonusHitpoints);
        var healths = foes.Select(t => (Template: t, Health: UltimusGameData.Health(t))).Where(f => f.Health > 0).ToList();
        if (healths.Count == 0) {
            return false;
        }

        var top = healths.MaxBy(f => f.Health);
        var all = healths.Sum(f => (long) f.Health);
        if (top.Health < (long) mine * STRONG_ONE && all < (long) mine * STRONG_ALL) {
            return false;
        }

        var topName = UltimusGameData.Creature(top.Template) is { Length: > 0 } c && c.IndexOf(" (template", StringComparison.Ordinal) is > 0 and var cut
            ? c[..cut] : "the strongest one";
        words = $"{topName} has {top.Health:N0} health{(healths.Count > 1 ? $", {all:N0} all together" : "")}, and I have {mine:N0}";

        return true;
    }

    private bool EnterFightNow(UltimusMember.Status me) {
        if (_partyMate == 0 || me.Wizard is null || !OverlayQuestTracker.SameZone(PlayerCheats.LiveWizard(_partyMate)?.Zone ?? "", me.Zone ?? "")) {
            return false;
        }

        var mate = PlayerCheats.LiveWizard(_partyMate);
        if (EnterFightTarget(me, (mate.Location.X, mate.Location.Y), _partyMateName) is null) {
            return false;
        }

        _partyEnterFightUntil = DateTime.UtcNow.AddMinutes(2);
        return true;
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

        UltimusOrders.Clear();
        _partyEnterFightUntil = DateTime.MinValue;
        _partyStrongWarned = 0;
        _partyStrongOkUntil = DateTime.MinValue;
        _partyOfflineSince = DateTime.MaxValue;
        Note("Ultimus", $"PARTY: no longer with {_partyMateName} ({why}): back to his own story.");
        if (why == "the group ended") {
            Announce(SAY_PARTY, $"Thanks for the group, {_partyMateName}! Back to my story.", urgent: true);
        }
        Think($"I'm done going along with {_partyMateName} ({why}). Back to my own story.", "party-end|" + _partyMate);
        _partyMate = 0;
        _partyMateName = "";
    }

}
