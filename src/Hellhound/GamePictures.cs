using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Imlight.Setup;

namespace Hellhound;

/// <summary>
/// The game's own pictures for Hellhound (Niduus, 2026-11-22: "Clicking on a bot shows it's stats and deck and shit like in
/// Ahziael WITH PICTURES!"): card art and frames, item icons and stat icons, read straight from the .wad files of the
/// Wizard101 install on this PC and handed to the page as PNG. The game files are only ever read, never changed.
/// </summary>
internal sealed class GamePictures(string serverFolder, string? givenFolder) {

    private readonly object _gate = new();
    private Dictionary<string, List<Entry>>? _images;
    private string? _folder;
    private readonly ConcurrentDictionary<string, byte[]?> _png = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, GameImage?> _decoded = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Entry(string Wad, long Offset, int Size, int ZSize, bool Zip, string Path);

    // Card sizes as the game's frames are drawn (Ahziael's CardPainter): the art window and the whole card.
    private const int CARD_W = 128, CARD_H = 196, SCALE = 2;

    // Each stat icon's picture, as Ahziael's gear page finds them.
    private static readonly Dictionary<string, string[]> s_statIcons = new(StringComparer.OrdinalIgnoreCase) {
        ["health"] = ["Icon_Health.dds", "Icon_MaxHealth.dds", "Icon_Health_Small.dds"],
        ["mana"] = ["Icon_Mana.dds", "Icon_MaxMana.dds"],
        ["energy"] = ["Icon_Energy.dds", "Icon_MaxEnergy.dds"],
        ["accuracy"] = ["Icon_Accuracy.dds"],
        ["damage"] = ["Type_Damage.dds"],
        ["damageflat"] = ["Type_Damage_Flat.dds"],
        ["resist"] = ["Type_Resist.dds"],
        ["resistflat"] = ["Type_Resist_Flat.dds"],
        ["pierce"] = ["Icon_Armor_Penetration.dds"],
        ["critical"] = ["Icon_CriticalRating.dds"],
        ["block"] = ["Icon_BlockRating.dds"],
        ["powerpip"] = ["Type_PowerPips.dds"],
        ["shadowpip"] = ["Icon_Shadow_Pip.dds"],
        ["pip"] = ["Type_Pip.dds"],
        ["outgoing"] = ["Icon_Outgoing.dds"],
        ["incoming"] = ["Icon_Incoming.dds"],
        ["heal"] = ["Type_Heal.dds"],
        ["stunresist"] = ["Type_Stun_Resist.dds"],
        ["archmastery"] = ["Icon_Archmastery.dds"],
        ["pipconversion"] = ["Icon_PipConversion.dds", "Icon_Pip_Conversion.dds"],
    };

    /// <summary>Where the pictures come from, or why there are none.</summary>
    internal string Folder { get { Index(); return _folder ?? ""; } }

    internal bool Ready => Index().Count > 0;

    /// <summary>Starts reading the game's list of pictures in the background (a few seconds the first time).</summary>
    internal void Warm() => _ = Task.Run(Index);

    /// <summary>Any picture by its file name (an item's icon, a school icon), as PNG, or null.</summary>
    internal byte[]? Picture(string name) {
        name = System.IO.Path.GetFileName((name ?? "").Replace('\\', '/'));
        if (name.Length == 0) {
            return null;
        }

        return _png.GetOrAdd("pic|" + name, _ => Image(name) is { } image ? Png(image) : null);
    }

    /// <summary>A stat's icon ("health", "accuracy", ...), as PNG, or null.</summary>
    internal byte[]? StatIcon(string key)
        => s_statIcons.TryGetValue(key ?? "", out var names) ? names.Select(Picture).FirstOrDefault(p => p is not null) : null;

    /// <summary>
    /// A card as the game draws it, without its words (the page writes the name and pips over it): the school's frame with
    /// the spell's art in its window, twice the game's size.
    /// </summary>
    internal byte[]? Card(string art, string school, string front, string kind) {
        var key = $"card|{art}|{school}|{front}|{kind}";
        return _png.GetOrAdd(key, _ => {
            if (!Ready) {
                return null;
            }

            var canvas = new byte[CARD_W * SCALE * CARD_H * SCALE * 4];
            Fill(canvas, 7, 23, 114, 101, 0x2A, 0x1C, 0x12);
            if ((Image(art) ?? Image($"Art_School_{school}.dds")) is { } picture) {
                Cover(canvas, picture, 7, 23, 114, 101);
            }

            var frame = (front.Length > 0 ? Image(front) : null)
                        ?? (kind == "tc" ? Image("School_Treasure_Blnk.dds") : null)
                        ?? (kind == "item" ? Image("School_Item_Blnk.dds") : null)
                        ?? Image($"School_{school}_Blnk.dds")
                        ?? (school.Equals("Shadow", StringComparison.OrdinalIgnoreCase) ? Image("School_BW_Blnk.dds") : null)
                        ?? Image("School_Item_Blnk.dds");
            if (frame is not null) {
                Draw(canvas, frame, 0, 0, Math.Min(CARD_W, frame.Width), Math.Min(CARD_H, frame.Height), 0, 0, CARD_W, CARD_H);
            }

            return Png(new GameImage(CARD_W * SCALE, CARD_H * SCALE, canvas));
        });
    }

    // ---------------------------------------------------------------------
    // Finding the game and its pictures
    // ---------------------------------------------------------------------

    private Dictionary<string, List<Entry>> Index() {
        lock (_gate) {
            if (_images is not null) {
                return _images;
            }

            var images = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);
            _folder = FindGameData();
            if (_folder is not null) {
                foreach (var wad in Directory.EnumerateFiles(_folder, "*.wad")) {
                    try {
                        ReadWad(wad, images);
                    }
                    catch (Exception e) {
                        AppWindow.Log($"Could not read {System.IO.Path.GetFileName(wad)}: {e.Message}");
                    }
                }

                AppWindow.Log($"Game pictures: {images.Count} found in {_folder}.");
            }
            else {
                AppWindow.Log("Game pictures: Wizard101's GameData folder (Root.wad) was not found; start Hellhound with --game <folder>.");
            }

            return _images = images;
        }
    }

    private string? FindGameData() {
        static bool Has(string? folder) => !string.IsNullOrWhiteSpace(folder) && File.Exists(System.IO.Path.Combine(folder, "Root.wad"));

        if (Has(givenFolder)) {
            return givenFolder;
        }

        // Ahziael remembers the game in imlight-install.json, in the folder that holds the server (or the server's own).
        var install = Directory.GetParent(serverFolder)?.FullName;
        foreach (var folder in new[] { install, serverFolder }.Where(f => f is not null)) {
            var file = System.IO.Path.Combine(folder!, "imlight-install.json");
            try {
                if (File.Exists(file) && JsonDocument.Parse(File.ReadAllText(file)).RootElement.TryGetProperty("RootWad", out var root)
                    && root.GetString() is { } rootWad && Has(System.IO.Path.GetDirectoryName(rootWad))) {
                    return System.IO.Path.GetDirectoryName(rootWad);
                }
            }
            catch (Exception) {
            }

            var copy = System.IO.Path.Combine(folder!, "Game files", "Copy of GameData");
            if (Has(copy)) {
                return copy;
            }
        }

        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        foreach (var guess in new[] {
                     System.IO.Path.Combine(common, "KingsIsle Entertainment", "Wizard101", "Data", "GameData"),
                     System.IO.Path.Combine(x86, "Steam", "steamapps", "common", "Wizard101", "Data", "GameData"),
                     System.IO.Path.Combine(x86, "KingsIsle Entertainment", "Wizard101", "Data", "GameData"),
                 }) {
            if (Has(guess)) {
                return guess;
            }
        }

        return null;
    }

    // A KIWAD archive: "KIWAD", version, file count, (a flags byte from version 2), then per file: offset, size, packed
    // size, packed (zlib) or not, checksum, name length and name. Only the pictures are kept in the list.
    private static void ReadWad(string wad, Dictionary<string, List<Entry>> images) {
        using var stream = OpenRead(wad);
        using var reader = new BinaryReader(stream, Encoding.ASCII);
        if (Encoding.ASCII.GetString(reader.ReadBytes(5)) != "KIWAD") {
            return;
        }

        var version = reader.ReadUInt32();
        var count = reader.ReadUInt32();
        if (version >= 2) {
            reader.ReadByte();
        }

        for (var i = 0; i < count; i++) {
            var offset = reader.ReadUInt32();
            var size = reader.ReadInt32();
            var zsize = reader.ReadInt32();
            var zip = reader.ReadByte() != 0;
            reader.ReadUInt32();
            var nameLength = reader.ReadInt32();
            var name = Encoding.ASCII.GetString(reader.ReadBytes(nameLength)).TrimEnd('\0');
            if (!name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            var key = System.IO.Path.GetFileName(name);
            if (!images.TryGetValue(key, out var list)) {
                images[key] = list = [];
            }

            list.Add(new Entry(wad, offset, size, zsize, zip, name));
        }
    }

    private static FileStream OpenRead(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private GameImage? Image(string? name) {
        name = System.IO.Path.GetFileName((name ?? "").Replace('\\', '/'));
        if (name.Length == 0) {
            return null;
        }

        return _decoded.GetOrAdd(name, n => {
            var images = Index();
            if (!images.TryGetValue(n, out var places) && !images.TryGetValue(n + ".dds", out places)) {
                return null;
            }

            // The round pictures the game shows over summoned creatures share names with card art, so those come last.
            foreach (var entry in places.OrderBy(p => p.Path.Contains("SummonedImages", StringComparison.OrdinalIgnoreCase) ? 1 : 0)) {
                try {
                    if (GameImage.Decode(Read(entry)) is { } image) {
                        return image;
                    }
                }
                catch (Exception) {
                }
            }

            return null;
        });
    }

    private static byte[] Read(Entry entry) {
        using var stream = OpenRead(entry.Wad);
        stream.Position = entry.Offset;
        var packed = new byte[entry.Zip ? entry.ZSize : entry.Size];
        stream.ReadExactly(packed);
        if (!entry.Zip) {
            return packed;
        }

        var data = new byte[entry.Size];
        using var zlib = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress);
        zlib.ReadExactly(data);

        return data;
    }

    // ---------------------------------------------------------------------
    // Drawing (card space is the game's 128 x 196; the canvas is twice that)
    // ---------------------------------------------------------------------

    private static void Fill(byte[] canvas, int x, int y, int w, int h, byte r, byte g, byte b) {
        for (var py = y * SCALE; py < (y + h) * SCALE; py++) {
            for (var px = x * SCALE; px < (x + w) * SCALE; px++) {
                var o = (py * CARD_W * SCALE + px) * 4;
                canvas[o] = r;
                canvas[o + 1] = g;
                canvas[o + 2] = b;
                canvas[o + 3] = 255;
            }
        }
    }

    // Fills the box with the middle of the picture, keeping its shape.
    private static void Cover(byte[] canvas, GameImage image, float x, float y, float w, float h) {
        float sw = image.Width, sh = image.Height;
        if (sw / sh > w / h) {
            sw = sh * w / h;
        }
        else {
            sh = sw * h / w;
        }

        Draw(canvas, image, (image.Width - sw) / 2, (image.Height - sh) / 2, sw, sh, x, y, w, h);
    }

    // Draws part of a picture into a box on the canvas, smoothed, laid over what is there.
    private static void Draw(byte[] canvas, GameImage image, float sx, float sy, float sw, float sh, float dx, float dy, float dw, float dh) {
        var width = CARD_W * SCALE;
        int x0 = (int) (dx * SCALE), y0 = (int) (dy * SCALE), x1 = (int) ((dx + dw) * SCALE), y1 = (int) ((dy + dh) * SCALE);
        Span<float> c = stackalloc float[4];
        for (var py = Math.Max(0, y0); py < Math.Min(CARD_H * SCALE, y1); py++) {
            var v = sy + (py + 0.5f - y0) / (y1 - y0) * sh - 0.5f;
            for (var px = Math.Max(0, x0); px < Math.Min(width, x1); px++) {
                var u = sx + (px + 0.5f - x0) / (x1 - x0) * sw - 0.5f;
                Sample(image, u, v, c);
                var a = c[3] / 255f;
                if (a <= 0) {
                    continue;
                }

                var o = (py * width + px) * 4;
                var under = canvas[o + 3] / 255f;
                var outA = a + under * (1 - a);
                for (var k = 0; k < 3; k++) {
                    canvas[o + k] = (byte) Math.Clamp((c[k] * a + canvas[o + k] * under * (1 - a)) / Math.Max(outA, 1e-4f), 0, 255);
                }

                canvas[o + 3] = (byte) Math.Clamp(outA * 255, 0, 255);
            }
        }
    }

    private static void Sample(GameImage image, float u, float v, Span<float> rgba) {
        var x = (int) Math.Floor(u);
        var y = (int) Math.Floor(v);
        float fx = u - x, fy = v - y;
        rgba.Clear();
        for (var j = 0; j < 2; j++) {
            for (var i = 0; i < 2; i++) {
                var weight = (i == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy);
                var px = Math.Clamp(x + i, 0, image.Width - 1);
                var py = Math.Clamp(y + j, 0, image.Height - 1);
                var o = (py * image.Width + px) * 4;
                for (var k = 0; k < 4; k++) {
                    rgba[k] += image.Pixels[o + k] * weight;
                }
            }
        }
    }

    // ---------------------------------------------------------------------
    // PNG
    // ---------------------------------------------------------------------

    private static readonly uint[] s_crc = MakeCrc();

    private static uint[] MakeCrc() {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++) {
            var c = n;
            for (var k = 0; k < 8; k++) {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    internal static byte[] Png(GameImage image) {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height);
        header[8] = 8;
        header[9] = 6;
        Chunk(output, "IHDR", header);

        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true)) {
            var row = image.Width * 4;
            for (var y = 0; y < image.Height; y++) {
                zlib.WriteByte(0);
                zlib.Write(image.Pixels, y * row, row);
            }
        }

        Chunk(output, "IDAT", raw.ToArray());
        Chunk(output, "IEND", []);

        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data) {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        var kind = Encoding.ASCII.GetBytes(type);
        output.Write(kind);
        output.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var b in kind) {
            crc = s_crc[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (var b in data) {
            crc = s_crc[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        BinaryPrimitives.WriteUInt32BigEndian(number, crc ^ 0xFFFFFFFF);
        output.Write(number);
    }
}
