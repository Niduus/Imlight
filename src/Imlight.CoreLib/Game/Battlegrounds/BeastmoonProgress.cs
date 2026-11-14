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
using System.Text.Json;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Battlegrounds;

/*
 * What a wizard keeps from Beastmoon between matches, in beastmoon.json next to the server (like classprojects.json):
 * - Levels: each form's level, by beast and school ("Cyclops-Life"), 0 to 4 for the game's levels 1 to 5. The game
 *   calls it the form's rating (MSG_BGISPOLYMORPHUNLOCKEDRESPONSE PolymorphRating); a form's
 *   PolymorphBattlegroundsBehavior has one m_ratingInfo per level (how many battle cards it may carry).
 * - Tiers: the tier each beast and school is unlocked to for good (its files -01 to -05; 0 is not unlocked). The game
 *   calls a form's tier its rating. The beasts of the event's match (m_unlockedPolymorphTags) are open at tier 1
 *   without it (BeastUpgrades.cs).
 * Battle Coins, the Battle Card Vendor's currency, are the game's event currency 2, kept with the wizard's stats
 * (WizGameStats m_currentEventCurrency2; the match window's counter shows it over m_baseEventCurrency2Pouch, the most a
 * wizard can hold: [Beastmoon] BattleCoinCap, 100 in the live game).
 * - BattleCards: the battle cards bought for each form tier (BG-Polymorph-Cyclops-Life-01), spell names.
 * [Beastmoon] AllTiers opens every tier and StartingLevel sets the level of a form never played (0 = level 1).
 */
internal static class BeastmoonProgress {

    internal const string PROGRESS_FILE = "beastmoon.json";

    internal static bool AllTiers => ConfigurationManager.GetValue("Beastmoon.AllTiers", false);
    internal static int StartingLevel => Math.Clamp(ConfigurationManager.GetValue("Beastmoon.StartingLevel", 0), 0, MAX_LEVEL);

    internal static int BattleCoinCap => Math.Max(0, ConfigurationManager.GetValue("Beastmoon.BattleCoinCap", 100));

    internal const int MAX_LEVEL = 4;
    internal const int MAX_TIER = 5;

    internal sealed class Hunter {
        public Dictionary<string, int> Levels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> Tiers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> BattleCards { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ProgressFile {
        public Dictionary<ulong, Hunter> Hunters { get; set; } = [];
    }

    private static readonly object s_lock = new();
    private static ProgressFile s_file;

    /// <summary>Reads, changes and saves a wizard's Beastmoon progress in one step.</summary>
    internal static T Change<T>(ulong charId, Func<Hunter, T> change) {
        lock (s_lock) {
            var hunter = HunterOf(charId);
            var result = change(hunter);
            Save();

            return result;
        }
    }

    /// <summary>Reads a wizard's progress (a copy is not made: read only, under no other lock).</summary>
    internal static T Read<T>(ulong charId, Func<Hunter, T> read) {
        lock (s_lock) {
            return read(HunterOf(charId));
        }
    }

    /// <summary>A form's level, 0 to 4 (the game's level 1 to 5).</summary>
    internal static int LevelOf(ulong charId, string formFileName)
        => Read(charId, h => h.Levels.TryGetValue(Battlegrounds.FormKey(formFileName), out var level) ? Math.Clamp(level, 0, MAX_LEVEL) : StartingLevel);

    /// <summary>The tier the wizard unlocked the form's beast and school to for good, 0 to 5 (AllTiers: 5).</summary>
    internal static int PersonalTier(ulong charId, string formFileName)
        => AllTiers ? MAX_TIER
            : Read(charId, h => h.Tiers.TryGetValue(Battlegrounds.FormKey(formFileName), out var tier) ? Math.Clamp(tier, 0, MAX_TIER) : 0);

    /// <summary>The highest tier of the form the wizard may take now: their own, or 1 when the event allows the beast.</summary>
    internal static int TierOf(ulong charId, string formFileName, bool eventAllows)
        => Math.Max(PersonalTier(charId, formFileName), eventAllows ? 1 : 0);

    internal static int BattleCoinsOf(Wizard wizard) => wizard?.GameStats?.m_currentEventCurrency2 ?? 0;

    /// <summary>
    /// Adds (or with a negative amount takes) Battle Coins, up to the cap, and tells the wizard's game. Returns how many
    /// were added (or taken).
    /// </summary>
    internal static int AddBattleCoins(Wizard wizard, IActorRef session, int amount) {
        if (wizard?.GameStats is not { } stats) {
            return 0;
        }

        var before = stats.m_currentEventCurrency2;
        stats.m_currentEventCurrency2 = Math.Clamp(before + amount, 0, Math.Max(BattleCoinCap, before));
        stats.m_baseEventCurrency2Pouch = BattleCoinCap;
        WizardData.Collections.WizardCollection.UpdateCharacterGameStats(wizard);
        session?.Tell(new WIZARD2_53_PROTOCOL.MSG_UPDATEEVENTCURRENCY2 {
            EventCurrency2 = stats.m_currentEventCurrency2,
            MaxEventCurrency2 = BattleCoinCap,
        });

        return stats.m_currentEventCurrency2 - before;
    }

    internal static List<string> BattleCardsOf(ulong charId, string formFileName)
        => Read(charId, h => h.BattleCards.TryGetValue(formFileName, out var cards) ? [.. cards] : new List<string>());

    private static Hunter HunterOf(ulong charId) {
        var file = File();
        if (!file.Hunters.TryGetValue(charId, out var hunter)) {
            hunter = new Hunter();
            file.Hunters[charId] = hunter;
        }

        // Dictionaries read back from the file compare case-sensitively; the keys are written in one case anyway.
        return hunter;
    }

    private static ProgressFile File() {
        if (s_file is not null) {
            return s_file;
        }

        try {
            s_file = System.IO.File.Exists(PROGRESS_FILE)
                ? JsonSerializer.Deserialize<ProgressFile>(System.IO.File.ReadAllText(PROGRESS_FILE)) ?? new ProgressFile()
                : new ProgressFile();
        }
        catch (Exception ex) {
            Logger.Warning("Beastmoon | could not read {0} ({1}); starting with no progress.", Logger.Args(PROGRESS_FILE, ex.Message));
            s_file = new ProgressFile();
        }

        return s_file;
    }

    private static void Save() {
        try {
            var json = JsonSerializer.Serialize(File(), new JsonSerializerOptions { WriteIndented = true });
            var temp = $"{PROGRESS_FILE}.{Guid.NewGuid():N}.tmp";
            System.IO.File.WriteAllText(temp, json);
            System.IO.File.Move(temp, PROGRESS_FILE, overwrite: true);
        }
        catch (Exception ex) {
            Logger.Warning("Beastmoon | could not save {0}: {1}", Logger.Args(PROGRESS_FILE, ex.Message));
        }
    }

}
