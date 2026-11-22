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
using System.Diagnostics;
using System.Threading.Tasks;

namespace Imlight.Setup;

/// <summary>
/// Runs the tools setup relies on, sending their output to the setup log.
/// </summary>
internal static class ProcessRunner {

    /// <summary>
    /// Runs a program to the end and returns its exit code. Its output goes to the log; with
    /// <paramref name="showProgress"/>, a dot is printed now and then so it does not look stuck.
    /// <paramref name="workingDirectory"/>, when given, is the folder it runs in.
    /// </summary>
    internal static async Task<int> RunAsync(string fileName, IEnumerable<string> arguments, bool showProgress = false,
        string? workingDirectory = null, bool lowPriority = false) {
        var startInfo = CreateStartInfo(fileName, arguments);
        if (workingDirectory is not null) {
            startInfo.WorkingDirectory = workingDirectory;
        }

        SetupLog.Detail($"> {fileName} {string.Join(' ', startInfo.ArgumentList)}");

        using var process = new Process { StartInfo = startInfo };
        var lines = 0;
        void OnLine(string? line) {
            if (line is null) {
                return;
            }

            SetupLog.Detail(line);
            if (showProgress && ++lines % 10 == 0) {
                Console.Write('.');
            }
        }

        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        process.Start();
        if (lowPriority) {
            // A build in the background gives way to the game and the server (its helper processes take this on).
            try {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException) {
                // Ended already, or not allowed here: it builds at the usual speed.
            }
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        if (showProgress) {
            Console.WriteLine();
        }

        SetupLog.Detail($"Exit code {process.ExitCode}");

        return process.ExitCode;
    }

    /// <summary>
    /// Runs a program and returns what it printed. Throws if it cannot be started.
    /// </summary>
    internal static string Capture(string fileName, IEnumerable<string> arguments) {
        using var process = Process.Start(CreateStartInfo(fileName, arguments))!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        return output;
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, IEnumerable<string> arguments) {
        var startInfo = new ProcessStartInfo(fileName) {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";

        return startInfo;
    }

}
