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
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Models.Player;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.MonsterMagic;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Game.Trading;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.monstercard summon Lost Soul 3` (or `expel`, a monster's name or template id, how many) gives Monstrology treasure
/// cards: the game's own Summon Monster and Expel Monster cards (Spells/MonsterMagicTC). `.monstercard 1735428589 2`
/// gives a card by its template id, which Ahziael's Monstrology tab sends. Only cards the game has, only for monsters
/// with a Monstrology page, and never past the treasure card book's limit (Niduus, 2026-11-12).
/// </summary>
internal class CommandMonsterCardProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    private const int MAX_AT_ONCE = 99;

    [Command("monstercard")]
    [Alias("mcard")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void MonsterCardCommand([Remainder] string text = "") {
        var wizard = Context.Character;
        if (wizard is null) {
            return;
        }

        var words = (text ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var count = 1;
        if (words.Count > 1 && int.TryParse(words[^1], out var asked)) {
            count = asked;
            words.RemoveAt(words.Count - 1);
        }

        if (words.Count == 0) {
            InformSenderClient("Use .monstercard summon <monster> <how many>, .monstercard expel <monster> <how many>, or .monstercard <card id> <how many>.");

            return;
        }

        if (!TryFindCard(words, out var templateId, out var card, out var why)) {
            InformSenderClient(why);

            return;
        }

        var have = wizard.SpellbookBehavior.TreasureCardTemplateIds?.Count ?? 0;
        var room = TradeLimits.TreasureBookCapacity - have;
        if (room <= 0) {
            InformSenderClient($"Your treasure card book is full ({TradeLimits.TreasureBookCapacity} cards).");

            return;
        }

        var giving = Math.Clamp(count, 1, Math.Min(MAX_AT_ONCE, room));
        LootGranter.GrantTreasureCards(Context.SessionActor, wizard, card, templateId, giving);
        var name = CardTitle(card);
        Logger.Information("Monstrology: {0} gave themselves {1} x {2} ({3}).", Logger.Args(wizard.CharId, giving, name, templateId));
        InformSenderClient(giving < count
            ? $"Added {giving} {name} (your treasure card book holds {TradeLimits.TreasureBookCapacity} cards, {MAX_AT_ONCE} at a time)."
            : $"Added {giving} {name} to your treasure cards.");
    }

    private static bool TryFindCard(System.Collections.Generic.List<string> words, out uint templateId, out SpellTemplate card, out string why) {
        templateId = 0;
        card = null;
        why = null;

        // A card's template id, as Ahziael sends it.
        if (words.Count == 1 && uint.TryParse(words[0], out var id) && CoreObjectFactory.GetCoreTemplate(id) is SpellTemplate byId) {
            if (!IsMonsterCard(byId)) {
                why = $"{id} is not a Summon Monster or Expel Monster card.";

                return false;
            }

            (templateId, card) = (id, byId);

            return true;
        }

        var kind = words[0].ToLowerInvariant() switch {
            "summon" or "summons" => Monstrology.SUMMON_CARD,
            "expel" or "expels" or "kill" => Monstrology.EXPEL_CARD,
            _ => 0,
        };
        if (kind == 0 || words.Count < 2) {
            why = "Say summon or expel first, then the monster: .monstercard summon Lost Soul 3.";

            return false;
        }

        var monsterName = string.Join(' ', words.Skip(1));
        var monster = uint.TryParse(monsterName, out var monsterId) ? Monstrology.MonsterOf(monsterId) : null;
        monster ??= IdCatalog.Search(IdCatalogKind.Creature, monsterName, 40)
            .Select(e => Monstrology.MonsterOf((uint) e.Id))
            .FirstOrDefault(m => m is not null && Monstrology.CardName(m, kind) is not null);
        if (monster is null) {
            why = $"No monster with a Monstrology page matches \"{monsterName}\".";

            return false;
        }

        var spellName = Monstrology.CardName(monster, kind);
        var spell = spellName is null ? null : SpellFactory.GetSpell(spellName);
        if (spell is null || CoreObjectFactory.GetCoreTemplate(spell.m_templateID) is not SpellTemplate template) {
            why = $"The game has no {(kind == Monstrology.SUMMON_CARD ? "Summon" : "Expel")} card for {monster.Name}.";

            return false;
        }

        (templateId, card) = (spell.m_templateID, template);

        return true;
    }

    // The game's Monstrology cards: Summon<id> (adjective MonsterMagicSummon) and Kill<id>, treasure cards only.
    private static bool IsMonsterCard(SpellTemplate spell) {
        string name = spell.m_name;
        if (!spell.m_Treasure || string.IsNullOrEmpty(name)) {
            return false;
        }

        foreach (var prefix in new[] { "Summon", "Kill" }) {
            if (name.StartsWith(prefix, StringComparison.Ordinal) && uint.TryParse(name[prefix.Length..], out var id)
                && Monstrology.MonsterOf(id) is not null) {
                return true;
            }
        }

        return false;
    }

    private static string CardTitle(SpellTemplate card) {
        string name = card.m_name;
        foreach (var (prefix, word) in new[] { ("Summon", "Summon"), ("Kill", "Expel") }) {
            if (name?.StartsWith(prefix, StringComparison.Ordinal) == true && uint.TryParse(name[prefix.Length..], out var id)) {
                return $"{word} {Monstrology.NameOf(id)}";
            }
        }

        return name;
    }

}
