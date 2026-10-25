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
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Social;

/// <summary>What two wizards did together; each kind has its own count in the friends window's Recent tab.</summary>
internal enum RecentWizardKind {
    Combat,
    Instance,
    Hatch,
    PvP,
}

/// <summary>One wizard someone met, and what they did together.</summary>
internal sealed class RecentWizardEntry {
    public ulong CharId { get; set; }
    /// <summary>Unix seconds of the last time they met.</summary>
    public uint LastSeen { get; set; }
    public uint Combat { get; set; }
    public uint Instance { get; set; }
    public uint Hatch { get; set; }
    public uint PvP { get; set; }
}

/// <summary>
/// The friends window's Recent tab (missing-features list, rated 9): the wizards you battled, grouped, hatched or
/// dueled with lately. The live game keeps this on its chat server; here it is kept in recent-wizards.json next to the
/// server so it lasts through restarts. [RecentWizards] Enabled turns it off, MaxPerWizard caps each list.
/// </summary>
internal static class RecentWizards {

    internal const string FILE = "recent-wizards.json";

    private static bool s_enabled => ConfigurationManager.GetValue("RecentWizards.Enabled", true);
    private static int s_max => Math.Clamp(ConfigurationManager.GetValue("RecentWizards.MaxPerWizard", 50), 1, 200);

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
    };

    private static readonly object s_lock = new();
    private static Dictionary<string, List<RecentWizardEntry>> s_lists;

    internal static bool Enabled => s_enabled;

    /// <summary>Remembers that these wizards met, each in the others' list.</summary>
    internal static void Met(IEnumerable<ulong> charIds, RecentWizardKind kind) {
        if (!s_enabled || charIds is null) {
            return;
        }

        var ids = charIds.Where(id => id != 0).Distinct().ToArray();
        if (ids.Length < 2) {
            return;
        }

        try {
            lock (s_lock) {
                var lists = Lists();
                foreach (var owner in ids) {
                    foreach (var other in ids.Where(id => id != owner)) {
                        Add(lists, owner, other, kind);
                    }
                }

                Save(lists);
            }

            Logger.Debug("Recent wizards | {0} met: {1}.", Logger.Args(kind, string.Join(", ", ids)));
        }
        catch (Exception ex) {
            Logger.Error("Recent wizards | Could not remember who met: {0}", Logger.Args(ex.Message));
        }
    }

    /// <summary>Remembers that one wizard met another (only in the first one's list; the other side adds its own).</summary>
    internal static void MetOneWay(ulong owner, ulong other, RecentWizardKind kind) {
        if (!s_enabled || owner == 0 || other == 0 || owner == other) {
            return;
        }

        try {
            lock (s_lock) {
                var lists = Lists();
                Add(lists, owner, other, kind);
                Save(lists);
            }
        }
        catch (Exception ex) {
            Logger.Error("Recent wizards | Could not remember who met: {0}", Logger.Args(ex.Message));
        }
    }

    /// <summary>A wizard's list, the most recent first.</summary>
    internal static List<RecentWizardEntry> For(ulong owner) {
        lock (s_lock) {
            return Lists().TryGetValue(Key(owner), out var list)
                ? [.. list.OrderByDescending(e => e.LastSeen).Select(Copy)]
                : [];
        }
    }

    /// <summary>Empties a wizard's list.</summary>
    internal static int Clear(ulong owner) {
        lock (s_lock) {
            var lists = Lists();
            if (!lists.Remove(Key(owner), out var list)) {
                return 0;
            }

            Save(lists);

            return list.Count;
        }
    }

    private static void Add(Dictionary<string, List<RecentWizardEntry>> lists, ulong owner, ulong other, RecentWizardKind kind) {
        if (!lists.TryGetValue(Key(owner), out var list)) {
            list = [];
            lists[Key(owner)] = list;
        }

        var entry = list.FirstOrDefault(e => e.CharId == other);
        if (entry is null) {
            entry = new RecentWizardEntry { CharId = other };
            list.Add(entry);
        }

        entry.LastSeen = (uint) DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        switch (kind) {
            case RecentWizardKind.Combat: entry.Combat++; break;
            case RecentWizardKind.Instance: entry.Instance++; break;
            case RecentWizardKind.Hatch: entry.Hatch++; break;
            case RecentWizardKind.PvP: entry.PvP++; break;
        }

        // The oldest drop off once the list is full.
        if (list.Count > s_max) {
            list.Sort((a, b) => b.LastSeen.CompareTo(a.LastSeen));
            list.RemoveRange(s_max, list.Count - s_max);
        }
    }

    private static string Key(ulong charId) => charId.ToString(CultureInfo.InvariantCulture);

    private static RecentWizardEntry Copy(RecentWizardEntry e) => new() {
        CharId = e.CharId, LastSeen = e.LastSeen, Combat = e.Combat, Instance = e.Instance, Hatch = e.Hatch, PvP = e.PvP,
    };

    private static Dictionary<string, List<RecentWizardEntry>> Lists() {
        if (s_lists is not null) {
            return s_lists;
        }

        try {
            s_lists = File.Exists(FILE)
                ? JsonSerializer.Deserialize<Dictionary<string, List<RecentWizardEntry>>>(File.ReadAllText(FILE), s_options)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) {
            Logger.Error("Recent wizards | Could not read {0}, starting empty: {1}", Logger.Args(FILE, ex.Message));
        }

        s_lists ??= [];

        return s_lists;
    }

    private static void Save(Dictionary<string, List<RecentWizardEntry>> lists) {
        try {
            var temp = $"{FILE}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(lists, s_options));
            File.Move(temp, FILE, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Error("Recent wizards | Could not save {0}: {1}", Logger.Args(FILE, ex.Message));
        }
    }

}
