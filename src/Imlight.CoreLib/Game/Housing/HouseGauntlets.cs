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
 *
 * ========================================================================
 * HOUSE GAUNTLETS
 * ========================================================================
 *
 * PURPOSE:
 * The housing gauntlets, Skeleton Key duels and rematches placed in a house (items with a HousingSigilBehaviorTemplate,
 * Niduus, 2026-10-23: "Make sure they have Sigils and programmed as best as you can get them"). A player who steps onto
 * one and stands still goes into a private copy of its dungeon; a one-shot (m_usedDisposableDungeonTemplateID set)
 * becomes its trophy where it stands once its boss is beaten.
 *
 * USAGE EXAMPLE:
 * if (HouseGauntlets.OnMove(house, charId, location) is { } pad) {
 *     var zone = HouseGauntlets.ZoneFor(pad.Sigil, level);
 *     HouseGauntlets.Begin(charId, house, pad, zone);
 *     // send the session a MSG_STARTSIGILENTRY to zone
 * }
 * HouseGauntlets.OnCombatWin(charId, zoneName, mobTemplateIds);
 *
 * NOTE:
 * - The dungeon is the template's m_sigilZoneInfoList: the first zone whose m_maximumLevel is at least the player's
 *   level (19, 39, ... 999 for the tiered gauntlets). A rematch lists its _STRD zone first and the other second, both
 *   999, so the first is used. The zones are ordinary zones with their own spawns, so the battles, the creatures and
 *   any boss cheats are the zone's own.
 * - Furniture is not a zone object, so the step onto a gauntlet is found from the player's position, like the
 *   teleporters (HouseTeleporters): within Housing.GauntletRadius of where it stands. The art the template shows is the
 *   client's own model; the server adds nothing to it.
 * - The countdown is the session's sigil countdown (ZoneService, MSG_STARTSIGILENTRY): the player is dismounted, any
 *   move stops it, and the trip goes into a fresh private copy. No timer shows over the furniture, because it is not a
 *   sigil object of the zone; the player is told in chat instead.
 * - A one-shot can be started by the owner of the house only; anyone else gets MSG_DISPOSABLEDUNGEONNOOWNER, the game's
 *   own "the owner must be present" message.
 * - A one-shot is used up by a won battle against a boss (NPCBehaviorTemplate.m_bossMob) in its dungeon: the zone the
 *   trip went to, or one under the same gauntlet folder. Its placed item takes the trophy template in the stored house,
 *   and the house's next blob shows it.
 *
 * TODO:
 * - A team going in together (the zone's dungeon sigils do it on a pad for several players).
 * - Which of a rematch's two zones the game picks.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/23/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Housing;

/// <summary>
/// A housing gauntlet placed in a house.
/// </summary>
/// <param name="GidId">The GIDID of the placed gauntlet.</param>
/// <param name="ItemId">The id of the placed item in the house.</param>
/// <param name="TemplateId">Its template.</param>
/// <param name="Position">Where it stands.</param>
/// <param name="Yaw">Which way it faces.</param>
/// <param name="Sigil">Its sigil behavior: the dungeons and the trophy.</param>
internal sealed record HouseGauntlet(uint GidId, uint ItemId, uint TemplateId, Vector3 Position, float Yaw, HousingSigilBehaviorTemplate Sigil) {

    /// <summary>Whether it is used up after one run.</summary>
    internal bool IsOneShot => Sigil.m_usedDisposableDungeonTemplateID != 0;

}

internal static class HouseGauntlets {

    private static readonly float s_radius = ConfigurationManager.GetValue("Housing.GauntletRadius", 120f);
    private static readonly float s_height = ConfigurationManager.GetValue("Housing.GauntletHeight", 150f);
    private static readonly TimeSpan s_runLifetime = TimeSpan.FromHours(ConfigurationManager.GetValue("Housing.GauntletRunHours", 4.0));

    // Template id to its sigil behavior, or null for a template that is not a gauntlet.
    private static readonly ConcurrentDictionary<uint, HousingSigilBehaviorTemplate> s_templates = new();

    // The gauntlets of each house, read again when the house's revision moves.
    private static readonly ConcurrentDictionary<ulong, (uint Revision, uint GidBase, IReadOnlyList<HouseGauntlet> Pads)> s_houses = new();

    // The gauntlet each character stands on, so one step starts one countdown.
    private static readonly ConcurrentDictionary<ulong, uint> s_standing = new();

    // The trip each character is on.
    private static readonly ConcurrentDictionary<ulong, Run> s_runs = new();

    private sealed record Run(ulong LotGid, uint ItemId, uint GidId, uint TemplateId, uint TrophyTemplateId, string Zone, string Family, DateTime Started);

    /// <summary>
    /// Whether a template is a housing gauntlet.
    /// </summary>
    /// <param name="templateId">The template of a placed item.</param>
    /// <param name="sigil">Its sigil behavior when this returns <c>true</c>.</param>
    internal static bool IsGauntlet(uint templateId, out HousingSigilBehaviorTemplate sigil) {
        sigil = templateId == 0 ? null : s_templates.GetOrAdd(templateId, id => CoreObjectFactory.GetCoreTemplate(id) is WizItemTemplate template
            ? (template.m_behaviors ?? []).OfType<HousingSigilBehaviorTemplate>().FirstOrDefault(s => s.m_sigilZoneInfoList is { Count: > 0 })
            : null);

        return sigil is not null;
    }

    /// <summary>
    /// Checks a character's position in a house and finds the gauntlet they just stepped onto.
    /// </summary>
    /// <param name="house">The house the character is in.</param>
    /// <param name="charId">The character.</param>
    /// <param name="location">Where the character stands.</param>
    /// <returns>The gauntlet, or <c>null</c> when they did not just step onto one.</returns>
    internal static HouseGauntlet OnMove(HouseState house, ulong charId, Vector3 location) {
        var pads = PadsOf(house);
        var pad = pads.Count == 0 ? null : pads.FirstOrDefault(candidate => IsOn(candidate.Position, location));
        if (pad is null) {
            s_standing.TryRemove(charId, out _);

            return null;
        }

        if (s_standing.TryGetValue(charId, out var standing) && standing == pad.GidId) {
            return null;
        }

        s_standing[charId] = pad.GidId;

        return pad;
    }

    /// <summary>
    /// Picks the dungeon of a gauntlet for a level: the first zone of the list whose maximum level is at least it.
    /// </summary>
    /// <returns>The zone, or <c>null</c> when the list names none.</returns>
    internal static string ZoneFor(HousingSigilBehaviorTemplate sigil, int level) {
        var zones = (sigil?.m_sigilZoneInfoList ?? [])
            .Where(info => info is not null && !string.IsNullOrEmpty(info.m_zoneName.ToString()))
            .ToList();
        var picked = zones.FirstOrDefault(info => level <= info.m_maximumLevel) ?? zones.LastOrDefault();

        return picked?.m_zoneName.ToString();
    }

    /// <summary>
    /// Remembers the trip a character starts, so a won boss battle in its dungeon can use up a one-shot.
    /// </summary>
    internal static void Begin(ulong charId, HouseState house, HouseGauntlet pad, string zone)
        => s_runs[charId] = new Run(house.LotGid, pad.ItemId, pad.GidId, pad.TemplateId, pad.Sigil.m_usedDisposableDungeonTemplateID,
            zone, FamilyOf(zone), DateTime.UtcNow);

    /// <summary>
    /// Uses up the one-shot a character went into when they win a battle against a boss in its dungeon.
    /// </summary>
    /// <param name="charId">The character who won.</param>
    /// <param name="zoneName">The zone they are in.</param>
    /// <param name="mobTemplateIds">The templates of the creatures they beat.</param>
    internal static void OnCombatWin(ulong charId, string zoneName, IReadOnlyCollection<ulong> mobTemplateIds) {
        if (!s_runs.TryGetValue(charId, out var run) || string.IsNullOrEmpty(zoneName)) {
            return;
        }

        if (DateTime.UtcNow - run.Started > s_runLifetime) {
            s_runs.TryRemove(charId, out _);

            return;
        }

        if (!InDungeon(run, zoneName)) {
            return;
        }

        var boss = (mobTemplateIds ?? []).FirstOrDefault(IsBoss);
        Logger.Information("Gauntlet: character {CharId} won a battle in {Zone}, the dungeon of gauntlet {TemplateId} of lot {LotGid} ({Mobs} creature(s), boss {Boss}).",
            Logger.Args(charId, zoneName, run.TemplateId, run.LotGid, mobTemplateIds?.Count ?? 0, boss == 0 ? "none" : boss.ToString()));
        if (boss == 0 || run.TrophyTemplateId == 0) {
            return;
        }

        s_runs.TryRemove(charId, out _);
        try {
            var house = HouseRegistry.Get(run.LotGid);
            if (house is null) {
                Logger.Warning("Gauntlet: lot {LotGid} could not be loaded, so gauntlet {ItemId} stays as it is.", Logger.Args(run.LotGid, run.ItemId));

                return;
            }

            var change = house.TrySwapTemplate(run.ItemId, run.TemplateId, run.TrophyTemplateId);
            if (!change.Done) {
                Logger.Warning("Gauntlet: gauntlet {ItemId}, template {TemplateId}, of lot {LotGid} could not become trophy {TrophyId} ({Result}).",
                    Logger.Args(run.ItemId, run.TemplateId, run.LotGid, run.TrophyTemplateId, change.Result));

                return;
            }

            Invalidate(run.LotGid);
            Logger.Information("Gauntlet: one-shot {ItemId}, template {TemplateId}, of lot {LotGid} is used up and is now trophy {TrophyId}, revision {Revision}.",
                Logger.Args(run.ItemId, run.TemplateId, run.LotGid, run.TrophyTemplateId, change.Revision));
        } catch (Exception exception) {
            Logger.Error("Gauntlet: turning gauntlet {ItemId} of lot {LotGid} into its trophy failed: {Reason}",
                Logger.Args(run.ItemId, run.LotGid, exception.Message));
        }
    }

    /// <summary>
    /// Forgets the gauntlets of a house, so the next move reads them again.
    /// </summary>
    internal static void Invalidate(ulong lotGid) => s_houses.TryRemove(lotGid, out _);

    /// <summary>
    /// Forgets where a character stands, when they leave the house.
    /// </summary>
    internal static void Leave(ulong charId) => s_standing.TryRemove(charId, out _);

    private static bool InDungeon(Run run, string zoneName)
        => string.Equals(zoneName, run.Zone, StringComparison.OrdinalIgnoreCase)
           || (run.Family is not null && zoneName.StartsWith(run.Family, StringComparison.OrdinalIgnoreCase));

    // The folder of a gauntlet's rooms: Housing_Gauntlet/KH_Gauntlet01/ for KH_Gauntlet01_01, Housing_Gauntlet_RPG/ for
    // HG_Gauntlet_RPG_Int01. A dungeon in a world's shared folder (Heroic_Dungeons_01, Polaris/Interiors) is one room.
    private static string FamilyOf(string zone) {
        var parts = (zone ?? "").Split('/');
        if (parts.Length >= 3 && !parts[^2].Equals("Interiors", StringComparison.OrdinalIgnoreCase)) {
            return string.Join('/', parts[..^1]) + "/";
        }

        return parts.Length >= 2 && parts[0].StartsWith("Housing_", StringComparison.OrdinalIgnoreCase) ? parts[0] + "/" : null;
    }

    private static bool IsBoss(ulong templateId)
        => (CoreObjectFactory.GetCoreTemplate(templateId) as GameObjectTemplate)?.m_behaviors?
            .OfType<NPCBehaviorTemplate>().Any(npc => npc.m_bossMob) == true;

    private static bool IsOn(Vector3 pad, Vector3 location) {
        var dx = location.X - pad.X;
        var dy = location.Y - pad.Y;

        return dx * dx + dy * dy <= s_radius * s_radius && Math.Abs(location.Z - pad.Z) <= s_height;
    }

    private static IReadOnlyList<HouseGauntlet> PadsOf(HouseState house) {
        var revision = house.Revision;
        var gidBase = house.GidBase;
        if (s_houses.TryGetValue(house.LotGid, out var cached) && cached.Revision == revision && cached.GidBase == gidBase) {
            return cached.Pads;
        }

        var snapshot = house.Snapshot();
        var pads = new List<HouseGauntlet>();
        for (var slot = 0; slot < snapshot.Slots.Count; slot++) {
            if (snapshot.Slots[slot] is { } item && IsGauntlet(item.TemplateId, out var sigil)) {
                pads.Add(new HouseGauntlet(unchecked(snapshot.GidBase + (uint) slot), item.Id, item.TemplateId,
                    new Vector3(item.X, item.Y, item.Z), item.Yaw, sigil));
            }
        }

        s_houses[house.LotGid] = (snapshot.Revision, snapshot.GidBase, pads);

        return pads;
    }

}
