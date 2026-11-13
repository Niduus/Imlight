# Teleporters wait a moment

Fixed: Teleporters inside an area, such as Triton Avenue's "To Lower Triton" and the way over to the Kraken, sent you away the instant you touched them. Now they play their sparkle and wait a moment first, as in the game. Touching another teleporter while one is already sending you does nothing. This is the same for players and bots.

Where: Imlight.ini [World] TeleporterDelaySeconds (default 1.5 seconds, 0 to 10; 0 is the old instant way). Applies live.

Test:
1. Go to Triton Avenue and walk into "To Lower Triton".
2. You should sparkle for about a second and a half before you are sent.
3. Walk to the Kraken and back: no more instant back-and-forth.
