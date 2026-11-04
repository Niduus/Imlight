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

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Imlight.CoreLib.Game.World;

/// <summary>
/// A zone with the name players know it by (Niduus, 2026-11-04: "locations in the UI with display name and code name,
/// with .tp able to read either"). Code is the game's zone path (WizardCity/WC_Streets/WC_Unicorn), Name the place's
/// name (Unicorn Way), Type S world area / D instance / T starting area, Flags r retired / d test zone of the game's
/// makers (spiraldb-extra/Zones/zone-details.json, from Wizard404).
/// </summary>
internal sealed record ZoneName(string Code, string Name, string Type = "", string Flags = "") {

    /// <summary>The last part of the code name, such as WC_Unicorn.</summary>
    internal string Tail => Code[(Code.LastIndexOf('/') + 1)..];

    /// <summary>Kept out of the game for players: old (retired) zones and the game makers' test zones.</summary>
    internal bool OldOrTest => Flags.Length > 0 || Code.StartsWith("Test/", StringComparison.OrdinalIgnoreCase);

}

/// <summary>What a typed place name matched: the zone, how, and the other zones it could also mean.</summary>
internal sealed record ZoneMatch(ZoneName Zone, string How, IReadOnlyList<ZoneName> Others);

/// <summary>
/// Finds the zone a typed place means, by its code name or by its name, with small typos forgiven. Shared by .tp on the
/// server and the Manager's Locations tab, so the tab can say exactly where `.tp Unicorn Way` goes.
/// </summary>
internal static class ZoneNameMatch {

    /// <summary>A name boiled down for matching: small letters and digits only, a leading "the" left out.</summary>
    internal static string Key(string text) {
        var key = new StringBuilder(text.Length);
        foreach (var letter in text) {
            if (char.IsLetterOrDigit(letter)) {
                key.Append(char.ToLowerInvariant(letter));
            }
        }

        var boiled = key.ToString();

        return boiled.Length > 3 && text.TrimStart().StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? boiled[3..] : boiled;
    }

    /// <summary>
    /// The zone a typed text means, or null when nothing is close. In order: the whole code name, the last part of the
    /// code name (WC_Unicorn), a world's name (its hub), the exact place name (Unicorn Way), a name or code name with the text inside it, then a
    /// close spelling. Where more zones fit equally, the main world area wins over houses, instances and old zones.
    /// </summary>
    internal static ZoneMatch? Find(IReadOnlyList<ZoneName> zones, string? typed) {
        var text = (typed ?? "").Trim().Trim('"', '\'');
        if (text.Length == 0 || zones.Count == 0) {
            return null;
        }

        var key = Key(text);
        var withSlash = text.Replace('\\', '/');

        var exact = zones.Where(z => z.Code.Equals(withSlash, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) {
            return Pick(exact, key, "code name");
        }

        if (key.Length == 0) {
            return null;
        }

        var tail = zones.Where(z => Key(z.Tail) == key).ToList();
        if (tail.Count > 0) {
            return Pick(tail, key, "code name");
        }

        // A world's name is its hub, not a room that happens to be named after the world (Krokotopia's Skeleton Key room).
        if (s_worldHubs.FirstOrDefault(w => Key(w.World) == key) is { Hub: { } hub }
            && zones.FirstOrDefault(z => z.Code.Equals(hub, StringComparison.OrdinalIgnoreCase)) is { } world) {
            return new ZoneMatch(world, "world", []);
        }

        var named = zones.Where(z => z.Name.Length > 0 && Key(z.Name) == key).ToList();
        if (named.Count > 0) {
            return Pick(named, key, "name");
        }

        if (key.Length >= 3) {
            var inside = zones.Where(z => (z.Name.Length > 0 && Key(z.Name).Contains(key, StringComparison.Ordinal))
                || z.Code.Contains(withSlash, StringComparison.OrdinalIgnoreCase)
                || Key(z.Code).Contains(key, StringComparison.Ordinal)).ToList();
            if (inside.Count > 0) {
                return Pick(inside, key, "part of a name");
            }
        }

        // A close spelling: "Unicron Way", "Colosus Boulevard".
        if (key.Length >= 4) {
            var allowed = Math.Max(1, key.Length / 5);
            var close = zones
                .Select(z => (Zone: z, Off: Math.Min(z.Name.Length > 0 ? Distance(Key(z.Name), key, allowed) : int.MaxValue, Distance(Key(z.Tail), key, allowed))))
                .Where(c => c.Off <= allowed)
                .ToList();
            if (close.Count > 0) {
                var best = close.Min(c => c.Off);

                return Pick([.. close.Where(c => c.Off == best).Select(c => c.Zone)], key, "close spelling");
            }
        }

        return null;
    }

    /// <summary>The fittest of zones that matched equally well; the rest come back as the others it could mean.</summary>
    private static ZoneMatch Pick(List<ZoneName> found, string key, string how) {
        // A name that starts with the text beats one with it in the middle (only differs for "part of a name").
        var ordered = found.OrderBy(z => Rank(z, key))
            .ThenBy(z => !(Key(z.Name).StartsWith(key, StringComparison.Ordinal) || Key(z.Tail).StartsWith(key, StringComparison.Ordinal)))
            .ThenBy(z => z.Code.Count(c => c == '/')).ThenBy(z => z.Code.Length)
            .ThenBy(z => z.Code, StringComparer.OrdinalIgnoreCase).ToList();

        return new ZoneMatch(ordered[0], how, [.. ordered.Skip(1)]);
    }

    // Each world's hub, where `.tp <world>` goes (zone-details.json; Ahmarra left out until it is out).
    private static readonly (string World, string Hub)[] s_worldHubs = [
        ("Wizard City", "WizardCity/WC_Hub"), ("Krokotopia", "Krokotopia/KT_Hub"), ("Marleybone", "Marleybone/MB_Hub"),
        ("Mooshu", "MooShu/MS_Hub"), ("Dragonspyre", "DragonSpire/DS_Hub_Cathedral"), ("Grizzleheim", "Grizzleheim/GH_MainHub"),
        ("Celestia", "Celestia/CL_Hub"), ("Wysteria", "Wysteria/PA_Hub"), ("Zafaria", "Zafaria/ZF_Z00_Hub"),
        ("Avalon", "Avalon/AV_Z00_Hub"), ("Azteca", "Azteca/AZ_Z00_Zocalo"), ("Aquila", "Aquila/AQ_Z00_Hub"),
        ("Khrysalis", "Khrysalis/KR_Z00_Hub"), ("Polaris", "Polaris/PL_Z00_Walruskberg"), ("Mirage", "Mirage/MR_Z00_Hub"),
        ("Empyrea", "Empyrea/EM_Z00_Aeriel_HUB"), ("Karamelle", "Karamelle/KM_Z00_HUB"), ("Lemuria", "Lemuria/LM_Z00_Hub"),
        ("Novus", "Novus/NV_Z01_Conatus"), ("Wallaru", "Wallaru/WL_Z01_Hub"), ("Darkmoor", "Darkmoor/DM_Z01_Graveholm"),
        ("Arcanum", "Arcanum/AR_Z01_Hub"), ("Selenopolis", "Krokotopia/KT_Selenopolis/KT_Z04_Selenopolis"),
    ];

    // Smaller is better: a main world area before a house, an instance, a retired zone or a test zone, and a zone whose
    // code name holds its place name (DM_Z01_CastleDarkmoor for Castle Darkmoor) before one that doesn't.
    private static int Rank(ZoneName zone, string key) =>
        (zone.OldOrTest ? 16 : 0)
        + (zone.Code.Contains("/Interiors/", StringComparison.OrdinalIgnoreCase) || zone.Code.StartsWith("Housing", StringComparison.OrdinalIgnoreCase) ? 4 : 0)
        + (zone.Type == "D" ? 2 : 0)
        + (zone.Name.Length > 0 && Key(zone.Tail).Contains(Key(zone.Name), StringComparison.Ordinal) || Key(zone.Tail).Contains(key, StringComparison.Ordinal) ? 0 : 1);

    // How many letters apart two keys are (added, left out or changed), stopping early past the most allowed.
    private static int Distance(string a, string b, int most) {
        if (Math.Abs(a.Length - b.Length) > most) {
            return int.MaxValue;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++) {
            current[0] = i;
            var rowBest = current[0];
            for (var j = 1; j <= b.Length; j++) {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                rowBest = Math.Min(rowBest, current[j]);
            }

            if (rowBest > most) {
                return int.MaxValue;
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

}
