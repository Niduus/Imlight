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
using System.Linq;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>
/// What the overlay's minimap can show in each running zone (Niduus, 2026-11-12: "mini map that is very detailed ...
/// Icons of where mobs are or boss locations as well with Sigils and so on"): every creature, boss, NPC, sigil, door and
/// chest, noted when its entity loads and dropped when it stops. Positions are read live from the objects, so a walking
/// mob moves on the map. Read by <see cref="OverlayMinimap"/>; nothing here changes the game.
/// </summary>
internal static class MinimapIndex {

    internal const string MOB = "mob";
    internal const string BOSS = "boss";
    internal const string NPC = "npc";
    internal const string SIGIL = "sigil";
    internal const string DOOR = "door";
    internal const string CHEST = "chest";

    /// <summary>One thing on the map: what kind it is, its name, and the live object whose spot it shows.</summary>
    internal sealed record Mark(string Kind, string Name, ulong Template, string[] Adjectives, CoreObject Object, int Level);

    private static readonly ConcurrentDictionary<IActorRef, ConcurrentDictionary<ZoneEntity, Mark>> s_zones = new();

    /// <summary>The marks in the zone run by <paramref name="zone"/> (a copy, safe to walk).</summary>
    internal static List<Mark> In(IActorRef zone)
        => zone is not null && s_zones.TryGetValue(zone, out var marks) ? [.. marks.Values] : [];

    /// <summary>An entity finished loading (on its own thread): noted when it is something a map shows.</summary>
    internal static void Loaded(ZoneEntity entity) {
        try {
            if (entity?.ActiveGameObject is null || entity.ZoneRef is null || Describe(entity) is not { } mark) {
                return;
            }

            s_zones.GetOrAdd(entity.ZoneRef, _ => new ConcurrentDictionary<ZoneEntity, Mark>(ReferenceEqualityComparer.Instance))[entity] = mark;
        }
        catch (Exception ex) {
            Logger.Debug("Minimap: could not note an object: {0}", Logger.Args(ex.Message));
        }
    }

    /// <summary>An entity stopped: it leaves the map.</summary>
    internal static void Stopped(ZoneEntity entity) {
        if (entity?.ZoneRef is { } zone && s_zones.TryGetValue(zone, out var marks)) {
            marks.TryRemove(entity, out _);
            if (marks.IsEmpty) {
                s_zones.TryRemove(new KeyValuePair<IActorRef, ConcurrentDictionary<ZoneEntity, Mark>>(zone, marks));
            }
        }
    }

    private static Mark Describe(ZoneEntity entity) {
        if (entity.Template is not GameObjectTemplate template) {
            return null;
        }

        var adjectives = (template.m_adjectiveList ?? []).Select(a => (string) a).Where(a => !string.IsNullOrEmpty(a)).ToArray();
        var id = (ulong) template.m_templateID;
        if (entity.GetComponentOfType<NpcComponent>() is { } npc) {
            var name = npc.NameOverride ?? NameOf(template);
            if (npc.IsMonster) {
                return new Mark(npc.IsBossMonster ? BOSS : MOB, name, id, adjectives, entity.ActiveGameObject, npc.Level);
            }

            return name.Length == 0 ? null : new Mark(NPC, name, id, adjectives, entity.ActiveGameObject, 0);
        }

        if (entity.GetComponentOfType<InteractDungeonSigilComponent>() is { } sigil) {
            var key = sigil.NpcNameKey;
            var where = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);

            return new Mark(SIGIL, string.IsNullOrWhiteSpace(where) ? "Sigil" : where.Trim(), id, adjectives, entity.ActiveGameObject, 0);
        }

        if (entity.GetComponentOfType<InteractDoorTeleportComponent>() is { } door) {
            var to = door.DestinationZone();

            return new Mark(DOOR, to.Length == 0 ? "Door" : "To " + World.ZoneNames.Shown(to), id, adjectives, entity.ActiveGameObject, 0);
        }

        if (entity.GetComponentOfType<StreetTeleporterComponent>() is not null || entity.GetComponentOfType<WorldTeleportDoorComponent>() is not null
            || entity.GetComponentOfType<CustomTeleporterComponent>() is not null) {
            var name = NameOf(template);

            return new Mark(DOOR, name.Length == 0 ? "Teleporter" : name, id, adjectives, entity.ActiveGameObject, 0);
        }

        if (entity.GetComponentOfType<SilverChestComponent>() is not null || entity.GetComponentOfType<WoodenChestComponent>() is not null) {
            return new Mark(CHEST, "Chest", id, adjectives, entity.ActiveGameObject, 0);
        }

        return null;
    }

    /// <summary>The name players see for a template, or empty.</summary>
    internal static string NameOf(GameObjectTemplate template) {
        string key = template?.m_displayName;
        var name = string.IsNullOrEmpty(key) ? "" : key.Contains(' ') ? key : Locale.GetEnglishName(key);

        return (name ?? "").Replace('\n', ' ').Trim();
    }

}
