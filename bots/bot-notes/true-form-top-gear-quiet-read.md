Fixed: First start after an update no longer drags

True Form reads every item in the game's files once to find the level 190 gear, and that made the first server start after v0.10.926 slower than usual. The read now runs quietly at low priority so the server comes up at its normal speed, and True Form simply finishes dressing a little later. Later starts use the kept list and are quick as before.

Where: server start, True Form.
Test: Restart the server and see that it starts at its usual speed, then check that True Form still wears level 190 gear a few minutes later.
