Changed: Ultimus keeps talking during battles
He used to answer every line in a battle with "brb, fighting!", even "what is your name". Now he answers questions normally during a battle and says he is fighting only once a battle (on a goodbye or a thanks), without asking questions back.
Where: Talking to him or whispering him while he is in a battle.
Test: Start a battle near him and whisper "what is your name": he answers with his name.

Fixed: Ultimus's sentences read properly
Quest step words were pasted into his sentences as they are, like "I'm Go to Krokotopia,Throne Room of Fire". Now they read as English: "I'm heading to Throne Room of Fire in Krokotopia". "Defeat 1 creatures" reads "defeating a creature", and commas get their space.
Where: His chat answers about what he is doing.
Test: Whisper "are you in battle?" while he is walking somewhere: the sentence reads naturally.

New: Ultimus follows a conversation
Short follow-ups now use what you were just talking about: after "What's your favorite school?", "What about ice?" gets his view on Ice and "why?" gets where his opinions come from. A full new question starts a new topic, so it doesn't get mixed into the last one. His favorite school is always Balance, his own school; which school is easiest to fight is said separately.
Where: Chatting with him in game.
Test: Ask "What's your favorite school?", then "What about ice?", then "How much gold do you have?".

New: Ultimus answers questions about you
"What is my name?", "What level am I?", "Where am I?", "Do you remember me?" and "How long have we been friends?" get real answers from what he can see in game. That is only your wizard, never your account.
Where: Chatting with him in game.
Test: Whisper "what is my name": he says your wizard's name.

New: Ultimus learns which answers land
When you answer him with "thanks", "cool" or "lol", he remembers that he read your words the right way. When you answer "huh?" or "that's not what I asked", he remembers it was wrong. Lines he couldn't understand are written down for the coach. This is kept across restarts.
Where: Server\logs\bots\chat-learned-Ultimus.json and chat-misses-Ultimus.txt.
Test: Chat with him a while, then look at the two files.

Changed: Ultimus heals before a boss
He no longer starts a boss fight under half health (he set off for Krokenkahmen at 34% health). The 25% rule still holds for ordinary steps.
Where: Automatic.
Test: Hurt him to under 50% before a boss step: he heals first.
