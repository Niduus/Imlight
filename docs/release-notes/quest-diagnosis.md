# Quest diagnosis for Ultimus (and the Quest Team)

New: When Ultimus raises an issue, it now checks why the quest step is stuck before it reports it. It looks at the quest's data (does the quest and step exist, is it switched off), the wizard (level, earlier quests, health, backpack, which step is open), and the live server (is the NPC spawned, did the NPC offer the quest, are the right creatures spawned and free, is there anything to click at the quest arrow, what did the server last say). It then names the most likely cause, how sure it is (High, Medium or Low), and whether the step can be done at all.

Where: Ahziael > Ultimus > Ultimus Issues. Each issue's text ends with "Diagnosis (... confidence): ...". Open "Debug facts" to see "Likely cause", "Can the step be done", and every check marked OK, PROBLEM or INFO. The same lines are in Server\logs\ultimus\debug\issue-NNNN.txt. A High confidence cause is also written in pink in Ultimus Logs.

Changed: The check lives in the shared bot code, so the Quest Team can use the same check.

Test:
1. In Ahziael, turn Ultimus on and let it quest for a while.
2. When an issue shows up in Ultimus Issues, read the end of its text for the Diagnosis line.
3. Click "Copy debug report" and check the Likely cause and the Check lines at the top.
