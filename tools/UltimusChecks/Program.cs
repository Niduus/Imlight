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
using System.Reflection;

// Ultimus's replay pack, run outside the game (the build check on every pull request, and a thread before it merges):
// each bug fixed from his logs is a case here, so a later change that brings one back fails before it is released.
// It runs the same checks his self-test runs on Niduus's PC after an update. Exit code 1 when any case fails.
var temp = Path.Combine(Path.GetTempPath(), "ultimus-checks");
Directory.CreateDirectory(temp);
var ini = Path.Combine(temp, "Imlight.ini");
File.WriteAllText(ini, "[Logging]\nLogLevel=WARNING\n");
Environment.SetEnvironmentVariable("IMLIGHT_LOG_PATH", Path.Combine(temp, "log.txt"));
Imlight.Common.ConfigurationManager.Initialize(ini);

var replay = Assembly.Load("Imlight.CoreLib").GetType("Imlight.CoreLib.Game.Ultimus.UltimusReplay", throwOnError: true);
var cases = (System.Collections.IEnumerable) replay.GetField("s_cases", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
var count = 0;
foreach (var c in cases) {
    count++;
    Console.WriteLine("case  " + c.GetType().GetProperty("Bug").GetValue(c));
}

var all = (List<string>) replay.GetMethod("Run", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
// A check that reads the game's own files (Root.wad and the rest) can't run where there are none, as in the build check:
// it is said as not run, never counted as passed. Niduus's PC runs it in Ultimus's self-test after every update.
var needsGame = all.FindAll(f => f.Contains("LocalWadCache", StringComparison.Ordinal) || f.Contains("GameData", StringComparison.Ordinal));
var failed = all.FindAll(f => !needsGame.Contains(f));
Console.WriteLine();
Console.WriteLine($"Ultimus replay pack: {count} bug case(s) plus the chat and AI safety checks; {failed.Count} failed, {needsGame.Count} not run here (they need the game's files).");
foreach (var f in needsGame) {
    Console.WriteLine("NOT RUN (needs game files)  " + f);
}

foreach (var f in failed) {
    Console.WriteLine("FAIL  " + f);
}

return failed.Count == 0 ? 0 : 1;
