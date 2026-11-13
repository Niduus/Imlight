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
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Ultimus's trips to the trainers (forked from the Quest Team's): it learns Balance spells as its level allows them, and
/// spends training points on what a solo wizard needs (a heal, a shield, a trap). Nothing is given: it walks there and
/// trains like a player, by the game's own trainer rules.
/// </summary>
internal static class UltimusTraining {

    internal const string TRAINER_ZONE = "WizardCity/WC_Ravenwood";

    // The spells Ultimus trains outside Balance, first wanted first: alone, it needs a heal and a shield of its own, and a
    // trap to build up its hits.
    private static readonly Dictionary<string, string[]> s_extra = new(StringComparer.OrdinalIgnoreCase) {
        ["Balance"] = ["Fairy", "Sprite", "Tower Shield", "Feint", "Unicorn"],
    };

    internal sealed record Lesson(uint TrainerTemplate, uint SpellId, string Spell, float X, float Y, string Zone = TRAINER_ZONE);

    /// <summary>
    /// What this wizard can learn right now, own school first: at the trainers of the area it stands in (any trainer the
    /// team can reach, Niduus, 2026-11-12: "able to visit other trainers that are accessible and learn spells that are
    /// accessible at their level and if they have a training point"), then at the Ravenwood trainers.
    /// </summary>
    internal static List<Lesson> Learnable(UltimusMember.Status member) {
        var lessons = new List<Lesson>();
        if (member.Wizard is not { } wizard) {
            return lessons;
        }

        var zones = new List<string>();
        if (!string.IsNullOrEmpty(member.Zone) && member.Wizard?.ZoneIsInstance != true) {
            zones.Add(member.Zone);
        }

        if (!zones.Any(z => string.Equals(z, TRAINER_ZONE, StringComparison.OrdinalIgnoreCase))) {
            zones.Add(TRAINER_ZONE);
        }

        try {
            foreach (var zone in zones) {
                Add(zone);
            }
        }
        catch (Exception e) {
            Imlight.Common.Logger.Debug("Ultimus: could not read the trainers: {0}", Imlight.Common.Logger.Args(e.Message));
        }

        return [.. lessons.DistinctBy(l => l.SpellId)
            .OrderBy(l => string.Equals(SchoolOfSpell(l.SpellId), member.Role, StringComparison.OrdinalIgnoreCase) ? 0 : 1)];

        void Add(string zone) {
            var placed = QuestStepSpots.Zone(zone).Objects ?? [];
            var extra = s_extra.GetValueOrDefault(member.Role) ?? [];
            foreach (var trainer in VanillaTrainers.All) {
                var spot = placed.FirstOrDefault(o => o.Template == trainer.TemplateId);
                if (spot.Template != trainer.TemplateId) {
                    continue;
                }

                foreach (var spell in trainer.Spells.Where(s => s.Kind == "School")) {
                    if (wizard.SpellbookBehavior.HasSpell(spell.Id) || CoreObjectFactory.GetCoreTemplate(spell.Id) is not SpellTemplate template) {
                        continue;
                    }

                    var own = string.Equals(spell.School, member.Role, StringComparison.OrdinalIgnoreCase);
                    var wanted = extra.Any(e => string.Equals(e, spell.Name, StringComparison.OrdinalIgnoreCase)
                                                || string.Equals(e, UltimusText.SpellName(spell.Id), StringComparison.OrdinalIgnoreCase));
                    if (!own && !wanted) {
                        continue;
                    }

                    if (!VanillaTrainers.Judge(wizard, spell, template).CanTrain) {
                        continue;
                    }

                    lessons.Add(new Lesson(trainer.TemplateId, spell.Id, UltimusText.SpellName(spell.Id), spot.Spot.X, spot.Spot.Y, zone));
                }
            }
        }
    }

    /// <summary>The index of a spell in a trainer's list (as its training window shows it), or -1.</summary>
    internal static int IndexAt(UltimusMember.Status member, ulong trainerGlobalId, uint spellId) {
        var mark = MinimapIndex.In(member.ZoneActor).FirstOrDefault(m => m.Object.m_globalID.Full == trainerGlobalId);
        var trainer = mark?.Entity?.GetComponentOfType<InteractTrainerComponent>();

        return trainer?.SpellInventory?.FindIndex(e => (uint) e.TemplateID == spellId) ?? -1;
    }

    private static string SchoolOfSpell(uint id)
        => CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate template ? (string) template.m_sMagicSchoolName ?? "" : "";

}
