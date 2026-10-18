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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Imlight.Setup;

/// <summary>
/// One thing the Lookup tab can find: a command, or something in the game with its ID or name.
/// </summary>
/// <param name="Kind">Command, Item, Spell, Creature, NPC, Zone or Quest.</param>
/// <param name="Name">What it is called.</param>
/// <param name="Id">Its template ID, when it has one.</param>
/// <param name="Detail">A short extra line, such as a school, a level or who may use a command.</param>
/// <param name="Typed">What to type in the game's chat to use it, when there is something to type.</param>
/// <param name="Help">What it does, in plain words.</param>
internal sealed record LookupEntry(string Kind, string Name, string Id, string Detail, string? Typed, string Help) {

    /// <summary>All of the text a search looks through, in lower case.</summary>
    internal string Haystack { get; } = $"{Kind} {Name} {Id} {Detail} {Typed} {Help}".ToLowerInvariant();

}

/// <summary>
/// Every in-game command, and every item, spell, creature, NPC, zone and quest the server listed,
/// searchable by name or ID.
/// </summary>
internal sealed class Lookup {

    internal const string EVERYTHING = "Everything";

    /// <summary>The filters the tab offers, in order.</summary>
    internal static readonly string[] KINDS = [EVERYTHING, "Command", "Item", "Spell", "Creature", "NPC", "Zone", "Quest"];

    private const string QA = "Quality assurance or higher";
    private const string HM = "Hall monitor or higher";
    private const string ADMIN = "Administrator only";
    private const string ANYONE = "Any account with a permission level";
    private const string SELECTED = " Click a player in game first to select them.";
    private const string TIME = " Time is like 30m, 12h, 7d or 1d12h.";

    /// <summary>
    /// Every command this server build understands. Type them in the game's chat, starting with a dot.
    /// </summary>
    internal static readonly LookupEntry[] COMMANDS = [
        // Your character.
        Command("Level up", ".mod levelup", QA, "Raises your character by one level. Also .mod lvlup."),
        Command("Set level", ".mod level <level>", QA, "Sets your character's level, such as .mod level 50."),
        Command("Add experience", ".mod addxp <amount>", QA, "Gives your character experience points."),
        Command("Run speed", ".mod speed <multiplier>", QA, "Changes how fast you run, such as .mod speed 3. Use .mod speed 1 for normal."),
        Command("Give item", ".mod additem <item ID>", QA, "Puts an item in your backpack. Find the ID by searching for the item in this tab."),
        Command("Give pet snack", ".mod addsnack <item ID>", QA, "Puts a pet snack in your snack bag."),
        Command("Give reagent", ".mod addreagent <item ID>", QA, "Puts a crafting reagent in your reagent bag."),
        Command("Change name", ".mod name <new name>", QA, "Changes the name shown over your character. Log out and back in to see it."),
        Command("Change badge", ".mod badge <badge text>", QA, "Changes the badge shown under your name. Log out and back in to see it."),
        Command("Add gold", ".mod addgold <amount>", QA, "Gives your character gold."),
        Command("Gold limit", ".mod maxgold <amount>", QA, "Changes the most gold your character can carry."),
        Command("Max health", ".mod maxhealth <amount>", QA, "Sets your character's maximum health. Also .mod maxhp."),
        Command("Max mana", ".mod maxmana <amount>", QA, "Sets your character's maximum mana."),
        Command("Max energy", ".mod maxenergy <amount>", QA, "Sets your character's maximum energy. Also .mod maxnrg."),
        Command("Current health", ".mod currenthealth <amount>", QA, "Sets how much health your character has right now. Also .mod currenthp."),
        Command("Current mana", ".mod currentmana <amount>", QA, "Sets how much mana your character has right now."),
        Command("Current energy", ".mod currentenergy <amount>", QA, "Sets how much energy your character has right now. Also .mod currentnrg."),
        Command("Heal", ".mod heal", QA, "Fills your health. Also .mod refillhealth or .mod refillhp."),
        Command("Refill mana", ".mod rejuv", QA, "Fills your mana. Also .mod refillmana, .mod refillmp or .mod rejuvenate."),
        Command("Refill energy", ".mod energize", QA, "Fills your energy. Also .mod refillenergy, .mod refillnrg or .mod refillpet."),
        Command("Potions", ".mod potionmax <amount>", QA, "Sets how many potions you can hold and fills them. Also .mod pmax."),
        Command("Add training points", ".mod addtp <amount>", QA, "Gives your character training points. Also .mod addtrainingpoints."),
        Command("Set training points", ".mod settp <amount>", QA, "Sets your character's training points. Also .mod settrainingpoints."),
        Command("Gain training points", ".gaintp <amount>", "Every player", "Gives your character 1 to 100 training points. Also .trainingpoints. " +
            "Works for every player unless the server turned it off (Settings, Trainers, PlayersCanGainTrainingPoints)."),
        Command("Cantrip level up", ".mod clvlup", QA, "Raises your cantrip level by one. Also .mod cantriplevelup."),
        Command("Set cantrip level", ".mod clvl <level>", QA, "Sets your cantrip level. Also .mod cantriplevel."),

        // Spells.
        Command("Learn spell", ".sb learn <spell ID>", QA, "Adds a spell to your spellbook for good. Find the ID by searching for the spell in this tab."),
        Command("Unlearn spell", ".sb unlearn <spell ID>", QA, "Takes a spell out of your spellbook."),
        Command("Unlearn all spells", ".sb unlearnall", QA, "Takes every learned spell out of your spellbook."),
        Command("Add spell for now", ".sb add <spell ID>", QA, "Gives you a spell until you log out, without learning it."),
        Command("Remove spell for now", ".sb remove <spell ID>", QA, "Takes away a spell added with .sb add."),
        Command("Learn all cantrips", ".sb allcantrips", QA, "Teaches your character every cantrip."),
        Command("All teleport effects", ".givemetpstyle", QA, "Unlocks every teleport effect. Log out and back in to see them."),
        Command("Shadow magic", ".intothedarkness", QA, "Gives your character shadow magic of its own: shadow pip slots, the shadow gauge and shadow pips in battle, even with the Shadow pips switch off. Log out and back in to see it."),

        // Battles.
        Command("Win battle", ".cheat win", QA, "Wins the battle you are in right now."),
        Command("Fast spell animations", ".cheat cinematic", QA, "Turns instant spell animations on or off."),
        Command("No fizzle", ".cheat nofizzle", QA, "Turns spells that never fizzle on or off."),

        // Moving around.
        Command("Teleport", ".tp <zone>", QA, "Takes you to a zone. Part of the name works, such as .tp Ravenwood. .tp gm goes to the GM island. Also .teleport or .port. Search Zone in this tab for every name."),
        Command("Where am I", ".debug gps", QA, "Shows the zone you are in and where you are standing."),
        Command("Hide object", ".debug disableobj <zone tag>", QA, "Hides an object in this zone by its tag."),
        Command("Show object", ".debug enableobj <zone tag>", QA, "Shows an object hidden with .debug disableobj."),
        Command("List zone changes", ".dynamod list", QA, "Lists the zone changes (dynamods) saved on your character."),
        Command("Set zone change", ".dynamod set <on or off> <tag>", QA, "Turns a zone change on or off for your character."),
        Command("Toggle zone change", ".dynamod toggle <tag>", QA, "Flips a zone change between on and off."),
        Command("Remove zone change", ".dynamod remove <tag>", QA, "Removes a zone change from your character."),

        // Quests.
        Command("Offer quest", ".quest offer <quest name>", QA, "Shows the quest's offer window, as if someone gave it to you. Search Quest in this tab for names."),
        Command("Give quest", ".quest grant <quest name>", QA, "Adds a quest to your quest book."),
        Command("Force give quest", ".quest forcegrant <quest name>", QA, "Adds a quest even when you could not normally get it."),
        Command("Complete quest", ".quest complete <quest name>", QA, "Finishes a quest."),
        Command("Complete current goal", ".quest completegoal", QA, "Finishes the goal you are on in your active quest."),
        Command("Complete quest goal", ".quest completegoalfor <quest name>", QA, "Finishes the goal you are on in a chosen quest."),
        Command("Remove quest", ".quest remove <quest name>", QA, "Takes a quest out of your quest book."),
        Command("My quests", ".quest list", QA, "Lists your quests."),
        Command("Active quest info", ".quest info", QA, "Shows details about your active quest."),
        Command("Show quest", ".quest show <quest name>", QA, "Shows details about any quest."),

        // Groups.
        Command("Group info", ".group info", QA, "Shows who is in your group."),
        Command("Kick from group", ".group kick <name>", QA, "Removes someone from your group. Also .group remove."),
        Command("Group leader", ".group leader <name>", QA, "Makes someone the group's leader."),
        Command("Disband group", ".group disband", QA, "Breaks up your group."),

        // Finding IDs in game.
        Command("Find item ID", ".lookup item <words>", QA, "Searches item names in game and shows their IDs."),
        Command("Find spell ID", ".lookup spell <words>", QA, "Searches spell names in game and shows their IDs."),
        Command("Find creature ID", ".lookup mob <words>", QA, "Searches creature names in game and shows their IDs. Also .lookup creature."),
        Command("Find NPC ID", ".lookup npc <words>", QA, "Searches NPC names in game and shows their IDs."),
        Command("Find any ID", ".lookup all <words>", QA, "Searches items, spells, creatures and NPCs at once."),

        // Players you have selected.
        Command("Mute player", ".mute <time> <reason>", HM, "Stops the selected player from chatting." + TIME + SELECTED),
        Command("Unmute player", ".unmute", HM, "Lets the selected player chat again." + SELECTED),
        Command("Kick player", ".kick <reason>", HM, "Disconnects the selected player." + SELECTED),
        Command("Warn player", ".warn <reason>", HM, "Adds a warning to the selected player's account." + SELECTED),
        Command("Player info", ".info", HM, "Shows the selected player's account details." + SELECTED),

        // Accounts.
        Command("Create account", ".account create <username> <password>", HM, "Makes a new account."),
        Command("Delete account", ".account delete <username>", ADMIN, "Deletes an account and its characters."),
        Command("Lock account", ".account lock <username>", HM, "Stops an account from logging in."),
        Command("Unlock account", ".account unlock <username>", HM, "Lets a locked account log in again."),
        Command("Change password", ".account password <username> <new password> <new password again>", ADMIN, "Changes an account's password."),
        Command("Permission level", ".account authlevel <username> <level>", ADMIN,
            "Changes what an account may do. Levels: None, QualityAssurance, HallMonitor, Developer, Administrator."),
        Command("Account info", ".account info <username>", HM, "Shows an account's details."),
        Command("Account warnings", ".account warnings <username>", ANYONE, "Lists an account's warnings and punishments. Also .account infractions or .account warns."),
        Command("Warn account", ".account warn <username> <reason>", HM, "Adds a warning to an account."),
        Command("Remove warning", ".account removewarn <username> <warning number>", HM, "Removes one of an account's warnings."),

        // Bans.
        Command("Ban account", ".ban account <username> <time> <reason>", HM, "Bans an account." + TIME + " Also .ban user."),
        Command("Ban computer", ".ban machine <machine ID> <time> <reason>", HM, "Bans a computer." + TIME + " Also .ban pc."),
        Command("Ban IP address", ".ban ip <IP address> <time> <reason>", HM, "Bans an internet address." + TIME),
        Command("Ban info", ".ban info <username>", HM, "Shows an account's bans."),
        Command("Unban account", ".unban account <username>", HM, "Lifts an account's ban. Also .unban user."),
        Command("Unban computer", ".unban machine <machine ID>", HM, "Lifts a computer's ban. Also .unban pc."),
        Command("Unban IP address", ".unban ip <IP address>", HM, "Lifts an internet address's ban."),
    ];

    private readonly List<LookupEntry> _entries;

    private Lookup(List<LookupEntry> entries, string? missing) {
        _entries = entries;
        Missing = missing;
    }

    /// <summary>
    /// Why the game lists are not here yet, or null when they are.
    /// </summary>
    internal string? Missing { get; }

    internal int Count => _entries.Count;

    /// <summary>
    /// Reads the commands and the lists the server wrote to its ID lookup folder.
    /// </summary>
    internal static Lookup Load(string folder) {
        var entries = new List<LookupEntry>(COMMANDS);
        var ids = Path.Combine(folder, "ids.csv");
        string? missing = null;

        if (File.Exists(ids)) {
            foreach (var fields in ReadCsv(ids)) {
                if (fields.Count < 5) {
                    continue;
                }

                var (id, kind, name, school, level) = (fields[0], fields[1], fields[2], fields[3], fields[4]);
                entries.Add(kind switch {
                    "Item" => new LookupEntry("Item", name, id, school, $".mod additem {id}",
                        "Pet snacks use .mod addsnack and reagents use .mod addreagent with the same ID."),
                    "Spell" => new LookupEntry("Spell", name, id, school, $".sb learn {id}", "Adds the spell to your spellbook."),
                    "Creature" => new LookupEntry("Creature", name, id, level is "" or "0" ? school : $"Level {level} {school}".Trim(), null, ""),
                    _ => new LookupEntry("NPC", name, id, "", null, ""),
                });
            }
        }
        else {
            missing = "Items, spells, creatures and NPCs show up here once the server has made its ID list. "
                + "Start the server and give it a few minutes the first time.";
        }

        var zones = Path.Combine(folder, "zones.txt");
        if (File.Exists(zones)) {
            entries.AddRange(File.ReadLines(zones)
                .Where(z => z.Trim().Length > 0)
                .Select(z => new LookupEntry("Zone", z.Trim(), "", "", $".tp {z.Trim()}", "Takes you to this zone.")));
        }

        var quests = Path.Combine(folder, "quests.csv");
        if (File.Exists(quests)) {
            foreach (var fields in ReadCsv(quests)) {
                if (fields.Count < 2 || fields[0].Length == 0) {
                    continue;
                }

                var (name, title) = (fields[0], fields[1]);
                entries.Add(new LookupEntry("Quest", title.Length > 0 ? title : name, "", title.Length > 0 ? name : "",
                    $".quest grant {name}", "Adds the quest to your quest book. .quest complete with the same name finishes it."));
            }
        }

        return new Lookup(entries, missing);
    }

    /// <summary>
    /// Entries of a kind that hold every word of the query, best matches first.
    /// </summary>
    internal List<LookupEntry> Search(string kind, string query, int max, out int total) {
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var pool = kind == EVERYTHING ? _entries : _entries.Where(e => e.Kind == kind);
        if (words.Length == 0 && kind == EVERYTHING) {
            pool = COMMANDS;
        }

        var matches = pool.Where(e => words.All(e.Haystack.Contains)).ToList();
        total = matches.Count;
        var exact = query.Trim();

        return [.. matches
            .OrderBy(e => !(e.Name.Equals(exact, StringComparison.OrdinalIgnoreCase) || e.Id == exact))
            .ThenBy(e => !e.Name.StartsWith(exact, StringComparison.OrdinalIgnoreCase))
            .ThenBy(e => Array.IndexOf(KINDS, e.Kind))
            .ThenBy(e => words.Length == 0 ? 0 : e.Name.Length)
            .Take(max)];
    }

    private static LookupEntry Command(string name, string typed, string who, string help)
        => new("Command", name, "", who, typed, help);

    /// <summary>
    /// The rows of a CSV file after its header line, allowing quoted fields.
    /// </summary>
    private static IEnumerable<List<string>> ReadCsv(string path) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        reader.ReadLine();
        while (reader.ReadLine() is { } line) {
            var fields = new List<string>();
            var field = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < line.Length; i++) {
                var c = line[i];
                if (quoted) {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') {
                        field.Append('"');
                        i++;
                    }
                    else if (c == '"') {
                        quoted = false;
                    }
                    else {
                        field.Append(c);
                    }
                }
                else if (c == '"') {
                    quoted = true;
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
            yield return fields;
        }
    }

}
