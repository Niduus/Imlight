<p align="center">
  <img src=".github/assets/ahziael-logo.svg" alt="Ahziael" width="560" />
</p>
<h6 align="center">A feature-rich fork of Imlight, the independent private server for a wizard adventure, with a one-click Windows app to install, run and customize it. Written entirely in C#.</h6>
<p align="center">
    <a href="https://github.com/Niduus/Imlight/releases/latest">
        <img src="https://img.shields.io/github/v/release/Niduus/Imlight?label=latest%20release"
            alt="latest release"></a>
    <a href="LICENSE">
        <img src="https://img.shields.io/github/license/Niduus/Imlight"
            alt="license"></a>
</p>

---

## About
**Ahziael** is a fork of [Imlight](https://github.com/Revive101/Imlight) by Revive101. Imlight is a framework for building a private server; Ahziael builds on it with a large set of additional game systems, thousands of recreated quests and world records, game modes such as a randomizer, and a desktop app that installs and manages everything for people with no development tools.

Like Imlight, Ahziael began as a learning exercise in MMORPG server design. We have the utmost respect for the original developers and have taken steps to ensure the project does not infringe on their intellectual property.

Ahziael keeps Imlight's **BYOD (bring-your-own-data)** philosophy and does not distribute any copyrighted game files. You must obtain the original game client independently. The app reads what it needs from your own installed copy, on your own computer.

## Disclaimers
Ahziael is a hobby project and is not affiliated with or endorsed by the original game's developer or publisher.

The game client does not ship with all of the data a server needs (quests, drop tables, shop lists, creature decks and zone exits are server-side). Ahziael uses [SpiralDB](https://github.com/Revive101/spiraldb) plus the records in this repository's [`data/`](data) folder to fill those gaps. Most systems are fully implemented, but how much content you see in a given area depends on that data, and some of it is necessarily a best guess at how the original servers behaved.

Ahziael refuses to connect to the official live servers. It is for private servers only.

## Quick Start (Windows)
1. Download **`ImlightManager.exe`** from the [latest release](https://github.com/Niduus/Imlight/releases/latest).
2. Run it. It will:
   - find your game install (or ask for the folder),
   - install the .NET 10 SDK into its own folder if your PC doesn't have it,
   - download the server source and the matching Imcodec version from GitHub (Git is not needed),
   - read the definition files Imcodec needs straight out of your own game's `Root.wad`,
   - build the server into `%LOCALAPPDATA%\ImlightManager`,
   - ask for an admin account, and
   - add one **Ahziael** shortcut to your desktop.
3. Open Ahziael and press **Play**. It starts the server and launches the game pointed at it.

No separate patch server is needed on a home PC: Ahziael serves game files to the server from your own game folder. Running the installer again (or **Update** inside the app) updates the server; your settings, logs and player database are kept. Ahziael checks for a newer version each time it opens.

Ahziael is also listed under **Settings > Apps** and can be uninstalled from there. It asks before deleting accounts and characters, and never touches your game install.

## The Ahziael App
Ahziael is a native dark-themed desktop app (Avalonia, no browser or web view). Its Home page starts and stops the server and searches everything at once: pages, settings, commands, items, spells, creatures, zones and quests. Everything else is grouped into pages:

| Page | Tabs | What it's for |
|---|---|---|
| **Play** | Play, Commands | Play on your server or on other private servers you save by address. Browse every chat command with what to type and who may use it. |
| **Quests** | My Quests, Quests, Spell Quests, Quest Testing, Quest Tree, Quest Editor | Follow a wizard's quests (Done, In progress, Next up, Later), see every world's main story in order, start, teleport to, complete or finish any quest, and edit quests or write your own. |
| **Progress** | Fast Forward, Best Gear, Collect Spells, Unlearn Spells | Bring a wizard up to a level or world with the quests' real rewards, find the best gear for your level and where it drops, collect the spells you should have by now, and unlearn the ones you don't want. |
| **Database** | Wiki, Lookup, Locations, Spells, Gear, Items, Furniture, Badges, Recipes, Vendors, Pet Talents, Pet Derby, Bosses, Phrases | A searchable wiki of the whole game: IDs for everything, spell cards drawn exactly as the game draws them, every place by its common name and code name with a one-click teleport, and every line of game text. |
| **Create** | Customs, Share, Player Cheats, Boss Test, Announcements | Build custom creatures and custom areas, export and import them to share, make player cheats, test boss cheats, and word what the server announces. |
| **Game Rules** | Game Modes, Battles and World, On and Off, What NPCs Give, Teleport Rooms, Animations, Events, Shops and Codes | Change how the game plays: modes, spell trainer rules, battle and world switches, turning any NPC, creature, zone or world off, what each NPC hands out, which dungeon rooms can be teleported into, spell animation timing, events, shops and promo codes. |
| **Server** | Server, Settings, Accounts, Logs, Game Files, Ahziael, Remote, Account Transfer | Start, stop and restart; edit every setting with its help text; manage accounts, ranks and chat levels; read tidy logs; decode the game's data files to readable JSON; manage a server on another PC; move accounts between servers. |
| **Help** | Guides, What's New | Step-by-step guides and patch notes for every version. |

An optional **AI Guide** page answers questions about Ahziael using your own API key.

See [src/Imlight.Setup/README.md](src/Imlight.Setup/README.md) for the full details of each tab and the installer's command-line options.

## Server Features
On top of Imlight's core (accounts, characters, movement, zones, combat, inventory, chat, friends and realms), Ahziael implements or extends the systems below. Nearly all of them can be tuned or switched off in `Imlight.ini` (or in the app), and many settings apply live without a restart.

### Questing and progression
- **2,410 added quests** across every world, plus repairs to SpiralDB's quests, quest goal events (spawning bosses, opening doors) and quest spell rewards. See [`data/quest-index.csv`](data/quest-index.csv).
- **Quest safety guards** so nothing a quest needs can leave a wizard stuck, and a quest taken again never pays twice. Players can type `.stuck` to run them at once.
- **Quest book** in chat, **Fast Forward** to a level or world, **Collect Spells**, **unlearn** and **respec** (Mr. Lincoln in Ravenwood, or `.respec`).
- **Spell trainer rules**: random or no spell prerequisites, no level requirements, free training, training points per level, `.gaintp`.
- **Daily Assignments**, **Monstrology** (Extract spells and Animus), **badges**, and level scaling.

### Combat
- Most of combat, including **critical and block**, **Archmastery** and Power Orbs, **Shadow magic** and shadow pips, **school pips**, **minions**, **Spell Fusion**, **spell upgrades** with Spellements and Saga Spellement Packs, **enchantments**, Daybreaker and Nightbringer, and mana.
- **Boss cheats** sent as true interrupts, editable per boss, with a **Second Chance Chest** after cheating bosses.
- **Tamed drops**: boss loot tiers depend on how fast you win.
- **PvP** with practice and ranked queues, arenas and ranking.
- **Pet May Casts**, battle wisps and victory dances.

### Pets
- Pet care, feeding, snacks and elixirs, pet XP and levels, talents and talent tokens, jewels and sockets.
- **Hatching** (`.hatch`, the hatchmaking kiosk, or right-click a player), pet minigames and the **Pet Derby**.
- Pet Pavilion vendors, morphing zones and the pet tome.

### Housing, crafting and life skills
- **Housing**: house deeds and templates, placing items, attics, vaults, teleporters, house music, gauntlets and **Castle Magic**, visiting other players' houses, and a personal **trainer hall**.
- **Crafting** with recipe shops, crafting stations and ingredients.
- **Gardening** with real-time growth (speed configurable), pests, harvests and ranks.
- **Fishing** with ponds, fishing spells, the fish basket, journal and levels.
- **Seamstress** stitching, **Photomancy** (`.photo`), **Cantrips** and mounts, including **multi-person mounts**.

### Social and economy
- **Groups** and **Team Up** at dungeon sigils, **trading** (including treasure cards), **guilds** (`.guild`) with guild houses, **Pixie Post** mail, the **Recent** friends tab, and a configurable chat filter with per-account chat levels.
- **Bazaar**, run by a separate Bazaar bot process so it never slows the server.
- **Crown Shop** for crowns or gold, the **Loyalty Store**, NPC shops, the bank, the **Recycling Bin**, rentals, wooden chests and promo codes.

### Events and endgame
- **Dungeons** that reset when empty, with sigils, room bosses and dungeon teleports.
- **Deckathalon** towers (30 floors per school), **guild raids**, the **Guild Museum**, class projects and an event button.

### Game modes
- **Randomizer**: shuffles schools, spells, drops, creatures, creature decks, item cards and pips, with seeds, a same-for-everyone race mode, and a **Tame** mode that keeps every quest finishable. See [docs/docs/modules/imlight/randomizer.md](docs/docs/modules/imlight/randomizer.md).
- **Die = Delete**: a wizard still dead when the battle ends is deleted for good.
- **Roshambo decks**, **Astral schools** (`.astral`), open-every-door and access-all-worlds switches, unlimited mana, and more.

### Customs
- **Custom mobs**: your own creatures built from a game creature, with their own name, stats, school grid, deck, cheats, drops and polymorph forms. Bring one in with `.joinme <creature> <number>`.
- **Custom areas**: zones, sigils, exits, portals, teleporters, NPCs and battles, with each room's boss picked per area.
- Everything custom can be **exported and imported** to share with other servers.

### Commands and lookup
- Dozens of in-game chat commands, listed with `.help` and on the app's Commands tab: `.tp` (accepts common names, partial names and small typos), `.lookup`, `.mod additem`, `.sb learn`, `.quest`, `.ver` and many more.
- An **ID lookup** of every item, spell, creature, NPC, zone and quest, written on boot and searchable in game, in the app, or as a standalone `index.html`.

## Hosting for Others
- **Remote Admin**: manage a server on another PC from Ahziael (settings, customs, bosses, cheats, accounts, logs and commands). Only Administrator accounts can connect, and the connection is encrypted with a fingerprint check.
- **Player downloads and Account Transfer**: players can download a copy of their own account and move it between servers.
- **Launcher installer**: [`installer/`](installer) builds `ImlightLauncherSetup.exe`, a lighter Windows installer for dedicated hosts. Its launcher updates itself and the server from this repository's GitHub releases. See [installer/README.md](installer/README.md).
- Each player is sent the address they can reach, so the same server works for players on the same PC, your home network and the internet.

## Data
Everything Ahziael adds on top of SpiralDB lives in [`data/`](data):

- [`data/spiraldb`](data/spiraldb): server-agnostic records in SpiralDB's own folders and format (quests, drop tables, creature decks, NPC shop lists, zone exits). Any server that reads SpiralDB can use them.
- [`data/imlight`](data/imlight): this server's own files (boss cheats, Crown Shop, Loyalty Store, trainers, shops, quest fixes, zone objects, dungeon teleports and more).

At build time both are copied into `spiraldb-extra/` next to the server, which is read after SpiralDB. [`data/README.md`](data/README.md) explains where everything lives and how to add to it.

## Manual Setup
If you'd rather run the server yourself instead of through the app (on Linux, for example), you'll need:

- The [.NET 10 SDK](https://dotnet.microsoft.com/download).
- A copy of the original game client.
- A patch server (required).
- A player database (optional).
- SpiralDB (optional, recommended).

### Patch Server
Ahziael does not ship game files. It downloads them from a patch server, keeps them in a local cache, and tells game clients where to find the same files. [Aurorium](https://github.com/Revive101/Aurorium) is the open source option made for Imlight. Its first boot takes a while.

Point `PatchServerInternalUrl` in the `[Patch Server]` section at your host (`http://127.0.0.1:12369` by default). Downloaded files are stored in `LocalWadCachePath`.

### Player Database
Ahziael uses [RavenDB](https://ravendb.net/) for player-generated data such as accounts, characters, chat logs and friendships. If `PlayerDatabaseUrl` is empty, an embedded instance is started automatically. If you provide one, set `PlayerDatabaseCertificatePath` to its client certificate (`.pfx`).

### SpiralDB
World data the client does not ship with is served by SpiralDB. On boot, the server fetches the latest data from the [spiraldb](https://github.com/Revive101/spiraldb) repository (or uses a local cache), loads it into memory, then lays `spiraldb-extra/` over it. Control this with `SpiralDBRemoteUrl`, `SpiralDBBranch`, `SpiralDBLocalPath`, `SpiralDBAutoFetch`, `SpiralDBDisableRemote` and `SpiralDBExtraPath` in `[Database]`.

### Configuration
The configuration is `Imlight.ini`, found in `src/Imlight.Director/Config/` and copied to `Config/` in the build output (the copy is what the server reads). Every setting is documented inline. The ones you will most likely touch first:

- `[Login Server]`: `LoginServerPort` (12000 by default).
- `[Game Server]`: `GameServerIP`, `GameServerPort`, `GameServerCount` and `RealmNames`.
- `[Patch Server]`: `PatchServerInternalUrl` and `LocalWadCachePath`.
- `[Database]`: the player database and SpiralDB settings, plus `AdminAccountUsername` and `AdminAccountPassword`.

An admin account is always created from `AdminAccountUsername` and `AdminAccountPassword`. **Change them before booting the server.**

### Client
Start the game client with a launch argument pointing at your server, for example `-L 127.0.0.1 12000`.

## Building
Clone with submodules. [Imcodec](https://github.com/Jooty/Imcodec) is included as a submodule and provides the tooling for the game's data formats:

```bash
git clone --recurse-submodules https://github.com/Niduus/Imlight.git
# or, if you already cloned without them:
git submodule update --init --recursive
```

Imcodec generates C# classes from the client's data definitions at compile time, and that data is not distributed. Before building:

1. Obtain a type dump JSON with [wiztype](https://github.com/wizspoil/wiztype) and save it in `submodule/Imcodec/src/Imcodec.ObjectProperty/GeneratorInput/`.
2. Unpack your client's `Root.wad` with Imcodec (`imcodec wad unpack`), find the files matching `*Messages*.xml`, and place them in `submodule/Imcodec/src/Imcodec.MessageLayer/GeneratorInput/`.

Then build the server:

```bash
dotnet build src/Imlight.sln
```

To build the Ahziael app itself:

```bash
dotnet publish src/Imlight.Setup -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:ImlightEdition=Manager
```

The app needs no game files to build; see [src/Imlight.Setup/README.md](src/Imlight.Setup/README.md#building-it) for the generator inputs it uses.

### Releasing
Pushing a `v*` tag runs the Release workflow, which builds `ImlightManager.exe` (and the launcher installer, when the `IMCODEC_GENERATOR_INPUTS` secret is set) and publishes a GitHub release with patch notes. Installed copies of Ahziael pick it up on their next start.

## Running
With your patch server up and `Imlight.ini` configured, start the Director:

```bash
dotnet run --project src/Imlight.Director
```

The Director starts the login, game and patch servers. The first boot takes a while as game files are downloaded and the ID lookup is built. The server is ready when it logs `Imlight may now be connected to.`

## Developer Tools
- [`tools/imlight-mcp`](tools/imlight-mcp): an optional local MCP server for inspecting and operating a development server (status, logs, builds, tests, game commands, combat, database queries). Off by default and loopback-only; read its [security notes](tools/imlight-mcp/README.md#security) before enabling the `[Agent Bridge]`.
- [`tools/`](tools) also holds the scripts that generate the records in `data/`: quest testing and quest tree data, mob drops, pack tables, vanilla trainers, the Crown Shop, the Guild Museum, wooden chests, dungeon teleports, the wiki and the quest index.
- [`docs/`](docs): the documentation book, covering server architecture, the network protocol, combat, zones and more.

## Contributing
Contributions are welcome from anyone. See [CONTRIBUTING.md](CONTRIBUTING.md).

For [Semver](https://semver.org/) purposes, commits should follow [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/#summary).

## Credits
- [Revive101](https://github.com/Revive101) for [Imlight](https://github.com/Revive101/Imlight), [SpiralDB](https://github.com/Revive101/spiraldb) and [Aurorium](https://github.com/Revive101/Aurorium). Their [documentation book](https://revive101.github.io/Imlight-docs/) and [Discord](https://discord.gg/HjJCwm5) cover upstream Imlight.
- [Jooty](https://github.com/Jooty) for [Imcodec](https://github.com/Jooty/Imcodec).
- The Kronos team's [Grimoire](https://kronos-project.github.io/grimoire/foreword.html).

## License
Ahziael, like Imlight, is licensed under the [GNU Affero General Public License v3.0](LICENSE).
