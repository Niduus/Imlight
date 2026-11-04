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
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

namespace Imlight.Setup;

/// <summary>
/// The Wiki tab (Niduus, 2026-11-04: "Since we have everything from the wiki, might as well treat it as a wiki to an
/// extent. Have the ability to look up mobs, their cheats and so on"): one search over every creature, spell and item,
/// and a page for each like a wiki's. A creature's page has its school, rank, health, stats, where it is, who comes with
/// it, its spells, its cheats and what it drops; a spell's page who casts it and who teaches or sells it; an item's page
/// who drops it, which quests give it and who sells it. Names on a page open their own page. It only reads: changes are
/// made on the Bosses, Customs and other tabs, which a button opens.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string WIKI = "Wiki";
    private const string WIKI_PLACES = "creature-places.json.gz";
    private const string WIKI_CREATURE = "Creature";
    private const string WIKI_SPELL = "Spell";
    private const string WIKI_ITEM = "Item";
    private const int WIKI_SHOWN = 300;
    private const int WIKI_LINKS = 60;

    private static readonly string[] s_wikiKinds = ["Creatures", "Spells", "Items", "Everything"];
    private static readonly string[] s_wikiSorts = ["Best match", "Name (A to Z)", "Rank (high first)", "Rank (low first)", "Health (high first)"];

    private readonly TextBox _wikiSearch = new() { Watermark = "Look up a creature, spell or item by name or ID, such as Rattlebones, Fire Cat or Dragon's Hoard Pack" };
    private readonly ComboBox _wikiKind = FilterCombo(s_wikiKinds, 140);
    private readonly ComboBox _wikiSchool = FilterCombo([ANY_FILTER], 130);
    private readonly ComboBox _wikiWorld = FilterCombo([ANY_FILTER], 160);
    private readonly NumericUpDown _wikiRankMin = FilterNumber();
    private readonly NumericUpDown _wikiRankMax = FilterNumber();
    private readonly ComboBox _wikiSort = FilterCombo(s_wikiSorts, 170);
    private readonly CheckBox _wikiBossesOnly = new() { Content = "Bosses only" };
    private readonly CheckBox _wikiCheatsOnly = new() { Content = "Has cheats" };
    private readonly CheckBox _wikiDropsOnly = new() { Content = "Drops something" };
    private readonly TextBlock _wikiCount = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _wikiList = new() { Background = Brushes.Transparent, Classes = { "compact" } };
    private readonly StackPanel _wikiPage = new() { Spacing = 6 };
    private readonly ScrollViewer _wikiPageScroll = new();
    private readonly List<(string Kind, string Id)> _wikiHistory = [];
    private (string Kind, string Id)? _wikiShown;
    private WikiData? _wiki;
    private bool _wikiLoading;

    /// <summary>One row of the list: a creature, spell or item.</summary>
    private sealed record WikiHit(string Kind, string Id, string Name, string Line, string School, string World, int Rank, int Health,
                                  bool Boss, bool Cheats, bool Drops, string Haystack);

    /// <summary>Everything the pages show, read together off the window's thread.</summary>
    private sealed class WikiData {
        public BossCatalog Catalog { get; init; } = null!;
        public Lookup? Lookup { get; init; }
        public Dictionary<string, BtPlaceEntry> Places { get; init; } = [];
        public string PlacesSource { get; init; } = "";
        public Dictionary<string, BtBoss> Cheats { get; init; } = [];
        public Dictionary<string, BossInfo> Creatures { get; init; } = [];
        public Dictionary<uint, List<BossInfo>> CastBy { get; init; } = [];
        public Dictionary<uint, List<uint>> DropsOf { get; init; } = [];
        public Dictionary<uint, List<BgNpc>> DroppedBy { get; init; } = [];
        public Dictionary<uint, List<BgQuest>> QuestsGiving { get; init; } = [];
        public bool HasSources { get; init; }
        public Dictionary<uint, List<VendorNpc>> SoldBy { get; init; } = [];
        public Dictionary<uint, List<(VendorNpc Npc, int Level)>> TaughtBy { get; init; } = [];
        public Dictionary<string, List<VendorNpc>> CardSoldBy { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public bool HasVendors { get; init; }
        public Dictionary<string, string> ItemPaths { get; init; } = [];
        public List<WikiHit> Hits { get; init; } = [];
    }

    private Control WikiTab() {
        _wikiKind.SelectedIndex = 0;
        _wikiList.ItemTemplate = new FuncDataTemplate<WikiHit>((hit, _) => hit is null ? new Panel() : WikiRow(hit));
        _wikiList.SelectionChanged += (_, e) => {
            if (e.Source == _wikiList && _wikiList.SelectedItem is WikiHit hit && _wikiShown != (hit.Kind, hit.Id)) {
                WikiOpen(hit.Kind, hit.Id, fromList: true);
            }
        };
        WhenFiltersChange(RenderWikiList, _wikiSearch, _wikiKind, _wikiSchool, _wikiWorld, _wikiRankMin, _wikiRankMax, _wikiSort,
            _wikiBossesOnly, _wikiCheatsOnly, _wikiDropsOnly);

        _wikiPageScroll.Content = Card(_wikiPage);
        var details = new Border { MinWidth = 440, Margin = new Thickness(14, 0, 0, 0), Child = _wikiPageScroll };
        WikiPageEmpty();

        var left = new DockPanel {
            Children = {
                WithDock(Fold("book", WIKI, "Look it up",
                    "Every creature, spell and item, like a wiki: click one to read its page. Names on a page open their own page.",
                    WithHint(_wikiSearch, "Type a name or an ID; a name typed partly or with a slip finds it too. Leave it empty to list " +
                        "them all, then narrow it with the filters. A creature's page shows its school, rank, health, stats, where it is, " +
                        "who comes with it, the spells it casts, its cheats and what it drops. A spell's page shows who casts it and who " +
                        "teaches or sells it. An item's page shows who drops it, which quests give it and who sells it. " +
                        "This tab only reads: the buttons on a page open the tab that changes it."),
                    WithCount(SearchFilters(
                        SearchFilter("Look up", _wikiKind),
                        SearchFilter("School", _wikiSchool),
                        SearchFilter("World", _wikiWorld),
                        SearchFilter("Rank from", _wikiRankMin),
                        SearchFilter("Rank to", _wikiRankMax),
                        SearchFilter("Sort by", _wikiSort),
                        SearchTick(_wikiBossesOnly),
                        SearchTick(_wikiCheatsOnly),
                        SearchTick(_wikiDropsOnly)),
                        _wikiCount)), Dock.Top),
                WithMargin(_wikiList, new Thickness(0, 14, 0, 0)),
            },
        };
        Grid.SetColumn(details, 1);

        return new Grid {
            Margin = new Thickness(24, 12, 24, 16),
            ColumnDefinitions = new ColumnDefinitions("2*,3*"),
            Children = { left, details },
        };
    }

    /// <summary>Reads the lists again when the server wrote them since. Runs each time the tab shows.</summary>
    private async Task LoadWikiAsync() {
        if (_wikiLoading) {
            return;
        }

        _wikiLoading = true;
        try {
            await LoadLookupAsync();
            if (_bossCatalog is null || BossCatalogChanged()) {
                await LoadBossCatalogAsync();
            }

            if (_wiki is not null && _wiki.Catalog == _bossCatalog && _wiki.Lookup == _lookup) {
                RenderWikiList();

                return;
            }

            if (_wiki is null) {
                _wikiCount.Text = "Loading…";
            }

            var (catalog, lookup) = (_bossCatalog!, _lookup);
            var (extra, changes, customs, idLookup) = (CheatExtraFolder, _manager.BossChangesFolder, _manager.CustomsFolder, _manager.IdLookupFolder);
            var vendorsPath = VendorsPath;
            _wiki = await Task.Run(() => ReadWiki(catalog, lookup, extra, changes, customs, idLookup, vendorsPath));
            FillFilterCombo(_wikiSchool, _wiki.Hits.Select(h => h.School).Where(s => s.Length > 0).OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
            FillFilterCombo(_wikiWorld, BtWorldsInOrder(_wiki.Hits.Select(h => h.World).Where(w => w.Length > 0)).Where(w => w != BT_NO_PLACE));
            RenderWikiList();
            if (_wikiShown is { } shown) {
                WikiOpen(shown.Kind, shown.Id, remember: false);
            }
        }
        finally {
            _wikiLoading = false;
        }
    }

    private static BtPlacesFile? ReadCreaturePlaces() {
        try {
            var assembly = typeof(ManagerWindow).Assembly.GetName().Name;
            using var stream = AssetLoader.Open(new Uri($"avares://{assembly}/Assets/{WIKI_PLACES}"));
            using var unzip = new GZipStream(stream, CompressionMode.Decompress);

            return JsonSerializer.Deserialize<BtPlacesFile>(unzip, s_qeJson);
        }
        catch (Exception) {
            // A build without the file: the pages say a place isn't written down.
            return null;
        }
    }

    private static WikiData ReadWiki(BossCatalog catalog, Lookup? lookup, string extra, string changes, string customs, string idLookup,
                                     string vendorsPath) {
        var placesFile = ReadCreaturePlaces();
        var places = placesFile?.Bosses ?? [];

        // Cheats: the game's files with the app's changes, as the Boss Test tab reads them (customs are on the Customs tab).
        var cheats = new Dictionary<string, BtBoss>(StringComparer.OrdinalIgnoreCase);
        try {
            foreach (var boss in ReadBossTest(extra, changes, customs, idLookup, placesFile).Where(b => !b.IsCustom)) {
                cheats.TryAdd(boss.Id, boss);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // No cheats shown; the rest of the pages still are.
        }

        var creatures = new Dictionary<string, BossInfo>();
        var castBy = new Dictionary<uint, List<BossInfo>>();
        foreach (var creature in catalog.Creatures) {
            creatures.TryAdd(creature.Id, creature);
            foreach (var spell in creature.Spells.Distinct()) {
                if (!castBy.TryGetValue(spell, out var list)) {
                    castBy[spell] = list = [];
                }

                list.Add(creature);
            }
        }

        // Drops and quest rewards (item-sources.json, written by the server for the Best Gear tab).
        var dropsOf = new Dictionary<uint, List<uint>>();
        var droppedBy = new Dictionary<uint, List<BgNpc>>();
        var questsGiving = new Dictionary<uint, List<BgQuest>>();
        var hasSources = false;
        try {
            var sourcesPath = Path.Combine(idLookup, BG_SOURCES_FILE);
            if (File.Exists(sourcesPath) && JsonSerializer.Deserialize<BgSourcesFile>(File.ReadAllText(sourcesPath)) is { } sources) {
                hasSources = true;
                var npcs = sources.Npcs ?? [];
                var quests = sources.Quests ?? [];
                foreach (var (item, of) in sources.Items ?? []) {
                    foreach (var npc in (of.Drops ?? []).Where(i => i >= 0 && i < npcs.Count).Select(i => npcs[i])) {
                        (droppedBy.TryGetValue(item, out var by) ? by : droppedBy[item] = []).Add(npc);
                        (dropsOf.TryGetValue(npc.Id, out var items) ? items : dropsOf[npc.Id] = []).Add(item);
                    }

                    foreach (var quest in (of.Quests ?? []).Where(i => i >= 0 && i < quests.Count).Select(i => quests[i])) {
                        (questsGiving.TryGetValue(item, out var q) ? q : questsGiving[item] = []).Add(quest);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            // Being written: read when the tab shows again.
        }

        // Shops and trainers (vendors.json, the Vendors tab's list).
        var soldBy = new Dictionary<uint, List<VendorNpc>>();
        var taughtBy = new Dictionary<uint, List<(VendorNpc, int)>>();
        var cardSoldBy = new Dictionary<string, List<VendorNpc>>(StringComparer.OrdinalIgnoreCase);
        var hasVendors = false;
        try {
            if (File.Exists(vendorsPath)) {
                hasVendors = true;
                foreach (var npc in ReadVendors(vendorsPath, lookup).Select(r => r.Npc)) {
                    foreach (var item in npc.Shops.SelectMany(s => s.Items ?? []).Distinct()) {
                        (soldBy.TryGetValue(item, out var by) ? by : soldBy[item] = []).Add(npc);
                    }

                    foreach (var spell in npc.Spells) {
                        (taughtBy.TryGetValue(spell.Id, out var by) ? by : taughtBy[spell.Id] = []).Add((npc, spell.Level));
                    }

                    foreach (var card in npc.TreasureCards.Distinct(StringComparer.OrdinalIgnoreCase)) {
                        (cardSoldBy.TryGetValue(card, out var by) ? by : cardSoldBy[card] = []).Add(npc);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            hasVendors = false;
        }

        // Each item's game file, to know which tab shows it.
        var itemPaths = new Dictionary<string, string>();
        try {
            var ids = Path.Combine(idLookup, "ids.csv");
            if (File.Exists(ids)) {
                foreach (var fields in Lookup.ReadCsv(ids)) {
                    if (fields.Count >= 6 && fields[1] == "Item") {
                        itemPaths.TryAdd(fields[0], fields[5]);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // The page offers every tab instead.
        }

        var hits = new List<WikiHit>();
        foreach (var c in catalog.Creatures) {
            var entry = places.GetValueOrDefault(c.Id);
            var world = entry?.Places.FirstOrDefault()?.World ?? "";
            var hasCheats = cheats.ContainsKey(c.Id);
            var boss = c.IsBoss || entry?.Title == "Boss";
            var drops = uint.TryParse(c.Id, out var cid) && dropsOf.ContainsKey(cid);
            var name = c.Name.Length > 0 ? c.Name : $"Creature {c.Id}";
            var line = string.Join(" · ", new[] {
                boss ? "Boss" : entry?.Title is { Length: > 0 } title ? title : "Creature",
                c.Level > 0 ? $"Rank {c.Level}" : "", c.School, c.Health > 0 ? $"{c.Health:N0} health" : "",
                entry?.Places.FirstOrDefault() is { } p ? (p.Name.Length > 0 && p.Name != p.World ? $"{p.Name}, {p.World}" : p.World) : "",
            }.Where(s => s.Length > 0));
            hits.Add(new WikiHit(WIKI_CREATURE, c.Id, name, line, c.School, world, c.Level, c.Health, boss, hasCheats, drops,
                SearchText.Normalize($"{name} {c.Id} {c.School} {world} {string.Join(' ', (entry?.Places ?? []).Select(pl => pl.Name))}")));
        }

        foreach (var s in catalog.Spells.Values) {
            var line = string.Join(" · ", new[] {
                "Spell", s.School,
                castBy.TryGetValue(s.Id, out var by) ? (by.Count == 1 ? "1 creature casts it" : $"{by.Count:N0} creatures cast it") : "",
            }.Where(t => t.Length > 0));
            hits.Add(new WikiHit(WIKI_SPELL, s.Id.ToString(CultureInfo.InvariantCulture), s.Name, line, s.School, "", 0, 0, false, false, false,
                SearchText.Normalize($"{s.Name} {s.Id} {s.School}")));
        }

        foreach (var item in lookup?.Entries.Where(e => e.Kind == "Item") ?? []) {
            var dropped = uint.TryParse(item.Id, out var iid) && droppedBy.ContainsKey(iid);
            var line = string.Join(" · ", new[] {
                WikiItemTab(itemPaths.GetValueOrDefault(item.Id) ?? "") switch { "Gear" => "Gear", "Furniture" => "House item", _ => "Item" },
                item.School, dropped ? "dropped by creatures" : "",
            }.Where(t => t.Length > 0));
            hits.Add(new WikiHit(WIKI_ITEM, item.Id, item.Name, line, item.School, "", 0, 0, false, false, dropped,
                SearchText.Normalize($"{item.Name} {item.Id} {item.School}")));
        }

        return new WikiData {
            Catalog = catalog, Lookup = lookup, Places = places, PlacesSource = placesFile?.Source ?? "", Cheats = cheats,
            Creatures = creatures, CastBy = castBy, DropsOf = dropsOf, DroppedBy = droppedBy, QuestsGiving = questsGiving, HasSources = hasSources,
            SoldBy = soldBy, TaughtBy = taughtBy, CardSoldBy = cardSoldBy, HasVendors = hasVendors, ItemPaths = itemPaths, Hits = hits,
        };
    }

    /// <summary>The tab that shows an item, from its game file's folder.</summary>
    private static string WikiItemTab(string path) {
        if (path.Contains("/Housing/", StringComparison.OrdinalIgnoreCase)) {
            return "Furniture";
        }

        string[] other = ["/Reagent", "Bundle", "/Reagents/", "/Jewels/", "/PetSnacks/", "/Elixir", "/Potion", "/Polymorph", "/Henchm", "/Emote", "/BoosterPack", "/Packs/"];

        return path.Length == 0 ? "" : other.Any(o => path.Contains(o, StringComparison.OrdinalIgnoreCase)) ? "Items" : "Gear";
    }

    private void RenderWikiList() {
        if (_wiki is null) {
            return;
        }

        var kind = _wikiKind.SelectedIndex switch { 0 => WIKI_CREATURE, 1 => WIKI_SPELL, 2 => WIKI_ITEM, _ => null };
        var (school, world) = (FilterPick(_wikiSchool), FilterPick(_wikiWorld));
        var creatureFilters = world is not null || _wikiRankMin.Value is not null || _wikiRankMax.Value is not null
                              || _wikiBossesOnly.IsChecked == true || _wikiCheatsOnly.IsChecked == true;
        var query = _wikiSearch.Text ?? "";
        var words = SearchText.Words(query);
        var exact = query.Trim();

        bool Fits(WikiHit h) => (kind is null || h.Kind == kind)
                                && (!creatureFilters || h.Kind == WIKI_CREATURE)
                                && (school is null || h.School.Equals(school, StringComparison.OrdinalIgnoreCase))
                                && (world is null || h.World == world)
                                && (h.Kind != WIKI_CREATURE || InFilterRange(h.Rank, _wikiRankMin, _wikiRankMax))
                                && (_wikiBossesOnly.IsChecked != true || h.Boss)
                                && (_wikiCheatsOnly.IsChecked != true || h.Cheats)
                                && (_wikiDropsOnly.IsChecked != true || h.Drops);

        var pool = _wiki.Hits.Where(Fits);
        var found = words.Length == 0 ? pool.ToList() : pool.Where(h => words.All(h.Haystack.Contains) || h.Id == exact).ToList();
        if (found.Count == 0 && words.Length > 0) {
            found = [.. pool.Where(h => CheatSearch.NameScore(h.Name, query) > 0)];
        }

        IOrderedEnumerable<WikiHit> sorted = _wikiSort.SelectedIndex switch {
            1 => found.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase),
            2 => found.OrderByDescending(h => h.Rank),
            3 => found.OrderBy(h => h.Kind != WIKI_CREATURE).ThenBy(h => h.Rank),
            4 => found.OrderByDescending(h => h.Health),
            _ => found.OrderBy(h => !(h.Name.Equals(exact, StringComparison.OrdinalIgnoreCase) || h.Id == exact))
                .ThenByDescending(h => words.Length == 0 ? 0 : CheatSearch.NameScore(h.Name, query))
                .ThenBy(h => h.Kind switch { WIKI_CREATURE => 0, WIKI_SPELL => 1, _ => 2 })
                .ThenBy(h => !h.Cheats).ThenBy(h => !h.Boss),
        };
        var shown = sorted.ThenBy(h => h.Name, StringComparer.OrdinalIgnoreCase).ThenBy(h => h.Rank).Take(WIKI_SHOWN).ToList();
        _wikiList.ItemsSource = shown;
        _wikiCount.Text = _wiki.Hits.Count == 0
            ? (_wiki.Catalog.Missing ?? _wiki.Lookup?.Missing ?? "Nothing to look up yet. Start the server once: it writes its lists a few minutes after it starts.")
            : found.Count > shown.Count ? $"{shown.Count:N0} of {found.Count:N0} shown" : $"{found.Count:N0} found";
    }

    private Control WikiRow(WikiHit hit) {
        var icon = hit.Kind switch { WIKI_CREATURE => hit.Boss ? "crown" : "skull", WIKI_SPELL => "cards", _ => "bag" };
        var accent = hit.Kind switch { WIKI_CREATURE => "Bosses", WIKI_SPELL => "Spells", _ => "Items" };
        var tags = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (hit.Cheats) {
            tags.Children.Add(QtTag("cheats", ManagerTheme.Accent("Bosses")));
        }

        return new DockPanel {
            Children = {
                WithDock(tags, Dock.Right),
                WithDock(new Border { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Child = ManagerTheme.Tile(icon, ManagerTheme.Accent(accent), 26) }, Dock.Left),
                new StackPanel {
                    VerticalAlignment = VerticalAlignment.Center, Spacing = 1,
                    Children = {
                        new TextBlock { Text = hit.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = hit.Line, FontSize = ManagerTheme.FONT_SMALL, Foreground = MUTED, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
            },
        };
    }

    private void WikiPageEmpty() {
        _wikiPage.Children.Clear();
        _wikiPage.Children.Add(new TextBlock { Text = "Pick something to read about", FontSize = 18, FontWeight = FontWeight.SemiBold });
        _wikiPage.Children.Add(Sub("Click a creature, spell or item on the left to see its page. On a page, click a name to open that page, " +
                                   "and Back to go to the one before."));
    }

    /// <summary>Shows the page of a creature, spell or item. Remember: Back comes back to the page shown before.</summary>
    private void WikiOpen(string kind, string id, bool remember = true, bool fromList = false) {
        if (_wiki is null) {
            return;
        }

        if (remember && _wikiShown is { } before && before != (kind, id)) {
            _wikiHistory.Add(before);
            if (_wikiHistory.Count > 50) {
                _wikiHistory.RemoveAt(0);
            }
        }

        _wikiShown = (kind, id);
        if (!fromList) {
            _wikiList.SelectedItem = null;
        }

        _wikiPage.Children.Clear();
        if (_wikiHistory.Count > 0) {
            _wikiPage.Children.Add(new WrapPanel {
                Children = {
                    Spaced(Action(Plain("Back"), () => {
                        var back = _wikiHistory[^1];
                        _wikiHistory.RemoveAt(_wikiHistory.Count - 1);
                        WikiOpen(back.Kind, back.Id, remember: false);

                        return Task.CompletedTask;
                    })),
                },
            });
        }

        switch (kind) {
            case WIKI_CREATURE: WikiCreaturePage(_wiki, id); break;
            case WIKI_SPELL: WikiSpellPage(_wiki, id); break;
            default: WikiItemPage(_wiki, id); break;
        }

        _wikiPageScroll.Offset = default;
    }

    /// <summary>Opens the Wiki tab on a page (Home's search results).</summary>
    private void OpenInWiki(string kind, string id) {
        SelectTab(WIKI);
        if (_wiki is not null) {
            WikiOpen(kind, id);
        }
        else {
            _wikiShown = (kind, id);
        }
    }

    // ---------------------------------------------------------------------
    // Page parts

    private void WikiTitle(string name, string line) {
        _wikiPage.Children.Add(new SelectableTextBlock { Text = name, FontSize = 22, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (line.Length > 0) {
            _wikiPage.Children.Add(new SelectableTextBlock { Text = line, Foreground = MUTED, TextWrapping = TextWrapping.Wrap });
        }
    }

    private void WikiHeading(string text) => _wikiPage.Children.Add(new TextBlock {
        Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.Bold, Foreground = ManagerTheme.Accent(WIKI),
        Margin = new Thickness(0, 12, 0, 0),
    });

    private void WikiText(string text, bool muted = false) {
        var block = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        if (muted) {
            block.Foreground = MUTED;
        }

        _wikiPage.Children.Add(block);
    }

    private void WikiButtons(params Control[] buttons) {
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var button in buttons) {
            row.Children.Add(Spaced(button));
        }

        _wikiPage.Children.Add(row);
    }

    private Button WikiCommandButton(string text, string command, string tip, bool primary = false) {
        var button = primary ? Primary(text) : Plain(text);
        ToolTip.SetTip(button, tip);

        return Action(button, () => {
            _manager.SendCommand(command);
            Say($"Sent {command}. The game's chat says if it worked.");

            return Task.CompletedTask;
        });
    }

    private Button WikiTabButton(string text, string tab, TextBox? search, string query, string tip) {
        var button = Plain(text);
        ToolTip.SetTip(button, tip);

        return Action(button, () => {
            SelectTab(tab);
            if (search is not null) {
                search.Text = query;
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>A name that opens its own page, with a line after it.</summary>
    private Control WikiLink(string kind, string id, string name, string after = "") {
        var link = new Button {
            Content = new TextBlock { Text = name, Foreground = ManagerTheme.Accent(WIKI), TextDecorations = TextDecorations.Underline, TextWrapping = TextWrapping.Wrap },
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(link, $"Open the page of {name}.");
        link.Click += (_, _) => WikiOpen(kind, id);
        var row = new WrapPanel { Margin = new Thickness(0, 1, 0, 1) };
        row.Children.Add(link);
        if (after.Length > 0) {
            row.Children.Add(new TextBlock { Text = "  " + after, FontSize = 13, Foreground = MUTED, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        }

        return row;
    }

    /// <summary>Up to WIKI_LINKS rows, then how many more there are.</summary>
    private void WikiRows(IReadOnlyCollection<Control> rows) {
        foreach (var row in rows.Take(WIKI_LINKS)) {
            _wikiPage.Children.Add(row);
        }

        if (rows.Count > WIKI_LINKS) {
            // The rest on a click, so a long list doesn't slow the page down.
            var more = Plain($"Show all {rows.Count:N0}");
            var holder = new StackPanel();
            holder.Children.Add(WithMargin(more, new Thickness(0, 4, 0, 0)));
            more.Click += (_, _) => {
                holder.Children.Clear();
                foreach (var row in rows.Skip(WIKI_LINKS)) {
                    holder.Children.Add(row);
                }
            };
            _wikiPage.Children.Add(holder);
        }
    }

    private Control WikiCreatureLink(WikiData wiki, string id, string? fallbackName = null) {
        var name = wiki.Creatures.GetValueOrDefault(id)?.Name is { Length: > 0 } n ? n
            : wiki.Places.GetValueOrDefault(id)?.Name is { Length: > 0 } p ? p : fallbackName ?? $"Creature {id}";
        var c = wiki.Creatures.GetValueOrDefault(id);
        var entry = wiki.Places.GetValueOrDefault(id);
        var where = entry?.Places.FirstOrDefault() is { } place ? (place.Name.Length > 0 && place.Name != place.World ? $"{place.Name}, {place.World}" : place.World) : "";
        var after = string.Join(" · ", new[] { c is { Level: > 0 } ? $"rank {c.Level}" : "", c?.School ?? "", where }.Where(s => s.Length > 0));

        return c is null ? new SelectableTextBlock { Text = $"{name} ({id})", FontSize = 13, TextWrapping = TextWrapping.Wrap } : WikiLink(WIKI_CREATURE, id, name, after);
    }

    private static string WikiPlace(BtPlace place) => place.Name.Length > 0 && place.Name != place.World ? $"{place.Name}  ·  {place.World}" : place.World;

    // ---------------------------------------------------------------------
    // Pages

    private void WikiCreaturePage(WikiData wiki, string id) {
        if (!wiki.Creatures.TryGetValue(id, out var c)) {
            WikiTitle($"Creature {id}", "This creature isn't on the server's list.");

            return;
        }

        var entry = wiki.Places.GetValueOrDefault(id);
        wiki.Cheats.TryGetValue(id, out var cheats);
        var boss = c.IsBoss || entry?.Title == "Boss";
        var kind = boss ? "Boss" : entry?.Title is { Length: > 0 } title ? title : "Creature";
        WikiTitle(c.Name.Length > 0 ? c.Name : $"Creature {id}", string.Join("  ·  ", new[] {
            kind, c.Level > 0 ? $"Rank {c.Level}" : "", c.School, c.Health > 0 ? $"{c.Health:N0} health" : "", $"ID {id}",
        }.Where(s => s.Length > 0)));
        if (cheats?.About is { Length: > 0 } about) {
            WikiText(about, muted: true);
        }

        var buttons = new List<Control> {
            WikiCommandButton("Bring into my battle", $".joinme {id}",
                $"While you are in a fight, it joins the enemy side (.joinme {id}). Start any fight first.", primary: true),
            WikiTabButton("Change it on Bosses", "Bosses", _bossSearch, id, "Opens the Bosses tab on this creature, to change its health, stats, spells and cheats."),
        };
        if (cheats is not null) {
            buttons.Add(WikiTabButton("Test its cheats", "Boss Test", _btSearch, id, "Opens the Boss Test tab on this boss."));
        }

        buttons.Add(Action(Plain("Copy ID"), () => CopyAsync(id)));
        WikiButtons([.. buttons]);

        // Stats.
        WikiHeading("Stats");
        // The server's words for them ("Damage: Ice 50%, Death 50%; Resist: ..."), a line each; the grid's numbers when
        // the list is from before the words.
        var stats = c.Stats.Split("; ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var (key, label, _) in BossChanges.GRID_ROWS) {
            if (c.Stats.Length > 0 || !c.Grid.TryGetValue(key, out var values)) {
                continue;
            }

            var parts = BossChanges.SCHOOLS.Zip(values).Where(p => Math.Abs(p.Second) > 0.001f)
                .Select(p => $"{p.First} {p.Second.ToString("0.##", CultureInfo.InvariantCulture)}").ToList();
            if (parts.Count == BossChanges.SCHOOLS.Length && values.Distinct().Count() == 1) {
                parts = [$"every school {values[0].ToString("0.##", CultureInfo.InvariantCulture)}"];
            }

            if (parts.Count > 0) {
                stats.Add($"{label}: {string.Join(", ", parts)}");
            }
        }

        WikiText(stats.Count > 0 ? string.Join('\n', stats) : "No stats beyond its health and rank.", muted: stats.Count == 0);

        // Where it is.
        WikiHeading("Where to find it");
        var places = entry?.Places ?? [];
        if (places.Count == 0) {
            WikiText($"Not written down. Bring it into any fight with .joinme {id} instead.", muted: true);
        }

        foreach (var place in places) {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            if (place.Zone.Length > 0) {
                var go = Plain("Go there");
                ToolTip.SetTip(go, $"Takes you to {place.Name} (.tp {place.Zone}).");
                row.Children.Add(WithDock(Action(go, () => {
                    _manager.SendCommand($".tp {place.Zone}");
                    Say($"Teleporting to {place.Name}. The game's chat says if it worked.");

                    return Task.CompletedTask;
                }), Dock.Right));
            }

            row.Children.Add(WithCenter(new SelectableTextBlock { Text = WikiPlace(place), TextWrapping = TextWrapping.Wrap, FontSize = 13 }));
            _wikiPage.Children.Add(row);
        }

        if (places.Count > 0 && wiki.PlacesSource.Length > 0) {
            _wikiPage.Children.Add(Sub($"Places from {wiki.PlacesSource}."));
        }

        if (entry?.MinionOf is { Count: > 0 } ups) {
            WikiHeading("Comes with");
            WikiRows([.. ups.Select(u => WikiCreatureLink(wiki, u.ToString(CultureInfo.InvariantCulture)))]);
        }

        if (entry?.Minions is { Count: > 0 } minions) {
            WikiHeading("Brings along");
            WikiRows([.. minions.Select(m => WikiCreatureLink(wiki, m.ToString(CultureInfo.InvariantCulture)))]);
        }

        // Spells.
        WikiHeading("Spells it casts");
        if (c.Spells.Count == 0) {
            WikiText("Its deck isn't in the game files (from Azteca on, many creatures' decks live only on KingsIsle's servers). " +
                     "With Stand-in decks on (Game Rules > Battles and World), it fights with the spells of its school and rank.", muted: true);
        }
        else {
            WikiRows([.. c.Spells.GroupBy(s => s).OrderByDescending(g => g.Count()).ThenBy(g => wiki.Catalog.Spells.GetValueOrDefault(g.Key)?.Name)
                .Select(g => {
                    var spell = wiki.Catalog.Spells.GetValueOrDefault(g.Key);
                    var after = string.Join(" · ", new[] { spell?.School ?? "", g.Count() > 1 ? $"{g.Count()} in its deck" : "" }.Where(s => s.Length > 0));

                    return WikiLink(WIKI_SPELL, g.Key.ToString(CultureInfo.InvariantCulture), spell?.Name ?? $"Spell {g.Key}", after);
                })]);
        }

        // Cheats.
        WikiHeading(cheats is null ? "Cheats" : $"Cheats ({cheats.Cheats.Count})");
        if (cheats is null) {
            WikiText("No cheats. Give it some on the Bosses tab.", muted: true);
        }
        else {
            var number = 0;
            foreach (var cheat in cheats.Cheats) {
                number++;
                var block = new StackPanel { Spacing = 2, Margin = new Thickness(0, 2, 0, 4) };
                var line = new SelectableTextBlock {
                    Text = $"{number}. {cheat.Summary}" + (cheat.Off ? "  (turned off)" : ""), TextWrapping = TextWrapping.Wrap, FontSize = 13,
                };
                if (cheat.Off) {
                    line.Foreground = MUTED;
                }

                block.Children.Add(line);
                if (cheat.Notes.Length > 0) {
                    block.Children.Add(WithMargin(new SelectableTextBlock { Text = cheat.Notes, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = MUTED },
                        new Thickness(18, 0, 0, 0)));
                }

                _wikiPage.Children.Add(block);
            }

            if (cheats.Sources.Count > 0) {
                _wikiPage.Children.Add(Sub(string.Join(" · ", cheats.Sources)));
            }
        }

        // Drops.
        WikiHeading("Drops");
        if (uint.TryParse(id, out var cid) && wiki.DropsOf.TryGetValue(cid, out var drops) && drops.Count > 0) {
            WikiRows([.. drops.Distinct().Select(item => (item, entry: wiki.Lookup?.ById("Item", item.ToString(CultureInfo.InvariantCulture))))
                .OrderBy(d => d.entry?.Name ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(d => WikiLink(WIKI_ITEM, d.item.ToString(CultureInfo.InvariantCulture), d.entry?.Name ?? $"Item {d.item}", d.entry?.School ?? ""))]);
            _wikiPage.Children.Add(Sub("What it can drop. The chance of each isn't known; the Drop Tables on the Shops and Codes tab change them."));
        }
        else {
            WikiText(wiki.HasSources ? "Nothing written down." : "Start the server once to see what it drops (it writes the list a minute after it starts).",
                muted: true);
        }
    }

    private void WikiSpellPage(WikiData wiki, string id) {
        uint.TryParse(id, out var sid);
        var spell = wiki.Catalog.Spells.GetValueOrDefault(sid);
        var name = spell?.Name ?? $"Spell {id}";
        WikiTitle(name, string.Join("  ·  ", new[] { "Spell", spell?.School ?? "", $"ID {id}" }.Where(s => s.Length > 0)));
        WikiButtons(
            WikiTabButton("See the card", "Spells", _spellsSearch, id, "Opens the Spells tab on this spell: its card, what it does and its pips."),
            WikiCommandButton("Learn it", $".sb learn {id}", $"Adds it to your wizard's spellbook (.sb learn {id})."),
            Action(Plain("Copy ID"), () => CopyAsync(id)));

        WikiHeading("Creatures that cast it");
        if (wiki.CastBy.TryGetValue(sid, out var by) && by.Count > 0) {
            WikiRows([.. by.OrderBy(c => !c.IsBoss).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Level).Select(c => WikiCreatureLink(wiki, c.Id))]);
        }
        else {
            WikiText("No creature's deck has it.", muted: true);
        }

        WikiHeading("Who teaches it");
        if (wiki.TaughtBy.TryGetValue(sid, out var teachers) && teachers.Count > 0) {
            WikiRows([.. teachers.DistinctBy(t => t.Npc.Id).Select(t => (Control) new SelectableTextBlock {
                Text = string.Join("  ·  ", new[] { t.Npc.Name, t.Level > 0 ? $"level {t.Level}" : "", t.Npc.Place }.Where(s => s.Length > 0)),
                FontSize = 13, TextWrapping = TextWrapping.Wrap,
            })]);
        }
        else {
            WikiText(wiki.HasVendors ? "No trainer teaches it." : "Start the server once to see trainers and shops.", muted: true);
        }

        if (wiki.CardSoldBy.TryGetValue(name, out var cards) && cards.Count > 0) {
            WikiHeading("Sells it as a treasure card");
            WikiRows([.. cards.Select(v => (Control) new SelectableTextBlock {
                Text = string.Join("  ·  ", new[] { v.Name, v.Place }.Where(s => s.Length > 0)), FontSize = 13, TextWrapping = TextWrapping.Wrap,
            })]);
        }
    }

    private void WikiItemPage(WikiData wiki, string id) {
        uint.TryParse(id, out var iid);
        var item = wiki.Lookup?.ById("Item", id);
        var name = item?.Name ?? $"Item {id}";
        var tab = WikiItemTab(wiki.ItemPaths.GetValueOrDefault(id) ?? "");
        WikiTitle(name, string.Join("  ·  ", new[] { tab switch { "Gear" => "Gear", "Furniture" => "House item", _ => "Item" }, item?.School ?? "", $"ID {id}" }
            .Where(s => s.Length > 0)));

        var buttons = new List<Control>();
        if (tab == "Furniture") {
            buttons.Add(WikiTabButton("See it on Furniture", "Furniture", _furnitureSearch, id, "Opens the Furniture tab on this house item."));
        }
        else if (tab == "Items") {
            buttons.Add(WikiTabButton("See it on Items", "Items", _itemsSearch, name, "Opens the Items tab on this item."));
        }
        else {
            buttons.Add(WikiTabButton("See it on Gear", "Gear", _gearSearch, id, "Opens the Gear tab on this item: its stats and picture."));
            if (tab.Length == 0) {
                buttons.Add(WikiTabButton("See it on Items", "Items", _itemsSearch, name, "Opens the Items tab on this item."));
            }
        }

        buttons.Add(WikiCommandButton("Give it to me", $".mod additem {id}",
            $"Puts it in your wizard's backpack (.mod additem {id}). Reagents and snacks are given on the Items tab instead."));
        buttons.Add(Action(Plain("Copy ID"), () => CopyAsync(id)));
        WikiButtons([.. buttons]);

        WikiHeading("Dropped by");
        if (wiki.DroppedBy.TryGetValue(iid, out var by) && by.Count > 0) {
            WikiRows([.. by.DistinctBy(n => n.Id).OrderBy(n => !n.Boss).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .Select(n => WikiCreatureLink(wiki, n.Id.ToString(CultureInfo.InvariantCulture), n.Name))]);
        }
        else {
            WikiText(wiki.HasSources ? "No creature drops it." : "Start the server once to see who drops it.", muted: true);
        }

        if (wiki.QuestsGiving.TryGetValue(iid, out var quests) && quests.Count > 0) {
            WikiHeading("Quest reward of");
            WikiRows([.. quests.DistinctBy(q => q.Code).Select(q => (Control) new SelectableTextBlock {
                Text = string.Join("  ·  ", new[] { q.Title, q.Zone }.Where(s => s.Length > 0)), FontSize = 13, TextWrapping = TextWrapping.Wrap,
            })]);
        }

        WikiHeading("Sold by");
        if (wiki.SoldBy.TryGetValue(iid, out var vendors) && vendors.Count > 0) {
            WikiRows([.. vendors.DistinctBy(v => v.Id).Select(v => (Control) new SelectableTextBlock {
                Text = string.Join("  ·  ", new[] { v.Name, v.Place }.Where(s => s.Length > 0)), FontSize = 13, TextWrapping = TextWrapping.Wrap,
            })]);
        }
        else {
            WikiText(wiki.HasVendors ? "No shop sells it." : "Start the server once to see trainers and shops.", muted: true);
        }
    }

}
