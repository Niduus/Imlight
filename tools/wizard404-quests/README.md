# Quests from Wizard404

Rebuilds the quests the server has no files for (Darkmoor, Lemuria, Novus, Wallaru, Ahmarra, Selenopolis and gaps
elsewhere) from Wizard404's public quest API.

1. `python download.py` saves the API answers to `raw\` (about one request a second, so several hundred quests take
   10 to 20 minutes). It can be stopped and restarted. If you have a Wizard404 developer key, set it first with
   `$env:WIZARD404_API_KEY = "..."`. If the site refuses access, the script stops instead of trying another way.
2. `python convert.py` writes quest files and reward tables to `out\` and lists everything it couldn't fill in in
   `out\report.txt`. It never writes into the server. Quests the server already has are skipped.
3. Copy the new files from `out\QuestTemplates` and `out\DropTables` into `Server\spiraldb-extra` (never over a file
   that is already there) and restart the server.

What the converter fills in by itself:
* NPCs that newer records name only by display name: the NPC object with that name in `Server\id-lookup\ids.csv`, the
  one in the quest's own area first. Each pick from several NPCs is listed in the report.
* Community records (no quest code): `codes.json` maps their template ID to the code whose hash is their quest ID. The
  codes were found in the game's own data files and from the code patterns of known quests (2026-11-28: 204 of 208).
* Quests no NPC offers: tagged `ImlightAutoStart`, so they start once the quest before them is done.
* Dungeon go-to steps with no zone: the zone of their location key's zone ID.

What the server does for these quests: a defeat step with no creature adjectives counts creatures by the step's tally
name or name key (BountyStandIns.CountsByTally); a use step with no object tag works when the game's object names the
quest step itself (the usual case).

`installed-2026-11-28.txt` lists the files the first install copied into the server, for removing them again.
