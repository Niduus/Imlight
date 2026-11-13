# Ultimus stuck going in and out of Ravenwood

Fixed: On "To Ravenwood!", walking through the Commons tunnel took you to the everyday Ravenwood instead of the quest's own Ravenwood, where Mr. Lincoln waits. Ultimus kept going back and forth through the tunnel every few seconds. It was never in one place long enough to be seen. Now, when two doors share one tunnel, the door tied to the quest step you are on takes you. This applies to players too.

Fixed: When Ultimus goes round in a loop, the issue now names the quest it is on. Ahziael skips the broken step so Ultimus moves on instead of looping forever, and the debug report shows the quest.

Where: The Commons tunnel to Ravenwood, while "To Ravenwood!" is on its first step. Ahziael > Ultimus > Ultimus Issues.

Test:
1. Start a new wizard (or use Ultimus) and take "To Ravenwood!" from Headmaster Ambrose.
2. Walk through the Commons tunnel to Ravenwood. You should arrive where Mr. Lincoln is waiting.
3. Turn Ultimus on and check that it stays in one area long enough to be found (Ultimus > TP to Ultimus).
