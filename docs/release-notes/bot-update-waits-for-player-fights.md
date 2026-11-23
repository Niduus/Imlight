Fixed: A bot update could pull a bot out of a fight you were in with it

When a bot update came in, the bots only waited for fights that had started before a set time, so a bot could be logged out in the middle of a newer fight with a player beside it. Now a bot update also waits for any fight where a player is fighting beside a bot, for up to 20 minutes, before the bots log out for it.
Where: any battle you share with Ultimus or another server-played bot while a bot update is waiting.
Test: join a bot's fight while a bot update is waiting and check the bot stays in until the fight ends.
