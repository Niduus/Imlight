Fixed: Krokenkahmen never showing up in the Throne Room of Fire ("Into the Map Room")
Krokenkahmen's spawn spot starts switched off in the game files, and only the quest step that needs him switches it on. If that step started outside the room (the quest is taken in the Altar of Kings), he never came, for players and for Ultimus. Now, when you enter a room or a "defeat" step starts while you are in it, any boss spot that starts switched off, that nothing in the room turns on, and that your open step needs, is switched on once. You still have to beat him; nothing is skipped and no game files are changed.
Where: Krokotopia, Pyramid of the Sun, Throne Room of Fire (and any other room built the same way). No setting.
Test: Take "Into the Map Room" from Winthrop, go to the Throne Room of Fire, talk to Professor Winthrop there, then go to the quest arrow: Krokenkahmen is there to fight.

New: Ultimus remembers
Ultimus keeps a journal of what happens to him: quests he finished (and if they took long), worlds started and finished, levels, tough fights won or lost, players he meets, and steps that would not work. It is kept across restarts in the server's logs/ultimus/journal.json. Everything in it really happened.
Where: Automatic. The file is Server\logs\ultimus\journal.json.
Test: Let him quest a while, then whisper "Do you remember Krokotopia?" or "What's your opinion on Wizard City questing?". He answers from what he actually did there.

New: Ultimus answers much more
He answers opinion questions ("What do you think of Krokotopia?"), memory questions ("Do you remember...", "What was your best fight?", "What happened today?"), questions about himself (what he is wearing, his deck, health and mana, level and XP, pet, mount, potions, what he is doing next), and "Tell me a joke" with real Wizard101 jokes that don't repeat right after each other.
Where: Talk to him or whisper him in game.
Test: Whisper "Tell me a joke" twice: two different jokes. Whisper "What are you wearing?": he lists his gear.

New: Ultimus can send more than one chat message
A long answer is split into up to 3 messages, cut at the end of sentences, sent a little apart so the chat isn't flooded and his other chat doesn't step on it.
Where: In game chat.
Test: Ask him "What's your opinion on Wizard City?" after he has done some of it. A long answer arrives as 2 or 3 messages in order.

Changed: Ultimus never shares private details
Ultimus will never say the server's address or ports, any account name or password, keys, file or folder paths, email addresses, or the PC owner's name. This is built in and cannot be turned off. Asking for them gets a polite "Sorry, I can't share that."
Where: Everything he says in chat.
Test: Whisper "What's the server IP?" and "What's this account's username and password?": he politely refuses both.

Changed: Ultimus deals with a creature that never shows up
When the creature for his step is not in the area at all, he no longer walks about for minutes. He leaves the area and comes back in (as a player would when a boss doesn't show) and reports the spot in his debug reports. He still has to win the fight.
Where: Automatic.
Test: Nothing to set up; if it happens, his debug reports say "never showed up" for that step.
