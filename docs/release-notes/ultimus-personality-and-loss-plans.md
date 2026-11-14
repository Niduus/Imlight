# Ultimus: honest chat, his own opinions, smarter after losses

New:
- Ultimus only goes looking for wisps or potions, or waits for wisps, when he is under 25% health. Above that he keeps questing.
- After losing the same fight twice, Ultimus thinks about why and picks what he believes is the best fix: going in at full health and mana, buying better gear, redoing his deck, a side quest of his level, or waiting a few minutes for help. Each fix is tried once before the next, and he says his reason.
- When a player tells him to do something and he agrees (like "go get potions"), he really does it. If his rules do not allow it, or he can't (no gold, potions already full), he says why instead.
- Saying just his name ("hey Ultimus", "ULTIMUS??") gets an answer ("Yes? I'm here!") and what he is working on.
- "Can you fish?" and other "can you" questions get an honest yes or no.
- "Who made you?" is answered honestly: Niduus made him.
- Favorite and least favorite questions (world, school, spell, quest, creature, area, player) are answered only from what he really played: the creatures that beat him, how worlds went, where he lost time. With not enough experience he says so honestly. Nothing is pre-written, and his opinions are kept across restarts and change as he plays.

Fixed:
- He waited for wisps at 63% health.
- He said "Got it!" to "go get potions" and then did nothing; "like right now" was ignored.
- "What's your least favorite school?" answered "I'm a Balance wizard".
- After a won fight he did not pick up the wisps the battle dropped (the beaten creatures standing there kept him off them). He now picks up his own battle wisps unless a creature stands right on one.
- He ran off to The Oasis to shop for a Jewel of Instinct while the quest giver stood right in front of him. A shopping trip to another area now waits until his story step here is done, and after two trips for one item with no sale, he stops looking for it.
- The quest step in the Spectator (and Quest Tracker) showed "Talk To NPCFormats_Goal_First_Last" instead of the person's name.
- When a player stepped onto his dungeon or tower sigil with him, he stepped off. He now stays on the sigil until the countdown takes them in together.

Where: Ultimus (Ahziael > Ultimus), his chat in game, the Ultimus Spectator add-on.

Test:
1. Hurt Ultimus to about 60% health: he keeps questing. Under 25% he goes for wisps or potions.
2. Watch him win a fight: he picks up the health or mana wisps it dropped if he is not full.
3. Type "hey Ultimus", "go get potions", "like right now", "Can you fish?", "Who made you?", "What's your favorite world?", "What's your least favorite school?" and check the answers make sense.
4. Step on his dungeon sigil with him: he stays on it and you both go in.
5. In Krokotopia, check the Spectator step reads a real name, not "NPCFormats_...".
