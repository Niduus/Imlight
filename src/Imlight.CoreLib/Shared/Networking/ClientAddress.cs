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
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Imlight.Common;

namespace Imlight.CoreLib.Shared.Networking;

/// <summary>
/// Picks the game server address to send each player, so players on this computer, on the same home
/// network, on a VPN or on the internet are each told an address they can reach.
/// </summary>
/// <remarks>
/// Every server runs on this one computer, so the address a player used to reach us works for all of them.
/// The one exception is a player on the internet reaching us through the router: our own end of their
/// connection is the home network address, which they cannot reach, so they get the public address instead.
/// </remarks>
public static class ClientAddress {

    private static readonly string[] PUBLIC_IP_SERVICES = [
        "https://api.ipify.org/", "https://icanhazip.com/", "https://checkip.amazonaws.com/",
    ];

    private static readonly TimeSpan PUBLIC_IP_CACHE_TIME = TimeSpan.FromMinutes(10);
    private static readonly object s_lock = new();
    private static string s_publicIp;
    private static DateTime s_publicIpTime;

    /// <summary>
    /// The address to send this player for a game server whose configured address is <paramref name="serverIp"/>.
    /// </summary>
    public static string For(SessionActor session, string serverIp) {
        if (!IPAddress.TryParse(session.RemoteIp ?? "", out var remote)) {
            return serverIp;
        }

        // A player on this computer always uses this computer.
        if (IPAddress.IsLoopback(remote)) {
            return "127.0.0.1";
        }

        // An address set by hand in GameServerIP (anything but this computer) is used for everyone else.
        if (!IsAuto(serverIp)) {
            return serverIp;
        }

        var local = session.LocalIp;
        if (local is null) {
            return serverIp;
        }

        if (IsHomeNetwork(local) && !IsHomeNetwork(remote)) {
            return PublicAddress() ?? local.ToString();
        }

        return local.ToString();
    }

    private static bool IsAuto(string serverIp)
        => string.IsNullOrWhiteSpace(serverIp)
           || serverIp.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)
           || serverIp.Trim().Equals("localhost", StringComparison.OrdinalIgnoreCase)
           || (IPAddress.TryParse(serverIp.Trim(), out var ip) && IPAddress.IsLoopback(ip));

    /// <summary>
    /// The address people on the internet reach this computer at: PublicAddress in [Game Server] when set,
    /// otherwise looked up online (and remembered for a few minutes).
    /// </summary>
    public static string PublicAddress() {
        var configured = ConfigurationManager.Settings["Game Server.PublicAddress"].AsString();
        if (!string.IsNullOrWhiteSpace(configured)) {
            return configured.Trim();
        }

        lock (s_lock) {
            if (s_publicIp is not null && DateTime.UtcNow - s_publicIpTime < PUBLIC_IP_CACHE_TIME) {
                return s_publicIp;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            foreach (var service in PUBLIC_IP_SERVICES) {
                try {
                    var text = http.GetStringAsync(service).GetAwaiter().GetResult().Trim();
                    if (IPAddress.TryParse(text, out _)) {
                        s_publicIp = text;
                        s_publicIpTime = DateTime.UtcNow;
                        Logger.Information("This server's public address is {Ip}.", Logger.Args(text));

                        return s_publicIp;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) {
                    // Try the next service.
                }
            }

            Logger.Warning("Could not find this server's public address. Players on the internet may not get into the game; " +
                           "set PublicAddress under [Game Server] in Imlight.ini.");

            return s_publicIp;
        }
    }

    /// <summary>
    /// True for addresses that only work inside a home network: 10.x, 172.16-31.x, 192.168.x and 169.254.x.
    /// </summary>
    public static bool IsHomeNetwork(IPAddress address) {
        if (address.IsIPv4MappedToIPv6) {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetwork) {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;
        }

        var b = address.GetAddressBytes();

        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

}
