New: The bots listen for Ahziael's signal

Every 5 minutes the bots now ask Ahziael, on this PC only, whether it is the one running this server, and write what they hear to the server log and logs/bots/ahziael-signal.txt ("heard" or "NOT heard" and why). This first step changes nothing about how they play. In a later bot update the bots will only play while they hear Ahziael's signal, so they cannot run anywhere but under Ahziael.

Where: every bot, from server start.
Test: After the Ahziael update that sends the signal, logs/bots/ahziael-signal.txt says "Ahziael signal heard". Before it, it says the server was not started by an Ahziael that sends the signal.
