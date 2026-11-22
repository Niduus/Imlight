Changed: The kill switch now lives in the private bots repo

The bots' kill switch is now killswitch.txt at the top of Niduus/ahziael-bots (no longer bots-killswitch.txt in Niduus/Imlight). Every Bot Update round signs it into the Hive Mind file with the brain and copies that file to Server\bots, so the bots read it from the signed file and need no internet or login. The bots play only when the signed Hive Mind file is there, is signed by Niduus, holds the switch, says "on = false", and was signed in the last 7 days. Otherwise no bot starts, and bots already playing log out within 5 minutes. logs/bots/killswitch.txt says what they saw. The kill switch no longer reaches GitHub, so the bots now have no web client except Ultimus's chat AI on this PC.

Where: every bot on every server.
Test: With killswitch.txt "on = false" in Niduus/ahziael-bots and a fresh signed file, logs/bots/killswitch.txt says "bots may play: the switch says on = false".
