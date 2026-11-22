New: The bots only ever play on your own Imlight server

Every bot (Ultimus, the TCO7, the Kitharas Family, True Form, Zoom, the Quest Team and the Bot Players) now checks where it was started before anything of it runs. If it is not inside your Imlight server, or the server's addresses point at the official game, no bot starts, no bot mini server runs, and the log says "BOTS LOCKED" with the reason (also in logs/bots/home-guard.txt). A new check also stops any bot change that would give the bots a network line off this PC, the official game's addresses or the game's own login from being merged or published.

Where: every bot, at server start and after every bot update.
Test: After this bot update, all bots log back in as before, and logs/bots has no home-guard.txt.
