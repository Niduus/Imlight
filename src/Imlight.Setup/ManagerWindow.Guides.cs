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
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Imlight.Setup.Blocks;

namespace Imlight.Setup;

/// <summary>
/// The "How to" guides (Niduus, 2026-10-22: "Make it very clear and user friendly of how users can make custom
/// mobs/bosses/drop tables/areas and how to edit them"): numbered, click-by-click steps that name the buttons as the app
/// shows them, all on the Guides tab. The Customs and Bosses tabs and the Cheats section keep one line each that opens
/// their guide there. The words are kept here as data, so a guide is changed in one place; GuidesMarkdown() writes the
/// same words as a printable page. The "?" hints beside the confusing boxes of the Customs tab are here too.
/// </summary>
internal sealed partial class ManagerWindow {

    // ---------------------------------------------------------------------
    // What a guide is made of
    // ---------------------------------------------------------------------
    // In the words: [Save changes] is a button, box or section as the app names it (shown highlighted), and
    // `.joinme 1336035 1` is something to type. Example lines start with W| (a When block), I| (an If block) or
    // D| (a Do block).

    private abstract record GuideBit;

    /// <summary>Numbered steps under a heading.</summary>
    private sealed record GuideSteps(string Heading, params string[] Items) : GuideBit;

    /// <summary>Words and what they mean.</summary>
    private sealed record GuideTerms(string Heading, params (string Term, string Meaning)[] Rows) : GuideBit;

    /// <summary>A worked example, drawn as the blocks it is made of.</summary>
    private sealed record GuideExample(string Heading, params string[] Lines) : GuideBit;

    /// <summary>A note worth knowing.</summary>
    private sealed record GuideTip(string Text) : GuideBit;

    /// <summary>Buttons that do the thing the guide talks about (the app only; the printed page lists them).</summary>
    private sealed record GuideActions(params string[] Labels) : GuideBit;

    private sealed record Guide(string Id, string Icon, string Accent, string Short, string Title, string Line, GuideBit[] Bits);

    // ---------------------------------------------------------------------
    // The guides
    // ---------------------------------------------------------------------

    private const string BACKUP = "Save a backup to my desktop";
    private const string OPEN_EXTRA = "Open the server's additions folder";
    private const string OPEN_SERVER = "Open the Server folder";

    private static readonly Guide[] s_guides = [
        new("mob", "paw", "Customs", "Custom mob", "How to make a custom mob",
            "Your own creature, copied from one in the game. You pick a creature, give it a name, and Imlight gives it a number. " +
            "The creature in the game never changes.",
            [
                new GuideSteps("Make it",
                    "Click [Customs] in the list on the left side of the window.",
                    "Find the section [Make a custom, step by step]. If it is folded shut, click its title to open it.",
                    "Step 1: click in the search box and type the name of the creature to start from, such as `Gobbler` or `Malistaire`. " +
                    "Its ID number works too.",
                    "In the list that shows up, press [Use] beside the creature you want. Its picture and name show under the box and " +
                    "the circle of Step 1 turns into a green tick.",
                    "Step 2: type the name players will see in battle, such as `Gobbler King`. The creature's own name is already " +
                    "filled in; keep it if you like.",
                    "Step 3 shows the number your custom gets (for example \"It will be custom 2\") and the line that brings it into " +
                    "a battle. There is nothing to type here.",
                    "Step 4: press [Make the custom]. Its editor opens below, with the sections [Identity], [Stats], [Deck], [Drops], " +
                    "[XP and gold], [Forms] and [Cheats]. The round buttons at the top of the editor jump to each one."),
                new GuideSteps("Change its stats and spells",
                    "Press [Stats] at the top of the editor. Type a new number in [Health], [Level] or any other box. An empty box " +
                    "keeps the game's number, which shows in grey.",
                    "In the school grid (damage, resist and the rest), a number under [All] counts for every school you leave empty " +
                    "in that row.",
                    "Press [Deck] to see its spells. To add one, type its name under [Add a spell] and press [Add] beside it. To take " +
                    "one out, press [Remove].",
                    "[Copies] is how many times it may cast that spell in one battle. 0 means it never runs out.",
                    "Press [Save changes] at the bottom of the editor. There is no need to restart the server: its next battle uses " +
                    "the new values."),
                new GuideSteps("Try it in the game",
                    "Start the server ([Server] tab, [Start server]) and play ([Play] tab, [Play Wizard101]).",
                    "Start a battle with any creature.",
                    "In the chat box, type the line shown under [Bring it in] in the editor's [Identity] section, such as " +
                    "`.joinme 1336035 2`. The first part is the creature it is made from (its ID or its name), the last number is " +
                    "your custom's number. [Copy .joinme] in the [Custom mobs] list copies the line for you.",
                    "Your custom joins the battle on the enemy side. To bring in two, add x2 at the end: `.joinme Gobbler 2 x2`."),
                new GuideTip("If the game says you may not use the command, set your account to Quality assurance (or higher) on " +
                             "the [Accounts] tab."),
                new GuideTip("A custom never changes the creature in the game. The Gobblers you meet in the world stay as they are; " +
                             "only your custom is different. To put your custom into a place by itself, see How to make a custom area."),
            ]),

        new("boss", "bolt", "cheats", "Custom boss and cheats", "How to make a custom boss and give it cheats",
            "A custom boss is made like any custom. Cheats are the extra moves a boss makes outside its turn. Each one is a few " +
            "blocks that snap together and read like a sentence.",
            [
                new GuideSteps("Make the boss",
                    "Make a custom from the boss you want to start from (see How to make a custom mob). For example: search " +
                    "`Malistaire`, press [Use], name it `Malistaire the Unbroken` and press [Make the custom].",
                    "Give it more [Health] under [Stats] if you want a harder fight.",
                    "Press [Cheats] at the top of the editor. The boss's cheats from the game are listed in grey; the ones you make " +
                    "or change show in orange."),
                new GuideTerms("How a cheat reads",
                    ("W|When", "What sets the cheat off. Every cheat has one, at the top. Such as [a round starts], [its health drops], " +
                               "[a player casts] or [it is hit]."),
                    ("I|If", "Extra conditions. The cheat goes off only when all of them are true. Such as [chance], " +
                             "[only the first time], [in phase] or [health under]. With none, it goes off every time its When happens."),
                    ("D|Do", "What the boss does, from top to bottom. Such as [cast a spell], [heal], [bring in a minion], " +
                             "[say a line] or [next phase].")),
                new GuideSteps("Make a new cheat",
                    "Press [New cheat] under the list of cheats. A new cheat opens at the bottom with two blocks: \"At the start " +
                    "of every round\" and \"Cast pick a spell\".",
                    "To change what sets it off, click a block under [When… (what sets it off)] in the [Cheat blocks] list on the " +
                    "left. It takes the place of the orange block at the top.",
                    "To add a condition, click a block under [If… (only when)]. To add an action, click a block under " +
                    "[Do… (what it does)]. You can also drag a block from the list to the place you want it.",
                    "Fill in the boxes inside each block: type the numbers, click [pick a spell] to search a spell, and click " +
                    "[where the spell aims] to choose who it hits.",
                    "To move a block, drag it by the dots on its left. The small buttons on a block: the eye turns it off (it stays " +
                    "but does nothing), the two squares make a copy, the bin deletes it.",
                    "Press [Save changes] at the bottom of the editor. The next battle uses the cheat."),
                new GuideExample("Worked example: at 50% health it casts Supernova on every player",
                    "W|When its health drops below 50 % of its health",
                    "D|Cast Supernova on every player, dispel-proof",
                    "D|Tell every player in chat: You cannot stop me!"),
                new GuideSteps("How to build that example",
                    "Press [New cheat].",
                    "In the [Cheat blocks] list, under [When… (what sets it off)], click [its health drops]. The top block now reads " +
                    "\"When its health drops below\".",
                    "Type `50` in its box and leave [% of its health] picked beside it.",
                    "In the Cast block, click [pick a spell], type `Supernova` and click it in the list.",
                    "Click [where the spell aims] and pick [every player].",
                    "Under [Do… (what it does)], click [chat message] and type `You cannot stop me!` in its box.",
                    "Press [Save changes]."),
                new GuideExample("Another example: every 3 rounds, half of the time, it heals itself",
                    "W|At the start of every 3 rounds",
                    "I|If a 50 % chance comes up",
                    "D|Heal itself by 5,000 hit points"),
                new GuideTip("[its health drops] goes off once (once in each phase), so it does not need [only the first time]. A boss " +
                             "starts in phase 1; a [next phase] block moves it on, and [in phase] blocks give each phase its own cheats."),
                new GuideSteps("Change or turn off a cheat from the game",
                    "Press [Edit blocks] on the cheat to open it, then change any box. It is marked \"Changed from the game's\".",
                    "Press the eye button on a cheat to turn it off without deleting it. Press it again to turn it back on.",
                    "The curved arrow puts back the game's version of that one cheat. [Put back the game's cheats] puts back all of them.",
                    "The game's cheats keep their place in the list. Only cheats you made can be moved up and down or deleted."),
                new GuideTip("The cheats of a custom count only for that custom. The boss in the game, and its page on the Bosses tab, " +
                             "keep their own."),
            ]),

        new("edit", "pencil", "Customs", "Change, copy or delete", "How to change, copy or delete a custom",
            "Every custom you made is listed under Custom mobs on the Customs tab, with its number, its name and the creature it " +
            "comes from.",
            [
                new GuideSteps("Change a custom",
                    "Click [Customs] on the left, then open [Custom mobs].",
                    "Press [Edit] beside the custom. Its editor opens below, the same as when you made it.",
                    "Change what you like and press [Save changes]. Changes you do not save are not kept."),
                new GuideSteps("Make a copy",
                    "Press [Duplicate] beside a custom. A new custom with the next free number is made, with the same stats, deck, " +
                    "cheats and drops, and \"(copy)\" after its name.",
                    "Press [Edit] on the copy to change it. The first one stays as it was."),
                new GuideSteps("Delete a custom",
                    "Press [Delete] beside it, then [OK].",
                    "It is gone for good (make a backup first if you might want it again: see Where your work is kept). A room that " +
                    "had it as its boss gets the game's boss back."),
                new GuideSteps("Start it over without deleting it",
                    "Open the custom with [Edit].",
                    "Press [Put back the game's values] at the bottom of the editor, then [OK]. Its health, stats, deck and cheats go " +
                    "back to the creature's. Its number, name, drops and forms stay."),
                new GuideTip("Forms: a boss that turns into another creature in battle (Malistaire into Haunted Malistaire) is that " +
                             "creature from then on. To have it turn into your custom instead, open [Forms], press [Add a form] and type " +
                             "the creature's ID and the number of a custom made from that creature."),
            ]),

        new("drops", "bag", "drops", "Drops, XP and gold", "How to set what it drops (drop tables, XP and gold)",
            "What a creature gives when it is defeated comes from its drop tables, and from its XP and gold section. Both are in " +
            "the editor of a custom (Customs tab) or of a boss (Bosses tab).",
            [
                new GuideTerms("What is in a drop table",
                    ("Chance it drops", "How often the table gives anything at all in a won battle. 100 is every battle; 25 is about one in four."),
                    ("Gold", "A random amount from the first number to the second. 0 and 0: no gold from this table."),
                    ("XP", "Experience each wizard gets when the table pays out. [Training points] work the same way (usually 0)."),
                    ("How items drop", "[Each item by its own chance]: every line rolls on its own, so a win can give several items. " +
                                       "[One item, picked at random]: one item of the list, as the server's own tables do."),
                    ("Chance, Count", "For each line (when items drop by their own chance): how often it drops, in %, and how many " +
                                      "(Count 1 to 3 gives one, two or three)."),
                    ("Guaranteed", "Ticked: that line always drops, whatever its chance.")),
                new GuideTip("Tables made in the app roll each item by its own chance, and a Guaranteed item always drops. The server's " +
                             "own tables (the ones that came with Imlight) give one random item of their list."),
                new GuideSteps("Add a drop table to a custom or boss",
                    "Open the custom with [Edit] (or click a boss on the [Bosses] tab) and press [Drops] at the top of the editor.",
                    "Type a few letters in the search box under [Drop tables], such as `Malistaire` or `CL-MAIN`.",
                    "Press [Add] beside a table. It shows as a gold label above the box. Each table pays out on its own, so more " +
                    "tables means more loot.",
                    "To take a table off, press the small bin on its label. With no tables at all, it drops what the game's creature drops.",
                    "Press [Save changes] at the bottom of the editor. It counts from the next battle."),
                new GuideSteps("Make a drop table of your own",
                    "In [Drops], press [New drop table]. The table's editor opens below, with a name already filled in, such as " +
                    "`Malistaire-the-Unbroken-Drops`. Change it if you like: creatures drop a table by its name.",
                    "Set [Chance it drops], [Gold] (from and to), [XP] and [Training points].",
                    "Leave [How items drop] on [Each item by its own chance] to set each line's chance.",
                    "Under [Items, treasure cards and spells], search an item, treasure card or spell by name or ID, such as " +
                    "`Ghost Hat`, and press [Add]. Its picture shows on its line.",
                    "On each line, type its [Chance] in %, its [Count] (from and to), or tick [Guaranteed]. The bin takes the line off.",
                    "Press [Save and drop it here] to save the table and add it to this creature at once (then press [Save changes] " +
                    "at the bottom), or [Save the table] to only save it."),
                new GuideSteps("Change a drop table",
                    "In the search under [Drops], press [Change] beside one of your own tables, or the pencil on its gold label.",
                    "A table of the server's shows [Change a copy]: it opens as a copy named with \"-Copy\" at the end (such as " +
                    "`CL-MAIN-Copy`), and the server's own file stays as it is. [Save and drop it here] puts the copy in its place on " +
                    "this creature.",
                    "Change what you like and press [Save the table]. [Delete the table] removes one of your own."),
                new GuideSteps("Set XP and gold for the whole win",
                    "Press [XP and gold] at the top of the editor.",
                    "[XP]: the experience each wizard gets for the win. [Gold from] and [Gold to]: a random amount in between (leave " +
                    "[Gold to] empty for exactly [Gold from]).",
                    "[Who gets it]: [Each wizard gets it all], or [Split between the wizards] (four wizards get a quarter each).",
                    "A box left empty keeps what the drop tables and the game give. Items still come from the drop tables.",
                    "Press [Save changes]. It counts from the next win, without a restart."),
                new GuideTip("A drop table you change counts for every creature that drops it, not only this one. To change the loot " +
                             "of one custom only, make a new table for it (or change a copy)."),
                new GuideTip("Your tables are files in spiraldb-extra/DropTables; the tables each game creature drops are in the " +
                             "NpcDropTable folder (see Where your work is kept)."),
            ]),

        new("areas", "pin", "zone", "Custom areas", "How to make a custom area",
            "Change a place in the game, or make a private copy of it: add objects and sigils, add creatures, and choose who the " +
            "boss of a room is.",
            [
                new GuideTerms("Words you will see",
                    ("Zone", "One place in the game, written like WizardCity/WC_Ravenwood. Type a few letters (Ravenwood) and pick it from the list."),
                    ("Variant", "A private copy of a zone. Empty means you change the place itself, for everyone. A name (such as MyBossRoom) " +
                                "makes a copy that only a sigil can send a team into, fresh for each team."),
                    ("Template", "What an object or creature is, as the game's ID number. Press [Find] beside it to search by name."),
                    ("Sigil pad", "The circle a team stands on to be sent somewhere together, like a dungeon's entrance."),
                    ("When it counts", "Each section has a tag. Green [Changes at once]. Blue [Counts from its next use]: the next battle, " +
                                       "or the next team that goes in. Orange [Needs the zone to load again]: leave and go in again, " +
                                       "or restart the server for a shared place such as Ravenwood.")),
                new GuideSteps("Make a new area",
                    "Click [Customs] on the left and open [Custom areas], then [New custom area or variant].",
                    "In [Zone], type a few letters of the place, such as `Ravenwood`, and pick it from the list.",
                    "Leave [Variant] empty to change that place for everyone, or type a name to make a private copy.",
                    "In [Start from], keep [An empty area], or pick a copy of an area you already have.",
                    "Press [Make the area]. It shows in the list below. Click it to open its sections."),
                new GuideSteps("Add or move an object (such as the Shadow Portal)",
                    "Open the area, then [Objects and sigils].",
                    "Press [Add an object]. A new object opens.",
                    "Give it a [Name] of its own, such as `Shadow Portal 2`.",
                    "Beside [Template], press [Find], type what it is (such as `Shadow`) and click it in the list.",
                    "In the game, stand where you want it and take one step. Then press [Use my position]: X, Y, Z and the way you " +
                    "face are filled in. [Rotation °] turns it (90 is a quarter turn); [Size] 1 is its own size, 2 is twice as big.",
                    "Press [Save area]. Objects change in the game at once.",
                    "To move one that is already there, such as the Imlight Ravenwood Shadow Portal in the Ravenwood area, open it, " +
                    "change [Where] (or press [Use my position]) and press [Save area]."),
                new GuideSteps("Add a sigil that sends a team to your own room",
                    "In [Objects and sigils], press [Add a sigil pad]. It starts as a pad for 4 players.",
                    "Put it in place with [Use my position], as above.",
                    "In [Sends to zone], pick where it goes. In [Arrives at], press [Named place] to pick a spot of that zone, or " +
                    "stand there in the game and press [Use my position].",
                    "In [Into variant], type a name for a private copy, such as `MyBossRoom`, and press [Make this variant's area].",
                    "Press [Save area]. Then open the new variant's area in the list to fill it."),
                new GuideSteps("Put your custom in a room as its boss",
                    "Make the custom first (How to make a custom mob), from the boss that is in that room.",
                    "Open the room's area, then [Battles, helpers and the boss]. If the boss is not listed yet, press [Add a battle] and " +
                    "pick the boss with [Find] beside [Boss].",
                    "Under [Boss of this room], pick your custom, such as \"Custom 1: Malistaire the Unbroken\". This is saved at once " +
                    "and counts from the next time a team goes in.",
                    "To have every spawn of a creature come in as your custom, not only the boss, open [Spawn as a custom], press " +
                    "[Add a spawn swap], pick the creature and then your custom in [Spawns as]. Press [Save area].",
                    "To give the room the game's boss again, pick the first choice in [Boss of this room] (the game's)."),
                new GuideTip("Made a mistake? [Undo unsaved changes] throws away what you have not saved. [Go back to the original] (in " +
                             "[General]) puts the area back as it came with Imlight. [Delete this area] removes it. Point at a [?] " +
                             "beside a box to read what it does."),
            ]),

        new("real", "crown", "Bosses", "Real boss or custom", "How to change a real boss, or make a custom instead",
            "The Bosses tab changes the boss everyone meets in the game. A custom is a separate copy that fights only where you " +
            "bring it in, so the base creature never changes.",
            [
                new GuideTerms("Which one to use",
                    ("Bosses tab", "Changes the game's own boss, everywhere it fights, for every player."),
                    ("Customs tab", "Makes a new creature with its own number. The game's boss stays as it is; the custom fights only when " +
                                    "you bring it in with `.joinme` or make it the boss of a room.")),
                new GuideSteps("Change a real boss",
                    "Click [Bosses] on the left.",
                    "Type the boss's name in the search box, such as `Rattlebones`. Turn off [Bosses only] to find any creature.",
                    "Click the boss in the list. Its editor opens below, with [Identity], [Stats], [Deck], [Drops], [XP and gold] and [Cheats].",
                    "Change what you want and press [Save changes]. It counts from its next battle; there is no need to restart.",
                    "To undo everything for that boss, press [Put back the game's values]."),
                new GuideTip("With the search box empty, the bosses you changed are listed, marked Changed. The game's own files are " +
                             "never changed: Imlight lays your changes over them, so putting back always works."),
                new GuideTip("Want a hard version and keep the normal one? Make a custom on the Customs tab and leave the real boss as it is."),
            ]),

        new("files", "folder", "Game Files", "Files and backups", "Where your work is kept, backups and undo",
            "Everything you make here is kept in the server's additions folder, spiraldb-extra, inside the Server folder of the " +
            "place Imlight is installed. Updates of Imlight keep it.",
            [
                new GuideTerms("What is where (inside spiraldb-extra)",
                    ("Customs", "One file for each custom, by number (1.json, 2.json...): its stats, deck, cheats, drops and forms. " +
                                "Rooms.json says which custom is the boss of which room. Placements.json holds objects moved with the " +
                                "older live editor."),
                    ("Customs/Area originals", "Each area as it came with Imlight, kept the first time you save it, for [Go back to the original]."),
                    ("ZoneObjects", "One file for each custom area (its zone, and @variant for a private copy)."),
                    ("BossOverrides", "Your changes to real bosses from the Bosses tab, one file for each creature ID."),
                    ("BossCheats", "The bosses' cheats as they came with Imlight. They are never changed; your cheat changes are saved in " +
                                   "the custom's or the BossOverrides file."),
                    ("DropTables, NpcDropTable", "The drop tables, and which tables each game creature drops.")),
                new GuideActions(BACKUP, OPEN_EXTRA, OPEN_SERVER),
                new GuideSteps("Make a backup",
                    $"Press [{BACKUP}]. Imlight puts the whole spiraldb-extra folder (customs, areas, boss changes and drop tables) " +
                    "into one .zip file on your desktop, with the date and time in its name.",
                    "Keep that file somewhere safe, such as a USB stick or a cloud folder. Make one before big changes."),
                new GuideSteps("Put a backup back",
                    "Stop the server: [Server] tab, [Stop server].",
                    $"Press [{OPEN_SERVER}].",
                    "Open the backup .zip file and drag the spiraldb-extra folder inside it into the Server folder. When Windows asks, " +
                    "choose to replace the files.",
                    "Start the server again."),
                new GuideTerms("Undo",
                    ("Before you save", "For a custom or a boss, open it again ([Edit], or click it in the list) without saving. For an " +
                                        "area, press [Undo unsaved changes]."),
                    ("After you save", "[Put back the game's values] (everything), [Put back the game's cheats] (only cheats), the curved " +
                                       "arrow on one cheat, [Go back to the original] for an area, or put a backup back.")),
            ]),
    ];

    private static Guide GuideOf(string id) => s_guides.First(g => g.Id == id);

    // ---------------------------------------------------------------------
    // Where the guides show
    // ---------------------------------------------------------------------

    private readonly Dictionary<string, Section> _guideSections = [];

    /// <summary>The Guides tab: every guide, folded, under a line on how to read them.</summary>
    private Control GuidesTab() {
        var cards = new List<Control> {
            Card(new StackPanel {
                Spacing = 8,
                Children = {
                    new TextBlock { Text = "New here? Start with How to make a custom mob.", FontSize = ManagerTheme.FONT_SECTION, FontWeight = FontWeight.SemiBold },
                    GuideText("Each guide is a list of clicks, in order. Words like [Save changes] are the buttons, boxes and sections " +
                              "exactly as the app names them, and words like `.joinme 1336035 1` are what to type. Click a guide's " +
                              "title to open it."),
                    GuideChips(),
                },
            }),
        };
        foreach (var guide in s_guides) {
            var section = HowTo(guide);
            _guideSections[guide.Id] = section;
            cards.Add(section);
        }

        return Page([.. cards]);
    }

    // Where a tab used to hold a guide of its own, it keeps one line that opens that guide on the Guides tab
    // (Niduus, 2026-10-22: "move all the Guide-related UI's into the Guide section").

    /// <summary>The top of the Customs tab: a line that opens How to make a custom mob.</summary>
    private Control CustomsGuide() => GuideLink("mob", "How do customs work?");

    /// <summary>The top of the Bosses tab: a line that opens How to change a real boss, or make a custom instead.</summary>
    private Control BossesGuide() => GuideLink("real", "How does changing a boss work?");

    /// <summary>In the editor's Cheats section: a line that opens How to make a custom boss and give it cheats.</summary>
    private Control CheatsGuide() => GuideLink("boss", "How do cheats work?");

    /// <summary>One line: a question, and a button that opens its guide on the Guides tab.</summary>
    private Control GuideLink(string id, string question) {
        var guide = GuideOf(id);
        var open = new Button {
            Padding = new Thickness(6, 3, 12, 3), CornerRadius = new CornerRadius(16), VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = new StackPanel {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = {
                    ManagerTheme.Tile(guide.Icon, ManagerTheme.Accent(guide.Accent), 22),
                    new TextBlock { Text = "Open the guide", VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        ToolTip.SetTip(open, $"{guide.Title}, on the Guides tab");
        open.Click += (_, _) => ShowGuide(id);

        return new StackPanel {
            Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Left,
            Children = {
                WithCenter(ManagerTheme.Icon("info", 18, ManagerTheme.Accent("Guides"))),
                new TextBlock { Text = question, Foreground = ManagerTheme.Brush(ManagerTheme.TEXT_SOFT), VerticalAlignment = VerticalAlignment.Center },
                open,
            },
        };
    }

    // A button for each guide, which opens it further down the Guides tab.
    private Control GuideChips() {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var guide in s_guides) {
            var chip = new Button {
                Padding = new Thickness(6, 3, 12, 3), Margin = new Thickness(0, 0, 8, 6), CornerRadius = new CornerRadius(16),
                Content = new StackPanel {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children = { ManagerTheme.Tile(guide.Icon, ManagerTheme.Accent(guide.Accent), 22), new TextBlock { Text = guide.Short, VerticalAlignment = VerticalAlignment.Center } },
                },
            };
            ToolTip.SetTip(chip, guide.Title);
            var id = guide.Id;
            chip.Click += (_, _) => ShowGuide(id);
            row.Children.Add(chip);
        }

        return row;
    }

    /// <summary>Opens the Guides tab, with that guide open and in view.</summary>
    private void ShowGuide(string? id) {
        var tabs = this.GetLogicalDescendants().OfType<TabControl>().FirstOrDefault(t => t.TabStripPlacement == Dock.Left);
        if (tabs?.Items.OfType<TabItem>().FirstOrDefault(t => (t.Tag as string) == "Guides") is { } tab) {
            tabs.SelectedItem = tab;
        }

        if (id is not null && _guideSections.TryGetValue(id, out var section)) {
            foreach (var other in _guideSections.Values) {
                other.SetOpen(other == section);
            }

            Dispatcher.UIThread.Post(() => section.BringIntoView(), DispatcherPriority.Background);
        }
    }

    // ---------------------------------------------------------------------
    // Drawing a guide
    // ---------------------------------------------------------------------

    /// <summary>A guide as a foldable section of the theme: its steps, words, examples and tips.</summary>
    private Section HowTo(Guide guide) {
        var body = new StackPanel { Spacing = 16 };
        foreach (var bit in guide.Bits) {
            body.Children.Add(bit switch {
                GuideSteps steps => StepsView(steps, ManagerTheme.Accent(guide.Accent)),
                GuideTerms terms => TermsView(terms),
                GuideExample example => ExampleView(example),
                GuideTip tip => TipView(tip.Text),
                GuideActions actions => ActionsView(actions),
                _ => new Panel(),
            });
        }

        var section = new Section(guide.Icon, ManagerTheme.Accent(guide.Accent), guide.Title, guide.Line, body);
        section.SetBadge("How to");
        section.SetOpen(false);

        return section;
    }

    private static Control StepsView(GuideSteps steps, IBrush accent) {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = steps.Heading, FontSize = ManagerTheme.FONT_HEADING + 1, FontWeight = FontWeight.SemiBold });
        for (var i = 0; i < steps.Items.Length; i++) {
            panel.Children.Add(new DockPanel {
                Children = {
                    WithDock(new Border {
                        Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = accent, Margin = new Thickness(0, 0, 12, 0),
                        VerticalAlignment = VerticalAlignment.Top,
                        Child = new TextBlock {
                            Text = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), FontWeight = FontWeight.Bold, FontSize = 12,
                            Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                        },
                    }, Dock.Left),
                    WithCenter(GuideText(steps.Items[i])),
                },
            });
        }

        return panel;
    }

    private static Control TermsView(GuideTerms terms) {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = terms.Heading, FontSize = ManagerTheme.FONT_HEADING + 1, FontWeight = FontWeight.SemiBold });
        foreach (var (term, meaning) in terms.Rows) {
            var (kind, word) = BlockOf(term);
            Control label = kind is { } k
                ? BlockChip(k, word)
                : new TextBlock { Text = word, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            label.VerticalAlignment = VerticalAlignment.Top;
            panel.Children.Add(ManagerTheme.Item(new DockPanel {
                Children = {
                    WithDock(new Border { Width = 170, Margin = new Thickness(0, 0, 12, 0), Child = label }, Dock.Left),
                    GuideText(meaning, muted: true),
                },
            }));
        }

        return panel;
    }

    private static Control ExampleView(GuideExample example) {
        var blocks = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var line in example.Lines) {
            var (kind, text) = BlockOf(line);
            blocks.Children.Add(kind is { } k ? BlockChip(k, text, indent: k != BlockKind.When) : GuideText(text));
        }

        return new StackPanel {
            Spacing = 8,
            Children = {
                new TextBlock { Text = example.Heading, FontSize = ManagerTheme.FONT_HEADING + 1, FontWeight = FontWeight.SemiBold },
                new Border {
                    Background = ManagerTheme.Brush(ManagerTheme.FIELD), CornerRadius = new CornerRadius(ManagerTheme.RADIUS_SMALL),
                    BorderBrush = ManagerTheme.LINE_BRUSH, BorderThickness = new Thickness(1), Padding = new Thickness(14, 12), Child = blocks,
                },
            },
        };
    }

    private static Control TipView(string text) => new Border {
        Background = ManagerTheme.Brush(ManagerTheme.ACCENT, 26), BorderBrush = ManagerTheme.Brush(ManagerTheme.ACCENT, 90), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(ManagerTheme.RADIUS_SMALL), Padding = new Thickness(12, 8),
        Child = new DockPanel {
            Children = {
                WithDock(new Border { Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top, Child = ManagerTheme.Icon("info", 18, ManagerTheme.Brush(ManagerTheme.ACCENT_LIGHT)) }, Dock.Left),
                GuideText(text),
            },
        },
    };

    private Control ActionsView(GuideActions actions) {
        var row = new WrapPanel();
        foreach (var label in actions.Labels) {
            Func<Task> action = label switch {
                BACKUP => BackupExtraAsync,
                OPEN_EXTRA => () => Open(_manager.ExtraFolder),
                OPEN_SERVER => () => Open(Path.GetDirectoryName(_manager.ExtraFolder) ?? _manager.InstallFolder),
                _ => () => Task.CompletedTask,
            };
            var button = Action(label == BACKUP ? Primary(label) : Plain(label), action);
            button.Content = new StackPanel {
                Orientation = Orientation.Horizontal, Spacing = 7,
                Children = { ManagerTheme.TextIcon(label == BACKUP ? "download" : "folder", 15), new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } },
            };
            row.Children.Add(Spaced(button));
        }

        return new StackPanel { Spacing = 4, Children = { row, Sub(_manager.ExtraFolder) } };
    }

    // "W|When ..." is a When block's words; anything else is plain.
    private static (BlockKind? Kind, string Text) BlockOf(string line) => line.Length > 2 && line[1] == '|'
        ? (line[0] switch { 'W' => BlockKind.When, 'I' => BlockKind.If, 'D' => BlockKind.Do, _ => null }, line[2..])
        : (null, line);

    // A little block in the block editor's colours (When orange, If blue, Do purple).
    private static Control BlockChip(BlockKind kind, string text, bool indent = false) {
        var (fill, edge, icon) = kind switch {
            BlockKind.When => (ManagerTheme.BLOCK_WHEN, ManagerTheme.BLOCK_WHEN_EDGE, "flag"),
            BlockKind.If => (ManagerTheme.BLOCK_IF, ManagerTheme.BLOCK_IF_EDGE, "dice"),
            _ => (ManagerTheme.BLOCK_DO, ManagerTheme.BLOCK_DO_EDGE, "sparkle"),
        };

        return new Border {
            Background = ManagerTheme.Brush(fill), BorderBrush = ManagerTheme.Brush(edge), BorderThickness = new Thickness(0, 0, 0, 3),
            CornerRadius = kind == BlockKind.When ? new CornerRadius(12, 12, 6, 6) : new CornerRadius(6), Padding = new Thickness(10, 5),
            Margin = new Thickness(indent ? 16 : 0, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel {
                Orientation = Orientation.Horizontal, Spacing = 7,
                Children = {
                    ManagerTheme.Icon(icon, 14, Brushes.White),
                    new TextBlock { Text = text, Foreground = Brushes.White, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
    }

    /// <summary>A guide's words: [labels] highlighted as the app's buttons and boxes, `typed words` in the code font.</summary>
    private static TextBlock GuideText(string text, bool muted = false) {
        var inlines = new InlineCollection();
        var label = ManagerTheme.Brush(ManagerTheme.ACCENT_LIGHT);
        var labelBack = ManagerTheme.Brush(ManagerTheme.ACCENT, 34);
        var typed = ManagerTheme.Brush(ManagerTheme.GOOD);
        foreach (var (part, kind) in GuideParts(text)) {
            inlines.Add(kind switch {
                '[' => new Run(part) { Foreground = label, Background = labelBack, FontWeight = FontWeight.SemiBold },
                '`' => new Run(part) { Foreground = typed, FontFamily = MONO, FontWeight = FontWeight.SemiBold },
                _ => new Run(part),
            });
        }

        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Inlines = inlines, LineHeight = 21 };
        if (muted) {
            block.Foreground = ManagerTheme.Brush(ManagerTheme.TEXT_SOFT);
        }

        return block;
    }

    // The words cut into plain parts, [labels] and `typed words`.
    private static IEnumerable<(string Part, char Kind)> GuideParts(string text) {
        var plain = new StringBuilder();
        for (var i = 0; i < text.Length; i++) {
            var close = text[i] switch { '[' => ']', '`' => '`', _ => '\0' };
            var end = close == '\0' ? -1 : text.IndexOf(close, i + 1);
            if (end < 0) {
                plain.Append(text[i]);

                continue;
            }

            if (plain.Length > 0) {
                yield return (plain.ToString(), ' ');
                plain.Clear();
            }

            yield return (text[(i + 1)..end], text[i]);
            i = end;
        }

        if (plain.Length > 0) {
            yield return (plain.ToString(), ' ');
        }
    }

    // ---------------------------------------------------------------------
    // Backups
    // ---------------------------------------------------------------------

    private async Task BackupExtraAsync() {
        var folder = _manager.ExtraFolder;
        if (!Directory.Exists(folder)) {
            throw new PanelException("There is nothing to back up yet: the server's spiraldb-extra folder is not there. Start the server once.");
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop)) {
            desktop = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        var target = Path.Combine(desktop, $"Imlight customs backup {DateTime.Now:yyyy-MM-dd HH-mm-ss}.zip");
        await Task.Run(() => ZipFile.CreateFromDirectory(folder, target, CompressionLevel.Optimal, includeBaseDirectory: true));
        Say($"Saved a backup to your desktop as \"{Path.GetFileName(target)}\". It holds your customs, areas, boss changes and drop tables.");
    }

    // ---------------------------------------------------------------------
    // "?" hints beside the confusing boxes (the Customs tab's wizard and areas)
    // ---------------------------------------------------------------------

    /// <summary>What each step of the Make a custom wizard means, in more words.</summary>
    private static readonly string[] s_wizardHints = [
        "The creature your custom is copied from. Its look, spells and stats are the start; you change them afterwards. " +
        "Not sure of the name? Type part of it, such as Mali, or look it up on the Lookup tab.",
        "Only the name shown in battle. It may be the same as the creature's.",
        "The number is how you call this custom in a battle: .joinme, then the creature's ID or name, then this number. " +
        "Two customs made from the same creature get different numbers.",
        "Nothing in the game changes. Press Make the custom, change its stats, spells and cheats in the editor that opens, " +
        "and press Save changes there.",
    ];

    /// <summary>The boxes of a custom area, by their label, and what they mean in plain words.</summary>
    private static readonly Dictionary<string, string> s_fieldHints = new(StringComparer.OrdinalIgnoreCase) {
        ["Zone"] = "A zone is one place in the game, such as Ravenwood. Its name has a slash in it, like WizardCity/WC_Ravenwood. " +
                   "Type a few letters (Ravenwood) and pick it from the list that drops down.",
        ["Variant"] = "Leave it empty to change the place itself, for every player. Type a name (such as MyBossRoom) to make a " +
                      "private copy instead: only a sigil pad that names this variant sends a team in, and each team gets a fresh copy.",
        ["Start from"] = "An empty area starts with nothing added. A copy of an area you already have starts with all of its " +
                         "objects, creatures and battles.",
        ["Used"] = "An off switch for the whole area. Off keeps everything you made, but the server acts as if the area is not " +
                   "there. Turn it on again to bring it all back.",
        ["Template"] = "Every thing in the game has a template: an ID number that says what it is (a tree, a portal, a sigil " +
                       "pad...). Press Find and type a name to look it up; you do not need to know the number.",
        ["What it is"] = "A plain object is there to look at (or to be a door). A sigil pad sends a team somewhere. A battle " +
                         "circle is where a fight starts.",
        ["Where"] = "X and Y are where it stands on the ground, Z is how high. Rotation turns it, 0 to 360 degrees (90 is a " +
                    "quarter turn). Yaw is the same turn in the game's own unit; you can leave it. Size 1 is its normal size. " +
                    "Easiest: stand on the spot in the game, take one step, and press Use my position.",
        ["Start state"] = "Some objects can look different, such as lit or dark. Leave it empty unless you know the state's " +
                          "name, such as Glowy or Off.",
        ["Pad type"] = "The shape of the pad and how many players fit on it. Press the small arrow in the box to pick one.",
        ["Pad tag"] = "A second name the game uses for the pad. Copying the tag of a real sigil (such as Street 3 Tower 1 " +
                      "Instance Sigil) makes it look and act like that one. You can keep the one it has.",
        ["Pad size"] = "How close players must stand to be on the pad. 0 uses the pad's own size, which is usually right.",
        ["Sends to zone"] = "The place the team arrives in after standing on the pad.",
        ["Arrives at"] = "The spot in that place. Press Named place to pick one of the place's own spots (such as Start), or " +
                         "stand there in the game and press Use my position.",
        ["Into variant"] = "Empty: the team goes to the normal place. A name: the team goes into a private copy with that " +
                           "variant's changes. Make that copy with the button beside the box.",
        ["Lit state"] = "How the pad glows. Glowy is its normal On look.",
        ["Circle type"] = "The look of the battle circle and how many places it has for each side.",
        ["Join distance"] = "How close something must be to join a battle. 0 uses the normal distance.",
        ["Creature"] = "Which creature. Press Find and type its name; the ID number fills in for you.",
        ["Spawns as"] = "Pick one of your customs made from this creature. It comes in instead of the normal one.",
        ["Boss"] = "The boss of this battle. Press Find to look it up by name.",
        ["Helpers"] = "Creatures that can join the boss's side when the fight starts. Add each one you want.",
        ["How many join"] = "How many of the helpers in the list join the fight. 0 means none.",
        ["Spawners"] = "A spawner is a hidden spot that brings creatures in. The spawners named here never bring in two of the " +
                       "same creature at once. Pick from the zone lists the zone's own.",
        ["Left out"] = "Things of the zone you want gone, such as the game's own boss when you place your own. Pick from the zone " +
                       "lists what is there.",
        ["Goes to zone"] = "Where players go when they leave this place.",
        ["Ways out"] = "The names of the zone's exits that should go to your place. Leave it empty to change every exit.",
        ["Doors"] = "Objects players can walk up to and press X to leave by, for a way out the game does not have.",
        ["Boss of this room"] = "Which boss fights here: the game's own, or one of your customs made from it. This is saved at " +
                                "once, without pressing Save area.",
    };

    /// <summary>A small round "?" that shows the words when pointed at or clicked.</summary>
    private static Control HintBubble(string text) {
        var words = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };
        var bubble = new Border {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1.5),
            BorderBrush = ManagerTheme.Brush(ManagerTheme.ACCENT_LIGHT, 200), Background = ManagerTheme.Brush(ManagerTheme.ACCENT, 40),
            VerticalAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Help),
            Child = new TextBlock {
                Text = "?", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = ManagerTheme.Brush(ManagerTheme.ACCENT_LIGHT),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        ToolTip.SetTip(bubble, words);
        ToolTip.SetShowDelay(bubble, 150);
        FlyoutBase.SetAttachedFlyout(bubble, new Flyout { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 } });
        bubble.PointerReleased += (_, _) => FlyoutBase.ShowAttachedFlyout(bubble);

        return bubble;
    }

    /// <summary>A custom area box's label, with a "?" when the box has a hint.</summary>
    private static Control FieldLabel(string label, double width) {
        var text = new TextBlock { Text = label, FontWeight = FontWeight.Medium, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        if (!s_fieldHints.TryGetValue(label, out var hint)) {
            text.Width = width;
            text.Margin = new Thickness(0, 6, 0, 0);

            return text;
        }

        return new StackPanel {
            Orientation = Orientation.Horizontal, Spacing = 6, Width = width, Margin = new Thickness(0, 6, 0, 0), VerticalAlignment = VerticalAlignment.Top,
            Children = { text, HintBubble(hint) },
        };
    }

    /// <summary>A step title of the Make a custom wizard, with its "?".</summary>
    private static Control WizardTitle(int index, string text) => new StackPanel {
        Orientation = Orientation.Horizontal, Spacing = 8,
        Children = {
            new TextBlock { Text = text, FontSize = ManagerTheme.FONT_HEADING + 1, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
            HintBubble(s_wizardHints[Math.Clamp(index, 0, s_wizardHints.Length - 1)]),
        },
    };

    // ---------------------------------------------------------------------
    // The printable copy
    // ---------------------------------------------------------------------

    /// <summary>All the guides as one Markdown page, with the same words as the app.</summary>
    internal static string GuidesMarkdown() {
        static string Md(string text) => string.Concat(GuideParts(text).Select(p => p.Kind switch {
            '[' => $"**{p.Part}**",
            '`' => $"`{p.Part}`",
            _ => p.Part,
        }));

        var md = new StringBuilder();
        md.AppendLine("# Making customs in the Imlight Manager");
        md.AppendLine();
        md.AppendLine("Step-by-step guides for making and changing custom mobs, custom bosses and their cheats, drop tables and " +
                      "custom areas. Words in **bold** are the buttons, boxes and sections exactly as the app names them; words " +
                      "like `.joinme 1336035 1` are what to type. The same guides are in the app, on the **Guides** tab; the " +
                      "**Customs** and **Bosses** tabs and the **Cheats** section each have an **Open the guide** button for theirs.");
        md.AppendLine();
        md.AppendLine("## Contents");
        md.AppendLine();
        foreach (var guide in s_guides) {
            md.AppendLine($"- {guide.Title}");
        }

        foreach (var guide in s_guides) {
            md.AppendLine();
            md.AppendLine($"## {guide.Title}");
            md.AppendLine();
            md.AppendLine($"*{Md(guide.Line)}*");
            foreach (var bit in guide.Bits) {
                md.AppendLine();
                switch (bit) {
                    case GuideSteps steps:
                        md.AppendLine($"### {steps.Heading}");
                        md.AppendLine();
                        for (var i = 0; i < steps.Items.Length; i++) {
                            md.AppendLine($"{i + 1}. {Md(steps.Items[i])}");
                        }

                        break;
                    case GuideTerms terms:
                        md.AppendLine($"### {terms.Heading}");
                        md.AppendLine();
                        foreach (var (term, meaning) in terms.Rows) {
                            md.AppendLine($"- **{BlockOf(term).Text}**: {Md(meaning)}");
                        }

                        break;
                    case GuideExample example:
                        md.AppendLine($"### {example.Heading}");
                        md.AppendLine();
                        foreach (var line in example.Lines) {
                            var (kind, text) = BlockOf(line);
                            var tag = kind switch { BlockKind.When => "When block (orange)", BlockKind.If => "If block (blue)", BlockKind.Do => "Do block (purple)", _ => "" };
                            md.AppendLine(tag.Length > 0 ? $"> **{tag}:** {text}  " : $"> {text}  ");
                        }

                        break;
                    case GuideTip tip:
                        md.AppendLine($"> **Tip:** {Md(tip.Text)}");
                        break;
                    case GuideActions actions:
                        md.AppendLine("In the app, this guide has the buttons " + string.Join(", ", actions.Labels.Select(l => $"**{l}**")) + ".");
                        break;
                }
            }
        }

        return md.ToString();
    }

}
