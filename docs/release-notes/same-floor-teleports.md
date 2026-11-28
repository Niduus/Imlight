Fixed: Teleports in a building take you to your quest's floor
Some teleports inside a building had no destination, so taking one put you back outside. When your quest step is on another floor of the same place, the teleport now takes you to that floor. Every time this happens it is written to the server log so it can be checked.
Where: buildings and dungeons with several floors, starting with Mirage.
Test: in the Mirage quest building with floors, take the teleport onward and check that you land on the floor your quest arrow points to.
