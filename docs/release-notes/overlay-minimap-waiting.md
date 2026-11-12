# Minimap: stuck on "Waiting for the map..." fixed

Fixed: the minimap only said "Waiting for the map..." because the server tripped over its own map while adding other wizards to it, and hung up on the overlay every time. The map now arrives.
Changed: if the server ever can't make a map, the minimap now says why instead of waiting forever, and the overlay stays connected.

Where: the Minimap piece of the game overlay (Play > Overlay).

Test:
1. Update Ahziael and let it restart the server once (or close and open Ahziael).
2. Click Play and log in.
3. The minimap should show the streets, your arrow, icons and the place name.
