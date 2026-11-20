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
using System.Text.RegularExpressions;
using Imlight.Common;
using Imlight.CoreLib.Shared.Behaviors;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// True Form's build on top of his school and setup:
/// <list type="bullet">
/// <item>His weave school (Niduus, 2026-11-20: "Go ice weave fire. This will make TF's primary ice and weave fire. Able to
/// take up 2 classes at once"): gear, jewels and pet count both schools' stats, his deck carries both schools' spells, and
/// his school pips (the deck's Archmastery school) go to the weave school.</item>
/// <item>Stat asks ("Get more Ice Accuracy will have TF make efforts to fit that"): the stats his commander asked for count
/// double when gear, jewels and pet talents are picked.</item>
/// <item>Build memory ("TF will have memories of these builds for the next time they encounter that player as a commander
/// and ask if they would like the last build"): each commander's last build, kept on disk.</item>
/// </list>
/// </summary>
internal static class TrueFormBuild {

    private static readonly string[] s_schools = ["fire", "ice", "storm", "myth", "life", "death", "balance"];

    /// <summary>A stat the commander asked for more of: BotGear's kind ("Accuracy", "ReduceDamage"...) and school ("" = every school).</summary>
    internal sealed record Push(string Kind, string School) {
        internal string Words => $"{(School.Length == 0 ? "universal" : School)} {KindWords(Kind)}";

        internal string Key => $"{Kind}:{School}";

        internal static Push FromKey(string key) {
            var parts = (key ?? "").Split(':');
            return parts.Length == 2 && parts[0].Length > 0 ? new Push(parts[0], parts[1]) : null;
        }
    }

    /// <summary>A commander's last build.</summary>
    internal sealed record Saved(MagicSchool School, TrueForm.Mode? Role, TrueForm.Setup Kind, MagicSchool? Weave, IReadOnlyList<Push> Pushes) {
        internal string Words {
            get {
                var setup = Kind == TrueForm.Setup.Own ? "balanced" : TrueForm.SetupWords(Kind).Replace("my ", "").Replace(" setup", "");
                var text = Kind == TrueForm.Setup.Universal ? "universal damage" : $"{School} {setup}";
                if (Role is { } role && Kind == TrueForm.Setup.Own) {
                    text = $"{School} playing {TrueForm.Words(role).Split(':')[0]}";
                }

                if (Weave is { } w) {
                    text += $", weaving {w}";
                }

                if (Pushes.Count > 0) {
                    text += $", with more {string.Join(" and ", Pushes.Select(p => p.Words))}";
                }

                return text;
            }
        }
    }

    // ---------------------------------------------------------------------
    // Weave and stat asks now (kept on disk with his school, so a restart keeps them)
    // ---------------------------------------------------------------------

    private const string FILE = "build.txt";

    private static string BuildPath => Path.Combine(TrueForm.Seat.Folder, FILE);

    private static readonly object s_lock = new();
    private static string s_weave;
    private static List<Push> s_pushes = [];
    private static bool s_read;

    // A weave or stat ask waiting for his next change (asked with "go ice weave fire": done when he switches).
    private static string s_pendingWeave;
    private static bool s_pendingWeaveSet;
    private static List<Push> s_pendingPushes;
    private static DateTime s_pendingAt;

    /// <summary>His weave school now, or null.</summary>
    internal static MagicSchool? Weave {
        get {
            Read();
            return s_weave is { } w && Enum.TryParse<MagicSchool>(w, true, out var school) ? school : null;
        }
    }

    /// <summary>The stats his commander asked for more of.</summary>
    internal static IReadOnlyList<Push> Pushes {
        get {
            Read();
            lock (s_lock) {
                return [.. s_pushes];
            }
        }
    }

    /// <summary>A weave or stat ask is waiting for his next change (so "I'm already Ice" is no answer).</summary>
    internal static bool HasPending {
        get {
            lock (s_lock) {
                return (s_pendingWeaveSet || s_pendingPushes is not null) && DateTime.UtcNow - s_pendingAt < TimeSpan.FromMinutes(5);
            }
        }
    }

    /// <summary>A weave school (or none) for his next change.</summary>
    internal static void AskWeave(MagicSchool? weave) {
        lock (s_lock) {
            s_pendingWeave = weave?.ToString();
            s_pendingWeaveSet = true;
            s_pendingAt = DateTime.UtcNow;
        }
    }

    /// <summary>Stat asks (or none) for his next change.</summary>
    internal static void AskPushes(IEnumerable<Push> pushes) {
        lock (s_lock) {
            s_pendingPushes = [.. pushes];
            s_pendingAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Called when he takes a new school or setup: a weave or stat ask said with it is put in place; going home (Myth, no
    /// commander) drops both; any other change keeps them.
    /// </summary>
    internal static void OnBecome(MagicSchool school, TrueForm.Mode? role, ulong headId, TrueForm.Setup kind) {
        Read();
        lock (s_lock) {
            var fresh = DateTime.UtcNow - s_pendingAt < TimeSpan.FromMinutes(5);
            if (headId == 0 && school == MagicSchool.Myth && kind == TrueForm.Setup.Own) {
                s_weave = null;
                s_pushes = [];
            }
            else {
                if (s_pendingWeaveSet && fresh) {
                    s_weave = s_pendingWeave;
                }

                if (s_pendingPushes is { } pushes && fresh) {
                    s_pushes = pushes;
                }
            }

            // Weaving his own school is no weave.
            if (s_weave is { } w && w.Equals(school.ToString(), StringComparison.OrdinalIgnoreCase)) {
                s_weave = null;
            }

            s_pendingWeaveSet = false;
            s_pendingWeave = null;
            s_pendingPushes = null;
            Write();
        }

        if (headId != 0) {
            Remember(headId, new Saved(school, role, kind, Weave, Pushes));
        }
    }

    private static void Read() {
        if (s_read) {
            return;
        }

        lock (s_lock) {
            if (s_read) {
                return;
            }

            s_read = true;
            try {
                if (File.Exists(BuildPath)) {
                    var parts = File.ReadAllText(BuildPath).Trim().Split('|');
                    s_weave = parts.Length > 0 && parts[0].Length > 0 ? parts[0] : null;
                    s_pushes = parts.Length > 1 ? [.. parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Push.FromKey).Where(p => p is not null)] : [];
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                Logger.Debug("True Form: could not read his build: {0}", Logger.Args(e.Message));
            }
        }
    }

    private static void Write() {
        try {
            Directory.CreateDirectory(TrueForm.Seat.Folder);
            File.WriteAllText(BuildPath, $"{s_weave ?? ""}|{string.Join(",", s_pushes.Select(p => p.Key))}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Logger.Warning("True Form: could not keep his build: {0}", Logger.Args(e.Message));
        }
    }

    // ---------------------------------------------------------------------
    // Words
    // ---------------------------------------------------------------------

    /// <summary>
    /// The weave school in an order ("go ice weave fire", "weave into storm", "and weaving death", "secondary school life"),
    /// with the order left without it; null when it names none.
    /// </summary>
    internal static MagicSchool? WeaveFrom(string order, out string rest) {
        rest = order ?? "";
        var m = Regex.Match(rest, @"\b(and\s+|with\s+|plus\s+)?(weave|weaves|weaving|weaved|woven|secondary|second school|off school|offschool|sub school|subschool)\s+(school\s+)?(in(to)?\s+|with\s+|as\s+)?(a\s+|an\s+)?(?<s>fire|ice|storm|myth|life|death|balance)\b");
        if (!m.Success) {
            m = Regex.Match(rest, @"\b(?<s>fire|ice|storm|myth|life|death|balance)\s+(weave|weaving|secondary|second school|off school)\b");
        }

        if (!m.Success || !Enum.TryParse<MagicSchool>(m.Groups["s"].Value, true, out var school)) {
            return null;
        }

        rest = Regex.Replace(rest.Remove(m.Index, m.Length), @"\s+", " ").Trim();
        return school;
    }

    /// <summary>"No weave", "stop weaving", "drop the weave", "unweave": his weave dropped.</summary>
    internal static bool NoWeaveFrom(string order) => Regex.IsMatch(order ?? "",
        @"\b(no|stop|drop|remove|clear|without|forget|lose|cancel)\s+(the\s+|my\s+|your\s+|ur\s+)?(weave|weaving|second school|secondary)\b|\bunweave\b");

    /// <summary>
    /// The stats asked for in an order ("get more ice accuracy", "more pierce", "boost universal resist", "max outgoing
    /// healing", "prioritize health"), or empty.
    /// </summary>
    internal static List<Push> PushesFrom(string order) {
        var pushes = new List<Push>();
        var w = (order ?? "").ToLowerInvariant();
        if (!Regex.IsMatch(w, @"\b(more|extra|higher|max|maximi[sz]e|boost|increase|raise|prioriti[sz]e|focus on|stack|add|get|need|want)\b")) {
            return pushes;
        }

        foreach (Match m in Regex.Matches(w,
                     @"\b(?:(?<s>universal|uni|all school|every school|fire|ice|storm|myth|life|death|balance)\s+)?(?<k>accuracy|acc|damage|dmg|pierce|piercing|armor piercing|resist|resistance|res|health|hp|crit|critical|critical rating|block|outgoing healing|outgoing|incoming healing|incoming|healing|power pip chance|power pips?|pip chance|stun resist|pip conversion|archmastery|shadow pip rating)\b")) {
            var kind = m.Groups["k"].Value switch {
                "accuracy" or "acc" => "Accuracy",
                "damage" or "dmg" => "Damage",
                "pierce" or "piercing" or "armor piercing" => "ArmorPiercing",
                "resist" or "resistance" or "res" => "ReduceDamage",
                "health" or "hp" => "MaxHealth",
                "crit" or "critical" or "critical rating" => "CriticalHit",
                "block" => "Block",
                "outgoing healing" or "outgoing" or "healing" => "Healing",
                "incoming healing" or "incoming" => "IncHealing",
                "power pip chance" or "power pip" or "power pips" or "pip chance" => "PowerPips",
                "stun resist" => "StunResist",
                "pip conversion" => "PipConversion",
                "archmastery" => "Archmastery",
                "shadow pip rating" => "ShadowPip",
                _ => null,
            };
            if (kind is null) {
                continue;
            }

            var said = m.Groups["s"].Value;
            var school = said.Length == 0 || said is "universal" or "uni" or "all school" or "every school" || kind is "MaxHealth" or "Healing" or "IncHealing"
                         or "PowerPips" or "Block" or "StunResist" or "Archmastery" or "ShadowPip"
                ? "" : char.ToUpperInvariant(said[0]) + said[1..];
            var push = new Push(kind, school);
            if (!pushes.Contains(push)) {
                pushes.Add(push);
            }
        }

        return pushes;
    }

    /// <summary>"Reset stats", "clear stat asks", "no more stat requests", "normal stats": his stat asks dropped.</summary>
    internal static bool NoPushesFrom(string order) => Regex.IsMatch(order ?? "",
        @"\b(reset|clear|drop|forget|remove|cancel|no more)\s+(the\s+|my\s+|your\s+|ur\s+|all\s+)?(stat|stats)(\s+(asks|requests|changes|focus|priorities))?\b|\bnormal stats\b");

    private static string KindWords(string kind) => kind switch {
        "ArmorPiercing" => "pierce",
        "ReduceDamage" => "resist",
        "MaxHealth" => "health",
        "CriticalHit" => "critical",
        "Healing" => "outgoing healing",
        "IncHealing" => "incoming healing",
        "PowerPips" => "power pip chance",
        "StunResist" => "stun resist",
        "PipConversion" => "pip conversion",
        "ShadowPip" => "shadow pip rating",
        _ => kind.ToLowerInvariant(),
    };

    // ---------------------------------------------------------------------
    // Build memory per commander
    // ---------------------------------------------------------------------

    private const string MEMORY_FILE = "builds.txt";

    private static string MemoryPath => Path.Combine(TrueForm.Seat.Folder, MEMORY_FILE);

    private static Dictionary<ulong, (Saved Build, DateTime At)> s_memory;

    private static Dictionary<ulong, (Saved Build, DateTime At)> Memory {
        get {
            if (s_memory is { } ready) {
                return ready;
            }

            var memory = new Dictionary<ulong, (Saved, DateTime)>();
            try {
                if (File.Exists(MemoryPath)) {
                    foreach (var line in File.ReadAllLines(MemoryPath)) {
                        var p = line.Split('|');
                        if (p.Length >= 7 && ulong.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                            && Enum.TryParse<MagicSchool>(p[1], true, out var school) && Enum.TryParse<TrueForm.Setup>(p[3], true, out var kind)) {
                            TrueForm.Mode? role = Enum.TryParse<TrueForm.Mode>(p[2], true, out var r) ? r : null;
                            MagicSchool? weave = Enum.TryParse<MagicSchool>(p[4], true, out var w) ? w : null;
                            var pushes = p[5].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Push.FromKey).Where(x => x is not null).ToList();
                            var at = DateTime.TryParse(p[6], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : DateTime.UtcNow;
                            memory[id] = (new Saved(school, role, kind, weave, pushes), at);
                        }
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                Logger.Debug("True Form: could not read his build memory: {0}", Logger.Args(e.Message));
            }

            return s_memory = memory;
        }
    }

    /// <summary>A commander's last build with him, or null.</summary>
    internal static Saved LastFor(ulong commander) {
        lock (s_lock) {
            return Memory.TryGetValue(commander, out var saved) ? saved.Build : null;
        }
    }

    private static void Remember(ulong commander, Saved build) {
        lock (s_lock) {
            Memory[commander] = (build, DateTime.UtcNow);
            try {
                Directory.CreateDirectory(TrueForm.Seat.Folder);
                File.WriteAllLines(MemoryPath, Memory.OrderByDescending(m => m.Value.At).Take(500).Select(m => string.Create(CultureInfo.InvariantCulture,
                    $"{m.Key}|{m.Value.Build.School}|{m.Value.Build.Role?.ToString() ?? ""}|{m.Value.Build.Kind}|{m.Value.Build.Weave?.ToString() ?? ""}|{string.Join(",", m.Value.Build.Pushes.Select(p => p.Key))}|{m.Value.At:O}")));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                Logger.Warning("True Form: could not keep his build memory: {0}", Logger.Args(e.Message));
            }
        }
    }

    /// <summary>The same build as he has now (so there is nothing to ask).</summary>
    internal static bool IsNow(Saved build) => build.School == TrueForm.School && build.Kind == TrueForm.Kind && build.Weave == Weave
                                               && build.Pushes.Select(p => p.Key).ToHashSet().SetEquals(Pushes.Select(p => p.Key));

    /// <summary>A commander's last build offered and waiting for their yes (2 minutes).</summary>
    internal static volatile Tuple<ulong, Saved> Offered;

    internal static DateTime OfferedAt;
}
