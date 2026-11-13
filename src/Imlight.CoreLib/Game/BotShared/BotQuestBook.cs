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
using System.Linq;
using System.Text.Json.Nodes;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Shared.Character;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// The XP bar and the quest book of a server-played wizard, for Ahziael's sheet (Niduus, 2026-11-13: "a live updating XP
/// bar ... with CURR/MAX displayed" and "a quest page ... that lays them out as they are in game"). Shared by Ultimus and
/// the Quest Team through BotSheet.Fill: "xp" and "xpMax" are the XP into this level and the XP the level takes, as the
/// game's bar counts them; "questBook" is one card per quest held, as the game's "All Current Quests" book shows them.
/// </summary>
internal static class BotQuestBook {

    private const int MAX_QUESTS = 40;

    /// <summary>The XP into the wizard's level and what the level takes (0 and 0 at the top level or when unknown).</summary>
    internal static (int Now, int Max) Xp(Wizard wizard) {
        var school = wizard?.MagicSchoolBehavior;
        if (school is null) {
            return (0, 0);
        }

        var level = (int) school.Level;
        var start = MagicLevelsConfig.GetExperiencePointsAtLevel(level);
        var end = MagicLevelsConfig.GetExperiencePointsAtLevel(level + 1);
        if (end <= start) {
            return (0, 0);
        }

        return (Math.Clamp(school.ExperiencePoints - start, 0, end - start), end - start);
    }

    /// <summary>One card per quest held: the active one first, then main quests, then the rest by level.</summary>
    internal static JsonArray Book(Wizard wizard, string activeTitle) {
        var cards = new JsonArray();
        var held = wizard?.QuestBehavior?.CurrentQuestInstances?.Where(q => q is not null).ToList() ?? [];
        var rows = new System.Collections.Generic.List<(JsonObject Card, int Order, int Level)>();
        foreach (var quest in held) {
            try {
                if (QuestTemplateCollection.GetQuestByName(quest.QuestName) is not { } template) {
                    continue;
                }

                var title = OverlayQuestTracker.English(template.m_questTitle, quest.QuestName);
                var main = QuestStory.IsMain(template);
                var active = activeTitle.Length > 0 && (string.Equals(title, activeTitle, StringComparison.OrdinalIgnoreCase)
                                                        || string.Equals(quest.QuestName, activeTitle, StringComparison.OrdinalIgnoreCase));
                var card = new JsonObject { ["title"] = title, ["main"] = main, ["active"] = active };

                // The first step not done that the book shows (a hidden step is not shown in the game either).
                var progress = quest.GoalProgress?.FirstOrDefault(g => g is not null && quest.IsGoalActive(g.GoalName)
                    && template.m_goals?.FirstOrDefault(t => t is not null && (string) t.m_goalName == g.GoalName) is { m_hideGoal: false });
                var goal = progress is null ? null : template.m_goals.First(t => t is not null && (string) t.m_goalName == progress.GoalName);
                if (goal is not null) {
                    var (verb, target) = Split(OverlayQuestTracker.StepTitle(goal), goal);
                    card["verb"] = verb;
                    card["target"] = target;
                    if (goal is BountyGoalTemplate bounty && bounty.m_bountyTotal > 1) {
                        card["count"] = Math.Max(0, progress.CurrentProgress);
                        card["of"] = bounty.m_bountyTotal;
                    }

                    var where = OverlayQuestTracker.StepWhere(goal, OverlayQuestTracker.StepZone(goal));
                    var parts = where.Split(" / ", 2, StringSplitOptions.TrimEntries);
                    card["world"] = parts.Length > 1 ? parts[0] : "";
                    card["place"] = parts.Length > 1 ? parts[1] : parts[0];
                }

                var (gold, xp, items) = Rewards(template);
                card["gold"] = gold;
                card["xp"] = xp;
                card["items"] = items;
                rows.Add((card, active ? 0 : main ? 1 : 2, template.m_questLevel));
            }
            catch (Exception ex) {
                Logger.Debug("Bot quest book: quest {0} could not be read: {1}", Logger.Args(quest.QuestName, ex.Message));
            }
        }

        foreach (var (card, _, _) in rows.OrderBy(r => r.Order).ThenBy(r => r.Level).Take(MAX_QUESTS)) {
            cards.Add(card);
        }

        return cards;
    }

    private static readonly string[] s_verbs = [
        "Talk to", "Speak to", "Speak with", "Go to", "Find", "Defeat", "Stop", "Collect", "Explore", "Use", "Visit", "Destroy",
        "Free", "Open", "Rescue", "Deliver", "Bring", "Return to", "Escort", "Check", "Investigate", "Search", "Get", "Buy",
    ];

    // "Talk to Diego the Duelmaster" -> ("Talk To", "Diego the Duelmaster"), as the book writes the verb over the portrait.
    private static (string Verb, string Target) Split(string step, GoalTemplate goal) {
        step = (step ?? "").Trim();
        foreach (var verb in s_verbs) {
            if (step.StartsWith(verb + " ", StringComparison.OrdinalIgnoreCase)) {
                var target = step[(verb.Length + 1)..].Trim();
                // "Defeat 3 Troubled Warriors": the count shows under the portrait, not in the name.
                if (goal is BountyGoalTemplate && target.Split(' ', 2) is [var n, var rest] && int.TryParse(n, out _)) {
                    target = rest;
                }

                return (TitleCase(verb), target);
            }
        }

        return (goal switch {
            PersonaGoalTemplate => "Talk To",
            BountyGoalTemplate => "Defeat",
            WaypointGoalTemplate => "Go To",
            _ => "",
        }, step);
    }

    private static string TitleCase(string words)
        => string.Join(' ', words.Split(' ').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));

    // The quest's reward drop tables: gold (the most it can give), XP and how many item rewards.
    private static (int Gold, int Xp, int Items) Rewards(QuestTemplate template) {
        var (gold, xp, items) = (0, 0, 0);
        foreach (var name in template.m_endResults?.m_results?.OfType<ResDropTable>().Select(r => (string) r.m_tableName)
                     .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct() ?? []) {
            if (DropTableFiles.Get(name) is not { } table) {
                continue;
            }

            gold += Math.Max(table.MinGold, table.MaxGold);
            xp += table.ExperienceAmount;
            items += table.Items?.Count(i => !string.IsNullOrEmpty(i.ItemId)) > 0 ? 1 : 0;
        }

        return (gold, xp, items);
    }

}
