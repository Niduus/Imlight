/*
 * Player cheats (Niduus, 2026-10-22: "add PLAYER cheats for the local player to do. This is just a fun idea that uses
 * the C commands"). Cheats like a boss's, made in the Manager (Create page, Player Cheats tab) and saved as
 * spiraldb-extra/PlayerCheats.json, that go off for the wizard played on the server's own PC only (never a friend's
 * wizard, as with the Manager's command box: CommandService.IsThisPc).
 *
 * The file: { "Enabled": true, "Cheats": [ cheat, ... ] }. Enabled false turns every player cheat off. A cheat has the
 * fields of a boss cheat (BossCheats.cs), every one of them (Niduus, 2026-10-23: "ALL CHEATS are to be compatible for
 * PLAYERS and CREATURES"): they run through the boss cheats' own code, with the wizard as "it" and the creatures as
 * "the players". The ones first made for the player cheats:
 *   When   BattleStart, RoundStart, RoundEnd, FromRound (Round, Every as for a boss; rounds of the battle), ICast (my
 *          spell, before it lands), ICrit (my spell was a critical hit), IPass (I pass), EnemyCasts (an enemy's
 *          spell, before it lands), MyHealthBelow (Percent or Health; once a battle), Pips (I have at least Pips).
 *   Spells (with AnyVersion), School, Kind, Category, CostMin, CostMax   narrow ICast, ICrit and EnemyCasts.
 *   Once, MaxTimes, Cooldown, Chance, HealthAbove, HealthUnder (my health), FromRound, ToRound, Off.
 *   Actions:
 *     { "Do": "FreeCast", "Spell"/"SpellID", "Target": p1-p4 / e1-e4 / all / Everyone (both sides) /
 *       EveryoneElse (both sides but me; a FreeCast casts once on each) / "" (as the spell aims),
 *       "Timing": "Now" / "First" / "Last" / "Before" / "After" (my card), "Silent", "Enchant", "Times",
 *       "OnTarget" }
 *       a free spell, as .c1 / .c2 cast it (CombatResolver.AddFreeDelayedCast). First and Last are the round's
 *       first and last moves (the next round's first when the round is already under way); Before and After are
 *       around my own card (the next round's when it has been played); Now is at once.
 *       OnTarget: a spell that only goes on its caster (a self blade, Shadow Shrike) goes on the Target instead (any
 *       Target a boss step takes: WeakestAlly, Ally, AllAllies, AllEnemies...), once on each one it names.
 *     { "Do": "Pips", "Amount", "Kind": Normal/Power/Shadow }   { "Do": "Heal", "Amount", "Percent" }
 *     { "Do": "Say", "Line" }  a .ca banner over me;  "Text": words in my chat.
 * Each one that goes off writes "Custom cheat | ... player cheat ..." to the log.
 *
 * Whose they are (Niduus, 2026-10-28: "expand on cheats so I can put them on other people ... If wizard is ... If wizards
 * name is ... Get creative"): a cheat with a "who has it" If about "it" (Who left out) goes to every wizard those Ifs
 * pick, on any PC; one without such an If stays with the wizard played on the server's own PC, as before. The Ifs:
 *   Wizard       is this wizard: Wizard (character ids, commas between), else Name (the wizard's whole name).
 *   WizardName   the wizard's whole name is one of Name (commas between; not "has these words", as Name is).
 *   Account      the wizard is on one of Account (usernames, commas between; empty: on any account).
 *   ThisPc       the wizard is played on the server's own PC.
 *   Friend       the wizard is a friend of Wizard / Name.     Grouped: in a group with Wizard / Name (none: with "it").
 *   Rank         the wizard's account is at least Rank (QualityAssurance, HallMonitor, Developer, Administrator).
 *   AnyWizard    every wizard (narrowed by the other Ifs: school, level...).
 * NOT and OR groups count as for any If. Every one of them also works about anyone else (Who: the attacker, someone
 * on the other side...) and in a creature's cheats (the boss hits harder when the attacker is on the account "x"); a
 * creature is no wizard, so it never has them. Online (not about "it"): Wizard / Name is online anywhere on the server.
 * The cheats a wizard does not have keep their place in the list (so "cheat 3" stays cheat 3) and never go off.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.WizardData;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Combat;

internal static class PlayerCheats {

    internal sealed class PlayerCheatFile {
        public bool Enabled { get; set; } = true;
        public List<BossCheats.BossCheat> Cheats { get; set; } = [];
    }

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    // Wizards (by character id) and whether their game runs on the server's PC.
    private static readonly ConcurrentDictionary<ulong, bool> s_thisPc = new();

    // The wizards that came into the game, by character id (for the "who has it" Ifs outside a battle circle).
    private static readonly ConcurrentDictionary<ulong, Wizard> s_wizards = new();

    // Each wizard's own list (the cheats it does not have put out of play), worked out again every few seconds.
    private static readonly ConcurrentDictionary<ulong, (List<BossCheats.BossCheat> File, DateTime At, List<BossCheats.BossCheat> Cheats)> s_own = new();

    // Friendships asked of the database, kept a little while (an If is asked many times a round).
    private static readonly ConcurrentDictionary<(ulong, ulong), (bool Friends, DateTime At)> s_friends = new();

    // A cheat a wizard does not have: no When goes off for it and it does nothing.
    private static readonly BossCheats.BossCheat s_notTheirs = new() { When = "NotThisWizard", Custom = true };

    // The Ifs that say who has a cheat.
    private static readonly HashSet<string> s_whoHasIt = new(StringComparer.OrdinalIgnoreCase) {
        "Wizard", "WizardName", "Account", "ThisPc", "Friend", "Grouped", "Rank", "AnyWizard",
    };

    private static readonly object s_lock = new();
    private static PlayerCheatFile s_file = new() { Enabled = false };
    private static DateTime s_fileTime = DateTime.MinValue;
    private static DateTime s_checked = DateTime.MinValue;

    private static string FilePath => Path.Combine(SpiralDB.ExtraPath, "PlayerCheats.json");

    /// <summary>A wizard came into the game (WizardService): whether its game runs on this PC.</summary>
    internal static void NoteWizard(Wizard wizard, bool onThisPc) {
        s_thisPc[wizard.CharId] = onThisPc;
        s_wizards[wizard.CharId] = wizard;
        s_own.TryRemove(wizard.CharId, out _);
    }

    /// <summary>Whether a wizard (by character id) is played on the server's own PC.</summary>
    internal static bool IsLocalChar(ulong charId) => s_thisPc.TryGetValue(charId, out var local) && local;

    /// <summary>Whether a battle circle is the wizard played on the server's own PC.</summary>
    internal static bool IsLocal(CombatDuelSubCircle circle)
        => circle is { IsHumanPlayer: true, _wizard: { } wizard } && s_thisPc.TryGetValue(wizard.CharId, out var local) && local;

    /// <summary>
    /// The cheats that are on, read again when the Manager saved the file (checked at most every two seconds), so a
    /// change counts from the next round with no restart.
    /// </summary>
    internal static List<BossCheats.BossCheat> Current() {
        lock (s_lock) {
            var now = DateTime.UtcNow;
            if (now - s_checked > TimeSpan.FromSeconds(2)) {
                s_checked = now;
                try {
                    var time = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
                    if (time != s_fileTime) {
                        s_fileTime = time;
                        s_file = time == DateTime.MinValue ? new PlayerCheatFile { Enabled = false }
                            : JsonSerializer.Deserialize<PlayerCheatFile>(File.ReadAllText(FilePath), s_options) ?? new PlayerCheatFile();
                        var all = s_file.Cheats ?? [];
                        for (var i = 0; i < all.Count; i++) {
                            if (all[i] is not null) {
                                all[i].Number = i + 1;
                            }
                        }

                        s_file.Cheats = [.. all.Where(c => c is not null && !c.Off && !c.Is("Off"))];
                        foreach (var cheat in s_file.Cheats) {
                            cheat.Custom = true;
                        }

                        Logger.Information("Player cheats {0}: {1} cheats.", Logger.Args(s_file.Enabled ? "on" : "off", s_file.Cheats.Count));
                    }
                }
                catch (Exception e) {
                    Logger.Warning("Player cheats could not be read: {0}", Logger.Args(e.Message));
                    s_file = new PlayerCheatFile { Enabled = false };
                }
            }

            return s_file.Enabled ? s_file.Cheats : [];
        }
    }

    /// <summary>The player cheats of a wizard in a battle circle (none for a creature).</summary>
    internal static List<BossCheats.BossCheat> For(CombatDuelSubCircle circle)
        => circle is { IsHumanPlayer: true, _wizard: { } wizard } ? ForWizard(wizard) : [];

    /// <summary>The player cheats of a wizard by character id (one that came into the game), else none.</summary>
    internal static List<BossCheats.BossCheat> ForChar(ulong charId)
        => s_wizards.TryGetValue(charId, out var wizard) ? ForWizard(wizard) : IsLocalChar(charId) ? Mine(Current(), charId, null) : [];

    /// <summary>
    /// The player cheats a wizard has: the whole list, with the ones that are not theirs out of play (they keep their
    /// place). Worked out again when the file changes and every few seconds (a friend added, a group joined).
    /// </summary>
    internal static List<BossCheats.BossCheat> ForWizard(Wizard wizard) {
        var file = Current();
        if (file.Count == 0) {
            return file;
        }

        var now = DateTime.UtcNow;
        if (s_own.TryGetValue(wizard.CharId, out var own) && own.File == file && now - own.At < TimeSpan.FromSeconds(5)) {
            return own.Cheats;
        }

        var cheats = Mine(file, wizard.CharId, wizard);
        s_own[wizard.CharId] = (file, now, cheats);

        return cheats;
    }

    private static List<BossCheats.BossCheat> Mine(List<BossCheats.BossCheat> file, ulong charId, Wizard wizard) {
        var local = IsLocalChar(charId);
        var cheats = file.Select(c => Has(c, charId, wizard, local) && PlayerCheatSwitches.IsOn(charId, c) ? c : s_notTheirs).ToList();

        return cheats.All(c => c == s_notTheirs) ? [] : cheats;
    }

    /// <summary>
    /// The cheats a wizard has, switched on or off (for `.cheats`): those its "who has it" Ifs give it, else, when it says
    /// no one, the wizard played on this PC.
    /// </summary>
    internal static List<BossCheats.BossCheat> Owned(ulong charId) {
        var wizard = s_wizards.GetValueOrDefault(charId);
        var local = IsLocalChar(charId);

        return [.. Current().Where(c => Has(c, charId, wizard, local))];
    }

    /// <summary>A wizard switched a cheat on or off: its list is worked out again at once.</summary>
    internal static void Forget(ulong charId) => s_own.TryRemove(charId, out _);

    /// <summary>Whether a cheat says who has it (a "who has it" If about "it").</summary>
    internal static bool SaysWho(BossCheats.BossCheat cheat)
        => (cheat.Conditions ?? []).Any(c => c is not null && AboutIt(c) && s_whoHasIt.Contains((c.If ?? "").Trim()));

    private static bool AboutIt(BossCheats.CheatCondition c) => (c.Who ?? "").Trim().ToLowerInvariant() is "" or "self" or "me";

    // Whether a wizard has a cheat: by its "who has it" Ifs about "it" (an OR group with any other If in it is left to
    // the battle), else only the wizard played on this PC.
    private static bool Has(BossCheats.BossCheat cheat, ulong charId, Wizard wizard, bool local) {
        if (!SaysWho(cheat)) {
            return local;
        }

        var conditions = (cheat.Conditions ?? []).Where(c => c is not null && !string.IsNullOrWhiteSpace(c.If)).ToList();
        var groups = new Dictionary<int, bool>();
        foreach (var c in conditions.Where(c => AboutIt(c) && s_whoHasIt.Contains(c.If.Trim()))) {
            var holds = WizardIs(c, charId, wizard, 0) == true != c.Not;
            if (c.Any <= 0) {
                if (!holds) {
                    return false;
                }
            }
            else {
                groups[c.Any] = groups.GetValueOrDefault(c.Any) || holds;
            }
        }

        return groups.All(g => g.Value || conditions.Any(c => c.Any == g.Key && !(AboutIt(c) && s_whoHasIt.Contains(c.If.Trim()))));
    }

    /// <summary>
    /// A "who has it" If about one fighter: a wizard (charId, and its Wizard when known), with ownerCharId the wizard
    /// whose cheat it is (0 for a creature's) for Grouped with no wizard picked. Null when it is not such an If.
    /// </summary>
    internal static bool? WizardIs(BossCheats.CheatCondition c, ulong charId, Wizard wizard, ulong ownerCharId) {
        var kind = (c.If ?? "").Trim().ToLowerInvariant();
        if (kind is not ("wizard" or "wizardname" or "account" or "thispc" or "friend" or "grouped" or "rank" or "anywizard")) {
            return null;
        }

        // A creature is no wizard.
        if (charId == 0) {
            return false;
        }

        wizard ??= s_wizards.GetValueOrDefault(charId);
        switch (kind) {
            case "wizard":
                return Picked(c).Contains(charId) || Picked(c).Count == 0 && NameIsOneOf(NameOf(charId, wizard), c.Name);
            case "wizardname":
                return NameIsOneOf(NameOf(charId, wizard), c.Name);
            case "account": {
                var accounts = Words(c.Account);

                return accounts.Count == 0 || accounts.Contains(AccountOf(charId, wizard), StringComparer.OrdinalIgnoreCase);
            }
            case "thispc":
                return IsLocalChar(charId);
            case "friend":
                return PickedOrNamed(c).Any(other => AreFriends(charId, other));
            case "grouped": {
                var others = PickedOrNamed(c);
                if (others.Count == 0 && Picked(c).Count == 0 && Words(c.Name).Count == 0 && ownerCharId != 0) {
                    others = [ownerCharId];
                }

                return others.Any(other => other != charId && Groups.GroupRegistry.AreGrouped(charId, other));
            }
            case "rank":
                return RankOf(charId, wizard) >= (Enum.TryParse<AuthLevel>((c.Rank ?? "").Trim(), true, out var rank) ? rank : AuthLevel.None);
            default:
                return true;
        }
    }

    /// <summary>Online: the picked wizard (or one of that name) is online anywhere on the server.</summary>
    internal static bool IsOnline(BossCheats.CheatCondition c) {
        var picked = Picked(c);
        var names = Words(c.Name);

        return OnlinePlayerCollection.GetCachedOnlinePlayers().Any(p => p.CharacterId != 0
            && (picked.Contains(p.CharacterId) || picked.Count == 0 && names.Any(n => SameName(n, p.WizardName))));
    }

    private static List<string> Words(string words)
        => [.. (words ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static List<ulong> Picked(BossCheats.CheatCondition c)
        => [.. Words(c.Wizard).Select(w => ulong.TryParse(w, out var id) ? id : 0).Where(id => id != 0)];

    // The picked wizards, else those of the typed names that came into the game.
    private static List<ulong> PickedOrNamed(BossCheats.CheatCondition c) {
        var picked = Picked(c);
        if (picked.Count > 0) {
            return picked;
        }

        var names = Words(c.Name);

        return names.Count == 0 ? [] : [.. s_wizards.Where(w => names.Any(n => SameName(n, NameOf(w.Key, w.Value)))).Select(w => w.Key)];
    }

    private static bool NameIsOneOf(string name, string names) => Words(names).Any(n => SameName(n, name));

    // A whole name, with no heed to capitals or extra spaces.
    private static bool SameName(string a, string b)
        => string.Join(' ', (a ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Equals(string.Join(' ', (b ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)), StringComparison.OrdinalIgnoreCase);

    private static string NameOf(ulong charId, Wizard wizard) {
        string name = wizard?.PlayerNameBehavior?.GetWizardName();

        return !string.IsNullOrEmpty(name) ? name : OnlinePlayerCollection.GetCachedOnlinePlayers().FirstOrDefault(p => p.CharacterId == charId)?.WizardName ?? "";
    }

    private static string AccountOf(ulong charId, Wizard wizard)
        => wizard?.Account?.Username is { Length: > 0 } name ? name
            : OnlinePlayerCollection.GetCachedOnlinePlayers().FirstOrDefault(p => p.CharacterId == charId)?.AccountName ?? "";

    private static AuthLevel RankOf(ulong charId, Wizard wizard) => wizard?.Account?.AuthLevel ?? AuthLevel.None;

    private static bool AreFriends(ulong a, ulong b) {
        if (a == b || a == 0 || b == 0) {
            return false;
        }

        var key = a < b ? (a, b) : (b, a);
        if (s_friends.TryGetValue(key, out var known) && DateTime.UtcNow - known.At < TimeSpan.FromSeconds(30)) {
            return known.Friends;
        }

        bool friends;
        try {
            friends = BuddyRelationshipCollection.AreFriends(a, b);
        }
        catch (Exception e) {
            Logger.Warning("Player cheats: could not ask whether {0} and {1} are friends: {2}", Logger.Args(a, b, e.Message));
            friends = false;
        }

        s_friends[key] = (friends, DateTime.UtcNow);

        return friends;
    }

}
