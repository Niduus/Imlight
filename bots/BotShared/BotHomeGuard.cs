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
 *
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Imlight.Common;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// The bots stay home (Niduus, 2026-11-22: "I fear that someone may try them on live realm and that's not the mission").
/// Every bot (Ultimus, the TCO7, the Kitharas Family, True Form, Zoom, the Quest Team, the Bot Players) plays only inside
/// an Imlight server, through the server's own pretend game (headless sessions, Shared/Networking/Server). None of them has
/// a network client, a login to a game's login server or a way to reach the official game. This guard keeps it that way
/// and fails closed: when anything about the place they were started in does not look like Niduus's own Imlight server,
/// no bot starts, no mini server runs, and the reason is written to the log and to logs/bots/home-guard.txt.
/// The CI check tools/bots-home-check/check.ps1 stops a change that would give the bots a way out from being merged or
/// published.
/// </summary>
internal static class BotHomeGuard {

    // The official game's own addresses. A bot never names them, and the server they run in may not point at them.
    private static readonly string[] OFFICIAL = ["wizard101.com", "kingsisle.com", "gameforge.com"];

    // The settings that say where Imlight's own servers are.
    private static readonly string[] ADDRESS_SETTINGS = [
        "Game Server.GameServerIP", "Game Server.PublicAddress", "Patch Server.PatchServerInternalUrl",
        "Login Server.LoginServerAddress", "Login Server.PublicAddress",
    ];

    private static readonly object s_lock = new();
    private static bool? s_ok;
    private static string s_why = "";

    /// <summary>True only inside an Imlight server (or one of its own bot mini servers). Checked once, then remembered.</summary>
    internal static bool Home {
        get {
            lock (s_lock) {
                if (s_ok is { } known) {
                    return known;
                }

                var why = new List<string>();
                try {
                    Check(why);
                }
                catch (Exception ex) {
                    why.Add("the check itself failed (" + ex.Message + "), so the bots stay off");
                }

                s_ok = why.Count == 0;
                s_why = string.Join("; ", why);
                if (!s_ok.Value) {
                    Locked();
                }

                return s_ok.Value;
            }
        }
    }

    /// <summary>Why the bots were kept off ("" when they may run).</summary>
    internal static string Why => s_why;

    private static void Check(List<string> why) {
        // 1. The program that loaded the bots is Imlight's own server (Imlight.Director, also used for the mini servers).
        var entry = Assembly.GetEntryAssembly()?.GetName().Name ?? "";
        if (!string.Equals(entry, "Imlight.Director", StringComparison.Ordinal)) {
            why.Add($"they were started by \"{(entry == "" ? "an unknown program" : entry)}\", not by the Imlight server");
        }

        // 2. Imlight's own settings file is there and names Imlight's own servers.
        var game = Setting("Game Server.GameServerName");
        var login = Setting("Login Server.LoginServerName");
        if (string.IsNullOrWhiteSpace(game) || string.IsNullOrWhiteSpace(login)) {
            why.Add("Imlight.ini has no Imlight game and login server in it");
        }

        // 3. None of the server's own addresses points at the official game.
        foreach (var key in ADDRESS_SETTINGS) {
            var value = Setting(key);
            if (NamesOfficial(value)) {
                why.Add($"{key} points at the official game ({value})");
            }
        }
    }

    /// <summary>True when the text names one of the official game's addresses.</summary>
    internal static bool NamesOfficial(string text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return false;
        }

        foreach (var name in OFFICIAL) {
            if (text.Contains(name, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }

        return false;
    }

    private static string Setting(string key) {
        try {
            return ConfigurationManager.GetSetting(key) ?? "";
        }
        catch (Exception) {
            return "";
        }
    }

    private static void Locked() {
        var line = "BOTS LOCKED: the bots only play on your own Imlight server, and this does not look like one: " + s_why +
                   ". No bot was started.";
        try {
            Logger.Error("{0}", Logger.Args(line));
        }
        catch (Exception) {
            // The log may not be up in a program that is not the Imlight server: the file below still says why.
        }

        try {
            var folder = Path.GetFullPath(BotMistakes.FOLDER);
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "home-guard.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (Exception) {
            // Nothing more to do: the bots stay off either way.
        }
    }

}
