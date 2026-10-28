/*
 * Cheat variables kept across battles (Niduus, 2026-10-28, the cheat ideas list: "count how many times a player beat this
 * boss and make it stronger each time"). Scope Saved: its own (a wizard's by character id, a creature's by its template,
 * so every one of that creature shares it); SavedAll: one for everyone. Kept in spiraldb-extra/CheatVariables.json:
 *   { "w123:wins": 4, "c45678:beaten": 9, "all:raids": 2 }
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.Combat;

internal static class SavedCheatVars {

    private static readonly object s_lock = new();
    private static Dictionary<string, int> s_vars;

    private static string FilePath => Path.Combine(SpiralDB.ExtraPath, "CheatVariables.json");

    /// <summary>The file key of a Saved / SavedAll variable, or null for one of this battle only.</summary>
    internal static string KeyOf(CombatDuelSubCircle owner, string name, string scope) {
        name = (name ?? "").Trim().ToLowerInvariant();
        return (scope ?? "").Trim().ToLowerInvariant() switch {
            "savedall" => $"all:{name}",
            "saved" => OwnerOf(owner) is { } who ? $"{who}:{name}" : null,
            _ => null,
        };
    }

    private static string OwnerOf(CombatDuelSubCircle owner)
        => owner?._wizard is { } wizard ? $"w{wizard.CharId}"
            : owner?.ParticipantObject is { } obj ? $"c{obj.m_templateID.Full}" : null;

    /// <summary>A message's {name}: the owner's saved variable, else everyone's, else null.</summary>
    internal static int? Find(CombatDuelSubCircle owner, string name) {
        lock (s_lock) {
            Load();
            name = (name ?? "").Trim().ToLowerInvariant();

            return OwnerOf(owner) is { } who && s_vars.TryGetValue($"{who}:{name}", out var mine) ? mine
                : s_vars.TryGetValue($"all:{name}", out var all) ? all : null;
        }
    }

    internal static int Get(string key) {
        lock (s_lock) {
            Load();

            return s_vars.GetValueOrDefault(key);
        }
    }

    internal static void Set(string key, int value) {
        lock (s_lock) {
            Load();
            s_vars[key] = value;
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(s_vars, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(FilePath + ".tmp", FilePath, overwrite: true);
            }
            catch (Exception e) {
                Logger.Warning("Saved cheat variables could not be written: {0}", Logger.Args(e.Message));
            }
        }
    }

    private static void Load() {
        if (s_vars is not null) {
            return;
        }

        try {
            s_vars = File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(FilePath)) ?? [] : [];
        }
        catch (Exception e) {
            Logger.Warning("Saved cheat variables could not be read: {0}", Logger.Args(e.Message));
            s_vars = [];
        }
    }

}
