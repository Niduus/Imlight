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
using Imcodec.Cryptography;
using Imcodec.IO;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Bazaar;

/// <summary>What a Bazaar entry is, which decides the bag it goes into when bought.</summary>
public enum BazaarKind : byte { Item, Snack, Reagent, Card }

/// <summary>One thing the Bazaar sells. Tabs are the StringHash of the item's adjectives (the game's tabs ask by them).</summary>
public sealed record BazaarItem(ulong Id, BazaarKind Kind, string Name, int Level, int BuyPrice, int SellPrice, uint[] Tabs,
    bool NoAuction, string SpellName = null, uint SpellTemplateId = 0);

/// <summary>What the game server needs to sell or buy one Bazaar entry.</summary>
public sealed class BazaarQuote {
    public ulong Id { get; set; }
    public BazaarKind Kind { get; set; }
    public string Name { get; set; }
    public int BuyPrice { get; set; }
    public int SellPrice { get; set; }
    public int Stock { get; set; }
    public string SpellName { get; set; }
    public uint SpellTemplateId { get; set; }
}

/// <summary>
/// The whole Bazaar (Niduus, 2026-10-27: "Have the bazaar populate with stuff that is NOT no-auction items. All items in
/// the start to be at 100+"): every item, snack, reagent and treasure card of the game files that the Bazaar may take,
/// and how many of each are left. The Bazaar bot (its own program, see <see cref="BazaarBot"/>) keeps one; the game
/// server keeps its own only while the bot is not running. Every [Bazaar] setting is read when it is used, so a change
/// to Imlight.ini counts at once.
/// </summary>
public sealed class BazaarBook {

    // The template id goes with the bazaar's type bits on top; live entries use type 9.
    private const ulong BAZAAR_TYPE = 9UL << 40;
    public const int RECORD_BYTES = 20;

    // What the snack, reagent, jewel and pin window asks for when it opens (its name is not known).
    private const uint MIXED_CATEGORY = 1610882238;

    private static readonly HashSet<uint> s_snackTabs = Hashes("PetSnack", "PetSnacks", "Snack", "Snacks");
    private static readonly HashSet<uint> s_cardTabs = Hashes("TreasureCards", "TreasureCard", "Treasure Cards", "Treasure Card",
        "Treasure", "TC", "TCs", "Spell", "Spells", "SpellCard", "SpellCards", "Card", "Cards", "CAT_TreasureCards", "Category_TreasureCards");
    private static readonly uint s_jewelTab = StringHash.Compute("Jewel");

    // Folders of ObjectData that hold no real items: monsters' own gear, Magic Mirror looks, blank templates, set bonuses.
    private static readonly string[] s_skippedFolders = ["MonsterItems/", "MagicMirrorShopOptions/", "Blanks/", "ItemSetBonus/"];

    private static HashSet<uint> Hashes(params string[] names)
        => [.. names.SelectMany(name => new[] { StringHash.Compute(name), StringHash.Compute(name.ToLowerInvariant()) })];

    private static int StartStock => Math.Clamp(ConfigurationManager.GetValue("Bazaar.StartStock", 100), 1, MaxStock);
    private static int MaxStock => Math.Clamp(ConfigurationManager.GetValue("Bazaar.MaxStock", 999), 1, 1_000_000);
    private static bool WithNoAuction => ConfigurationManager.GetValue("Bazaar.NoAuctionItems", false);
    private static int CardsPerSchool => Math.Clamp(ConfigurationManager.GetValue("Bazaar.TreasureCards", 500), 0, 100_000);

    private readonly List<BazaarItem> _items;
    private readonly Dictionary<ulong, BazaarItem> _byId;
    // Each card's place in its school, cheapest first: [Bazaar] TreasureCards shows the first that many.
    private readonly Dictionary<ulong, int> _cardRank = [];

    // How many are left of the entries that are not at the start stock. Unlisted entries have the start stock.
    private readonly Dictionary<ulong, int> _stock = [];
    private readonly Lock _lock = new();
    private readonly string _stockFile;
    private bool _dirty;

    public int Count => _items.Count;

    private BazaarBook(List<BazaarItem> items, string stockFile) {
        _items = items;
        _byId = [];
        foreach (var item in items) {
            _byId.TryAdd(item.Id, item);
        }

        foreach (var school in items.Where(item => item.Kind == BazaarKind.Card).GroupBy(item => item.Tabs.FirstOrDefault())) {
            var rank = 0;
            foreach (var card in school.OrderBy(card => card.BuyPrice).ThenBy(card => card.Name, StringComparer.Ordinal)) {
                _cardRank[card.Id] = rank++;
            }
        }

        _stockFile = stockFile;
        LoadStock();
    }

    /// <summary>
    /// Reads every template of the game files once, without keeping them, so it costs no memory after. Takes a while.
    /// </summary>
    public static BazaarBook Build(string stockFile) {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var items = new List<BazaarItem>();
        var looked = 0;
        foreach (var location in CoreObjectFactory.TemplateManifest?.m_serializedTemplates ?? []) {
            string file = location?.m_filename;
            if (file is null || !file.StartsWith("ObjectData/", StringComparison.OrdinalIgnoreCase)
                || s_skippedFolders.Any(folder => file.AsSpan("ObjectData/".Length).StartsWith(folder, StringComparison.OrdinalIgnoreCase))) {
                continue;
            }

            looked++;
            try {
                // Read without CoreObjectFactory's cache, which would keep every template for good.
                if (CoreObjectFactory.LoadTemplateFile(file) is WizItemTemplate template && ItemOf(location.m_id, file, template) is { } item) {
                    items.Add(item);
                }
            }
            catch (Exception e) {
                Logger.Debug("Bazaar: could not read {0}: {1}", Logger.Args(file, e.Message));
            }
        }

        items.AddRange(Cards());

        // The order everything is sent in when a tab asks for everything: gear and housing by level, then the rest.
        items = [.. items.OrderBy(item => item.Kind).ThenBy(item => item.Level).ThenBy(item => item.Id)];
        Logger.Information("Bazaar: read {0} item templates in {1} s.", Logger.Args(looked, stopwatch.Elapsed.TotalSeconds.ToString("0.0")));

        return new BazaarBook(items, stockFile);
    }

    private static BazaarItem ItemOf(ulong id, string file, WizItemTemplate template) {
        var adjectives = (template.m_adjectiveList ?? []).Select(adjective => adjective.ToString()).ToList();
        if (adjectives.Contains("EditorOnly") || ((string) template.m_objectName ?? "").Contains("TEST", StringComparison.Ordinal)) {
            return null;
        }

        // An item without a name shows blank in the game.
        string key = template.m_displayName;
        var name = string.IsNullOrEmpty(key) ? null : Locale.GetEnglishName(key);
        if (string.IsNullOrWhiteSpace(name)) {
            return null;
        }

        var kind = template switch {
            PetSnackItemTemplate => BazaarKind.Snack,
            ReagentItemTemplate => BazaarKind.Reagent,
            _ when file.StartsWith("ObjectData/Reagents/", StringComparison.OrdinalIgnoreCase) => BazaarKind.Reagent,
            _ => BazaarKind.Item,
        };
        var tabs = adjectives.Select(StringHash.Compute).Distinct().ToArray();
        var cost = Math.Max(0, (int) template.m_baseCost);
        var sell = Math.Max(1, (int) Math.Ceiling(cost * 0.5f));

        // Snacks, reagents, jewels and pins stay at 1 gold, as they were for testing (Niduus, 2026-10-19).
        var buy = kind != BazaarKind.Item || tabs.Contains(s_jewelTab) ? 1 : -1;

        return new BazaarItem(id, kind, name, RequiredLevel(template), buy, sell, tabs, adjectives.Contains("FLAG_NoAuction"));
    }

    private static int RequiredLevel(WizItemTemplate template)
        => (int) ((template.m_equipRequirements?.m_requirements ?? [])
            .OfType<ReqMagicLevel>()
            .Where(requirement => !requirement.m_applyNOT)
            .Select(requirement => requirement.m_numericValue)
            .DefaultIfEmpty(0)
            .Max());

    // Treasure cards: the ones in the game's TreasureCards folder, one per name and school, priced from 1 to 200 gold.
    private static IEnumerable<BazaarItem> Cards() {
        var seen = new HashSet<string>();
        var cards = new List<BazaarItem>();
        // The plain "<name> TC" card comes first, so it is the one kept when a card has several versions.
        var spells = Imlight.CoreLib.Game.Spells.SpellFactory.AllSpellTemplates()
            .OrderBy(pair => ((string) pair.Template.m_name)?.EndsWith(" TC", StringComparison.Ordinal) == true ? 0 : 1)
            .ThenBy(pair => ((string) pair.Template.m_name)?.Length ?? int.MaxValue)
            .ThenBy(pair => (string) pair.Template.m_name, StringComparer.Ordinal);
        foreach (var (path, spell) in spells) {
            string name = spell.m_name;
            if (string.IsNullOrEmpty(name) || !spell.m_Treasure
                || path?.Replace('\\', '/').Contains("TreasureCards/", StringComparison.OrdinalIgnoreCase) != true
                || string.IsNullOrEmpty((string) spell.m_sMagicSchoolName) || string.IsNullOrEmpty((string) spell.m_displayName)
                || !seen.Add($"{spell.m_displayName}|{spell.m_sMagicSchoolName}")
                || Imlight.CoreLib.Game.Spells.SpellFactory.GetSpell(name) is not { } card) {
                continue;
            }

            var templateId = (uint) card.m_templateID;
            var price = Math.Clamp(spell.m_baseCost, 1, 200);
            var school = Enum.TryParse<MagicSchool>((string) spell.m_sMagicSchoolName, true, out var found) ? (uint) found : 0;
            string shown = spell.m_displayName;
            cards.Add(new BazaarItem(templateId, BazaarKind.Card, Locale.GetEnglishName(shown) is { Length: > 0 } english ? english : name,
                0, price, Math.Max(1, price / 2), school == 0 ? [] : [school], false, name, templateId));
        }

        return cards;
    }

    // Gear, housing and the rest: [Bazaar] ItemPricePercent of what the game says they are worth.
    private static int PricePercent => Math.Clamp(ConfigurationManager.GetValue("Bazaar.ItemPricePercent", 200), 0, 100_000);

    private static int PriceOf(BazaarItem item) => PriceOf(item, PricePercent);

    private static int PriceOf(BazaarItem item, int percent) {
        if (item.BuyPrice >= 0) {
            return item.BuyPrice;
        }

        return Math.Max(1, (int) Math.Min(int.MaxValue, (long) item.SellPrice * 2 * percent / 100));
    }

    private bool Sold(BazaarItem item) => !item.NoAuction || WithNoAuction;

    private int StockOf(ulong id) => StockOf(id, StartStock);

    private int StockOf(ulong id, int start) => _stock.TryGetValue(id, out var left) ? left : start;

    /// <summary>A short line of what is in the Bazaar, for the log.</summary>
    public string Describe() {
        var sold = _items.Where(Sold).ToList();
        string Count(Func<BazaarItem, bool> what) => sold.Count(what).ToString("N0");

        return $"{sold.Count:N0} things: {Count(item => item.Kind == BazaarKind.Item && !item.Tabs.Contains(s_jewelTab))} gear, " +
               $"housing and other items, {Count(item => item.Tabs.Contains(s_jewelTab))} jewels and pins, " +
               $"{Count(item => item.Kind == BazaarKind.Snack)} snacks, {Count(item => item.Kind == BazaarKind.Reagent)} reagents, " +
               $"{Count(item => item.Kind == BazaarKind.Card)} treasure cards. Each starts with {StartStock} in stock.";
    }

    // ---------------------------------------------------------------------
    // Lists
    // ---------------------------------------------------------------------

    /// <summary>
    /// The entries a tab gets, as the game's 20-byte records (id, stock, buy price, sell price). The tab's own items
    /// come first; with everything, the rest of the Bazaar follows, because the game keeps one list per window and
    /// sorts it into the tabs itself. Entries with none left are not listed.
    /// </summary>
    public byte[] Records(uint category, bool everything) {
        lock (_lock) {
            var start = StartStock;
            var cards = CardsPerSchool;
            var withNoAuction = WithNoAuction;
            var shown = _items.Where(item => (!item.NoAuction || withNoAuction) && StockOf(item.Id, start) > 0 && ShowsCard(item, cards)).ToList();
            var own = shown.Where(item => IsFor(item, category)).ToList();
            IEnumerable<BazaarItem> list = own;
            if (everything) {
                // The rest of the window the tab is in (the kind of its first item) first, then everything else.
                var kind = own.Count > 0 ? own[0].Kind : BazaarKind.Item;
                var ownIds = own.Select(item => item.Id).ToHashSet();
                list = own.Concat(shown.Where(item => !ownIds.Contains(item.Id)).OrderBy(item => item.Kind == kind ? 0 : 1));
            }
            else if (own.Count == 0) {
                list = shown;
            }

            return Write(list, start);
        }
    }

    /// <summary>The records of these entries as they are now (for the stock update after a buy or sell).</summary>
    public byte[] RecordsOf(IEnumerable<ulong> ids) {
        lock (_lock) {
            return Write(ids.Select(id => _byId.TryGetValue(id & 0xFFFFFFFFUL, out var item) ? item : null).Where(item => item is not null), StartStock);
        }
    }

    private byte[] Write(IEnumerable<BazaarItem> items, int start) {
        var writer = new BitWriter();
        var percent = PricePercent;
        foreach (var item in items) {
            writer.WriteUInt64(item.Id | BAZAAR_TYPE);
            writer.WriteInt32(StockOf(item.Id, start));
            writer.WriteInt32(PriceOf(item, percent));
            writer.WriteInt32(item.SellPrice);
        }

        return writer.GetData();
    }

    private bool ShowsCard(BazaarItem item, int cardsPerSchool)
        => item.Kind != BazaarKind.Card || (_cardRank.TryGetValue(item.Id, out var rank) && rank < cardsPerSchool);

    private static bool IsFor(BazaarItem item, uint category) {
        if (Enum.IsDefined(typeof(MagicSchool), (int) category) && category != 0) {
            // The treasure card tab asks once per school, with the school's id.
            return item.Kind == BazaarKind.Card && item.Tabs.Contains(category);
        }

        if (s_cardTabs.Contains(category)) {
            return item.Kind == BazaarKind.Card;
        }

        if (s_snackTabs.Contains(category)) {
            return item.Kind == BazaarKind.Snack;
        }

        if (category == MIXED_CATEGORY) {
            return item.Kind is BazaarKind.Snack or BazaarKind.Reagent || item.Tabs.Contains(s_jewelTab);
        }

        return item.Tabs.Contains(category);
    }

    // ---------------------------------------------------------------------
    // Buying and selling
    // ---------------------------------------------------------------------

    public BazaarQuote Find(ulong id) {
        lock (_lock) {
            if (!_byId.TryGetValue(id & 0xFFFFFFFFUL, out var item) || !Sold(item)) {
                return null;
            }

            return new BazaarQuote {
                Id = item.Id, Kind = item.Kind, Name = item.Name, BuyPrice = PriceOf(item), SellPrice = item.SellPrice,
                Stock = StockOf(item.Id), SpellName = item.SpellName, SpellTemplateId = item.SpellTemplateId,
            };
        }
    }

    /// <summary>Takes copies out of the stock for a buyer. False when there are not that many left.</summary>
    public bool Take(ulong id, int count, string who, int gold, out int left) {
        lock (_lock) {
            left = 0;
            if (!_byId.TryGetValue(id & 0xFFFFFFFFUL, out var item) || !Sold(item) || count < 1) {
                return false;
            }

            left = StockOf(item.Id);
            if (left < count) {
                return false;
            }

            left -= count;
            _stock[item.Id] = left;
            _dirty = true;
            Logger.Information("Bazaar: {0} bought {1} {2} for {3} gold; {4} left.", Logger.Args(who ?? "someone", count, item.Name, gold, left));

            return true;
        }
    }

    /// <summary>Puts what a player sold into the stock, up to [Bazaar] MaxStock. Returns the new stock.</summary>
    public int Give(ulong id, int count, string who, int gold, bool putBack = false) {
        lock (_lock) {
            if (!_byId.TryGetValue(id & 0xFFFFFFFFUL, out var item)) {
                Logger.Information("Bazaar: {0} sold {1} of template {2} for {3} gold; the Bazaar does not sell it, so it is not put up.",
                    Logger.Args(who ?? "someone", count, id & 0xFFFFFFFFUL, gold));

                return 0;
            }

            var stock = (int) Math.Min(MaxStock, (long) StockOf(item.Id) + Math.Max(0, count));
            if (stock == StartStock) {
                _stock.Remove(item.Id);
            }
            else {
                _stock[item.Id] = stock;
            }

            _dirty = true;
            if (putBack) {
                Logger.Information("Bazaar: {0} {1} put back (the buyer's bag was full); {2} in stock.", Logger.Args(count, item.Name, stock));
            }
            else {
                Logger.Information("Bazaar: {0} sold {1} {2} for {3} gold; {4} in stock now.", Logger.Args(who ?? "someone", count, item.Name, gold, stock));
            }

            return stock;
        }
    }

    /// <summary>
    /// Brings every entry below the start stock up by [Bazaar] RestockAmount (not past the start stock). Returns how
    /// many entries got more.
    /// </summary>
    public int Restock() {
        lock (_lock) {
            var amount = Math.Max(0, ConfigurationManager.GetValue("Bazaar.RestockAmount", 10));
            var start = StartStock;
            var restocked = new List<string>();
            foreach (var (id, left) in _stock.ToList()) {
                if (left >= start || amount == 0) {
                    continue;
                }

                var now = Math.Min(start, left + amount);
                if (now == start) {
                    _stock.Remove(id);
                }
                else {
                    _stock[id] = now;
                }

                restocked.Add(_byId.TryGetValue(id, out var item) ? $"{item.Name} {now}" : $"{id} {now}");
            }

            if (restocked.Count > 0) {
                _dirty = true;
                Logger.Information("Bazaar: restocked {0} items: {1}{2}.", Logger.Args(restocked.Count,
                    string.Join(", ", restocked.Take(15)), restocked.Count > 15 ? ", ..." : ""));
            }

            return restocked.Count;
        }
    }

    // ---------------------------------------------------------------------
    // Saved stock
    // ---------------------------------------------------------------------

    private void LoadStock() {
        if (_stockFile is null || !File.Exists(_stockFile)) {
            return;
        }

        try {
            var saved = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(_stockFile)) ?? [];
            foreach (var (key, left) in saved) {
                if (ulong.TryParse(key, out var id) && _byId.ContainsKey(id)) {
                    _stock[id] = Math.Max(0, left);
                }
            }

            Logger.Information("Bazaar: {0} entries have their saved stock.", Logger.Args(_stock.Count));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) {
            Logger.Warning("Bazaar: could not read the saved stock in {0}, everything starts full: {1}", Logger.Args(_stockFile, e.Message));
        }
    }

    /// <summary>Writes the stock to its file when it changed. A book without a file keeps it only in memory.</summary>
    public void SaveStock() {
        string json;
        lock (_lock) {
            if (_stockFile is null || !_dirty) {
                return;
            }

            json = JsonSerializer.Serialize(_stock.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value));
            _dirty = false;
        }

        try {
            var temp = $"{_stockFile}.tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, _stockFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Logger.Warning("Bazaar: could not save the stock to {0}: {1}", Logger.Args(_stockFile, e.Message));
            lock (_lock) {
                _dirty = true;
            }
        }
    }

}
