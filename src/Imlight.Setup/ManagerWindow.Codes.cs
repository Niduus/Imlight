using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Imlight.Setup.Blocks;

namespace Imlight.Setup;

/// <summary>
/// The Codes card on the Server tab (Niduus, 2026-10-23: "Make a code section in the manager under server. This will
/// allow the creation of codes and their time limits."). Each code has its text, an optional start and end, the uses
/// per account, an on/off switch and what it gives: items picked by name, gold and crowns. Saved to codes.json next
/// to the server, which reads it again whenever it changes. Who redeemed what is in code-redemptions.json.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string CODES_FILE = "codes.json";
    private const string CODE_REDEMPTIONS_FILE = "code-redemptions.json";
    private const string CODE_TIME_FORMAT = "yyyy-MM-dd HH:mm";

    // The server's example code with no codes.json: one Hyper Elixir (ObjectData/Elixirs/Elixir-043.xml).
    private const string HYPER_ELIXIR_ID = "1441704";

    private sealed class CodeItemDraft {
        internal string ItemId = "";
        internal string ItemName = "";
        internal int Count = 1;
    }

    private sealed class CodeDraft {
        internal string Code = "";
        internal bool On = true;
        internal string Starts = "";
        internal string Ends = "";
        internal int Uses = 1;
        internal int Gold;
        internal int Crowns;
        internal string Note = "";
        internal List<CodeItemDraft> Items = [];
    }

    private readonly StackPanel _codeRows = new() { Spacing = 8 };
    private readonly TextBlock _codesNote = Sub("");
    private List<CodeDraft> _codes = [];

    private string CodesPath => Path.Combine(_manager.ServerFolder, CODES_FILE);
    private string CodeRedemptionsPath => Path.Combine(_manager.ServerFolder, CODE_REDEMPTIONS_FILE);

    private Control CodesCard() {
        LoadCodes();

        return Shut(Fold("gift", "Server", "Codes",
            "Codes players type in the game's promo code window, or in chat as .code HYPER, to get items, gold and crowns. " +
            "A code redeemed on the character select screen is given to the next wizard that enters the game. " +
            "Press Save codes after a change; the server uses it at once, no restart needed.",
            Row(Action(Primary("Save codes"), SaveCodesAsync), Action(Plain("Add a code"), () => {
                _codes.Add(new CodeDraft { Code = UniqueCodeName() });
                RenderCodes();

                return Task.CompletedTask;
            }), Action(Plain("Reload"), () => {
                LoadCodes();
                Say("Codes read again from codes.json.");

                return Task.CompletedTask;
            })),
            _codesNote,
            _codeRows));
    }

    private string UniqueCodeName() {
        for (var i = _codes.Count + 1; ; i++) {
            var name = $"NEWCODE{i}";
            if (!_codes.Any(c => c.Code.Equals(name, StringComparison.OrdinalIgnoreCase))) {
                return name;
            }
        }
    }

    private void LoadCodes() {
        _codes = [];
        try {
            if (!File.Exists(CodesPath)) {
                // What the server uses with no file.
                _codes.Add(new CodeDraft {
                    Code = "HYPER", Note = "Example code",
                    Items = [new CodeItemDraft { ItemId = HYPER_ELIXIR_ID, ItemName = "Hyper Elixir", Count = 1 }],
                });
            }
            else if (JsonNode.Parse(File.ReadAllText(CodesPath)) is JsonObject json) {
                foreach (var code in (json["Codes"] as JsonArray ?? []).OfType<JsonObject>()) {
                    _codes.Add(new CodeDraft {
                        Code = Text(code, "Code"),
                        On = Flag(code, "On") ?? true,
                        Starts = Text(code, "Starts"),
                        Ends = Text(code, "Ends"),
                        Uses = (int) (Number(code, "UsesPerAccount") ?? 1),
                        Gold = (int) (Number(code, "Gold") ?? 0),
                        Crowns = (int) (Number(code, "Crowns") ?? 0),
                        Note = Text(code, "Note"),
                        Items = [.. (code["Items"] as JsonArray ?? []).OfType<JsonObject>().Select(item => new CodeItemDraft {
                            ItemId = item["ItemId"]?.ToString() ?? "",
                            ItemName = Text(item, "ItemName"),
                            Count = (int) (Number(item, "Count") ?? 1),
                        })],
                    });
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) {
            Say($"Could not read the codes: {ex.Message}", isError: true);
        }

        RenderCodes();

        static string Text(JsonObject json, string key) => json[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
        static double? Number(JsonObject json, string key)
            => json[key] is JsonValue value
                ? value.TryGetValue<double>(out var number) ? number
                : value.TryGetValue<string>(out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null
                : null;
        static bool? Flag(JsonObject json, string key) => json[key] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;
    }

    /// <summary>How many accounts redeemed each code (upper case), from code-redemptions.json.</summary>
    private Dictionary<string, int> CodeUses() {
        var uses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try {
            if (File.Exists(CodeRedemptionsPath) && JsonNode.Parse(File.ReadAllText(CodeRedemptionsPath)) is JsonObject json) {
                foreach (var account in (json["Accounts"] as JsonObject ?? []).Select(a => a.Value).OfType<JsonObject>()) {
                    foreach (var used in account["Used"] as JsonObject ?? []) {
                        uses[used.Key] = uses.GetValueOrDefault(used.Key) + 1;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) {
            // The server may be writing it; the counts show next time.
        }

        return uses;
    }

    private void RenderCodes() {
        _codeRows.Children.Clear();
        var uses = CodeUses();
        _codesNote.Text = _codes.Count == 0
            ? "No codes yet. Press Add a code."
            : File.Exists(CodesPath) ? $"{_codes.Count} code(s) in {CODES_FILE}." : "This is the example code the server uses until you save codes here.";
        foreach (var code in _codes) {
            _codeRows.Children.Add(CodeRow(code, uses.GetValueOrDefault(code.Code.Trim())));
        }
    }

    private Control CodeRow(CodeDraft code, int redeemed) {
        var text = new TextBox { Text = code.Code, Width = 220, Watermark = "Such as HYPER" };
        text.TextChanged += (_, _) => code.Code = (text.Text ?? "").Trim();
        var on = new ToggleSwitch { OnContent = "On", OffContent = "Off", IsChecked = code.On };
        on.IsCheckedChanged += (_, _) => code.On = on.IsChecked == true;
        var starts = new TextBox { Text = code.Starts, Width = 170, Watermark = "Empty: from now" };
        starts.TextChanged += (_, _) => code.Starts = (starts.Text ?? "").Trim();
        var ends = new TextBox { Text = code.Ends, Width = 170, Watermark = "Empty: never ends" };
        ends.TextChanged += (_, _) => code.Ends = (ends.Text ?? "").Trim();
        var usesBox = CodeNumber(0, 1000, code.Uses);
        usesBox.ValueChanged += (_, _) => code.Uses = (int) (usesBox.Value ?? 1);
        var gold = CodeNumber(0, 10_000_000, code.Gold);
        gold.ValueChanged += (_, _) => code.Gold = (int) (gold.Value ?? 0);
        var crowns = CodeNumber(0, 10_000_000, code.Crowns);
        crowns.ValueChanged += (_, _) => code.Crowns = (int) (crowns.Value ?? 0);
        var note = new TextBox { Text = code.Note, Width = 360, Watermark = "A note for you; players do not see it" };
        note.TextChanged += (_, _) => code.Note = note.Text ?? "";

        var items = new StackPanel { Spacing = 2 };
        var search = new TextBox { Watermark = "Search an item by name or ID to add, such as Hyper Elixir", Width = 460, HorizontalAlignment = HorizontalAlignment.Left };
        var results = new StackPanel { Spacing = 2 };
        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };

        void RenderItems() {
            items.Children.Clear();
            if (code.Items.Count == 0) {
                items.Children.Add(Sub("No items. Search one below and press Add."));
            }

            foreach (var item in code.Items) {
                var count = CodeNumber(1, 999, item.Count);
                count.ValueChanged += (_, _) => item.Count = (int) (count.Value ?? 1);
                var remove = new Button { Padding = new Thickness(6, 4), Content = BlockIcons.Icon("trash", 14, new SolidColorBrush(Color.FromRgb(230, 110, 110))) };
                ToolTip.SetTip(remove, "Take this item off the code");
                remove.Click += (_, _) => {
                    code.Items.Remove(item);
                    RenderItems();
                };
                var catalog = CatalogItem(item.ItemId);
                items.Children.Add(new Border {
                    Padding = new Thickness(4, 4), BorderBrush = LINE, BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = new DockPanel {
                        Children = {
                            WithDock(Row(WithCenter(new TextBlock { Text = "How many", Foreground = MUTED }), count, remove), Dock.Right),
                            WithDock(new Border { Margin = new Thickness(0, 0, 10, 0), Child = DropIcon(item.ItemId) }, Dock.Left),
                            new StackPanel {
                                VerticalAlignment = VerticalAlignment.Center,
                                Children = {
                                    new TextBlock { Text = item.ItemName.Length > 0 ? item.ItemName : catalog?.Name ?? $"Item {item.ItemId}", FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                                    new TextBlock { Text = $"{DropKind(catalog)}  ·  ID {item.ItemId}", Foreground = MUTED, FontSize = 12 },
                                },
                            },
                        },
                    },
                });
            }
        }

        void Add(string id, string name) {
            if (code.Items.Any(i => i.ItemId == id)) {
                throw new PanelException($"{(name.Length > 0 ? name : id)} is already on this code. Change its count instead.");
            }

            code.Items.Add(new CodeItemDraft { ItemId = id, ItemName = name, Count = 1 });
            search.Text = "";
            results.Children.Clear();
            RenderItems();
        }

        void RenderResults() {
            results.Children.Clear();
            var query = (search.Text ?? "").Trim();
            if (query.Length == 0) {
                return;
            }

            var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var catalog = DropCatalog().Where(i => i.Kind == "Item").ToList();
            var found = query.All(char.IsDigit)
                ? catalog.Where(i => i.Id == query).ToList()
                : catalog.Where(i => words.All(i.Haystack.Contains))
                    .OrderBy(i => !i.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ThenBy(i => i.Name.Length).Take(8).ToList();
            if (found.Count == 0) {
                results.Children.Add(Sub(catalog.Count == 0
                    ? "The item list shows up once the server has made its ID list. You can still type an item's ID."
                    : "Nothing matches."));
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

        delay.Tick += (_, _) => {
            delay.Stop();
            RenderResults();
        };
        search.TextChanged += (_, _) => {
            delay.Stop();
            delay.Start();
        };
        RenderItems();

        var buttons = Row(Action(Danger(Plain("Delete the code")), async () => {
            if (await ConfirmAsync($"Delete the code {code.Code}? Press Save codes afterwards to make it stop working.")) {
                _codes.Remove(code);
                RenderCodes();
            }
        }));
        if (redeemed > 0) {
            buttons.Children.Add(Action(Plain("Forget who redeemed it"), async () => {
                if (await ConfirmAsync($"Let every account redeem {code.Code} again? ({redeemed} account(s) redeemed it.)")) {
                    ForgetCodeUses(code.Code.Trim());
                    Say($"Every account can redeem {code.Code} again.");
                    RenderCodes();
                }
            }));
        }

        return ManagerTheme.Item(new StackPanel {
            Spacing = 6,
            Children = {
                new TextBlock {
                    Text = (code.Code.Length > 0 ? code.Code : "New code") + (redeemed > 0 ? $"  ·  redeemed by {redeemed} account(s)" : ""),
                    FontWeight = FontWeight.SemiBold,
                },
                StatRow("Code", Row(text, on), "What players type. Upper or lower case does not matter. Off: it does not work."),
                StatRow("Starts", starts, $"From when it works, like {DateTime.Now:yyyy-MM-dd} 18:00 (the server's time). Empty: right away."),
                StatRow("Ends", ends, $"When it stops working, like {DateTime.Now.AddDays(7):yyyy-MM-dd} 23:59. Empty: it never ends."),
                StatRow("Uses per account", usesBox, "How many times one account can redeem it. 0: no limit."),
                StatRow("Gold", gold, "Gold for the wizard (up to the gold pouch)."),
                StatRow("Crowns", crowns, "Crowns for the account."),
                StatRow("Note", note, ""),
                new TextBlock { Text = "Items", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) },
                items,
                search,
                results,
                buttons,
            },
        });
    }

    private static NumericUpDown CodeNumber(decimal min, decimal max, int value)
        => new() { Minimum = min, Maximum = max, Increment = 1, FormatString = "0", Width = 150, Value = Math.Clamp(value, min, max) };

    private async Task SaveCodesAsync() {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new JsonArray();
        foreach (var code in _codes) {
            var name = code.Code.Trim();
            if (name.Length == 0) {
                throw new PanelException("Every code needs its text (what players type).");
            }

            if (name.Any(char.IsWhiteSpace)) {
                throw new PanelException($"\"{name}\": a code cannot have spaces.");
            }

            if (!seen.Add(name)) {
                throw new PanelException($"There are two codes named {name}. Rename or delete one.");
            }

            var starts = CodeTime(code.Starts, $"{name}: Starts");
            var ends = CodeTime(code.Ends, $"{name}: Ends");
            if (starts is not null && ends is not null && ends <= starts) {
                throw new PanelException($"{name}: it ends before it starts.");
            }

            var items = new JsonArray();
            foreach (var item in code.Items) {
                if (!ulong.TryParse(item.ItemId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) {
                    throw new PanelException($"{name}: {item.ItemId} is not an item ID.");
                }

                items.Add(new JsonObject { ["ItemId"] = id, ["ItemName"] = item.ItemName, ["Count"] = Math.Max(1, item.Count) });
            }

            list.Add(new JsonObject {
                ["Code"] = name,
                ["On"] = code.On,
                ["Starts"] = starts?.ToString(CODE_TIME_FORMAT, CultureInfo.InvariantCulture) ?? "",
                ["Ends"] = ends?.ToString(CODE_TIME_FORMAT, CultureInfo.InvariantCulture) ?? "",
                ["UsesPerAccount"] = Math.Max(0, code.Uses),
                ["Gold"] = Math.Max(0, code.Gold),
                ["Crowns"] = Math.Max(0, code.Crowns),
                ["Note"] = code.Note,
                ["Items"] = items,
            });
        }

        Directory.CreateDirectory(_manager.ServerFolder);
        var temp = $"{CodesPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temp, new JsonObject { ["Codes"] = list }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, CodesPath, overwrite: true);
        LoadCodes();
        Say($"Saved {_codes.Count} code(s). They work in the game right away.");
    }

    private static DateTime? CodeTime(string text, string label) {
        if (string.IsNullOrWhiteSpace(text)) {
            return null;
        }

        if (!DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var when)
            && !DateTime.TryParse(text.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out when)) {
            throw new PanelException($"{label}: write the date and time like {DateTime.Now:yyyy-MM-dd} 18:00, or leave it empty.");
        }

        return when;
    }

    private void ForgetCodeUses(string code) {
        if (!File.Exists(CodeRedemptionsPath) || JsonNode.Parse(File.ReadAllText(CodeRedemptionsPath)) is not JsonObject json) {
            return;
        }

        foreach (var account in (json["Accounts"] as JsonObject ?? []).Select(a => a.Value).OfType<JsonObject>()) {
            if (account["Used"] is JsonObject used) {
                foreach (var key in used.Select(u => u.Key).Where(k => k.Equals(code, StringComparison.OrdinalIgnoreCase)).ToList()) {
                    used.Remove(key);
                }
            }
        }

        var temp = $"{CodeRedemptionsPath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, CodeRedemptionsPath, overwrite: true);
    }

}
