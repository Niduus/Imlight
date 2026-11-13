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
/// (<see cref="MinimapIndex"/>), NPCs, sigils, doors, chests and other wizards. Quests are not tracked here; that is
/// for a separate add-on. Only what the game already shows the player; nothing is changed.
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

    /// <summary>
    /// [Overlay] MinimapSmoothing: how much the rough edges of paths, grass and water are rounded off (game units, 0 = the
    /// game's exact collision shapes, up to 400); live, for each overlay's next area.
    /// </summary>
    internal static float Smoothing => Math.Clamp(ConfigurationManager.GetValue("Overlay.MinimapSmoothing", 45f), 0f, 400f);

    private const int MAX_MARKS = 300;

    /// <summary>
    /// What one wizard's minimap shows, built on that wizard's own session. <paramref name="zone"/> is the zone actor the
    /// wizard is in; <paramref name="reach"/> is how far the overlay's full screen map shows (more than [Overlay]
    /// MinimapRange then).
    /// </summary>
    internal static JsonObject Build(Wizard wizard, IActorRef zone, float reach = 0) {
        var here = wizard.Location;
        string zonePath = wizard.Zone ?? "";
        var range = Math.Max(Range, Math.Clamp(reach, 0f, 20000f));
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
        void Add(string kind, float x, float y, string name, int level = 0, int rank = 5, ulong id = 0) {
            var far = Far(here.X, here.Y, x, y);
            if (far > range && rank > 1) {
                return;
            }

            var mark = new JsonObject { ["k"] = kind, ["x"] = Round(x), ["y"] = Round(y) };
            if (!string.IsNullOrEmpty(name)) {
                mark["n"] = name.Length > 60 ? name[..60] : name;
            }

            if (level > 0) {
                mark["l"] = level;
            }

            // Which thing it is, so the overlay can glide it from where it was (Niduus, 2026-11-12: "choppy live
            // updates of mobs. I want them smooth").
            if (id != 0) {
                mark["i"] = id.ToString(CultureInfo.InvariantCulture);
            }

            marks.Add((mark, far, rank));
        }

        // No quests here: quest tracking will be its own add-on (Niduus, 2026-11-12: "Do not track quests. That will be
        // a different plugin").
        var creatures = Creatures;
        foreach (var mark in live) {
            // Only what this wizard's game shows: no NPC held back for a quest, hidden by a dynamod or turned off.
            if (mark.Entity?.GetComponentOfType<Zone.Components.RenderComponent>() is { } render && !render.ShownTo(wizard)) {
                continue;
            }

            var spot = mark.Object.m_location;
            var id = (ulong) mark.Object.m_globalID.Full;
            switch (mark.Kind) {
                case MinimapIndex.MOB or MinimapIndex.BOSS:
                    if (!creatures) {
                        continue;
                    }

                    Add(mark.Kind, spot.X, spot.Y, mark.Name, mark.Level, mark.Kind == MinimapIndex.BOSS ? 1 : 6, id);
                    break;
                case MinimapIndex.WISP_HEALTH or MinimapIndex.WISP_MANA:
                    continue;
                case MinimapIndex.NPC:
                    Add(MinimapIndex.NPC, spot.X, spot.Y, mark.Name, rank: 7, id: id);
                    break;
                default:
                    Add(mark.Kind, spot.X, spot.Y, mark.Name, rank: mark.Kind == MinimapIndex.SIGIL ? 1 : 4, id: id);
                    break;
            }
        }

        map["marks"] = new JsonArray([.. marks.OrderBy(m => m.Rank > 2 ? 1 : 0).ThenBy(m => m.Far).Take(MAX_MARKS).Select(m => (JsonNode) m.Mark)]);
        if (creatures && MobSpawnAreas.OnMinimap) {
            map["spawns"] = Spawns(zone, live, here.X, here.Y, range);
        }

        return map;
    }

    private const int MAX_SPAWNS = 60;

    /// <summary>
    /// Each street's mob areas near the wizard (Niduus, 2026-11-13: "highlights of where mob spawns are on the street and
    /// where they end"; his drawing: one line round a street's whole group of mobs): for each group its outline (o, loops
    /// of x, y pairs, filled even-odd: the edge a mob can pull a wizard from), the walks of its creatures (w: each p, x, y pairs, and how it is
    /// walked), who spawns there (the names of the creatures walking it now first, so a custom or a randomized creature
    /// shows its own name), their levels, how many at once, and whether one is a boss.
    /// </summary>
    private static JsonArray Spawns(IActorRef zone, List<MinimapIndex.Mark> live, float x, float y, float range) {
        var spawns = new JsonArray();
        var creatures = live.Where(m => m.Kind is MinimapIndex.MOB or MinimapIndex.BOSS && !string.IsNullOrEmpty(m.Name)).ToList();
        foreach (var group in MobSpawnAreas.Groups(zone).Select(g => (Group: g, Far: g.DistanceTo(x, y)))
                     .Where(g => g.Far <= range).OrderBy(g => g.Far).Take(MAX_SPAWNS).Select(g => g.Group)) {
            var walking = creatures.Where(m => group.Contains(m.Object.m_location.X, m.Object.m_location.Y))
                .Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var names = walking.Concat(group.Names.Where(n => !walking.Contains(n, StringComparer.OrdinalIgnoreCase))).Take(4).ToList();
            var outline = new JsonArray();
            foreach (var loop in group.Outline.Take(12)) {
                var points = new JsonArray();
                for (var i = 0; i + 1 < loop.Length && i < 600; i += 2) {
                    points.Add(Round(loop[i]));
                    points.Add(Round(loop[i + 1]));
                }

                outline.Add(points);
            }

            var walks = new JsonArray();
            foreach (var area in group.Areas.Take(40)) {
                var points = new JsonArray();
                foreach (var (px, py) in area.Points.Take(80)) {
                    points.Add(Round(px));
                    points.Add(Round(py));
                }

                walks.Add(new JsonObject { ["w"] = area.How.ToString().ToLowerInvariant(), ["p"] = points });
            }

            var spawn = new JsonObject {
                ["i"] = string.Join("-", group.Areas.Select(a => a.Id).Order().Take(3)),
                ["o"] = outline,
                ["w"] = walks,
                ["c"] = group.Count,
            };

            if (names.Count > 0) {
                spawn["n"] = string.Join(", ", names);
            }

            var (min, max) = (group.Areas.Where(a => a.MaxLevel > 0).Select(a => a.MinLevel).DefaultIfEmpty(0).Min(), group.Areas.Max(a => a.MaxLevel));
            if (max > 0) {
                spawn["l"] = min == max ? $"{max}" : $"{min}-{max}";
            }

            if (group.Boss) {
                spawn["b"] = true;
            }

            spawns.Add(spawn);
        }

        return spawns;
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
            marks.Add(new JsonObject {
                ["k"] = team ? "team" : "wizard", ["x"] = Round(other.X), ["y"] = Round(other.Y), ["n"] = other.Name ?? "",
                ["i"] = "w" + other.CharId.ToString(CultureInfo.InvariantCulture),
            });
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

}
