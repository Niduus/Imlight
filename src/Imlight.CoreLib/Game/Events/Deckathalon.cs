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
 * - The way in is the live game's: the event window's Go button sends the wizard to the Library, where the game's
 *   portal to the Duel Arena shows only while a Deckathalon is on (WC-Teleport-LibToArena, its spawn requirements ask
 *   for an active TreasureTower_01_<School>). In the arena, the portal of the school that is on (HO-TreasureTower_01_
 *   <School>, spawned while the game registry entry TreasureTower_01_<School> is 1) starts the climb.
 * - The floors are the game's own tower zones, WizardCity/TreasureTower/WC_TT01_<School>_L01 to _L30, each a private
 *   copy as they are: the floor's creature on its circle, walking up to it starts the battle, and winning opens the
 *   portal to the next floor (Trigger Teleport). The zone's exit and the end of the climb lead back to the arena.
 * - Scores (best floor and points, per event) and the rewards a wizard already had are kept in deckathalon.json next
 *   to the server.
 * - Event points, as the game's event counts them: 1 for each floor beaten and 1 for each treasure card used in any
 *   battle while the event is on (up to 20 a day); kept as tenths (10 a point). A climb also keeps its own points
 *   for chat: 100 for each floor number cleared, 10 for each treasure card used, and up to 125 more for a quick win.
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

    internal static bool Enabled => ConfigurationManager.GetValue("Deckathalon.Enabled", true);
    internal static bool TreasureCardsOnly => ConfigurationManager.GetValue("Deckathalon.TreasureCardsOnly", true);
    private static bool s_noGearStats => ConfigurationManager.GetValue("Deckathalon.NoGearStats", true);
    private static bool s_alwaysOpen => ConfigurationManager.GetValue("Deckathalon.AlwaysOpen", false);
    private static bool s_starterPack => ConfigurationManager.GetValue("Deckathalon.StarterPack", true);
    // Only the floor's portal takes a wizard on, as in the game: there is no timed move up, whatever an older
    // NextFloorSeconds setting says (Niduus, 2026-10-25: "ME WALKING TO THE TRIGGER IS WHAT SHOAULD TELEPORT ME AND
    // ONLY THEN").
    private static int s_floors => Math.Clamp(ConfigurationManager.GetValue("Deckathalon.Floors", TOP_FLOOR), 1, TOP_FLOOR);

    // Credits (Niduus, 2026-10-25: "Deckathalon starts with a claim of 3 credits every 24 hours (Config this) and you can
    // stash them during THAT event. Each entry is a credit"). Credits belong to one event and are kept until it ends.
    // Read each time, so a change in the Manager's Settings applies without a server restart.
    private static int s_claimCredits => Math.Max(0, ConfigurationManager.GetValue("Deckathalon.ClaimCredits", 3));
    private static double s_claimHours => Math.Max(0, ConfigurationManager.GetValue("Deckathalon.ClaimHours", 24.0));
    private static int s_entryCredits => Math.Max(0, ConfigurationManager.GetValue("Deckathalon.EntryCredits", 1));

    // Where a wizard goes when they leave the tower: the tower portals of the Duel Arena, as in the game.
    internal const string ARENA_ZONE = "WizardCity/WC_Duel_Arena";
    internal const string ARENA_LOCATION = "From TT01";

    // The game's tower zones (WizardCity/TreasureTower/WC_TT01_<School>_L<Floor>), and the triggers of their portals.
    private const string FLOOR_ZONE_PREFIX = "WizardCity/TreasureTower/WC_TT01_";
    private const string NEXT_FLOOR_TRIGGER = "Trigger Teleport";

    // Every 3rd floor is a checkpoint, as the game's TreasureTower_01_<School> template lists them (its m_locationList:
    // checkpoint 1 starts on floor 4, 2 on floor 7, ... 9 on floor 28).
    internal const int CHECKPOINT_FLOORS = 3;
    internal const int MAX_CHECKPOINT = 9;

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
        // The day (UTC, yyyy-MM-dd) of the daily counts below: treasure cards and floors that gave event points.
        public string Day { get; set; } = "";
        public int CardsToday { get; set; }
        public int FloorsToday { get; set; }
        // The highest checkpoint reached (0 = none, 1 = floor 3 cleared, ...): a later climb can start after it.
        public int Checkpoint { get; set; }
        // The event credits the tower portal's window shows (given with `.credits`).
        public int Credits { get; set; }
        // When the wizard last claimed credits (UTC, round-trip format); empty when never.
        public string LastClaim { get; set; } = "";
    }

    // The tower portal's window was opened for these wizards (with the school), so its Use button starts their climb.
    private static readonly ConcurrentDictionary<ulong, string> s_portalOffers = new();

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

    /// <summary>The best wizards of an event: the most event points first (as the game's leaderboard), then the highest floor.</summary>
    internal static List<Score> Top(string eventKey, int count) {
        var scores = Scores;
        lock (s_scoreLock) {
            return scores.Events.TryGetValue(eventKey, out var byWizard)
                ? [.. byWizard.Values.Where(s => s.Points > 0 || s.BestFloor > 0).OrderByDescending(s => s.Points).ThenByDescending(s => s.BestFloor).Take(count)]
                : [];
        }
    }

    /// <summary>The best wizards of an event with their character ids, in the order of <see cref="Top"/>.</summary>
    internal static List<(ulong CharId, Score Score)> TopWithIds(string eventKey, int count) {
        var scores = Scores;
        lock (s_scoreLock) {
            return scores.Events.TryGetValue(eventKey, out var byWizard)
                ? [.. byWizard.Where(e => e.Value.Points > 0 || e.Value.BestFloor > 0)
                    .OrderByDescending(e => e.Value.Points).ThenByDescending(e => e.Value.BestFloor).Take(count)
                    .Select(e => (ulong.TryParse(e.Key, out var id) ? id : 0UL, e.Value))]
                : [];
        }
    }

    /// <summary>A wizard's best of an event and their place among everyone's (1 is first), or null when they have no points yet.</summary>
    internal static (Score Score, int Place)? PlaceOf(string eventKey, ulong charId) {
        var scores = Scores;
        lock (s_scoreLock) {
            if (!scores.Events.TryGetValue(eventKey, out var byWizard) || !byWizard.TryGetValue(charId.ToString(), out var mine)
                || (mine.Points <= 0 && mine.BestFloor <= 0)) {
                return null;
            }

            var ahead = byWizard.Values.Count(s => s != mine
                && (s.Points > mine.Points || (s.Points == mine.Points && s.BestFloor > mine.BestFloor)));

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

    /// <summary>The game's zone of a tower floor, such as WizardCity/TreasureTower/WC_TT01_Fire_L01.</summary>
    internal static string FloorZone(Tower tower, int floor) => $"{FLOOR_ZONE_PREFIX}{tower.School}_L{Math.Clamp(floor, 1, TOP_FLOOR):00}";

    /// <summary>Whether a zone is one of the game's tower floors.</summary>
    internal static bool IsFloorZone(string zone) => zone?.StartsWith(FLOOR_ZONE_PREFIX, StringComparison.OrdinalIgnoreCase) == true;

    private static bool OnFloor(Wizard wizard, Run run, int floor)
        => string.Equals(wizard.Zone, FloorZone(run.Tower, floor), StringComparison.OrdinalIgnoreCase);

    /// <summary>The creature of a floor.</summary>
    internal static uint CreatureOf(Tower tower, int floor) => tower.Floors[Math.Clamp(floor, 1, TOP_FLOOR) - 1];

    /// <summary>The name a creature template shows in the game, or its template id.</summary>
    internal static string ShownName(uint templateId) {
        var template = CoreObjectFactory.GetCoreTemplate(templateId) as WizGameObjectTemplate;
        var name = template?.m_displayName.ToString();

        return string.IsNullOrWhiteSpace(name) ? templateId.ToString() : name;
    }

    #endregion

    #region Runs

    /// <summary>The climb a wizard is on, or null.</summary>
    internal static Run RunOf(ulong charId) => s_runs.TryGetValue(charId, out var run) ? run : null;

    /// <summary>Whether a wizard's battle is in the tower: the wizard is climbing and stands in their floor's room.</summary>
    internal static bool FightsHere(Wizard wizard)
        => Enabled && wizard is not null && RunOf(wizard.CharId) is { Won: false } run
           && OnFloor(wizard, run, run.Floor);

    /// <summary>
    /// Starts a climb from floor 1: the school's tower when its Deckathalon is on, or (for the server's testers, or with
    /// [Deckathalon] AlwaysOpen) any school's. Returns false with the reason when it cannot.
    /// </summary>
    internal static bool TryStart(IActorRef session, Wizard wizard, string school, bool tester, out string reply, int checkpoint = 0) {
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

        // A climb of the event's tower costs credits; practice climbs of another tower are free.
        if (isEvent && s_entryCredits > 0) {
            var paying = ScoreOf(current.Key, wizard.CharId, name);
            lock (s_scoreLock) {
                if (paying.Credits < s_entryCredits) {
                    reply = $"You need {s_entryCredits} Deckathalon credit{(s_entryCredits == 1 ? "" : "s")} to climb and have {paying.Credits}. "
                        + (s_claimCredits > 0 ? $"Claim {s_claimCredits} free credits at the arena's tower portal every {s_claimHours:0.#} hours." : "");

                    return false;
                }

                paying.Credits -= s_entryCredits;
            }
        }

        var run = new Run {
            CharId = wizard.CharId,
            Name = name,
            Tower = tower,
            EventKey = isEvent ? current.Key : $"Practice-{tower.School}",
        };
        var score = ScoreOf(run.EventKey, wizard.CharId, name);
        lock (s_scoreLock) {
            score.Runs++;
            // A saved checkpoint the wizard picked: the climb starts on the floor after it.
            run.Floor = Math.Clamp(Math.Min(checkpoint, score.Checkpoint) * CHECKPOINT_FLOORS + 1, 1, s_floors);
        }

        s_runs[wizard.CharId] = run;
        s_portalOffers.TryRemove(wizard.CharId, out _);

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

        if (run.Floor > 1) {
            lines.Add($"You start from your checkpoint, on floor {run.Floor}.");
        }

        lines.Add("Step into the battle circle to fight. Win to go up a floor. Type .deckathalon leave to leave.");
        reply = string.Join("<br>", lines);
        GoToFloor(session, run);

        return true;
    }

    /// <summary>
    /// The highest checkpoint a wizard reached in the school's tower during the event that is on (0 when none, or no
    /// such event is on: practice climbs always start from floor 1 at the portal).
    /// </summary>
    internal static int CheckpointOf(Wizard wizard, string school) {
        if (!Enabled || wizard is null || CurrentEvent is not { } current || TowerOf(school) is not { } tower
            || current.School != tower.School) {
            return 0;
        }

        return Math.Clamp(ScoreOf(current.Key, wizard.CharId, null).Checkpoint, 0, Math.Min(MAX_CHECKPOINT, (s_floors - 1) / CHECKPOINT_FLOORS));
    }

    /// <summary>A wizard's credits in the Deckathalon that is on (0 when none is on).</summary>
    internal static int CreditsOf(Wizard wizard)
        => Enabled && wizard is not null && CurrentEvent is { } current ? ScoreOf(current.Key, wizard.CharId, null).Credits : 0;

    /// <summary>
    /// Gives a wizard credits in the Deckathalon that is on (a negative amount takes them away, never below 0).
    /// </summary>
    /// <returns>The wizard's credits now, or null when no Deckathalon is on.</returns>
    internal static int? AddCredits(Wizard wizard, int amount) {
        if (!Enabled || wizard is null || CurrentEvent is not { } current) {
            return null;
        }

        int now;
        var score = ScoreOf(current.Key, wizard.CharId, wizard.PlayerNameBehavior?.GetWizardName());
        lock (s_scoreLock) {
            score.Credits = (int) Math.Clamp((long) score.Credits + amount, 0, int.MaxValue);
            now = score.Credits;
        }

        SaveScores();

        return now;
    }

    /// <summary>How many credits one climb of the event's tower costs ([Deckathalon] EntryCredits; 0 = free).</summary>
    internal static int EntryCredits => s_entryCredits;

    /// <summary>Whether a wizard can claim credits now: never claimed in this event, or ClaimHours have passed since.</summary>
    internal static bool CanClaim(Wizard wizard) {
        if (!Enabled || s_claimCredits <= 0 || wizard is null || CurrentEvent is not { } current) {
            return false;
        }

        var score = ScoreOf(current.Key, wizard.CharId, null);
        lock (s_scoreLock) {
            return ClaimDue(score);
        }
    }

    private static bool ClaimDue(Score score)
        => !DateTime.TryParse(score.LastClaim, null, System.Globalization.DateTimeStyles.RoundtripKind, out var last)
            || DateTime.UtcNow >= last.ToUniversalTime().AddHours(s_claimHours);

    /// <summary>Claims the wizard's credits in the event that is on when they are due.</summary>
    /// <returns>A message for the wizard, or null when nothing was due.</returns>
    internal static string Claim(Wizard wizard) {
        if (!Enabled || s_claimCredits <= 0 || wizard is null || CurrentEvent is not { } current) {
            return null;
        }

        int now;
        var score = ScoreOf(current.Key, wizard.CharId, wizard.PlayerNameBehavior?.GetWizardName());
        lock (s_scoreLock) {
            if (!ClaimDue(score)) {
                return null;
            }

            score.Credits = (int) Math.Min((long) score.Credits + s_claimCredits, int.MaxValue);
            score.LastClaim = DateTime.UtcNow.ToString("o");
            now = score.Credits;
        }

        SaveScores();
        Logger.Information("Deckathalon | {0} claimed {1} credits ({2} now).", Logger.Args(wizard.CharId, s_claimCredits, now));

        return $"You claimed {s_claimCredits} Deckathalon credits. You have {now} now; each climb uses {s_entryCredits}.";
    }

    /// <summary>Whether the school's tower is the one of the Deckathalon that is on.</summary>
    internal static bool IsEventTower(string school)
        => Enabled && CurrentEvent is { } current && TowerOf(school) is { } tower && current.School == tower.School;

    /// <summary>Remembers that the tower portal's window was opened for a wizard, so its Use button starts the climb.</summary>
    internal static void OfferPortal(ulong charId, string school) => s_portalOffers[charId] = school;

    /// <summary>The school of the tower portal window opened for a wizard, once; null when none was.</summary>
    /// <summary>The tower a wizard's open portal window is for, without taking the offer.</summary>
    internal static string PortalOfferOf(ulong charId) => s_portalOffers.TryGetValue(charId, out var school) ? school : null;

    internal static string TakePortalOffer(ulong charId) => s_portalOffers.TryRemove(charId, out var school) ? school : null;

    /// <summary>Ends a climb and sends the wizard out of the tower.</summary>
    internal static string Leave(IActorRef session, Wizard wizard) {
        if (!s_runs.TryRemove(wizard.CharId, out var run)) {
            return "You are not climbing the Deckathalon.";
        }

        var inTower = IsFloorZone(wizard.Zone);
        if (inTower) {
            session.Tell(ExitTransfer());
        }

        return $"You left the {run.Tower.School} Deckathalon on floor {run.Floor} with {run.Points} points.";
    }

    /// <summary>
    /// A treasure card used in battle: 10 points of the climb in the tower, and while a Deckathalon is on, a point of the
    /// event anywhere, as the game's event counts them (TreasureTower_01_<School>: CPT_CastTreasureCard, 1 point, 20 a
    /// day). Each point shows the game's "+1" event toast (Niduus, 2026-10-25: "not getting the +1 to event
    /// notification for using a treasure card", "points not tracking").
    /// </summary>
    internal static void CardUsed(Wizard wizard, IActorRef client) {
        if (!Enabled || wizard is null) {
            return;
        }

        if (FightsHere(wizard) && RunOf(wizard.CharId) is { } run) {
            run.CardsUsed++;
            run.Points += 10;
        }

        if (CurrentEvent is not { } current) {
            return;
        }

        var score = ScoreOf(current.Key, wizard.CharId, wizard.PlayerNameBehavior?.GetWizardName() ?? wizard.CharId.ToString());
        lock (s_scoreLock) {
            Today(score);
            if (score.CardsToday >= CARDS_PER_DAY) {
                return;
            }

            score.CardsToday++;
            score.Points += 10;
        }

        SaveScores();
        Toast(client, current, score);
    }

    // The game's event counts: a point for each floor beaten, and a point for each treasure card, up to 20 a day.
    private const int CARDS_PER_DAY = 20;

    // Starts a new day of a score's daily counts (the event's days are UTC days, as m_currentDay).
    private static void Today(Score score) {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (score.Day != today) {
            score.Day = today;
            score.CardsToday = 0;
            score.FloorsToday = 0;
        }
    }

    // The game's "+1" event toast (ClientClassProjectComponent::UpdateClassProjectStatus shows Points when it is not 0).
    private static void Toast(IActorRef client, GameEvents.RunningEvent current, Score score)
        => client?.Tell(new Imcodec.MessageLayer.Generated.WIZARD2_53_PROTOCOL.MSG_UPDATECLASSPROJECTSTATUS {
            Status = 1,
            ProjectID = EventButton.ProjectId(current),
            Count = (uint) Math.Max(0, score.Points / 10),
            Points = 1,
            HasUnclaimedReward = 0,
        });

    /// <summary>
    /// A won battle: when it was the wizard's floor, the floor counts (points, best floor, rewards) and its portal
    /// opens; walking into it goes up to the next floor (or out of the tower after the top one).
    /// </summary>
    internal static void OnWin(IActorRef session, Wizard wizard, IEnumerable<ulong> mobTemplateIds, int rounds) {
        if (!Enabled || wizard is null || RunOf(wizard.CharId) is not { Won: false } run) {
            return;
        }

        // The floor's zone has only its own creature, so a battle won there is the floor's.
        if (!OnFloor(wizard, run, run.Floor)) {
            return;
        }

        try {
            var floor = run.Floor;
            run.Points += 100 * floor + Math.Max(0, 6 - Math.Max(1, rounds)) * 25;
            var score = ScoreOf(run.EventKey, run.CharId, run.Name);
            var lines = new List<string>();
            lock (s_scoreLock) {
                score.BestFloor = Math.Max(score.BestFloor, floor);
                if (floor % CHECKPOINT_FLOORS == 0 && floor < s_floors && floor / CHECKPOINT_FLOORS > score.Checkpoint) {
                    score.Checkpoint = Math.Min(floor / CHECKPOINT_FLOORS, MAX_CHECKPOINT);
                    lines.Add($"Checkpoint saved: next time you enter the tower, you can start from floor {floor + 1}.");
                }

                // The event's point for the floor (CPT_DefeatMob), counted like the treasure cards' (CardUsed).
                Today(score);
                score.FloorsToday++;
                score.Points += 10;
            }

            Rewards(session, wizard, run, score, floor, lines);
            SaveScores();
            if (CurrentEvent is { } current && current.Key == run.EventKey) {
                Toast(session, current, score);
            }

            Logger.Information("Deckathalon | {0} clears floor {1} of the {2} tower ({3} points).",
                Logger.Args(run.Name, floor, run.Tower.School, run.Points));

            if (floor >= s_floors) {
                run.Won = true;
                s_runs.TryRemove(run.CharId, out _);
                lines.Insert(0, floor >= TOP_FLOOR
                    ? $"You beat {run.Tower.Professor} and conquered the {run.Tower.School} Deckathalon with {run.Points} points!"
                    : $"You reached the top of the {run.Tower.School} Deckathalon (floor {floor}) with {run.Points} points!");
                lines.Add("Walk into the portal to go back to the arena.");
                Tell(session, lines);

                return;
            }

            run.Floor = floor + 1;
            var next = CreatureOf(run.Tower, run.Floor);
            lines.Insert(0, $"Floor {floor} cleared ({run.Points} points). Walk into the portal to go up to floor {run.Floor}: "
                + (run.Floor == TOP_FLOOR ? $"{run.Tower.Professor} waits there." : $"{ShownName(next)}."));
            Tell(session, lines);
        }
        catch (Exception ex) {
            Logger.Error("Deckathalon | the win on floor {0} of {1} failed: {2}", Logger.Args(run.Floor, run.Name, ex));
        }
    }

    /// <summary>A lost (or fled) battle on the wizard's floor ends the climb.</summary>
    internal static void OnDefeat(IActorRef session, Wizard wizard) {
        if (!Enabled || wizard is null || RunOf(wizard.CharId) is not { } run
            || !OnFloor(wizard, run, run.Floor)) {
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

    private static void GoToFloor(IActorRef session, Run run) => session.Tell(new ZONE_102_PROTOCOL.MSG_ZONETRANSFER {
        DestinationZone = FloorZone(run.Tower, run.Floor),
        DestinationLocation = "Start",
        SendToClient = true,
        IsPrivate = true,
        OwnerCharId = run.CharId,
        ResetInstance = true,
    });

    /// <summary>
    /// A teleport of a tower floor, which names no destination: the next-floor portal (open once the floor is won) goes
    /// up a floor, and every other one (the zone's exit, the top floor's portal) back to the arena. Returns false when
    /// the zone is not a tower floor.
    /// </summary>
    internal static bool FloorTeleport(IActorRef session, ulong charId, string zone, string triggerName) {
        if (!IsFloorZone(zone)) {
            return false;
        }

        if (string.Equals(triggerName, NEXT_FLOOR_TRIGGER, StringComparison.OrdinalIgnoreCase)
            && RunOf(charId) is { Won: false } run
            && !string.Equals(zone, FloorZone(run.Tower, run.Floor), StringComparison.OrdinalIgnoreCase)) {
            GoToFloor(session, run);

            return true;
        }

        if (RunOf(charId) is { } leaving) {
            // Walking out of the tower ends the climb, as .deckathalon leave does.
            s_runs.TryRemove(charId, out _);
            Tell(session, [$"You left the {leaving.Tower.School} Deckathalon on floor {leaving.Floor} with {leaving.Points} points."]);
        }

        session.Tell(ExitTransfer());

        return true;
    }

    private static ZONE_102_PROTOCOL.MSG_ZONETRANSFER ExitTransfer() => new() {
        DestinationZone = ARENA_ZONE,
        DestinationLocation = ARENA_LOCATION,
        SendToClient = true,
    };


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
