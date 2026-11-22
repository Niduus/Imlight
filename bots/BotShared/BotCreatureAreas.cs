/*
 * Imlight
 * Copyright (C) 2025 Revive101
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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData.Collections;

namespace Imlight.CoreLib.Game.BotShared;

/// <summary>
/// The creature-area book (Niduus, 2026-11-22, "Creature-area book + wider"): which areas each creature appears in, read
/// ahead from every book area's own spawn files (BotBookAreas), so a bot knows where to find a creature before it has been
/// to that area. The area a bot stands in it already knows from its creature paths (Tools.MobSpawnAreas); this is for the
/// areas it is not in. A "defeat X" step that names no area, with no quest arrow and never seen anywhere, used to have
/// nothing to go on: now the nearest area whose files spawn X is where the step is. Written to logs/ultimus/creature-areas.json
/// (area: creature templates) and creature-areas.txt (by world, for Niduus); read again at each start, built again after
/// 3 days. Read-only facts, shared by Ultimus, the TCO7, the Kitharas and the Quest Team.
/// </summary>
internal static class BotCreatureAreas {

    private const string BOOK_FILE = "creature-areas.json";
    private const string REPORT_FILE = "creature-areas.txt";

    // Area -> the creature templates its spawn files bring out.
    private static readonly ConcurrentDictionary<string, ulong[]> s_book = new(StringComparer.OrdinalIgnoreCase);
    // (from area | tags) -> the answer, for a while: the planner asks every tick.
    private static readonly ConcurrentDictionary<string, (string Zone, DateTime At)> s_asked = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan s_keep = TimeSpan.FromMinutes(10);
    private static int s_building;

    /// <summary>Whether the book is read or built.</summary>
    internal static bool Ready { get; private set; }

    /// <summary>How many areas and creature kinds the book holds.</summary>
    internal static (int Areas, int Creatures) Count() => (s_book.Count, s_book.Values.SelectMany(t => t).Distinct().Count());

    /// <summary>The areas whose spawn files bring out a creature with one of these tags (or a stand-in the quest counts).</summary>
    internal static List<string> AreasWith(IReadOnlyCollection<string> adjectives) {
        if (!Ready || adjectives is not { Count: > 0 }) {
            return [];
        }

        var wanted = new HashSet<ulong>();
        var looked = new HashSet<ulong>();
        var list = new List<string>();
        foreach (var (zone, templates) in s_book) {
            foreach (var t in templates) {
                if (looked.Add(t) && Wanted(t, adjectives)) {
                    wanted.Add(t);
                }

                if (wanted.Contains(t)) {
                    list.Add(zone);
                    break;
                }
            }
        }

        return list;
    }

    /// <summary>
    /// The area to go to for a creature step that names no area, from <paramref name="fromZone"/>: of the areas whose spawn
    /// files bring it out (not this one), the street areas before dungeon copies, this world's before others, then the fewest
    /// areas on the way. "" when the book does not know one, or none can be reached by the route book.
    /// </summary>
    internal static string Nearest(string fromZone, IReadOnlyCollection<string> adjectives) {
        if (!Ready || string.IsNullOrWhiteSpace(fromZone) || adjectives is not { Count: > 0 }) {
            return "";
        }

        var key = fromZone.Trim() + "|" + string.Join(",", adjectives.OrderBy(a => a, StringComparer.Ordinal));
        if (s_asked.TryGetValue(key, out var was) && DateTime.UtcNow - was.At < s_keep) {
            return was.Zone;
        }

        var answer = "";
        try {
            var world = fromZone.Trim().Split('/')[0];
            answer = AreasWith(adjectives)
                .Where(z => !OverlayQuestTracker.SameZone(z, fromZone) && !OverlayQuestTracker.SameZone(fromZone, z))
                .Select(z => (Zone: z, Hops: Hops(fromZone, z)))
                .Where(c => c.Hops > 0)
                .OrderBy(c => OverlayQuestRoute.IsDungeon(c.Zone) ? 1 : 0)
                .ThenBy(c => string.Equals(c.Zone.Split('/')[0], world, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(c => c.Hops)
                .ThenBy(c => c.Zone, StringComparer.OrdinalIgnoreCase)
                .Select(c => c.Zone).FirstOrDefault() ?? "";
        }
        catch (Exception ex) {
            Logger.Debug("Bots: the creature-area book could not answer for {0}: {1}", Logger.Args(fromZone, ex.Message));
        }

        s_asked[key] = (answer, DateTime.UtcNow);

        return answer;
    }

    // Areas on the way, by the route book (0 when it knows no way).
    private static int Hops(string from, string to) {
        try {
            return OverlayQuestRoute.Way(from, to, false, storyOnly: !BotBookAreas.IsSideWorld(to)).Count;
        }
        catch (Exception) {
            return 0;
        }
    }

    private static bool Wanted(ulong template, IReadOnlyCollection<string> adjectives) {
        if (Quests.BountyStandIns.Counts(adjectives, template)) {
            return true;
        }

        var own = (CoreObjectFactory.GetCoreTemplate(template) as GameObjectTemplate)?.m_adjectiveList;

        return own?.Any(a => adjectives.Contains((string) a, StringComparer.Ordinal)) == true;
    }

    /// <summary>Builds the book on a background thread (or reads it from its file), one area at a time.</summary>
    internal static void BuildInBackground() {
        if (Interlocked.Exchange(ref s_building, 1) == 1) {
            return;
        }

        _ = Task.Run(async () => {
            try {
                var path = Path.Combine(Ultimus.UltimusWire.SharedFolder, BOOK_FILE);
                if (BotBookAreas.Fresh(path)
                    && Ultimus.UltimusWire.Read<Dictionary<string, ulong[]>>(await File.ReadAllTextAsync(path)) is { Count: > 0 } saved) {
                    foreach (var (zone, templates) in saved) {
                        s_book[zone] = templates ?? [];
                    }

                    Ready = true;

                    return;
                }

                foreach (var zone in BotBookAreas.All()) {
                    try {
                        // The templates the area's spawners bring out (its spawnData file, as the server itself reads it).
                        if (QuestStepSpots.Zone(zone).Spawned is { Length: > 0 } spawned) {
                            s_book[zone] = [.. spawned.Where(IsCreature).Distinct().OrderBy(t => t)];
                        }
                    }
                    catch (Exception ex) {
                        Logger.Debug("Bots: the creature-area book could not read {0}: {1}", Logger.Args(zone, ex.Message));
                    }

                    await Task.Delay(10);
                }

                foreach (var empty in s_book.Where(p => p.Value.Length == 0).Select(p => p.Key).ToList()) {
                    s_book.TryRemove(empty, out _);
                }

                Ready = true;
                Directory.CreateDirectory(Ultimus.UltimusWire.SharedFolder);
                await File.WriteAllTextAsync(path, Ultimus.UltimusWire.Write(s_book.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(p => p.Key, p => p.Value)));
                BotBookAreas.Mark(path);
                await File.WriteAllTextAsync(Path.Combine(Ultimus.UltimusWire.SharedFolder, REPORT_FILE), Report(DateTime.Now));
                var (areas, creatures) = Count();
                Logger.Information("Bots: the creature-area book has {0} areas and {1} kinds of creature ({2}).",
                    Logger.Args(areas, creatures, BOOK_FILE));
            }
            catch (Exception ex) {
                Logger.Debug("Bots: the creature-area book could not be built: {0}", Logger.Args(ex.Message));
                Interlocked.Exchange(ref s_building, 0);
            }
        });
    }

    // A creature: a template with a creature's battle behavior (not a wisp, a reagent or a prop on a path).
    private static bool IsCreature(ulong template)
        => CoreObjectFactory.GetCoreTemplate(template) is GameObjectTemplate t
           && t.m_behaviors?.OfType<NPCBehaviorTemplate>().Any() == true;

    private static string Report(DateTime written) {
        var text = new StringBuilder();
        var (areas, creatures) = Count();
        text.AppendLine($"CREATURE-AREA BOOK (written {written:yyyy-MM-dd HH:mm}): {areas} areas, {creatures} kinds of creature, " +
                        "read from each area's own spawn files. Bots use it for a \"defeat\" step that names no area.");
        foreach (var world in s_book.Keys.GroupBy(z => z.Split('/')[0], StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)) {
            text.AppendLine();
            text.AppendLine(MainWorlds.Name(world.Key).ToUpperInvariant());
            foreach (var zone in world.OrderBy(z => z, StringComparer.OrdinalIgnoreCase)) {
                var names = s_book[zone].Select(NameOf).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
                text.AppendLine($"  {Shown(zone)}: {string.Join(", ", names)}");
            }
        }

        return text.ToString();
    }

    private static string NameOf(ulong template) {
        try {
            return CoreObjectFactory.GetCoreTemplate(template) is GameObjectTemplate t ? MinimapIndex.NameOf(t) ?? "" : "";
        }
        catch (Exception) {
            return "";
        }
    }

    private static string Shown(string zone) {
        try {
            return $"{Ultimus.UltimusText.Zone(zone)} ({zone})";
        }
        catch (Exception) {
            return zone;
        }
    }
}
