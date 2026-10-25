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
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.World;

/// <summary>A fishing or gardening spell trainer of the real game and the spells it teaches.</summary>
internal sealed record TradeTrainer(uint TemplateId, string Name, string Kind, IReadOnlyList<uint> Spells);

/// <summary>
/// The real game's fishing and gardening spell trainers (Floyd, Haarek Silverscale, Charley, Farley and the rest),
/// read from spiraldb-extra/TradeTrainers/Trainers.json (from Wizard404). InteractTrainerComponent makes each one
/// teach its spells, free and at any rank.
/// </summary>
internal static class TradeTrainers {

    private static readonly Lazy<IReadOnlyDictionary<uint, TradeTrainer>> s_trainers = new(Load);

    /// <summary>The trainer with this NPC template id, or null.</summary>
    internal static TradeTrainer For(uint templateId)
        => s_trainers.Value.TryGetValue(templateId, out var trainer) ? trainer : null;

    private static IReadOnlyDictionary<uint, TradeTrainer> Load() {
        var path = Path.Combine(SpiralDB.ExtraPath, "TradeTrainers", "Trainers.json");
        var result = new Dictionary<uint, TradeTrainer>();
        try {
            if (!File.Exists(path)) {
                Logger.Warning("Trade trainers: {0} is missing; fishing and gardening trainers teach nothing.", Logger.Args(path));

                return result;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var entry in document.RootElement.GetProperty("Trainers").EnumerateArray()) {
                var trainer = new TradeTrainer(
                    entry.GetProperty("TemplateId").GetUInt32(),
                    entry.TryGetProperty("Name", out var name) ? name.GetString() ?? "" : "",
                    entry.TryGetProperty("Kind", out var kind) ? kind.GetString() ?? "" : "",
                    [.. entry.GetProperty("Spells").EnumerateArray().Select(s => s.GetUInt32())]);
                result[trainer.TemplateId] = trainer;
            }

            Logger.Information("Trade trainers: {0} fishing and gardening trainers.", Logger.Args(result.Count));
        }
        catch (Exception e) {
            Logger.Error("Trade trainers: could not read {0}: {1}", Logger.Args(path, e.Message));
        }

        return result;
    }

}
