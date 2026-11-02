<p align="center">
  <img src="https://i.ibb.co/kQT3jSJ/logo-full.png" />
  <h6 align="center">An independent private server project aimed to reimagine a wizard adventure, written entirely in C#.</h6>
</p>
<p align="center">
    <a href="https://discord.gg/HjJCwm5">
        <img src="https://img.shields.io/discord/940647911182729257?logo=discord"
            alt="chat on Discord"></a>
</p>

---

## About
The "<B>Imlight</B>" project began as a learning exercise to explore the mechanics and architecture of MMORPG server design. We have the utmost respect for the original developers and have taken steps to ensure our project does not infringe on their intellectual property. 

That being said, Imlight has a BYOD (bring-your-own-data) philosophy and does not distribute any copyrighted game files. Users must obtain the original client and any necessary assets independently.

## Disclaimers
Imlight is a framework for building a private server. Do not expect a full game experience OOTB.

The game client does not ship with all of the data required to run a true server, and thus some of it must be recollected manually. You can find the data we've managed to collect in the [spiraldb](https://github.com/Revive101/spiraldb) repository.

If you care to contribute to the data recollection effort, you may use [Imview](#imview). Imview is Imlight's one-stop tool for managing Imlight, as well as data recollection.

### What you can expect
- [x] Character management
- [x] Moving through the world *
- [x] Zone objects
- [x] Mobs
- [x] Inventory and equipment
- [x] Multiplayer
- [x] Chat
- [x] Friends
- [x] Shops *
- [x] Dye Shop
- [x] Mounts
- [ ] Multi-person Mounts
- [x] Spell Training *
- [x] Most parts of combat
- [x] Reagents 
- [x] Minigames
- [x] Creating spell decks
- [x] Dungeons
- [x] Cantrips
- [x] In-game commands
- [x] Potions
- [x] NPC interactions
- [x] Bazaar
- [x] Realms
- [x] Pets (partial)
- [ ] Pet minigames
- [ ] Crafting
- [ ] Housing
- [x] Quests *
- [x] Drop tables *

* Engine is fully implemented. However, actual content depends on [SpiralDB](https://github.com/Revive101/spiraldb) data, which may not be available in all places.

## Getting Started
The only true requirement is the patch server. The rest of the components are optional, but are recommended.

#### Patch Server
You may use our open source option, [Aurorium](https://github.com/Revive101/Aurorium), to host one yourself. Both Imlight and your game client may be pointed to an Aurorium instance to serve game patches and data.

Aurorium's first boot will take time.

#### Player Database
Imlight uses [RavenDB](https://ravendb.net/) for player-generated data such as accounts, characters, chat logs, and friendships. If you do not provide a remote URL in the configuration, Imlight will spool up an embedded instance. If you <i>do</i> provide one, a certificate path must be provided as well.

#### Spiral Database 
World data — NPC inventories, zone transfers, quest templates, drop tables, and other static content the game client does not ship with — is served by **SpiralDB**. On boot, Imlight fetches the latest data from the [spiraldb](https://github.com/Revive101/spiraldb) GitHub repository (or uses a local cache) and loads it directly into memory. No RavenDB instance is needed for world data.

You may control the SpiralDB behaviors in the configuration file, such as changing the remote and cache paths, and whether to fetch the latest data on boot.

The quests and other records this fork adds on top of SpiralDB are in [`data/`](data): `data/spiraldb` holds server-agnostic records in SpiralDB's own format, `data/imlight` holds this server's own files. [`data/README.md`](data/README.md) explains where everything lives, and [`data/quest-index.csv`](data/quest-index.csv) lists every added quest.

### Configuration
Imlight's configuration is stored in `Imlight.ini`. It is recommended to review the configuration file and adjust it to your needs before booting the server. Additionally, an admin account is always created and the details for this account can be found in the configuration file as well. You should change these details before booting the server.

### Client
You can start the game client with a launch argument to connect to your server. `-L 127.0.0.1 12000` will work fine.

## Building

#### Imcodec
[Imcodec](https://github.com/Jooty/Imcodec) is imported as a submodule and provides tooling required by Imlight for handling proprietary game data. 

##### Building Imcodec
Imcodec relies on compile-time source generation to produce C# classes from game data definitions.
1. Obtain a type dump JSON file using [wiztype](https://github.com/wizspoil/wiztype). This file should be saved in
    ```
    /submodule/Imcodec/src/Imcodec.ObjectProperty/GeneratorInput/
    ```
2. Unpack the root archive using Imcodec itself (via `imcodec wad unpack`). Inside, locate the message definition files which can be matched with a wildcard of `*Messages*.xml`. Place these files in:
   ```
   /submodule/Imcodec/src/Imcodec.MessageLayer/GeneratorInput/
   ```

## Setup for original Imlight
`ImlightSetup.exe` (`src/Imlight.Setup`) installs original Imlight on any Windows PC with no tools needed. It finds Wizard101, installs .NET if needed, downloads the latest source, reads the files Imcodec needs from the player's own game, builds the server and adds one desktop shortcut to the app. Starting the server also hands it the game files from the player's own Wizard101 folder, so no separate patch server is needed. Running it again updates the server. Once installed, `Imlight.exe` is one app that runs the server, plays Wizard101 on it or on other private servers (never the live game), lists every in-game command and every item, spell, creature, NPC, zone and quest ID with a search, shows a tidy log, updates and uninstalls, changes settings and manages accounts. `ImlightManager.exe` is the same installer set up for this fork, with a randomizer switch. See [src/Imlight.Setup/README.md](src/Imlight.Setup/README.md).

## Installer
`installer/` builds `ImlightLauncherSetup.exe`, a Windows installer for people who just want to run a server. It asks for an admin account, the patch server address and the server mode, and adds Start menu shortcuts for the server, its settings, its logs and the ID lookup. The Start shortcut runs the Imlight Launcher, which updates itself and the server from this repository's GitHub releases before starting it. Pushing a `v*` tag publishes a release. See [installer/README.md](installer/README.md).

## Information
* The Kronos teams' [Grimoire](https://kronos-project.github.io/grimoire/foreword.html).
* Our [Documentation](https://revive101.github.io/Imlight-docs/) book.

## Contributing
Contributions are welcome from anyone.

For [Semver](https://semver.org/) purposes, we ask that all commits abide by [conventions](https://www.conventionalcommits.org/en/v1.0.0/#summary).
