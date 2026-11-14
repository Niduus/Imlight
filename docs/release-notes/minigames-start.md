Fixed: Mini games start again
Opening a locked chest's mini game (Shock-a-Lock, Catch-a-Key) sent you into the mini game's empty room, frozen, with no game window. The game was told to start the mini game while it was still on its loading screen, so it never did. It is now told once the room has loaded. Arcade kiosk games start the same way, so they get the fix too.
Test: Open a locked wooden chest that offers Shock-a-Lock or Catch-a-Key, or pick Potion Motion at a kiosk in the Commons, and play a round.

Changed: A mini game that still won't start sends you back
If the game window still has not come up after about half a minute, you are sent back where you came from with a message, instead of being stuck in the empty room. A mini game left untouched for 10 minutes also sends you back now.
Test: Nothing to do; if a mini game ever fails to open, wait about 30 seconds.
