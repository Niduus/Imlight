# Quest line knows Triton Avenue's teleporters

Fixed: The quest line now uses the teleporter pads inside an area, such as Triton Avenue's "To Lower Triton" and "To Upper Triton" pads and the Kraken Isle pads. They were never found before, because the game data gives their landing spot as map numbers instead of a place name. Doors that lead to another spot in the same area, like the "To Triton Avenue" pads, now count as teleporters too.

Fixed: When there is no legal way to the step, the line no longer cuts straight across black areas or walls. It stops where the legal way stops, and the ring still shows the step.

Changed: Ultimus and the Quest Team know the same pads now, so they take them too.

Where: Play > Overlay, the Quest Tracker and Minimap.

Test:
1. Stand on the upper lawn in Triton Avenue and pick a quest whose way goes to Olde Town. The line should lead onto the "To Lower Triton" pad, then carry on from where you land.
2. Stand by "To Upper Triton" with a step up top. The line should lead onto that pad.
3. Pick a step you can't reach on foot. The line should not cross black areas.
