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
using System.Globalization;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Game.Housing;
using Imlight.CoreLib.Game.Social;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// Guilds from chat (see <see cref="Guilds"/>): `.guild` shows yours, `.guild create <name>|random`, `.guild names`,
/// `.guild invite <wizard>`, `.guild accept|decline`, `.guild leave`, `.guild kick <wizard>`, `.guild leader <wizard>`,
/// `.guild rename <name>`, `.guild disband yes`, `.guild house`; `.g <words>` talks to the guild.
/// </summary>
internal class CommandGuildProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    private static readonly Random s_random = new();

    [Command("guild")]
    private void GuildCommand([Remainder] string what = "") {
        if (!Guilds.Enabled) {
            InformSenderClient("Guilds are off ([Guilds] Enabled = false in Imlight.ini).");

            return;
        }

        var text = (what ?? "").Trim();
        var space = text.IndexOf(' ');
        var verb = (space < 0 ? text : text[..space]).ToLowerInvariant();
        var rest = space < 0 ? "" : text[(space + 1)..].Trim();
        var me = Context.Character;

        switch (verb) {
            case "":
            case "info":
            case "members":
                ShowGuild(me);
                break;
            case "create":
                CreateGuild(me, rest);
                break;
            case "names":
                ShowNames();
                break;
            case "invite":
                InviteToGuild(me, rest);
                break;
            case "accept":
            case "join":
                AcceptInvite(me);
                break;
            case "decline":
                InformSenderClient(Guilds.Decline(me.CharId) ? "You turned the guild invite down." : "You have no guild invite.");
                break;
            case "leave":
                LeaveGuild(me);
                break;
            case "kick":
                KickFromGuild(me, rest);
                break;
            case "leader":
                HandOverGuild(me, rest);
                break;
            case "rename":
                RenameGuild(me, rest);
                break;
            case "disband":
                DisbandGuild(me, rest);
                break;
            case "house":
            case "home":
                GoToGuildHouse(me);
                break;
            default:
                InformSenderClient(Help());
                break;
        }
    }

    [Command("g")]
    private void GuildChatCommand([Remainder] string words = "") {
        var me = Context.Character;
        var guild = Guilds.For(me.CharId);
        words = (words ?? "").Trim();
        if (guild is null) {
            InformSenderClient("You are not in a guild. Make one with .guild create, or ask a guild member to .guild invite you.");

            return;
        }

        if (words.Length == 0) {
            InformSenderClient("Use .g and what you want to say to your guild.");

            return;
        }

        var line = $"[Guild] {NameOf(me.CharId)}: {words}";
        var sent = TellGuild(guild, line, skip: me.CharId);
        InformSenderClient(sent == 0 ? $"{line} (nobody else in the guild is online)" : line);
        Logger.Information("[Guild {0}] {1}: {2}", Logger.Args(guild.Id, NameOf(me.CharId), words));
    }

    private void ShowGuild(Wizard me) {
        var guild = Guilds.For(me.CharId);
        if (guild is null) {
            var invite = Guilds.PendingInvite(me.CharId, out var inviter);
            InformSenderClient(invite is null
                ? "You are not in a guild.<br>" + Help()
                : $"{NameOf(inviter)} invited you to {GuildName(guild: invite)}. Type .guild accept to join or .guild decline.");

            return;
        }

        InformSenderClient(DescribeGuild(guild), true);
    }

    /// <summary>The guild's name, leader and members, online ones marked; also what the Guild Tome shows.</summary>
    internal static string DescribeGuild(GuildRecord guild) {
        var online = OnlinePlayerCollection.GetOnlinePlayers().Select(p => p.CharacterId).ToHashSet();
        var members = guild.Members
            .OrderByDescending(id => id == guild.LeaderCharId)
            .ThenByDescending(online.Contains)
            .Select(id => $"{NameOf(id)}{(id == guild.LeaderCharId ? " (leader)" : "")}{(online.Contains(id) ? " - online" : "")}");
        var since = DateTimeOffset.FromUnixTimeSeconds(guild.Created).LocalDateTime.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);

        return $"<center>{GuildName(guild)}</center>\nMade {since}. {guild.Members.Count} of {Guilds.MaxMembers} members:\n"
            + string.Join("\n", members)
            + "\n\n.g <words> talks to the guild. .guild house goes to the guild house.";
    }

    private void CreateGuild(Wizard me, string name) {
        if (name.Length == 0) {
            InformSenderClient("Use .guild create and a name from the game's guild name words, such as .guild create Valiant Crimson Dragons, or .guild create random. .guild names lists the words.");

            return;
        }

        if (!TryPickName(name, out var keys)) {
            return;
        }

        var failure = Guilds.Create(me.CharId, keys, out var guild);
        if (failure != GuildFailure.None) {
            InformSenderClient(Explain(failure));

            return;
        }

        SendGuildNameToSelf(guild.NameKeys);
        InformSenderClient($"You made the guild {GuildName(guild)}! Invite wizards with .guild invite <name>, and use .guild house to open your guild house. Your guild name shows over your head after you change areas.", true);
    }

    private void RenameGuild(Wizard me, string name) {
        if (name.Length == 0 || !TryPickName(name, out var keys)) {
            if (name.Length == 0) {
                InformSenderClient("Use .guild rename and the new name, or .guild rename random.");
            }

            return;
        }

        var failure = Guilds.Rename(me.CharId, keys);
        if (failure != GuildFailure.None) {
            InformSenderClient(Explain(failure));

            return;
        }

        var guild = Guilds.For(me.CharId);
        SendGuildNameToSelf(keys);
        TellGuild(guild, $"[Guild] {NameOf(me.CharId)} renamed the guild to {GuildName(guild)}.", skip: me.CharId);
        InformSenderClient($"Your guild is now {GuildName(guild)}.");
    }

    private bool TryPickName(string name, out uint keys) {
        keys = 0;
        if (name.Equals("random", StringComparison.OrdinalIgnoreCase)) {
            for (var tries = 0; tries < 20 && keys == 0; tries++) {
                keys = WizardNameBank.RandomGuildName(s_random);
            }
        } else if (!WizardNameBank.TryFindGuildNameKeys(name, out keys)) {
            InformSenderClient($"\"{name}\" is not made of the game's guild name words: a first word (like Valiant), a colour (like Crimson) and a last word (like Dragons). .guild names lists them.");

            return false;
        }

        if (keys == 0) {
            InformSenderClient("That name is too short. Use at least two words, such as Valiant Dragons.");

            return false;
        }

        return true;
    }

    private void ShowNames() {
        var (firsts, middles, lasts) = WizardNameBank.GuildNameWords();
        InformSenderClient("A guild name is up to three words, in this order:\n"
            + $"First word: {string.Join(", ", firsts)}\n\n"
            + $"Colour: {string.Join(", ", middles)}\n\n"
            + $"Last word: {string.Join(", ", lasts)}", true);
    }

    private void InviteToGuild(Wizard me, string who) {
        var other = FindOnline(who);
        if (other is null || other.CharId == me.CharId) {
            InformSenderClient("Use .guild invite and the name of a wizard who is online now.");

            return;
        }

        var failure = Guilds.Invite(me.CharId, other.CharId);
        if (failure != GuildFailure.None) {
            InformSenderClient(failure == GuildFailure.AlreadyInGuild ? $"{NameOf(other.CharId)} is already in a guild." : Explain(failure));

            return;
        }

        var guild = Guilds.For(me.CharId);
        TellWizard(other.CharId, $"{NameOf(me.CharId)} invited you to join the guild {GuildName(guild)}. Type .guild accept to join or .guild decline. The invite lasts 10 minutes.", true);
        InformSenderClient($"You invited {NameOf(other.CharId)} to the guild.");
    }

    private void AcceptInvite(Wizard me) {
        var failure = Guilds.Accept(me.CharId, out var guild);
        if (failure != GuildFailure.None) {
            InformSenderClient(Explain(failure));

            return;
        }

        SendGuildNameToSelf(guild.NameKeys);
        TellGuild(guild, $"[Guild] {NameOf(me.CharId)} joined the guild!", skip: me.CharId);
        InformSenderClient($"You joined {GuildName(guild)}! .g talks to the guild and .guild house takes you to the guild house. Your guild name shows over your head after you change areas.", true);
    }

    private void LeaveGuild(Wizard me) {
        var failure = Guilds.Leave(me.CharId, out var guild);
        if (failure != GuildFailure.None) {
            InformSenderClient(Explain(failure));

            return;
        }

        SendGuildNameToSelf(0);
        if (guild.Members.Count == 0) {
            InformSenderClient($"You left {GuildName(guild)}. You were the last one, so the guild is gone.");

            return;
        }

        TellGuild(guild, $"[Guild] {NameOf(me.CharId)} left the guild.", skip: me.CharId);
        InformSenderClient($"You left {GuildName(guild)}.");
    }

    private void KickFromGuild(Wizard me, string who) {
        var guild = Guilds.For(me.CharId);
        var target = FindMember(guild, who);
        if (target == 0) {
            InformSenderClient(guild is null ? Explain(GuildFailure.NotInGuild) : "Use .guild kick and the name of someone in your guild (.guild lists them).");

            return;
        }

        var failure = Guilds.Kick(me.CharId, target);
        if (failure != GuildFailure.None) {
            InformSenderClient(Explain(failure));

            return;
        }

        TellWizard(target, $"You were removed from the guild {GuildName(guild)}.", true);
        TellGuild(guild, $"[Guild] {NameOf(target)} was removed from the guild.", skip: target);
    }

    private void HandOverGuild(Wizard me, string who) {
        var guild = Guilds.For(me.CharId);
        var target = FindMember(guild, who);
        if (target == 0) {
            InformSenderClient(guild is null ? Explain(GuildFailure.NotInGuild) : "Use .guild leader and the name of someone in your guild (.guild lists them).");

            return;
        }

        var failure = Guilds.HandOver(me.CharId, target);
        if (failure != GuildFailure.None) {
            InformSenderClient(Explain(failure));

            return;
        }

        TellGuild(guild, $"[Guild] {NameOf(target)} leads the guild now. The guild house is now {NameOf(target)}'s guild house.", skip: 0);
    }

    private void DisbandGuild(Wizard me, string confirm) {
        if (!confirm.Equals("yes", StringComparison.OrdinalIgnoreCase)) {
            InformSenderClient("This ends the guild for everyone in it. Type .guild disband yes to do it.");

            return;
        }

        var failure = Guilds.Disband(me.CharId, out var guild);
        if (failure != GuildFailure.None) {
            InformSenderClient(Explain(failure));

            return;
        }

        SendGuildNameToSelf(0);
        TellGuild(guild, $"[Guild] {NameOf(me.CharId)} ended the guild {GuildName(guild)}.", skip: me.CharId);
        InformSenderClient($"You ended the guild {GuildName(guild)}.");
    }

    private void GoToGuildHouse(Wizard me) {
        var guild = Guilds.For(me.CharId);
        if (guild is null) {
            InformSenderClient(Explain(GuildFailure.NotInGuild));

            return;
        }

        if (!TryGetGuildHouseEntry(me, guild, out var entry, out var problem)) {
            InformSenderClient(problem);

            return;
        }

        Context.SessionActor.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = entry.Zone,
            DestinationLocation = HouseDeeds.EntryLocation,
            SendToClient = true,
            IsPrivate = true,
            OwnerCharId = entry.OwnerCharId,
            HouseEntry = entry,
        });
    }

    /// <summary>
    /// The guild house is the leader's: the leader gets the deed the first time (like Home), members visit the
    /// leader's grounds once the leader has opened them.
    /// </summary>
    private static bool TryGetGuildHouseEntry(Wizard me, GuildRecord guild, out HouseEntry entry, out string problem) {
        entry = null;
        problem = null;
        if (!HouseTemplates.TryGet(Guilds.HouseTemplateId, out var template)) {
            problem = "The guild house could not be found in the game files.";

            return false;
        }

        if (guild.LeaderCharId == me.CharId) {
            if (HouseDeeds.TryPrepareHouse(me, template.TemplateId, out entry, out var failure)) {
                return true;
            }

            problem = failure == HouseGrantFailure.BackpackFull
                ? "Make room in your backpack for the guild house deed first."
                : "Your guild house is not available right now. Please try again later.";

            return false;
        }

        var leader = WizardCollection.GetCharacterUnloaded(guild.LeaderCharId);
        if (leader?.HouseLots is null || !leader.HouseLots.TryGetValue(template.TemplateId, out var lotGid) || HouseCollection.Load(lotGid) is null) {
            problem = $"{NameOf(guild.LeaderCharId)} has not opened the guild house yet. Ask them to type .guild house once.";

            return false;
        }

        entry = HouseEntry.ForTeleport(lotGid, guild.LeaderCharId, template);

        return true;
    }

    private void SendGuildNameToSelf(uint keys)
        => Context.SessionActor.Tell(new WIZARD3_56_PROTOCOL.MSG_UPDATEGUILDNAME {
            PlayerGID = Context.CharacterObject?.m_globalID ?? 0,
            PartyName = keys,
            PartyNameLocale = Context.Character.PlayerNameBehavior?.LocaleId ?? 0,
        });

    private static int TellGuild(GuildRecord guild, string line, ulong skip) {
        if (guild is null) {
            return 0;
        }

        var sent = 0;
        foreach (var member in guild.Members.Where(id => id != skip)) {
            if (TellWizard(member, line, false)) {
                sent++;
            }
        }

        return sent;
    }

    private static bool TellWizard(ulong charId, string line, bool important) {
        var player = OnlinePlayerCollection.GetOnlinePlayer(charId);
        if (player?.ActorPath is null || AgentBridge.Actors is null) {
            return false;
        }

        AgentBridge.Actors.ActorSelection(player.ActorPath).Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE {
            Message = line,
            Modal = (byte) (important ? 1 : 0),
        }, ActorRefs.NoSender);

        return true;
    }

    private static ulong FindMember(GuildRecord guild, string who) {
        if (guild is null || string.IsNullOrWhiteSpace(who)) {
            return 0;
        }

        who = who.Trim();
        if (ulong.TryParse(who, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && guild.Members.Contains(id)) {
            return id;
        }

        return guild.Members.FirstOrDefault(m => string.Equals(NameOf(m), who, StringComparison.OrdinalIgnoreCase));
    }

    private static Wizard FindOnline(string who) {
        who = (who ?? "").Trim();
        if (who.Length == 0) {
            return null;
        }

        if (ulong.TryParse(who, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) {
            return OnlinePlayerCollection.GetOnlinePlayer(id) is null ? null : WizardCollection.GetCharacterUnloaded(id);
        }

        return OnlinePlayerCollection.GetOnlinePlayers()
            .Select(p => WizardCollection.GetCharacterUnloaded(p.CharacterId))
            .FirstOrDefault(w => string.Equals(w?.PlayerNameBehavior?.GetWizardName(), who, StringComparison.OrdinalIgnoreCase));
    }

    private static string NameOf(ulong charId)
        => WizardCollection.GetCharacterUnloaded(charId)?.PlayerNameBehavior?.GetWizardName() ?? $"character {charId}";

    internal static string GuildName(GuildRecord guild)
        => guild is null ? "your guild" : WizardNameBank.GetGuildEnglishName(guild.NameKeys);

    private static string Explain(GuildFailure failure) => failure switch {
        GuildFailure.Disabled => "Guilds are off ([Guilds] Enabled = false in Imlight.ini).",
        GuildFailure.AlreadyInGuild => "You are already in a guild. Leave it first with .guild leave.",
        GuildFailure.NotInGuild => "You are not in a guild. Make one with .guild create, or ask a guild member to .guild invite you.",
        GuildFailure.NotLeader => "Only the guild leader can do that.",
        GuildFailure.NameTaken => "Another guild already has that name. Try another, or .guild create random.",
        GuildFailure.Full => $"The guild is full ({Guilds.MaxMembers} members).",
        GuildFailure.NoInvite => "You have no guild invite, or it ran out. Ask a guild member to .guild invite you again.",
        GuildFailure.NotMember => "That wizard is not in your guild (.guild lists the members).",
        GuildFailure.LeaderMustHandOver => "You lead the guild. Give it to someone with .guild leader <name> first, or end it with .guild disband yes.",
        _ => "That did not work.",
    };

    private static string Help()
        => "Guild commands:\n.guild - your guild and its members\n.guild create <name> or .guild create random\n.guild names - the words a name can use\n"
           + ".guild invite <wizard>, .guild accept, .guild decline\n.guild leave, .guild kick <wizard>, .guild leader <wizard>\n"
           + ".guild rename <name>, .guild disband yes\n.guild house - go to the guild house\n.g <words> - talk to your guild";

}
