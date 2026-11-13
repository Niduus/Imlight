# Routes cross the gaps the game walks over

Fixed: In a few places the ground had a gap (a seam between floor pieces, a strip beside a wall) that the game lets you walk over, but the quest line, Ultimus, the Quest Team and Bot Players treated it as a wall. Now the game's own walking map (zone.nav in each area's file) closes those gaps when there is floor all round them at the same height. Gaps inside buildings or walled-off corners stay shut.

Checked against every area of the game (2,917 places): 16 places change, mostly in Dragonspyre (Wizard Tower, Conquest Plaza, Amphitheatre, Arena, Crystal Grove, Hatchery), plus Avalon's High Road, Krokosphinx Retreat, Grizzleheim Red Claw, Celestia Kingdom of the Crabs and two house insides (Sun Palace, Pyramid of the Lost).

Where: Quest Tracker line on the Minimap, Ultimus, Quest Team, Bot Players (they share one route finder).

Test: Pick a quest whose way goes through Dragonspyre's Wizard Tower or Conquest Plaza. The line on the Minimap should go straight across the floor instead of bending round small black gaps.
