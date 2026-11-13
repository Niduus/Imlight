# Smarter Ultimus routes, live XP bar and quest book

New: Ultimus now plans his way like a player. Before each walk he checks whether walls or water cut the next step off from where he stands (an island such as the Kraken isle). If so, he walks to the area teleporter that leads there instead of pacing at the water's edge. He knows each area's teleporters from the game's own area files, and he also remembers any teleporter he steps on that the files missed. What he learns is kept in logs/ultimus/pads.json, so it survives a server restart.

New: when Ultimus is stuck a second time, he first tries another way (a teleporter that lands nearer his step). Only if that fails does the server send him back to the start of the area.

Changed: on his way to a step, Ultimus now walks around creatures he does not need to fight, the way a player avoids starting fights that are not part of the quest. The creature he is going to fight is not avoided.

New: Ultimus's page in Ahziael has a live XP bar under his name, styled like the game's. It shows the XP into the level over the XP the level takes (for example "865 / 1,340"). It slides along when he gains XP, and on a level up it fills, empties, and carries on.

New: Ultimus's page also has a Quests drop-down laid out like the game's "All Current Quests" book. It shows two columns of cards, four to a page with red arrows to turn the page. Each card has the title banner (with gold stars for main quests), what to do (Talk To, Defeat, Stop), who, the "x of y" count, the world and area, and the gold, XP and item rewards. The quest he is playing now is lit yellow. The Quest Team page gets the same XP bar and quest book for each wizard, because it uses the same sheet.

Where: Ahziael > Ultimus page (and the Quest Team page). The Ultimus log shows "Takes the teleporter at ...", "learns a teleporter in ..." and "tries another way".

Test:
1. Open the Ultimus page. The XP bar should show under his name with the current / max numbers, and slide forward after a fight.
2. Open the Quests drop-down. His quests should show as cards. The one he plays now should be yellow. Use the red arrows to turn pages.
3. If Ultimus ends up on the Kraken isle in Triton Avenue, he should take its teleporter off the isle within a minute or two, without being sent back to the start.
4. Watch him walk to a quest NPC through an area with creatures. He should bend around them rather than walk into fights.
