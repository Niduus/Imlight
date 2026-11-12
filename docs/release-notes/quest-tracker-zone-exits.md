# Quest Tracker: the minimap leads you through each zone exit

Changed: When the picked quest is in another area, the minimap line now goes to the exit in your area that starts the way there. That can be a door, a walk-through exit at the edge of the area, a dungeon sigil, or the Spiral Door.
Changed: When the quest is in another world, the line leads you to your world's Spiral Door first, area by area. Once you are in the quest's world, it leads you area by area to the quest's area, and then to the person, creatures or dungeon you need.
Changed: The "Way:" line in the tracker follows the same route as the line on the minimap.
Fixed: Places no longer show doubled names like "Grizzleheim / Grizzleheim|Savarstaad Pass". They now read "Grizzleheim / Savarstaad Pass".

Where: In the game overlay, use the Quest Tracker together with the Minimap.

Test:
1. Pick a quest whose next step is in another world, for example "Tall Enough to Meet the Sun" while you are in Grizzleheim.
2. Check that the minimap draws a line to an exit of your area. Follow it zone by zone until you reach the Spiral Door.
3. Go through the Spiral Door to that world. Check that the line keeps leading you area by area to the quest's area, and then to the target.
4. Check that the place lines in the tracker read "World / Place" with no "|".
