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
 *
 * ========================================================================
 * ID CATALOG
 * ========================================================================
 *
 * PURPOSE:
 * Builds a searchable list of every item, spell, creature and NPC template ID
 * so server owners can find the IDs that commands such as `mod additem` and
 * `sb learn` expect.
 *
 * USAGE EXAMPLE:
 * IdCatalog.StartBackgroundBuild();
 * var matches = IdCatalog.Search(IdCatalogKind.Spell, "fire cat", 10);
 *
 * NOTE:
 * - Building reads every template from the game client's archives, so it runs
 *   in the background and is cached on disk per game revision.
 * - The output folder holds ids.csv (opens in a spreadsheet) and index.html
 *   (opens in a browser with a search box), plus zones.txt and quests.csv,
 *   which the app's Lookup tab reads alongside ids.csv, and creatures.csv
 *   (each creature's health, stats and deck), which the app's Bosses tab reads.
 *
 * TODO:
 *
 * Version: KALI 1.0
 * Last Updated: 10/18/2026
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;

namespace Imlight.CoreLib.Shared.Resources;

/// <summary>
/// The kinds of templates listed in the <see cref="IdCatalog"/>.
/// </summary>
public enum IdCatalogKind {
    Item,
    Spell,
    Creature,
    Npc,
}

/// <summary>
/// One template listed in the <see cref="IdCatalog"/>.
/// </summary>
public sealed record IdCatalogEntry(ulong Id, IdCatalogKind Kind, string Name, string School, int Level, string Path);

/// <summary>
/// A searchable list of item, spell, creature and NPC template IDs.
/// </summary>
public static class IdCatalog {

    private const string CSV_NAME = "ids.csv";
    private const string HTML_NAME = "index.html";
    private const string REVISION_NAME = "revision.txt";
    private const string ZONES_NAME = "zones.txt";
    private const string QUESTS_NAME = "quests.csv";
    private const string PHRASES_NAME = "phrases.csv";
    private const string BUNDLES_NAME = "bundles.csv";
    private const string CREATURES_NAME = "creatures.csv";
    // Grid: the game's value of each row of the Bosses tab's school grid (BossOverrides.GameGrid).
    private const string CREATURES_HEADER = "Id,Name,School,Level,Health,Boss,Spells,Stats,Grid";
    private const string CSV_HEADER = "Id,Kind,Name,School,Level,Path";
    private const int INFORM_ALIVE_INTERVAL = 10000;

    private static readonly bool s_buildOnBoot
        = ConfigurationManager.GetValue("ID Lookup.BuildOnBoot", true);
    private static readonly string s_outputPath
        = ConfigurationManager.GetValue("ID Lookup.OutputPath", "./id-lookup");
    private static readonly string s_gameRevision
        = ConfigurationManager.Settings["Global Settings.GameRevision"].AsString();

    private static volatile IReadOnlyList<IdCatalogEntry> s_entries;

    /// <summary>
    /// True once the catalog has been built or loaded and can be searched.
    /// </summary>
    public static bool IsReady => s_entries is not null;

    /// <summary>
    /// The folder the catalog files are written to.
    /// </summary>
    public static string OutputPath => Path.GetFullPath(s_outputPath);

    /// <summary>
    /// Loads the catalog from disk if it matches the current game revision, otherwise builds
    /// it from the game data. Runs in the background so it never delays the server.
    /// </summary>
    public static void StartBackgroundBuild() {
        if (!s_buildOnBoot) {
            Logger.Information("ID lookup is turned off in the configuration.");

            return;
        }

        _ = Task.Run(WriteZonesAndQuests);
        // The Customs tab's vendor and trainer presets, worked out from the game files each boot.
        _ = Task.Run(() => Imlight.CoreLib.Game.World.CustomNpcPresets.Write(OutputPath));
        _ = Task.Run(() => {
            try {
                if (TryLoadFromDisk()) {
                    Logger.Information("ID lookup loaded {Count} IDs from {Path}.",
                        Logger.Args(s_entries.Count, OutputPath));

                    return;
                }

                Logger.Information("Building the ID lookup. This happens once per game revision and may take a few minutes.");
                var entries = Build(out var creatures);
                WriteToDisk(entries, creatures);
                s_entries = entries;

                Logger.Information("ID lookup is ready with {Count} IDs. Open {Path} to browse them.",
                    Logger.Args(entries.Count, Path.Combine(OutputPath, HTML_NAME)));
            }
            catch (Exception ex) {
                Logger.Error("Could not build the ID lookup. {Exception}", Logger.Args(ex));
            }
        });
    }

    /// <summary>
    /// Finds catalog entries whose name contains every word of the query.
    /// </summary>
    /// <param name="kind">The kind to search, or null to search every kind.</param>
    /// <param name="query">The words to look for, or an ID.</param>
    /// <param name="maxResults">The most entries to return.</param>
    /// <returns>Matching entries, exact name matches first.</returns>
    public static List<IdCatalogEntry> Search(IdCatalogKind? kind, string query, int maxResults) {
        var entries = s_entries;
        if (entries is null || string.IsNullOrWhiteSpace(query)) {
            return [];
        }

        query = query.Trim();
        var pool = kind is null ? entries : entries.Where(e => e.Kind == kind);

        // An ID returns the template with that ID.
        if (ulong.TryParse(query, out var id)) {
            return [.. pool.Where(e => e.Id == id).Take(maxResults)];
        }

        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return [.. pool
            .Where(e => words.All(w => e.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e => !e.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(e => e.Name.Length)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)];
    }

    private static List<IdCatalogEntry> Build(out List<string> creatures) {
        var entries = new List<IdCatalogEntry>();
        creatures = [];
        var counter = 0;

        foreach (var location in CoreObjectFactory.TemplateManifest.m_serializedTemplates) {
            if (location is null) {
                continue;
            }

            if (++counter % INFORM_ALIVE_INTERVAL == 0) {
                Logger.Information("ID lookup has read {Count} templates..", Logger.Args(counter));
            }

            string path = location.m_filename;
            try {
                // Load without caching: holding every template in memory is not worth it for a lookup.
                var template = CoreObjectFactory.LoadTemplateFile(path);
                var entry = CreateEntry(location.m_id, path, template);
                if (entry is not null) {
                    entries.Add(entry);
                    if (entry.Kind == IdCatalogKind.Creature && CreatureRow(entry, template) is { } row) {
                        creatures.Add(row);
                    }
                }
            }
            catch (Exception ex) {
                Logger.Debug("ID lookup skipped template {Id} ({Path}). {Message}",
                    Logger.Args(location.m_id, path, ex.Message));
            }
        }

        return [.. entries.OrderBy(e => e.Kind).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static IdCatalogEntry CreateEntry(ulong id, string path, CoreTemplate template) {
        switch (template) {
            case SpellTemplate spell: {
                string displayKey = spell.m_displayName;
                string internalName = spell.m_name;
                string school = spell.m_sMagicSchoolName;

                return new(id, IdCatalogKind.Spell, NameOf(displayKey, internalName), school ?? "", 0, path);
            }
            case ItemTemplate item: {
                string displayKey = item.m_displayName;
                string objectName = item.m_objectName;
                string school = template is WizItemTemplate wizItem ? wizItem.m_school : "";

                return new(id, IdCatalogKind.Item, NameOf(displayKey, objectName), school ?? "", 0, path);
            }
            case GameObjectTemplate gameObject: {
                var npcBehavior = gameObject.m_behaviors.OfType<NPCBehaviorTemplate>().FirstOrDefault();
                if (npcBehavior is null) {
                    return null;
                }

                string displayKey = gameObject.m_displayName;
                string objectName = gameObject.m_objectName;
                var name = NameOf(displayKey, objectName);

                // Anything that can duel is a creature; the rest are townsfolk.
                if (gameObject.m_behaviors.OfType<DuelistBehaviorTemplate>().Any()) {
                    string school = npcBehavior.m_schoolOfFocus;

                    return new(id, IdCatalogKind.Creature, name, school ?? "", (int) npcBehavior.m_nLevel, path);
                }

                return new(id, IdCatalogKind.Npc, name, "", 0, path);
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// A creature's line in creatures.csv: what it fights with before any change from the app's Bosses tab.
    /// </summary>
    private static string CreatureRow(IdCatalogEntry entry, CoreTemplate template) {
        if (template is not GameObjectTemplate gameObject
            || gameObject.m_behaviors.OfType<NPCBehaviorTemplate>().FirstOrDefault() is not { } npc) {
            return null;
        }

        try {
            var spells = Imlight.CoreLib.Game.Zone.Components.CombatCreatureDeckComponent.GameSpellIds(gameObject);
            var stats = Imlight.CoreLib.Game.Combat.BossOverrides.GameStatsOf(gameObject, npc);

            return string.Join(',',
                entry.Id.ToString(CultureInfo.InvariantCulture),
                CsvField(entry.Name),
                CsvField(entry.School),
                entry.Level.ToString(CultureInfo.InvariantCulture),
                npc.m_nStartingHealth.ToString(CultureInfo.InvariantCulture),
                npc.m_bossMob ? "1" : "0",
                string.Join(' ', spells.Select(id => id.ToString(CultureInfo.InvariantCulture))),
                CsvField(Imlight.CoreLib.Game.Combat.BossOverrides.Describe(stats)),
                CsvField(Imlight.CoreLib.Game.Combat.BossOverrides.GameGrid(stats)));
        }
        catch (Exception ex) {
            Logger.Debug("ID lookup left creature {Id} out of the creature list. {Message}", Logger.Args(entry.Id, ex.Message));

            return null;
        }
    }

    private static string NameOf(string displayKey, string fallback) {
        var englishName = string.IsNullOrEmpty(displayKey) ? "" : Locale.GetEnglishName(displayKey);

        return string.IsNullOrEmpty(englishName) ? fallback ?? "" : englishName;
    }

    // ---------------------------------------------------------------------
    // Disk cache
    // ---------------------------------------------------------------------

    private static bool TryLoadFromDisk() {
        var revisionFile = Path.Combine(OutputPath, REVISION_NAME);
        var csvFile = Path.Combine(OutputPath, CSV_NAME);
        // A list from before the Bosses tab (or before its school grid) has no creatures.csv of this kind, so it is
        // built again once.
        var creaturesFile = Path.Combine(OutputPath, CREATURES_NAME);
        if (!File.Exists(revisionFile) || !File.Exists(csvFile) || !File.Exists(creaturesFile)
            || File.ReadLines(creaturesFile).FirstOrDefault() != CREATURES_HEADER) {
            return false;
        }

        if (File.ReadAllText(revisionFile).Trim() != s_gameRevision) {
            return false;
        }

        var entries = new List<IdCatalogEntry>();
        foreach (var line in File.ReadLines(csvFile).Skip(1)) {
            var fields = ParseCsvLine(line);
            if (fields.Count != 6
                || !ulong.TryParse(fields[0], out var id)
                || !Enum.TryParse(fields[1], out IdCatalogKind kind)
                || !int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var level)) {
                // A damaged file is rebuilt rather than half loaded.
                return false;
            }

            entries.Add(new(id, kind, fields[2], fields[3], level, fields[5]));
        }

        s_entries = entries;

        return true;
    }

    private static void WriteToDisk(List<IdCatalogEntry> entries, List<string> creatures) {
        Directory.CreateDirectory(OutputPath);

        var csv = new StringBuilder().AppendLine(CSV_HEADER);
        foreach (var e in entries) {
            csv.Append(e.Id.ToString(CultureInfo.InvariantCulture)).Append(',')
               .Append(e.Kind).Append(',')
               .Append(CsvField(e.Name)).Append(',')
               .Append(CsvField(e.School)).Append(',')
               .Append(e.Level.ToString(CultureInfo.InvariantCulture)).Append(',')
               .AppendLine(CsvField(e.Path));
        }
        File.WriteAllText(Path.Combine(OutputPath, CSV_NAME), csv.ToString());
        File.WriteAllText(Path.Combine(OutputPath, HTML_NAME), BuildHtml(entries));
        File.WriteAllLines(Path.Combine(OutputPath, CREATURES_NAME), [CREATURES_HEADER, .. creatures]);

        // Written last so an interrupted build is redone on the next boot.
        File.WriteAllText(Path.Combine(OutputPath, REVISION_NAME), s_gameRevision);
    }

    /// <summary>
    /// Writes the zone names the teleport command takes and the quest names the quest commands take.
    /// They are quick to read, so they are rewritten on every boot.
    /// </summary>
    private static void WriteZonesAndQuests() {
        try {
            Directory.CreateDirectory(OutputPath);
            var zones = Imlight.CoreLib.Game.World.AccessPassManager.Zones.OrderBy(z => z, StringComparer.OrdinalIgnoreCase);
            File.WriteAllLines(Path.Combine(OutputPath, ZONES_NAME), zones);

            var quests = new StringBuilder().AppendLine("Name,Title");
            foreach (var quest in Imlight.CoreLib.WizardData.Collections.QuestTemplateCollection.GetAllQuests()) {
                string name = quest.m_questName;
                string titleKey = quest.m_questTitle;
                if (string.IsNullOrEmpty(name)) {
                    continue;
                }

                quests.Append(CsvField(name)).Append(',').AppendLine(CsvField(NameOf(titleKey, "")));
            }
            File.WriteAllText(Path.Combine(OutputPath, QUESTS_NAME), quests.ToString());

            // The game's own lines for .ca, numbered (Niduus, 2026-10-20: `.ca 1` plays the first phrase).
            var phrases = new StringBuilder().AppendLine("Number,Text,Key");
            var number = 0;
            foreach (var (key, text) in Locale.Phrases) {
                phrases.Append(++number).Append(',').Append(CsvField(text)).Append(',').AppendLine(CsvField(key));
            }
            File.WriteAllText(Path.Combine(OutputPath, PHRASES_NAME), phrases.ToString());
        }
        catch (Exception ex) {
            Logger.Warning("Could not write the zone and quest lists for the app. {Exception}", Logger.Args(ex));
        }

        // The bundle list for the app's Lookup tab (Niduus, 2026-10-21: the list moved out of the game's chat).
        try {
            var bundles = new StringBuilder().AppendLine("Name,Gifts");
            foreach (var family in Imlight.CoreLib.Game.Commands.Protocols.BundleCatalog.Families) {
                bundles.Append(CsvField(family.Name)).Append(',').Append(family.Gifts.Count).AppendLine();
            }
            File.WriteAllText(Path.Combine(OutputPath, BUNDLES_NAME), bundles.ToString());
        }
        catch (Exception ex) {
            Logger.Warning("Could not write the bundle list for the app. {Exception}", Logger.Args(ex));
        }
    }

    private static string CsvField(string value) {
        value ??= "";
        if (value.IndexOfAny([',', '"', '\n', '\r']) < 0) {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static List<string> ParseCsvLine(string line) {
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++) {
            var c = line[i];
            if (inQuotes) {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') {
                    field.Append('"');
                    i++;
                }
                else if (c == '"') {
                    inQuotes = false;
                }
                else {
                    field.Append(c);
                }
            }
            else if (c == '"') {
                inQuotes = true;
            }
            else if (c == ',') {
                fields.Add(field.ToString());
                field.Clear();
            }
            else {
                field.Append(c);
            }
        }
        fields.Add(field.ToString());

        return fields;
    }

    private static string BuildHtml(List<IdCatalogEntry> entries) {
        // Rows are [id, kind, name, school, level]; the id is a string so large ids keep every digit.
        // The serializer escapes '<' and '>', so the data cannot close the script tag.
        var rows = entries.Select(e => new object[] {
            e.Id.ToString(CultureInfo.InvariantCulture), e.Kind.ToString(), e.Name, e.School, e.Level
        });
        var json = JsonSerializer.Serialize(rows);

        return HTML_TEMPLATE
            .Replace("{{REVISION}}", System.Net.WebUtility.HtmlEncode(s_gameRevision))
            .Replace("{{DATA}}", json);
    }

    private const string HTML_TEMPLATE = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Imlight ID Lookup</title>
<style>
  :root { color-scheme: light dark; --bg: #f7f7f8; --fg: #1d1d1f; --muted: #6b6b70; --line: #dcdce0; --accent: #5b3cc4; --row: #ffffff; }
  @media (prefers-color-scheme: dark) { :root { --bg: #17171a; --fg: #ececf0; --muted: #9a9aa2; --line: #2e2e34; --accent: #a58cff; --row: #1f1f23; } }
  * { box-sizing: border-box; }
  body { margin: 0; padding: 16px; font: 15px/1.4 system-ui, sans-serif; background: var(--bg); color: var(--fg); }
  h1 { font-size: 20px; margin: 0 0 4px; }
  p { margin: 0 0 12px; color: var(--muted); }
  .bar { display: flex; flex-wrap: wrap; gap: 8px; margin-bottom: 12px; }
  input, select { font: inherit; padding: 8px 10px; border: 1px solid var(--line); border-radius: 6px; background: var(--row); color: var(--fg); }
  input { flex: 1 1 240px; }
  table { width: 100%; border-collapse: collapse; background: var(--row); }
  th, td { text-align: left; padding: 6px 8px; border-bottom: 1px solid var(--line); }
  th { position: sticky; top: 0; background: var(--row); }
  td.id { font-family: ui-monospace, monospace; white-space: nowrap; }
  button { font: inherit; border: 1px solid var(--line); border-radius: 6px; background: transparent; color: var(--accent); cursor: pointer; padding: 2px 8px; }
  #count { color: var(--muted); margin-bottom: 8px; }
</style>
</head>
<body>
<h1>Imlight ID Lookup</h1>
<p>Game revision {{REVISION}}. Search by name, then copy a command into the in-game chat.</p>
<div class="bar">
  <input id="q" type="search" placeholder="Search names or IDs, e.g. fire cat" autofocus>
  <select id="kind">
    <option value="">Everything</option>
    <option>Item</option>
    <option>Spell</option>
    <option>Creature</option>
    <option>Npc</option>
  </select>
</div>
<div id="count"></div>
<table>
  <thead><tr><th>ID</th><th>Kind</th><th>Name</th><th>School</th><th>Level</th><th>Command</th></tr></thead>
  <tbody id="rows"></tbody>
</table>
<script>
const DATA = {{DATA}};
const LIMIT = 500;
const q = document.getElementById('q');
const kind = document.getElementById('kind');
const rows = document.getElementById('rows');
const count = document.getElementById('count');

function commandFor(row) {
  if (row[1] === 'Item') return '.mod additem ' + row[0];
  if (row[1] === 'Spell') return '.sb learn ' + row[0];
  return '';
}

function render() {
  const words = q.value.trim().toLowerCase().split(/\s+/).filter(Boolean);
  const k = kind.value;
  const matches = DATA.filter(r => (!k || r[1] === k)
    && words.every(w => r[0] === w || r[2].toLowerCase().includes(w)));
  count.textContent = matches.length > LIMIT
    ? 'Showing the first ' + LIMIT + ' of ' + matches.length + ' matches. Type more to narrow it down.'
    : matches.length + ' matches';
  rows.replaceChildren(...matches.slice(0, LIMIT).map(r => {
    const tr = document.createElement('tr');
    const cells = [r[0], r[1], r[2], r[3], r[4] || ''];
    cells.forEach((v, i) => {
      const td = document.createElement('td');
      if (i === 0) td.className = 'id';
      td.textContent = v;
      tr.appendChild(td);
    });
    const td = document.createElement('td');
    const cmd = commandFor(r);
    if (cmd) {
      const b = document.createElement('button');
      b.textContent = cmd;
      b.title = 'Copy';
      b.onclick = () => navigator.clipboard && navigator.clipboard.writeText(cmd).then(() => { b.textContent = 'Copied'; setTimeout(() => b.textContent = cmd, 1200); });
      td.appendChild(b);
    }
    tr.appendChild(td);
    return tr;
  }));
}

q.addEventListener('input', render);
kind.addEventListener('change', render);
render();
</script>
</body>
</html>
""";

}
