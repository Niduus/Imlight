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
 *
 * ========================================================================
 * SAGA SPELLEMENT PACKS
 * ========================================================================
 *
 * PURPOSE:
 * The Malistaire, Morganthe, Spider and Dasein Saga Spellement Packs have seven slots each (two
 * Spellements_P_Elemental_Arc01_01, two Spellements_P_Spiritual_Arc01_01, three Spellements_P_School_Arc01_01). SpiralDB
 * has no Elemental or Spiritual list, so those gave a Spellement of any arc, and its School list holds every school,
 * so nothing favoured the opener's school (Niduus, 2026-10-27: "the arc specific
 * spellement packs are NOT giving correct spellements from said arc ... as well as not giving a majority to the
 * school of the pack opener's school").
 *
 * Each slot now gives a Spellement of the pack's own arc, from spiraldb-extra/Packs/SagaSpellements.json
 * (tools/pack-tables/make_saga_spellements.py makes it from Wizard404's pack pages):
 * - the School slot always one of the opener's school;
 * - the Elemental and Spiritual slots one of the opener's school at [SpellementPacks] OwnSchoolChance percent
 *   (read live), else one of another school.
 */

using Imlight.CoreLib.Game.Tools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.Packs;

public static partial class SagaSpellements {

    private const string FILE_NAME = "SagaSpellements.json";

    private static readonly JsonSerializerOptions s_jsonOptions = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    private sealed class Spellement {
        public uint Id { get; set; }
        public string Name { get; set; } = "";
        public string School { get; set; } = "";
    }

    private sealed class Arc {
        public string Pack { get; set; } = "";
        public List<Spellement> Spellements { get; set; } = [];
    }

    private static readonly ReloadingLazy<Dictionary<string, Arc>> s_arcs = new(Load);

    private static int OwnSchoolChance => Math.Clamp(ConfigurationManager.GetValue("SpellementPacks.OwnSchoolChance", 50), 0, 100);

    [GeneratedRegex(@"^Spellements_P_(Elemental|Spiritual|School)_(Arc\d+)_", RegexOptions.IgnoreCase)]
    private static partial Regex SlotPattern();

    /// <summary>
    /// The Spellement a Saga Spellement Pack slot gives a wizard of this school, or null when the slot is not one of
    /// theirs or its arc is unknown.
    /// </summary>
    public static (uint Id, string Name)? Roll(string tableName, string school) {
        var match = SlotPattern().Match(tableName);
        if (!match.Success || !s_arcs.Value.TryGetValue(match.Groups[2].Value, out var arc) || arc.Spellements.Count == 0) {
            return null;
        }

        var own = arc.Spellements.Where(s => s.School.Equals(school, StringComparison.OrdinalIgnoreCase)).ToList();
        var others = arc.Spellements.Except(own).ToList();
        var schoolSlot = match.Groups[1].Value.Equals("School", StringComparison.OrdinalIgnoreCase);
        var pool = own.Count > 0 && (schoolSlot || others.Count == 0 || Random.Shared.Next(100) < OwnSchoolChance) ? own
            : others.Count > 0 ? others : arc.Spellements;
        var pick = pool[Random.Shared.Next(pool.Count)];

        return (pick.Id, pick.Name);
    }

    private static Dictionary<string, Arc> Load() {
        var path = Path.Combine(SpiralDB.ExtraPath, "Packs", FILE_NAME);
        if (!File.Exists(path)) {
            Logger.Warning("Saga Spellement Packs: no {0}, so their slots give a random Spellement.", Logger.Args(path));
            return [];
        }

        try {
            using var stream = File.OpenRead(path);
            var arcs = JsonSerializer.Deserialize<Dictionary<string, Arc>>(stream, s_jsonOptions) ?? [];
            foreach (var arc in arcs.Values) {
                arc.Spellements = [.. arc.Spellements.Where(s => CoreObjectFactory.HasTemplate(s.Id))];
            }

            Logger.Information("Saga Spellement Packs: {0}.", Logger.Args(string.Join(", ",
                arcs.Select(a => $"{a.Key} {a.Value.Pack} ({a.Value.Spellements.Count})"))));

            return new Dictionary<string, Arc>(arcs, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) {
            Logger.Warning("Saga Spellement Packs: could not read {0}: {1}", Logger.Args(path, ex.Message));
            return [];
        }
    }
}
