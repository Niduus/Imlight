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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imcodec.Types;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Loyalty;

/// <summary>
/// Majid's Loyalty Store (Niduus, 2026-10-25: "Loyalty shop is not present and have all his rotations viewable via
/// manager and able to have configs for when his wares rotate and a option in configs to announce his rotation").
///
/// Majid (WC-Shop-NPC-Loyalty, 1507720) stands in the Wizard City Shopping District. His wares change every so often:
/// the rotations are the game's monthly ones, shipped in spiraldb-extra/LoyaltyStore/Rotations.json (Wizard404). They
/// are paid with Loyalty Tokens; each account can claim TokensPerRotation tokens once per rotation in his window.
///
/// The app's Loyalty Store card writes loyalty-store.json next to the server:
///   Schedule        Monthly (the 1st of each month, midnight, as the game), Every (Days and Hours from StartedAt) or
///                   Never (StartRotation stays on).
///   Days, Hours     how long each rotation lasts with Every.
///   StartRotation   the rotation that went on at StartedAt (its Id, such as "2026-10"); the ones after it follow in
///                   order, and after the last the first comes again.
///   StartedAt       when StartRotation went on (UTC). For Monthly only its month counts.
///   Skip            rotations that are left out.
///   Shuffle         true: after each round through the rotations, the next round goes in a mixed order.
///   Announce        true: everyone online is told when the wares change.
///   TokensPerRotation  the tokens an account can claim once in each rotation.
/// With no file: Monthly, the newest rotation on this month, 5 tokens, announced.
///
/// What each account has (tokens, what it bought in this rotation, its purchase log) is kept in loyalty.json.
///
/// How the game's window talks to the server (WizardGraphicalClient.exe, 2026-10-25): the dialog option
/// (LoyaltyStoreOption) sends MSG_REQUESTEXPLICITLOYALTYSTOREUPDATE; answered with AskClientToRequestLoyaltyInfo 0
/// the client asks MSG_LOYALTYSTORELISTREQUEST, and MSG_SHOPLOYALTYLIST opens the window: LoyaltyInfoUpdateData is
/// serialized as a shop list is (no flags), LoyaltyStoreData with its serializer flags in front (the client reads
/// it with stateful flags). Buying is MSG_LOYALTYSTORE_PURCHASE_LOCK_REQUEST (answered with the price, Error 0 shows
/// the confirm box) then MSG_LOYALTYSTORE_PURCHASE_REQUEST, answered with MSG_UPDATELOYALTYPURCHASE (one
/// LoyaltyPurchaseEntry: its m_tokensBalance becomes the window's balance, its m_itemTemplateID counts a purchase).
/// </summary>
internal static class LoyaltyStore {

    internal const string CONFIG_FILE = "loyalty-store.json";
    private const string STATE_FILE = "loyalty.json";
    private const string FOLDER = "LoyaltyStore";
    private const string ROTATIONS_FILE = "Rotations.json";

    internal const uint MAJID_TEMPLATE_ID = 1507720;
    internal const string MAJID_OBJECT_NAME = "WC-Shop-NPC-Loyalty";
    private const int HISTORY_LIMIT = 20;

    internal const string MONTHLY = "Monthly";
    internal const string EVERY = "Every";
    internal const string NEVER = "Never";

    internal sealed class RotationItem {
        public uint TemplateId { get; set; }
        public string Name { get; set; } = "";
        public int Price { get; set; } = 1;
        public int Limit { get; set; } = 1;
        public string Kind { get; set; } = "";

        /// <summary>The window's sale id: the rotation's place in the file and the item's place in it.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public uint SaleId { get; set; }
    }

    internal sealed class Rotation {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<RotationItem> Items { get; set; } = [];
    }

    private sealed class RotationFile {
        public List<Rotation> Rotations { get; set; } = [];
    }

    internal sealed class Config {
        public string Schedule { get; set; } = MONTHLY;
        public int Days { get; set; } = 30;
        public int Hours { get; set; }
        public string StartRotation { get; set; } = "";
        public DateTime? StartedAt { get; set; }
        public List<string> Skip { get; set; } = [];
        public bool Shuffle { get; set; }
        public bool Announce { get; set; } = true;
        public int TokensPerRotation { get; set; } = 5;

        internal TimeSpan Length => TimeSpan.FromDays(Math.Max(0, Days)) + TimeSpan.FromHours(Math.Max(0, Hours));
    }

    /// <summary>The rotation that is on: which one, when it started and when it ends (null with Never).</summary>
    internal sealed record Current(Rotation Rotation, DateTime Start, DateTime? End) {

        /// <summary>This rotation on this start: what claims and purchase limits are counted by.</summary>
        internal string Key => $"{Rotation.Id}@{Start:yyyy-MM-ddTHH:mm}";

    }

    internal sealed class Purchase {
        public long Time { get; set; }
        public ulong CharId { get; set; }
        public string Name { get; set; } = "";
        public uint ItemId { get; set; }
        public int Tokens { get; set; }
        public int Balance { get; set; }
    }

    internal sealed class AccountState {
        public int Tokens { get; set; }
        public string ClaimedKey { get; set; } = "";
        public string BoughtKey { get; set; } = "";
        public Dictionary<uint, int> Bought { get; set; } = [];
        public List<Purchase> History { get; set; } = [];
    }

    private sealed class StateFile {
        public Dictionary<string, AccountState> Accounts { get; set; } = [];
    }

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly object s_lock = new();
    private static List<Rotation> s_rotations;
    private static Config s_config = new();
    private static DateTime s_configTime;
    private static StateFile s_state;
    private static Timer s_timer;
    private static ActorSystem s_system;
    private static string s_currentKey;

    internal static IReadOnlyList<Rotation> Rotations {
        get {
            lock (s_lock) {
                return s_rotations ??= LoadRotations();
            }
        }
    }

    internal static Config Settings {
        get {
            lock (s_lock) {
                return s_config;
            }
        }
    }

    /// <summary>Starts watching the app's file and telling everyone when the wares change. Calling it again does nothing.</summary>
    internal static void Start(ActorSystem system) {
        if (Interlocked.CompareExchange(ref s_system, system, null) is not null) {
            return;
        }

        LoadConfig();
        s_currentKey = Now()?.Key;
        if (Now() is { } current) {
            Logger.Information("Loyalty Store | Majid sells the {0} wares{1}.", Logger.Args(current.Rotation.Name, TimeLeft(current)));
        }

        s_timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    internal static bool IsMajid(CoreTemplate template)
        => template is GameObjectTemplate goTemplate
        && (goTemplate.m_templateID == MAJID_TEMPLATE_ID || goTemplate.m_objectName.ToString() == MAJID_OBJECT_NAME);

    /// <summary>The rotation that is on now, or null when there are none.</summary>
    internal static Current Now() => At(DateTime.UtcNow);

    internal static Current At(DateTime utcNow) {
        var config = Settings;
        var used = Rotations.Where(r => r.Items.Count > 0 && !(config.Skip ?? []).Contains(r.Id)).ToList();
        if (used.Count == 0) {
            used = [.. Rotations.Where(r => r.Items.Count > 0)];
        }

        if (used.Count == 0) {
            return null;
        }

        var startIndex = used.FindIndex(r => r.Id == config.StartRotation);
        if (startIndex < 0) {
            // No pick (or a skipped one): the rotation of this month if the game had one, else the newest.
            startIndex = used.FindIndex(r => r.Id == DateTime.Now.ToString("yyyy-MM"));
            startIndex = startIndex >= 0 ? startIndex : used.Count - 1;
        }

        var schedule = config.Schedule ?? MONTHLY;
        long steps;
        DateTime start;
        DateTime? end;
        // Not started from the app yet: the set went on in its own month ("2026-10"), as in the game.
        var anchor = (config.StartedAt ?? MonthOf(used[startIndex].Id) ?? MonthStart(DateTime.Now).ToUniversalTime()).ToUniversalTime();
        if (string.Equals(schedule, NEVER, StringComparison.OrdinalIgnoreCase)) {
            return new Current(used[startIndex], anchor, null);
        }

        if (string.Equals(schedule, EVERY, StringComparison.OrdinalIgnoreCase) && config.Length > TimeSpan.Zero) {
            var startedAt = anchor;
            steps = utcNow < startedAt ? 0 : (utcNow - startedAt).Ticks / config.Length.Ticks;
            start = startedAt + TimeSpan.FromTicks(config.Length.Ticks * steps);
            end = start + config.Length;
        }
        else {
            // Monthly, in the server's own time: the month StartedAt fell in is StartRotation's.
            var first = MonthStart(anchor.ToLocalTime());
            var thisMonth = MonthStart(utcNow.ToLocalTime());
            steps = Math.Max(0, (thisMonth.Year - first.Year) * 12 + thisMonth.Month - first.Month);
            start = thisMonth.ToUniversalTime();
            end = thisMonth.AddMonths(1).ToUniversalTime();
        }

        return new Current(used[IndexAfter(used.Count, startIndex, steps, config.Shuffle)], start, end);
    }

    /// <summary>
    /// Which rotation comes steps after the first: in order, and with Shuffle each round after the first is a mixed
    /// order of its own (the same every time it is worked out, so the app shows what the server sells).
    /// </summary>
    internal static int IndexAfter(int count, int startIndex, long steps, bool shuffle) {
        var position = startIndex + steps;
        var round = position / count;
        var place = (int) (position % count);
        if (!shuffle || round == 0) {
            return place;
        }

        var order = Enumerable.Range(0, count).ToArray();
        new Random((int) (round * 7919 % int.MaxValue)).Shuffle(order);

        return order[place];
    }

    private static DateTime MonthStart(DateTime local) => new(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Local);

    /// <summary>The start of the month a set is named after ("2026-10"), in UTC, or null.</summary>
    private static DateTime? MonthOf(string id)
        => DateTime.TryParseExact(id, "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var month)
            ? MonthStart(month).ToUniversalTime()
            : null;

    /// <summary>" (3d 4h left)", or nothing.</summary>
    internal static string TimeLeft(Current current) {
        if (current.End is not { } end || end <= DateTime.UtcNow) {
            return "";
        }

        var left = end - DateTime.UtcNow;
        var text = left.TotalDays >= 1 ? $"{(int) left.TotalDays}d {left.Hours}h"
            : left.TotalHours >= 1 ? $"{left.Hours}h {left.Minutes}m"
            : $"{Math.Max(1, (int) Math.Ceiling(left.TotalMinutes))}m";

        return $" ({text} left)";
    }

    internal static string Describe(Current current)
        => $"{current.Rotation.Name}: {string.Join(", ", current.Rotation.Items.Select(i => $"{i.Name} ({i.Price} token{(i.Price == 1 ? "" : "s")})"))}";

    private static void Tick() {
        try {
            if (File.Exists(CONFIG_FILE) ? File.GetLastWriteTimeUtc(CONFIG_FILE) != s_configTime : s_configTime != default) {
                LoadConfig();
            }

            var now = Now();
            if (now?.Key == s_currentKey) {
                return;
            }

            s_currentKey = now?.Key;
            if (now is null) {
                return;
            }

            Logger.Information("Loyalty Store | New wares: {0}.", Logger.Args(Describe(now)));
            if (Settings.Announce && Tools.Announcements.Text("loyalty.new", ("wares", now.Rotation.Name), ("time left", TimeLeft(now))) is { } line) {
                Broadcast(line);
            }
        }
        catch (Exception ex) {
            Logger.Error("Loyalty Store | Could not check the rotation: {0}", Logger.Args(ex.Message));
        }
    }

    private static void Broadcast(string line) {
        if (s_system is null) {
            return;
        }

        foreach (var player in OnlinePlayerCollection.GetOnlinePlayers().Where(p => p.CharacterId != 0 && p.ActorPath is not null)) {
            s_system.ActorSelection(player.ActorPath).Tell(new EXTENDEDBASE_2_PROTOCOL.MSG_SERVERMESSAGE { Message = line, Modal = 0 });
        }
    }

    // ---- What each account has ----

    /// <summary>Runs a change on an account's state and saves it.</summary>
    internal static T Change<T>(ulong accountId, Func<AccountState, T> change) {
        T result;
        string json;
        lock (s_lock) {
            var state = State;
            var key = accountId.ToString();
            if (!state.Accounts.TryGetValue(key, out var account)) {
                account = new AccountState();
                state.Accounts[key] = account;
            }

            account.Bought ??= [];
            account.History ??= [];
            if (Now() is { } now && account.BoughtKey != now.Key) {
                // A new rotation: its limits count from zero.
                account.BoughtKey = now.Key;
                account.Bought.Clear();
            }

            result = change(account);
            json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        }

        try {
            var temp = $"{STATE_FILE}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, STATE_FILE, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Warning("Loyalty Store | Could not save {0}: {1}", Logger.Args(STATE_FILE, ex.Message));
        }

        return result;
    }

    /// <summary>The tokens an account can claim in this rotation.</summary>
    internal static int CanClaim(AccountState account)
        => Now() is { } now && account.ClaimedKey != now.Key ? Math.Max(0, Settings.TokensPerRotation) : 0;

    private static StateFile State {
        get {
            if (s_state is not null) {
                return s_state;
            }

            try {
                s_state = File.Exists(STATE_FILE)
                    ? JsonSerializer.Deserialize<StateFile>(File.ReadAllText(STATE_FILE), s_options) ?? new StateFile()
                    : new StateFile();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
                Logger.Warning("Loyalty Store | Could not read {0} ({1}); starting with no tokens.", Logger.Args(STATE_FILE, ex.Message));
                s_state = new StateFile();
            }

            s_state.Accounts ??= [];

            return s_state;
        }
    }

    // ---- The game's window ----

    /// <summary>The list that opens Majid's window: the wares on now, the account's tokens and what it bought.</summary>
    internal static WIZARD3_56_PROTOCOL.MSG_SHOPLOYALTYLIST ListMessage(ulong accountId, int crowns, uint updateId) {
        var now = Now();
        var info = Change(accountId, account => InfoOf(account));
        var store = new LoyaltyStoreData {
            m_items = [.. (now?.Rotation.Items ?? []).Where(i => CoreObjectFactory.GetCoreTemplate(i.TemplateId) is not null).Select(i => new LoyaltyStoreItem {
                m_saleID = i.SaleId,
                m_templateId = (GID) (ulong) i.TemplateId,
                m_loyaltyTokensPrice = i.Price,
                m_purchaseLimit = i.Limit,
            })],
        };

        return new WIZARD3_56_PROTOCOL.MSG_SHOPLOYALTYLIST {
            Credits = crowns,
            LoyaltyInfoUpdateData = Serialize(info, SerializerFlags.None),
            LoyaltyStoreData = Serialize(store, SerializerFlags.SerializeFlags),
            Updates = "",
            UpdateID = updateId,
        };
    }

    private static LoyaltyInfoUpdateData InfoOf(AccountState account) => new() {
        m_loyaltyNumTokens = account.Tokens,
        m_loyaltyTier = 1,
        m_loyaltyLastTier = 1,
        m_loyaltyNumTokensCanClaim = CanClaim(account),
        m_loyaltyPurchaseHistoryLimit = HISTORY_LIMIT,
        m_loyaltyPurchaseHistoryData = [.. account.History.Select(EntryOf)],
        m_loyaltyPurchaseLimitCountersData = [.. account.Bought.Select(b => new LoyaltyPurchaseLimitCounterEntry {
            m_itemTemplateID = b.Key,
            m_purchaseCount = b.Value,
        })],
    };

    internal static LoyaltyPurchaseEntry EntryOf(Purchase purchase) => new() {
        m_timeOfPurchase = (int) purchase.Time,
        m_charID = (GID) purchase.CharId,
        m_packedName = purchase.Name ?? "",
        m_itemTemplateID = purchase.ItemId,
        m_tokensAmount = purchase.Tokens,
        m_tokensBalance = purchase.Balance,
    };

    internal static void AddHistory(AccountState account, Purchase purchase) {
        account.History.Insert(0, purchase);
        if (account.History.Count > HISTORY_LIMIT) {
            account.History.RemoveRange(HISTORY_LIMIT, account.History.Count - HISTORY_LIMIT);
        }
    }

    /// <summary>The ware on now with this sale id (or, failing that, this item).</summary>
    internal static RotationItem WareOf(uint saleId, uint templateId) {
        var items = Now()?.Rotation.Items ?? [];

        return items.FirstOrDefault(i => i.SaleId == saleId && saleId != 0) ?? items.FirstOrDefault(i => i.TemplateId == templateId);
    }

    internal static Imcodec.IO.ByteString Serialize(PropertyClass value, SerializerFlags flags) {
        var serializer = new ObjectSerializer(Versionable: false, Behaviors: flags);
        if (!serializer.Serialize(value, PropertyFlags.Prop_Save | PropertyFlags.Prop_Public, out var data)) {
            Logger.Error("Loyalty Store | Could not serialize {0}.", Logger.Args(value.GetType().Name));

            return "";
        }

        return data;
    }

    // ---- Files ----

    private static List<Rotation> LoadRotations() {
        var path = Path.Combine(SpiralDB.ExtraPath, FOLDER, ROTATIONS_FILE);
        try {
            var file = File.Exists(path) ? JsonSerializer.Deserialize<RotationFile>(File.ReadAllText(path), s_options) : null;
            var rotations = (file?.Rotations ?? []).Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Id)).ToList();
            for (var r = 0; r < rotations.Count; r++) {
                rotations[r].Items = [.. (rotations[r].Items ?? []).Where(i => i is not null && i.TemplateId != 0)];
                for (var i = 0; i < rotations[r].Items.Count; i++) {
                    rotations[r].Items[i].SaleId = (uint) ((r + 1) * 100 + i + 1);
                }
            }

            Logger.Information("Loyalty Store | {0} rotations of Majid's wares loaded.", Logger.Args(rotations.Count));

            return rotations;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            Logger.Warning("Loyalty Store | Could not read {0}: {1}", Logger.Args(path, ex.Message));

            return [];
        }
    }

    private static void LoadConfig() {
        if (!File.Exists(CONFIG_FILE)) {
            lock (s_lock) {
                s_config = new Config();
                s_configTime = default;
            }

            return;
        }

        try {
            var time = File.GetLastWriteTimeUtc(CONFIG_FILE);
            var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(CONFIG_FILE), s_options) ?? new Config();
            config.Skip ??= [];
            lock (s_lock) {
                s_config = config;
                s_configTime = time;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            // The app is still writing the file; it is read on the next round.
        }
    }

}
