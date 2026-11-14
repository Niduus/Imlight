# Ultimus sells at the Bazaar, and his chat shows in full

New:
- Ultimus can now sell the gear he will never wear at the Bazaar, not only quick sell it at a shop. The Bazaar pays about ten times what a shop does, so when his backpack is nearly full and he is in Wizard City anyway, he walks to the Bazaar to sell. When he is already at the Bazaar to buy something, he sells his junk there too.
- If the Bazaar is turned off, he never goes there to sell; he quick sells at a shop as before.
- Same rules as before: only when the backpack is nearly full, never while hurt, never in the middle of a quest step that is right in front of him. New Playbook setting Gold.BazaarSellGain (150): selling at the Bazaar must pay at least this much more than a shop.

- Ultimus answers questions about his own state from what he is really doing: "Are you in combat?", "are you walking?", "are you questing?", "are you waiting?", "are you stuck?" get an honest yes or no and what he is doing.

Fixed:
- Ultimus's chat lines had words swapped for "..." ("Working on ... the Map ..." for the quest Off the Map). His lines now show in full, and quest names are no longer wrapped in quote marks.

Where: Ultimus (Ahziael > Ultimus), the Bazaar in Olde Town, his whispers and chat in game.

Test:
1. Whisper Ultimus "are you in combat?" while he walks and while he fights: he says no, then yes.
2. Ask Ultimus "what quest are you on?" or "how are you?": the whole line shows, with no "...".
3. With the Bazaar on, let his backpack get nearly full while he is in Wizard City: he goes to the Bazaar and sells what he won't wear, and his gold goes up.
4. Turn the Bazaar off in Ahziael: he quick sells at a shop instead and never walks to the Bazaar.
