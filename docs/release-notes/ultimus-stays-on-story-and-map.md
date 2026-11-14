Fixed: Ultimus no longer wanders into gauntlets or other worlds
Ultimus walked into the Sinbad gauntlet, and his Bazaar trip went through Aquila. Now he only travels through main-story worlds. If he ends up somewhere his quest doesn't name, he walks back out first.
Where: Ahziael > Ultimus
Test: Send him on a Bazaar trip from Krokotopia. He should never go into the Sinbad portal or through Aquila.

Fixed: Ultimus stays on the floor
His walks now stop where the floor ends, so no walking through walls or over black parts of the Minimap. At the Bazaar he stands in front of the counter, not behind it. If he is ever off the floor for 3 seconds, he snaps back.
Where: Ahziael > Ultimus
Test: Watch him at the Bazaar. He should stay in front of the counter.

Fixed: Recall after a shop trip waits for the buy to finish
The recall back after a shop or Bazaar trip was sent before the buy was done, so it got lost. Now it waits.
Where: Ahziael > Ultimus

New: Nearest target first
On a step with several targets, like talking to two Privates in "Gather the Troops", Ultimus goes to the nearest one first by the real walk. The Quest Tracker line draws the nearest target solid and the others faded.
Where: Play > Overlay
Test: Pick "Gather the Troops". The line to the nearest Private should be solid, and the other one faded.
