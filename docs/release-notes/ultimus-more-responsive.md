# Ultimus is more responsive

Changed: Ultimus reacts faster outside battle. It closes quest dialogs sooner (at most 4 seconds instead of 6). It accepts a quest offer about half a second sooner, clicks things once a second instead of every two seconds, and retries a click that did nothing after 12 seconds instead of 20. When its step moves, it plans the walk again after 8 seconds instead of 15. At a street teleporter stone, it waits 2.5 seconds instead of 4.

Changed: Ultimus answers the same player again after 12 seconds instead of 30.

Fixed: Ultimus no longer whispers back that it is busy with something it isn't doing. Before, it could say "One minute, I'm busy fighting" or "Sorry I'm busy training my pet" while walking to a quest. Now it picks a questing or plain "busy" line, and says "fighting" only while it really is in a battle.

Not changed: the 2.5 to 5 seconds Ultimus takes to think in battle.

Where: Ultimus.

Test: Watch Ultimus talk to a quest NPC. The dialogs should go by faster. Whisper "What are you doing?" to him: he should answer with a questing or busy line, never fighting or pets unless he is in a battle.
