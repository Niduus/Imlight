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

namespace Imlight.CoreLib.Game.Quests;

/// <summary>
/// The NPC a "talk to" step names. A step names its NPC by object name (WC-ST01-NPC04) or by object name plus
/// "_Persona" (WC-ST01-NPC03_Persona). A few NPC objects spell the same name with underscores where every quest
/// spells it with dashes: Vassek Shadowspite is CL_STORM_NPC01 and five Stormriven quests ask for
/// CL-STORM-NPC01_Persona; Piper Melville is CL_SCI_NPC01 and Science Center asks for CL-SCI-NPC01_Persona.
/// Matched by name only, those steps never finished (Zoom, A Sunny Disposition, 2026-11-24). Here a dash and an
/// underscore count as the same letter when the game has no object of the step's own spelling.
/// </summary>
internal static class PersonaName {
    internal const string SUFFIX = "_Persona";

    /// <summary>The name without "_Persona", with every underscore read as a dash.</summary>
    internal static string Key(string name) {
        name = (name ?? "").Trim();
        if (name.EndsWith(SUFFIX, StringComparison.Ordinal)) {
            name = name[..^SUFFIX.Length];
        }

        return name.Replace('_', '-');
    }

    /// <summary>
    /// True when the step's persona names this NPC object: the same name, or the same name spelled the other way
    /// when the game has no object of the step's own spelling (a few stand-ins exist both ways, EM-ZANA-NPC01_C and
    /// EM-ZANA-NPC01-C; a step that names one of those never takes the other).
    /// </summary>
    internal static bool Names(string personaName, string objectName) {
        if (string.IsNullOrEmpty(personaName) || string.IsNullOrEmpty(objectName)) {
            return false;
        }

        if (string.Equals(personaName, objectName, StringComparison.Ordinal)
            || string.Equals(personaName, objectName + SUFFIX, StringComparison.Ordinal)) {
            return true;
        }

        var own = personaName.EndsWith(SUFFIX, StringComparison.Ordinal) ? personaName[..^SUFFIX.Length] : personaName;
        // Most names have no underscore at all: then only the exact name above can match (checked for every NPC and step).
        if (own.Length != objectName.Length || (!own.Contains('_') && !objectName.Contains('_'))) {
            return false;
        }

        return string.Equals(Key(personaName), Key(objectName), StringComparison.Ordinal)
            && !Imlight.CoreLib.Game.Madlibs.QuestMadlibs.HasObjectFile(own);
    }

    /// <summary>The same object name spelled the other way (dashes for underscores, or the reverse), or "".</summary>
    internal static string OtherSpelling(string objectName) {
        if (string.IsNullOrEmpty(objectName)) {
            return "";
        }

        var dashed = objectName.Replace('_', '-');
        return dashed != objectName ? dashed : objectName.Replace('-', '_') is var under && under != objectName ? under : "";
    }
}
