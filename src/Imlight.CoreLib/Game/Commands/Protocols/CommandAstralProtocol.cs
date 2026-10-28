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

using Imlight.CoreLib.Auth;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.astral` says your astral school and its perks; `.astral sun` (moon, star, shadow) picks one, `.astral off` takes
/// it away. See <see cref="AstralSchools"/>.
/// </summary>
internal class CommandAstralProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("astral")]
    [Alias("astralschool")]
    private void AstralCommand([Remainder] string school = "") {
        var stats = Context.Character.GameStats;
        var picked = (school ?? "").Trim();
        if (picked.Length == 0) {
            var current = AstralSchools.Find(stats.m_astralSchool);
            InformSenderClient(current is null
                ? "You have no astral school. Type .astral sun, .astral moon, .astral star or .astral shadow to pick one."
                : $"Your astral school is {current}: {AstralSchools.Describe(AstralSchools.PerksOf(current))} in battle. Type .astral off to take it away.");

            return;
        }

        // Every player can always take theirs away, whatever [AstralSchools] says.
        if (picked.ToLowerInvariant() is "off" or "none" or "remove" or "clear") {
            Context.Character.SetAstralSchool(null);
            InformSenderClient(AstralSchools.ShowAsSchoolIcon
                ? "You no longer have an astral school. Log out and back in to see your own school's icon again."
                : "You no longer have an astral school.");

            return;
        }

        if (!AstralSchools.Enabled) {
            InformSenderClient("Astral schools are turned off on this server.");

            return;
        }

        if (!AstralSchools.PlayersCanPick && !AuthorityRequester.RequestAuthority(AuthLevel.QualityAssurance, Context.Account, "Command astral")) {
            InformSenderClient("Only staff can pick an astral school on this server. Type .astral off to take yours away.");

            return;
        }

        if (AstralSchools.Find(picked) is not { } name) {
            InformSenderClient("Pick Sun, Moon, Star or Shadow, such as .astral sun. Type .astral off to take it away.");

            return;
        }

        Context.Character.SetAstralSchool(name);
        InformSenderClient($"You are now a {name} wizard: {AstralSchools.Describe(AstralSchools.PerksOf(name))} in battle, "
            + "on top of your own school. It starts with your next battle."
            + (AstralSchools.ShowAsSchoolIcon ? " Log out and back in to see its icon by your name." : ""));
    }

}
