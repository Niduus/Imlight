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
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Auth;
using Imlight.CoreLib.Game.Codes;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.code HYPER` redeems a code (the Manager's Codes card makes them). The server never opens the game's promo code
/// window (Niduus, 2026-10-24: "Completely remove Promo Code UI usage on the server"), so chat is the only way to
/// redeem. `.code watch` logs what the game sends for 2 minutes. Administrators can also make and change codes in chat
/// (Niduus, 2026-11-04, chat commands for Ahziael's features), writing codes.json as the Codes card does:
/// `.code make SUMMER gold 5000 item Hyper Elixir x2 uses 1 for 7d`, `.code list`, `.code off SUMMER`, `.code on SUMMER`,
/// `.code remove SUMMER`.
/// </summary>
internal class CommandCodeProtocol : CommandProtocol {

    internal override string Group { get; set; } = "";

    // Anyone may redeem codes; `.code watch` is for quality assurance.
    [Command("code")]
    [Alias("codes", "redeem")]
    private void CodeCommand([Remainder] string text = "") {
        text = (text ?? "").Trim();
        if (text.Length == 0) {
            InformSenderClient("Type .code and the code to redeem it, such as .code HYPER.");

            return;
        }

        var words = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (words[0].Equals("watch", StringComparison.OrdinalIgnoreCase)) {
            // For finding what a button sends (Niduus, 2026-10-23).
            if (!AuthorityRequester.RequestAuthority(AuthLevel.QualityAssurance, Context.Account, "Command code watch")) {
                InformSenderClient("You do not have permission to use this command.");

                return;
            }

            Context.SessionActor.Tell(new WatchClientMessages { Seconds = 120 }, ActorRefs.NoSender);
            InformSenderClient("For the next 2 minutes the server log gets every message your game sends. Press the button now.");

            return;
        }

        // Ahziael's Codes card in chat, for administrators: a player typing a code called MAKE still redeems it.
        if (words[0].ToLowerInvariant() is "make" or "list" or "remove" or "delete" or "on" or "off"
            && (Context.Account?.AuthLevel ?? AuthLevel.None) >= AuthLevel.Administrator) {
            try {
                ManageCodes(words[0].ToLowerInvariant(), words.Length > 1 ? words[1].Trim() : "");
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {
                InformSenderClient($"codes.json could not be changed: {ex.Message}");
            }

            return;
        }

        try {
            PromoCodes.Redeem(Context.Character?.Account ?? Context.Account, text, Context.Character, Context.SessionActor, out var answer);
            InformSenderClient(answer);
        }
        catch (Exception ex) {
            Logger.Error("Codes | Redeeming \"{0}\" with .code failed. {1}", Logger.Args(text, ex.Message));
            InformSenderClient("Something went wrong redeeming that code. Try again.");
        }
    }

    private static readonly string[] s_makeWords = ["gold", "crowns", "item", "uses", "for", "starts", "note"];

    private void ManageCodes(string action, string rest) {
        var file = PromoCodes.ReadForChange();
        var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (action == "list") {
            var page = words.Count > 0 && int.TryParse(words[0], out var asked) ? Math.Max(1, asked) : 1;
            if (file.Codes.Count == 0) {
                InformSenderClient("There are no codes. Make one with .code make CODE gold 500 (or item, crowns, uses, for).");

                return;
            }

            var pages = (file.Codes.Count + 9) / 10;
            page = Math.Min(page, pages);
            var text = new System.Text.StringBuilder()
                .AppendLine($"<center>Codes: {file.Codes.Count}{(pages > 1 ? $" (page {page} of {pages})" : "")}</center>")
                .AppendLine();
            foreach (var code in file.Codes.Skip((page - 1) * 10).Take(10)) {
                var window = PromoCodes.Window(code);
                text.AppendLine($"{code.Code}{(code.On ? "" : " (off)")}: {PromoCodes.Gives(code)}"
                    + $", {(code.UsesPerAccount == 0 ? "no limit" : $"{code.UsesPerAccount} per account")}"
                    + (window.Length > 0 ? $", {window}" : ""));
            }

            if (page < pages) {
                text.AppendLine().Append($"More: type .code list {page + 1}");
            }

            InformSenderClient(text.ToString().TrimEnd(), true);

            return;
        }

        if (words.Count == 0) {
            InformSenderClient(action == "make"
                ? "Type .code make, the code, then what it gives: such as .code make SUMMER gold 5000 item Hyper Elixir x2 uses 1 for 7d."
                : $"Type .code {action} and the code, such as .code {action} SUMMER.");

            return;
        }

        var name = words[0].ToUpperInvariant();
        var existing = file.Codes.FirstOrDefault(c => string.Equals((c.Code ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase));
        if (action != "make") {
            if (existing is null) {
                InformSenderClient($"There is no code {name}. .code list shows them.");

                return;
            }

            if (action is "remove" or "delete") {
                file.Codes.Remove(existing);
            }
            else {
                existing.On = action == "on";
            }

            PromoCodes.Save(file);
            Logger.Information("Codes | {0} used .code {1} {2} in game.", Logger.Args(Context.Account?.Username, action, name));
            InformSenderClient(action is "remove" or "delete" ? $"Removed the code {name}."
                : action == "on" ? $"The code {name} works again." : $"The code {name} is off: nobody can redeem it until .code on {name}.");

            return;
        }

        if (existing is not null) {
            InformSenderClient($"The code {name} already exists. .code remove {name} first to make it again.");

            return;
        }

        if (name.Length > 30 || !name.All(char.IsLetterOrDigit)) {
            InformSenderClient("A code is letters and numbers only (30 at most), such as SUMMER2026.");

            return;
        }

        var made = new PromoCode { Code = name, UsesPerAccount = 1, Note = $"Made in game by {Context.Account?.Username}" };
        for (var i = 1; i < words.Count; i++) {
            var word = words[i].ToLowerInvariant();
            var value = i + 1 < words.Count ? words[i + 1] : "";
            switch (word) {
                case "gold" when int.TryParse(value, out var gold) && gold > 0:
                    made.Gold = gold;
                    i++;

                    break;
                case "crowns" when int.TryParse(value, out var crowns) && crowns > 0:
                    made.Crowns = crowns;
                    i++;

                    break;
                case "uses" when int.TryParse(value, out var uses) && uses >= 0:
                    made.UsesPerAccount = uses;
                    i++;

                    break;
                case "for" when CommandUtilities.TryParseDuration(value, out var length) && length > TimeSpan.Zero:
                    made.Ends = DateTime.Now.Add(length).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
                    i++;

                    break;
                case "item": {
                    // The item's words run to the next keyword; "x3" at the end is how many.
                    var end = i + 1;
                    while (end < words.Count && !s_makeWords.Contains(words[end].ToLowerInvariant())) {
                        end++;
                    }

                    var itemWords = words.Skip(i + 1).Take(end - i - 1).ToList();
                    var count = 1;
                    if (itemWords.Count > 1 && itemWords[^1].StartsWith('x') && int.TryParse(itemWords[^1][1..], out var times) && times > 0) {
                        count = Math.Min(times, 999);
                        itemWords.RemoveAt(itemWords.Count - 1);
                    }

                    var item = FindItem(string.Join(' ', itemWords));
                    if (item is null) {
                        return;
                    }

                    made.Items.Add(new PromoCodeItem { ItemId = item.Id, ItemName = item.Name, Count = count });
                    i = end - 1;

                    break;
                }
                case "gold" or "crowns" or "uses":
                    InformSenderClient($"{word} needs a number after it, such as {word} {(word == "uses" ? "1" : "500")}.");

                    return;
                case "for":
                    InformSenderClient("for needs a time after it, such as for 7d, for 12h or for 1d12h.");

                    return;
                default:
                    InformSenderClient($"\"{words[i]}\" isn't understood here. Use gold <number>, crowns <number>, item <name or ID> (x2 for two), "
                        + "uses <number, 0 for no limit> and for <time, like 7d>.");

                    return;
            }
        }

        if (made.Gold == 0 && made.Crowns == 0 && made.Items.Count == 0) {
            InformSenderClient("A code needs something to give: gold, crowns or an item.");

            return;
        }

        file.Codes.Add(made);
        PromoCodes.Save(file);
        Logger.Information("Codes | {0} made the code {1} in game: {2}.", Logger.Args(Context.Account?.Username, name, PromoCodes.Gives(made)));
        var madeWindow = PromoCodes.Window(made);
        InformSenderClient($"Made the code {name}: it gives {PromoCodes.Gives(made)}, "
            + (made.UsesPerAccount == 0 ? "as often as an account likes" : $"{made.UsesPerAccount} time{(made.UsesPerAccount == 1 ? "" : "s")} per account")
            + (madeWindow.Length > 0 ? $", {madeWindow}" : "") + $". Players type .code {name} to get it. It shows on Ahziael's Codes card too.");
    }

    private Imlight.CoreLib.Shared.Resources.IdCatalogEntry FindItem(string words) {
        if (words.Length == 0) {
            InformSenderClient("Type the item's name or ID after item, such as item Hyper Elixir.");

            return null;
        }

        if (!Imlight.CoreLib.Shared.Resources.IdCatalog.IsReady) {
            if (ulong.TryParse(words, out var id)) {
                return new(id, Imlight.CoreLib.Shared.Resources.IdCatalogKind.Item, "", "", 0, "");
            }

            InformSenderClient("The item list is still being built. Try again in a minute, or use the item's ID.");

            return null;
        }

        var found = Imlight.CoreLib.Shared.Resources.IdCatalog.Search(Imlight.CoreLib.Shared.Resources.IdCatalogKind.Item, words, 6);
        if (found.Count == 0) {
            InformSenderClient($"No item is called \"{words}\". .lookup item finds names and IDs.");

            return null;
        }

        if (found.Count > 1 && !found[0].Name.Equals(words, StringComparison.OrdinalIgnoreCase) && !ulong.TryParse(words, out _)) {
            InformSenderClient($"Several items match \"{words}\": {string.Join(", ", found.Select(f => $"{f.Name} ({f.Id})"))}. Use the full name or the ID.");

            return null;
        }

        return found[0];
    }

}
