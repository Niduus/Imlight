Fixed: True Form missed the best gear and picked two-talent pet jewels that repeat a talent

The True Form bot only looked at the game's level 180 and up gear and never at crowns gear, so his full healing setup missed pieces like Sword of Kings. Now his full healing, tank, glass and universal setups look at every piece of gear in the game's files at any level, crowns pieces included, and count what a piece's sockets add once filled. A pet jewel that carries two talents is now skipped when either one repeats a talent his pet already has.

Where: True Form bot, full setups and pet jewel.
Test: Ask him for a life healer build and check his wand and outgoing healing. Then check his pet card after a relog: its jewel should not repeat any talent on the pet.
