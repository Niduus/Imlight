# Ultimus: smarter zone exits, gear math, one copy of all his logs

Fixed: Ultimus still wouldn't put on the Adept robe. His gear math read percent stats 100 times too small (12% damage counted as 0.12%), so no piece ever looked worth a swap. Percent stats now count as percent, so the robe and other good pieces get worn. Once per level he also says in his Thoughts which piece he is keeping in his backpack and why.

New: smarter zone exits. When Ultimus stands at a walk-through exit and the game does not take him on, he wanders the exit's whole area like a player feeling for the trigger. Each try walks out and back in through a different part: the middle first, then wider rings, from every side. After 8 tries (or about 100 seconds), Ahziael takes him through to the area the exit leads to. That is written up in pink (abnormal), in his Thoughts and in the Mistakes book. Standing at the exit while he works it no longer counts as being stuck.

Fixed: a go-back to an area's start no longer counts as "got free" when he gets stuck again right away. He now has to keep moving for 45 seconds first.

Fixed: his thought "I has stood still" now reads "I have stood still".

New: "Copy all logs" copies every Ultimus log in one go: his log, Thoughts, the Mistakes book, the quest log, abnormal fixes, creatures he fought, issues, status, what he learned and his playbook. Every line is indented and tagged with the log it came from, for example [Main], [Thoughts], [Mistakes].

Where: Ahziael > Ultimus > Logs (the top card, "All of Ultimus's logs in one copy"). The rest happens on the server.

Test:
1. Start the server with Ultimus on. Within a minute or two, his Thoughts should show him putting on the Adept robe, or say why he keeps it in the backpack.
2. Let him reach The Oasis exit to Altar of Kings. He should walk in and out through different parts of the exit. If the game never takes him, he should be taken through within about 2 minutes.
3. On the Logs tab, click Copy all logs and paste into Notepad. Each line should start with a tag like [Main] or [Thoughts].
