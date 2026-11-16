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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Imlight.Common;
using Imlight.CoreLib.Shared.Behaviors;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// One wizard that Ultimus's whole brain plays (a "seat"). Ultimus is the first seat and the head of everything (Niduus,
/// 2026-11-16: "Ultimus will be our head Universal base for everything. Anything he does, everything under him is expected to
/// do the same, if not better"). The Calling of 7 (TCO7) are seven more seats, one per school, each its own Ultimus: the same
/// Director, the same member, the same battle picks, the same mini server, only with their own name, school, folder and
/// settings. Every seat reads and adds to Ultimus's knowledge of the world (his maps, teleporters, stuck solutions, quest
/// tree, creatures; <see cref="UltimusWire.SharedFolder"/>), and keeps its own diary, effort, status and issues in its own
/// folder (Ultimus: logs/ultimus; TCO7: logs/tco7/&lt;name&gt;), laid out exactly as his so Ahziael shows each one the same way.
///
/// Which seat the code works for is <see cref="Current"/>: the seat's Director and member actors set it for every message
/// they handle, and a battle sets it for the wizard whose turn it is. Anything not inside one is Ultimus.
/// </summary>
internal sealed class UltimusSeat {

    /// <summary>The title every TCO7 member wears (Niduus, 2026-11-16: "All the same titles \"Essential\""): the Essential badge.</summary>
    internal const string TCO7_TITLE = "Essential";

    /// <summary>Where the TCO7 members keep their folders, inside the server's logs folder.</summary>
    internal const string TCO7_FOLDER = "logs/tco7";

    /// <summary>Ultimus himself: the first seat, the head of the rest.</summary>
    internal static readonly UltimusSeat Ultimus = new("Ultimus", "Ultimus", "Balance", UltimusWire.FOLDER, "Ultimus-Balance", 0);

    /// <summary>The Calling of 7, one per school (Niduus, 2026-11-16), in this order.</summary>
    internal static readonly UltimusSeat[] Tco7 = [
        new("Tulkar", "Tulkar", "Ice", TCO7_FOLDER + "/tulkar", "TCO7-Tulkar", 1),
        new("Cyndr", "Cyndr", "Fire", TCO7_FOLDER + "/cyndr", "TCO7-Cyndr", 2),
        new("Kurrent", "Kurrent", "Storm", TCO7_FOLDER + "/kurrent", "TCO7-Kurrent", 3),
        new("Mythis", "Mythis", "Myth", TCO7_FOLDER + "/mythis", "TCO7-Mythis", 4),
        new("Idunn", "Idunn", "Life", TCO7_FOLDER + "/idunn", "TCO7-Idunn", 5),
        new("Garm", "Garm", "Death", TCO7_FOLDER + "/garm", "TCO7-Garm", 6),
        new("Ra", "Ra", "Balance", TCO7_FOLDER + "/ra", "TCO7-Ra", 7),
    ];

    /// <summary>Every seat: Ultimus first, then the TCO7.</summary>
    internal static readonly UltimusSeat[] All = [Ultimus, .. Tco7];

    private UltimusSeat(string key, string name, string role, string folder, string username, int index) {
        Key = key;
        Name = name;
        Role = role;
        FolderName = folder;
        Username = username;
        Index = index;
    }

    /// <summary>Its key (also its name): "Ultimus", "Tulkar", ...</summary>
    internal string Key { get; }

    /// <summary>The wizard's name in the game.</summary>
    internal string Name { get; }

    /// <summary>Its school, which is also its role in the team code ("Balance", "Ice", ...).</summary>
    internal string Role { get; }

    /// <summary>Its folder, relative to the server folder (logs/ultimus, logs/tco7/tulkar, ...).</summary>
    internal string FolderName { get; }

    /// <summary>Its account's name (no password anyone knows).</summary>
    internal string Username { get; }

    /// <summary>0 for Ultimus, 1-7 for the TCO7.</summary>
    internal int Index { get; }

    internal bool IsUltimus => Index == 0;

    internal bool IsTco7 => Index > 0;

    /// <summary>The seat's school.</summary>
    internal MagicSchool School => Enum.TryParse<MagicSchool>(Role, true, out var school) ? school : MagicSchool.Balance;

    /// <summary>The full path of its folder.</summary>
    internal string Folder => Path.GetFullPath(FolderName);

    /// <summary>"Ultimus" or "TCO7 member Tulkar", for the server log.</summary>
    internal string LogName => IsUltimus ? "Ultimus" : $"TCO7 {Name}";

    /// <summary>
    /// Whether this seat plays: Ultimus by [Ultimus] Enabled; a TCO7 member by [TCO7] Enabled (the whole team, off by default)
    /// and its own [TCO7] &lt;Name&gt; switch (on unless turned off). Live.
    /// </summary>
    internal bool Enabled => IsUltimus
        ? ConfigurationManager.GetValue("Ultimus.Enabled", false)
        : ConfigurationManager.GetValue("TCO7.Enabled", false) && ConfigurationManager.GetValue("TCO7." + Name, true)
          && ConfigurationManager.GetValue(Section + ".Enabled", true);

    /// <summary>Its own section in Imlight.ini: [Ultimus], or [TCO7-Tulkar] and so on (Ahziael's member page writes it).</summary>
    internal string Section => IsUltimus ? "Ultimus" : "TCO7-" + Name;

    /// <summary>The port of its mini server: Ultimus [Ultimus] Port (12373); the TCO7 [TCO7] Port (12381) and the next six.</summary>
    internal int Port => IsUltimus
        ? ConfigurationManager.GetValue("Ultimus.Port", 12373)
        : Math.Clamp(ConfigurationManager.GetValue("TCO7.Port", 12381), 1024, 65000) + Index - 1;

    /// <summary>
    /// One of its settings: Ultimus's from [Ultimus]; a TCO7 member's from its own [TCO7-&lt;Name&gt;], else [TCO7], else
    /// Ultimus's own (they do what he does unless Niduus sets them apart; an empty value means "the same as Ultimus").
    /// </summary>
    internal T Setting<T>(string key, T fallback) {
        var his = ConfigurationManager.GetValue("Ultimus." + key, fallback);

        return IsUltimus ? his : ConfigurationManager.GetValue(Section + "." + key, ConfigurationManager.GetValue("TCO7." + key, his));
    }

    public override string ToString() => Key;

    // ---------------------------------------------------------------------
    // Which seat the code works for now
    // ---------------------------------------------------------------------

    private static readonly AsyncLocal<UltimusSeat> s_current = new();

    // The seat a whole process works for (a TCO7 member's mini server is started with --seat <name>).
    private static UltimusSeat s_process = Ultimus;

    /// <summary>The seat the code works for now: the one set by <see cref="Use"/>, else the process's (Ultimus).</summary>
    internal static UltimusSeat Current => s_current.Value ?? s_process;

    /// <summary>A mini server started for one seat works for it alone (UltimusBrain, --seat).</summary>
    internal static void SetProcessSeat(UltimusSeat seat) => s_process = seat ?? Ultimus;

    /// <summary>The seat named (its key or name, any case), or null.</summary>
    internal static UltimusSeat ByKey(string key)
        => string.IsNullOrWhiteSpace(key) ? null : All.FirstOrDefault(s => s.Key.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Works for <paramref name="seat"/> until the returned scope is disposed (then for the one before).</summary>
    internal static Scope Use(UltimusSeat seat) {
        var before = s_current.Value;
        s_current.Value = seat;

        return new Scope(before);
    }

    internal readonly struct Scope : IDisposable {
        private readonly UltimusSeat _before;

        internal Scope(UltimusSeat before) => _before = before;

        public void Dispose() => s_current.Value = _before;
    }

    // ---------------------------------------------------------------------
    // Whose wizard is whose
    // ---------------------------------------------------------------------

    private static readonly ConcurrentDictionary<ulong, UltimusSeat> s_byChar = new();
    private static readonly ConcurrentDictionary<ulong, UltimusSeat> s_byGlobal = new();

    /// <summary>The seat's wizard (its character id) is known: its battles and chat find the seat by it.</summary>
    internal void RememberCharacter(ulong charId) {
        foreach (var (id, seat) in s_byChar) {
            if (seat == this && id != charId) {
                s_byChar.TryRemove(id, out _);
            }
        }

        if (charId != 0) {
            s_byChar[charId] = this;
        }
    }

    /// <summary>The seat's wizard in the game (its object id) is known.</summary>
    internal void RememberGlobal(ulong globalId) {
        foreach (var (id, seat) in s_byGlobal) {
            if (seat == this && id != globalId) {
                s_byGlobal.TryRemove(id, out _);
            }
        }

        if (globalId != 0) {
            s_byGlobal[globalId] = this;
        }
    }

    /// <summary>The seat logged out: its wizard is nobody's seat any more in battles.</summary>
    internal void ForgetGlobal() {
        foreach (var (id, seat) in s_byGlobal) {
            if (seat == this) {
                s_byGlobal.TryRemove(id, out _);
            }
        }
    }

    /// <summary>The seat whose wizard this character is, or null.</summary>
    internal static UltimusSeat OfCharacter(ulong charId) => charId != 0 && s_byChar.TryGetValue(charId, out var seat) ? seat : null;

    /// <summary>The seat whose wizard this game object is, or null.</summary>
    internal static UltimusSeat OfGlobal(ulong globalId) => globalId != 0 && s_byGlobal.TryGetValue(globalId, out var seat) ? seat : null;

    /// <summary>Every character any seat plays (Ultimus and the TCO7).</summary>
    internal static ICollection<ulong> Characters => s_byChar.Keys;

    // ---------------------------------------------------------------------
    // What each seat keeps for itself
    // ---------------------------------------------------------------------

    private readonly ConcurrentDictionary<object, object> _locals = new();

    internal T Local<T>(object key, Func<T> make) => (T) _locals.GetOrAdd(key, _ => make());

    internal void SetLocal<T>(object key, T value) => _locals[key] = value;

}

/// <summary>
/// A value each seat keeps for itself (it was one value for Ultimus alone): reads and writes go to the <see cref="UltimusSeat.Current"/>
/// seat's own copy.
/// </summary>
internal sealed class SeatLocal<T> {

    private readonly Func<T> _make;

    internal SeatLocal(Func<T> make = null) => _make = make ?? (() => default);

    internal T Value {
        get => UltimusSeat.Current.Local(this, _make);
        set => UltimusSeat.Current.SetLocal(this, value);
    }

    /// <summary>The copy of a given seat.</summary>
    internal T Of(UltimusSeat seat) => seat.Local(this, _make);

}
