# Ultimus: main story only, Menu Chat answers, friends and groups

Fixed: Ultimus picked side quests such as "Secret Strife of Pets" and "A Good Day to Cyclops" because they were marked as main quests in the game files. He now follows only the chain of quests that leads to each world's final quest.

Fixed: Ultimus and the Quest Team never answered Menu Chat. The phrase arrived as a code ("chatEntryKey73") rather than its words, so nothing matched. The server now looks up the words, and the bots answer hellos, thanks, goodbyes and asks again.

New: Ultimus is the only bot you can befriend. Send him a friend request and he accepts it. He never sends one himself. Once you are friends you see him on your friends list and can teleport to him. The Quest Team turns friend requests down.

Changed: Ultimus turns down every group invite, from players, friends and bots alike, because he quests alone. The Quest Team still groups with its own members and turns down everyone else. You now get the "no" at once instead of the invite hanging.

Where: no settings. The Ultimus log shows "accepts a friend request" and "turns down a group invitation".

Test:
1. Stand near Ultimus and say "Hello" in Menu Chat. He should answer within a moment.
2. Send Ultimus a friend request. He should accept it and show on your friends list. Try teleporting to him.
3. Invite Ultimus to a group. He should decline at once.
4. Send a Quest Team bot a friend request. It should be declined.
5. Watch the Ultimus log: he should no longer take side quests like "Secret Strife of Pets".
