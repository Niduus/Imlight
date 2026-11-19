Added: The bots share a watchdog book of the spots where they lost hours
Every time a bot's watchdog goes off, the spot, the quest and the step are written down in one book that Ultimus, the TCO7 and the Quest Team all read, together with what got the bot going again. A bot stuck by a spot in the book tries the move that worked there first, and the Quest Team now gets early watchdog alarms too, not only the two hour stop.
Where: logs/ultimus/watchdog-book.txt, and .ultimus watchdog in the game.
Test: Let the bots play for a few hours, then open logs/ultimus/watchdog-book.txt and check that it lists the spots with the bot's name and what freed it.
