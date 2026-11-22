New: The bots know which areas each creature appears in, and their books cover the side worlds

The bots already knew where creatures walk in the area they stand in. Now they also read every area's own spawn files ahead of time, so a "defeat" step that names no area and has no quest arrow is played in the nearest area where those creatures appear (streets before dungeons). Their NPC book, exit book and Quest Atlas now also cover the side worlds (Grizzleheim, Wintertusk, Wysteria, Aquila, Selenopolis), not only the main worlds, and they are built again once at the first start after this update.

Where: Ultimus, the TCO7, the Kitharas Family and the Quest Team. New files: logs\ultimus\creature-areas.json and creature-areas.txt.
Test: A few minutes after the server starts, open logs\ultimus\creature-areas.txt and check that it lists areas by world with the creatures in each. The server log says "the creature-area book has ... areas".
