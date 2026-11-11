New: Set how smart, selfish and aggressive each creature is
Every creature and boss now has Intellect, Selfishness and Aggression boxes (0 to 100). Intellect is how often it picks its biggest attack or heal, Selfishness how often it buffs or heals itself instead of a teammate, and Aggression how often it attacks instead of preparing with blades, traps, shields or heals. Empty boxes keep the game's own numbers, shown in grey (they show up a few minutes after the server first starts on this version, while it remakes its creature list). Changes count from the creature's next round, even in a battle already going.
Where: Database > Bosses
Test: Untick Bosses only, find any creature, type 100 in Intellect and 100 in Aggression, press Save changes, then fight it and watch it cast its biggest attack every round.

New: Set Intellect, Selfishness and Aggression for every creature at once
Three new settings set them for every creature on the server: Creature Intellect, Creature Selfishness and Creature Aggression (0 to 100, or -1 to keep each creature's own). A creature changed in the Bosses tab keeps its own numbers. They apply without a restart.
Where: Server > Settings
Test: Search the settings for Creature Aggression, set it to 0, save, and fight a creature that has blades or shields in its deck: it should prepare much more often.

Fixed: Creatures always attacked and never used their game Aggression
The game keeps Aggression as a number out of 10, but the server read it as out of 1, so nearly every creature attacked every round it could and only bladed or shielded when it had no attack. Now a creature with 8 attacks about 80% of rounds and prepares the rest, as the game means. To get the old way back, set Creature Aggression to 100.
Where: Server > Settings
Test: Fight a creature that carries blades or traps and see it use them now and then.

Fixed: Smart creatures picked a random heal
Intellect did nothing for heals: a creature that meant to cast its biggest heal always cast a random one instead. Now it picks the biggest heal on its smart rounds.
