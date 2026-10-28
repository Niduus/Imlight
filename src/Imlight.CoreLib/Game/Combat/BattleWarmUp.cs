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
using System.Diagnostics;
using System.Threading;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Combat;

/// <summary>
/// Reads what the first battle after a server start needs (the boss cheat files, the spell lookups) on a low-priority
/// thread soon after the start. Read by the first battle itself, it took 6 to 9 seconds, and running into a monster
/// did nothing all that time (Niduus, 2026-10-28: "a duel should happen when running close near a mob and/or into
/// them. Sometimes, this is not the case").
/// </summary>
public static class BattleWarmUp {

    private static readonly TimeSpan s_delay = TimeSpan.FromSeconds(10);

    public static void Start() {
        var thread = new Thread(Run) {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "Battle warm-up",
        };
        thread.Start();
    }

    private static void Run() {
        Thread.Sleep(s_delay);
        var watch = Stopwatch.StartNew();
        try {
            var bosses = BossCheats.WarmUp();
            CombatResolver.WarmUp();
            Logger.Information("Battle warm-up: boss cheats ({0} bosses) and spell lookups ready in {1} ms.",
                Logger.Args(bosses, watch.ElapsedMilliseconds));
        }
        catch (Exception e) {
            Logger.Warning("Battle warm-up failed; the first battle reads it instead: {0}", Logger.Args(e.Message));
        }
    }

}
