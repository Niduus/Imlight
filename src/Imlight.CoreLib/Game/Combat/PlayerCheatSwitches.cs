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
