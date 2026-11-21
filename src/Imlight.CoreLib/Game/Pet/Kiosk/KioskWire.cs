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

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Pet;

/// <summary>
/// What the game server and the kiosk's mini server say to each other over 127.0.0.1: one line of JSON each way per
/// question, like the Bazaar bot.
/// </summary>
internal sealed class KioskRequest {
    public string Op { get; set; }
    public ulong Id { get; set; }
    public ulong Owner { get; set; }
    public HatchmakingListing Listing { get; set; }
    public HatchedPartner Partner { get; set; }
    public KioskNotice Notice { get; set; }
}

internal sealed class KioskReply {
    public bool Ok { get; set; }
    public bool Done { get; set; }
    public int Count { get; set; }
    public List<HatchmakingListing> Listings { get; set; }
    public HatchmakingListing Listing { get; set; }
    public ulong Owner { get; set; }
    public List<KioskNotice> Notices { get; set; }
    public string Error { get; set; }
}

internal static class KioskWire {

    // The folder of the mini server's log and status, inside the server's logs folder (like logs/bazaar-bot). The
    // listings stay in hatchmaking-listings.json next to the server, where they always were.
    internal const string FOLDER = "logs/kiosk-bot";
    internal const string LOG_FILE = "kiosk-bot.txt";
    internal const string STATUS_FILE = "status.txt";

    internal static string Folder => Path.GetFullPath(FOLDER);

    internal static int Port => Math.Clamp(ConfigurationManager.GetValue("HatchKiosk.Port", 12390), 1024, 65000);

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.General);

    internal static string Write<T>(T value) => JsonSerializer.Serialize(value, s_json);

    internal static T Read<T>(string line) => JsonSerializer.Deserialize<T>(line, s_json);

    /// <summary>
    /// The status file Ahziael reads, one value a line: state (starting, ready, stopped, asleep, off), process id, how
    /// many pets are at the kiosk, when it was written, and the port. Written by the mini server while it runs, and by
    /// the game server while it sleeps or is off.
    /// </summary>
    internal static void WriteStatus(string state, int pid, int count, int port) {
        try {
            var text = $"{state}\n{pid}\n{count}\n{DateTime.UtcNow:O}\n{port}\n";
            Directory.CreateDirectory(Folder);
            var path = Path.Combine(Folder, STATUS_FILE);
            File.WriteAllText(path + ".tmp", text);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // Shown as unknown in Ahziael until the next try.
        }
    }

}
