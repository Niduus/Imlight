Fixed: Game data reader updated to the newest Imcodec

Imlight now uses the newest Imcodec, the library that reads the game's data and network messages. It is safer with broken or odd game data, so a bad message is turned away instead of tripping up the server, and it recovers when a message arrives garbled. Nothing you see in game should change.

Where: the server and Ahziael's Game Files tab.

Test: Start the server, log in and play for a few minutes, including a battle and a zone change. Open Ahziael's Game Files tab and open a few files. Everything should work as before.
