New: Ultimus, the Solo Quester
Ultimus is one Balance wizard named Ultimus that the server plays by itself, alone, through the Main Story from level 1. Its goal is the Main Story above all. Players are welcome in its battles, and it joins a player's battle when the creature its quest needs is in it. It never joins the Quest Team, and the Quest Team never joins it. It has its own little program, its own logs and its own settings.
Where: Ultimus > Ultimus
Test: Start the server, turn Ultimus on, wait a minute, then click "TP to Ultimus" and watch it walk to its first quest.

New: Ultimus shows its quest, stats, gear and deck
The Ultimus page shows what it is doing now, its quest and step, its stats, the gear it wears, its deck and the spells it trained. It also has a shout-out to Discord user gese11 for sharing the bot concept.
Where: Ultimus > Ultimus
Test: With Ultimus playing, open the page and check that the stats, gear and deck fill in.

New: Ultimus debug reports for broken quests
When a quest does not work for Ultimus, Ultimus Issues shows it with a debug report: what went wrong, what a player would expect, where Ultimus was, the quest's and step's codes and data file, and the log that led up to it. "Copy debug report" copies it all, ready to hand to Claude to fix the quest. The reports are also saved as files in Server\logs\ultimus\debug.
Where: Ultimus > Ultimus Issues
Test: Let Ultimus play a while. If an issue shows, click "Copy debug report" and paste it somewhere to read it.

New: Pink lines for odd things Ultimus notices
Ultimus's log shows odd things it noticed in pink, such as a creature that starts no battle when walked into, being moved by the server without asking, or a battle that lasts over 10 minutes. What it learned from lost fights is kept in learned.jsonl in its folder.
Where: Ultimus > Ultimus Logs
Test: Open Ultimus Logs while it plays and look for the colour list above the log.
