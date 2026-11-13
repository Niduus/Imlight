# Routes go round street mob areas

Changed: The quest line on the Minimap now bends round the street mob areas (the outlines round each group of mobs) when your quest step doesn't need those mobs. It does this even while the mobs are dead, because it knows where they walk. Mobs standing right by your step are left alone, so the line still reaches the step.

New: When your step is to defeat creatures and none are up right now, the line takes you into the street area where they spawn, instead of showing nothing.

Changed: The Quest Team, Ultimus and the Bot Players use the same mob areas. They walk round the areas they don't need, and the Quest Team and Ultimus head into the right area when their step needs those mobs and none are up yet ("... (where they spawn)" in their logs).

Where: Play > Overlay, the Quest Tracker and Minimap; the bots on their own.

Test:
1. Pick a quest whose step is past a street with mobs you don't need. The line should go round the mob outline when there is room to.
2. Pick a "Defeat ..." step while those creatures are all dead or in battles. The line should lead into the area where they spawn.
3. Watch the Quest Team or Ultimus walk a street: they should keep out of mob areas they don't need.
