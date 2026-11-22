New: Ahziael sends the bots its signal

When Ahziael starts the server, it now hands the server a fresh secret and answers the bots on this PC only, for as long as the server runs. The bots ask every 5 minutes and write whether they heard it in logs/bots/ahziael-signal.txt. A server started without Ahziael gives its bots no signal. For now this only shows in that file; a later bot update makes the bots play only while they hear Ahziael. The port is [Bots] AhziaelSignalPort in Imlight.ini (12395).

Where: Ahziael's server start, and the bots.
Test: Start the server from Ahziael and wait a minute: logs/bots/ahziael-signal.txt says "Ahziael signal heard".
