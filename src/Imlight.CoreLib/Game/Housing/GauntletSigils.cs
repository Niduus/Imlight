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
 * ========================================================================
 * HOUSE GAUNTLETS
 * ========================================================================
 *
 * PURPOSE:
 * The sigil places set by hand in the game (Niduus, 2026-10-24: "Can we make a feature so I can manually adjust the
 * sigil?"): the .sigil command saves, per gauntlet template, where its pad stands relative to the gauntlet, how high,
 * which way its arc faces and its size, in gauntlet-sigils.json next to the server. Every copy of that gauntlet uses it.
 * The values beat the code's defaults and the Housing.GauntletSigil* ini keys.
 *
 * USAGE EXAMPLE:
 * var set = GauntletSigils.Of(701493);              // null when nothing is saved for it
 * GauntletSigils.Change(701493, "Winterbane", s => s.Height = 40);
 *
 * NOTE:
 * - The file is read again whenever it changes (the Manager's Reset buttons edit it), and Version moves then.
 * - OffsetX/OffsetY are in the gauntlet's own frame (so they turn with it): X along its front, Y to its side. Turn is the
 *   arc's facing in degrees from the gauntlet's yaw. A value left out (null) keeps the default.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/24/2026
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Housing;

/// <summary>
/// The sigil of one gauntlet template, as set by hand.
/// </summary>
internal sealed class GauntletSigilSetting {
    /// <summary>The gauntlet's name, for the Manager.</summary>
    public string Name { get; set; } = "";
    /// <summary>The pad centre along the gauntlet's front (its own frame).</summary>
    public float? OffsetX { get; set; }
    /// <summary>The pad centre to the gauntlet's side (its own frame).</summary>
    public float? OffsetY { get; set; }
    /// <summary>How high the pad stands over the gauntlet's floor.</summary>
    public float? Height { get; set; }
    /// <summary>The arc's facing, in degrees from the gauntlet's yaw.</summary>
    public float? Turn { get; set; }
    /// <summary>The pad's size (1 is the towers' pad).</summary>
    public float? Scale { get; set; }
    /// <summary>When it was last changed, for the Manager.</summary>
    public string Updated { get; set; } = "";

    [JsonIgnore]
    internal bool IsEmpty => OffsetX is null && OffsetY is null && Height is null && Turn is null && Scale is null;

    internal GauntletSigilSetting Copy() => (GauntletSigilSetting) MemberwiseClone();

    internal string Signature
        => string.Join(',', new[] { OffsetX, OffsetY, Height, Turn, Scale }
            .Select(v => v is { } f ? f.ToString("0.##", CultureInfo.InvariantCulture) : "-"));
}

/// <summary>
/// gauntlet-sigils.json: the hand-set sigils of the gauntlets, by template.
/// </summary>
internal static class GauntletSigils {

    internal const string FILE = "gauntlet-sigils.json";

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Lock s_lock = new();
    private static Dictionary<uint, GauntletSigilSetting> s_current = [];
    private static DateTime s_written = DateTime.MinValue;
    private static long s_checked;
    private static int s_version;

    /// <summary>Moves whenever the saved sigils change (by the command or the file).</summary>
    internal static int Version {
        get {
            Refresh();

            return Volatile.Read(ref s_version);
        }
    }

    /// <summary>The hand-set sigil of a template, or <c>null</c> when none is saved.</summary>
    internal static GauntletSigilSetting Of(uint templateId) {
        Refresh();
        lock (s_lock) {
            return s_current.TryGetValue(templateId, out var set) ? set : null;
        }
    }

    /// <summary>Every hand-set sigil.</summary>
    internal static IReadOnlyDictionary<uint, GauntletSigilSetting> All() {
        Refresh();
        lock (s_lock) {
            return new Dictionary<uint, GauntletSigilSetting>(s_current);
        }
    }

    /// <summary>Changes the hand-set sigil of a template and saves the file.</summary>
    internal static GauntletSigilSetting Change(uint templateId, string name, Action<GauntletSigilSetting> change) {
        Refresh(force: true);
        lock (s_lock) {
            var set = s_current.TryGetValue(templateId, out var old) ? old.Copy() : new GauntletSigilSetting();
            change(set);
            set.Name = name ?? set.Name;
            set.Updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            var next = new Dictionary<uint, GauntletSigilSetting>(s_current);
            if (set.IsEmpty) {
                next.Remove(templateId);
            } else {
                next[templateId] = set;
            }

            Save(next);

            return set.IsEmpty ? null : set;
        }
    }

    /// <summary>Forgets the hand-set sigil of a template, so it goes back to the defaults.</summary>
    internal static bool Remove(uint templateId) {
        Refresh(force: true);
        lock (s_lock) {
            if (!s_current.ContainsKey(templateId)) {
                return false;
            }

            var next = new Dictionary<uint, GauntletSigilSetting>(s_current);
            next.Remove(templateId);
            Save(next);

            return true;
        }
    }

    private static void Save(Dictionary<uint, GauntletSigilSetting> next) {
        var text = JsonSerializer.Serialize(next.OrderBy(p => p.Key)
            .ToDictionary(p => p.Key.ToString(CultureInfo.InvariantCulture), p => p.Value), s_options);
        var temp = FILE + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, FILE, overwrite: true);
        s_current = next;
        s_written = File.GetLastWriteTimeUtc(FILE);
        Interlocked.Increment(ref s_version);
    }

    // Reads the file again when it changed, at most every two seconds (every step of a player asks).
    private static void Refresh(bool force = false) {
        var now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref s_checked) < 2000) {
            return;
        }

        Interlocked.Exchange(ref s_checked, now);
        try {
            var written = File.Exists(FILE) ? File.GetLastWriteTimeUtc(FILE) : DateTime.MinValue;
            if (written == s_written) {
                return;
            }

            lock (s_lock) {
                if (written == s_written) {
                    return;
                }

                var read = written == DateTime.MinValue
                    ? []
                    : JsonSerializer.Deserialize<Dictionary<string, GauntletSigilSetting>>(File.ReadAllText(FILE), s_options) ?? [];
                s_current = read
                    .Where(p => p.Value is not null && uint.TryParse(p.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    .ToDictionary(p => uint.Parse(p.Key, CultureInfo.InvariantCulture), p => p.Value);
                s_written = written;
                Interlocked.Increment(ref s_version);
                Logger.Information("Gauntlet: {Count} hand-set sigil(s) {From}.",
                    Logger.Args(s_current.Count, written == DateTime.MinValue ? "(no " + FILE + ")" : "read from " + FILE));
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) {
            // The Manager may still be writing it; it is read again on the next check.
            Logger.Debug("Gauntlet: could not read {File} yet: {Reason}", Logger.Args(FILE, ex.Message));
        }
    }

}
