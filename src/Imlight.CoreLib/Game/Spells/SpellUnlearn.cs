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
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Spells;

/// <summary>
/// Unlearn any spell (Niduus, 2026-10-31: "make a UI that allows for players to UNLEARN spells that are on their
/// wizard"). Takes the spells a player picks out of their spellbook and every deck, and gives back the training points
/// they cost the way the game's own reset does (<see cref="TrainingReset.BoughtSpells"/>: what trained-spells.json
/// recorded, else 1 point for a trainer spell outside the wizard's school; own-school, quest, drop and free spells give
/// nothing back, as in the game).
///
/// `.unlearn &lt;spell&gt;` says what it would give back and `.unlearn &lt;spell&gt; yes` does it, for every player.
/// Ahziael's Progress > Unlearn Spells sends `.unlearn list` (the server writes the spellbook to
/// fast-forward/&lt;wizard id&gt;-spellbook.txt) and `.unlearn picked yes` (the spells in
/// fast-forward/&lt;wizard id&gt;-unlearn-pick.txt; what it did goes to &lt;wizard id&gt;-unlearn-picked-result.txt).
/// </summary>
internal static class SpellUnlearn {

    private const string FOLDER = "fast-forward";

    /// <summary>A spell the wizard knows, with what unlearning it gives back.</summary>
    internal readonly record struct Known(uint SpellId, string Name, string School, int PointsBack, string Kind);

    private static string PathOf(ulong charId, string suffix)
        => Path.Combine(FOLDER, charId.ToString(CultureInfo.InvariantCulture) + suffix);

    internal static string PickPath(ulong charId) => PathOf(charId, "-unlearn-pick.txt");

    /// <summary>Every spell in the wizard's spellbook, by name.</summary>
    internal static List<Known> Book(Wizard wizard) {
        if (wizard?.SpellbookBehavior?.LearnedSpellTemplateIds is not { } known) {
            return [];
        }

        var costs = Costs(wizard);

        return [
            .. known.Distinct()
                .Select(id => new Known(id, BossDevSpells.NameOf(id), SchoolOf(id), costs.GetValueOrDefault(id), BossDevSpells.KindOf(id) ?? ""))
                .OrderBy(spell => spell.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(spell => spell.SpellId),
        ];
    }

    /// <summary>
    /// The known spells a player meant by a name or template id: the exact name (all copies of that name), else every
    /// spell whose name has it in it.
    /// </summary>
    internal static List<Known> Find(Wizard wizard, string text) {
        text = (text ?? "").Trim();
        if (text.Length == 0) {
            return [];
        }

        var book = Book(wizard);
        if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) {
            return [.. book.Where(spell => spell.SpellId == id)];
        }

        var exact = book.Where(spell => string.Equals(spell.Name, text, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(InternalName(spell.SpellId), text, StringComparison.OrdinalIgnoreCase)).ToList();

        return exact.Count > 0 ? exact : [.. book.Where(spell => spell.Name.Contains(text, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>
    /// Takes these spells out of the wizard's spellbook and decks, tells the game, and gives back their training points.
    /// </summary>
    /// <returns>The spells taken, each with the points it gave back.</returns>
    internal static List<Known> Unlearn(IActorRef playerActor, Wizard wizard, IEnumerable<uint> spellIds) {
        var costs = Costs(wizard);
        var taken = new List<Known>();
        foreach (var id in spellIds.Distinct().ToList()) {
            if (wizard.SpellbookBehavior?.HasSpell(id) != true) {
                continue;
            }

            try {
                TrainingReset.RemoveFromDecks(playerActor, wizard, id);
                if (!wizard.UnlearnSpell(id)) {
                    continue;
                }

                playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_REMOVESPELLFROMBOOK { SpellID = (int) id });
                taken.Add(new Known(id, BossDevSpells.NameOf(id), SchoolOf(id), costs.GetValueOrDefault(id), BossDevSpells.KindOf(id) ?? ""));
            }
            catch (Exception ex) {
                Logger.Error("Unlearn | Could not take spell {0} from {1}: {2}",
                    Logger.Args(id, wizard.PlayerNameBehavior?.GetWizardName(), ex.Message));
            }
        }

        TrainingReset.Forget(wizard, taken.Select(spell => spell.SpellId));
        var points = taken.Sum(spell => spell.PointsBack);
        if (points > 0) {
            var newPoints = wizard.MagicSchoolBehavior.TrainingPoints + points;
            wizard.UpdateTrainingPoints(newPoints);
            playerActor.Tell(new WIZARD_12_PROTOCOL.MSG_UPDATETRAINING { TrainingPoints = newPoints });
        }

        Logger.Information("Unlearn | {0}: {1} spells unlearned ({2}), {3} training points back.",
            Logger.Args(wizard.PlayerNameBehavior?.GetWizardName(), taken.Count, string.Join(", ", taken.Select(t => t.Name)), points));

        return taken;
    }

    /// <summary>The spells Ahziael picked, from the wizard's pick file (which is then deleted).</summary>
    internal static List<uint> ReadPicked(ulong charId) {
        var ids = new List<uint>();
        var path = PickPath(charId);
        try {
            if (File.Exists(path)) {
                foreach (var line in File.ReadLines(path)) {
                    if (uint.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) {
                        ids.Add(id);
                    }
                }

                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Unlearn | The pick file could not be read: {0}", Logger.Args(ex.Message));
        }

        return ids;
    }

    /// <summary>Writes the wizard's spellbook for Ahziael's Unlearn Spells page.</summary>
    internal static void WriteBook(Wizard wizard) {
        var text = Header()
            .Append("points\t").Append((wizard.MagicSchoolBehavior?.TrainingPoints ?? 0).ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var spell in Book(wizard)) {
            text.Append("spell\t").Append(Clean(spell.Name)).Append('\t')
                .Append(spell.SpellId.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(Clean(spell.School)).Append('\t')
                .Append(spell.PointsBack.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(spell.Kind).Append('\n');
        }

        Write(PathOf(wizard.CharId, "-spellbook.txt"), text);
    }

    /// <summary>Writes what an unlearn from Ahziael did.</summary>
    internal static void WriteResult(ulong charId, List<Known> taken) {
        var text = Header()
            .Append("points\t").Append(taken.Sum(t => t.PointsBack).ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (var spell in taken) {
            text.Append("removed\t").Append(Clean(spell.Name)).Append('\t')
                .Append(spell.SpellId.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(spell.PointsBack.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        Write(PathOf(charId, "-unlearn-picked-result.txt"), text);
    }

    // What each bought spell gives back, as the game's reset counts it.
    private static Dictionary<uint, int> Costs(Wizard wizard) {
        var costs = new Dictionary<uint, int>();
        foreach (var spell in TrainingReset.BoughtSpells(wizard)) {
            costs.TryAdd(spell.SpellId, spell.Cost);
        }

        return costs;
    }

    private static string SchoolOf(uint id)
        => CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate spell ? (string) spell.m_sMagicSchoolName ?? "" : "";

    private static string InternalName(uint id)
        => CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate spell ? (string) spell.m_name ?? "" : "";

    private static StringBuilder Header() => new StringBuilder()
        .Append("state\tdone\n")
        .Append("written\t").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');

    private static string Clean(string text) => (text ?? "").Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

    private static void Write(string path, StringBuilder text) {
        try {
            Directory.CreateDirectory(FOLDER);
            File.WriteAllText(path + ".tmp", text.ToString());
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Unlearn | {0} could not be written: {1}", Logger.Args(path, ex.Message));
        }
    }

}
