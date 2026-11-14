Fixed: Ultimus no longer stays stuck at The Oasis start for hours
Every way of getting unstuck now goes through one list of fixes, and a spot never gets the same fix twice. That includes the 6-minute "no progress" check, which used to send him back to the same start again and again. A spot remembers what failed there, even when the next quest step is a different one. Two new fixes come before Ahziael finishes the step: a small hop next to a goal he can see, and Ahziael taking him to the area his step is in (like .stuck go). When the map gives no way from where he stands, he walks off in a straight line instead of standing still. If he stands inside an exit that doesn't take him on, he steps back out and walks in again.
Where: Ultimus > Logs
Test: Send Ultimus to Krokotopia for "Digging in the Dirt". If he gets stuck at The Oasis, each stuck line should name a different fix, and he should be in Altar of Kings within a few minutes.

Changed: Ultimus picks gear by reasoning, like a Balance player questing alone
He no longer judges one piece at a time. He looks at what each piece does for his whole outfit. First he wants enough resist, enough accuracy and enough health, and he works his power pip chance toward 100%. Above those, damage comes first, with pierce. Each swap is explained in his thoughts, for example: "resist 8% to 14% and now meets what I want, damage 10% to 9%". Once a level, he looks over his outfit and says what he is short of. He is never handed gear by Ahziael. (He still owns his Adept robe. A Cloak replaced it because of an early bug, now fixed, that read the robe as having no stats. He will now put the robe back on if it is better for his whole outfit.)
Where: Ultimus > Logs
Test: Watch the Ultimus Thoughts section after he gets new gear. Each swap should say what it raises and why.

New: Ultimus's playbook
Ultimus now plays by a written playbook of Wizard101 know-how for questing, battle, gear and learning. Each rule has a number and a reason, for example "heal at 50% health". His thoughts name the rule behind a choice. To change a rule, open the playbook from the Ultimus Thoughts section, change a number and save. He uses the new number within a few seconds, without a restart.
Where: Ultimus > Logs
Test: Press Open his playbook, change HealAt under [Battle] to 0.7, save, and watch him heal earlier in his next fight.

New: Ultimus Thoughts
Ultimus thinks out loud: why he picks what he picks, what he remembers about a creature or a spot where he got stuck, and what he will do differently. The thoughts appear in brown in his log and in a new Ultimus Thoughts section.
Where: Ultimus > Logs
Test: Watch the Ultimus Thoughts section while he quests. Before a fight it should say whether he has fought that creature before.

New: Ultimus learns from the creatures he fights
He remembers every creature he fought for a quest: how many fights he won and lost, and how much health each fight took. If a creature beat him or takes most of his health, he heals up to full health and mana before fighting it again. This is kept in logs/ultimus/foes.json.
Where: Ultimus > Logs
Test: After he loses a fight, his next try at that creature should start with a thought about healing up to full first.

New: The bots' mistakes book
Ultimus writes down each mistake he makes: getting stuck, losing a fight, or finding no way on the map. Each entry records where it happened, the step, his reasoning at the time, what he tried, what worked, and what prevents it. The Quest Team reads the book when it gets stuck at the same spot and uses what freed Ultimus there. The book is in the server's logs\bots folder (mistakes.txt), and you can read it in Ahziael.
Where: Ultimus > Logs
Test: Open the Mistakes book section after Ultimus has been stuck once; the spot, his reasoning and the fix should be listed.

New: Line of sight circle
Ultimus sees about 40 feet around him. You can change this with SightRadius in the [Ultimus] section of the server settings, and it takes effect without a restart. If his goal is inside that circle and nothing is in the way, he walks straight to it, even when the route map disagrees. If something blocks the straight line, he looks for a short way around inside the circle. As a last resort, when he is stuck right by his goal, he hops to a clear spot next to it, never through a wall. That hop is written in the mistakes book so the route can be fixed later.
Where: Ultimus > Logs
Test: Watch Ultimus Thoughts near a quest person: it should say "I can see ... with nothing in the way: I walk straight to it."

Changed: Ultimus never counts on Balance Prism, and he reasons against Balance-resistant creatures
Balance Prism is in the game's files, but no player can get it by vanilla means, so Ultimus never puts it in his deck or plays it. Against creatures that resist Balance, he compares two options using their resist, his pierce and the cards in his hand. If his pierce covers most of their resist, he goes full pierce with Balance spells. Otherwise he hits off-school, with a split hit like Hydra or a spell he trained from another school. Each hit now counts his pierce, and his log says which option he chose and why.
Where: Ultimus > Logs
Test: Watch a fight against Balance creatures (for example in Krokotopia). His hit lines should say "They resist Balance ..." and name the option he chose.
