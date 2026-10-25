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
 * CROWN SHOP SETTINGS
 * ========================================================================
 *
 * PURPOSE:
 * What the Manager's Crown Shop card sets (Niduus, 2026-10-25): the items on the Featured tab, the owner's own sales
 * and the Crowns Rewards event. The app writes crown-shop.json next to the server; the server reads it again within a
 * second of a change, with no restart.
 *
 * NOTE:
 * - A sale sells an item for its own crowns and gold price, with the normal price crossed out and the SALE sticker.
 *   A sale with an end time (Until) stops then; one without stays on.
 * - Crowns Rewards: while the event is on, crowns spent in the shop add up per account, and each tier reached gives
 *   its reward once. A new event (a new StartedAt) starts everyone at 0. Progress is kept in crowns-rewards.json.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;

namespace Imlight.CoreLib.Game.CrownShop;

public static class CrownShopConfig {

    private const string FILE = "crown-shop.json";
    private const string PROGRESS_FILE = "crowns-rewards.json";

    private static readonly object s_lock = new();
    private static readonly JsonSerializerOptions s_json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public sealed class Sale {
        public ulong TemplateId { get; set; }
        public int Crowns { get; set; }
        public int Gold { get; set; }
        public DateTime? Until { get; set; }
    }

    public sealed class RewardTier {
        public int Crowns { get; set; }
        public ulong TemplateId { get; set; }
        public int Count { get; set; } = 1;
    }

    public sealed class Rewards {
        public bool On { get; set; }
        public string Name { get; set; } = "Crowns Rewards";
        public DateTime? StartedAt { get; set; }
        public DateTime? Until { get; set; }
        public List<RewardTier> Tiers { get; set; } = [];
    }

    private sealed class FileData {
        public List<ulong> Featured { get; set; } = [];
        public List<Sale> Sales { get; set; } = [];
        public Rewards Rewards { get; set; } = new();
    }

    private sealed class Progress {
        public string Event { get; set; } = "";
        public Dictionary<ulong, int> Spent { get; set; } = [];
        public Dictionary<ulong, int> TiersGiven { get; set; } = [];
    }

    private static FileData s_data = new();
    private static DateTime s_fileTime = DateTime.MinValue;
    private static DateTime s_checked = DateTime.MinValue;
    private static bool s_hasFile;
    private static Progress s_progress;

    /// <summary>Goes up each time the file changes or a sale ends, so the shop list is built again.</summary>
    public static int Version { get; private set; }

    /// <summary>The items on the Featured tab, in order.</summary>
    public static IReadOnlyList<ulong> Featured {
        get {
            Refresh();
            return s_data.Featured;
        }
    }

    /// <summary>The sale on an item right now, or null.</summary>
    public static Sale SaleOn(ulong templateId) {
        Refresh();
        var now = DateTime.UtcNow;

        return s_data.Sales.LastOrDefault(s => s.TemplateId == templateId && (s.Until is null || s.Until > now));
    }

    /// <summary>The Crowns Rewards event, when it is on right now.</summary>
    public static Rewards ActiveRewards {
        get {
            Refresh();
            var rewards = s_data.Rewards;

            return rewards is { On: true } && rewards.Tiers.Count > 0 && (rewards.Until is null || rewards.Until > DateTime.UtcNow)
                ? rewards : null;
        }
    }

    /// <summary>
    /// Adds crowns an account spent while Crowns Rewards is on, and returns the tiers it reached by it (each once).
    /// </summary>
    public static List<RewardTier> AddSpent(ulong accountId, int crowns) {
        var rewards = ActiveRewards;
        if (rewards is null || crowns <= 0) {
            return [];
        }

        lock (s_lock) {
            var progress = LoadProgress(EventKey(rewards));
            var spent = progress.Spent.GetValueOrDefault(accountId) + crowns;
            progress.Spent[accountId] = spent;
            var tiers = rewards.Tiers.OrderBy(t => t.Crowns).ToList();
            var given = progress.TiersGiven.GetValueOrDefault(accountId);
            var reached = tiers.Skip(given).TakeWhile(t => spent >= t.Crowns).ToList();
            progress.TiersGiven[accountId] = given + reached.Count;
            SaveProgress(progress);

            return reached;
        }
    }

    /// <summary>What an account has spent in the event that is on, and the next tier, for the shop's message.</summary>
    public static (int Spent, RewardTier Next) Standing(ulong accountId) {
        var rewards = ActiveRewards;
        if (rewards is null) {
            return (0, null);
        }

        lock (s_lock) {
            var progress = LoadProgress(EventKey(rewards));
            var given = progress.TiersGiven.GetValueOrDefault(accountId);

            return (progress.Spent.GetValueOrDefault(accountId), rewards.Tiers.OrderBy(t => t.Crowns).Skip(given).FirstOrDefault());
        }
    }

    private static string EventKey(Rewards rewards) => $"{rewards.Name}|{rewards.StartedAt:o}";

    private static Progress LoadProgress(string key) {
        if (s_progress is null) {
            try {
                s_progress = File.Exists(PROGRESS_FILE)
                    ? JsonSerializer.Deserialize<Progress>(File.ReadAllText(PROGRESS_FILE), s_json) ?? new Progress()
                    : new Progress();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) {
                Logger.Warning("CrownShop: could not read {0}: {1}", Logger.Args(PROGRESS_FILE, ex.Message));
                s_progress = new Progress();
            }
        }

        if (s_progress.Event != key) {
            s_progress = new Progress { Event = key };
        }

        return s_progress;
    }

    private static void SaveProgress(Progress progress) {
        try {
            var temp = $"{PROGRESS_FILE}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(progress, s_json));
            File.Move(temp, PROGRESS_FILE, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Warning("CrownShop: could not save {0}: {1}", Logger.Args(PROGRESS_FILE, ex.Message));
        }
    }

    // Reads the file again when it changed, at most once a second; a sale that ended also counts as a change.
    private static void Refresh() {
        var now = DateTime.UtcNow;
        if (now - s_checked < TimeSpan.FromSeconds(1)) {
            return;
        }

        lock (s_lock) {
            if (now - s_checked < TimeSpan.FromSeconds(1)) {
                return;
            }
            s_checked = now;

            var changed = false;
            var exists = File.Exists(FILE);
            if (!exists && s_hasFile) {
                s_data = new FileData();
                s_hasFile = false;
                changed = true;
            }
            else if (exists && File.GetLastWriteTimeUtc(FILE) != s_fileTime) {
                try {
                    var time = File.GetLastWriteTimeUtc(FILE);
                    s_data = JsonSerializer.Deserialize<FileData>(File.ReadAllText(FILE), s_json) ?? new FileData();
                    s_data.Featured ??= [];
                    s_data.Sales ??= [];
                    s_data.Rewards ??= new Rewards();
                    s_data.Rewards.Tiers ??= [];
                    s_fileTime = time;
                    s_hasFile = true;
                    changed = true;
                    Logger.Information("CrownShop: {0} featured items, {1} sales, Crowns Rewards {2}.",
                        Logger.Args(s_data.Featured.Count, s_data.Sales.Count, s_data.Rewards.On ? "on" : "off"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
                    // The app is still writing it; it is read on the next round.
                    Logger.Debug("CrownShop: could not read {0} yet: {1}", Logger.Args(FILE, ex.Message));
                }
            }

            var ended = s_data.Sales.Count(s => s.Until is { } until && until <= now);
            if (ended != s_endedSales) {
                s_endedSales = ended;
                changed = true;
            }

            if (changed) {
                Version++;
            }
        }
    }

    private static int s_endedSales;

}
