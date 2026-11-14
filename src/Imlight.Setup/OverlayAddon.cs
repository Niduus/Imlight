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
using System.Text.Json.Serialization;

namespace Imlight.Setup;

/// <summary>
/// An overlay add-on (Niduus, 2026-11-11: "Include a plugin mode where uses can share their own customizations to the UI
/// overlay"): a file (.ahziaeladdon) that holds ready-made widgets (one or more texts, boxes and pictures that are added
/// together) and styles (a look to put on any text or box). Like a design file it is only data, checked and kept in range
/// when it is read, so an add-on can never run anything; it only adds what could be made by hand in the designer. Added
/// ones live in the "Overlay add-ons" folder of Ahziael's folder.
/// </summary>
internal sealed class OverlayAddon {

    internal const string EXTENSION = ".ahziaeladdon";
    internal const string FOLDER = "Overlay add-ons";
    internal const string FORMAT = "Ahziael overlay add-on";
    internal const int MAX_WIDGETS = 30;
    internal const int MAX_STYLES = 60;
    internal const long MAX_BYTES = 8L * 1024 * 1024;

    public string Format { get; set; } = "";
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "1";
    public List<OverlayWidget> Widgets { get; set; } = [];
    public List<OverlayStyle> Styles { get; set; } = [];

    /// <summary>
    /// Other add-ons (by name) this one needs on the overlay too (Niduus, 2026-11-13: "Ultimus Spectator requires
    /// Minimap"): downloaded with it, and put on with it when none of their pieces are on yet.
    /// </summary>
    public List<string> Requires { get; set; } = [];

    /// <summary>The file it was read from (not saved in it).</summary>
    [JsonIgnore]
    internal string Path { get; set; } = "";

    private static readonly JsonSerializerOptions s_json = new() {
        WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() }, MaxDepth = 10,
    };

    internal OverlayAddon Clean() {
        Name = Short(Name, 60);
        if (Name.Length == 0) {
            Name = "Add-on";
        }

        Author = Short(Author, 60);
        Description = Short(Description, 400);
        Version = Short(Version, 20);
        Requires = (Requires ?? []).Select(r => Short(r, 60)).Where(r => r.Length > 0 && !r.Equals(Name, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        Widgets = (Widgets ?? []).Where(w => w is not null).Take(MAX_WIDGETS).Select(w => {
            w.Name = Short(w.Name, 60);
            w.Elements = (w.Elements ?? []).Where(e => e is not null).Take(OverlayDesign.MAX_ELEMENTS).Select(e => e.Clean()).ToList();
            OverlayDesign.FixFamily(w.Elements);
            return w;
        }).Where(w => w.Elements.Count > 0).ToList();
        Styles = (Styles ?? []).Where(st => st?.Look is not null).Take(MAX_STYLES).Select(st => {
            st.Name = Short(st.Name, 60);
            st.Look = st.Look.Clean();
            return st;
        }).ToList();

        return this;
    }

    private static string Short(string? text, int max) {
        text = new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();

        return text.Length > max ? text[..max] : text;
    }

    internal string ToJson() {
        Format = FORMAT;

        return JsonSerializer.Serialize(this, s_json);
    }

    /// <summary>An add-on from its file's text, checked; null when it is not one.</summary>
    internal static OverlayAddon? FromJson(string json) {
        try {
            var addon = JsonSerializer.Deserialize<OverlayAddon>(json, s_json);

            return addon is null || addon.Format != FORMAT ? null : addon.Clean();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException) {
            return null;
        }
    }

    internal static string FolderIn(string installFolder) => System.IO.Path.Combine(installFolder, FOLDER);

    private static readonly object s_haveLock = new();
    private static (string Folder, DateTime At, HashSet<string> Names) s_have = ("", DateTime.MinValue, []);

    /// <summary>
    /// Whether an add-on by this name is downloaded on this PC (read again every few seconds). Used for what an add-on
    /// needs installed to work (Niduus, 2026-11-14: Minimap is required "for install, for Spectate to work"), whether or
    /// not its pieces are on the overlay. True when Ahziael's folder isn't known.
    /// </summary>
    internal static bool IsInstalled(string? installFolder, string name) {
        if (string.IsNullOrEmpty(installFolder)) {
            return true;
        }

        lock (s_haveLock) {
            if (s_have.Folder != installFolder || DateTime.UtcNow - s_have.At > TimeSpan.FromSeconds(5)) {
                s_have = (installFolder, DateTime.UtcNow, All(installFolder).Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
            }

            return s_have.Names.Contains(name);
        }
    }

    /// <summary>Every add-on added so far (files that aren't add-ons are left out).</summary>
    internal static List<OverlayAddon> All(string installFolder) {
        var list = new List<OverlayAddon>();
        var folder = FolderIn(installFolder);
        if (!Directory.Exists(folder)) {
            return list;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*" + EXTENSION).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
            try {
                if (new FileInfo(file).Length <= MAX_BYTES && FromJson(File.ReadAllText(file)) is { } addon) {
                    addon.Path = file;
                    list.Add(addon);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            }
        }

        return list;
    }

    /// <summary>Adds an add-on: its checked copy (never the file as it came) goes in the add-ons folder.</summary>
    internal static OverlayAddon Install(string installFolder, OverlayAddon addon) {
        var folder = FolderIn(installFolder);
        Directory.CreateDirectory(folder);
        var safe = new string(addon.Name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim('.', ' ');
        var path = System.IO.Path.Combine(folder, (safe.Length == 0 ? "Add-on" : safe) + EXTENSION);
        File.WriteAllText(path, addon.ToJson());
        addon.Path = path;

        return addon;
    }

    /// <summary>Copies of pieces with new Ids, keeping who belongs to whom among them (others' parents are let go).</summary>
    internal static List<OverlayElement> CopyFamily(IEnumerable<OverlayElement> elements) {
        var copies = elements.Select(e => (Old: e.Id, New: Copy(e))).ToList();
        var ids = copies.Where(c => c.Old.Length > 0).GroupBy(c => c.Old).ToDictionary(g => g.Key, _ => OverlayElement.NewId());
        foreach (var (old, copy) in copies) {
            copy.Id = old.Length > 0 && ids.TryGetValue(old, out var id) ? id : OverlayElement.NewId();
        }

        foreach (var (_, copy) in copies) {
            copy.Parent = copy.Parent.Length > 0 && ids.TryGetValue(copy.Parent, out var parent) ? parent : "";
        }

        return copies.Select(c => c.New).ToList();
    }

    /// <summary>What a piece put on from this widget remembers (<see cref="OverlayElement.From"/>).</summary>
    internal static string FromKey(OverlayAddon addon, OverlayWidget widget) => addon.Name + "/" + widget.Name;

    /// <summary>
    /// The pieces of <paramref name="elements"/> that this widget put on, with their children: the ones marked with it,
    /// and (for pieces put on before Ahziael marked them) unmarked ones of the same kind and name as one of its pieces.
    /// </summary>
    internal static List<OverlayElement> OnOverlay(OverlayAddon addon, OverlayWidget widget, List<OverlayElement> elements) {
        var key = FromKey(addon, widget);
        var looks = widget.Elements.Where(w => w.Name.Length > 0).Select(w => (w.Kind, w.Name)).ToHashSet();
        var found = elements.Where(e => e.From.Length > 0
            ? e.From.Equals(key, StringComparison.OrdinalIgnoreCase)
            : looks.Contains((e.Kind, e.Name))).ToList();

        // Their children come along (a widget's family, or pieces someone pinned to it).
        var ids = found.Select(e => e.Id).Where(id => id.Length > 0).ToHashSet();
        for (var grew = true; grew;) {
            grew = false;
            foreach (var e in elements) {
                if (e.Parent.Length > 0 && ids.Contains(e.Parent) && !found.Contains(e)) {
                    found.Add(e);
                    ids.Add(e.Id);
                    grew = true;
                }
            }
        }

        return found;
    }

    /// <summary>A copy of a text or box (the widget's own stays as it was).</summary>
    internal static OverlayElement Copy(OverlayElement element) {
        var copy = OverlayDesign.FromJson(new OverlayDesign { Elements = [element] }.ToJson())!.Elements[0];
        // Alone in that design its parent is missing, so the links are put back as they were.
        copy.Id = element.Id;
        copy.Parent = element.Parent;

        return copy;
    }

    // What a style leaves alone: what the piece is and says, and where it is.
    private static readonly HashSet<string> s_notLook = [
        nameof(OverlayElement.Kind), nameof(OverlayElement.Name), nameof(OverlayElement.Visible), nameof(OverlayElement.Text),
        nameof(OverlayElement.Anchor), nameof(OverlayElement.X), nameof(OverlayElement.Y), nameof(OverlayElement.Layer),
        nameof(OverlayElement.Spacing), nameof(OverlayElement.Width), nameof(OverlayElement.Height), nameof(OverlayElement.Locked),
        nameof(OverlayElement.Picture), nameof(OverlayElement.PictureFit), nameof(OverlayElement.Id), nameof(OverlayElement.Parent),
        nameof(OverlayElement.Action), nameof(OverlayElement.Command), nameof(OverlayElement.CheatHow), nameof(OverlayElement.Map), nameof(OverlayElement.Quests),
        nameof(OverlayElement.From),
    ];

    /// <summary>Puts a style's look (fonts, colours, edges, shadow...) on a text or box.</summary>
    internal static void ApplyStyle(OverlayElement target, OverlayElement look) {
        var copy = Copy(look);
        foreach (var property in typeof(OverlayElement).GetProperties()) {
            if (property.CanWrite && !s_notLook.Contains(property.Name)) {
                property.SetValue(target, property.GetValue(copy));
            }
        }

        target.Clean();
    }

}

/// <summary>Texts, boxes and pictures added together.</summary>
internal sealed class OverlayWidget {

    public string Name { get; set; } = "";
    public List<OverlayElement> Elements { get; set; } = [];

}

/// <summary>A look to put on any text or box.</summary>
internal sealed class OverlayStyle {

    public string Name { get; set; } = "";
    public OverlayElement Look { get; set; } = new();

}
