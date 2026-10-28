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
 * PLUGINS
 * ========================================================================
 *
 * PURPOSE:
 * Lets an add-on change the server without being a copy of it (Niduus, 2026-10-28: "Begin implementation of plugins
 * as well that can modify Ahziael ... He wants the updated Ahziael but doesn't want to use all his usage updating").
 * A plugin is a folder in Server/Plugins with a plugin.json and C# files under server/. The server compiles those
 * files itself when it starts (Imlight.Director PluginLoader), against the very server that runs, so a plugin keeps
 * working through updates as long as the parts it uses stay. Plugins see the server's internals (AgentScriptInternals).
 *
 * NOTE:
 * Turning a plugin on or off, adding, updating or removing one applies without a restart: the loader reloads every
 * plugin when the Plugins folder changes. Everything a plugin hooks through PluginContext is undone when it unloads.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Imlight.Common;
using Imlight.CoreLib.Game.Commands;

namespace Imlight.CoreLib.Plugins;

/// <summary>
/// What a plugin's server code implements. Every class in the plugin that implements it is made once and loaded.
/// </summary>
internal interface IImlightPlugin {

    /// <summary>Called when the plugin is turned on: hook in through the context.</summary>
    void Load(PluginContext context);

    /// <summary>Called before the plugin is turned off or reloaded. Hooks made through the context undo themselves.</summary>
    void Unload() { }

}

/// <summary>What a plugin is given to hook into the server. Everything hooked through it is undone at unload.</summary>
internal sealed class PluginContext {

    private readonly List<Action> _undo = [];
    private readonly object _lock = new();

    internal PluginContext(string id, string name, string folder, IReadOnlyDictionary<string, string> defaults = null) {
        Id = id;
        Name = name;
        Folder = folder;
        Settings = new PluginSettings(Path.Combine(folder, PluginSettings.FILE_NAME), defaults);
    }

    /// <summary>The plugin's id (its folder name).</summary>
    public string Id { get; }

    /// <summary>The plugin's name as plugin.json gives it.</summary>
    public string Name { get; }

    /// <summary>The plugin's folder, for its own data files.</summary>
    public string Folder { get; }

    /// <summary>The plugin's settings (settings.ini in its folder, written from Ahziael's Plugins page). Read live.</summary>
    public PluginSettings Settings { get; }

    /// <summary>A path inside the plugin's folder.</summary>
    public string File(string relativePath) => Path.Combine(Folder, relativePath);

    public void Log(string message) => Logger.Information("[{0}] {1}", Logger.Args(Name, message));

    public void Warn(string message) => Logger.Warning("[{0}] {1}", Logger.Args(Name, message));

    public void Error(string message, Exception ex = null)
        => Logger.Error("[{0}] {1} {2}", Logger.Args(Name, message, ex?.ToString() ?? ""));

    /// <summary>Runs the handler every time the event happens, until the plugin unloads.</summary>
    public void On<T>(PluginEvent<T> pluginEvent, Action<T> handler) {
        var owned = pluginEvent.Add(handler, this);
        AddUndo(() => pluginEvent.Remove(owned));
    }

    /// <summary>Lets the plugin change a value the server is about to use, until the plugin unloads.</summary>
    public void Change<T>(PluginFilter<T> filter, Func<T, T> change) {
        var owned = filter.Add(change, this);
        AddUndo(() => filter.Remove(owned));
    }

    /// <summary>
    /// Adds chat commands (a CommandProtocol, as the server's own commands are written). The loader adds every
    /// CommandProtocol class in the plugin by itself, so this is only for one made with arguments.
    /// </summary>
    public void AddCommands(CommandProtocol protocol) {
        PluginCommands.Add(protocol);
        AddUndo(() => PluginCommands.Remove(protocol));
    }

    /// <summary>Something to do when the plugin unloads (stop a timer, put a value back).</summary>
    public void AddUndo(Action undo) {
        lock (_lock) {
            _undo.Add(undo);
        }
    }

    internal void UndoAll() {
        Action[] undo;
        lock (_lock) {
            undo = [.. _undo];
            _undo.Clear();
        }

        // Last hooked, first undone.
        for (var i = undo.Length - 1; i >= 0; i--) {
            try {
                undo[i]();
            }
            catch (Exception ex) {
                Error("Could not undo a hook while unloading.", ex);
            }
        }
    }

}

/// <summary>A plugin's settings.ini: "Key = value" lines, read again whenever the file changes.</summary>
internal sealed class PluginSettings {

    internal const string FILE_NAME = "settings.ini";

    private readonly string _path;
    private readonly IReadOnlyDictionary<string, string> _defaults;
    private DateTime _written = DateTime.MinValue;
    private Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <param name="defaults">plugin.json's defaults, for settings never saved.</param>
    internal PluginSettings(string path, IReadOnlyDictionary<string, string> defaults = null) {
        _path = path;
        _defaults = defaults ?? new Dictionary<string, string>();
        _values = new Dictionary<string, string>(_defaults, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every setting the file has now.</summary>
    public IReadOnlyDictionary<string, string> All {
        get {
            Refresh();
            return _values;
        }
    }

    public string Text(string key, string fallback = "")
        => All.TryGetValue(key, out var value) ? value : fallback;

    public bool On(string key, bool fallback = false)
        => All.TryGetValue(key, out var value)
            ? value.Trim().ToLowerInvariant() is "true" or "on" or "yes" or "1"
            : fallback;

    public int Number(string key, int fallback = 0)
        => All.TryGetValue(key, out var value) && int.TryParse(value.Trim(), out var number) ? number : fallback;

    public double Decimal(string key, double fallback = 0)
        => All.TryGetValue(key, out var value)
           && double.TryParse(value.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number
            : fallback;

    private void Refresh() {
        lock (_lock) {
            var written = System.IO.File.Exists(_path) ? System.IO.File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
            if (written == _written) {
                return;
            }

            _written = written;
            var values = new Dictionary<string, string>(_defaults, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in Read(_path)) {
                values[key] = value;
            }

            _values = values;
        }
    }

    internal static Dictionary<string, string> Read(string path) {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!System.IO.File.Exists(path)) {
            return values;
        }

        try {
            foreach (var raw in System.IO.File.ReadAllLines(path)) {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] is ';' or '#' or '[') {
                    continue;
                }

                var equals = line.IndexOf('=');
                if (equals > 0) {
                    values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
                }
            }
        }
        catch (IOException) {
            // Being written right now; the next read gets it.
        }

        return values;
    }

}

/// <summary>Something that happens in the server that plugins can react to.</summary>
internal sealed class PluginEvent<T> {

    internal sealed record Handler(Action<T> Run, PluginContext Owner);

    // Replaced whole on every change, so raising needs no lock.
    private volatile Handler[] _handlers = [];
    private readonly object _lock = new();

    /// <summary>Whether any plugin listens, to skip building the arguments when none does.</summary>
    internal bool Heard => _handlers.Length > 0;

    internal Handler Add(Action<T> run, PluginContext owner) {
        var handler = new Handler(run, owner);
        lock (_lock) {
            _handlers = [.. _handlers, handler];
        }

        return handler;
    }

    internal void Remove(Handler handler) {
        lock (_lock) {
            _handlers = [.. _handlers.Where(h => h != handler)];
        }
    }

    /// <summary>Tells every plugin. A plugin that throws is logged and never stops the server or other plugins.</summary>
    internal void Raise(T args) {
        foreach (var handler in _handlers) {
            try {
                handler.Run(args);
            }
            catch (Exception ex) {
                handler.Owner.Error("An event handler failed.", ex);
            }
        }
    }

}

/// <summary>A value the server is about to use that plugins may change, each in turn.</summary>
internal sealed class PluginFilter<T> {

    internal sealed record Change(Func<T, T> Run, PluginContext Owner);

    private volatile Change[] _changes = [];
    private readonly object _lock = new();

    internal bool Heard => _changes.Length > 0;

    internal Change Add(Func<T, T> run, PluginContext owner) {
        var change = new Change(run, owner);
        lock (_lock) {
            _changes = [.. _changes, change];
        }

        return change;
    }

    internal void Remove(Change change) {
        lock (_lock) {
            _changes = [.. _changes.Where(c => c != change)];
        }
    }

    /// <summary>The value after every plugin changed it. A plugin that throws leaves the value as it was given.</summary>
    internal T Apply(T value) {
        foreach (var change in _changes) {
            try {
                value = change.Run(value);
            }
            catch (Exception ex) {
                change.Owner.Error("A change failed; the value was left as it was.", ex);
            }
        }

        return value;
    }

}

/// <summary>Chat commands plugins added. The command dispatcher looks here before the server's own commands.</summary>
internal static class PluginCommands {

    private static volatile CommandProtocol[] s_protocols = [];
    private static readonly object s_lock = new();

    internal static CommandProtocol[] All => s_protocols;

    internal static void Add(CommandProtocol protocol) {
        lock (s_lock) {
            s_protocols = [.. s_protocols, protocol];
        }
    }

    internal static void Remove(CommandProtocol protocol) {
        lock (s_lock) {
            s_protocols = [.. s_protocols.Where(p => p != protocol)];
        }
    }

    /// <summary>A plugin's command group (".roguelike start"), or null.</summary>
    internal static CommandProtocol Group(string name)
        => s_protocols.FirstOrDefault(p => !string.IsNullOrEmpty(p.Group) && string.Equals(p.Group, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The plugins' commands that have no group (".run" rather than ".roguelike run").</summary>
    internal static IEnumerable<CommandProtocol> Groupless => s_protocols.Where(p => string.IsNullOrEmpty(p.Group));

}

/// <summary>The events and changes plugins can hook. The server raises each where it happens.</summary>
internal static class PluginEvents {

    /// <summary>The server takes connections now (or the plugin was turned on while it already did).</summary>
    public static readonly PluginEvent<PluginNothing> ServerReady = new();

    /// <summary>Once a second, on a thread of its own, while any plugin is loaded.</summary>
    public static readonly PluginEvent<PluginNothing> EverySecond = new();

}

/// <summary>The argument of an event that has none.</summary>
internal readonly record struct PluginNothing;
