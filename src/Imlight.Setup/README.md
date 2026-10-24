# ImlightSetup.exe

A one-click installer for **original Imlight** ([Revive101/Imlight](https://github.com/Revive101/Imlight)), for people with no development tools.

The same program also builds as **ImlightManager.exe**, which installs this fork (randomizer mode, ID lookup) into `%LOCALAPPDATA%\ImlightManager` instead. The two can be installed side by side.

## What it does

1. **Finds Wizard101** in the usual places (KingsIsle or Steam). If it can't, it asks for the folder.
2. **Installs the .NET 10 SDK** into the Imlight folder if the computer doesn't have it. Nothing else on the computer changes.
3. **Downloads the latest Imlight** and the exact Imcodec version it uses from GitHub. Git is not needed.
4. **Reads the game files Imcodec needs** (`*Messages*.xml`) straight out of the player's own `Root.wad`, and uses the type dump Imcodec ships. Nothing copyrighted is downloaded or shared.
5. **Builds the server** into `%LOCALAPPDATA%\ImlightServer\Server`.
6. **Asks for an admin account** on a first install.
7. **Installs `Imlight.exe`, the app, and opens it**. The only shortcut is the app's, on the desktop; everything else is inside the app. Shortcuts and the Start menu folder from earlier versions are removed. Updating also adds any new settings to your existing `Config\Imlight.ini` without changing the ones you set. Imlight is also listed under **Settings > Apps**, so it can be uninstalled from there.

## No patch server needed

Imlight reads game files from a patch server. On a home computer, starting the server runs a small one built into `Imlight.exe` that hands the server the files from the player's own Wizard101 folder (`Data\GameData`), then starts the server. Nothing is downloaded and nothing leaves the computer. If something else (such as Aurorium) already uses the patch server port, Imlight uses that instead. The Aurorium card on the Tools tab installs [Aurorium](https://github.com/Revive101/Aurorium) into any folder you pick and starts it, so it downloads every game file; with **Server uses Aurorium's files** on, the server is given Aurorium's files (newest revision first), and Wizard101's for any it doesn't have yet.

If the server asks for an area's file that the player's game hasn't downloaded yet, the window says so. Visiting that area once in the real game downloads it.

## The app

Opening `Imlight.exe` (the desktop shortcut) shows the app in its own dark window. The window is drawn by the program itself (Avalonia), so it uses no web browser, no web view and no account. Closing the window stops the server. Everything is in one place:

- **Play**: play Wizard101 on the server on this computer (it starts the server first if needed), or on another private server you save by address and port. The live game is refused, by name (`wizard101.com`, `kingsisle.com`, `gameforge.com`) and by address. A server on this computer cannot use a port Imlight needs (login, game, patch, database, game files, which is also Aurorium's, or the app's own 12380 to 12399).
- **Lookup**: every in-game command with a plain name, what to type and who may use it, plus every item, spell, creature, NPC, zone and quest with its ID or name and the command that uses it. One search box covers all of it, with a filter by kind and Copy buttons. The game lists come from files the server writes to its ID lookup folder (`ids.csv`, `zones.txt`, `quests.csv`). Picking **Spell** switches to a spell view: a list on the left (school, pip cost, accuracy, type and what the spell does on each row, so same-named spells are easy to tell apart) and, on the right, the card as the game draws it for the spell under the mouse, or the one last clicked. The cards are read straight from the game's `.wad` files (spells, text and fonts from `Root.wad`; art, frames and icons from whichever `.wad` has them), never changed, with a Learn on my wizard button that sends `.sb learn <ID>`.
- **Quests**: every quest in the order it is played, for the wizard picked at the top: Done, In progress, Next up (every quest it needs is done) or Later, with filters for world and main story and a search box. **Start** runs `.quest forcegrant <code>`, **Teleport** runs `.tp <zone>` to the zone of the NPC who gives it (or of its first step when the giver's zone is not known), and **Finish** runs `.quest complete <code>` on a quest in progress. The server writes the list to `quest-guide.txt` in its ID lookup folder at start, and each wizard's done and current quests to `quest-progress/<wizard id>.txt` when a zone loads and when a quest starts, ends or is removed.
- **Bosses**: pick a boss (or any creature that fights) and change its health, level, school, damage, resist, incoming boost (weakness), pierce, critical and block rating school by school (a grid with an All column), accuracy, healing, pips, most shadow pips, whether it can be stunned or beguiled, and its deck of spells (add by searching a spell's name, remove, set how many copies), and its cheats from `spiraldb-extra/BossCheats/<ID>.json`: each shown in plain words, changed field by field (the game's setting in grey), turned off, put back or removed, new ones added, each casting a list of spells in order (or one at random), each spell silent or not and dispel immune or not. Saving writes `spiraldb-extra/BossOverrides/<ID>.json` in the server folder; the server lays it over the game's values from the boss's next battle, with no restart. The list comes from `creatures.csv` in the ID lookup folder.
- **Customs**: custom mobs and custom areas. A custom is its own creature made from a game creature (such as Malistaire the Undying 1336035), with a number (1, 2, 3...), a name shown in battle, and its own stats, school grid, deck, cheats (the Bosses tab's editors), drops and forms (the custom a polymorph form such as Haunted Malistaire becomes). Saved as `spiraldb-extra/Customs/<number>.json`; it counts only for that custom, never for the game's creature or other customs. Bring one in with `.joinme <creature name or ID> <number>`, such as `.joinme 1336035 1`; New custom, Edit, Duplicate, Delete and Copy .joinme are in the list. Custom areas lists the `spiraldb-extra/ZoneObjects` files (zones, sigils with position, facing and destination, exits) and lets you pick each room's boss: the game's creature or a custom of it (`spiraldb-extra/Customs/Rooms.json`), from the next time a team goes in.
- **Server**: start, stop and restart the server, switch randomizer mode, Die = Delete mode, "Reduce mana even if fizzle", school pips and "Open every door" on or off (with Die = Delete on, a wizard that is still dead when its battle ends is deleted for good; fleeing and the tutorial battle don't count), change what spell trainers ask for (random or no spell requirements, no level requirements, free training, training points for every level gained and the players' `.gaintp` command), and read the log with repeated lines folded into one (`×30`) and debug lines hidden.
- **Settings**: change any setting in `Config\Imlight.ini`, with each setting's help text.
- **Accounts**: create accounts, change their level or password, and lock or unlock them (the server must be running).
- **Game Files**: copies the game's data files (every `.wad` in `Data\GameData`) into `Game files\Copy of GameData` in the install folder, then reads only that copy into `Game files\Readable`: every BiND data file decoded to JSON, plain text files, the English game text and the message lists, plus a `Key details` folder of tables for quests, badges, triggers, event areas (with positions), spawns, zones, NPCs and objects placed in each zone (with positions), named zone places, houses, pets, pet talents, creatures, items and spells, and every network message with its fields. The Wizard101 folder is only read, and the game program is never opened.
- **Tools**: update or uninstall Imlight, and open the log, settings and install folders. Each time the app opens it checks for a newer Imlight and updates by itself (it asks first if the server is running); a checkbox here turns that off.

## Update and uninstall

**Update Imlight** on the Tools tab gets the newest setup if there is one, downloads the latest Imlight and rebuilds. `Config\Imlight.ini`, the logs and the player database are kept. **Uninstall Imlight** removes Imlight, its shortcut and its Apps entry; it asks before deleting accounts and characters, and Wizard101 is not touched. Both stop the server and close the app first.

Everything it does is written to `setup-log.txt` in the install folder. If setup fails, that file says why. Share it when asking for help.

## Options

```
ImlightSetup --dir <folder>          Install somewhere else.
             --game <folder>         Your Wizard101 folder, if setup can't find it.
             --type-dump <file>      Your own wiztype type dump, if the bundled one doesn't match your client.
             --repo <owner/repo>     Build a different GitHub repository.
             --branch <name>         Build a different branch.
             --source-folder <folder>  Build an Imlight source folder on this PC (copied, never changed),
                                     or run a server already built there, where it is.
             --yes --admin-password <text>   Install without asking anything.
             --panel                 Open the app (the installed Imlight.exe does this with no option).
             --update                Update Imlight (what Update Imlight on the Tools tab runs).
             --start                 Run the server in a plain window, without the app.
             --uninstall             Uninstall Imlight.
```

Without `--repo` or `--source-folder`, setup uses the Server source chosen on the app's Tools tab, kept in `server-source.txt` in the install folder (no file means the default repository).

## Building it

`dotnet publish src/Imlight.Setup -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`. Add `-p:ImlightEdition=Manager` for ImlightManager.exe. It uses Imcodec's archive reader and data file decoder; the decoder is generated from a type dump, so first copy `submodule\Imcodec\test\CodeGen\Inputs\*.json` into `submodule\Imcodec\src\Imcodec.ObjectProperty\GeneratorInput` (the Release workflow does this). No game files are needed to build. The Release workflow attaches ImlightManager.exe to every release; ImlightSetup.exe is only built by hand.
