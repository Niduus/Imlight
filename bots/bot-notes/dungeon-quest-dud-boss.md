# Dungeon quest boss that starts no battle

Fixed: a bot no longer goes back to a dungeon quest's boss that it stood on with no battle starting, and it starts the dungeon over instead.

Idunn stood on Shoji in Shirataki Temple, no battle started, and she set Shoji aside for 10 minutes. The "dungeon's own quest first" and "another quest right here" choices still picked Shoji every few seconds, so she went back and forth with the way out about 700 times in ten minutes. Both choices now skip a creature that was set aside. When that creature belongs to the dungeon's own quest, she walks out and back in for a fresh copy, as she already did for her main step's creature.

Where: bots/Ultimus/UltimusDirector.Smart.cs (DungeonQuestFirst, WhileHere), bots/Ultimus/UltimusDirector.Bounds.cs (ResetCopy, QuickReset, FirstQuestDud).

Test: watch a TCO7, Kitharas or Ultimus bot in a dungeon whose boss starts no battle. It should say "stood on this ... no battle started" once, then "walks out ... for a fresh copy", with no flip-flop between the boss and the exit.
