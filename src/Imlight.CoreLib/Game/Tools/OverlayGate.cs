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
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using Imlight.Common;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>
/// The Ahziael overlay check (Niduus, 2026-11-11: "Have the Server be given an option to have this as a requirement for
/// entry, if client doesn't, kick that client from connection. Have this as a toggle (Off default)").
///
/// Ahziael draws its overlay over each Wizard101 window on the player's PC, and while it does it keeps one connection
/// open to this server's Remote Admin port (op "overlay"), saying every few seconds how many game windows it is drawing
/// on. A game connection is let in while its address has that many overlay windows or more. With
/// [Overlay] RequiredToPlay on, the game servers kick a wizard whose address has none (after GraceSeconds), with
/// KickMessage. Applies live: turning it on checks everyone already playing, turning it off stops the kicks at once.
///
/// It is a friendly check, not anti-cheat: someone who writes their own program can say "I'm the overlay" too.
/// </summary>
public static class OverlayGate {

    public const string DEFAULT_MESSAGE =
        "This server needs the Ahziael overlay. Get Ahziael from github.com/Niduus/Imlight/releases " +
        "(ImlightManager.exe), add this server under Play, click Play in Ahziael, and log in again.";

    /// <summary>How often the overlay says it is still there; a link quiet for 3 of these counts as gone.</summary>
    public const int BEAT_SECONDS = 5;

    private static readonly TimeSpan s_quiet = TimeSpan.FromSeconds(BEAT_SECONDS * 3);

    public static bool Required => ConfigurationManager.GetValue("Overlay.RequiredToPlay", false);

    /// <summary>Seconds a wizard may play without the overlay before the kick, so a game started a moment early isn't thrown out.</summary>
    public static int GraceSeconds => Math.Clamp(ConfigurationManager.GetValue("Overlay.GraceSeconds", 15), 3, 300);

    public static string KickMessage {
        get {
            var text = ConfigurationManager.GetValue("Overlay.KickMessage", "");

            return string.IsNullOrWhiteSpace(text) ? DEFAULT_MESSAGE : text.Trim();
        }
    }

    private sealed class Link {

        internal string Address;
        internal int Windows;
        internal DateTime SeenUtc;
        internal string Version;

    }

    private static readonly ConcurrentDictionary<long, Link> s_links = new();
    private static long s_nextId;

    /// <summary>Most overlays one address may have open at once.</summary>
    public const int MAX_PER_ADDRESS = 8;

    /// <summary>A new overlay link from <paramref name="address"/>, or 0 when that address already has too many.</summary>
    public static long Open(string address, int windows, string version) {
        address = Normalize(address);
        if (s_links.Values.Count(l => l.Address == address) >= MAX_PER_ADDRESS) {
            return 0;
        }

        var id = Interlocked.Increment(ref s_nextId);
        s_links[id] = new Link {
            Address = address, Windows = Math.Clamp(windows, 0, 16), SeenUtc = DateTime.UtcNow,
            Version = (version ?? "").Length > 32 ? version[..32] : version ?? "",
        };

        return id;
    }

    public static void Beat(long id, int windows) {
        if (s_links.TryGetValue(id, out var link)) {
            link.Windows = Math.Clamp(windows, 0, 16);
            link.SeenUtc = DateTime.UtcNow;
        }
    }

    public static void Close(long id) => s_links.TryRemove(id, out _);

    /// <summary>How many game connections from this address the overlays there cover.</summary>
    public static int Covered(string address) {
        address = Normalize(address);
        var now = DateTime.UtcNow;

        return s_links.Values.Where(l => l.Address == address && now - l.SeenUtc < s_quiet).Sum(l => l.Windows);
    }

    /// <summary>Overlays open now (for the log line and Ahziael).</summary>
    public static int OpenCount => s_links.Count;

    /// <summary>
    /// The plain address of "1.2.3.4", "1.2.3.4:5678", "[::ffff:1.2.3.4]:5678" or "::1", as both sides write it:
    /// IPv4 for an IPv4 address dressed as IPv6, and every loopback address as 127.0.0.1.
    /// </summary>
    public static string Normalize(string text) {
        text = (text ?? "").Trim();
        IPAddress address = null;
        if (IPEndPoint.TryParse(text, out var endPoint)) {
            address = endPoint.Address;
        }
        else if (IPAddress.TryParse(text.Trim('[', ']'), out var plain)) {
            address = plain;
        }

        if (address is null) {
            return text;
        }

        if (address.IsIPv4MappedToIPv6) {
            address = address.MapToIPv4();
        }

        return IPAddress.IsLoopback(address) ? "127.0.0.1" : address.ToString();
    }

    // ---------------------------------------------------------------------
    // The server's own overlay
    // ---------------------------------------------------------------------

    /// <summary>
    /// The texts and boxes the host made for everyone who plays here (Niduus, 2026-11-11: "create 2 seperate sections in
    /// the UI overlay: Server and Client ... Server always loads first and positions itself first"). Ahziael's
    /// Play &gt; Overlay &gt; Server saves it to spiraldb-extra/Customs/ServerOverlay.json; it goes to each overlay over the
    /// link, as data only (the overlay checks every value before drawing it).
    /// </summary>
    public const string SERVER_DESIGN_FILE = "ServerOverlay.json";

    /// <summary>Largest server design sent, in bytes.</summary>
    public const int MAX_DESIGN_BYTES = 48 << 10;

    private static readonly object s_designLock = new();
    private static DateTime s_designCheck = DateTime.MinValue;
    private static DateTime s_designTime = DateTime.MinValue;
    private static string s_designStamp = "none";
    private static JsonNode s_design;

    /// <summary>The server's design as it is now (re-read when the file changes): its stamp, and the design or null for none.</summary>
    public static (string Stamp, JsonNode Design) ServerDesign() {
        lock (s_designLock) {
            var now = DateTime.UtcNow;
            if (now - s_designCheck < TimeSpan.FromSeconds(2)) {
                return (s_designStamp, s_design);
            }

            s_designCheck = now;
            try {
                var path = Path.Combine(SpiralDB.ExtraPath, "Customs", SERVER_DESIGN_FILE);
                var time = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
                if (time != s_designTime) {
                    s_designTime = time;
                    s_design = null;
                    s_designStamp = "none";
                    if (time != DateTime.MinValue && new FileInfo(path).Length <= MAX_DESIGN_BYTES) {
                        s_design = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                        s_designStamp = s_design is null ? "none" : time.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {
                // Half written or not readable: tried again on the next check.
                s_designTime = DateTime.MinValue;
            }

            return (s_designStamp, s_design);
        }
    }

}
