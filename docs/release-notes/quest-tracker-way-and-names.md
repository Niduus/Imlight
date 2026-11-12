# Quest Tracker: the way to other areas, real step names

Changed: the way to a quest in another area now shows in the Quest Tracker itself, not on the Minimap. Under the picked quest it lists the areas to go through, door by door, including the Spiral Door when the quest is in another world ("Way: Ravenwood > Spiral Door to Grizzleheim > Savarstaad Pass"). The Minimap still draws the line to the next door, or to the Spiral Door, from where you stand.
Fixed: the Quest Tracker reads steps the way the game's quest book does: "Stop Morganthe" instead of "Defeat 1 creatures", and the place as "Azteca / Xibalba".
Fixed: the Minimap no longer shows "Go to Savarstaad Pass, then Savarstaad Pass".
Where: Play > Overlay
Test: Pick a quest whose step is in another world. The tracker should list the way under it, and the Minimap should show no text banner. Check that "Tall Enough to Meet the Sun" reads "Stop Morganthe" and "Azteca / Xibalba".
