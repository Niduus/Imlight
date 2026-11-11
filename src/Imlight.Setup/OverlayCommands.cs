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
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Imlight.Setup;

/// <summary>
/// Where the overlay's command and cheat buttons send what they run (Niduus, 2026-11-11: "I want the cheat buttons and
/// to link the cheats in the buttons but only for that client only"). No username or password: it goes over the
/// overlay's own connection to the server the game was started on (<see cref="OverlayHost.Link"/>), for the wizard in
/// the game window the button sits on. The server links that window to its wizard by itself when the PC has one game
/// window and one wizard in the game; otherwise the window shows a code once and the player types ".link code" in that
/// game's chat. The server runs it as that wizard, with that account's own rank, as if typed in chat.
/// </summary>
internal static class OverlayCommands {

    /// <summary>One of the player's cheats, as a server would list it.</summary>
    internal sealed record CheatInfo(int Number, string Name, bool On, bool CanSwitch, bool CanUse, string Owner = "");

    /// <summary>Whether it worked, and what to show; <see cref="Code"/> is a ".link" code the player must type first.</summary>
    internal sealed record Result(bool Ok, string Text, IReadOnlyList<CheatInfo> Cheats, string Code = "");

    /// <summary>
    /// Runs <paramref name="value"/> on the server: kind "command" (a chat command), "cheat" ("on|off|use NAME") or
    /// "cheats" (the list). <paramref name="game"/> is the game window the button is on (zero from Ahziael's designer).
    /// </summary>
    internal static Task<Result> SendAsync(string kind, string value, IntPtr game = default, CancellationToken stop = default)
        => OverlayHost.Link.SendAsync(game.ToInt64(), kind, value, stop);

    /// <summary>The server's answer to one button.</summary>
    internal static Result Read(JsonObject answer) {
        var ok = answer["ok"]?.GetValue<bool>() == true;
        var text = ((string?) (ok ? answer["text"] : answer["error"]) ?? "").Trim();
        var cheats = new List<CheatInfo>();
        if (answer["cheats"] is JsonArray list) {
            foreach (var item in list.OfType<JsonObject>().Take(500)) {
                cheats.Add(new CheatInfo((int?) item["number"] ?? 0, ((string?) item["name"] ?? "").Trim(),
                    (bool?) item["on"] ?? false, (bool?) item["canSwitch"] ?? false, (bool?) item["canUse"] ?? false, ((string?) item["owner"] ?? "").Trim()));
            }
        }

        var code = new string(((string?) answer["link"] ?? "").Where(char.IsDigit).Take(8).ToArray());
        if (text.Length > 300) {
            text = text[..300];
        }

        return new Result(ok, ok && text.Length == 0 ? "Done." : text, cheats, code);
    }

}
