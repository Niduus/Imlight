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

namespace Imlight.CoreLib.Game.QuestTeam;

/// <summary>
/// The team's trips to the school trainers in Ravenwood (Niduus, 2026-11-12: "They will walk to their school trainers and
/// learn their spells. They will also adapt, having say the Ice go train Feint"). Each wizard learns its school's spells
/// as its level allows them, and spends training points on the spells its job needs: Ice (tank and support) Feint, Life
/// (heal and support) Tower Shield; Death and Storm keep theirs. Nothing is given: they walk there and train like a player,
/// by the game's own trainer rules.
/// </summary>
internal static class QuestTeamTraining {

    internal const string TRAINER_ZONE = "WizardCity/WC_Ravenwood";

    // The spells each job trains outside its school, by name, first wanted first.
    private static readonly Dictionary<string, string[]> s_extra = new(StringComparer.OrdinalIgnoreCase) {
        ["Ice"] = ["Feint"],
        ["Life"] = ["Tower Shield"],
        ["Death"] = [],
        ["Storm"] = [],
    };

    internal sealed record Lesson(uint TrainerTemplate, uint SpellId, string Spell, float X, float Y);

    /// <summary>What this wizard can learn at the Ravenwood trainers right now, own school first.</summary>
    internal static List<Lesson> Learnable(QuestTeamMember.Status member) {
        var lessons = new List<Lesson>();
        if (member.Wizard is not { } wizard) {
            return lessons;
        }

        try {
            var placed = QuestStepSpots.Zone(TRAINER_ZONE).Objects;
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
                                                || string.Equals(e, QuestTeamText.SpellName(spell.Id), StringComparison.OrdinalIgnoreCase));
                    if (!own && !wanted) {
                        continue;
                    }

                    if (!VanillaTrainers.Judge(wizard, spell, template).CanTrain) {
                        continue;
                    }

                    lessons.Add(new Lesson(trainer.TemplateId, spell.Id, QuestTeamText.SpellName(spell.Id), spot.Spot.X, spot.Spot.Y));
                }
            }
        }
        catch (Exception e) {
            Imlight.Common.Logger.Debug("Quest Team: could not read the trainers: {0}", Imlight.Common.Logger.Args(e.Message));
        }

        return [.. lessons.DistinctBy(l => l.SpellId).OrderBy(l => string.Equals(SchoolOfSpell(l.SpellId), member.Role, StringComparison.OrdinalIgnoreCase) ? 0 : 1)];
    }

    /// <summary>The index of a spell in a trainer's list (as its training window shows it), or -1.</summary>
    internal static int IndexAt(QuestTeamMember.Status member, ulong trainerGlobalId, uint spellId) {
        var mark = MinimapIndex.In(member.ZoneActor).FirstOrDefault(m => m.Object.m_globalID.Full == trainerGlobalId);
        var trainer = mark?.Entity?.GetComponentOfType<InteractTrainerComponent>();

        return trainer?.SpellInventory?.FindIndex(e => (uint) e.TemplateID == spellId) ?? -1;
    }

    private static string SchoolOfSpell(uint id)
        => CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate template ? (string) template.m_sMagicSchoolName ?? "" : "";

}
