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
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;

namespace Imlight.CoreLib.Game;

/// <summary>
/// Which accounts may come back into each game server, kept in login-keys.json next to the server so they outlive a
/// restart of the whole server (an update that changed the server's program: Niduus, 2026-10-28). A game that lost its
/// connection tries to come back by itself; with its account listed here, the restarted server lets it in and puts the
/// wizard back where they were saved, instead of sending them to the login screen. The keys themselves are not stored:
/// a key is worked out again from the account (SessionKey.GenerateHash).
/// </summary>
internal static class KeptLoginKeys {

    private const string FILE = "login-keys.json";

    private sealed record Entry(string Server, ulong AccountId, DateTime ExpiresUtc);

    private static readonly object s_lock = new();
    private static List<Entry> s_entries;

    /// <summary>Remembers that <paramref name="accountId"/> may join <paramref name="server"/> until the key expires.</summary>
    internal static void Remember(string server, ulong accountId, TimeSpan validFor) {
        lock (s_lock) {
            var entries = Load();
            entries.RemoveAll(entry => entry.AccountId == accountId && entry.Server == server);
            entries.Add(new Entry(server, accountId, DateTime.UtcNow + validFor));
            Save(entries);
        }
    }

    /// <summary>The accounts that may still join <paramref name="server"/>, with how long their key has left.</summary>
    internal static List<(ulong AccountId, TimeSpan Left)> For(string server) {
        lock (s_lock) {
            var now = DateTime.UtcNow;

            return [.. Load().Where(entry => entry.Server == server && entry.ExpiresUtc > now)
                .Select(entry => (entry.AccountId, entry.ExpiresUtc - now))];
        }
    }

    private static List<Entry> Load() {
        if (s_entries is not null) {
            return s_entries;
        }

        try {
            s_entries = File.Exists(FILE) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FILE)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            s_entries = [];
        }

        s_entries.RemoveAll(entry => entry.ExpiresUtc <= DateTime.UtcNow);

        return s_entries;
    }

    private static void Save(List<Entry> entries) {
        try {
            File.WriteAllText(FILE + ".new", JsonSerializer.Serialize(entries));
            File.Move(FILE + ".new", FILE, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Warning("Could not keep the login keys for after a restart: {0}", Logger.Args(ex.Message));
        }
    }

}
