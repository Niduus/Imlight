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

using System.Collections.Generic;
using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Effects;
using Imlight.CoreLib.Shared.Items;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// Jewel and pin commands for the gear and pet you are wearing: `.jewel list`, `.jewel add`, `.jewel clear`.
/// </summary>
internal class CommandJewelProtocol : CommandProtocol {

    private const int SEARCH_LIMIT = 200;

    internal override string Group { get; set; } = "jewel";

    [Command("list")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void ListCommand() {
        var lines = new List<string>();
        foreach (var item in Context.Character.EquipmentBehavior.EquippedItems) {
            var sockets = JewelSockets.GetSockets(ItemHelper.GetItemTemplate(item));
            if (sockets.Count == 0) {
                continue;
            }

            var pairs = JewelSockets.GetPairs(item);
            var slots = sockets.Select((socket, index) => {
                var pair = pairs.FirstOrDefault(p => p?.m_socketNumber == index);
                var jewel = pair is null ? null : CoreObjectFactory.GetCoreTemplate(pair.m_jewelTemplateID) as WizItemTemplate;

                return $"{JewelSockets.ShapeName(socket.m_socketType)}: {(jewel is null ? "empty" : Name(jewel))}";
            });
            lines.Add($"{Name(ItemHelper.GetItemTemplate(item))} ({string.Join("; ", slots)})");
        }

        InformSenderClient(lines.Count == 0 ? "Nothing you are wearing has sockets." : string.Join(" | ", lines));
    }

    [Command("add")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void AddCommand([Remainder] string jewelName) {
        var wizard = Context.Character;
        var jewels = IdCatalog.Search(IdCatalogKind.Item, jewelName, SEARCH_LIMIT)
            .Select(e => CoreObjectFactory.GetCoreTemplate(e.Id) as WizItemTemplate)
            .Where(JewelSockets.IsJewel)
            .ToList();
        if (jewels.Count == 0) {
            InformSenderClient(IdCatalog.IsReady
                ? "No jewel or pin by that name or ID. Look one up with .lookup."
                : "The ID lookup is still loading. Try again in a minute, or use the jewel's ID.");

            return;
        }

        // The first jewel that fits an empty socket on something you wear.
        string lastProblem = null;
        foreach (var jewel in jewels) {
            foreach (var item in wizard.EquipmentBehavior.EquippedItems) {
                var sockets = JewelSockets.GetSockets(ItemHelper.GetItemTemplate(item));
                for (var socket = 0; socket < sockets.Count; socket++) {
                    var problem = JewelSockets.CheckSocket(wizard, item, jewel, socket);
                    if (problem is not null) {
                        if (sockets[socket]?.m_socketType == JewelSockets.GetShape(jewel)) {
                            lastProblem = problem;
                        }

                        continue;
                    }

                    JewelSockets.Socket(item, socket, (uint) jewel.m_templateID);
                    WizardItemCollection.SaveItemBehaviors(item);
                    EquipmentExtras.Refresh(wizard);
                    InformSenderClient($"Put {Name(jewel)} in your {Name(ItemHelper.GetItemTemplate(item))}. Relog to see it on the item.");

                    return;
                }
            }
        }

        InformSenderClient(lastProblem ?? "Nothing you are wearing has an empty socket of that shape.");
    }

    [Command("clear")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void ClearCommand() {
        var wizard = Context.Character;
        var count = 0;
        foreach (var item in wizard.EquipmentBehavior.EquippedItems) {
            var pairs = JewelSockets.GetPairs(item);
            if (pairs.Count == 0) {
                continue;
            }

            count += pairs.Count;
            pairs.Clear();
            WizardItemCollection.SaveItemBehaviors(item);
        }

        EquipmentExtras.Refresh(wizard);
        InformSenderClient(count == 0 ? "Your worn items have no jewels in them." : $"Took {count} jewel{(count == 1 ? "" : "s")} out of your worn items. Relog to see it.");
    }

    private static string Name(WizItemTemplate template) {
        if (template is null) {
            return "?";
        }

        var shown = Locale.GetEnglishName(template.m_displayName);

        return string.IsNullOrEmpty(shown) ? template.m_objectName : shown;
    }

}
