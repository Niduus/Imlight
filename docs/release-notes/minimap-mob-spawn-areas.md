New: Mob areas on the minimap
Each street's mobs now show as one outlined area, like a red line drawn round them. It comes from the game's own spawn data: where the mobs spawn, the path they walk, and how close you can get before they pull you into a battle (the edge of the area). The area stays on the map while its mobs are dead and waiting to respawn. Over each area it says who spawns there, their level and how many at once. A boss's area has its own colour. You can also turn on the walk each mob takes, as a dotted line.
Where: Play > Overlay (Mob spawn areas, Spawn area edge, Mob walks); Imlight.ini [Overlay] MinimapSpawns
Test: Walk on Unicorn Way or Triton Avenue with the minimap on. Each group of mobs should have a soft red area with a line round it, and the name and level over it. Stand just outside the line: no battle should start until a mob walks close.

Fixed: Small holes in the ground no longer block the way
A small gap between two pieces of ground, ringed by floor at the same height, now counts as floor for the quest line, the Quest Team, Ultimus and the bots. Before, it was a wall to them.
Where: The quest line on the minimap, and every server-played wizard
Test: Follow a quest line across a plaza. It should go straight over flat ground, not around small black spots.
