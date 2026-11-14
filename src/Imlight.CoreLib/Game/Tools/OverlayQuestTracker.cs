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
using System.Linq;
using System.Text.Json.Nodes;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>
/// The overlay's Quest Tracker (Niduus, 2026-11-12: "It will show all your quest in catagory in the UI. Story, Main and
/// Side quests"). Each game window's tracker asks every couple of seconds (op "quests" on the overlay link,
/// <see cref="OverlayLinks.QuestsAsync"/>); the wizard's own session answers with every quest held now, its kind (story,
/// main or side, <see cref="QuestStory"/>), its world and its steps now with where they are. Picking a quest in the tracker
/// draws its way on the Minimap (<see cref="OverlayQuestRoute"/>). Only reads; nothing is changed.
/// </summary>
internal static class OverlayQuestTracker {

    /// <summary>[Overlay] QuestTracker: the Quest Tracker may get its quests from this server. On unless turned off; live.</summary>
    internal static bool On => ConfigurationManager.GetValue("Overlay.QuestTracker", true);

    private const int MAX_QUESTS = 120;
    private const int MAX_STEPS = 6;

    /// <summary>Every quest the wizard holds now, story first, then main, then side; the ones here before the rest.</summary>
    internal static JsonObject Build(Wizard wizard, Akka.Actor.IActorRef zone = null, string pick = "") {
        string zonePath = wizard.Zone ?? "";
        var rows = new List<(JsonObject Row, int Order, bool Here, int Level)>();
        foreach (var held in wizard.QuestBehavior?.CurrentQuestInstances?.Where(q => q is not null).ToList() ?? []) {
            try {
                if (QuestTemplateCollection.GetQuestByName(held.QuestName) is not { } quest) {
                    continue;
                }

                var kind = QuestStory.KindOf(quest);
                var steps = new JsonArray();
                var here = false;
                var elsewhere = "";
                foreach (var progress in held.GoalProgress ?? []) {
                    if (progress is null || !held.IsGoalActive(progress.GoalName) || steps.Count >= MAX_STEPS) {
                        continue;
                    }

                    var goal = quest.m_goals?.FirstOrDefault(g => g is not null && (string) g.m_goalName == progress.GoalName);
                    if (goal is null || goal.m_hideGoal) {
                        continue;
                    }

                    var stepZone = StepZone(goal);
                    var stepHere = stepZone.Length == 0 || SameZone(stepZone, zonePath)
                                   || QuestArrowGoals.GetSpots(quest, goal).Any(s => SameZone(s.Zone, zonePath));
                    here |= stepHere;
                    if (!stepHere && elsewhere.Length == 0) {
                        elsewhere = stepZone;
                    }

                    steps.Add(new JsonObject {
                        ["s"] = GameStepText(goal, progress), ["where"] = StepWhere(goal, stepZone), ["here"] = stepHere,
                    });
                }

                var row = new JsonObject {
                    ["id"] = held.QuestName, ["t"] = English(quest.m_questTitle, held.QuestName), ["k"] = kind,
                    ["w"] = QuestStory.WorldOf(quest), ["l"] = (int) quest.m_questLevel, ["steps"] = steps, ["here"] = here,
                };
                // The picked quest, with nothing to do here: the areas to go through to reach its step.
                if (!here && elsewhere.Length > 0 && string.Equals(pick, held.QuestName, StringComparison.OrdinalIgnoreCase)) {
                    var live = MinimapIndex.In(zone);
                    var spiralDoorHere = live.Any(m => m.Template == OverlayQuestRoute.SPIRAL_DOOR);
                    OverlayQuestRoute.NoteDoor(zonePath, live);
                    var exits = OverlayQuestRoute.ExitsHere(zonePath, live, wizard).Select(e => e.To);
                    row["way"] = OverlayQuestRoute.WayWords(zonePath, elsewhere, spiralDoorHere, exits);
                }

                var order = kind switch { QuestStory.INSTANCE => 0, QuestStory.STORY => 1, QuestStory.MAIN => 2, _ => 3 };
                rows.Add((row, order, here, quest.m_questLevel));
            }
            catch (Exception ex) {
                Logger.Debug("Quest tracker: quest {0} could not be read: {1}", Logger.Args(held.QuestName, ex.Message));
            }
        }

        return new JsonObject {
            ["zone"] = zonePath,
            ["place"] = zonePath.Length == 0 ? "" : ZoneNames.Shown(zonePath),
            ["quests"] = new JsonArray([.. rows.OrderBy(r => r.Order).ThenByDescending(r => r.Here).ThenBy(r => r.Level)
                .Take(MAX_QUESTS).Select(r => (JsonNode) r.Row)]),
        };
    }

    internal static bool SameZone(string a, string b) {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) {
            return false;
        }

        if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        var contained = AccessPassManager.GetContainedZoneName(a.Trim());

        return contained is not null && string.Equals(contained, b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The zone a step is in, or empty when the quest doesn't say.</summary>
    internal static string StepZone(GoalTemplate goal) {
        string zone = goal is WaypointGoalTemplate { m_zoneTag.IsEmpty: false } waypoint ? (string) waypoint.m_zoneTag : (string) goal.m_destinationZone;
        if (string.IsNullOrWhiteSpace(zone)) {
            return "";
        }

        return AccessPassManager.GetContainedZoneName(zone.Trim()) ?? zone.Trim();
    }

    /// <summary>The step in plain words, with who or what ("Talk to Headmaster Ambrose", "Defeat 3 Rattlebones").</summary>
    internal static string StepTitle(GoalTemplate goal) {
        // A creature step names the creature, as the game's quest book does ("Stop Morganthe", not "Defeat 1 creatures").
        if (goal is BountyGoalTemplate bounty && FoeNames(bounty) is { Count: > 0 } foes) {
            string title = goal.m_goalTitle;
            var verb = string.IsNullOrWhiteSpace(title) ? "Defeat" : English(title, title);
            if (verb.StartsWith("WizardQuestGoals", StringComparison.OrdinalIgnoreCase)) {
                verb = "Defeat";
            }

            var count = bounty.m_bountyTotal > 1 ? $"{bounty.m_bountyTotal} " : "";

            return English($"{verb} {count}{string.Join(" or ", foes.Take(2))}", "");
        }

        var text = QuestGuide.StepText(goal);

        return string.IsNullOrWhiteSpace(text) ? English(goal.m_goalTitle, (string) goal.m_goalName ?? "") : English(text, text);
    }

    // How the game words each kind of step in its quest helper (Locale Quest.lang), with the madlib tokens the server
    // sends the game for that step (QuestMadlibs.GetAppropriateMadlibBlockForGoal). The fallbacks are the same lines,
    // for when the game's text tables aren't loaded.
    private static readonly Dictionary<GOAL_TYPE, (string Key, string Fallback)> s_goalLines = new() {
        [GOAL_TYPE.GOAL_TYPE_BOUNTY] = ("Quest_BountyChatLabel", "#1:$GOAL_NAME$ #2:$GOAL_TALLYTEXT2$"),
        [GOAL_TYPE.GOAL_TYPE_BOUNTYCOLLECT] = ("Quest_BountyCollectChatLabel", "Collect #2:$GOAL_TALLYTEXT$"),
        [GOAL_TYPE.GOAL_TYPE_USAGE] = ("Quest_UsageChatLabel", "#1:$GOAL_NAME$ #2:$GOAL_TALLYTEXT$"),
        [GOAL_TYPE.GOAL_TYPE_SCAVENGE] = ("", "#1:$GOAL_NAME$ #2:$GOAL_TALLYTEXT$"),
        [GOAL_TYPE.GOAL_TYPE_WAYPOINT] = ("", "#1:$GOAL_NAME$ #2:$GOAL_TALLYTEXT$"),
        [GOAL_TYPE.GOAL_TYPE_PERSONA] = ("", "#1:$GOAL_NAME$ #2:$GOAL_FULLNAME$"),
    };

    private static readonly System.Text.RegularExpressions.Regex s_place = new(@"#\d+:");
    private static readonly System.Text.RegularExpressions.Regex s_key = new(@"^[A-Za-z][A-Za-z0-9]*_[A-Za-z0-9]+$");
    private static readonly System.Text.RegularExpressions.Regex s_token = new(@"\$GOAL_([A-Z0-9_]+)\$");

    /// <summary>
    /// A step as the game itself words it in its quest helper and quest book (Niduus, 2026-11-14: "Make sure quest tracker
    /// is accurate to real game quests description"): the game's own line for the step's kind, filled with the same
    /// words the server sends the game for it (the step's title, the person's name in the game's name format, what to
    /// defeat, collect or use), and the game's "N of M" count when the step needs more than one. Our own wording
    /// (<see cref="StepTitle"/>) only when the game's has nothing to show.
    /// </summary>
    internal static string GameStepText(GoalTemplate goal, GoalInstance progress = null) {
        try {
            var block = Madlibs.QuestMadlibs.GetAppropriateMadlibBlockForGoal(goal, progress);
            var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var arg in block?.m_madlibs ?? []) {
                switch (arg) {
                    case MadlibArgT_ByteString text when !string.IsNullOrEmpty(text.m_madlibToken):
                        args[text.m_madlibToken] = text.m_madlibArgument.ToString() ?? "";
                        break;
                    case MadlibArgT_int number when !string.IsNullOrEmpty(number.m_madlibToken):
                        args[number.m_madlibToken] = number.m_madlibArgument.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        break;
                }
            }

            if (args.Count > 0 && s_goalLines.TryGetValue(goal.m_goalType, out var line)) {
                var format = line.Key.Length > 0 ? Locale.GetEnglishName(line.Key) : "";
                var text = Fill(string.IsNullOrWhiteSpace(format) ? line.Fallback : format, args, 0);
                if (text.Length > 0) {
                    var total = Madlibs.QuestMadlibs.GetGoalTotal(goal);
                    if (total > 1 && goal.m_goalType is GOAL_TYPE.GOAL_TYPE_BOUNTY or GOAL_TYPE.GOAL_TYPE_BOUNTYCOLLECT
                            or GOAL_TYPE.GOAL_TYPE_USAGE or GOAL_TYPE.GOAL_TYPE_SCAVENGE) {
                        var counter = Locale.GetEnglishName("Quest_GoalCounter");
                        args["COUNT"] = Math.Max(progress?.CurrentProgress ?? 0, 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
                        args["TOTAL"] = total.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        text += $" ({Fill(string.IsNullOrWhiteSpace(counter) ? "#1:$GOAL_COUNT$ of #2:$GOAL_TOTAL$" : counter, args, 0)})";
                    }

                    return text.Length > 120 ? text[..120] : text;
                }
            }
        }
        catch (Exception) {
            // Our own wording below.
        }

        return StepTitle(goal);
    }

    // A game line with its tokens filled: each value is the game's text for it when it is a text key ("WizardQuestGoals_7"
    // is "Talk to"), a name format filled in turn ("NPCFormats_Goal_First_Last"), else as written.
    private static string Fill(string format, Dictionary<string, string> args, int depth) {
        var text = s_place.Replace(format ?? "", "");
        text = s_token.Replace(text, match => {
            var value = args.GetValueOrDefault(match.Groups[1].Value, "");
            if (string.IsNullOrWhiteSpace(value)) {
                return "";
            }

            var shown = value.Contains(' ') ? value : Locale.GetEnglishName(value);
            // A name format the game's tables here lack ("NPCFormats_Goal_First_Last", Niduus's Spectator on v0.10.855 showed
            // "Talk To NPCFormats_Goal_First_Last"): the person's first and last names, as the format puts them.
            if (value.StartsWith("NPCFormats_", StringComparison.Ordinal) && (string.IsNullOrWhiteSpace(shown) || shown == value)) {
                shown = string.Join(' ', new[] { "TITLE", "FIRSTNAME", "LASTNAME" }
                    .Select(t => args.GetValueOrDefault(t, ""))
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v.Contains(' ') ? v : Locale.GetEnglishName(v) is { Length: > 0 } n && n != v ? n : s_key.IsMatch(v) ? "" : v)
                    .Where(v => v.Length > 0));
            }

            // A text key the game's tables lack ("WizQst173685_00000030") shows nothing, never the key itself.
            shown = !string.IsNullOrWhiteSpace(shown) ? shown : s_key.IsMatch(value) ? "" : value;
            return depth < 2 && shown.Contains("$GOAL_", StringComparison.Ordinal) ? Fill(shown, args, depth + 1) : shown;
        });
        // Quotes left round an empty nickname go.
        text = text.Replace("\"\"", "").Replace('\n', ' ').Replace('\r', ' ');

        return System.Text.RegularExpressions.Regex.Replace(text, @"\s{2,}", " ").Trim();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> s_adjectiveNames = new(StringComparer.Ordinal);

    // The names of the creatures a step's adjectives stand for ("AZ-Tritops-Mummy-B.AdjRef" is Morganthe).
    private static List<string> FoeNames(BountyGoalTemplate bounty) {
        var names = new List<string>();
        foreach (var adjective in (bounty.m_npcAdjectives ?? []).Select(a => (string) a).Where(a => !string.IsNullOrEmpty(a))) {
            var name = s_adjectiveNames.GetOrAdd(adjective, a => {
                try {
                    var file = a.EndsWith(".AdjRef", StringComparison.OrdinalIgnoreCase) ? a[..^".AdjRef".Length] : a;
                    var id = CoreObjectFactory.IdOfFileName(file);

                    return id != 0 && CoreObjectFactory.GetCoreTemplate(id) is GameObjectTemplate template ? MinimapIndex.NameOf(template) : "";
                }
                catch (Exception) {
                    return "";
                }
            });
            if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>Where a step is, as the game's quest book says it: "Azteca / Xibalba".</summary>
    internal static string StepWhere(GoalTemplate goal, string stepZone) {
        if (stepZone.Length == 0) {
            return "";
        }

        string location = goal.m_locationName;
        var place = string.IsNullOrWhiteSpace(location) ? ZoneNames.Shown(stepZone) : English(location, location);
        var worldName = WorldName(stepZone);
        // The game writes some places as "World|Place" ("Grizzleheim|Savarstaad Pass").
        if (place.Contains('|')) {
            var parts = place.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 1) {
                worldName = parts[0];
            }

            place = parts.Length > 0 ? parts[^1] : "";
        }

        return string.Equals(worldName, place, StringComparison.OrdinalIgnoreCase) ? place : $"{worldName} / {place}";
    }

    /// <summary>A zone's world as players say it ("Wizard City" for WizardCity/WC_Hub).</summary>
    internal static string WorldName(string zone) {
        var world = (zone ?? "").Split('/')[0].Trim();
        return world.ToLowerInvariant() switch {
            "mooshu" => "Mooshu",
            "dragonspire" or "dragonspyre" => "Dragonspyre",
            _ => System.Text.RegularExpressions.Regex.Replace(world, "(?<=[a-z])(?=[A-Z])", " "),
        };
    }

    internal static string English(string key, string fallback) {
        var text = string.IsNullOrEmpty(key) || key.Contains(' ') ? key : Locale.GetEnglishName(key);
        text = string.IsNullOrWhiteSpace(text) ? fallback ?? "" : text;
        text = text.Replace('\n', ' ').Replace('\r', ' ').Trim();

        return text.Length > 120 ? text[..120] : text;
    }

}
