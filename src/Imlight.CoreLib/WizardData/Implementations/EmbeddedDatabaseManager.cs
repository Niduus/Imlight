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
using System.IO;
using System.Linq;
using System.Net.Sockets;
using Raven.Client.Documents;
using Raven.Client.Documents.Conventions;
using Raven.Client.ServerWide;
using Raven.Embedded;
using Imlight.Common;

namespace Imlight.CoreLib.WizardData.Implementations;

public static class EmbeddedDatabaseManager {

    public static bool IsRunning { get; private set; }

    private static readonly string s_embeddedDatabasePath 
        = ConfigurationManager.Settings["Database.EmbeddedDatabaseDataDirectory"];
    private static readonly bool s_embeddedDatabaseUseFull 
        = ConfigurationManager.Settings["Database.EmbeddedDatabaseUseFull"].AsBool();
    private static readonly string s_embeddedDatabaseFullPath 
        = ConfigurationManager.Settings["Database.EmbeddedDatabaseFullPath"];
    private static readonly ushort s_embeddedDatabasePort 
        = ConfigurationManager.Settings["Database.EmbeddedDatabasePort"].AsUShort();
    private static readonly long s_embeddedDatabaseTimeoutTime 
        = ConfigurationManager.Settings["Database.EmbeddedDatabaseTimeoutTime"].AsLong();
    private static readonly string s_embeddedDatabaseDotNetPath
        = ConfigurationManager.Settings["Database.EmbeddedDatabaseDotNetPath"].AsString();

    /// <summary>The .NET version the database is built for; older 10.0 versions are used when that is all there is.</summary>
    private static readonly Version s_wantedRuntime = new(10, 0, 8);

    public static void Start() {
        try {
            if (IsRunning) {
                return;
            }

            Logger.Information("Initializing embedded RavenDB database for the first time..");
            Logger.Information("Database data directory: {0}", Logger.Args(s_embeddedDatabasePath));

            // Configure the embedded server.
            var serverOptions = new ServerOptions {
                DataDirectory = s_embeddedDatabasePath,
                ServerUrl = $"http://127.0.0.1:{s_embeddedDatabasePort}",
                MaxServerStartupTimeDuration = TimeSpan.FromSeconds(s_embeddedDatabaseTimeoutTime),
                CommandLineArgs = ["--Databases.MaxIdleTimeInSec=-1"]
            };

            // The database runs on .NET 10 with ASP.NET Core. The app may have installed .NET just for Imlight,
            // where "dotnet" alone does not find it, so point the database at a dotnet that has it.
            if (FindDotNet(out var dotnet, out var runtime)) {
                serverOptions.DotNetPath = dotnet;
                if (runtime < s_wantedRuntime) {
                    serverOptions.FrameworkVersion = runtime.ToString();
                }

                Logger.Information("The player database runs on .NET {Version} from {Path}.", Logger.Args(runtime, dotnet));
            }
            else {
                Logger.Error("No .NET 10 with ASP.NET Core was found for the player database, so the server cannot start. " +
                             "Click Update Imlight on the Tools tab of ImlightManager, or install the .NET 10 SDK.");
            }

            if (IsPortTaken(s_embeddedDatabasePort)) {
                Logger.Warning("Another program already uses port {Port}, which the player database needs. If the server " +
                               "does not start, close that program or change EmbeddedDatabasePort under [Database] in Imlight.ini.",
                    Logger.Args(s_embeddedDatabasePort));
            }

            // If we're using the full database, we need to set the server directory.
            // Otherwise, we're using the embedded database.
            if (s_embeddedDatabaseUseFull) {
                serverOptions.ServerDirectory = s_embeddedDatabaseFullPath;

                Logger.Information("Using full RavenDB server at {0}", 
                    Logger.Args(s_embeddedDatabaseFullPath));
            }

            EmbeddedServer.Instance.StartServer(serverOptions);
            IsRunning = true;

            EmbeddedServer.Instance.ServerProcessExited += (sender, args) => {
                Logger.Error("Embedded database process exited unexpectedly. Restarting..");
                
                try {
                    EmbeddedServer.Instance.RestartServerAsync().Wait();
                }
                catch (Exception ex) {
                    Logger.Error("Failed to restart embedded database: {0}", Logger.Args(ex.Message));
                }

                Logger.Information("Embedded database restarted.");
            };

            Logger.Information("Embedded database initialized.");
        }
        catch (Exception ex) {
            Logger.Error("Failed to initialize embedded database: {0}", Logger.Args(ex.Message));
        }
    }

    /// <summary>
    /// Finds a dotnet with .NET 10 and ASP.NET Core 10: EmbeddedDatabaseDotNetPath when set, the copy ImlightManager
    /// installs next to the server, DOTNET_ROOT, the usual install folder, then PATH. The newest version wins.
    /// </summary>
    private static bool FindDotNet(out string dotnet, out Version runtime) {
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(s_embeddedDatabaseDotNetPath)) {
            candidates.Add(s_embeddedDatabaseDotNetPath.Trim());
        }

        candidates.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "dotnet", exe)));
        foreach (var root in new[] { Environment.GetEnvironmentVariable("DOTNET_ROOT"), Environment.GetEnvironmentVariable("DOTNET_ROOT(x86)") }) {
            if (!string.IsNullOrWhiteSpace(root)) {
                candidates.Add(Path.Combine(root, exe));
            }
        }

        candidates.Add(OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", exe)
            : "/usr/share/dotnet/dotnet");
        candidates.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim('"'), exe)));

        dotnet = null;
        runtime = null;
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase)) {
            try {
                if (!File.Exists(candidate)) {
                    continue;
                }

                // Follow a link (such as /usr/bin/dotnet) to the folder that holds "shared".
                var target = new FileInfo(candidate).ResolveLinkTarget(true)?.FullName ?? candidate;
                var folder = Path.GetDirectoryName(target)!;
                var best = Versions(folder, "Microsoft.NETCore.App").Intersect(Versions(folder, "Microsoft.AspNetCore.App"))
                    .Where(v => v.Major == 10)
                    .DefaultIfEmpty()
                    .Max();
                if (best is not null && (runtime is null || best > runtime)) {
                    dotnet = candidate;
                    runtime = best;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) {
                // Not usable; try the next one.
            }
        }

        return dotnet is not null;
    }

    private static IEnumerable<Version> Versions(string dotnetFolder, string framework) {
        var folder = Path.Combine(dotnetFolder, "shared", framework);

        return Directory.Exists(folder)
            ? Directory.GetDirectories(folder)
                .Select(Path.GetFileName)
                .Select(name => Version.TryParse(name, out var v) ? v : null)
                .Where(v => v is not null)
            : [];
    }

    private static bool IsPortTaken(int port) {
        try {
            using var client = new TcpClient();

            return client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromMilliseconds(500)) && client.Connected;
        }
        catch (AggregateException) {
            return false;
        }
    }

    public static IDocumentStore GetDocumentStore(string databaseName) {
        if (!IsRunning) {
            return null;
        }

        var databaseOptions = new DatabaseOptions(new DatabaseRecord { DatabaseName = databaseName }) {
            Conventions = RavenSystemTextJsonBridge.Apply(new DocumentConventions())
        };
        var docStore = EmbeddedServer.Instance.GetDocumentStore(databaseOptions);
        
        return docStore;
    }

}
