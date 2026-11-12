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
        void Add(string kind, float x, float y, string name, int level = 0, int rank = 5) {
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

            marks.Add((mark, far, rank));
        }

        // No quests here: quest tracking will be its own add-on (Niduus, 2026-11-12: "Do not track quests. That will be
        // a different plugin").
        var creatures = Creatures;
        foreach (var mark in live) {
            var spot = mark.Object.m_location;
            switch (mark.Kind) {
                case MinimapIndex.MOB or MinimapIndex.BOSS:
                    if (!creatures) {
                        continue;
                    }

                    Add(mark.Kind, spot.X, spot.Y, mark.Name, mark.Level, mark.Kind == MinimapIndex.BOSS ? 1 : 6);
                    break;
                case MinimapIndex.NPC:
                    Add(MinimapIndex.NPC, spot.X, spot.Y, mark.Name, rank: 7);
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

}
