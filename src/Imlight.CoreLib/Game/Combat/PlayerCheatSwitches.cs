/*
 * Players switch their own cheats (Niduus, 2026-10-28: "How can we approach users using their own cheats? Phantom
 * commands?"). A player cheat with PlayersSwitch on (the Manager's "Players can switch this" box) can be turned off
 * and back on by each wizard that has it, with `.cheat off <name>` / `.cheat on <name>`; PlayersStartOff makes it start
 * off until the wizard switches it on. The server owner still picks who has a cheat (its "who gets it" Ifs).
 *
 * What each wizard picked is kept in spiraldb-extra/PlayerCheatSwitches.json:
 *   { "<character id>": { "name:free blades": false, "number:3": true } }
 * A cheat is known by its Name (lower case), else by its number in the player cheat file.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.Combat;

internal static class PlayerCheatSwitches {

    private static readonly object s_lock = new();
    private static Dictionary<string, Dictionary<string, bool>> s_switches;

    private static string FilePath => Path.Combine(SpiralDB.ExtraPath, "PlayerCheatSwitches.json");

    /// <summary>How a cheat is known in the file: by its name, else by its number.</summary>
    internal static string KeyOf(BossCheats.BossCheat cheat)
        => string.IsNullOrWhiteSpace(cheat.Name) ? $"number:{cheat.Number}" : $"name:{cheat.Name.Trim().ToLowerInvariant()}";

    /// <summary>What a player sees a cheat called: its name, else "Cheat 3".</summary>
    internal static string LabelOf(BossCheats.BossCheat cheat)
        => string.IsNullOrWhiteSpace(cheat.Name) ? $"Cheat {cheat.Number}" : cheat.Name.Trim();

    /// <summary>Whether a cheat the wizard has is on for it: always, unless players may switch it.</summary>
    internal static bool IsOn(ulong charId, BossCheats.BossCheat cheat) {
        if (!cheat.PlayersSwitch || charId == 0) {
            return true;
        }

        lock (s_lock) {
            Load();

            return s_switches.TryGetValue(charId.ToString(), out var mine) && mine.TryGetValue(KeyOf(cheat), out var on)
                ? on
                : !cheat.PlayersStartOff;
        }
    }

    /// <summary>A wizard switched a cheat on or off: kept in the file, and counted from now.</summary>
    internal static void Set(ulong charId, BossCheats.BossCheat cheat, bool on) {
        lock (s_lock) {
            Load();
            if (!s_switches.TryGetValue(charId.ToString(), out var mine)) {
                mine = [];
                s_switches[charId.ToString()] = mine;
            }

            mine[KeyOf(cheat)] = on;
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(s_switches, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(FilePath + ".tmp", FilePath, overwrite: true);
            }
            catch (Exception e) {
                Logger.Warning("Player cheat switches could not be saved: {0}", Logger.Args(e.Message));
            }
        }

        PlayerCheats.Forget(charId);
        Logger.Information("Player cheats | wizard {0} switched \"{1}\" {2}.", Logger.Args(charId, LabelOf(cheat), on ? "on" : "off"));
    }

    private static void Load() {
        if (s_switches is not null) {
            return;
        }

        try {
            s_switches = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, bool>>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (Exception e) {
            Logger.Warning("Player cheat switches could not be read: {0}", Logger.Args(e.Message));
            s_switches = [];
        }
    }

}

/// <summary>
/// Cheats whose When is "Pressed" (Niduus, 2026-11-11: players "activate their cheats" from their Ahziael): a wizard
/// presses one with `.cheat use &lt;name&gt;` (typed, or sent from Ahziael and its overlay), and it goes off in their battle
/// at the next look at the Always cheats: the start of the round, or right after the next move. A press waits 90
/// seconds at most. A player cheat is pressed only by the wizard that has it; a creature's "Pressed" cheat goes off when
/// a wizard in its battle presses a cheat of that name (that wizard is "the attacker").
/// </summary>
internal static class PlayerCheatPresses {

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong CharId, string Key), DateTime> s_pressed = new();
    private static readonly TimeSpan s_keep = TimeSpan.FromSeconds(90);

    /// <summary>A press of the cheat known as <paramref name="key"/> (PlayerCheatSwitches.KeyOf, or "name:" and a name).</summary>
    internal static void Press(ulong charId, string key) {
        var now = DateTime.UtcNow;
        foreach (var old in s_pressed.Where(p => now - p.Value > s_keep).Select(p => p.Key).ToList()) {
            s_pressed.TryRemove(old, out _);
        }

        s_pressed[(charId, key)] = now;
    }

    /// <summary>Whether the wizard pressed that cheat (and not too long ago); the press is used up.</summary>
    internal static bool Take(ulong charId, string key)
        => s_pressed.TryRemove((charId, key), out var at) && DateTime.UtcNow - at < s_keep;

    /// <summary>Whether a wizard has a press waiting, so nothing is looked at when no one pressed anything.</summary>
    internal static bool Any => !s_pressed.IsEmpty;

}
