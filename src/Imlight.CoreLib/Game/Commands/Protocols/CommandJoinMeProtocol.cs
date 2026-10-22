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
using System.Linq;
using Akka.Actor;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.joinme Gobbler` (Niduus, 2026-10-20): in battle, a creature joins on the enemy side. A number at the end adds
/// that many, while the enemy side has room.
/// Some bosses share one name across worlds; <see cref="s_versions"/> picks the one Niduus means (2026-10-21:
/// `.joinme Malistaire the Undying` is the Castle Darkmoor boss, `.joinme Malistaire the Undying Azteca` the Azteca one).
/// A custom of the app's Customs tab (Customs.cs) comes in by its number after the creature it is based on (Niduus,
/// 2026-10-22): `.joinme Malistaire the Undying 1` or `.joinme 1336035 1` (or `... custom 1`). A number that is no
/// custom still adds that many, as before; `x2` at the end always does (`.joinme Gobbler x2`,
/// `.joinme Malistaire the Undying 1 x2`).
/// </summary>
internal class CommandJoinMeProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    // Name (with an optional world word) to the template ID of that version.
    private static readonly Dictionary<string, ulong> s_versions = new(StringComparer.OrdinalIgnoreCase) {
        ["Malistaire the Undying"] = 1336035,                 // G14-DM-Malistaire_Boss, Castle Darkmoor
        ["Malistaire the Undying Darkmoor"] = 1336035,
        ["Malistaire the Undying Castle Darkmoor"] = 1336035,
        ["Malistaire the Undying Azteca"] = 684531,           // AZ-Human-LichMalistaire-A
    };

    [Command("joinme")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void JoinMeCommand([Remainder] string creature) {
        if (!IdCatalog.IsReady) {
            InformSenderClient("The ID lookup is still being built, or is turned off in the configuration. Try again shortly.");

            return;
        }

        var name = (creature ?? "").Trim();
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var times = 1;
        // "x2" at the end: that many.
        if (words.Count > 1 && words[^1].Length > 1 && char.ToLowerInvariant(words[^1][0]) == 'x'
            && int.TryParse(words[^1][1..], out var many) && many > 0) {
            times = Math.Min(many, 4);
            words.RemoveAt(words.Count - 1);
        }

        // "custom 1" at the end: that custom.
        var customId = 0;
        var customAsked = false;
        var byNumber = false;
        if (words.Count > 2 && words[^2].Equals("custom", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(words[^1], out var asked) && asked > 0) {
            customId = asked;
            customAsked = true;
            words.RemoveRange(words.Count - 2, 2);
        }

        name = string.Join(' ', words);
        IdCatalogEntry match = null;
        // A number at the end: a custom when there is one of that number (checked first, as the search would also
        // take "1336035 1" for 1336035 itself), else part of the name, else how many.
        if (!customAsked && words.Count > 1 && int.TryParse(words[^1], out var number) && number > 0) {
            if (Customs.Get(number) is not null && Find(string.Join(' ', words[..^1])) is { } based) {
                match = based;
                customId = number;
                customAsked = true;
                byNumber = true;
            }
            else if ((match = Find(name)) is null) {
                match = Find(string.Join(' ', words[..^1]));
                times = Math.Min(number, 4);
            }
        }
        else {
            match = Find(name);
        }

        if (match is null) {
            InformSenderClient(name.Length == 0
                ? "Use .joinme and a creature's name or ID, such as .joinme Gobbler, or a custom: .joinme Malistaire the Undying 1."
                : $"No creature called \"{name}\" was found. Try .lookup mob {name} to see the names.");

            return;
        }

        var shown = match.Name;
        if (customAsked) {
            var custom = Customs.Get(customId);
            if (custom is null) {
                InformSenderClient($"There is no custom {customId}. The app's Customs tab lists them.");

                return;
            }

            if (custom.BaseTemplateID != match.Id) {
                var customName = custom.Name.Length > 0 ? custom.Name : $"Custom {customId}";
                InformSenderClient($"Custom {customId} ({customName}) is based on {BaseName(custom.BaseTemplateID)}, not on {match.Name} ({match.Id}). " +
                                   $"Use .joinme {custom.BaseTemplateID} {customId}." +
                                   (byNumber ? $" To add several {match.Name}, use .joinme {match.Name} x{customId}." : ""));

                return;
            }

            shown = custom.Name.Length > 0 ? $"{custom.Name} (custom {customId})" : $"{match.Name} (custom {customId})";
        }

        for (var i = 0; i < times; i++) {
            Context.SessionActor.Tell(new COMBAT_106_PROTOCOL.MSG_CHEATJOINME {
                CreatureTemplateId = (uint) match.Id,
                CreatureName = shown,
                CustomId = customId,
            });
        }
    }

    private static string BaseName(ulong templateId)
        => IdCatalog.Search(IdCatalogKind.Creature, templateId.ToString(), 1).FirstOrDefault() is { } entry
            ? $"{entry.Name} ({templateId})" : templateId.ToString();

    private static IdCatalogEntry Find(string name) {
        if (name.Length == 0) {
            return null;
        }

        var query = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (s_versions.TryGetValue(query, out var id)) {
            query = id.ToString();
        }

        return IdCatalog.Search(IdCatalogKind.Creature, query, 1).FirstOrDefault();
    }

}
