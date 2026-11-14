Changed: Ultimus never tries the same failed fix twice
When Ultimus is stuck, he now remembers what he tried at that spot. He first tries whatever worked there before, otherwise a fix he hasn't tried there yet: step aside, another way, walk away and come back, go back to the area start. When he gets free, he remembers what worked. He also counts as stuck sooner, after 30 seconds instead of 56. It is saved in logs\ultimus\abnormal-fixes.json.
Where: Ahziael > Ultimus
Test: Watch Ultimus in Krokotopia. Each stuck line in his log should name a different fix, and when he gets free it says "got free with ... and remembers it for that spot".

Fixed: Ultimus walks through doors that have nothing to click
At a door like the Pyramid door in The Oasis, he now walks on through the doorway, and presses the door if that does nothing.
Where: Ahziael > Ultimus
Test: Send Ultimus to The Oasis. He should get into the Pyramid.
