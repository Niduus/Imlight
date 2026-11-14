# Ultimus: story order, self-test, Pause and explain, milestones, side quests, weekly pace

New: Story order. Ultimus knows which world of the story he is in (for example "world 2 of 19, Krokotopia, played at about levels 10 to 20; next: Marleybone") and whether his level is ahead or behind. He says it in his thoughts, and it shows in the Progress panel.
New: Self-test after each update. The first time he plays after Ahziael updates the server, he checks his own parts: playbook, quest data, way finding, creatures, trainers, gear list and Bazaar. He writes the result to self-test.txt and shows it as a milestone, so anything an update broke shows up right away.
New: "Pause and explain" button on the Ultimus tab. It pauses him and opens explain.txt, which lists where he is, his goals, his next move and how much he trusts it, his mood, his gold and his last thoughts. Click "Pause or resume" to let him go on.
New: Milestones in Ahziael's top banner: a world finished, a level up, being badly stuck, the story finished, the self-test and the weekly report. "Show what happened" opens his logs.
New: Side quests only when they help. When he needs XP and a side quest of about his level is given in the same area, he takes it instead of fighting one creature at a time. You can turn this off with Side.On = 0 in his playbook.
New: Weekly pace report (pace-report.txt). It shows how long each world took him, next to about how long a player takes.
Changed: Copy all logs also includes his explanation, self-test, training plan and pace report.

Where: Ahziael > Ultimus (the Pause and explain button) and the banner at the top of Ahziael.

Test: With Ultimus playing, click "Pause and explain". A text file with his plan should open within a few seconds. After the next update, the banner should show his self-test result.
