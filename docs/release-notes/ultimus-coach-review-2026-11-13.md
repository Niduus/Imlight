# Ultimus coach review (logs from 2026-11-13)

Fixed:
- Ultimus no longer rests to full health before every fight just because he got stuck a lot. Resting to full is for after a lost fight. Last night he stood still at 80% health for minutes between easy XP fights, with no potions or wisps around.
- Time spent levelling up for a quest (XP fights and healing between them) no longer counts against the quest. "Payback" was flagged as slow while he was really levelling from 12 toward 14 for it.
- Menu Chat lines fit the moment now. He said "You're welcome" and "Welcome to my home! Have a nice Stay!" when a player joined his battle, and "That quest has a nice reward" after a win. Phrases that start with the right words come first, and phrases that only mention them are left out.
- The self-test after an update no longer raises a CRITICAL "Gear list: no gear known" alarm while the gear list is still loading in the background just after a start.

Already fixed in earlier patches (seen in these logs on older builds):
- 10 hours stuck at the start of The Oasis (08:15 to 18:40): the old stuck code kept sending him back to the area start, which was the stuck spot itself. The newer stuck ladder got him out in about 17 minutes.
- About 2 hours stuck in the "Tool Time" loop at Danforth: the NPC with two quests now clicks the right one. Confirmed working: right after the 21:39 update he picked up "Back to the Archeologist" at once.
- The Sinbad Gauntlet wander and the Garden of Hesperides detour are fixed in the "stays on the main story" patch.

Where: Ultimus (server side).

Test:
1. Let Ultimus fight for XP. Between fights he heals only when under his ready health (75%), not to full.
2. Join his battle. His Menu Chat welcome is a real welcome line.
3. Restart the server. The self-test no longer fails on the gear list.
