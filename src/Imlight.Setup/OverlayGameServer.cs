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
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Imlight.Setup;

/// <summary>
/// The server a running game is really playing on, read from Windows' own list of the game's open connections (nothing
/// in the game is touched or read). The overlay links to that same address and port, the one the game plays on, so
/// every player who can play gets their own minimap and Quest Tracker with no other port to open, however the game was
/// started (Niduus, 2026-11-16: "They should be player sided").
/// </summary>
internal static class OverlayGameServer {

    // Web and patch traffic, never the game server.
    private static readonly HashSet<int> s_notGame = [80, 443, 8080];

    /// <summary>
    /// The server address and port the game processes <paramref name="gameIds"/> are connected to, or null when none is
    /// (still loading). When they are connected to more than one, the one linked now (<paramref name="linked"/>) wins,
    /// then one at the linked server's address (from its login port on to its game port), then the busiest.
    /// </summary>
    internal static IPEndPoint? ServerOf(IReadOnlyCollection<uint> gameIds, Func<IPEndPoint, bool> linked, Func<IPAddress, bool> sameServer) {
        if (gameIds.Count == 0 || !OperatingSystem.IsWindows()) {
            return null;
        }

        var found = new Dictionary<IPEndPoint, int>();
        foreach (var (pid, remote, port) in Connections()) {
            if (gameIds.Contains(pid) && !s_notGame.Contains(port) && !remote.Equals(IPAddress.Any)) {
                var end = new IPEndPoint(remote, port);
                found[end] = found.GetValueOrDefault(end) + 1;
            }
        }

        if (found.Count == 0) {
            return null;
        }

        return found.OrderByDescending(f => linked(f.Key)).ThenByDescending(f => sameServer(f.Key.Address)).ThenByDescending(f => f.Value).First().Key;
    }

    /// <summary>The same server: equal, or both this PC (127.0.0.1 and this PC's own addresses).</summary>
    internal static bool Same(IPAddress a, IPAddress b) => a.Equals(b) || (IsThisPc(a) && IsThisPc(b));

    internal static bool IsThisPc(IPAddress address) {
        if (IPAddress.IsLoopback(address)) {
            return true;
        }

        try {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Any(u => (u.Address.IsIPv4MappedToIPv6 ? u.Address.MapToIPv4() : u.Address).Equals(address));
        }
        catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException) {
            return false;
        }
    }

    // ---- Windows' TCP table (IPv4: the game connects over IPv4) ----

    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_CONNECTIONS = 4;
    private const uint MIB_TCP_STATE_ESTAB = 5;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid {
        internal uint State;
        internal uint LocalAddr;
        internal uint LocalPort;
        internal uint RemoteAddr;
        internal uint RemotePort;
        internal uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    private static List<(uint Pid, IPAddress Remote, int Port)> Connections() {
        var list = new List<(uint, IPAddress, int)>();
        var size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
        for (var tries = 0; tries < 3 && result == ERROR_INSUFFICIENT_BUFFER; tries++) {
            var buffer = Marshal.AllocHGlobal(size);
            try {
                result = GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
                if (result != 0) {
                    continue;
                }

                var count = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
                for (var i = 0; i < count; i++) {
                    var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(buffer + 4 + i * rowSize);
                    if (row.State != MIB_TCP_STATE_ESTAB) {
                        continue;
                    }

                    // Ports are in network order in the low two bytes.
                    var port = (int) (((row.RemotePort & 0xFF) << 8) | ((row.RemotePort >> 8) & 0xFF));
                    list.Add((row.OwningPid, new IPAddress(row.RemoteAddr), port));
                }
            }
            finally {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return list;
    }
}
