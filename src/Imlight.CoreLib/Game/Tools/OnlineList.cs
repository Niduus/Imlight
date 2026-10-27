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
using System.Threading;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>
/// Writes who is online to online-players.json next to the server, for the "Who's online" card on Ahziael's Home
/// (Niduus, 2026-10-27). Every 2 seconds it reads the players kept in memory (never the database, so logins are never
/// held up) and writes the file only when the list changed.
/// </summary>
internal static class OnlineList {

    private const string FILE = "online-players.json";
    // A zone change drops a player for a moment and adds them back; they stay listed that long so the card doesn't flicker.
    private static readonly TimeSpan s_gone = TimeSpan.FromSeconds(8);

    private static Timer s_timer;
    private static string s_written;
    private static readonly Dictionary<ulong, (Row Row, DateTime Seen)> s_rows = [];

    private sealed record Row(string Account, string Wizard, string Zone);

    /// <summary>Starts writing the list. Calling it again does nothing.</summary>
    internal static void Start() {
        var timer = new Timer(_ => Write());
        if (Interlocked.CompareExchange(ref s_timer, timer, null) is not null) {
            timer.Dispose();

            return;
        }

        // A list left from the last run is old.
        try {
            File.Delete(FILE);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
        }

        timer.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private static void Write() {
        // A slow round (a busy disk) is skipped over, not stacked up.
        if (!Monitor.TryEnter(s_rows)) {
            return;
        }

        try {
            var now = DateTime.UtcNow;
            foreach (var player in OnlinePlayerCollection.GetCachedOnlinePlayers()) {
                s_rows[player.AccountId] = (new Row(player.AccountName ?? "", player.CharacterId == 0 ? "" : player.WizardName ?? "",
                                                    player.CharacterId == 0 ? "" : player.CurrentZone ?? ""), now);
            }

            foreach (var gone in s_rows.Where(r => now - r.Value.Seen > s_gone).Select(r => r.Key).ToList()) {
                s_rows.Remove(gone);
            }

            var players = s_rows.Values.Select(r => r.Row)
                .OrderBy(r => r.Account, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Wizard, StringComparer.OrdinalIgnoreCase)
                .Select(r => new { r.Account, r.Wizard, r.Zone });
            var json = JsonSerializer.Serialize(new { Players = players });
            if (json == s_written) {
                return;
            }

            // Written whole and then moved, so the app never reads half a list.
            File.WriteAllText(FILE + ".new", json);
            File.Move(FILE + ".new", FILE, overwrite: true);
            s_written = json;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // The app is reading it; the next round writes it.
        }
        catch (Exception ex) {
            Logger.Error("Could not write who is online for the app: {0}", Logger.Args(ex.Message));
        }
        finally {
            Monitor.Exit(s_rows);
        }
    }

}
