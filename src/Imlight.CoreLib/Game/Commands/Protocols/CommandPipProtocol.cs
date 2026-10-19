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
using System.Globalization;
using System.Linq;
using Akka.Actor;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// Battle pips: `.pip give power max`, `.pip give fire 2`, `.pip give all max`. They last for the battle.
/// </summary>
internal class CommandPipProtocol : CommandProtocol {

    internal override string Group { get; set; } = "pip";

    internal static readonly MagicSchool[] Schools = [
        MagicSchool.Fire, MagicSchool.Ice, MagicSchool.Storm, MagicSchool.Myth,
        MagicSchool.Life, MagicSchool.Death, MagicSchool.Balance,
    ];

    [Command("give")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void GiveCommand(string kind, string amount) {
        kind = kind.Trim().ToLowerInvariant();
        var known = kind is "light" or "power" or "shadow" or "all"
            || Schools.Any(school => school.ToString().Equals(kind, StringComparison.OrdinalIgnoreCase));
        int count;
        if (amount.Trim().Equals("max", StringComparison.OrdinalIgnoreCase)) {
            count = -1;
        }
        else if (!int.TryParse(amount, NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count < 1) {
            known = false;
        }

        if (!known) {
            InformSenderClient("Use .pip give, then light, power, shadow, all or a school for Archmastery pips (fire, ice, " +
                "storm, myth, life, death, balance), then a number or max. For example .pip give power max.");

            return;
        }

        Context.SessionActor.Tell(new COMBAT_106_PROTOCOL.MSG_CHEATGIVEPIPS { Kind = kind, Amount = count });
    }

}

/// <summary>
/// `.mastery fire` (or `.mastery all`): counts as mastering that school for the rest of the battle.
/// </summary>
internal class CommandMasteryProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("mastery")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void MasteryCommand(string school) {
        school = school.Trim();
        MagicSchool[] schools = school.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? CommandPipProtocol.Schools
            : [.. CommandPipProtocol.Schools.Where(s => s.ToString().Equals(school, StringComparison.OrdinalIgnoreCase))];
        if (schools.Length == 0) {
            InformSenderClient("Use .mastery and a school (fire, ice, storm, myth, life, death, balance) or all.");

            return;
        }

        Context.SessionActor.Tell(new COMBAT_106_PROTOCOL.MSG_CHEATMASTERY { Schools = [.. schools.Select(s => (uint) s)] });
    }

}
