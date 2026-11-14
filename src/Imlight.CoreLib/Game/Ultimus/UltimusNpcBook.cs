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
using System.Threading;
using System.Threading.Tasks;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Game.Madlibs;
using Imlight.CoreLib.Game.Services;
using Imlight.CoreLib.Game.Tools;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// The NPC location book (Niduus, 2026-11-14, approved): the exact spot (area, x, y, z) of every NPC the main story's quests
/// name, as givers or as people to talk to, from the game's own zone files. Ultimus looks a person up here first, as a
/// player who knows the story knows where they stand, instead of searching every area in the background. Written to
/// logs/ultimus/npc-book.json (read again at each server start, built again after 3 days). Read-only.
/// </summary>
internal static class UltimusNpcBook {

    internal sealed record Place(string Zone, float X, float Y, float Z);

    internal sealed record Entry(string Name, List<Place> Places);

    private const string BOOK_FILE = "npc-book.json";
    private static readonly ConcurrentDictionary<ulong, Entry> s_book = new();
    private static int s_building;

    /// <summary>Whether the book is read or built.</summary>
    internal static bool Ready { get; private set; }

    /// <summary>The places an NPC stands, nearest-world first (none when the book does not know them).</summary>
    internal static List<Place> Of(ulong npc, string fromZone = "") {
        if (npc == 0 || !s_book.TryGetValue(npc, out var entry)) {
            return [];
        }

        var world = (fromZone ?? "").Split('/')[0];

        return [.. entry.Places.OrderByDescending(p => string.Equals(p.Zone.Split('/')[0], world, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => OverlayQuestTracker.SameZone(p.Zone, fromZone))];
    }

    /// <summary>The spot of an NPC in this very area, or null.</summary>
    internal static Place In(ulong npc, string zone)
        => npc == 0 || !s_book.TryGetValue(npc, out var entry) ? null
            : entry.Places.FirstOrDefault(p => OverlayQuestTracker.SameZone(p.Zone, zone) || OverlayQuestTracker.SameZone(zone, p.Zone));

    /// <summary>The NPC a player named ("Headmaster Ambrose", "ambrose"): an exact name first, then a name that holds it.</summary>
    internal static Entry Find(string name) {
        var wanted = (name ?? "").Trim().TrimEnd('?', '!', '.').Trim();
        if (wanted.StartsWith("the ", StringComparison.OrdinalIgnoreCase)) {
            wanted = wanted[4..];
        }

        if (wanted.Length < 3) {
            return null;
        }

        var entries = s_book.Values.Where(e => !string.IsNullOrWhiteSpace(e.Name) && e.Places.Count > 0).ToList();

        return entries.FirstOrDefault(e => string.Equals(e.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? entries.Where(e => e.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.Name.Length).FirstOrDefault();
    }

    /// <summary>The NPCs whose names a text says (whole words), longest name first: for hints from a quest's words.</summary>
    internal static List<(ulong Id, Entry Entry)> Named(string text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return [];
        }

        return [.. s_book.Where(p => p.Value.Name is { Length: >= 4 } name && p.Value.Places.Count > 0 && BotShared.MenuChatBook.Says(text, name))
            .OrderByDescending(p => p.Value.Name.Length).Select(p => (p.Key, p.Value))];
    }

    internal static int Count => s_book.Count;

    /// <summary>Builds the book on a background thread (or reads it from its file), one area at a time.</summary>
    internal static void BuildInBackground() {
        if (Interlocked.Exchange(ref s_building, 1) == 1) {
            return;
        }

        _ = Task.Run(async () => {
            try {
                var path = Path.Combine(UltimusWire.Folder, BOOK_FILE);
                if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromDays(3)
                    && UltimusWire.Read<Dictionary<string, Entry>>(await File.ReadAllTextAsync(path)) is { Count: > 0 } saved) {
                    foreach (var (id, entry) in saved) {
                        if (ulong.TryParse(id, out var template) && entry is not null) {
                            s_book[template] = entry;
                        }
                    }

                    Ready = true;

                    return;
                }

                // Who the story names: each main-story quest's giver and each person its steps send you to talk to.
                var wanted = new HashSet<ulong>();
                foreach (var (quest, giver) in QuestService.QuestFinderGivers().Where(g => UltimusPlanner.IsMainStoryWorld(g.Quest))) {
                    if (giver != 0) {
                        wanted.Add(giver);
                    }

                    foreach (var persona in (quest.m_goals ?? []).OfType<PersonaGoalTemplate>()) {
                        var npc = QuestMadlibs.SpeakerTemplateOf(persona.m_personaName, 0);
                        if (npc != 0) {
                            wanted.Add(npc);
                        }
                    }
                }

                var zones = SpiralDB.GetAllZoneData().Select(z => z?.ZoneName).Where(z => !string.IsNullOrWhiteSpace(z) && OverlayQuestRoute.OnStory(z))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var found = new Dictionary<ulong, List<Place>>();
                foreach (var zone in zones) {
                    foreach (var (template, spot) in QuestStepSpots.Zone(zone).Objects.Where(o => wanted.Contains(o.Template))) {
                        if (!found.TryGetValue(template, out var list)) {
                            found[template] = list = [];
                        }

                        list.Add(new Place(zone, spot.X, spot.Y, spot.Z));
                    }

                    await Task.Delay(10);
                }

                foreach (var (template, places) in found) {
                    var name = CoreObjectFactory.GetCoreTemplate(template) is GameObjectTemplate t ? MinimapIndex.NameOf(t) : "";
                    s_book[template] = new Entry(name, places);
                }

                Ready = true;
                Directory.CreateDirectory(UltimusWire.Folder);
                await File.WriteAllTextAsync(path, UltimusWire.Write(s_book.OrderBy(p => p.Value.Name).ToDictionary(p => p.Key.ToString(), p => p.Value)));
                Logger.Information("Ultimus: the NPC location book has {0} of the story's {1} NPCs ({2}).", Logger.Args(s_book.Count, wanted.Count, BOOK_FILE));
            }
            catch (Exception ex) {
                Logger.Debug("Ultimus: the NPC location book could not be built: {0}", Logger.Args(ex.Message));
            }
        });
    }

}
