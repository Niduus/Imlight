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
using System.Security.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.BotPlayers;
using Imlight.CoreLib.Game.Randomization;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Ultimus's wizards (Niduus, 2026-11-12: "All Ultimus wizards are to be named Ultimus with it's class set as Balance").
/// One for now, the solo quester; the list is kept so more can be added later (each is a "seat" in <see cref="Roles"/>).
/// Each is on an account of its own (Ultimus-Balance, no password anyone knows), made at level 1 the way the character
/// maker makes a wizard, then named Ultimus. It keeps its progress; Ahziael's Restart makes a new one.
/// </summary>
internal static class UltimusRoster {

    /// <summary>One wizard of the team.</summary>
    internal sealed class Member {
        public string Role { get; set; } = "";
        public string Username { get; set; } = "";
        public ulong AccountId { get; set; }
        public ulong CharId { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class Saved {
        public int Generation { get; set; }
        public DateTime Made { get; set; }
        public List<Member> Members { get; set; } = [];
    }

    /// <summary>The seats, by school: one Balance wizard, the solo quester.</summary>
    internal static readonly string[] Roles = ["Balance"];

    /// <summary>Every Ultimus wizard's name.</summary>
    internal const string NAME = "Ultimus";

    internal static MagicSchool SchoolOf(string role) => MagicSchool.Balance;

    private const string USERNAME_PREFIX = "Ultimus-";

    private static string FilePath => Path.Combine(UltimusWire.Folder, UltimusWire.TEAM_FILE);

    /// <summary>The team's four wizards, made when they don't exist yet; null when they could not be made.</summary>
    internal static List<Member> Load() {
        var saved = Read();
        var members = new List<Member>();
        foreach (var role in Roles) {
            var member = saved?.Members.FirstOrDefault(m => m.Role == role);
            if (member is not null && AccountCollection.GetAccountForCharacterList(member.AccountId) is { } known
                && known.CharacterIds.Contains(member.CharId)) {
                MakeHuman(known.Characters?.FirstOrDefault(c => c.CharId == member.CharId));
                NameIt(known.Characters?.FirstOrDefault(c => c.CharId == member.CharId));
            }

            if (member is null || AccountCollection.GetAccountForCharacterList(member.AccountId) is not { } account
                || !account.CharacterIds.Contains(member.CharId)) {
                member = Make(role);
                if (member is null) {
                    return null;
                }
            }

            members.Add(member);
        }

        Write(new Saved { Generation = saved?.Generation ?? 1, Made = saved?.Made ?? DateTime.UtcNow, Members = members });
        MakeFriends(members);

        return members;
    }

    /// <summary>The wizard is named Ultimus (a custom name over its name keys), whatever the name lists gave it.</summary>
    internal static void NameIt(Wizard wizard) {
        try {
            if (wizard?.PlayerNameBehavior is not { } names || names.NameOverride == NAME) {
                return;
            }

            wizard.Rename(names.NameIndices, NAME);
            Logger.Information("Ultimus: the wizard {0} is named {1}.", Logger.Args(wizard.CharId, NAME));
        }
        catch (Exception e) {
            Logger.Warning("Ultimus: could not name the wizard: {0}", Logger.Args(e.Message));
        }
    }

    /// <summary>Ultimus wizards are friends with each other (kept from the Quest Team, for when there is more than one).</summary>
    internal static void MakeFriends(List<Member> members) {
        foreach (var member in members) {
            try {
                if (WizardCollection.GetCharacter(member.CharId) is not { FriendsBehavior: not null } wizard) {
                    continue;
                }

                var added = false;
                foreach (var other in members.Where(o => o.CharId != member.CharId)) {
                    if (!wizard.FriendsBehavior.HasRelationshipWith(other.CharId)) {
                        added |= wizard.AddOrRepairRelationship(other.CharId);
                    }
                }

                if (added) {
                    WizardCollection.UpdateCharacterFriendBehavior(wizard);
                }
            }
            catch (Exception ex) {
                Logger.Warning("Ultimus: could not friend {0} with the others: {1}", Logger.Args(member.Name, ex.Message));
            }
        }
    }

    /// <summary>A wizard with no race shows pet names over it (found by the Quest Team): made human.</summary>
    internal static void MakeHuman(Wizard wizard) {
        try {
            if (wizard?.WizardAvatar is not { } avatar || (avatar.m_eRace == eRace.Human && wizard.PlayerNameBehavior?.Race == eRace.Human)) {
                return;
            }

            avatar.m_eRace = eRace.Human;
            if (wizard.PlayerNameBehavior is { } name) {
                name.Race = eRace.Human;
            }

            WizardCollection.UpdateCharacterRace(wizard);
            Logger.Information("Ultimus: {0} is made human, so its name shows right.", Logger.Args(wizard.PlayerNameBehavior?.GetWizardName()));
        }
        catch (Exception e) {
            Logger.Warning("Ultimus: could not fix a wizard's race: {0}", Logger.Args(e.Message));
        }
    }

    /// <summary>A new level 1 Ultimus in place of the old one (Ahziael's Restart). It must be logged out.</summary>
    internal static List<Member> Restart() {
        var saved = Read();
        // The old wizards were just logged out: a moment for their last saves before they are deleted.
        System.Threading.Thread.Sleep(3000);
        foreach (var role in Roles) {
            DeleteAccount(USERNAME_PREFIX + role);
        }

        // The database finds accounts by name through an index that catches up a moment after a delete (the Quest Team's
        // Restart failed on it): wait until the old names are gone.
        foreach (var role in Roles) {
            var username = USERNAME_PREFIX + role;
            if (!WaitFor(() => AccountCollection.GetAccountForCharacterList(username) is null)) {
                Logger.Warning("Ultimus: the old account {0} still shows after 20 seconds; making the new one anyway.", Logger.Args(username));
            }
        }

        var members = new List<Member>();
        foreach (var role in Roles) {
            var member = Make(role);
            if (member is null) {
                return null;
            }

            members.Add(member);
        }

        Write(new Saved { Generation = (saved?.Generation ?? 0) + 1, Made = DateTime.UtcNow, Members = members });
        UltimusLink.Note("Ultimus", "Restart: a new level 1 Ultimus was made.");

        return members;
    }

    /// <summary>Whether a character is an Ultimus wizard.</summary>
    internal static bool IsMember(ulong charId) => s_charIds.Contains(charId);

    private static volatile HashSet<ulong> s_charIds = [];

    internal static void Remember(IEnumerable<Member> members) => s_charIds = [.. members.Select(m => m.CharId)];

    private static void DeleteAccount(string username) {
        try {
            if (AccountCollection.GetAccountForCharacterList(username) is not null) {
                AccountCollection.DeleteAccount(username);
            }
        }
        catch (Exception e) {
            Logger.Warning("Ultimus: could not delete the old account {0}: {1}", Logger.Args(username, e.Message));
        }
    }

    private static Member Make(string role) {
        var username = USERNAME_PREFIX + role;
        try {
            var account = AccountCollection.GetAccountForCharacterList(username);
            if (account is null) {
                var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
                if (!AccountCollection.CreateAccount(new Account(username, "", DatabaseUtilities.CreateHashedPassword(secret)) {
                        AuthLevel = AuthLevel.None,
                    })) {
                    Logger.Warning("Ultimus: the database still had an account named {0}; waiting for it.", Logger.Args(username));
                }

                // The new account shows in the name index a moment after it is saved.
                WaitFor(() => AccountCollection.GetAccountForCharacterList(username) is not null);
                account = AccountCollection.GetAccountForCharacterList(username);
            }

            if (account is null) {
                Logger.Error("Ultimus: the account {0} could not be made.", Logger.Args(username));

                return null;
            }

            // A wizard made before (a crash before team.json was written) is used again.
            var existing = account.Characters?.FirstOrDefault();
            if (existing is not null) {
                MakeHuman(existing);
                NameIt(existing);
                return new Member { Role = role, Username = username, AccountId = account.AccountId, CharId = existing.CharId,
                    Name = NAME };
            }

            var random = new Random(RandomNumberGenerator.GetInt32(int.MaxValue));
            var gender = random.Next(2) == 0 ? eGender.Male : eGender.Female;
            var (firsts, middles, lasts) = WizardNameBank.WizardNameListSizes(gender);
            var first = random.Next(Math.Clamp(firsts, 1, 256));
            var middle = middles > 1 ? random.Next(1, Math.Min(middles, 256)) : 0;
            var last = lasts > 1 ? random.Next(1, Math.Min(lasts, 256)) : 0;
            var nameKeys = (uint) ((first << 16) | (middle << 8) | last);

            var avatar = BotLooks.NewAvatar(new WizardCharacterBehavior(), gender, random);
            // A new avatar has no race; the game reads a wizard's name keys from the human name lists only for a human
            // (without it the name plates showed pet names: "Baby Millie", "Sparky").
            avatar.m_eRace = eRace.Human;
            var wizard = new Wizard(SchoolOf(role), avatar, nameKeys);
            Randomizer.GiveStartingSpells(wizard);
            if (!account.AddCharacter(wizard)) {
                Logger.Error("Ultimus: the {0} wizard could not be added to {1}.", Logger.Args(role, username));

                return null;
            }

            NameIt(wizard);
            var name = NAME;
            Logger.Information("Ultimus: made the {0} wizard {1} (account {2}).", Logger.Args(role, name, username));

            return new Member { Role = role, Username = username, AccountId = account.AccountId, CharId = wizard.CharId, Name = name };
        }
        catch (Exception e) {
            Logger.Error("Ultimus: could not make the {0} wizard: {1}", Logger.Args(role, e));

            return null;
        }
    }

    // Up to 20 seconds for the database to show a change.
    private static bool WaitFor(Func<bool> done) {
        for (var i = 0; i < 40; i++) {
            try {
                if (done()) {
                    return true;
                }
            }
            catch (Exception e) {
                Logger.Debug("Ultimus: waiting for the database: {0}", Logger.Args(e.Message));
            }

            System.Threading.Thread.Sleep(500);
        }

        return false;
    }

    private static Saved Read() {
        try {
            return File.Exists(FilePath) ? UltimusWire.Read<Saved>(File.ReadAllText(FilePath)) : null;
        }
        catch (Exception) {
            return null;
        }
    }

    private static void Write(Saved saved) {
        try {
            Directory.CreateDirectory(UltimusWire.Folder);
            File.WriteAllText(FilePath, UltimusWire.Write(saved));
        }
        catch (Exception e) {
            Logger.Warning("Ultimus: could not write {0}: {1}", Logger.Args(FilePath, e.Message));
        }
    }

}
