# Missing encounter quests (set aside 2026-11-28)

53 encounter quests of the live game are not on the server and are in no file on this PC (not in CSR.wad, not on
Wizard404). The game's arrow file (Root.wad poi.dat) still describes every step of each: its zone, its spot and its
target (311 steps: 133 places, 128 creatures, 24 objects, 6 NPCs). Codes: the 53 lines of `missing ENC` in the analysis
below, among them AH-DARK-ADV-ENC-004/005/009, DM-MORT-ENC-004_Hard, LM-NIGHT-ENC-002..004, MS-CAT-ENC-001..003,
WL-HOPE-ENC-003/004, the AZ/AV/KT/ZF WEAVE encounters and ME-SINBAD-ENC-001.

Plan, not started:
1. A generator that builds each one from poi.dat: steps named "Goal", "Goal 1", ... (the game's names, so arrows work),
   in goal order; a place is a go-to step at its spot (zone entry + spot, completed by QuestSpotGoals within 450), a
   creature a defeat step with that creature's own adjective, an object a use step with its tag, an NPC a talk step.
   Rewards: the median gold and experience of the same area's encounters in CSR.wad. No dialog (none exists).
2. A server rule for how they start: the game starts encounters from zone triggers (ResAddEncounter, whose name the
   server's types cannot read), and only 9 of 299 known encounters name their start volume "<code>_Goal00", so the
   volume cannot be guessed. Instead: a generated encounter (marked by a client tag) starts when the wizard reaches its
   first step's spot, or enters its dungeon zone when the first step is a fight; never again once done.

Data used: poi.dat records (quest hash, goal hash, zone, target template, spot); codes from the game files and the
per-world code search (tools\game-quests\found-codes-2026-11-28.txt for the 68 found that way).
