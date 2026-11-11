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

using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Game.Spells;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using System.Collections.Generic;
using System.Linq;

namespace Imlight.CoreLib.Game.Commands.Protocols;

internal class CommandSpellbookProtocol : CommandProtocol {

    internal override string Group { get; set; } = "sb";

    [Command("learn")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void LearnSpellCommand(string spellTemplateId) {
        // Parse the spell template ID as a uint.
        if (!uint.TryParse(spellTemplateId, out var spellTemplateIdUint)) {
            InformSenderClient("Invalid spell template ID.");

            return;
        }

        var template = CoreObjectFactory.GetCoreTemplate(spellTemplateIdUint);
        if (template == null) {
            InformSenderClient("Invalid spell template ID.");

            return;
        }
        if (template is not SpellTemplate spellTemplate) {
            InformSenderClient("Invalid spell template ID.");

            return;
        }

        var spell = SpellFactory.GetSpell(spellTemplateIdUint);
        if (spell == null) {
            InformSenderClient("Invalid spell template ID.");

            return;
        }

        var learned = Context.Character.LearnSpell(spell);

        // A shadow-enhanced spell (Wings of Night) and the other fused spells are Fusion Formulas: the game lists them
        // on the deck's Fusion page, not on a school page, and they only fuse with both of their cards known. So the
        // cards come with it, as .fusion learn does (reported 2026-11-11: "shadow enhanced spells are learnable but they
        // won't appear in your spellbook").
        var fusion = SpellFusions.RecipeOf(spellTemplateIdUint) is not null;
        var cards = fusion ? SpellFusions.TeachIngredients(Context.Character, spellTemplateIdUint) : [];

        Context.SessionActor.Tell(new WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK { SpellID = (int) spellTemplateIdUint });
        foreach (var cardId in cards) {
            Context.SessionActor.Tell(new WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK { SpellID = (int) cardId });
        }

        // An upgradable spell's group is shown by the game only once it is told the group is known.
        if (TieredSpells.Enabled && TieredSpells.GroupOfTemplate(spellTemplateIdUint) >= 0
            && TieredSpells.VisibilityMessage(Context.Character) is { } visibility) {
            Context.SessionActor.Tell(visibility);
        }

        var shownName = ShownName(spellTemplate);
        if (fusion) {
            var cardNames = cards.Select(id => CoreObjectFactory.GetCoreTemplate(id) as SpellTemplate).Where(t => t is not null).Select(ShownName).ToList();
            InformSenderClient((learned ? $"You have learned the Fusion Formula {shownName}." : $"You already know the Fusion Formula {shownName}.")
                               + (cardNames.Count > 0 ? $" You also learned {string.Join(" and ", cardNames)}, the cards it fuses." : "")
                               + " Open your deck and click the Fusion tab to see it; it is not on a school page."
                               + " In battle, drag one of its cards onto the other to fuse them.");
        }
        else if (!learned) {
            InformSenderClient("Failed to learn spell. You may already know this spell.");
        }
        else {
            InformSenderClient($"You have learned the spell {shownName}.");
        }
    }

    // The name the game shows ("Wings of Night"), or the file name when it has none.
    private static string ShownName(SpellTemplate template) {
        string display = template.m_displayName;
        var shown = string.IsNullOrEmpty(display) ? null : Locale.GetEnglishName(display);

        return string.IsNullOrEmpty(shown) ? (string) template.m_name : shown;
    }

    [Command("unlearn")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void UnlearnSpellCommand(string spellTemplateId) {
        // Parse the spell template ID as a uint.
        if (!uint.TryParse(spellTemplateId, out var spellTemplateIdUint)) {
            InformSenderClient("Invalid spell template ID.");

            return;
        }

        var template = CoreObjectFactory.GetCoreTemplate(spellTemplateIdUint);
        if (template == null) {
            InformSenderClient("Invalid spell template ID.");

            return;
        }
        if (template is not SpellTemplate spellTemplate) {
            InformSenderClient("Invalid spell template ID.");

            return;
        }
        var spellName = spellTemplate.m_name;

        var spell = SpellFactory.GetSpell(spellTemplateIdUint);
        if (spell == null) {
            InformSenderClient("Invalid spell template ID.");

            return;
        }

        if (!Context.Character.UnlearnSpell(spellTemplateIdUint)) {
            InformSenderClient("Failed to unlearn spell. You may not know this spell.");
        }
        else {
            InformSenderClient($"You have unlearned the spell {spellName}.");
        }

        var clientMsg = new WIZARD_12_PROTOCOL.MSG_REMOVESPELLFROMBOOK {
            SpellID = (int) spellTemplateIdUint
        };
        Context.SessionActor.Tell(clientMsg);
    }
    [Command("unlearnall")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void UnlearnallSpellCommand() {

        if (Context.Character.SpellbookBehavior.LearnedSpellTemplateIds != null) {
            List<uint> spellList = [.. Context.Character.SpellbookBehavior.LearnedSpellTemplateIds];

            foreach (var spell in spellList) {
                var clientMsg = new WIZARD_12_PROTOCOL.MSG_REMOVESPELLFROMBOOK {
                    SpellID = (int) spell
                };
                Context.SessionActor.Tell(clientMsg);

                Context.Character.SpellbookBehavior.RemoveSpellFromBook(spell);

                // Persistent save.
                WizardCollection.UnlearnSpell(Context.Character, spell);
                InformSenderClient($"You have unlearned the spell {spell}.");
            }
        }
        else {
            InformSenderClient($"No Spells To Unlearn");
        }
    }

    [Command("add")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void AddSpellCommand(string spellTemplateId) {
        // Parse the spell template ID as a uint.
        if (!uint.TryParse(spellTemplateId, out var spellTemplateIdUint)) {
            InformSenderClient("Invalid spell template ID.");

            return;
        }

        var spell = SpellFactory.GetSpell(spellTemplateIdUint);
        if (spell == null) {
            InformSenderClient("Invalid spell template ID.");

            return;
        }

        Context.Character.AddTemporarySpell(spell);
        InformSenderClient("You have added the spell.");
    }

    [Command("remove")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void RemoveSpellCommand(string spellTemplateId) {
        // Parse the spell template ID as a uint.
        if (!uint.TryParse(spellTemplateId, out var spellTemplateIdUint)) {
            InformSenderClient("Invalid spell template ID.");

            return;
        }

        var spell = SpellFactory.GetSpell(spellTemplateIdUint);
        if (spell == null) {
            InformSenderClient("Invalid spell template ID.");

            return;
        }

        Context.Character.RemoveTemporarySpell(spellTemplateIdUint);
        InformSenderClient("You have removed the spell.");
    }

    [Command("allcantrips")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void LearnallCantripsCommand() {
        foreach (var (spell, _) in Imlight.CoreLib.Game.Cantrips.CantripFactory.PlayerCantrips()) {
            if (Context.Character.SpellbookBehavior.HasSpell(spell)) {
                continue;
            }

            var learn_spell = SpellFactory.GetSpell(spell);
            if (learn_spell is null || !Context.Character.LearnSpell(learn_spell)) {
                continue;
            }
            var clientMsg = new WIZARD_12_PROTOCOL.MSG_ADDSPELLTOBOOK { 
                SpellID = (int) spell
            };
            Context.SessionActor.Tell(clientMsg);
        }

        InformSenderClient($"You have learned all cantrips.");
    }

}
