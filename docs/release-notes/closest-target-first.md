# Closest target first, faded trails to the others

New: When a quest step has several targets (like "Gather the Troops" with two soldiers to talk to), the Minimap's quest line goes to the closest one by the real walk, not as the crow flies. That line stays full colour. The other targets get faded trails and faded rings, so you can see where they are without mixing them up.

Changed: Ultimus now does the closest step in the area first, measured by the same real walk, and finishes steps where he is before leaving for another area.

Where: Minimap (Quest Tracker line, and Ultimus's own map), Ultimus.

Test: Pick "Gather the Troops" (or any step with two people to talk to). One solid line should go to the nearer soldier and a faded trail to the other. Walk toward the far one: when it becomes the nearer, the lines swap.
