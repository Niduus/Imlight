# Ultimus reads creature decks, rounds no longer rush, smarter questing

Fixed: a round could play out all at once. The creatures pick their spells as a round opens, and when Ultimus (or a Quest Team wizard or a Bot Player) picked straight after them, the round ended before its planning time had even begun. The planning screen then started over the round's spells, and the game rushed every animation through in a few seconds. Now a round only ends during planning, and the next planning never begins while spells are playing. This affects every battle, not only Ultimus's.

Changed: Ultimus takes a person's time to plan. He looks at his hand for 2.5 to 5 seconds before reaching for a card, and he never picks before planning starts. When he joins a battle mid-planning he plans with the time that is left, and he still lets a player before him pick first.

New: Ultimus reads the decks of the creatures he fights, as a player knows them from the wikis or from fighting them before. He knows which schools they can hit with (natural attacks too), and whether they trap, blade, shield or heal.
- In battle he only casts a shield that stops what the enemies can actually hit with. He no longer casts Elemental Shield on a Myth creature that has no Fire, Ice or Storm hits. A shield that is useless in this fight may be discarded.
- Before a fight he makes his deck ready with the shields that fit what the creature hits with.
- His gear's resist leans toward the schools the area's creatures hit with.
- His log says what he read, for example "reads the enemies' decks: hits with Myth, Storm, traps".

New: Ultimus watches creatures as he walks. One that wanders near his way has the way planned again around it, and one that walks up close has him step back out of its reach. He only starts fights his Main Story step needs.

Fixed: "talk to" steps that name no area (for example Cyrus Drake, Victor Darkwood and Merle Ambrose in "Run and Done") were skipped within seconds. Ultimus now works out where that person stands and travels there like a player.

Changed: Ultimus no longer gives up on a step at once. When he can't see how to do a step, he looks around the area for 2 minutes before it is written up and skipped. An NPC that offers nothing gets 90 seconds instead of 30.

Fixed: Ultimus wrote "won the battle" after losing one, because a defeated wizard comes back with some health. Losses are now logged as losses.

Fixed: Ultimus's battle log named creatures by short codes such as "0" or "95DD". It now uses their real names, such as "Cyclops Legionnaire".

Where: battles (everyone) and Ultimus. No settings.

Test:
1. Start a battle with Ultimus on and watch round 1. Ultimus's pick should take a few seconds, and the spells should play at normal speed.
2. Fight with a friend or a Bot Player. Round 1 should give everyone the full planning time.
3. Watch Ultimus fight a Myth creature (a Cyclops) while he is hurt. He should not cast Elemental Shield. Look in his log for "reads the enemies' decks".
4. Let Ultimus reach "Run and Done". He should travel to Cyrus Drake, Victor Darkwood and Merle Ambrose instead of skipping those steps.
5. Watch him walk past creatures that his step doesn't need. He should go around them or step back, not walk into them.
