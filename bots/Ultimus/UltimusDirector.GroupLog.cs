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
using Imlight.CoreLib.Game.BotShared;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// What he does in a group, written down closely (Niduus, 2026-11-17: "Implement more checks and to log what he does in a
/// group, especially with location records. I want to take the wheel so badly and have him follow me but I am not sure
/// you can scope the difference of it"). While he is grouped, every couple of seconds group-YYYYMMDD.txt (beside his other
/// notes) gets one line: whether he is FOLLOWING his group leader or making his OWN DECISION, what that is, where he is,
/// where the leader is, how far apart, who is in a battle, and how much of the guided run is written down. Each switch
/// between the two, and each check that fails (left behind, in another area, his own way while the leader walks off), also
/// goes in his main log so it stands out.
/// </summary>
internal sealed partial class UltimusDirectorActor {

    private static readonly TimeSpan s_groupLogEvery = TimeSpan.FromSeconds(2);
    private DateTime _groupLogAt = DateTime.MinValue;
    private string _groupMode;
    private DateTime _behindSince = DateTime.MaxValue, _apartZoneSince = DateTime.MaxValue, _ownWhileMovingSince = DateTime.MaxValue;
    private (float X, float Y) _mateSeen;

    // How far behind counts as left behind (same area), and how long before each check speaks up.
    private const float GROUP_BEHIND = 1500f;
    private static readonly TimeSpan s_groupCheck = TimeSpan.FromSeconds(20);

    private void GroupLog(UltimusMember.Status me, UltimusPlanner.Target target) {
        if (_partyMate == 0 || me is null) {
            if (_groupMode is not null) {
                Note("Ultimus", "GROUP: not in a group any more; his own story again.", UltimusLink.NOTABLE);
                _groupMode = null;
            }

            return;
        }

        var following = target?.Key?.StartsWith(PARTY_KEY, StringComparison.Ordinal) == true;
        var mode = following ? "FOLLOWING" : "OWN DECISION";
        var name = PartyMateName is { Length: > 0 } n ? n : "my group leader";
        var what = target?.What ?? "nothing to do";
        if (mode != _groupMode) {
            Note("Ultimus", following
                    ? $"GROUP: FOLLOWING {name} now: {what}."
                    : $"GROUP: OWN DECISION now (not following {name}): {what}" + (target?.Key is { Length: > 0 } k ? $" [{k.Split('|')[0]}]" : "") + ".",
                UltimusLink.NOTABLE);
            _groupMode = mode;
        }

        var now = DateTime.UtcNow;
        var mate = PlayerCheats.LiveWizard(_partyMate);
        var sameZone = mate is not null && OverlayQuestTracker.SameZone(mate.Zone ?? "", me.Zone ?? "");
        var apart = sameZone ? UltimusPlanner.Far(me.X, me.Y, mate.Location.X, mate.Location.Y) : float.NaN;
        var mateMoving = mate is not null && UltimusPlanner.Far(_mateSeen.X, _mateSeen.Y, mate.Location.X, mate.Location.Y) > 40;

        // The checks: each speaks up once when it has held for a while, and once more when it clears.
        Check(ref _apartZoneSince, mate is not null && !sameZone,
            $"GROUP CHECK: {name} is in {UltimusText.Zone(mate?.Zone ?? "")} and he is in {UltimusText.Zone(me.Zone ?? "")}",
            $"GROUP CHECK: back in the same area as {name}");
        Check(ref _behindSince, sameZone && apart > GROUP_BEHIND,
            $"GROUP CHECK: {apart:0} away from {name} in the same area (left behind or lost)",
            $"GROUP CHECK: caught up with {name}");
        Check(ref _ownWhileMovingSince, !following && mateMoving && sameZone && apart > 600,
            $"GROUP CHECK: doing his own thing ({what}) while {name} walks off",
            $"GROUP CHECK: no longer doing his own thing while {name} walks off");

        // The key moments of the leader's way (Niduus, 2026-11-17: "Key moments. NPC locations, Zone transfers, teleporters,
        // the works. Point A, B and C with everything up to and between"): each one a line of its own, in both logs.
        if (mate is not null) {
            KeyMoments(me, mate, name, sameZone);
        }

        if (now - _groupLogAt < s_groupLogEvery) {
            return;
        }

        _groupLogAt = now;
        if (mate is not null) {
            _mateSeen = (mate.Location.X, mate.Location.Y);
        }

        try {
            var meFight = CombatDuelComponent.IsInBattle(me.GlobalId);
            var mateFight = mate is not null && CombatDuelComponent.IsInBattle(Wizard.GetGameObjectId(_partyMate));
            var run = UltimusGuided.Now();
            var line = $"{DateTime.Now:HH:mm:ss} | {mode}{(following ? " " + name : "")} | {what}" +
                       (target?.Key is { Length: > 0 } key ? $" [{key}]" : "") +
                       $" | me {UltimusText.Zone(me.Zone ?? "")} ({me.X:0}, {me.Y:0}, {me.Z:0}){(meFight ? " IN BATTLE" : "")}" +
                       (mate is null ? $" | {name}: not seen"
                           : $" | {name} {UltimusText.Zone(mate.Zone ?? "")} ({mate.Location.X:0}, {mate.Location.Y:0}, {mate.Location.Z:0})" +
                             $"{(mateFight ? " IN BATTLE" : "")}{(mateMoving ? " moving" : "")}") +
                       (sameZone ? $" | apart {apart:0}" : " | other area") +
                       (run.Zone is { Length: > 0 } ? $" | guided run {UltimusText.Zone(run.Zone)}: {run.Spots} spots, {run.Jumps} teleporters" : "");
            File.AppendAllLines(Path.Combine(UltimusSeat.Current.Folder, $"group-{DateTime.Now:yyyyMMdd}.txt"), [line]);
        }
        catch (Exception) {
            // A note only: never in the way of play.
        }

        void Check(ref DateTime since, bool holds, string bad, string good) {
            if (holds) {
                if (since == DateTime.MaxValue) {
                    since = now;
                }
                else if (since != DateTime.MinValue && now - since >= s_groupCheck) {
                    Note("Ultimus", bad + $" for {(now - since).TotalSeconds:0}s.", UltimusLink.ABNORMAL);
                    since = DateTime.MinValue;
                }

                return;
            }

            if (since == DateTime.MinValue) {
                Note("Ultimus", good + ".", UltimusLink.NOTABLE);
            }

            since = DateTime.MaxValue;
        }
    }

    // The leader as last seen, for the key moments.
    private (string Zone, float X, float Y, float Z, DateTime At, bool Fight) _mateWas;
    private string _mateAtNpc;
    private DateTime _mateStillSince = DateTime.MaxValue;
    private readonly Dictionary<string, string> _gateStates = new(StringComparer.OrdinalIgnoreCase);
    private string _gateZone;

    private void KeyMoments(UltimusMember.Status me, Wizard mate, string name, bool sameZone) {
        var now = DateTime.UtcNow;
        var zone = mate.Zone ?? "";
        var (x, y, z) = (mate.Location.X, mate.Location.Y, mate.Location.Z);
        var fight = CombatDuelComponent.IsInBattle(Wizard.GetGameObjectId(_partyMate));
        var was = _mateWas;
        _mateWas = (zone, x, y, z, now, fight);
        if (was.Zone is null) {
            Key($"{name} is at POINT A: {UltimusText.Zone(zone)} ({x:0}, {y:0}, {z:0})");
            return;
        }

        // An area change: where they left from and where they came in.
        if (!OverlayQuestTracker.SameZone(was.Zone, zone)) {
            Key($"ZONE TRANSFER: {name} left {UltimusText.Zone(was.Zone)} at ({was.X:0}, {was.Y:0}, {was.Z:0}) and came into " +
                $"{UltimusText.Zone(zone)} at ({x:0}, {y:0}, {z:0})");
            _mateAtNpc = null;
            return;
        }

        // A big move in a moment in the same area: a teleporter (its name when it is a known one).
        if (now - was.At < TimeSpan.FromSeconds(5) && UltimusPlanner.Far(was.X, was.Y, x, y) >= 1200 && !fight && !was.Fight) {
            var pad = UltimusPads.In(zone).Where(p => UltimusPlanner.Far(p.X, p.Y, was.X, was.Y) <= 500)
                .OrderBy(p => UltimusPlanner.Far(p.X, p.Y, was.X, was.Y)).FirstOrDefault();
            Key($"TELEPORTER: {name} went from ({was.X:0}, {was.Y:0}, {was.Z:0}) to ({x:0}, {y:0}, {z:0})" +
                (pad is not null ? $" by \"{pad.Name}\"" : ""));
        }

        // Battles: where they began and ended, and against whom.
        if (fight && !was.Fight) {
            var foes = sameZone && me.ZoneActor is { } za
                ? MinimapIndex.In(za).Where(m => m.Kind is MinimapIndex.MOB or MinimapIndex.BOSS
                                                 && UltimusPlanner.Far(m.Object.m_location.X, m.Object.m_location.Y, x, y) <= 600)
                    .Select(m => m.Name).Distinct().Take(4).ToList()
                : [];
            Key($"BATTLE: {name} started a battle at ({x:0}, {y:0}, {z:0})" + (foes.Count > 0 ? $" against {string.Join(", ", foes)}" : ""));
        }
        else if (!fight && was.Fight) {
            Key($"BATTLE OVER: {name} at ({x:0}, {y:0}, {z:0})");
        }

        // Standing a few seconds by an NPC: a talk (where the NPC stands).
        var moved = UltimusPlanner.Far(was.X, was.Y, x, y) > 30;
        _mateStillSince = moved ? DateTime.MaxValue : _mateStillSince == DateTime.MaxValue ? now : _mateStillSince;
        if (!moved && !fight && sameZone && now - _mateStillSince >= TimeSpan.FromSeconds(3) && me.ZoneActor is { } zoneActor
            && MinimapIndex.In(zoneActor).Where(m => m.Kind == MinimapIndex.NPC && !string.IsNullOrEmpty(m.Name)
                                                     && UltimusPlanner.Far(m.Object.m_location.X, m.Object.m_location.Y, x, y) <= 250)
                .OrderBy(m => UltimusPlanner.Far(m.Object.m_location.X, m.Object.m_location.Y, x, y)).FirstOrDefault() is { } npc
            && _mateAtNpc != npc.Name) {
            _mateAtNpc = npc.Name;
            Key($"NPC: {name} stopped by {npc.Name}, who stands at ({npc.Object.m_location.X:0}, {npc.Object.m_location.Y:0}, " +
                $"{npc.Object.m_location.Z:0}), {name} at ({x:0}, {y:0}, {z:0})");
        }

        // Doors and gates of this area that open or shut (a dungeon's floor doors, a lever's door).
        if (sameZone && me.ZoneActor is { } actor && ZoneStates.Of(actor) is { } states) {
            var book = BotFloors.BookOf(me.Zone);
            if (_gateZone != me.Zone) {
                _gateZone = me.Zone;
                _gateStates.Clear();
            }

            foreach (var gate in book.Gates) {
                var state = states.GetObjectState(gate.Object) ?? "";
                if (_gateStates.TryGetValue(gate.Object, out var before) && before != state && state.Length > 0) {
                    Key($"DOOR: \"{gate.Object}\" at ({gate.X:0}, {gate.Y:0}, {gate.Z:0}) went {state}" +
                        (gate.Through.Length > 0 ? $" (the way on is the teleporter \"{gate.Through}\")" : "") +
                        $"; {name} at ({x:0}, {y:0}, {z:0})");
                }

                _gateStates[gate.Object] = state;
            }
        }

        void Key(string text) {
            Note("Ultimus", "GROUP KEY: " + text + ".", UltimusLink.NOTABLE);
            try {
                File.AppendAllLines(Path.Combine(UltimusSeat.Current.Folder, $"group-{DateTime.Now:yyyyMMdd}.txt"),
                    [$"{DateTime.Now:HH:mm:ss} | KEY | {text}"]);
            }
            catch (Exception) {
                // A note only.
            }
        }
    }

}
