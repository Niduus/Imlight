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
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Quests;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Clean fight data (Niduus, 2026-11-15: "only accurate data being drawn, documented and saved ... as accurate as humanly
/// possible. NO EXCEPTIONS"). What really stood in each of his battles, read from the battle itself (never from his quest
/// step's words, which named foes "1 creatures", "1 Golden Key" or "a fight for XP toward level 14"): every enemy's
/// creature id, its real name from the game data, its health, and who fought on his side. The battle writes it each
/// round (CombatDuelComponent.Ultimus); the director takes it when the fight ends and saves one full line per fight to
/// logs/ultimus/fights.jsonl, the raw record later bots (the Quest Team, then the general bots) are built from.
/// </summary>
internal static class UltimusFights {

    internal const string FILE = "fights.jsonl";

    internal sealed record Enemy(uint Template, string Name, int MaxHealth, int Health, bool Minion);

    internal sealed record Battle(int Round, List<Enemy> Enemies, int Humans, int Bots, DateTime FirstSeen);

    // Kept per seat (Ultimus and each TCO7 member has its own; UltimusSeat).
    private static readonly SeatLocal<ConcurrentDictionary<ulong, Battle>> s_battlesSeat = new(() => new ConcurrentDictionary<ulong, Battle>());
    private static ConcurrentDictionary<ulong, Battle> s_battles { get => s_battlesSeat.Value; set => s_battlesSeat.Value = value; }

    /// <summary>The creature's real name, from the game data; empty when the data has none.</summary>
    internal static string RealName(ulong template) {
        if (template == 0) {
            return "";
        }

        var name = QuestSafety.NameOf(template);

        return string.IsNullOrWhiteSpace(name) || name == template.ToString() ? "" : name;
    }

    /// <summary>A round of his battle: who stands on each side (called by the battle once a round).</summary>
    internal static void Seen(ulong ultimusGlobalId, int round, IEnumerable<CombatDuelSubCircle> enemies, IEnumerable<CombatDuelSubCircle> allies) {
        if (ultimusGlobalId == 0) {
            return;
        }

        try {
            var list = enemies.Where(e => e is { Occupied: true } && !e.IsHumanPlayer)
                .Select(e => {
                    var template = (uint) Math.Min(e.ParticipantObject?.m_templateID ?? 0, uint.MaxValue);
                    var stats = e.ParticipantGameStats;
                    var most = stats is null ? 0 : stats.m_baseHitpoints + stats.m_bonusHitpoints;
                    return new Enemy(template, RealName(template), most, stats?.m_currentHitpoints ?? 0, e.IsSummonedMinion);
                })
                .ToList();
            var side = allies.Where(a => a is { Occupied: true }).ToList();
            var humans = side.Count(a => a.IsHumanPlayer && a.ParticipantObject?.m_globalID.Full != ultimusGlobalId);
            var bots = side.Count(a => !a.IsHumanPlayer && !a.IsSummonedMinion);
            s_battles.AddOrUpdate(ultimusGlobalId,
                _ => new Battle(round, list, humans, bots, DateTime.UtcNow),
                // The first round's enemies are kept (they are the battle he walked into); creatures that join later are added.
                (_, old) => old with {
                    Round = Math.Max(old.Round, round),
                    Enemies = [.. old.Enemies.Select(o => list.FirstOrDefault(n => n.Template == o.Template && n.MaxHealth == o.MaxHealth) is { } now ? o with { Health = now.Health } : o with { Health = 0 }),
                               .. list.Where(n => !old.Enemies.Any(o => o.Template == n.Template && o.MaxHealth == n.MaxHealth))],
                    Humans = Math.Max(old.Humans, humans),
                });
        }
        catch (Exception) {
            // A battle read half-way is not saved as if whole: the fight line says the enemies are unknown.
        }
    }

    /// <summary>His battle that just ended, taken (null when the battle never wrote one).</summary>
    internal static Battle Take(ulong ultimusGlobalId) => s_battles.TryRemove(ultimusGlobalId, out var battle) ? battle : null;

    /// <summary>The enemy that counts as "the foe" of a fight he did not pick: the one with the most health.</summary>
    internal static Enemy Main(Battle battle) => battle?.Enemies.Where(e => !e.Minion && e.Template != 0).OrderByDescending(e => e.MaxHealth).FirstOrDefault();

    /// <summary>One full line for this fight in fights.jsonl.</summary>
    internal static void Save(Dictionary<string, object> fight) {
        try {
            Directory.CreateDirectory(UltimusWire.Folder);
            File.AppendAllText(Path.Combine(UltimusWire.Folder, FILE),
                UltimusWire.Write(fight).Replace("\r", "").Replace("\n", "") + Environment.NewLine);
        }
        catch (IOException) {
            // The log line says the fight anyway.
        }
        catch (UnauthorizedAccessException) {
        }
    }
}
