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
 * ========================================================================
 * PLUGIN LOADER
 * ========================================================================
 *
 * PURPOSE:
 * Compiles and runs the plugins in Server/Plugins (see Imlight.CoreLib.Plugins.PluginApi). A plugin is a folder with
 * a plugin.json and C# files under server/; plugins-on.txt lists the ones turned on. Each one is compiled on its own
 * as "Imlight.Plugin" (the name the CoreLib shows its internals to) into an unloadable context, so turning plugins on
 * and off, updating and removing them needs no restart: any change to the folder reloads them all.
 *
 * NOTE:
 * Each plugin's outcome goes to status.json in its folder for Ahziael's Plugins page: on, off or a problem with the
 * compile errors. "--check-plugins [folder]" compiles them without starting the server and prints the errors.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using Imlight.Common;
using Imlight.CoreLib.Game.Commands;
using Imlight.CoreLib.Plugins;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Imlight.Director;

internal static class PluginLoader {

    internal const string FOLDER = "Plugins";
    internal const string SWITCHES_FILE = "plugins-on.txt";
    internal const string MANIFEST_FILE = "plugin.json";
    internal const string STATUS_FILE = "status.json";
    internal const string CODE_FOLDER = "server";

    // CoreLib lets this assembly name see its internals.
    private const string AssemblyName = "Imlight.Plugin";

    private sealed record Loaded(string Id, PluginContext Context, AssemblyLoadContext Assemblies, List<IImlightPlugin> Plugins);

    private static readonly object s_lock = new();
    private static readonly List<Loaded> s_loaded = [];
    private static List<MetadataReference> s_references;
    private static FileSystemWatcher s_watcher;
    private static Timer s_reloadTimer;
    private static Timer s_secondTimer;

    internal static string Folder => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FOLDER);

    /// <summary>Loads the plugins that are on and reloads them all whenever the Plugins folder changes.</summary>
    internal static void Start() {
        Directory.CreateDirectory(Folder);
        Reload();

        s_reloadTimer = new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
        s_watcher = new FileSystemWatcher(Folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
        FileSystemEventHandler changed = (_, e) => {
            // The server writes status.json itself, and settings are read live, so neither needs a reload.
            var name = Path.GetFileName(e.FullPath);
            if (name.Equals(STATUS_FILE, StringComparison.OrdinalIgnoreCase) || name.Equals(PluginSettings.FILE_NAME, StringComparison.OrdinalIgnoreCase)) {
                return;
            }

            // Wait for the copy or unzip to finish.
            s_reloadTimer.Change(1500, Timeout.Infinite);
        };
        s_watcher.Changed += changed;
        s_watcher.Created += changed;
        s_watcher.Deleted += changed;
        s_watcher.Renamed += (s, e) => changed(s, e);
        s_watcher.EnableRaisingEvents = true;

        s_secondTimer = new Timer(_ => {
            if (PluginEvents.EverySecond.Heard) {
                PluginEvents.EverySecond.Raise(default);
            }
        }, null, 1000, 1000);
    }

    /// <summary>The ids of the plugins turned on (plugins-on.txt, one per line).</summary>
    internal static HashSet<string> SwitchedOn(string folder) {
        var path = Path.Combine(folder, SWITCHES_FILE);
        try {
            return File.Exists(path)
                ? [.. File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && l[0] != '#')]
                : [];
        }
        catch (IOException) {
            return [];
        }
    }

    private static void Reload() {
        lock (s_lock) {
            UnloadAll();

            string[] folders;
            try {
                folders = Directory.Exists(Folder) ? Directory.GetDirectories(Folder) : [];
            }
            catch (Exception ex) {
                Logger.Error("Plugins: the Plugins folder could not be read. {Message}", Logger.Args(ex.Message));
                return;
            }

            var on = new HashSet<string>(SwitchedOn(Folder), StringComparer.OrdinalIgnoreCase);
            foreach (var folder in folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)) {
                if (!File.Exists(Path.Combine(folder, MANIFEST_FILE))) {
                    continue;
                }

                var id = Path.GetFileName(folder);
                var manifest = ReadManifest(folder);
                if (!on.Contains(id)) {
                    WriteStatus(folder, "off", manifest.Version, "Turned off.", []);
                    continue;
                }

                Load(id, folder, manifest);
            }

            // Every plugin was just made new, so each hears it once; the server takes connections before Start.
            PluginEvents.ServerReady.Raise(default);
        }
    }

    private static void Load(string id, string folder, Manifest manifest) {
        var (image, errors) = Compile(folder);
        if (image is null) {
            Logger.Error("Plugin {0} could not be compiled: {1}", Logger.Args(manifest.Name, string.Join(" | ", errors.Take(5))));
            WriteStatus(folder, "problem", manifest.Version, "Its code does not fit this version of the server.", errors);
            return;
        }

        var context = new PluginContext(id, manifest.Name, folder, manifest.Defaults);
        var assemblies = new AssemblyLoadContext($"Plugin {id}", isCollectible: true);
        var loaded = new Loaded(id, context, assemblies, []);
        try {
            var assembly = assemblies.LoadFromStream(new MemoryStream(image));
            var types = assembly.GetTypes().Where(t => !t.IsAbstract && !t.IsInterface).ToArray();

            foreach (var type in types.Where(t => t.IsSubclassOf(typeof(CommandProtocol)) && t.GetConstructor(Type.EmptyTypes) is not null)) {
                context.AddCommands((CommandProtocol) Activator.CreateInstance(type)!);
            }

            foreach (var type in types.Where(t => typeof(IImlightPlugin).IsAssignableFrom(t))) {
                var plugin = (IImlightPlugin) Activator.CreateInstance(type)!;
                plugin.Load(context);
                loaded.Plugins.Add(plugin);
            }

            s_loaded.Add(loaded);

            Logger.Information("Plugin {0} {1} is on.", Logger.Args(manifest.Name, manifest.Version));
            WriteStatus(folder, "on", manifest.Version, "On.", []);
        }
        catch (Exception ex) {
            var inner = ex is TargetInvocationException { InnerException: not null } ? ex.InnerException : ex;
            Logger.Error("Plugin {0} failed to start: {1}", Logger.Args(manifest.Name, inner.ToString()));
            Unload(loaded);
            WriteStatus(folder, "problem", manifest.Version, $"It stopped while starting: {inner.Message}", [inner.ToString()]);
        }
    }

    private static void UnloadAll() {
        foreach (var loaded in s_loaded) {
            Unload(loaded);
        }

        s_loaded.Clear();
    }

    private static void Unload(Loaded loaded) {
        foreach (var plugin in loaded.Plugins) {
            try {
                plugin.Unload();
            }
            catch (Exception ex) {
                loaded.Context.Error("Its Unload failed.", ex);
            }
        }

        loaded.Context.UndoAll();
        loaded.Assemblies.Unload();
    }

    /// <summary>Compiles a plugin's server/*.cs; returns the assembly or the errors.</summary>
    internal static (byte[] Image, string[] Errors) Compile(string folder) {
        var code = Path.Combine(folder, CODE_FOLDER);
        // A plugin may bring no code at all (only settings or data); an empty assembly is fine.
        var files = Directory.Exists(code) ? Directory.GetFiles(code, "*.cs", SearchOption.AllDirectories) : [];

        var parse = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), parse, path: Path.GetRelativePath(folder, f))).ToList();
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Disable)
            .WithMetadataImportOptions(MetadataImportOptions.All);
        var compilation = CSharpCompilation.Create(AssemblyName, trees, References(), options);

        using var stream = new MemoryStream();
        var emitted = compilation.Emit(stream);
        if (!emitted.Success) {
            var errors = emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => {
                    var span = d.Location.GetLineSpan();
                    return $"{span.Path} line {span.StartLinePosition.Line + 1}: {d.Id} {d.GetMessage()}";
                }).ToArray();

            return (null, errors);
        }

        return (stream.ToArray(), []);
    }

    private static List<MetadataReference> References() {
        if (s_references is not null) {
            return s_references;
        }

        // Everything the server is built from, loaded now so a plugin can use any of it.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<Assembly>([Assembly.GetExecutingAssembly()]);
        while (queue.Count > 0) {
            var assembly = queue.Dequeue();
            foreach (var name in assembly.GetReferencedAssemblies()) {
                if (seen.Add(name.FullName)) {
                    try {
                        queue.Enqueue(Assembly.Load(name));
                    }
                    catch (Exception) {
                        // Not shipped with the server (a part only some platforms use).
                    }
                }
            }
        }

        s_references = [.. AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .GroupBy(a => a.GetName().Name)
            .Select(g => (MetadataReference) MetadataReference.CreateFromFile(g.First().Location))];

        return s_references;
    }

    private static void WriteStatus(string folder, string state, string version, string message, IEnumerable<string> errors) {
        try {
            var json = JsonSerializer.Serialize(new { state, version, message, errors = errors.Take(40).ToArray(), at = DateTime.Now },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(folder, STATUS_FILE), json);
        }
        catch (Exception ex) {
            Logger.Warning("Plugins: the status of {0} could not be written. {1}", Logger.Args(Path.GetFileName(folder), ex.Message));
        }
    }

    private sealed record Manifest(string Name, string Version, Dictionary<string, string> Defaults);

    private static Manifest ReadManifest(string folder) {
        var id = Path.GetFileName(folder);
        try {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, MANIFEST_FILE)),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            string Value(string key) => document.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (document.RootElement.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Array) {
                foreach (var setting in settings.EnumerateArray()) {
                    if (setting.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String
                        && setting.TryGetProperty("default", out var value)) {
                        defaults[key.GetString()!] = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString().ToLowerInvariant();
                    }
                }
            }

            return new Manifest(Value("name") ?? id, Value("version") ?? "", defaults);
        }
        catch (Exception) {
            return new Manifest(id, "", []);
        }
    }

    /// <summary>"--check-plugins [folder]": compiles every plugin in the folder (default Plugins) and prints the errors.</summary>
    internal static int Check(string folder) {
        folder ??= Folder;
        var failed = 0;
        foreach (var plugin in Directory.Exists(folder) ? Directory.GetDirectories(folder) : []) {
            if (!File.Exists(Path.Combine(plugin, MANIFEST_FILE))) {
                continue;
            }

            var (image, errors) = Compile(plugin);
            Console.WriteLine(image is null ? $"{Path.GetFileName(plugin)}: {errors.Length} error(s)" : $"{Path.GetFileName(plugin)}: compiles");
            foreach (var error in errors) {
                Console.WriteLine("  " + error);
            }

            failed += image is null ? 1 : 0;
        }

        return failed;
    }

}
