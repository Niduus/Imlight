Fixed: Cards a boss gives now show up in your hand
When a boss hands out a card, like Failed Ritual Oni giving each wizard a Locust Swarm at the start of round 4, the card never reached the hand because the server skipped that part of the spell. Now the given card goes into your next hand, in place of a random card if the hand is full. This works for every spell in the game that gives a card or adds one to the deck, about 345 of them, and for creatures as well as wizards.
Where: Any battle with a boss that gives cards, such as Failed Ritual Oni, Thrym or Grizzlebort.
Test: Fight Failed Ritual Oni and wait for round 4; at the next planning phase your hand should hold its Locust Swarm, and casting it should turn off its Shield Trap cheat.
