# Ultimus shows up again and frees himself

Fixed: Ultimus could be online but invisible, and stand still for hours. After a server restart he sometimes joined a different realm (Phoenix) than the players (Centaur). Each realm is its own world, so nobody could see him or his battles. The Quest Team and the Bot Players could land there too. Now all server-run wizards always play on the first realm in [Game Server] RealmNames, the one players are on.

Fixed: Ultimus stood on the Kraken isle for hours. The isle can only be left by its teleporter, and he only uses street teleporter stones, so he could not find a way off. New self-check: when stepping aside twice has not freed him, the server sends him back to where the area starts, and he finds his way from there.

Fixed: A teleporter could still send you back after the cool down while you were still standing on the pad you landed on. Now a teleporter only works again once you have walked off the spot the last one put you on.

Where: no new settings. The server log says "Bot recall" when Ultimus is sent back, and the Ultimus log notes it in pink.

Test:
1. Restart the server, turn Ultimus on, and use Ultimus tp: you should see him.
2. Take the teleporter to the Kraken and stand still on the pad for 20 seconds: you should not be sent back.
3. Walk off the pad and back on: now it takes you back.
