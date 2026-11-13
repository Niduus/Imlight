# Routes never go through gauntlets, events or dungeons

Fixed: From the Commons, the Quest Tracker's way to Grizzleheim said "Sinbad & the Iron Sultan Gauntlet > Spiral Door to Grizzleheim". It sent you through the Portal of Peril's gauntlet to use its Spiral Door. Now the way never passes through an event (Portal of Peril gauntlets, holidays), a gauntlet, the Pet Derby, heroic dungeons or the Deckathalon. It also avoids every dungeon or tower instance on the way, unless the quest step is deeper inside that same dungeon. Gauntlets and events no longer count as Spiral Door spots either.

Where: Quest Tracker "Way:" text and Minimap line, Ultimus, Quest Team and Bot Players (they share one route finder).

Test: In the Commons, pick "Face Your Fate" (or any Grizzleheim quest). The way should read "Spiral Chamber > Spiral Door to Grizzleheim (Northguard)" or similar, with no gauntlet in it.
