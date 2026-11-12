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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Madlibs;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>
/// The overlay's minimap (Niduus, 2026-11-12: "mini map that is very detailed. Showing main quest objectives, Side
/// Quest zones, objectives and so on. Icons of where mobs are or boss locations as well with Sigils and so on"). Each
/// game window's overlay asks about once a second (op "map" on the overlay link, <see cref="OverlayLinks.MapAsync"/>);
/// the wizard's own session answers with where the wizard stands and faces, and what is around: creatures and bosses
/// (<see cref="MinimapIndex"/>), NPCs with a quest to give, quest steps here (main and side), sigils, doors, chests and
/// other wizards. Only what the game already shows the player; nothing is changed.
/// </summary>
internal static class OverlayMinimap {

    /// <summary>[Overlay] Minimap: the minimap may get its data from this server. On unless turned off; live.</summary>
    internal static bool On => ConfigurationManager.GetValue("Overlay.Minimap", true);

    /// <summary>[Overlay] MinimapRange: how far around the wizard things are sent (game units, 500 to 20000); live.</summary>
    internal static float Range => Math.Clamp(ConfigurationManager.GetValue("Overlay.MinimapRange", 6000f), 500f, 20000f);

    /// <summary>[Overlay] MinimapCreatures: creatures and bosses show on the minimap; live.</summary>
    internal static bool Creatures => ConfigurationManager.GetValue("Overlay.MinimapCreatures", true);

    /// <summary>[Overlay] MinimapStreets: the minimap gets each area's streets, grass, water and walls; live.</summary>
    internal static bool Streets => ConfigurationManager.GetValue("Overlay.MinimapStreets", true);

    private const int MAX_MARKS = 300;
    private const int MAX_QUESTS = 200;

    /// <summary>
    /// What one wizard's minimap shows, built on that wizard's own session. <paramref name="zone"/> is the zone actor the
    /// wizard is in.
    /// </summary>
    internal static JsonObject Build(Wizard wizard, IActorRef zone) {
        var here = wizard.Location;
        string zonePath = wizard.Zone ?? "";
        var range = Range;
        var map = new JsonObject {
            ["zone"] = zonePath,
            ["place"] = zonePath.Length == 0 ? "" : ZoneNames.Shown(zonePath),
            ["x"] = Round(here.X),
            ["y"] = Round(here.Y),
            ["yaw"] = Math.Round(wizard.Orientation.Z, 3),
            ["range"] = range,
        };

        var live = MinimapIndex.In(zone);
        var marks = new List<(JsonObject Mark, float Far, int Rank)>();
        void Add(string kind, float x, float y, string name, string quest = "", int level = 0, int rank = 5) {
            var far = Far(here.X, here.Y, x, y);
            if (far > range && rank > 1) {
                return;
            }

            var mark = new JsonObject { ["k"] = kind, ["x"] = Round(x), ["y"] = Round(y) };
            if (!string.IsNullOrEmpty(name)) {
                mark["n"] = name.Length > 60 ? name[..60] : name;
            }

            if (quest.Length > 0) {
                mark["q"] = quest;
            }

            if (level > 0) {
                mark["l"] = level;
            }

            marks.Add((mark, far, rank));
        }

        // The quests held now: their steps here get a mark, steps elsewhere a line under the map.
        var quests = new JsonArray();
        var wantedMobs = new List<(string[] Adjectives, string Kind)>();
        var wantedNpcs = new Dictionary<ulong, string>();
        var current = wizard.QuestBehavior?.CurrentQuestInstances?.Where(q => q is not null).ToList() ?? [];
        foreach (var held in current) {
            try {
                if (QuestTemplateCollection.GetQuestByName(held.QuestName) is not { } quest) {
                    continue;
                }

                var main = quest.m_mainline || ((string) quest.m_questName ?? "").Contains("MAIN", StringComparison.OrdinalIgnoreCase);
                var kind = main ? "main" : "side";
                var title = English(quest.m_questTitle, held.QuestName);
                foreach (var progress in held.GoalProgress ?? []) {
                    if (progress is null || !held.IsGoalActive(progress.GoalName)) {
                        continue;
                    }

                    var goal = quest.m_goals?.FirstOrDefault(g => g is not null && (string) g.m_goalName == progress.GoalName);
                    if (goal is null || goal.m_hideGoal) {
                        continue;
                    }

                    var step = StepTitle(goal);
                    var stepZone = StepZone(goal);
                    var isHere = stepZone.Length == 0 || SameZone(stepZone, zonePath);
                    var marked = false;
                    foreach (var (spotZone, spot) in QuestArrowGoals.GetSpots(quest, goal)) {
                        if (SameZone(spotZone, zonePath)) {
                            Add(kind, spot.X, spot.Y, step.Length > 0 ? step : title, kind, rank: 0);
                            marked = isHere = true;
                        }
                    }

                    if (goal is PersonaGoalTemplate persona) {
                        var npc = QuestMadlibs.SpeakerTemplateOf(persona.m_personaName, 0);
                        if (npc != 0) {
                            wantedNpcs.TryAdd(npc, kind);
                            if (!marked && live.FirstOrDefault(m => m.Template == npc) is { } standing) {
                                Add(kind, standing.Object.m_location.X, standing.Object.m_location.Y, step.Length > 0 ? step : title, kind, rank: 0);
                                marked = isHere = true;
                            }
                        }
                    }

                    if (goal is BountyGoalTemplate bounty) {
                        var adjectives = (bounty.m_npcAdjectives ?? []).Select(a => (string) a).Where(a => !string.IsNullOrEmpty(a)).ToArray();
                        if (adjectives.Length > 0) {
                            wantedMobs.Add((adjectives, kind));
                        }
                    }

                    if (quests.Count < MAX_QUESTS) {
                        quests.Add(new JsonObject {
                            ["t"] = title, ["main"] = main, ["step"] = step,
                            ["where"] = isHere ? "" : ZoneNames.Shown(stepZone), ["here"] = isHere,
                        });
                    }
                }
            }
            catch (Exception ex) {
                Logger.Debug("Minimap: quest {0} could not be read: {1}", Logger.Args(held.QuestName, ex.Message));
            }
        }

        // The main story's steps and this area's own; steps of side quests in other worlds stay off the map.
        // Main quests first, then the ones in this zone.
        map["quests"] = new JsonArray([.. quests.OfType<JsonObject>()
            .Where(q => (bool?) q["main"] == true || (bool?) q["here"] == true)
            .OrderByDescending(q => (bool?) q["main"] == true).ThenByDescending(q => (bool?) q["here"] == true)
            .Select(q => (JsonNode) q.DeepClone())]);

        // Who has a quest to give this wizard, among the NPCs here.
        var givers = new HashSet<ulong>();
        try {
            var npcsHere = live.Where(m => m.Kind == MinimapIndex.NPC).Select(m => m.Template).ToHashSet();
            foreach (var (quest, giver) in QuestService.QuestFinderGivers()) {
                if (npcsHere.Contains(giver) && !givers.Contains(giver) && MapService.IsOfferedTo(quest, wizard)) {
                    givers.Add(giver);
                }
            }
        }
        catch (Exception ex) {
            Logger.Debug("Minimap: quest givers could not be read: {0}", Logger.Args(ex.Message));
        }

        var creatures = Creatures;
        foreach (var mark in live) {
            var spot = mark.Object.m_location;
            switch (mark.Kind) {
                case MinimapIndex.MOB or MinimapIndex.BOSS:
                    if (!creatures) {
                        continue;
                    }

                    var wanted = wantedMobs.FirstOrDefault(w => mark.Adjectives.Any(a => w.Adjectives.Contains(a, StringComparer.Ordinal))
                                                            || Quests.BountyStandIns.Counts(w.Adjectives, mark.Template)).Kind ?? "";
                    Add(mark.Kind, spot.X, spot.Y, mark.Name, wanted, mark.Level, mark.Kind == MinimapIndex.BOSS ? 1 : wanted.Length > 0 ? 2 : 6);
                    break;
                case MinimapIndex.NPC:
                    var questFor = wantedNpcs.GetValueOrDefault(mark.Template, "");
                    var gives = givers.Contains(mark.Template);
                    Add(gives ? "giver" : MinimapIndex.NPC, spot.X, spot.Y, mark.Name, questFor, rank: gives || questFor.Length > 0 ? 2 : 7);
                    break;
                default:
                    Add(mark.Kind, spot.X, spot.Y, mark.Name, rank: mark.Kind == MinimapIndex.SIGIL ? 1 : 4);
                    break;
            }
        }

        map["marks"] = new JsonArray([.. marks.OrderBy(m => m.Rank > 2 ? 1 : 0).ThenBy(m => m.Far).Take(MAX_MARKS).Select(m => (JsonNode) m.Mark)]);

        return map;
    }

    /// <summary>Other wizards in the same zone as the map's own, added once their sessions answered.</summary>
    internal static void AddWizards(JsonObject map, ulong charId, IActorRef zone,
                                    IEnumerable<(ulong CharId, IActorRef Zone, string Name, float X, float Y)> others) {
        if (map["marks"] is not JsonArray marks || zone is null) {
            return;
        }

        var x = Number(map["x"], 0);
        var y = Number(map["y"], 0);
        var range = Number(map["range"], Range);
        foreach (var other in others.Where(o => o.CharId != charId && Equals(o.Zone, zone)).DistinctBy(o => o.CharId).Take(60)) {
            if (Far(x, y, other.X, other.Y) > range) {
                continue;
            }

            var team = Groups.GroupRegistry.AreGrouped(charId, other.CharId);
            marks.Add(new JsonObject { ["k"] = team ? "team" : "wizard", ["x"] = Round(other.X), ["y"] = Round(other.Y), ["n"] = other.Name ?? "" });
        }
    }

    private static float Far(float x1, float y1, float x2, float y2) => MathF.Sqrt(((x2 - x1) * (x2 - x1)) + ((y2 - y1) * (y2 - y1)));

    // A number this map was built with: stored as a whole number or not, so read as either (a (float) cast of a whole
    // number throws, and that hung up every overlay asking for a map: Niduus, 2026-11-12, "Waiting for the map...").
    private static float Number(JsonNode node, float fallback) {
        if (node is not JsonValue value) {
            return fallback;
        }

        if (value.TryGetValue<int>(out var whole)) {
            return whole;
        }

        if (value.TryGetValue<float>(out var single)) {
            return single;
        }

        return value.TryGetValue<double>(out var wide) ? (float) wide : fallback;
    }

    private static int Round(float value) => float.IsFinite(value) ? (int) MathF.Round(value) : 0;

    private static bool SameZone(string a, string b) {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) {
            return false;
        }

        if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        var contained = AccessPassManager.GetContainedZoneName(a.Trim());

        return contained is not null && string.Equals(contained, b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string StepZone(GoalTemplate goal) {
        string zone = goal is WaypointGoalTemplate { m_zoneTag.IsEmpty: false } waypoint ? (string) waypoint.m_zoneTag : (string) goal.m_destinationZone;
        if (string.IsNullOrWhiteSpace(zone)) {
            return "";
        }

        return AccessPassManager.GetContainedZoneName(zone.Trim()) ?? zone.Trim();
    }

    // The step in plain words, with who or what ("Talk to Headmaster Ambrose", "Defeat 3 Rattlebones"); the goal's own
    // title alone is only its verb ("Talk To", "Defeat": Niduus, 2026-11-12).
    private static string StepTitle(GoalTemplate goal) {
        var text = QuestGuide.StepText(goal);

        return string.IsNullOrWhiteSpace(text) ? English(goal.m_goalTitle, (string) goal.m_goalName ?? "") : English(text, text);
    }

    private static string English(string key, string fallback) {
        var text = string.IsNullOrEmpty(key) || key.Contains(' ') ? key : Locale.GetEnglishName(key);
        text = string.IsNullOrWhiteSpace(text) ? fallback ?? "" : text;
        text = text.Replace('\n', ' ').Replace('\r', ' ').Trim();

        return text.Length > 120 ? text[..120] : text;
    }

}
