/*
 * Our own quests in spiraldb-extra (Celestia onward, made from Wizard404, Niduus 2026-10-24) are written with the
 * game's English text: the quest title, what each NPC says, the step titles, places and "defeat X" names. The game
 * shows a quest's words by the key of its own line (WizQst1ED97_00000002), so at boot each English line is swapped for
 * the key of the game line that says the same thing. A line the game does not have is kept as it is.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.WizardData;

public static partial class QuestTextKeys {

    [GeneratedRegex(@"^[A-Za-z0-9\-]+_[A-Za-z0-9]+$")]
    private static partial Regex KeyPattern();

    // Longer than any NPC's name: a line of dialogue put where the name goes.
    private const int MAX_NAME_LENGTH = 48;

    private static bool IsText(string value)
        => !string.IsNullOrWhiteSpace(value) && !KeyPattern().IsMatch(value.Trim());

    /// <summary>Gives every quest line written in English the key of the game's own line. Runs once after SpiralDB loads.</summary>
    public static void ResolveAll(IReadOnlyList<QuestTemplate> quests) {
        try {
            Resolve(quests);
        } catch (Exception ex) {
            Logger.Warning("Quest text could not be matched to the game's lines: {0}", Logger.Args(ex.Message));
        }

        try {
            DropLinesTheGameLacks(quests);
        } catch (Exception ex) {
            Logger.Warning("Quest lines the game no longer has could not be left out: {0}", Logger.Args(ex.Message));
        } finally {
            Locale.ReleaseQuestTables();
        }

        try {
            ResolvePlaces(quests);
        } catch (Exception ex) {
            Logger.Warning("Quest places could not be matched to the game's places: {0}", Logger.Args(ex.Message));
        }
    }

    private static IEnumerable<NPCDialogEntry> DialogEntries(ActorDialogListBase list)
        => (list as ActorDialogList)?.m_dialogs?
            .SelectMany(d => d.m_dialogEntries ?? [])
            .OfType<NPCDialogEntry>() ?? [];

    private static void Resolve(IReadOnlyList<QuestTemplate> quests) {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        void Want(string value) {
            if (IsText(value)) {
                wanted.Add(Locale.PlainText(value));
            }
        }

        var ours = quests.Where(q => q is not null && (IsText(q.m_questTitle)
            || DialogEntries(q.m_dialogList).Any(e => IsText(e.m_dialog) || IsText(e.m_nameOverride))
            || q.m_goals.Any(g => DialogEntries(g.m_dialogList).Any(e => IsText(e.m_nameOverride)))
            || q.m_goals.Any(g => IsText(g.m_goalTitle)
                || IsText(g.m_tallyCounter?.m_descriptor) || IsText(g.m_tallyCounter?.m_descriptor2)
                || DialogEntries(g.m_dialogList).Any(e => IsText(e.m_dialog))))).ToList();
        if (ours.Count == 0) {
            return;
        }

        foreach (var quest in ours) {
            Want(quest.m_questTitle);
            foreach (var entry in DialogEntries(quest.m_dialogList)) {
                Want(entry.m_dialog);
                Want(entry.m_nameOverride);
            }

            foreach (var goal in quest.m_goals) {
                Want(goal.m_goalTitle);
                Want(goal.m_tallyCounter?.m_descriptor);
                Want(goal.m_tallyCounter?.m_descriptor2);
                foreach (var entry in DialogEntries(goal.m_dialogList)) {
                    Want(entry.m_dialog);
                    Want(entry.m_nameOverride);
                }
            }
        }

        var keys = Locale.KeysOfTexts(wanted);
        int matched = 0, kept = 0;

        string Pick(string value, string homeTable, params string[] tables) {
            if (!IsText(value)) {
                return value;
            }

            if (!keys.TryGetValue(Locale.PlainText(value), out var found) || found.Count == 0) {
                kept++;

                return value;
            }

            matched++;
            foreach (var table in (homeTable is null ? tables : tables.Prepend(homeTable))) {
                var key = found.FirstOrDefault(k => k.StartsWith(table + "_", StringComparison.Ordinal)
                    || table.EndsWith('*') && k.StartsWith(table.TrimEnd('*'), StringComparison.Ordinal));
                if (key is not null) {
                    return key;
                }
            }

            return found[0];
        }

        // The name over a line (m_nameOverride) is a key of the game's name tables; our quests have it written out
        // ("Old One"), which the game cannot show. A format key or a whole sentence there is no name at all: it goes,
        // and the line is named for its persona (QuestMadlibs.NameDialogSpeakers).
        string SpeakerName(string value) {
            if (string.IsNullOrEmpty(value) || value.StartsWith("NPCFormats_", StringComparison.Ordinal)) {
                return string.IsNullOrEmpty(value) ? value : "";
            }

            var picked = Pick(value, null, "NPCs", "WC-NPCs", "WCNameplates", "WizardMobs", "WizardGameObjects");

            return IsText(picked) && picked.Length > MAX_NAME_LENGTH ? "" : picked;
        }

        foreach (var quest in ours) {
            // The quest's own dialogue table is the one most of its lines are in.
            var allLines = DialogEntries(quest.m_dialogList)
                .Concat(quest.m_goals.SelectMany(g => DialogEntries(g.m_dialogList)))
                .Select(e => (string) e.m_dialog)
                .Concat(quest.m_goals.SelectMany(g => new[] { (string) g.m_tallyCounter?.m_descriptor, (string) g.m_tallyCounter?.m_descriptor2 }))
                .Where(IsText)
                .ToList();
            var home = allLines
                .SelectMany(line => keys.TryGetValue(Locale.PlainText(line), out var k) ? k.Select(x => x[..x.LastIndexOf('_')]).Distinct() : [])
                .Where(t => t.StartsWith("WizQst", StringComparison.Ordinal))
                .GroupBy(t => t)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault()?.Key;

            quest.m_questTitle = Pick(quest.m_questTitle, null, "QuestTitle", "QuestTitle*");
            foreach (var entry in DialogEntries(quest.m_dialogList)) {
                entry.m_dialog = Pick(entry.m_dialog, home, "WizQst*");
                entry.m_nameOverride = SpeakerName(entry.m_nameOverride);
            }

            foreach (var goal in quest.m_goals) {
                goal.m_goalTitle = Pick(goal.m_goalTitle, null, "WizardQuestGoals");
                if (goal.m_tallyCounter is { } tally) {
                    tally.m_descriptor = Pick(tally.m_descriptor, home, "WizardMobs", "WizItems", "WizardGameObjects", "WizQst*");
                    tally.m_descriptor2 = Pick(tally.m_descriptor2, home, "WizardMobs", "WizItems", "WizardGameObjects", "WizQst*");
                }

                foreach (var entry in DialogEntries(goal.m_dialogList)) {
                    entry.m_dialog = Pick(entry.m_dialog, home, "WizQst*");
                    entry.m_nameOverride = SpeakerName(entry.m_nameOverride);
                }
            }
        }

        Logger.Information("Quest text: {0} English lines of {1} quests got the game's own line, {2} the game does not have stay as written.",
            Logger.Args(matched, ours.Count, kept));
    }


    /// <summary>
    /// The captured quests still hold pages the game has since taken out (Lady Oriel's "Who could it be now?" has
    /// three: a line of hers and the two mount lessons of Gamma's that the free one-day broom went with). The game
    /// has no text for them, so their page showed an empty bar between Back and More (Niduus 2026-10-25). Such a page
    /// is left out, unless it sets something off (a dialog event or action) or it is all the dialog has.
    /// </summary>
    private static void DropLinesTheGameLacks(IReadOnlyList<QuestTemplate> quests) {
        int dropped = 0;
        void Trim(ActorDialogListBase list) {
            foreach (var dialog in (list as ActorDialogList)?.m_dialogs ?? []) {
                if (dialog?.m_dialogEntries is not { Count: > 1 } entries) {
                    continue;
                }

                var gone = entries.Where(e => e is NPCDialogEntry npc
                    && string.IsNullOrEmpty(npc.m_dialogEvent) && string.IsNullOrEmpty(npc.m_action)
                    && Locale.HasQuestLine(npc.m_dialog) == false).ToList();
                if (gone.Count == 0 || gone.Count == entries.Count) {
                    continue;
                }

                foreach (var entry in gone) {
                    entries.Remove(entry);
                }

                dropped += gone.Count;
            }
        }

        foreach (var quest in quests.Where(q => q is not null)) {
            Trim(quest.m_dialogList);
            foreach (var goal in quest.m_goals ?? []) {
                Trim(goal?.m_dialogList);
            }
        }

        Logger.Information("Quest text: left out {0} dialog pages the game no longer has a line for.", Logger.Args(dropped));
    }

    private const string PLACE_TABLE = "ZoneLocName";

    // A zone folder whose name is not spelled like its world in the place table.
    private static readonly Dictionary<string, string> s_worldSpellings = new(StringComparer.Ordinal) {
        ["dragonspire"] = "dragonspyre",
    };

    /// <summary>
    /// The quest book shows where a step is by a line that says "World|Place", mostly a key of the game's place table
    /// (ZoneLocName_00000002 is "Grizzleheim|Savarstaad Pass"); any other line shows as
    /// "ERROR: unknown zone. ERROR: unknown world." (Press Pass, Niduus 2026-10-24). Each step of our own quests gets
    /// the place of its world with the same name, else the place of its zone's name (CL_Z01i1_Survey_Camp is
    /// Survey Camp), else the place other steps in the same zone use, else in the same area of the world (an inside
    /// of CL_Z05 takes the place of CL_Z05), else the place the world's steps use most.
    /// </summary>
    private static void ResolvePlaces(IReadOnlyList<QuestTemplate> quests) {
        var places = Locale.Table(PLACE_TABLE);
        if (places is null || places.Count == 0) {
            return;
        }

        var byName = new Dictionary<(string World, string Place), string>();
        foreach (var (id, text) in places.OrderBy(p => p.Key, StringComparer.Ordinal)) {
            var bar = text?.IndexOf('|') ?? -1;
            if (bar > 0) {
                byName.TryAdd((Locale.PlainText(text[..bar]), Locale.PlainText(text[(bar + 1)..])), $"{PLACE_TABLE}_{id}");
            }
        }

        bool IsPlace(string value)
            => value is not null && value.StartsWith(PLACE_TABLE + "_", StringComparison.Ordinal)
               && places.ContainsKey(value[(PLACE_TABLE.Length + 1)..]);

        var goals = quests.Where(q => q?.m_goals is not null).SelectMany(q => q.m_goals).Where(g => g is not null).ToList();

        // First the steps whose place is named in the place table, by its own name or its zone's.
        var unplaced = new List<GoalTemplate>();
        foreach (var goal in goals) {
            // A step with no place shows none, as in the game. The game's own quests also name places by a line of
            // their dialogue table (WizQst1ED24_00000009 says "World|Place"); those are kept.
            string current = goal.m_locationName;
            if (string.IsNullOrEmpty(current) || IsPlace(current) || current.StartsWith("WizQst", StringComparison.Ordinal)) {
                continue;
            }

            var zone = (string) goal.m_destinationZone ?? "";
            var world = WorldOf(zone);
            var name = Locale.PlainText(Locale.GetEnglishName(current) is { Length: > 0 } shown ? shown : current);
            if ((name.Length > 0 && byName.TryGetValue((world, name), out var key))
                || byName.TryGetValue((world, ZoneWords(zone)), out key)) {
                goal.m_locationName = key;
            } else {
                unplaced.Add(goal);
            }
        }

        // The rest take the place most steps in the same zone, area or world use.
        var byZone = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        void Count(string group, string key) {
            if (!byZone.TryGetValue(group, out var counts)) {
                counts = [];
                byZone[group] = counts;
            }

            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        foreach (var goal in goals.Where(g => IsPlace(g.m_locationName))) {
            var zone = (string) goal.m_destinationZone ?? "";
            Count("zone:" + zone, goal.m_locationName);
            if (AreaOf(zone) is { } area) {
                Count("area:" + area, goal.m_locationName);
            }

            Count("world:" + WorldOf(zone), goal.m_locationName);
        }

        string Most(string group)
            => byZone.TryGetValue(group, out var counts) ? counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).First().Key : null;

        int kept = 0;
        foreach (var goal in unplaced) {
            var zone = (string) goal.m_destinationZone ?? "";
            var key = Most("zone:" + zone) ?? (AreaOf(zone) is { } area ? Most("area:" + area) : null) ?? Most("world:" + WorldOf(zone));
            if (key is null) {
                kept++;
                continue;
            }

            goal.m_locationName = key;
        }

        Logger.Information("Quest places: {0} steps got a place the game knows, {1} by the place their zone or world uses most; {2} have none.",
            Logger.Args(goals.Count(g => IsPlace(g.m_locationName)), unplaced.Count - kept, kept));
    }

    /// <summary>The world of a zone, as the place table spells it: Celestia/Interiors/CL_Z01i1_Survey_Camp is celestia.</summary>
    private static string WorldOf(string zone) {
        var world = Locale.PlainText(zone.Split('/')[0]);

        return s_worldSpellings.GetValueOrDefault(world, world);
    }

    /// <summary>The words of a zone's own name: CL_Z01i1_Survey_Camp is surveycamp.</summary>
    private static string ZoneWords(string zone)
        => Locale.PlainText(string.Join(" ", zone.Split('/')[^1].Split('_').Where(part => !ZoneCode().IsMatch(part))));

    /// <summary>The world and numbered area of a zone (CL_Z05 in Celestia), which its insides share; null when it has none.</summary>
    private static string AreaOf(string zone) {
        var match = AreaCode().Match(zone);

        return match.Success ? $"{WorldOf(zone)}/{match.Groups[1].Value.ToUpperInvariant()}" : null;
    }

    [GeneratedRegex(@"^([A-Z]{1,3}\d*|Z\d+\w*|I\d+|i\d+|\d+)$")]
    private static partial Regex ZoneCode();

    [GeneratedRegex(@"_(Z\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AreaCode();

}
