Fixed: Quest dialog history showed only empty rows
Opening a quest's dialog history in the quest book showed blank rows with no words or pictures, and it wiped what the game had saved for that quest. The server now sends every line you have been told for the quest so far (the offer, each step started and each step done), and the game keeps them. Histories that were already wiped fill back in the next time you open them.
Where: In the game, quest book > a quest > dialog history button
Test: Open the quest book, pick a quest you have done a few steps of (for example "Tall Enough to Meet the Sun"), and open its dialog history. The rows show who spoke, their picture and what they said, and closing and opening it again keeps them.
