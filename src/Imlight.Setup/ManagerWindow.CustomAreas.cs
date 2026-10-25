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
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using static Imlight.Setup.AreaStyle;

namespace Imlight.Setup;

/// <summary>
/// The Customs tab's Custom areas (Niduus, 2026-10-22: "more specific, descriptive and modular for people to change
/// anything and everything ... organized sections of what and each, using their icons too"). Every area file
/// (spiraldb-extra/ZoneObjects) opens into sections, one for each part of the file: general, objects (with sigil pads
/// and battle circles), creatures, spawn swaps, encounters and the room's boss, distinct spawners, left-out objects,
/// the exit and the file's text. Each field says what it does; each section says when its changes count. Save writes
/// the same file the server reads, keeping every field written by hand.
/// </summary>
internal sealed partial class ManagerWindow {

    private readonly StackPanel _areaRows = new() { Spacing = 12 };
    private readonly Dictionary<string, AreaEdit> _areaEdits = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _areaOpen = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Border Host, Func<SpellBook, string?> Name)> _areaGameIcons = [];
    private readonly Dictionary<string, Bitmap?> _areaBitmaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly AutoCompleteBox _newAreaZone = new() { Width = 360, Watermark = "The zone, such as WizardCity/WC_Ravenwood", FilterMode = AutoCompleteFilterMode.ContainsOrdinal, MinimumPrefixLength = 1 };
    private readonly TextBox _newAreaVariant = new() { Width = 260, Watermark = "Empty, or a name such as MyBossRoom" };
    private readonly ComboBox _newAreaFrom = new() { Width = 360 };
    private AreaCatalog? _areaCatalog;
    private DateTime _areaCatalogFrom;
    private Task? _areaCatalogLoading;
    private Task? _areaBookLoading;
    private string? _sigilIconName;

    private string AreaFolder => Path.Combine(_manager.ExtraFolder, AreaFiles.FOLDER);

    /// <summary>One area being edited: the file as it is on disk, and the copy the boxes change.</summary>
    private sealed class AreaEdit(CustomArea area, JsonObject work) {

        internal CustomArea Area { get; } = area;

        internal JsonObject Work { get; set; } = work;

        internal string Saved { get; set; } = Clean(work).ToJsonString();

        /// <summary>Boxes that hold something that is not a number, with what to say.</summary>
        internal Dictionary<Control, string> Errors { get; } = [];

        /// <summary>Objects moved or added by the older live editor (Customs/Placements.json), folded in on Save.</summary>
        internal bool HasPlacements { get; set; }

        /// <summary>Object names taken out with the older live editor (they stay out until shown again).</summary>
        internal HashSet<string> Hidden { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>What an object was before it was switched to another kind, so switching back brings it again.</summary>
        internal Dictionary<string, JsonNode> Stash { get; } = [];

        internal List<TextBlock> DirtyMarks { get; } = [];

        internal bool Dirty => Clean(Work).ToJsonString() != Saved;

        internal void Changed() {
            foreach (var mark in DirtyMarks) {
                mark.IsVisible = Dirty;
            }
        }

        internal string Zone => CustomArea.Text(Work, "Zone");

        internal string Variant => CustomArea.Text(Work, "Variant");

        internal string Room => Variant.Length > 0 ? $"{Zone}@{Variant}" : Zone;

    }

    private enum PickKind { Creature, Object, Any }

    // ---------------------------------------------------------------------
    // The card
    // ---------------------------------------------------------------------

    private Control CustomAreasCard() {
        // The legend is one line, with the longer words behind its "?".
        var legend = new WrapPanel {
            Children = {
                WithMargin(WithCenter(Sub("Changes count:")), new Thickness(0, 0, 8, 4)),
                WithMargin(TimingTag(Timing.Live), new Thickness(0, 0, 8, 4)),
                WithMargin(TimingTag(Timing.NextUse), new Thickness(0, 0, 8, 4)),
                WithMargin(TimingTag(Timing.Reload), new Thickness(0, 0, 8, 4)),
            },
        };

        return Fold("pin", "zone", "Custom areas",
            "Change places in the game, or make private copies of them.",
            WithHint(legend,
                "Places the server changes or adds to the game, one file each (spiraldb-extra/ZoneObjects): objects and " +
                "sigils, creatures, battles with helpers, who the boss of a room is, where leaving goes, and more. Open an " +
                "area, then open any of its sections; every box says what it does. Press Save area when you are done. " +
                "Nothing in the game files is ever changed, and updates keep the areas you saved here. " +
                "Each section says when its changes count: point at its tag for more."),
            NewAreaPanel(),
            _areaRows);
    }

    private Control NewAreaPanel() {
        var body = new StackPanel {
            Spacing = FIELD_SPACING,
            Children = {
                WithHint(Sub("A zone with no variant changes the zone itself, for everyone."),
                    "Make a new area for a zone, or a variant: a private copy of a zone that only a sigil with that variant " +
                    "sends teams into, fresh each time (as the Malistaire room is). A zone with no variant changes the zone " +
                    "itself for everyone."),
                Field("Zone", "Which zone this area changes. Type a few letters to search.", _newAreaZone),
                Field("Variant", "Empty changes the zone itself. A name makes a private copy of the zone with these changes.", _newAreaVariant),
                Field("Start from", "An empty area, or a copy of an area you already have.", _newAreaFrom),
                WithMargin(Row(Action(IconButton("Make the area", AreaIcon.Add, primary: true), MakeAreaAsync),
                    Action(IconButton("Open the areas folder", AreaIcon.File), () => Open(AreaFolder))), new Thickness(LABEL_WIDTH, 4, 0, 0)),
            },
        };

        return OpenState(Section(SectionHeader(Tile(Glyph(AreaIcon.Add, ZONE), ZONE), "New custom area or variant", null, Timing.Live),
            body, ZONE, false), "new-area");
    }

    private void RenderAreas() {
        _ = Guard(LoadAreaCatalogAsync);
        EnsureAreaBook();
        _areaRows.Children.Clear();
        _areaGameIcons.Clear();
        var areas = CustomArea.All(AreaFolder);
        var files = areas.Select(a => a.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _areaEdits.Keys.Where(k => !files.Contains(k)).ToList()) {
            _areaEdits.Remove(gone);
        }

        _newAreaFrom.ItemsSource = new[] { "An empty area" }.Concat(areas.Select(a => "A copy of " + AreaTitle(a.Zone, a.Variant))).ToList();
        _newAreaFrom.SelectedIndex = Math.Clamp(_newAreaFrom.SelectedIndex, 0, areas.Count);
        _newAreaZone.ItemsSource = _areaCatalog?.Zones;
        if (areas.Count == 0) {
            _areaRows.Children.Add(Sub("There are no custom areas yet. Make one above."));

            return;
        }

        var placements = SafePlacements();
        foreach (var area in areas) {
            // Unsaved work is kept while the tab is redrawn.
            if (!_areaEdits.TryGetValue(area.File, out var edit) || (!edit.Dirty && edit.Area.Json.ToJsonString() != area.Json.ToJsonString())) {
                edit = NewEdit(area, placements);
                _areaEdits[area.File] = edit;
            }

            _areaRows.Children.Add(AreaView(edit));
        }
    }

    private AreaEdit NewEdit(CustomArea area, JsonObject placements) {
        var work = (JsonObject) area.Json.DeepClone();
        var edit = new AreaEdit(area, work);
        if (BossChanges.FindIn(placements, area.Room) is JsonObject room) {
            var objects = Arr(work, "Objects");
            foreach (var (name, node) in room) {
                if (node is not JsonObject placement) {
                    continue;
                }

                var obj = objects.OfType<JsonObject>().FirstOrDefault(o => CustomArea.Text(o, "Name").Equals(name, StringComparison.OrdinalIgnoreCase));
                if (BossChanges.FindIn(placement, "Removed") is JsonValue r && r.ToJsonString() == "true") {
                    edit.Hidden.Add(name);

                    continue;
                }

                if (obj is null) {
                    if (CustomArea.Number(placement, "TemplateID") <= 0) {
                        continue;
                    }

                    obj = new JsonObject { ["Name"] = name, ["Notes"] = "Added in the Customs tab." };
                    objects.Add(obj);
                }

                foreach (var key in new[] { "TemplateID", "X", "Y", "Z", "Yaw", "Scale" }) {
                    if (BossChanges.FindIn(placement, key) is JsonValue v) {
                        Set(obj, key, JsonNode.Parse(v.ToJsonString()));
                    }
                }

                edit.HasPlacements = true;
            }
        }

        edit.Saved = Clean(edit.Work).ToJsonString();

        return edit;
    }

    private void RerenderArea(AreaEdit edit) {
        var index = _areaRows.Children.ToList().FindIndex(c => c.Tag == edit);
        if (index < 0) {
            RenderAreas();

            return;
        }

        _areaRows.Children[index] = AreaView(edit);
    }

    // ---------------------------------------------------------------------
    // One area
    // ---------------------------------------------------------------------

    private Control AreaView(AreaEdit edit) {
        edit.DirtyMarks.Clear();
        edit.Errors.Clear();
        var key = edit.Area.File;
        var work = edit.Work;
        var enabled = !(BossChanges.FindIn(work, "Enabled") is JsonValue en && en.ToJsonString() == "false");

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        title.Children.Add(Tile(Glyph(edit.Variant.Length > 0 ? AreaIcon.Variant : AreaIcon.Zone, ZONE, 20), ZONE, 36));
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        names.Children.Add(new TextBlock { Text = ZoneDisplay(edit.Zone), FontSize = 16, FontWeight = FontWeight.SemiBold });
        names.Children.Add(new TextBlock {
            Text = edit.Variant.Length > 0 ? $"{edit.Zone}  ·  private copy \"{edit.Variant}\"" : $"{edit.Zone}  ·  the zone itself, for everyone",
            Foreground = MUTED, FontSize = 12,
        });
        title.Children.Add(names);
        var counts = new List<string>();
        void CountOf(string k, string one, string many) {
            var n = Entries(work, k).Count();
            if (n > 0) {
                counts.Add(n == 1 ? $"1 {one}" : $"{n} {many}");
            }
        }

        CountOf("Objects", "object", "objects");
        CountOf("Creatures", "creature", "creatures");
        CountOf("Encounters", "battle", "battles");
        if (BossChanges.FindIn(work, "Exit") is JsonObject) {
            counts.Add("own exit");
        }

        if (counts.Count > 0) {
            title.Children.Add(WithCenter(Count(string.Join(" · ", counts))));
        }

        if (!enabled) {
            title.Children.Add(WithCenter(new Border {
                CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1), Background = Brush(REMOVED, 50),
                Child = new TextBlock { Text = "Switched off", FontSize = 12 },
            }));
        }

        title.Children.Add(WithCenter(DirtyMark(edit)));

        var body = new StackPanel { Spacing = SECTION_SPACING, Margin = new Thickness(0, 4, 0, 0) };
        body.Children.Add(SaveBar(edit));
        body.Children.Add(GeneralSection(edit, enabled));
        body.Children.Add(ObjectsSection(edit));
        body.Children.Add(CreaturesSection(edit));
        body.Children.Add(SwapsSection(edit));
        body.Children.Add(EncountersSection(edit));
        body.Children.Add(DistinctSection(edit));
        body.Children.Add(RemovedSection(edit));
        body.Children.Add(ExitSection(edit));
        body.Children.Add(AdvancedSection(edit));
        body.Children.Add(SaveBar(edit));

        var view = OpenState(Section(title, body, ZONE, false), key);
        view.Tag = edit;

        return view;
    }

    private TextBlock DirtyMark(AreaEdit edit) {
        var mark = new TextBlock { Text = "Unsaved changes", Foreground = DIRTY, FontWeight = FontWeight.Medium, IsVisible = edit.Dirty, VerticalAlignment = VerticalAlignment.Center };
        edit.DirtyMarks.Add(mark);

        return mark;
    }

    private Control SaveBar(AreaEdit edit) => new WrapPanel {
        Children = {
            Spaced(Action(IconButton("Save area", AreaIcon.File, primary: true), () => SaveAreaAsync(edit))),
            Spaced(Action(IconButton("Undo unsaved changes", AreaIcon.Back), () => {
                _areaEdits.Remove(edit.Area.File);
                RenderAreas();
                Say("The unsaved changes are undone.");

                return Task.CompletedTask;
            })),
            Spaced(WithCenter(DirtyMark(edit))),
        },
    };

    // ---------------------------------------------------------------------
    // General
    // ---------------------------------------------------------------------

    private Control GeneralSection(AreaEdit edit, bool enabled) {
        var work = edit.Work;
        var toggle = new ToggleSwitch { IsChecked = enabled, OnContent = "On", OffContent = "Off" };
        toggle.IsCheckedChanged += (_, _) => {
            if (toggle.IsChecked == true) {
                Set(work, "Enabled", null);
            }
            else {
                Set(work, "Enabled", false);
            }

            edit.Changed();
        };
        var original = File.Exists(AreaFiles.OriginalOf(_manager.CustomsFolder, edit.Area.File));
        var buttons = new WrapPanel {
            Children = {
                Spaced(Action(IconButton("Make a variant of this", AreaIcon.Variant), () => MakeVariantAsync(edit))),
                Spaced(Action(IconButton("Open the file", AreaIcon.File), () => Open(edit.Area.File))),
                Spaced(Danger(Action(IconButton("Delete this area", AreaIcon.Delete), () => DeleteAreaAsync(edit)))),
            },
        };
        if (original) {
            buttons.Children.Insert(1, Spaced(Action(IconButton("Go back to the original", AreaIcon.Back), () => RestoreAreaAsync(edit))));
        }

        var body = new StackPanel {
            Spacing = FIELD_SPACING,
            Children = {
                Field("Used", "Off keeps the file but the server leaves the whole area out (its objects go at once).", toggle),
                Field("Zone", "The zone this area changes. Changing it moves the whole area to that zone.",
                    ZoneBox(edit, work, "Zone", required: true)),
                Field("Variant", "Empty: the zone itself, for everyone. A name: a private copy that only a sigil pad with this " +
                                 "variant sends teams into, loaded fresh with these changes.", Text(edit, work, "Variant", "Empty for the zone itself", 260)),
                Field("Notes", "Your own notes. The server does not use them.", Text(edit, work, "Notes", "What this area is for", 560, multiline: true)),
                Field("File", "Where it is kept. The name follows the zone and variant.",
                    new SelectableTextBlock { Text = Path.GetFileName(edit.Area.File), Foreground = MUTED, VerticalAlignment = VerticalAlignment.Center }),
                WithMargin(buttons, new Thickness(LABEL_WIDTH, 4, 0, 0)),
            },
        };

        return OpenState(Section(SectionHeader(Tile(Glyph(AreaIcon.General, GENERAL), GENERAL), "General", null, Timing.Live), body, GENERAL, false),
            edit.Area.File + "/general");
    }

    // ---------------------------------------------------------------------
    // Objects
    // ---------------------------------------------------------------------

    private Control ObjectsSection(AreaEdit edit) {
        var objects = Arr(edit.Work, "Objects");
        var body = new StackPanel { Spacing = FIELD_SPACING };
        body.Children.Add(Sub("Things the server puts in the zone: anything with a template, such as a portal, a statue, a sigil pad that " +
                              "sends a team somewhere, or a battle circle. Moving, turning, adding or removing one changes the running " +
                              "game at once, battle circles too. Add a duel circle makes a PvP circle where your wizard stands."));
        if (edit.HasPlacements) {
            body.Children.Add(Note("Some objects were moved earlier with the old live boxes. Those places are shown here and are written " +
                                   "into the file when you press Save area."));
        }

        foreach (var obj in objects.OfType<JsonObject>().ToList()) {
            body.Children.Add(ObjectItem(edit, objects, obj));
        }

        body.Children.Add(Row(
            Action(IconButton("Add an object", AreaIcon.Add), () => AddEntry(edit, objects, new JsonObject {
                ["Name"] = UniqueName(edit, "New object"), ["TemplateID"] = 0, ["X"] = 0, ["Y"] = 0, ["Z"] = 0, ["Yaw"] = 0, ["Scale"] = 1,
            }, "/objects/")),
            Action(IconButton("Add a sigil pad", AreaIcon.Sigil), () => AddEntry(edit, objects, new JsonObject {
                ["Name"] = UniqueName(edit, "New sigil"), ["TemplateID"] = 107081, ["X"] = 0, ["Y"] = 0, ["Z"] = 0, ["Yaw"] = 0, ["Scale"] = 1,
                ["Sigil"] = DefaultSigil(),
            }, "/objects/")),
            Action(IconButton("Add a duel circle", AreaIcon.CombatCircle), () => AddDuelCircle(edit, objects))));

        var count = objects.Count;

        return OpenState(Section(SectionHeader(SigilTile(OBJECTS, AreaIcon.Objects), "Objects and sigils", count.ToString(CultureInfo.InvariantCulture), Timing.Live),
            body, OBJECTS, false), edit.Area.File + "/objects");
    }

    // Niduus (2026-10-23): a duel circle in one press, with the right template and type, where the wizard stands.
    private Task AddDuelCircle(AreaEdit edit, JsonArray objects) {
        var circle = new JsonObject {
            ["Name"] = UniqueName(edit, "Duel circle"), ["TemplateID"] = AreaCatalog.DUEL_CIRCLE_TEMPLATE,
            ["X"] = 0, ["Y"] = 0, ["Z"] = 0, ["Yaw"] = 0, ["Scale"] = 1,
            ["CombatSigil"] = new JsonObject { ["SigilType"] = AreaCatalog.PVP_SIGIL_TYPE, ["Radius"] = 0, ["Combat"] = "Turns" },
        };
        string said;
        try {
            var (who, x, y, z, yaw) = MyPosition(edit.Zone);
            circle["X"] = double.Parse(x, CultureInfo.InvariantCulture);
            circle["Y"] = double.Parse(y, CultureInfo.InvariantCulture);
            circle["Z"] = double.Parse(z, CultureInfo.InvariantCulture);
            circle["Yaw"] = double.Parse(yaw, CultureInfo.InvariantCulture);
            said = $"Added a duel circle where {who} stands. Press Save area and it appears at once.";
        }
        catch (Exception ex) when (ex is PanelException or FormatException) {
            said = "Added a duel circle. Stand where it should go in the game, press Use my position on it, then Save area.";
        }

        AddEntry(edit, objects, circle, "/objects/");
        Say(said);

        return Task.CompletedTask;
    }

    // Fixes battle circles set up wrong (Niduus: simpler for anyone, correcting their work): a duel circle (PvP type), or
    // a circle whose template is a creature, an NPC, an item or nothing, gets the game's Duel circle template; a type
    // typed with spaces or no type is set right. Says what it changed.
    private List<string> FixCircles(JsonObject work) {
        var fixes = new List<string>();
        foreach (var o in Entries(work, "Objects")) {
            if (BossChanges.FindIn(o, "CombatSigil") is not JsonObject circle) {
                continue;
            }

            var name = CustomArea.Text(o, "Name").Trim();
            var type = CustomArea.Text(circle, "SigilType").Trim();
            var template = (ulong) CustomArea.Number(o, "TemplateID");
            var isDuel = type.Equals(AreaCatalog.PVP_SIGIL_TYPE, StringComparison.OrdinalIgnoreCase);
            if (type.Length == 0) {
                Set(circle, "SigilType", AreaCatalog.PVP_SIGIL_TYPE);
                fixes.Add($"\"{name}\" had no circle type; it is now a duel circle ({AreaCatalog.PVP_SIGIL_TYPE})");
                isDuel = true;
            }
            else if (!isDuel && !AreaCatalog.COMBAT_SIGIL_TYPES.Contains(type, StringComparer.OrdinalIgnoreCase)
                     && AreaCatalog.COMBAT_SIGIL_TYPES.FirstOrDefault(t => t.Equals(type.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)) is { } typed) {
                Set(circle, "SigilType", typed);
                fixes.Add($"\"{name}\" circle type spelled as {typed}");
            }

            var kind = _areaCatalog is not null && _areaCatalog.Templates.TryGetValue(template, out var known) ? known.Kind : "";
            var wrongLook = template == 0 || IsCreature(template) || kind is "NPC" or "Creature" or "Item";
            if (template != AreaCatalog.DUEL_CIRCLE_TEMPLATE && (isDuel || wrongLook)) {
                var was = template == 0 ? "no template" : $"template {template} ({TemplateName(template, PickKind.Object)})";
                Set(o, "TemplateID", AreaCatalog.DUEL_CIRCLE_TEMPLATE);
                fixes.Add($"\"{name}\" had {was}; it is now the game's Duel circle ({AreaCatalog.DUEL_CIRCLE_TEMPLATE})");
            }
        }

        return fixes;
    }

    private static JsonObject DefaultSigil() => new() {
        ["SigilType"] = AreaCatalog.SIGIL_TYPES[0], ["Tag"] = "", ["Radius"] = 0, ["DestinationZone"] = "", ["DestinationLoc"] = "",
        ["Variant"] = "", ["LitState"] = ZoneLit,
    };

    private const string ZoneLit = "Glowy";

    private Control ObjectItem(AreaEdit edit, JsonArray objects, JsonObject obj) {
        var name = CustomArea.Text(obj, "Name");
        var sigil = BossChanges.FindIn(obj, "Sigil") as JsonObject;
        var circle = BossChanges.FindIn(obj, "CombatSigil") as JsonObject;
        var kind = sigil is not null ? 1 : circle is not null ? 2 : 0;
        var color = kind == 1 ? SIGIL : kind == 2 ? COMBAT : OBJECTS;
        var templateName = TemplateName((ulong) CustomArea.Number(obj, "TemplateID"), PickKind.Object);

        var kindBox = new ComboBox {
            Width = 360, SelectedIndex = kind,
            ItemsSource = new[] { "A plain object (for looks, or a door)", "A sigil pad (sends a team somewhere)", "A battle circle (where battles start)" },
        };
        kindBox.SelectionChanged += (_, _) => {
            if (kindBox.SelectedIndex == kind || kindBox.SelectedIndex < 0) {
                return;
            }

            foreach (var part in new[] { "Sigil", "CombatSigil" }) {
                if (BossChanges.FindIn(obj, part) is { } had) {
                    edit.Stash[name + "/" + part] = had.DeepClone();
                    Set(obj, part, null);
                }
            }

            var wanted = kindBox.SelectedIndex switch { 1 => "Sigil", 2 => "CombatSigil", _ => null };
            if (wanted is not null) {
                Set(obj, wanted, edit.Stash.TryGetValue(name + "/" + wanted, out var back) ? back.DeepClone()
                    : wanted == "Sigil" ? DefaultSigil() : new JsonObject { ["SigilType"] = AreaCatalog.COMBAT_SIGIL_TYPES[0], ["Radius"] = 0 });
            }

            edit.Changed();
            RerenderArea(edit);
        };

        var body = new StackPanel {
            Spacing = FIELD_SPACING,
            Children = {
                Field("Name", "Its own name in this area (it must be different from the others). The server gives the object its ID " +
                              "from this name, so renaming it makes the server take it away and put it back.", Text(edit, obj, "Name", "Such as Shadow Portal 2", 360, required: true)),
                Field("Template", "What it is: a template ID from the game. Find searches by name or ID.", TemplateBox(edit, obj, "TemplateID", PickKind.Object, required: true)),
                Field("What it is", "A sigil pad and a battle circle have settings of their own below.", kindBox),
                Field("Where", "Where it stands (X, Y, Z), which way it faces (Rotation in degrees, or Yaw in radians) and its size " +
                               "(1 is its own size). Use my position fills these with where your wizard stands and faces.",
                    PlaceEditor(edit, obj, () => edit.Zone, withScale: true)),
                Field("Start state", "The state it starts in, such as Glowy, Off or Red (a sigil's red Advanced Mode glow). Empty: the template's own (a sigil pad: its Look below).",
                    Text(edit, obj, "StartState", "Empty for its own", 200)),
                Field("Notes", "Your own notes.", Text(edit, obj, "Notes", "", 560, multiline: true)),
            },
        };
        if (sigil is not null) {
            body.Children.Add(SubPart(SigilTile(SIGIL, AreaIcon.Sigil, 24), "Sigil pad", Timing.Live, SigilFields(edit, obj, sigil), SIGIL));
        }

        if (circle is not null) {
            body.Children.Add(SubPart(Tile(Glyph(AreaIcon.CombatCircle, COMBAT, 14), COMBAT, 24), "Battle circle", Timing.Live, new StackPanel {
                Spacing = FIELD_SPACING,
                Children = {
                    Field("Circle type", "The circle's look and its number of places (the game's Sigils/ name).",
                        Suggest(edit, circle, "SigilType", AreaCatalog.COMBAT_SIGIL_TYPES, "Such as CombatSigil_4EnemySlots", 360)),
                    Field("Join distance", "How near a player must be to join a battle here. 0: 600, the usual.", Number(edit, circle, "Radius", "0", 100)),
                    Field("Duel", $"With circle type {AreaCatalog.PVP_SIGIL_TYPE} this is a duel circle: wizards who stand on its " +
                                  "two sides for a few seconds duel each other. Choose how they fight.", DuelStyleBox(edit, circle)),
                },
            }, COMBAT));
        }

        if (edit.Hidden.Contains(name)) {
            body.Children.Insert(0, Note($"\"{name}\" was taken out with the old live editor, so the server leaves it out.", Row(
                Action(IconButton("Show it again", AreaIcon.Back), () => {
                    CustomPlacements.Set(_manager.CustomsFolder, edit.Room, name, null);
                    edit.Hidden.Remove(name);
                    RerenderArea(edit);
                    Say($"\"{name}\" is back. The server puts it in {edit.Zone} now.");

                    return Task.CompletedTask;
                }))));
        }

        body.Children.Add(Row(
            Action(IconButton("Duplicate", AreaIcon.Copy), () => {
                var copy = (JsonObject) obj.DeepClone();
                Set(copy, "Name", UniqueName(edit, name + " copy"));

                return AddEntry(edit, objects, copy, "/objects/");
            }),
            Danger(Action(IconButton("Remove from the area", AreaIcon.Delete), () => RemoveEntry(edit, objects, obj)))));

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(kind == 1 ? SigilTile(SIGIL, AreaIcon.Sigil, 26) : Tile(Glyph(kind == 2 ? AreaIcon.CombatCircle : AreaIcon.Objects, color, 15), color, 26));
        header.Children.Add(new TextBlock { Text = name.Length > 0 ? name : "(no name)", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(new TextBlock { Text = templateName, Foreground = MUTED, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 });
        if (edit.Hidden.Contains(name)) {
            header.Children.Add(WithCenter(Count("taken out")));
        }

        return OpenState(Item(new Expander { Header = header, Content = body, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, BorderThickness = new Thickness(0) }, color),
            edit.Area.File + "/objects/" + name);
    }

    private Control SigilFields(AreaEdit edit, JsonObject obj, JsonObject sigil) => new StackPanel {
        Spacing = FIELD_SPACING,
        Children = {
            Sub("A pad a team stands on to be sent somewhere together, like a dungeon's sigil."),
            Field("Pad type", "The pad's shape and how many players it holds (the game's Sigils/ name).",
                Suggest(edit, sigil, "SigilType", AreaCatalog.SIGIL_TYPES, "Such as MinigameSemiCircleSigil4Actor", 360)),
            Field("Pad tag", "The pad's second tag, which the game shows. Usually the name of the sigil it copies.",
                Text(edit, sigil, "Tag", "Such as Street 3 Tower 1 Instance Sigil", 360)),
            Field("Pad size", "How near players must stand. 0: the pad's own size.", Number(edit, sigil, "Radius", "0", 100)),
            Field("Sends to zone", "The zone the team is sent to.", ZoneBox(edit, sigil, "DestinationZone")),
            Field("Arrives at", "Where in that zone: X,Y,Z,Yaw, or a named place there. Named place lists the zone's own spots.",
                LocEditor(edit, sigil, "DestinationLoc", () => CustomArea.Text(sigil, "DestinationZone"))),
            Field("Into variant", "Empty: the zone itself. A name: a fresh private copy loaded with that variant's area.",
                VariantBox(edit, sigil)),
            Field("Look", "How the pad glows. Advanced Mode is the red glow the game shows for a dungeon's harder Advanced Mode. " +
                          "Only the 4 player pad (107081) and the Skeleton Key sigil (1313007) have it; other pads stay as they are.",
                LookBox(edit, obj, sigil)),
        },
    };

    // How a duel circle's wizards fight; the file keeps Combat: Turns or Together.
    private Control DuelStyleBox(AreaEdit edit, JsonObject circle) {
        var styles = new[] { ("Take turns (the PvP way)", "Turns"), ("Both sides at once (like a monster battle)", "Together") };
        var now = CustomArea.Text(circle, "Combat").Trim();
        var box = new ComboBox {
            Width = 360, ItemsSource = styles.Select(x => x.Item1).ToList(),
            SelectedIndex = now.Equals("Together", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
        };
        box.SelectionChanged += (_, _) => {
            if (box.SelectedIndex >= 0) {
                Set(circle, "Combat", styles[box.SelectedIndex].Item2);
                edit.Changed();
            }
        };

        return box;
    }

    // The pad's look in plain words; the file keeps the state the server sends (LitState: Glowy, Red or Off).
    private Control LookBox(AreaEdit edit, JsonObject obj, JsonObject sigil) {
        var looks = AreaCatalog.SIGIL_LOOKS.ToList();
        var now = CustomArea.Text(sigil, "LitState").Trim();
        if (now.Replace(" ", "").ToLowerInvariant() is "advanced" or "advancedmode" or "advance" or "advancemode") {
            now = "Red";
        }

        var index = now.Length == 0 ? 0 : looks.FindIndex(l => l.State.Equals(now, StringComparison.OrdinalIgnoreCase));
        if (index < 0) {
            looks.Add(($"Its own state: {now}", now));
            index = looks.Count - 1;
        }

        var box = new ComboBox { Width = 300, ItemsSource = looks.Select(l => l.Label).ToList(), SelectedIndex = index };
        var warning = new TextBlock { Foreground = MUTED, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 640 };

        void Warn() {
            var template = (ulong) CustomArea.Number(obj, "TemplateID");
            var red = box.SelectedIndex >= 0 && looks[box.SelectedIndex].State == "Red";
            warning.Text = red && !AreaCatalog.ADVANCED_LOOK_TEMPLATES.Contains(template)
                ? "This pad's template has no red glow, so the game keeps it as it is. Use template 107081 (the 4 player pad) for Advanced Mode."
                : "";
            warning.IsVisible = warning.Text.Length > 0;
        }

        box.SelectionChanged += (_, _) => {
            if (box.SelectedIndex < 0) {
                return;
            }

            Set(sigil, "LitState", looks[box.SelectedIndex].State);
            Warn();
            edit.Changed();
        };
        Warn();

        return new StackPanel { Spacing = 2, Children = { box, warning } };
    }

    private Control VariantBox(AreaEdit edit, JsonObject sigil) {
        var known = CustomArea.All(AreaFolder).Where(a => a.Variant.Length > 0).Select(a => a.Variant).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var box = Suggest(edit, sigil, "Variant", known, "Empty for the zone itself", 260);
        var make = Action(IconButton("Make this variant's area", AreaIcon.Add), () => {
            var zone = CustomArea.Text(sigil, "DestinationZone").Trim();
            var variant = CustomArea.Text(sigil, "Variant").Trim();
            if (zone.Length == 0 || variant.Length == 0) {
                throw new PanelException("Fill in Sends to zone and Into variant first.");
            }

            if (File.Exists(Path.Combine(AreaFolder, AreaFiles.FileNameOf(zone, variant)))) {
                throw new PanelException($"{AreaTitle(zone, variant)} already has its area. Open it below.");
            }

            AreaFiles.Create(AreaFolder, _manager.CustomsFolder, zone, variant, null);
            _areaOpen[Path.Combine(AreaFolder, AreaFiles.FileNameOf(zone, variant))] = true;
            RenderAreas();
            Say($"Made the area for {AreaTitle(zone, variant)}. Save this area too, so the sigil sends teams there.");

            return Task.CompletedTask;
        });

        return Row(box, make);
    }

    // ---------------------------------------------------------------------
    // Creatures and spawn swaps
    // ---------------------------------------------------------------------

    private Control CreaturesSection(AreaEdit edit) {
        var creatures = Arr(edit.Work, "Creatures");
        var body = new StackPanel { Spacing = FIELD_SPACING };
        body.Children.Add(Sub("Creatures the server adds to the zone. Each stands still at its place, fights like the zone's own creatures " +
                              "and, in a private copy, does not come back once defeated. Added, changed and removed ones count as soon as you save."));
        foreach (var creature in creatures.OfType<JsonObject>().ToList()) {
            var id = (ulong) CustomArea.Number(creature, "TemplateID");
            var name = CustomArea.Text(creature, "Name");
            var content = new StackPanel {
                Spacing = FIELD_SPACING,
                Children = {
                    Field("Name", "Its own name in this area, different from the others.", Text(edit, creature, "Name", "Such as Guard 1", 300, required: true)),
                    Field("Creature", "Which creature. Find searches by name or ID.", TemplateBox(edit, creature, "TemplateID", PickKind.Creature, required: true)),
                    Field("Spawns as", "One of your Custom mobs in its place, or the creature itself.", CreatureCustomBox(edit, creature)),
                    Field("Where", "Where it stands and which way it faces.", PlaceEditor(edit, creature, () => edit.Zone, withScale: false)),
                    Field("Notes", "Your own notes.", Text(edit, creature, "Notes", "", 560, multiline: true)),
                    Row(Danger(Action(IconButton("Remove", AreaIcon.Delete), () => RemoveEntry(edit, creatures, creature)))),
                },
            };
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(CreatureIcon(id, 24));
            header.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(new TextBlock { Text = TemplateName(id, PickKind.Creature), Foreground = MUTED, VerticalAlignment = VerticalAlignment.Center });
            body.Children.Add(OpenState(Item(new Expander { Header = header, Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, BorderThickness = new Thickness(0) }, CREATURES),
                edit.Area.File + "/creatures/" + name));
        }

        body.Children.Add(Row(Action(IconButton("Add a creature", AreaIcon.Add), () => AddEntry(edit, creatures, new JsonObject {
            ["Name"] = UniqueName(edit, "New creature"), ["TemplateID"] = 0, ["X"] = 0, ["Y"] = 0, ["Z"] = 0, ["Yaw"] = 0,
        }, "/creatures/"))));

        return OpenState(Section(SectionHeader(Tile(Glyph(AreaIcon.Creatures, CREATURES), CREATURES), "Creatures", creatures.Count.ToString(CultureInfo.InvariantCulture), Timing.Live),
            body, CREATURES, false), edit.Area.File + "/creatures");
    }

    // Which of the customs a creature of the area spawns as (its Custom); none is the creature itself.
    private Control CreatureCustomBox(AreaEdit edit, JsonObject creature) {
        var customs = CustomMob.All(_manager.CustomsFolder);
        var now = (int) CustomArea.Number(creature, "Custom");
        var items = new List<string> { "The creature itself" };
        items.AddRange(customs.Select(c => $"Custom {c.Id}: {c.Shown}"));
        if (now > 0 && customs.All(c => c.Id != now)) {
            items.Add($"Custom {now} (missing)");
        }

        var box = new ComboBox {
            Width = 300, ItemsSource = items,
            SelectedIndex = now <= 0 ? 0 : customs.FindIndex(c => c.Id == now) is var i and >= 0 ? i + 1 : items.Count - 1,
        };
        box.SelectionChanged += (_, _) => {
            if (box.SelectedIndex == 0) {
                creature.Remove("Custom");
            }
            else if (box.SelectedIndex > 0 && box.SelectedIndex <= customs.Count) {
                var custom = customs[box.SelectedIndex - 1];
                Set(creature, "Custom", custom.Id);
                if (CustomArea.Number(creature, "TemplateID") <= 0 && ulong.TryParse(custom.BaseId, NumberStyles.None, CultureInfo.InvariantCulture, out var baseId)) {
                    Set(creature, "TemplateID", baseId);
                }
            }
            else {
                return;
            }

            edit.Changed();
        };

        return box;
    }

    private Control SwapsSection(AreaEdit edit) {
        var swaps = Obj(edit.Work, "ReplaceSpawns");
        var customs = CustomMob.All(_manager.CustomsFolder);
        var body = new StackPanel { Spacing = FIELD_SPACING };
        body.Children.Add(Sub("Creatures that this area's spawners bring in as one of your customs instead (Custom mobs above), such as " +
                              "Malistaire the Undying as your custom 1. The Boss of this room choice in Battles wins over this."));
        var rows = new StackPanel { Spacing = FIELD_SPACING };
        foreach (var (template, value) in swaps.ToList()) {
            rows.Children.Add(SwapRow(edit, swaps, template, value is JsonValue v && int.TryParse(v.ToJsonString(), out var n) ? n : 0, customs));
        }

        body.Children.Add(rows);
        body.Children.Add(Row(Action(IconButton("Add a spawn swap", AreaIcon.Add), () => {
            var key = "0";
            for (var i = 1; swaps.ContainsKey(key); i++) {
                key = (-i).ToString(CultureInfo.InvariantCulture);
            }

            swaps[key] = 1;
            edit.Changed();
            RerenderArea(edit);

            return Task.CompletedTask;
        })));

        return OpenState(Section(SectionHeader(Tile(Glyph(AreaIcon.Swap, CREATURES), CREATURES), "Spawn as a custom", swaps.Count.ToString(CultureInfo.InvariantCulture), Timing.NextUse),
            body, CREATURES, false), edit.Area.File + "/swaps");
    }

    private Control SwapRow(AreaEdit edit, JsonObject swaps, string template, int custom, List<CustomMob> customs) {
        var key = template;
        var holder = new JsonObject { ["T"] = ulong.TryParse(template, out var t) ? t : 0 };
        var templateBox = TemplateBox(edit, holder, "T", PickKind.Creature, required: true, changed: () => {
            var now = ((ulong) CustomArea.Number(holder, "T")).ToString(CultureInfo.InvariantCulture);
            if (now == key || swaps.ContainsKey(now)) {
                return;
            }

            var value = swaps[key]?.DeepClone();
            swaps.Remove(key);
            swaps[now] = value;
            key = now;
        });
        var items = new List<string>();
        var ids = new List<int>();
        void Fill() {
            items.Clear();
            ids.Clear();
            foreach (var c in customs.Where(c => c.BaseId == key)) {
                items.Add($"Custom {c.Id}: {c.Shown}");
                ids.Add(c.Id);
            }

            if (custom > 0 && !ids.Contains(custom)) {
                items.Add($"Custom {custom} ({(customs.Any(c => c.Id == custom) ? "made from another creature" : "missing")})");
                ids.Add(custom);
            }
        }

        Fill();
        var combo = new ComboBox { Width = 300, ItemsSource = items.ToList(), SelectedIndex = ids.IndexOf(custom), PlaceholderText = "No custom of this creature yet" };
        combo.SelectionChanged += (_, _) => {
            if (combo.SelectedIndex >= 0 && combo.SelectedIndex < ids.Count) {
                swaps[key] = ids[combo.SelectedIndex];
                edit.Changed();
            }
        };

        return Item(new StackPanel {
            Spacing = FIELD_SPACING,
            Children = {
                Field("Creature", "The creature the spawners would bring in.", templateBox),
                Field("Spawns as", "Which of your customs comes in its place. Make one under Custom mobs first.", combo),
                Row(Danger(Action(IconButton("Remove", AreaIcon.Delete), () => {
                    swaps.Remove(key);
                    edit.Changed();
                    RerenderArea(edit);

                    return Task.CompletedTask;
                }))),
            },
        }, CREATURES);
    }

    // ---------------------------------------------------------------------
    // Encounters
    // ---------------------------------------------------------------------

    private Control EncountersSection(AreaEdit edit) {
        var encounters = Arr(edit.Work, "Encounters");
        var rooms = SafeRooms();
        var customs = CustomMob.All(_manager.CustomsFolder);
        var body = new StackPanel { Spacing = FIELD_SPACING };
        body.Children.Add(Sub("Battles that get extra enemies: when a battle against the boss starts here, some of its helpers join on its " +
                              "side from the first round. The zone's own helpers that are not fighting come first, then new ones are " +
                              "spawned for the rest."));
        foreach (var encounter in encounters.OfType<JsonObject>().ToList()) {
            var boss = (ulong) CustomArea.Number(encounter, "Boss");
            var minions = Arr(encounter, "Minions");
            var content = new StackPanel {
                Spacing = FIELD_SPACING,
                Children = {
                    Field("Boss", "The creature whose battle this is. Find searches by name or ID.", TemplateBox(edit, encounter, "Boss", PickKind.Creature, required: true)),
                    Field("Helpers", "Creatures that can join the boss. Each is a different creature.", IdList(edit, minions, PickKind.Creature, null)),
                    Field("How many join", $"How many different helpers join, from the list. 0: none. At most {minions.Count}.",
                        Number(edit, encounter, "MinionCount", "0", 100, integer: true)),
                    Field("Join distance", "How near the battle circle the zone's own helpers must be to join. 0: anywhere in the zone.",
                        Number(edit, encounter, "JoinRadius", "0", 100)),
                    Field("Notes", "Your own notes.", Text(edit, encounter, "Notes", "", 560, multiline: true)),
                },
            };
            if (boss > 0) {
                content.Children.Insert(1, SubPart(Tile(Glyph(AreaIcon.Boss, SIGIL, 14), SIGIL, 24), "Boss of this room", Timing.NextUse, new StackPanel {
                    Spacing = FIELD_SPACING,
                    Children = {
                        Sub("Who fights here: the game's creature, or one of your customs made from it. Saved at once; it counts from the " +
                            "next time a team goes in."),
                        BossPicker(edit, boss.ToString(CultureInfo.InvariantCulture), rooms, customs),
                    },
                }, SIGIL));
            }

            content.Children.Add(Row(Danger(Action(IconButton("Remove this battle", AreaIcon.Delete), () => RemoveEntry(edit, encounters, encounter)))));
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            header.Children.Add(CreatureIcon(boss, 24));
            header.Children.Add(new TextBlock { Text = "Battle against " + (boss > 0 ? TemplateName(boss, PickKind.Creature) : "(no boss yet)"), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var count = (int) CustomArea.Number(encounter, "MinionCount");
            header.Children.Add(WithCenter(Count($"{count} of {minions.Count} helpers join")));
            body.Children.Add(OpenState(Item(new Expander { Header = header, Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, BorderThickness = new Thickness(0) }, ENCOUNTERS),
                edit.Area.File + "/encounters/" + boss));
        }

        body.Children.Add(Row(Action(IconButton("Add a battle", AreaIcon.Add), () => AddEntry(edit, encounters, new JsonObject {
            ["Boss"] = 0, ["Minions"] = new JsonArray(), ["MinionCount"] = 0, ["JoinRadius"] = 0,
        }, "/encounters/"))));

        return OpenState(Section(SectionHeader(Tile(Glyph(AreaIcon.Encounters, ENCOUNTERS), ENCOUNTERS), "Battles, helpers and the boss", encounters.Count.ToString(CultureInfo.InvariantCulture), Timing.NextUse),
            body, ENCOUNTERS, false), edit.Area.File + "/encounters");
    }

    // Who a room's boss is: the game's creature, or one of the customs made from it (Customs/Rooms.json, saved at once).
    private Control BossPicker(AreaEdit edit, string boss, Dictionary<string, Dictionary<string, int>> rooms, List<CustomMob> customs) {
        var choices = customs.Where(c => c.BaseId == boss).ToList();
        var items = new List<string> { $"{CreatureName(boss)} (the game's)" };
        items.AddRange(choices.Select(c => $"Custom {c.Id}: {c.Shown}"));
        var fromFile = BossChanges.FindIn(edit.Work, "ReplaceSpawns") is JsonObject replace
                       && BossChanges.FindIn(replace, boss) is JsonValue v && int.TryParse(v.ToJsonString(), out var f) ? f : 0;
        var room = edit.Area.Room;
        var set = rooms.TryGetValue(room, out var swaps) && swaps.TryGetValue(boss, out var s) ? s : 0;
        var now = set != 0 ? Math.Max(0, set) : fromFile;
        var combo = new ComboBox { Width = 360, ItemsSource = items, SelectedIndex = Math.Max(0, choices.FindIndex(c => c.Id == now) + 1) };
        if (now > 0 && choices.All(c => c.Id != now)) {
            items.Add($"Custom {now} (missing)");
            combo.ItemsSource = items.ToList();
            combo.SelectedIndex = items.Count - 1;
        }

        combo.SelectionChanged += async (_, _) => await Guard(() => {
            var index = combo.SelectedIndex;
            if (index < 0 || index > choices.Count) {
                return Task.CompletedTask;
            }

            var custom = index == 0 ? (fromFile > 0 ? -1 : 0) : choices[index - 1].Id;
            CustomRooms.Set(_manager.CustomsFolder, room, boss, custom);
            RenderCustoms();
            Say(index == 0
                ? $"{RoomName(room)} has the game's {CreatureName(boss)} again, from the next time a team goes in."
                : $"{RoomName(room)} has custom {choices[index - 1].Id} as its boss from the next time a team goes in.");

            return Task.CompletedTask;
        });

        return Field("Boss of this room", choices.Count == 0 ? "Make a custom of this creature under Custom mobs to pick it here." : "", combo);
    }

    // ---------------------------------------------------------------------
    // Distinct spawners, left-out objects and the exit
    // ---------------------------------------------------------------------

    private Control DistinctSection(AreaEdit edit) {
        var spawners = Arr(edit.Work, "DistinctSpawners");
        var body = new StackPanel {
            Spacing = FIELD_SPACING,
            Children = {
                Sub("Spawners of the zone (by name) that never have two of the same creature out at once: each spawn is picked among " +
                    "the creatures not already out. Such a spawner fills up a few seconds after the zone loads."),
                Field("Spawners", "The names of the zone's own spawners. Pick lists the ones in the game files.",
                    TextList(edit, spawners, "Spawner name, such as MalMinions", () => _areaCatalog?.SpawnersIn(edit.Zone) ?? [])),
            },
        };

        return OpenState(Section(SectionHeader(Tile(Glyph(AreaIcon.Distinct, DISTINCT), DISTINCT), "Spawners with no repeats", spawners.Count.ToString(CultureInfo.InvariantCulture), Timing.NextUse),
            body, DISTINCT, false), edit.Area.File + "/distinct");
    }

    private Control RemovedSection(AreaEdit edit) {
        var removed = Arr(edit.Work, "Remove");
        var body = new StackPanel {
            Spacing = FIELD_SPACING,
            Children = {
                Sub("The zone's own objects and spawners of these templates are left out, such as a boss this area places itself so " +
                    "there are not two."),
                Field("Left out", "Templates to leave out. From the zone lists the zone's own objects.", IdList(edit, removed, PickKind.Any, () => edit.Zone)),
            },
        };

        return OpenState(Section(SectionHeader(Tile(Glyph(AreaIcon.Removed, REMOVED), REMOVED), "Left out of the zone", removed.Count.ToString(CultureInfo.InvariantCulture), Timing.Reload),
            body, REMOVED, false), edit.Area.File + "/removed");
    }

    private Control ExitSection(AreaEdit edit) {
        var body = new StackPanel { Spacing = FIELD_SPACING };
        body.Children.Add(Sub("Where the zone's ways out go instead, such as back to the sigil that sent the team in."));
        if (BossChanges.FindIn(edit.Work, "Exit") is not JsonObject exit) {
            body.Children.Add(Sub("This area keeps the zone's own exits."));
            body.Children.Add(Row(Action(IconButton("Give it an exit of its own", AreaIcon.Add), () => {
                Set(edit.Work, "Exit", new JsonObject { ["Zone"] = "", ["Loc"] = "", ["Triggers"] = new JsonArray(), ["Doors"] = new JsonArray() });
                _areaOpen[edit.Area.File + "/exit"] = true;
                edit.Changed();
                RerenderArea(edit);

                return Task.CompletedTask;
            })));
        }
        else {
            body.Children.Add(Field("Goes to zone", "The zone leaving sends players to.", ZoneBox(edit, exit, "Zone", required: true)));
            body.Children.Add(Field("Arrives at", "Where in that zone: X,Y,Z,Yaw, or a named place there.", LocEditor(edit, exit, "Loc", () => CustomArea.Text(exit, "Zone"))));
            body.Children.Add(Field("Ways out", "The zone's teleports (trigger or door names) that go there. Empty: every one that leaves the zone.",
                TextList(edit, Arr(exit, "Triggers"), "Trigger name, such as BacktoLower", () => _areaCatalog?.TeleportsIn(edit.Zone) ?? [])));
            body.Children.Add(Field("Doors", "Objects that become press-X doors to the exit, for one the server has no teleport for (such as a " +
                                             "portal that appears after a boss). These count from the next time the zone loads.",
                IdList(edit, Arr(exit, "Doors"), PickKind.Any, () => edit.Zone)));
            body.Children.Add(Row(Danger(Action(IconButton("Remove the exit", AreaIcon.Delete), () => {
                Set(edit.Work, "Exit", null);
                edit.Changed();
                RerenderArea(edit);

                return Task.CompletedTask;
            }))));
        }

        return OpenState(Section(SectionHeader(Tile(Glyph(AreaIcon.Exit, EXIT), EXIT), "Exit", null, Timing.NextUse), body, EXIT, false),
            edit.Area.File + "/exit");
    }

    private Control AdvancedSection(AreaEdit edit) {
        var box = new TextBox {
            AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = MONO, MinHeight = 160, MaxHeight = 420,
            Text = AreaFiles.Pretty(edit.Work),
        };
        var body = new StackPanel {
            Spacing = FIELD_SPACING,
            Children = {
                Sub("The whole area as the file's text, for anything the boxes above do not cover. Use this text puts it into the boxes " +
                    "(then Save area writes it)."),
                box,
                Row(
                    Action(IconButton("Use this text", AreaIcon.Code), () => {
                        JsonObject parsed;
                        try {
                            parsed = JsonNode.Parse(box.Text ?? "", documentOptions: AreaFiles.READ) as JsonObject
                                     ?? throw new PanelException("The text is not one { ... } block.");
                        }
                        catch (System.Text.Json.JsonException ex) {
                            throw new PanelException($"The text has a mistake: {ex.Message}");
                        }

                        edit.Work = parsed;
                        edit.Errors.Clear();
                        edit.Changed();
                        RerenderArea(edit);
                        Say("The text is in the boxes. Press Save area to keep it.");

                        return Task.CompletedTask;
                    }),
                    Action(IconButton("Show the boxes' text", AreaIcon.Back), () => {
                        box.Text = AreaFiles.Pretty(edit.Work);

                        return Task.CompletedTask;
                    })),
            },
        };

        return OpenState(Section(SectionHeader(Tile(Glyph(AreaIcon.Code, ADVANCED), ADVANCED), "The file's text", null, null), body, ADVANCED, false),
            edit.Area.File + "/advanced");
    }

    // ---------------------------------------------------------------------
    // Saving, making and deleting
    // ---------------------------------------------------------------------

    private async Task SaveAreaAsync(AreaEdit edit) {
        if (edit.Errors.Count > 0) {
            throw new PanelException(edit.Errors.Values.First());
        }

        var fixes = FixCircles(edit.Work);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in new[] { "Objects", "Creatures" }) {
            foreach (var o in Entries(edit.Work, part)) {
                var name = CustomArea.Text(o, "Name").Trim();
                if (name.Length == 0) {
                    throw new PanelException($"Every one of the {part.ToLowerInvariant()} needs a name.");
                }

                if (!names.Add(name)) {
                    throw new PanelException($"Two things are called \"{name}\". Each needs a name of its own.");
                }

                if (CustomArea.Number(o, "TemplateID") <= 0) {
                    throw new PanelException($"\"{name}\" needs a template. Press Find beside Template.");
                }
            }
        }

        if (Entries(edit.Work, "Encounters").Any(e => CustomArea.Number(e, "Boss") <= 0)) {
            throw new PanelException("A battle has no boss yet. Pick one, or remove the battle.");
        }

        if (BossChanges.FindIn(edit.Work, "ReplaceSpawns") is JsonObject swaps && swaps.Any(s => !ulong.TryParse(s.Key, out var t) || t == 0)) {
            throw new PanelException("A spawn swap has no creature yet. Pick one, or remove it.");
        }

        if (BossChanges.FindIn(edit.Work, "Exit") is JsonObject exit && CustomArea.Text(exit, "Zone").Trim().Length == 0) {
            throw new PanelException("The exit needs a zone. Fill in Goes to zone, or remove the exit.");
        }

        var before = Clean(edit.Area.Json);
        var cleaned = Clean(edit.Work);
        string Part(JsonObject json, params string[] keys) => string.Join("|", keys.Select(k => BossChanges.FindIn(json, k)?.ToJsonString() ?? ""));
        var live = new List<string>();
        var next = new List<string>();
        var reload = new List<string>();
        if (Part(before, "Objects") != Part(cleaned, "Objects") || edit.HasPlacements) {
            live.Add("objects");
        }

        if (Part(before, "Zone", "Variant", "Enabled") != Part(cleaned, "Zone", "Variant", "Enabled")) {
            live.Add("where the area is used");
        }

        if (Part(before, "Encounters") != Part(cleaned, "Encounters")) {
            next.Add("battles");
        }

        if (Part(before, "ReplaceSpawns") != Part(cleaned, "ReplaceSpawns")) {
            next.Add("spawn swaps");
        }

        if (Part(before, "DistinctSpawners") != Part(cleaned, "DistinctSpawners")) {
            next.Add("spawners with no repeats");
        }

        if (Part(before, "Exit") != Part(cleaned, "Exit")) {
            next.Add("the exit");
        }

        if (Part(before, "Creatures") != Part(cleaned, "Creatures")) {
            live.Add("creatures");
        }

        if (Part(before, "Remove") != Part(cleaned, "Remove")) {
            reload.Add("left-out objects");
        }

        var oldRoom = edit.Area.Room;
        var file = AreaFiles.Save(AreaFolder, _manager.CustomsFolder, edit.Area.File, cleaned);
        if (edit.HasPlacements) {
            // The moved and added objects are in the file now; taken-out ones stay out.
            foreach (var (name, node) in BossChanges.FindIn(SafePlacements(), oldRoom) as JsonObject ?? []) {
                if (!(node is JsonObject p && BossChanges.FindIn(p, "Removed") is JsonValue r && r.ToJsonString() == "true")) {
                    CustomPlacements.Set(_manager.CustomsFolder, oldRoom, name, null);
                }
            }
        }

        if (!string.Equals(file, edit.Area.File, StringComparison.OrdinalIgnoreCase)) {
            _areaOpen[file] = true;
        }

        _areaEdits.Remove(edit.Area.File);
        RenderAreas();

        var title = AreaTitle(edit.Zone, edit.Variant);
        var parts = new List<string> { $"Saved {title}." };
        if (fixes.Count > 0) {
            parts.Add($"Fixed: {string.Join("; ", fixes)}.");
        }

        if (!InstalledCommands.IsServerRunning(_manager.InstallFolder)) {
            parts.Add("The server is off, so it uses everything when it starts.");
        }
        else {
            if (live.Count > 0) {
                parts.Add($"Changed at once: {string.Join(", ", live)}.");
            }

            if (next.Count > 0) {
                parts.Add($"From their next use: {string.Join(", ", next)}.");
            }

            if (reload.Count > 0) {
                parts.Add($"From the next time the zone loads: {string.Join(", ", reload)} " +
                          (edit.Variant.Length > 0 ? "(leave the room and go in again)." : "(restart the server on the Server tab; for a dungeon, leave and go in again)."));
            }
        }

        if (!string.Equals(oldRoom, edit.Room, StringComparison.OrdinalIgnoreCase) && SafeRooms().ContainsKey(oldRoom)) {
            parts.Add($"The boss picks made for {RoomName(oldRoom)} stay with that name; pick the boss again in Battles.");
        }

        Say(string.Join(" ", parts));
        await Task.CompletedTask;
    }

    private Task MakeAreaAsync() {
        var zone = (_newAreaZone.Text ?? "").Trim();
        var variant = (_newAreaVariant.Text ?? "").Trim();
        if (zone.Length == 0) {
            throw new PanelException("Type the zone first, such as WizardCity/WC_Ravenwood.");
        }

        var areas = CustomArea.All(AreaFolder);
        var from = _newAreaFrom.SelectedIndex > 0 && _newAreaFrom.SelectedIndex <= areas.Count ? areas[_newAreaFrom.SelectedIndex - 1].Json : null;
        var file = AreaFiles.Create(AreaFolder, _manager.CustomsFolder, zone, variant, from);
        _areaOpen[file] = true;
        _newAreaZone.Text = "";
        _newAreaVariant.Text = "";
        RenderAreas();
        Say($"Made the area for {AreaTitle(zone, variant)}. Open it below to add to it." +
            (variant.Length > 0 ? $" Teams get into it through a sigil pad whose Into variant is {variant}." : ""));

        return Task.CompletedTask;
    }

    private async Task MakeVariantAsync(AreaEdit edit) {
        var name = (await AskAsync($"Name the variant of {ZoneDisplay(edit.Zone)}: a private copy with this area's changes, such as MyBossRoom.", false) ?? "").Trim();
        if (name.Length == 0) {
            return;
        }

        var copy = (JsonObject) edit.Work.DeepClone();
        var file = AreaFiles.Create(AreaFolder, _manager.CustomsFolder, edit.Zone, name, copy);
        _areaOpen[file] = true;
        RenderAreas();
        Say($"Made the variant {name} of {edit.Zone}, a copy of this area. Teams get into it through a sigil pad whose Into variant is {name}.");
    }

    private async Task DeleteAreaAsync(AreaEdit edit) {
        if (!await ConfirmAsync($"Delete the area for {AreaTitle(edit.Zone, edit.Variant)}? Its objects leave the game at once and the file is gone.")) {
            return;
        }

        File.Delete(edit.Area.File);
        _areaEdits.Remove(edit.Area.File);
        RenderAreas();
        Say($"The area for {AreaTitle(edit.Zone, edit.Variant)} is deleted.");
    }

    private async Task RestoreAreaAsync(AreaEdit edit) {
        if (!await ConfirmAsync("Put this area back as it was before it was first saved here? The changes made in this tab are lost.")) {
            return;
        }

        AreaFiles.RestoreOriginal(_manager.CustomsFolder, edit.Area.File);
        _areaEdits.Remove(edit.Area.File);
        RenderAreas();
        Say("The area is back as it was. An update can replace it again from now on.");
    }

    private Task AddEntry(AreaEdit edit, JsonArray list, JsonObject entry, string section) {
        list.Add(entry);
        _areaOpen[edit.Area.File + section.TrimEnd('/')] = true;
        var name = CustomArea.Text(entry, "Name");
        _areaOpen[edit.Area.File + section + (name.Length > 0 ? name : "0")] = true;
        edit.Changed();
        RerenderArea(edit);

        return Task.CompletedTask;
    }

    private Task RemoveEntry(AreaEdit edit, JsonArray list, JsonObject entry) {
        list.Remove(entry);
        edit.Changed();
        RerenderArea(edit);

        return Task.CompletedTask;
    }

    private static string UniqueName(AreaEdit edit, string start) {
        var taken = Entries(edit.Work, "Objects").Concat(Entries(edit.Work, "Creatures")).Select(o => CustomArea.Text(o, "Name"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = start;
        for (var i = 2; taken.Contains(name); i++) {
            name = $"{start} {i}";
        }

        return name;
    }

    // ---------------------------------------------------------------------
    // Fields
    // ---------------------------------------------------------------------

    /// <summary>A labelled field: the label on the left, the box, and what it does under the box.</summary>
    private static Control Field(string label, string help, Control box) {
        var right = new StackPanel { Spacing = 2, Children = { box } };
        if (help.Length > 0) {
            right.Children.Add(new TextBlock { Text = help, Foreground = MUTED, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left });
        }

        return new DockPanel {
            Children = {
                WithDock(FieldLabel(label, LABEL_WIDTH), Dock.Left),
                right,
            },
        };
    }

    private static Control Note(string text, Control? action = null) {
        var panel = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap } } };
        if (action is not null) {
            panel.Children.Add(action);
        }

        return new Border { Background = Brush(RELOAD, 30), BorderBrush = Brush(RELOAD, 120), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6), Child = panel };
    }

    private static Control SubPart(Control tile, string title, Timing? timing, Control body, Color color) => new Border {
        BorderBrush = Brush(color, 120), BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(12, 4, 0, 4), Margin = new Thickness(0, 4, 0, 0),
        Child = new StackPanel { Spacing = FIELD_SPACING, Children = { SectionHeader(tile, title, null, timing), body } },
    };

    private static Button IconButton(string text, AreaIcon icon, bool primary = false) {
        var button = primary ? Primary("") : Plain("");
        button.Content = new StackPanel {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Children = { Glyph(icon, primary ? Colors.White : Color.FromRgb(200, 205, 215), 14), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
        };

        return button;
    }

    // Remembers whether a section is open, so it stays so when the tab is drawn again.
    private T OpenState<T>(T control, string key) where T : Control {
        var expander = control as Expander ?? (control as Border)?.Child as Expander;
        if (expander is null) {
            return control;
        }

        if (_areaOpen.TryGetValue(key, out var open)) {
            expander.IsExpanded = open;
        }

        expander.PropertyChanged += (_, e) => {
            if (e.Property == Expander.IsExpandedProperty) {
                _areaOpen[key] = expander.IsExpanded;
            }
        };

        return control;
    }

    private static TextBox Text(AreaEdit edit, JsonObject target, string key, string watermark, double width, bool required = false, bool multiline = false) {
        var box = new TextBox {
            Width = width, Watermark = watermark, Text = CustomArea.Text(target, key), HorizontalAlignment = HorizontalAlignment.Left,
            AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MaxHeight = multiline ? 140 : double.PositiveInfinity,
        };
        box.TextChanged += (_, _) => {
            var text = multiline ? (box.Text ?? "") : (box.Text ?? "").Trim();
            Set(target, key, text.Length == 0 && !required && BossChanges.FindIn(target, key) is null ? null : text);
            edit.Changed();
        };

        return box;
    }

    private static TextBox Number(AreaEdit edit, JsonObject target, string key, string watermark, double width, bool integer = false) {
        var has = BossChanges.FindIn(target, key) is JsonValue;
        var box = new TextBox {
            Width = width, Watermark = watermark, HorizontalAlignment = HorizontalAlignment.Left,
            Text = has ? N(CustomArea.Number(target, key)) : "",
        };
        var normal = box.BorderBrush;
        box.TextChanged += (_, _) => {
            var text = (box.Text ?? "").Trim();
            if (text.Length == 0) {
                Set(target, key, null);
                edit.Errors.Remove(box);
            }
            else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && (!integer || n == Math.Floor(n))) {
                Set(target, key, integer ? JsonValue.Create((long) n) : JsonValue.Create(Math.Round(n, 4)));
                edit.Errors.Remove(box);
            }
            else {
                edit.Errors[box] = integer ? $"\"{text}\" is not a whole number, such as 3." : $"\"{text}\" is not a number, such as -2340 or 0.5.";
            }

            box.BorderBrush = edit.Errors.ContainsKey(box) ? BAD : normal;
            edit.Changed();
        };

        return box;
    }

    private Control ZoneBox(AreaEdit edit, JsonObject target, string key, bool required = false) {
        var box = new AutoCompleteBox {
            Width = 360, Text = CustomArea.Text(target, key), ItemsSource = _areaCatalog?.Zones, FilterMode = AutoCompleteFilterMode.ContainsOrdinal,
            MinimumPrefixLength = 1, MaxDropDownHeight = 280, Watermark = "Such as WizardCity/WC_Ravenwood", HorizontalAlignment = HorizontalAlignment.Left,
        };
        var shown = new TextBlock { Foreground = MUTED, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        void Show() {
            var zone = (box.Text ?? "").Trim();
            shown.Text = _areaCatalog is null || zone.Length == 0 ? ""
                : _areaCatalog.ZoneNames.TryGetValue(zone, out var n) ? n
                : _areaCatalog.Zones.Contains(zone, StringComparer.OrdinalIgnoreCase) ? "" : "Not in this PC's zone lists (check the spelling)";
        }

        Show();
        box.PropertyChanged += (_, e) => {
            if (e.Property != AutoCompleteBox.TextProperty) {
                return;
            }

            var text = (box.Text ?? "").Trim();
            if (text == CustomArea.Text(target, key)) {
                return;
            }

            Set(target, key, text.Length == 0 && !required ? null : text);
            Show();
            edit.Changed();
        };

        return Row(box, shown);
    }

    private TextBox Suggest(AreaEdit edit, JsonObject target, string key, IEnumerable<string> choices, string watermark, double width) {
        var box = Text(edit, target, key, watermark, width);
        var list = new StackPanel { Spacing = 2 };
        var flyout = new Flyout { Content = new ScrollViewer { MaxHeight = 300, Content = list } };
        foreach (var choice in choices) {
            var b = Plain(choice);
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.Click += (_, _) => {
                box.Text = choice;
                flyout.Hide();
            };
            list.Children.Add(b);
        }

        if (list.Children.Count > 0) {
            box.InnerRightContent = new Button { Content = "▾", Padding = new Thickness(8, 0), Flyout = flyout, Background = Brushes.Transparent };
        }

        return box;
    }

    // X, Y, Z, rotation (degrees and yaw) and maybe size, written into the entry as they are typed.
    private Control PlaceEditor(AreaEdit edit, JsonObject target, Func<string> zone, bool withScale) {
        var x = Number(edit, target, "X", "0", 90);
        var y = Number(edit, target, "Y", "0", 90);
        var z = Number(edit, target, "Z", "0", 80);
        var yaw = Number(edit, target, "Yaw", "0", 80);
        var yawValue = CustomArea.Number(target, "Yaw");
        var degrees = new TextBox { Width = 70, Text = Math.Round((yawValue * 180.0 / Math.PI % 360 + 360) % 360, 1).ToString(CultureInfo.InvariantCulture) };
        var syncing = false;
        // Only typing in Rotation changes Yaw (drawing the box must not round the file's yaw).
        degrees.TextChanged += (_, _) => {
            if (!syncing && degrees.IsFocused && double.TryParse(degrees.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) {
                syncing = true;
                yaw.Text = N(d * Math.PI / 180.0);
                syncing = false;
            }
        };
        yaw.TextChanged += (_, _) => {
            if (!syncing && double.TryParse(yaw.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var r)) {
                syncing = true;
                degrees.Text = Math.Round((r * 180.0 / Math.PI % 360 + 360) % 360, 1).ToString(CultureInfo.InvariantCulture);
                syncing = false;
            }
        };

        static Control Labelled(string label, Control box) => WithMargin(new StackPanel {
            Orientation = Orientation.Horizontal, Spacing = 4,
            Children = { WithCenter(new TextBlock { Text = label, Foreground = MUTED }), box },
        }, new Thickness(0, 0, 12, 4));

        var row = new WrapPanel {
            Children = { Labelled("X", x), Labelled("Y", y), Labelled("Z", z), Labelled("Rotation °", degrees), Labelled("Yaw", yaw) },
        };
        if (withScale) {
            row.Children.Add(Labelled("Size", Number(edit, target, "Scale", "1", 60)));
        }

        row.Children.Add(WithMargin(Action(IconButton("Use my position", AreaIcon.Position), () => {
            var (who, px, py, pz, pyaw) = MyPosition(zone());
            x.Text = px;
            y.Text = py;
            z.Text = pz;
            yaw.Text = pyaw;
            Say($"Filled with where {who} stands and faces. Press Save area to put it there.");

            return Task.CompletedTask;
        }), new Thickness(0, 0, 0, 4)));

        return row;
    }

    // "X,Y,Z,Yaw" or a named place, with the zone's named places and Use my position.
    private Control LocEditor(AreaEdit edit, JsonObject target, string key, Func<string> zone) {
        var box = Text(edit, target, key, "X,Y,Z,Yaw or a named place", 300);
        var list = new StackPanel { Spacing = 2 };
        var flyout = new Flyout { Content = new ScrollViewer { MaxHeight = 320, Content = list } };
        flyout.Opening += (_, _) => {
            list.Children.Clear();
            var places = _areaCatalog?.PlacesIn(zone().Trim()) ?? [];
            if (places.Count == 0) {
                list.Children.Add(Sub(_areaCatalog?.HasGameTables == true
                    ? "The game files list no named places for this zone."
                    : "Named places show here once the game files are read (Tools tab, Game files)."));
            }

            foreach (var place in places) {
                var b = Plain($"{place.Name}   ({N(place.X)}, {N(place.Y)}, {N(place.Z)})");
                b.HorizontalAlignment = HorizontalAlignment.Stretch;
                b.Click += (_, _) => {
                    box.Text = place.Name.Contains(' ') || places.Count(p => p.Name == place.Name) > 1 ? place.Loc : place.Name;
                    flyout.Hide();
                };
                list.Children.Add(b);
            }
        };
        var pick = new Button { Content = IconLabel("Named place", AreaIcon.Place), Flyout = flyout };
        var mine = Action(IconButton("Use my position", AreaIcon.Position), () => {
            var (who, px, py, pz, pyaw) = MyPosition(zone().Trim());
            box.Text = $"{px},{py},{pz},{pyaw}";
            Say($"Filled with where {who} stands and faces.");

            return Task.CompletedTask;
        });

        return Row(box, pick, mine);
    }

    private static Control IconLabel(string text, AreaIcon icon) => new StackPanel {
        Orientation = Orientation.Horizontal, Spacing = 6,
        Children = { Glyph(icon, Color.FromRgb(200, 205, 215), 14), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
    };

    // A template ID box with its name, icon and a Find search.
    private Control TemplateBox(AreaEdit edit, JsonObject target, string key, PickKind kind, bool required = false, Action? changed = null) {
        var id = (ulong) CustomArea.Number(target, key);
        var box = new TextBox { Width = 120, Text = id > 0 ? id.ToString(CultureInfo.InvariantCulture) : "", Watermark = "Template ID" };
        var icon = new Border { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 };
        var normal = box.BorderBrush;
        void Show() {
            var now = ulong.TryParse((box.Text ?? "").Trim(), out var n) ? n : 0;
            icon.Child = now == 0 ? null : kind == PickKind.Creature || IsCreature(now) ? CreatureIcon(now, 20) : Glyph(AreaIcon.Objects, OBJECTS, 18);
            name.Text = now == 0 ? "" : TemplateName(now, kind);
            if (now != 0 && name.Text.StartsWith("Not in", StringComparison.Ordinal)) {
                name.Foreground = MUTED;
            }
            else {
                name.ClearValue(TextBlock.ForegroundProperty);
            }
        }

        Show();
        box.TextChanged += (_, _) => {
            var text = (box.Text ?? "").Trim();
            if (text.Length == 0) {
                Set(target, key, required ? JsonValue.Create(0UL) : null);
                edit.Errors.Remove(box);
            }
            else if (ulong.TryParse(text, out var n)) {
                Set(target, key, JsonValue.Create(n));
                edit.Errors.Remove(box);
            }
            else {
                edit.Errors[box] = $"\"{text}\" is not a template ID. Press Find to search by name.";
            }

            box.BorderBrush = edit.Errors.ContainsKey(box) ? BAD : normal;
            Show();
            changed?.Invoke();
            edit.Changed();
        };

        var find = new Button { Content = IconLabel("Find", AreaIcon.Search), Flyout = PickerFlyout(kind, null, picked => box.Text = picked.ToString(CultureInfo.InvariantCulture)) };

        return Row(box, find, icon, name);
    }

    // A search for templates by name or ID; with a zone, also the zone's own objects.
    private Flyout PickerFlyout(PickKind kind, Func<string>? zone, Action<ulong> picked) {
        var search = new TextBox { Width = 420, Watermark = kind == PickKind.Creature ? "Search creatures by name or ID" : "Search by name or ID" };
        var results = new StackPanel { Spacing = 2 };
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        void Fill() {
            results.Children.Clear();
            var query = (search.Text ?? "").Trim();
            var rows = new List<(ulong Id, Control Icon, string Text)>();
            if (query.Length == 0 && zone?.Invoke() is { Length: > 0 } z && _areaCatalog is not null) {
                foreach (var g in _areaCatalog.ObjectsIn(z).GroupBy(o => o.TemplateId).Take(60)) {
                    rows.Add((g.Key, Glyph(AreaIcon.Objects, OBJECTS, 16), $"{g.First().Name}  ·  ID {g.Key}  ·  {g.Count()} in the zone"));
                }
            }

            // With nothing typed (and no objects of the zone), the creatures and objects are listed by name.
            if (rows.Count == 0) {
                if (kind != PickKind.Object && _bossCatalog is { Missing: null } bosses) {
                    foreach (var c in bosses.Search(query, bossesOnly: false, new HashSet<string>(), 12, out _)) {
                        rows.Add((ulong.Parse(c.Id, CultureInfo.InvariantCulture), CreatureIcon(ulong.Parse(c.Id, CultureInfo.InvariantCulture), 18),
                            string.Join("  ·  ", new[] { c.Name, $"ID {c.Id}", c.Level > 0 ? $"level {c.Level}" : "", c.School, c.IsBoss ? "boss" : "" }.Where(t => t.Length > 0))));
                    }
                }

                if (kind != PickKind.Creature && _areaCatalog is not null) {
                    foreach (var t in _areaCatalog.Search(query, 14, t => t.Kind != "Creature" || kind == PickKind.Any)) {
                        if (rows.Any(r => r.Id == t.Id)) {
                            continue;
                        }

                        rows.Add((t.Id, t.Kind == "Sigil or trigger" ? Glyph(AreaIcon.Sigil, SIGIL, 16) : Glyph(AreaIcon.Objects, OBJECTS, 16),
                            $"{t.Label}  ·  ID {t.Id}  ·  {t.Kind}"));
                    }
                }

                if (rows.Count == 0) {
                    results.Children.Add(Sub(_areaCatalog?.HasGameTables == true || _bossCatalog?.Missing is null
                        ? "Nothing matches."
                        : "Nothing matches. Start the server once so it makes its ID lists, and read the game files on the Tools tab for objects."));
                }
            }

            foreach (var (id, icon, text) in rows) {
                var b = new Button {
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent,
                    Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { icon, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } } },
                };
                b.Click += (_, _) => {
                    picked(id);
                    flyout.Hide();
                };
                results.Children.Add(b);
            }
        }

        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        delay.Tick += (_, _) => {
            delay.Stop();
            Fill();
        };
        search.TextChanged += (_, _) => {
            delay.Stop();
            delay.Start();
        };
        flyout.Opening += (_, _) => Fill();
        flyout.Content = new StackPanel { Spacing = 6, Width = 460, Children = { search, new ScrollViewer { MaxHeight = 360, Content = results } } };

        return flyout;
    }

    // A list of template IDs (helpers, left-out objects, doors): each with its name and a Remove, and Add.
    private Control IdList(AreaEdit edit, JsonArray list, PickKind kind, Func<string>? zone) {
        var panel = new StackPanel { Spacing = 4 };
        for (var i = 0; i < list.Count; i++) {
            var index = i;
            var holder = new JsonObject { ["T"] = list[i] is JsonValue v && ulong.TryParse(v.ToJsonString().Trim('"'), out var n) ? n : 0 };
            var row = TemplateBox(edit, holder, "T", kind, required: true, changed: () => list[index] = JsonValue.Create((ulong) CustomArea.Number(holder, "T")));
            panel.Children.Add(Row(row, Danger(Action(IconButton("Remove", AreaIcon.Delete), () => {
                list.RemoveAt(index);
                edit.Changed();
                RerenderArea(edit);

                return Task.CompletedTask;
            }))));
        }

        var add = new Button {
            Content = IconLabel(zone is null ? "Add" : "Add (search, or pick from the zone)", AreaIcon.Add),
            Flyout = PickerFlyout(kind, zone, picked => {
                if (list.Any(e => e?.ToJsonString() == picked.ToString(CultureInfo.InvariantCulture))) {
                    Say("That one is in the list already.");

                    return;
                }

                list.Add(picked);
                edit.Changed();
                RerenderArea(edit);
            }),
        };
        panel.Children.Add(add);

        return panel;
    }

    // A list of names (spawners, triggers): each a box with Remove, plus Add and the game files' names to pick from.
    private Control TextList(AreaEdit edit, JsonArray list, string watermark, Func<List<string>> suggestions) {
        var panel = new StackPanel { Spacing = 4 };
        for (var i = 0; i < list.Count; i++) {
            var index = i;
            var box = new TextBox { Width = 300, Text = (list[i] as JsonValue)?.ToString() ?? "", Watermark = watermark };
            box.TextChanged += (_, _) => {
                list[index] = (box.Text ?? "").Trim();
                edit.Changed();
            };
            panel.Children.Add(Row(box, Danger(Action(IconButton("Remove", AreaIcon.Delete), () => {
                list.RemoveAt(index);
                edit.Changed();
                RerenderArea(edit);

                return Task.CompletedTask;
            }))));
        }

        var picks = new StackPanel { Spacing = 2 };
        var flyout = new Flyout { Content = new ScrollViewer { MaxHeight = 320, Content = picks } };
        flyout.Opening += (_, _) => {
            picks.Children.Clear();
            var names = suggestions();
            if (names.Count == 0) {
                picks.Children.Add(Sub("The game files list none for this zone (or they have not been read yet on the Tools tab). Use Add an empty one."));
            }

            foreach (var entry in names) {
                var name = entry.Split('\t')[0];
                var b = Plain(entry.Replace("\t", "   ·   "));
                b.HorizontalAlignment = HorizontalAlignment.Stretch;
                b.Click += (_, _) => {
                    flyout.Hide();
                    if (list.Any(e => (e as JsonValue)?.ToString() == name)) {
                        return;
                    }

                    list.Add(name);
                    edit.Changed();
                    RerenderArea(edit);
                };
                picks.Children.Add(b);
            }
        };
        panel.Children.Add(Row(
            new Button { Content = IconLabel("Pick from the zone", AreaIcon.Search), Flyout = flyout },
            Action(IconButton("Add an empty one", AreaIcon.Add), () => {
                list.Add("");
                edit.Changed();
                RerenderArea(edit);

                return Task.CompletedTask;
            })));

        return panel;
    }

    // ---------------------------------------------------------------------
    // Names, positions and the game's icons
    // ---------------------------------------------------------------------

    private bool IsCreature(ulong id) => _bossCatalog?.Creatures.Any(c => c.Id == id.ToString(CultureInfo.InvariantCulture)) == true;

    private string TemplateName(ulong id, PickKind kind) {
        if (id == 0) {
            return "";
        }

        var key = id.ToString(CultureInfo.InvariantCulture);
        if (_bossCatalog?.Creatures.FirstOrDefault(c => c.Id == key) is { } c) {
            return string.Join("  ·  ", new[] { c.Name, c.Level > 0 ? $"level {c.Level}" : "", c.School, c.IsBoss ? "boss" : "" }.Where(t => t.Length > 0));
        }

        var name = _areaCatalog?.NameOf(id) ?? "";

        return name.Length > 0 ? name : kind == PickKind.Creature
            ? "Not in the creature list (start the server once so it makes it)"
            : "Not in the lists (read the game files on the Tools tab to name objects)";
    }

    // The creature's school icon from the game files, or a coloured dot; a boss gets a crown beside it.
    private Control CreatureIcon(ulong id, double size) {
        var c = _bossCatalog?.Creatures.FirstOrDefault(b => b.Id == id.ToString(CultureInfo.InvariantCulture));
        var school = c?.School ?? "";
        Control icon = school.Length > 0
            ? GameIcon(SchoolDot(school, size), book => book.SchoolIcon(school), size)
            : Tile(Glyph(AreaIcon.Creatures, CREATURES, size * 0.6), CREATURES, size);
        if (c?.IsBoss != true) {
            return icon;
        }

        return new Panel {
            Width = size + 6, Height = size,
            Children = {
                icon,
                new Border {
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -4, -6, 0),
                    Child = Glyph(AreaIcon.Boss, SIGIL, size * 0.55),
                },
            },
        };
    }

    // The sigil section's tile: the game's own sigil picture when it is found, or the built-in icon.
    private Control SigilTile(Color color, AreaIcon fallback, double size = TILE)
        => Tile(GameIcon(Glyph(fallback, color, size * 0.58), _ => _sigilIconName, size * 0.72), color, size);

    /// <summary>A built-in icon that becomes the game's own picture once the game files are read (if it is there).</summary>
    private Control GameIcon(Control fallback, Func<SpellBook, string?> name, double size) {
        var host = new Border { Width = size, Height = size, Child = fallback };
        if (_spellBook is { } book) {
            if (GameBitmap(book, name(book)) is { } bitmap) {
                host.Child = new Image { Source = bitmap, Width = size, Height = size, Stretch = Stretch.Uniform };
            }
        }
        else {
            _areaGameIcons.Add((host, name));
        }

        return host;
    }

    private Bitmap? GameBitmap(SpellBook book, string? name) {
        if (string.IsNullOrEmpty(name)) {
            return null;
        }

        if (!_areaBitmaps.TryGetValue(name, out var bitmap)) {
            try {
                _areaBitmaps[name] = bitmap = book.Image(name) is { } image ? ToBitmap(image) : null;
            }
            catch (Exception) {
                _areaBitmaps[name] = bitmap = null;
            }
        }

        return bitmap;
    }

    // Reads the game files' pictures in the background (the Spells view shares them), then swaps the icons in.
    private void EnsureAreaBook() {
        if (_spellBook is not null || _areaBookLoading is not null) {
            return;
        }

        var folder = new[] { _gameFiles.GameDataFolder, _gameFiles.CopyFolder }
            .FirstOrDefault(f => f is not null && File.Exists(Path.Combine(f, "Root.wad")));
        if (folder is null) {
            return;
        }

        _areaBookLoading = LoadAsync();

        async Task LoadAsync() {
            try {
                var (book, sigil) = await Task.Run(() => {
                    var book = SpellBook.Load(folder);
                    var sigil = book.FindImages("sigil", "icon").Concat(book.FindImages("sigil"))
                        .FirstOrDefault(n => book.Image(n) is { Width: >= 16 and <= 128, Height: >= 16 and <= 128 });

                    return (book, sigil);
                });
                var shared = _spellBook ??= book;
                _sigilIconName = sigil;
                foreach (var (host, name) in _areaGameIcons) {
                    if (GameBitmap(shared, name(shared)) is { } bitmap) {
                        host.Child = new Image { Source = bitmap, Width = host.Width, Height = host.Height, Stretch = Stretch.Uniform };
                    }
                }

                _areaGameIcons.Clear();
            }
            catch (Exception) {
                // The built-in icons stay.
            }
        }
    }

    private async Task LoadAreaCatalogAsync() {
        var lists = new[] { Path.Combine(_manager.IdLookupFolder, "ids.csv"), Path.Combine(_manager.IdLookupFolder, "zones.txt"),
            Path.Combine(_gameFiles.KeyDetailsFolder, "Items and objects.csv"), Path.Combine(_gameFiles.KeyDetailsFolder, "Zones.csv") };
        var changed = lists.Where(File.Exists).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
        if ((_areaCatalog is not null && changed <= _areaCatalogFrom) || _areaCatalogLoading is not null) {
            return;
        }

        _areaCatalogLoading = Task.Run(() => AreaCatalog.Load(_manager.IdLookupFolder, _gameFiles.KeyDetailsFolder)).ContinueWith(t => {
            Dispatcher.UIThread.Post(() => {
                _areaCatalogLoading = null;
                if (t.IsCompletedSuccessfully) {
                    _areaCatalog = t.Result;
                    _areaCatalogFrom = changed;
                    // Redrawn with the names, unless something is being typed.
                    if (_areaEdits.Values.All(e => !e.Dirty)) {
                        RenderAreas();
                    }
                    else {
                        _newAreaZone.ItemsSource = _areaCatalog.Zones;
                    }
                }
            });
        }, TaskScheduler.Default);
        await Task.CompletedTask;
    }

    // Where the wizard on this PC stands and faces (the one who moved last if several, or anyone if no game on this PC has moved),
    // from the server's logs/positions.txt (the Play tab's Position).
    private (string Who, string X, string Y, string Z, string Yaw) MyPosition(string zone) {
        var parts = MyPositionLine(zone);

        return (parts[0], parts[2], parts[3], parts[4], parts[5]);
    }

    // The position file's line for that wizard: name, zone, X, Y, Z, facing, time and where.
    private string[] MyPositionLine(string zone) {
        string[] lines;
        try {
            lines = File.Exists(PositionFile) ? File.ReadAllLines(PositionFile) : [];
        }
        catch (IOException) {
            throw new PanelException("The position file is being written; press again.");
        }

        var all = lines.Select(l => l.Split('\t')).Where(p => p.Length >= 7).ToList();
        var parts = all.FirstOrDefault(p => p.Length >= 8 && p[7] == "local") ?? all.FirstOrDefault()
                    ?? throw new PanelException("There is no position yet. Start the server, go into the game and walk a step.");
        if (zone.Length > 0 && !string.Equals(parts[1], zone, StringComparison.OrdinalIgnoreCase)) {
            throw new PanelException($"{parts[0]} is in {parts[1]}, not {zone}. Go there in the game and walk a step.");
        }

        return parts;
    }

    private JsonObject SafePlacements() {
        try {
            return CustomPlacements.Load(_manager.CustomsFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {
            return [];
        }
    }

    private string ZoneDisplay(string zone) => _areaCatalog?.ZoneNames.TryGetValue(zone, out var name) == true && name.Length > 0 ? name : zone.Length > 0 ? zone : "(no zone)";

    private string AreaTitle(string zone, string variant) => variant.Length > 0 ? $"{ZoneDisplay(zone)} ({variant})" : ZoneDisplay(zone);

    private static string N(double value) => Math.Round(value, 3).ToString(CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------------
    // JSON
    // ---------------------------------------------------------------------

    /// <summary>Sets a field, keeping the spelling it already has (the server reads any case); null removes it.</summary>
    /// <summary>A copy without empty lists and groups (the boxes make them as they are drawn; the server needs none).</summary>
    private static JsonObject Clean(JsonObject json) {
        var copy = (JsonObject) json.DeepClone();
        Strip(copy);

        return copy;

        static void Strip(JsonNode? node) {
            if (node is JsonObject obj) {
                foreach (var (k, v) in obj.ToList()) {
                    Strip(v);
                    if (v is JsonArray { Count: 0 } || (v is JsonObject { Count: 0 } && !k.Equals("Sigil", StringComparison.OrdinalIgnoreCase)
                                                        && !k.Equals("CombatSigil", StringComparison.OrdinalIgnoreCase))) {
                        obj.Remove(k);
                    }
                }
            }
            else if (node is JsonArray array) {
                foreach (var item in array) {
                    Strip(item);
                }
            }
        }
    }

    private static void Set(JsonObject json, string key, JsonNode? value) {
        var existing = json.Select(p => p.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (value is null) {
            if (existing is not null) {
                json.Remove(existing);
            }

            return;
        }

        json[existing ?? key] = value;
    }

    private static JsonArray Arr(JsonObject json, string key) {
        if (BossChanges.FindIn(json, key) is JsonArray array) {
            return array;
        }

        var made = new JsonArray();
        Set(json, key, made);

        return made;
    }

    private static JsonObject Obj(JsonObject json, string key) {
        if (BossChanges.FindIn(json, key) is JsonObject obj) {
            return obj;
        }

        var made = new JsonObject();
        Set(json, key, made);

        return made;
    }

    private static IEnumerable<JsonObject> Entries(JsonObject json, string key) => (BossChanges.FindIn(json, key) as JsonArray ?? []).OfType<JsonObject>();

}
