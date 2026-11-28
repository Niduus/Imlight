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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Imcodec.Cryptography;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Spells;

/// <summary>
/// Cards made in Blood Moon (Niduus, 2026-11-23: "create custom cards"). Each card lives in
/// spiraldb-extra/Customs/CustomCards.json (read again when the file changes, no restart). The game can only draw cards
/// it has in its own files, so a Blood Moon card is always shown as one of the game's cards (its "Shown as" card: that
/// card's picture, name and cast animation), while the server casts it with the card's own pips, accuracy, school and
/// effects. A card reaches battle as an item card of a Blood Moon gear piece (Game/Equipment/CustomGear.cs); the server
/// marks each copy in the hand so it knows which card it really is.
/// </summary>
internal static class CustomCards {

    internal const string FILE = "CustomCards.json";

    /// <summary>One effect line: what it does (Damage, Heal, ...), how much, its school, who it hits and for how many rounds.</summary>
    internal sealed record Line(string Type, int Amount, string School, string Target, int Rounds);

    /// <summary>One card as the file holds it.</summary>
    internal sealed record Card(int Id, string Name, uint ShownAs, string School, int Pips, int Accuracy, List<Line> Effects);

    private static readonly object s_lock = new();
    private static DateTime s_checked = DateTime.MinValue;
    private static DateTime s_fileTime = DateTime.MinValue;
    private static Dictionary<int, Card> s_cards = [];
    private static readonly ConcurrentDictionary<int, SpellTemplate> s_templates = new();
    private static readonly ConcurrentDictionary<kSpellEffects, SpellEffect> s_samples = new();

    // Which hand or gear card is which Blood Moon card. Weak, so used-up cards are forgotten with their objects.
    private static readonly ConditionalWeakTable<Spell, object> s_marks = new();
    private static readonly ConditionalWeakTable<ProvideSpellEffect, object> s_provides = new();

    private static string FilePath => Path.Combine(SpiralDB.ExtraPath, "Customs", FILE);

    internal static IReadOnlyCollection<Card> All {
        get {
            Reload();
            lock (s_lock) {
                return [.. s_cards.Values.OrderBy(c => c.Id)];
            }
        }
    }

    internal static Card Get(int id) {
        Reload();
        lock (s_lock) {
            return s_cards.GetValueOrDefault(id);
        }
    }

    // ------------------------------------------------------------------ marking cards

    /// <summary>The Blood Moon card a spell is, or 0.</summary>
    internal static int IdOf(Spell spell) => spell is not null && s_marks.TryGetValue(spell, out var id) ? (int) id : 0;

    /// <summary>Marks a spell as a Blood Moon card and gives it the card's pips, accuracy and school. Nothing for 0.</summary>
    internal static void Mark(Spell spell, int id) {
        if (spell is null || id == 0) {
            return;
        }

        s_marks.AddOrUpdate(spell, id);
        if (TemplateOf(id) is { } template) {
            spell.m_pipCost = template.m_spellRank;
            spell.m_accuracy = (byte) Math.Clamp((int) template.m_accuracy, 0, 100);
            spell.m_magicSchoolID = StringHash.Compute(template.m_sMagicSchoolName);
        }
    }

    /// <summary>What a marked spell really casts, or null for an ordinary spell.</summary>
    internal static SpellTemplate TemplateFor(Spell spell) => IdOf(spell) is var id and not 0 ? TemplateOf(id) : null;

    /// <summary>The item card line for a gear piece that gives this card <paramref name="count"/> times, or null.</summary>
    internal static ProvideSpellEffect Provide(int id, int count, uint slotHash) {
        var card = Get(id);
        if (card is null || CoreObjectFactory.GetCoreTemplate(card.ShownAs) is not SpellTemplate shown) {
            Logger.Warning("Custom card {0} is not in {1} or its Shown as card {2} is not a spell; left off the gear.",
                Logger.Args(id, FILE, card?.ShownAs ?? 0));

            return null;
        }

        var info = new ProvideSpellEffectInfo { m_effectName = "ProvideSpell", m_spellName = shown.m_name, m_numSpells = Math.Max(1, count) };
        if (GameEffectFactory.CreateEffectFromInfo(info, slotHash) is not ProvideSpellEffect provide) {
            return null;
        }

        s_provides.AddOrUpdate(provide, id);

        return provide;
    }

    /// <summary>The cards an item card line gives: marked Blood Moon cards, or the game's own.</summary>
    internal static Spell[] CardsFor(ProvideSpellEffect provide) {
        if (!s_provides.TryGetValue(provide, out var id)) {
            return SpellFactory.CreateSpellsFromEffect(provide) ?? [];
        }

        var card = Get((int) id);
        var cards = new List<Spell>();
        for (var i = 0; i < Math.Max(1, provide.m_numSpells) && card is not null; i++) {
            if (SpellFactory.GetSpell(card.ShownAs) is not { } spell) {
                break;
            }

            spell.m_itemCard = true;
            Mark(spell, card.Id);
            cards.Add(spell);
        }

        return [.. cards];
    }

    /// <summary>Takes the cards of an item card line back out of the wizard's item cards.</summary>
    internal static void RemoveCardsFor(Wizard wizard, ProvideSpellEffect provide) {
        var temporary = wizard.SpellbookBehavior?.TemporarySpells;
        if (temporary is null) {
            return;
        }

        if (s_provides.TryGetValue(provide, out var id)) {
            for (var i = 0; i < Math.Max(1, provide.m_numSpells); i++) {
                var index = temporary.FindIndex(s => IdOf(s) == (int) id);
                if (index < 0) {
                    break;
                }

                temporary.RemoveAt(index);
            }

            return;
        }

        for (var i = 0; i < Math.Max(1, provide.m_numSpells); i++) {
            wizard.RemoveTemporarySpell(StringHash.Compute(provide.m_spellName));
        }
    }

    // ------------------------------------------------------------------ the card the server casts

    /// <summary>
    /// The card's own spell: its Shown as card with the card's pips, accuracy, school and effects. Each effect is copied
    /// from a game card's effect of the same kind (the Shown as card's own first), so it is shaped as the game expects.
    /// </summary>
    internal static SpellTemplate TemplateOf(int id) {
        Reload();
        if (s_templates.TryGetValue(id, out var made)) {
            return made;
        }

        var card = Get(id);
        if (card is null || CoreObjectFactory.GetCoreTemplate(card.ShownAs) is not SpellTemplate shown) {
            return null;
        }

        var school = string.IsNullOrWhiteSpace(card.School) ? (string) shown.m_sMagicSchoolName : card.School.Trim();
        var effects = card.Effects.Count == 0 ? [.. shown.m_effects ?? []] : card.Effects.Select(l => Build(l, shown, school)).Where(e => e is not null).ToList();
        var template = shown with {
            m_effects = effects,
            m_sMagicSchoolName = school,
            m_accuracy = card.Accuracy > 0 ? Math.Clamp(card.Accuracy, 0, 100) : shown.m_accuracy,
            m_spellRank = card.Pips < 0 || shown.m_spellRank is null ? shown.m_spellRank
                : shown.m_spellRank with { m_spellRank = (byte) Math.Clamp(card.Pips, 0, 14) },
        };
        s_templates[id] = template;
        Logger.Information("Custom card {0} ({1}): shown as {2}, {3} pips, {4}% {5}, {6} effects.",
            Logger.Args(card.Id, card.Name, (string) shown.m_name, card.Pips, (int) template.m_accuracy, school, effects.Count));

        return template;
    }

    private static SpellEffect Build(Line line, SpellTemplate shown, string cardSchool) {
        if (!TryEnum<kSpellEffects>(line.Type, out var type)) {
            Logger.Warning("Custom cards: effect type {0} is not known; left out.", Logger.Args(line.Type));
            return null;
        }

        var sample = CombatEnchantments.Leaves(shown.m_effects).FirstOrDefault(e => e.m_effectType == type) ?? Sample(type);
        if (sample is null) {
            Logger.Warning("Custom cards: no game card has a {0} effect to copy; left out.", Logger.Args(line.Type));
            return null;
        }

        var target = TryEnum<kEffectTarget>(line.Target, out var t) ? t : sample.m_effectTarget;
        var school = string.IsNullOrWhiteSpace(line.School) ? cardSchool : line.School.Trim();

        return sample with {
            m_effectParam = line.Amount,
            m_sDamageType = school,
            m_effectTarget = target,
            m_numRounds = line.Rounds > 0 ? line.Rounds : sample.m_numRounds,
        };
    }

    // The first plain effect of a type found on any game card.
    private static SpellEffect Sample(kSpellEffects type) => s_samples.GetOrAdd(type, k =>
        SpellFactory.AllSpellTemplates()
            .SelectMany(t => t.Template.m_effects ?? [])
            .FirstOrDefault(e => e is not null && e.GetType() == typeof(SpellEffect) && e.m_effectType == k));

    private static bool TryEnum<T>(string text, out T value) where T : struct, Enum {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) {
            return false;
        }

        var name = text.Trim().Replace(" ", "");
        return Enum.TryParse(name, true, out value) || Enum.TryParse("k" + name, true, out value);
    }

    // ------------------------------------------------------------------ the file

    private static void Reload() {
        lock (s_lock) {
            var now = DateTime.UtcNow;
            if (now - s_checked < TimeSpan.FromSeconds(2)) {
                return;
            }

            s_checked = now;
            try {
                var time = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
                if (time == s_fileTime) {
                    return;
                }

                s_fileTime = time;
                s_cards = time == DateTime.MinValue ? [] : Read(File.ReadAllText(FilePath));
                s_templates.Clear();
                Logger.Information("Custom cards: {0} cards read from {1}.", Logger.Args(s_cards.Count, FILE));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
                s_fileTime = DateTime.MinValue;
            }
        }
    }

    internal static Dictionary<int, Card> Read(string json) {
        var cards = new Dictionary<int, Card>();
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        foreach (var node in (root?["Cards"] as JsonArray ?? []).OfType<JsonObject>()) {
            try {
                var id = (int?) node["Id"] ?? 0;
                var shownAs = (uint?) (long?) node["ShownAs"] ?? 0;
                if (id <= 0 || shownAs == 0) {
                    continue;
                }

                var lines = (node["Effects"] as JsonArray ?? []).OfType<JsonObject>()
                    .Select(e => new Line((string) e["Type"] ?? "", (int?) e["Amount"] ?? 0, (string) e["School"] ?? "",
                        (string) e["Target"] ?? "", (int?) e["Rounds"] ?? 0))
                    .ToList();
                cards[id] = new Card(id, (string) node["Name"] ?? $"Custom card {id}", shownAs, (string) node["School"] ?? "",
                    (int?) node["Pips"] ?? -1, (int?) node["Accuracy"] ?? 0, lines);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException) {
                Logger.Warning("Custom cards: a card in {0} could not be read: {1}", Logger.Args(FILE, ex.Message));
            }
        }

        return cards;
    }

}
