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
 *
 * ========================================================================
 * TEAM UP BOARD
 * ========================================================================
 *
 * PURPOSE:
 * Team Up (Niduus, 2026-10-24: everything rated 5 and up in, "Team Up kiosk: join a team for a dungeon with one
 * click"). A player at a dungeon sigil presses Team Up! and asks for a team; other players see it in the Join a Team
 * window (the kiosk, or .teamup) and join with one click. When the team is full, everyone pressed Ready, or the wait
 * runs out with at least two, the whole team goes into one fresh copy of the dungeon, owned by the player who asked.
 *
 * USAGE EXAMPLE:
 * TeamUpBoard.Request(owner, sigil, size, farming, mode, min, max);   // MSG_REQUESTTEAMUP
 * TeamUpBoard.Join(member, sigilId);                                  // MSG_REQUESTTEAMHELPJOIN
 *
 * NOTE:
 * What the game wants was read from WizardGraphicalClient.exe (r806919, 2026-10-24):
 * - MSG_TEAMUPADDPLAYERTOCLIENT fills one of the 4 slots of the Team Up! window (TeamUpMainWindow) and opens the window
 *   when it is not open and SecondsLeft is above 0. A new SigilID empties the slots. PackedName is the packed name
 *   (the group messages' name blob), PlayerSchool the school name ("Fire": Schools/Art_School_Fire.dds) and SigilName
 *   a locale key.
 * - MSG_TEAMUPREMOVEPLAYERFROMCLIENT takes a player out; Disbanded tells the others the asker cancelled
 *   (GUI2_00001118) and StopTimer closes the window.
 * - MSG_TEAMUPTELEPORTPLAYER only empties the slots; the server moves the players.
 * - MSG_REQUESTTEAMHELPINFO opens the Join a Team window (TeamHelpWindow) or refreshes it. WorldInfo is a
 *   TeamHelpUpdateInfo (m_worldList: the world tabs to show, see TeamUpWorlds) and SigilInfo a
 *   TeamHelpUpdateSigilInfoList (the teams of the tab shown), both plain ObjectProperty blobs (property mask 4).
 *   A tab click sends MSG_CLIENTREQUESTTEAMHELPWORLDINFO; the answer is MSG_REQUESTTEAMHELPINFO again with that tab's
 *   teams, because the game handles only that one. The window keeps a team's m_sigilID as 32 bits.
 * Every member's session gets its trip as a MSG_STARTSIGILENTRY with NoSigil, the way a pad's team goes.
 *
 * TODO:
 * - Volunteering (Team Help volunteers) and the monthly gauntlet credits are not done.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.IO;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.TeamUp;

internal static class TeamUpBoard {

    private const uint CLIENT_PROPERTY_MASK = 4;

    // [TeamUp] Enabled: Team Up and the Join a Team window are on.
    internal static readonly bool IsEnabled
        = ConfigurationManager.GetValue("TeamUp.Enabled", true);

    // [TeamUp] SigilButton: dungeon sigils for two or more players show the Team Up! button.
    internal static readonly bool SigilButton
        = ConfigurationManager.GetValue("TeamUp.SigilButton", true);

    // [TeamUp] WaitSeconds: how long a team waits for players before it goes (with two or more) or ends.
    internal static readonly int WaitSeconds
        = Math.Clamp(ConfigurationManager.GetValue("TeamUp.WaitSeconds", 300), 30, 3600);

    // A full team goes this long after the last player joined, so everyone sees the team first.
    private static readonly TimeSpan FULL_DELAY = TimeSpan.FromSeconds(3);

    internal sealed class Member {
        public ulong CharId { get; init; }
        public ulong Gid { get; init; }
        public IActorRef Session { get; set; }
        public byte[] NameBlob { get; init; } = [];
        public string School { get; init; } = "";
        public uint Level { get; init; }
    }

    internal sealed class Team {
        public uint Id { get; init; }
        public TeamUpSigils.Sigil Sigil { get; init; }
        public uint WorldId { get; init; }
        public byte Size { get; set; }
        public bool IsFarming { get; set; }
        public byte Mode { get; set; }
        public uint MinimumLevel { get; set; }
        public uint MaximumLevel { get; set; }
        public DateTime Created { get; init; } = DateTime.UtcNow;
        public DateTime Deadline { get; set; }
        public DateTime? GoAt { get; set; }
        public List<Member> Members { get; } = [];
        public HashSet<ulong> Ready { get; } = [];
        public Member Owner => Members.Count > 0 ? Members[0] : null;
        public string DisplayKey => TeamUpSigils.DisplayKeyOf(Sigil.DestinationZone);
        public uint SecondsLeft => (uint) Math.Max(0, Math.Ceiling((Deadline - DateTime.UtcNow).TotalSeconds));
    }

    private static readonly object s_lock = new();
    private static readonly List<Team> s_teams = [];
    private static uint s_nextId = 1;
    private static Timer s_ticker;

    // ---------------------------------------------------------------- asking for a team

    /// <summary>The Team Up! window's Team Up! button at a sigil (or its options, with edit).</summary>
    internal static void Request(Member owner, TeamUpSigils.Sigil sigil, byte size, bool farming, byte mode,
                                 uint minLevel, uint maxLevel, bool edit) {
        lock (s_lock) {
            var own = TeamOf(owner.CharId);
            if (edit && own is not null && own.Owner?.CharId == owner.CharId) {
                own.Size = ClampSize(size, own.Members.Count);
                own.IsFarming = farming;
                own.Mode = mode;
                own.MinimumLevel = minLevel;
                own.MaximumLevel = maxLevel;
                Logger.Information("Team Up: {0} changed team {1} (size {2}, farming {3}, levels {4}-{5}).",
                    Logger.Args(owner.CharId, own.Id, own.Size, farming, minLevel, maxLevel));
                CheckFull(own);

                return;
            }

            if (own is not null) {
                LeaveLocked(owner.CharId, "asked for a new team");
            }

            var team = new Team {
                Id = s_nextId++,
                Sigil = sigil,
                WorldId = TeamUpWorlds.WorldOf(sigil.DestinationZone),
                Size = ClampSize(size, 1),
                IsFarming = farming,
                Mode = mode,
                MinimumLevel = minLevel,
                MaximumLevel = maxLevel,
                Deadline = DateTime.UtcNow + TimeSpan.FromSeconds(WaitSeconds),
            };
            team.Members.Add(owner);
            s_teams.Add(team);
            EnsureTicker();

            Logger.Information("Team Up: {0} asks for a team of {1} for {2} (team {3}, world {4}, {5} s).",
                Logger.Args(owner.CharId, team.Size, sigil.DestinationZone, team.Id, team.WorldId, WaitSeconds));
            SendAdd(team, owner, owner);
        }
    }

    // ---------------------------------------------------------------- the Join a Team window

    /// <summary>The Join a Team window for a player, opened (or refreshed) on the tab of the given world.</summary>
    internal static WIZARD2_53_PROTOCOL.MSG_REQUESTTEAMHELPINFO HelpInfo(uint worldId, uint level) {
        List<TeamHelpUpdateSigilInfo> sigils;
        lock (s_lock) {
            sigils = [.. s_teams
                .Where(t => t.WorldId == worldId && t.GoAt is null && t.Members.Count < t.Size && FitsLevel(t, level))
                .Select(ToSigilInfo)];
        }

        return new WIZARD2_53_PROTOCOL.MSG_REQUESTTEAMHELPINFO {
            WorldInfo = Serialize(new TeamHelpUpdateInfo { m_worldList = [.. TeamUpWorlds.All.Select(w => w.Id)] }),
            SigilInfo = Serialize(new TeamHelpUpdateSigilInfoList { m_sigilInfoList = sigils }),
        };
    }

    /// <summary>How many teams are waiting for players, for the .teamup command.</summary>
    internal static IReadOnlyList<(string Zone, int Count, int Size, uint SecondsLeft)> Waiting() {
        lock (s_lock) {
            return [.. s_teams.Where(t => t.GoAt is null).Select(t => (t.Sigil.DestinationZone, t.Members.Count, (int) t.Size, t.SecondsLeft))];
        }
    }

    // ---------------------------------------------------------------- joining, leaving and ready

    /// <summary>A player picked a team in the Join a Team window. False (with why) when it can't be joined.</summary>
    internal static bool Join(Member member, uint teamId, out string why) {
        why = null;
        lock (s_lock) {
            var team = s_teams.FirstOrDefault(t => t.Id == teamId);
            if (team is null || team.GoAt is not null || team.Members.Count >= team.Size) {
                why = "That team is no longer available.";

                return false;
            }

            if (team.Members.Any(m => m.CharId == member.CharId)) {
                why = "You are already on that team.";

                return false;
            }

            if (!FitsLevel(team, member.Level)) {
                why = $"That team is for levels {team.MinimumLevel} to {(team.MaximumLevel == 0 ? "any" : team.MaximumLevel)}.";

                return false;
            }

            if (TeamOf(member.CharId) is not null) {
                LeaveLocked(member.CharId, "joined another team");
            }

            team.Members.Add(member);
            team.Ready.Clear();
            Logger.Information("Team Up: {0} joined team {1} ({2} of {3}).",
                Logger.Args(member.CharId, team.Id, team.Members.Count, team.Size));

            // The new player sees everyone (the owner first, which opens the window); the others see the new player.
            foreach (var other in team.Members) {
                SendAdd(team, other, member);
                if (other != member) {
                    SendAdd(team, member, other);
                }
            }

            CheckFull(team);

            return true;
        }
    }

    /// <summary>A player cancelled Team Up! (or logged out). The asker's leaving ends the team for everyone.</summary>
    internal static void Leave(ulong charId, string why) {
        lock (s_lock) {
            LeaveLocked(charId, why);
        }
    }

    /// <summary>The Ready button of the Team Up! window: once every member is ready, the team goes.</summary>
    internal static void MarkReady(ulong charId) {
        lock (s_lock) {
            var team = TeamOf(charId);
            if (team is null || team.GoAt is not null) {
                return;
            }

            team.Ready.Add(charId);
            var count = (byte) team.Members.Count(m => team.Ready.Contains(m.CharId));
            var sender = team.Members.First(m => m.CharId == charId);
            foreach (var member in team.Members) {
                member.Session.Tell(new WIZARD3_56_PROTOCOL.MSG_UPDATETEAMUPREADY {
                    ReadyCount = count,
                    SigilID = team.Id,
                    PlayerGID = sender.Gid,
                }, ActorRefs.NoSender);
            }

            Logger.Information("Team Up: {0} is ready on team {1} ({2} of {3}).",
                Logger.Args(charId, team.Id, count, team.Members.Count));
            if (team.Members.Count >= 2 && count >= team.Members.Count) {
                Go(team, "everyone is ready");
            }
            else if (team.Members.Count < 2) {
                sender.Session.Tell(Note("Nobody has joined yet. The team goes as soon as someone joins and everyone is ready."), ActorRefs.NoSender);
            }
        }
    }

    internal static bool IsQueued(ulong charId) {
        lock (s_lock) {
            return TeamOf(charId) is not null;
        }
    }

    // ---------------------------------------------------------------- inside

    private static Team TeamOf(ulong charId)
        => s_teams.FirstOrDefault(t => t.Members.Any(m => m.CharId == charId));

    private static byte ClampSize(byte size, int atLeast)
        => (byte) Math.Clamp(size is >= 2 and <= 4 ? size : 4, Math.Max(2, atLeast), 4);

    private static bool FitsLevel(Team team, uint level)
        => (team.MinimumLevel == 0 || level >= team.MinimumLevel) && (team.MaximumLevel == 0 || level <= team.MaximumLevel);

    private static void LeaveLocked(ulong charId, string why) {
        var team = TeamOf(charId);
        if (team is null) {
            return;
        }

        var leaver = team.Members.First(m => m.CharId == charId);
        var wasOwner = team.Owner == leaver;
        team.Members.Remove(leaver);
        team.Ready.Remove(charId);

        // The leaver's own window closes.
        leaver.Session.Tell(Remove(team, leaver, leaver, disbanded: false, stop: true), ActorRefs.NoSender);

        if (wasOwner || team.GoAt is not null && team.Members.Count < 2) {
            foreach (var other in team.Members) {
                other.Session.Tell(Remove(team, other, other, disbanded: true, stop: true), ActorRefs.NoSender);
            }

            s_teams.Remove(team);
            Logger.Information("Team Up: team {0} ended, {1} left ({2}).", Logger.Args(team.Id, charId, why));

            return;
        }

        team.GoAt = null;
        foreach (var other in team.Members) {
            other.Session.Tell(Remove(team, leaver, other, disbanded: false, stop: false), ActorRefs.NoSender);
        }

        Logger.Information("Team Up: {0} left team {1} ({2}); {3} of {4} now.",
            Logger.Args(charId, team.Id, why, team.Members.Count, team.Size));
    }

    private static void CheckFull(Team team) {
        if (team.GoAt is null && team.Members.Count >= team.Size) {
            team.GoAt = DateTime.UtcNow + FULL_DELAY;
            Logger.Information("Team Up: team {0} is full, it goes in {1} s.", Logger.Args(team.Id, FULL_DELAY.TotalSeconds));
        }
    }

    // Everyone goes into one fresh copy of the dungeon, owned by the asker; the others a moment after the owner, so
    // the copy is there first (as a pad's team does).
    private static void Go(Team team, string why) {
        s_teams.Remove(team);
        var owner = team.Owner;
        Logger.Information("Team Up: team {0} goes into {1} with {2} players ({3}).",
            Logger.Args(team.Id, team.Sigil.DestinationZone, team.Members.Count, why));

        for (var i = 0; i < team.Members.Count; i++) {
            var member = team.Members[i];
            var isOwner = member == owner;
            member.Session.Tell(new WIZARD2_53_PROTOCOL.MSG_TEAMUPTELEPORTPLAYER {
                SigilID = team.Id,
                PlayerGID = member.Gid,
                ZoneID = 0,
                PlayerPosition = (byte) i,
                PlayerCount = (byte) team.Members.Count,
                Level = member.Level,
            }, ActorRefs.NoSender);
            member.Session.Tell(Remove(team, member, member, disbanded: false, stop: true), ActorRefs.NoSender);
            member.Session.Tell(new ZONE_102_PROTOCOL.MSG_STARTSIGILENTRY {
                NoSigil = true,
                SigilLoc = "",
                DestinationZone = team.Sigil.DestinationZone,
                DestinationLoc = team.Sigil.DestinationLoc,
                Variant = team.Sigil.Variant,
                InstanceOwnerCharId = isOwner ? 0 : owner.CharId,
                KeepInstance = !isOwner,
                FaceSlot = i,
                CountdownSeconds = isOwner ? 2.0f : 3.0f + 0.5f * i,
                LogName = $"Team Up {team.Id}",
            }, ActorRefs.NoSender);
        }
    }

    private static void EnsureTicker() {
        s_ticker ??= new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private static void Tick() {
        try {
            lock (s_lock) {
                var now = DateTime.UtcNow;
                foreach (var team in s_teams.ToList()) {
                    if (team.GoAt is { } goAt) {
                        if (now >= goAt) {
                            Go(team, "the team is full");
                        }

                        continue;
                    }

                    if (now < team.Deadline) {
                        continue;
                    }

                    if (team.Members.Count >= 2) {
                        Go(team, "the wait is over");

                        continue;
                    }

                    var owner = team.Owner;
                    s_teams.Remove(team);
                    if (owner is not null) {
                        owner.Session.Tell(Remove(team, owner, owner, disbanded: false, stop: true), ActorRefs.NoSender);
                        owner.Session.Tell(Note("Nobody joined your Team Up in time. Press Team Up! at the sigil to ask again."), ActorRefs.NoSender);
                    }

                    Logger.Information("Team Up: team {0} ended, nobody joined in time.", Logger.Args(team.Id));
                }
            }
        }
        catch (Exception ex) {
            Logger.Error("Team Up: the timer failed. {0}", Logger.Args(ex.Message));
        }
    }

    // ---------------------------------------------------------------- messages

    // Tells `to` about `who`.
    private static void SendAdd(Team team, Member to, Member who)
        => to.Session.Tell(new WIZARD2_53_PROTOCOL.MSG_TEAMUPADDPLAYERTOCLIENT {
            SigilID = team.Id,
            PlayerGID = who.Gid,
            PackedName = new ByteString(who.NameBlob),
            PlayerSchool = who.School,
            PlayerLevel = who.Level,
            SigilName = team.DisplayKey,
            UpdatePlayerGID = to.Gid,
            SecondsLeft = Math.Max(1u, team.SecondsLeft),
            FromSigil = (byte) (who == team.Owner ? 1 : 0),
            RequestedSigilMode = team.Mode,
        }, ActorRefs.NoSender);

    private static WIZARD2_53_PROTOCOL.MSG_TEAMUPREMOVEPLAYERFROMCLIENT Remove(Team team, Member who, Member to, bool disbanded, bool stop)
        => new() {
            SigilID = team.Id,
            PlayerGID = who.Gid,
            UpdatePlayerGID = to.Gid,
            Disbanded = (byte) (disbanded ? 1 : 0),
            StopTimer = (byte) (stop ? 1 : 0),
        };

    private static EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE Note(string text)
        => new() { Message = text, Modal = 0 };

    private static TeamHelpUpdateSigilInfo ToSigilInfo(Team team)
        => new() {
            m_sigilID = team.Id,
            m_buttonStringKey = team.DisplayKey,
            m_playerSchoolIDList = [.. team.Members.Select(m => StringHash.Compute(m.School))],
            m_isInstance = true,
            m_secondsElapsed = (uint) Math.Max(0, (DateTime.UtcNow - team.Created).TotalSeconds),
            m_isFarming = team.IsFarming,
            m_minimumTeamSize = team.Size,
            m_pSigilMonthlyGauntletInfo = null,
            m_requestedSigilMode = Enum.IsDefined(typeof(RequestedSigilMode), (int) team.Mode)
                ? (RequestedSigilMode) team.Mode
                : RequestedSigilMode.REQUESTED_DEFAULT_MODE_SIGIL,
        };

    private static ByteString Serialize(PropertyClass value) {
        var serializer = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
        if (!serializer.Serialize(value, CLIENT_PROPERTY_MASK, out var data)) {
            Logger.Error("Team Up: could not write {0}.", Logger.Args(value.GetType().Name));

            return default;
        }

        return data;
    }

}
