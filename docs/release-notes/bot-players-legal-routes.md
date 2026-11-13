# Bot Players: street bots walk the same legal routes as the Quest Tracker

Changed: Street bots now find their way with the same route finder as the Minimap's quest line, Ultimus and the Quest Team. They walk only on floor that can be walked: never over the Minimap's black, never through walls, and never across water. Their feet follow the ground on slopes and stairs.

Changed: Street bots only appear and wander on ground that connects to where you are. They never appear on a cut-off island or a small boxed-in patch behind a fence.

Where: Any street with bots in it.

Test:
1. Stand in Triton Avenue or the Commons and watch a few bots walk around. They should follow the paths around buildings and fences, the way the Quest Tracker line goes, never walking through a wall or over black.
2. No bot should appear behind a fence or on a patch you can't walk to.
