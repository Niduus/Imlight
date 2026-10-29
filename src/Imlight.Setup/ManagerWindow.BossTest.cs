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
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

namespace Imlight.Setup;

/// <summary>
/// The Boss Test tab on the Create page (Niduus, 2026-10-28: "What bosses do I have to test for cheats?" then "Make BOSS
/// TEST in the exe"). Every creature with cheats, by world: what each cheat should do, where the creature is fought,
/// buttons to go there or bring it into the battle you are in (.joinme), and a tick once its cheats worked. Like the Quest
/// Testing tab, but read from the cheat files themselves each time the tab opens, so the cheats made in the app (the
/// Bosses tab's changes and the customs) are in it too and stay on this PC. Where each creature is fought is built into
/// the app (Assets/boss-places.json.gz, made by tools/boss-test/build_boss_places.py). The ticks are kept on this PC
/// (install folder, boss-test-done.json) with what the cheats were when ticked, so a boss whose cheats changed since
/// shows up again.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string BOSS_TEST_PLACES = "boss-places.json.gz";
    private const string BOSS_TEST_DONE = "boss-test-done.json";
    private const string BT_NO_PLACE = "Place not known";

    // Where each source of cheats comes from, as the filter and the tags say it.
    private const string BT_WIZARD404 = "From Wizard404's notes";
    private const string BT_WIKI = "From a wiki page";
    private const string BT_GAME = "From the game's files";
    private const string BT_CHANGED = "Changed in Ahziael";
    private const string BT_YOURS = "Your own cheats";
    private const string BT_CUSTOM = "Your custom";
    private const string BT_RETEST = "Changed since you tested";

    // The worlds in story order; the rest after them by name.
    private static readonly string[] s_btWorldOrder = [
        "Wizard City", "Krokotopia", "Marleybone", "MooShu", "Dragonspyre", "Celestia", "Wysteria", "Grizzleheim", "Zafaria",
        "Avalon", "Azteca", "Aquila", "Khrysalis", "Polaris", "Mirage", "Empyrea", "Karamelle", "Lemuria", "Novus", "Wallaru",
        "Selenopolis", "Darkmoor", "Dungeons",
    ];

    private sealed class BtPlace {
        public string Name { get; init; } = "";
        public string Zone { get; init; } = "";
        public string World { get; init; } = "";
    }

    private sealed class BtPlaceEntry {
        public string Name { get; init; } = "";
        public List<BtPlace> Places { get; init; } = [];
        public List<ulong> MinionOf { get; init; } = [];
    }

    private sealed class BtPlacesFile {
        public string Source { get; init; } = "";
        public Dictionary<string, BtPlaceEntry> Bosses { get; init; } = [];
    }

    private sealed record BtCheat(string Summary, string Notes, bool Off);

    private sealed class BtBoss {
        public string Key { get; init; } = "";
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string World { get; init; } = "";
        public string About { get; init; } = "";
        public List<string> Sources { get; init; } = [];
        public List<BtCheat> Cheats { get; init; } = [];
        public List<BtPlace> Places { get; init; } = [];
        public List<string> MinionOf { get; init; } = [];
        public string JoinMe { get; init; } = "";
        public bool IsCustom { get; init; }
        // What its cheats are now, to see whether they changed since the tick.
        public string Signature { get; init; } = "";
        public string Haystack { get; set; } = "";
    }

    private enum BtRowKind { World, Boss }

    private sealed record BtRow(BtRowKind Kind, string Text, string Line, BtBoss? Boss);

    private static readonly string[] s_btShow = [ANY_FILTER, "Left to test", "Tested", BT_RETEST];

    private readonly TextBox _btSearch = new() { Watermark = "Search a boss, its number, a place, a spell or what a cheat does" };
    private readonly ComboBox _btWorld = FilterCombo([ANY_FILTER], 170);
    private readonly ComboBox _btSource = FilterCombo([ANY_FILTER], 200);
    private readonly ComboBox _btShowing = FilterCombo(s_btShow, 170);
    private readonly CheckBox _btMinions = new() { Content = "Include minions", IsChecked = true };
    private readonly TextBlock _btCount = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _btList = new() { Background = Brushes.Transparent };
    private readonly StackPanel _btDetails = new() { Spacing = 6 };

    private List<BtBoss> _btBosses = [];
    private Dictionary<string, string> _btDone = new(StringComparer.OrdinalIgnoreCase);
    private BtPlacesFile? _btPlaces;
    private BtBoss? _btPicked;
    private ObservableCollection<BtRow> _btRows = [];
    private int _btShown;
    private bool _btLoading;

    private string BossTestDonePath => Path.Combine(_manager.InstallFolder, BOSS_TEST_DONE);

    private bool BtDone(BtBoss boss) => _btDone.TryGetValue(boss.Key, out var signature) && signature == boss.Signature;

    private bool BtRetest(BtBoss boss) => _btDone.TryGetValue(boss.Key, out var signature) && signature != boss.Signature;

    private static bool BtIsMinion(BtBoss boss) => boss.MinionOf.Count > 0 || boss.About.Contains("minion", StringComparison.OrdinalIgnoreCase);

    private Control BossTestTab() {
        _btShowing.SelectedIndex = 1;
        _btList.ItemTemplate = new FuncDataTemplate<BtRow>((row, _) => row is null ? new Panel() : BossTestRowView(row));
        _btList.SelectionChanged += (_, e) => {
            if (e.Source != _btList) {
                return;
            }

            if (_btList.SelectedItem is BtRow { Boss: { } boss }) {
                if (boss != _btPicked) {
                    ShowBossTest(boss);
                }
            }
            else if (_btList.SelectedItem is not null) {
                _btList.SelectedItem = null;
            }
        };
        WhenFiltersChange(RenderBossTest, _btSearch, _btWorld, _btSource, _btShowing, _btMinions);

        var next = Primary("Next to test");
        ToolTip.SetTip(next, "Shows the first boss in the list that isn't ticked yet.");
        var details = new ScrollViewer { MinWidth = 420, Margin = new Thickness(14, 0, 0, 0), Content = Card(_btDetails) };
        ShowBossTest(null);

        var left = new DockPanel {
            Children = {
                WithDock(Fold("crown", "Bosses", "Boss cheat testing",
                    "Every boss and minion with cheats, by world: what each cheat should do, where to find it, and a tick once its " +
                    "cheats worked. Cheats you made or changed in Ahziael are in it too.",
                    WithHint(_btSearch, "Start any fight, then press Bring into my battle on a boss (or type its .joinme line in chat): " +
                        "it joins the enemy side. Play a few rounds and watch for what its cheats should do. Tick it when they all " +
                        "worked. If you change its cheats later, it comes back as \"Changed since you tested\". Ticks are kept on this PC only."),
                    SearchFilters(
                        SearchFilter("World", _btWorld),
                        SearchFilter("Where the cheats come from", _btSource),
                        SearchFilter("Showing", _btShowing),
                        SearchTick(_btMinions)),
                    new WrapPanel {
                        Children = {
                            Spaced(Action(next, () => {
                                BossTestNext();

                                return Task.CompletedTask;
                            })),
                        },
                    },
                    _btCount), Dock.Top),
                WithMargin(_btList, new Thickness(0, 14, 0, 0)),
            },
        };
        Grid.SetColumn(details, 1);

        return new Grid {
            Margin = new Thickness(24, 12, 24, 16),
            ColumnDefinitions = new ColumnDefinitions("3*,2*"),
            Children = { left, details },
        };
    }

    /// <summary>Reads every cheat file, the app's changes and the customs again. Runs each time the tab shows.</summary>
    private async Task LoadBossTestAsync() {
        if (_btLoading) {
            return;
        }

        _btLoading = true;
        try {
            if (_btBosses.Count == 0) {
                _btCount.Text = "Loading…";
            }

            _btPlaces ??= await Task.Run(ReadBossPlaces);
            var places = _btPlaces;
            var (extra, changes, customs, idLookup) = (CheatExtraFolder, _manager.BossChangesFolder, _manager.CustomsFolder, _manager.IdLookupFolder);
            _btBosses = await Task.Run(() => ReadBossTest(extra, changes, customs, idLookup, places));
            _btDone = ReadBossTestDone();
            FillFilterCombo(_btWorld, BtWorldsInOrder(_btBosses.Select(b => b.World)));
            FillFilterCombo(_btSource, _btBosses.SelectMany(b => b.Sources).Distinct().OrderBy(s => s));
            RenderBossTest();
            var picked = _btPicked is null ? null : _btBosses.FirstOrDefault(b => b.Key == _btPicked.Key);
            ShowBossTest(picked);
        }
        finally {
            _btLoading = false;
        }
    }

    private static IEnumerable<string> BtWorldsInOrder(IEnumerable<string> worlds)
        => worlds.Distinct().OrderBy(BtWorldRank).ThenBy(w => w, StringComparer.OrdinalIgnoreCase);

    private static int BtWorldRank(string world) {
        var i = Array.IndexOf(s_btWorldOrder, world);

        return world == BT_NO_PLACE ? int.MaxValue : i >= 0 ? i : s_btWorldOrder.Length;
    }

    private static BtPlacesFile? ReadBossPlaces() {
        try {
            var assembly = typeof(ManagerWindow).Assembly.GetName().Name;
            using var stream = AssetLoader.Open(new Uri($"avares://{assembly}/Assets/{BOSS_TEST_PLACES}"));
            using var unzip = new GZipStream(stream, CompressionMode.Decompress);

            return JsonSerializer.Deserialize<BtPlacesFile>(unzip, s_qeJson);
        }
        catch (Exception) {
            // A build without the file, or a broken one: no places, the rest still works.
            return null;
        }
    }

    // The first comment line of a cheat file, such as "Spider Adept (Rank 14, Myth, 2,415 health), Khrysalis. ...".
    private static string BtHeader(string text) {
        foreach (var raw in text.Split('\n').Take(4)) {
            var line = raw.Trim().TrimStart('﻿', '{').Trim();
            if (line.StartsWith("//", StringComparison.Ordinal)) {
                return line.TrimStart('/').Trim();
            }
        }

        return "";
    }

    private static JsonObject? BtReadJson(string file) {
        try {
            return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file), null, s_jsonc) as JsonObject : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    // A game line's key in a cheat's words, such as "CombatMessages_00001011".
    private static readonly Regex s_btLineKey = new("\"(?<key>[A-Za-z][A-Za-z0-9]*_[0-9]+)\"", RegexOptions.Compiled);

    private static List<BtCheat> BtCheats(BossCheatList list, CheatLines lines, out string signature) {
        var cheats = new List<BtCheat>();
        var all = new StringBuilder();
        foreach (var rule in list.Cheats) {
            string summary;
            try {
                summary = CheatSpecs.Summary(rule);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException or KeyNotFoundException) {
                summary = rule.Name.Length > 0 ? rule.Name : "A cheat the app can't put in words.";
            }

            // The line as the battle shows it, instead of its key.
            summary = s_btLineKey.Replace(summary, m => lines.ByKey(m.Groups["key"].Value) is { Length: > 0 } text ? $"\"{text.Trim()}\"" : m.Value);
            cheats.Add(new BtCheat(rule.Name.Length > 0 ? $"{rule.Name}: {summary}" : summary, rule.Notes, rule.Off));
            all.Append(rule.Off ? "off:" : "on:").Append(rule.Canonical().ToJsonString()).Append('\n');
        }

        signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(all.ToString())))[..16];

        return cheats;
    }

    private static List<BtBoss> ReadBossTest(string extraFolder, string changesFolder, string customsFolder, string idLookupFolder,
                                             BtPlacesFile? places) {
        var bosses = new List<BtBoss>();
        var lines = CheatLines.Load(idLookupFolder);
        var placeOf = places?.Bosses ?? [];

        string NameOf(string id) => placeOf.TryGetValue(id, out var p) && p.Name.Length > 0 ? p.Name : $"Creature {id}";

        BtBoss Make(string key, string id, string name, string world, string about, List<string> sources, List<BtCheat> cheats,
                    string signature, string joinMe, bool custom) {
            var entry = placeOf.GetValueOrDefault(id);
            var where = entry?.Places ?? [];
            if (world.Length == 0) {
                world = where.FirstOrDefault()?.World ?? BT_NO_PLACE;
            }

            var boss = new BtBoss {
                Key = key, Id = id, Name = name, World = world, About = about, Sources = sources, Cheats = cheats, Places = where,
                MinionOf = [.. (entry?.MinionOf ?? []).Select(m => $"{NameOf(m.ToString())} ({m})")],
                JoinMe = joinMe, IsCustom = custom, Signature = signature,
            };
            boss.Haystack = SearchText.Normalize(string.Join(' ', [
                name, id, world, about, joinMe, string.Join(' ', sources), string.Join(' ', boss.MinionOf),
                string.Join(' ', where.Select(p => $"{p.Name} {p.Zone} {p.World}")),
                string.Join(' ', cheats.Select(c => $"{c.Summary} {c.Notes}")),
            ]));

            return boss;
        }

        // Each creature with a cheat file, with the changes made in the app laid over it.
        var withFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cheatFolder = Path.Combine(extraFolder, "BossCheats");
        if (Directory.Exists(cheatFolder)) {
            foreach (var file in Directory.EnumerateFiles(cheatFolder, "*.json")) {
                var id = Path.GetFileNameWithoutExtension(file);
                try {
                    var text = File.ReadAllText(file);
                    var json = JsonNode.Parse(text, null, s_jsonc) as JsonObject;
                    var saved = BtReadJson(Path.Combine(changesFolder, $"{id}.json"));
                    var savedCheats = saved is null ? null : BossChanges.FindIn(saved, "Cheats") as JsonObject;
                    var list = BossCheatList.Load(extraFolder, id, savedCheats);
                    if (list.Cheats.Count == 0) {
                        continue;
                    }

                    withFile.Add(id);
                    var about = BtHeader(text);
                    var sources = new List<string> {
                        text.Contains("Wizard404", StringComparison.OrdinalIgnoreCase) ? BT_WIZARD404
                            : text.Contains("Central", StringComparison.OrdinalIgnoreCase) ? BT_WIKI
                            : BT_GAME,
                    };
                    if (savedCheats is not null) {
                        sources.Add(BT_CHANGED);
                    }

                    var name = (json is null ? null : BossChanges.FindIn(json, "Name") as JsonValue)?.ToString() is { Length: > 0 } n ? n : NameOf(id);
                    var cheats = BtCheats(list, lines, out var signature);
                    bosses.Add(Make(id, id, name, CheatSearch.WorldOfCheatFile(text), about, sources, cheats, signature, $".joinme {id}", custom: false));
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException
                                               or FormatException or ArgumentException) {
                    // A file that cannot be read is left out; the Bosses tab says what is wrong with it when it is opened.
                }
            }
        }

        // Creatures with no cheat file that were given cheats in the Bosses tab.
        if (Directory.Exists(changesFolder)) {
            foreach (var file in Directory.EnumerateFiles(changesFolder, "*.json")) {
                var id = Path.GetFileNameWithoutExtension(file);
                if (withFile.Contains(id)) {
                    continue;
                }

                try {
                    if (BtReadJson(file) is not { } saved || BossChanges.FindIn(saved, "Cheats") is not JsonObject savedCheats) {
                        continue;
                    }

                    var list = BossCheatList.Load(extraFolder, id, savedCheats);
                    if (list.Cheats.Count == 0) {
                        continue;
                    }

                    var name = (BossChanges.FindIn(saved, "Name") as JsonValue)?.ToString() is { Length: > 0 } n ? n : NameOf(id);
                    var cheats = BtCheats(list, lines, out var signature);
                    bosses.Add(Make(id, id, name, "", "Cheats you gave it on the Bosses tab.", [BT_YOURS], cheats, signature, $".joinme {id}", custom: false));
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException
                                               or FormatException or ArgumentException) {
                    // As above.
                }
            }
        }

        // The customs with cheats: their own over the creature they are based on.
        foreach (var custom in CustomMob.All(customsFolder)) {
            try {
                var json = BtReadJson(CustomMob.FileOf(customsFolder, custom.Id));
                var savedCheats = json is null ? null : BossChanges.FindIn(json, "Cheats") as JsonObject;
                var list = BossCheatList.Load(extraFolder, custom.BaseId, savedCheats);
                if (list.Cheats.Count == 0) {
                    continue;
                }

                var cheats = BtCheats(list, lines, out var signature);
                var about = $"Your custom {custom.Id}, made from {NameOf(custom.BaseId)} ({custom.BaseId}).";
                bosses.Add(Make($"custom:{custom.Id}", custom.BaseId, custom.Shown, "", about, [BT_CUSTOM], cheats, signature, custom.JoinMe, custom: true));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException
                                           or FormatException or ArgumentException) {
                // As above.
            }
        }

        return [.. bosses.OrderBy(b => BtWorldRank(b.World)).ThenBy(b => b.World, StringComparer.OrdinalIgnoreCase)
            .ThenBy(b => BtIsMinion(b)).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ThenBy(b => b.Key, StringComparer.Ordinal)];
    }

    private Dictionary<string, string> ReadBossTestDone() {
        try {
            if (File.Exists(BossTestDonePath)) {
                var done = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(BossTestDonePath)) ?? [];
                return new Dictionary<string, string>(done, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            Say($"Could not read your tested bosses ({BOSS_TEST_DONE}): {ex.Message}", isError: true);
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveBossTestDone() {
        try {
            Directory.CreateDirectory(_manager.InstallFolder);
            var sorted = _btDone.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(p => p.Key, p => p.Value);
            File.WriteAllText(BossTestDonePath, JsonSerializer.Serialize(sorted, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Say($"Could not save your tested bosses ({BOSS_TEST_DONE}): {ex.Message}", isError: true);
        }
    }

    private void SetBossTested(BtBoss boss, bool tested) {
        if (tested == BtDone(boss)) {
            return;
        }

        if (tested) {
            _btDone[boss.Key] = boss.Signature;
        }
        else {
            _btDone.Remove(boss.Key);
        }

        SaveBossTestDone();
        BossTestRefreshRow(boss);
        if (_btPicked == boss) {
            ShowBossTest(boss);
        }
    }

    /// <summary>Redraws one boss's row and its world's heading after a tick, without moving the list.</summary>
    private void BossTestRefreshRow(BtBoss boss) {
        var index = -1;
        for (var i = 0; i < _btRows.Count; i++) {
            if (_btRows[i].Boss == boss) {
                index = i;
                break;
            }
        }

        if (index >= 0) {
            // A tested boss leaves a "Left to test" list, and an untested one a "Tested" list.
            if (_btShowing.SelectedIndex is 1 or 2 or 3) {
                _btRows.RemoveAt(index);
                _btShown--;
            }
            else {
                _btRows[index] = new BtRow(BtRowKind.Boss, boss.Name, "", boss);
            }

            for (var i = Math.Min(index, _btRows.Count - 1); i >= 0; i--) {
                if (_btRows[i].Kind == BtRowKind.World) {
                    _btRows[i] = BossTestWorldRow(boss.World);
                    break;
                }
            }
        }

        BossTestCount(null);
    }

    private BtRow BossTestWorldRow(string world) {
        var inWorld = _btBosses.Where(b => b.World == world).ToList();

        return new BtRow(BtRowKind.World, world, $"{inWorld.Count(BtDone):N0} of {inWorld.Count:N0} tested.", null);
    }

    private bool BossTestMatches(BtBoss boss, string[] words, string? world, string? source) {
        var done = BtDone(boss);

        return words.All(w => boss.Haystack.Contains(w, StringComparison.Ordinal))
               && (world is null || boss.World == world)
               && (source is null || boss.Sources.Contains(source))
               && (_btMinions.IsChecked == true || !BtIsMinion(boss))
               && _btShowing.SelectedIndex switch {
                   1 => !done,
                   2 => done,
                   3 => BtRetest(boss),
                   _ => true,
               };
    }

    private void RenderBossTest() {
        if (_btBosses.Count == 0) {
            _btRows = [];
            _btList.ItemsSource = null;
            _btCount.Text = Directory.Exists(Path.Combine(CheatExtraFolder, "BossCheats"))
                ? "No creature has cheats yet."
                : "The server's cheat files aren't here yet. Install or update the server on the Server tab first.";

            return;
        }

        var words = SearchText.Words(_btSearch.Text);
        var world = FilterPick(_btWorld);
        var source = FilterPick(_btSource);
        var rows = new List<BtRow>();
        var shown = 0;
        foreach (var group in _btBosses.GroupBy(b => b.World)) {
            var matching = group.Where(b => BossTestMatches(b, words, world, source)).ToList();
            if (matching.Count == 0) {
                continue;
            }

            rows.Add(BossTestWorldRow(group.Key));
            rows.AddRange(matching.Select(b => new BtRow(BtRowKind.Boss, b.Name, "", b)));
            shown += matching.Count;
        }

        _btRows = new ObservableCollection<BtRow>(rows);
        _btList.ItemsSource = _btRows;
        BossTestCount(shown);
    }

    private void BossTestCount(int? shown) {
        if (shown is { } s) {
            _btShown = s;
        }

        var done = _btBosses.Count(BtDone);
        var retest = _btBosses.Count(BtRetest);
        _btCount.Text = $"{FilterCount(_btShown, _btBosses.Count, "bosses and minions with cheats")} {done:N0} tested" +
                        (_btBosses.Count > 0 ? $" ({done * 100 / _btBosses.Count}%)" : "") +
                        $", {_btBosses.Count - done:N0} left" +
                        (retest > 0 ? $", {retest:N0} of them changed since you tested." : ".") +
                        $" {_btBosses.Sum(b => b.Cheats.Count):N0} cheats in all.";
    }

    private Control BossTestRowView(BtRow row) {
        if (row.Kind == BtRowKind.World) {
            return new StackPanel {
                Margin = new Thickness(2, 16, 0, 4), Spacing = 2,
                Children = {
                    new TextBlock { Text = row.Text, FontSize = 20, FontWeight = FontWeight.Bold, Foreground = ManagerTheme.Accent("Bosses") },
                    Sub(row.Line),
                },
            };
        }

        var boss = row.Boss!;
        var tick = new CheckBox { IsChecked = BtDone(boss), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(tick, "Tick once this boss's cheats all worked.");
        tick.IsCheckedChanged += (_, _) => SetBossTested(boss, tick.IsChecked == true);

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(tick);
        title.Children.Add(new TextBlock { Text = boss.Name, FontWeight = FontWeight.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(WithCenter(Sub(boss.JoinMe)));

        var tags = new WrapPanel { Margin = new Thickness(32, 2, 0, 0) };
        if (BtRetest(boss)) {
            tags.Children.Add(WithMargin(QtTag(BT_RETEST, WARN), new Thickness(0, 0, 6, 2)));
        }

        tags.Children.Add(WithMargin(QtTag(boss.Cheats.Count == 1 ? "1 cheat" : $"{boss.Cheats.Count} cheats", ManagerTheme.Accent("Bosses")), new Thickness(0, 0, 6, 2)));
        if (BtIsMinion(boss)) {
            tags.Children.Add(WithMargin(QtTag("Minion", MUTED), new Thickness(0, 0, 6, 2)));
        }

        foreach (var source in boss.Sources) {
            tags.Children.Add(WithMargin(QtTag(source, source is BT_WIZARD404 or BT_WIKI or BT_GAME ? MUTED : WARN), new Thickness(0, 0, 6, 2)));
        }

        var where = boss.Places.Count > 0 ? string.Join(", ", boss.Places.Take(2).Select(p => p.Name)) + (boss.Places.Count > 2 ? " and more" : "") : "";

        return new Border {
            Padding = new Thickness(4, 5, 4, 5),
            BorderBrush = LINE,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Opacity = BtDone(boss) ? 0.6 : 1,
            Child = new StackPanel {
                Spacing = 2,
                Children = { title, tags, WithMargin(Sub(where.Length > 0 ? where : boss.About), new Thickness(32, 0, 0, 0)) },
            },
        };
    }

    private void BossTestNext() {
        var next = _btBosses.FirstOrDefault(b => !BtDone(b) && (_btMinions.IsChecked == true || !BtIsMinion(b)));
        if (next is null) {
            Say("Every boss with cheats is ticked as tested.");

            return;
        }

        if (_btRows.All(r => r.Boss != next)) {
            _btSearch.Text = "";
            _btWorld.SelectedIndex = 0;
            _btSource.SelectedIndex = 0;
            _btShowing.SelectedIndex = 1;
            RenderBossTest();
        }

        ShowBossTest(next);
        if (_btRows.FirstOrDefault(r => r.Boss == next) is { } row) {
            _btList.SelectedItem = row;
            _btList.ScrollIntoView(row);
        }
    }

    private Task RunBossTestCommand(string command, string said) {
        _manager.SendCommand(command);
        Say(said + " The game's chat says if it worked.");

        return Task.CompletedTask;
    }

    private void ShowBossTest(BtBoss? boss) {
        _btPicked = boss;
        _btDetails.Children.Clear();
        if (boss is null) {
            _btDetails.Children.Add(Sub("Click a boss on the left to see what its cheats should do and where it is. " +
                                        "Or press Next to test to start where you left off."));

            return;
        }

        void Heading(string text) => _btDetails.Children.Add(new TextBlock {
            Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.Bold, Foreground = MUTED, Margin = new Thickness(0, 10, 0, 0),
        });

        void Lines(IEnumerable<string> lines) => _btDetails.Children.Add(new SelectableTextBlock {
            Text = string.Join('\n', lines), TextWrapping = TextWrapping.Wrap, FontSize = 13,
        });

        _btDetails.Children.Add(new SelectableTextBlock { Text = boss.Name, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (boss.About.Length > 0) {
            _btDetails.Children.Add(Sub(boss.About));
        }

        var tested = new CheckBox { Content = "Tested: its cheats worked", IsChecked = BtDone(boss) };
        tested.IsCheckedChanged += (_, _) => SetBossTested(boss, tested.IsChecked == true);
        _btDetails.Children.Add(tested);
        if (BtRetest(boss)) {
            _btDetails.Children.Add(new TextBlock {
                Text = "Its cheats changed since you ticked it, so test it again.", Foreground = WARN, TextWrapping = TextWrapping.Wrap,
            });
        }

        var buttons = new WrapPanel();
        var join = Primary("Bring into my battle");
        ToolTip.SetTip(join, $"While you are in a fight, it joins the enemy side ({boss.JoinMe}). Start any fight first.");
        buttons.Children.Add(Spaced(Action(join, () => RunBossTestCommand(boss.JoinMe, $"Sent {boss.JoinMe}. It joins your battle from the next round."))));
        buttons.Children.Add(Spaced(Action(Plain("Copy .joinme"), () => CopyAsync(boss.JoinMe))));
        if (!boss.IsCustom) {
            var open = Plain("Open in Bosses");
            ToolTip.SetTip(open, "Shows this creature on the Bosses tab, to change its cheats.");
            buttons.Children.Add(Spaced(Action(open, () => {
                SelectTab("Bosses");
                _bossSearch.Text = boss.Id;

                return Task.CompletedTask;
            })));
        }
        else {
            var open = Plain("Open in Customs");
            ToolTip.SetTip(open, "Shows your customs, to change this one's cheats.");
            buttons.Children.Add(Spaced(Action(open, () => {
                SelectTab("Customs");

                return Task.CompletedTask;
            })));
        }

        _btDetails.Children.Add(buttons);

        Heading("How to test it");
        Lines([
            "1. Start any fight (walk into a monster), or go to one of the places below.",
            "2. Press Bring into my battle, or type " + boss.JoinMe + " in chat. It joins the enemy side.",
            "3. Play until each cheat below has had its chance, then tick it if they all went off the way it says.",
        ]);

        Heading($"What its cheats should do ({boss.Cheats.Count})");
        var number = 0;
        foreach (var cheat in boss.Cheats) {
            number++;
            var block = new StackPanel { Spacing = 2, Margin = new Thickness(0, 2, 0, 4) };
            var line = new SelectableTextBlock {
                Text = $"{number}. {cheat.Summary}" + (cheat.Off ? "  (turned off, so it should NOT happen)" : ""),
                TextWrapping = TextWrapping.Wrap, FontSize = 13,
            };
            if (cheat.Off) {
                line.Foreground = MUTED;
            }

            block.Children.Add(line);
            if (cheat.Notes.Length > 0) {
                block.Children.Add(WithMargin(new SelectableTextBlock { Text = cheat.Notes, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = MUTED },
                    new Thickness(18, 0, 0, 0)));
            }

            _btDetails.Children.Add(block);
        }

        Heading("Where it is");
        if (boss.MinionOf.Count > 0) {
            Lines([$"Comes with {string.Join(", ", boss.MinionOf)}."]);
        }

        if (boss.Places.Count == 0) {
            Lines(["Not written down. Bring it into any fight with " + boss.JoinMe + " instead."]);
        }

        foreach (var place in boss.Places) {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            if (place.Zone.Length > 0) {
                var go = Plain("Go there");
                ToolTip.SetTip(go, $"Takes you to {place.Name} (.tp {place.Zone}).");
                row.Children.Add(WithDock(Action(go, () => RunBossTestCommand($".tp {place.Zone}", $"Teleporting to {place.Name}.")), Dock.Right));
            }

            row.Children.Add(WithCenter(new SelectableTextBlock {
                Text = $"{place.Name}  ·  {place.World}", TextWrapping = TextWrapping.Wrap, FontSize = 13,
            }));
            _btDetails.Children.Add(row);
        }

        if (_btPlaces is { Source.Length: > 0 } places && boss.Places.Count > 0) {
            _btDetails.Children.Add(Sub($"Places from {places.Source}."));
        }
    }

}
