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
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Bank;
using Imlight.CoreLib.Game.DropTables;
using Imlight.CoreLib.Game.Housing;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.castlemagic` gives one of every Castle Magic object and every Castle Magic spell the wizard does not have yet;
/// `.castlemagic objects` and `.castlemagic spells` give only one kind (Game/Housing/HouseCastleMagic).
/// </summary>
internal class CommandCastleMagicProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("castlemagic")]
    [Alias("cm")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void CastleMagicCommand([Remainder] string what = "") {
        what = (what ?? "").Trim().ToLowerInvariant();
        var giveObjects = what is "" or "all" or "objects" or "object" or "items";
        var giveSpells = what is "" or "all" or "spells" or "spell" or "cards";
        if (!giveObjects && !giveSpells) {
            InformSenderClient("Type .castlemagic for the whole kit, or .castlemagic objects or .castlemagic spells.");

            return;
        }

        var full = false;
        var objects = giveObjects ? GiveObjects(out full) : 0;
        var spells = giveSpells ? GiveSpells() : 0;
        var note = giveObjects && full ? " Your backpack and bank are full, so some objects were left out." : "";
        InformSenderClient($"Castle Magic: {objects} object(s) and {spells} spell(s) added. Place the objects in your house, "
            + "then open your spellbook's Castle Magic tab to cast spells onto them. Castle Magic spells are not used up." + note);
    }

    private int GiveObjects(out bool full) {
        full = false;
        var wizard = Context.Character;
        var given = 0;
        foreach (var templateId in HouseCastleMagic.ObjectTemplates) {
            if (CoreObjectFactory.GetCoreTemplate(templateId) is not WizItemTemplate template
                || HousingItems.Classify(template) != HousingItemKind.Furniture
                || HousingItems.CountInBackpack(wizard, templateId) > 0) {
                continue;
            }

            if (!wizard.TryCreateItem(templateId, out var item)) {
                continue;
            }

            var placement = ItemPlacement.Place(wizard, item);
            if (placement == Placement.Lost) {
                full = true;

                break;
            }

            ItemPlacement.Announce(Context.SessionActor, wizard, item, placement);
            given++;
        }

        return given;
    }

    private int GiveSpells() {
        var wizard = Context.Character;
        var given = 0;
        foreach (var templateId in HouseCastleMagic.SpellTemplates) {
            if (wizard.SpellbookBehavior.TreasureCardCount(templateId) > 0
                || CoreObjectFactory.GetCoreTemplate(templateId) is not CastleMagicSpellTemplate spell) {
                continue;
            }

            try {
                given += LootGranter.GrantTreasureCards(Context.SessionActor, wizard, spell, templateId, 1);
            } catch (Exception exception) {
                Logger.Warning("Castle Magic: spell {SpellId} could not be given: {Reason}", Logger.Args(templateId, exception.Message));
            }
        }

        return given;
    }

}
