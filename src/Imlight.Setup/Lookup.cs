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
internal sealed record LookupEntry(string Kind, string Name, string Id, string Detail, string? Typed, string Help, bool Custom = false) {

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
    internal static readonly string[] KINDS = [EVERYTHING, "Command", "Bundle", "Item", "Spell", "Creature", "NPC", "Zone", "Quest", "Phrase"];

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
        Imlight("Level up", ".mod levelup", QA, "Raises your character by one level. Also .mod lvlup."),
        Command("Set level", ".level MAX or .level <level>", QA, "Sets your character to the max level, or to the level you type, such as .level 50. Also .mod level."),
        Imlight("Add experience", ".mod addxp <amount>", QA, "Gives your character experience points."),
        Imlight("Run speed", ".mod speed <multiplier>", QA, "Changes how fast you run, such as .mod speed 3. Use .mod speed 1 for normal."),
        Imlight("Give item", ".mod additem <item ID>", QA, "Puts an item in your backpack. Find the ID by searching for the item in this tab."),
        Imlight("Give pet snack", ".mod addsnack <item ID>", QA, "Puts a pet snack in your snack bag."),
        Imlight("Give reagent", ".mod addreagent <item ID>", QA, "Puts a crafting reagent in your reagent bag."),
        Imlight("Change name", ".mod name <new name>", QA, "Changes the name shown over your character. Log out and back in to see it."),
        Imlight("Change badge", ".mod badge <badge text>", QA, "Changes the badge shown under your name. Log out and back in to see it."),
        Imlight("Add gold", ".mod addgold <amount>", QA, "Gives your character gold."),
        Imlight("Gold limit", ".mod maxgold <amount>", QA, "Changes the most gold your character can carry."),
        Imlight("Max health", ".mod maxhealth <amount>", QA, "Sets your character's maximum health. Also .mod maxhp."),
        Imlight("Max mana", ".mod maxmana <amount>", QA, "Sets your character's maximum mana."),
        Imlight("Max energy", ".mod maxenergy <amount>", QA, "Sets your character's maximum energy. Also .mod maxnrg."),
        Imlight("Current health", ".mod currenthealth <amount>", QA, "Sets how much health your character has right now. Also .mod currenthp."),
        Imlight("Current mana", ".mod currentmana <amount>", QA, "Sets how much mana your character has right now."),
        Imlight("Current energy", ".mod currentenergy <amount>", QA, "Sets how much energy your character has right now. Also .mod currentnrg."),
        Imlight("Heal", ".mod heal", QA, "Fills your health. Also .mod refillhealth or .mod refillhp."),
        Imlight("Refill mana", ".mod rejuv", QA, "Fills your mana. Also .mod refillmana, .mod refillmp or .mod rejuvenate."),
        Imlight("Refill energy", ".mod energize", QA, "Fills your energy. Also .mod refillenergy, .mod refillnrg or .mod refillpet."),
        Imlight("Potions", ".mod potionmax <amount>", QA, "Sets how many potions you can hold and fills them. Also .mod pmax."),
        Imlight("Add training points", ".mod addtp <amount>", QA, "Gives your character training points. Also .mod addtrainingpoints."),
        Imlight("Set training points", ".mod settp <amount>", QA, "Sets your character's training points. Also .mod settrainingpoints."),
        Command("Gain training points", ".gaintp <amount>", "Every player", "Gives your character 1 to 100 training points. Also .trainingpoints. " +
            "Works for every player unless the server turned it off (Settings, Trainers, PlayersCanGainTrainingPoints)."),
        Imlight("Cantrip level up", ".mod clvlup", QA, "Raises your cantrip level by one. Also .mod cantriplevelup."),
        Imlight("Set cantrip level", ".mod clvl <level>", QA, "Sets your cantrip level. Also .mod cantriplevel."),

        // Spells.
        Imlight("Learn spell", ".sb learn <spell ID>", QA, "Adds a spell to your spellbook for good. Find the ID by searching for the spell in this tab."),
        Imlight("Unlearn spell", ".sb unlearn <spell ID>", QA, "Takes a spell out of your spellbook."),
        Imlight("Unlearn all spells", ".sb unlearnall", QA, "Takes every learned spell out of your spellbook."),
        Imlight("Add spell for now", ".sb add <spell ID>", QA, "Gives you a spell until you log out, without learning it."),
        Imlight("Remove spell for now", ".sb remove <spell ID>", QA, "Takes away a spell added with .sb add."),
        Imlight("Learn all cantrips", ".sb allcantrips", QA, "Teaches your character every cantrip."),
        Command("Rename", ".name self <name> or .name pet <name>", QA, "Changes your wizard's name (relog to see it) or the name of the pet you are wearing. Use reset as the name to go back to the old one."),
        Command("Pet cast test", ".petcasttest <0-6> or .petcasttest off", QA, "Changes how a pet's May Cast is sent to the game, to find the way it plays. 0 = plain pet copy, 1 = plain normal card, 2 = pet flags with pet copy, 3 = pet flags with normal card, 4 = pet flags and forced with pet copy, 5 = pet flags and forced with normal card, 6 = forced normal card. Start a new battle after changing it."),
        Command("All teleport effects", ".givemetpstyle", QA, "Unlocks every teleport effect. Log out and back in to see them."),
        Command("Pick teleport effect", ".tpstyle <number>", QA, "Wears teleport effect number <number> (0 is the normal one). Try 1, 2, 3 and so on, log out and back in, then teleport to see it."),
        Command("Overdose", ".overdose on", QA, "Your battles start with Quickening already active. Type .overdose off to stop."),
        Command("All badges", ".givemeallbadges", QA, "Gives your character every badge in the game, so quests and doors that need a badge let you through."),
        Imlight("Give one badge", ".badge grant <badge name or ID>", QA, "Gives your character one badge and its reward. Type the badge's name or its ID number."),
        Imlight("Badge progress", ".badge progress <counter> <amount>", QA, "Adds to a badge counter, such as the number of a creature type you have defeated, and gives any badges it reaches."),
        Imlight("List my badges", ".badge list", QA, "Shows the badges your character has, with their progress."),
        Imlight("Reset badges", ".badge reset", QA, "Removes all of your character's badges and badge progress."),
        Command("Shadow magic", ".intothedarkness", QA, "Gives your character shadow magic of its own with 1 shadow pip slot, like the game does when you unlock it, plus the shadow gauge, even with the Shadow pips switch off. Log out and back in to see it."),
        Command("Backpack size", ".backpack 300   or   .backpack max", QA, "Sets how many items your backpack holds, up to 500 (max). Change area or log out and back in to see it in the backpack window."),
        Command("Bank size", ".bank 500   or   .bank max", QA, "Sets how many items your bank holds, up to 1000 (max)."),
        Command("Go to the Bazaar", ".bazaar", QA, "Takes you to the Bazaar in Olde Town."),
        Command("Trainer tomes", ".hall   or   .hall 30   or   .hall flip", QA, "Gives one trainer tome (the lectern Abner sells), or as many as the number. Each tome placed in your house becomes a set trainer: every spell the game's trainers teach, split by school into parts of up to 50 (Fire Trainer 1, Fire Trainer 2, then Ice Trainer 1...). A part never mixes schools. The parts go along your row of tomes starting at the end where you placed the first one, and each tome keeps its part. In the house, .hall flip starts the row from the other end (leave and come back to see it)."),
        Command("Seller furniture", ".hall vendors", QA, "Gives one piece of furniture per seller: Harvest, Rare Harvest, Shop, Drop, Event and Pet reagents, Spellements, and Circle, Square, Triangle, Tear and Star jewels and Power, Shield and Pip pins. Place one in your house and its seller stands on it, selling every item of its kind for 1 gold."),
        Command("Draw a card in battle", ".draw 2 Fire Cat   or   .draw Fire Cat", QA, "In battle, when it is your turn to pick, puts that many copies of a card in your hand (up to the room left in your hand of 7), even cards that are not in your deck. A card ID works too."),
        Command("Give pips in battle", ".pip give power max   or   .pip give fire 2", QA, "In battle, gives pips until the battle ends. Types: light (normal), power, shadow, a school for Archmastery pips (fire, ice, storm, myth, life, death, balance), or all (power and shadow pips only, never Archmastery pips). Use a number or max. It does not use mana."),
        Command("Harmony mode", ".harmony on   or   .harmony off", QA, "While on, every battle you start or join begins with all 7 pips as power pips and full shadow pips. Stays on until you turn it off."),
        Command("All-Knowing mode", ".allknowing on   or   .allknowing off", QA, "While on, you have mastery of every school in every battle (the amulet kind, not Archmastery), so power pips count as 2 for all your spells, shadow cards included. You can turn it on anywhere. Stays on until you turn it off."),
        Command("Give gear by name", ".hat 1 Malistaire Veil   or   .hat 1 60 Malistaire Veil   or   .pet 1 Chromatic Pet   or   .robe 2 1234567", QA, "Gives that many of an item, found by name or ID. Kinds: .hat, .robe, .boot, .wand, .athame, .amulet, .ring, .pet, .deck, .potion, or .gear for any item. The amount can be left out (1). A level after the amount picks the version of gear made for that level; with no level, the highest-level version is given. With a name, the best match of that kind is given; an ID is given as it is."),
        Command("Shop window test", ".shoptest 1   or   .shoptest reagent   or   .shoptest off", QA, "For finding the right shop window. A number makes every shop open with that shop type; reagent, snack, jewel, housing, recipe, mount or equipment makes every shop offer that kind of menu option. Walk away from the shop and back after each change. off puts shops back to normal."),
        Command("Light and Shadow mode", ".lightnshadow on   or   .lightnshadow off", QA, "While on, each battle round fills your pips back up as power pips, fills your shadow pips and refills your mana. School (Archmastery) pips stay as they are. Works well with .harmony on."),
        Command("Cast before your card (c1)", ".c1 e2 Fire Cat   or   .c1 me 12345   or   .c1 p2 Tower Shield", QA, "In battle, while you pick your card: casts that spell for free this round, right before your own card. Type it again to chain more spells, or put several in one line split by commas, each starting with c1, c2, cf or cl to pick its queue: .c1 Tower Shields, c2 Pyrodynamics, cl Fire Cat. The target is optional: me (or self) is you, p1 to p4 are the circles of your own side and e1 to e4 the enemy circles, numbered the way the game shows them (p1 is whoever stands on your side's circle 1, which may be a friend), and all hits everyone. s is only the silent option, so silent on yourself is .c1 s me Tower Shield. An empty circle or a defeated target is refused with a list of who stands on each circle. Without a target (or for spells that hit everyone), spells for enemies hit the enemy you picked (or the first one standing) and others go on you."),
        Command("Cast after your card (c2)", ".c2 e1 Fire Cat   or   .c2 12345", QA, "Like .c1, but the spell casts right after your own card this round."),
        Command("Cast first in the round (cf)", ".cf e all Fire Cat   or   .cf s me Tower Shield", QA, "Like .c1, but the spell casts before anyone's turn this round, before the boss cheats of the round's start."),
        Command("Cast last in the round (cl)", ".cl se all Fire Cat   or   .cl p1 Satyr", QA, "Like .c1, but the spell casts after everyone's turn this round, after the boss cheats of the round's end."),
        Command("Cast before your card, no animation (c1s)", ".c1s e2 Fire Cat", QA, "Like .c1, but the game skips the spell's casting animation, so the round goes faster. Mix them in one line: .c1s Tower Shields, c2 Pyrodynamics."),
        Command("Cast after your card, no animation (c2s)", ".c2s e1 Fire Cat", QA, "Like .c2, but the game skips the spell's casting animation."),
        Command("Cast first or last, no animation (cfs / cls)", ".cfs all Fire Cat   or   .cls e1 Fire Cat", QA, "Like .cf and .cl, but the game skips the spell's casting animation (the same as .cf s and .cl s)."),
        Command("Skip your casting animation (cast)", ".cast none", QA, "Your spells skip their casting animation until .cast reset or the server restarts. (Picking another school's animation isn't possible: the game always plays the spell's own.)"),
        Command("Boss-style message (ca)", ".ca 1   or   .ca gained power pip", QA, "In battle, shows one of the game's own lines like a boss's cheat line, before the next round's first spell. Use the phrase's number from the Phrases tab, or words from the line (.ca find <words> lists matches with their numbers). Typed text of your own can't be shown."),
        Command("Boss-style message in the cast queue", ".c1 Fire Cat, ca 1, Fire Cat", QA, "A ca part in a .c1, .c2, .cf or .cl chain shows that phrase over you just before the next spell in the queue (after the last one if nothing follows). .ca 1, 5 shows several phrases in a row before the round's first spell."),
        Command("Your battle stats (stats)", ".stats", QA, "Shows the damage, resist, pierce and accuracy the server fights with for each school, plus flat damage. Hold it next to the game's own Stats page: if a number differs, the server is reading your gear wrong. No diminishing returns, just the plain sums of gear, jewels and pet."),
        Command("Your side goes first (wegofirst)", ".wegofirst", QA, "In a battle against creatures, your side (you and your friends) acts first, from this round's spells on. The sun moves to your side."),
        Command("The other side goes first (theygofirst)", ".theygofirst", QA, "In a battle against creatures, the enemies act first, from this round's spells on."),
        Command("Add an enemy to your battle (joinme)", ".joinme Gobbler", QA, "In a battle against creatures, the creature joins on the enemy side and fights from the next round. Use its name or ID (.lookup mob finds them). x and a number at the end adds that many while there is room: .joinme Gobbler x2 (a plain number that is no custom does too). .joinme Malistaire the Undying gives the Castle Darkmoor one; add Azteca for the Azteca one."),
        Command("Add one of your customs to your battle (joinme)", ".joinme Malistaire the Undying 1", QA, "Brings in a custom from the Customs tab: the creature it is based on (name or ID), then the custom's number. .joinme 1336035 1 and .joinme Malistaire the Undying custom 1 do the same. It fights with the custom's name, stats, deck, cheats and drops; the creature itself is not changed. A custom based on another creature is refused."),
        Command("Free cast options (s / e)", ".cl se all Fire Cat   or   .c1 e Fire Cat 3", QA, "Options after .c1, .c2, .cf or .cl: s skips the casting animation, e adds the best enchantment that fits (Epic on attacks, Primordial on heals, Sharpened Blade on blades, Potent Trap on traps, Aegis or Indemnity); mix them like se. First and last in the round are their own commands, .cf and .cl (the old f and l options get a hint to use them). A number after the spell's name repeats it, up to 20 times: .c1 e Fire Cat 3. Target all casts a single-target spell once on every enemy (or every teammate for a heal or blade)."),
        Command("Mastery in battle", ".mastery fire   or   .mastery astral   or   .mastery all", QA, "In battle, you count as having that school's mastery until the battle ends, so power pips count as 2 for its spells. Schools: fire, ice, storm, myth, life, death, balance, sun, moon, star and shadow; astral means sun, moon and star; shadow also masters the seven schools, because shadow cards belong to one of them; all means every one. This is school mastery (the amulet kind), not Archmastery."),
        Command("Level 50 Elixir", ".elixir50", QA, "Like the crown shop's Level 50 Elixir, for free: sets your character to level 50 with its training points, finishes the main quests through Dragonspyre with the spells they teach, and gives the Dragonspyre quest gear for your school. Log out and back in to see everything."),
        Command("Shadow pip slots", ".sp slots <number>", QA, "Sets how many shadow pip slots your character has, from 1 to 8, such as .sp slots 2 for the second slot. Log out and back in to see it."),
        Command("Pet info", ".pet info", QA, "Shows the level, XP, stats and talents of the pet you are wearing."),
        Command("Feed your pet", ".pet feed <snack>", QA, "Feeds one snack from your snack bag to the pet you are wearing, by name or ID, for pet XP and stats. .pet feedall <snack> feeds the whole stack. Add snacks with .mod addsnack <id>."),
        Command("Pet XP", ".pet xp <amount>", QA, "Gives the pet you are wearing that much pet XP. It levels up and shows new talents on the way."),
        Command("Pet level", ".pet level <level>", QA, "Sets the pet you are wearing to a level, 1 to 7 or Baby, Teen, Adult, Ancient, Epic, Mega or Ultra, with a new talent for each level up to Mega."),
        Command("Pet talent", ".pet talent <name>", QA, "Gives the pet you are wearing a talent, such as .pet talent Pain-Giver. .pet untalent <name> takes one off."),
        Command("Pet talent slot", ".pet talent get <slot> <name>", QA, "Puts a talent into a slot of your pet's talent list, 0 at the top of the pet window and 9 at the bottom, without claiming it. .pet talent claim <slot> shows it (5 at most) and .pet talent unclaim <slot> hides it again."),
        Command("Pet derby slot", ".pet derby get <slot> <name>", QA, "The same as .pet talent get, claim and unclaim, for the derby talents."),
        Command("Set a pet stat", ".pet stats <stat> <amount|max>", QA, "Sets Strength, Intellect, Agility, Will, Power or Happiness of the pet you are wearing to an amount or its maximum. An amount above the maximum raises the maximum to it, such as .pet stats strength 260."),
        Command("Max pet stats", ".pet maxstats", QA, "Raises every stat of the pet you are wearing to its maximum."),
        Command("Hatch two pets", ".hatch <pet 1> <pet 2>", QA, "Hatches two of your own pets (worn or in your backpack) into an egg in your backpack. Type .hatch alone to list your pets with numbers, then use the numbers, such as .hatch 1 2, or names, such as .hatch Fire Cat Storm Dragon. The egg is one of the two kinds and gets talents from both parents. How long it takes and the gold it costs are under Settings, Hatching."),
        Command("Bundle to gift box", ".bundle <name>", QA, "Puts that bundle in your gifts (the gift button). Claim it there and pick the tier you want. Find the names here: pick Bundle in the list on the right, search, and click Send to my gifts."),
        Command("Clear gift box", ".bundle clear", QA, "Removes every unclaimed gift from your gift button list."),
        Command("Jewels you wear", ".jewel list", QA, "Lists the sockets on the gear and pet you are wearing and what is in them."),
        Command("Add a jewel", ".jewel add <jewel or pin>", QA, "Puts a jewel or pin, by name or ID, into the first empty socket of that shape on what you are wearing. You can also drag jewels onto gear in the game."),
        Command("Clear jewels", ".jewel clear", QA, "Empties every socket on the gear and pet you are wearing."),

        // Battles.
        Imlight("Win battle", ".cheat win", QA, "Wins the battle you are in right now."),
        Imlight("Fast spell animations", ".cheat cinematic", QA, "Turns instant spell animations on or off."),
        Imlight("No fizzle", ".cheat nofizzle", QA, "Turns spells that never fizzle on or off."),

        // Moving around.
        Imlight("Teleport", ".tp <zone>", QA, "Takes you to a zone. Part of the name works, such as .tp Ravenwood. .tp gm goes to the GM island. Also .teleport or .port. Search Zone in this tab for every name."),
        Imlight("Where am I", ".debug gps", QA, "Shows the zone you are in and where you are standing."),
        Imlight("Hide object", ".debug disableobj <zone tag>", QA, "Hides an object in this zone by its tag."),
        Imlight("Show object", ".debug enableobj <zone tag>", QA, "Shows an object hidden with .debug disableobj."),
        Imlight("List zone changes", ".dynamod list", QA, "Lists the zone changes (dynamods) saved on your character."),
        Imlight("Set zone change", ".dynamod set <on or off> <tag>", QA, "Turns a zone change on or off for your character."),
        Imlight("Toggle zone change", ".dynamod toggle <tag>", QA, "Flips a zone change between on and off."),
        Imlight("Remove zone change", ".dynamod remove <tag>", QA, "Removes a zone change from your character."),

        // Quests.
        Command("Go to quest", ".toquest \"quest name\"", QA, "Puts you on any quest to test it, by its name (Ghost Hunters) or code: marks the quests before it done, takes you to its first step and lists its steps. Steps that wait on a trigger are marked; skip one with .quest completegoal."),
        Imlight("Offer quest", ".quest offer <quest name>", QA, "Shows the quest's offer window, as if someone gave it to you. Search Quest in this tab for names."),
        Imlight("Give quest", ".quest grant <quest name>", QA, "Adds a quest to your quest book."),
        Imlight("Force give quest", ".quest forcegrant <quest name>", QA, "Adds a quest even when you could not normally get it."),
        Imlight("Complete quest", ".quest complete <quest name>", QA, "Finishes a quest."),
        Imlight("Complete current goal", ".quest completegoal", QA, "Finishes the goal you are on in your active quest."),
        Imlight("Complete quest goal", ".quest completegoalfor <quest name>", QA, "Finishes the goal you are on in a chosen quest."),
        Imlight("Remove quest", ".quest remove <quest name>", QA, "Takes a quest out of your quest book."),
        Imlight("My quests", ".quest list", QA, "Lists your quests."),
        Imlight("Active quest info", ".quest info", QA, "Shows details about your active quest."),
        Imlight("Show quest", ".quest show <quest name>", QA, "Shows details about any quest."),

        // Groups.
        Imlight("Group info", ".group info", QA, "Shows who is in your group."),
        Imlight("Kick from group", ".group kick <name>", QA, "Removes someone from your group. Also .group remove."),
        Imlight("Group leader", ".group leader <name>", QA, "Makes someone the group's leader."),
        Imlight("Disband group", ".group disband", QA, "Breaks up your group."),

        // Finding IDs in game.
        Command("Find item ID", ".lookup item <words>", QA, "Searches item names in game and shows their IDs."),
        Command("Find spell ID", ".lookup spell <words>", QA, "Searches spell names in game and shows their IDs."),
        Command("Find creature ID", ".lookup mob <words>", QA, "Searches creature names in game and shows their IDs. Also .lookup creature."),
        Command("Find NPC ID", ".lookup npc <words>", QA, "Searches NPC names in game and shows their IDs."),
        Command("Find any ID", ".lookup all <words>", QA, "Searches items, spells, creatures and NPCs at once."),

        // Players you have selected.
        Imlight("Mute player", ".mute <time> <reason>", HM, "Stops the selected player from chatting." + TIME + SELECTED),
        Imlight("Unmute player", ".unmute", HM, "Lets the selected player chat again." + SELECTED),
        Imlight("Kick player", ".kick <reason>", HM, "Disconnects the selected player." + SELECTED),
        Imlight("Warn player", ".warn <reason>", HM, "Adds a warning to the selected player's account." + SELECTED),
        Imlight("Player info", ".info", HM, "Shows the selected player's account details." + SELECTED),

        // Accounts.
        Imlight("Create account", ".account create <username> <password>", HM, "Makes a new account."),
        Imlight("Delete account", ".account delete <username>", ADMIN, "Deletes an account and its characters."),
        Imlight("Lock account", ".account lock <username>", HM, "Stops an account from logging in."),
        Imlight("Unlock account", ".account unlock <username>", HM, "Lets a locked account log in again."),
        Imlight("Change password", ".account password <username> <new password> <new password again>", ADMIN, "Changes an account's password."),
        Imlight("Permission level", ".account authlevel <username> <level>", ADMIN,
            "Changes what an account may do. Levels: None, QualityAssurance, HallMonitor, Developer, Administrator."),
        Imlight("Account info", ".account info <username>", HM, "Shows an account's details."),
        Imlight("Account warnings", ".account warnings <username>", ANYONE, "Lists an account's warnings and punishments. Also .account infractions or .account warns."),
        Imlight("Warn account", ".account warn <username> <reason>", HM, "Adds a warning to an account."),
        Imlight("Remove warning", ".account removewarn <username> <warning number>", HM, "Removes one of an account's warnings."),

        // Bans.
        Imlight("Ban account", ".ban account <username> <time> <reason>", HM, "Bans an account." + TIME + " Also .ban user."),
        Imlight("Ban computer", ".ban machine <machine ID> <time> <reason>", HM, "Bans a computer." + TIME + " Also .ban pc."),
        Imlight("Ban IP address", ".ban ip <IP address> <time> <reason>", HM, "Bans an internet address." + TIME),
        Imlight("Ban info", ".ban info <username>", HM, "Shows an account's bans."),
        Imlight("Unban account", ".unban account <username>", HM, "Lifts an account's ban. Also .unban user."),
        Imlight("Unban computer", ".unban machine <machine ID>", HM, "Lifts a computer's ban. Also .unban pc."),
        Imlight("Unban IP address", ".unban ip <IP address>", HM, "Lifts an internet address's ban."),
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

        // The game's bundles, written by the server (the list moved out of the game's chat, Niduus 2026-10-21).
        var bundles = Path.Combine(folder, "bundles.csv");
        if (File.Exists(bundles)) {
            foreach (var fields in ReadCsv(bundles)) {
                if (fields.Count < 2 || fields[0].Length == 0) {
                    continue;
                }

                entries.Add(new LookupEntry("Bundle", fields[0], "", fields[1] == "1" ? "1 gift" : $"{fields[1]} gifts",
                    $".bundle {fields[0]}", "Puts this bundle in your gifts (the gift button). Claim it there and pick the tier you want."));
            }
        }

        // The game's own lines for .ca, by number.
        var phrases = Path.Combine(folder, "phrases.csv");
        if (File.Exists(phrases)) {
            foreach (var fields in ReadCsv(phrases)) {
                if (fields.Count < 3 || !int.TryParse(fields[0], out _)) {
                    continue;
                }

                entries.Add(new LookupEntry("Phrase", fields[1], fields[0], "", $".ca {fields[0]}",
                    "In battle, shows this line like a boss's cheat line before the next round's first spell."));
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

    // Commands added for this build (Niduus's), shown under Custom in the Commands tab.
    private static LookupEntry Command(string name, string typed, string who, string help)
        => new("Command", name, "", who, typed, help, Custom: true);

    // Commands that come with Imlight itself.
    private static LookupEntry Imlight(string name, string typed, string who, string help)
        => new("Command", name, "", who, typed, help);

    /// <summary>
    /// The rows of a CSV file after its header line, allowing quoted fields.
    /// </summary>
    internal static IEnumerable<List<string>> ReadCsv(string path) {
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
