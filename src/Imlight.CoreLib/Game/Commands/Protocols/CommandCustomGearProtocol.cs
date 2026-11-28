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

using System.Linq;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Models.Player;
using System.Text;
using Imlight.CoreLib.Game.Bank;
using Imlight.CoreLib.Game.Equipment;
using Imlight.CoreLib.Game.Spells;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.customgear` lists the gear made in Blood Moon, `.customgear &lt;number or name&gt;` gives you one;
/// `.customcards` lists the cards made in Blood Moon (they come with the gear that holds them). See
/// <see cref="CustomGear"/> and <see cref="CustomCards"/>.
/// </summary>
internal class CommandCustomGearProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    [Command("customgear")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void CustomGearCommand([Remainder] string what = "") {
        if (string.IsNullOrWhiteSpace(what)) {
            var all = CustomGear.All;
            if (all.Count == 0) {
                InformSenderClient("No Blood Moon gear yet. Make some in Blood Moon's Make window, Gear page.");
                return;
            }

            var list = new StringBuilder().AppendLine("<center>Blood Moon gear</center>\n");
            foreach (var piece in all) {
                list.AppendLine($"{piece.Id}  {piece.Name} ({CustomGear.SlotWord(piece.Slot)}, {piece.Stats.Count} stats, {piece.Cards.Count + piece.Spells.Count} cards)");
            }

            list.AppendLine("\nGet one with .customgear <number>.");
            InformSenderClient(list.ToString(), true);

            return;
        }

        var found = CustomGear.Find(what);
        if (found is null) {
            InformSenderClient($"No Blood Moon gear called \"{what.Trim()}\". Type .customgear to see them all.");
            return;
        }

        var item = CustomGear.Make(Context.Character, found, out var problem);
        if (item is null) {
            InformSenderClient(problem);
            return;
        }

        var placement = ItemPlacement.Place(Context.Character, item, overLimit: false);
        if (placement == Placement.Lost) {
            InformSenderClient($"Could not give {found.Name}: the backpack and the bank are full.");
            return;
        }

        ItemPlacement.Announce(Context.SessionActor, Context.Character, item, placement);
        InformSenderClient($"Gave you {found.Name}. Its own stats count while you wear it (look on your stats page); "
                           + "the game's hover box still shows the QA item it is made from.");
    }

    [Command("customcards")]
    [Alias("customcard")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void CustomCardsCommand() {
        var all = CustomCards.All;
        if (all.Count == 0) {
            InformSenderClient("No Blood Moon cards yet. Make some in Blood Moon's Make window, Cards page.");
            return;
        }

        var list = new StringBuilder().AppendLine("<center>Blood Moon cards</center>\n");
        foreach (var card in all) {
            var holders = CustomGear.All.Where(g => g.Cards.Contains(card.Id)).Select(g => g.Name).ToList();
            list.AppendLine($"{card.Id}  {card.Name} ({(card.Pips < 0 ? "?" : card.Pips.ToString())} pips) "
                            + (holders.Count == 0 ? "- on no gear yet" : "- on " + string.Join(", ", holders)));
        }

        list.AppendLine("\nA card comes into battle with the Blood Moon gear that holds it.");
        InformSenderClient(list.ToString(), true);
    }

}
