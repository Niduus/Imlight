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
using System.Linq;
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
        ("Battle", new("LookAheadDelay", 0.9, "Planning two rounds: damage next round counts this share of damage now (a round later is a round more they hit me).")),
        ("Battle", new("LookAheadRisky", 0.75, "...and only this share when the creatures' habits say they could take me under my heal line before then.")),
        ("Battle", new("TrapGuess", 30, "A trap whose size I can't read counts as this many percent.")),
        ("Battle", new("HealerFirst", 0.3, "A creature that heals this share of its casts or more is hit first: a fight drags on while it heals.")),
        ("Mood", new("CarefulAfterStuck", 4, "Stuck this many times in an hour makes me careful.")),
        ("Mood", new("BoldAfterWins", 5, "This many fights won in a row (none lost) makes me bold.")),
        ("Mood", new("CarefulHealShift", 0.1, "Careful, I heal this much sooner in battle (share of health) and rest to full before every fight.")),
        ("Mood", new("BoldHealShift", 0.05, "Bold, I heal this much later in battle and rest less.")),
        ("Confidence", new("Careful", 45, "A plan I trust less than this (out of 100) is done the careful way: full health first, the map's way, not a shortcut.")),
        ("Push", new("On", 1, "1 = push mode: the main story first; no shop trips except in a new world's first minutes or after a lost fight. 0 = off. Nothing is ever skipped or finished for me.")),
        ("Push", new("LevelGap", 3, "Before a fight step, if the quest's level is more than this over mine, I fight other creatures for XP first.")),
        ("Guard", new("StepMinutes", 20, "Time budget for one step: past it I stop, say why, and plan again (another open step, another way).")),
        ("Guard", new("QuestFactor", 4, "Time budget for one quest: this many times its expected time (at least 30 minutes), not counting levelling up for it.")),
        ("Guard", new("SideTripMinutes", 8, "Time budget for a side trip (a shop, the Bazaar, a trainer, selling): past it I drop the trip and go back to the story.")),
        ("Guard", new("NoStoryMinutes", 15, "Off-track alarm: no story progress (a step, a quest, XP while levelling for one) for this long and I stop and plan again.")),
        ("Guard", new("CheckMinutes", 5, "Every this-many minutes I ask: am I closer to my goal than last time? Two noes in a row and I switch strategy.")),
        ("Guard", new("MinWinChance", 70, "Risk budget: a fight I trust less than this (out of 100) is not started until I have prepared (full health and mana, a deck for it).")),
        ("Push", new("ShopMinutes", 30, "In push mode, shop trips only in my first this-many minutes in a new world (its shops have the next gear).")),
        ("Watchdog", new("Minutes", 45, "No step done and no XP gained for this long: I raise the alarm and use a player's unstuck moves (area start, then .stuck go).")),
        ("Pace", new("StepPause", 4, "After a step is done I take up to this many seconds, as a player reads the next step. 0 = no pause.")),
        ("Pace", new("DialogPerLine", 0.8, "Seconds I read each line of a dialog.")),
        ("Pace", new("DialogMost", 7, "...and at most this many seconds for one dialog.")),
        ("Pace", new("Wobble", 6, "My walk wobbles by up to this much between its turns, so I don't walk like a ruler. 0 = straight.")),
        ("Backpack", new("Room", 8, "With fewer free backpack spaces than this, I sell gear I will never wear at the next shop. Quest items and worn gear stay.")),
        ("Gold", new("ReservePerLevel", 150, "Gold I keep back per level for the next world's gear; only a clear upgrade is bought past it.")),
        ("Gold", new("LookAheadLevels", 5, "My gold plan looks this many levels ahead (about the next world).")),
        ("Gold", new("Gifts", 0, "1 = Ahziael tops up my gold when I am short (the old way). 0 = I earn and plan my gold as a player.")),
        ("Gear", new("BazaarGain", 8, "An upgrade at the Bazaar must be at least this much better to be worth the trip to Olde Town.")),
        ("Side", new("On", 1, "1 = when I need XP, a side quest of about my level given in my area comes before fighting for it. 0 = only fights.")),
        ("Side", new("LevelsBelow", 2, "A side quest for XP may be at most this many levels under mine (lower ones pay too little).")),
        ("Confidence", new("Sure", 80, "A plan I trust this much or more is done at once.")),
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
        "THINKING (each step)",
        "- I keep four goals in mind and say them: the world's story I am finishing, the quest, its step, and my next move.",
        "- Each plan gets a confidence out of 100: what I know of the place, the creature and the way there. Under Confidence.Careful",
        "  I do it the careful way; at Confidence.Sure or more I do it at once.",
        "- Before I act I say what I expect (\"this exit takes me to Altar of Kings\", \"I win and lose about 20% health\").",
        "  When something else happens, that surprise is a lesson: it goes in the Mistakes book and the shared brain.",
        "- My mood: careful after a lost fight, many stuck spots or many surprises in the last hour; bold after many wins in a row.",
        "",
        "BATTLE PLANNING (two rounds ahead)",
        "- Before each round I look at my whole hand over two rounds: hit now and hit again, blade now and hit bigger next round,",
        "  trap now and hit bigger next round, or save my pips now for a bigger spell next round. I play the line that deals the",
        "  most over both rounds, counting a round later as worth a bit less (Battle.LookAheadDelay), and much less when the",
        "  creatures could take me under my heal line before then (Battle.LookAheadRisky).",
        "- Damage past a creature's health is wasted: a big hit on a nearly dead creature counts only what it takes.",
        "- I play round the creatures' habits from the shared brain: one that heals often is hit first; one that tends to hit",
        "  hard on this round makes me heal or shield first; one that shields on this round makes building up worth more.",
        "",
        "DAILY REVIEW",
        "- Once a day I read my day: fights won and lost, where I got stuck, what surprised me. I write \"today I learned\"",
        "  (learned-today.txt) and change a few playbook numbers myself, only inside the limits in self-tuning.txt.",
        "",
        "LEARNING",
        "- Remember each creature: how the fight went and what it took. Plan the next fight from that.",
        "- Write each mistake, with my reasoning, in the Mistakes book, and read it before a similar step.",
    ];

    // Know-how added in version 3 (Niduus, 2026-11-14): written in new playbooks, and added once at the end of older ones.
    private static readonly string[] s_knowHow3 = [
        "HUMAN RULES (Niduus)",
        "- I quest as a human does. No command ever skips, jumps or finishes a step for me; my only commands get me unstuck.",
        "- Under-leveled, I fight creatures for XP. No XP is given to me.",
        "- A step that looks broken is flagged for Niduus; I keep trying it the way a player would.",
        "- No pet training and no crafting. Gear from shops and the Bazaar is fine.",
        "- Fast travel a player has: Mark Location before a trip to a trainer or shop, Recall Location after; Spiral Doors and teleporters.",
        "",
        "LIVING LIKE A PLAYER",
        "- A short pause after each step; dialogs read at a reading pace; a small wobble in my walk (Pace).",
        "- Backpack nearly full: I sell gear I will never wear at a shop (Backpack.Room).",
        "- Gold: no gifts. I keep a reserve for the next world's gear and say my gold plan in each new world (Gold).",
        "- Each new world: gear check at its shops, and the Bazaar for a clear upgrade (Gear.BazaarGain). Never crafting.",
        "- Each new world and level: a training plan with a reason for each spell (training-plan.txt).",
        "- A lost fight: I work out why and change one thing for the next try: full health and a shield deck, then XP, then gear.",
        "- I remember the players I meet (players.json) and greet them again.",
        "",
        "MISSION AND GUARDRAILS (Niduus, 2026-11-14)",
        "- My mission: finish the main story solo, like a human. Every move is checked against it; a move that does not serve it",
        "  is turned down and written in my thoughts with the reason.",
        "- Hard limits, only Niduus changes them and my daily review never touches them: no step skipping; no free XP, gold or",
        "  items; no crafting or pet training; I stay in bounds (the floor, the main story worlds); side areas (dungeons,",
        "  events, other worlds) only when a quest names them.",
        "- Off-track alarm: off the story's areas, on a quest that is not the story's, or no story progress for a while",
        "  (Guard.NoStoryMinutes): I stop, say why, and plan again back to the story.",
        "- Time budgets: a step (Guard.StepMinutes), a quest (Guard.QuestFactor), a side trip (Guard.SideTripMinutes).",
        "- Risk budget: a fight I trust under Guard.MinWinChance is prepared for first.",
        "- Progress check every Guard.CheckMinutes: two noes in a row and I switch strategy.",
        "- Every move has a \"because\" in my thoughts. A move I can't give a reason for, I don't make.",
        "- Ahziael can pause me, send me back to the story, and lock any number so my daily review leaves it alone.",
        "",
        "PUSH MODE",
        "- Main story first. Before each step I check my level against the quest's (Push.LevelGap) and what went wrong here before",
        "  (step-memory.json). A failure that happened even once at a step is remembered for good.",
        ""
    ];

    private static readonly object s_lock = new();
    private static Dictionary<string, Rule> s_rules;
    private static DateTime s_read = DateTime.MinValue;
    private static DateTime s_written = DateTime.MinValue;
    private static bool s_upgraded;

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

            if (!s_upgraded) {
                s_upgraded = true;
                Upgrade(path);
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

    // Rules and know-how added after his playbook was first written: added at its end, his own changes kept (the file is
    // never written over).
    private const string KNOW_HOW_MARK = "# Know-how version 2";
    private const string KNOW_HOW_MARK_3 = "# Know-how version 3";

    private static void Upgrade(string path) {
        var text = File.ReadAllText(path);
        var add = new System.Text.StringBuilder();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        foreach (var raw in text.Split('\n')) {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) {
                section = line[1..^1].Trim();
            }
            else if (line.IndexOf('=') is var eq and > 0 && !line.StartsWith('#') && !line.StartsWith('-')) {
                known.Add(section + "." + line[..eq].Trim());
            }
        }

        foreach (var group in s_defaults.Where(d => !known.Contains(d.Section + "." + d.Rule.Name)).GroupBy(d => d.Section)) {
            add.AppendLine();
            add.AppendLine($"[{group.Key}]");
            foreach (var (_, rule) in group) {
                add.AppendLine($"{rule.Name} = {rule.Value.ToString(CultureInfo.InvariantCulture)} ; {rule.Why}");
            }
        }

        if (!text.Contains(KNOW_HOW_MARK, StringComparison.Ordinal)) {
            add.AppendLine();
            add.AppendLine(KNOW_HOW_MARK + " (added by Ahziael; the rules above are kept as you set them)");
            add.AppendLine("[Know-how]");
            foreach (var line in s_knowHow) {
                add.AppendLine(line.Length == 0 ? "" : line.StartsWith('-') || line.StartsWith(' ') ? line : "# " + line);
            }
        }

        if (!text.Contains(KNOW_HOW_MARK_3, StringComparison.Ordinal)) {
            add.AppendLine();
            add.AppendLine(KNOW_HOW_MARK_3 + " (added by Ahziael)");
            foreach (var line in s_knowHow3) {
                add.AppendLine(line.Length == 0 ? "" : line.StartsWith('-') || line.StartsWith(' ') ? line : "# " + line);
            }
        }

        if (add.Length > 0) {
            File.AppendAllText(path, add.ToString());
        }
    }

    /// <summary>
    /// He changes one of his own numbers (the daily review, inside the limits in self-tuning.txt): the line in the file is
    /// changed in place, its reason kept, and a note of why goes after it.
    /// </summary>
    internal static bool Set(string name, double value, string because) {
        lock (s_lock) {
            try {
                var path = Path.Combine(UltimusWire.Folder, FILE);
                if (!File.Exists(path)) {
                    Load();
                }

                var dot = name.IndexOf('.');
                var (wantSection, wantName) = (name[..dot], name[(dot + 1)..]);
                var lines = File.ReadAllLines(path).ToList();
                var section = "";
                for (var i = 0; i < lines.Count; i++) {
                    var line = lines[i].Trim();
                    if (line.StartsWith('[') && line.EndsWith(']')) {
                        section = line[1..^1].Trim();
                        continue;
                    }

                    var eq = line.IndexOf('=');
                    if (eq <= 0 || line.StartsWith('#') || !section.Equals(wantSection, StringComparison.OrdinalIgnoreCase)
                        || !line[..eq].Trim().Equals(wantName, StringComparison.OrdinalIgnoreCase)) {
                        continue;
                    }

                    var rest = line[(eq + 1)..];
                    var why = rest.IndexOf(';') is var semi and >= 0 ? rest[(semi + 1)..].Trim() : "";
                    var cut = why.IndexOf(" [I set this", StringComparison.Ordinal);
                    if (cut >= 0) {
                        why = why[..cut];
                    }

                    lines[i] = $"{wantName} = {value.ToString("0.###", CultureInfo.InvariantCulture)} ; {why} [I set this {DateTime.Now:yyyy-MM-dd}: {because}]";
                    File.WriteAllLines(path, lines);
                    s_rules = null;

                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) {
                Logger.Debug("Ultimus: {0} could not be changed: {1}", Logger.Args(FILE, ex.Message));
            }

            return false;
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
        text.AppendLine(KNOW_HOW_MARK);
        text.AppendLine("[Know-how]");
        foreach (var line in s_knowHow) {
            text.AppendLine(line.Length == 0 ? "" : line.StartsWith('-') || line.StartsWith(' ') ? line : "# " + line);
        }

        text.AppendLine();
        text.AppendLine(KNOW_HOW_MARK_3);
        foreach (var line in s_knowHow3) {
            text.AppendLine(line.Length == 0 ? "" : line.StartsWith('-') || line.StartsWith(' ') ? line : "# " + line);
        }

        return text.ToString();
    }

}
