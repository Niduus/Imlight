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
using System.Text;
using System.Threading;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Tools;

/// <summary>
/// Writes where each wizard on the server is standing to logs/positions.txt, for the Position card in
/// ImlightManager. It only reads what the game already sends the server when a wizard moves.
/// </summary>
internal static class PlayerPositions {

    private sealed record Position(string Wizard, string Zone, float X, float Y, float Z, float Facing, DateTime Updated, bool OnThisPc);

    private static readonly ConcurrentDictionary<ulong, Position> s_positions = new();
    private static readonly TimeSpan s_forgetAfter = TimeSpan.FromMinutes(10);
    private static Timer s_timer;
    private static int s_dirty;

    /// <summary>
    /// Records a wizard's position. The file is written at most twice a second.
    /// </summary>
    internal static void Update(ulong charId, string wizard, string zone, float x, float y, float z, float facing, bool onThisPc) {
        s_positions[charId] = new Position(wizard ?? "", zone ?? "", x, y, z, facing, DateTime.Now, onThisPc);
        Interlocked.Exchange(ref s_dirty, 1);
        if (s_timer is null) {
            Interlocked.CompareExchange(ref s_timer, new Timer(_ => Write(), null, 500, 500), null);
        }
    }

    private static void Write() {
        if (Interlocked.Exchange(ref s_dirty, 0) == 0) {
            return;
        }

        try {
            var now = DateTime.Now;
            foreach (var (charId, position) in s_positions) {
                if (now - position.Updated > s_forgetAfter) {
                    s_positions.TryRemove(charId, out _);
                }
            }

            // One wizard per line, tab separated: name, zone, X, Y, Z, facing (radians), local time of the last move,
            // and "local" when that wizard's game runs on this PC (else "remote"), so Use my position can pick the host's wizard.
            var text = new StringBuilder();
            foreach (var p in s_positions.Values.OrderByDescending(p => p.Updated)) {
                text.Append(p.Wizard).Append('\t').Append(p.Zone).Append('\t')
                    .Append(p.X.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append('\t')
                    .Append(p.Y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append('\t')
                    .Append(p.Z.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append('\t')
                    .Append(p.Facing.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append('\t')
                    .Append(p.Updated.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)).Append('\t')
                    .Append(p.OnThisPc ? "local" : "remote").Append('\n');
            }

            var folder = Path.Combine(Directory.GetCurrentDirectory(), "logs");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "positions.txt");
            var temp = path + ".tmp";
            File.WriteAllText(temp, text.ToString());
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) {
            Logger.Debug("Could not write player positions: {0}", Logger.Args(ex.Message));
        }
    }

}
