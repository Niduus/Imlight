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
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Imlight.Setup.Blocks;

namespace Imlight.Setup;

/// <summary>A drop table file of spiraldb-extra/DropTables, as the list of the Drops section shows it.</summary>
internal sealed record DropTableInfo(string Name, string Description, string File, bool AppMade);

/// <summary>An item or spell a drop table can name, from the server's ID list (ids.csv).</summary>
internal sealed record DropCatalogItem(string Id, string Kind, string Name, string School, string Path) {

    internal string Haystack { get; } = $"{Name} {Id} {School}".ToLowerInvariant();

}

/// <summary>One line of a drop table: an item (or spell) and how it drops.</summary>
internal sealed class DropEntry {

    internal string ItemId { get; set; } = "";
    internal string ItemName { get; set; } = "";
    internal double Chance { get; set; } = 100;
    internal int Min { get; set; } = 1;
    internal int Max { get; set; } = 1;
    internal bool Guaranteed { get; set; }

    // The entry's other fields (Notes, Requirements), written back as they are.
    internal JsonObject Rest { get; set; } = [];

}

/// <summary>
/// A drop table being made or changed in the Drops section, in the format the server reads (DropTable.cs): gold, XP,
/// training points and items. Made in the app, it rolls each item on its own (EachItem): its chance, how many and
/// whether it always drops.
/// </summary>
internal sealed class DropDraft {

    internal string? File { get; set; }
    internal string Name { get; set; } = "";
    internal string OldName { get; set; } = "";
    internal string CopiedFrom { get; set; } = "";
    internal string Description { get; set; } = "";
    internal double RollChance { get; set; } = 100;
    internal bool EachItem { get; set; } = true;
    internal int MinGold { get; set; }
    internal int MaxGold { get; set; }
    internal int Experience { get; set; }
    internal int TrainingPoints { get; set; }
    internal List<DropEntry> Items { get; } = [];
    internal JsonObject Original { get; set; } = [];

    private static readonly JsonSerializerOptions s_write = new() { WriteIndented = true };

    internal static DropDraft Load(string file) {
        var json = JsonNode.Parse(System.IO.File.ReadAllText(file), documentOptions: new JsonDocumentOptions {
            AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip,
        }) as JsonObject ?? throw new PanelException($"{Path.GetFileName(file)} is not a drop table.");
        var draft = new DropDraft {
            File = file,
            Name = Text(json, "Name"),
            Description = Text(json, "Description"),
            RollChance = Math.Round((Number(json, "RollChance") ?? 1) * 100, 2),
            EachItem = BossChanges.FindIn(json, "EachItem") is JsonValue each && each.TryGetValue<bool>(out var e) && e,
            MinGold = (int) (Number(json, "MinGold") ?? 0),
            MaxGold = (int) (Number(json, "MaxGold") ?? 0),
            Experience = (int) (Number(json, "ExperienceAmount") ?? 0),
            TrainingPoints = (int) (Number(json, "TrainingPoints") ?? 0),
            Original = json,
        };
        draft.OldName = draft.Name;
        if (BossChanges.FindIn(json, "Items") is JsonArray items) {
            foreach (var item in items.OfType<JsonObject>()) {
                var rest = (JsonObject) item.DeepClone();
                foreach (var key in new[] { "ItemId", "ItemName", "Chance", "MinCount", "MaxCount", "Guaranteed" }) {
                    if (rest.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Key is { } found) {
                        rest.Remove(found);
                    }
                }

                var min = (int) (Number(item, "MinCount") ?? 1);
                draft.Items.Add(new DropEntry {
                    ItemId = Text(item, "ItemId"),
                    ItemName = Text(item, "ItemName"),
                    Chance = Number(item, "Chance") ?? 100,
                    Min = min,
                    Max = (int) (Number(item, "MaxCount") ?? min),
                    Guaranteed = BossChanges.FindIn(item, "Guaranteed") is JsonValue g && g.TryGetValue<bool>(out var gv) && gv,
                    Rest = rest,
                });
            }
        }

        return draft;
    }

    /// <summary>Writes the table where the server reads it; a new table gets a file named after it.</summary>
    internal void Save(string folder) {
        var json = (JsonObject) Original.DeepClone();
        void Put(string key, JsonNode? value) {
            if (json.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Key is { } found) {
                json.Remove(found);
            }

            json[key] = value;
        }

        Put("Name", Name);
        Put("Description", Description);
        Put("RollChance", Math.Round(RollChance / 100, 4));
        Put("MinGold", MinGold);
        Put("MaxGold", MaxGold);
        Put("ExperienceAmount", Experience);
        Put("TrainingPoints", TrainingPoints);
        Put("EachItem", EachItem);
        Put("MadeInApp", true);
        if (CopiedFrom.Length > 0) {
            Put("CopiedFrom", CopiedFrom);
        }

        Put("Items", new JsonArray([.. Items.Select(item => {
            var line = new JsonObject { ["ItemId"] = item.ItemId, ["ItemName"] = item.ItemName };
            if (EachItem) {
                line["Chance"] = item.Chance;
                line["MinCount"] = item.Min;
                line["MaxCount"] = item.Max;
                line["Guaranteed"] = item.Guaranteed;
            }

            foreach (var (key, node) in item.Rest) {
                line[key] = node?.DeepClone();
            }

            return (JsonNode) line;
        })]));

        Directory.CreateDirectory(folder);
        File ??= Path.Combine(folder, FileName(folder, Name));
        var temp = File + ".saving";
        System.IO.File.WriteAllText(temp, json.ToJsonString(s_write));
        System.IO.File.Move(temp, File, overwrite: true);
    }

    /// <summary>A file name for a new table, such as my-boss-gear.json, that no other table has.</summary>
    private static string FileName(string folder, string name) {
        var slug = new StringBuilder();
        foreach (var c in name.ToLowerInvariant()) {
            slug.Append(char.IsLetterOrDigit(c) ? c : '-');
        }

        var stem = slug.ToString().Trim('-');
        stem = stem.Length == 0 ? "drop-table" : stem;
        var file = stem + ".json";
        for (var n = 2; System.IO.File.Exists(Path.Combine(folder, file)); n++) {
            file = $"{stem}-{n}.json";
        }

        return file;
    }

    private static string Text(JsonObject json, string key) => (BossChanges.FindIn(json, key) as JsonValue)?.ToString() ?? "";

    private static double? Number(JsonObject json, string key)
        => BossChanges.FindIn(json, key) is JsonValue value
            ? value.TryGetValue<double>(out var n) ? n
            : double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null
            : null;

}

/// <summary>
/// Drop tables of one's own (Niduus, 2026-10-22: "Include custom drop tables, including XP and Gold counts"): made, changed
/// and copied in the editor's Drops section, saved to spiraldb-extra/DropTables, where the server reads them from the
/// next battle on (DropTableFiles).
/// </summary>
internal sealed partial class ManagerWindow {

    private readonly Border _dropEditorHost = new();
    private readonly Dictionary<string, Bitmap?> _itemIcons = new(StringComparer.OrdinalIgnoreCase);
    private List<DropCatalogItem>? _dropCatalog;
    private DateTime _dropCatalogFrom;
    private DropDraft? _dropDraft;

    private string DropTablesFolder => Path.Combine(_manager.ExtraFolder, "DropTables");

    /// <summary>The items and spells of the server's ID list, read again when it changes.</summary>
    private List<DropCatalogItem> DropCatalog() {
        var file = Path.Combine(_manager.IdLookupFolder, "ids.csv");
        var written = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
        if (_dropCatalog is not null && written == _dropCatalogFrom) {
            return _dropCatalog;
        }

        _dropCatalog = [];
        _dropCatalogFrom = written;
        try {
            if (File.Exists(file)) {
                foreach (var fields in Lookup.ReadCsv(file)) {
                    if (fields.Count >= 3 && fields[1] is "Item" or "Spell" && fields[0].All(char.IsDigit)) {
                        _dropCatalog.Add(new DropCatalogItem(fields[0], fields[1], fields[2], fields.Count > 3 ? fields[3] : "",
                            fields.Count > 5 ? fields[5] : ""));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Searching by ID still works.
        }

        return _dropCatalog;
    }

    private DropCatalogItem? CatalogItem(string id) => DropCatalog().FirstOrDefault(i => i.Id == id);

    /// <summary>What a line of a table is, in words: an item, a treasure card or a spell it teaches.</summary>
    private string DropKind(DropCatalogItem? item) => item?.Kind switch {
        "Spell" => CardOf(uint.TryParse(item.Id, out var id) ? id : 0) is { } card && !card.Treasure ? "Spell it teaches" : "Treasure card",
        "Item" => item.School is { Length: > 0 } school && school != "None" ? $"Item · {school}" : "Item",
        _ => "Not in the ID list",
    };

    /// <summary>The game's picture of an item or a spell's card, or a drawn bag until it is found.</summary>
    private Control DropIcon(string id, double size = 34) {
        var item = CatalogItem(id);
        if (item?.Kind == "Spell" && uint.TryParse(id, out var spellId)) {
            return new Border { Width = size, Height = size, Child = SpellThumb(spellId, item.School, size * 0.72) };
        }

        var host = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(6), ClipToBounds = true };
        host.Child = BlockIcons.Tile("bag", BlockColors.Accent("drops"), size);
        if (item is null || item.Path.Length == 0 || _spellBook is not { } book) {
            return host;
        }

        if (_itemIcons.TryGetValue(id, out var cached)) {
            if (cached is not null) {
                host.Child = new Image { Source = cached, Stretch = Stretch.Uniform };
            }

            return host;
        }

        _ = Task.Run(() => book.ItemIcon(item.Path)).ContinueWith(t => Dispatcher.UIThread.Post(() => {
            var bitmap = t.Status == TaskStatus.RanToCompletion && t.Result is { } image ? ToBitmap(image) : null;
            _itemIcons[id] = bitmap;
            if (bitmap is not null) {
                host.Child = new Image { Source = bitmap, Stretch = Stretch.Uniform };
            }
        }), TaskScheduler.Default);

        return host;
    }

    /// <summary>Opens a table in the editor: an app-made one as it is, any other as a copy (its file stays as it is).</summary>
    private void EditDropTable(DropTableInfo table) {
        var draft = DropDraft.Load(table.File);
        if (!table.AppMade) {
            draft.CopiedFrom = draft.Name;
            draft.Name = UniqueTableName(draft.Name + "-Copy");
            draft.File = null;
            Say($"{draft.CopiedFrom} is one of the server's tables, so you are changing a copy named {draft.Name}. Save the table to keep it.");
        }

        OpenDropEditor(draft);
    }

    private string UniqueTableName(string name) {
        var taken = DropTables().Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unique = name;
        for (var n = 2; taken.Contains(unique); n++) {
            unique = $"{name}-{n}";
        }

        return unique;
    }

    private void NewDropTable() {
        var shown = _editCustom?.Shown ?? _boss?.Name ?? "My";
        OpenDropEditor(new DropDraft {
            Name = UniqueTableName($"{shown.Replace(' ', '-')}-Drops"),
            Description = $"Drops of {shown}, made in the app.",
        });
    }

    private void OpenDropEditor(DropDraft draft) {
        _dropDraft = draft;
        _dropEditorHost.Child = DropEditor(draft);
        _dropEditorHost.BringIntoView();
    }

    private void CloseDropEditor() {
        _dropDraft = null;
        _dropEditorHost.Child = null;
    }

    private Control DropEditor(DropDraft draft) {
        var name = new TextBox { Text = draft.Name, Width = 360, Watermark = "Such as Malistaire-Unbroken-Gear" };
        var description = new TextBox { Text = draft.Description, Width = 360, Watermark = "A note for you" };
        var roll = new TextBox { Text = Num(draft.RollChance), Width = 90 };
        var mode = new ComboBox { Width = 300, ItemsSource = new[] { "Each item by its own chance", "One item, picked at random" } };
        mode.SelectedIndex = draft.EachItem ? 0 : 1;
        var minGold = new TextBox { Text = draft.MinGold.ToString(CultureInfo.InvariantCulture), Width = 100 };
        var maxGold = new TextBox { Text = draft.MaxGold.ToString(CultureInfo.InvariantCulture), Width = 100 };
        var xp = new TextBox { Text = draft.Experience.ToString(CultureInfo.InvariantCulture), Width = 100 };
        var tp = new TextBox { Text = draft.TrainingPoints.ToString(CultureInfo.InvariantCulture), Width = 100 };
        var rows = new StackPanel { Spacing = 4 };
        var search = new TextBox { Watermark = "Search an item, treasure card or spell by name or ID, such as Ghost Hat or 730267", Width = 520, HorizontalAlignment = HorizontalAlignment.Left };
        var results = new StackPanel { Spacing = 2 };
        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };

        void RenderRows() {
            rows.Children.Clear();
            if (draft.Items.Count == 0) {
                rows.Children.Add(Sub("No items yet. Search one below and press Add. A table can give only gold and XP too."));
            }

            foreach (var entry in draft.Items) {
                rows.Children.Add(DropEntryRow(draft, entry, RenderRows));
            }
        }

        mode.SelectionChanged += (_, _) => {
            draft.EachItem = mode.SelectedIndex == 0;
            RenderRows();
        };
        delay.Tick += (_, _) => {
            delay.Stop();
            RenderItemResults(draft, search, results, RenderRows);
        };
        search.TextChanged += (_, _) => {
            delay.Stop();
            delay.Start();
        };
        RenderRows();

        void Collect() {
            var newName = (name.Text ?? "").Trim();
            if (newName.Length == 0) {
                throw new PanelException("Give the drop table a name. Creatures drop it by its name.");
            }

            if (DropTables().Any(t => t.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)
                                      && !string.Equals(t.File, draft.File, StringComparison.OrdinalIgnoreCase))) {
                throw new PanelException($"There is already a drop table named {newName}. Pick another name.");
            }

            draft.Name = newName;
            draft.Description = (description.Text ?? "").Trim();
            draft.RollChance = Percent(roll, "Chance it drops") ?? 100;
            draft.MinGold = WholeNumber(minGold, "Gold from", 0, 10_000_000) ?? 0;
            draft.MaxGold = WholeNumber(maxGold, "Gold to", 0, 10_000_000) ?? draft.MinGold;
            if (draft.MaxGold < draft.MinGold) {
                throw new PanelException("The second gold number is the most it gives, so it cannot be less than the first.");
            }

            draft.Experience = WholeNumber(xp, "XP", 0, 100_000_000) ?? 0;
            draft.TrainingPoints = WholeNumber(tp, "Training points", 0, 1000) ?? 0;
            if (draft.Items.FirstOrDefault(i => i.Max < i.Min) is { } wrong) {
                throw new PanelException($"{wrong.ItemName}: the most that drop cannot be less than the fewest.");
            }
        }

        async Task SaveAsync(bool useHere) {
            Collect();
            draft.Save(DropTablesFolder);
            _dropTables = null;
            var drops = CustomDropNames();
            var renamed = draft.OldName.Length > 0 && !draft.OldName.Equals(draft.Name, StringComparison.OrdinalIgnoreCase);
            var swapped = false;
            for (var i = 0; i < drops.Count; i++) {
                if ((renamed && drops[i].Equals(draft.OldName, StringComparison.OrdinalIgnoreCase))
                    || (draft.CopiedFrom.Length > 0 && useHere && drops[i].Equals(draft.CopiedFrom, StringComparison.OrdinalIgnoreCase))) {
                    drops[i] = draft.Name;
                    swapped = true;
                }
            }

            if (useHere && !drops.Contains(draft.Name, StringComparer.OrdinalIgnoreCase)) {
                drops.Add(draft.Name);
                swapped = true;
            }

            draft.OldName = draft.Name;
            draft.CopiedFrom = "";
            _customDrops.Text = string.Join(", ", drops.Distinct(StringComparer.OrdinalIgnoreCase));
            RenderDropChips();
            OpenDropEditor(draft);
            Say($"Saved the drop table {draft.Name} ({Path.GetFileName(draft.File)}). It counts from the next battle." +
                (swapped ? " Press Save changes at the bottom so this creature drops it." : ""));
            await Task.CompletedTask;
        }

        var buttons = Row(
            Action(Primary("Save the table"), () => SaveAsync(false)),
            Action(Plain("Save and drop it here"), () => SaveAsync(true)),
            Action(Plain("Close"), () => {
                CloseDropEditor();

                return Task.CompletedTask;
            }));
        if (draft.File is { } file && DropTables().FirstOrDefault(t => string.Equals(t.File, file, StringComparison.OrdinalIgnoreCase)) is { AppMade: true } made) {
            buttons.Children.Add(Danger(Action(Plain("Delete the table"), async () => {
                if (!await ConfirmAsync($"Delete the drop table {made.Name}? Creatures that drop it stop dropping it.")) {
                    return;
                }

                File.Delete(made.File);
                _dropTables = null;
                _customDrops.Text = string.Join(", ", CustomDropNames().Where(d => !d.Equals(made.Name, StringComparison.OrdinalIgnoreCase)));
                CloseDropEditor();
                Say($"Deleted the drop table {made.Name}.");
            })));
        }

        var title = draft.File is null ? (draft.CopiedFrom.Length > 0 ? $"A copy of {draft.CopiedFrom}" : "New drop table") : $"Drop table {draft.Name}";
        var where = draft.File is null ? "It is saved to spiraldb-extra/DropTables when you press Save the table."
            : $"Saved in spiraldb-extra/DropTables/{Path.GetFileName(draft.File)}.";
        // A Wizard404 gauntlet table takes its chance from DropOdds.json, so its own chance box only counts without one.
        if (BossChanges.FindIn(draft.Original, "Odds") is JsonValue odds && odds.ToString() is { Length: > 0 } oddsKey) {
            where += $" Its chance comes from spiraldb-extra/DropOdds.json ({oddsKey}); the chance here counts only if that file has no {oddsKey}.";
        }

        return new Border {
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(28, 201, 162, 39)),
            BorderBrush = BlockColors.Accent("drops"),
            BorderThickness = new Thickness(1),
            Child = new StackPanel {
                Spacing = 8,
                Children = {
                    new DockPanel {
                        Children = {
                            WithDock(new Border { Margin = new Thickness(0, 0, 12, 0), Child = BlockIcons.Tile("bag", BlockColors.Accent("drops"), 34) }, Dock.Left),
                            new StackPanel {
                                Children = {
                                    new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold },
                                    Sub("A drop table is rolled once for each wizard in every battle won against a creature that drops it. " + where),
                                },
                            },
                        },
                    },
                    StatRow("Name", name, "Its name. Creatures drop it by this name."),
                    StatRow("Description", description, "A note for you; the game does not show it."),
                    StatRow("Chance it drops", WithUnit(roll, "%"), "The chance this table gives anything in a won battle. 100 means every battle."),
                    StatRow("Gold", Row(WithUnit(minGold, "to"), maxGold), "A random amount from the first number to the second. 0 and 0: no gold from this table."),
                    StatRow("XP", xp, "Experience it gives each wizard."),
                    StatRow("Training points", tp, "Training points it gives each wizard. Usually 0."),
                    StatRow("How items drop", mode, "Each item by its own chance: every line rolls on its own. One at random: one item of the list, as the server's own tables do."),
                    WithMargin(new TextBlock { Text = "Items, treasure cards and spells", FontSize = 15, FontWeight = FontWeight.SemiBold }, new Thickness(0, 8, 0, 0)),
                    Sub("Treasure cards go to the treasure book; other spells are taught. Guaranteed lines always drop."),
                    rows,
                    search,
                    results,
                    WithMargin(buttons, new Thickness(0, 8, 0, 0)),
                },
            },
        };
    }

    private static Control WithUnit(Control box, string unit) => new StackPanel {
        Orientation = Orientation.Horizontal, Spacing = 6,
        Children = { box, new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center, Foreground = MUTED } },
    };

    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static double? Percent(TextBox box, string label) {
        var text = (box.Text ?? "").Trim().TrimEnd('%').Trim();
        if (text.Length == 0) {
            return null;
        }

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0 || value > 100) {
            throw new PanelException($"{label} must be a percent from 0 to 100.");
        }

        return value;
    }

    /// <summary>One line of the table: its picture, name and kind, and (each by its own chance) chance, count and guaranteed.</summary>
    private Control DropEntryRow(DropDraft draft, DropEntry entry, Action changed) {
        var item = CatalogItem(entry.ItemId);
        var chance = new TextBox { Text = Num(entry.Chance), Width = 72, IsEnabled = !entry.Guaranteed };
        chance.TextChanged += (_, _) => {
            if (double.TryParse((chance.Text ?? "").TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) {
                entry.Chance = Math.Clamp(v, 0, 100);
            }
        };
        var min = new NumericUpDown { Minimum = 1, Maximum = 999, Increment = 1, FormatString = "0", Width = 110, Value = entry.Min };
        var max = new NumericUpDown { Minimum = 1, Maximum = 999, Increment = 1, FormatString = "0", Width = 110, Value = entry.Max };
        min.ValueChanged += (_, _) => {
            entry.Min = (int) (min.Value ?? 1);
            if (max.Value < min.Value) {
                max.Value = min.Value;
            }
        };
        max.ValueChanged += (_, _) => entry.Max = (int) (max.Value ?? 1);
        var guaranteed = new CheckBox { Content = "Guaranteed", IsChecked = entry.Guaranteed };
        ToolTip.SetTip(guaranteed, "It always drops, whatever its chance.");
        guaranteed.IsCheckedChanged += (_, _) => {
            entry.Guaranteed = guaranteed.IsChecked == true;
            chance.IsEnabled = !entry.Guaranteed;
        };
        var remove = new Button { Padding = new Thickness(6, 4), Content = BlockIcons.Icon("trash", 14, new SolidColorBrush(Color.FromRgb(230, 110, 110))) };
        ToolTip.SetTip(remove, "Take this line off the table");
        remove.Click += (_, _) => {
            draft.Items.Remove(entry);
            changed();
        };

        var settings = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (draft.EachItem) {
            settings.Children.Add(WithCenter(new TextBlock { Text = "Chance", Foreground = MUTED }));
            settings.Children.Add(WithUnit(chance, "%"));
            settings.Children.Add(WithCenter(new TextBlock { Text = "Count", Foreground = MUTED }));
            settings.Children.Add(min);
            settings.Children.Add(WithCenter(new TextBlock { Text = "to", Foreground = MUTED }));
            settings.Children.Add(max);
            settings.Children.Add(guaranteed);
        }

        settings.Children.Add(remove);

        return new Border {
            Padding = new Thickness(4, 4),
            BorderBrush = LINE,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new DockPanel {
                Children = {
                    WithDock(settings, Dock.Right),
                    WithDock(new Border { Margin = new Thickness(0, 0, 10, 0), Child = DropIcon(entry.ItemId) }, Dock.Left),
                    new StackPanel {
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = {
                            new TextBlock { Text = entry.ItemName.Length > 0 ? entry.ItemName : item?.Name ?? $"Item {entry.ItemId}", FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                            new TextBlock { Text = $"{DropKind(item)}  ·  ID {entry.ItemId}", Foreground = MUTED, FontSize = 12 },
                        },
                    },
                },
            },
        };
    }

    private void RenderItemResults(DropDraft draft, TextBox search, StackPanel results, Action changed) {
        results.Children.Clear();
        var query = (search.Text ?? "").Trim();
        if (query.Length == 0) {
            return;
        }

        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var catalog = DropCatalog();
        var found = query.All(char.IsDigit)
            ? catalog.Where(i => i.Id == query).ToList()
            : catalog.Where(i => words.All(i.Haystack.Contains))
                .OrderBy(i => !i.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ThenBy(i => i.Name.Length).Take(8).ToList();
        if (found.Count == 0) {
            results.Children.Add(Sub(catalog.Count == 0
                ? "The item list shows up once the server has made its ID list. You can still type an item's ID."
                : "Nothing matches."));
        }

        void Add(string id, string itemName) {
            if (draft.Items.Any(i => i.ItemId == id)) {
                throw new PanelException($"{itemName} is already in the table. Change its line instead.");
            }

            draft.Items.Add(new DropEntry { ItemId = id, ItemName = itemName, Chance = draft.EachItem ? 25 : 100 });
            changed();
        }

        foreach (var item in found) {
            results.Children.Add(new DockPanel {
                Children = {
                    WithDock(Action(Plain("Add"), () => {
                        Add(item.Id, item.Name);

                        return Task.CompletedTask;
                    }), Dock.Right),
                    WithDock(new Border { Margin = new Thickness(0, 0, 10, 0), Child = DropIcon(item.Id, 28) }, Dock.Left),
                    WithCenter(new TextBlock { Text = $"{item.Name}  ·  {DropKind(item)}  ·  ID {item.Id}" }),
                },
            });
        }

        if (found.Count == 0 && query.All(char.IsDigit) && query.Length > 3) {
            results.Children.Add(Row(Action(Plain($"Add ID {query} anyway"), () => {
                Add(query, "");

                return Task.CompletedTask;
            })));
        }
    }

}
