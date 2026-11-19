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
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Game.Ultimus;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// A dungeon's floors, the way a player plays them (Niduus, 2026-11-16: "Ultimus is not respecting floors. In order to TP
/// he has to complete the battles for that floor for the zone TP to unlock. This also has a puzzle in the dungeon ... he's
/// not doing any of the battles at all is the issue"). Read from the area's own files, shared by Ultimus and the Quest Team:
/// each room's battle circle (stepping on it starts the room's fight), each gate or door that opens later and what opens
/// it (a creature beaten, a spot stepped on, a lever pulled once a puzzle is right), and each puzzle plate with the state
/// it flips. <see cref="Next"/> gives the next floor duty near the wizard: the room's battle circle first, then the
/// creatures, then the spots and the puzzle, never a skip. Big Ben: every room's circle brings out the floor's elite whose
/// defeat opens the door by the teleporter up, and the plate room (Ice, Skull, Moon and Bird down) unlocks the lever.
/// </summary>
internal static class BotFloors {

    /// <summary>A room's battle circle: the trigger that starts its fight and the spot to step on.</summary>
    internal sealed record Circle(string Trigger, string Volume, float X, float Y, float Z = float.NaN);

    /// <summary>A puzzle plate: stepping on <see cref="Volume"/> turns <see cref="Object"/> from one state to another.</summary>
    internal sealed record Toggle(string Object, string From, string To, string Volume, float X, float Y, float Z = float.NaN);

    /// <summary>How a gate opens.</summary>
    internal enum Opener { Beat, Step, Click, Other }

    /// <summary>
    /// A gate or door that opens later: its trigger, its spot, what opens it, the spot to step on or the thing to click,
    /// and the object states a puzzle needs first.
    /// </summary>
    internal sealed record Gate(string Object, string Trigger, float X, float Y, Opener By, string Volume, float VX, float VY,
        string Lever, float LX, float LY, List<(string Object, string State)> Needs, string Through = "", float TX = float.NaN, float TY = float.NaN,
        float Z = float.NaN, float VZ = float.NaN, float LZ = float.NaN) {

        /// <summary>
        /// The puzzle's pieces are clicked themselves, in the order of <see cref="Needs"/>, with no lever after (the Vault of
        /// Ice's obelisks and braziers); <see cref="LX"/>, <see cref="LY"/> is the middle of the pieces, often a wing away.
        /// </summary>
        internal bool Pieces { get; init; }

        /// <summary>Where each piece of a <see cref="Pieces"/> puzzle stands.</summary>
        internal Dictionary<string, (float X, float Y, float Z)> PieceSpots { get; init; } = [];

        /// <summary>What must be set before each piece can be (another piece or a lever in a given state), by piece.</summary>
        internal Dictionary<string, List<(string Object, string State)>> PieceFirst { get; init; } = [];

        /// <summary>
        /// The dungeon quest that must be done before its opener works at all (the trigger's ReqEncounterComplete), or "".
        /// Emperor's Retreat: "Activator Volume (2)" opens "Keymaster'sDoor" only once KT-SPH5-C02-003 (the Colossus in the
        /// fourth tower) is complete.
        /// </summary>
        internal string Encounter { get; init; } = "";
    }

    /// <summary>What the area's files say about its floors.</summary>
    internal sealed record Book(List<Circle> Circles, List<Gate> Gates, List<Toggle> Toggles, Dictionary<string, string> Starts) {
        internal static readonly Book Empty = new([], [], [], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        internal bool IsEmpty => Circles.Count == 0 && Gates.Count == 0;

        /// <summary>Every teleporter on (not one back or out), by its volume's name and spot.</summary>
        internal List<(string Name, float X, float Y)> Ahead { get; init; } = [];

        /// <summary>Every teleporter back or out (an "Exit" one: down to the floor before, or out of the area), by its volume's name and spot.</summary>
        internal List<(string Name, float X, float Y)> Back { get; init; } = [];
    }

    /// <summary>One floor duty: step on a spot, fight by a spot, or click the lever there.</summary>
    internal sealed record Duty(string Kind, float X, float Y, string What, string Key, string Why);

    internal const string STEP = "step";
    internal const string FIGHT = "fight";
    internal const string CLICK = "click";

    // How near a room's things count as "this room" (Big Ben rooms are 6000 across with the circle in the middle).
    internal const float ROOM = 3200f;

    // How far back from a door, toward the wizard, its own side is looked at (OurSide).
    private const float DOOR_SIDE = 400f;
    // How far a puzzle gate may be from the plates and lever that open it.
    private const float PUZZLE = 6500f;
    // Close enough to a spot to have stepped on it.
    internal const float ON_SPOT = 120f;
    // How near a door its teleporter on may be (Big Ben: the pad up stands 190 past each floor's door).
    private const float THROUGH = 900f;
    // How near the straight way to a spot a quest-locked door must be to stand in it.
    private const float LOCKED_DOOR = 500f;

    // A thing this much nearer a locked door than the wizard is (a share of his distance to it) is behind that door.
    private const float LOCKED_NEAR_SHARE = 0.25f;
    // Tries per spot or lever before it is left be.
    internal const int TRIES = 3;

    private static readonly ConcurrentDictionary<string, Book> s_books = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The area's floor book (empty for an area without battle circles or gates).</summary>
    internal static Book BookOf(string zonePath) {
        if (string.IsNullOrEmpty(zonePath)) {
            return Book.Empty;
        }

        return s_books.GetOrAdd(zonePath, path => {
            try {
                if (!ResourceManager.TryLoadArchive(path, out var wad)) {
                    return Book.Empty;
                }

                return Build(UltimusPads.Read<WizZoneTriggers>(wad.OpenFile("triggers.xml")?.ToArray()),
                    UltimusPads.Read<WizZoneVolumes>(wad.OpenFile("volumes.xml")?.ToArray()),
                    UltimusPads.Read<WizZoneData>(ResourceManager.OpenZoneData(wad)?.ToArray()));
            }
            catch (Exception ex) {
                Logger.Debug("Bot floors: {0} could not be read: {1}", Logger.Args(path, ex.Message));
                return Book.Empty;
            }
        });
    }

    /// <summary>Reads the floors from an area's triggers, volumes and objects.</summary>
    internal static Book Build(WizZoneTriggers triggerFile, WizZoneVolumes volumeFile, WizZoneData zoneData) {
        var triggers = triggerFile?.m_triggers?.Where(t => t is not null).ToList() ?? [];
        var volumes = volumeFile?.m_volumes?.Where(v => v is not null).ToList() ?? [];

        Volume VolumeOf(string fire)
            => volumes.FirstOrDefault(v => v.m_enterEvents?.Any(e => (string) e == fire) == true || "Enter_" + (string) v.m_volumeName == fire);

        List<string> Fires(Trigger t) => [.. t.m_fireEvents?.Select(e => e.ToString()).Where(e => !string.IsNullOrEmpty(e)) ?? []];

        List<Result> Results(Trigger t) => [.. t.m_results?.m_results?.Where(r => r is not null) ?? []];

        List<Requirement> Needs(Trigger t) => [.. t.m_requirements?.m_requirements?.Where(r => r is not null) ?? []];

        var templates = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);

        Trigger Named(string name) => triggers.FirstOrDefault(x => string.Equals((string) x.m_triggerName, name, StringComparison.OrdinalIgnoreCase));

        // The pieces a puzzle needs, in the order a player sets them: down the "ActivateFrom_" chain to the trigger that
        // fires when a piece changes ("Sun1.Idle_On.EnterState") or on the pieces' own events ("ToFire"), with the states it
        // needs. When other checks of the same pieces need some of them set first (the Vault's obelisks: Snake, then Moon,
        // then Bird, then Sun), that order is kept: a piece more checks need comes first.
        List<(string Object, string State)> PiecesPuzzle(Trigger start) {
            var t = start;
            for (var hop = 0; hop < 4 && t is not null; hop++) {
                var needs = Needs(t).OfType<ReqTriggerObjectState>().Where(n => !n.m_applyNOT && !string.IsNullOrEmpty(n.m_triggerObjName))
                    .Select(n => ((string) n.m_triggerObjName, (string) n.m_triggerObjState)).ToList();
                var fires = Fires(t);
                // Needs on a chain of other triggers (the Vault's "Master Lock": the three locks broken) are other gates.
                if (needs.Count > 0 && fires.Any(f => f.StartsWith("ActivateFrom_", StringComparison.OrdinalIgnoreCase))) {
                    return [];
                }

                if (needs.Count > 0 && fires.Count > 0) {
                    var final = needs;
                    var rank = final.ToDictionary(n => n.Item1, _ => 0, StringComparer.OrdinalIgnoreCase);
                    foreach (var other in triggers) {
                        var met = Needs(other).OfType<ReqTriggerObjectState>().Where(n => !n.m_applyNOT
                            && final.Any(f => string.Equals(f.Item1, (string) n.m_triggerObjName, StringComparison.OrdinalIgnoreCase)
                                              && string.Equals(f.Item2, (string) n.m_triggerObjState, StringComparison.OrdinalIgnoreCase))).ToList();
                        foreach (var n in met) {
                            rank[(string) n.m_triggerObjName]++;
                        }
                    }

                    return [.. final.OrderByDescending(n => rank[n.Item1])];
                }

                t = fires.FirstOrDefault(f => f.StartsWith("ActivateFrom_", StringComparison.OrdinalIgnoreCase)) is { } up ? Named(up["ActivateFrom_".Length..]) : null;
            }

            return [];
        }

        // What a piece needs before it can be set (its own options' requirements): the Vault's Bug obelisk lights only with
        // Lever1 Off, the Skull one only with Lever1 On, so the lever is pulled between them, as a player finds.
        List<(string Object, string State)> PieceNeeds(string piece, string state) {
            try {
                if (!templates.TryGetValue(piece, out var id) || CoreObjectFactory.GetCoreTemplate(id) is not GameObjectTemplate template) {
                    return [];
                }

                return [.. template.m_behaviors?.OfType<InteractableBehaviorTemplate>().SelectMany(b => b.m_interactOptions ?? [])
                    .OfType<InteractStateOptionTemplate>()
                    .Where(o => string.Equals(o.m_enterState.ToString(), state, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(o => o.m_requirements?.m_requirements ?? []).OfType<ReqTriggerObjectState>()
                    .Where(r => !r.m_applyNOT && !string.IsNullOrEmpty(r.m_triggerObjName))
                    .Select(r => ((string) r.m_triggerObjName, (string) r.m_triggerObjState)) ?? []];
            }
            catch (Exception ex) {
                Logger.Debug("Bot floors: the piece {0} could not be read: {1}", Logger.Args(piece, ex.Message));
                return [];
            }
        }

        // Start states: the trigger objects, then the area's object list.
        var starts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var spots = new Dictionary<string, (float X, float Y, float Z)>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in triggers) {
            if (t.m_triggerObjInfo is { } info && info.m_templateID != 0) {
                foreach (string name in new[] { (string) info.m_zoneTag, (string) info.m_triggerObjName }) {
                    if (!string.IsNullOrEmpty(name)) {
                        if (!string.IsNullOrEmpty(info.m_startState)) {
                            starts.TryAdd(name, info.m_startState);
                        }

                        spots.TryAdd(name, (info.m_location.X, info.m_location.Y, info.m_location.Z));
                    }
                }
            }
        }

        foreach (var o in zoneData?.m_objectList?.Where(o => o is not null) ?? []) {
            string tag = o.m_zoneTag;
            if (string.IsNullOrEmpty(tag)) {
                continue;
            }

            if (!string.IsNullOrEmpty(o.m_startState)) {
                starts.TryAdd(tag, o.m_startState);
            }

            spots.TryAdd(tag, (o.m_location.X, o.m_location.Y, o.m_location.Z));
            templates.TryAdd(tag, o.m_templateID.Full);
        }

        // Battle circles.
        var circles = new List<Circle>();
        foreach (var t in triggers.Where(t => Results(t).Any(r => r is ResInitiateCombat))) {
            foreach (var fire in Fires(t)) {
                if (VolumeOf(fire) is { } v) {
                    circles.Add(new Circle((string) t.m_triggerName ?? "", (string) v.m_volumeName ?? "", v.m_locationX, v.m_locationY, v.m_locationZ));
                }
            }
        }

        // Puzzle plates: a volume that flips an object it needs in a state to another state.
        var toggles = new List<Toggle>();
        foreach (var t in triggers) {
            var needs = Needs(t).OfType<ReqTriggerObjectState>().ToList();
            foreach (var set in Results(t).OfType<ResModifyTriggerObject>()) {
                string obj = set.m_triggerObjName;
                var from = needs.FirstOrDefault(n => string.Equals((string) n.m_triggerObjName, obj, StringComparison.OrdinalIgnoreCase));
                if (from is null || from.m_applyNOT || string.IsNullOrEmpty(obj)) {
                    continue;
                }

                foreach (var fire in Fires(t)) {
                    if (VolumeOf(fire) is { } v) {
                        toggles.Add(new Toggle(obj, from.m_triggerObjState, set.m_triggerObjState, (string) v.m_volumeName ?? "", v.m_locationX, v.m_locationY, v.m_locationZ));
                    }
                }
            }
        }

        // Gates and doors that open later, and what opens them.
        var gates = new List<Gate>();
        foreach (var t in triggers) {
            foreach (var set in Results(t).OfType<ResModifyTriggerObject>()) {
                string obj = set.m_triggerObjName;
                if (string.IsNullOrEmpty(obj) || !Opens(set.m_triggerObjState) || OverlayQuestPath.IsLiftState(set.m_triggerObjState)
) {
                    continue;
                }

                if (!spots.TryGetValue(obj, out var at)) {
                    continue;
                }

                var gate = new Gate(obj, (string) t.m_triggerName ?? "", at.X, at.Y, Opener.Other, "", float.NaN, float.NaN, "", float.NaN, float.NaN, [], Z: at.Z);
                foreach (var fire in Fires(t)) {
                    if (string.Equals(fire, "Monster_Killed", StringComparison.OrdinalIgnoreCase)) {
                        gate = gate with { By = Opener.Beat };
                        break;
                    }

                    if (VolumeOf(fire) is { } v) {
                        gate = gate with { By = Opener.Step, Volume = (string) v.m_volumeName ?? "", VX = v.m_locationX, VY = v.m_locationY, VZ = v.m_locationZ };
                        break;
                    }

                    // "Lever.Down.EnterState": the lever itself turned.
                    var bits = fire.Split('.');
                    if (bits.Length == 3 && bits[2].Equals("EnterState", StringComparison.OrdinalIgnoreCase) && spots.TryGetValue(bits[0], out var lever)) {
                        gate = gate with { By = Opener.Click, Lever = bits[0], LX = lever.X, LY = lever.Y, LZ = lever.Z };
                        break;
                    }

                    // "ActivateFrom_X": another trigger X, fired by a click on a lever, enabled once its puzzle is right.
                    if (fire.StartsWith("ActivateFrom_", StringComparison.OrdinalIgnoreCase)
                        && triggers.FirstOrDefault(x => string.Equals((string) x.m_triggerName, fire["ActivateFrom_".Length..], StringComparison.OrdinalIgnoreCase)) is { } from) {
                        if (Fires(from).Select(VolumeOf).FirstOrDefault(v => v is not null) is { } step) {
                            gate = gate with { By = Opener.Step, Volume = (string) step.m_volumeName ?? "", VX = step.m_locationX, VY = step.m_locationY, VZ = step.m_locationZ };
                            break;
                        }

                        // A puzzle whose pieces are clicked themselves (the Vault of Ice: "Break BirdLock" fires from
                        // "BirdRoomVictory", which fires from "SunCheck" once the four obelisks are lit in order).
                        if (PiecesPuzzle(from) is { Count: > 0 } pieces && pieces.All(n => spots.ContainsKey(n.Object))) {
                            var (px, py, pz) = (pieces.Average(n => spots[n.Object].X), pieces.Average(n => spots[n.Object].Y), pieces.Average(n => spots[n.Object].Z));
                            var first = pieces.ToDictionary(n => n.Object, n => PieceNeeds(n.Object, n.State), StringComparer.OrdinalIgnoreCase);
                            var spotsOf = pieces.Select(n => n.Object).Concat(first.Values.SelectMany(l => l.Select(f => f.Object)))
                                .Where(spots.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToDictionary(o => o, o => spots[o], StringComparer.OrdinalIgnoreCase);
                            gate = gate with {
                                By = Opener.Click, LX = px, LY = py, LZ = pz, Needs = pieces, Pieces = true, PieceSpots = spotsOf, PieceFirst = first,
                            };
                            break;
                        }

                        var enabler = triggers.FirstOrDefault(p => Results(p).OfType<ResPostEvent>()
                            .Any(r => string.Equals((string) r.m_eventName, "Enable_" + (string) from.m_triggerName, StringComparison.OrdinalIgnoreCase)));
                        var needs = enabler is null
                            ? []
                            : Needs(enabler).OfType<ReqTriggerObjectState>().Where(n => !n.m_applyNOT)
                                .Select(n => ((string) n.m_triggerObjName, (string) n.m_triggerObjState)).ToList();
                        var near = needs.Count > 0 && toggles.FirstOrDefault(g => needs.Any(n => n.Item1 == g.Object)) is { } plate ? (plate.X, plate.Y) : (at.X, at.Y);
                        var leverName = spots.Keys.Where(k => IsLever(k))
                            .OrderBy(k => Far(spots[k].X, spots[k].Y, near.Item1, near.Item2)).FirstOrDefault();
                        if (leverName is not null && Far(spots[leverName].X, spots[leverName].Y, at.X, at.Y) <= PUZZLE) {
                            gate = gate with {
                                By = Opener.Click, Lever = leverName,
                                LX = spots[leverName].X, LY = spots[leverName].Y, LZ = spots[leverName].Z, Needs = needs,
                            };
                        }

                        break;
                    }
                }

                // A quest that must be done first, on the trigger itself or on the one it fires from.
                var quest = Needs(t).OfType<ReqEncounterComplete>().FirstOrDefault(r => !r.m_applyNOT && !string.IsNullOrEmpty(r.m_encounterName));
                if (quest is not null) {
                    gate = gate with { Encounter = quest.m_encounterName };
                }

                gates.Add(gate);
            }
        }

        // The teleporter on just past each door (not one back or out): once the door is open, the way on is through it.
        var pads = new List<(string Name, float X, float Y)>();
        var backs = new List<(string Name, float X, float Y)>();
        foreach (var t in triggers.Where(t => Results(t).Any(r => r is ResTeleport))) {
            string name = (string) t.m_triggerName ?? "";
            if (name.Contains("Exit", StringComparison.OrdinalIgnoreCase) || name.Contains("Out", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Back", StringComparison.OrdinalIgnoreCase) || name.Contains("Hub", StringComparison.OrdinalIgnoreCase)) {
                foreach (var fire in Fires(t)) {
                    if (VolumeOf(fire) is { } bv) {
                        backs.Add(((string) bv.m_volumeName ?? name, bv.m_locationX, bv.m_locationY));
                    }
                }

                continue;
            }

            foreach (var fire in Fires(t)) {
                if (VolumeOf(fire) is { } v) {
                    pads.Add(((string) v.m_volumeName ?? name, v.m_locationX, v.m_locationY));
                }
            }
        }

        for (var i = 0; i < gates.Count; i++) {
            var g = gates[i];
            var by = pads.Where(p => Far(p.X, p.Y, g.X, g.Y) <= THROUGH).OrderBy(p => Far(p.X, p.Y, g.X, g.Y)).ToList();
            if (by.Count > 0) {
                gates[i] = g with { Through = by[0].Name, TX = by[0].X, TY = by[0].Y };
            }
        }

        return new Book(circles.DistinctBy(c => c.Trigger + "|" + c.Volume).ToList(), gates.DistinctBy(g => g.Object + "|" + g.Trigger).ToList(),
            toggles.DistinctBy(g => g.Object + "|" + g.From + "|" + g.Volume).ToList(), starts) { Ahead = pads, Back = backs };
    }

    // A thing a player pulls or presses.
    private static bool IsLever(string name)
        => name.Contains("Lever", StringComparison.OrdinalIgnoreCase) || name.Contains("Switch", StringComparison.OrdinalIgnoreCase)
           || name.Contains("Crank", StringComparison.OrdinalIgnoreCase) || name.Contains("Button", StringComparison.OrdinalIgnoreCase);

    // A gate or door that opens (Open, OpenIdle, Opening, Unlock ...).
    internal static bool Opens(string state)
        => !string.IsNullOrEmpty(state)
           && (state.Contains("Open", StringComparison.OrdinalIgnoreCase) || state.Contains("Unlock", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The nearest teleporter on from (x, y) on the same ground, not behind a door still shut (the floor's duty opens that
    /// one first), or null. With no way on the map to a step on a later floor, this is the way a player goes: the floor's
    /// own teleporter up.
    /// </summary>
    internal static (string Name, float X, float Y)? PadAhead(Book book, Live live, float x, float y) {
        if (book is null || book.Ahead.Count == 0) {
            return null;
        }

        string StateOf(string obj) => live?.StateOf?.Invoke(obj) is { Length: > 0 } st ? st : book.Starts.GetValueOrDefault(obj) ?? "";
        // Behind a shut door, or a door's pad he is already past (the room before's way up lands by this room's start).
        var skip = book.Gates.Where(g => g.Through.Length > 0
                                         && (!Opens(StateOf(g.Object)) || ((x - g.TX) * (g.TX - g.X)) + ((y - g.TY) * (g.TY - g.Y)) >= 0))
            .Select(g => g.Through).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pad in book.Ahead.Where(p => Far(x, y, p.X, p.Y) <= ROOM * 2 && !skip.Contains(p.Name)).OrderBy(p => Far(x, y, p.X, p.Y))) {
            if (live?.SameGround?.Invoke(pad.X, pad.Y) != false) {
                return pad;
            }

            // The pad past a door opened in this copy, the door on his side.
            if (book.Gates.FirstOrDefault(g => string.Equals(g.Through, pad.Name, StringComparison.OrdinalIgnoreCase)) is { } gate
                && Opens(StateOf(gate.Object)) && Far(x, y, gate.X, gate.Y) <= ROOM && OurSide(live, gate, x, y)) {
                return pad;
            }
        }

        return null;
    }

    /// <summary>The open door a teleporter on sits just past, or null. Our ground map has every door shut, so a walk to
    /// that teleporter ends at the door; a player walks straight through it.</summary>
    internal static Gate OpenDoorBefore(Book book, Live live, string padName) {
        if (book is null || string.IsNullOrEmpty(padName)) {
            return null;
        }

        string StateOf(string obj) => live?.StateOf?.Invoke(obj) is { Length: > 0 } st ? st : book.Starts.GetValueOrDefault(obj) ?? "";

        return book.Gates.FirstOrDefault(g => string.Equals(g.Through, padName, StringComparison.OrdinalIgnoreCase) && Opens(StateOf(g.Object)));
    }

    // How close a walk may pass a shut door's middle before it counts as going through it (Big Ben's doors fill a corridor
    // about 500 wide).
    private const float DOOR_REACH = 260f;

    /// <summary>
    /// The walk cut short of the first door still shut that it goes through, or the walk as it is. Our ground map is drawn
    /// with every door open (no door is in the area's walls), so a walk on it goes straight through a shut one; a player's
    /// game stops him at the door. Big Ben, 2026-11-17 04:00: Ultimus walked from room 1 through the shut Door42 and Door4
    /// to room 3's elevator without a fight (Niduus: "he's just ignoring the fights").
    /// </summary>
    internal static List<(float X, float Y)> CutAtShutDoor(Book book, Func<string, string> stateOf, float x, float y, float z,
        List<(float X, float Y)> points, out Gate door) {
        door = null;
        if (book is null || book.Gates.Count == 0 || points is not { Count: > 0 }) {
            return points;
        }

        string StateOf(string obj) => stateOf?.Invoke(obj) is { Length: > 0 } st ? st : book.Starts.GetValueOrDefault(obj) ?? "";
        var shut = book.Gates.Where(g => OnFloor(z, g.Z) && !Opens(StateOf(g.Object))).ToList();
        if (shut.Count == 0) {
            return points;
        }

        var (px, py) = (x, y);
        for (var i = 0; i < points.Count; i++) {
            var (qx, qy) = points[i];
            var (dx, dy) = (qx - px, qy - py);
            var len2 = (dx * dx) + (dy * dy);
            if (len2 >= 1) {
                foreach (var g in shut) {
                    var t = (((g.X - px) * dx) + ((g.Y - py) * dy)) / len2;
                    // Only a door the leg goes up to or past, and not one he stands at now walking away from it.
                    if (t <= 0 || Far(px, py, g.X, g.Y) <= DOOR_REACH * 0.5f) {
                        continue;
                    }

                    var ct = MathF.Min(t, 1f);
                    if (Far(px + (dx * ct), py + (dy * ct), g.X, g.Y) > DOOR_REACH || (t > 1 && Far(qx, qy, g.X, g.Y) > DOOR_REACH)) {
                        continue;
                    }

                    // Stop on his side of it.
                    var len = MathF.Sqrt(len2);
                    var stop = MathF.Max(0f, (t * len) - DOOR_REACH - 40f) / len;
                    var cut = points.Take(i).ToList();
                    cut.Add((px + (dx * stop), py + (dy * stop)));
                    door = g;

                    return cut;
                }
            }

            (px, py) = (qx, qy);
        }

        return points;
    }

    /// <summary>The open door a teleporter on at (x, y) sits just past, or null (see OpenDoorBefore).</summary>
    internal static Gate OpenDoorBeforeAt(Book book, Live live, float x, float y) {
        if (book is null) {
            return null;
        }

        string StateOf(string obj) => live?.StateOf?.Invoke(obj) is { Length: > 0 } st ? st : book.Starts.GetValueOrDefault(obj) ?? "";

        return book.Gates.Where(g => g.Through.Length > 0 && !float.IsNaN(g.TX) && Far(g.TX, g.TY, x, y) <= 300 && Opens(StateOf(g.Object)))
            .OrderBy(g => Far(g.TX, g.TY, x, y)).FirstOrDefault();
    }

    // The door is on his ground, or just this side of it is: our ground map is drawn with every door shut, so a door's own
    // spot (and the teleporter past it) can read as the next room's ground. Big Ben's fifteenth floor: Door31 opened after
    // Gear Spinner and Shrieker were beaten, but its spot read as the ground past it, so floor duty had nothing left and he
    // walked toward Meowiarty's spot far away for an hour (Niduus, 2026-11-17: "why does he get stuck here?").
    private static bool OurSide(Live live, Gate g, float x, float y) {
        if (live?.SameGround is null || live.SameGround(g.X, g.Y)) {
            return true;
        }

        var far = Far(x, y, g.X, g.Y);
        if (far <= 1) {
            return true;
        }

        var back = MathF.Min(DOOR_SIDE, far);
        return live.SameGround(g.X + ((x - g.X) / far * back), g.Y + ((y - g.Y) / far * back));
    }

    // How far under his floor a battle circle's volume may sit and still be his room's (Big Ben room 1: 730 under; Counterweight East's floors are 900 apart, so a floor below is never taken).
    private const float SUNK_CIRCLE = 800f;

    // How far up or down a thing may be and still be on the wizard's floor (Counterweight East's floors are 900 apart).
    internal const float FLOOR = 300f;

    /// <summary>Whether a thing at height <paramref name="oz"/> is on the floor of a wizard at height <paramref name="z"/> (unknown: yes).</summary>
    internal static bool OnFloor(float z, float oz) => float.IsNaN(z) || float.IsNaN(oz) || MathF.Abs(z - oz) <= FLOOR;

    internal static float Far(float x1, float y1, float x2, float y2) => MathF.Sqrt(((x1 - x2) * (x1 - x2)) + ((y1 - y2) * (y1 - y2)));

    /// <summary>What the bot sees of the zone right now: object states, trigger fires and creatures (null when unknown).</summary>
    internal sealed record Live(Func<string, string> StateOf, Func<string, int> FiresOf, Func<string, bool?> EnabledOf, Func<float, float, bool> FoesNear,
        Func<float, float, bool> SameGround = null) {

        /// <summary>Whether the wizard has done a dungeon quest (a gate's <see cref="Gate.Encounter"/>); null when unknown.</summary>
        internal Func<string, bool?> EncounterDone { get; init; }
    }

    /// <summary>
    /// The next floor duty for a wizard at (x, y), or null when the floor is done: the room's battle circle not yet
    /// started, then creatures by a fight-gate or a started circle, then a gate's spot to step on, then a puzzle's plates
    /// and its lever. <paramref name="tries"/> counts the steps and clicks already made, by key (kept by the bot per zone copy).
    /// </summary>
    /// <summary>Whether this wizard has done a dungeon quest, as the zone's ReqEncounterComplete reads it; null when unknown.</summary>
    internal static Func<string, bool?> EncounterDoneFor(Wizard wizard) => name => {
        if (wizard is null || string.IsNullOrEmpty(name)) {
            return null;
        }

        try {
            return wizard.HasCompletedQuest(name) || wizard.HasQuestRegistryValue(name, "Complete");
        }
        catch (Exception) {
            return null;
        }
    };

    // How far a point is from the segment (ax, ay)-(bx, by).
    private static float ToSegment(float px, float py, float ax, float ay, float bx, float by) {
        var (dx, dy) = (bx - ax, by - ay);
        var len = (dx * dx) + (dy * dy);
        var t = len <= 0 ? 0 : Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / len, 0, 1);
        return Far(px, py, ax + (t * dx), ay + (t * dy));
    }

    internal static Duty Next(Book book, Live live, float x, float y, IReadOnlyDictionary<string, int> tries, float z = float.NaN) {
        if (book is null || book.IsEmpty) {
            return null;
        }

        // Only this floor's things (Niduus, 2026-11-16, Counterweight East: "He killed the boss, then decided to fight the
        // mobs a floor down, then hit the lever. He should: Fight the mobs, Flip the lever, Ascend a floor/Fight the boss").
        // The floors stand one over another, so a circle or lever right above or below is not this room's.
        bool Here(float oz) => OnFloor(z, oz);

        // A battle circle's volume can sit well under the floor it is on (Big Ben's first room: "InitiateCombat-Room1" at
        // -935 under a floor at -203, so floor duty never stepped on it; 2026-11-17 04:57 he stood at the shut Door42 until a
        // stuck fix found the circle). One under him, on his ground, with no circle of his own floor in the room, is his.
        bool HereCircle(Circle c) => Here(c.Z)
            || (!float.IsNaN(z) && !float.IsNaN(c.Z) && c.Z < z && z - c.Z <= SUNK_CIRCLE && live?.SameGround?.Invoke(c.X, c.Y) != false
                && !book.Circles.Any(o => !ReferenceEquals(o, c) && Far(o.X, o.Y, c.X, c.Y) <= ROOM && Here(o.Z)));

        int Tried(string key) => tries?.GetValueOrDefault(key) ?? 0;
        string StateOf(string obj) => live?.StateOf?.Invoke(obj) is { Length: > 0 } s ? s : book.Starts.GetValueOrDefault(obj) ?? "";
        bool Closed(Gate g) => !Opens(StateOf(g.Object));
        bool Off(string trigger) => live?.EnabledOf?.Invoke(trigger) == false;
        // A gate whose opener waits on a dungeon quest not done yet: nothing here opens it now (Tulkar stood on Emperor's
        // Retreat's "Activator Volume (2)" for an hour, 2026-11-17, as "Keymaster'sDoor" opens only after the Colossus).
        bool Locked(Gate g) => g.Encounter.Length > 0 && live?.EncounterDone?.Invoke(g.Encounter) == false;
        // A spot past such a locked, shut door: walking there ends at the door.
        // Past a locked door: the door lies on the straight way there, or (seen from far off, where the straight line says
        // little of the real way round) the thing stands much nearer that door than he does. The Calling of 7, 2026-11-18:
        // Emperor's Retreat's Snake obelisk stands 3,400 from the locked Keymaster's door and 20,000 from them; from one spot
        // the straight line missed the door by more than 500 and floor duty sent them there, from a step beside it not, and
        // they flipped between it and Shalek the Wise every second for two hours.
        bool PastLocked(float tx, float ty) => book.Gates.Any(g => Locked(g) && Closed(g) && Far(x, y, g.X, g.Y) < Far(x, y, tx, ty)
                                                                  && (ToSegment(g.X, g.Y, x, y, tx, ty) <= LOCKED_DOOR
                                                                      || Far(g.X, g.Y, tx, ty) <= Far(x, y, tx, ty) * LOCKED_NEAR_SHARE));

        // 1. The room's battle circle, when it has not started its fight in this copy of the area.
        foreach (var circle in book.Circles.Where(c => Far(x, y, c.X, c.Y) <= ROOM && HereCircle(c)).OrderBy(c => Far(x, y, c.X, c.Y))) {
            var key = "circle|" + circle.Trigger;
            if ((live?.FiresOf?.Invoke(circle.Trigger) ?? 0) > 0 || Tried(key) >= 1 || Off(circle.Trigger)) {
                continue;
            }

            // The fight is only there to open the floor's door, and that door is open already: a player walks past the
            // circle. Big Ben's eighteenth floor: Door25 opens when the seventeenth floor's fight is won, and Niduus walked
            // straight through floor 18 with no fight (2026-11-17, his guided run).
            if (book.Gates.Any(g => g.By == Opener.Beat && g.Through.Length > 0 && !Closed(g) && Here(g.Z)
                                    && Far(circle.X, circle.Y, g.X, g.Y) <= ROOM && Before(g))) {
                continue;
            }

            return new Duty(STEP, circle.X, circle.Y, $"the room's battle circle \"{circle.Volume}\"", key,
                "each room's fight starts on its battle circle, and the way on opens once it is won");
        }

        var near = book.Gates.Where(g => Closed(g) && Far(x, y, g.X, g.Y) <= ROOM && Here(g.Z)).OrderBy(g => Far(x, y, g.X, g.Y)).ToList();

        // 2. Creatures by a closed door that opens on a fight, or by the room's circle once its fight started.
        foreach (var gate in near.Where(g => g.By == Opener.Beat)) {
            if (live?.FoesNear?.Invoke(gate.X, gate.Y) == true) {
                return new Duty(FIGHT, gate.X, gate.Y, $"the creatures by \"{gate.Object}\"", "beat|" + gate.Object,
                    $"\"{gate.Object}\" opens once the room's creatures are beaten");
            }
        }

        // Only a circle whose fight has started: before that its creatures are not out for a player (Niduus, 2026-11-16, Big
        // Ben's twentieth floor: "didn't show mobs on my screen but Ultimus started the combat with them").
        foreach (var circle in book.Circles.Where(c => Far(x, y, c.X, c.Y) <= ROOM && HereCircle(c) && (live?.FiresOf is null || live.FiresOf(c.Trigger) > 0))) {
            if (live?.FoesNear?.Invoke(circle.X, circle.Y) == true) {
                return new Duty(FIGHT, circle.X, circle.Y, $"the creatures of the room's battle circle \"{circle.Volume}\"", "room|" + circle.Trigger,
                    "the room's creatures are beaten before going on");
            }
        }

        // 3. A spot that opens a door here.
        foreach (var gate in near.Where(g => g.By == Opener.Step && !Off(g.Trigger) && Here(g.VZ) && !Locked(g))) {
            var key = "step|" + gate.Volume;
            if (Tried(key) < TRIES) {
                return new Duty(STEP, gate.VX, gate.VY, $"\"{gate.Volume}\", which opens \"{gate.Object}\"", key,
                    $"stepping on it opens \"{gate.Object}\"");
            }
        }

        // 4. A puzzle: the plates that are wrong, one at a time, then the lever.
        foreach (var gate in book.Gates.Where(g => g.By == Opener.Click && Closed(g) && !Off(g.Trigger) && !Locked(g)
                                                   && Far(x, y, g.LX, g.LY) <= PUZZLE && Here(g.LZ) && !PastLocked(g.LX, g.LY))
                     .OrderBy(g => Far(x, y, g.LX, g.LY))) {
            var wrong = gate.Needs.Where(n => !string.Equals(StateOf(n.Object), n.State, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var (obj, state) in wrong) {
                var now = StateOf(obj);
                var plate = book.Toggles.FirstOrDefault(t => t.Object == obj && string.Equals(t.From, now, StringComparison.OrdinalIgnoreCase)
                                                             && string.Equals(t.To, state, StringComparison.OrdinalIgnoreCase))
                            ?? book.Toggles.FirstOrDefault(t => t.Object == obj && string.Equals(t.From, now, StringComparison.OrdinalIgnoreCase));
                if (plate is null) {
                    continue;
                }

                var key = $"plate|{obj}|{now}";
                if (Tried(key) < TRIES) {
                    return new Duty(STEP, plate.X, plate.Y, $"the puzzle plate \"{obj}\" (now {now}, needs {state})", key,
                        $"the lever that opens \"{gate.Object}\" works once {string.Join(", ", gate.Needs.Select(n => $"{n.Object} is {n.State}"))}");
                }
            }

            if (wrong.Count == 0 && Tried("lever|" + gate.Lever) < TRIES + 2) {
                return new Duty(CLICK, gate.LX, gate.LY, $"the lever \"{gate.Lever}\", which opens \"{gate.Object}\"", "lever|" + gate.Lever,
                    gate.Needs.Count > 0 ? "the puzzle is right, so the lever now opens the door" : $"pulling it opens \"{gate.Object}\"");
            }
        }

        // 4b. A puzzle whose pieces are clicked themselves, often in a wing far from the lock it opens. The Vault of Ice
        // (Krokotopia): three locks bar the Frost door to the Keeper of the Fang, and each opens from its own wing, where a
        // guardian's defeat brings out the pieces: four obelisks lit in order, two obelisks, three braziers set to Fire, Ice
        // and Storm. Tulkar, Ra, Kurrent and Mythis stood at the shut BirdLock for hours (2026-11-17), as nothing showed
        // what opens it. As a player: the wing's battle circle, its creatures, then each piece not yet right, in order.
        foreach (var gate in book.Gates.Where(g => g.Pieces && Closed(g) && !Off(g.Trigger) && Here(g.LZ) && !Locked(g)
                                                   && !PastLocked(g.LX, g.LY) && !g.PieceSpots.Values.Any(p => PastLocked(p.X, p.Y)))
                     .OrderBy(g => Far(x, y, g.LX, g.LY))) {
            var wing = book.Circles.Where(c => Far(c.X, c.Y, gate.LX, gate.LY) <= ROOM).OrderBy(c => Far(c.X, c.Y, gate.LX, gate.LY)).FirstOrDefault();
            if (wing is not null && (live?.FiresOf?.Invoke(wing.Trigger) ?? 0) == 0 && Tried("circle|" + wing.Trigger) < 1 && !Off(wing.Trigger)) {
                return new Duty(STEP, wing.X, wing.Y, $"the battle circle \"{wing.Volume}\" by the puzzle that opens \"{gate.Object}\"", "circle|" + wing.Trigger,
                    "beating its guardian brings out the puzzle, and the puzzle opens a lock on the way on");
            }

            if (wing is not null && live?.FoesNear?.Invoke(wing.X, wing.Y) == true) {
                return new Duty(FIGHT, wing.X, wing.Y, $"the creatures by the puzzle that opens \"{gate.Object}\"", "room|" + wing.Trigger,
                    "the puzzle comes out once they are beaten");
            }

            foreach (var (obj, state) in gate.Needs) {
                var now = StateOf(obj);
                if (string.Equals(now, state, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }

                // Something set first (the lever the obelisk waits on): that one is clicked first.
                if (gate.PieceFirst.GetValueOrDefault(obj)?.FirstOrDefault(f => !string.Equals(StateOf(f.Object), f.State, StringComparison.OrdinalIgnoreCase)) is { Object: { } pre } first
                    && gate.PieceSpots.TryGetValue(pre, out var preAt)) {
                    var was = StateOf(pre);
                    var preKey = $"piece|{pre}|{was}|for|{obj}";
                    if (Tried(preKey) < TRIES + 2) {
                        return new Duty(CLICK, preAt.X, preAt.Y, $"\"{pre}\" (now {(was.Length > 0 ? was : "unknown")}, needs {first.State} before \"{obj}\" can be set)", preKey,
                            $"\"{obj}\" only turns while {pre} is {first.State}, and \"{gate.Object}\" opens once {string.Join(", then ", gate.Needs.Select(n => $"{n.Object} is {n.State}"))}");
                    }

                    break;
                }

                var key = $"piece|{obj}|{now}";
                if (Tried(key) < TRIES + 2 && gate.PieceSpots.TryGetValue(obj, out var at)) {
                    return new Duty(CLICK, at.X, at.Y, $"the puzzle piece \"{obj}\" (now {(now.Length > 0 ? now : "unknown")}, needs {state})", key,
                        $"\"{gate.Object}\" opens once {string.Join(", then ", gate.Needs.Select(n => $"{n.Object} is {n.State}"))}");
                }

                // The order counts (the obelisks): never a later piece before this one.
                break;
            }
        }

        // 5. A door that opened in this copy of the area (it starts shut): through it to the teleporter on, as a player goes on
        // once the room is done. He beat Big Ben's sixth floor and its door opened, then walked back to the south wall
        // toward Meowiarty's spot for an hour, since the boss is on the same height (his log, 2026-11-16 03:14).
        foreach (var gate in book.Gates.Where(g => g.Through.Length > 0 && !Closed(g) && !Opens(book.Starts.GetValueOrDefault(g.Object) ?? "")
                                                   && Far(x, y, g.X, g.Y) <= ROOM && Here(g.Z) && Before(g) && OurSide(live, g, x, y))
                     .OrderBy(g => Far(x, y, g.X, g.Y))) {
            var key = "through|" + gate.Object;
            if (Tried(key) < TRIES) {
                return new Duty(STEP, gate.TX, gate.TY, $"the teleporter \"{gate.Through}\" past \"{gate.Object}\"", key,
                    $"\"{gate.Object}\" is open now, so the way on is through it");
            }
        }

        return null;

        // On the door's side of its teleporter, not already past it (the next room's landing is near the same door).
        bool Before(Gate g) => ((x - g.TX) * (g.TX - g.X)) + ((y - g.TY) * (g.TY - g.Y)) < 0;
    }

}
