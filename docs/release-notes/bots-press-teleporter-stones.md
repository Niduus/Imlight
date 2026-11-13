# Bots press street teleporters and doors

Fixed: Ultimus and the Quest Team now press a street teleporter stone (like "To Teleporter Hub") or a same-street door when their way goes through it, the way a player presses X. Before, they walked up to it and stood there, because standing next to it does nothing.

Fixed: Ultimus now knows about the street teleporter stones when it checks if it is boxed in. Before, it could say "no teleporter on it" while standing next to one, and then go back to the start of the street over and over.

Where: Ultimus and the Quest Team, on any street with teleporter stones or doors that lead to another spot of the same street (for example Triton Avenue in Wizard City).

Test: Let Ultimus play "Clear as Crystal" in Triton Avenue. When it walks to a "To Teleporter Hub" stone, it should press it and come out at the other stone within a few seconds. The Ultimus log says "presses To Teleporter Hub, the teleporter toward ...".
