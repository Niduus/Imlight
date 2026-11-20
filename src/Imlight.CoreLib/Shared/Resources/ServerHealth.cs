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
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using Imlight.Common;

namespace Imlight.CoreLib.Shared.Resources;

/// <summary>
/// Writes how busy the server and the PC are to the log: every 5 s for the first 5 minutes after a start, later only
/// when the server is busy. Logins and zone loads took up to a minute right after a start (Niduus's log of
/// 2026-10-26), and these lines show whether CPU, memory (the PC's or the server's) or garbage collection is the cause.
/// </summary>
public static class ServerHealth {

    private static readonly TimeSpan s_interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_startPhase = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_quietInterval = TimeSpan.FromSeconds(60);

    private static Timer s_timer;
    private static readonly Stopwatch s_sinceStart = Stopwatch.StartNew();
    private static readonly Stopwatch s_sinceSample = Stopwatch.StartNew();
    private static TimeSpan s_lastCpu;
    private static int s_lastGen0, s_lastGen1, s_lastGen2;
    private static TimeSpan s_lastQuietLog = TimeSpan.Zero;

    // The speed line (Niduus, 2026-11-20): every 10 minutes, the server's average CPU and how long path finding takes, with
    // how the server was built ("patchable" for Live patches, else "fast"), so both builds can be compared on one PC.
    private static readonly TimeSpan s_speedInterval = TimeSpan.FromMinutes(10);
    private static readonly Stopwatch s_sinceSpeed = Stopwatch.StartNew();
    private static TimeSpan s_speedCpu;
    private static long s_routes;
    private static long s_routeTicks;

    /// <summary>One path search and how long it took (Stopwatch ticks).</summary>
    internal static void CountRoute(long ticks) {
        Interlocked.Increment(ref s_routes);
        Interlocked.Add(ref s_routeTicks, ticks);
    }

    private static void SpeedLine(Process process) {
        if (s_sinceSpeed.Elapsed < s_speedInterval) {
            return;
        }

        var cpu = process.TotalProcessorTime;
        var cpuPercent = (cpu - s_speedCpu).TotalMilliseconds / (s_sinceSpeed.Elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100;
        s_speedCpu = cpu;
        s_sinceSpeed.Restart();
        var routes = Interlocked.Exchange(ref s_routes, 0);
        var ticks = Interlocked.Exchange(ref s_routeTicks, 0);
        var build = typeof(ServerHealth).Assembly.GetCustomAttribute<DebuggableAttribute>() is { IsJITOptimizerDisabled: true } ? "patchable (Live patches)" : "fast";
        Logger.Information("Speed: server built {0}; CPU {1:0.0}% of {2} cores over the last 10 minutes; {3} path searches, {4:0.00} ms each on average.",
            Logger.Args(build, cpuPercent, Environment.ProcessorCount, routes, routes == 0 ? 0 : ticks * 1000.0 / Stopwatch.Frequency / routes));
    }

    public static void Start() {
        if (s_timer is not null) {
            return;
        }

        s_lastCpu = Process.GetCurrentProcess().TotalProcessorTime;
        s_speedCpu = s_lastCpu;
        s_timer = new Timer(_ => Sample(), null, s_interval, s_interval);
    }

    private static void Sample() {
        try {
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var elapsed = s_sinceSample.Elapsed;
            s_sinceSample.Restart();
            var cpuPercent = elapsed.TotalMilliseconds <= 0 ? 0
                : (cpu - s_lastCpu).TotalMilliseconds / (elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100;
            s_lastCpu = cpu;

            int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
            var gc = GC.GetGCMemoryInfo();
            var pcLoad = gc.TotalAvailableMemoryBytes <= 0 ? 0 : gc.MemoryLoadBytes * 100.0 / gc.TotalAvailableMemoryBytes;
            ThreadPool.GetAvailableThreads(out var freeWorkers, out _);
            ThreadPool.GetMaxThreads(out var maxWorkers, out _);

            var inStart = s_sinceStart.Elapsed < s_startPhase;
            var busy = cpuPercent >= 70 || pcLoad >= 90 || gen2 - s_lastGen2 >= 2;
            var quietDue = s_sinceStart.Elapsed - s_lastQuietLog >= s_quietInterval;
            if (inStart || (busy && quietDue)) {
                s_lastQuietLog = s_sinceStart.Elapsed;
                Logger.Information(
                    "Health: server CPU {0:0}% of {1} cores, server memory {2} MB (game data {3} MB), PC memory {4:0}% used of {5} MB, "
                    + "clean-ups {6}/{7}/{8} (pause {9:0.0}%), pool threads busy {10}, waiting work {11}; after the last clean-up "
                    + "{12} MB in use, {13} MB of gaps, {14} MB set aside.",
                    Logger.Args(cpuPercent, Environment.ProcessorCount, process.WorkingSet64 / 1048576,
                        GC.GetTotalMemory(false) / 1048576, pcLoad, gc.TotalAvailableMemoryBytes / 1048576,
                        gen0 - s_lastGen0, gen1 - s_lastGen1, gen2 - s_lastGen2, gc.PauseTimePercentage,
                        maxWorkers - freeWorkers, ThreadPool.PendingWorkItemCount,
                        gc.HeapSizeBytes / 1048576, gc.FragmentedBytes / 1048576, gc.TotalCommittedBytes / 1048576));
            }

            SpeedLine(process);
            s_lastGen0 = gen0;
            s_lastGen1 = gen1;
            s_lastGen2 = gen2;
        }
        catch (Exception ex) {
            Logger.Warning("Health: could not read the server's numbers: {0}", Logger.Args(ex.Message));
        }
    }

}
