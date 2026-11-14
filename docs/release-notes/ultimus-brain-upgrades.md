# Ultimus's brain upgrades, and saving his logs for the coach

New: **goals.** With each new step, his Thoughts list his four goals: finish this world's story, the quest, its step, and his next move. You can always see why he is doing something.

New: **confidence.** Each plan gets a score out of 100, with the reasons. Examples: "I never fought it (-10)", "bots got stuck by there 5 times (-25)", "I beat it 3 times (+15)". Under 45 (playbook Confidence.Careful) he does it the careful way: full health and mana first, and the map's way with no line-of-sight shortcuts.

New: **predict, then check.** Before an exit, door or Spiral Door he says where he expects it to take him. Before a fight he says how he expects it to go. If something else happens, it's a surprise: he writes it in his Thoughts, the Mistakes book and the bots' shared brain as a lesson.

New: **planning battles two rounds ahead.** He scores his whole hand over two rounds and plays the line that deals the most:
- hit now, then hit again
- blade now, then a bigger hit
- trap now, then a bigger hit
- save his pips now for a bigger spell next round

Damage past a creature's health counts for nothing. He gives his reason, for example: "plans two rounds: Weakness... worth about 340; better than Scarab now, then Scarab, worth 220".

New: **creature habits (the shared brain).** In every battle a server bot is in (Ultimus, the Quest Team or Bot Players), each creature's moves are learned: what it casts on which round, the schools it hits with, and how much of a bot's health a hit takes. Ultimus plays around these habits:
- he hits a creature that heals often first
- if a creature tends to hit hard this round, he heals or shields first
- if a creature tends to shield this round, building up is worth more

The shared brain lives in logs\bots\knowledge.txt (and .json).

New: **mood.** He turns careful after a lost fight, after 4 or more stuck spots, or after 3 surprises in the last hour. Careful means he rests to full before fights and heals sooner. He turns bold after 5 or more wins in a row: he rests a little less and heals a little later. The numbers are in his playbook (Mood.*).

New: **daily review.** Once a day, and when the server stops after at least an hour of play, he writes "today I learned" in logs\ultimus\learned-today.txt. He also changes a few of his own playbook numbers (ready health, heal line, careful confidence), but only inside the limits in logs\ultimus\self-tuning.txt. Set Auto = off there to stop him.

Changed: his playbook adds the new rules and a much longer know-how section at its end (thinking, battle planning, daily review). Your own changes are kept.

New: **Save for coach** and **Save joint file** on Ultimus > Logs:
- Save for coach puts every log as its own file in a dated folder you pick, plus thoughts.txt and one joint file, ready to attach in the chat.
- Save joint file saves all the logs in full in one file.

Copy all logs now also includes "today I learned" and the shared brain.

Where: Ultimus (server). Ahziael > Ultimus > Logs (the top card, and the buttons under Ultimus Thoughts).

Test:
1. Start the server with Ultimus on and open Ultimus Thoughts. With each new step you should see "My goals: ...", "I trust ... /100: ..." and "I expect ...".
2. In a battle, his log lines should say "plans two rounds: ...".
3. After a few battles, open logs\bots\knowledge.txt. It should list creature habits.
4. Click Save for coach and pick your Desktop. A folder named "Ultimus logs <date>" should open with all the files.
