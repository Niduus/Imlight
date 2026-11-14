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
using Imlight.Common;
using Imlight.CoreLib.Shared.Packets;

namespace Imlight.CoreLib.Game.Battlegrounds;

/*
 * The control points of a Beastmoon arena and their capture.
 *
 * The arena's script waits for events that nothing in the zone posts, so the server posts them:
 *   EVENT_CTF_Red_0N / EVENT_CTF_Blue_0N   a team took point N: its flag (Team_Red / Team_Blue), the point's
 *                                          teleporter, alerts and pip spawns (Trigger_CP_Red_01, Trigger_CP1_Red, ...)
 *   EVENT_Disputed_0N                      points 1, 2 and 5 are contested: the flag goes to "Disputed" and the
 *                                          point's teleporters close (Trigger_CP_Contested_01)
 *   EVENT_CTF_Combat_0N                    the same for points 3 and 4, which name it a fight (Trigger_CP_Contested_03)
 * The arenas are read for their points when they load, so nothing is written per zone:
 * - a point N exists when a trigger fires on EVENT_CTF_Red_0N; its flag is the object that trigger's
 *   ResModifyTriggerObject names with "Flag" in it (BG_ControlPoint_Flag-001 instance, BG_ControlPoint_Flag_Sun);
 * - its capture area is the large sphere around the flag (gamedata.bin places the flag, volumes.xml the sphere;
 *   in all four arenas exactly one sphere, r 811 to 987, holds each flag: Activator Volume (4) for point 1 of
 *   BMH_Zone_02, AV_Sun in the others). Who stands in it is worked out from each wizard's position as they move
 *   (MSG_PLAYERMOVE), the same 3D distance the volume itself uses: its Enter_ and Exit_ events were not enough, since a
 *   wizard the server moves (the end of a PvE wave sends everyone to the base) does not leave the volume's own list;
 * - which "contested" event the point answers to is whichever of the two the script waits for.
 *
 * The capture rules are not in the client data (the flag's server behavior, class 1120896859, is not in the type dump),
 * so these are this server's, and the times are settings:
 * - a wizard counts at a point while on a team and neither defeated (DeathEffect) nor in the countdown (BG-PlanningPhase);
 * - one team alone at a point it does not hold takes it after [Beastmoon] CaptureSeconds; leaving resets the timer;
 * - both teams at a point make it contested: the point is held by no one until one team is there alone and takes it.
 * In PvE (BattlegroundTemplate m_pveMode) there is one team. There, "contested" is the point's creatures defending it: the
 * center point's EVENT_Disputed_05 spawns the wave's boss (BG_PVE_Zone_01 Trigger_CP_Contested_Spiral, and _Wave2,
 * _Wave3, each m_triggerMax 1), so a wizard walking into such a point posts it at once, and the point is not taken by
 * standing in it. The end of a wave (ResUnknown5ABB361D, Zone.Battleground.cs) clears that for the next wave.
 * The client also has a sigil proxy (BGSigilProxyBehavior, MSG_BGSigilProxyEvent) for a capture timer over the point;
 * its message format is unverified, so it is not sent yet: the flags change through the zone's own script.
 */
internal sealed class ControlPoints {

    internal static float CaptureSeconds => Math.Max(1f, ConfigurationManager.GetValue("Beastmoon.CaptureSeconds", 10f));
    // BattlegroundSigilTemplate m_tickRateMS (Sigils/BGPolymorphSigil.xml) is 250.
    internal static int TickMilliseconds => Math.Clamp(ConfigurationManager.GetValue("Beastmoon.TickMilliseconds", 250), 50, 5000);

    internal sealed class Point {

        internal int Number;
        internal string Name;
        internal string Flag;
        internal string Volume;
        internal float X, Y, Z, Radius;
        internal readonly List<string> ContestedEvents = [];

        internal Battlegrounds.Team Owner;
        internal Battlegrounds.Team Capturing;
        internal bool Contested;
        internal float Progress;

        // Who is inside: the player's actor, their character, and their object (for the events the point posts).
        internal readonly Dictionary<IActorRef, (ulong CharId, CoreObject Object)> Inside = [];

        // Wizards the server just moved away (the end of a PvE wave): their game's last moves from before the
        // teleport still arrive afterwards, so they count again only once a move of theirs is seen outside the point,
        // or after MUST_LEAVE_SECONDS (the late moves come within a second or two).
        internal readonly HashSet<IActorRef> MustLeave = [];
        internal DateTime MustLeaveUntil;

    }

    /// <summary>An event a point posts, with a wizard of the team that caused it (the script's steps run for them).</summary>
    internal readonly record struct Post(string EventName, IActorRef Player, CoreObject PlayerObject);

    /// <summary>A line for some players' chat.</summary>
    internal readonly record struct Tell(IEnumerable<IActorRef> Players, string Text);

    internal IReadOnlyList<Point> Points => _points;

    private const double MUST_LEAVE_SECONDS = 5;

    /// <summary>A PvE match: one team, and the points the script calls contested are defended by creatures.</summary>
    internal bool PvE { get; set; }

    private readonly List<Point> _points;

    private ControlPoints(List<Point> points) => _points = points;

    // The points' names by number, as BMH_Zone_03..05 tie their flags to them (BG_ControlPoint_Flag_Sun on
    // EVENT_CTF_Red_01, ... Flag_Spiral on _05) and the PvE arenas their name plates (BG_CPNamePlate-Sun in AV_Sun).
    private static readonly string[] s_pointWords = ["Sun", "Moon", "Eye", "Star", "Spiral"];

    /// <summary>The arena's control points, read from its triggers, volumes and objects; null if it has none.</summary>
    internal static ControlPoints Discover(string zonePath, WizZoneTriggers triggers, WizZoneVolumes volumes, WizZoneData zoneData) {
        if (triggers?.m_triggers is null || volumes?.m_volumes is null || zoneData?.m_objectList is null) {
            return null;
        }

        var points = new List<Point>();
        for (var n = 1; n <= 9; n++) {
            var captured = $"EVENT_CTF_Red_{n:00}";
            var onCapture = triggers.m_triggers.Where(t => Fires(t, captured)).ToList();
            if (onCapture.Count == 0) {
                continue;
            }

            var flag = onCapture
                .SelectMany(t => t.m_results?.m_results ?? [])
                .OfType<ResModifyTriggerObject>()
                .Select(r => (string) r.m_triggerObjName)
                .FirstOrDefault(name => name?.Contains("Flag", StringComparison.OrdinalIgnoreCase) == true);
            var flagInfo = flag is null ? null
                : zoneData.m_objectList.FirstOrDefault(o => string.Equals((string) o?.m_zoneTag, flag, StringComparison.OrdinalIgnoreCase));

            Volume volume = null;
            if (flagInfo is not null) {
                var at = flagInfo.m_location;
                volume = volumes.m_volumes
                    .Where(v => v is not null && v.m_radius > 300f)
                    .Select(v => (Volume: v, Distance: MathF.Sqrt(Square(v.m_locationX - at.X) + Square(v.m_locationY - at.Y) + Square(v.m_locationZ - at.Z))))
                    .Where(x => x.Distance <= x.Volume.m_radius)
                    .OrderBy(x => x.Distance)
                    .Select(x => x.Volume)
                    .FirstOrDefault();
            }

            // The PvE arenas have no flags (their capture changes name plates, teleporters and gates); every arena that
            // names its points calls the areas after them, in this order: AV_Sun is point 1 ... AV_Spiral point 5.
            if (volume is null && n <= s_pointWords.Length) {
                var named = "AV_" + s_pointWords[n - 1];
                volume = volumes.m_volumes.FirstOrDefault(v => v is not null && v.m_radius > 300f
                    && string.Equals((string) v.m_volumeName, named, StringComparison.OrdinalIgnoreCase));
                flag ??= s_pointWords[n - 1];
            }

            if (volume is null) {
                Logger.Warning("Beastmoon | {0}: no capture area found for point {1} ({2}); it cannot be taken.",
                    Logger.Args(zonePath, n, flag ?? "no flag"));

                continue;
            }

            var point = new Point {
                Number = n,
                Flag = flag,
                Name = PointName(flag, n),
                Volume = volume.m_volumeName,
                X = volume.m_locationX,
                Y = volume.m_locationY,
                Z = volume.m_locationZ,
                Radius = volume.m_radius,
            };

            foreach (var contested in new[] { $"EVENT_Disputed_{n:00}", $"EVENT_CTF_Combat_{n:00}" }) {
                if (triggers.m_triggers.Any(t => Fires(t, contested))) {
                    point.ContestedEvents.Add(contested);
                }
            }

            points.Add(point);
        }

        if (points.Count == 0) {
            return null;
        }

        Logger.Information("Beastmoon | {0}: control points {1}.", Logger.Args(zonePath,
            string.Join("; ", points.Select(p => $"{p.Number} {p.Name} ({p.Flag} in {p.Volume}, contested: {string.Join("/", p.ContestedEvents)})"))));

        return new ControlPoints(points);
    }

    private static bool Fires(Trigger trigger, string eventName)
        => trigger?.m_fireEvents?.Any(e => string.Equals((string) e, eventName, StringComparison.OrdinalIgnoreCase)) == true;

    private static float Square(float v) => v * v;

    // BG_ControlPoint_Flag_Sun -> "the Sun"; BMH_Zone_02 numbers its flags (Flag-001), so those are "point 1".
    private static string PointName(string flag, int number) {
        var tail = flag.Replace(" instance", "", StringComparison.OrdinalIgnoreCase);
        tail = tail[(tail.LastIndexOfAny(['_', '-']) + 1)..];

        return tail.Length > 0 && char.IsLetter(tail[0]) ? "the " + tail : $"point {number}";
    }

    private static readonly string[] s_sigils = ["Sun", "Moon", "Eye", "Star", "Spiral"];

    /// <summary>
    /// The points as the battleground map's icons (MSG_BGPOIUpdate, a BattlegroundPOI: the game's sigil pictures
    /// GUI/Minimap/BG_Sigil_Sun.dds and the others, where each point stands). Null when no point has a sigil.
    /// </summary>
    internal Imcodec.MessageLayer.IMessage PoiMessage() {
        var list = new List<BattlegroundPOIData>();
        foreach (var point in _points) {
            var sigil = s_sigils.FirstOrDefault(s => point.Name?.EndsWith(s, StringComparison.OrdinalIgnoreCase) == true
                                                    || point.Volume?.EndsWith("_" + s, StringComparison.OrdinalIgnoreCase) == true);
            if (sigil is null) {
                continue;
            }

            list.Add(new BattlegroundPOIData {
                m_poiType = 0,
                m_icon = $"GUI/Minimap/BG_Sigil_{sigil}.dds",
                m_objectID = new Imcodec.Types.GID(0),
                m_position = new Imcodec.Math.Vector3(point.X, point.Y, point.Z),
                m_yaw = 0,
            });
        }

        if (list.Count == 0 || !new Imcodec.ObjectProperty.ObjectSerializer(Versionable: false, Behaviors: Imcodec.ObjectProperty.SerializerFlags.None)
                .Serialize(new BattlegroundPOI { m_poiList = list }, 7, out var data)) {
            return null;
        }

        return new Imcodec.MessageLayer.Generated.WIZARD2_53_PROTOCOL.MSG_BGPOIUpdate { Data = data };
    }

    /// <summary>A wizard moved: they stand in the points whose sphere holds them, and in no other.</summary>
    internal void OnPlayerMoved(IActorRef player, CoreObject playerObject) {
        if (player is null || playerObject is not WizClientObject wizard) {
            return;
        }

        var at = playerObject.m_location;
        foreach (var point in _points) {
            var inside = Square(at.X - point.X) + Square(at.Y - point.Y) + Square(at.Z - point.Z) <= point.Radius * point.Radius;
            if (point.MustLeave.Count > 0 && DateTime.UtcNow >= point.MustLeaveUntil) {
                point.MustLeave.Clear();
            }

            if (inside && !point.MustLeave.Contains(player)) {
                point.Inside[player] = (wizard.m_characterId, playerObject);
            }
            else if (!inside) {
                point.Inside.Remove(player);
                point.MustLeave.Remove(player);
            }
        }
    }

    /// <summary>A wizard left the zone.</summary>
    internal void OnPlayerLeft(IActorRef player) {
        foreach (var point in _points) {
            point.Inside.Remove(player);
            point.MustLeave.Remove(player);
        }
    }

    /// <summary>Runs the capture timers for one tick; what the points post and say is added to the lists.</summary>
    internal void Tick(float seconds, List<Post> posts, List<Tell> tells) {
        foreach (var point in _points) {
            var red = Counting(point, Battlegrounds.Team.Red);
            var blue = Counting(point, Battlegrounds.Team.Blue);

            if (PvE && point.ContestedEvents.Count > 0) {
                var arrived = red.Count > 0 ? red : blue;
                if (arrived.Count > 0 && !point.Contested) {
                    point.Contested = true;
                    foreach (var e in point.ContestedEvents) {
                        posts.Add(new Post(e, arrived[0].Actor, arrived[0].Object));
                    }

                    tells.Add(new Tell(arrived.Select(x => x.Actor), $"{Capitalized(point.Name)} is defended! Defeat its guardians."));
                    Logger.Information("Beastmoon | {0} (point {1}) is defended; its creatures come.", Logger.Args(point.Name, point.Number));
                }

                continue;
            }

            if (red.Count > 0 && blue.Count > 0) {
                if (!point.Contested) {
                    point.Contested = true;
                    point.Owner = Battlegrounds.Team.None;
                    point.Capturing = Battlegrounds.Team.None;
                    point.Progress = 0;
                    var (actor, obj) = red[0];
                    foreach (var e in point.ContestedEvents) {
                        posts.Add(new Post(e, actor, obj));
                    }

                    tells.Add(new Tell(red.Concat(blue).Select(x => x.Actor), $"{Capitalized(point.Name)} is contested!"));
                }

                continue;
            }

            var team = red.Count > 0 ? Battlegrounds.Team.Red : blue.Count > 0 ? Battlegrounds.Team.Blue : Battlegrounds.Team.None;
            var here = team == Battlegrounds.Team.Red ? red : blue;
            if (team == Battlegrounds.Team.None || team == point.Owner) {
                point.Capturing = Battlegrounds.Team.None;
                point.Progress = 0;

                continue;
            }

            if (point.Capturing != team) {
                point.Capturing = team;
                point.Progress = 0;
                tells.Add(new Tell(here.Select(x => x.Actor),
                    $"Taking {point.Name} for the {team} team: stay {CaptureSeconds:0.#} seconds."));
            }

            point.Progress += seconds;
            if (point.Progress < CaptureSeconds) {
                continue;
            }

            point.Owner = team;
            point.Contested = false;
            point.Capturing = Battlegrounds.Team.None;
            point.Progress = 0;
            posts.Add(new Post($"EVENT_CTF_{team}_{point.Number:00}", here[0].Actor, here[0].Object));
            Logger.Information("Beastmoon | The {0} team took {1} (point {2}).", Logger.Args(team, point.Name, point.Number));
            // Each wizard who took it: the Beastmoon event counts it (CPT_ClaimCaptureArea, BattlegroundService).
            foreach (var taker in here) {
                taker.Actor?.Tell(new ZONE_102_PROTOCOL.MSG_BATTLEGROUNDCONTROL { Kind = BattlegroundControlKind.CapturedArea },
                    Akka.Actor.ActorRefs.NoSender);
            }
        }
    }

    /// <summary>
    /// After a PvE wave: the defended points are ready for the next wave's creatures, and nobody stands in any point any
    /// more (everyone was sent to the base; their Exit_ events come later, when their game reports the move). Without
    /// that, the wizard still counted in the center brought the next wave's boss at once.
    /// </summary>
    internal void NextWave(IEnumerable<IActorRef> movedAway) {
        var moved = movedAway?.ToList() ?? [];
        foreach (var point in _points) {
            point.Inside.Clear();
            point.MustLeave.Clear();
            point.MustLeave.UnionWith(moved);
            point.MustLeaveUntil = DateTime.UtcNow.AddSeconds(MUST_LEAVE_SECONDS);
            point.Capturing = Battlegrounds.Team.None;
            point.Progress = 0;
            if (point.ContestedEvents.Count > 0) {
                point.Contested = false;
            }
        }
    }

    // The wizards of a team who count at a point (see the header).
    private static List<(IActorRef Actor, CoreObject Object)> Counting(Point point, Battlegrounds.Team team) {
        var list = new List<(IActorRef, CoreObject)>();
        foreach (var (actor, (charId, obj)) in point.Inside) {
            var player = Battlegrounds.Of(charId);
            if (player is null || player.Team != team
                || Battlegrounds.HasEffect(charId, Battlegrounds.DEATH_EFFECT)
                || Battlegrounds.HasEffect(charId, Battlegrounds.PLANNING_EFFECT)) {
                continue;
            }

            list.Add((actor, obj));
        }

        return list;
    }

    private static string Capitalized(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>How the points stand, for `.bg points`.</summary>
    internal string Status()
        => string.Join(" | ", _points.Select(p => {
            var state = p.Contested ? "contested" : p.Owner == Battlegrounds.Team.None ? "nobody's" : $"{p.Owner}'s";
            var taking = p.Capturing == Battlegrounds.Team.None ? "" : $", {p.Capturing} taking it ({p.Progress:0.#}/{CaptureSeconds:0.#}s)";

            return $"{p.Number} {p.Name}: {state}, {p.Inside.Count} inside{taking}";
        }));

}
