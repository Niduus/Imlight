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

    /// <summary>The team name of the TCO7: their settings section ([TCO7]) and what the bots call them.</summary>
    internal const string TCO7_TEAM = "TCO7";

    /// <summary>The Kitharas Family's team name: their settings section ([Kitharas]).</summary>
    internal const string KITHARAS_TEAM = "Kitharas";

    /// <summary>Where the Kitharas Family keep their folders, inside the server's logs folder.</summary>
    internal const string KITHARAS_FOLDER = "logs/kitharas";

    // Seat numbers of the Kitharas Family start after this one (Ultimus 0, TCO7 1-7, True Form 8; 51-70 are the family).
    private const int KITHARAS_BASE = 50;

    /// <summary>How many of the Kitharas Family may play at once when [Kitharas] Count is not set (PC load: 5 to start).</summary>
    internal const int KITHARAS_DEFAULT_COUNT = 5;

    /// <summary>
    /// The Kitharas Family (Niduus, 2026-11-20: "New Bots: All function the same with the same purpose as TCO7", random
    /// classes): twenty more seats of Ultimus's brain, each exactly as a TCO7 member. They read everything Ultimus and the Hive
    /// mind know, talk with each other, team up when two are on the same quest or quest alone, and think for themselves. Their
    /// schools were drawn at random once (2026-11-20) and stay with them. The short name is what a player says to call one
    /// ("Moon, heal me"), since "Kitharas" alone would call all twenty.
    /// </summary>
    internal static readonly UltimusSeat[] Kitharas = [
        Family("KitharasMoon", "Kitharas Moon", "Moon", "Ice", 1),
        Family("KitharasHart", "Kitharas Hart", "Hart", "Ice", 2),
        Family("KitharasRock", "Kitharas Rock", "Rock", "Storm", 3),
        Family("KitharasAndromeda", "Kitharas Andromeda", "Andromeda", "Fire", 4),
        Family("KitharasHydra", "Kitharas Hydra", "Hydra", "Ice", 5),
        Family("KitharasJackle", "Kitharas Jackle", "Jackle", "Balance", 6),
        Family("KitharasLamia", "Kitharas Lamia", "Lamia", "Fire", 7),
        Family("KitharasLibra", "Kitharas Libra", "Libra", "Myth", 8),
        Family("KitharasShade", "Kitharas Shade", "Shade", "Storm", 9),
        Family("KitharasWraith", "Kitharas Wraith", "Wraith", "Fire", 10),
        Family("LumeriaOfKitharas", "Lumeria of Kitharas", "Lumeria", "Myth", 11),
        Family("KitharasConsumer", "Kitharas Consumer", "Consumer", "Balance", 12),
        Family("KitharasMordiael", "Kitharas Mordiael", "Mordiael", "Storm", 13),
        Family("KitharasDevourer", "Kitharas Devourer", "Devourer", "Storm", 14),
        Family("KitharasHexation", "Kitharas Hexation", "Hexation", "Myth", 15),
        Family("KitharasSentence", "Kitharas Sentence", "Sentence", "Balance", 16),
        Family("KitharasWasSilenced", "Kitharas Was Silenced", "Silenced", "Storm", 17),
        Family("CrimesAgainstKitharas", "Crimes Against Kitharas", "Crimes", "Myth", 18),
        Family("KitharasUmbella", "Kitharas Umbella", "Umbella", "Life", 19),
        Family("AlchemiZistOfKitharas", "AlchemiZist of Kitharas", "AlchemiZist", "Fire", 20),
    ];

    private static UltimusSeat Family(string key, string name, string call, string school, int n)
        => new(key, name, school, $"{KITHARAS_FOLDER}/{call.ToLowerInvariant()}", "Kitharas-" + call, KITHARAS_BASE + n, call);

    /// <summary>Every seat: Ultimus first, then the TCO7, then True Form, then the Kitharas Family.</summary>
    internal static readonly UltimusSeat[] All = [Ultimus, .. Tco7, TrueForm, .. Kitharas];

    private UltimusSeat(string key, string name, string role, string folder, string username, int index, string callName = null) {
        Key = key;
        Name = name;
        Role = role;
        FolderName = folder;
        Username = username;
        Index = index;
        CallName = callName ?? name.Split(' ')[0];
    }

    /// <summary>
    /// The one word a player says to call this wizard: the first word of its name ("Tulkar"), or a Kitharas Family member's own
    /// word ("Moon" for Kitharas Moon, "Crimes" for Crimes Against Kitharas).
    /// </summary>
    internal string CallName { get; }

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

    /// <summary>0 for Ultimus, 1-7 for the TCO7, 8 for True Form, 51-70 for the Kitharas Family.</summary>
    internal int Index { get; }

    internal bool IsUltimus => Index == 0;

    internal bool IsTco7 => Index is > 0 and <= 7;

    /// <summary>A member of the Kitharas Family (<see cref="Kitharas"/>).</summary>
    internal bool IsKitharas => Index is > KITHARAS_BASE and <= KITHARAS_BASE + 20;

    /// <summary>A Kitharas Family member's number, 1 to 20 (0 for any other seat).</summary>
    internal int Number => IsKitharas ? Index - KITHARAS_BASE : 0;

    /// <summary>
    /// Its team: "TCO7" or "Kitharas" (the [TCO7] or [Kitharas] settings section), null for Ultimus and True Form. A team
    /// member talks with, teams up with and holds a council with its own team (the Kitharas Family work the same as the TCO7).
    /// </summary>
    internal string Team => IsTco7 ? TCO7_TEAM : IsKitharas ? KITHARAS_TEAM : null;

    /// <summary>A member of a team (TCO7 or the Kitharas Family): plays as the TCO7 do on top of Ultimus.</summary>
    internal bool IsTeamBot => Team is not null;

    /// <summary>Its team's members, itself included (empty for Ultimus and True Form).</summary>
    internal UltimusSeat[] Mates => IsTco7 ? Tco7 : IsKitharas ? Kitharas : [];

    /// <summary>Another seat of the same team (two TCO7 members, or two of the Kitharas Family).</summary>
    internal bool SameTeam(UltimusSeat other) => other is not null && other != this && Team is not null && Team == other.Team;

    /// <summary>A team's members by its team name ("TCO7" or "Kitharas", any case), empty for any other.</summary>
    internal static UltimusSeat[] TeamOf(string team)
        => TCO7_TEAM.Equals(team, StringComparison.OrdinalIgnoreCase) ? Tco7
            : KITHARAS_TEAM.Equals(team, StringComparison.OrdinalIgnoreCase) || "kf".Equals(team, StringComparison.OrdinalIgnoreCase)
              || "family".Equals(team, StringComparison.OrdinalIgnoreCase) ? Kitharas : [];

    /// <summary>
    /// How many of the Kitharas Family may play now: [Kitharas] Count (5 unless set, so twenty wizards don't all start at once
    /// on Niduus's PC), 0 while [Kitharas] Enabled is off. Live.
    /// </summary>
    internal static int KitharasCount => ConfigurationManager.GetValue(KITHARAS_TEAM + ".Enabled", false)
        ? Math.Clamp(ConfigurationManager.GetValue(KITHARAS_TEAM + ".Count", KITHARAS_DEFAULT_COUNT), 0, Kitharas.Length)
        : 0;

    /// <summary>True Form, the helper bot (bots/TrueForm).</summary>
    internal bool IsTrueForm => Index == 8;

    /// <summary>The seat's school.</summary>
    internal MagicSchool School => Enum.TryParse<MagicSchool>(Role, true, out var school) ? school : MagicSchool.Balance;

    /// <summary>The full path of its folder.</summary>
    internal string Folder => Path.GetFullPath(FolderName);

    /// <summary>"Ultimus" or "TCO7 member Tulkar", for the server log.</summary>
    internal string LogName => IsUltimus ? "Ultimus" : IsTrueForm ? "True Form" : IsKitharas ? Name : $"TCO7 {Name}";

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
        // A Kitharas Family member: [Kitharas] Enabled, its own switches, and among the first [Kitharas] Count of those switched on.
        : IsKitharas ? KitharasOn(this) && Kitharas.Where(KitharasOn).Take(KitharasCount).Contains(this)
        : ConfigurationManager.GetValue("TCO7.Enabled", false) && ConfigurationManager.GetValue("TCO7." + Name, true)
          && ConfigurationManager.GetValue(Section + ".Enabled", true);

    // A Kitharas Family member's own switches: [Kitharas] <Key> and [Kitharas-<Key>] Enabled, both on unless turned off.
    private static bool KitharasOn(UltimusSeat seat)
        => ConfigurationManager.GetValue(KITHARAS_TEAM + "." + seat.Key, true) && ConfigurationManager.GetValue(seat.Section + ".Enabled", true);

    /// <summary>
    /// Its own section in Imlight.ini: [Ultimus], [TCO7-Tulkar] and so on (Ahziael's member page writes it), or
    /// [Kitharas-KitharasMoon] for the Kitharas Family.
    /// </summary>
    internal string Section => IsUltimus ? "Ultimus" : IsTrueForm ? "TrueForm" : IsKitharas ? KITHARAS_TEAM + "-" + Key : "TCO7-" + Name;

    /// <summary>The port of its mini server: Ultimus [Ultimus] Port (12373); the TCO7 [TCO7] Port (12381) and the next six.</summary>
    internal int Port => IsUltimus
        ? ConfigurationManager.GetValue("Ultimus.Port", 12373)
        : IsTrueForm ? Math.Clamp(ConfigurationManager.GetValue("TrueForm.Port", 12389), 1024, 65000)
        // The Kitharas Family: [Kitharas] Port (12701) and the next nineteen (clear of the TCO7's, True Form's and 12410, the
        // game's remote port).
        : IsKitharas ? Math.Clamp(ConfigurationManager.GetValue(KITHARAS_TEAM + ".Port", 12701), 1024, 64000) + Number - 1
        : Math.Clamp(ConfigurationManager.GetValue("TCO7.Port", 12381), 1024, 65000) + Index - 1;

    /// <summary>
    /// One of its settings: Ultimus's from [Ultimus]; a TCO7 member's from its own [TCO7-&lt;Name&gt;], else [TCO7], else
    /// Ultimus's own (they do what he does unless Niduus sets them apart; an empty value means "the same as Ultimus").
    /// </summary>
    internal T Setting<T>(string key, T fallback) {
        var his = ConfigurationManager.GetValue("Ultimus." + key, fallback);

        return IsUltimus ? his
            : IsTrueForm ? ConfigurationManager.GetValue(Section + "." + key, his)
            : ConfigurationManager.GetValue(Section + "." + key, ConfigurationManager.GetValue(Team + "." + key, his));
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
        => string.IsNullOrWhiteSpace(key) ? null
            : All.FirstOrDefault(s => s.Key.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase))
              ?? All.FirstOrDefault(s => s.Name.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The seat a player names: its key, its full name or (a Kitharas Family member) its call word, any case; or null.</summary>
    internal static UltimusSeat Named(string who)
        => ByKey(who) ?? (string.IsNullOrWhiteSpace(who) ? null
            : Kitharas.FirstOrDefault(s => s.CallName.Equals(who.Trim(), StringComparison.OrdinalIgnoreCase)));

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

    /// <summary>Every character any seat plays (Ultimus, the TCO7, True Form and the Kitharas Family).</summary>
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
