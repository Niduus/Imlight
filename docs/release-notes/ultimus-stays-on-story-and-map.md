# Ultimus stays on the main story and on the map

Fixed: Ultimus wandered into the Sinbad & the Iron Sultan Gauntlet. The game's own world list names the Monthly Gauntlets, raids, heroic dungeons, battlegrounds and housing gauntlets as "arrival areas", so the route finder took the Sinbad gauntlet for a place with a Spiral Door. It planned The Commons, then the Sinbad portal, then the Spiral Door to Krokotopia. These areas are never on the way now, for Ultimus, the Quest Team and the Quest Tracker line.
Fixed: Ultimus and the Quest Team only travel through main-story worlds now. His Bazaar trip had gone through Aquila's Garden of Hesperides.
New: Leaving comes first. If Ultimus ever ends up off the story, in an area his quest does not name, he walks back out before anything else. After 2 minutes, or with no way out on foot, Ahziael takes him back to the last story area he was in (an unstuck move). It is written in the mistakes book.
Fixed: The recall after a shop or Bazaar trip waits until the buy or sale is done. It was sent 1 second before the Bazaar buy and was lost. If it fails, he tries once more, then walks back.
Fixed: Ultimus keeps inside the world's bounds.
- Every walk is checked against the area's floor and stops where the floor ends. No walking through walls or over the Minimap's black.
- A hop never lands off the floor.
- At the Bazaar he walks up to the counter, not behind it.
New: A bounds watchdog. If he is ever off the walkable ground for 3 seconds, he snaps back to the last spot on the floor he stood on, and it is written in the mistakes book.
New: The zone transfer book, logs\ultimus\zone-transfers.json. It holds every exit of the story's areas, read from the game's zone files: its exact trigger spot (x, y, z), where it leads, and the spot (x, y, z) it lands on. After every zone change Ultimus checks that he landed where the book says. If the way took him to an area he did not mean, he backs out the same way (once for each way, so it can't loop).
New: The NPC location book, logs\ultimus\npc-book.json. It holds the exact spot of every NPC the main story names. Ultimus looks people up there first and walks straight to them.
New: The time-waste map, logs\ultimus\time-waste.txt. It shows where he loses the most time (standing still, getting unstuck, a step he can't work out), by area and by spot.
New: The nightly auto-coach. When it is on, each night Ultimus puts all his logs in a dated folder (logs\ultimus\coach), each file on its own plus one joint file. The last 14 nights are kept. It is off by default.
New: The closest target first. On a step with several targets (for example "Gather the Troops": talk to Private Farnsworth and Private Primwell), Ultimus goes to the nearest by the real walk, then the next. For creatures he picks the nearest by the real walk too, not one behind a wall.
New: Faded trails on the Minimap. The Quest Tracker and Ultimus Spectator lines draw the closest target solid and the other targets as faded trails.

Where: Ahziael > Ultimus > "When to tell you" > Nightly auto-coach (off by default). The new files are in logs\ultimus.

Test:
1. Let Ultimus quest in Krokotopia and send him on a Bazaar trip. He should recall back after the buy, never walk through The Commons into the Sinbad portal, and never go through Aquila.
2. Watch him in the Bazaar. He should stay on the floor in front of the counter.
3. Pick "Gather the Troops" in the Quest Tracker. The line to the nearest Private is solid, and the line to the other is faded.
