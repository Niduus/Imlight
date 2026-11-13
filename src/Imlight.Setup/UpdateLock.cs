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
using System.IO;
using System.Text;

namespace Imlight.Setup;

/// <summary>
/// One Ahziael update at a time. Every step that downloads, builds or puts in an update holds this, across every
/// Ahziael program on the PC (the window, the hidden build, the installer and the server starter). Niduus, 2026-11-13:
/// clicking Update while a new release was still coming out had two updates going at once, and Ahziael stopped working.
/// </summary>
/// <remarks>
/// The lock is a file in the install folder that only one program can have open. Windows lets go of it when that
/// program ends, even if it crashes, so a stuck lock can't happen.
/// </remarks>
internal sealed class UpdateLock : IDisposable {

    internal const string FILE = "update-in-progress.lock";

    private readonly FileStream _stream;
    private readonly string _path;

    private UpdateLock(FileStream stream, string path) {
        _stream = stream;
        _path = path;
    }

    /// <summary>Takes the lock, or returns null when another update step has it.</summary>
    /// <param name="what">What this step is, kept in the file for the logs ("Building Imlight 1.2.3").</param>
    internal static UpdateLock? TryTake(string installFolder, string what) {
        var path = Path.Combine(installFolder, FILE);
        try {
            Directory.CreateDirectory(installFolder);
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try {
                stream.SetLength(0);
                var text = Encoding.UTF8.GetBytes($"{what}\nProgram {Environment.ProcessId}, since {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n");
                stream.Write(text);
                stream.Flush();
            }
            catch (IOException) {
                // What it says is only for people reading the folder; the lock itself is held.
            }

            return new UpdateLock(stream, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    /// <summary>Whether another program is in the middle of an update step right now.</summary>
    internal static bool IsHeld(string installFolder) {
        using var taken = TryTake(installFolder, "Checking");

        return taken is null && File.Exists(Path.Combine(installFolder, FILE));
    }

    private bool _released;

    public void Dispose() {
        if (_released) {
            return;
        }

        _released = true;
        _stream.Dispose();
        try {
            File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Another step took it straight away; it is theirs now.
        }
    }

}
