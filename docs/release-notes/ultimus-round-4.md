# Ultimus: cutscenes, dungeons, quest hints, rivals, session goals, friends page

New:
- **Patience with cutscenes.** When a cutscene plays he stands and watches it to the end, as a player does. Watching never counts as being stuck, and the progress check waits. A cutscene that goes on for over 2 minutes is flagged. The watch time is in his playbook (Pace.CutsceneSeconds).
- **Dungeon sense.** Before he walks into a dungeon he heals up to 95% health and 60% mana, since a dungeon is done in one go (Dungeon.PrepHealth and Dungeon.PrepMana). Inside one there are no side trips. His Thoughts note each new room, and how long the dungeon took when he leaves.
- **Hints from the quest's own words.** If he can't place a step (no spot on the quest arrow, or the person isn't where he looked), he reads the step's text. If it names a person in his NPC book or an area on the story's way, he goes there, as a player reading the quest would.
- **Rivals.** Creatures that beat him, or that take most of his health, are his rivals.
  - The list is in logs\ultimus\rivals.txt, worst first.
  - He meets a rival at full health and mana, with a deck made for it.
  - If a rival beat him twice, he levels up once more before facing it again.
- **Session goals and an end report.** When he logs in he sets goals: finish his current quest, finish 3 story quests, gain a level, and no off-track alarms. Every 10 minutes logs\ultimus\session-report.txt shows how it is going. When he logs out or you Stop him, he writes the final report (goals met, what happened, rivals, what to do next time), which is also added to session-reports.txt.
- **Friends page in Ahziael.** The Ultimus tab has a new "Friends he made" card: every player he has met, how often you talked, battles fought together, your school and world if you told him, when he last saw you, and whether he has muted you for a while.

Where: Ahziael > Ultimus tab. His logs folder: rivals.txt, session-report.txt, session-reports.txt.

Test:
1. Let Ultimus reach a quest with a cutscene. He stands still until it ends, with no stuck flags.
2. Watch him walk to a dungeon door. He heals up first if he is under 95% health.
3. After a session, click Stop. session-report.txt shows his goals with [x] for the ones he met.
4. Say hi to him in game, then open the "Friends he made" card. You are listed.
