# Tools

Scripts and small programs that build records in `data/`, data files Ahziael ships in `src/Imlight.Setup/Assets/`,
and the checks the build runs. None of them run inside the server or the app. Most read a copy of the game files or
the Wizard404 export, and never change the game folder itself.

Python scripts run with `python3 <script> --help` (or read the note at the top of the script). C# tools run with
`dotnet run --project tools/<folder>`.

## Builds `data/`

| Tool | What it makes |
|---|---|
| [`data-index`](data-index) | `data/quest-index.csv`, the searchable list of every quest file. Run it after adding or changing quests. |
| [`game-quests`](game-quests) | Server quest files from the game's own quest files ([guide](game-quests/README.md)). |
| [`wizard404-quests`](wizard404-quests) | Quest files from the Wizard404 export ([guide](wizard404-quests/README.md)). |
| [`mob-drops`](mob-drops) | Creature drop tables (`data/spiraldb/NpcDropTable` and `DropTables/w404-*`). |
| [`pack-tables`](pack-tables) | Booster pack loot tables and the Saga Spellement packs. |
| [`crown-shop`](crown-shop) | `data/imlight/CrownShop/GameFileItems.json`, the Crown Shop items SpiralDB's list leaves out. |
| [`wooden-chests`](wooden-chests) | `data/imlight/WoodenChests/WoodenChests.json`, what the world's wooden chests give. |
| [`guild-museum`](guild-museum) | `data/imlight/GuildMuseum/Galleries.json`, the Guild Museum's galleries. |
| [`vanilla-trainers`](vanilla-trainers) | The spell trainers' real requirements. |
| [`dungeon-teleports`](dungeon-teleports) | Where dungeon sigils and doors go when SpiralDB has no record of them. |
| [`selenopolis-teleports`](selenopolis-teleports) | Where the Selenopolis boat, doors and sigils go. |
| [`boss-twins`](boss-twins) | Boss cheats for creatures that share a fight file with a boss. |

## Builds files Ahziael ships

| Tool | What it makes |
|---|---|
| [`quest-tree`](quest-tree) | `quest-tree.json.gz`, the Quest Tree page. |
| [`quest-testing`](quest-testing) | `quest-testing.json.gz`, the Quest Testing page. |
| [`boss-test`](boss-test) | `boss-places.json.gz`, where each boss is fought (Boss Test page). |
| [`wiki`](wiki) | `creature-places.json.gz`, where every creature is fought (Wiki page). |
| [`server-messages`](server-messages) | `ServerMessageCatalog.Generated.cs`, the list of every server message for the Messages page. Run it after adding a new server message. |
| [`typedump`](typedump) | A new type dump after a game update ([guide](typedump/README.md)). |

## Used by the build and releases

| Tool | What it does |
|---|---|
| [`release-notes.ps1`](release-notes.ps1) | Collects the notes in `docs/release-notes/` into a release's "What's new" list. |
| [`release-notes-skip.txt`](release-notes-skip.txt) | Pull request numbers left out of the patch notes. |
| [`live-patch-check`](live-patch-check) | Checks whether a server code change can go into a running server without a restart. Runs on every pull request that changes `src/Imlight.CoreLib`. |
| [`UltimusChecks`](UltimusChecks), [`bots-home-check`](bots-home-check), [`hive-sign`](hive-sign) | Checks and files for the computer-controlled players. Ask Niduus before changing them. |
| [`imlight-mcp`](imlight-mcp) | Lets an AI coding agent build, run and test the server ([guide](imlight-mcp/README.md)). |
