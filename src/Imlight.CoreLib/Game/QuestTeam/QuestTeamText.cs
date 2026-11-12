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

using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.QuestTeam;

/// <summary>Names in plain words for the Quest Team's logs (quest titles, areas, spells).</summary>
internal static class QuestTeamText {

    internal static string Title(string questName) {
        var quest = string.IsNullOrEmpty(questName) ? null : QuestTemplateCollection.GetQuestByName(questName);
        if (quest is null) {
            return questName ?? "";
        }

        var title = QuestGuide.QuestTitle(quest);

        return string.IsNullOrWhiteSpace(title) ? questName : $"\"{title}\"";
    }

    internal static string Zone(string zone) => string.IsNullOrEmpty(zone) ? "?" : ZoneNames.Shown(zone);

    internal static string SpellName(uint id) {
        if (CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate spell) {
            string key = spell.m_displayName;
            var shown = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);

            return string.IsNullOrWhiteSpace(shown) ? (string) spell.m_name ?? $"spell {id}" : shown;
        }

        return $"spell {id}";
    }

    internal static string Short(string text, int most) {
        text = (text ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();

        return text.Length <= most ? text : text[..most] + "...";
    }

}
