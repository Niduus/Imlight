# Minimap: exact streets and walls, full screen button, no quests

Changed: the minimap draws each area from the game's own ground and walls, shape by shape, instead of a blocky grid. Streets, grass, water, walls, buildings, trees and posts have clean edges and sit exactly where they are in the game.
New: a small full screen button on the minimap's top right corner. It opens a big map over the game window, north up, with + to see closer, - to see farther, and X to go back to the corner minimap. The big map shows creatures, bosses, sigils, doors and NPC names farther out than the corner minimap.
Changed: the minimap no longer tracks quests. Quest marks, quest lines under the map, quest givers and the rings on quest mobs are gone, along with their settings. Quest tracking will be its own overlay add-on.

Where: the Minimap piece of the game overlay (Play > Overlay). The full screen button shows only over the game, not in the designer.

Test:
1. Update Ahziael and let it restart the server once.
2. Click Play, log in, and walk around Unicorn Way.
3. The streets, walls and buildings on the minimap should match what's around you, with smooth edges.
4. There should be no quest lines under the minimap and no quest stars or diamonds on it.
5. Click the small square button on the minimap's top right corner. A big map should fill the game window.
6. Click + and - to zoom in and out, then click X to go back to the small minimap.
