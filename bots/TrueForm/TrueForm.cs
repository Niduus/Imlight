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
using Imlight.CoreLib.Shared.Behaviors;

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

    /// <summary>The names players call him by in chat.</summary>
    internal static readonly string[] CalledBy = [TITLE_NAME, "Kitharas", "Ahziael", "TF", "Truform", "Trueform"];

    /// <summary>Whether a line from his group reads as an order for him: a mode, a spell to use, leave, or what he plays.</summary>
    internal static bool LooksLikeOrder(string text) {
        var order = Order(text);
        return order.Length > 0 && (ModeFrom(order) is not null && order.Split(' ').Length <= 6
                                    || System.Text.RegularExpressions.Regex.IsMatch(order,
                                        @"^(use|cast|keep|bring|hold|save|leave|go home|go back|bye|goodbye|dismissed|you can go|you can leave|go away|stop using|clear spells|pick your own|what mode|which mode|what are you playing|how are you playing)\b")
                                    || System.Text.RegularExpressions.Regex.IsMatch(order, @"\b(what mode|which mode|your mode|your role)\b"));
    }

    /// <summary>
    /// What is left of a player's line once his name, greetings and politeness are taken off ("True Form, can you please
    /// heal us?" is "heal us"), lower case, so an order reads the same however it is said (Niduus, 2026-11-19: "Bots need
    /// better obedience to commands and to better identify them and their meaning").
    /// </summary>
    internal static string Order(string text) {
        var t = " " + System.Text.RegularExpressions.Regex.Replace((text ?? "").ToLowerInvariant(), @"[^a-z0-9' ]+", " ") + " ";
        foreach (var name in CalledBy.Select(n => n.ToLowerInvariant()).OrderByDescending(n => n.Length)) {
            t = t.Replace(" " + name + " ", " ");
        }

        t = System.Text.RegularExpressions.Regex.Replace(t,
            @"\b(hey|hi|yo|ok|okay|please|pls|plz|now|thanks|thank you|ty|can you|could you|would you|will you|can u|could u|i want you to|i need you to|i'd like you to|go ahead and|just|kindly|for me|for us|bud|buddy|man|bro|dude)\b", " ");
        return System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();
    }

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
            // Whole words only: "that boss hits hard" is no order to attack.
            if (names.Any(n => System.Text.RegularExpressions.Regex.IsMatch(w, $@"(^|[^a-z]){System.Text.RegularExpressions.Regex.Escape(n)}($|[^a-z])"))) {
                return mode;
            }
        }

        // A typo in a mode word ("atack", "defned", "suport", "healler"): one letter off, for words of five letters or more.
        foreach (var token in w.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length >= 5)) {
            foreach (var (mode, names) in s_modeWords) {
                if (names.Any(n => n.Length >= 5 && !n.Contains(' ') && OneOff(token, n))) {
                    return mode;
                }
            }
        }

        return null;
    }

    // One letter off: the shared order reader's test (BotShared.BotOrderWords), the same for every bot.
    private static bool OneOff(string a, string b) => BotShared.BotOrderWords.OneOff(a, b);

    private static readonly (Mode Mode, string[] Names)[] s_modeWords = [
        (Mode.Auto, ["auto", "normal", "smart", "your call", "whatever"]),
        (Mode.Attack, ["attack", "attacker", "attacking", "hit", "hitter", "dps", "damage", "offense", "offence", "aggressive"]),
        (Mode.Defend, ["defend", "defense", "defence", "defender", "defending", "tank", "protect", "shield", "shields", "guard"]),
        (Mode.Support, ["support", "supporter", "supporting", "heal", "healer", "healing", "heals", "buff", "buffer", "medic"]),
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
    // His school (Niduus, 2026-11-19: "Go ice for me", "Go ice and tank for me"; "TF stays natively Myth though, switching
    // back when he leaves the group"; "STITCH STAYS THE SAME ACROSS THE BOARD")
    // ---------------------------------------------------------------------

    /// <summary>The schools he can switch to.</summary>
    internal static readonly MagicSchool[] Schools = [MagicSchool.Fire, MagicSchool.Ice, MagicSchool.Storm, MagicSchool.Myth,
        MagicSchool.Life, MagicSchool.Death, MagicSchool.Balance];

    private static volatile string s_school = Kept()?.School is { } kept && kept != MagicSchool.Myth ? kept.ToString() : null;

    /// <summary>The school he plays now: Myth unless his group asked for another.</summary>
    internal static MagicSchool School => s_school is { } asked && Enum.TryParse<MagicSchool>(asked, true, out var school) ? school : MagicSchool.Myth;

    private static volatile string s_role = Kept() is { School: not MagicSchool.Myth } kept ? kept.Role?.ToString() : null;

    /// <summary>The role the group asked for with the school ("Go ice and tank"), or null for the school's own.</summary>
    internal static Mode? Role => s_role is { } asked && Enum.TryParse<Mode>(asked, true, out var mode) ? mode : null;

    /// <summary>A school switch asked for, waiting to be done: he logs out, changes, and logs back in to the group.</summary>
    internal sealed record SwapAsk(MagicSchool School, Mode? Role, ulong HeadId, string HeadName, Setup Kind = Setup.Own) {
        internal bool Universal => Kind == Setup.Universal;
    }

    /// <summary>
    /// His setups beside his school's own: universal damage, full outgoing healing, full resist (Niduus, 2026-11-19: "make a
    /// full healing set where it's all outgoing and full tank set where it's full resist").
    /// </summary>
    internal enum Setup { Own, Universal, FullHeal, FullTank }

    private static volatile SwapAsk s_swap;

    /// <summary>Asks for a switch (Myth with no head: back home after the group).</summary>
    internal static void AskSwap(MagicSchool school, Mode? role, ulong headId, string headName, Setup kind = Setup.Own)
        => s_swap = new SwapAsk(school, role, headId, headName ?? "", kind);

    internal static SwapAsk TakeSwap() => System.Threading.Interlocked.Exchange(ref s_swap, null);

    /// <summary>Puts the asked school and role in place, for the next time he is made ready.</summary>
    internal static void Become(MagicSchool school, Mode? role, ulong headId = 0, string headName = "", Setup kind = Setup.Own) {
        s_school = school == MagicSchool.Myth ? null : school.ToString();
        s_role = role?.ToString();
        s_setup = kind;
        Keep(school, role, headId, headName, kind);
    }

    private static Setup s_setup = Kept()?.Kind ?? Setup.Own;

    /// <summary>The setup he wears now.</summary>
    internal static Setup Kind => s_setup;

    /// <summary>A setup other than his school's own (universal, full heal, full tank).</summary>
    internal static bool Special => s_setup != Setup.Own;

    /// <summary>A setup in words.</summary>
    internal static string SetupWords(Setup kind) => kind switch {
        Setup.Universal => "my universal damage setup",
        Setup.FullHeal => "my full healing setup",
        Setup.FullTank => "my full tank setup",
        _ => "my own setup",
    };

    /// <summary>The gear style for his setup now (full heal and full tank put everything on healing or resist).</summary>
    internal static BotShared.BotStyle StyleNow(MagicSchool school) => s_setup switch {
        Setup.Universal => BotShared.BotStyle.UniversalHitter,
        Setup.FullHeal => BotShared.BotStyle.FullHealer,
        Setup.FullTank => BotShared.BotStyle.FullTank,
        _ => StyleFor(school, Role),
    };

    /// <summary>"Full heal(ing/er) set(up)", "go full healer": the full healing setup asked for.</summary>
    internal static bool FullHealFrom(string words) => System.Text.RegularExpressions.Regex.IsMatch((words ?? "").ToLowerInvariant(),
        @"\b(full|max(ed)?|all)\s+(out(going)?\s+)?(heal|heals|healing|healer)\b|\b(heal(ing|er)?)\s+(set|setup|set up|build|loadout|gear)\b");

    /// <summary>"Full tank set(up)", "go full tank", "full resist": the full tank setup asked for.</summary>
    internal static bool FullTankFrom(string words) => System.Text.RegularExpressions.Regex.IsMatch((words ?? "").ToLowerInvariant(),
        @"\b(full|max(ed)?|all)\s+(tank|tanky|resist|resistance)\b|\b(tank|resist)\s+(set|setup|set up|build|loadout|gear)\b");

    /// <summary>The schools for a setup, best first (healing: Life, Death...; tank: Ice, Balance...).</summary>
    internal static MagicSchool[] BestSchoolsFor(Setup kind) => kind switch {
        Setup.FullHeal => [MagicSchool.Life, MagicSchool.Death, MagicSchool.Balance, MagicSchool.Ice, MagicSchool.Myth, MagicSchool.Storm, MagicSchool.Fire],
        Setup.FullTank => [MagicSchool.Ice, MagicSchool.Balance, MagicSchool.Death, MagicSchool.Life, MagicSchool.Myth, MagicSchool.Fire, MagicSchool.Storm],
        _ => Schools,
    };

    /// <summary>A full setup asked for without a school: waiting for the asker to pick one (3 minutes).</summary>
    internal static volatile SwapAsk PendingPick;

    internal static DateTime PendingPickAt;

    /// <summary>
    /// His universal setup: gear, jewels, pet and set bonuses min-maxed on damage (and pierce) to every school, so he hits
    /// hard with any school's spell (Niduus, 2026-11-19: "a universal setup where he's min-maxed to the brim on Universal
    /// damage"). "Go universal for me"; back to his own setup after the group.
    /// </summary>
    internal static bool Universal => s_setup == Setup.Universal;

    /// <summary>"Go universal", "switch to universal", "universal damage setup": the universal setup asked for.</summary>
    internal static bool UniversalFrom(string words) {
        var w = (words ?? "").Trim().ToLowerInvariant();
        return System.Text.RegularExpressions.Regex.IsMatch(w,
                   @"(^|\s)(go|switch( over)?( to)?|change( to)?|swap( to)?|be|become|turn( into)?|play( as)?|set up( as)?|use( your)?)\s+(full\s+|max(ed)?\s+|a\s+)?(uni|universal)\b")
               || System.Text.RegularExpressions.Regex.IsMatch(w, @"\b(uni|universal)\s+(damage|dmg|setup|set up|build|loadout|gear|mode)\b");
    }

    // His switched school, role and group's head kept on disk (logs/trueform/school.txt), so a bot update or server restart
    // (which logs every bot out and in) does not drop him back to Myth in the middle of a group: he comes back as that
    // school and asks the head to group again, back to Myth after 3 minutes if they do not.
    private const string SCHOOL_FILE = "school.txt";

    private static SwapAsk s_kept;

    private static bool s_keptRead;

    private static string SchoolPath => Path.Combine(Seat.Folder, SCHOOL_FILE);

    private static SwapAsk Kept() {
        if (s_keptRead) {
            return s_kept;
        }

        s_keptRead = true;
        try {
            if (File.Exists(SchoolPath)) {
                var parts = File.ReadAllText(SchoolPath).Trim().Split('|');
                if (parts.Length >= 4 && Enum.TryParse<MagicSchool>(parts[0], true, out var school)
                    && ulong.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var head)) {
                    Mode? role = Enum.TryParse<Mode>(parts[1], true, out var m) ? m : null;
                    var kind = parts.Length >= 5 && Enum.TryParse<Setup>(parts[4], true, out var k) ? k : Setup.Own;
                    s_kept = new SwapAsk(school, role, head, parts[3], kind);
                    if ((school != MagicSchool.Myth || kind != Setup.Own) && head != 0) {
                        // Back after an update or restart: go back to the group's head (TrueFormSwitch re-calls them).
                        Rejoining = s_kept;
                        // Three more minutes, for his log in and getting ready.
                        RejoiningSince = DateTime.UtcNow.AddMinutes(3);
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Logger.Debug("True Form: could not read his school: {0}", Logger.Args(e.Message));
        }

        return s_kept;
    }

    private static void Keep(MagicSchool school, Mode? role, ulong headId, string headName, Setup kind = Setup.Own) {
        s_kept = new SwapAsk(school, role, headId, headName ?? "", kind);
        s_keptRead = true;
        try {
            Directory.CreateDirectory(Seat.Folder);
            File.WriteAllText(SchoolPath, string.Create(CultureInfo.InvariantCulture,
                $"{school}|{role?.ToString() ?? ""}|{(school == MagicSchool.Myth && kind == Setup.Own ? 0 : headId)}|{(headName ?? "").Replace('|', ' ')}|{kind}"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            Logger.Warning("True Form: could not keep his school: {0}", Logger.Args(e.Message));
        }
    }

    /// <summary>
    /// While he logs out and back in for a switch: the group's head he goes back to, and since when. His group ending then is
    /// no goodbye: the mode, the asked spells and the school stay.
    /// </summary>
    internal static volatile SwapAsk Rejoining;

    // No initializer: reading his kept school (above, first) may set it already.
    internal static DateTime RejoiningSince;

    /// <summary>A school asked for before he was in the player's group: done once they group (3 minutes at most).</summary>
    internal static volatile SwapAsk SwapAfterJoin;

    internal static DateTime SwapAfterJoinAt = DateTime.MinValue;

    /// <summary>A school in a player's words ("go ice", "switch to storm", "be a fire wizard"), or null.</summary>
    internal static MagicSchool? SchoolFrom(string words) {
        var w = (words ?? "").Trim().ToLowerInvariant();
        var m = System.Text.RegularExpressions.Regex.Match(w,
            @"(^|\s)(go|switch( over)?( to)?|change( to)?|swap( to)?|be( an?)?|become( an?)?|turn( into)?|play( as)?( an?)?)\s+(?<s>fire|ice|storm|myth|life|death|balance|pyro|thaum|thermo|diviner|conjurer|theurgist|necromancer|sorcerer)\b");
        if (!m.Success) {
            return null;
        }

        return m.Groups["s"].Value switch {
            "fire" or "pyro" => MagicSchool.Fire,
            "ice" or "thaum" or "thermo" => MagicSchool.Ice,
            "storm" or "diviner" => MagicSchool.Storm,
            "myth" or "conjurer" => MagicSchool.Myth,
            "life" or "theurgist" => MagicSchool.Life,
            "death" or "necromancer" => MagicSchool.Death,
            _ => MagicSchool.Balance,
        };
    }

    /// <summary>The gear style for a school and role: the role's, or the school's own when none was asked.</summary>
    internal static BotShared.BotStyle StyleFor(MagicSchool school, Mode? role) => role switch {
        Mode.Attack => BotShared.BotStyle.Hitter,
        Mode.Defend => BotShared.BotStyle.Tank,
        Mode.Support => BotShared.BotStyle.Healer,
        _ => school switch {
            MagicSchool.Ice => BotShared.BotStyle.Tank,
            MagicSchool.Life => BotShared.BotStyle.Healer,
            MagicSchool.Fire or MagicSchool.Storm => BotShared.BotStyle.Hitter,
            _ => BotShared.BotStyle.Backup,
        },
    };

    /// <summary>The school's own role in words ("tank", "healer").</summary>
    internal static string OwnRole(MagicSchool school) => StyleFor(school, null) switch {
        BotShared.BotStyle.Tank => "a tank",
        BotShared.BotStyle.Healer => "a healer",
        BotShared.BotStyle.Hitter => "a hitter",
        _ => "a hitter who blades and helps the team",
    };

    /// <summary>
    /// A warning when the role does not suit the school (Niduus: "Go storm and tank for me" will let him try, but it may not
    /// always succeed; "Go fire and heal me" wouldn't work too well), else null.
    /// </summary>
    internal static string Misfit(MagicSchool school, Mode? role) => (role, school) switch {
        (Mode.Defend, MagicSchool.Storm or MagicSchool.Fire or MagicSchool.Myth) =>
            $"{school} isn't built to tank: low health and resist, and few shields of its own. I'll try, but I may go down.",
        (Mode.Support, MagicSchool.Fire or MagicSchool.Storm or MagicSchool.Myth) =>
            $"{school} has hardly any heals of its own, so I'll heal with other schools' spells. I'll try, but it won't be as strong as Life.",
        (Mode.Attack, MagicSchool.Life or MagicSchool.Ice) =>
            $"{school} doesn't hit as hard as Storm, Fire or Myth. I'll try, but the damage will be lower.",
        _ => null,
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
