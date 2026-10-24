/*
 * Player cheats (Niduus, 2026-10-22: "add PLAYER cheats for the local player to do. This is just a fun idea that uses
 * the C commands"). Cheats like a boss's, made in the Manager (Commands tab, Player cheats) and saved as
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
 *     { "Do": "FreeCast", "Spell"/"SpellID", "Target": p1-p4 / e1-e4 / all / "" (as the spell aims),
 *       "Timing": "Now" / "First" / "Last" / "Before" / "After" (my card), "Silent", "Enchant", "Times" }
 *       a free spell, as .c1 / .c2 cast it (CombatResolver.AddFreeDelayedCast). First and Last are the round's
 *       first and last moves (the next round's first when the round is already under way); Before and After are
 *       around my own card (the next round's when it has been played); Now is at once.
 *     { "Do": "Pips", "Amount", "Kind": Normal/Power/Shadow }   { "Do": "Heal", "Amount", "Percent" }
 *     { "Do": "Say", "Line" }  a .ca banner over me;  "Text": words in my chat.
 * Each one that goes off writes "Custom cheat | ... player cheat ..." to the log.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.WizardData;

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

    private static readonly object s_lock = new();
    private static PlayerCheatFile s_file = new() { Enabled = false };
    private static DateTime s_fileTime = DateTime.MinValue;
    private static DateTime s_checked = DateTime.MinValue;

    private static string FilePath => Path.Combine(SpiralDB.ExtraPath, "PlayerCheats.json");

    /// <summary>A wizard came into the game (WizardService): whether its game runs on this PC.</summary>
    internal static void NoteWizard(ulong charId, bool onThisPc) => s_thisPc[charId] = onThisPc;

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
                        s_file.Cheats = [.. (s_file.Cheats ?? []).Where(c => c is not null && !c.Off && !c.Is("Off"))];
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

}
