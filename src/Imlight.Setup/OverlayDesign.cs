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
/// What the game overlay shows (Niduus, 2026-11-11: "this UI will be sandboxed too. No code programming with full
/// customization including font, color, gradients and so on"). Plain data only: texts and boxes with their look. Nothing
/// in it runs; a design read from a file is checked and every number kept in range (<see cref="Clean"/>), so a shared
/// design can never do more than draw.
/// </summary>
internal sealed class OverlayDesign {

    internal const string FILE = "overlay.json";
    internal const string SHARE_EXTENSION = ".ahziaeloverlay";
    internal const string CREDIT_TEXT = "Ahziael made by Niduus";
    internal const int MAX_ELEMENTS = 40;

    /// <summary>Off: nothing is drawn over the game (and a server that needs the overlay turns this PC away).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How the "Ahziael made by Niduus | version" line looks. Its words and its corner can't change.</summary>
    public OverlayElement Credit { get; set; } = DefaultCredit();

    public List<OverlayElement> Elements { get; set; } = [];

    internal static OverlayElement DefaultCredit() => new() {
        Kind = OverlayElement.TEXT, Name = "Ahziael credit", Anchor = OverlayAnchor.BottomLeft, X = 10, Y = 8,
        Text = CREDIT_TEXT, Size = 13, Bold = true,
        Fill = new OverlayPaint { Kind = OverlayPaint.LINEAR, Color1 = "#FFFFFFFF", Color2 = "#FFC9B6FF", Angle = 90 },
        Background = new OverlayPaint { Kind = OverlayPaint.SOLID, Color1 = "#8C140F28" },
        CornerRadius = 6, Padding = 6, Opacity = 0.9, Shadow = true, ShadowColor = "#C0000000", ShadowBlur = 4, ShadowY = 1,
    };

    /// <summary>The words the credit line shows, with the app's version.</summary>
    internal static string CreditLine(string version) => $"{CREDIT_TEXT} | {version}";

    // ---------------------------------------------------------------------
    // Reading and saving
    // ---------------------------------------------------------------------

    private static readonly JsonSerializerOptions s_json = new() {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
        // Big numbers or deep nesting in a shared file are refused rather than read.
        MaxDepth = 8,
    };

    internal static string PathIn(string installFolder) => Path.Combine(installFolder, FILE);

    /// <summary>Where saved design files go by default: Ahziael's own folder, never the desktop (Niduus, 2026-11-11).</summary>
    internal static string DesignsFolder(string installFolder) {
        var folder = Path.Combine(installFolder, "Overlay designs");
        try {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return installFolder;
        }

        return folder;
    }

    internal static OverlayDesign Load(string installFolder) {
        try {
            var path = PathIn(installFolder);
            if (File.Exists(path) && new FileInfo(path).Length < 512 * 1024) {
                return FromJson(File.ReadAllText(path)) ?? new OverlayDesign();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        }

        return new OverlayDesign();
    }

    internal void Save(string installFolder) {
        var path = PathIn(installFolder);
        var temp = path + ".tmp";
        File.WriteAllText(temp, ToJson());
        File.Move(temp, path, true);
    }

    internal string ToJson() => JsonSerializer.Serialize(this, s_json);

    /// <summary>A design from its JSON, cleaned; null when it is not a design.</summary>
    internal static OverlayDesign? FromJson(string json) {
        try {
            return JsonSerializer.Deserialize<OverlayDesign>(json, s_json)?.Clean();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException) {
            return null;
        }
    }

    internal OverlayDesign Copy() => FromJson(ToJson()) ?? new OverlayDesign();

    /// <summary>Every value back in range, and the credit line's words and corner put back.</summary>
    internal OverlayDesign Clean() {
        Credit = (Credit ?? DefaultCredit()).Clean();
        Credit.Kind = OverlayElement.TEXT;
        Credit.Text = CREDIT_TEXT;
        Credit.Anchor = OverlayAnchor.BottomLeft;
        Credit.Visible = true;
        Credit.Name = "Ahziael credit";
        // It shows at all times, so it can't be made too small, too faint or pushed off the window.
        Credit.Size = Math.Max(10, Credit.Size);
        Credit.Opacity = Math.Max(0.5, Credit.Opacity);
        Credit.X = Math.Clamp(Credit.X, 0, 200);
        Credit.Y = Math.Clamp(Credit.Y, 0, 200);
        Credit.Rotation = 0;
        Elements = (Elements ?? []).Where(e => e is not null).Take(MAX_ELEMENTS).Select(e => e.Clean()).ToList();

        return this;
    }

}

internal enum OverlayAnchor { TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom, BottomRight }

/// <summary>One text or box of the overlay.</summary>
internal sealed class OverlayElement {

    internal const string TEXT = "Text";
    internal const string BOX = "Box";
    internal const int MAX_TEXT = 300;

    public string Kind { get; set; } = TEXT;
    public string Name { get; set; } = "";
    public bool Visible { get; set; } = true;

    /// <summary>
    /// The words. {version} is Ahziael's version, {server} the server's address, {time} and {date} the clock.
    /// </summary>
    public string Text { get; set; } = "";

    public OverlayAnchor Anchor { get; set; } = OverlayAnchor.TopLeft;

    /// <summary>How far in from the corner or edge it is pinned to, at 100% scale.</summary>
    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>A box's size; for a text, 0 is as wide as its words (more makes it wrap).</summary>
    public double Width { get; set; }

    public double Height { get; set; }

    public string Font { get; set; } = "";
    public double Size { get; set; } = 16;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool Strikethrough { get; set; }
    public double LetterSpacing { get; set; }
    public double LineHeight { get; set; }

    /// <summary>Left, Center or Right, for a text that wraps.</summary>
    public string Align { get; set; } = "Left";

    public OverlayPaint Fill { get; set; } = new() { Kind = OverlayPaint.SOLID, Color1 = "#FFFFFFFF" };
    public OverlayPaint Background { get; set; } = new();
    public string BorderColor { get; set; } = "#00000000";
    public double BorderWidth { get; set; }
    public double CornerRadius { get; set; }
    public double Padding { get; set; }

    public bool Shadow { get; set; }
    public string ShadowColor { get; set; } = "#C0000000";
    public double ShadowBlur { get; set; } = 4;
    public double ShadowX { get; set; }
    public double ShadowY { get; set; } = 1;

    /// <summary>0 (invisible) to 1.</summary>
    public double Opacity { get; set; } = 1;

    /// <summary>Degrees, clockwise.</summary>
    public double Rotation { get; set; }

    internal bool IsBox => Kind == BOX;

    internal OverlayElement Clean() {
        Kind = Kind == BOX ? BOX : TEXT;
        Name = Trim(Name, 40);
        Text = Trim(Text, MAX_TEXT);
        if (!Enum.IsDefined(Anchor)) {
            Anchor = OverlayAnchor.TopLeft;
        }

        X = Range(X, -4000, 4000);
        Y = Range(Y, -4000, 4000);
        Width = Range(Width, 0, 4000);
        Height = Range(Height, 0, 4000);
        Font = Trim(Font, 80);
        Size = Range(Size, 4, 200, 16);
        LetterSpacing = Range(LetterSpacing, -10, 50);
        LineHeight = Range(LineHeight, 0, 300);
        Align = Align is "Center" or "Right" ? Align : "Left";
        Fill = (Fill ?? new OverlayPaint()).Clean();
        Background = (Background ?? new OverlayPaint()).Clean();
        BorderColor = OverlayPaint.CleanColor(BorderColor, "#00000000");
        BorderWidth = Range(BorderWidth, 0, 40);
        CornerRadius = Range(CornerRadius, 0, 500);
        Padding = Range(Padding, 0, 200);
        ShadowColor = OverlayPaint.CleanColor(ShadowColor, "#C0000000");
        ShadowBlur = Range(ShadowBlur, 0, 50);
        ShadowX = Range(ShadowX, -50, 50);
        ShadowY = Range(ShadowY, -50, 50);
        Opacity = Range(Opacity, 0, 1, 1);
        Rotation = Range(Rotation, -360, 360);

        return this;
    }

    private static string Trim(string? text, int max) {
        text = new string((text ?? "").Where(c => c == '\n' || !char.IsControl(c)).ToArray());

        return text.Length > max ? text[..max] : text;
    }

    internal static double Range(double value, double min, double max, double fallback = 0)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

}

/// <summary>A colour or a gradient.</summary>
internal sealed class OverlayPaint {

    internal const string NONE = "None";
    internal const string SOLID = "Solid";
    internal const string LINEAR = "Linear";
    internal const string RADIAL = "Radial";

    internal static readonly string[] KINDS = [NONE, SOLID, LINEAR, RADIAL];

    public string Kind { get; set; } = NONE;

    /// <summary>#AARRGGBB or #RRGGBB.</summary>
    public string Color1 { get; set; } = "#FFFFFFFF";

    public string Color2 { get; set; } = "#FF000000";

    /// <summary>Empty: a gradient of two colours; set, a third colour in the middle.</summary>
    public string Color3 { get; set; } = "";

    /// <summary>A straight gradient's direction in degrees: 0 left to right, 90 top to bottom.</summary>
    public double Angle { get; set; }

    internal bool IsNone => Kind == NONE;

    internal OverlayPaint Clean() {
        Kind = KINDS.Contains(Kind) ? Kind : NONE;
        Color1 = CleanColor(Color1, "#FFFFFFFF");
        Color2 = CleanColor(Color2, "#FF000000");
        Color3 = string.IsNullOrWhiteSpace(Color3) ? "" : CleanColor(Color3, "");
        Angle = OverlayElement.Range(Angle, -360, 360);

        return this;
    }

    /// <summary>The colour as #AARRGGBB, or <paramref name="fallback"/> when it is not one.</summary>
    internal static string CleanColor(string? text, string fallback) {
        text = (text ?? "").Trim();
        if (!text.StartsWith('#')) {
            text = "#" + text;
        }

        var hex = text[1..];
        if (hex.Length == 6) {
            hex = "FF" + hex;
        }

        return hex.Length == 8 && hex.All(Uri.IsHexDigit) ? "#" + hex.ToUpperInvariant() : fallback;
    }

}
