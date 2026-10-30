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
    private void FusionsCommand() {
        var wizard = Context.Character;
        if (!SpellFusions.Enabled) {
            InformSenderClient("Spell Fusion is switched off ([SpellFusion] Enabled).");

            return;
        }

        var known = SpellFusions.KnownRecipes(wizard);
        if (known.Count == 0) {
            InformSenderClient("You know no Fusion Formulas yet. Trainers teach them (the Selenopolis weaving trainers and others); with .fusion learn all a tester learns every one.");

            return;
        }

        var text = new StringBuilder($"Your Fusion Formulas ({known.Count}):");
        foreach (var recipe in known.OrderBy(r => r.Name)) {
            text.Append($"\n{FusedName(recipe)}: {Ingredient(recipe.Primary)} + {Ingredient(recipe.Secondary)}");
        }

        text.Append("\nIn battle, drag one card onto the other to fuse them, then cast the fused card.");
        InformSenderClient(text.ToString(), true);
    }

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
