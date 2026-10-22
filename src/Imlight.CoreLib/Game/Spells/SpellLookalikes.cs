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
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;

namespace Imlight.CoreLib.Game.Spells;

/// <summary>
/// Boss and monster copies of spells often play the boss's own animation first (a boss summon,
/// then the spell). For each such copy this finds a normal player spell whose effects are exactly
/// the same, so a wizard casting the copy can be shown the normal spell's animation instead.
/// Copies with no exact twin are left alone.
/// </summary>
public static class SpellLookalikes {

    private static readonly string[] s_copyWords = ["Fight Specific", "MOB", "Rematch"];
    private static readonly string[] s_notPlayerWords = ["Fight Specific", "Battleground", "ForTesting", "MOB", "Rematch", "Boss", "NPC", "Test", "Rasputin", "Wand"];

    // Wand hits, monster attacks ("NA ..."), pet May Casts (their animation needs a pet: Emulate Life showed nothing as
    // "Pet - CircleOfThorns", Niduus 2026-10-22) and empty animations are not what a player would expect to see instead.
    private static readonly string[] s_notTwinStarts = ["Wand-", "NA ", "Pet - ", "Pet-"];
    private static readonly string[] s_notTwinBases = ["Blank", "RavenwoodCam"];

    /// <summary>
    /// Maps the template id of every boss or monster copy to the template id of its player twin.
    /// </summary>
    /// <param name="spells">Every spell template, with its file and template id.</param>
    /// <param name="hasAnimation">Whether the game has an animation for a spell base, when known.</param>
    public static Dictionary<uint, uint> Build(IEnumerable<(string Path, SpellTemplate Spell, uint Id)> spells,
                                               Func<string, bool> hasAnimation = null) {
        var players = new Dictionary<string, (string Path, SpellTemplate Spell, uint Id)>();
        var copies = new List<(string Path, SpellTemplate Spell, uint Id)>();
        var playerBases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var all = spells.ToList();
        // Animations pets also use rank after the wizard-only ones (Emulate Life showed nothing as the pet's Circle of Thorns).
        s_petBases = all.Where(e => e.Spell is not null && e.Spell.m_spellSourceType == kSpellSourceType.kPet && !IsEmpty(e.Spell.m_spellBase))
            .Select(e => (string) e.Spell.m_spellBase).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in all) {
            var spell = entry.Spell;
            if (spell?.m_effects is null || spell.m_effects.Count == 0 || IsEmpty(spell.m_spellBase) || IsEmpty(spell.m_sTypeName)) {
                continue;
            }

            string name = spell.m_name;
            if (IsCopy(entry.Path, name)) {
                // Polymorph forms' own spells keep their animations: those are the form's attacks, not a boss's.
                if (!Contains(name, "Poly")) {
                    copies.Add(entry);
                }

                continue;
            }

            if (!IsPlayerSpell(entry.Path, name, spell) || spell.m_spellSourceType == kSpellSourceType.kPet || (hasAnimation is not null && !hasAnimation(spell.m_spellBase))) {
                continue;
            }

            playerBases.Add(spell.m_spellBase);
            Keep(players, Signature(spell), entry);
            if (IsGlobal(spell)) {
                Keep(players, Signature(spell, numbers: false), entry);
            }
        }

        var twins = new Dictionary<uint, uint>();
        foreach (var copy in copies) {
            // A copy that already plays a player spell's animation (such as "Wyldfire - Monster") keeps it.
            if (playerBases.Contains(copy.Spell.m_spellBase)) {
                continue;
            }

            // A global's animation does not show its numbers, so a stronger or weaker copy of a player global
            // (the bosses' "Emulate" globals) still gets that global's animation.
            var found = players.TryGetValue(Signature(copy.Spell), out var twin);
            if (IsGlobal(copy.Spell) && players.TryGetValue(Signature(copy.Spell, numbers: false), out var loose)
                && (!found || Rank(loose).Item1 < Rank(twin).Item1)) {
                // An exact twin whose animation pets share loses to a looser one only wizards use.
                twin = loose;
                found = true;
            }

            if (!found) {
                continue;
            }

            string copyBase = copy.Spell.m_spellBase;
            string twinBase = twin.Spell.m_spellBase;
            if (!string.Equals(copyBase, twinBase, StringComparison.OrdinalIgnoreCase)) {
                twins[copy.Id] = twin.Id;
            }
        }

        return twins;
    }

    private static void Keep(Dictionary<string, (string Path, SpellTemplate Spell, uint Id)> players, string key,
                             (string Path, SpellTemplate Spell, uint Id) entry) {
        if (!players.TryGetValue(key, out var best) || Rank(entry).CompareTo(Rank(best)) < 0) {
            players[key] = entry;
        }
    }

    private static bool IsGlobal(SpellTemplate spell)
        => spell.m_effects.All(effect => effect is not null && effect.GetType() == typeof(SpellEffect) && effect.m_effectTarget == kEffectTarget.kGlobal);

    internal static bool IsCopy(string path, string name)
        => s_copyWords.Any(word => Contains(path, word) || Contains(name, word));

    private static bool IsPlayerSpell(string path, string name, SpellTemplate spell)
        => !s_notPlayerWords.Any(word => Contains(path, word) || Contains(name, word))
           && !spell.m_battlegroundsOnly
           && spell.m_spellRank is not null
           && !s_notTwinStarts.Any(start => name?.StartsWith(start, StringComparison.OrdinalIgnoreCase) == true)
           && !s_notTwinBases.Any(word => string.Equals(spell.m_spellBase, word, StringComparison.OrdinalIgnoreCase));

    private static HashSet<string> s_petBases = [];

    // Lower is better: an animation only wizards use, then trained spells, treasure cards, anything else.
    private static (int, int, int, uint) Rank((string Path, SpellTemplate Spell, uint Id) entry) {
        var pet = s_petBases.Contains(entry.Spell.m_spellBase) ? 1 : 0;
        var where = Contains(entry.Path, "Trained Spells") ? 0 : Contains(entry.Path, "TreasureCards") ? 1 : 2;
        string name = entry.Spell.m_name;

        return (pet, where, name?.Length ?? 0, entry.Id);
    }

    /// <summary>
    /// Everything a spell does, written out as text, so two spells that do the same thing get the same text.
    /// </summary>
    internal static string Signature(SpellTemplate spell, bool numbers = true) {
        var text = new StringBuilder(numbers ? "" : "~numbers~");
        foreach (var effect in spell.m_effects) {
            Write(text, effect, 0, numbers);
            text.Append(';');
        }

        return text.ToString();
    }

    private static void Write(StringBuilder text, object value, int depth, bool numbers) {
        switch (value) {
            case null:
                text.Append('~');
                return;
            case PropertyClass property when depth < 8:
                text.Append(property.GetType().Name).Append('{');
                foreach (var info in Properties(property.GetType())) {
                    if (!numbers && s_numbers.Contains(info.Name)) {
                        continue;
                    }

                    text.Append(info.Name).Append('=');
                    Write(text, info.GetValue(property), depth + 1, numbers);
                    text.Append(',');
                }
                text.Append('}');
                return;
            case string or ByteString:
                text.Append(value.ToString());
                return;
            case IEnumerable list:
                text.Append('[');
                foreach (var item in list) {
                    Write(text, item, depth + 1, numbers);
                    text.Append('|');
                }
                text.Append(']');
                return;
            default:
                text.Append(value);
                return;
        }
    }

    private static readonly HashSet<string> s_numbers = ["m_effectParam", "m_numRounds", "m_paramPerRound", "m_rank"];

    private static readonly Dictionary<System.Type, PropertyInfo[]> s_properties = [];

    private static PropertyInfo[] Properties(System.Type type) {
        lock (s_properties) {
            if (!s_properties.TryGetValue(type, out var list)) {
                list = [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.Name.StartsWith("m_"))
                    .OrderBy(p => p.Name, StringComparer.Ordinal)];
                s_properties[type] = list;
            }

            return list;
        }
    }

    private static bool IsEmpty(ByteString text) => string.IsNullOrEmpty(text);

    private static bool Contains(string text, string word)
        => text?.Contains(word, StringComparison.OrdinalIgnoreCase) == true;
}
