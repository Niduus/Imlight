/*
 * Spell name families (Niduus, 2026-10-22: "instead of labeling all of 'Scald' for example, I can just type 'Scald' and
 * it will include all cards named Scald"). A plain name such as "Scald" stands for every spell that is that spell or a
 * version of it: "Scald", "Scald (Treasure Card)", "Scald - Item Card", "Scald - Mob", "Scald_TC", "Scald TC"... but
 * not another spell whose name only starts the same ("Scalding Fury").
 *
 * The server (boss and player cheats) and the Manager (its "matches 7 spells" count) both use this one file, so they
 * agree on what a name covers. The Manager links it in (Imlight.Setup.csproj).
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Game.Combat;

internal static class SpellNameFamily {

    // What may come right after the name in a version's name.
    private static readonly string[] s_separators = [" - ", " -", "-", " – ", " (", "(", " [", "[", ":", "_", ","];

    // Words that, after a space, mark a version rather than another spell.
    private static readonly HashSet<string> s_versionWords = new(StringComparer.OrdinalIgnoreCase) {
        "tc", "treasure", "item", "items", "mob", "mobs", "monster", "boss", "npc", "pvp", "pve", "pet", "gear", "card",
        "enchanted", "amulet", "wand", "deck", "jewel", "ring", "athame", "hat", "robe", "boots", "mount", "tier", "rank",
        "v1", "v2", "v3", "ii", "iii", "iv", "old", "new", "test", "cheat", "trigger", "minion", "ai",
    };

    private static string Normalize(string? name) => string.Join(' ', (name ?? "").Trim().ToLowerInvariant()
        .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Whether <paramref name="name"/> is the spell <paramref name="family"/> or one of its versions.</summary>
    internal static bool InFamily(string? family, string? name) {
        var f = Normalize(family);
        var n = Normalize(name);
        if (f.Length == 0 || n.Length == 0) {
            return false;
        }

        if (n == f) {
            return true;
        }

        if (n.StartsWith(f, StringComparison.Ordinal)) {
            var rest = n[f.Length..];
            if (s_separators.Any(s => rest.StartsWith(s, StringComparison.Ordinal))) {
                return true;
            }

            if (rest.StartsWith(' ')) {
                var word = new string(rest.TrimStart().TakeWhile(char.IsLetterOrDigit).ToArray());

                return s_versionWords.Contains(word);
            }

            return false;
        }

        // "TC - Scald", "PvP: Scald": a version word, then a separator, then the name.
        if (n.EndsWith(f, StringComparison.Ordinal)) {
            var before = n[..^f.Length].TrimEnd(' ', '-', ':', '_', '–');

            return before.Length < n.Length - f.Length && before.Length > 0 && s_versionWords.Contains(before.Split(' ').Last());
        }

        return false;
    }

    /// <summary>Whether any of a spell's names (its game name, the name on its card, its base name) is in the family.</summary>
    internal static bool AnyInFamily(string? family, params string?[] names) => names.Any(n => InFamily(family, n));

    /// <summary>Whether a cheat's word for a spell is its template ID ("857718733"), not a name.</summary>
    internal static bool IsSpellId(string? word, out uint id) {
        var w = (word ?? "").Trim();
        id = 0;

        return w.Length > 0 && w.All(char.IsAsciiDigit) && uint.TryParse(w, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out id) && id > 0;
    }

    /// <summary>Whether any of the words is a spell's template ID.</summary>
    internal static bool AnyIds(IEnumerable<string>? wanted) => (wanted ?? []).Any(w => IsSpellId(w, out _));

    /// <summary>
    /// Whether a spell is one of <paramref name="wanted"/>: by exact name, or with <paramref name="anyVersion"/> by family.
    /// </summary>
    internal static bool IsOneOf(IEnumerable<string>? wanted, bool anyVersion, params string?[] names)
        => (wanted ?? []).Any(w => !string.IsNullOrWhiteSpace(w)
                                   && (anyVersion ? AnyInFamily(w, names)
                                       : names.Any(n => string.Equals(n?.Trim(), w.Trim(), StringComparison.OrdinalIgnoreCase))));

    /// <summary>
    /// A cheat's spell kinds (Category), worked out from what a spell is: the game's type name, what its effects do
    /// (Damage, Heal...) and whether it is a treasure card. Several kinds may be asked for, with commas between ("Charm,
    /// Ward"): a spell of any of them matches.
    /// </summary>
    internal static bool CategoryMatches(string? wanted, ICollection<string> kindsOfSpell)
        => string.IsNullOrWhiteSpace(wanted)
           || wanted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Any(w => kindsOfSpell.Contains(KindSynonym(w), StringComparer.OrdinalIgnoreCase));

    // Wizard404's and the players' words for kinds (Niduus, 2026-10-25: "jinx=trap, curse=weakness").
    private static readonly Dictionary<string, string> s_kindSynonyms = new(StringComparer.OrdinalIgnoreCase) {
        ["Jinx"] = "Trap", ["Jinxes"] = "Trap", ["Traps"] = "Trap", ["Curse"] = "Weakness", ["Curses"] = "Weakness",
        ["Weaknesses"] = "Weakness", ["Blades"] = "Blade", ["Shields"] = "Shield", ["Charms"] = "Charm", ["Wards"] = "Ward",
        ["Auras"] = "Aura", ["DoTs"] = "DoT", ["HoTs"] = "HoT", ["DamageOverTime"] = "DoT", ["HealOverTime"] = "HoT",
        ["Buff"] = "Blade", ["Debuff"] = "Weakness", ["Bubble"] = "Global", ["Minions"] = "Minion", ["Heals"] = "Heal",
    };

    /// <summary>The kind a word stands for ("Jinx" is Trap, "Curse" is Weakness), or the word itself.</summary>
    internal static string KindSynonym(string? word) {
        var w = (word ?? "").Trim();

        return s_kindSynonyms.TryGetValue(w, out var kind) ? kind : w;
    }

    /// <summary>Whether a pip cost is in a cheat's range. Max 0 is no top; an X spell costs 0.</summary>
    // max 0: no top; max -1: exactly 0 pips (an X spell or a 0-pip card).
    internal static bool CostMatches(int cost, int min, int max) => cost >= min && (max == 0 || (max < 0 ? cost == 0 : cost <= max));

}
