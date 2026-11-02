/*
 * The game's own codes for quests we rebuilt from Wizard404's recorded play-throughs (Lemuria onward, Darkmoor).
 * Those quests are named "LM-W404-1179504649" here, while the game's zone scripts ask for "LM-NIGHT-MAIN-001": the NPC
 * who steps out once you take a quest, a boss's intro as you walk up, a door that opens after a step. The server did not
 * know those codes, took them for retired quests and dropped their lines, so none of it happened (Arc 4 main quest
 * dialogue check, Niduus 2026-11-02: "smoke test all dialog along with key events ... NPC reveals, animations").
 * spiraldb-extra/QuestCodeAliases.json maps each game code to ours (the code Wizard404 recorded for the quest).
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;

namespace Imlight.CoreLib.WizardData;

public static class QuestCodeAliases {

    internal const string FILE_NAME = "QuestCodeAliases.json";

    private static Dictionary<string, string> s_toOurs = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string[]> s_toGame = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads the list. Runs after SpiralDB loads; a game code we have a quest of our own for is left alone.</summary>
    public static void Load(string extraPath) {
        try {
            var path = Path.Combine(extraPath ?? "", FILE_NAME);
            if (!File.Exists(path)) {
                return;
            }

            var read = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
            var toOurs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (game, ours) in read) {
                if (!string.IsNullOrEmpty(game) && !string.IsNullOrEmpty(ours)
                    && !SpiralDB.QuestExists(game) && SpiralDB.QuestExists(ours)) {
                    toOurs[game] = ours;
                }
            }

            s_toOurs = toOurs;
            s_toGame = toOurs.GroupBy(p => p.Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(p => p.Key).ToArray(), StringComparer.OrdinalIgnoreCase);
            Logger.Information("Quest code aliases: {0} game quest codes point at our quests.", Logger.Args(toOurs.Count));
        } catch (Exception ex) {
            Logger.Warning("Quest code aliases could not be read: {0}", Logger.Args(ex.Message));
        }
    }

    /// <summary>Our code for the quest the game calls <paramref name="questName"/>, or the name itself.</summary>
    public static string Resolve(string questName)
        => !string.IsNullOrEmpty(questName) && s_toOurs.TryGetValue(questName, out var ours) ? ours : questName;

    /// <summary>The game's codes for our quest <paramref name="ourName"/> (none for most quests).</summary>
    public static IReadOnlyList<string> GameCodes(string ourName)
        => !string.IsNullOrEmpty(ourName) && s_toGame.TryGetValue(ourName, out var codes) ? codes : [];

    /// <summary>A registry entry named after a game code ("LM-NIGHT-MAIN-001_Complete") under our code instead.</summary>
    public static string ResolveEntry(string entryName) {
        if (string.IsNullOrEmpty(entryName) || s_toOurs.Count == 0) {
            return entryName;
        }

        var cut = entryName.IndexOf('_');
        return cut > 0 && s_toOurs.TryGetValue(entryName[..cut], out var ours) ? ours + entryName[cut..] : entryName;
    }

}
