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
using System.Text;
using Imlight.CoreLib.Auth;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// Spell upgrades by chat, the same as the game's upgrade window (see <see cref="TieredSpells"/>): `.upgrades` lists
/// the upgradable spells you know, their next tiers and costs; `.upgrade Fire Cat B` upgrades Fire Cat along path B
/// (path A when none is given). `.spellements 200` sets your Spellements to 200 for every upgradable spell you know (QA).
/// </summary>
internal class CommandUpgradeProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    // The most Spellements .spellements sets per spell (the upgrade window shows "999+" above this).
    private const int MAX_SHARDS = 999;

    [Command("upgrades")]
    [Alias("tiers")]
    private void UpgradesCommand() {
        var wizard = Context.Character;
        var known = TieredSpells.KnownTieredSpells(wizard);
        if (known.Count == 0) {
            InformSenderClient("You know no upgradable spells yet. Fire Cat, Thunder Snake, Frost Beetle and the other first spells of each school can be upgraded, and more from level 50 on.");

            return;
        }

        var text = new StringBuilder("Your upgradable spells:");
        foreach (var (_, template, group) in known.OrderBy(k => (string) k.Template.m_name)) {
            var shard = TieredSpells.ShardOf(group);
            var have = TieredSpells.ShardCount(wizard, shard);
            var next = TieredSpells.NextTiers(template);
            text.Append($"\n{Shown(template)} ({(string) template.m_name}), {have} Spellements: ");
            text.Append(next.Count == 0
                ? "top tier"
                : string.Join(", ", next.Select(n => $"path {PathOf(n.Name)} {n.Cost}")));
        }

        text.Append("\nUpgrade with .upgrade <spell> <path>, such as .upgrade Fire Cat B.");
        InformSenderClient(text.ToString(), true);
    }

    [Command("upgrade")]
    private void UpgradeCommand([Remainder] string text = "") {
        text = (text ?? "").Trim();
        if (text.Length == 0) {
            InformSenderClient("Type .upgrade <spell> <path>, such as .upgrade Fire Cat B. .upgrades lists your spells.");

            return;
        }

        // .upgrade reset Mortality: back to the plain spell, for upgrades that came from somewhere else.
        if (text.StartsWith("reset ", StringComparison.OrdinalIgnoreCase)) {
            var name = text[6..].Trim();
            var spell = TieredSpells.KnownTieredSpells(Context.Character).FirstOrDefault(k => Matches(k.Template, name));
            if (spell.Template is null) {
                InformSenderClient($"You know no upgradable spell called {name}. .upgrades lists yours.");

                return;
            }

            var removed = TieredSpells.ResetGroup(Context.SessionActor, Context.Character, spell.Group);
            InformSenderClient(removed == 0
                ? $"{name} has no upgrades to take away."
                : $"Took away {removed} upgraded tier(s) of {name}. You keep the plain spell; Spellements are not given back.");

            return;
        }

        // The last word is the path when it is one or two letters (A, B, C, D, AB, BA).
        var path = "";
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && words[^1].Length <= 2 && words[^1].All(char.IsLetter)) {
            path = words[^1].ToUpperInvariant();
            text = string.Join(' ', words[..^1]);
        }

        var wizard = Context.Character;
        var known = TieredSpells.KnownTieredSpells(wizard);
        var match = known.FirstOrDefault(k => Matches(k.Template, text));
        if (match.Template is null) {
            InformSenderClient($"You know no upgradable spell called {text}. .upgrades lists yours.");

            return;
        }

        var next = TieredSpells.NextTiers(match.Template);
        if (next.Count == 0) {
            InformSenderClient($"{Shown(match.Template)} is at its top tier.");

            return;
        }

        var choice = path.Length == 0
            ? next.OrderBy(n => PathOf(n.Name)).First()
            : next.FirstOrDefault(n => PathOf(n.Name) == path);
        if (choice.Template is null) {
            InformSenderClient($"{Shown(match.Template)} has no path {path}. It has: {string.Join(", ", next.Select(n => PathOf(n.Name)))}.");

            return;
        }

        TieredSpells.TryUpgrade(Context.SessionActor, wizard, match.TemplateId, choice.TemplateId, out var message);
        InformSenderClient(message);
    }

    [Command("spellements")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void SpellementsCommand(string amount = "200") {
        if (!int.TryParse(amount, out var each) || each < 1) {
            InformSenderClient("Type .spellements 200 for 200 of each.");

            return;
        }

        each = Math.Min(each, MAX_SHARDS);
        var given = TieredSpells.GiveShardsForKnownSpells(Context.SessionActor, Context.Character, each);
        InformSenderClient(given == 0
            ? $"Your Spellements are now {each} for each upgradable spell you know (or you know none)."
            : $"Your Spellements are now {each} for each upgradable spell you know ({given} given).");
    }

    // "Fire Cat - T03 - A" -> "A"; "Fire Cat - T02" -> "A" (the game's only path there).
    private static string PathOf(string spellName) {
        var parts = spellName.Split(" - ", StringSplitOptions.TrimEntries);
        var last = parts[^1];

        return parts.Length >= 3 && last.Length <= 2 ? last.ToUpperInvariant() : "A";
    }

    private static string Shown(Imcodec.ObjectProperty.TypeCache.SpellTemplate template) {
        string display = template.m_displayName;
        var shown = string.IsNullOrEmpty(display) ? null : Locale.GetEnglishName(display);

        return string.IsNullOrEmpty(shown) ? (string) template.m_name : shown;
    }

    private static bool Matches(Imcodec.ObjectProperty.TypeCache.SpellTemplate template, string text)
        => string.Equals(Shown(template), text, StringComparison.OrdinalIgnoreCase)
            || string.Equals((string) template.m_name, text, StringComparison.OrdinalIgnoreCase);

}
