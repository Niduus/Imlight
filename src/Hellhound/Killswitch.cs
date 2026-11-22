using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Hellhound;

/// <summary>
/// The bots' kill switch (Niduus, 2026-11-22: "if its on (= true) then the bots all won't work. Even with the key"; "the
/// killswitch goes to the bots repo"). The switch is killswitch.txt at the top of the private Niduus/ahziael-bots repo; each
/// Bot Update round signs it into Server\bots\hive-mind.ahzhive (brain/killswitch.txt) with Niduus's key. The app checks that
/// file exactly as the bots do (bots/BotShared/BotHive.Open + BotKillSwitch, #1667), with the same public key, so it says the
/// same thing they will. The bots may play only when the file is signed by Niduus and unchanged, holds the switch, the switch
/// says "on = false", and it was signed in the last 7 days. The app never reads GitHub.
/// </summary>
internal static class Killswitch {

    internal const string FILE_NAME = "hive-mind.ahzhive";
    private const string SIGNED_NAME = "killswitch.txt";
    private const string FORMAT = "ahziael-hive-mind/1";
    private const int MAX_AGE_DAYS = 7;

    // Niduus's Hive Mind public key: the same one as in bots/BotShared/BotHive.cs.
    private const string PUBLIC_KEY =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEFhM3aqlBd1KF5GQkhDxGUQWmyX2dVn9T4bMgsDDVgDxtzGdWhM3/mer5eU1JmHa0iFEP9YSZmzu49BfXVG2Eww==";

    /// <summary>Whether the bots must stop, and why, read from the server's signed Hive Mind file.</summary>
    internal static (bool Stop, string Why) Read(string serverFolder) {
        var path = Path.Combine(serverFolder, "bots", FILE_NAME);
        if (!Open(path, out var what, out var signed, out var made)) {
            return (true, "the switch could not be read: " + what);
        }

        if (!signed.TryGetValue(SIGNED_NAME, out var data)) {
            return (true, "the signed Hive Mind file has no kill switch in it (" + what + ")");
        }

        if (DateTime.UtcNow - made > TimeSpan.FromDays(MAX_AGE_DAYS)) {
            return (true, $"the signed Hive Mind file is more than {MAX_AGE_DAYS} days old ({what})");
        }

        return Parse(Encoding.UTF8.GetString(data))
            ? (true, "the switch says on = true (" + what + ")")
            : (false, "the switch says on = false (" + what + ")");
    }

    /// <summary>Niduus's signature over manifest.json, then every signed file against its SHA-256 (as BotHive.Open).</summary>
    private static bool Open(string path, out string what, out Dictionary<string, byte[]> signed, out DateTime made) {
        signed = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        made = DateTime.MinValue;
        try {
            if (!File.Exists(path)) {
                what = $"no signed Hive Mind file ({path})";
                return false;
            }

            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            var manifest = Entry(zip, "manifest.json");
            var signature = Entry(zip, "signature.bin");
            if (manifest is null || signature is null) {
                what = "the Hive Mind file has no manifest or no signature";
                return false;
            }

            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PUBLIC_KEY), out _);
            if (!key.VerifyData(manifest, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) {
                what = "the Hive Mind file is not signed by Niduus";
                return false;
            }

            var info = JsonNode.Parse(manifest);
            if ((string?) info?["format"] != FORMAT || info["files"] is not JsonArray files) {
                what = "the Hive Mind file is of an unknown kind";
                return false;
            }

            var found = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var item in files) {
                var name = (string?) item?["path"];
                var data = name is null ? null : Entry(zip, "brain/" + name);
                if (name is null || data is null
                    || !string.Equals(Convert.ToHexString(SHA256.HashData(data)), (string?) item!["sha256"], StringComparison.OrdinalIgnoreCase)) {
                    what = $"the Hive Mind file was changed after it was signed ({name})";
                    return false;
                }

                found[name] = data;
            }

            if (DateTime.TryParse((string?) info["made"], CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when)) {
                made = when;
            }

            signed = found;
            what = $"signed Hive Mind file by {(string?) info["by"]}, made {(string?) info["made"]}, {files.Count} file(s)";
            return true;
        }
        catch (Exception ex) {
            what = "the Hive Mind file could not be read (" + ex.Message + ")";
            return false;
        }
    }

    private static byte[]? Entry(ZipArchive zip, string name) {
        var entry = zip.GetEntry(name);
        if (entry is null) {
            return null;
        }

        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);

        return copy.ToArray();
    }

    /// <summary>"on = true" (any case, spaces allowed) on the first line that is not a # comment and has an "=" (as the bots).</summary>
    internal static bool Parse(string text) {
        foreach (var raw in text.Split('\n')) {
            var line = raw.Trim();
            if (line.StartsWith('#') || !line.Contains('=')) {
                continue;
            }

            var parts = line.Split('=', 2);
            if (parts[0].Trim().EndsWith("on", StringComparison.OrdinalIgnoreCase)) {
                return parts[1].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }
}
