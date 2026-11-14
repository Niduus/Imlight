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
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>Which of an NPC's options a server-run wizard clicks (Ultimus and the Quest Team).</summary>
internal static class BotOptions {

    // An NPC with more than one quest to give shows one option per quest (Assistant Danforth gives "Tool Time" and "Back to
    // the Archeologist"). The first option was always clicked, so Danforth offered "Tool Time" over and over, Ultimus left it
    // every time and never got the story quest (v0.10.852 log: stuck 45 times in the Chamber of Fire). Now the option that
    // names the quest it wants is clicked; when none names it, each click tries the next option.
    internal static (string Service, uint Index, string Display) Pick(List<(string Service, uint Index, string Display)> matching, string questName, int clicked) {
        if (matching.Count <= 1) {
            return matching.FirstOrDefault();
        }

        var quest = string.IsNullOrEmpty(questName) ? null : QuestTemplateCollection.GetQuestByName(questName);
        if (quest is not null) {
            string title = quest.m_questTitle;
            string name = quest.m_questName;
            var english = string.IsNullOrEmpty(title) ? "" : Locale.GetEnglishName(title);
            foreach (var option in matching) {
                var display = option.Display ?? "";
                if (display.Length == 0) {
                    continue;
                }

                if (string.Equals(display, title, StringComparison.OrdinalIgnoreCase) || string.Equals(display, name, StringComparison.OrdinalIgnoreCase)
                    || display.Contains(name, StringComparison.OrdinalIgnoreCase)
                    || english.Length > 0 && (string.Equals(display, english, StringComparison.OrdinalIgnoreCase)
                                              || string.Equals(Locale.GetEnglishName(display), english, StringComparison.OrdinalIgnoreCase))) {
                    return option;
                }
            }
        }

        return matching[clicked % matching.Count];
    }

}
