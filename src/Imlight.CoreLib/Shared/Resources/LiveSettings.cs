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

using Imlight.Common;

namespace Imlight.CoreLib.Shared.Resources;

/// <summary>
/// Reads Imlight.ini again whenever it is saved, by hand or from the Manager, so its settings apply without a restart
/// (Niduus, 2026-10-25: "I want ALL configs to happen dynamically"). Ports, paths, logging, the locale and the database
/// are only read at startup. The Manager looks for the line below in this dll to know the server reloads by itself.
/// </summary>
public static class LiveSettings {

    public const string ReloadedLine = "Imlight.ini changed; the new settings are in use now.";

    public static void Start() {
        ConfigurationManager.Reloaded += () => Logger.Information(ReloadedLine);
        ConfigurationManager.ReloadFailed += ex => Logger.Error("Imlight.ini changed but could not be read; the earlier settings stay. {Message}",
            Logger.Args(ex.Message));
        ConfigurationManager.WatchForChanges();
    }

}
