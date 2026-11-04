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
 * STREET TELEPORTER
 * ========================================================================
 *
 * PURPOSE:
 * The game's own street teleporter stones (Niduus 2026-10-27: "existing street teleporters do not work"): a street's
 * hub of three stones (WC_Firecat_TeleporterA/B/C) and, far down the street, the "To Teleporter Hub" stone of each
 * (WC_Firecat_TeleporterA_ToMain). The game files give them nothing but a Press X; where they go is the server's
 * part. A stone sends the wizard to its partner in the same zone: the hub stone to its far stone and back.
 *
 * Each world names its pairs its own way (Niduus 2026-10-28: "street teleporters do not always work on streets where
 * they exist in vanilla"):
 *   WC_Firecat_TeleporterA            / WC_Firecat_TeleporterA_ToMain
 *   MS_NEW_Ham_TeleporterA            / MS_NEW_Ham_TeleporterA_ToMain
 *   KT_TeleporterB_Arena              / KT_TeleporterB_ToHub_Arena (and _BackToHub_, _ToMain_)
 *   MB_TeleportStoneAMain             / MB_TeleportStoneASecondary
 *   MS_TeleporterMainA                / MS_TeleporterSecondaryA
 *   DS_TeleportStoneA_Main            / DS_TeleportStoneA_Secondary
 *   CL_TeleportStoneBMain-Z02         / CL_TeleportStoneBSecondary-Z02
 *   ZF_TeleportStoneAMain_DrumJungle  / ZF_TeleportStoneASecondary_DrumJungle
 * A pair is the two stones whose names match once the far and hub words are taken out. Some names are misspelled in
 * the game files (KTl_TeleporterA_RoyalHall / KT_TeleporterA_ToMain_RoyalHall, KT_TeleporterA_DjeseritTomb /
 * KT_TeleporterA_ToMain_DkeserotTomb), so a stone with no exact match takes the other-side stone of its letter.
 * The server log says which stone went where ("Street teleporter: ... uses ... and goes to ...").
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.WizBang;
using Imlight.CoreLib.Game.World;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Zone.Components;

internal sealed partial class StreetTeleporterComponent(ZoneEntity entity) : ZoneEntityComponent(entity), IServiceComponent, IComponentFactory {

    // "Press X to Teleport", the stones' own line.
    private const string TELEPORT_TEXT_KEY = "GUI_00005578";

    // How long the teleport effect plays before the player is moved, as the game's own teleports do.
    private const int EFFECT_DELAY_IN_MS = 1500;

    // How far in front of the stone the wizard lands, clear of its collision.
    private const float ARRIVE_DISTANCE = 150f;

    // How far from the stone the walk point it lands on may be, how far above or below it, and how much further than
    // the nearest walk point another one may be and still be picked for being on the open side.
    private const float WALK_POINT_REACH = 800f;
    private const float WALK_POINT_HEIGHT = 200f;
    private const float WALK_POINT_SLACK = 150f;

    // How much room a landing spot needs from the nearest wall, and how far above the ground the wizard is put down.
    private const float WALK_POINT_ROOM = 50f;
    private const float LAND_LIFT = 10f;

    // How far a landing spot must be from any teleporter stone of the zone: the stones are game objects, not part of
    // the zone's walls, so the wall check cannot see them, and a walk point can sit right on one (Niduus 2026-11-03:
    // Royal Hall's Biti's Chamber stone C has a walk point 44 units from it and put him inside the obelisk).
    private const float STONE_GAP = 140f;

    // The walk grid is coarse (a point every ~480 units), so a far walk point is only the direction: the wizard lands
    // this far from the stone on the way to it, where that spot has room, like the game's own short step off a stone.
    private const float LAND_NEAR = 200f;

    // The words that make a stone the far one of its pair, and the hub stone's own word.
    [GeneratedRegex(@"BackToHub|ToHub|ToMain|Secondary", RegexOptions.IgnoreCase)]
    private static partial Regex FarWord();

    [GeneratedRegex(@"Main", RegexOptions.IgnoreCase)]
    private static partial Regex MainWord();

    // The stone's letter in its hub: TeleporterA, TeleportA, TeleportStoneAMain, TeleporterMainA, TeleportStoneZ03AMain.
    [GeneratedRegex(@"Teleport(?:er|Stone)?(?:Main|Secondary)?(?:Z\d+)?(?<letter>[A-D])(?=$|[_\-]|Main|Secondary)")]
    private static partial Regex StoneLetter();

    // How far apart the stones of one hub can stand.
    private const float HUB_SIZE = 2500f;

    private readonly record struct StoneName(string Key, char Letter, bool Far);

    public string ServiceName     => "Interact";
    public string NpcIcon         => (Entity.Template as GameObjectTemplate)?.m_sIcon;
    public string NpcNameKey      => (Entity.Template as GameObjectTemplate)?.m_displayName;
    public string NpcTextKey      => TELEPORT_TEXT_KEY;
    public WizBangs WizBang       => WizBangs.None;
    public string StateName       => null;
    public string InteractWizBang => null;
    public string DisplayKey      => null;

    private bool _partnerLooked;
    private CoreObjectInfo _partner;
    private List<Vector3> _stoneSpots;

    public static bool ShouldAttachToEntity(CoreTemplate template)
        => template is GameObjectTemplate go
           && go.m_behaviors?.OfType<InteractableBehaviorTemplate>().Any() == true
           && !go.m_behaviors.Any(b => b is NPCBehaviorTemplate)
           && Parse(go.m_objectName.ToString()) is not null
           // A stone the zone data already sends somewhere is the door component's.
           && !InteractDoorTeleportComponent.ShouldAttachToEntity(template);

    public IEnumerable<ServiceOptionBase> GetServiceOptions(Wizard playerCharacter) {
        // A Customs hub stone uses these templates too and goes where Niduus set it.
        if (CustomTeleporters.For(Entity.Info?.m_zoneTag) is not null || Partner() is null) {
            return [];
        }

        return [new InteractableOption { m_serviceName = ServiceName }];
    }

    public void OnServiceInteraction(IActorRef playerActor, Wizard playerCharacter, CoreObject playerObject, uint serviceOptionIndex) {
        var partner = Partner();
        if (partner is null || playerActor is null || CustomTeleporters.For(Entity.Info?.m_zoneTag) is not null) {
            return;
        }

        // The wizard lands a step from the partner stone toward the middle of the hub's stones: inside the ring at the
        // hub, back up the street at a far stone (the stones' own yaws do not say which side is open ground).
        var (towardX, towardY) = HubMiddle() ?? (partner.m_location.X + 1, partner.m_location.Y);
        var dx = towardX - partner.m_location.X;
        var dy = towardY - partner.m_location.Y;
        var length = MathF.Max(MathF.Sqrt((dx * dx) + (dy * dy)), 0.001f);
        var x = partner.m_location.X + (dx / length * ARRIVE_DISTANCE);
        var y = partner.m_location.Y + (dy / length * ARRIVE_DISTANCE);
        var z = partner.m_location.Z;

        // That guess can be inside a wall where the street bends (Niduus 2026-11-02, Firecat Alley), so the wizard
        // lands on the game's own walk grid instead, on a point clear of walls and fountains (see WalkPoint).
        if (WalkPoint(partner, x, y) is { } ground) {
            // A little above the point's ground height, so the wizard drops onto the floor rather than into it.
            (x, y, z) = (ground.X, ground.Y, ground.Z + LAND_LIFT);
            var (awayX, awayY) = (x - partner.m_location.X, y - partner.m_location.Y);
            if ((awayX * awayX) + (awayY * awayY) > 900f) {
                (dx, dy) = (awayX, awayY);
            }
        }

        // Facing on along that line, away from the stone (a wizard with yaw w faces (-sin w, -cos w)).
        var wizardYaw = MathF.Atan2(-dx, -dy);
        var spot = string.Join(",", new[] { x, y, z, wizardYaw }.Select(v => v.ToString(CultureInfo.InvariantCulture)));

        Logger.Information("Street teleporter: {0} uses {1} in {2} and goes to {3} at {4}.",
            Logger.Args(playerCharacter?.CharId ?? 0, (Entity.Template as GameObjectTemplate)?.m_objectName, Entity.Zone?.ZonePath,
                partner.m_zoneTag, spot));

        playerActor.Tell(new CHARACTER_103_PROTOCOL.MSG_DOTELEPORTEFFECTS(), ActorRefs.NoSender);
        var move = new CHARACTER_103_PROTOCOL.MSG_MOVEINZONE { Location = spot };
        Task.Delay(EFFECT_DELAY_IN_MS).ContinueWith(_ => playerActor.Tell(move, ActorRefs.NoSender));
    }

    // The other stone of the pair in this zone: the hub stone for a far one, the far one for a hub stone.
    private CoreObjectInfo Partner() {
        if (_partnerLooked) {
            return _partner;
        }

        _partnerLooked = true;
        var me = Parse((Entity.Template as GameObjectTemplate)?.m_objectName.ToString());
        var objects = Entity.Zone?.ZoneData?.m_objectList;
        if (me is not { } mine || objects is null) {
            return null;
        }

        var others = Stones(objects).Where(s => s.Name.Far != mine.Far).ToList();
        // The same name but for the far and hub words; else, for a misspelled name, the other side's stone of its
        // letter, the one whose name starts most like this one's (then the nearest); a stone with no letter takes the
        // zone's only other-side stone with none (CL_TeleportStoneMain_Z06 / CL_TeleportStonetoHub).
        var sameLetter = others.Where(s => s.Name.Letter == mine.Letter).ToList();
        _partner = others.FirstOrDefault(s => s.Name.Key == mine.Key).Info
            ?? (mine.Letter != '\0'
                ? sameLetter.OrderByDescending(s => SharedStart(s.Name.Key, mine.Key))
                    .ThenBy(s => DistanceSquared(s.Info, Entity.Info))
                    .FirstOrDefault().Info
                : sameLetter.Count == 1 ? sameLetter[0].Info : null);

        if (_partner is null) {
            Logger.Debug("Street teleporter {0} in {1} has no partner stone in the zone.",
                Logger.Args((Entity.Template as GameObjectTemplate)?.m_objectName, Entity.Zone?.ZonePath));
        }

        return _partner;
    }

    // A walk point of the zone's own walk grid by the stone, clear of the zone's walls and fountains (some grid points
    // are inside them: Niduus 2026-11-02, the Wormguts Tower stone put him in Firecat Alley's fountain). Of the clear
    // points on the stone's floor no more than a little further than the nearest clear one, the one nearest (guessX,
    // guessY); with no clear point, the one with the most room. Null when the zone has no walk grid or none is close.
    private Vector3? WalkPoint(CoreObjectInfo stone, float guessX, float guessY) {
        var zonePath = Entity.Zone?.ZonePath;
        var at = stone.m_location;
        var near = new List<(Vector3 Point, float Distance, float Room)>();
        var stones = StoneSpots();
        foreach (var point in ZoneNav.Points(zonePath)) {
            var distance = Flat(point.X - at.X, point.Y - at.Y);
            if (MathF.Abs(point.Z - at.Z) > WALK_POINT_HEIGHT || distance > WALK_POINT_REACH * WALK_POINT_REACH) {
                continue;
            }

            // Not on top of the stone itself or another stone (or hub centre piece) nearby.
            if (distance < STONE_GAP * STONE_GAP
                || stones.Any(s => MathF.Abs(s.Z - point.Z) <= WALK_POINT_HEIGHT && Flat(s.X - point.X, s.Y - point.Y) < STONE_GAP * STONE_GAP)) {
                continue;
            }

            var room = ZoneCollision.Clearance(zonePath, new Vector3(point.X, point.Y, point.Z + LAND_LIFT));
            near.Add((point, MathF.Sqrt(distance), room));
        }

        if (near.Count == 0) {
            return null;
        }

        var clear = near.Where(p => p.Room >= WALK_POINT_ROOM).ToList();
        if (clear.Count == 0) {
            return near.MaxBy(p => p.Room).Point;
        }

        var limit = clear.Min(p => p.Distance) + WALK_POINT_SLACK;
        var pick = clear.Where(p => p.Distance <= limit)
            .MinBy(p => Flat(p.Point.X - guessX, p.Point.Y - guessY));

        return Closer(zonePath, new Vector3(at.X, at.Y, at.Z), pick.Point, pick.Distance, stones) ?? pick.Point;
    }

    // The spot LAND_NEAR from the stone on the way to a farther walk point, or null when the walk point is near enough
    // already or that spot lacks room or is by another stone.
    private static Vector3? Closer(string zonePath, Vector3 stone, Vector3 point, float distance, List<Vector3> stones) {
        if (distance <= LAND_NEAR + 1f) {
            return null;
        }

        var t = LAND_NEAR / distance;
        var spot = new Vector3(
            stone.X + ((point.X - stone.X) * t),
            stone.Y + ((point.Y - stone.Y) * t),
            stone.Z + ((point.Z - stone.Z) * t));
        if (stones.Any(s => MathF.Abs(s.Z - spot.Z) <= WALK_POINT_HEIGHT
                && (s.X != stone.X || s.Y != stone.Y)
                && Flat(s.X - spot.X, s.Y - spot.Y) < STONE_GAP * STONE_GAP)) {
            return null;
        }

        return ZoneCollision.Clearance(zonePath, new Vector3(spot.X, spot.Y, spot.Z + LAND_LIFT)) >= WALK_POINT_ROOM
            ? spot
            : null;
    }

    private static float Flat(float dx, float dy) => (dx * dx) + (dy * dy);

    // Where the zone's teleporter objects stand: every stone and hub centre piece (KT_Teleporter_Hub and the like).
    private List<Vector3> StoneSpots() {
        if (_stoneSpots is not null) {
            return _stoneSpots;
        }

        var objects = Entity.Zone?.ZoneData?.m_objectList;
        _stoneSpots = objects is null
            ? []
            : objects.Where(o => o is not null && o.m_templateID != 0
                    && CoreObjectFactory.FileNameOf(o.m_templateID) is { } file
                    && file.Contains("Teleport", StringComparison.OrdinalIgnoreCase))
                .Select(o => new Vector3(o.m_location.X, o.m_location.Y, o.m_location.Z))
                .ToList();

        return _stoneSpots;
    }

    // The middle of the hub stones round whichever of the pair is the hub one, or null with fewer than two.
    private (float X, float Y)? HubMiddle() {
        var me = Parse((Entity.Template as GameObjectTemplate)?.m_objectName.ToString());
        var objects = Entity.Zone?.ZoneData?.m_objectList;
        var hub = me is { Far: false } ? Entity.Info : _partner;
        if (me is null || objects is null || hub is null) {
            return null;
        }

        var hubStones = Stones(objects).Where(s => !s.Name.Far && s.Name.Letter != '\0'
            && DistanceSquared(s.Info, hub) < HUB_SIZE * HUB_SIZE).Select(s => s.Info).ToList();
        if (hubStones.Count < 2) {
            return null;
        }

        return (hubStones.Average(o => o.m_location.X), hubStones.Average(o => o.m_location.Y));
    }

    // The zone's teleporter stones, by their template's file name.
    private static IEnumerable<(CoreObjectInfo Info, StoneName Name)> Stones(IEnumerable<CoreObjectInfo> objects)
        => objects.Where(o => o is not null && o.m_templateID != 0)
            .Select(o => (Info: o, Name: Parse(CoreObjectFactory.FileNameOf(o.m_templateID))))
            .Where(s => s.Name is not null)
            .Select(s => (s.Info, s.Name.Value));

    private static StoneName? Parse(string name) {
        if (string.IsNullOrEmpty(name) || !name.Contains("Teleport", StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        var far = FarWord().IsMatch(name);
        var letter = StoneLetter().Match(name) is { Success: true } match ? match.Groups["letter"].Value[0] : '\0';
        var rest = FarWord().Replace(name, "");
        if (!far && letter == '\0' && !MainWord().IsMatch(rest)) {
            return null;
        }

        var key = MainWord().Replace(rest, "").Replace("_", "").Replace("-", "").ToLowerInvariant();

        return new StoneName(key, letter, far);
    }

    private static int SharedStart(string a, string b) {
        var n = 0;
        while (n < a.Length && n < b.Length && a[n] == b[n]) {
            n++;
        }

        return n;
    }

    private static float DistanceSquared(CoreObjectInfo a, CoreObjectInfo b) {
        if (a is null || b is null) {
            return float.MaxValue;
        }

        var (dx, dy) = (a.m_location.X - b.m_location.X, a.m_location.Y - b.m_location.Y);

        return (dx * dx) + (dy * dy);
    }

}
