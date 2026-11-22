New: A kill switch for every bot

The file bots-killswitch.txt at the top of Niduus/Imlight now switches every bot off when it says "on = true". The bots read it at start and every 5 minutes: while it is on, no bot starts, and bots already playing log out at once, even with the signed Hive Mind file and Ahziael's signal. A server that cannot reach GitHub keeps the last state it read (logs/bots/killswitch.txt). After it is set back to "on = false", the bots come back at the next bot update or server start.

Where: every bot on every server.
Test: Leave it "on = false": the bots play as before and logs/bots/killswitch.txt says "on = false".
