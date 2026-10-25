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
 *
 * ========================================================================
 * NPC CHANGES
 * ========================================================================
 *
 * PURPOSE:
 * Changes to the game's own vendors and trainers, from the app's Customs tab (Niduus, 2026-10-25: "add custom options
 * for existing vendors and trainers (In detail)"), read from spiraldb-extra/Customs/NpcChanges.json. The game files are
 * never changed. Each entry is one NPC template (every copy of it in the game):
 *   TemplateID   the NPC.
 *   Name         a note for the app (its name); not used by the server.
 *   Shop         HideShop: no shop at all. ShopMode Add (its own items, with these added) or Replace (only these).
 *                Items: ID (item template) and Price (gold; 0 keeps the item's own price), which also sets the price of
 *                an item it already sells. RemoveItems: item ids it no longer sells. Window: the shop window (as a
 *                custom vendor's: Equipment, Furniture, Mount, Snack, Reagent, Jewel, Combined, Automatic), or empty
 *                for its own.
 *   Treasure     HideTreasureCards, TreasureCardMode Add or Replace, TreasureCards (ID: the spell, Price: gold),
 *                RemoveTreasureCards (spell ids).
 *   Training     HideTraining, SpellMode Add or Replace, Spells (ID: the spell, Price: training points; -1 keeps the
 *                game's own cost), RemoveSpells (spell ids).
 * The file is read again whenever it changes, and each NPC uses the new changes the next time a player talks to it.
 * An NPC that had no shop or spell list at all gets one when its zone is next loaded.
 *
 * Created by: Claude
 * Version: KALI 1.0
 * Last Updated: 10/25/2026
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;
using Imlight.CoreLib.WizardData;

namespace Imlight.CoreLib.Game.World;

internal static class NpcChanges {

    internal const string ADD = "Add";
    internal const string REPLACE = "Replace";

    internal sealed class Entry {
        public uint ID { get; set; }
        public int Price { get; set; }
    }

    internal sealed class Change {
        public uint TemplateID { get; set; }
        public string Name { get; set; } = "";

        public bool HideShop { get; set; }
        public string ShopMode { get; set; } = ADD;
        public string Window { get; set; } = "";
        public List<Entry> Items { get; set; } = [];
        public List<uint> RemoveItems { get; set; } = [];

        public bool HideTreasureCards { get; set; }
        public string TreasureCardMode { get; set; } = ADD;
        public List<Entry> TreasureCards { get; set; } = [];
        public List<uint> RemoveTreasureCards { get; set; } = [];

        public bool HideTraining { get; set; }
        public string SpellMode { get; set; } = ADD;
        public List<Entry> Spells { get; set; } = [];
        public List<uint> RemoveSpells { get; set; } = [];

        internal bool ReplacesItems => string.Equals(ShopMode, REPLACE, StringComparison.OrdinalIgnoreCase);
        internal bool ReplacesTreasureCards => string.Equals(TreasureCardMode, REPLACE, StringComparison.OrdinalIgnoreCase);
        internal bool ReplacesSpells => string.Equals(SpellMode, REPLACE, StringComparison.OrdinalIgnoreCase);
    }

    private const string FOLDER = "Customs";
    internal const string FILE = "NpcChanges.json";
    private static readonly TimeSpan s_checkEvery = TimeSpan.FromSeconds(1);

    private static readonly JsonSerializerOptions s_options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly object s_lock = new();
    private static Dictionary<uint, Change> s_changes = [];
    private static DateTime s_fileTime = DateTime.MinValue;
    private static DateTime s_lastCheck = DateTime.MinValue;
    private static int s_version;

    /// <summary>Goes up each time the file is read again, so an NPC knows to use its new changes.</summary>
    internal static int Version {
        get {
            Refresh();

            return s_version;
        }
    }

    /// <summary>The changes of an NPC template, or null.</summary>
    internal static Change For(uint templateId) {
        Refresh();

        return s_changes.TryGetValue(templateId, out var change) ? change : null;
    }

    private static void Refresh() {
        var now = DateTime.UtcNow;
        if (now - s_lastCheck < s_checkEvery) {
            return;
        }

        lock (s_lock) {
            if (now - s_lastCheck < s_checkEvery) {
                return;
            }

            s_lastCheck = now;
            var path = Path.Combine(SpiralDB.ExtraPath, FOLDER, FILE);
            var time = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (time == s_fileTime) {
                return;
            }

            s_fileTime = time;
            s_changes = Read(path);
            s_version++;
        }
    }

    private static Dictionary<uint, Change> Read(string path) {
        if (!File.Exists(path)) {
            return [];
        }

        try {
            var changes = new Dictionary<uint, Change>();
            foreach (var change in JsonSerializer.Deserialize<List<Change>>(File.ReadAllText(path), s_options) ?? []) {
                if (change is null || change.TemplateID == 0) {
                    continue;
                }

                change.Items = Clean(change.Items);
                change.TreasureCards = Clean(change.TreasureCards);
                change.Spells = Clean(change.Spells);
                change.RemoveItems ??= [];
                change.RemoveTreasureCards ??= [];
                change.RemoveSpells ??= [];
                changes[change.TemplateID] = change;
            }

            Logger.Information("NPC changes: {0} NPCs ({1}).", Logger.Args(changes.Count,
                string.Join(", ", changes.Values.Select(c => $"{c.TemplateID} {c.Name}"))));

            return changes;
        }
        catch (Exception e) {
            // The last good changes stay (the app writes the file whole, so this is a file broken by hand).
            Logger.Warning("NPC changes: {0} could not be read: {1}", Logger.Args(FILE, e.Message));

            return s_changes;
        }
    }

    private static List<Entry> Clean(List<Entry> entries)
        => [.. (entries ?? []).Where(e => e is not null && e.ID != 0).GroupBy(e => e.ID).Select(g => g.First())];

}
