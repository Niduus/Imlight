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

namespace Imlight.CoreLib.Game.QuestTeam;

/// <summary>
/// The four wizards of the Quest Team (Niduus, 2026-11-12: "a party of Ice, Life, Death and Storm"), always in that
/// order: each on an account of its own (QuestTeam-Ice and so on, no password anyone knows), made at level 1 the way the
/// character maker makes a wizard. They keep their progress; Ahziael's Restart makes four new ones.
/// </summary>
internal static class QuestTeamRoster {

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

    /// <summary>The roles, in the team's order: who goes first.</summary>
    internal static readonly string[] Roles = ["Ice", "Life", "Death", "Storm"];

    internal static MagicSchool SchoolOf(string role) => role switch {
        "Ice" => MagicSchool.Ice,
        "Life" => MagicSchool.Life,
        "Death" => MagicSchool.Death,
        _ => MagicSchool.Storm,
    };

    private const string USERNAME_PREFIX = "QuestTeam-";

    private static string FilePath => Path.Combine(QuestTeamWire.Folder, QuestTeamWire.TEAM_FILE);

    /// <summary>The team's four wizards, made when they don't exist yet; null when they could not be made.</summary>
    internal static List<Member> Load() {
        var saved = Read();
        var members = new List<Member>();
        foreach (var role in Roles) {
            var member = saved?.Members.FirstOrDefault(m => m.Role == role);
            if (member is not null && AccountCollection.GetAccountForCharacterList(member.AccountId) is { } known
                && known.CharacterIds.Contains(member.CharId)) {
                MakeHuman(known.Characters?.FirstOrDefault(c => c.CharId == member.CharId));
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

        return members;
    }

    /// <summary>A team wizard made before v0.10.819 had no race, so the game showed pet names over it: made human.</summary>
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
            Logger.Information("Quest Team: {0} is made human, so its name shows right.", Logger.Args(wizard.PlayerNameBehavior?.GetWizardName()));
        }
        catch (Exception e) {
            Logger.Warning("Quest Team: could not fix a wizard's race: {0}", Logger.Args(e.Message));
        }
    }

    /// <summary>Four new level 1 wizards in place of the old ones (Ahziael's Restart). The team must be logged out.</summary>
    internal static List<Member> Restart() {
        var saved = Read();
        foreach (var role in Roles) {
            DeleteAccount(USERNAME_PREFIX + role);
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
        QuestTeamLink.Note("Team", "Restart: four new level 1 wizards were made.");

        return members;
    }

    /// <summary>Whether a character is one of the Quest Team's.</summary>
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
            Logger.Warning("Quest Team: could not delete the old account {0}: {1}", Logger.Args(username, e.Message));
        }
    }

    private static Member Make(string role) {
        var username = USERNAME_PREFIX + role;
        try {
            var account = AccountCollection.GetAccountForCharacterList(username);
            if (account is null) {
                var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
                AccountCollection.CreateAccount(new Account(username, "", DatabaseUtilities.CreateHashedPassword(secret)) {
                    AuthLevel = AuthLevel.None,
                });
                account = AccountCollection.GetAccountForCharacterList(username);
            }

            if (account is null) {
                Logger.Error("Quest Team: the account {0} could not be made.", Logger.Args(username));

                return null;
            }

            // A wizard the team made before (a crash before team.json was written) is used again.
            var existing = account.Characters?.FirstOrDefault();
            if (existing is not null) {
                MakeHuman(existing);
                return new Member { Role = role, Username = username, AccountId = account.AccountId, CharId = existing.CharId,
                    Name = existing.PlayerNameBehavior?.GetWizardName() ?? role };
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
                Logger.Error("Quest Team: the {0} wizard could not be added to {1}.", Logger.Args(role, username));

                return null;
            }

            var name = wizard.PlayerNameBehavior?.GetWizardName() ?? role;
            Logger.Information("Quest Team: made the {0} wizard {1} (account {2}).", Logger.Args(role, name, username));

            return new Member { Role = role, Username = username, AccountId = account.AccountId, CharId = wizard.CharId, Name = name };
        }
        catch (Exception e) {
            Logger.Error("Quest Team: could not make the {0} wizard: {1}", Logger.Args(role, e));

            return null;
        }
    }

    private static Saved Read() {
        try {
            return File.Exists(FilePath) ? QuestTeamWire.Read<Saved>(File.ReadAllText(FilePath)) : null;
        }
        catch (Exception) {
            return null;
        }
    }

    private static void Write(Saved saved) {
        try {
            Directory.CreateDirectory(QuestTeamWire.Folder);
            File.WriteAllText(FilePath, QuestTeamWire.Write(saved));
        }
        catch (Exception e) {
            Logger.Warning("Quest Team: could not write {0}: {1}", Logger.Args(FilePath, e.Message));
        }
    }

}
