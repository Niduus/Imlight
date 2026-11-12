Changed: Phrase numbers stay the same through game updates
The Ahmarra update added new game lines, and that moved the numbers of the old ones. From now on every line keeps its number: the lines a game update adds get the next numbers at the end, so your boss cheats and favorites that use a number keep showing the same line. The new Ahmarra lines are already in the list.
Where: Database > Phrases
Test: Start the server once, then open Phrases and tick "New in the last game update" (it shows nothing until the next game update adds lines). Note the number of any line, restart the server, and check the line still has the same number.

Fixed: The Besieged Ship and Hungry Lizard exits take you out
These dungeon exits don't say where they go, so you were stuck inside. Walking into them now takes you back to where you were before the dungeon, like other dungeon exits.
Where: Ahmarra > Bale Flotilla > Besieged Ship, and Azteca > Hungry Lizard
Test: Go into the Besieged Ship in Bale Flotilla, beat the fight, and walk into the exit. You should come back out in Bale Flotilla.

Fixed: Ahmarric Relic spots give Eldritch Signs
The new reagent spots in Ahmarra gave nothing, because the server looked for a reagent called "Ahmarric Relic". It now reads which reagent the spot gives from its own game file.
Where: Ahmarra (reagent spots)
Test: Collect a glowing relic reagent spot in Ahmarra. You should get Eldritch Sign.
