# Quest line follows legal routes (teleporters, no strangers' houses)

Fixed: The quest line on the Minimap no longer goes the long way round through the black (no ground) areas when the quest step is a short walk across the street. When the step stands in a wall (an NPC in an alcove, a door), the line now ends on the floor on your side of it, not on the floor behind it.

New: When the step is on ground you can't walk to (Triton Avenue's Kraken isle, a raised lawn), the line now goes to the teleporter pad that gets you there. It stops on the pad, a small dashed ring marks the pad and where you land, and it goes on from the landing spot. Up to four teleporters in a row.

Fixed: The way to another world no longer sends you through a house you don't own (the Castle Tours Apartment door in the Commons, the apartment door in Olde Town). A house's world gate only works for its owner, so a house door is only used when you own that house. Otherwise the way goes to the public Spiral Door in Ravenwood.

Changed: The Quest Team uses the same rules: it takes the teleporter when its step is cut off, and never walks into a house. Ultimus already used the same walking map and teleporters, and it never uses house doors either.

Where: Play > Overlay, the Quest Tracker and Minimap. Pick a quest in the tracker.

Test:
1. In Triton Avenue, pick a quest whose step is on the other side of a "To Triton Avenue" pad. The line should go to the pad, then carry on from where you land.
2. Pick a quest step close by across open ground. The line should go straight there and not loop out through black areas.
3. In the Commons or Olde Town, pick a quest in another world (for example Azteca). The way should say Ravenwood and the Spiral Door, not "Castle Tours Apartment".
