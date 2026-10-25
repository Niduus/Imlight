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
    private const string ANYONE_AT_ALL = "Every player";
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
        Command("Reset training points", ".respec", "Every player", "Says how many training points you would get back and which spells you would lose. " +
            ".respec yes takes every spell you bought with training points out of your spellbook and decks and gives the points back. Also .resettp."),
        Imlight("Cantrip level up", ".mod clvlup", QA, "Raises your cantrip level by one. Also .mod cantriplevelup."),
        Imlight("Set cantrip level", ".mod clvl <level>", QA, "Sets your cantrip level. Also .mod cantriplevel."),

        // Spells.
        Imlight("Learn spell", ".sb learn <spell ID>", QA, "Adds a spell to your spellbook for good. Find the ID by searching for the spell in this tab."),
        Imlight("Unlearn spell", ".sb unlearn <spell ID>", QA, "Takes a spell out of your spellbook."),
        Imlight("Unlearn all spells", ".sb unlearnall", QA, "Takes every learned spell out of your spellbook."),
        Imlight("Add spell for now", ".sb add <spell ID>", QA, "Gives you a spell until you log out, without learning it."),
        Imlight("Remove spell for now", ".sb remove <spell ID>", QA, "Takes away a spell added with .sb add."),
        Imlight("Learn all cantrips", ".sb allcantrips", QA, "Teaches your character every cantrip."),
        Command("Deckathalon", ".deckathalon   or   .deckathalon start", ANYONE_AT_ALL, "Says which Deckathalon is on (the Events card of the Imlight app starts them) and climbs its tower: 30 floors, treasure cards only, no gear stats. .deckathalon leave stops, .deckathalon top shows the best wizards. Also .deck and .tower."),
        Command("Events and the Scroll of Fortune", ".events   or   .events claim", ANYONE_AT_ALL, "Lists the game events on (the Scroll of Fortune is always on; the Events card of the Imlight app queues the others), your points in each and the rewards waiting. .events claim claims every reward you have the points for, like the claim buttons of the event window. Also .scroll."),
        Command("Deckathalon any tower", ".deckathalon start <school>", QA, "Testers can climb any school's tower at any time, such as .deckathalon start ice. These are practice climbs with their own scores."),
        Command("Deckathalon credits", ".credits   or   .credits 10", QA, "Shows your credits in the Deckathalon that is on. Each climb of its tower uses a credit, and the tower portal window has a Claim button for free credits (3 every 24 hours; [Deckathalon] ClaimCredits, ClaimHours and EntryCredits in Settings). .credits 10 gives you 10 more, .credits -5 takes 5 away."),
        Command("Loyalty Store", ".loyalty   or   .loyalty claim", ANYONE_AT_ALL, "Says what Majid (Shopping District) sells now for Loyalty Tokens and how long for, and your tokens. .loyalty go takes you to him. .loyalty claim gets this set's tokens, like the Claim button in his window. Also .majid. The Loyalty Store card of the Imlight app sets when his wares change."),
        Command("Loyalty Tokens", ".loyalty tokens <number>", QA, "Testers can set their account's Loyalty Tokens, such as .loyalty tokens 50."),
        Command("Rename window", ".rename   or   .rename cancel", ANYONE_AT_ALL, "Turns on the game's own rename window for your wizard. Log out to the character select screen and pick the wizard: the game asks you for a new name from its name lists. It stays on for 15 minutes; .rename cancel turns it off."),
        Command("Rename now", ".rename <new name>", ANYONE_AT_ALL, "Renames your wizard straight away. A name from the game's lists (such as .rename Walter Blanco) becomes a real game name; anything else becomes a custom name (up to 31 letters). Relog to see it."),
        Command("Custom name", ".name self <name> or .name pet <name>", QA, "Changes your wizard's name (relog to see it) or the name of the pet you are wearing. Use reset as the name to go back to the old one."),
        Command("Pet cast test", ".petcasttest <0-6> or .petcasttest off", QA, "Changes how a pet's May Cast is sent to the game, to find the way it plays. 0 = plain pet copy, 1 = plain normal card, 2 = pet flags with pet copy, 3 = pet flags with normal card, 4 = pet flags and forced with pet copy, 5 = pet flags and forced with normal card, 6 = forced normal card. Start a new battle after changing it."),
        Command("All teleport effects", ".givemetpstyle", QA, "Unlocks every teleport effect. Log out and back in to see them."),
        Command("Pick teleport effect", ".tpstyle <number>", QA, "Wears teleport effect number <number> (0 is the normal one). Try 1, 2, 3 and so on, log out and back in, then teleport to see it."),
        Command("Overdose", ".overdose on", QA, "Your battles start with Quickening already active. Type .overdose off to stop."),
        Command("Castle Magic kit", ".castlemagic   or   .castlemagic objects   or   .castlemagic spells", QA, "Gives your character one of every Castle Magic object (levers, buttons, orbs, timers, detectors) and every Castle Magic spell it does not have yet. Place the objects in your house, then cast the spells onto them from the spellbook's Castle Magic tab. The spells are not used up."),
        Command("Garden status", ".garden", QA, "Shows your gardening rank and XP, and in a house how much soil and how many plants its garden has, how many are ready to harvest and how many need water, sun, pests taken away and so on."),
        Command("Garden: grow now", ".garden grow   or   .garden ready", QA, "In your own house: .garden grow moves every plant on to its next stage with all its needs met; .garden ready only makes the grown plants ready to harvest. For testing, so you do not wait hours."),
        Command("Garden: growth speed", ".garden speed <number>   or   .garden speed <seedling|young|mature|harvest|needs> <number>   or   .garden speed reset", QA, "How many times faster than the game every garden grows, such as .garden speed 60 (an hour becomes a minute), or one part only, such as .garden speed young 10. .garden speed reset goes back to the game's own times. It counts right away, also for plants already growing, and the Server tab's Plant growth card shows the same."),
        Command("Garden: pause", ".garden pause   or   .garden resume", QA, "Stops every garden (nothing grows, no needs or pests come) until .garden resume. Same as the Pause switch on the Plant growth card."),
        Command("Garden: set stage or age", ".garden stage <seedling|young|mature|elder|wilting|dead>   or   .garden age <30m|2h|1d>", QA, "In your own house: .garden stage mature puts every plant in that stage with fresh times; .garden age 2h lets two hours of plant time pass, so needs, pests and stages come as they would."),
        Command("Garden: plant times", ".garden times", QA, "In a house: lists each plant's stage, how long it has been in it, the time to its next stage and harvest, and the growth speed."),
        Command("Garden: set rank", ".garden rank <1-20>", QA, "Sets your gardening rank, such as .garden rank 5, and teaches the garden spells of that rank."),
        Command("Garden: spells and seeds", ".garden spells   or   .garden seeds", QA, ".garden spells teaches, for free, every garden spell of your rank you do not know yet (normally you buy them from the garden trainers, Farley and the others). .garden seeds gives you 3 of each of a few seeds Farley sells."),
        Command("All badges", ".givemeallbadges", QA, "Gives your character every badge in the game, so quests and doors that need a badge let you through."),
        Imlight("Give one badge", ".badge grant <badge name or ID>", QA, "Gives your character one badge and its reward. Type the badge's name or its ID number."),
        Imlight("Badge progress", ".badge progress <counter> <amount>", QA, "Adds to a badge counter, such as the number of a creature type you have defeated, and gives any badges it reaches."),
        Imlight("List my badges", ".badge list", QA, "Shows the badges your character has, with their progress."),
        Imlight("Reset badges", ".badge reset", QA, "Removes all of your character's badges and badge progress."),
        Command("Shadow magic", ".intothedarkness", QA, "Gives your character shadow magic of its own with 1 shadow pip slot, like the game does when you unlock it, plus the shadow gauge, even with the Shadow pips switch off. Log out and back in to see it."),
        Command("Backpack size", ".backpack 300   or   .backpack max", QA, "Sets how many items your backpack holds, up to 500 (max). Change area or log out and back in to see it in the backpack window."),
        Command("Bank size", ".bank 500   or   .bank max", QA, "Sets how many items your bank holds, up to 1000 (max)."),
        Imlight("Fishing", ".fishing   or   .fishing basket   or   .fishing sell", ANYONE_AT_ALL, "Shows your fishing level and XP, how full your fish basket is and which fish swim in this area. .fishing basket lists your fish; .fishing sell sells the whole basket for gold."),
        Imlight("Fishing (testing)", ".fishing spells   or   .fishing level 10   or   .fishing reset", QA, "Teaches every fishing spell, sets your fishing level, or fills this area's ponds with new fish."),
        Imlight("Guild", ".guild", ANYONE_AT_ALL, "Shows your guild, its leader and members (who is online), or the guild commands if you have no guild."),
        Imlight("Make a guild", ".guild create <name>   or   .guild create random", ANYONE_AT_ALL, "Makes a guild with you as leader. The name uses the game's guild words, up to three: a first word, a colour and a last word, such as Valiant Crimson Dragons. .guild names lists the words."),
        Imlight("Guild invites", ".guild invite <wizard>   .guild accept   .guild decline", ANYONE_AT_ALL, "Invites a wizard who is online to your guild; they type .guild accept within 10 minutes to join."),
        Imlight("Leave or run a guild", ".guild leave   .guild kick <wizard>   .guild leader <wizard>   .guild rename <name>   .guild disband yes", ANYONE_AT_ALL, "Leaving, and for the leader: removing a member, handing the guild over, renaming it or ending it."),
        Imlight("Guild house", ".guild house", ANYONE_AT_ALL, "Takes you to your guild's guild house: the leader gets the Guild House deed the first time, members visit the leader's guild house."),
        Imlight("Guild chat", ".g <words>", ANYONE_AT_ALL, "Says something to everyone in your guild who is online."),
        Imlight("Recent wizards", ".recent   or   .recent clear", ANYONE_AT_ALL, "Lists the wizards you battled, grouped, hatched or dueled with lately (the same list as the Friends window's Recent tab). .recent clear empties it."),
        Imlight("Add a recent wizard", ".recent add <wizard name or ID>", QA, "Puts a wizard who is online in your recent wizards list (and you in theirs), to try the Friends window's Recent tab without a battle first."),
        Imlight("Item Finder", ".itemfinder", ANYONE_AT_ALL, "Opens the game's Item Finder window: it shows where every item is, in your backpack, bank, shared bank, attic and each of your houses. A Backpack Butler placed in your house opens it too (press X at the Butler), and so does every bank (Item Finder next to the bank's own option). Also .finditem."),
        Command("Go to the Bazaar", ".bazaar", QA, "Takes you to the Bazaar in Olde Town."),
        Command("Trainer tomes", ".hall   or   .hall 30   or   .hall flip", QA, "Gives one trainer tome (the lectern Abner sells), or as many as the number. Each tome placed in your house becomes a set trainer: every spell the game's trainers teach, split by school into parts of up to 50 (Fire Trainer 1, Fire Trainer 2, then Ice Trainer 1...). A part never mixes schools. The parts go along your row of tomes starting at the end where you placed the first one, and each tome keeps its part. In the house, .hall flip starts the row from the other end (leave and come back to see it)."),
        Command("Seller furniture", ".hall vendors", QA, "Gives one piece of furniture per seller: Harvest, Rare Harvest, Shop, Drop, Event and Pet reagents, Spellements, and Circle, Square, Triangle, Tear and Star jewels and Power, Shield and Pip pins. Place one in your house and its seller stands on it, selling every item of its kind for 1 gold."),
        Command("Set a gauntlet's sigil here", ".sigil here   or   .sigil here face", QA + ", or the owner of the house you are in", "In your house, near a placed gauntlet (the nearest within 600): the gauntlet's sigil moves so its centre is where you stand and its arc faces away from the gauntlet (with face: the way you are facing). It is saved for that gauntlet, so every copy of it in every house uses it, and it moves at once. The Customs tab's House gauntlet sigils card lists what is set."),
        Command("Gauntlet sigil size", ".sigil size 0.8", QA + ", or the owner of the house you are in", "Sets the size of the nearest gauntlet's sigil (1 is the normal tower sigil, from 0.1 to 5). Saved for that gauntlet everywhere."),
        Command("Raise or lower a gauntlet sigil", ".sigil up 10   or   .sigil down 10", QA + ", or the owner of the house you are in", "Raises or lowers the nearest gauntlet's sigil by that much, such as onto a porch or a step. Saved for that gauntlet everywhere."),
        Command("Turn a gauntlet sigil", ".sigil turn 90   or   .sigil turn -45", QA + ", or the owner of the house you are in", "Turns the arc of the nearest gauntlet's sigil by that many degrees. After .sigil here it turns where it stands; otherwise the whole sigil swings round the gauntlet."),
        Command("Move a gauntlet sigil", ".sigil forward 10   or   .sigil back   or   .sigil left 20   or   .sigil right 5", QA + ", or the owner of the house you are in", "Moves the nearest gauntlet's sigil by that much (10 with no number): forward is further out from the gauntlet's front, back is toward it, and left and right are as you stand before the gauntlet looking at it. It starts from where the sigil is now, set by .sigil here or not, and keeps its turn. Also fwd and backward. Saved for that gauntlet everywhere, and it moves at once."),
        Command("Gauntlet sigil info", ".sigil info", QA + ", or the owner of the house you are in", "Shows the nearest gauntlet's name and template and its sigil now: how far and which side, how high, its turn and size, and whether it was set by hand."),
        Command("Reset a gauntlet sigil", ".sigil reset", QA + ", or the owner of the house you are in", "Puts the nearest gauntlet's sigil back to its defaults (the Reset button on the Customs tab's House gauntlet sigils card does the same)."),
        Command("Draw a card in battle", ".draw 2 Fire Cat   or   .draw Fire Cat", QA, "In battle, when it is your turn to pick, puts that many copies of a card in your hand (up to the room left in your hand of 7), even cards that are not in your deck. A card ID works too."),
        Command("Give pips in battle", ".pip give power max   or   .pip give fire 2", QA, "In battle, gives pips until the battle ends. Types: light (normal), power, shadow, a school for Archmastery pips (fire, ice, storm, myth, life, death, balance), or all (power and shadow pips only, never Archmastery pips). Use a number or max. It does not use mana."),
        Command("Harmony mode", ".harmony on   or   .harmony off", QA, "While on, every battle you start or join begins with all 7 pips as power pips and full shadow pips. Stays on until you turn it off."),
        Command("All-Knowing mode", ".allknowing on   or   .allknowing off", QA, "While on, you have mastery of every school in every battle (the amulet kind, not Archmastery), so power pips count as 2 for all your spells, shadow cards included. You can turn it on anywhere. Stays on until you turn it off."),
        Command("Give gear by name", ".hat 1 Malistaire Veil   or   .hat 1 60 Malistaire Veil   or   .pet 1 Chromatic Pet   or   .robe 2 1234567", QA, "Gives that many of an item, found by name or ID. Kinds: .hat, .robe, .boot, .wand, .athame, .amulet, .ring, .pet, .deck, .potion, or .gear for any item. The amount can be left out (1). A level after the amount picks the version of gear made for that level; with no level, the highest-level version is given. With a name, the best match of that kind is given; an ID is given as it is."),
        Command("Shop window test", ".shoptest 1   or   .shoptest reagent   or   .shoptest off", QA, "For finding the right shop window. A number makes every shop open with that shop type; reagent, snack, jewel, housing, recipe, mount or equipment makes every shop offer that kind of menu option. Walk away from the shop and back after each change. off puts shops back to normal."),
        Command("Photomancy camera", ".photo", ANYONE_AT_ALL, "Opens the Photomancy tome, the game's camera, anywhere. Take a photo there and the game saves it on your computer. Every filter is open. Photos of quest objects (such as the Darkmoor photo quests) count for the quest. The real tome stands in the Golem Tower too: press X at it."),
        Command("My spell upgrades", ".upgrades", ANYONE_AT_ALL, "Lists the upgradable spells you know (such as Fire Cat), how many of each one's Spellements you have, and what each next tier (path A, B, C or D) costs."),
        Command("Upgrade a spell", ".upgrade Fire Cat B   or   .upgrade Fire Cat   or   .upgrade reset Mortality", ANYONE_AT_ALL, "Upgrades a spell you know to its next tier along that path (path A when none is given), paying its Spellements, the same as the Upgrade button in the spellbook's spell upgrade page. The new tier is added to your spellbook and the old tier stays, so every path can be unlocked; cards in your decks change to the new tier. .upgrade reset takes all upgraded tiers of a spell away again (back to the plain spell, no Spellements back), such as a top tier learned with Learn spell."),
        Command("Give Spellements", ".spellements 200", QA, "Sets your Spellements to exactly that many (up to 999) for every upgradable spell you know, for trying spell upgrades. The upgrade window shows 999+ when you have more than 999, so this also trims extra ones. The Spellement Seller (.hall vendors) sells them for 1 gold too."),
        Command("Guild Museum", ".museum   or   .museum avalon   or   .museum khrysalis   or   .museum avalon list   or   .museum back", ANYONE_AT_ALL, "Shows how far your Avalon and Khrysalis museum galleries are. .museum avalon (or khrysalis) takes you into that gallery, list shows each exhibit's tier, back takes you to the museum island of your guild grounds. Defeating a creature fills the museum artifacts it has with essence, and an exhibit goes up a tier when all its artifacts have enough. Press X at an exhibit stand to see what it still needs. A guild fills one museum together."),
        Command("Guild Museum test", ".museum fill 3   or   .museum fill avalon 1   or   .museum fill 0   or   .museum ids index   or   .museum show", QA, "For testing the museum: fill sets every artifact (of one gallery, or both) to what a tier needs (0 empties it again). ids picks the numbers the museum messages use (hash, index, index1 or door) until the server restarts, to find the ones that make the exhibit stands light up. show sends the gallery's exhibits again."),
        Command("Redeem a code", ".code HYPER", ANYONE_AT_ALL, "Redeems a code made on the Server tab's Codes card and says what it gave, such as \"Success on redeeming: 1 Hyper Elixir\"."),
        Command("Join a team", ".teamup", ANYONE_AT_ALL, "Opens Join a Team: pick a world, then a team a player asked for at a dungeon sigil, and you join it. When the team is full (or everyone presses Ready) the whole team goes into the dungeon together. A Join A Team kiosk does the same. Also .team."),
        Command("Ask for a team", ".teamup ask or .teamup ask <2-4>", ANYONE_AT_ALL, "While standing on a dungeon sigil: asks for a team for that dungeon, the same as the sigil's Team Up! button. .teamup list shows the teams waiting."),
        Command("Test Pixie Post", ".mail   or   .mail Hello there", ANYONE_AT_ALL, "Sends you a Pixie Post from yourself, so you can try the mail without a second wizard. The new mail button lights up; open your friends list and its Mail tab to read it, then try Reply and Delete. Real Pixie Posts are sent from the friends list to a friend."),
        Command("Watch what the game sends", ".code watch", QA, "For 2 minutes, the server log gets every message your game sends (look for \"Code watch\" in the Logs tab). Use it to see what a button sends."),
        Command("Magic Mirror", ".mirror", ANYONE_AT_ALL, "Takes you to the Wizard City Magic Mirror Shop. Talk to the Magic Mirror there to change your hair, face, skin, eyes and more (free). Your new look comes back each time you enter a zone; character select still shows the old one."),
        Command("Undo the mirror look", ".mirror reset", ANYONE_AT_ALL, "Drops the look you picked in the Magic Mirror. Change zones or relog to see your old look."),
        Command("Change gender", ".mirror girl   or   .mirror boy", ANYONE_AT_ALL, "Makes your wizard a girl or a boy. Log out to character select and back in to see it, then visit the Magic Mirror for a new look."),
        Command("Set mirror look numbers", ".mirror look 3 1 2", QA, "Sets your look by numbers, in this order: hair style, hair color, skin color, face, face paint, eye color, nose, mouth, accessory, eyebrow color, lip color (-1 leaves a part as it is). For finding the right numbers if the mirror picks the wrong ones."),
        Command("Magic Mirror data", ".mirror data", QA, "Writes magic-mirror-data.txt next to the server: every mirror option and the number it becomes. Open a Magic Mirror first."),
        Command("Daily Assignment", ".daily   or   .daily new", ANYONE_AT_ALL, "Shows today's Daily Assignment of the Gravulum Order (the same as Aegon Statz's Daily Assignment option in Ravenwood): a creature to defeat or a place to visit, and the reward for your streak. .daily new swaps it for another one if you have not done it yet."),
        Command("Test Daily Assignments", ".daily done   or   .daily day 5   or   .daily reset", QA, "For testing: .daily done finishes today's assignment and gives its reward, .daily day 5 makes the next finished one day 5 of your streak (to see each day's reward), .daily reset clears your assignment and streak."),
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
        Command("Damage lines on or off (stats damage)", ".stats damage", QA, "Turns on or off the chat lines after each of your hits that say what it really dealt to each enemy (the game's own pop-up can show less). Off until you turn them on."),
        Command("Your side goes first (wegofirst)", ".wegofirst", QA, "In a battle against creatures, your side (you and your friends) acts first, from this round's spells on. The sun moves to your side."),
        Command("The other side goes first (theygofirst)", ".theygofirst", QA, "In a battle against creatures, the enemies act first, from this round's spells on."),
        Command("Add an enemy to your battle (joinme)", ".joinme Gobbler", QA, "In a battle against creatures, the creature joins on the enemy side and fights from the next round. Use its name or ID (.lookup mob finds them). x and a number at the end adds that many while there is room: .joinme Gobbler x2 (a plain number that is no custom does too). .joinme Malistaire the Undying gives the Castle Darkmoor one; add Azteca for the Azteca one."),
        Command("Add one of your customs to your battle (joinme)", ".joinme Malistaire the Undying 1", QA, "Brings in a custom from the Customs tab: the creature it is based on (name or ID), then the custom's number. .joinme 1336035 1 and .joinme Malistaire the Undying custom 1 do the same. It fights with the custom's name, stats, deck, cheats and drops; the creature itself is not changed. A custom based on another creature is refused."),
        Command("Free cast options (s / e)", ".cl se all Fire Cat   or   .c1 e Fire Cat 3", QA, "Options after .c1, .c2, .cf or .cl: s skips the casting animation, e adds the best enchantment that fits (Epic on attacks, Primordial on heals, Sharpened Blade on blades, Potent Trap on traps, Aegis or Indemnity); mix them like se. First and last in the round are their own commands, .cf and .cl (the old f and l options get a hint to use them). A number after the spell's name repeats it, up to 20 times: .c1 e Fire Cat 3. Target all casts a single-target spell once on every enemy (or every teammate for a heal or blade)."),
        Command("Mastery in battle", ".mastery fire   or   .mastery astral   or   .mastery all", QA, "In battle, you count as having that school's mastery until the battle ends, so power pips count as 2 for its spells. Schools: fire, ice, storm, myth, life, death, balance, sun, moon, star and shadow; astral means sun, moon and star; shadow also masters the seven schools, because shadow cards belong to one of them; all means every one. This is school mastery (the amulet kind), not Archmastery."),
        Command("Crafting window anywhere", ".craft", QA, "Opens the game's crafting window wherever you are, with every recipe you know. Crafting uses up the reagents, items and treasure cards the recipe needs and gives what it makes. Crafting stations in the world, recipe vendors and crafting stations placed in your house open it too, with only their own recipes."),
        Command("Your recipes", ".recipes", QA, "Lists the recipes you know. Buy recipes at a recipe shop (they go into the recipe book of your spellbook) or use .recipe learn."),
        Command("Learn a recipe", ".recipe learn <name or id>", QA, "Teaches you a recipe by its game name or number, such as .recipe learn Recipe-Snack-Pizza. Find names with .recipe find. .recipe forget <name> takes one away."),
        Command("Find a recipe", ".recipe find <text>", QA, "Lists up to 25 of the game's recipe names that contain the text, such as .recipe find Boots."),
        Command("Crafting cooldowns", ".recipe cooldowns   or   .recipe cooldowns clear", QA, "Shows which crafting slots are in use and how long until each is free. clear frees them all now."),
        Command("Craft a recipe now", ".recipe make <name or id>   or   .recipe make <name or id> <count>", QA, "Crafts a recipe you know right where you stand, with the amounts the Manager's Recipes tab set. Use it when the game's crafting window greys out Create."),
        Command("Level 50 Elixir", ".elixir50", QA, "Like the crown shop's Level 50 Elixir, for free: sets your character to level 50 with its training points, finishes the main quests through Dragonspyre with the spells they teach, and gives the Dragonspyre quest gear for your school. Log out and back in to see everything."),
        Command("Monstrology tome", ".tome", ANYONE_AT_ALL, "Opens the Monstrology tome (the T key does too) and says your Monstrology level, XP and how many monsters you have Animus from. Put an Extract spell on a damage spell in battle to collect Animus; win the battle to keep it."),
        Command("Monstrology level", ".tome level <number>", QA, "Sets your Monstrology level, from 1 to 35, such as .tome level 20. Monsters of a higher rank rarely give Animus and can't be made into cards or house guests."),
        Command("Add Animus", ".tome animus <monster name or id> <number>", QA, "Adds Animus for a monster to your Monstrology tome, such as .tome animus Dishonored Samoorai 30, so you can test the tome's Create buttons."),
        Command("Shadow pip slots", ".sp slots <number>", QA, "Sets how many shadow pip slots your character has, from 1 to 8, such as .sp slots 2 for the second slot. Log out and back in to see it."),
        Command("Look like your pet (petform)", ".petform", QA, "Turns your wizard into the pet you are wearing, like a transformation elixir, and hides your pet meanwhile. The Play as Pet button beside your pet's picture does the same. Other players see it too. Type .petform again to turn back. It wears off when you log out."),
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

    private Dictionary<(string, string), LookupEntry>? _byId;

    /// <summary>The entry of a kind (Item, Spell, Creature, NPC) with this ID, or null.</summary>
    internal LookupEntry? ById(string kind, string id) {
        _byId ??= _entries.Where(e => e.Id.Length > 0).GroupBy(e => (e.Kind, e.Id)).ToDictionary(g => g.Key, g => g.First());

        return _byId.TryGetValue((kind, id), out var entry) ? entry : null;
    }

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
