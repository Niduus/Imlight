Fixed: NPCs stand where your quest step says to find them
Some NPCs move to a new place partway through a quest in the real game, but the server always showed them at their first spot, far from where the quest arrow pointed. Now an NPC you are on a talk step for stands where the game's own quest arrow puts it, for you only. Other players keep seeing it where their own quest has it.
Where: every world, for NPCs that move during a quest.
Test: on a talk step whose arrow points away from where the NPC first stood, follow the arrow and check that the NPC is there.
