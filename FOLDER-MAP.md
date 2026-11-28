# Folder map

Where everything in this repository is, in one page. New here? Read this, then [CONTRIBUTING.md](CONTRIBUTING.md).

The folder names at the top level are fixed. Ahziael downloads this repository when it updates and looks for these
folders by name, and the release and live patch steps do too. Please don't rename or move a top-level folder (or the
projects inside `src/`). Moving files around inside a project is usually fine.

## Top level

| Folder or file | What it is | Change it? |
|---|---|---|
| [`src/`](src) | All the C# code: the server, the Ahziael app and the launcher. See [below](#src-the-code). | Yes, this is where most work happens |
| [`data/`](data) | Quests, drops, creature decks, shops, zone exits, boss cheats and the server's other records ([guide](data/README.md)). | Yes |
| [`tools/`](tools) | Scripts that build records in `data/` and files Ahziael ships, plus the checks the build runs ([guide](tools/README.md)). | Yes |
| [`docs/`](docs) | The release notes for each change, and the developer book about how the server works ([guide](docs/README.md)). | Yes, every change adds a release note |
| [`overlay-addons/`](overlay-addons) | Overlay add-ons players can download inside Ahziael ([guide](overlay-addons/README.md)). | Only Niduus adds these |
| [`installer/`](installer) | The lighter installer for people who only host a server ([guide](installer/README.md)). | Rarely |
| [`submodule/Imcodec`](submodule) | [Imcodec](https://github.com/Jooty/Imcodec), which reads the game's file and network formats. A git submodule, not our code. | No |
| [`bots/`](bots) | Code for the computer-controlled players. | Ask Niduus first |
| [`Hive mind/`](Hive%20mind) | What the computer-controlled players have learned. Ahziael shows these pages. | Ask Niduus first |
| [`.github/`](.github) | The build check and release steps (workflows), issue and pull request templates, the logo. | Ask Niduus first |
| [`.agents/AGENT.md`](.agents/AGENT.md) | Code style and rules for AI coding agents (and people) working here. | When a rule changes |
| `README.md` | The front page. | Ask Niduus first |
| `FOLDER-MAP.md`, `CONTRIBUTING.md` | This map, and how to send a change. | Yes, keep them up to date |
| `.editorconfig`, `omnisharp.json`, `global.json` | Formatting rules and the .NET version. | Rarely |

## `src/`: the code

| Project | What it is |
|---|---|
| [`Imlight.Director`](src/Imlight.Director) | The server program you start. It starts the login, game and patch parts. Its settings file is `Config/Imlight.ini`, and every setting is explained in it. |
| [`Imlight.CoreLib`](src/Imlight.CoreLib) | The server itself: everything the game does. See the next table. |
| [`Imlight.Common`](src/Imlight.Common) | Small pieces the others share: reading settings, live setting values, the log. |
| [`Imlight.Setup`](src/Imlight.Setup) | The **Ahziael** app (built as `ImlightManager.exe`): installs, builds and runs the server, and has every page of the app ([guide](src/Imlight.Setup/README.md)). |
| [`Imlight.Launcher`](src/Imlight.Launcher) | The small launcher that keeps a server-only install up to date. |
| `Imlight.sln` | Opens all the projects at once in an IDE. |

### Inside `Imlight.CoreLib`

| Folder | What is in it |
|---|---|
| `Auth/`, `Login/` | Logging in, picking a server, and the character list. |
| `Patch/` | Talking to the patch server: the game's file list and fetching the game files the server reads. |
| `Shared/` | Networking, packets, encryption, characters, items and other building blocks. |
| `WizardData/` | Loading world data from SpiralDB and `data/`, and saving players (RavenDB). Quest fixes and aliases are read here. |
| `Game/` | Everything in the game, one folder per system (below). |

The biggest folders in `Game/`: `Zone` (zones, NPCs, objects, doors and sigils), `Commands` (every chat command),
`Services` (answers to the game's network messages), `Combat` (battles, spells, cheats), `PvP`, `Results` (rewards
and drops), `Pet`, `World`, `Housing`, `Requirements`, `Quests`, `Spells`. Smaller systems have their own folder
with an easy name: `Bank`, `Bazaar`, `Badges`, `CrownShop`, `Fishing`, `Gardening`, `Groups`, `Minigames`,
`Seamstress`, `Trading` and so on. `LivePatches` puts new server code into a running server without a restart.

## `data/`: the records

| Folder | What is in it |
|---|---|
| [`data/spiraldb/`](data/spiraldb) | Records in SpiralDB's own folders and format (quests, drop tables, creature decks, NPC shops, zone exits). Any SpiralDB server can read them. |
| [`data/imlight/`](data/imlight) | This server's own files: boss cheats, the Crown Shop, trainers, shops, quest repairs, zone objects, routes, packs, the Guild Museum. |
| [`data/typedump/`](data/typedump) | The game program's type list that the build needs ([guide](data/typedump/README.md)). |
| [`data/quest-index.csv`](data/quest-index.csv) | Every quest file in one searchable table. Rebuild it with `tools/data-index` after adding quests. |

## Finding something fast

| Looking for | Look in |
|---|---|
| A chat command | `src/Imlight.CoreLib/Game/Commands/` |
| A page of the Ahziael app | `src/Imlight.Setup/ManagerWindow.<PageName>.cs` (for example `ManagerWindow.Quests.cs`) |
| A setting | `src/Imlight.Director/Config/Imlight.ini` |
| A quest's steps | `data/spiraldb/QuestTemplates/questtemplates_<quest code>.json` (search `data/quest-index.csv` for the code) |
| A boss's cheats | `data/imlight/BossCheats/` |
| Where a zone's doors and sigils go | `data/spiraldb/ZoneTransfer/` and `data/imlight/ZoneObjects/` |
| What changed in a release | `docs/release-notes/` and the [releases page](https://github.com/Niduus/Imlight/releases) |
| How a build or release runs | `.github/workflows/` |
