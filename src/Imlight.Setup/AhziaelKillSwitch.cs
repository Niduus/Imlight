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
 *
 */

using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Imlight.Setup;

/// <summary>
/// Ahziael's own kill switch for the bots (Niduus, 2026-11-22: "Make sure that the next Ahziael update respects the killswitch
/// too. Make a separate one for that though, so when the time comes, I can just flick it"; card: "Bots in Ahziael only").
/// It is ahziael-killswitch.txt at the top of Niduus's private bots repo (Niduus/ahziael-bots), separate from the bots' own
/// killswitch.txt. Every Bot Update round signs it into Server\bots\hive-mind.ahzhive (tools/hive-sign/sign.py), and Ahziael
/// reads it from there, checked with Niduus's public key, each time it starts the server.
/// When it says "on = true", Ahziael starts the server without any bots: it sends no Ahziael signal and tells the server
/// (IMLIGHT_AHZIAEL_BOTS_OFF) not to load the bots at all. The game server itself runs for players as usual.
/// When the file or the switch is missing, or the file is not signed by Niduus, this switch is simply not flicked (the bots'
/// own kill switch and signed Hive Mind check still decide whether bots play).
/// </summary>
internal static class AhziaelKillSwitch {

    internal const string BOTS_OFF_VARIABLE = "IMLIGHT_AHZIAEL_BOTS_OFF";
    internal const string SIGNED_NAME = "ahziael-killswitch.txt";
    private const string HIVE_FILE = "hive-mind.ahzhive";
    private const string FORMAT = "ahziael-hive-mind/1";

    // Niduus's Hive Mind public key (the same one the bots hold in bots/BotShared/BotHive.cs).
    private const string PUBLIC_KEY =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEFhM3aqlBd1KF5GQkhDxGUQWmyX2dVn9T4bMgsDDVgDxtzGdWhM3/mer5eU1JmHa0iFEP9YSZmzu49BfXVG2Eww==";

    /// <summary>True when Niduus's signed switch says Ahziael must start the server without bots. Writes what it found.</summary>
    internal static bool BotsOff(string serverFolder) {
        var on = Read(Path.Combine(serverFolder, "bots", HIVE_FILE), out var why);
        try {
            var folder = Path.Combine(serverFolder, "logs", "bots");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "ahziael-killswitch.txt"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Ahziael {(on ? "starts the server WITHOUT bots" : "lets the bots start")}: {why}{Environment.NewLine}");
        }
        catch (Exception) {
            // Only the note is lost.
        }

        return on;
    }

    private static bool Read(string path, out string why) {
        try {
            if (!File.Exists(path)) {
                why = "no signed Hive Mind file, so the switch is not flicked";
                return false;
            }

            using var zip = ZipFile.OpenRead(path);
            var manifest = Entry(zip, "manifest.json");
            var signature = Entry(zip, "signature.bin");
            if (manifest is null || signature is null) {
                why = "the Hive Mind file has no manifest or signature, so the switch is not flicked";
                return false;
            }

            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PUBLIC_KEY), out _);
            if (!key.VerifyData(manifest, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) {
                why = "the Hive Mind file is not signed by Niduus, so the switch is not flicked";
                return false;
            }

            var info = JsonNode.Parse(manifest);
            if ((string?) info?["format"] != FORMAT || info["files"] is not JsonArray files) {
                why = "the Hive Mind file is of an unknown kind, so the switch is not flicked";
                return false;
            }

            foreach (var file in files) {
                if ((string?) file?["path"] != SIGNED_NAME) {
                    continue;
                }

                var data = Entry(zip, "brain/" + SIGNED_NAME);
                if (data is null || !string.Equals(Convert.ToHexString(SHA256.HashData(data)), (string?) file["sha256"], StringComparison.OrdinalIgnoreCase)) {
                    why = "the switch was changed after signing, so it is not flicked";
                    return false;
                }

                var on = Parse(Encoding.UTF8.GetString(data));
                why = $"the switch says on = {(on ? "true" : "false")} (signed {(string?) info["made"]})";
                return on;
            }

            why = "the signed Hive Mind file has no Ahziael switch, so it is not flicked";
            return false;
        }
        catch (Exception ex) {
            why = "the Hive Mind file could not be read (" + ex.Message + "), so the switch is not flicked";
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

    /// <summary>"on = true" (any case, spaces allowed) on the first line that is not a # comment and names "on".</summary>
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
