# Teleporters no longer bounce you back

Fixed: Since 0.10.839, a teleporter within an area (Triton Avenue, the Kraken isle) could drop you right on top of the teleporter that leads back, which sent you straight back again, over and over, so you were stuck.

Now, after a teleporter sends you, no teleporter works for you for a few seconds. The one you land on also does nothing while you keep standing on it. Walk off it and back on to use it.

Where: Imlight.ini [World] TeleporterCooldownSeconds (default 5 seconds, 0 to 60). Applies live. [World] TeleporterDelaySeconds is unchanged.

Test:
1. Go to Triton Avenue and take the teleporter over to the Kraken.
2. You should land and stay there, not be sent back.
3. Walk off the pad and back on: now it takes you back.
