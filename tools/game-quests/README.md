# Quests from the game's own files

The client ships `Data\GameData\CSR.wad`, which holds `QuestData/<area>/<QUEST>.xml`: the full template of 3,354 quests
(dialog, step volumes, step results, rewards). This tool reads them into the server's quest types.

    dotnet run -c Release -- repair
    dotnet run -c Release -- missing --only "<regex on the quest code>"

* `repair` fills in what the quests rebuilt from Wizard404 (`Server\spiraldb-extra\QuestTemplates`) lack: dialog
  events and actions (matched by the line's sound file), the volume a go-to step is reached by, a step's end results
  (gates, bridges, walls, step-end dialogs not already shown), start/end results, missing object tags and creature
  adjectives. Step names stay as they are, so progress players already have still matches. A step whose kind or title
  differs from the game's step at that place is left alone. Writes to `out\repair`.
* `missing` writes the quests the server lacks to `out\missing\QuestTemplates`, with their gold and experience as a
  reward table in `out\missing\DropTables`.

The server's types have no fields for `ResAddEncounter` and `ResAddEncounterXP` (the game program's own type list knows
them only by name), so the tool writes them as `ResPostEvent "ImlightStartEncounter:<quest>"` (ResPostEventHandler starts
it) and as experience. Encounters started by a zone volume start when a player walks into the volume of the encounter's
first step (VolumeComponent).

2026-11-28: repair filled in 1,384 quests (originals in `backup-2026-11-28\QuestTemplates`); `missing --only "-ENC-|-Enc-"`
added 75 encounters (`installed-encounters-2026-11-28.txt`). Still not on the server and in CSR.wad: about 700 more
(`missing --only .` lists them; some are retired or test quests).
