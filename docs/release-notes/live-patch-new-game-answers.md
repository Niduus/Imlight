Fixed: Updates that add a new answer to the game wait for a restart
When Ahziael put an update into the running server without a restart, a brand new answer to something the game asks for never switched on, because the server only reads that list when it starts. That is why Friendly Players still said "No Quest" after the update that added the bots' quests. Now such an update waits for the next restart, like other updates that cannot go into a running server, and Ahziael says so.
Where: Ahziael, Updates, while the server is running. No setting.
Test: Restart the server once, then open Friendly Players with bots on: each bot shows its quest. After the next update that adds a new answer, the Updates log says it waits for the next restart.
