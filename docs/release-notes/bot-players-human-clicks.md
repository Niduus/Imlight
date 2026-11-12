# Bot Players: street bots click like a person in battle

Changed: Street bots in a battle now play like a person at the keyboard. They take a moment to read their hand (longer with more cards), discard the cards they won't play one at a time, then click their card. Each click is the same one your game sends. If they change their mind after a player picks, they click again quickly. They still wait for players in the circles before them and help players first.

Where: Any battle a street bot joins.

Test:
1. Start a fight with a street bot next to you. Watch its circle: it should not pick the instant the round starts. It discards, then picks a few seconds in.
2. Pick your card first. The bot should change its pick to help you when that makes sense.
3. The round should start once everyone has picked, without waiting for the timer.
