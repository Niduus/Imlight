# Ultimus picks the right quest when an NPC has two

Fixed: in the Chamber of Fire, Assistant Danforth gives two quests, "Tool Time" (a side quest) and "Back to the Archeologist" (the story). Ultimus always clicked the first one, so Danforth offered "Tool Time" every time, Ultimus turned it down, and he never got the story quest. He got flagged stuck there 45 times. Now he clicks the option that names the quest he wants. If no option names it, each try clicks the next one. The Quest Team does the same.

Fixed: a pink "has more health than its most" no longer shows right after Ultimus changes gear. Taking off a piece with health lowers his max health a moment before the game trims his current health.

Fixed: his thoughts said "I have not fought and Collect Nirini Quartermaster". Now they say "Nirini Quartermaster".

Changed: "Copy all logs" now puts the newest lines first, and takes only the last 3 hours of his main log, so the useful part fits in one chat paste. The order is: status, Thoughts, Mistakes, Fixes, Foes, Quests, main log, Learned, Playbook. The big issues file is left out because every issue is already in the main log.

Where: Ultimus (server), and Ahziael > Ultimus > Logs > Copy all logs.

Test: let Ultimus reach Assistant Danforth in the Chamber of Fire after "Quarter Master". He should get "Back to the Archeologist" within a click or two. Then click Copy all logs and paste it: the top of each part should be the newest lines.
