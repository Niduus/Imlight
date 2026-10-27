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
using System.Diagnostics;
using System.IO;

namespace Imlight.CoreLib.Game.Commands.Protocols;

/// <summary>
/// `.ver` says which Ahziael version the running server was built by, the commit it was built from and when it
/// started, so a player can tell whether a restart picked up an update. Every player can use it.
/// </summary>
internal class CommandVersionProtocol : CommandProtocol {

    // Written next to the server by the Ahziael app when it builds the server (SetupSteps.BuildServerAsync).
    internal const string VERSION_FILE = "ahziael-version.txt";
    // The release workflow's server build, and the app's note of the source commit.
    private const string RELEASE_VERSION_FILE = "version.txt";
    private const string SOURCE_FILE = "server-source-commit.txt";

    internal override string Group { get; set; } = "";

    [Command("ver")]
    [Alias("version")]
    private void VersionCommand([Remainder] string argument = "") {
        var folder = AppContext.BaseDirectory;
        var version = Read(Path.Combine(folder, VERSION_FILE)) ?? Read(Path.Combine(folder, RELEASE_VERSION_FILE));
        var source = Read(Path.Combine(folder, SOURCE_FILE));
        var commit = source?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts ? parts[^1] : null;
        if (commit is { Length: > 7 }) {
            commit = commit[..7];
        }

        string started;
        try {
            started = Process.GetCurrentProcess().StartTime.ToString("MMM d, h:mm tt");
        }
        catch (Exception) {
            started = "unknown";
        }

        InformSenderClient($"Ahziael {(version is null ? "version unknown (built before .ver existed)" : "v" + version.TrimStart('v'))}"
            + (commit is null ? "" : $", server built from {commit}")
            + $", server started {started}.");
    }

    private static string Read(string path) {
        try {
            return File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } text ? text : null;
        }
        catch (Exception) {
            return null;
        }
    }

}
