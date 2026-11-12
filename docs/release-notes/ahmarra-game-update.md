New: Ahziael rebuilds the server by itself after a Wizard101 update
When Wizard101 patches the way the game talks to the server (the Ahmarra update adds 13 new game messages and changes 6), a server built before the patch can no longer understand the game. Ahziael now notices this within a few minutes of the patch, builds the server again in the background while the old one keeps running, then restarts into it like an update. If automatic updates are off, it asks first.
Where: Server > Ahziael
Test: After Wizard101 updates, open Ahziael. Within a few minutes the bar at the top says the server is being built again for the updated game. Wait until Ahziael restarts, then press Play and log in.

Changed: The lookup lists refresh after a Wizard101 update
The item, spell, creature, quest and zone lists used to stay as they were after the game updated. Now the server builds them again once after a patch, so Ahmarra's new gear, spells, creatures and areas show up in the searches.
Where: Database > Items
Test: After the update and the rebuild, start the server, wait a few minutes, then search for "Ahmarra" in the item search. The new Ahmarra jewels show up.

New: Ahmarra on the Spiral Door, Team Up, .tp and the Quests tab
Ahmarra is listed on the Spiral Door once your quests open it (or with Open All Doors on), has its own Team Up tab, `.tp Ahmarra` takes you to the Bale Flotilla, and the Quests and Boss Test tabs sort Ahmarra quests and bosses under Ahmarra. Before the game updates, none of this shows, so the current game keeps working as it is.
Where: Quests > My Quests
Test: After the update, turn on Open All Doors, use the Spiral Door in the Commons and pick Ahmarra. Or type `.tp Ahmarra` in chat.
