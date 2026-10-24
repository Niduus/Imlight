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
 * DECKATHALON
 * ========================================================================
 *
 * PURPOSE:
 * The Deckathalon (Niduus, 2026-10-24: "Events need to be looked into like the Deckathalon stuff"): a tower of 30 floors,
 * one battle on each, fought with treasure cards only and without the stats of gear, pets and jewels (the deck's own
 * stats count). Each school has its own tower, with a professor on floor 30. The app's Events card (GameEvents) says
 * which school's Deckathalon is on; `.deckathalon` climbs it.
 *
 * USAGE EXAMPLE:
 * Deckathalon.TryStart(session, wizard, school, out var reply);   // .deckathalon
 * Deckathalon.FightsHere(wizard);                                 // a battle in the tower: TCs only, base stats
 * Deckathalon.OnWin(session, wizard, mobTemplateIds, rounds);     // the floor's creature was beaten: the next floor
 *
 * NOTE:
 * - The creatures are the game's own Deckathalon ones (the client calls the event the Treasure Tower):
 *   CE_TreasureTower_Floor_01_Fire to CE_TreasureTower_Floor_30_Fire and the same for every school, read from the
 *   game files' creature list on 2026-10-24. Their health and stats are made for a wizard without gear (Fire Hornet
 *   on floor 1 has 195 health, Dalia Falmea on floor 30 has 4,670).
 * - The game's tower room is not in the game folder any more (the Wizard101 folder has no Treasure Tower zone; the live
 *   game downloads event zones while an event is on), so the floors are Briskbreeze Tower's battle rooms (Room02 to
 *   Room10), each a private copy loaded with a ZoneObjects variant (Deckathalon-<School>-<Floor>) made here: the room's
 *   own creatures and duel circle are left out, and a battle circle with the floor's creature stands where the room's
 *   circle was. The rooms go round (floor 10 is Room02 again) and floor 30 is always Room10. The room's doors lead out
 *   to the Commons.
 * - Scores (best floor and points, per event) and the rewards a wizard already had are kept in deckathalon.json next
 *   to the server.
 * - Points: 100 for each floor number cleared, 10 for each treasure card used, and up to 125 more for a quick win.
 * - Rewards, once per event: a Tower Pack of the school every 5 floors, and the event's trophies (3rd place at floor
 *   10, 2nd at 20, 1st at 30).
 *
 * TODO:
 * - The game's Deckathalon crafting station and rune currency.
 * - The game's leaderboard window (the scores are shown in chat with .deckathalon top).
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Game.Packs;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.WizardData.Models.World;

namespace Imlight.CoreLib.Game.Events;

internal static class Deckathalon {

    internal const string VARIANT_PREFIX = "Deckathalon-";
    internal const int TOP_FLOOR = 30;
    private const string SCORES_FILE = "deckathalon.json";

    internal static readonly bool Enabled = ConfigurationManager.GetValue("Deckathalon.Enabled", true);
    internal static readonly bool TreasureCardsOnly = ConfigurationManager.GetValue("Deckathalon.TreasureCardsOnly", true);
    private static readonly bool s_noGearStats = ConfigurationManager.GetValue("Deckathalon.NoGearStats", true);
    private static readonly bool s_alwaysOpen = ConfigurationManager.GetValue("Deckathalon.AlwaysOpen", false);
    private static readonly bool s_starterPack = ConfigurationManager.GetValue("Deckathalon.StarterPack", true);
    private static readonly double s_nextFloorSeconds = Math.Clamp(ConfigurationManager.GetValue("Deckathalon.NextFloorSeconds", 6.0), 1.0, 60.0);
    private static readonly int s_floors = Math.Clamp(ConfigurationManager.GetValue("Deckathalon.Floors", TOP_FLOOR), 1, TOP_FLOOR);

    // Where a wizard goes when they leave the tower, and where its doors lead.
    private const string EXIT_ZONE = "WizardCity/WC_Hub";
    private const string EXIT_LOCATION = "Start";

    private const ulong BATTLE_CIRCLE_TEMPLATE = 512231;
    private const ulong ROOM_DUEL_CIRCLE_TEMPLATE = 560;

    /// <summary>A school's tower: its creatures by floor (1 to 30), its professor, its Tower Pack and its trophies.</summary>
    internal sealed record Tower(string School, MagicSchool Magic, uint[] Floors, string Professor, uint TowerPack,
                                 uint Bronze, uint Silver, uint Gold);

    // CE_TreasureTower_Floor_01_<School> .. CE_TreasureTower_Floor_30_<School>, and the packs and trophies of the event,
    // from the game files' creature and item lists (2026-10-24).
    internal static readonly Tower[] Towers = [
        new("Fire", MagicSchool.Fire, [
            1466819, 1466820, 1466821, 1466822, 1466823, 1466824, 1466825, 1466826, 1466827, 1466828,
            1466829, 1466830, 1475618, 1475619, 1475620, 1475621, 1475622, 1475623, 1475624, 1475625,
            1475626, 1475627, 1475628, 1475629, 1475630, 1475631, 1475632, 1475633, 1475634, 1475611,
        ], "Dalia Falmea", 1441848, 1448524, 1448525, 1448526),
        new("Ice", MagicSchool.Ice, [
            1466807, 1466808, 1466809, 1466810, 1466811, 1466812, 1466813, 1466814, 1466815, 1466816,
            1466817, 1466818, 1475635, 1475636, 1475637, 1475638, 1475639, 1475640, 1475641, 1475642,
            1475643, 1475644, 1475645, 1475646, 1475647, 1475648, 1475649, 1475650, 1475651, 1475612,
        ], "Lydia Greyrose", 1441838, 1470620, 1470618, 1470619),
        new("Storm", MagicSchool.Storm, [
            1475652, 1475653, 1475654, 1475655, 1475656, 1475657, 1475658, 1475659, 1475660, 1475661,
            1475662, 1475663, 1475664, 1475665, 1475666, 1475667, 1475668, 1475669, 1475670, 1475671,
            1475672, 1475673, 1475674, 1475675, 1475676, 1475677, 1475678, 1475679, 1475680, 1475613,
        ], "Halston Balestrom", 1441961, 1448527, 1448528, 1448529),
        new("Myth", MagicSchool.Myth, [
            1475681, 1475682, 1475683, 1475684, 1475685, 1475686, 1475687, 1475688, 1475689, 1475690,
            1475691, 1475692, 1475693, 1475694, 1475695, 1475696, 1475697, 1475698, 1475699, 1475700,
            1475701, 1475702, 1475703, 1475704, 1475705, 1475706, 1475707, 1475708, 1475709, 1475614,
        ], "Cyrus Drake", 1441962, 1448536, 1448537, 1448538),
        new("Life", MagicSchool.Life, [
            1475710, 1475711, 1475712, 1475713, 1475714, 1475715, 1475716, 1475717, 1475718, 1475719,
            1475720, 1475721, 1475722, 1475723, 1475724, 1475725, 1475726, 1475727, 1475728, 1475729,
            1475730, 1475731, 1475732, 1475733, 1475734, 1475735, 1475736, 1475737, 1475738, 1475615,
        ], "Moolinda Wu", 1441960, 1448533, 1448534, 1448535),
        new("Death", MagicSchool.Death, [
            1466831, 1466832, 1466833, 1466834, 1466835, 1466836, 1466837, 1466838, 1466839, 1466840,
            1466841, 1466842, 1475739, 1475740, 1475741, 1475742, 1475743, 1475744, 1475745, 1475746,
            1475747, 1475748, 1475749, 1475750, 1475751, 1475752, 1475753, 1475754, 1475755, 1475616,
        ], "Dworgyn", 1441849, 1448530, 1448531, 1448532),
        new("Balance", MagicSchool.Balance, [
            1475756, 1475757, 1475758, 1475759, 1475760, 1475761, 1475762, 1475763, 1475764, 1475765,
            1475766, 1475767, 1475768, 1475769, 1475770, 1475771, 1475772, 1475773, 1475774, 1475775,
            1475776, 1475777, 1475778, 1475779, 1475780, 1475781, 1475782, 1475783, 1475784, 1475617,
        ], "Alhazred", 1441963, 1448539, 1448540, 1448541),
    ];

    /// <summary>A floor's room: a Briskbreeze Tower battle room and where its duel circle stands.</summary>
    private sealed record Room(string Zone, float X, float Y, float Z);

    private static readonly Room[] s_rooms = [
        new("WizardCity/Gauntlets/WC_Gauntlet_01/Room02", 3.470825f, -43.25f, 1.577637f),
        new("WizardCity/Gauntlets/WC_Gauntlet_01/Room03", 3.470825f, -43.25f, 1.577637f),
        new("WizardCity/Gauntlets/WC_Gauntlet_01/Room04", 16.04494f, 114.4375f, -167.4351f),
        new("WizardCity/Gauntlets/WC_Gauntlet_01/Room05", 3.470825f, -43.25f, 1.577637f),
        new("WizardCity/Gauntlets/WC_Gauntlet_01/Room06", 3.470825f, -43.25f, 1.577637f),
        new("WizardCity/Gauntlets/WC_Gauntlet_01/Room07", 3.470825f, -43.25f, 1.577637f),
        new("WizardCity/Gauntlets/WC_Gauntlet_01/Room08", -1.459076f, 142.6258f, -167.0424f),
        new("WizardCity/Gauntlets/WC_Gauntlet_01/Room09", 3.470825f, -43.25f, 1.577637f),
        new("WizardCity/Gauntlets/WC_Gauntlet_01/Room10", 3.470825f, -43.25f, 1.577637f),
    ];

    // Briskbreeze Tower's own creatures (its spawns, from the game files' spawn list), left out of a floor.
    private static readonly ulong[] s_roomCreatures = [164774, 164775, 164776, 191087, 191088, 191089, 191090, 191091, 191093];

    /// <summary>A climb of the tower: whose, which school, the floor they are on and the points so far.</summary>
    internal sealed class Run {
        internal ulong CharId;
        internal string Name = "";
        internal Tower Tower;
        internal string EventKey = "";
        internal int Floor = 1;
        internal int Points;
        internal int CardsUsed;
        internal bool Won;
    }

    private static readonly ConcurrentDictionary<ulong, Run> s_runs = new();

    #region Scores

    private sealed class ScoreFile {
        public Dictionary<string, Dictionary<string, Score>> Events { get; set; } = [];
    }

    internal sealed class Score {
        public string Name { get; set; } = "";
        public int BestFloor { get; set; }
        public int Points { get; set; }
        public int Runs { get; set; }
        public List<string> Rewards { get; set; } = [];
    }

    private static readonly object s_scoreLock = new();
    private static ScoreFile s_scores;

    private static ScoreFile Scores {
        get {
            lock (s_scoreLock) {
                if (s_scores is not null) {
                    return s_scores;
                }

                try {
                    s_scores = File.Exists(SCORES_FILE)
                        ? JsonSerializer.Deserialize<ScoreFile>(File.ReadAllText(SCORES_FILE)) ?? new ScoreFile()
                        : new ScoreFile();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
                    Logger.Warning("Deckathalon: could not read {0} ({1}); starting with no scores.", Logger.Args(SCORES_FILE, ex.Message));
                    s_scores = new ScoreFile();
                }

                s_scores.Events ??= [];

                return s_scores;
            }
        }
    }

    private static Score ScoreOf(string eventKey, ulong charId, string name) {
        var scores = Scores;
        lock (s_scoreLock) {
            if (!scores.Events.TryGetValue(eventKey, out var byWizard)) {
                byWizard = [];
                scores.Events[eventKey] = byWizard;
            }

            var key = charId.ToString();
            if (!byWizard.TryGetValue(key, out var score)) {
                score = new Score();
                byWizard[key] = score;
            }

            if (!string.IsNullOrEmpty(name)) {
                score.Name = name;
            }

            score.Rewards ??= [];

            return score;
        }
    }

    private static void SaveScores() {
        try {
            string json;
            lock (s_scoreLock) {
                json = JsonSerializer.Serialize(Scores, new JsonSerializerOptions { WriteIndented = true });
            }

            var temp = $"{SCORES_FILE}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, SCORES_FILE, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Warning("Deckathalon: could not save {0}: {1}", Logger.Args(SCORES_FILE, ex.Message));
        }
    }

    /// <summary>The best wizards of an event: highest floor first, then the most points.</summary>
    internal static List<Score> Top(string eventKey, int count) {
        var scores = Scores;
        lock (s_scoreLock) {
            return scores.Events.TryGetValue(eventKey, out var byWizard)
                ? [.. byWizard.Values.Where(s => s.BestFloor > 0).OrderByDescending(s => s.BestFloor).ThenByDescending(s => s.Points).Take(count)]
                : [];
        }
    }

    /// <summary>A wizard's best of an event and their place among everyone's (1 is first), or null when they cleared no floor.</summary>
    internal static (Score Score, int Place)? PlaceOf(string eventKey, ulong charId) {
        var scores = Scores;
        lock (s_scoreLock) {
            if (!scores.Events.TryGetValue(eventKey, out var byWizard) || !byWizard.TryGetValue(charId.ToString(), out var mine)
                || mine.BestFloor <= 0) {
                return null;
            }

            var ahead = byWizard.Values.Count(s => s != mine
                && (s.BestFloor > mine.BestFloor || (s.BestFloor == mine.BestFloor && s.Points > mine.Points)));

            return (mine, ahead + 1);
        }
    }

    #endregion

    #region Towers and floors

    /// <summary>A school's name as the towers spell it (fire, FIRE: Fire), or null for no school.</summary>
    internal static string SchoolName(string school)
        => Towers.FirstOrDefault(t => string.Equals(t.School, school?.Trim(), StringComparison.OrdinalIgnoreCase))?.School;

    internal static Tower TowerOf(string school)
        => Towers.FirstOrDefault(t => string.Equals(t.School, school?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The Deckathalon on now (from the app's Events card), or null.</summary>
    internal static GameEvents.RunningEvent CurrentEvent
        => GameEvents.Current is { IsDeckathalon: true } running && TowerOf(running.School) is not null ? running : null;

    // Floors 1 to 9 climb Room02 to Room10, then the rooms go round again; floor 30 is always the top room.
    private static Room RoomOf(int floor)
        => floor >= TOP_FLOOR ? s_rooms[^1] : s_rooms[(Math.Max(1, floor) - 1) % s_rooms.Length];

    private static string VariantOf(Tower tower, int floor) => $"{VARIANT_PREFIX}{tower.School}-{floor}";

    /// <summary>The creature of a floor.</summary>
    internal static uint CreatureOf(Tower tower, int floor) => tower.Floors[Math.Clamp(floor, 1, TOP_FLOOR) - 1];

    /// <summary>The name a creature template shows in the game, or its template id.</summary>
    internal static string ShownName(uint templateId) {
        var template = CoreObjectFactory.GetCoreTemplate(templateId) as WizGameObjectTemplate;
        var name = template?.m_displayName.ToString();

        return string.IsNullOrWhiteSpace(name) ? templateId.ToString() : name;
    }

    /// <summary>
    /// The ZoneObjects files of every floor of every tower, which ZoneObjects reads with its own: each floor's room,
    /// without its own creatures and duel circle, with a battle circle and the floor's creature on it, and the room's
    /// doors leading out to the Commons.
    /// </summary>
    internal static List<ZoneObjects.ZoneObjectFile> ZoneFiles() {
        if (!Enabled) {
            return [];
        }

        var files = new List<ZoneObjects.ZoneObjectFile>();
        foreach (var tower in Towers) {
            for (var floor = 1; floor <= TOP_FLOOR; floor++) {
                var room = RoomOf(floor);
                var boss = floor == TOP_FLOOR;
                files.Add(new ZoneObjects.ZoneObjectFile {
                    Zone = room.Zone,
                    Variant = VariantOf(tower, floor),
                    Notes = $"Deckathalon (Deckathalon.cs): the {tower.School} tower's floor {floor}.",
                    Objects = [
                        new ZoneObjects.ZoneObject {
                            Name = "Deckathalon_Circle",
                            TemplateID = BATTLE_CIRCLE_TEMPLATE,
                            X = room.X, Y = room.Y, Z = room.Z,
                            CombatSigil = new ZoneObjects.ZoneCombatSigil {
                                SigilType = boss ? "CombatSigil_1EnemySlot" : "CombatSigil_4EnemySlots",
                            },
                        },
                    ],
                    Creatures = [
                        new ZoneObjects.ZoneObject {
                            Name = "Deckathalon_Floor",
                            TemplateID = CreatureOf(tower, floor),
                            X = room.X, Y = room.Y, Z = room.Z,
                        },
                    ],
                    Remove = [ROOM_DUEL_CIRCLE_TEMPLATE, .. s_roomCreatures],
                    Exit = new ZoneObjects.ZoneExit { Zone = EXIT_ZONE, Loc = EXIT_LOCATION, Triggers = [], Doors = [] },
                });
            }
        }

        return files;
    }

    #endregion

    #region Runs

    /// <summary>The climb a wizard is on, or null.</summary>
    internal static Run RunOf(ulong charId) => s_runs.TryGetValue(charId, out var run) ? run : null;

    /// <summary>Whether a wizard's battle is in the tower: the wizard is climbing and stands in their floor's room.</summary>
    internal static bool FightsHere(Wizard wizard)
        => Enabled && wizard is not null && RunOf(wizard.CharId) is { Won: false } run
           && string.Equals(wizard.Zone, RoomOf(run.Floor).Zone, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Starts a climb from floor 1: the school's tower when its Deckathalon is on, or (for the server's testers, or with
    /// [Deckathalon] AlwaysOpen) any school's. Returns false with the reason when it cannot.
    /// </summary>
    internal static bool TryStart(IActorRef session, Wizard wizard, string school, bool tester, out string reply) {
        reply = "";
        if (!Enabled) {
            reply = "The Deckathalon is turned off on this server.";

            return false;
        }

        if (wizard.IsInDuel) {
            reply = "Finish your battle first.";

            return false;
        }

        var current = CurrentEvent;
        var tower = string.IsNullOrWhiteSpace(school) ? TowerOf(current?.School) : TowerOf(school);
        if (tower is null) {
            reply = string.IsNullOrWhiteSpace(school)
                ? "No Deckathalon is on right now. The Events card of the Imlight app starts one."
                : "Pick a school: fire, ice, storm, myth, life, death or balance.";

            return false;
        }

        var isEvent = current is not null && current.School == tower.School;
        if (!isEvent && !tester && !s_alwaysOpen) {
            reply = current is null
                ? "No Deckathalon is on right now. The Events card of the Imlight app starts one."
                : $"The {current.School} Deckathalon is on now. Type .deckathalon to climb it.";

            return false;
        }

        var name = wizard.PlayerNameBehavior?.GetWizardName() ?? wizard.CharId.ToString();
        var run = new Run {
            CharId = wizard.CharId,
            Name = name,
            Tower = tower,
            EventKey = isEvent ? current.Key : $"Practice-{tower.School}",
        };
        s_runs[wizard.CharId] = run;
        var score = ScoreOf(run.EventKey, wizard.CharId, name);
        lock (s_scoreLock) {
            score.Runs++;
        }

        SaveScores();
        Logger.Information("Deckathalon | {0} starts the {1} tower ({2}).", Logger.Args(name, tower.School, run.EventKey));

        var lines = new List<string> {
            $"The {tower.School} Deckathalon: {s_floors} floors, one battle on each, with {tower.Professor} at the top.",
        };
        if (TreasureCardsOnly) {
            lines.Add("Only treasure cards can be cast in the tower, so put treasure cards in your deck (its Treasure Cards side).");
        }

        if (s_noGearStats) {
            lines.Add("Gear, pets and jewels give no stats in the tower; your deck's own stats still count.");
        }

        if (s_starterPack && TreasureCardsInDeck(wizard) == 0 && !GotToday(score, "starter")) {
            lines.Add($"You had no treasure cards in your deck, so here is a {tower.School} Tower Pack. Put its cards in your deck before you step in the circle.");
            lock (s_scoreLock) {
                score.Rewards.Add($"starter@{DateTime.UtcNow:yyyy-MM-dd}");
            }

            SaveScores();
            TryOpenPack(session, wizard, tower.TowerPack);
        }

        lines.Add("Step into the battle circle to fight. Win to go up a floor. Type .deckathalon leave to leave.");
        reply = string.Join("<br>", lines);
        GoToFloor(session, run, 0);

        return true;
    }

    /// <summary>Ends a climb and sends the wizard out of the tower.</summary>
    internal static string Leave(IActorRef session, Wizard wizard) {
        if (!s_runs.TryRemove(wizard.CharId, out var run)) {
            return "You are not climbing the Deckathalon.";
        }

        var inTower = string.Equals(wizard.Zone, RoomOf(run.Floor).Zone, StringComparison.OrdinalIgnoreCase);
        if (inTower) {
            session.Tell(ExitTransfer());
        }

        return $"You left the {run.Tower.School} Deckathalon on floor {run.Floor} with {run.Points} points.";
    }

    /// <summary>A treasure card used in battle in the tower: 10 points.</summary>
    internal static void CardUsed(Wizard wizard) {
        if (FightsHere(wizard) && RunOf(wizard.CharId) is { } run) {
            run.CardsUsed++;
            run.Points += 10;
        }
    }

    /// <summary>
    /// A won battle: when it was the wizard's floor, the floor counts (points, best floor, rewards) and, after a few
    /// seconds, the wizard goes up to the next floor, or out of the tower after the top one.
    /// </summary>
    internal static void OnWin(IActorRef session, Wizard wizard, IEnumerable<ulong> mobTemplateIds, int rounds) {
        if (!Enabled || wizard is null || RunOf(wizard.CharId) is not { Won: false } run) {
            return;
        }

        var creature = CreatureOf(run.Tower, run.Floor);
        var mobs = (mobTemplateIds ?? []).Select(Combat.Customs.BaseOf).ToList();
        if (!mobs.Contains(creature)
            || !string.Equals(wizard.Zone, RoomOf(run.Floor).Zone, StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        try {
            var floor = run.Floor;
            run.Points += 100 * floor + Math.Max(0, 6 - Math.Max(1, rounds)) * 25;
            var score = ScoreOf(run.EventKey, run.CharId, run.Name);
            var lines = new List<string>();
            lock (s_scoreLock) {
                if (floor > score.BestFloor || (floor == score.BestFloor && run.Points > score.Points)) {
                    score.BestFloor = floor;
                    score.Points = Math.Max(score.Points, run.Points);
                }
            }

            Rewards(session, wizard, run, score, floor, lines);
            SaveScores();
            Logger.Information("Deckathalon | {0} clears floor {1} of the {2} tower ({3} points).",
                Logger.Args(run.Name, floor, run.Tower.School, run.Points));

            if (floor >= s_floors) {
                run.Won = true;
                s_runs.TryRemove(run.CharId, out _);
                lines.Insert(0, floor >= TOP_FLOOR
                    ? $"You beat {run.Tower.Professor} and conquered the {run.Tower.School} Deckathalon with {run.Points} points!"
                    : $"You reached the top of the {run.Tower.School} Deckathalon (floor {floor}) with {run.Points} points!");
                lines.Add($"Leaving the tower in {s_nextFloorSeconds:0} seconds.");
                Tell(session, lines);
                Later(session, ExitTransfer());

                return;
            }

            run.Floor = floor + 1;
            var next = CreatureOf(run.Tower, run.Floor);
            lines.Insert(0, $"Floor {floor} cleared ({run.Points} points). Up to floor {run.Floor} in {s_nextFloorSeconds:0} seconds: "
                + (run.Floor == TOP_FLOOR ? $"{run.Tower.Professor} waits there." : $"{ShownName(next)}."));
            Tell(session, lines);
            GoToFloor(session, run, s_nextFloorSeconds);
        }
        catch (Exception ex) {
            Logger.Error("Deckathalon | the win on floor {0} of {1} failed: {2}", Logger.Args(run.Floor, run.Name, ex));
        }
    }

    /// <summary>A lost (or fled) battle on the wizard's floor ends the climb.</summary>
    internal static void OnDefeat(IActorRef session, Wizard wizard) {
        if (!Enabled || wizard is null || RunOf(wizard.CharId) is not { } run
            || !string.Equals(wizard.Zone, RoomOf(run.Floor).Zone, StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        s_runs.TryRemove(run.CharId, out _);
        var score = ScoreOf(run.EventKey, run.CharId, run.Name);
        Logger.Information("Deckathalon | {0} falls on floor {1} of the {2} tower ({3} points).",
            Logger.Args(run.Name, run.Floor, run.Tower.School, run.Points));
        Tell(session, [
            $"The Deckathalon is over for this climb: you fell on floor {run.Floor} with {run.Points} points.",
            $"Your best: floor {score.BestFloor}. Type .deckathalon to climb again.",
        ]);
    }

    // Every 5 floors a Tower Pack, and the trophies at floors 10, 20 and 30, each once per event.
    private static void Rewards(IActorRef session, Wizard wizard, Run run, Score score, int floor, List<string> lines) {
        var loot = new DropTableResult();
        var trophy = floor switch {
            10 => run.Tower.Bronze,
            20 => run.Tower.Silver,
            TOP_FLOOR => run.Tower.Gold,
            _ => 0u,
        };
        if (trophy != 0 && FirstTime(score, $"trophy{floor}")) {
            loot.Items.Add(new DropItemResult { ItemId = trophy.ToString(), Quantity = 1 });
            lines.Add($"Reward: {ItemName(trophy)}.");
        }

        if (loot.Items.Count > 0) {
            LootGranter.Grant(session, wizard, loot);
        }

        if (floor % 5 == 0 && FirstTime(score, $"pack{floor}")) {
            lines.Add($"Reward: a {run.Tower.School} Tower Pack.");
            TryOpenPack(session, wizard, run.Tower.TowerPack);
        }
    }

    private static bool FirstTime(Score score, string reward) {
        lock (s_scoreLock) {
            if (score.Rewards.Contains(reward)) {
                return false;
            }

            score.Rewards.Add(reward);

            return true;
        }
    }

    private static bool GotToday(Score score, string reward) {
        lock (s_scoreLock) {
            return score.Rewards.Contains($"{reward}@{DateTime.UtcNow:yyyy-MM-dd}");
        }
    }

    private static string ItemName(uint templateId) {
        var name = (CoreObjectFactory.GetCoreTemplate(templateId) as WizItemTemplate)?.m_displayName.ToString();

        return string.IsNullOrWhiteSpace(name) ? $"item {templateId}" : name;
    }

    private static void TryOpenPack(IActorRef session, Wizard wizard, uint packTemplateId) {
        try {
            PackManager.OpenPack(session, wizard, packTemplateId);
        }
        catch (Exception ex) {
            Logger.Error("Deckathalon | could not open pack {0} for {1}: {2}", Logger.Args(packTemplateId, wizard.CharId, ex));
        }
    }

    /// <summary>How many treasure cards a wizard has in their deck.</summary>
    internal static int TreasureCardsInDeck(Wizard wizard) {
        var spells = wizard?.SpellbookBehavior?.SpellList;
        if (spells is null) {
            return 0;
        }

        var learned = wizard.SpellbookBehavior.LearnedSpellTemplateIds;

        return spells.Where(s => s is not null && (CoreObjectFactory.GetCoreTemplate(s.m_templateID) is SpellTemplate { m_Treasure: true }
                || (learned is { Count: > 0 } && !learned.Contains(s.m_templateID) && wizard.SpellbookBehavior.TreasureCardCount(s.m_templateID) > 0)))
            .Sum(s => Math.Max(1, (int) s.m_quantity));
    }

    private static void GoToFloor(IActorRef session, Run run, double delaySeconds) {
        var transfer = new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
            DestinationZone = RoomOf(run.Floor).Zone,
            DestinationLocation = "Start",
            SendToClient = true,
            IsPrivate = true,
            OwnerCharId = run.CharId,
            ResetInstance = true,
            Variant = VariantOf(run.Tower, run.Floor),
        };
        if (delaySeconds <= 0) {
            session.Tell(transfer);
        }
        else {
            Later(session, transfer, delaySeconds);
        }
    }

    private static ZONE_102_PROTOCOL.MSG_ZONETRANSFER ExitTransfer() => new() {
        DestinationZone = EXIT_ZONE,
        DestinationLocation = EXIT_LOCATION,
        SendToClient = true,
    };

    private static void Later(IActorRef session, ZONE_102_PROTOCOL.MSG_ZONETRANSFER transfer, double seconds = -1) {
        var wait = TimeSpan.FromSeconds(seconds < 0 ? s_nextFloorSeconds : seconds);
        _ = Task.Delay(wait).ContinueWith(_ => session.Tell(transfer));
    }

    private static void Tell(IActorRef session, IEnumerable<string> lines) {
        foreach (var line in lines) {
            session.Tell(new Imcodec.MessageLayer.Generated.EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = line, Modal = 0 });
        }
    }

    #endregion

    #region Stats

    /// <summary>
    /// The stats a wizard fights with in the tower: the stats of their level, with only the equipped deck's stats on
    /// top (gear, pets and jewels give none), and full health and mana. A copy: the saved wizard is not changed.
    /// With [Deckathalon] NoGearStats off, a copy of their own stats.
    /// </summary>
    internal static ServerWizGameStats StatsFor(Wizard wizard) {
        var stats = wizard.GameStats.CopyForDuel();
        if (!s_noGearStats) {
            return stats;
        }

        stats.SetBaseStats();
        stats.ClearEquipmentStats();
        // The lists ClearEquipmentStats keeps are shared with the saved wizard; the copy gets its own.
        stats.m_accReducePercent = null;
        stats.m_healBonusPercent = null;
        stats.m_healIncBonusPercent = null;
        stats.m_criticalHitPercentBySchool = stats.m_criticalHitPercentBySchool is null ? null : [.. stats.m_criticalHitPercentBySchool];
        stats.m_blockPercentBySchool = stats.m_blockPercentBySchool is null ? null : [.. stats.m_blockPercentBySchool];
        stats.m_pipConversionPercentPerSchool = stats.m_pipConversionPercentPerSchool is null ? null : [.. stats.m_pipConversionPercentPerSchool];
        stats.m_spellChargeBonus = stats.m_spellChargeBonus is null ? null : [.. stats.m_spellChargeBonus];
        stats.m_bonusHitpoints = 0;
        stats.m_bonusShadowPipRating = 0;
        stats.m_archmasteryBonus = 0;

        var equipment = wizard.EquipmentBehavior;
        var deckSlot = equipment?.SlotList?.FirstOrDefault(s => s.SlotType == EquipmentSlotType.Deck);
        var deckItem = deckSlot?.ItemId is null ? null
            : equipment.EquippedItems?.FirstOrDefault(i => i.m_globalID == deckSlot.ItemId);
        if (deckItem is not null && ItemHelper.GetItemTemplate(deckItem) is { } deckTemplate) {
            CharacterEffectHelper.AddEffectsToGameStats(stats, deckTemplate);
        }

        stats.m_currentHitpoints = stats.m_baseHitpoints;
        stats.m_currentMana = stats.m_baseMana;

        return stats;
    }

    #endregion

}
