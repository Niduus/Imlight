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
using System.Text.Json.Serialization;

namespace Imlight.CoreLib.WizardData.Models.Player;

public class DynamodSet {

    internal ulong CharId { get; set; }
    internal List<Dynamod> Dynamods { get; set; }

    [JsonConstructor]
    public DynamodSet() { }

    // ctor
    internal DynamodSet(ulong charId) {
        CharId = charId;
        Dynamods = [];
    }

    internal bool AddDynamod(Dynamod dynamod) {
        if (Dynamods == null) {
            Dynamods = [dynamod];

            return true;
        }

        // A mod for the same object in an overlapping place (the same zone, "any zone", or a zone path's start) replaces the
        // old one, and the new one goes last. The game gets every mod with no zone and applies them in order, so two mods for
        // one object would fight (a quest's mod and an older "any zone" one for the same tag).
        Dynamods.RemoveAll(existing => existing is not null
            && string.Equals(existing.ClientTag, dynamod.ClientTag, StringComparison.OrdinalIgnoreCase)
            && ZonesOverlap(existing.ZoneName, dynamod.ZoneName));
        Dynamods.Add(dynamod);

        return true;
    }

    /// <summary>Whether a mod for <paramref name="a"/> and one for <paramref name="b"/> can name the same object: either names
    /// no zone, or one zone path starts with the other ("WizardCity/Interiors").</summary>
    internal static bool ZonesOverlap(string a, string b)
        => string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)
            || a.StartsWith(b, StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);

    internal bool RemoveDynamod(string clientTag) {
        if (Dynamods == null) {
            Dynamods = [];

            return false;
        }

        foreach (var existingDynamod in Dynamods) {
            if (existingDynamod is null) {
                continue;
            }

            if (existingDynamod.ClientTag.Equals(clientTag, StringComparison.OrdinalIgnoreCase)) {
                Dynamods.Remove(existingDynamod);

                return true;
            }
        }

        return false;
    }
    
}

[Serializable]
public class Dynamod {

    internal string ZoneName { get; set; }
    internal string ClientTag { get; set; }
    internal string ModState { get; set; }

}
