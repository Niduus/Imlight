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
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// True Form's deck as his commander builds it, every detail kept (Niduus, 2026-11-20: "I want him to listen to every detail
/// including adding spells, specific ones too. Not just 'Add blades' but 'Add Deathblade'"; "in his healing section, he has
/// spells that do damage or summon minions when I hadn't say to include them"; "Only accepting one response over another").
/// <list type="bullet">
/// <item>Only: "heals and blades only", "only pack heals, blades and shields": nothing else goes in.</item>
/// <item>Never: "no damage spells", "no minions", "remove globals": kinds left out.</item>
/// <item>Named spells: "add Deathblade", "add 3 Legion Shield", "put in Fairy and Pixie" go in first (as many copies as
/// asked, up to the deck's rule); "remove Fireblade", "take out Satyr" never.</item>
/// <item>Every part of one message counts ("only heals and blades, add Deathblade, no minions"); each order adds to the plan
/// until "normal deck" or "reset your deck".</item>
/// </list>
/// The plan is kept in logs/trueform/deck-plan.txt (over bot updates) and rules his battle hand too.
/// </summary>
internal static class TrueFormDeckPlan {

    internal sealed record Named(uint Id, string Shown, int Count);

    private sealed class Plan {
        internal List<string> Only;
        internal HashSet<string> Never = new(StringComparer.Ordinal);
        internal List<Named> Add = [];
        internal Dictionary<uint, string> Remove = [];
    }

    private const string FILE = "deck-plan.txt";

    private static readonly object s_lock = new();
    private static Plan s_plan = Load();

    // The kinds a commander names, in his words.
    private static readonly (string Tag, string Pattern, string Words)[] s_kinds = [
        ("attack", @"hits?|hitting|damage|dmg|attacks?|attacking|nukes?|aoes?|offensive|dots?", "damage spells"),
        ("heal", @"heals?|healing|healers?|heal spells?", "heals"),
        ("blade", @"blades?|blading|buffs?", "blades"),
        ("trap", @"traps?|hexe?s?|curses?", "traps"),
        ("shield", @"shields?|wards?|absorbs?|tower shields?", "shields"),
        ("weakness", @"weakness(?:es)?|weakens?", "weaknesses"),
        ("stun", @"stuns?|stunning|freezes?", "stuns"),
        ("cleanse", @"cleanses?|cleansing|charm removal on us", "cleanses"),
        ("dispel", @"dispels?|charm removals?|enchant removals?|removers?|steal charms?|pierces? charms?", "dispels and charm removal"),
        ("minion", @"minions?|summons?|summoning|pets?|animate", "minions"),
        ("global", @"globals?|bubbles?|fields?|domes?", "globals"),
        ("aura", @"auras?", "auras"),
        ("prism", @"prisms?", "prisms"),
        ("pip", @"pip spells?|pips?|power pips?|pip gain", "pip spells"),
        ("enchant", @"enchants?|enchantments?", "enchantments"),
        ("polymorph", @"polymorphs?|polys?", "polymorphs"),
    ];

    private static readonly Dictionary<string, string> s_words = s_kinds.ToDictionary(k => k.Tag, k => k.Words);

    /// <summary>Kinds left out unless asked for by name, whatever the plan: what does damage, summons or turns him.</summary>
    private static readonly string[] s_harmful = ["attack", "minion", "polymorph"];

    /// <summary>A heals deck when nothing more exact was said ("focus on healing", "heals deck").</summary>
    internal static readonly string[] HealDefaults = ["heal", "blade", "shield", "cleanse"];

    /// <summary>Whether any plan is set.</summary>
    internal static bool Any {
        get {
            lock (s_lock) {
                return s_plan.Only is not null || s_plan.Never.Count > 0 || s_plan.Add.Count > 0 || s_plan.Remove.Count > 0;
            }
        }
    }

    /// <summary>The kinds the plan allows in the order said, or null when any kind may go in.</summary>
    internal static IReadOnlyList<string> Only {
        get {
            lock (s_lock) {
                return s_plan.Only?.ToList();
            }
        }
    }

    /// <summary>The spells asked for by name, in the order asked.</summary>
    internal static IReadOnlyList<Named> Added {
        get {
            lock (s_lock) {
                return [.. s_plan.Add];
            }
        }
    }

    /// <summary>Whether the plan lets this spell in (named spells always; removed ones never).</summary>
    internal static bool Allows(uint id) {
        lock (s_lock) {
            if (s_plan.Remove.ContainsKey(id)) {
                return false;
            }

            if (s_plan.Add.Any(a => a.Id == id)) {
                return true;
            }

            var tags = Tags(id);
            if (tags.Overlaps(s_plan.Never)) {
                return false;
            }

            if (s_plan.Only is { } only) {
                return tags.Overlaps(only) && s_harmful.All(h => !tags.Contains(h) || only.Contains(h));
            }

            return true;
        }
    }

    /// <summary>Whether a spell id was asked for by name.</summary>
    internal static bool IsAdded(uint id) {
        lock (s_lock) {
            return s_plan.Add.Any(a => a.Id == id);
        }
    }

    // ---------------------------------------------------------------------
    // What kind a spell is
    // ---------------------------------------------------------------------

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, HashSet<string>> s_tags = new();

    /// <summary>Every kind a spell is: attack, heal, blade, trap, shield, weakness, stun, cleanse, dispel, minion, global, aura, prism, pip, enchant, polymorph, other.</summary>
    internal static HashSet<string> Tags(uint id) => s_tags.GetOrAdd(id, key => {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        try {
            var card = RewrittenAI.ReadTemplate(key);
            if (CoreObjectFactory.GetCoreTemplate(key) is not SpellTemplate template || card is null) {
                tags.Add("other");
                return tags;
            }

            var leaves = CombatEnchantments.Leaves(template.m_effects ?? []).Where(e => e is not null).ToList();
            bool Friendly(kEffectTarget t) => t is kEffectTarget.kSelf or kEffectTarget.kFriendlySingle or kEffectTarget.kFriendlySingleNotMe
                or kEffectTarget.kFriendlyTeam or kEffectTarget.kFriendlyTeamAllAtOnce or kEffectTarget.kMultiTargetFriendly;
            if (card.Hit) {
                tags.Add("attack");
            }

            if (card.Heal && !card.Hit) {
                tags.Add("heal");
            }

            if (card.Blade && !card.Hit) {
                tags.Add("blade");
            }

            if (card.Trap && !card.Hit) {
                tags.Add("trap");
            }

            if (card.Shield && !card.Hit) {
                tags.Add("shield");
            }

            if (card.Weakness && !card.Hit) {
                tags.Add("weakness");
            }

            if (card.Stun) {
                tags.Add("stun");
            }

            if (card.RemovesCharms) {
                tags.Add("dispel");
            }

            if (leaves.Any(e => Friendly(e.m_effectTarget) && e.m_effectType is kSpellEffects.kRemoveCharm or kSpellEffects.kRemoveOverTime)) {
                tags.Add("cleanse");
            }

            var types = leaves.Select(e => e.m_effectType.ToString()).ToList();
            if (types.Any(t => t.Contains("Summon", StringComparison.Ordinal))) {
                tags.Add("minion");
            }

            if (types.Any(t => t.Contains("Polymorph", StringComparison.Ordinal))) {
                tags.Add("polymorph");
            }

            if (types.Any(t => t.Contains("Pip", StringComparison.Ordinal))) {
                tags.Add("pip");
            }

            if (leaves.Any(e => e.m_effectTarget == kEffectTarget.kGlobal)) {
                tags.Add("global");
            }

            if (card.Shown.EndsWith("Prism", StringComparison.OrdinalIgnoreCase)) {
                tags.Add("prism");
            }

            if (CombatEnchantments.IsEnchantment(template)) {
                tags.Add("enchant");
            }

            if (tags.Count == 0 && leaves.Count > 0 && leaves.All(e => e.m_effectTarget == kEffectTarget.kSelf) && leaves.Any(e => e.m_numRounds > 0)) {
                tags.Add("aura");
            }

            if (tags.Count == 0) {
                tags.Add("other");
            }
        }
        catch (Exception) {
            tags.Add("other");
        }

        return tags;
    });

    /// <summary>The first kind of a spell for the deck's turns: attack, heal, blade, trap, shield ... other.</summary>
    internal static string Main(uint id) {
        var tags = Tags(id);
        foreach (var tag in new[] { "minion", "attack", "heal", "blade", "trap", "shield", "weakness", "stun", "cleanse", "dispel", "global", "aura", "prism", "pip", "enchant", "polymorph" }) {
            if (tags.Contains(tag)) {
                return tag;
            }
        }

        return "other";
    }

    // ---------------------------------------------------------------------
    // Reading an order
    // ---------------------------------------------------------------------

    private enum Verb { None, Add, Remove, Only }

    private static readonly Regex s_deckWord = new(@"\b(deck|decks|cards?|spells?|spell list|pack|packs|packing|bring|carry|load|include|add|remove|take out|put in|get rid of|exclude)\b", RegexOptions.Compiled);

    private static readonly Regex s_reset = new(@"\b(reset|normal|default|regular|usual|mixed|standard|clear)\b.*\b(deck|plan|spells?)\b|\bdeck\b.*\b(reset|back to normal|cleared?)\b", RegexOptions.Compiled);

    private static readonly Regex s_remove = new(@"\b(remove|take out|take away|pull out|drop|get rid of|exclude|no more|nothing|no|not|without|zero|never (?:use|pack|include|put in|bring)|don'?t (?:use|pack|include|put in|put|add|bring|carry|want|need|keep)|do not (?:use|pack|include|put in|put|add|bring|carry|want|need|keep)|stop (?:using|packing))\b\s*(?:any\s+|using\s+|use\s+|more\s+|the\s+|of\s+)?", RegexOptions.Compiled);

    private static readonly Regex s_add = new(@"\b(add|include|put in|put|pack|bring|carry|load|use|keep|give (?:yourself|urself)|get|want|need|have|with|plus|also)\b\s*", RegexOptions.Compiled);

    private static readonly Regex s_filler = new(@"\b(my|your|ur|the|a|an|some|more|extra|spells?|cards?|deck|in|into|to|from|of|it|them|in it|as well|too|also|please|pls|for me|for us|copies|copy|of each|each|all|every|types?|kinds?|ones?|just|any|at all|that do|that|do|which|is|are|be|is it|instead|now|then|its|his|on|for|focus|focused|setup|set up|build|make|rebuild|i|we|want|you|to have|have|should|must|can|could|would|go)\b", RegexOptions.Compiled);

    private static readonly Dictionary<string, int> s_numbers = new(StringComparer.Ordinal) {
        ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10,
        ["a couple"] = 2, ["couple"] = 2, ["a few"] = 3, ["few"] = 3, ["single"] = 1, ["double"] = 2, ["triple"] = 3,
    };

    /// <summary>
    /// Reads a deck order said to him, every part of it. True when it was one; <paramref name="reply"/> says back exactly what
    /// he took in, and what he could not find. <paramref name="healsOnly"/> is true when the plan leaves out damage.
    /// </summary>
    internal static bool TryOrder(string text, int level, TrueForm.DeckFocus? focus, out string reply, out bool healsOnly) {
        reply = "";
        healsOnly = false;
        var raw = (text ?? "").Replace("\r", " ").Replace("\n", ". ");
        // Each part on its own: "only heals and blades, add Deathblade. No minions".
        var clauses = Regex.Split(raw, @"[.;!?,]+|\b(?:also|then|plus|and also|and then|but)\b", RegexOptions.IgnoreCase)
            .Select(TrueForm.Order).Where(c => c.Length > 0).ToList();
        var whole = TrueForm.Order(raw);
        if (whole.Length == 0) {
            return false;
        }

        var deckish = s_deckWord.IsMatch(whole);
        var changes = new List<string>();
        var missing = new List<string>();
        var plan = Copy();
        var verb = Verb.None;
        var any = false;
        if (s_reset.IsMatch(whole) && !Regex.IsMatch(whole, @"\b(only|add|remove|no|without)\b")) {
            lock (s_lock) {
                s_plan = new Plan();
                Save(s_plan);
            }

            reply = "My deck plan is cleared: back to my normal mix.";
            return true;
        }

        List<string> saidOnly = null;
        var addedKinds = new List<string>();
        foreach (var clause in clauses) {
            var c = " " + clause + " ";
            string rest;
            var only = Regex.IsMatch(c, @"\bonly\b|\bnothing but\b|\bjust\b.*\b(and|or)\b");
            var remove = s_remove.Match(c);
            var add = s_add.Match(c);
            if (only && (!remove.Success || remove.Index > Regex.Match(c, @"\bonly\b|\bnothing but\b|\bjust\b").Index)) {
                verb = Verb.Only;
                rest = Regex.Replace(c, @"\b(only|nothing but|just)\b", " ");
                rest = s_add.Replace(rest, " ");
            }
            else if (remove.Success && (!add.Success || remove.Index <= add.Index)) {
                verb = Verb.Remove;
                rest = c[(remove.Index + remove.Length)..];
            }
            else if (add.Success && (deckish || Regex.IsMatch(add.Value, @"\b(add|include|put in|put|pack|bring|carry|load)\b"))) {
                verb = Verb.Add;
                rest = c[(add.Index + add.Length)..];
            }
            else if (verb != Verb.None) {
                // "add Deathblade, Fireblade and Stormblade": a part with no verb of its own goes on with the last one.
                rest = c;
            }
            else {
                continue;
            }

            var items = Regex.Split(rest, @"\band\b|\bor\b|&|\+|/").Select(i => i.Trim()).Where(i => i.Length > 0).ToList();
            var onlyKinds = new List<string>();
            foreach (var part in items) {
                // "take out the damage spells and put in more shields": a list part with its own verb turns to it.
                var item = part;
                var own = " " + item + " ";
                if (s_remove.Match(own) is { Success: true, Index: <= 1 } ownRemove) {
                    verb = Verb.Remove;
                    item = own[(ownRemove.Index + ownRemove.Length)..].Trim();
                }
                else if (s_add.Match(own) is { Success: true, Index: <= 1 } ownAdd && Regex.IsMatch(ownAdd.Value, @"\b(add|include|put in|put|pack|bring|carry|load)\b")) {
                    verb = Verb.Add;
                    item = own[(ownAdd.Index + ownAdd.Length)..].Trim();
                }

                var (count, words) = CountOf(item);
                words = Regex.Replace(s_filler.Replace(" " + words + " ", " "), @"\s+", " ").Trim();
                if (words.Length == 0) {
                    continue;
                }

                if (KindOf(words) is { } kind) {
                    any = true;
                    switch (verb) {
                        case Verb.Only:
                            if (!onlyKinds.Contains(kind)) {
                                onlyKinds.Add(kind);
                            }

                            break;
                        case Verb.Remove:
                            plan.Never.Add(kind);
                            plan.Only?.Remove(kind);
                            changes.Add($"no {s_words[kind]}");
                            break;
                        case Verb.Add:
                            plan.Never.Remove(kind);
                            if (plan.Only is { } list && !list.Contains(kind)) {
                                list.Add(kind);
                            }

                            addedKinds.Add(kind);
                            changes.Add($"{s_words[kind]} in");
                            break;
                    }

                    continue;
                }

                // A spell by its name ("Deathblade", "legion shield", "dark sprite"), in an order about his deck.
                if (deckish && FindSpell(words, level) is { } entry) {
                    any = true;
                    if (verb == Verb.Remove) {
                        plan.Remove[entry.Id] = entry.Shown;
                        plan.Add.RemoveAll(a => a.Id == entry.Id);
                        changes.Add($"{entry.Shown} out");
                    }
                    else {
                        plan.Remove.Remove(entry.Id);
                        var had = plan.Add.FirstOrDefault(a => a.Id == entry.Id);
                        plan.Add.RemoveAll(a => a.Id == entry.Id);
                        var copies = Math.Clamp(count ?? had?.Count ?? 1, 1, 10);
                        plan.Add.Add(new Named(entry.Id, entry.Shown, copies));
                        changes.Add(copies > 1 ? $"{copies} {entry.Shown}" : entry.Shown);
                    }

                    continue;
                }

                // Only a real try at a name counts as missing (not "please" or "it").
                if (words.Length >= 4 && (verb != Verb.Remove || deckish)) {
                    missing.Add(words);
                }
            }

            if (verb == Verb.Only && onlyKinds.Count > 0) {
                // "only pack heals, blades and shields": every part of the list, not the last one.
                saidOnly ??= [];
                saidOnly.AddRange(onlyKinds.Where(k => !saidOnly.Contains(k)));
                plan.Only = [.. saidOnly];
                foreach (var k in onlyKinds) {
                    plan.Never.Remove(k);
                }
            }
        }

        if (saidOnly is { Count: > 0 }) {
            changes.Insert(0, $"only {Join(saidOnly.Select(k => s_words[k]))}");
        }

        // A heals deck said with more detail ("focus on healing. No damage spells"): still nothing but heals, blades, shields
        // and cleanses (no minions or other extras), plus the kinds asked in.
        if (any && focus == TrueForm.DeckFocus.Heals && plan.Only is null) {
            plan.Only = [.. HealDefaults.Concat(addedKinds).Distinct().Where(k => !plan.Never.Contains(k))];
            changes.Insert(0, $"only {Join(plan.Only.Select(k => s_words[k]))}");
        }

        // "Focus on healing", "heals deck" with nothing more exact: heals, blades, shields and cleanses, and nothing that hits or summons.
        if (!any && focus is { } f) {
            switch (f) {
                case TrueForm.DeckFocus.Heals:
                    plan.Only = [.. HealDefaults];
                    changes.Add($"only {Join(HealDefaults.Select(k => s_words[k]))}");
                    any = true;
                    break;
                case TrueForm.DeckFocus.Mixed:
                    plan = new Plan();
                    changes.Add("my normal mix");
                    any = true;
                    break;
                default:
                    plan.Only = null;
                    plan.Never.Clear();
                    any = true;
                    break;
            }
        }

        if (!any) {
            if (missing.Count > 0 && deckish) {
                reply = $"I couldn't find a spell called {Join(missing.Select(m => $"\"{m}\""))}. Say its name as the game shows it, like \"add Deathblade\".";
                return true;
            }

            return false;
        }

        lock (s_lock) {
            s_plan = plan;
            Save(plan);
        }

        healsOnly = plan.Only is { } o ? !o.Contains("attack") : plan.Never.Contains("attack");
        var have = Describe(plan);
        reply = $"Got it: {Join(changes.Distinct())}." + (missing.Count > 0 ? $" I couldn't find {Join(missing.Select(m => $"\"{m}\""))}; say it as the game names it." : "")
                + (have.Length > 0 ? $" My deck plan now: {have}." : "");
        Logger.Information("True Form: deck plan from \"{0}\": {1}{2}", Logger.Args(text, have, missing.Count > 0 ? $" (not found: {string.Join(", ", missing)})" : ""));
        return true;
    }

    /// <summary>The plan in words: "only heals and blades; Deathblade x2; no minions; Fireblade out".</summary>
    internal static string Words() {
        lock (s_lock) {
            return Describe(s_plan);
        }
    }

    private static string Describe(Plan plan) {
        var parts = new List<string>();
        if (plan.Only is { Count: > 0 } only) {
            parts.Add($"only {Join(only.Select(k => s_words[k]))}");
        }

        if (plan.Add.Count > 0) {
            parts.Add("with " + Join(plan.Add.Select(a => a.Count > 1 ? $"{a.Shown} x{a.Count}" : a.Shown)));
        }

        if (plan.Never.Count > 0) {
            parts.Add($"no {Join(plan.Never.Select(k => s_words[k]))}");
        }

        if (plan.Remove.Count > 0) {
            parts.Add($"never {Join(plan.Remove.Values)}");
        }

        return string.Join("; ", parts);
    }

    private static string KindOf(string words) {
        foreach (var (tag, pattern, _) in s_kinds) {
            if (Regex.IsMatch(words, $@"^(?:{pattern})(?:\s+(?:spells?|cards?))?$")) {
                return tag;
            }
        }

        return null;
    }

    private static (int? Count, string Words) CountOf(string item) {
        var m = Regex.Match(item, @"^\s*(\d+)\s*x?\s+(.+)$");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n)) {
            return (n, m.Groups[2].Value);
        }

        m = Regex.Match(item, @"^(.+?)\s+x\s*(\d+)\s*$");
        if (m.Success && int.TryParse(m.Groups[2].Value, out n)) {
            return (n, m.Groups[1].Value);
        }

        foreach (var (word, value) in s_numbers.OrderByDescending(x => x.Key.Length)) {
            if (Regex.IsMatch(item, $@"^\s*{word}\s+(?:of\s+)?")) {
                return (value, Regex.Replace(item, $@"^\s*{word}\s+(?:of\s+)?", ""));
            }
        }

        return (null, item);
    }

    private static TrueFormPool.Entry FindSpell(string words, int level) {
        var found = TrueFormPool.Find(words, level, false);
        if (found is null && words.EndsWith('s')) {
            found = TrueFormPool.Find(words[..^1], level, false);
        }

        if (found is not null) {
            return found;
        }

        // Every version too ("Deathblade" when a stronger one heads its family).
        static string Squash(string t) => new((t ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var want = Squash(words);
        var all = TrueFormPool.Versions(level, false);
        return all.Where(e => Squash(e.Shown) == want || Squash(e.Shown) == want.TrimEnd('s')).OrderBy(e => e.Treasure).FirstOrDefault()
               ?? all.Where(e => want.Length >= 5 && Squash(e.Shown).StartsWith(want, StringComparison.Ordinal)).OrderBy(e => e.Treasure).ThenBy(e => e.Shown.Length).FirstOrDefault();
    }

    private static string Join(IEnumerable<string> items) {
        var list = items.ToList();
        return list.Count switch {
            0 => "",
            1 => list[0],
            2 => $"{list[0]} and {list[1]}",
            _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1],
        };
    }

    // ---------------------------------------------------------------------
    // Kept on disk
    // ---------------------------------------------------------------------

    private static string PathOf => Path.Combine(UltimusSeat.TrueForm.Folder, FILE);

    private static Plan Copy() {
        lock (s_lock) {
            return new Plan {
                Only = s_plan.Only?.ToList(),
                Never = new HashSet<string>(s_plan.Never, StringComparer.Ordinal),
                Add = [.. s_plan.Add],
                Remove = new Dictionary<uint, string>(s_plan.Remove),
            };
        }
    }

    /// <summary>Clears the plan (back to his normal mix).</summary>
    internal static void Clear() {
        lock (s_lock) {
            s_plan = new Plan();
            Save(s_plan);
        }
    }

    private static void Save(Plan plan) {
        try {
            Directory.CreateDirectory(UltimusSeat.TrueForm.Folder);
            var lines = new List<string>();
            if (plan.Only is { } only) {
                lines.Add("only|" + string.Join(",", only));
            }

            if (plan.Never.Count > 0) {
                lines.Add("never|" + string.Join(",", plan.Never));
            }

            lines.AddRange(plan.Add.Select(a => $"add|{a.Id}|{a.Count}|{a.Shown}"));
            lines.AddRange(plan.Remove.Select(r => $"remove|{r.Key}|{r.Value}"));
            File.WriteAllLines(PathOf, lines);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Logger.Debug("True Form: could not keep his deck plan: {0}", Logger.Args(e.Message));
        }
    }

    private static Plan Load() {
        var plan = new Plan();
        try {
            if (!File.Exists(PathOf)) {
                return plan;
            }

            foreach (var line in File.ReadAllLines(PathOf)) {
                var p = line.Split('|');
                switch (p[0]) {
                    case "only" when p.Length > 1:
                        plan.Only = [.. p[1].Split(',', StringSplitOptions.RemoveEmptyEntries)];
                        break;
                    case "never" when p.Length > 1:
                        plan.Never = new HashSet<string>(p[1].Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
                        break;
                    case "add" when p.Length > 3 && uint.TryParse(p[1], out var id) && int.TryParse(p[2], out var n):
                        plan.Add.Add(new Named(id, p[3], n));
                        break;
                    case "remove" when p.Length > 2 && uint.TryParse(p[1], out var gone):
                        plan.Remove[gone] = p[2];
                        break;
                }
            }
        }
        catch (Exception) {
            // A fresh plan.
        }

        return plan;
    }
}
