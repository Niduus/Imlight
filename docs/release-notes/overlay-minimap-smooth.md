# Minimap: streets in the right place, smooth movement, clearer quest lines

Fixed: the streets on the minimap were drawn about 12,000 steps to one side of where they really are, so they never matched the area (Unicorn Way showed a big blank wedge). They now sit exactly where they are in the game, and the game's own map page lines up much better too.
Changed: the minimap moves smoothly. It asks the server for your spot four times a second and glides between updates, and only the minimap's corner of the overlay is drawn again, so it doesn't slow the game down.
Changed: quest lines under the minimap now say who to talk to or what to defeat ("Talk to Headmaster Ambrose", "Defeat 3 Rattlebones"), and show only the main story and this area's quests, not side quests from other worlds.

Where: the Minimap piece of the game overlay (Play > Overlay).

Test:
1. Update Ahziael and let it restart the server once.
2. Click Play, log in and walk around Unicorn Way.
3. The streets, grass and walls on the minimap should match what's around you, and the map should glide as you walk and turn.
4. The quest lines should name who or what, and show no side quests from other worlds.
