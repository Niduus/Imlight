# Ultimus Core: the Quest Team and Bot Players think with Ultimus's battle brain

New: Ultimus Core. The battle brain Ultimus grew is now shared. The Quest Team and the street Bot Players use it too, while each keeps its own goals and jobs:
- Ultimus plays alone.
- The Quest Team plays its set team's roles: heal, shield, trap and blade for each other, and it still quests only with its own team.
- Street Bot Players fight like players.
New: What they share. All of them read the creatures' habits from the shared brain, hit a creature that heals often first, and count pierce in how hard a hit lands. The Quest Team's hitter also plans two rounds ahead, like Ultimus.
New: The Quest Team has its own mood. After a lost fight it plays carefully and heals a little sooner. After a winning streak it heals a little later. Ultimus's mood is separate from the team's.
Changed: Solo and team data never mix. Everything the Quest Team writes in the shared brain is tagged "team of 4", and everything Ultimus writes is tagged "solo" (or "with players" when he fights next to a real player). When you turn the Quest Team on later, its team data stays apart from Ultimus's solo data.

Where: Nothing new to click. You see it in the bots' battle reasons ("plans two rounds", "heals often, so it goes first") and in logs\bots\knowledge.txt.

Test: With the Quest Team on, watch a battle. The team's Storm wizard should sometimes say "plans two rounds". The lessons in logs\bots\knowledge.txt should say "team of 4" for the Quest Team and "solo" for Ultimus.
