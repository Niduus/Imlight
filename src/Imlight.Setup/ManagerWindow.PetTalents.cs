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
/// The Database's Pet Talents and Pet Derby tabs (Niduus, 2026-10-27: "add to database pet talents and derby. Allow for
/// the ability to use the .pet commands as well easily in that UI"): every pet talent (May Casts, Will Casts, cards, stat
/// talents, pet powers) and every derby racing talent, with its rarity, what it does and which pets can have it, listed in
/// full when nothing is typed. The server writes the list (pet-talents.json in the ID lookup folder). Beside the list,
/// buttons run the .pet commands on the pet the picked wizard wears, and a picked talent can go straight into a slot.
/// Pet Derby races themselves are not built on the server, so the derby tab is for looking and for setting talents.
/// </summary>
internal sealed partial class ManagerWindow {

    private const string PET_TALENTS_FILE = "pet-talents.json";

    private sealed record PetTalentPet(uint Id, string Name, string Object);

    private sealed record PetTalentInfo(string Name, string Shown, string Description, string Kind, int Rank, bool Derby,
        bool Retired, bool Hidden, string BreedsAs, List<string> Effects, List<string> Stats, string Card, uint CardId,
        string Buff, float Cooldown, List<uint> Pets, List<uint> Sure) {
        internal string Title => Shown.Length > 0 ? Shown : Name;
    }

    private sealed record PetTalentFile(List<PetTalentPet> Pets, List<PetTalentInfo> Talents);

    private sealed record PetTalentRow(PetTalentInfo Talent, string Haystack, string PetNames);

    private static readonly string[] s_petRarities = ["", "Common", "Uncommon", "Rare", "Ultra-Rare", "Epic"];
    private static readonly string[] s_petStats = ["Strength", "Intellect", "Agility", "Will", "Power", "Happiness"];
    private static readonly string[] s_petLevels = ["Baby", "Teen", "Adult", "Ancient", "Epic", "Mega", "Ultra"];
    private static readonly string[] s_petTalentKinds = [ANY_FILTER, "May Cast", "Will Cast", "Gives a card", "Wizard stat", "Pet stat", "Pet power", "Extra XP", "Other"];
    private static readonly string[] s_petTalentSorts = ["Name", "Rarity (rarest first)", "Rarity (commonest first)", "Most pets first", "Fewest pets first", "Kind", "Talent name in the files"];
    private static readonly string[] s_petDerbySorts = ["Name", "Rarity (rarest first)", "Rarity (commonest first)", "Most pets first", "Fewest pets first", "Shortest wait first", "Talent name in the files"];

    /// <summary>One of the two tabs: its own search, filters, list, details and wizard picker.</summary>
    private sealed class PetTalentPage(bool derby) {
        internal bool Derby { get; } = derby;
        internal TextBox Search { get; } = new() {
            Watermark = derby ? "Search a derby talent, what it does, or a pet that has it, such as Banana or Sprint"
                : "Search a talent, what it does, the card it casts, or a pet that has it, such as Spritely, Pain-Giver or Fire Cat",
        };
        internal ComboBox Kind { get; } = FilterCombo(derby ? [ANY_FILTER, "Buff", "Debuff", "Neither"] : s_petTalentKinds, 150);
        internal ComboBox Rarity { get; } = FilterCombo([ANY_FILTER, .. s_petRarities.Skip(1)], 130);
        internal ComboBox Stat { get; } = FilterCombo([ANY_FILTER, .. s_petStats.Take(5)], 130);
        internal TextBox Does { get; } = new() { Watermark = ANY_FILTER, Width = 180 };
        internal TextBox Pet { get; } = new() { Watermark = ANY_FILTER, Width = 180 };
        internal TextBox Card { get; } = new() { Watermark = ANY_FILTER, Width = 180 };
        internal NumericUpDown PetsMin { get; } = FilterNumber(10);
        internal NumericUpDown PetsMax { get; } = FilterNumber(10);
        internal NumericUpDown WaitMin { get; } = FilterNumber(5);
        internal NumericUpDown WaitMax { get; } = FilterNumber(5);
        internal ComboBox Sort { get; } = FilterCombo(derby ? s_petDerbySorts : s_petTalentSorts, 170);
        internal CheckBox HideOld { get; } = new() { Content = "Hide retired and hidden ones", IsChecked = true };
        internal CheckBox SureOnly { get; } = new() { Content = "Only ones some pet always gets" };
        internal CheckBox OnPets { get; } = new() { Content = "Only ones a pet can have" };
        internal TextBlock Count { get; } = new() { TextWrapping = TextWrapping.Wrap };
        internal ListBox List { get; } = new() { Background = Brushes.Transparent };
        internal StackPanel Details { get; } = new() { Spacing = 6 };
        internal DetailsPopup? Popup { get; set; }
        internal PetTalentInfo? Picked { get; set; }
        internal ComboBox Wizard { get; } = new() { MinWidth = 220, PlaceholderText = "Pick a wizard" };
    }

    private readonly PetTalentPage _petTalentPage = new(derby: false);
    private readonly PetTalentPage _petDerbyPage = new(derby: true);
    private List<PetTalentRow> _petTalentRows = [];
    private Dictionary<uint, PetTalentPet> _petTalentPets = [];
    private DateTime _petTalentsWritten;

    private string PetTalentsPath => Path.Combine(_manager.IdLookupFolder, PET_TALENTS_FILE);

    private Control PetTalentsTab() => PetTalentTab(_petTalentPage);

    private Control PetDerbyTab() => PetTalentTab(_petDerbyPage);

    private Control PetTalentTab(PetTalentPage page) {
        page.List.ItemTemplate = new FuncDataTemplate<PetTalentRow>((row, _) => row is null ? new Panel() : PetTalentRowView(row));
        page.List.SelectionChanged += (_, e) => {
            if (e.Source == page.List && page.List.SelectedItem is PetTalentRow row && row.Talent != page.Picked) {
                ShowPetTalent(page, row.Talent);
            }
        };
        WhenFiltersChange(() => RenderPetTalents(page), page.Search, page.Kind, page.Rarity, page.Stat, page.Does, page.Pet,
            page.Card, page.PetsMin, page.PetsMax, page.WaitMin, page.WaitMax, page.Sort, page.HideOld, page.SureOnly, page.OnPets);

        var filters = page.Derby
            ? SearchFilters(
                SearchFilter("Helps or hinders", page.Kind),
                SearchFilter("Rarity", page.Rarity),
                SearchFilter("Does something like", page.Does),
                SearchFilter("A pet that has it", page.Pet),
                SearchFilter("Wait between uses from (seconds)", page.WaitMin),
                SearchFilter("Wait to (seconds)", page.WaitMax),
                SearchFilter("How many pets, from", page.PetsMin),
                SearchFilter("How many pets, to", page.PetsMax),
                SearchFilter("Sort by", page.Sort),
                SearchTick(page.HideOld),
                SearchTick(page.OnPets),
                SearchTick(page.SureOnly))
            : SearchFilters(
                SearchFilter("Kind", page.Kind),
                SearchFilter("Rarity", page.Rarity),
                SearchFilter("Grows with pet stat", page.Stat),
                SearchFilter("Does something like", page.Does),
                SearchFilter("Card it casts or gives", page.Card),
                SearchFilter("A pet that has it", page.Pet),
                SearchFilter("How many pets, from", page.PetsMin),
                SearchFilter("How many pets, to", page.PetsMax),
                SearchFilter("Sort by", page.Sort),
                SearchTick(page.HideOld),
                SearchTick(page.OnPets),
                SearchTick(page.SureOnly));

        var details = new ScrollViewer { Width = 640, Content = page.Details };
        page.Popup = new DetailsPopup(page.Derby ? "Derby talent" : "Pet talent", details,
            () => { page.List.SelectedItem = null; ShowPetTalent(page, null); }, by => StepList(page.List, by));
        var commands = new ScrollViewer { Width = 380, Margin = new Thickness(14, 0, 0, 0), Content = PetCommandsCard(page) };
        ShowPetTalent(page, null);

        return new Panel {
            Children = {
                new DockPanel {
                    Margin = new Thickness(24, 12, 24, 16),
                    Children = {
                        WithDock(commands, Dock.Right),
                        WithDock(Fold(page.Derby ? "bolt" : "star", page.Derby ? "Pet Derby" : "Pet Talents",
                            page.Derby ? "Find derby talents" : "Find pet talents",
                            page.Derby
                                ? "Every racing talent of the game files. Pet Derby races aren't on this server yet, but your pet can still get these talents."
                                : "Search or filter, then click a talent to see what it does, which pets can have it, and to put it on your pet.",
                            WithHint(page.Search, page.Derby
                                ? "Search by the talent's name, what it does (speed, morale, lane, obstacle) or a pet's name. With nothing typed, every " +
                                  "derby talent shows. Click one to see it and to put it in one of your pet's derby slots."
                                : "Search by the talent's name, what it does, the card it casts or gives, or a pet's name. With nothing typed, every " +
                                  "talent shows. Click one to see it and to put it in one of your pet's talent slots."),
                            filters,
                            page.Count), Dock.Top),
                        WithMargin(page.List, new Thickness(0, 14, 0, 0)),
                    },
                },
                page.Popup,
            },
        };
    }

    /// <summary>Reads the list again when the server has written it since. Runs when either tab shows.</summary>
    private async Task LoadPetTalentsAsync(PetTalentPage page) {
        FillPetWizards(page);
        var path = PetTalentsPath;
        try {
            if (!File.Exists(path)) {
                (_petTalentRows, _petTalentPets, _petTalentsWritten) = ([], [], default);
                RenderPetTalents(page);

                return;
            }

            var written = File.GetLastWriteTimeUtc(path);
            if (written != _petTalentsWritten) {
                page.Count.Text = "Loading…";
                var (rows, pets) = await Task.Run(() => ReadPetTalents(path));
                (_petTalentRows, _petTalentPets, _petTalentsWritten) = (rows, pets, written);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            page.Count.Text = $"Could not read {PET_TALENTS_FILE}: {ex.Message}";

            return;
        }

        RenderPetTalents(page);
    }

    private static (List<PetTalentRow> Rows, Dictionary<uint, PetTalentPet> Pets) ReadPetTalents(string path) {
        var file = JsonSerializer.Deserialize<PetTalentFile>(File.ReadAllText(path));
        var pets = (file?.Pets ?? []).Where(p => p is not null).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        var rows = new List<PetTalentRow>();
        foreach (var raw in file?.Talents ?? []) {
            if (raw is null) {
                continue;
            }

            var talent = raw with {
                Name = raw.Name ?? "", Shown = raw.Shown ?? "", Description = raw.Description ?? "", Kind = raw.Kind ?? "",
                BreedsAs = raw.BreedsAs ?? "", Effects = raw.Effects ?? [], Stats = raw.Stats ?? [], Card = raw.Card ?? "",
                Buff = raw.Buff ?? "", Pets = raw.Pets ?? [], Sure = raw.Sure ?? [],
            };
            var petNames = string.Join(' ', talent.Pets.Select(id => pets.TryGetValue(id, out var p) ? $"{p.Name} {p.Object} {id}" : $"{id}"));
            var haystack = SearchText.Normalize($"{talent.Shown} {talent.Name} {talent.Description} {talent.Kind} {Rarity(talent.Rank)} " +
                $"{talent.Buff} {string.Join(' ', talent.Effects)} {string.Join(' ', talent.Stats)} {talent.Card} {talent.CardId} {petNames}");
            rows.Add(new PetTalentRow(talent, haystack, SearchText.Normalize(petNames)));
        }

        return ([.. rows.OrderBy(r => r.Talent.Title, StringComparer.OrdinalIgnoreCase)], pets);
    }

    private static string Rarity(int rank) => rank > 0 && rank < s_petRarities.Length ? s_petRarities[rank] : rank > 0 ? $"Rank {rank}" : "";

    private void RenderPetTalents(PetTalentPage page) {
        var all = _petTalentRows.Where(r => r.Talent.Derby == page.Derby).ToList();
        if (all.Count == 0) {
            page.List.ItemsSource = null;
            page.Count.Text = File.Exists(PetTalentsPath)
                ? "The pet talent list is empty."
                : "No pet talent list yet. Start the server once: it writes the list about two minutes after it starts (with the ID lookup on), " +
                  "and it shows here then.";

            return;
        }

        var words = SearchText.Words(page.Search.Text);
        var kind = FilterPick(page.Kind);
        var rarity = FilterPick(page.Rarity);
        var stat = FilterPick(page.Stat);
        var does = SearchText.Words(page.Does.Text);
        var pet = SearchText.Words(page.Pet.Text);
        var card = SearchText.Words(page.Card.Text);

        var rows = all.Where(row => {
            var t = row.Talent;

            return words.All(w => row.Haystack.Contains(w, StringComparison.Ordinal))
                && (kind is null || (page.Derby ? t.Buff.Equals(kind, StringComparison.OrdinalIgnoreCase) : t.Kind == kind))
                && (rarity is null || Rarity(t.Rank) == rarity)
                && (stat is null || t.Stats.Contains(stat, StringComparer.OrdinalIgnoreCase))
                && AllWordsIn(does, $"{t.Description} {string.Join(' ', t.Effects)}")
                && pet.All(w => row.PetNames.Contains(w, StringComparison.Ordinal))
                && AllWordsIn(card, $"{t.Card} {t.CardId}")
                && (card.Length == 0 || t.Card.Length > 0)
                && InFilterRange(t.Pets.Count, page.PetsMin, page.PetsMax)
                && (!page.Derby || InFilterRange((long) Math.Round(t.Cooldown), page.WaitMin, page.WaitMax))
                && (page.HideOld.IsChecked != true || !(t.Retired || t.Hidden))
                && (page.OnPets.IsChecked != true || t.Pets.Count > 0)
                && (page.SureOnly.IsChecked != true || t.Sure.Count > 0);
        }).ToList();

        var byName = StringComparer.OrdinalIgnoreCase;
        rows = page.Sort.SelectedIndex switch {
            1 => [.. rows.OrderByDescending(r => r.Talent.Rank).ThenBy(r => r.Talent.Title, byName)],
            2 => [.. rows.OrderBy(r => r.Talent.Rank).ThenBy(r => r.Talent.Title, byName)],
            3 => [.. rows.OrderByDescending(r => r.Talent.Pets.Count).ThenBy(r => r.Talent.Title, byName)],
            4 => [.. rows.OrderBy(r => r.Talent.Pets.Count).ThenBy(r => r.Talent.Title, byName)],
            5 when page.Derby => [.. rows.OrderBy(r => r.Talent.Cooldown <= 0).ThenBy(r => r.Talent.Cooldown).ThenBy(r => r.Talent.Title, byName)],
            5 => [.. rows.OrderBy(r => Array.IndexOf(s_petTalentKinds, r.Talent.Kind)).ThenBy(r => r.Talent.Title, byName)],
            6 => [.. rows.OrderBy(r => r.Talent.Name, byName)],
            _ => rows,
        };

        page.List.ItemsSource = rows;
        var what = page.Derby ? "derby talents" : "pet talents";
        page.Count.Text = page.Derby
            ? FilterCount(rows.Count, all.Count, what)
            : $"{FilterCount(rows.Count, all.Count, what)} {rows.Count(r => r.Talent.Kind == "May Cast"):N0} May Casts, " +
              $"{rows.Count(r => r.Talent.Kind == "Will Cast"):N0} Will Casts, {rows.Count(r => r.Talent.Kind == "Gives a card"):N0} give a card.";
    }

    private Control PetTalentRowView(PetTalentRow row) {
        var t = row.Talent;
        var tags = new[] {
            Rarity(t.Rank),
            t.Derby ? (t.Buff is "Buff" or "Debuff" ? t.Buff : "") : t.Kind,
            t.Derby && t.Cooldown > 0 ? $"waits {t.Cooldown:0.#} s" : "",
            t.Pets.Count > 0 ? $"{t.Pets.Count:N0} pet{(t.Pets.Count == 1 ? "" : "s")}" : "no pet has it",
            t.Retired ? "retired" : t.Hidden ? "hidden" : "",
        }.Where(s => s.Length > 0);

        return new Border {
            Padding = new Thickness(4, 6),
            BorderBrush = LINE,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel {
                Spacing = 2,
                Children = {
                    new StackPanel {
                        Orientation = Orientation.Horizontal, Spacing = 10,
                        Children = {
                            new TextBlock { Text = t.Title, FontWeight = FontWeight.SemiBold, FontSize = 15 },
                            WithCenter(new TextBlock { Text = string.Join("  ·  ", tags), Foreground = MUTED }),
                        },
                    },
                    Sub(t.Effects.Count > 0 ? string.Join(" ", t.Effects.Take(2)) : t.Description.Length > 0 ? t.Description : t.Name),
                },
            },
        };
    }

    private void ShowPetTalent(PetTalentPage page, PetTalentInfo? talent) {
        var shownRow = talent is null ? null : (page.List.ItemsSource as List<PetTalentRow>)?.FirstOrDefault(r => r.Talent == talent);
        page.Popup?.ShowFor(page.List, shownRow);
        page.Picked = talent;
        page.Details.Children.Clear();
        if (talent is null) {
            return;
        }

        var details = page.Details;
        details.Children.Add(new SelectableTextBlock { Text = talent.Title, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        details.Children.Add(Sub(string.Join(". ", new[] {
            Rarity(talent.Rank),
            talent.Derby ? $"Derby talent{(talent.Buff is "Buff" or "Debuff" ? $", a {talent.Buff.ToLowerInvariant()}" : "")}" : talent.Kind,
            talent.Derby && talent.Cooldown > 0 ? $"Waits {talent.Cooldown:0.#} seconds between uses" : "",
            talent.Retired ? "Retired by the game" : "",
            talent.Hidden ? "Hidden from the game's searches" : "",
        }.Where(s => s.Length > 0)) + "."));
        details.Children.Add(new DockPanel {
            Children = {
                WithDock(WithCenter(Action(Plain("Copy"), () => CopyAsync(talent.Name))), Dock.Right),
                WithCenter(new SelectableTextBlock { Text = talent.Name, FontFamily = MONO, Foreground = GOOD, TextWrapping = TextWrapping.Wrap }),
            },
        });

        // Put it on the picked wizard's pet.
        var slot = new ComboBox { ItemsSource = Enumerable.Range(0, 10).Select(i => $"Slot {i}").ToList(), SelectedIndex = 0, Width = 110 };
        var claim = new CheckBox { Content = "Claim it (show it in the pet window)", IsChecked = true };
        var group = talent.Derby ? ".pet derby" : ".pet talent";
        var put = Action(Primary("Put on my pet"), () => {
            var at = Math.Max(0, slot.SelectedIndex);
            RunPetCommand(page, $"{group} get {at} {talent.Name}", $"Sent {group} get {at} {talent.Name}.");
            if (claim.IsChecked == true) {
                _manager.SendCommand($"{group} claim {at}", WizardIdOf(page));
            }

            return Task.CompletedTask;
        });
        ToolTip.SetTip(put, $"Puts this talent in the picked slot of the pet {WizardWord(page)} wears (slot 0 is the top of the pet window). " +
            "If the pet has it in another slot, the two swap. Claim shows it, at most five per pet.");
        var buttons = new WrapPanel { Children = { Spaced(slot), Spaced(put), Spaced(WithCenter(claim)) } };
        if (!talent.Derby) {
            var give = Action(Plain("Add to my pet"), () => {
                RunPetCommand(page, $".pet talent {talent.Name}", $"Sent .pet talent {talent.Name}.");

                return Task.CompletedTask;
            });
            ToolTip.SetTip(give, "Adds it to the pet's talents without picking a slot (.pet talent).");
            var take = Action(Plain("Take off my pet"), () => {
                RunPetCommand(page, $".pet untalent {talent.Name}", $"Sent .pet untalent {talent.Name}.");

                return Task.CompletedTask;
            });
            ToolTip.SetTip(take, "Takes it off the pet (.pet untalent).");
            buttons.Children.Add(Spaced(give));
            buttons.Children.Add(Spaced(take));
        }

        details.Children.Add(buttons);
        details.Children.Add(Sub($"Runs on the pet {WizardWord(page)} wears. Pick the wizard in Your pet beside the list. Relog to see a change in the pet window."));

        void Heading(string text) => details.Children.Add(new TextBlock {
            Text = text.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.Bold, Foreground = MUTED, Margin = new Thickness(0, 10, 0, 0),
        });

        void Lines(IEnumerable<string> lines) => details.Children.Add(new SelectableTextBlock {
            Text = string.Join('\n', lines), TextWrapping = TextWrapping.Wrap, FontSize = 13,
        });

        if (talent.Description.Length > 0) {
            Heading("The game's words");
            Lines([talent.Description]);
        }

        if (talent.Effects.Count > 0) {
            Heading("What it does");
            Lines(talent.Effects);
        }

        if (talent.Card.Length > 0) {
            Heading(talent.Kind == "Gives a card" ? "Card it gives" : "Spell it casts");
            Lines([talent.CardId > 0 ? $"{talent.Card}  (spell ID {talent.CardId})" : talent.Card]);
        }

        if (talent.Stats.Count > 0) {
            Heading("Pet stats it uses");
            Lines([string.Join(", ", talent.Stats)]);
        }

        if (talent.BreedsAs.Length > 0 && !talent.BreedsAs.Equals(talent.Name, StringComparison.OrdinalIgnoreCase)) {
            Heading("When hatched, passes on as");
            Lines([talent.BreedsAs]);
        }

        Heading($"Pets that can have it ({talent.Pets.Count:N0})");
        if (talent.Pets.Count == 0) {
            Lines(["No pet in the game files lists it."]);

            return;
        }

        const int MAX_PETS = 300;
        var sure = talent.Sure.ToHashSet();
        var names = talent.Pets
            .Select(id => (Id: id, Name: _petTalentPets.TryGetValue(id, out var p) ? p.Name : "Unknown pet"))
            .OrderByDescending(p => sure.Contains(p.Id)).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => $"{p.Name}  ({p.Id}){(sure.Contains(p.Id) ? ", always gets it" : "")}")
            .ToList();
        Lines(names.Count > MAX_PETS ? [.. names.Take(MAX_PETS), $"…and {names.Count - MAX_PETS:N0} more."] : names);
    }

    /// <summary>The Your pet card: buttons for the .pet commands, run on the pet the picked wizard wears.</summary>
    private Control PetCommandsCard(PetTalentPage page) {
        var level = FilterCombo(s_petLevels, 120);
        var xp = new NumericUpDown { Minimum = 1, Maximum = 10_000_000, Value = 500, Increment = 100, FormatString = "0", Width = 130 };
        var stat = FilterCombo(s_petStats, 130);
        var amount = new TextBox { Text = "max", Width = 90, Watermark = "max or 255" };
        var snack = new TextBox { Watermark = "Snack name or ID", Width = 200 };
        var slot = new ComboBox { ItemsSource = Enumerable.Range(0, 10).Select(i => $"Slot {i}").ToList(), SelectedIndex = 0, Width = 110 };
        var group = page.Derby ? ".pet derby" : ".pet talent";

        Button Run(Button button, Func<string> command, string tip) {
            ToolTip.SetTip(button, tip);

            return Action(button, () => {
                var text = command();
                RunPetCommand(page, text, $"Sent {text}.");

                return Task.CompletedTask;
            });
        }

        void Need(bool ok, string problem) {
            if (!ok) {
                throw new PanelException(problem);
            }
        }

        Control Line(string label, params Control[] controls) {
            var row = new WrapPanel();
            row.Children.AddRange(controls);

            return new StackPanel {
                Spacing = 4,
                Children = { new TextBlock { Text = label, Foreground = MUTED, FontSize = 12, IsVisible = label.Length > 0 }, row },
            };
        }

        var refresh = Action(Plain("Refresh"), () => {
            FillPetWizards(page);

            return Task.CompletedTask;
        });
        ToolTip.SetTip(refresh, "Lists the wizards again. A wizard shows here once it has logged in on this server.");

        return Fold("heart", page.Derby ? "Pet Derby" : "Pet Talents", "Your pet",
            "The .pet commands, on the pet your wizard wears. The game's chat says if it worked.",
            Line("Wizard", Spaced(page.Wizard), Spaced(refresh)),
            Line("Look", Spaced(Run(Primary("Show my pet"), () => ".pet info", "Says the pet's level, XP, stats and talents in the game's chat (.pet info)."))),
            Line("Level", Spaced(level), Spaced(Run(Plain("Set level"), () => $".pet level {Math.Max(0, level.SelectedIndex) + 1}",
                "Makes the pet that age (.pet level). New talents for its age are rolled from its pool."))),
            Line("XP", Spaced(xp), Spaced(Run(Plain("Add XP"), () => $".pet xp {(int) (xp.Value ?? 0)}",
                "Gives the pet this much XP, so it may grow up (.pet xp)."))),
            Line("Stats", Spaced(stat), Spaced(amount), Spaced(Run(Plain("Set stat"), () => {
                var value = (amount.Text ?? "").Trim();
                Need(value.Equals("max", StringComparison.OrdinalIgnoreCase) || int.TryParse(value, out _), "Type a number or max for the stat.");

                return $".pet stats {stat.SelectedItem} {value}";
            }, "Sets one pet stat to a number or its top (.pet stats)."))),
            Line("", Spaced(Run(Plain("Max every stat"), () => ".pet maxstats", "Puts every pet stat at its top (.pet maxstats)."))),
            Line("Feed", Spaced(snack), Spaced(Run(Plain("Feed one"), () => {
                Need(!string.IsNullOrWhiteSpace(snack.Text), "Type the name or ID of a snack in your wizard's snack bag.");

                return $".pet feed {snack.Text!.Trim()}";
            }, "Feeds one snack from the wizard's snack bag (.pet feed): XP and stats like the game's feed window.")),
                Spaced(Run(Plain("Feed all"), () => {
                    Need(!string.IsNullOrWhiteSpace(snack.Text), "Type the name or ID of a snack in your wizard's snack bag.");

                    return $".pet feedall {snack.Text!.Trim()}";
                }, "Feeds every snack of that kind in the bag (.pet feedall)."))),
            Line(page.Derby ? "Derby slots" : "Talent slots", Spaced(slot),
                Spaced(Run(Plain("What's in it"), () => $"{group} get {Math.Max(0, slot.SelectedIndex)}", $"Says which talent the slot holds ({group} get).")),
                Spaced(Run(Plain("Claim"), () => $"{group} claim {Math.Max(0, slot.SelectedIndex)}", $"Shows the slot's talent in the pet window, at most five ({group} claim).")),
                Spaced(Run(Plain("Unclaim"), () => $"{group} unclaim {Math.Max(0, slot.SelectedIndex)}", $"Hides the slot's talent again ({group} unclaim)."))),
            Sub("To give the pet a talent, click it in the list and press Put on my pet."));
    }

    /// <summary>Lists the wizards that have logged in, this PC's first, keeping the one picked.</summary>
    private void FillPetWizards(PetTalentPage page) {
        var wizards = ReadQuestWizards();
        var picked = (page.Wizard.SelectedItem as QuestWizard)?.File;
        page.Wizard.ItemsSource = wizards;
        page.Wizard.SelectedItem = wizards.FirstOrDefault(w => w.File == picked) ?? wizards.FirstOrDefault();
    }

    private static string? WizardIdOf(PetTalentPage page)
        => page.Wizard.SelectedItem is QuestWizard wizard ? Path.GetFileNameWithoutExtension(wizard.File) : null;

    private static string WizardWord(PetTalentPage page)
        => page.Wizard.SelectedItem is QuestWizard { Name.Length: > 0 } wizard ? wizard.Name : "your wizard";

    // Only the picked wizard runs it, not every wizard playing on this PC.
    private void RunPetCommand(PetTalentPage page, string command, string said) {
        _manager.SendCommand(command, WizardIdOf(page));
        Say(said + (page.Wizard.SelectedItem is QuestWizard { Name.Length: > 0 } wizard ? $" Only {wizard.Name}'s pet." : "") +
            " The game's chat says if it worked.");
    }

}
