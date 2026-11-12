# Quest Tracker: the minimap line goes around pools, pits and ledges

Fixed: The quest line on the minimap no longer goes straight across places you can't walk over, like the sunken pool in the Vault of Ice Entrance Hall. It now goes around drops, ledges and pits, and around thin walls and fences that it used to miss.
Changed: Places with one floor over another, like a hall under a balcony or a stream under a bridge, can still be walked through.

Where: In the game overlay, use the Quest Tracker together with the Minimap.

Test:
1. Go to The Vault of Ice Entrance Hall in Krokotopia and pick a quest whose way goes past the pool.
2. Check that the minimap line goes around the pool, not across it.
3. In a town area, check that the line still follows the streets and doesn't jump over fences.
