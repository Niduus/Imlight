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
using System.Reflection;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Badges;
using Imlight.CoreLib.Game.World;

namespace Imlight.CoreLib.Game.BotPlayers;

/// <summary>
/// What a bot looks like (Niduus, 2026-11-12: "random looks", "random badges that are easy to unlock"): its own face,
/// hair and colors from the character maker's lists, and the title of a badge a wizard of its level has most likely
/// earned: one of the worlds it has been through, or an early monster badge.
/// </summary>
internal static class BotLooks {

    // The badge list's world filters, and the level a wizard usually reaches each world.
    private static readonly (string Filter, int Level)[] s_worlds = [
        ("WizardCity", 1), ("Krokotopia", 10), ("Marleybone", 20), ("MooShu", 25), ("Dragonspyre", 35), ("Celestia", 50),
        ("Zafaria", 60), ("Avalon", 70), ("Azteca", 80), ("Khrysalis", 90), ("Polaris", 100), ("Mirage", 110), ("Empyrea", 120),
        ("Karamelle", 140), ("Lemuria", 150), ("Novus", 160), ("Wallaru", 170), ("Selenopolis", 180),
    ];

    /// <summary>A copy of a player's avatar with a look of its own, for a bot of the given gender.</summary>
    internal static WizardCharacterBehavior NewAvatar(WizardCharacterBehavior from, eGender gender, Random random) {
        var avatar = Copy(from);
        avatar.m_eGender = gender;
        if (MagicMirror.RandomLook(gender, random) is { } look) {
            MagicMirror.WriteToAvatar(avatar, look);
        }

        return avatar;
    }

    /// <summary>
    /// A badge title for a bot of this level, or null: a world badge of a world it has finished (the world before the one
    /// its level is in) or of that world, picking the small ones (few kills, no long collections).
    /// </summary>
    internal static (uint Id, string Title)? Badge(int level, Random random) {
        var catalog = BadgeCatalog.Current;
        if (!catalog.IsReady) {
            return null;
        }

        var worlds = s_worlds.Where(w => w.Level <= level).Select(w => w.Filter).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var choices = catalog.Records
            .Where(b => b is { ShowTitle: true, Retired: false, ExtraCredit: false } && !string.IsNullOrWhiteSpace(b.Title)
                        && worlds.Contains(b.Filter ?? "") && b.Max <= 50)
            .ToList();
        if (choices.Count == 0) {
            return null;
        }

        var badge = choices[random.Next(choices.Count)];

        return (badge.Id, badge.Title);
    }

    // A field-by-field copy, so a bot's look never changes the player's.
    private static WizardCharacterBehavior Copy(WizardCharacterBehavior from) {
        var copy = new WizardCharacterBehavior();
        foreach (var property in typeof(WizardCharacterBehavior).GetProperties(BindingFlags.Instance | BindingFlags.Public)) {
            if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0) {
                property.SetValue(copy, property.GetValue(from));
            }
        }

        foreach (var field in typeof(WizardCharacterBehavior).GetFields(BindingFlags.Instance | BindingFlags.Public)) {
            field.SetValue(copy, field.GetValue(from));
        }

        return copy;
    }

}
