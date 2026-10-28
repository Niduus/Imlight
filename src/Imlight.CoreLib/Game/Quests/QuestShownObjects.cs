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
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.Quests;

/// <summary>
/// The zone tags that some quest brings out with a dynamod (a quest start, end, step start or step end that adds a mod
/// showing the object or putting it in a state): such an object waits for that quest when the game placed it hidden.
/// Worked out once for each set of quest changes.
/// </summary>
internal static class QuestShownObjects {

    private static Tuple<int, HashSet<string>> s_known = Tuple.Create(-1, new HashSet<string>());
    private static readonly object s_lock = new();

    public static bool Contains(string zoneTag)
        => !string.IsNullOrEmpty(zoneTag) && Tags().Contains(zoneTag);

    private static HashSet<string> Tags() {
        var known = s_known;
        if (known.Item1 == QuestOptions.Version) {
            return known.Item2;
        }

        lock (s_lock) {
            known = s_known;
            var version = QuestOptions.Version;
            if (known.Item1 == version) {
                return known.Item2;
            }

            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var quest in SpiralDB.QuestTemplates) {
                if (quest is null) {
                    continue;
                }

                Collect(quest.m_startResults, tags);
                Collect(quest.m_endResults, tags);
                foreach (var goal in quest.m_goals ?? []) {
                    Collect(goal?.m_activateResults, tags);
                    Collect(goal?.m_completeResults, tags);
                }
            }

            s_known = Tuple.Create(version, tags);

            return tags;
        }
    }

    private static void Collect(ResultList results, HashSet<string> tags) {
        foreach (var result in results?.m_results ?? []) {
            switch (result) {
                case ResAddDynaMod mod when !string.IsNullOrEmpty(mod.m_dynaModClientTag) && Shows(mod):
                    tags.Add(mod.m_dynaModClientTag);
                    break;
                case ResIfThenElse branch:
                    Collect(branch.m_thenResults, tags);
                    Collect(branch.m_elseResults, tags);
                    break;
            }
        }
    }

    // A mod that shows its object, or one that puts it in a state other than "Off" (an empty state hides, as the
    // RenderComponent reads it).
    private static bool Shows(ResAddDynaMod mod) {
        var state = mod.m_dynaModState.ToString();

        return mod.m_bShowsObject
            || !string.IsNullOrEmpty(state) && !string.Equals(state, "Off", StringComparison.OrdinalIgnoreCase);
    }

}
