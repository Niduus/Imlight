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
using Imlight.CoreLib.Game.ItemSafety;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// The item history from chat (Niduus, 2026-10-28: items disappearing): `.lostitems` lists the last items that left
/// this wizard and why, `.lostitems &lt;id&gt;` gives one back (QA). Ahziael's Home card does the same with a button.
/// </summary>
internal class CommandLostItemsProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    private const int SHOWN = 12;

    [Command("lostitems")]
    [AuthRequired(AuthLevel.QualityAssurance)]
    private void LostItemsCommand([Remainder] string id = "") {
        var wizard = Context.Character;
        id = (id ?? "").Trim();
        if (id.Length == 0) {
            var records = ItemHistory.RecentFor(wizard.CharId, SHOWN)
                .Where(record => record.Kind != ItemRecordKind.Restored)
                .ToList();
            if (records.Count == 0) {
                InformSenderClient("No item has left this wizard since the item history started.");

                return;
            }

            InformSenderClient($"The last {records.Count} items that left this wizard, newest first. Type .lostitems and an id to give one back:");
            foreach (var record in records) {
                var state = record.Restored ? " (given back)" : record.Kind == ItemRecordKind.Moved ? " (moved, not lost)" : "";
                InformSenderClient($"{record.BackupId}: {record.ItemName}, {record.Kind.ToString().ToLowerInvariant()}: {record.Reason}{state}");
            }

            return;
        }

        var backup = ItemHistory.Find(id);
        if (backup is null) {
            InformSenderClient($"No item record has the id {id}. Type .lostitems to see them.");

            return;
        }

        InformSenderClient(ItemHistory.TryRestore(wizard, backup, Context.SessionActor, out var message)
            ? $"{backup.ItemName} is back: {message}."
            : $"{backup.ItemName} was not given back: {message}.");
    }

}
