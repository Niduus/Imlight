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
using System.Linq;
using System.Text;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Auth;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// Spell Fusion by chat (see <see cref="SpellFusions"/>): `.fusions` lists the Fusion Formulas you know and the two
/// cards each one needs; `.fusion learn Desert Banshee` (or `all`) teaches a formula (QA); `.fusion forget all` takes
/// them away again (QA).
/// </summary>
internal class CommandFusionProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("fusions")]
    [Alias("formulas")]
    private void FusionsCommand([Remainder] string name = "") {
        var wizard = Context.Character;
        if (!SpellFusions.Enabled) {
            InformSenderClient("Spell Fusion is switched off ([SpellFusion] Enabled).");

            return;
        }

        name = (name ?? "").Trim();
        if (name.Length > 0) {
            // .fusions Desert Banshee: one formula, every card that counts for it.
            var picked = SpellFusions.Recipes.Where(r => Matches(r, name)).ToList();
            if (picked.Count == 0) {
                InformSenderClient($"No Fusion Formula is named {name}. Type .fusions for yours.");

                return;
            }

            var detail = new StringBuilder();
            foreach (var recipe in picked) {
                var mine = SpellFusions.KnownRecipes(wizard).Contains(recipe);
                detail.Append($"{FusedName(recipe)}{SchoolOf(recipe)} ({(mine ? "you know this formula" : "you don't know this formula yet")})\n");
                detail.Append($"Material 1: {Material(recipe.Primary, wizard)}\n");
                detail.Append($"   any of: {string.Join(", ", SpellFusions.CardsOf(recipe.Primary).Select(Shown).Distinct())}\n");
                detail.Append($"Material 2: {Material(recipe.Secondary, wizard)}\n");
                detail.Append($"   any of: {string.Join(", ", SpellFusions.CardsOf(recipe.Secondary).Select(Shown).Distinct())}\n");
            }

            detail.Append("Put both materials in your deck. In battle, when both are in your hand, drag one onto the other, then cast the fused card.");
            InformSenderClient(detail.ToString(), true);

            return;
        }

        var known = SpellFusions.KnownRecipes(wizard);
        if (known.Count == 0) {
            InformSenderClient("You know no Fusion Formulas yet. Trainers teach them (the Selenopolis weaving trainers and others); with .fusion learn all a tester learns every one.");

            return;
        }

        var text = new StringBuilder($"Your Fusion Formulas ({known.Count}). Fused spell = material 1 + material 2:");
        foreach (var recipe in known.OrderBy(r => FusedName(r))) {
            text.Append($"\n{FusedName(recipe)}{SchoolOf(recipe)} = {Material(recipe.Primary, wizard)} + {Material(recipe.Secondary, wizard)}");
        }

        text.Append("\n[deck] = in your deck, [known] = you know it but it's not in your deck, [missing] = you don't know it yet.");
        text.Append("\nPut both materials in your deck. In battle, drag one onto the other, then cast the fused card. .fusions <name> shows every card that counts.");
        InformSenderClient(text.ToString(), true);
    }

    // "Banshee [deck]": the material's card name and whether the wizard knows it and has it in the deck.
    private static string Material(string adjective, Wizard wizard) {
        var cards = SpellFusions.CardsOf(adjective);
        var main = SpellFusions.MainCardOf(adjective);
        var name = main != 0 ? Shown(main) : Ingredient(adjective);
        var deck = wizard.SpellbookBehavior?.SpellList?.Select(d => d.m_templateID).ToHashSet() ?? [];
        var state = cards.Any(deck.Contains) ? "deck"
            : cards.Any(wizard.SpellbookBehavior.HasSpell) ? "known"
            : "missing";

        return $"{name} [{state}]";
    }

    private static string SchoolOf(SpellFusions.Recipe recipe)
        => recipe.Outputs.Count > 0 && CoreObjectFactory.GetCoreTemplate(recipe.Outputs[0]) is SpellTemplate t
           && !string.IsNullOrEmpty((string) t.m_sMagicSchoolName)
            ? $" ({(string) t.m_sMagicSchoolName})"
            : "";

    [Command("fusion")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void FusionCommand(string action = "", [Remainder] string text = "") {
        action = (action ?? "").Trim().ToLowerInvariant();
        text = (text ?? "").Trim();
        if (action is not ("learn" or "forget") || text.Length == 0) {
            InformSenderClient("Type .fusion learn <fused spell or formula> (or all), or .fusion forget all. .fusions lists yours.");

            return;
        }

        var wizard = Context.Character;
        var picked = string.Equals(text, "all", StringComparison.OrdinalIgnoreCase)
            ? SpellFusions.Recipes.ToList()
            : SpellFusions.Recipes.Where(r => Matches(r, text)).ToList();
        if (picked.Count == 0) {
            InformSenderClient($"No Fusion Formula is named {text}. .fusion learn all teaches every one.");

            return;
        }

        var count = 0;
        foreach (var recipe in picked) {
            if (action == "learn") {
                // One fused spell per formula is what makes it the wizard's; the plain one (not an upgraded tier).
                if (recipe.Outputs.Any(wizard.SpellbookBehavior.HasSpell)) {
                    continue;
                }

                var id = recipe.Outputs.OrderBy(o => Name(o).Contains(" - T0", StringComparison.Ordinal) ? 1 : 0).First();
                if (SpellFactory.GetSpell(id) is { } spell && wizard.LearnSpell(spell)) {
                    Context.SessionActor.Tell(new WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK { SpellID = (int) id }, Akka.Actor.ActorRefs.NoSender);
                    count++;
                }
            }
            else {
                foreach (var id in recipe.Outputs.Where(wizard.SpellbookBehavior.HasSpell).ToList()) {
                    if (wizard.UnlearnSpell(id)) {
                        Context.SessionActor.Tell(new WIZARD_12_PROTOCOL.MSG_REMOVESPELLFROMBOOK { SpellID = (int) id }, Akka.Actor.ActorRefs.NoSender);
                        count++;
                    }
                }
            }
        }

        InformSenderClient(action == "learn"
            ? $"Learned {count} Fusion Formula{(count == 1 ? "" : "s")}. .fusions lists them; they work in your next battle."
            : $"Forgot {count} fused spell{(count == 1 ? "" : "s")}.");
    }

    private static bool Matches(SpellFusions.Recipe recipe, string text)
        => string.Equals(recipe.Name, text, StringComparison.OrdinalIgnoreCase)
           || recipe.Outputs.Any(o => string.Equals(Name(o), text, StringComparison.OrdinalIgnoreCase)
                                      || string.Equals(Shown(o), text, StringComparison.OrdinalIgnoreCase));

    private static string Name(uint id) => (string) (CoreObjectFactory.GetCoreTemplate(id) as SpellTemplate)?.m_name ?? "";

    private static string Shown(uint id) {
        if (CoreObjectFactory.GetCoreTemplate(id) is not SpellTemplate template) {
            return "";
        }

        string display = template.m_displayName;
        var shown = string.IsNullOrEmpty(display) ? null : Locale.GetEnglishName(display);

        return string.IsNullOrEmpty(shown) ? (string) template.m_name : shown;
    }

    private static string FusedName(SpellFusions.Recipe recipe)
        => recipe.Outputs.Count == 0 ? recipe.Name : Shown(recipe.Outputs[0]);

    // "Fusion_Banshee" -> "Banshee"; the adjective names the card family.
    private static string Ingredient(string adjective)
        => adjective.StartsWith("Fusion_", StringComparison.OrdinalIgnoreCase) ? adjective[7..] : adjective;
}
