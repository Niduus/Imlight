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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Every creature Ultimus fought for a quest step, and how it went (Niduus, 2026-11-13: "I want Ultimus to learn from his
/// enemies that he's fought ... to make better decisions"): fights, wins, losses, how much of its health a fight took and
/// how long it lasted. A creature that beat it, or that takes most of its health, is a tough one: it goes in at full health
/// and mana. Kept in logs/ultimus/foes.json, so it is not lost when the server restarts or updates.
/// </summary>
internal static class UltimusFoes {

    private const string FILE = "foes.json";


    internal sealed class Foe {
        public uint Template { get; set; }
        public string Name { get; set; } = "";
        public int Fought { get; set; }
        public int Won { get; set; }
        public int Lost { get; set; }
        public double HealthLost { get; set; }
        public double Seconds { get; set; }
        public DateTime Last { get; set; }

        public double AverageHealthLost => Fought == 0 ? 0 : HealthLost / Fought;

        public bool Tough => Lost > 0 && Lost >= Won || (Fought > 0 && AverageHealthLost >= UltimusPlaybook.Value("Quest.ToughShare"));

        public string Describe() =>
            $"{Name}: fought {Fought} time(s), won {Won}, lost {Lost}; a fight takes about {AverageHealthLost:P0} of its health " +
            $"and {(Fought == 0 ? 0 : Seconds / Fought / 60):0.#} minutes";
    }

    private static readonly object s_lock = new();
    private static Dictionary<uint, Foe> s_foes;

    /// <summary>What it knows of this creature, or null when it never fought it.</summary>
    internal static Foe Of(uint template) {
        if (template == 0) {
            return null;
        }

        lock (s_lock) {
            return Foes().TryGetValue(template, out var foe) ? foe : null;
        }
    }

    /// <summary>A fight with this creature ended: written down.</summary>
    internal static Foe Fought(uint template, string name, bool won, double healthLostShare, double seconds) {
        if (template == 0) {
            return null;
        }

        lock (s_lock) {
            var all = Foes();
            if (!all.TryGetValue(template, out var foe)) {
                all[template] = foe = new Foe { Template = template };
            }

            if (!string.IsNullOrWhiteSpace(name)) {
                foe.Name = name;
            }

            foe.Fought++;
            foe.Won += won ? 1 : 0;
            foe.Lost += won ? 0 : 1;
            foe.HealthLost += Math.Clamp(healthLostShare, 0, 1);
            foe.Seconds += Math.Max(0, seconds);
            foe.Last = DateTime.UtcNow;
            Save();
            WriteRivals();

            return foe;
        }
    }

    /// <summary>
    /// His rivals (Niduus, 2026-11-14, approved): the creatures that beat him, or that take most of his health, worst first.
    /// </summary>
    internal static List<Foe> Rivals() {
        lock (s_lock) {
            return [.. Foes().Values.Where(f => f.Lost > 0 || f.Tough).OrderByDescending(f => f.Lost).ThenByDescending(f => f.AverageHealthLost)];
        }
    }

    // logs/ultimus/rivals.txt: his rivals in plain words, for Niduus.
    private static void WriteRivals() {
        try {
            var rivals = Rivals();
            var lines = new List<string> {
                $"ULTIMUS'S RIVALS ({DateTime.Now:yyyy-MM-dd HH:mm}): the creatures that beat him or take most of his health, worst first.",
                "Against a rival he goes in at full health and mana, and after it beat him twice he levels up once more first.",
                "",
            };
            lines.AddRange(rivals.Count == 0 ? ["- None yet."] : rivals.Select(r => "- " + r.Describe()));
            Directory.CreateDirectory(UltimusWire.Folder);
            File.WriteAllLines(Path.Combine(UltimusWire.Folder, "rivals.txt"), lines);
        }
        catch (IOException) {
            // Written again after the next fight.
        }
    }

    private static Dictionary<uint, Foe> Foes() {
        if (s_foes is not null) {
            return s_foes;
        }

        s_foes = [];
        try {
            var path = Path.Combine(UltimusWire.Folder, FILE);
            if (File.Exists(path) && JsonSerializer.Deserialize<List<Foe>>(File.ReadAllText(path)) is { } read) {
                foreach (var foe in read.Where(f => f is not null && f.Template != 0)) {
                    foe.Name ??= "";
                    s_foes[foe.Template] = foe;
                }
            }
        }
        catch (Exception ex) {
            Logger.Debug("Ultimus: {0} could not be read: {1}", Logger.Args(FILE, ex.Message));
        }

        return s_foes;
    }

    private static void Save() {
        try {
            Directory.CreateDirectory(UltimusWire.Folder);
            var path = Path.Combine(UltimusWire.Folder, FILE);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(Foes().Values.OrderByDescending(f => f.Last).ToList(),
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) {
            Logger.Debug("Ultimus: {0} could not be written: {1}", Logger.Args(FILE, ex.Message));
        }
    }

}
