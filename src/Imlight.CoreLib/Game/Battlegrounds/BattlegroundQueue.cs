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
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Battlegrounds;

/*
 * Matchmaking for Beastmoon (the event window's Join Match: Solo / Group). A lobby per match gathers the wizards who
 * join within [Beastmoon] QueueSeconds of the first one, or until the match's m_matchSize (6 for PvE) is reached; a
 * group joins as one, and a group bigger than a lobby's room opens a new one. Then the whole lobby goes into one copy
 * of the arena: the first wizard's copy, which they build fresh (BattlegroundEntry.SendIntoMatch), and the others
 * follow a few seconds later, once it is up. A wizard who joins again moves to the newest lobby; the game's Cancel
 * (ProjectID 0) takes them out.
 * Each wizard's session does its own going in (MSG_BATTLEGROUNDCONTROL Start, Text the match, Player the copy's owner),
 * since only it holds the wizard (Game/Services/BattlegroundService.cs).
 */
internal static class BattlegroundQueue {

    internal static int QueueSeconds => Math.Clamp(ConfigurationManager.GetValue("Beastmoon.QueueSeconds", 10), 0, 120);
    private static readonly TimeSpan FollowDelay = TimeSpan.FromSeconds(3);

    internal sealed record Entry(ulong CharId, ulong GameObjectId, IActorRef Session);

    private sealed class Lobby {
        internal string MatchName;
        internal uint ProjectId;
        internal int Size;
        internal readonly List<Entry> Members = [];
        internal Timer Timer;
    }

    private static readonly object s_lock = new();
    private static readonly List<Lobby> s_lobbies = [];

    /// <summary>Puts a wizard (and their group mates, already checked to be free) in a lobby for the match.</summary>
    internal static void Join(string matchName, uint projectId, Entry wizard, IReadOnlyCollection<Entry> mates) {
        var size = Math.Max(1, Battlegrounds.Template(matchName)?.m_matchSize ?? 6);
        var party = new[] { wizard }.Concat(mates ?? []).DistinctBy(e => e.CharId).ToList();
        Lobby launch = null;
        Lobby lobby;
        lock (s_lock) {
            foreach (var member in party) {
                RemoveLocked(member.CharId);
            }

            lobby = s_lobbies.FirstOrDefault(l => l.MatchName == matchName && l.Members.Count + party.Count <= l.Size);
            if (lobby is null) {
                lobby = new Lobby { MatchName = matchName, ProjectId = projectId, Size = size };
                var opened = lobby;
                lobby.Timer = new Timer(_ => Launch(opened), null, TimeSpan.FromSeconds(QueueSeconds), Timeout.InfiniteTimeSpan);
                s_lobbies.Add(lobby);
            }

            lobby.Members.AddRange(party);
            if (lobby.Members.Count >= lobby.Size) {
                s_lobbies.Remove(lobby);
                lobby.Timer.Dispose();
                launch = lobby;
            }
        }

        Logger.Information("Beastmoon | {0} wait for {1} ({2} of {3}).",
            Logger.Args(string.Join(", ", party.Select(p => p.CharId)), matchName, lobby.Members.Count, lobby.Size));
        foreach (var member in lobby.Members) {
            member.Session.Tell(new WIZARD2_53_PROTOCOL.MSG_BATTLEGROUNDQUEUEUPDATE {
                ProjectID = projectId, PlayerGID = member.GameObjectId, Waiting = 1, Kicked = "",
            });
            member.Session.Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE {
                Message = $"{lobby.Members.Count} of {lobby.Size} hunters waiting for the match.", Modal = 0,
            });
        }

        if (launch is not null) {
            Launch(launch);
        }
    }

    /// <summary>Takes a wizard out of any lobby (the game's Cancel). Returns whether they were waiting.</summary>
    internal static bool Leave(ulong charId) {
        lock (s_lock) {
            return RemoveLocked(charId);
        }
    }

    private static bool RemoveLocked(ulong charId) {
        foreach (var lobby in s_lobbies.ToList()) {
            if (lobby.Members.RemoveAll(m => m.CharId == charId) > 0) {
                if (lobby.Members.Count == 0) {
                    lobby.Timer.Dispose();
                    s_lobbies.Remove(lobby);
                }

                return true;
            }
        }

        return false;
    }

    private static void Launch(Lobby lobby) {
        List<Entry> members;
        lock (s_lock) {
            s_lobbies.Remove(lobby);
            lobby.Timer?.Dispose();
            members = [.. lobby.Members];
        }

        if (members.Count == 0) {
            return;
        }

        var owner = members[0];
        Logger.Information("Beastmoon | {0} starts with {1} hunter(s), in the copy of {2}.",
            Logger.Args(lobby.MatchName, members.Count, owner.CharId));
        owner.Session.Tell(new ZONE_102_PROTOCOL.MSG_BATTLEGROUNDCONTROL { Kind = BattlegroundControlKind.Start, Text = lobby.MatchName });

        var followers = members.Skip(1).ToList();
        if (followers.Count == 0) {
            return;
        }

        Task.Delay(FollowDelay).ContinueWith(_ => {
            foreach (var follower in followers) {
                follower.Session.Tell(new ZONE_102_PROTOCOL.MSG_BATTLEGROUNDCONTROL {
                    Kind = BattlegroundControlKind.Start, Text = lobby.MatchName, Player = owner.CharId,
                });
            }
        });
    }

}
