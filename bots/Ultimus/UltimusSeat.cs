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

    /// <summary>Where True Form keeps his folder, inside the server's logs folder.</summary>
    internal const string TRUE_FORM_FOLDER = "logs/trueform";

    /// <summary>
    /// True Form (Niduus, 2026-11-19): the helper bot whose real name is Ahziael, called "True Form" so he is never mixed up
    /// with the Ahziael app. A max level Myth wizard with every spell, who waits at his spot in The Commons and comes along
    /// with any player who calls him (bots/TrueForm). His in-game name is "True Form of Kitharas" (Niduus, 2026-11-19, after
    /// his first test; it was Ahziael) and his title is True Form. The account keeps its first name.
    /// </summary>
    internal static readonly UltimusSeat TrueForm = new("TrueForm", "True Form of Kitharas", "Myth", TRUE_FORM_FOLDER, "TrueForm-Ahziael", 8);

    /// <summary>Where the questing street bots keep their folders, inside the server's logs folder.</summary>
    internal const string STREET_FOLDER = "logs/street-questers";

    /// <summary>The most questing street bots there can be at once ([StreetQuest] Count is held to this).</summary>
    internal const int STREET_MOST = 30;

    // Seat numbers of the questing street bots start after this one (Ultimus 0, TCO7 1-7, True Form 8).
    private const int STREET_BASE = 100;

    // Their schools, taken in turn, so every school quests (bot 1 Ice, 2 Fire, ... 7 Balance, 8 Ice again).
    private static readonly string[] s_streetSchools = ["Ice", "Fire", "Storm", "Myth", "Life", "Death", "Balance"];

    /// <summary>
    /// The questing street bots (Niduus, 2026-11-20: "an Ahziael update that will allow normal street bots to quest like TCO7.
    /// I want to be able to swarm the server with bots to collect more accurate data so the bots have multiple success paths
    /// instead of one single"). Each is one more seat of Ultimus's brain, exactly as a TCO7 member: the same quest rules, way
    /// finder, battles and shared knowledge. They wear a plain random name from the game's own name lists (no custom names,
    /// as every street bot), take the schools in turn, and play while [StreetQuest] Enabled is on and their number is within
    /// [StreetQuest] Count (live: lowering it logs the extra ones out within seconds).
    /// </summary>
    internal static readonly UltimusSeat[] Street = [.. Enumerable.Range(1, STREET_MOST).Select(n => new UltimusSeat(
        $"Street{n:00}", null, s_streetSchools[(n - 1) % s_streetSchools.Length], $"{STREET_FOLDER}/street{n:00}", $"Street-{n:00}", STREET_BASE + n))];

    /// <summary>Every seat: Ultimus first, then the TCO7, then True Form, then the questing street bots.</summary>
    internal static readonly UltimusSeat[] All = [Ultimus, .. Tco7, TrueForm, .. Street];

    private UltimusSeat(string key, string name, string role, string folder, string username, int index) {
        Key = key;
        _name = name;
        Role = role;
        FolderName = folder;
        Username = username;
        Index = index;
    }

    /// <summary>Its key (also its name): "Ultimus", "Tulkar", ...</summary>
    internal string Key { get; }

    private string _name;

    /// <summary>
    /// The wizard's name in the game. A questing street bot's comes from its wizard (a random name from the game's lists, made
    /// with it and kept in name.txt in its folder); until it has one it is "Street bot 3".
    /// </summary>
    internal string Name {
        get {
            if (_name is not null) {
                return _name;
            }

            try {
                var file = Path.Combine(Folder, STREET_NAME_FILE);
                if (File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } saved) {
                    return _name = saved;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                // Read again next time.
            }

            return $"Street bot {Number}";
        }
    }

    // A questing street bot's own name, written when its wizard is made (UltimusRoster).
    private const string STREET_NAME_FILE = "name.txt";

    /// <summary>A questing street bot's wizard was made (or found) with this name: it goes by it from now on.</summary>
    internal void NameStreetBot(string name) {
        if (!IsStreet || string.IsNullOrWhiteSpace(name)) {
            return;
        }

        _name = name.Trim();
        try {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, STREET_NAME_FILE), _name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Logger.Warning("{0}: could not write its name: {1}", Logger.Args(LogName, e.Message));
        }
    }

    /// <summary>Its school, which is also its role in the team code ("Balance", "Ice", ...).</summary>
    internal string Role { get; }

    /// <summary>Its folder, relative to the server folder (logs/ultimus, logs/tco7/tulkar, ...).</summary>
    internal string FolderName { get; }

    /// <summary>Its account's name (no password anyone knows).</summary>
    internal string Username { get; }

    /// <summary>0 for Ultimus, 1-7 for the TCO7.</summary>
    internal int Index { get; }

    internal bool IsUltimus => Index == 0;

    internal bool IsTco7 => Index is > 0 and <= 7;

    /// <summary>True Form, the helper bot (bots/TrueForm).</summary>
    internal bool IsTrueForm => Index == 8;

    /// <summary>A questing street bot (<see cref="Street"/>).</summary>
    internal bool IsStreet => Index > STREET_BASE;

    /// <summary>A questing street bot's number, 1 and up (0 for any other seat).</summary>
    internal int Number => IsStreet ? Index - STREET_BASE : 0;

    /// <summary>How many questing street bots may play now: [StreetQuest] Count while [StreetQuest] Enabled is on (live).</summary>
    internal static int StreetCount => ConfigurationManager.GetValue("StreetQuest.Enabled", false)
        ? Math.Clamp(ConfigurationManager.GetValue("StreetQuest.Count", 4), 0, STREET_MOST)
        : 0;

    /// <summary>
    /// Two seats that count each other as fellow players (they join each other's street fights as two players do): two TCO7
    /// members, or two questing street bots while [StreetQuest] HelpEachOther is on. Ultimus stays alone, and the TCO7 and
    /// the street bots keep apart, so each one's data stays its own.
    /// </summary>
    internal bool IsFellow(UltimusSeat other)
        => other is not null && other != this
           && ((IsTco7 && other.IsTco7) || (IsStreet && other.IsStreet && ConfigurationManager.GetValue("StreetQuest.HelpEachOther", true)));

    /// <summary>The seat's school.</summary>
    internal MagicSchool School => Enum.TryParse<MagicSchool>(Role, true, out var school) ? school : MagicSchool.Balance;

    /// <summary>The full path of its folder.</summary>
    internal string Folder => Path.GetFullPath(FolderName);

    /// <summary>"Ultimus" or "TCO7 member Tulkar", for the server log.</summary>
    internal string LogName => IsUltimus ? "Ultimus" : IsTrueForm ? "True Form" : IsStreet ? $"Street bot {Name}" : $"TCO7 {Name}";

    // "Ultimus" as a word of its own, not "[Ultimus]" (a settings section) and not part of a longer word.
    private static readonly System.Text.RegularExpressions.Regex s_ultimusWord = new(@"(?<![\[\w])Ultimus(?![\]\w])");

    /// <summary>
    /// A text written about the wizard himself, with his own name in it: a TCO7 member's issues say "Tulkar went in a loop",
    /// not "Ultimus went in a loop" (they all run Ultimus's brain, whose texts name him). Ultimus's own texts stay as they are.
    /// </summary>
    internal string Own(string text) => IsUltimus || string.IsNullOrEmpty(text) ? text : s_ultimusWord.Replace(text, Name);

    /// <summary>
    /// Whether this seat plays: Ultimus by [Ultimus] Enabled; a TCO7 member by [TCO7] Enabled (the whole team, off by default)
    /// and its own [TCO7] &lt;Name&gt; switch (on unless turned off). Live.
    /// </summary>
    internal bool Enabled => IsUltimus
        ? ConfigurationManager.GetValue("Ultimus.Enabled", false)
        // True Form: [TrueForm] Enabled, on unless turned off (Niduus asked for him to stand at his spot; live).
        : IsTrueForm ? ConfigurationManager.GetValue("TrueForm.Enabled", true)
        // A questing street bot: [StreetQuest] Enabled, and its number within [StreetQuest] Count.
        : IsStreet ? Number <= StreetCount
        : ConfigurationManager.GetValue("TCO7.Enabled", false) && ConfigurationManager.GetValue("TCO7." + Name, true)
          && ConfigurationManager.GetValue(Section + ".Enabled", true);

    /// <summary>Its own section in Imlight.ini: [Ultimus], or [TCO7-Tulkar] and so on (Ahziael's member page writes it).</summary>
    internal string Section => IsUltimus ? "Ultimus" : IsTrueForm ? "TrueForm" : IsStreet ? STREET_SECTION : "TCO7-" + Name;

    /// <summary>The questing street bots' section in Imlight.ini (one for them all).</summary>
    internal const string STREET_SECTION = "StreetQuest";

    /// <summary>The port of its mini server: Ultimus [Ultimus] Port (12373); the TCO7 [TCO7] Port (12381) and the next six.</summary>
    internal int Port => IsUltimus
        ? ConfigurationManager.GetValue("Ultimus.Port", 12373)
        : IsTrueForm ? Math.Clamp(ConfigurationManager.GetValue("TrueForm.Port", 12389), 1024, 65000)
        // The questing street bots: [StreetQuest] Port (12601) and the next ones (clear of 12410, the game's remote port).
        : IsStreet ? Math.Clamp(ConfigurationManager.GetValue(STREET_SECTION + ".Port", 12601), 1024, 64000) + Number - 1
        : Math.Clamp(ConfigurationManager.GetValue("TCO7.Port", 12381), 1024, 65000) + Index - 1;

    /// <summary>
    /// One of its settings: Ultimus's from [Ultimus]; a TCO7 member's from its own [TCO7-&lt;Name&gt;], else [TCO7], else
    /// Ultimus's own (they do what he does unless Niduus sets them apart; an empty value means "the same as Ultimus").
    /// </summary>
    internal T Setting<T>(string key, T fallback) {
        var his = ConfigurationManager.GetValue("Ultimus." + key, fallback);

        return IsUltimus ? his
            : IsTrueForm || IsStreet ? ConfigurationManager.GetValue(Section + "." + key, his)
            : ConfigurationManager.GetValue(Section + "." + key, ConfigurationManager.GetValue("TCO7." + key, his));
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

    /// <summary>Every character any seat plays (Ultimus, the TCO7, True Form and the questing street bots).</summary>
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
