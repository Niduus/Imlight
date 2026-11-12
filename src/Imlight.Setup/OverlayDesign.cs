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

    /// <summary>The server's own texts and boxes, in the server's spiraldb-extra/Customs folder (OverlayGate reads it).</summary>
    internal const string SERVER_FILE = "ServerOverlay.json";

    /// <summary>What the linked server sent last, kept beside overlay.json so both copies (Ahziael, on its own) see it.</summary>
    internal const string FROM_SERVER_FILE = "overlay-server.json";

    /// <summary>The game window a design is laid out on: sizes and places are in pixels of a 1280 x 720 window.</summary>
    internal const double REFERENCE_W = 1280;
    internal const double REFERENCE_H = 720;

    /// <summary>
    /// How much bigger a game window of this size (device-independent pixels) draws the design than the 1280 x 720 one
    /// it is set up on (Niduus, 2026-11-11: "multi clients too with odd res from one another. This way, it's still set up
    /// as projected"). The same for width and height, so nothing is stretched; corners and sides keep their pieces.
    /// </summary>
    internal static double Fit(double width, double height)
        => width <= 0 || height <= 0 ? 1 : Math.Clamp(Math.Min(width / REFERENCE_W, height / REFERENCE_H), 0.6, 4);

    /// <summary>The least room every other text and box keeps from the Ahziael line, so nothing ever covers it.</summary>
    internal const double CREDIT_CLEARANCE = 4;

    /// <summary>
    /// Off: the texts and boxes you made are hidden. The Ahziael line and the sparkle button always show (Niduus,
    /// 2026-11-11: "the Ahziael one can't be turned off either").
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How the "Ahziael made by Niduus | version" line looks. Its words and its corner can't change.</summary>
    public OverlayElement Credit { get; set; } = DefaultCredit();

    public List<OverlayElement> Elements { get; set; } = [];

    internal static OverlayElement DefaultCredit() => new() {
        Kind = OverlayElement.TEXT, Name = "Ahziael credit", Anchor = OverlayAnchor.BottomLeft, X = 10, Y = 8,
        Text = CREDIT_TEXT, Size = 13, Bold = true,
        Fill = new OverlayPaint { Kind = OverlayPaint.LINEAR, Color1 = "#FFFFFFFF", Color2 = "#FFC9B6FF", Angle = 90 },
        Background = new OverlayPaint { Kind = OverlayPaint.SOLID, Color1 = "#8C140F28" },
        CornerRadius = 6, Padding = 6, Opacity = 1, Shadow = true, ShadowColor = "#C0000000", ShadowBlur = 4, ShadowY = 1,
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

    internal static OverlayDesign Load(string installFolder) => LoadFile(PathIn(installFolder));

    internal static OverlayDesign LoadFile(string path) {
        try {
            if (File.Exists(path) && new FileInfo(path).Length < 12 * 1024 * 1024) {
                return FromJson(File.ReadAllText(path)) ?? new OverlayDesign();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        }

        return new OverlayDesign();
    }

    internal void Save(string installFolder) => SaveFile(PathIn(installFolder));

    internal void SaveFile(string path) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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

    /// <summary>
    /// Every piece gets its own Id, and a Parent that is another piece of the same list, never itself or one of its own
    /// children (so a family can't go round in a circle); anything else is cleared.
    /// </summary>
    internal static void FixFamily(List<OverlayElement> elements) {
        var ids = new HashSet<string>();
        foreach (var e in elements) {
            if (e.Id.Length == 0 || !ids.Add(e.Id)) {
                e.Id = OverlayElement.NewId();
                ids.Add(e.Id);
            }
        }

        var byId = elements.ToDictionary(e => e.Id);
        foreach (var e in elements) {
            if (e.Parent.Length == 0) {
                continue;
            }

            // Up the family from the parent: reaching this one again, or a missing one, ends it.
            var seen = new HashSet<string> { e.Id };
            var at = e.Parent;
            while (at.Length > 0) {
                if (!seen.Add(at) || !byId.TryGetValue(at, out var up)) {
                    e.Parent = "";
                    break;
                }

                at = up.Parent;
            }
        }
    }

    internal OverlayDesign Copy() => FromJson(ToJson()) ?? new OverlayDesign();

    /// <summary>
    /// Every value back in range. The credit line keeps the look people gave it (Niduus, 2026-11-11: "allow customization
    /// of the Ahziael UI but not the ability to modify it's text. It stays the same no matter what"), but its words, its
    /// place and that it is shown always come from the program ("It can not be repositioned or disabled or moved in
    /// anyway").
    /// </summary>
    internal OverlayDesign Clean() {
        // The same object stays (the designer keeps hold of it).
        var credit = Credit ??= DefaultCredit();
        var look = DefaultCredit();
        credit.Clean();

        // Who it is, where it is and that it is there: never up to anyone.
        credit.Kind = OverlayElement.TEXT;
        credit.Name = look.Name;
        credit.Text = look.Text;
        credit.Anchor = look.Anchor;
        credit.X = look.X;
        credit.Y = look.Y;
        credit.Width = 0;
        credit.Height = 0;
        credit.Rotation = 0;
        credit.Visible = true;
        credit.Locked = false;
        credit.Id = "";
        credit.Parent = "";
        credit.Layer = 0;
        credit.Spacing = 0;
        credit.Picture = "";
        credit.Action = look.Action;
        credit.Command = "";
        credit.CheatHow = look.CheatHow;

        // Always readable: big enough, not faded away, and the letters always have a colour.
        credit.Size = Math.Max(credit.Size, 10);
        credit.Opacity = Math.Max(credit.Opacity, 0.6);
        if (credit.Fill.IsNone || !Seen(credit.Fill.Color1) || credit.Fill.Kind != OverlayPaint.SOLID && !Seen(credit.Fill.Color2)) {
            credit.Fill = look.Fill;
        }

        Elements = (Elements ?? []).Where(e => e is not null).Take(MAX_ELEMENTS).Select(e => e.Clean()).ToList();
        FixFamily(Elements);

        return this;
    }

    // A letter colour at least 60% solid (colours are #AARRGGBB once cleaned).
    private static bool Seen(string color) =>
        color.Length == 9 && int.TryParse(color.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var alpha) && alpha >= 0x99;

    /// <summary>Puts the Ahziael line's look back to how Ahziael ships it.</summary>
    internal void ResetCreditLook() {
        var credit = Credit ??= DefaultCredit();
        var look = DefaultCredit();
        foreach (var property in typeof(OverlayElement).GetProperties()) {
            if (property.CanWrite) {
                property.SetValue(credit, property.GetValue(look));
            }
        }
    }

}

internal enum OverlayAnchor { TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom, BottomRight }

/// <summary>One text, box or button of the overlay.</summary>
internal sealed class OverlayElement {

    internal const string TEXT = "Text";
    internal const string BOX = "Box";
    internal const string PICTURE = "Picture";

    /// <summary>
    /// A text that can be clicked in game (Niduus, 2026-11-11): it runs its <see cref="Action"/>. Only buttons take
    /// clicks; everything else on the overlay lets them through to the game.
    /// </summary>
    internal const string BUTTON = "Button";

    /// <summary>
    /// The minimap (Niduus, 2026-11-12: "mini map that is very detailed"): a map of what is around the wizard in this game
    /// window, from the server (quest steps, quest givers, mobs, bosses, sigils, doors, other wizards). How it looks and
    /// what it shows is in <see cref="Map"/>. Like a box it has no words and lets clicks through to the game.
    /// </summary>
    internal const string MINIMAP = "Minimap";

    internal const int MAX_TEXT = 300;
    internal const int MAX_COMMAND = 200;

    /// <summary>Largest picture kept in a design, in bytes of the file (PNG, JPEG, GIF or BMP only).</summary>
    internal const int MAX_PICTURE_BYTES = 200 * 1024;

    internal static readonly string[] PICTURE_FITS = ["Fit", "Fill", "Stretch"];

    public string Kind { get; set; } = TEXT;

    /// <summary>What a button does when clicked: one of <see cref="OverlayAction"/>'s kinds.</summary>
    public string Action { get; set; } = OverlayAction.COMMAND;

    /// <summary>
    /// For <see cref="OverlayAction.COMMAND"/>: the chat command run on the server as the one who clicks
    /// (".tp Ravenwood"). For <see cref="OverlayAction.CHEAT"/>: the cheat's name. Plain words, never code.
    /// </summary>
    public string Command { get; set; } = "";

    /// <summary>For a cheat button: Use (press it), On, Off, or Switch (on when off, off when on).</summary>
    public string CheatHow { get; set; } = OverlayAction.USE;
    public string Name { get; set; } = "";

    /// <summary>Its own name for the family links (made by Ahziael).</summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// The Id of the piece it belongs to, or empty for the game window (Niduus, 2026-11-11: "make sure parenting and
    /// childing is supported for the UI's ... Children moving with parents"). A child is pinned to a corner or side of
    /// its parent instead of the window, so it moves with it, and is hidden when its parent is.
    /// </summary>
    public string Parent { get; set; } = "";

    internal static string NewId() => Guid.NewGuid().ToString("N")[..10];
    public bool Visible { get; set; } = true;

    /// <summary>
    /// The words. {version} is Ahziael's version, {server} the server's address, {time} and {date} the clock.
    /// </summary>
    public string Text { get; set; } = "";

    public OverlayAnchor Anchor { get; set; } = OverlayAnchor.TopLeft;

    /// <summary>Higher layers are drawn over lower ones; the same layer keeps the list's order.</summary>
    public int Layer { get; set; }

    /// <summary>
    /// When this one would lap over another, it is moved off it so this much room is left between them. 0: it may lap
    /// over others (text on a box). It always keeps clear of the Ahziael line.
    /// </summary>
    public double Spacing { get; set; }

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

    /// <summary>Locked in place in the designers: it can't be dragged or sized by mistake (the boxes still change it).</summary>
    public bool Locked { get; set; }

    /// <summary>A picture's file, as base64 (only PNG, JPEG, GIF and BMP; checked in <see cref="Clean"/>). Only data: nothing in it runs.</summary>
    public string Picture { get; set; } = "";

    /// <summary>How a picture fills its box: Fit (whole picture), Fill (covers, cropped) or Stretch.</summary>
    public string PictureFit { get; set; } = "Fit";

    /// <summary>For a minimap: what it shows and how (null for anything else).</summary>
    public OverlayMap? Map { get; set; }

    /// <summary>A box, a picture or a minimap: no words (a button has words).</summary>
    internal bool IsBox => Kind is BOX or PICTURE or MINIMAP;

    internal bool IsMinimap => Kind == MINIMAP;

    internal bool IsPicture => Kind == PICTURE;

    internal bool IsButton => Kind == BUTTON;

    internal OverlayElement Clean() {
        Kind = Kind is BOX or PICTURE or BUTTON or MINIMAP ? Kind : TEXT;
        Map = Kind == MINIMAP ? (Map ?? new OverlayMap()).Clean() : null;
        Picture = Kind == PICTURE ? CleanPicture(Picture) : "";
        PictureFit = PICTURE_FITS.Contains(PictureFit) ? PictureFit : "Fit";
        Action = OverlayAction.KINDS.Contains(Action) ? Action : OverlayAction.COMMAND;
        Command = Trim(Command, MAX_COMMAND).Replace("\n", " ").Trim();
        CheatHow = OverlayAction.HOWS.Contains(CheatHow) ? CheatHow : OverlayAction.USE;
        Name = Trim(Name, 40);
        Id = new string((Id ?? "").Where(char.IsAsciiLetterOrDigit).Take(32).ToArray());
        Parent = new string((Parent ?? "").Where(char.IsAsciiLetterOrDigit).Take(32).ToArray());
        Text = Trim(Text, MAX_TEXT);
        if (!Enum.IsDefined(Anchor)) {
            Anchor = OverlayAnchor.TopLeft;
        }

        X = Range(X, -4000, 4000);
        Y = Range(Y, -4000, 4000);
        Width = Range(Width, 0, 4000);
        Layer = Math.Clamp(Layer, 0, 99);
        Spacing = Range(Spacing, 0, 500);
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

    /// <summary>The picture if it is a real, small enough PNG, JPEG, GIF or BMP; otherwise none.</summary>
    internal static string CleanPicture(string? base64) {
        if (string.IsNullOrEmpty(base64) || base64.Length > MAX_PICTURE_BYTES * 4 / 3 + 8) {
            return "";
        }

        try {
            var bytes = Convert.FromBase64String(base64);
            var known = bytes.Length > 8 && (
                (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) // PNG
                || (bytes[0] == 0xFF && bytes[1] == 0xD8) // JPEG
                || (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) // GIF
                || (bytes[0] == 0x42 && bytes[1] == 0x4D)); // BMP

            return known && bytes.Length <= MAX_PICTURE_BYTES ? base64 : "";
        }
        catch (FormatException) {
            return "";
        }
    }

    private static string Trim(string? text, int max) {
        text = new string((text ?? "").Where(c => c == '\n' || !char.IsControl(c)).ToArray());

        return text.Length > max ? text[..max] : text;
    }

    internal static double Range(double value, double min, double max, double fallback = 0)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

}

/// <summary>
/// What an overlay button can do. Each is a fixed choice made in the designer; a design file only names one, so a shared
/// design can't make a button do anything else.
/// </summary>
internal static class OverlayAction {

    /// <summary>Runs a chat command on the server, as the player who clicked, with that player's own rights.</summary>
    internal const string COMMAND = "Command";

    /// <summary>Uses one of the player's cheats on the server (the host's settings decide which they may use).</summary>
    internal const string CHEAT = "Cheat";

    /// <summary>
    /// Presses a mob (creature) cheat whose When is "pressed", by name, in the battle of the wizard who clicked (Niduus,
    /// 2026-11-11: "link to player/mob cheats").
    /// </summary>
    internal const string MOB_CHEAT = "MobCheat";

    /// <summary>Opens the overlay designer.</summary>
    internal const string DESIGNER = "Designer";

    /// <summary>Shows or hides the player's own texts, boxes and buttons (not the server's or the Ahziael line).</summary>
    internal const string TOGGLE_MINE = "ToggleMine";

    /// <summary>Brings Ahziael's window to the front.</summary>
    internal const string OPEN_AHZIAEL = "OpenAhziael";

    internal static readonly string[] KINDS = [COMMAND, CHEAT, MOB_CHEAT, DESIGNER, TOGGLE_MINE, OPEN_AHZIAEL];

    /// <summary>The words for each kind in the designer.</summary>
    internal static readonly string[] LABELS = [
        "Run an Imlight or Ahziael command", "Use one of my player cheats", "Set off a mob (creature) cheat", "Open the overlay designer", "Show or hide my own pieces", "Open Ahziael",
    ];

    internal const string USE = "Use";
    internal const string ON = "On";
    internal const string OFF = "Off";
    internal const string SWITCH = "Switch";

    internal static readonly string[] HOWS = [USE, ON, OFF, SWITCH];

    internal static readonly string[] HOW_LABELS = ["Use it (a \"When pressed\" cheat)", "Turn it on", "Turn it off", "Switch it on or off"];

    /// <summary>True when the action goes to the server (the others happen on this PC).</summary>
    internal static bool ToServer(string kind) => kind is COMMAND or CHEAT or MOB_CHEAT;

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

/// <summary>
/// What a minimap shows and how (Niduus, 2026-11-12: "Showing main quest objectives, Side Quest zones, objectives and so
/// on. Icons of where mobs are or boss locations as well with Sigils and so on"). Plain choices and colours only.
/// </summary>
internal sealed class OverlayMap {

    /// <summary>Turns with the wizard (the way they face is always up); off, north is always up.</summary>
    public bool Turn { get; set; } = true;

    /// <summary>How far from the middle to the edge, in game units (smaller is closer in).</summary>
    public double Zoom { get; set; } = 2200;

    public bool Round { get; set; } = true;

    /// <summary>How big the icons are, in pixels at 100%.</summary>
    public double IconSize { get; set; } = 9;

    /// <summary>Names beside quest steps, bosses, sigils and doors.</summary>
    public bool Names { get; set; } = true;

    public double NameSize { get; set; } = 10;

    /// <summary>The place's name at the top of the map.</summary>
    public bool Place { get; set; } = true;

    /// <summary>How many quest lines under the map (0 to 8).</summary>
    public int QuestLines { get; set; } = 3;

    /// <summary>Swaps left and right, in case the map comes out mirrored on some game.</summary>
    public bool Mirror { get; set; }

    /// <summary>The area's streets, grass, water and walls under the icons (from the server).</summary>
    public bool Streets { get; set; } = true;

    public double StreetsOpacity { get; set; } = 0.9;

    /// <summary>The game's own map page of the area under the streets and icons (read from the game files; lines up roughly).</summary>
    public bool GamePicture { get; set; }

    public double GamePictureOpacity { get; set; } = 0.85;

    public string PathColor { get; set; } = "#FFB9A47C";
    public string GrassColor { get; set; } = "#FF3C6236";
    public string WaterColor { get; set; } = "#FF3A6BAA";
    public string WallColor { get; set; } = "#FF1C1C26";

    public bool Quests { get; set; } = true;
    public bool Givers { get; set; } = true;
    public bool Npcs { get; set; } = true;
    public bool Mobs { get; set; } = true;
    public bool Bosses { get; set; } = true;
    public bool Sigils { get; set; } = true;
    public bool Doors { get; set; } = true;
    public bool Chests { get; set; } = true;
    public bool Wizards { get; set; } = true;

    public string MainColor { get; set; } = "#FFFFD24A";
    public string SideColor { get; set; } = "#FF4AB4FF";
    public string GiverColor { get; set; } = "#FFFFE14A";
    public string NpcColor { get; set; } = "#C0E6E6F0";
    public string MobColor { get; set; } = "#FFFF5050";
    public string BossColor { get; set; } = "#FFFF2D6E";
    public string SigilColor { get; set; } = "#FFB57CFF";
    public string DoorColor { get; set; } = "#FF4ADE80";
    public string ChestColor { get; set; } = "#FFD69A4A";
    public string WizardColor { get; set; } = "#FF5FE1FF";
    public string TeamColor { get; set; } = "#FF6BFF8A";
    public string YouColor { get; set; } = "#FFFFFFFF";
    public string TextColor { get; set; } = "#FFFFFFFF";

    internal OverlayMap Clean() {
        Zoom = OverlayElement.Range(Zoom, 300, 12000, 2200);
        IconSize = OverlayElement.Range(IconSize, 4, 30, 9);
        NameSize = OverlayElement.Range(NameSize, 6, 30, 10);
        QuestLines = Math.Clamp(QuestLines, 0, 8);
        StreetsOpacity = OverlayElement.Range(StreetsOpacity, 0, 1, 0.9);
        GamePictureOpacity = OverlayElement.Range(GamePictureOpacity, 0, 1, 0.85);
        PathColor = OverlayPaint.CleanColor(PathColor, "#FFB9A47C");
        GrassColor = OverlayPaint.CleanColor(GrassColor, "#FF3C6236");
        WaterColor = OverlayPaint.CleanColor(WaterColor, "#FF3A6BAA");
        WallColor = OverlayPaint.CleanColor(WallColor, "#FF1C1C26");
        MainColor = OverlayPaint.CleanColor(MainColor, "#FFFFD24A");
        SideColor = OverlayPaint.CleanColor(SideColor, "#FF4AB4FF");
        GiverColor = OverlayPaint.CleanColor(GiverColor, "#FFFFE14A");
        NpcColor = OverlayPaint.CleanColor(NpcColor, "#C0E6E6F0");
        MobColor = OverlayPaint.CleanColor(MobColor, "#FFFF5050");
        BossColor = OverlayPaint.CleanColor(BossColor, "#FFFF2D6E");
        SigilColor = OverlayPaint.CleanColor(SigilColor, "#FFB57CFF");
        DoorColor = OverlayPaint.CleanColor(DoorColor, "#FF4ADE80");
        ChestColor = OverlayPaint.CleanColor(ChestColor, "#FFD69A4A");
        WizardColor = OverlayPaint.CleanColor(WizardColor, "#FF5FE1FF");
        TeamColor = OverlayPaint.CleanColor(TeamColor, "#FF6BFF8A");
        YouColor = OverlayPaint.CleanColor(YouColor, "#FFFFFFFF");
        TextColor = OverlayPaint.CleanColor(TextColor, "#FFFFFFFF");

        return this;
    }

}
