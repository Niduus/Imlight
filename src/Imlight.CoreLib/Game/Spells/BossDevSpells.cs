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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Spells;

/// <summary>
/// Unlearn dev/boss spells (Niduus, 2026-10-29: "add an 'Unlearn dev/boss' spells button to spells. This way, people can
/// test spells while also remaining legit to their desired characters"). Takes every boss spell (Merle Ambrose's list)
/// and dev spell (the Dev trainer tomes' list: a Test folder, or named Dev, Debug, GM or Admin) out of a wizard's
/// spellbook and decks, and gives back the training points trained-spells.json says they cost. Spells any game trainer
/// teaches are never taken. `.unlearnboss` lists them, `.unlearnboss yes` does it; Ahziael's Spells page sends the
/// second and shows fast-forward/&lt;wizard id&gt;-unlearn-result.txt.
/// </summary>
internal static class BossDevSpells {

    private const string RESULT_FOLDER = "fast-forward";

    /// <summary>A boss or dev spell a wizard knows.</summary>
    internal readonly record struct Found(uint SpellId, string Name, string Kind);

    // Spell id -> "Boss" or "Dev".
    private static readonly Lazy<Dictionary<uint, string>> s_kinds = new(Build);

    private static Dictionary<uint, string> Build() {
        var kinds = new Dictionary<uint, string>();
        try {
            var trainerSpells = SpiralDB.NpcSpellInventories.Values
                .Where(inventory => inventory?.Spells is not null)
                .SelectMany(inventory => inventory.Spells)
                .Select(entry => (uint) entry.TemplateID)
                .ToHashSet();
            foreach (var entry in InteractTrainerComponent.TestSpellsOf(InteractTrainerComponent.TestTrainerKind.Boss)) {
                if (!trainerSpells.Contains((uint) entry.TemplateID)) {
                    kinds.TryAdd((uint) entry.TemplateID, "Boss");
                }
            }

            foreach (var spell in HouseTrainerSets.DevSpells()) {
                if (!trainerSpells.Contains((uint) spell.Id)) {
                    kinds.TryAdd((uint) spell.Id, "Dev");
                }
            }
        }
        catch (Exception ex) {
            Logger.Error("Unlearn boss/dev | Could not build the list: {0}", Logger.Args(ex.Message));
        }

        Logger.Information("Unlearn boss/dev | {0} boss and {1} dev spells.",
            Logger.Args(kinds.Count(k => k.Value == "Boss"), kinds.Count(k => k.Value == "Dev")));

        return kinds;
    }

    /// <summary>The boss and dev spells a wizard knows, by name.</summary>
    internal static List<Found> Of(Wizard wizard) {
        if (wizard?.SpellbookBehavior?.LearnedSpellTemplateIds is not { } known) {
            return [];
        }

        return [
            .. known.Distinct()
                .Where(id => s_kinds.Value.ContainsKey(id))
                .Select(id => new Found(id, NameOf(id), s_kinds.Value[id]))
                .OrderBy(found => found.Kind)
                .ThenBy(found => found.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// Takes every boss and dev spell out of the wizard's spellbook and decks, gives back what they cost in training
    /// points and writes what it did for Ahziael.
    /// </summary>
    /// <returns>The spells taken, each with the points it gave back.</returns>
    internal static List<(Found Spell, int Points)> Unlearn(IActorRef playerActor, Wizard wizard) {
        var taken = new List<Found>();
        foreach (var spell in Of(wizard)) {
            try {
                TrainingReset.RemoveFromDecks(playerActor, wizard, spell.SpellId);
                if (!wizard.UnlearnSpell(spell.SpellId)) {
                    continue;
                }

                playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_REMOVESPELLFROMBOOK { SpellID = (int) spell.SpellId });
                taken.Add(spell);
            }
            catch (Exception ex) {
                Logger.Error("Unlearn boss/dev | Could not take spell {0} from {1}: {2}",
                    Logger.Args(spell.SpellId, wizard.PlayerNameBehavior?.GetWizardName(), ex.Message));
            }
        }

        var costs = TrainingReset.Forget(wizard, taken.Select(spell => spell.SpellId));
        var result = taken.Select(spell => (spell, costs.GetValueOrDefault(spell.SpellId))).ToList();
        var points = result.Sum(r => r.Item2);
        if (points > 0) {
            var newPoints = wizard.MagicSchoolBehavior.TrainingPoints + points;
            wizard.UpdateTrainingPoints(newPoints);
            playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_UPDATETRAINING { TrainingPoints = newPoints });
        }

        Logger.Information("Unlearn boss/dev | {0}: {1} spells taken, {2} training points back.",
            Logger.Args(wizard.PlayerNameBehavior?.GetWizardName(), result.Count, points));
        WriteResult(wizard.CharId, result);

        return result;
    }

    /// <summary>"Boss" or "Dev" for a boss or dev spell, else null.</summary>
    internal static string KindOf(uint id) => s_kinds.Value.GetValueOrDefault(id);

    internal static string NameOf(uint id) {
        if (CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate spell) {
            string key = spell.m_displayName;
            var shown = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);

            return string.IsNullOrWhiteSpace(shown) ? (string) spell.m_name ?? $"spell {id}" : shown;
        }

        return $"spell {id}";
    }

    private static void WriteResult(ulong charId, List<(Found Spell, int Points)> result) {
        try {
            static string Clean(string text) => (text ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
            var text = new StringBuilder()
                .Append("state\tdone\n")
                .Append("written\t").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
            foreach (var (spell, points) in result) {
                text.Append("spell\t").Append(Clean(spell.Name)).Append('\t').Append(spell.Kind).Append('\t')
                    .Append(spell.SpellId.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(points.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }

            Directory.CreateDirectory(RESULT_FOLDER);
            var path = Path.Combine(RESULT_FOLDER, charId.ToString(CultureInfo.InvariantCulture) + "-unlearn-result.txt");
            File.WriteAllText(path + ".tmp", text.ToString());
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Unlearn boss/dev result could not be written: {0}", Logger.Args(ex.Message));
        }
    }

}
