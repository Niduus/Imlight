# Data

Everything Imlight adds on top of [SpiralDB](https://github.com/Revive101/spiraldb) lives here, in two folders:

| Folder | What it is | Who can use it |
|---|---|---|
| [`spiraldb/`](spiraldb) | World records in **SpiralDB's own folders and format**: quests, drop tables, creature decks, NPC shop lists, zone exits. | Any server that reads SpiralDB. Nothing in it is specific to Imlight. |
| [`imlight/`](imlight) | This server's own files: boss cheats, Crown Shop list, trainers, shops, quest repairs, zone placements and so on. | Imlight only (the server code reads each one). |

Looking for a quest? Open **[`quest-index.csv`](quest-index.csv)**. GitHub shows it as a searchable table with
every quest's world, code, title and file, and whether it is new or replaces SpiralDB's quest of the same code.

## `spiraldb/`: server-agnostic records

Same folder names, file names and JSON as SpiralDB, so the folder can be laid over any copy of SpiralDB as it is.

| Folder | Files | What |
|---|---:|---|
| `QuestTemplates` | 2,419 | Quests SpiralDB does not have (Celestia onward, and gaps in the first worlds), plus 10 repaired SpiralDB quests. One file per quest, named `questtemplates_<quest code>.json`. |
| `DropTables` | 11,199 | Quest rewards (`droptables_<quest code>.json`), creature drops (`w404-*`), pack and pet-level tables. |
| `NpcDropTable` | 4,755 | Which drop tables each creature uses, by creature template id. |
| `CreatureSpellbook` | 516 | Creature decks. |
| `NpcInventory` | 10 | NPC shop lists (the garden vendors). |
| `ZoneTransfer` | 6 | Zone exits SpiralDB lacks. |

Quests by world:

| World | New quests | Replace SpiralDB's |
|---|---:|---:|
| Wizard City | 269 | 4 |
| Krokotopia | 4 | 5 |
| Marleybone | 16 | 1 |
| Mooshu | 5 | 0 |
| Dragonspyre | 14 | 0 |
| Grizzleheim | 3 | 0 |
| Celestia | 153 | 0 |
| Wysteria | 63 | 0 |
| Zafaria | 188 | 0 |
| Avalon | 192 | 0 |
| Azteca | 228 | 0 |
| Aquila | 10 | 0 |
| Khrysalis | 321 | 0 |
| Polaris | 83 | 0 |
| Arcanum | 83 | 0 |
| Mirage | 126 | 0 |
| Empyrea | 172 | 0 |
| Karamelle | 104 | 0 |
| Lemuria | 95 | 0 |
| Novus | 86 | 0 |
| Wallaru | 82 | 0 |
| Selenopolis | 32 | 0 |
| Darkmoor | 80 | 0 |
| **All** | **2,409** | **10** |

Rules for these files:

- A file replaces SpiralDB's file of the same record (a quest of the same `m_questName`, a drop table of the same name).
- `"$type"` must be the **first** key of every object that has one, or the file is skipped.
- Quest codes start with the world: `WC` Wizard City, `KT` Krokotopia, `MB` Marleybone, `MS` Mooshu, `DS` Dragonspyre,
  `GH` Grizzleheim, `CL` Celestia, `PA` Wysteria, `ZF` Zafaria, `AV` Avalon, `AZ` Azteca, `AQ` Aquila, `KR` Khrysalis,
  `PL` Polaris, `AR` Arcanum, `MR` Mirage, `EM` Empyrea, `KM` Karamelle, `LM` Lemuria, `NV` Novus, `WL` Wallaru,
  `SL` Selenopolis, `DM` Darkmoor. Codes with `W404` were made from the live game's quest list (Wizard404).

## `imlight/`: this server's own files

| Path | What |
|---|---|
| `QuestFixes.json` | Repairs laid over captured quests (missing dialogue, steps). |
| `QuestCodeAliases.json` | Other codes the game uses for the same quest. |
| `QuestGoalEvents.json` | Zone events sent when a quest step starts or ends (spawn a boss, open a door). |
| `QuestSpellRewards.json` | Spells quests teach. |
| `DropOdds.json` | How often each rank of creature drops. |
| `BossCheats/` | Boss cheats, one file per creature template id. `CheatExamples/` has examples the server never reads. |
| `CrownShop/`, `LoyaltyStore/`, `Shops/`, `Packs/` | Crown Shop items, Loyalty Store rotations, live shop lists and NPC zones, Saga Spellements. |
| `TradeTrainers/`, `VanillaTrainers/`, `Presets/`, `Gardening/` | Trainers' lists and the garden trainers' spells. |
| `WoodenChests/`, `GuildMuseum/` | Wooden chest loot, Guild Museum galleries. |
| `Zones/`, `ZoneObjects/`, `DungeonTeleports/` | Zone names and kinds, objects the server adds to zones, dungeon teleports. |

## How the server gets these files

The server project ([`Imlight.Director.csproj`](../src/Imlight.Director/Imlight.Director.csproj)) copies both
folders into one folder next to the server, `spiraldb-extra/`, as it always has. The server reads SpiralDB first
and then that folder (`[Database] SpiralDBExtraPath` in `Imlight.ini`). The installed folder keeps its name so
servers that are already set up, and the files players save there (customs, boss changes, quest options), keep
working as they are.

The scripts that make these files are in [`tools/`](../tools). `tools/data-index/make_quest_index.py` remakes
`quest-index.csv` after quests are added (give it a SpiralDB checkout to mark the ones that replace SpiralDB's).
