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
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Battlegrounds;

/*
 * The game's table of beast forms, Root.wad BattlegroundPolymorphs.xml: one entry per beast and school (70), each with
 * its five tier templates (m_polymorphList), its beast (m_polymorphCategory), its role, and what each tier costs to
 * unlock (m_ratingBadgeInfo: the badge it gives, a Lunari cost and reagents; the first is "Unlock Beast").
 * An entry's position is the index the game keeps the form's level under: a battleground event's ClassProjectPlayer
 * m_extendedData holds the levels four bits each, two to a byte, the even index in the low half (m_usesExtraData off;
 * ClassProjectPlayer read at WizardGraphicalClient 0x141b63190), and MSG_SetBGPolymorphLevel sets one. The game shows
 * the stored level plus one.
 */
internal static class BeastTable {

    private const string FILE = "BattlegroundPolymorphs.xml";

    private static List<BattlegroundPolymorphList> s_entries;

    internal static IReadOnlyList<BattlegroundPolymorphList> Entries {
        get {
            if (s_entries is not null) {
                return s_entries;
            }

            var table = RootArchiveLoader.GetFile<BattlegroundPolymorphs>(FILE);
            s_entries = [.. table?.m_polymorphTypeList ?? []];
            Logger.Information("Beastmoon | {0}: {1} beast forms.", Logger.Args(FILE, s_entries.Count));

            return s_entries;
        }
    }

    /// <summary>The entry index of a form (any of its tiers), or -1.</summary>
    internal static int IndexOf(string formFileName) {
        var form = Battlegrounds.Form(formFileName, out _);
        if (form is null) {
            return -1;
        }

        for (var i = 0; i < Entries.Count; i++) {
            if (Entries[i]?.m_polymorphList?.Contains(form.m_templateID) == true) {
                return i;
            }
        }

        return -1;
    }

    /// <summary>What reaching this tier costs (1 is "Unlock Beast"), or null.</summary>
    internal static RatingBadgeInfo TierCost(string formFileName, int tier) {
        var index = IndexOf(formFileName);
        var costs = index < 0 ? null : Entries[index]?.m_ratingBadgeInfo;

        return costs is null || tier < 1 || tier > costs.Count ? null : costs[tier - 1];
    }

    /// <summary>The tier-1 form file of each entry, by index (null where the server has no such form).</summary>
    internal static List<string> FirstTierForms()
        => [.. Entries.Select(e => e?.m_polymorphList is { Count: > 0 } list && Battlegrounds.FormById(list[0], out var file) is not null ? file : null)];

    /// <summary>The wizard's form levels as the game keeps them in a battleground event's m_extendedData.</summary>
    internal static List<byte> LevelBytes(ulong charId) {
        var forms = FirstTierForms();
        var bytes = new byte[(forms.Count + 1) / 2];
        for (var i = 0; i < forms.Count; i++) {
            var level = forms[i] is null ? 0 : Math.Clamp(BeastmoonProgress.LevelOf(charId, forms[i]), 0, 15);
            bytes[i / 2] |= (byte) (i % 2 == 0 ? level : level << 4);
        }

        return [.. bytes];
    }

}
