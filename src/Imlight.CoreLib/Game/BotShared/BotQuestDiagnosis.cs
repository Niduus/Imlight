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
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Madlibs;
using Imlight.CoreLib.Game.Quests;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// Works out whether a bot's quest step can be done at all, and what most likely stops it (Niduus, 2026-11-13: "add in
/// detail character and quest detection to determine when/if a quest can/cannot be completed via server/player
/// interactions ... to assume what is breaking it has its accuracy go up way higher than normal"). It reads the quest's
/// data, the wizard's own quest and character state, the running zone (who is spawned, what is in battle) and what the
/// server sent the wizard's game (NPC options, last text), checks each in turn, and ranks the failed checks into one
/// likely cause with a confidence. Nothing here changes the game. Shared by Ultimus and the Quest Team.
/// </summary>
internal static class BotQuestDiagnosis {

    internal const string OK = "OK";
    internal const string PROBLEM = "PROBLEM";
    internal const string INFO = "INFO";

    /// <summary>What was found: every check as one line, the most likely cause, how sure, and whether the step looks doable.</summary>
    internal sealed class Result {
        public List<string> Checks { get; } = [];
        public string LikelyCause { get; set; } = "";
        public string Confidence { get; set; } = "Low";
        public string Completable { get; set; } = "Unclear";
        public string Category { get; set; } = "";

        /// <summary>The verdict in one line, for an issue's text.</summary>
        public string Summary => $"Diagnosis ({Confidence} confidence): {LikelyCause} Can the step be done? {Completable}.";
    }

    // A failed check: its weight is how surely it alone stops the step (100 = the step can't be done while it stands).
    private sealed record Problem(int Weight, string Category, string Cause);

    // Words in a server message that tell of a refusal.
    private static readonly string[] s_refusals = ["can't", "cannot", "not ", "full", "require", "must ", "need", "unable", "only ", "locked", "already"];

    /// <summary>
    /// Diagnoses <paramref name="goalName"/> of <paramref name="questName"/> for <paramref name="wizard"/>, standing in
    /// <paramref name="zone"/> at (<paramref name="x"/>, <paramref name="y"/>). <paramref name="options"/> are the options
    /// of every NPC or object the server sent its game (by object id); <paramref name="globalId"/> is its object id, to see
    /// whether it is in a battle. Never throws: a check that can't be read says so.
    /// </summary>
    internal static Result Run(Wizard wizard, string zone, IActorRef zoneActor, float x, float y, string questName, string goalName,
        IReadOnlyDictionary<ulong, List<(string Service, uint Index, string Display)>> options, string lastServerText, ulong globalId = 0) {
        var result = new Result();
        var problems = new List<Problem>();

        void Ok(string text) => result.Checks.Add($"{OK}: {text}");
        void Info(string text) => result.Checks.Add($"{INFO}: {text}");
        void Bad(int weight, string category, string text, string cause) {
            result.Checks.Add($"{PROBLEM}: {text}");
            problems.Add(new Problem(weight, category, cause));
        }

        try {
            zone ??= "";
            options ??= new Dictionary<ulong, List<(string, uint, string)>>();
            Character(wizard, globalId, Ok, Info, Bad);

            // 1. The quest itself.
            if (string.IsNullOrEmpty(questName)) {
                Info("no quest named for this issue, so only the character was checked");
                return Finish(result, problems);
            }

            if (QuestTemplateCollection.GetQuestByName(questName) is not { } quest) {
                Bad(100, "data", $"quest {questName} is not in the quest data",
                    $"The quest {questName} is missing from the quest data (data/spiraldb/QuestTemplates), so the server can't run it.");
                return Finish(result, problems);
            }

            Ok($"quest {questName} is in the quest data ({quest.m_goals?.Count(g => g is not null) ?? 0} steps)");
            if (QuestOptions.IsOff(questName)) {
                Bad(95, "setting", "the quest is switched off or set to skip in Ahziael (Quests options)",
                    "The quest is switched off or set to skip in Ahziael, so the server won't offer or advance it.");
            }

            // 2. The wizard and the quest.
            var held = wizard?.QuestBehavior?.CurrentQuestInstances?.FirstOrDefault(q => q is not null
                && string.Equals(q.QuestName, questName, StringComparison.OrdinalIgnoreCase));
            var level = wizard?.MagicSchoolBehavior?.Level ?? 0;
            if (held is null) {
                if (wizard?.HasCompletedQuest(questName) == true) {
                    Bad(70, "server", "the wizard already finished this quest, yet it is still going for it",
                        "The quest is already done on the wizard, but the bot still went for it: the quest list it was sent or read is out of date.");
                }
                else if (wizard is not null && quest.m_requirements is not null) {
                    var context = new Imlight.CoreLib.Game.Requirements.Contexts.QuestRequirementContext(quest.m_requirements, null, null, wizard, questName);
                    if (QuestService.FastForwardMeets(quest.m_requirements, context, false, level)) {
                        Info("the wizard does not hold the quest yet, and meets everything the quest asks to be offered it");
                    }
                    else if (QuestService.FastForwardMeets(quest.m_requirements, context, true, level)) {
                        Bad(90, "character", $"the wizard (level {level}) is under the level the quest asks for",
                            $"The wizard is level {level}, under the level the quest needs, so no NPC will offer it.");
                    }
                    else {
                        Bad(88, "character", "the wizard does not meet the quest's other requirements (an earlier quest, a badge or an item)",
                            "The wizard lacks something the quest requires besides level (most often an earlier quest not finished), so it is never offered.");
                    }
                }
                else {
                    Info("the wizard does not hold the quest yet");
                }
            }
            else {
                Ok($"the wizard holds the quest (level {level})");
            }

            // 3. The step.
            var goals = (quest.m_goals ?? []).Where(g => g is not null).ToList();
            var goal = string.IsNullOrEmpty(goalName) ? null : goals.FirstOrDefault(g => string.Equals(g.m_goalName, goalName, StringComparison.Ordinal));
            if (goal is null && held is not null) {
                goal = goals.FirstOrDefault(g => held.IsGoalActive(g.m_goalName));
            }

            if (goal is null) {
                if (!string.IsNullOrEmpty(goalName)) {
                    Bad(90, "data", $"step {goalName} is not one of the quest's steps",
                        $"The step {goalName} isn't in the quest's data, so the server can never mark it done.");
                }
                else if (held is not null) {
                    var anyActive = held.GoalProgress?.Any(g => g is not null && held.IsGoalActive(g.GoalName)) == true;
                    if (!anyActive) {
                        Bad(80, "server", "the wizard holds the quest but no step of it is open",
                            "The wizard holds the quest with no open step: the server finished the last step without moving the quest on (or never opened the first).");
                    }
                }

                return Finish(result, problems);
            }

            string name = goal.m_goalName;
            var instance = held?.GoalProgress?.FirstOrDefault(g => g is not null && string.Equals(g.GoalName, name, StringComparison.Ordinal));
            if (held is not null) {
                if (instance is null || !instance.DoesPlayerHaveGoal()) {
                    Bad(72, "server", $"step {name} is not open on the wizard yet",
                        "The bot is going for a step the server hasn't opened yet: the step before it is not counted as done.");
                }
                else if (instance.IsGoalCompleted()) {
                    Bad(75, "server", $"step {name} is done, but the quest has not moved on",
                        "The step is marked done but the quest did not move to its next step or finish: the server's step chain is stuck.");
                }
                else {
                    Ok($"step {name} is open on the wizard");
                }
            }

            Info($"step {name} is a {goal.GetType().Name.Replace("Template", "")}: \"{OverlayQuestTracker.StepTitle(goal)}\"");

            // 4. Where the step is.
            var stepZone = OverlayQuestTracker.StepZone(goal);
            var here = stepZone.Length == 0 || OverlayQuestTracker.SameZone(stepZone, zone) || OverlayQuestTracker.SameZone(zone, stepZone);
            if (stepZone.Length == 0) {
                Info("the quest data names no area for this step");
            }
            else if (here) {
                Ok($"the wizard is in the step's area ({stepZone})");
            }
            else {
                Info($"the step is in {stepZone}; the wizard is in {zone}{(wizard?.ZoneIsInstance == true ? " (an instance)" : "")}");
                if (QuestStepSpots.Zone(stepZone).Objects.Length == 0) {
                    Bad(60, "data", $"the step's area {stepZone} could not be read from the game files",
                        $"The step's area {stepZone} could not be found in the game files: the quest data may name an area that doesn't exist.");
                }

                var way = OverlayQuestRoute.Way(zone, stepZone, false);
                if (way.Count == 0) {
                    Bad(70, "route", $"no way from {zone} to {stepZone} is known",
                        $"There is no known way from {zone} to the step's area {stepZone}.");
                }
                else {
                    Info($"way there: {string.Join(" > ", way.Select(w => w.Zone))}");
                }
            }

            var spots = QuestArrowGoals.GetSpots(quest, goal).ToList();
            var spotsHere = spots.Where(s => OverlayQuestTracker.SameZone(s.Zone, zone) || OverlayQuestTracker.SameZone(zone, s.Zone)).ToList();
            if (spots.Count == 0) {
                Info("the game's quest arrow has no spot for this step (poi.dat)");
            }
            else {
                Info($"quest arrow spots: {spots.Count} in all, {spotsHere.Count} in this area" +
                     (spotsHere.Count == 0 ? "" : $", nearest {spotsHere.Min(s => Far(x, y, s.Spot.X, s.Spot.Y)):0} away"));
            }

            // 5. What the step asks, against the live zone and what the server sent the wizard's game.
            var live = MinimapIndex.In(zoneActor);
            Info($"the running area has {live.Count} things on its map ({live.Count(m => m.Kind is MinimapIndex.MOB or MinimapIndex.BOSS)} creatures, " +
                 $"{live.Count(m => m.Kind == MinimapIndex.NPC)} NPCs); the wizard's game was sent options by {options.Count}");

            switch (goal) {
                case PersonaGoalTemplate persona:
                    Persona(persona, stepZone, here, live, options, x, y, Ok, Info, Bad);
                    break;
                case BountyGoalTemplate bounty:
                    Creatures((bounty.m_npcAdjectives ?? []).Select(a => (string) a), here, live, instance, QuestMadlibs.GetGoalTotal(goal), "defeat", Ok, Info, Bad);
                    break;
                case ScavengeGoalTemplate scavenge: {
                    Creatures((scavenge.m_itemAdjectives ?? []).Select(a => (string) a), here, live, instance, QuestMadlibs.GetGoalTotal(goal), "collect from", Ok, Info, Bad);
                    var chance = goal.m_tallyCounter?.m_percentChance;
                    if (chance is <= 0) {
                        Bad(80, "data", "the step's drop chance is 0%", "The quest item's drop chance is 0% in the quest data, so it never drops.");
                    }

                    if (wizard is not null && wizard.BackpackFreeSlots <= 0) {
                        Bad(70, "character", "the backpack is full", "The wizard's backpack is full, so a quest item may not be given.");
                    }

                    break;
                }

                case WaypointGoalTemplate:
                    if (here && spotsHere.Count == 0) {
                        Bad(65, "data", "a go-to step with no spot in this area to go to",
                            "The step says to go somewhere in this area, but the game's quest arrow has no spot here, so the bot can't tell where.");
                    }

                    break;
                default:
                    Usable(here, spotsHere, live, options, Ok, Bad);
                    break;
            }

            // 6. The last thing the server said.
            if (!string.IsNullOrWhiteSpace(lastServerText)) {
                var said = lastServerText.Trim();
                var refusal = s_refusals.FirstOrDefault(r => said.Contains(r, StringComparison.OrdinalIgnoreCase));
                if (refusal is not null) {
                    Bad(45, "server", $"the server last said \"{Short(said)}\" (sounds like a refusal)",
                        $"The server refused something: \"{Short(said)}\".");
                }
                else {
                    Info($"the server last said \"{Short(said)}\"");
                }
            }
        }
        catch (Exception e) {
            Info($"a check could not be read: {e.Message}");
        }

        return Finish(result, problems);
    }

    private static void Character(Wizard wizard, ulong globalId, Action<string> ok, Action<string> info, Action<int, string, string, string> bad) {
        if (wizard is null) {
            bad(100, "character", "the wizard is not loaded", "The wizard isn't loaded on the server (logged out or not attached), so nothing it does counts.");
            return;
        }

        var stats = wizard.GameStats;
        if (stats is not null) {
            var max = stats.m_baseHitpoints + stats.m_bonusHitpoints;
            if (stats.m_currentHitpoints <= 0) {
                bad(50, "character", "the wizard has no health", "The wizard has no health left, so it can't win a fight.");
            }
            else {
                ok($"health {stats.m_currentHitpoints}/{max}, mana {stats.m_currentMana}/{stats.m_baseMana + stats.m_bonusMana}");
            }
        }

        if (globalId != 0 && CombatDuelComponent.IsInBattle(globalId)) {
            info("the wizard is in a battle right now");
        }

        info($"backpack free slots {wizard.BackpackFreeSlots}");
    }

    private static void Persona(PersonaGoalTemplate persona, string stepZone, bool here, List<MinimapIndex.Mark> live,
        IReadOnlyDictionary<ulong, List<(string Service, uint Index, string Display)>> options, float x, float y,
        Action<string> ok, Action<string> info, Action<int, string, string, string> bad) {
        string who = persona.m_personaName;
        var npc = QuestMadlibs.SpeakerTemplateOf(persona.m_personaName, 0);
        if (npc == 0) {
            bad(85, "data", $"the NPC \"{who}\" can't be matched to any NPC",
                $"The step's NPC \"{who}\" doesn't match any NPC in the game data, so nobody can be talked to for it.");
            return;
        }

        ok($"the NPC \"{who}\" is NPC template {npc}");
        if (stepZone.Length > 0) {
            var placed = QuestStepSpots.Zone(stepZone).Objects.Count(o => o.Template == npc);
            if (placed > 0) {
                ok($"the NPC is placed in {stepZone}'s zone file");
            }
            else {
                info($"the NPC is not placed in {stepZone}'s zone file (it may be spawned by a quest event)");
            }
        }

        if (!here) {
            return;
        }

        var standing = live.Where(m => m.Template == npc).OrderBy(m => Far(x, y, m.Object.m_location.X, m.Object.m_location.Y)).ToList();
        if (standing.Count == 0) {
            bad(78, "server", "the NPC is not spawned in the running area",
                "The step's NPC isn't spawned in the running area, so there's nobody to talk to (its spawn or quest event didn't happen).");
            return;
        }

        var nearest = standing[0];
        var distance = Far(x, y, nearest.Object.m_location.X, nearest.Object.m_location.Y);
        ok($"the NPC is spawned here, {distance:0} away");
        if (!options.TryGetValue(nearest.Object.m_globalID.Full, out var offered)) {
            if (distance < 150) {
                bad(82, "server", "the wizard stands by the NPC but the server sent no options for it",
                    "The wizard stands by the NPC but the server sent its game no options for it: the server doesn't think the NPC has anything for this wizard.");
            }
            else {
                info("no options from the NPC yet (the wizard isn't close enough to be sent them)");
            }

            return;
        }

        var services = string.Join(", ", offered.Select(o => o.Service).Distinct());
        if (offered.Any(o => o.Service.StartsWith("Quest", StringComparison.OrdinalIgnoreCase))) {
            ok($"the NPC offers the wizard: {services}");
        }
        else {
            bad(85, "server", $"the NPC offers only {services}, nothing for the quest",
                $"The NPC offers only {services}: the server doesn't link this step to this NPC (persona or area mismatch in the quest data, or the step isn't open).");
        }
    }

    private static void Creatures(IEnumerable<string> raw, bool here, List<MinimapIndex.Mark> live, GoalInstance instance, int total,
        string verb, Action<string> ok, Action<string> info, Action<int, string, string, string> bad) {
        var adjectives = (raw ?? []).Where(a => !string.IsNullOrEmpty(a)).ToArray();
        if (adjectives.Length == 0) {
            bad(88, "data", "the step names no creatures (no adjectives)",
                "The step names no creatures in the quest data, so no battle can count for it.");
            return;
        }

        info($"creatures to {verb}: {string.Join("/", adjectives)}; stand-ins by name: {string.Join(", ", BountyStandIns.NamesOf(adjectives).DefaultIfEmpty("none"))}");
        if (instance is { CurrentProgress: >= 0 and < int.MaxValue } progress) {
            info($"progress {progress.CurrentProgress}/{total}");
        }

        if (!here) {
            return;
        }

        var wanted = live.Where(m => m.Kind is MinimapIndex.MOB or MinimapIndex.BOSS
                                     && (m.Adjectives.Any(a => adjectives.Contains(a, StringComparer.Ordinal)) || BountyStandIns.Counts(adjectives, m.Template))).ToList();
        if (wanted.Count == 0) {
            bad(76, "server", "no matching creature is spawned in the running area",
                "No creature that counts for the step is spawned in this area (wrong spawn table, a spawn that needs a quest event, or the creatures have other tags).");
            return;
        }

        var busy = wanted.Count(m => CombatDuelComponent.IsInBattle(m.Object.m_globalID.Full));
        ok($"{wanted.Count} matching creatures here ({string.Join(", ", wanted.Select(m => m.Name).Distinct().Take(4))}), {busy} in battles");
        if (busy == wanted.Count) {
            info("every matching creature is in a battle right now");
        }
    }

    private static void Usable(bool here, List<(string Zone, Imcodec.Math.Vector3 Spot)> spotsHere, List<MinimapIndex.Mark> live,
        IReadOnlyDictionary<ulong, List<(string Service, uint Index, string Display)>> options, Action<string> ok, Action<int, string, string, string> bad) {
        if (!here) {
            return;
        }

        if (spotsHere.Count == 0) {
            bad(60, "data", "a use or click step with no quest arrow spot in this area",
                "The step is to use something here, but the game's quest arrow has no spot in this area, so there's nothing to find.");
            return;
        }

        var near = live.Where(m => spotsHere.Any(s => Far(s.Spot.X, s.Spot.Y, m.Object.m_location.X, m.Object.m_location.Y) < 250)).ToList();
        var clickable = near.Where(m => options.TryGetValue(m.Object.m_globalID.Full, out var o) && o.Count > 0).ToList();
        if (clickable.Count > 0) {
            ok($"at the arrow's spot the server offers: {string.Join(", ", clickable.Select(m => m.Name).Distinct().Take(4))}");
        }
        else if (options.Values.Any(o => o.Any(v => v.Service is "Interact" || v.Service.StartsWith("Quest", StringComparison.OrdinalIgnoreCase)))) {
            ok("the server sent something clickable for the quest nearby");
        }
        else {
            bad(62, "server", "nothing at the arrow's spot was offered to click",
                "At the quest arrow's spot the server offered nothing to click: the object isn't spawned, or it isn't linked to this step.");
        }
    }

    private static Result Finish(Result result, List<Problem> problems) {
        var ranked = problems.OrderByDescending(p => p.Weight).ToList();
        if (ranked.Count == 0) {
            result.LikelyCause = "Every check passed: the quest data, the wizard and the live server all look right for this step, " +
                                 "so the bot's own play (its path, clicks or timing) is the most likely problem, not the quest.";
            result.Confidence = "Medium";
            result.Completable = "Yes, by what the server shows";
            result.Category = "bot";

            return result;
        }

        var top = ranked[0];
        var gap = ranked.Count > 1 ? top.Weight - ranked[1].Weight : 100;
        result.LikelyCause = top.Cause + (ranked.Count > 1 ? $" (also seen: {string.Join(" ", ranked.Skip(1).Take(2).Select(p => p.Cause))})" : "");
        result.Category = top.Category;
        result.Confidence = top.Weight >= 80 && gap >= 5 ? "High" : top.Weight >= 60 ? "Medium" : "Low";
        result.Completable = top.Weight >= 85 ? "No, not while this stands" : top.Weight >= 70 ? "Probably not right now" : "Maybe";

        return result;
    }

    private static string Short(string text) => text.Length <= 140 ? text : text[..137] + "...";

    private static float Far(float x1, float y1, float x2, float y2) => MathF.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));

}
