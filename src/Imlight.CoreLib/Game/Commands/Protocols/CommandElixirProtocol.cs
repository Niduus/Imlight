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
using Akka.Actor;
using Imcodec.Cryptography;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.Requirements;
using Imlight.CoreLib.Game.Requirements.Contexts;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.elixir50`: the Level 50 Elixir from the crown shop, for free. Like the real one it sets the wizard to
/// level 50 (with the training points for those levels), finishes the main quests of Wizard City, Krokotopia,
/// Marleybone, MooShu and Dragonspyre with the spells, badges and progress they give, and hands out the
/// Dragonspyre quest gear for the wizard's school.
/// </summary>
internal class CommandElixirProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("elixir50")]
    [Alias("level50")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void Level50ElixirCommand()
        => InformSenderClient(Level50Elixir.Use(Context.SessionActor, Context.CharacterObject, Context.Character));

}

/// <summary>
/// The Level 50 Elixir, used by `.elixir50` and by buying it in the crown shop.
/// </summary>
internal static class Level50Elixir {

    internal const uint TEMPLATE_ID = 1386000;

    private const byte ELIXIR_LEVEL = 50;
    private static readonly string[] s_elixirWorlds = ["WC-", "KT-", "MB-", "MS-", "DS-"];
    // Gear comes from the Dragonspyre quests only, so the bags don't fill with every older reward.
    private const string GEAR_WORLD = "DS-";

    /// <summary>
    /// Uses the elixir on a wizard.
    /// </summary>
    /// <returns>What happened, for the chat window.</returns>
    internal static string Use(IActorRef session, CoreObject characterObject, Wizard wizard) {
        try {
            if (wizard.MagicSchoolBehavior.Level < ELIXIR_LEVEL) {
                session.Tell(new CHARACTER_103_PROTOCOL.MSG_LEVELUP { NewLevel = ELIXIR_LEVEL }, null);
            }

            var quests = SpiralDB.QuestTemplates
                .Where(q => q?.m_mainline == true && s_elixirWorlds.Any(w => ((string) q.m_questName ?? "").StartsWith(w, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            int completed = 0, spells = 0;
            var gearTables = new List<string>();
            var badges = new List<uint>();
            foreach (var quest in quests) {
                string name = quest.m_questName;
                if (wizard.HasCompletedQuest(name)) {
                    continue;
                }

                var done = wizard.HasQuest(name) ? wizard.CompleteQuest(name) : wizard.SetQuestRegistryValue(name, "Complete", 1);
                if (!done) {
                    continue;
                }

                completed++;
                foreach (var result in quest.m_endResults?.m_results ?? []) {
                    switch (result) {
                        case ResLearnSpell learn when Passes(learn.m_requirements, session, characterObject, wizard):
                            if (SpellTeacher.TryTeach(session, wizard, (uint) learn.m_templateID) == SpellTeachResult.Learned) {
                                spells++;
                            }
                            break;
                        case ResModifyEntry entry when !string.IsNullOrEmpty(entry.m_entryName):
                            // Quest progress flags that doors and later quests check.
                            if (entry.m_isQuestRegistry && !string.IsNullOrEmpty(entry.m_questName)) {
                                wizard.SetQuestRegistryValue(entry.m_questName, entry.m_entryName, (ulong) entry.m_value);
                            }
                            else if (!entry.m_isQuestRegistry) {
                                wizard.SetRegistryValue(entry.m_entryName, (ulong) entry.m_value);
                            }
                            break;
                        case ResAddBadge badge when !string.IsNullOrEmpty(badge.m_badgeName):
                            badges.Add(StringHash.Compute(badge.m_badgeName));
                            break;
                        case ResDropTable table when name.StartsWith(GEAR_WORLD, StringComparison.OrdinalIgnoreCase)
                                                      && !string.IsNullOrEmpty(table.m_tableName):
                            gearTables.Add(table.m_tableName);
                            break;
                    }
                }
            }

            foreach (var badgeId in badges.Distinct()) {
                session.Tell(new CHARACTER_103_PROTOCOL.MSG_BADGEEVENT { Kind = BadgeEventKind.Award, BadgeId = badgeId });
            }

            // The quest reward tables pick the piece for the wizard's school. No experience: the level is set above.
            if (gearTables.Count > 0) {
                var loot = DropTableRoller.Roll([.. gearTables], session, characterObject, wizard);
                LootGranter.GrantAndDisplay(session, wizard, loot, 0f);
            }

            Logger.Information("Level 50 Elixir for {0}: {1} quests finished, {2} spells learned, {3} reward tables.",
                Logger.Args(wizard.CharId, completed, spells, gearTables.Count));

            return $"Level 50 Elixir used: level 50, {completed} main quests through Dragonspyre finished, " +
                   $"{spells} spells learned and the Dragonspyre gear for your school added. Relog to see everything.";
        }
        catch (Exception e) {
            Logger.Error("Level 50 Elixir failed for {0}: {1}", Logger.Args(wizard?.CharId, e));

            return "The Level 50 Elixir ran into a problem. Please send the server log.";
        }
    }

    private static bool Passes(RequirementList requirements, IActorRef session, CoreObject characterObject, Wizard wizard)
        => RequirementDispatcher.EvaluateRequirements(requirements,
            new GenericRequirementContext(requirements, session, characterObject, wizard));

}
