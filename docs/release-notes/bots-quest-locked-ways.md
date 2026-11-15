New: Bots do the quest that opens a locked way first, anywhere
The Temple of Storms' teleporter stayed shut until its own tests were done. That lesson now works everywhere: when a door or teleporter won't take Ultimus or the Quest Team, and the game ties it to a quest step they hold, they do that quest first and then go on. Before they get there, the game's own zone files are read for every story world, so they already know which ways open only after a quest step.
Where: Ultimus and the Quest Team, in every world. The list is written to logs/bots/gates-ahead.txt.
Test: Start the server with Ultimus on and open logs/bots/gates-ahead.txt after a minute; it should list the ways that open with a quest. In a place like the Temple of Storms he should do the place's own quest before the boss.
