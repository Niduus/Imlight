/*
 * Roshambo decks (Niduus, 2026-10-31: "When on, mobs will have their existing deck exchanged with duplicates, only cards
 * that can roshambo are placed instead of the default cards"). The [Roshambo] section of Imlight.ini, read live: a
 * change counts from the next battle. The game files are never changed; only the deck a creature fights with is.
 *
 * A Roshambo card is one the game itself marks as one:
 *   - the PvP_Roshambo adjective (the Rhoshambo Spells folder: the Gambit Jinns and Onis, the Ramp spells such as
 *     Meltdown, Putrefaction and Righting the Scales), or
 *   - a Roshambo keyword in its card text: $Clear$, $Echo$ or $Gambit$ (and the others in s_keywords). Most of these
 *     are Spellwrighting upgrades: Dark Sprite has none, but its upgrade Dark Sprite - T02 - A has a Gambit.
 *
 * Each card of a creature's deck becomes, for that battle:
 *   1. itself, when it is already a Roshambo card;
 *   2. else a Roshambo version of the same card, picked at random (the same name shown in battle, the same Spell
 *      Fusion adjective such as Fusion_DarkSprite, or the same Spellwrighting group): Dark Sprite -> Dark Sprite - T03 - A;
 *   3. else, with OtherCards on, a Roshambo card of the same school and kind (damage, heal, charm...), as close to its
 *      pip cost as can be found; with OtherCards off the card stays as it is.
 * Treasure cards, pet May Casts, spells with no casting animation and battleground-only spells are never put in.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Combat;

internal static class RoshamboDecks {

    internal static bool Enabled => ConfigurationManager.GetValue("Roshambo.Enabled", false);
    // Bosses get Roshambo decks too (off: only regular monsters).
    private static bool s_bosses => ConfigurationManager.GetValue("Roshambo.Bosses", true);
    // Cards with no Roshambo version become another Roshambo card of their school and kind (off: they stay).
    private static bool s_otherCards => ConfigurationManager.GetValue("Roshambo.OtherCards", true);
    // A deck saved in the app's Bosses tab is kept as it was made (off: it is swapped too).
    private static bool s_keepCustomDecks => ConfigurationManager.GetValue("Roshambo.KeepCustomDecks", true);

    // The card text words that make a card a Roshambo card.
    private static readonly string[] s_keywords = ["$Clear$", "$Echo$", "$Gambit$"];
    private const string ROSHAMBO_ADJECTIVE = "PvP_Roshambo";
    // Spells only for one place, left out of every pick.
    private static readonly string[] s_neverFolders = ["Battleground", "Test/", "Cantrip", "Sigil", "WhirlyBurly", "Sherlock"];
    // Spells made for one fight: fine as the same card's version, but not picked for other cards.
    private static readonly string[] s_oneFightFolders = ["Fight Specific", "Raid", "Epic-CombatTrigger"];

    private sealed record Card(uint Id, string School, string Kind, int Pips, bool XPip, HashSet<string> Keys, bool ForOthers);

    private sealed class Pool {
        public readonly HashSet<uint> Roshambo = [];
        public readonly Dictionary<string, List<Card>> ByKey = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<Card> ForOthers = [];
    }

    private static readonly Lazy<Pool> s_pool = new(BuildPool, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The deck a creature fights with when Roshambo decks are on, or <paramref name="deck"/> as it was.
    /// </summary>
    /// <param name="deck">The creature's deck for this battle.</param>
    /// <param name="isBoss">Whether the creature is a boss.</param>
    /// <param name="customDeck">Whether the deck was saved in the app's Bosses tab.</param>
    /// <param name="who">The creature's name, for the log.</param>
    internal static List<CombatDeckSpellData> Apply(List<CombatDeckSpellData> deck, bool isBoss, bool customDeck, string who) {
        if (!Enabled || deck is not { Count: > 0 } || (isBoss && !s_bosses) || (customDeck && s_keepCustomDecks)) {
            return deck;
        }

        try {
            var pool = s_pool.Value;
            var swapped = new List<CombatDeckSpellData>();
            var changes = new List<string>();
            foreach (var card in deck) {
                var id = Swap(pool, card.TemplateId);
                if (id != card.TemplateId) {
                    changes.Add($"{NameOf(card.TemplateId)} -> {NameOf(id)}");
                }

                // Two cards may become the same one: the creature carries it once, with both counts.
                if (swapped.Find(s => s.TemplateId == id) is { } same) {
                    same.Quantity = (uint) Math.Min(uint.MaxValue, (ulong) same.Quantity + card.Quantity);

                    continue;
                }

                swapped.Add(new CombatDeckSpellData {
                    TemplateId = id,
                    Quantity = card.Quantity,
                    IsBattleCard = card.IsBattleCard,
                    IsItemCard = card.IsItemCard,
                    IsTreasureCard = card.IsTreasureCard,
                });
            }

            if (changes.Count > 0) {
                Logger.Information("Roshambo deck for {Who}: {Changes}.", Logger.Args(who, string.Join(", ", changes)));
            }

            return swapped;
        }
        catch (Exception ex) {
            Logger.Warning("Roshambo deck for {Who} could not be made; its own deck is used. {Message}", Logger.Args(who, ex.Message));

            return deck;
        }
    }

    /// <summary>Whether the game marks a spell as a Roshambo card.</summary>
    internal static bool IsRoshambo(SpellTemplate spell) {
        if (spell is null) {
            return false;
        }

        if (spell.m_adjectives?.Any(a => string.Equals((string) a, ROSHAMBO_ADJECTIVE, StringComparison.OrdinalIgnoreCase)) == true) {
            return true;
        }

        string key = spell.m_description;
        var text = string.IsNullOrEmpty(key) ? "" : Locale.GetEnglishName(key);

        return s_keywords.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private static uint Swap(Pool pool, uint id) {
        if (pool.Roshambo.Contains(id) || CoreObjectFactory.GetCoreTemplate(id) is not SpellTemplate spell || IsRoshambo(spell)) {
            return id;
        }

        // A Roshambo copy of the very same card first.
        var versions = KeysOf(id, spell)
            .SelectMany(k => pool.ByKey.TryGetValue(k, out var list) ? list : [])
            .DistinctBy(c => c.Id)
            .ToList();
        if (versions.Count > 0) {
            return versions[Random.Shared.Next(versions.Count)].Id;
        }

        if (!s_otherCards) {
            return id;
        }

        string school = spell.m_sMagicSchoolName;
        string kind = spell.m_sTypeName;
        var pips = spell.m_spellRank?.m_spellRank ?? 0;
        var xPip = spell.m_spellRank?.m_xPipSpell == true;

        // Then a card of its school and kind, as near its pip cost as there is; then of its school; then of its kind.
        IEnumerable<Card>[] tries = [
            pool.ForOthers.Where(c => Same(c.School, school) && Same(c.Kind, kind)),
            pool.ForOthers.Where(c => Same(c.School, school)),
            pool.ForOthers.Where(c => Same(c.Kind, kind)),
        ];
        foreach (var found in tries) {
            var list = found.ToList();
            if (list.Count == 0) {
                continue;
            }

            var nearest = list.Min(c => Distance(c, pips, xPip));
            var closest = list.Where(c => Distance(c, pips, xPip) == nearest).ToList();

            return closest[Random.Shared.Next(closest.Count)].Id;
        }

        return id;
    }

    private static int Distance(Card card, int pips, bool xPip)
        => card.XPip == xPip ? Math.Abs(card.Pips - pips) : 100;

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string NameOf(uint id) {
        if (CoreObjectFactory.GetCoreTemplate(id) is not SpellTemplate spell) {
            return id.ToString();
        }

        string name = spell.m_name;

        return name;
    }

    /// <summary>What ties versions of one card together: its name in battle, its Spell Fusion adjective, its upgrade group.</summary>
    private static HashSet<string> KeysOf(uint id, SpellTemplate spell) {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string displayKey = spell.m_displayName;
        var shown = string.IsNullOrEmpty(displayKey) ? "" : Locale.GetEnglishName(displayKey);
        if (!string.IsNullOrWhiteSpace(shown)) {
            keys.Add("name:" + string.Join(' ', shown.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        }

        foreach (var adjective in spell.m_adjectives ?? []) {
            var text = (string) adjective;
            if (text?.StartsWith("Fusion_", StringComparison.OrdinalIgnoreCase) == true) {
                keys.Add("fusion:" + text);
            }
        }

        var group = TieredSpells.GroupOfTemplate(id);
        if (group >= 0) {
            keys.Add("group:" + group);
        }

        return keys;
    }

    private static Pool BuildPool() {
        var pool = new Pool();
        foreach (var (id, spell, path) in Randomization.Randomizer.CombatSpellsWithPaths()) {
            if (id > uint.MaxValue || !IsRoshambo(spell) || s_neverFolders.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase))) {
                continue;
            }

            var templateId = (uint) id;
            var keys = KeysOf(templateId, spell);
            string school = spell.m_sMagicSchoolName;
            string kind = spell.m_sTypeName;
            var card = new Card(templateId, school ?? "", kind ?? "", spell.m_spellRank?.m_spellRank ?? 0,
                spell.m_spellRank?.m_xPipSpell == true, keys,
                !s_oneFightFolders.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase)));

            pool.Roshambo.Add(templateId);
            foreach (var key in keys) {
                if (!pool.ByKey.TryGetValue(key, out var list)) {
                    pool.ByKey[key] = list = [];
                }

                list.Add(card);
            }

            if (card.ForOthers) {
                pool.ForOthers.Add(card);
            }
        }

        Logger.Information("Roshambo decks can use {Count} Roshambo cards ({Others} for cards with no Roshambo version).",
            Logger.Args(pool.Roshambo.Count, pool.ForOthers.Count));

        return pool;
    }

}
