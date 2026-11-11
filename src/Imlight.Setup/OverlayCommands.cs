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
using System.Threading;
using System.Threading.Tasks;

namespace Imlight.Setup;

/// <summary>
/// Where the overlay's command and cheat buttons send what they run. The sign-in with a game password and secret key
/// was taken out (Niduus, 2026-11-11: "I do not like this idea. Scrap it completely."); a link with no password, tied to
/// the wizard in that game window, takes its place. Until then every call answers, in plain words, that it can't.
/// </summary>
internal static class OverlayCommands {

    /// <summary>One of the player's cheats, as a server would list it.</summary>
    internal sealed record CheatInfo(int Number, string Name, bool On, bool CanSwitch, bool CanUse);

    /// <summary>Whether it worked, and what to show.</summary>
    internal sealed record Result(bool Ok, string Text, IReadOnlyList<CheatInfo> Cheats);

    internal const string NOT_NOW = "Command and cheat buttons can't reach the server for now. Type the command in the game's chat instead.";

    /// <summary>For now always answers that commands and cheats can't be sent from here.</summary>
    internal static Task<Result> SendAsync(string kind, string value, CancellationToken stop = default)
        => Task.FromResult(new Result(false, NOT_NOW, []));

}
