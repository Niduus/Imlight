# Ultimus lives like a player: pacing, backpack, gold, gear, training plan, lost fights, friends

New: Pacing. After each step he takes a short moment (up to 4 seconds), reads dialogs at a reading pace, and his walk wobbles a little so he does not walk in perfectly straight lines. You can change all of this in his playbook (Pace).
New: Backpack sense. When fewer than 8 backpack spaces are free, he walks to a shopkeeper and sells the gear he will never wear, like a player does. Quest items and the gear he is wearing are kept. Before, Ahziael simply removed the junk and gave him the gold.
Changed: Gold. Ahziael no longer gives him gold. He keeps a reserve for the next world's gear (150 gold per level) and says his gold plan in each new world. To bring the old top-ups back, set Gold.Gifts = 1 in his playbook.
New: Gear check at each new world. He looks at that world's shops first, and goes to the Bazaar in Olde Town when it has a clear upgrade he can pay for. He never crafts and never trains pets.
New: Training plan. In each new world, and at each new level, he writes training-plan.txt in his folder. It lists the spells he can learn and why each one helps him (for example, "Fairy: a heal of my own").
New: Learning from a lost fight. He works out why he lost (he went in hurt, it hits very hard, it heals often, the fight ran long) and changes one thing for the next try. First he goes in at full health with a deck built for that fight, then he fights for XP first, then he looks for better gear.
New: He remembers the players who talk to him (players.json) and greets them again when they come back.

Where: Ahziael > Ultimus (Thoughts and log). The settings are in his playbook under Pace, Backpack, Gold and Gear.

Test: Let Ultimus play for a while. His thoughts should show a gold plan and a training plan when he enters a new world, and "sells N things it will not wear" once his backpack fills up. Say hello to him in Menu Chat, come back an hour later, and say hello again: his log should say he remembers you.
