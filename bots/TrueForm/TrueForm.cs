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
using System.Globalization;
using System.IO;
using System.Linq;
using Imlight.Common;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Groups;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// True Form (Niduus, 2026-11-19): a helper bot whose real name is Ahziael. He is called "True Form" so he is never mixed up
/// with the Ahziael app. He is one more seat of Ultimus's brain (UltimusSeat.TrueForm), so he walks with the one shared route
/// finder and fights with the shared battle brain, but he never quests: when nobody has him he stands at his spot in The
/// Commons, by the carousel, and any player may call him (".trueform", "True Form, join me", or a group invite). In a group
/// he follows the group's head, matches the head's level with his spells, and plays attack, defend or support, picked each
/// round from how the fight goes, or the one the group asks for. There is one True Form on the server, so he helps one group
/// at a time. What is kept here is what the battle side, the commands and his director share: his spot, his mode and the
/// group he is with.
/// </summary>
internal static class TrueForm {

    /// <summary>How he plays a fight.</summary>
    internal enum Mode {
        // Each round, from how the fight goes: support when someone is in danger, defend when a big hit is coming, else attack.
        Auto,
        // Hits as hard as he can, with blades and traps.
        Attack,
        // Shields, absorbs, weakness and stuns: keeps the hits off the team.
        Defend,
        // Heals, cleanses and blades for the team.
        Support,
        // A funny troll (Niduus, 2026-11-19): hampers his own team on purpose, with moves the game allows.
        Evil,
    }

    /// <summary>What players call him in chat.</summary>
    internal const string TITLE_NAME = "True Form";

    /// <summary>The seat he plays in (Ultimus's brain).</summary>
    internal static UltimusSeat Seat => UltimusSeat.TrueForm;

    /// <summary>His wizard's character id, or 0 when he is not playing.</summary>
    internal static ulong CharId => UltimusDirector.MembersOf(Seat) is { Count: > 0 } members ? members[0].CharId : 0;

    // ---------------------------------------------------------------------
    // His mode
    // ---------------------------------------------------------------------

    private static volatile string s_mode;

    /// <summary>[TrueForm] Mode: how he plays when nobody asked (Auto unless set; live).</summary>
    private static Mode DefaultMode
        => Enum.TryParse<Mode>(ConfigurationManager.GetValue("TrueForm.Mode", "Auto"), true, out var mode) && mode != Mode.Evil ? mode : Mode.Auto;

    /// <summary>How he plays now: what his group asked for, else [TrueForm] Mode.</summary>
    internal static Mode Now => s_mode is { } asked && Enum.TryParse<Mode>(asked, true, out var mode) ? mode : DefaultMode;

    /// <summary>Whether the group asked for a mode (else it is the default).</summary>
    internal static bool Asked => s_mode is not null;

    internal static void SetMode(Mode mode) => s_mode = mode.ToString();

    /// <summary>Back to the default (the group ended).</summary>
    internal static void ResetMode() => s_mode = null;

    /// <summary>A mode from a player's words ("attack", "heal", "tank", "troll"), or null.</summary>
    internal static Mode? ModeFrom(string words) {
        var w = (words ?? "").Trim().ToLowerInvariant();
        foreach (var (mode, names) in s_modeWords) {
            if (names.Any(n => w == n || w.Contains(" " + n) || w.StartsWith(n + " ", StringComparison.Ordinal) || w.EndsWith(" " + n, StringComparison.Ordinal))) {
                return mode;
            }
        }

        return null;
    }

    private static readonly (Mode Mode, string[] Names)[] s_modeWords = [
        (Mode.Auto, ["auto", "normal", "smart", "your call", "whatever"]),
        (Mode.Attack, ["attack", "attacker", "hit", "hitter", "dps", "damage", "offense"]),
        (Mode.Defend, ["defend", "defense", "defence", "tank", "protect", "shield"]),
        (Mode.Support, ["support", "heal", "healer", "heals", "buff", "buffer"]),
        (Mode.Evil, ["evil", "troll", "chaos", "prank"]),
    ];

    /// <summary>The mode in a player's words, for chat.</summary>
    internal static string Words(Mode mode) => mode switch {
        Mode.Attack => "attack: I hit as hard as I can",
        Mode.Defend => "defend: I keep the hits off the team",
        Mode.Support => "support: heals, cleanses and blades for the team",
        Mode.Evil => "evil: I'm on your team... technically",
        _ => "auto: I pick attack, defend or support each round",
    };

    // ---------------------------------------------------------------------
    // His group
    // ---------------------------------------------------------------------

    /// <summary>
    /// The head of his group (Niduus, 2026-11-19: "He matches the Head of the group, since there needs to be a group leader for
    /// him to begin to morph his deck around"): its leader, or when he leads it himself, the highest level player in it.
    /// 0 when he is in no group with a player.
    /// </summary>
    internal static ulong Head() {
        var me = CharId;
        if (me == 0 || !GroupRegistry.TryGetGroup(me, out var group)) {
            return 0;
        }

        if (group.LeaderCharId != me && group.LeaderCharId != 0 && IsPlayer(group.LeaderCharId)) {
            return group.LeaderCharId;
        }

        return group.Members.Where(m => m.CharId != me && IsPlayer(m.CharId))
            .OrderByDescending(m => LevelOf(m.CharId)).Select(m => m.CharId).FirstOrDefault();
    }

    /// <summary>Whether he is in a group with at least one real player.</summary>
    internal static bool WithPlayer => Head() != 0;

    /// <summary>
    /// The level his spells match: the head's level in a group with a player; the server's top level alone (or when the
    /// head's level is not known).
    /// </summary>
    internal static int SpellLevel() {
        var head = Head();
        var level = head == 0 ? 0 : LevelOf(head);
        return level > 0 ? level : TopLevel;
    }

    /// <summary>The server's top wizard level (Character.MaxLevel), at least 1.</summary>
    internal static int TopLevel => Math.Max(1, Shared.Character.MagicLevelsConfig.MaxLevel);

    /// <summary>
    /// Dev spells are his only while grouped with a player (Niduus, 2026-11-19: "While in a group with a player will resort to
    /// using spells that match it's level with dev spells able"), and [TrueForm] DevSpells is on (on unless turned off; live).
    /// </summary>
    internal static bool DevSpellsNow => WithPlayer && ConfigurationManager.GetValue("TrueForm.DevSpells", true);

    internal static int LevelOf(ulong charId) => PlayerCheats.LiveWizard(charId)?.MagicSchoolBehavior?.Level ?? 0;

    /// <summary>A real player: not a server bot (Ultimus and his seats, the Quest Team).</summary>
    internal static bool IsPlayer(ulong charId)
        => charId != 0 && !UltimusRoster.IsMember(charId) && !QuestTeam.QuestTeamRoster.IsMember(charId);

    // ---------------------------------------------------------------------
    // His spot
    // ---------------------------------------------------------------------

    /// <summary>Where he waits when nobody has him.</summary>
    internal sealed record Spot(string Zone, float X, float Y, float Z, float Yaw);

    /// <summary>
    /// The Commons, on the wooden deck by the carousel, between Madam Magda and Hilda Brewer (Niduus's screenshot,
    /// 2026-11-19). The game files place the carousel at (-4918, -592) and Hilda Brewer at (-4427, 1010); the deck's exact
    /// point is set in the game with ".trueform home" standing on it, and kept in logs/trueform/home.txt.
    /// </summary>
    internal static readonly Spot DefaultHome = new("WizardCity/WC_Hub", -4640f, -380f, 251f, 0f);

    private const string HOME_FILE = "home.txt";

    private static Spot s_home;

    private static string HomePath => Path.Combine(Seat.Folder, HOME_FILE);

    /// <summary>His spot: the one set with ".trueform home", else the default by the carousel.</summary>
    internal static Spot Home {
        get {
            if (s_home is { } known) {
                return known;
            }

            try {
                if (File.Exists(HomePath)) {
                    var parts = File.ReadAllText(HomePath).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 5
                        && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                        && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                        && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)
                        && float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var yaw)) {
                        return s_home = new Spot(parts[0], x, y, z, yaw);
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                Logger.Debug("True Form: could not read his spot: {0}", Logger.Args(e.Message));
            }

            return s_home = DefaultHome;
        }
    }

    /// <summary>Keeps a new spot (".trueform home").</summary>
    internal static void SetHome(Spot spot) {
        s_home = spot;
        try {
            Directory.CreateDirectory(Seat.Folder);
            File.WriteAllText(HomePath, string.Create(CultureInfo.InvariantCulture, $"{spot.Zone} {spot.X:0.##} {spot.Y:0.##} {spot.Z:0.##} {spot.Yaw:0.###}"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Logger.Warning("True Form: could not keep his spot: {0}", Logger.Args(e.Message));
        }
    }

    // ---------------------------------------------------------------------
    // Calls
    // ---------------------------------------------------------------------

    private static volatile CallAsk s_call;

    /// <summary>A player who called him (".trueform" or "True Form, join me"), for his director to answer.</summary>
    internal sealed record CallAsk(ulong CharId, string Name, DateTime At);

    internal static void Call(ulong charId, string name) => s_call = new CallAsk(charId, name ?? "", DateTime.UtcNow);

    /// <summary>The call waiting, taken once; null when there is none (or it is over a minute old).</summary>
    internal static CallAsk TakeCall() {
        var call = s_call;
        s_call = null;
        return call is not null && DateTime.UtcNow - call.At < TimeSpan.FromMinutes(1) ? call : null;
    }

    private static volatile bool s_leave;

    /// <summary>".trueform leave": he leaves his group and goes back to his spot.</summary>
    internal static void AskLeave() => s_leave = true;

    internal static bool TakeLeave() {
        var leave = s_leave;
        s_leave = false;
        return leave;
    }

    // ---------------------------------------------------------------------
    // Spells asked for by name (".trueform use <spell>")
    // ---------------------------------------------------------------------

    private static volatile uint[] s_requested = [];

    /// <summary>Spells his group asked him to use (put in his hand first); cleared when the group ends.</summary>
    internal static System.Collections.Generic.IReadOnlyList<uint> Requested => s_requested;

    internal static void Request(uint id) {
        if (!s_requested.Contains(id)) {
            s_requested = [.. s_requested.Take(6), id];
        }
    }

    internal static void ClearRequested() => s_requested = [];

    /// <summary>The player he is with now, by name, for the commands ("" when nobody).</summary>
    internal static volatile string With = "";

}
