# Ultimus: XP by fighting, push mode, watchdog, progress panel

Changed: When Ultimus is under-leveled for the next story quest, he now fights creatures for the XP like a player does. The old command that gave him XP is gone.
New: Push mode (on by default, playbook Push.On). He plays the main story first. He only takes shop trips in his first 30 minutes in a new world or after he loses a fight.
New: Step pre-check. Before each step he checks his level against the quest's level and reads what went wrong at that step before. If a fight step's quest is more than 3 levels over him (Push.LevelGap), or he already lost that fight, he fights other creatures for XP first.
New: Step memory. Anything that went wrong at a step is kept for good in step-memory.json, even if it only happened once.
New: Watchdog. If 45 minutes pass with no step done and no XP gained (Watchdog.Minutes), he raises a red alarm. Then he uses a player's unstuck moves: first back to the area start, then .stuck go. He never skips a step.
New: Mark and Recall. Before a trip to a trainer or a shop in another area, he marks his spot (Mark Location, which costs mana) and recalls back afterwards.
New: Solo tags. Every lesson and mistake he writes says "solo" or "with players", so solo data and team data never mix.
New: Progress panel on the Ultimus Logs tab. It shows his level, area, story world, how long each world took, time since his last progress, his mood and today's counts. Copy all logs and Save for coach include it.

Where: Ahziael > Ultimus > Logs > Progress. Playbook rules are in the Push and Watchdog sections.

Test: Let Ultimus play. The Progress panel should fill in within a minute. When he trains in Ravenwood from another area, his log should say "marks his spot" and later "recalls to the spot he marked". His log should never say he was given XP.
