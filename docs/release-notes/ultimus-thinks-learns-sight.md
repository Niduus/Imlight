Fixed: Ultimus no longer stays stuck at The Oasis start for hours
Every way of getting unstuck now goes through one list of fixes, and a spot never gets the same fix twice. That includes the 6-minute "no progress" check, which used to send him back to the same start again and again. A spot remembers what failed there, even when the next quest step is a different one. Two new fixes come before Ahziael finishes the step: a small hop next to a goal he can see, and Ahziael taking him to the area his step is in (like .stuck go). When the map gives no way from where he stands, he walks off in a straight line instead of standing still. If he stands inside an exit that doesn't take him on, he steps back out and walks in again.
Where: Ultimus > Logs
Test: Send Ultimus to Krokotopia for "Digging in the Dirt". If he gets stuck at The Oasis, each stuck line should name a different fix, and he should be in Altar of Kings within a few minutes.

Fixed: Ultimus wears his Adept robe again
His school's Adept robe was held back until level 30 once it came off, so it never went back on. Now he puts it back on at any level. If it has gone missing, Ahziael gives it back once.
Where: Ultimus
Test: Look at Ultimus's wizard sheet: the robe slot should show his school's Adept robe.

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
