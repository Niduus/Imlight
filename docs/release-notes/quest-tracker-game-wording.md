# Quest Tracker steps read like the game's own quest helper

Changed: The Quest Tracker's step lines now use the game's own wording, from its text files, filled in the same way the game fills them. Before, it used Ahziael's own wording ("Defeat 3 Troubled Warrior", "Go to ..."). Now it reads like the game: "Talk to Private Farnsworth", "Defeat Troubled Warriors (1 of 3)", "Collect Shields (0 of 3)", "Use Fire Feather On Great Fire Pit". Steps that need more than one now show the game's "N of M" count, and it goes up as you do them. The quest names were already the game's own. The labels on the Minimap's quest rings use the same wording.

Checked against all 2,420 quests' 4,710 steps that aren't "talk to" steps: none shows a raw text key.

Where: Quest Tracker add-on, Minimap quest rings.

Test: Pick a quest with a "defeat" step. The line should match the game's quest helper word for word and show "(0 of 3)" style progress.
