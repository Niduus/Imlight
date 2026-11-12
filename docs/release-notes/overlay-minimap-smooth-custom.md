Fixed: Minimap edges are smooth now
The streets, grass, water and walls on the overlay minimap no longer have jagged corners. Their outlines are smoothed into curves on the server. The new MinimapSmoothing setting in Imlight.ini (Overlay part) picks how smooth: 0 is the exact shape, 45 is the normal, up to 400. It applies live.
Where: Play > Overlay
Test: Open the minimap in the Commons. The path edges and the pond should be round curves, not little steps.

Fixed: Mobs and wizards glide on the minimap
Dots on the minimap now slide smoothly between updates instead of jumping, even while you stand still. The map also updates more often.
Where: Play > Overlay
Test: Stand near walking mobs and watch them on the minimap. They should move smoothly.

Fixed: Water is blue on the minimap
Ponds and lakes (like the Commons pond) are drawn as water now, and bridges and paths above water are drawn on top of it.
Where: Play > Overlay
Test: Open the full screen map in the Commons. The pond should be blue.

Fixed: Hidden NPCs no longer show on the minimap
NPCs and objects the game hides from you (not unlocked yet, waiting on a quest) are left off your minimap.
Where: Play > Overlay
Test: Go where an NPC only shows up during a quest. The minimap should not show it before you get there in the quest.

New: Change every part of the minimap
The overlay designer has new "Minimap look" and "Full screen map" groups: gliding on or off, the north mark and its colour, the edge ring, your arrow size, outline colour, names for mobs and NPCs, levels, name backgrounds, the size of each kind of mark, walls and trees on or off with their colours, the full screen button, its margin, background and zoom.
Where: Play > Overlay
Test: Open the designer, pick the minimap, change a few of these and see the map change right away.

Changed: Overlay changes are always saved
Everything you change in the overlay, including zooming the full screen map in and out, is saved on your PC right away and comes back next time.
Where: Play > Overlay
Test: Zoom the full screen map, close the game and open it again. The zoom should stay.
