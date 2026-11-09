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
/// (Niduus, 2026-10-27) and the Overview tab (2026-11-09), and saves when each account was last online. Every 2 seconds it reads the players kept in memory (never the database, so logins are never
/// held up) and writes the file only when the list changed.
/// </summary>
internal static class OnlineList {

    private const string FILE = "online-players.json";
    // A zone change drops a player for a moment and adds them back; they stay listed that long so the card doesn't flicker.
    private static readonly TimeSpan s_gone = TimeSpan.FromSeconds(8);

    // How often an online account's "last online" is saved, so the Accounts tab is right even after a crash.
    private static readonly TimeSpan s_saveEvery = TimeSpan.FromMinutes(1);

    private static Timer s_timer;
    private static string s_written;
    private static readonly Dictionary<ulong, (Row Row, DateTime Seen)> s_rows = [];
    // When each account came online this time, and when its "last online" was last saved (and with what).
    private static readonly Dictionary<ulong, DateTime> s_since = [];
    private static readonly Dictionary<ulong, (DateTime At, string Wizard, string Zone)> s_saved = [];

    private sealed record Row(string Account, string Wizard, string Zone, int Level, string School, bool InBattle, DateTime Since);

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
            var inBattleIds = Zone.Components.CombatDuelComponent.CharactersInBattle();
            var saves = new List<(ulong Account, DateTime At, string Wizard, string Zone)>();
            foreach (var player in OnlinePlayerCollection.GetCachedOnlinePlayers()) {
                var picking = player.CharacterId == 0;
                if (!s_since.TryGetValue(player.AccountId, out var since)) {
                    since = now;
                    s_since[player.AccountId] = since;
                }

                var row = new Row(player.AccountName ?? "", picking ? "" : player.WizardName ?? "", picking ? "" : player.CurrentZone ?? "",
                                  picking ? 0 : player.Level, picking ? "" : player.School ?? "",
                                  !picking && inBattleIds.Contains(player.CharacterId), since);
                s_rows[player.AccountId] = (row, now);

                // Saved when the wizard or the place changes, and once a minute while they play.
                var changed = !s_saved.TryGetValue(player.AccountId, out var saved)
                    || (row.Wizard.Length > 0 && (saved.Wizard != row.Wizard || saved.Zone != row.Zone));
                if (changed || now - saved.At >= s_saveEvery) {
                    var wizard = row.Wizard.Length > 0 ? row.Wizard : saved.Wizard ?? "";
                    var zone = row.Wizard.Length > 0 ? row.Zone : saved.Zone ?? "";
                    s_saved[player.AccountId] = (now, wizard, zone);
                    saves.Add((player.AccountId, now, wizard, zone));
                }
            }

            foreach (var gone in s_rows.Where(r => now - r.Value.Seen > s_gone).ToList()) {
                s_rows.Remove(gone.Key);
                s_since.Remove(gone.Key);
                // Logged off: the last time they were seen is when they were last online.
                var wizard = s_saved.TryGetValue(gone.Key, out var saved) ? saved.Wizard : gone.Value.Row.Wizard;
                var zone = s_saved.TryGetValue(gone.Key, out saved) ? saved.Zone : gone.Value.Row.Zone;
                s_saved.Remove(gone.Key);
                saves.Add((gone.Key, gone.Value.Seen, wizard ?? "", zone ?? ""));
            }

            if (saves.Count > 0) {
                // The database is never written from inside the list's round, so a slow save never holds it up.
                System.Threading.Tasks.Task.Run(() => {
                    foreach (var save in saves) {
                        AccountCollection.RecordLastOnline(save.Account, save.At, save.Wizard, save.Zone);
                    }
                });
            }

            var players = s_rows.Values.Select(r => r.Row)
                .OrderBy(r => r.Account, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Wizard, StringComparer.OrdinalIgnoreCase)
                .Select(r => new { r.Account, r.Wizard, r.Zone, r.Level, r.School, r.InBattle, Since = r.Since.ToString("o") });
            // How many are in a battle, so Ahziael can wait for the battles to end before it restarts the server for an update.
            var inBattle = Zone.Components.CombatDuelComponent.PlayersInBattle;
            var json = JsonSerializer.Serialize(new { Players = players, InBattle = inBattle });
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
