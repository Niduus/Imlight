Fixed: Teleports inside dungeons take you on to your quest's room
Some teleports inside a dungeon had no destination in the server's data, so taking one threw you out of the dungeon. Teleports that lead deeper into a dungeon, such as to the next room, floor or level, now take you to the room of the dungeon your quest step is in. Every time this happens it is written to the server log so it can be checked.
Where: dungeons and towers with several rooms in every world.
Test: in a dungeon with several rooms, take the teleport to the next room and check that you arrive in the room your quest arrow points to and are not sent out of the dungeon.
