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
 *
 */

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Imlight.Common;

namespace Imlight.CoreLib.Game;

/// <summary>
/// Loads the bots and swaps them for a newer build without a server restart (Niduus, 2026-11-19: "have them soft reset only
/// themselves"). The bots run in a box of their own (a collectible load context) that is thrown away at a swap.
/// <para>
/// The server's bots folder: Imlight.Bots.dll is the bots built with the server ("built in"); updates\bots-v12\ holds a
/// published bot update (source\ as downloaded, Imlight.Bots.dll as built here, built-for.txt, update.json); active.txt
/// says which runs; state.json is what Ahziael's Bots page shows.
/// </para>
/// </summary>
public static class BotLoader {

    internal const string BUILT_IN = "built in";

    private const string ACTIVE_FILE = "active.txt";
    private const string BUILT_FOR_FILE = "built-for.txt";

    /// <summary>The server's bots folder.</summary>
    internal static string Folder => Path.Combine(AppContext.BaseDirectory, "bots");

    internal static string UpdatesFolder => Path.Combine(Folder, "updates");

    private static readonly SemaphoreSlim s_swap = new(1, 1);
    private static AssemblyLoadContext s_context;

    /// <summary>The last problem with the bots (a build that failed, ...), for Ahziael; empty when none.</summary>
    internal static string Problem { get; private set; } = "";

    /// <summary>Which bots are wanted: "built in" or a bot update's name ("bots-v12").</summary>
    internal static string Wanted {
        get {
            try {
                var path = Path.Combine(Folder, ACTIVE_FILE);

                return File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } text ? text : BUILT_IN;
            }
            catch (IOException) {
                return BUILT_IN;
            }
        }
        private set {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, ACTIVE_FILE), value);
        }
    }

    /// <summary>
    /// The game server's start: puts the bots in (the bot update picked last time when it still builds for this server,
    /// else the built-in ones). Returns false when no bots could be loaded at all; the server then runs without bots.
    /// </summary>
    public static bool LoadAtStart() {
        // A bot update built for other server parts means the server itself was updated since: its own bots run, and the
        // first check (a minute later) swaps back to the bot update only when it is newer than the server's own code.
        var wanted = Wanted;
        if (wanted != BUILT_IN && !Ready(wanted, buildIfNeeded: false)) {
            Logger.Information("Bots: the server was updated since bot update {0} came in; its own bots run until the update check.",
                Logger.Args(wanted));
            Problem = "";
            wanted = BUILT_IN;
        }

        var loaded = TryLoad(wanted, out var side, out var context) || (wanted != BUILT_IN && TryLoad(wanted = BUILT_IN, out side, out context));
        if (!loaded) {
            Logger.Error("Bots: no bots could be loaded ({0}); the server runs without Ultimus, the TCO7, the Quest Team and Bot Players.",
                Logger.Args(Problem));
            BotUpdates.WriteState();

            return false;
        }

        s_context = context;
        BotDoor.Install(side, wanted);
        Logger.Information("Bots: {0} are in.", Logger.Args(Label(wanted)));
        BotUpdates.WriteState();
        BotUpdates.Start();

        return true;
    }

    /// <summary>
    /// This program was started as a bots' mini server (--bots, --questteam or --ultimus): loads the bots the game server
    /// runs (never builds; the game server built them already) and runs it. False when the mode is not the bots'.
    /// </summary>
    public static bool RunMiniServer(string[] args) {
        if (!args.Any(a => a is "--bots" or "--questteam" or "--ultimus")) {
            return false;
        }

        var wanted = Wanted;
        if (wanted != BUILT_IN && !Ready(wanted, buildIfNeeded: false)) {
            wanted = BUILT_IN;
        }

        if (!TryLoad(wanted, out var side, out _) && (wanted == BUILT_IN || !TryLoad(BUILT_IN, out side, out _))) {
            Logger.Fatal("Bots: the mini server could not load the bots: {0}", Logger.Args(Problem));

            return true;
        }

        side.RunMiniServer(args);

        return true;
    }

    /// <summary>The server is up: the bots' mini servers may be looked after (on while their switches are on).</summary>
    public static void StartWatching() => BotDoor.StartWatching();

    internal static string Label(string version) => version == BUILT_IN ? "the bots built with the server" : $"bot update {version}";

    private static string DllOf(string version) => version == BUILT_IN
        ? Path.Combine(Folder, BotBuilder.ASSEMBLY + ".dll")
        : Path.Combine(UpdatesFolder, version, BotBuilder.ASSEMBLY + ".dll");

    /// <summary>Whether a bot update is built for this server (building it from its source when asked and needed).</summary>
    internal static bool Ready(string version, bool buildIfNeeded) {
        if (version == BUILT_IN) {
            return File.Exists(DllOf(version));
        }

        var folder = Path.Combine(UpdatesFolder, version);
        var key = BotBuilder.ServerKey();
        var builtFor = Path.Combine(folder, BUILT_FOR_FILE);
        try {
            if (File.Exists(DllOf(version)) && File.Exists(builtFor) && File.ReadAllText(builtFor).Trim() == key) {
                return true;
            }

            if (!buildIfNeeded) {
                Problem = $"{version} is not built for this server yet";

                return false;
            }

            var source = Path.Combine(folder, "source");
            if (!Directory.Exists(source)) {
                Problem = $"{version} has no source";

                return false;
            }

            Logger.Information("Bots: building {0} for this server...", Logger.Args(version));
            var started = DateTime.UtcNow;
            if (BotBuilder.Build(source, DllOf(version)) is { } failed) {
                Problem = $"{version} {failed}";
                Logger.Error("Bots: {0}", Logger.Args(Problem));

                return false;
            }

            File.WriteAllText(builtFor, key);
            Logger.Information("Bots: {0} built in {1:0} s.", Logger.Args(version, (DateTime.UtcNow - started).TotalSeconds));

            return true;
        }
        catch (Exception e) {
            Problem = $"{version} could not be built: {e.Message}";
            Logger.Error("Bots: {0}", Logger.Args(e));

            return false;
        }
    }

    // The bots' own box: they share the server's parts (CoreLib, Akka, ...) and nothing else of theirs stays behind.
    private sealed class BotContext(string name) : AssemblyLoadContext(name, isCollectible: true) {

        protected override Assembly Load(AssemblyName assemblyName) => null;

    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryLoad(string version, out BotSide side, out AssemblyLoadContext context) {
        side = null;
        context = null;
        var dll = DllOf(version);
        if (!File.Exists(dll)) {
            Problem = $"{Label(version)} are missing ({dll})";

            return false;
        }

        try {
            context = new BotContext($"bots {version}");
            var pdb = Path.ChangeExtension(dll, ".pdb");
            // Read from memory, so the files stay free to replace while the bots run.
            using var dllStream = new MemoryStream(File.ReadAllBytes(dll));
            using var pdbStream = File.Exists(pdb) ? new MemoryStream(File.ReadAllBytes(pdb)) : null;
            var assembly = context.LoadFromStream(dllStream, pdbStream);
            var type = assembly.GetTypes().FirstOrDefault(t => !t.IsAbstract && t.IsSubclassOf(typeof(BotSide)));
            if (type is null || Activator.CreateInstance(type, nonPublic: true) is not BotSide made) {
                Problem = $"{Label(version)} have no doorway (BotSide)";
                context.Unload();
                context = null;

                return false;
            }

            side = made;

            return true;
        }
        catch (Exception e) {
            Problem = $"{Label(version)} could not be loaded: {(e is ReflectionTypeLoadException r ? r.LoaderExceptions.FirstOrDefault()?.Message : e.Message)}";
            Logger.Error("Bots: {0}", Logger.Args(e));
            try {
                context?.Unload();
            }
            catch (InvalidOperationException) {
                // Not collectible after all; nothing to free.
            }

            context = null;

            return false;
        }
    }

    /// <summary>
    /// Swaps the running bots for <paramref name="version"/>: it is built and loaded first (a failure keeps the bots as they
    /// are), then the bots wait for their battles to end (up to 15 minutes), save and log out, and the new ones come in.
    /// Players and the server stay up. Returns null when done, else why not.
    /// </summary>
    internal static async Task<string> SwapAsync(string version, string why) {
        if (!await s_swap.WaitAsync(TimeSpan.Zero)) {
            return "a bot swap is already under way";
        }

        try {
            if (version == BotDoor.Version && BotDoor.Side is not null) {
                return null;
            }

            if (!Ready(version, buildIfNeeded: true) || !TryLoad(version, out var side, out var context)) {
                BotUpdates.WriteState();

                return Problem;
            }

            // No fight is left half done: the swap waits while a bot is in a battle, up to 15 minutes.
            var waitUntil = DateTime.UtcNow.AddMinutes(15);
            while (BotDoor.Side?.BusyWhy() is { } busy && DateTime.UtcNow < waitUntil) {
                BotUpdates.Note($"waiting to swap the bots: {busy}");
                await Task.Delay(TimeSpan.FromSeconds(10));
            }

            Logger.Information("Bots: swapping to {0} ({1}); the bots log out and come back, players stay in.", Logger.Args(Label(version), why));
            var old = BotDoor.Side;
            if (old is not null) {
                try {
                    await old.StopAsync($"bot update ({Label(version)})", TimeSpan.FromSeconds(90));
                }
                catch (Exception e) {
                    Logger.Error("Bots: the old bots did not stop cleanly: {0}", Logger.Args(e));
                }
            }

            BotDoor.Remove();
            var oldContext = s_context;
            Wanted = version;
            s_context = context;
            BotDoor.Install(side, version);
            Problem = "";
            Logger.Information("Bots: {0} are in.", Logger.Args(Label(version)));
            BotUpdates.Note($"now running {Label(version)}");
            BotUpdates.WriteState();
            try {
                oldContext?.Unload();
            }
            catch (InvalidOperationException) {
                // Not collectible: it stays loaded, unused.
            }

            return null;
        }
        catch (Exception e) {
            Problem = $"the swap to {version} failed: {e.Message}";
            Logger.Error("Bots: {0}", Logger.Args(e));
            BotUpdates.WriteState();

            return Problem;
        }
        finally {
            s_swap.Release();
        }
    }

}
