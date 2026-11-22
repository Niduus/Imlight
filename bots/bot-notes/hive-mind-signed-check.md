New: The bots check for a signed Hive Mind file

At start, the bots now look for the Hive Mind file Niduus signs (Server\bots\hive-mind.ahzhive) and check that it is really his and that nothing in it was changed. They say what they found in the server log ("Hive Mind OK" or "Hive Mind NOT VALID") and in logs/bots/hive-mind-check.txt. This first step changes nothing about how the bots play; it only proves the check works before bots need the file.

Where: every bot, at server start and after every bot update.
Test: After this bot update, logs/bots/hive-mind-check.txt says "Hive Mind OK" once the signed file is in Server\bots, and "NOT VALID: no signed Hive Mind file" before that.
