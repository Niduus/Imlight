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
 * Niduus, 2026-10-23: "Make sure they have Sigils and programmed as best as you can get them"). Each one gets a dungeon
 * sigil pad in front of it, the glowing sigil of the game: a player who stands on it and presses X counts down into a
 * private copy of its dungeon, with up to three others. A one-shot (m_usedDisposableDungeonTemplateID set) becomes its
 * trophy where it stands once its boss is beaten.
 *
 * USAGE EXAMPLE:
 * HouseGauntlets.Sync(zoneActor, house);       // a player enters the house, or an object is placed, moved or picked up
 * if (HouseGauntlets.PadOf(padGlobalId) is { } pad) {
 *     var zone = HouseGauntlets.ZoneFor(pad.Gauntlet.Sigil, level);
 *     HouseGauntlets.Begin(charId, pad, zone);
 * }
 * HouseGauntlets.OnCombatWin(charId, zoneName, mobTemplateIds);
 *
 * NOTE:
 * - The client draws no sigil for a gauntlet: its template is a plain furniture model (the trophy pedestal of a
 *   Skeleton Key duel, SP_UltraDungeon_*_KH.nif) with a HousingSigilBehavior the client only reads as data
 *   (WizardGraphicalClient has no code for it but its property class), so the server puts the sigil there, as the
 *   game's own server does (its HousingSigilManager). The pad is the dungeon sigil octagon 107081 the towers use
 *   (MinigameSemiCircleSigil4Actor, so a team of 4 can go), added to the running house zone with
 *   MSG_ZONEOBJECTSCHANGED (ZoneObjects.MakeRuntimeObject) and made a press-X sigil by InteractDungeonSigilComponent.
 *   Round 6 watched for a step onto the gauntlet itself, which its solid collision keeps players off, so nothing fired.
 * - Where the pad goes (round 8, Niduus, 2026-10-23: "a lot of these sigils are wrong / also lacking or even inaccurate
 *   prompt position"): no gauntlet template names a sigil object, offset or facing (all 41 HousingSigilBehavior items of
 *   Root.wad were read: render, collision, furniture info, sounds, dependent resources; the models' .col.xml files are
 *   in HousingObject*-WorldData, which is not here). The semicircle pad's circles stand 270 from its centre in an arc, so
 *   round 7, which put the centre 250 to one side of the model, left the circles beside or under it, and the prompt (over
 *   the centre) away from them; its 200 reach also missed the circles. Now the centre goes to the model's front, with
 *   the model's yaw, so the arc stands before it, and the reach is the pad's own 370. The front distance of each model
 *   is a guess by its kind (s_fronts); Housing.GauntletSigilDistance.<template id>, Housing.GauntletSigilDistanceScale,
 *   Housing.GauntletSigilAngle, Housing.GauntletSigilRadius and Housing.GauntletSigilScale correct it without a rebuild.
 *   Its global id comes from the lot, the item, its template, its placement and its front, so a moved gauntlet's pad goes
 *   and comes back, and a trophy (no sigil behavior, or named _Trophy) has none.
 * - The dungeon is the template's m_sigilZoneInfoList: the first zone whose m_maximumLevel is at least the level of the
 *   player who starts the countdown (19, 39, ... 999 for the tiered gauntlets); the others go into that copy. A rematch
 *   lists its _STRD zone first and the other second, both 999, so the first is used. The zones are ordinary zones with
 *   their own spawns, so the battles, the creatures and the boss cheats are the zone's own.
 * - A one-shot can be started by the owner of the house only; anyone else gets MSG_DISPOSABLEDUNGEONNOOWNER, the game's
 *   own "the owner must be present" message, and can join the owner's countdown.
 * - A one-shot is used up by a won battle against a boss (NPCBehaviorTemplate.m_bossMob) in its dungeon: the zone the
 *   trip went to, or one under the same gauntlet folder. Its placed item takes the trophy template in the stored house,
 *   the house's next blob shows it, and its pad goes on the next sync.
 *
 * TODO:
 * - Check with a capture where the game puts the sigil of each gauntlet model, and its template.
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
using Akka.Actor;
using Imcodec.Math;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
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

/// <summary>
/// The sigil pad of a placed gauntlet.
/// </summary>
/// <param name="GlobalId">The pad's global id in the house zone.</param>
/// <param name="LotGid">The lot of the house.</param>
/// <param name="OwnerCharId">The owner of the house.</param>
/// <param name="Gauntlet">The gauntlet.</param>
internal sealed record HouseGauntletPad(ulong GlobalId, ulong LotGid, ulong OwnerCharId, HouseGauntlet Gauntlet);

internal static class HouseGauntlets {

    // The dungeon sigil octagon of the towers, a pad for a team of 4.
    private const uint PadTemplateId = 107081;
    private const string PadSigilType = "MinigameSemiCircleSigil4Actor";

    // Turns every pad around its gauntlet, in degrees, and scales every front distance (a correction without a rebuild).
    private static readonly float s_angle = ConfigurationManager.GetValue("Housing.GauntletSigilAngle", 0f);
    private static readonly float s_distanceScale = ConfigurationManager.GetValue("Housing.GauntletSigilDistanceScale", 1f);
    // 0: the pad's own reach (InteractDungeonSigilComponent, 370), which covers its circles 270 from its centre.
    private static readonly float s_padRadius = ConfigurationManager.GetValue("Housing.GauntletSigilRadius", 0f);
    private static readonly float s_padScale = ConfigurationManager.GetValue("Housing.GauntletSigilScale", 1f);

    // How far the front of each gauntlet model is from where it stands, by the model's file name (a part of it). The
    // pad's centre goes there, so its circles stand before the model. Guessed from the models' kinds and the screenshots
    // (Niduus, 2026-10-23); Housing.GauntletSigilDistance.<template id> overrides one.
    private static readonly (string Model, float Front)[] s_fronts = [
        ("SP_UltraDungeon_", 40f), ("SP_Rematch", 40f), ("SP_OneShot_", 60f), ("UnreadableBook", 40f),
        ("SP_RpgBlind", 120f), ("Gauntlet26_Teleporter", 100f), ("TicketBooth", 120f), ("DoomsdayKrok", 150f),
        ("PA_Fountain_Gauntlet", 150f), ("HighlandsGauntlet", 200f), ("SinbadRuins", 200f), ("SwampBoatDock", 200f),
        ("AV_CombatArena_Gauntlet", 200f), ("VA_ClockTower", 220f), ("TrainStation", 250f), ("MS_Pagoda_Gauntlet", 250f),
        ("lot_structure_tower", 250f), ("CL_ExplorersGauntlet", 300f), ("GH_LongHouse_Gauntlet", 350f),
    ];
    private const float DefaultFront = 150f;
    private static readonly ConcurrentDictionary<uint, float> s_frontOf = new();

    private static readonly TimeSpan s_runLifetime = TimeSpan.FromHours(ConfigurationManager.GetValue("Housing.GauntletRunHours", 4.0));
    private static readonly TimeSpan s_zoneLifetime = TimeSpan.FromHours(12);

    // Template id to its sigil behavior, or null for a template that is not a gauntlet.
    private static readonly ConcurrentDictionary<uint, HousingSigilBehaviorTemplate> s_templates = new();

    // The gauntlets of each house, read again when the house's revision moves.
    private static readonly ConcurrentDictionary<ulong, (uint Revision, uint GidBase, IReadOnlyList<HouseGauntlet> Pads)> s_houses = new();

    // Every pad made, by its global id, for the sigil that is pressed.
    private static readonly ConcurrentDictionary<ulong, HouseGauntletPad> s_pads = new();

    // The pads each running house zone has, so every visitor's sync sends only what changed.
    private sealed class ZonePads {
        internal readonly HashSet<ulong> Ids = [];
        internal DateTime Used = DateTime.UtcNow;
    }

    private static readonly ConcurrentDictionary<IActorRef, ZonePads> s_zones = new();

    // The trip each character is on.
    private static readonly ConcurrentDictionary<ulong, Run> s_runs = new();

    private sealed record Run(ulong LotGid, uint ItemId, uint GidId, uint TemplateId, uint TrophyTemplateId, string Zone, string Family, DateTime Started);

    /// <summary>
    /// Whether a template is a housing gauntlet.
    /// </summary>
    /// <param name="templateId">The template of a placed item.</param>
    /// <param name="sigil">Its sigil behavior when this returns <c>true</c>.</param>
    internal static bool IsGauntlet(uint templateId, out HousingSigilBehaviorTemplate sigil) {
        // A trophy (a used up one-shot or rematch) has no sigil behavior in the game data; one named as a trophy never gets a pad.
        sigil = templateId == 0 ? null : s_templates.GetOrAdd(templateId, id => CoreObjectFactory.GetCoreTemplate(id) is WizItemTemplate template
                && !template.m_objectName.ToString().EndsWith("_Trophy", StringComparison.OrdinalIgnoreCase)
            ? (template.m_behaviors ?? []).OfType<HousingSigilBehaviorTemplate>().FirstOrDefault(s => s.m_sigilZoneInfoList is { Count: > 0 })
            : null);

        return sigil is not null;
    }

    /// <summary>
    /// Gives a running house zone the sigil pads of the house's gauntlets as they stand now: new ones are added, ones of
    /// gauntlets that were moved, picked up or used up are taken away.
    /// </summary>
    /// <param name="zone">The house zone.</param>
    /// <param name="house">The house.</param>
    /// <param name="why">What led to the sync, for the log.</param>
    internal static void Sync(IActorRef zone, HouseState house, string why) {
        if (zone is null || zone.IsNobody() || house is null) {
            return;
        }

        var gauntlets = PadsOf(house);
        var wanted = new Dictionary<ulong, HouseGauntlet>();
        foreach (var gauntlet in gauntlets) {
            wanted[PadIdOf(house.LotGid, gauntlet)] = gauntlet;
        }

        Forget();
        var pads = s_zones.GetOrAdd(zone, _ => new ZonePads());
        var change = new ZONE_102_PROTOCOL.MSG_ZONEOBJECTSCHANGED();
        lock (pads) {
            pads.Used = DateTime.UtcNow;
            foreach (var id in pads.Ids.Where(id => !wanted.ContainsKey(id)).ToList()) {
                change.Removed.Add(id);
                pads.Ids.Remove(id);
                s_pads.TryRemove(id, out _);
            }

            foreach (var (id, gauntlet) in wanted) {
                if (!pads.Ids.Add(id)) {
                    continue;
                }

                var zoneName = ZoneFor(gauntlet.Sigil, 1);
                s_pads[id] = new HouseGauntletPad(id, house.LotGid, house.OwnerCharId, gauntlet);
                change.Added.Add(ZoneObjects.MakeRuntimeObject(KeyOf(house.LotGid, gauntlet), PadOf(gauntlet, zoneName)));
            }
        }

        if (change.Added.Count + change.Removed.Count == 0) {
            return;
        }

        zone.Tell(change, ActorRefs.NoSender);
        Logger.Information("Gauntlet: lot {LotGid} ({Why}) has {Count} gauntlet(s); its zone gets {Added} sigil pad(s) and loses {Removed}: {Pads}.",
            Logger.Args(house.LotGid, why, gauntlets.Count, change.Added.Count, change.Removed.Count,
                string.Join("; ", change.Added.Select(info => $"{info.m_zoneTag} at ({info.m_location.X:0}, {info.m_location.Y:0}, {info.m_location.Z:0})"))));
    }

    /// <summary>
    /// The gauntlet whose sigil pad has this global id, or <c>null</c> for any other pad.
    /// </summary>
    internal static HouseGauntletPad PadOf(ulong globalId) => s_pads.TryGetValue(globalId, out var pad) ? pad : null;

    /// <summary>
    /// The gauntlets of a house, for the log.
    /// </summary>
    internal static int CountIn(HouseState house) => house is null ? 0 : PadsOf(house).Count;

    // Where the pad of a gauntlet stands. The pad is the semicircle sigil of the towers (107081, MinigameSemiCircleSigil4Actor):
    // its four circles stand 270 from its centre, at 126 to 236 degrees of its sigil (ZoneService.TryGetSigilFaceSlot),
    // which is (-cos yaw, sin yaw) from the centre for a pad of that yaw. A gauntlet model faces that same way (round 7
    // put the centre along (-sin yaw, -cos yaw), and the circles showed beside the clock tower, Niduus, 2026-10-23), so
    // the pad takes the gauntlet's yaw and its centre goes to the model's front: the circles stand before the model, and
    // the game's "Press X" shows over the centre, at the model's door, as at a tower.
    private static ZoneObjects.ZoneObject PadOf(HouseGauntlet gauntlet, string zoneName) {
        var heading = gauntlet.Yaw + (s_angle * MathF.PI / 180f);
        var front = FrontOf(gauntlet.TemplateId);

        return new ZoneObjects.ZoneObject {
            Name = $"HouseGauntletSigil_{gauntlet.GidId}",
            TemplateID = PadTemplateId,
            X = gauntlet.Position.X - (MathF.Cos(heading) * front),
            Y = gauntlet.Position.Y + (MathF.Sin(heading) * front),
            Z = gauntlet.Position.Z,
            Yaw = heading,
            Scale = s_padScale,
            Sigil = new ZoneObjects.ZoneSigil {
                SigilType = PadSigilType,
                Tag = $"HouseGauntlet {gauntlet.TemplateId}",
                Radius = s_padRadius,
                DestinationZone = zoneName ?? "",
                DestinationLoc = "",
            },
        };
    }

    /// <summary>
    /// How far the front of a gauntlet's model is from where it stands.
    /// </summary>
    internal static float FrontOf(uint templateId)
        => s_frontOf.GetOrAdd(templateId, id => {
            var own = ConfigurationManager.GetValue($"Housing.GauntletSigilDistance.{id}", -1f);
            if (own >= 0) {
                return own;
            }

            var model = ModelOf(id);
            var front = s_fronts.FirstOrDefault(f => model.Contains(f.Model, StringComparison.OrdinalIgnoreCase)).Front;

            return (front > 0 ? front : DefaultFront) * s_distanceScale;
        });

    private static string ModelOf(uint templateId)
        => CoreObjectFactory.GetCoreTemplate(templateId) is WizItemTemplate template
            ? (template.m_behaviors ?? []).Select(b => b switch {
                // An AnimationBehaviorTemplate is a RenderBehaviorTemplate too.
                RenderBehaviorTemplate render => render.m_assetName.ToString(),
                _ => null,
            }).FirstOrDefault(name => !string.IsNullOrEmpty(name)) ?? ""
            : "";

    private static string KeyOf(ulong lotGid, HouseGauntlet gauntlet)
        => $"HouseGauntlet/{lotGid}/{gauntlet.ItemId}/{gauntlet.TemplateId}/{gauntlet.Position.X:0.#}/{gauntlet.Position.Y:0.#}/{gauntlet.Position.Z:0.#}/{gauntlet.Yaw:0.###}/{FrontOf(gauntlet.TemplateId)}/{s_angle}/v8";

    private static ulong PadIdOf(ulong lotGid, HouseGauntlet gauntlet) => ZoneObjects.RuntimeIdOf(KeyOf(lotGid, gauntlet));

    // Zones that have not synced for a long time are gone (a house zone stops when its last player leaves).
    private static void Forget() {
        var now = DateTime.UtcNow;
        foreach (var (zone, pads) in s_zones) {
            if (now - pads.Used > s_zoneLifetime) {
                s_zones.TryRemove(zone, out _);
            }
        }
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
    internal static void Begin(ulong charId, HouseGauntletPad pad, string zone) {
        var gauntlet = pad.Gauntlet;
        s_runs[charId] = new Run(pad.LotGid, gauntlet.ItemId, gauntlet.GidId, gauntlet.TemplateId,
            gauntlet.Sigil.m_usedDisposableDungeonTemplateID, zone, FamilyOf(zone), DateTime.UtcNow);
        Logger.Information("Gauntlet: character {CharId} goes into {Zone} from gauntlet {TemplateId} (item {ItemId}) of lot {LotGid}{OneShot}.",
            Logger.Args(charId, zone, gauntlet.TemplateId, gauntlet.ItemId, pad.LotGid,
                gauntlet.IsOneShot ? $", a one-shot that becomes trophy {gauntlet.Sigil.m_usedDisposableDungeonTemplateID} after its boss" : ""));
    }

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
