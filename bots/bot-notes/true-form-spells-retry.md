Fixed: True Form no longer ends up with an old spellbook after a bot update

After a bot update, True Form waited up to 2 minutes for the game's spell list. If it wasn't ready by then, he gave up for good ("his spells were not read in time") and kept his old spellbook and deck. Now he waits only 15 seconds and logs in. If his spells still aren't ready, he tries again every 30 seconds out of battle until he gets them. This is the same fix Zoom got.
Where: True Form, right after a bot update or a school/setup switch.
Test: Right after a bot update, ask True Form to "go fire". He should be back quickly. Within a minute or two his deck window should show his fire spells. The server log says "True Form: got his spells on a later try" or "spells from his kept list".
