# Minimap and overlay buttons: the game on the server's own PC is found again

Fixed: the minimap stayed empty (a dark circle with a blank bar) when the game and the server run on the same PC but the game reaches the server by another address of that PC, such as a Tailscale or home network address. The server now knows every address of its own PC, so the minimap (and the overlay's command and cheat buttons) find your wizard.
Fixed: the minimap's words (the place name, quest lines, the N and any note like "Log in to the game") were invisible over the game. They are now drawn so they always show.
Changed: the server's log now says what the minimap shows each time it changes (for example "the map is sent." or why not), so a blank map can be explained.

Where: Play > Overlay (the Minimap piece) and the game window.

Test:
1. Update Ahziael and let it restart the server once (or close and start Ahziael).
2. Start the game with Play in Ahziael and log in.
3. The minimap in the corner should show the streets, your arrow, icons and the place name at the bottom.
4. If it shows a note instead, the words should now be readable: send a screenshot.
