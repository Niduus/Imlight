# Ultimus corrects himself faster and never repeats a fix that failed

Changed: Ultimus now counts as stuck after 30 seconds of standing still, down from 56.

New: Ultimus writes down every time he is flagged ABNORMAL for being stuck: the street, the spot, the quest step and what he did about it. The same or a nearby flag for the same step never gets the same fix twice. He first tries whatever worked at that spot before. Otherwise he picks a fix he hasn't tried there yet, in this order:

1. step aside
2. another way (a teleporter or a new route around the stuck spot)
3. walk well away and come back another way
4. go back to where the area starts
5. last, Ahziael finishes the step

When he gets free, he remembers which fix worked there. All of this is kept in logs/ultimus/abnormal-fixes.json, so it survives restarts and updates. He never goes back to the start of an area while he is stuck at the start itself, and he skips stepping aside when he is boxed in.

Fixed: at a door that offers nothing to click (like the Pyramid door in The Oasis), Ultimus now walks on through the doorway. If that does nothing, he presses the door directly. If nothing works after 45 seconds, it is written up.

Fixed: the "go back to the start" loop guard from the last patch never switched on. It does now.

Where: Ultimus.

Test: Watch Ultimus in Krokotopia. In the log, a stuck line should say what he tries, for example "...: another way (a teleporter or a new route)". A second flag at the same spot should name a different fix and list the ones tried before. When he gets free, the log says "got free with ... and remembers it for that spot".
