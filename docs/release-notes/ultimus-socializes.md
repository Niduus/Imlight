# Ultimus talks like a real player

New:
- Ultimus now reads what players say near him or whisper to him, typed or Menu Chat, works out what they mean, and answers the actual question with his real information.
- Ask what quest he is doing, in any wording ("what quest are you on?", "wyd", "what are you up to?"), and he tells you his real quest and its current step. He never says "busy" anymore, even mid-battle.
- He also answers where he is, his level, his school and his name. He can give tips too: "where is Headmaster Ambrose?" comes from his NPC book, and "how do I beat ...?" comes from what the bots have seen that creature do.
- He remembers each player: when you met, how often you talked, battles fought together, and your school and world if you told him. When you come back after a while he welcomes you back.
- Sometimes he asks a short question back, such as your school or where you quest, and remembers the answer.
- He has a personality: a friendly, slightly nerdy Balance wizard whose mood follows his progress. He never says the same line twice in a row.
- He uses typed chat freely. He answers Menu Chat with Menu Chat when a phrase fits, and with typed chat otherwise. If his account's chat level would hide a typed line, he sends it as a Menu Chat line instead.
- In battle he keeps it short ("brb, fighting!"). If you ask to help while he is fighting, he says "Sure, jump in!"

Changed:
- Boundaries: he quests solo (no groups or follows), gives no gold or items, doesn't teleport on request, and never shares server or private info. If you sincerely ask whether he is a bot, he says yes.
- Rude players get one calm answer, then 30 minutes of silence. Spammers (more than 5 lines in 20 seconds, or the same line three times) get 10 minutes of silence.
- He answers at most 8 lines a minute and leaves a few seconds between answers to the same player. Questions always get an answer.
- Every exchange goes into his Thoughts with the reason for his answer.

Where: in game, near Ultimus or by whispering to him. His memory of players is in logs/bots/social-Ultimus.json.

Test:
1. Stand near Ultimus and type "what quest are you on?". He answers with his quest name and step.
2. Whisper "hi" to him, then whisper again after an hour. The second time he welcomes you back.
3. Type "where is" plus an NPC from his current world. He says which area they stand in.
4. Join one of his battles, then type "gg" after it. He remembers the battle the next time you say hi.
5. Send the same line three times quickly. He goes quiet for 10 minutes.
