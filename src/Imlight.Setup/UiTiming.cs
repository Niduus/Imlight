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

namespace Imlight.Setup;

/// <summary>
/// A light timing log of the Manager's slow spots (building a tab, redrawing a list), for finding what makes the
/// window stutter. Written to the debug output, and to the console when IMLIGHT_UI_TIMING=1 is set.
/// </summary>
internal static class UiTiming {

    private static readonly bool s_console = Environment.GetEnvironmentVariable("IMLIGHT_UI_TIMING") == "1";

    internal static T Measure<T>(string what, Func<T> work) {
        var clock = Stopwatch.StartNew();
        var result = work();
        Log(what, clock.Elapsed);

        return result;
    }

    internal static void Measure(string what, Action work) {
        var clock = Stopwatch.StartNew();
        work();
        Log(what, clock.Elapsed);
    }

    private static void Log(string what, TimeSpan took) {
        var line = $"[ui] {what}: {took.TotalMilliseconds:F1} ms";
        Debug.WriteLine(line);
        if (s_console) {
            Console.Error.WriteLine(line);
        }
    }

}
