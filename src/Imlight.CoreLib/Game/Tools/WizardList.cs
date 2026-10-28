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
using System.Linq;
using System.Text.Json;
using System.Threading;
using Imlight.Common;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Databases;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>
/// Writes every account and its wizards (name, school, level) to wizard-list.json next to the server, for the player
/// cheats' "wizard is" block in Ahziael (Niduus, 2026-10-28: "picking their account and having the condition of 'If wizard
/// is' and have a drop down of the wizard list on said account with school"). Written a little after the server starts,
/// a few seconds after a wizard comes into the game (a new one, a new name, a new level), and every ten minutes. The file
/// stays when the server stops, so the app can show the list then too.
/// </summary>
internal static class WizardList {

    private const string FILE = "wizard-list.json";

    private static Timer s_timer;
    private static string s_written;
    private static int s_writing;

    /// <summary>Starts writing the list. Calling it again does nothing.</summary>
    internal static void Start() {
        var timer = new Timer(_ => Write());
        if (Interlocked.CompareExchange(ref s_timer, timer, null) is not null) {
            timer.Dispose();

            return;
        }

        timer.Change(TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(10));
    }

    /// <summary>A wizard came into the game: the list is written again in a few seconds.</summary>
    internal static void Changed() => s_timer?.Change(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10));

    private static void Write() {
        // A slow round (a busy database) is skipped over, not stacked up.
        if (Interlocked.Exchange(ref s_writing, 1) == 1) {
            return;
        }

        try {
            using var session = PlayerDatabase.Instance.Store.OpenSession();
            var accounts = session.Query<Account>(collectionName: AccountCollection.CollectionName).ToList();
            var wizards = session.Query<Wizard>(collectionName: WizardCollection.CollectionName).ToList()
                .Where(w => w is not null).ToLookup(w => w.AccountId);
            var list = accounts.Where(a => a is not null && !string.IsNullOrEmpty(a.Username))
                .OrderBy(a => a.Username, StringComparer.OrdinalIgnoreCase)
                .Select(a => new {
                    Account = a.Username,
                    Rank = a.AuthLevel.ToString(),
                    Wizards = wizards[a.AccountId]
                        .Select(w => new {
                            Id = w.CharId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            Name = Safe(() => w.PlayerNameBehavior?.GetWizardName()) ?? "",
                            School = w.MagicSchoolBehavior?.MagicSchool.ToString() ?? "",
                            Level = w.MagicSchoolBehavior?.Level ?? 0,
                        })
                        .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase),
                });
            var json = JsonSerializer.Serialize(new { Accounts = list });
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
            Logger.Warning("Could not write the wizard list for the app: {0}", Logger.Args(ex.Message));
        }
        finally {
            Interlocked.Exchange(ref s_writing, 0);
        }
    }

    private static string Safe(Func<string> name) {
        try {
            return name();
        }
        catch (Exception) {
            return null;
        }
    }

}
