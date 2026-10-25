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
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.Game.Housing;

namespace Imlight.CoreLib.Game.Social;

/// <summary>One guild: its name (adventure party name keys), leader and members.</summary>
internal sealed class GuildRecord {
    public ulong Id { get; set; }
    public uint NameKeys { get; set; }
    public ulong LeaderCharId { get; set; }
    public List<ulong> Members { get; set; } = [];
    /// <summary>Unix seconds of when the guild was made.</summary>
    public long Created { get; set; }
}

/// <summary>Why a guild change did not happen.</summary>
internal enum GuildFailure {
    None,
    Disabled,
    AlreadyInGuild,
    NotInGuild,
    NotLeader,
    NameTaken,
    Full,
    NoInvite,
    NotMember,
    LeaderMustHandOver,
}

/// <summary>
/// Guilds (missing-features list): a leader, up to [Guilds] MaxMembers wizards, a name from the game's adventure
/// party name lists, and the leader's Guild House as the guild's home. The live game keeps guilds on its chat server
/// and builds them through the Social window; that window's messages carry blobs nobody has captured, so guilds here
/// are made with the .guild command and kept in guilds.json next to the server.
/// </summary>
internal static class Guilds {

    internal const string FILE = "guilds.json";

    /// <summary>The Guild House deed (SP_GuildHouse_01): the grounds; its structure is the house inside.</summary>
    internal static uint HouseTemplateId => ConfigurationManager.GetValue("Guilds.HouseTemplate", 1520393u);

    private static bool s_enabled => ConfigurationManager.GetValue("Guilds.Enabled", true);
    private static bool s_nameplate => ConfigurationManager.GetValue("Guilds.ShowOnNameplate", true);
    private static int s_maxMembers => Math.Clamp(ConfigurationManager.GetValue("Guilds.MaxMembers", 51), 2, 500);
    private static readonly TimeSpan s_inviteLife = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
    };

    private static readonly object s_lock = new();
    private static List<GuildRecord> s_guilds;
    // Invitee -> (guild, who asked, when).
    private static readonly Dictionary<ulong, (ulong GuildId, ulong InviterCharId, DateTime At)> s_invites = [];

    internal static bool Enabled => s_enabled;

    internal static int MaxMembers => s_maxMembers;

    /// <summary>The guild a wizard is in (a copy), or null.</summary>
    internal static GuildRecord For(ulong charId) {
        lock (s_lock) {
            var guild = All().FirstOrDefault(g => g.Members.Contains(charId));

            return guild is null ? null : Copy(guild);
        }
    }

    /// <summary>Whether two wizards are in the same guild.</summary>
    internal static bool SameGuild(ulong a, ulong b) {
        if (!s_enabled || a == 0 || b == 0) {
            return false;
        }

        lock (s_lock) {
            return All().Any(g => g.Members.Contains(a) && g.Members.Contains(b));
        }
    }

    /// <summary>The name keys a wizard's nameplate shows for their guild, or 0 outside a guild.</summary>
    internal static uint NameKeysFor(ulong charId)
        => s_enabled && s_nameplate ? For(charId)?.NameKeys ?? 0 : 0;

    /// <summary>Whether a house template is one of the guild house's (the grounds or the house inside).</summary>
    internal static bool IsGuildHouseTemplate(uint templateId) {
        if (templateId == 0 || !HouseTemplates.TryGet(HouseTemplateId, out var deed)) {
            return false;
        }

        return templateId == deed.TemplateId || (deed.HasStructure && templateId == deed.StructureTemplateId);
    }

    internal static GuildFailure Create(ulong leaderCharId, uint nameKeys, out GuildRecord guild) {
        guild = null;
        if (!s_enabled) {
            return GuildFailure.Disabled;
        }

        lock (s_lock) {
            var guilds = All();
            if (guilds.Any(g => g.Members.Contains(leaderCharId))) {
                return GuildFailure.AlreadyInGuild;
            }

            if (guilds.Any(g => g.NameKeys == nameKeys)) {
                return GuildFailure.NameTaken;
            }

            var made = new GuildRecord {
                Id = NewId(guilds),
                NameKeys = nameKeys,
                LeaderCharId = leaderCharId,
                Members = [leaderCharId],
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            guilds.Add(made);
            Save(guilds);
            guild = Copy(made);
        }

        Logger.Information("Guilds | {0} made guild {1} ({2}).", Logger.Args(leaderCharId, guild.Id, guild.NameKeys));

        return GuildFailure.None;
    }

    internal static GuildFailure Rename(ulong leaderCharId, uint nameKeys) {
        lock (s_lock) {
            var guilds = All();
            var guild = guilds.FirstOrDefault(g => g.Members.Contains(leaderCharId));
            if (guild is null) {
                return GuildFailure.NotInGuild;
            }

            if (guild.LeaderCharId != leaderCharId) {
                return GuildFailure.NotLeader;
            }

            if (guilds.Any(g => g != guild && g.NameKeys == nameKeys)) {
                return GuildFailure.NameTaken;
            }

            guild.NameKeys = nameKeys;
            Save(guilds);
        }

        return GuildFailure.None;
    }

    /// <summary>Remembers an invite from a guild member to a wizard outside any guild; it lasts ten minutes.</summary>
    internal static GuildFailure Invite(ulong inviterCharId, ulong inviteeCharId) {
        lock (s_lock) {
            var guilds = All();
            var guild = guilds.FirstOrDefault(g => g.Members.Contains(inviterCharId));
            if (guild is null) {
                return GuildFailure.NotInGuild;
            }

            if (guilds.Any(g => g.Members.Contains(inviteeCharId))) {
                return GuildFailure.AlreadyInGuild;
            }

            if (guild.Members.Count >= s_maxMembers) {
                return GuildFailure.Full;
            }

            s_invites[inviteeCharId] = (guild.Id, inviterCharId, DateTime.UtcNow);
        }

        return GuildFailure.None;
    }

    /// <summary>The guild that invited a wizard, if the invite has not run out.</summary>
    internal static GuildRecord PendingInvite(ulong inviteeCharId, out ulong inviterCharId) {
        inviterCharId = 0;
        lock (s_lock) {
            if (!s_invites.TryGetValue(inviteeCharId, out var invite) || DateTime.UtcNow - invite.At > s_inviteLife) {
                s_invites.Remove(inviteeCharId);

                return null;
            }

            inviterCharId = invite.InviterCharId;
            var guild = All().FirstOrDefault(g => g.Id == invite.GuildId);

            return guild is null ? null : Copy(guild);
        }
    }

    internal static GuildFailure Accept(ulong inviteeCharId, out GuildRecord joined) {
        joined = null;
        lock (s_lock) {
            if (!s_invites.Remove(inviteeCharId, out var invite) || DateTime.UtcNow - invite.At > s_inviteLife) {
                return GuildFailure.NoInvite;
            }

            var guilds = All();
            if (guilds.Any(g => g.Members.Contains(inviteeCharId))) {
                return GuildFailure.AlreadyInGuild;
            }

            var guild = guilds.FirstOrDefault(g => g.Id == invite.GuildId);
            if (guild is null) {
                return GuildFailure.NoInvite;
            }

            if (guild.Members.Count >= s_maxMembers) {
                return GuildFailure.Full;
            }

            guild.Members.Add(inviteeCharId);
            Save(guilds);
            joined = Copy(guild);
        }

        return GuildFailure.None;
    }

    internal static bool Decline(ulong inviteeCharId) {
        lock (s_lock) {
            return s_invites.Remove(inviteeCharId);
        }
    }

    /// <summary>Takes a wizard out of their guild. A leader with others still in the guild must hand over first.</summary>
    internal static GuildFailure Leave(ulong charId, out GuildRecord left) {
        left = null;
        lock (s_lock) {
            var guilds = All();
            var guild = guilds.FirstOrDefault(g => g.Members.Contains(charId));
            if (guild is null) {
                return GuildFailure.NotInGuild;
            }

            if (guild.LeaderCharId == charId && guild.Members.Count > 1) {
                return GuildFailure.LeaderMustHandOver;
            }

            guild.Members.Remove(charId);
            if (guild.Members.Count == 0) {
                guilds.Remove(guild);
            }

            Save(guilds);
            left = Copy(guild);
        }

        return GuildFailure.None;
    }

    internal static GuildFailure Kick(ulong leaderCharId, ulong memberCharId) {
        lock (s_lock) {
            var guilds = All();
            var guild = guilds.FirstOrDefault(g => g.Members.Contains(leaderCharId));
            if (guild is null) {
                return GuildFailure.NotInGuild;
            }

            if (guild.LeaderCharId != leaderCharId) {
                return GuildFailure.NotLeader;
            }

            if (memberCharId == leaderCharId || !guild.Members.Remove(memberCharId)) {
                return GuildFailure.NotMember;
            }

            Save(guilds);
        }

        return GuildFailure.None;
    }

    internal static GuildFailure HandOver(ulong leaderCharId, ulong memberCharId) {
        lock (s_lock) {
            var guilds = All();
            var guild = guilds.FirstOrDefault(g => g.Members.Contains(leaderCharId));
            if (guild is null) {
                return GuildFailure.NotInGuild;
            }

            if (guild.LeaderCharId != leaderCharId) {
                return GuildFailure.NotLeader;
            }

            if (!guild.Members.Contains(memberCharId) || memberCharId == leaderCharId) {
                return GuildFailure.NotMember;
            }

            guild.LeaderCharId = memberCharId;
            Save(guilds);
        }

        return GuildFailure.None;
    }

    internal static GuildFailure Disband(ulong leaderCharId, out GuildRecord disbanded) {
        disbanded = null;
        lock (s_lock) {
            var guilds = All();
            var guild = guilds.FirstOrDefault(g => g.Members.Contains(leaderCharId));
            if (guild is null) {
                return GuildFailure.NotInGuild;
            }

            if (guild.LeaderCharId != leaderCharId) {
                return GuildFailure.NotLeader;
            }

            guilds.Remove(guild);
            Save(guilds);
            disbanded = Copy(guild);
            foreach (var key in s_invites.Where(i => i.Value.GuildId == guild.Id).Select(i => i.Key).ToList()) {
                s_invites.Remove(key);
            }
        }

        return GuildFailure.None;
    }

    private static ulong NewId(List<GuildRecord> guilds) {
        var id = (ulong) DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        while (guilds.Any(g => g.Id == id)) {
            id++;
        }

        return id;
    }

    private static GuildRecord Copy(GuildRecord g) => new() {
        Id = g.Id, NameKeys = g.NameKeys, LeaderCharId = g.LeaderCharId, Members = [.. g.Members], Created = g.Created,
    };

    private static List<GuildRecord> All() {
        if (s_guilds is not null) {
            return s_guilds;
        }

        try {
            s_guilds = File.Exists(FILE)
                ? JsonSerializer.Deserialize<List<GuildRecord>>(File.ReadAllText(FILE), s_options)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) {
            Logger.Error("Guilds | Could not read {0}, starting with no guilds: {1}", Logger.Args(FILE, ex.Message));
        }

        s_guilds ??= [];
        s_guilds.RemoveAll(g => g is null);
        foreach (var guild in s_guilds) {
            guild.Members ??= [];
        }

        return s_guilds;
    }

    private static void Save(List<GuildRecord> guilds) {
        try {
            var temp = $"{FILE}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(guilds, s_options));
            File.Move(temp, FILE, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Error("Guilds | Could not save {0}: {1}", Logger.Args(FILE, ex.Message));
        }
    }

}
