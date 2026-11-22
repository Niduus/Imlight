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
using System.Text.Json.Nodes;
using Imlight.Common;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// The signed Hive Mind file (Niduus, 2026-11-22: "All the bots MUST run off a signed Hive Mind file ... Without the Hive Mind
/// file, anyones bots are as good as fresh noob bots ... This isn't to be used on live realm, only on Ahziael").
/// Server\bots\hive-mind.ahzhive is made by tools/hive-sign/sign.py: a zip with manifest.json (what it is, who made it,
/// when, the SHA-256 of every brain file), signature.bin (ECDSA P-256 / SHA-256 over manifest.json, made with Niduus's private
/// key, which is in no repository) and brain/&lt;file&gt;. This class checks it with Niduus's public key below.
/// Stage 1 (now): the bots check the file at start and say what they found in the server log and in
/// logs/bots/hive-mind-check.txt; nothing about how they play changes yet.
/// </summary>
internal static class BotHive {

    internal const string FILE_NAME = "hive-mind.ahzhive";
    private const string FORMAT = "ahziael-hive-mind/1";

    // Niduus's Hive Mind public key (ECDSA P-256, SubjectPublicKeyInfo, base64). Only his private key signs a file it accepts.
    private const string PUBLIC_KEY =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEFhM3aqlBd1KF5GQkhDxGUQWmyX2dVn9T4bMgsDDVgDxtzGdWhM3/mer5eU1JmHa0iFEP9YSZmzu49BfXVG2Eww==";

    private static readonly object s_lock = new();
    private static bool? s_valid;
    private static string s_what = "";

    /// <summary>Where the server keeps the signed file: its own bots folder.</summary>
    internal static string FilePath => Path.Combine(AppContext.BaseDirectory, "bots", FILE_NAME);

    /// <summary>True when a Hive Mind file signed by Niduus is there and nothing in it was changed. Checked once per start.</summary>
    internal static bool Valid {
        get {
            lock (s_lock) {
                if (s_valid is { } known) {
                    return known;
                }

                s_valid = Check(FilePath, out s_what);
                Report();

                return s_valid.Value;
            }
        }
    }

    /// <summary>What the check found, in plain words.</summary>
    internal static string What {
        get {
            _ = Valid;
            return s_what;
        }
    }

    /// <summary>Checks a Hive Mind file: Niduus's signature over its manifest, and every brain file against its SHA-256.</summary>
    internal static bool Check(string path, out string what) {
        try {
            if (!File.Exists(path)) {
                what = $"no signed Hive Mind file ({path})";
                return false;
            }

            using var zip = ZipFile.OpenRead(path);
            var manifest = Read(zip, "manifest.json");
            var signature = Read(zip, "signature.bin");
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
            if ((string) info?["format"] != FORMAT || info["files"] is not JsonArray files) {
                what = "the Hive Mind file is of an unknown kind";
                return false;
            }

            foreach (var entry in files) {
                var name = (string) entry?["path"];
                var data = name is null ? null : Read(zip, "brain/" + name);
                if (data is null || !string.Equals(Convert.ToHexString(SHA256.HashData(data)), (string) entry["sha256"], StringComparison.OrdinalIgnoreCase)) {
                    what = $"the Hive Mind file was changed after it was signed ({name})";
                    return false;
                }
            }

            what = $"signed Hive Mind file by {(string) info["by"]}, made {(string) info["made"]}, {files.Count} brain file(s)";
            return true;
        }
        catch (Exception ex) {
            what = "the Hive Mind file could not be read (" + ex.Message + ")";
            return false;
        }
    }

    private static byte[] Read(ZipArchive zip, string name) {
        var entry = zip.GetEntry(name);
        if (entry is null) {
            return null;
        }

        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);

        return copy.ToArray();
    }

    private static void Report() {
        var line = (s_valid == true ? "Hive Mind OK: " : "Hive Mind NOT VALID: ") + s_what + ".";
        try {
            Logger.Information("{0}", Logger.Args(line));
        }
        catch (Exception) {
            // The file below still says it.
        }

        try {
            var folder = Path.GetFullPath(BotMistakes.FOLDER);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "hive-mind-check.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (Exception) {
            // Nothing more to do.
        }
    }

}
