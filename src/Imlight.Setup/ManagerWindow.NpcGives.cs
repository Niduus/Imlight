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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace Imlight.Setup;

/// <summary>
/// Game Rules > What NPCs Give (Niduus, 2026-11-04: "a npc section too for quests and stuff to be able to be toggled of
/// what they can and can't give"): pick any NPC of the game that gives something, and switch each quest it offers, each
/// item and recipe it sells, each treasure card and each spell it teaches on or off, or a whole part at once. The switches
/// are kept with the NPC's other changes in spiraldb-extra/Customs/NpcChanges.json (HideQuests, QuestsOff, RemoveItems,
/// RemoveRecipes, RemoveTreasureCardNames, RemoveSpells, HideShop, HideTreasureCards, HideTraining), which the server reads
/// again within a second, so a switch counts the next time a player talks to the NPC. The lists come from what the server
/// wrote: vendors.json (shops, cards, spells) and quest-details.json (who gives each quest), with the Quest Editor's givers.
/// </summary>
internal sealed partial class ManagerWindow {

    private const int NPC_GIVES_MAX_ROWS = 300;

    private static readonly string[] s_ngGives = [ANY_FILTER, "Quests", "Shop items", "Treasure cards", "Spells"];
    private static readonly string[] s_ngShow = ["Every NPC", "Only ones with something off", "Only ones with everything on"];

    private sealed record NgQuest(string Code, string Title, int Level, bool OffForEveryone);

    private sealed record NgNpc(uint Id, string Name, string Kind, string World, string Place, VendorNpc? Vendor, List<NgQuest> Quests,
        string Haystack) {
        internal int ShopCount => Vendor?.Shops.Sum(s => s.Items.Count + s.Recipes.Count) ?? 0;
        internal int CardCount => Vendor?.TreasureCards.Count ?? 0;
        internal int SpellCount => Vendor?.Spells.Count ?? 0;
    }

    private readonly TextBox _ngSearch = new() { Watermark = "Search an NPC, a place, or a quest, item or spell it gives, such as Gamma or Fire Elf" };
    private readonly ComboBox _ngGivesCombo = FilterCombo(s_ngGives, 150);
    private readonly ComboBox _ngWorld = FilterCombo([ANY_FILTER], 160);
    private readonly ComboBox _ngShowCombo = FilterCombo(s_ngShow, 220);
    private readonly TextBlock _ngCount = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _ngList = new() { Background = Brushes.Transparent, Classes = { "compact" } };
    private readonly StackPanel _ngDetails = new() { Spacing = 6 };

    private List<NgNpc> _ngNpcs = [];
    private Dictionary<uint, NpcChange> _ngChanges = [];
    private Dictionary<string, LookupEntry> _ngNames = new(StringComparer.Ordinal);
    private NgNpc? _ngPicked;

    private Control NpcGivesTab() {
        _ngList.ItemTemplate = new FuncDataTemplate<NgNpc>((npc, _) => npc is null ? new Panel() : NgRowView(npc));
        _ngList.SelectionChanged += (_, e) => {
            if (e.Source == _ngList && _ngList.SelectedItem is NgNpc npc && npc != _ngPicked) {
                ShowNpcGives(npc);
            }
        };
        WhenFiltersChange(RenderNpcGives, _ngSearch, _ngGivesCombo, _ngWorld, _ngShowCombo);
        ShowNpcGives(null);

        var details = new ScrollViewer { Width = 480, Margin = new Thickness(14, 0, 0, 0), Content = Card(_ngDetails) };

        return new DockPanel {
            Margin = new Thickness(24, 12, 24, 16),
            Children = {
                WithDock(details, Dock.Right),
                WithDock(Fold("bag", "Customs", "What NPCs give",
                    "Pick an NPC, then switch each quest it offers, and each thing it sells or teaches, on or off. " +
                    "It counts the next time a player talks to it, with no server restart.",
                    WithHint(_ngSearch,
                        "Every NPC that offers a quest, sells something or teaches a spell is here. Type its name, its place, or the name of " +
                        "a quest, item or spell it gives. Click one to see its switches on the right. Off means the NPC no longer offers or " +
                        "sells that one. A player who already has an off quest keeps it and can still hand it in. Every copy of that NPC in " +
                        "the game changes. The game's files are never changed. To add new things to an NPC or change prices, use Create > " +
                        "Customs > Change the game's vendors and trainers. To turn a quest off for every NPC, use Create > Quest Editor."),
                    WithCount(SearchFilters(
                        SearchFilter("Gives", _ngGivesCombo),
                        SearchFilter("World", _ngWorld),
                        SearchFilter("Show", _ngShowCombo)),
                        _ngCount)), Dock.Top),
                WithMargin(_ngList, new Thickness(0, 14, 0, 0)),
            },
        };
    }

    /// <summary>Reads the vendor list, the quest details and the switches again. Runs when the tab shows.</summary>
    private async Task LoadNpcGivesAsync() {
        await LoadVendorsAsync();
        LoadQuestDetails();
        _qeChanges = ReadQuestChanges();
        LoadQuestGuide();
        _ngChanges = SafeNpcChanges().GroupBy(c => c.TemplateID).ToDictionary(g => g.Key, g => g.First());

        _ngNames = new Dictionary<string, LookupEntry>(StringComparer.Ordinal);
        foreach (var entry in _lookup?.Entries ?? []) {
            if (entry.Kind is "Item" or "Spell") {
                _ngNames.TryAdd($"{entry.Kind}:{entry.Id}", entry);
            }
        }

        _ngNpcs = BuildNpcGives();
        FillFilterCombo(_ngWorld, _ngNpcs.Select(n => n.World).Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(WorldOrder).ThenBy(w => w, StringComparer.OrdinalIgnoreCase));
        if (_ngPicked is { } picked) {
            ShowNpcGives(_ngNpcs.FirstOrDefault(n => n.Id == picked.Id));
        }

        RenderNpcGives();
    }

    private List<NgNpc> BuildNpcGives() {
        // The quests each NPC offers: the Quest Editor's giver when it has one, else the game's.
        var quests = new Dictionary<uint, List<NgQuest>>();
        var givers = new Dictionary<uint, (string Name, string Zone)>();
        foreach (var row in AllQuestRows()) {
            var giver = row.Change is { Giver: > 0 } change ? change.Giver : row.Quest.Giver;
            if (giver == 0 || giver > uint.MaxValue) {
                continue;
            }

            var id = (uint) giver;
            if (!quests.TryGetValue(id, out var list)) {
                quests[id] = list = [];
            }

            if (list.All(q => !string.Equals(q.Code, row.Quest.Code, StringComparison.OrdinalIgnoreCase))) {
                list.Add(new NgQuest(row.Quest.Code, row.Quest.Title.Length > 0 ? row.Quest.Title : row.Quest.Code,
                    row.Change?.Level ?? row.Quest.Level, row.Change is { Off: true } || row.Change is { Skip: true }));
            }

            if (!givers.ContainsKey(id)) {
                var npc = _qeNpcs.GetValueOrDefault(giver);
                var name = giver == row.Quest.Giver && row.Quest.GiverName.Length > 0 ? row.Quest.GiverName : npc?.Name ?? "";
                givers[id] = (name, npc?.Zone ?? "");
            }
        }

        var npcs = new List<NgNpc>();
        var seen = new HashSet<uint>();
        foreach (var row in _vendors) {
            var v = row.Npc;
            if (!seen.Add(v.Id)) {
                continue;
            }

            var given = quests.GetValueOrDefault(v.Id) ?? [];
            var kind = v.Test.Length > 0 ? "Test trainer" : v.Kind;
            if (given.Count > 0) {
                kind = $"{kind}, quest giver";
            }

            var name = v.Name.Length > 0 ? v.Name : givers.TryGetValue(v.Id, out var g) && g.Name.Length > 0 ? g.Name : v.Object;
            npcs.Add(new NgNpc(v.Id, name, kind, v.World, v.Place, v, SortQuests(given),
                row.Haystack + " " + QuestHaystack(given) + " " + SearchText.Normalize(name)));
        }

        foreach (var (id, given) in quests) {
            if (!seen.Add(id)) {
                continue;
            }

            var (name, zone) = givers.GetValueOrDefault(id);
            name = name is { Length: > 0 } ? name : LookName(id);
            var world = zone is { Length: > 0 } ? WorldOfZone(zone) : given.Count > 0 ? QuestWorldOf(given[0].Code, "") : "";
            npcs.Add(new NgNpc(id, name, "Quest giver", world, zone ?? "", null, SortQuests(given),
                SearchText.Normalize($"{name} {id} {zone} {world} quest giver") + " " + QuestHaystack(given)));
        }

        return [.. npcs.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)];

        static List<NgQuest> SortQuests(List<NgQuest> list)
            => [.. list.OrderBy(q => q.Level).ThenBy(q => q.Title, StringComparer.OrdinalIgnoreCase)];

        static string QuestHaystack(List<NgQuest> list)
            => SearchText.Normalize(string.Join(' ', list.Select(q => $"{q.Title} {q.Code}")));
    }

    // How many of an NPC's things are switched off (a whole part counts as everything in it).
    private int OffCount(NgNpc npc) {
        if (!_ngChanges.TryGetValue(npc.Id, out var c)) {
            return 0;
        }

        var off = c.HideQuests ? npc.Quests.Count : npc.Quests.Count(q => c.QuestsOff.Contains(q.Code, StringComparer.OrdinalIgnoreCase));
        if (npc.Vendor is { } v) {
            off += c.HideShop ? npc.ShopCount : v.Shops.Sum(s => s.Items.Count(c.RemoveItems.Contains)
                + s.Recipes.Count(r => c.RemoveRecipes.Contains(r, StringComparer.OrdinalIgnoreCase)));
            off += c.HideTreasureCards ? npc.CardCount : v.TreasureCards.Count(t => c.RemoveTreasureCardNames.Contains(t, StringComparer.OrdinalIgnoreCase));
            off += c.HideTraining ? npc.SpellCount : v.Spells.Count(s => c.RemoveSpells.Contains(s.Id));
        }

        return off;
    }

    private void RenderNpcGives() {
        if (_ngNpcs.Count == 0) {
            _ngList.ItemsSource = null;
            _ngCount.Text = "Nothing to list yet. Start the server once and give it a few minutes: it writes the lists of vendors, " +
                "trainers and quests this page shows.";

            return;
        }

        var words = SearchText.Words(_ngSearch.Text);
        var gives = FilterPick(_ngGivesCombo);
        var world = FilterPick(_ngWorld);
        var show = _ngShowCombo.SelectedIndex;
        var exact = (_ngSearch.Text ?? "").Trim();
        var matches = _ngNpcs.Where(n => words.All(w => n.Haystack.Contains(w, StringComparison.Ordinal))
                                         && (world is null || string.Equals(n.World, world, StringComparison.OrdinalIgnoreCase))
                                         && gives switch {
                                             "Quests" => n.Quests.Count > 0,
                                             "Shop items" => n.ShopCount > 0,
                                             "Treasure cards" => n.CardCount > 0,
                                             "Spells" => n.SpellCount > 0,
                                             _ => true,
                                         }
                                         && (show <= 0 || (OffCount(n) > 0) == (show == 1)))
            .OrderBy(n => !(n.Name.Equals(exact, StringComparison.OrdinalIgnoreCase)
                            || n.Id.ToString(CultureInfo.InvariantCulture) == exact))
            .ToList();
        _ngList.ItemsSource = matches.Take(NPC_GIVES_MAX_ROWS).ToList();
        _ngCount.Text = matches.Count > NPC_GIVES_MAX_ROWS
            ? $"Showing {NPC_GIVES_MAX_ROWS} of {matches.Count:N0}. Type more of a name to find the one you want."
            : $"{matches.Count:N0} found.";
    }

    private Control NgRowView(NgNpc npc) {
        var what = new List<string>();
        if (npc.Quests.Count > 0) {
            what.Add($"{npc.Quests.Count:N0} quest{(npc.Quests.Count == 1 ? "" : "s")}");
        }

        if (npc.ShopCount > 0) {
            what.Add($"{npc.ShopCount:N0} shop item{(npc.ShopCount == 1 ? "" : "s")}");
        }

        if (npc.CardCount > 0) {
            what.Add($"{npc.CardCount:N0} treasure card{(npc.CardCount == 1 ? "" : "s")}");
        }

        if (npc.SpellCount > 0) {
            what.Add($"{npc.SpellCount:N0} spell{(npc.SpellCount == 1 ? "" : "s")}");
        }

        var off = OffCount(npc);

        return new StackPanel {
            Margin = new Thickness(4, 3),
            Spacing = 1,
            Children = {
                new TextBlock { Text = npc.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock {
                    Text = string.Join("  ·  ", new[] { npc.Kind, npc.World, npc.Id.ToString(CultureInfo.InvariantCulture) }.Where(t => t.Length > 0)),
                    Foreground = MUTED, FontSize = ManagerTheme.FONT_SMALL, TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock {
                    Text = (what.Count > 0 ? string.Join(", ", what) : "Nothing listed") + (off > 0 ? $"  ·  {off:N0} off" : ""),
                    Foreground = off > 0 ? WARN : MUTED, FontSize = ManagerTheme.FONT_SMALL, TextWrapping = TextWrapping.Wrap,
                },
            },
        };
    }

    private string NgName(string kind, uint id)
        => _ngNames.TryGetValue($"{kind}:{id}", out var e) ? e.Name : $"Unknown {kind.ToLowerInvariant()}";

    private void ShowNpcGives(NgNpc? npc) {
        _ngPicked = npc;
        _ngDetails.Children.Clear();
        if (npc is null) {
            _ngDetails.Children.Add(Sub("Click an NPC on the left to see everything it gives, each with a switch."));

            return;
        }

        var change = _ngChanges.GetValueOrDefault(npc.Id);
        _ngDetails.Children.Add(new TextBlock { Text = npc.Name, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        _ngDetails.Children.Add(Sub(string.Join("  ·  ", new[] { npc.Kind, npc.Place, $"ID {npc.Id}" }.Where(t => t.Length > 0))));
        _ngDetails.Children.Add(Sub("On: it gives it, as in the game. Off: it no longer does. Each switch saves at once."));

        var allOn = Plain("Turn everything back on");
        ToolTip.SetTip(allOn, "Switches every quest, item, card and spell of this NPC back on. Things you added or prices you set on Customs stay.");
        _ngDetails.Children.Add(Row(Action(allOn, () => NgAllOnAsync(npc))));

        void Heading(string text) => _ngDetails.Children.Add(new TextBlock {
            Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.Bold, Foreground = MUTED, Margin = new Thickness(0, 12, 0, 0),
        });

        // A switch for a whole part: off hides everything in it.
        void PartSwitch(string text, bool off, Action<NpcChange, bool> set) {
            var toggle = NgToggle(!off);
            toggle.IsCheckedChanged += (_, _) => {
                NgSave(npc, c => set(c, toggle.IsChecked != true), $"{text} for {npc.Name}: {(toggle.IsChecked == true ? "on" : "off")}");
                ShowNpcGives(npc);
            };
            _ngDetails.Children.Add(NgLine(text, "The whole part at once.", toggle, bold: true));
        }

        void ThingSwitch(string text, string detail, bool on, bool partOff, Action<NpcChange, bool> set) {
            var toggle = NgToggle(on);
            toggle.IsEnabled = !partOff;
            if (partOff) {
                ToolTip.SetTip(toggle, "The whole part is off. Switch the part back on first.");
            }

            toggle.IsCheckedChanged += (_, _) => NgSave(npc, c => set(c, toggle.IsChecked == true),
                $"{text} at {npc.Name}: {(toggle.IsChecked == true ? "on" : "off")}");
            _ngDetails.Children.Add(NgLine(text, detail, toggle));
        }

        if (npc.Quests.Count > 0) {
            Heading($"Quests it offers ({npc.Quests.Count:N0})");
            var hidden = change?.HideQuests == true;
            PartSwitch("Offers quests", hidden, (c, off) => c.HideQuests = off);
            foreach (var quest in npc.Quests) {
                var detail = $"{quest.Code}{(quest.Level > 0 ? $"  ·  level {quest.Level}" : "")}" +
                    (quest.OffForEveryone ? "  ·  off or skipped for everyone in the Quest Editor" : "");
                var on = change is null || !change.QuestsOff.Contains(quest.Code, StringComparer.OrdinalIgnoreCase);
                ThingSwitch(quest.Title, detail, on, hidden, (c, isOn) => {
                    c.QuestsOff.RemoveAll(q => string.Equals(q, quest.Code, StringComparison.OrdinalIgnoreCase));
                    if (!isOn) {
                        c.QuestsOff.Add(quest.Code);
                    }
                });
            }
        }

        if (npc.Vendor is { } vendor) {
            if (npc.ShopCount > 0) {
                Heading($"Sells ({npc.ShopCount:N0})");
                var hidden = change?.HideShop == true;
                PartSwitch("Has its shop", hidden, (c, off) => c.HideShop = off);
                foreach (var shop in vendor.Shops) {
                    if (vendor.Shops.Count > 1 || shop.Title.Length > 0) {
                        _ngDetails.Children.Add(Sub($"{shop.Window} shop{(shop.Title.Length > 0 ? $": {shop.Title}" : "")}"));
                    }

                    foreach (var item in shop.Items.Distinct()) {
                        var on = change is null || !change.RemoveItems.Contains(item);
                        ThingSwitch(NgName("Item", item), $"Item {item}", on, hidden, (c, isOn) => {
                            c.RemoveItems.Remove(item);
                            if (!isOn) {
                                c.RemoveItems.Add(item);
                            }
                        });
                    }

                    foreach (var recipe in shop.Recipes.Distinct(StringComparer.OrdinalIgnoreCase)) {
                        var on = change is null || !change.RemoveRecipes.Contains(recipe, StringComparer.OrdinalIgnoreCase);
                        ThingSwitch(recipe, "Recipe", on, hidden, (c, isOn) => {
                            c.RemoveRecipes.RemoveAll(r => string.Equals(r, recipe, StringComparison.OrdinalIgnoreCase));
                            if (!isOn) {
                                c.RemoveRecipes.Add(recipe);
                            }
                        });
                    }
                }
            }

            if (npc.CardCount > 0) {
                Heading($"Treasure cards ({npc.CardCount:N0})");
                var hidden = change?.HideTreasureCards == true;
                PartSwitch("Has its treasure card shop", hidden, (c, off) => c.HideTreasureCards = off);
                foreach (var card in vendor.TreasureCards.Distinct(StringComparer.OrdinalIgnoreCase)) {
                    var on = change is null || !change.RemoveTreasureCardNames.Contains(card, StringComparer.OrdinalIgnoreCase);
                    ThingSwitch(card, "Treasure card", on, hidden, (c, isOn) => {
                        c.RemoveTreasureCardNames.RemoveAll(n => string.Equals(n, card, StringComparison.OrdinalIgnoreCase));
                        if (!isOn) {
                            c.RemoveTreasureCardNames.Add(card);
                        }
                    });
                }
            }

            if (npc.SpellCount > 0) {
                Heading($"Teaches ({npc.SpellCount:N0})");
                var hidden = change?.HideTraining == true;
                PartSwitch("Has its spell list", hidden, (c, off) => c.HideTraining = off);
                foreach (var spell in vendor.Spells.OrderBy(s => s.Level).ThenBy(s => NgName("Spell", s.Id), StringComparer.OrdinalIgnoreCase)) {
                    var school = _ngNames.TryGetValue($"Spell:{spell.Id}", out var e) && e.School.Length > 0 ? $"{e.School}  ·  " : "";
                    var on = change is null || !change.RemoveSpells.Contains(spell.Id);
                    ThingSwitch(NgName("Spell", spell.Id), $"{school}Spell {spell.Id}{(spell.Level > 0 ? $"  ·  level {spell.Level}" : "")}", on, hidden,
                        (c, isOn) => {
                            c.RemoveSpells.Remove(spell.Id);
                            if (!isOn) {
                                c.RemoveSpells.Add(spell.Id);
                            }
                        });
                }
            }
        }

        if (npc.Quests.Count + npc.ShopCount + npc.CardCount + npc.SpellCount == 0) {
            _ngDetails.Children.Add(Sub("The lists have nothing for this NPC."));
        }
    }

    private static ToggleSwitch NgToggle(bool on)
        => new() { OnContent = "On", OffContent = "Off", IsChecked = on, VerticalAlignment = VerticalAlignment.Center };

    private static Control NgLine(string text, string detail, ToggleSwitch toggle, bool bold = false) => new DockPanel {
        Margin = new Thickness(0, 1),
        Children = {
            WithDock(toggle, Dock.Right),
            new StackPanel {
                Spacing = 0, VerticalAlignment = VerticalAlignment.Center,
                Children = {
                    new TextBlock { Text = text, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = detail, Foreground = MUTED, FontSize = ManagerTheme.FONT_SMALL, TextWrapping = TextWrapping.Wrap },
                },
            },
        },
    };

    // Reads the NPC's changes again (so ones made on Customs or another PC are kept), changes them and saves at once.
    private void NgSave(NgNpc npc, Action<NpcChange> set, string said) {
        try {
            var change = SafeNpcChanges().FirstOrDefault(c => c.TemplateID == npc.Id)?.Copy()
                ?? new NpcChange { TemplateID = npc.Id, Name = npc.Name };
            set(change);
            NpcChanges.Set(_manager.CustomsFolder, npc.Id, change.ChangesNothing ? null : change);
            _ngChanges = SafeNpcChanges().GroupBy(c => c.TemplateID).ToDictionary(g => g.Key, g => g.First());
            RenderNpcGives();
            Say($"{said}. It counts the next time a player talks to it.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            Say($"Could not save {npc.Name}'s switches: {ex.Message}", isError: true);
        }
    }

    private async Task NgAllOnAsync(NgNpc npc) {
        if (OffCount(npc) == 0) {
            Say($"Everything of {npc.Name} is on already.");

            return;
        }

        if (!await ConfirmAsync($"Switch everything {npc.Name} gives back on?")) {
            return;
        }

        NgSave(npc, c => {
            c.HideQuests = false;
            c.QuestsOff.Clear();
            c.HideShop = false;
            c.RemoveItems.Clear();
            c.RemoveRecipes.Clear();
            c.HideTreasureCards = false;
            c.RemoveTreasureCards.Clear();
            c.RemoveTreasureCardNames.Clear();
            c.HideTraining = false;
            c.RemoveSpells.Clear();
        }, $"Everything of {npc.Name} is on again");
        ShowNpcGives(npc);
    }

}
