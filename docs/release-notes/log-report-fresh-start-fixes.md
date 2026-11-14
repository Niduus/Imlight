Fixed: A brand new Ultimus or Quest Team gets its first quest
On a new server (or after pressing Restart for Ultimus), a level 1 bot skipped the tutorial, landed in the Headmaster's house and then stood still saying "no World or Story quest is left to get". Wizard City's story was worked out wrong: its "last quest" was a later quest that needs Dragonspyre, and the school choice after Unicorn Way cut the start of the world off the story. The story of every world is now worked out from the world's own longest quest chain (school choices included), so Unicorn Way and the rest of Wizard City count again. The Quest Tracker's Story list shows them too.
Where: Ahziael > Ultimus
Test: On the Ultimus tab press Restart. The new level 1 Ultimus should get "Unicorn Way" from Merle Ambrose within a minute.

Fixed: Ultimus no longer breaks off getting unstuck
When Ultimus flagged a stuck spot for you, an error while writing the report could stop his unstuck move, and his next try started with an empty list ("I tried  and none got me free"). The report is now written safely and he carries on.
Where: Ahziael > Ultimus

Fixed: No more "An error in the background" lines when the server starts or stops
Ahziael checks whether the server is up by knocking on its port. A knock that took too long was written in Ahziael's error log as a background error, several times an evening. It was harmless and is now ignored quietly.
Where: Server > Logs
