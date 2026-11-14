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
using System.Globalization;
using System.IO;
using Imlight.Common;

namespace Imlight.CoreLib.Game.Ultimus;

/// <summary>
/// Ultimus's playbook: the Wizard101 know-how he plays by (Niduus, 2026-11-14: "personally dump some logic and reason into
/// his programming so he is best equipped with what to expect with least likely hood to fail his goals"). Each rule has a
/// name, a number he uses and the reason, and his thoughts name the rule behind a choice. It is a plain text file,
/// logs/ultimus/playbook.txt, written with the starting rules the first time and never overwritten after, so Niduus can
/// change any number or reason; a change applies within a few seconds, without a restart. A rule missing from the file
/// uses its starting value.
/// </summary>
internal static class UltimusPlaybook {

    private const string FILE = "playbook.txt";

    internal sealed record Rule(string Name, double Value, string Why);

    // The starting rules, by section. Numbers are shares (0.5 = half) or percents where the reason says so.
    private static readonly (string Section, Rule Rule)[] s_defaults = [
        ("Quest", new("ReadyHealth", 0.75, "Before a step, at least this share of health; below it I heal first (potion, wisp, potion shop).")),
        ("Quest", new("ReadyMana", 0.25, "Before a step, at least this share of mana, so I can cast my big spells.")),
        ("Quest", new("ToughFoeHealth", 0.95, "A creature that beat me, or takes most of my health, is met at this share of health or more.")),
        ("Quest", new("ToughFoeMana", 0.6, "...and at least this share of mana.")),
        ("Quest", new("ToughShare", 0.55, "A fight that takes this share of my health on average makes that creature a tough one.")),
        ("Quest", new("SameFailLimit", 2, "If a step fails the same way this many times, I change my approach. I never loop.")),
        ("Battle", new("HealAt", 0.5, "In battle I heal myself at or below this share of health.")),
        ("Battle", new("BigHealAt", 0.3, "At or below this share, healing comes before anything, even a finishing hit.")),
        ("Battle", new("BalanceResistNotice", 0.1, "Creatures resisting Balance by this share or more: I weigh full pierce against off-school hits (no Balance Prism: no player can get one).")),
        ("Gear", new("AccuracyFloor", 5, "Gear accuracy (percent) I want at level 1; below the floor, accuracy counts a lot more.")),
        ("Gear", new("AccuracyPerLevel", 0.15, "Accuracy floor added per level (percent).")),
        ("Gear", new("AccuracyMax", 20, "The accuracy floor never goes over this (percent).")),
        ("Gear", new("ResistFloor", 8, "Resist (percent) I want at level 1: alone, nobody shields me.")),
        ("Gear", new("ResistPerLevel", 0.3, "Resist floor added per level (percent).")),
        ("Gear", new("ResistMax", 40, "The resist floor never goes over this (percent).")),
        ("Gear", new("HealthPerLevel", 10, "Health from gear I want per level, so the world's creatures can't kill me in a round or two.")),
        ("Gear", new("PowerPipGoal", 100, "Power pip chance (percent) I work toward; past it, power pip chance is worth nothing.")),
        ("Gear", new("DamageWeight", 4, "Above the floors, damage is my top priority...")),
        ("Gear", new("PierceWeight", 3, "...together with pierce, which goes through the creatures' resist.")),
        ("Gear", new("FloorWeight", 4, "How much a point under a floor (resist, accuracy) counts: survival comes first.")),
        ("Gear", new("OverFloorShare", 0.25, "A point above a floor counts this share of a point under it.")),
    ];

    // The know-how written in the file for reading (not numbers he reads).
    private static readonly string[] s_knowHow = [
        "QUESTING",
        "- Before a step: the right area, the items the step needs, the level it expects, enough health and mana. Short of anything, fix it first.",
        "- Take the shortest legal way: teleporters, Spiral Doors and world gates before long walks.",
        "- For defeat or collect steps, fight the nearest creature that counts; walk round the others, as every extra fight costs health.",
        "- In a dungeon, clear it in order and never leave mid-step.",
        "- Before a boss or a dungeon, rest to full health and mana. Read the boss's school and cheats first.",
        "- A step that fails twice the same way gets a new approach. Never loop.",
        "",
        "BATTLE (Balance, alone)",
        "- Survival first: heal at HealAt, and heal before anything at BigHealAt. Keep a heal in hand when hurt.",
        "- Shield only against what the creatures can actually hit with. Otherwise pips go into damage.",
        "- Build one big hit: a blade on myself, a trap on the target, then the biggest hit I can afford.",
        "- Against several creatures, a hit-all spell. Against one, the strongest single hit.",
        "- Target order: one I can finish this round, then healers and shielders, then the hardest hitter, then the boss.",
        "- Opposed schools: Fire/Ice, Storm/Myth, Life/Death.",
        "- Balance Prism is in the game's files, but no player can get it by vanilla means: never plan around it.",
        "- Against creatures that resist Balance: if my pierce covers most of their resist, go full pierce with Balance spells;",
        "  otherwise hit off-school: a split hit like Hydra (Fire, Ice, Storm) or Spectral Blast, or a spell I trained from another school.",
        "- If two heals in a row can't keep me up, the fight is lost: next time, gear up or level first, and write it in the Mistakes book.",
        "",
        "GEAR (Balance, alone)",
        "- Balanced, as the school's name says: enough accuracy, enough resist, enough health, power pip chance toward 100%.",
        "- Above those floors, damage first and pierce with it. Surviving comes first, but I still have to hit hard.",
        "- Compare the whole outfit, not one piece: a piece is worth what it adds to the outfit's weakest need.",
        "- Look again after every new world, drop, shop and quest reward. Never developers' or testers' gear.",
        "",
        "LEARNING",
        "- Remember each creature: how the fight went and what it took. Plan the next fight from that.",
        "- Write each mistake, with my reasoning, in the Mistakes book, and read it before a similar step.",
    ];

    private static readonly object s_lock = new();
    private static Dictionary<string, Rule> s_rules;
    private static DateTime s_read = DateTime.MinValue;
    private static DateTime s_written = DateTime.MinValue;

    /// <summary>A rule by "Section.Name": the number in the file, or its starting value.</summary>
    internal static Rule Get(string name) {
        lock (s_lock) {
            Load();
            if (s_rules.TryGetValue(name, out var rule)) {
                return rule;
            }
        }

        foreach (var (section, rule) in s_defaults) {
            if (section + "." + rule.Name == name) {
                return rule;
            }
        }

        return new Rule(name, 0, "");
    }

    internal static double Value(string name) => Get(name).Value;

    /// <summary>How a thought names a rule: "playbook Battle.HealAt = 0.5".</summary>
    internal static string Cite(string name) => $"playbook {name} = {Get(name).Value.ToString("0.##", CultureInfo.InvariantCulture)}";

    private static void Load() {
        if (s_rules is not null && DateTime.UtcNow - s_read < TimeSpan.FromSeconds(5)) {
            return;
        }

        s_read = DateTime.UtcNow;
        var path = Path.Combine(UltimusWire.Folder, FILE);
        try {
            if (!File.Exists(path)) {
                Directory.CreateDirectory(UltimusWire.Folder);
                File.WriteAllText(path, DefaultText());
            }

            var written = File.GetLastWriteTimeUtc(path);
            if (s_rules is not null && written == s_written) {
                return;
            }

            s_written = written;
            var rules = new Dictionary<string, Rule>(StringComparer.OrdinalIgnoreCase);
            var section = "";
            foreach (var raw in File.ReadAllLines(path)) {
                var line = raw.Trim();
                if (line.StartsWith('[') && line.EndsWith(']')) {
                    section = line[1..^1].Trim();
                    continue;
                }

                var equals = line.IndexOf('=');
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('-') || equals <= 0) {
                    continue;
                }

                var name = line[..equals].Trim();
                var rest = line[(equals + 1)..];
                var semicolon = rest.IndexOf(';');
                var number = (semicolon >= 0 ? rest[..semicolon] : rest).Trim();
                var why = semicolon >= 0 ? rest[(semicolon + 1)..].Trim() : "";
                if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) {
                    rules[section + "." + name] = new Rule(name, value, why);
                }
            }

            s_rules = rules;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Logger.Debug("Ultimus: {0} could not be read: {1}", Logger.Args(FILE, ex.Message));
            s_rules ??= [];
        }
    }

    private static string DefaultText() {
        var text = new System.Text.StringBuilder();
        text.AppendLine("# Ultimus's playbook: the Wizard101 know-how he plays by.");
        text.AppendLine("# Change any number after the = sign; the words after the ; are the reason he gives in his thoughts.");
        text.AppendLine("# Changes apply within a few seconds, no restart. Delete this file to get the starting rules back.");
        text.AppendLine();
        var last = "";
        foreach (var (section, rule) in s_defaults) {
            if (section != last) {
                if (last.Length > 0) {
                    text.AppendLine();
                }

                text.AppendLine($"[{section}]");
                last = section;
            }

            text.AppendLine($"{rule.Name} = {rule.Value.ToString(CultureInfo.InvariantCulture)} ; {rule.Why}");
        }

        text.AppendLine();
        text.AppendLine("[Know-how]");
        foreach (var line in s_knowHow) {
            text.AppendLine(line.Length == 0 ? "" : line.StartsWith('-') || line.StartsWith(' ') ? line : "# " + line);
        }

        return text.ToString();
    }

}
