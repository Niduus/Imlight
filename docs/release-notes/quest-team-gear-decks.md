New: Quest Team wizards wear the best gear for their school and level
Each Quest Team wizard puts on the best gear it owns for its own school and level, favouring its school's stats over universal ones. It also walks to a shop in the world it is in and buys better gear with its own gold (it keeps a quarter of its gold for potions and trainers). Crowns-only and retired items are never picked. Bot Players get the same gear when they spawn.
Where: Quest Team > Quest Team (open a wizard's "Quests, gear, deck and trained spells")
Test: Turn the Quest Team on, let it play for a while, and open a wizard's details. Its gear should match its school and level, and Team Logs should show lines like "puts on ..." and "buys ...".

New: Quest Team decks change for the next fight, and they discard cards in battle
Before each fight the wizards rebuild their decks for what they are about to fight (the enemy's school, a boss or a crowd), and again when they learn new spells. In battle they discard cards they do not need, like a Storm wizard's heals, or a shield that is already on everyone. Bot Players do the same.
Where: Quest Team > Team Logs
Test: Watch a Quest Team fight. Team Logs should show "makes its deck ready for ..." before it, and "discards ..." during it.

New: Go to a Quest Team wizard
Each Quest Team wizard has a "TP to" button that takes your wizard right next to it, wherever it is, even inside its own copy of a dungeon. You can also type .questteam tp Ice (or Life, Death, Storm), and .questteam where lists where they all are.
Where: Quest Team > Quest Team
Test: With the team playing and your wizard in the game, click "TP to Storm". You should land next to the Storm wizard.

New: See each Quest Team wizard's quests, gear, deck and spells
Each wizard on the Quest Team page has a "Quests, gear, deck and trained spells" section showing the quest it is on and its other quests, every piece of gear it wears, the cards in its deck and what the deck was made for, and every spell it has trained.
Where: Quest Team > Quest Team
Test: With the team playing, open the section under each wizard and check it matches what you see in game.

Fixed: Quest Team wizards never logged in
Turning the Quest Team on showed all four wizards "offline" at level 0, with the team stuck on "waiting for everyone to be in the game". The wizards could not start at all, so they never tried to log in. They now log in one after the other a few seconds after the team starts.
Where: Quest Team > Quest Team
Test: Start the server and turn the Quest Team on. Within a minute, Team log should show each wizard "is in ..." and the page should show them playing.

Fixed: Bots no longer wear developers' gear or Deckathalon decks
Quest Team wizards and Bot Players never pick, buy or put on the developers' and testers' own gear (like the QA robe, or anything marked Dev, Debug, GM, Admin or Test), and never equip a Deckathalon deck. A Quest Team wizard already wearing such gear takes it off; a Deckathalon deck is swapped for a normal deck as soon as it has one. Developers' test spells never go into a bot's deck, and any already there are taken out.
Where: Quest Team > Quest Team (each wizard's gear and deck); Bot Players on the streets
Test: Turn the Quest Team on and open each wizard's "Quests, gear, deck and trained spells". No QA or dev gear and no Deckathlete's Deck should show, and Team log should show "takes off ..." for anything that was on.

Fixed: Quest Team walks facing forward
The Quest Team wizards walked sideways. They now face the way they walk.
Where: in game, near the Quest Team
Test: Watch a Quest Team wizard walk. It should face where it is going.

Fixed: Quest Team names show right in game
The name plates over the Quest Team wizards showed pet names (like "Baby Millie" or "Sparky"), because the game didn't know they were human wizards. Their real names now show, and wizards made before this update are fixed when the team starts.
Where: in game, above each Quest Team wizard
Test: Turn the Quest Team on and go to them. The names over their heads should match the names on the Quest Team page.

Changed: Quest Team only plays the main story worlds
The team only does quests in the main story worlds (Wizard City, Krokotopia, Marleybone, MooShu, Dragonspyre, Celestia and on). It skips Grizzleheim, Wintertusk, Wysteria, Aquila and Darkmoor.
Where: Quest Team > Quest Team
Test: Let the team play. "Doing now" and Team log should never send them to Grizzleheim, Wintertusk or Wysteria.

New: Quest Team keeps its fixed formation
The team always stands Ice 1st, Life 2nd, Death 3rd and Storm 4th in battle. If anyone else (a player or a bot) takes one of their places, or one of them ends up out of order, all of them flee, gather by Ice, and go back to the fight together in order. If fleeing put them out of a dungeon, they use Dungeon Recall to go back in, Ice first.
Where: Quest Team > Team Logs ("leaves the battle to keep its formation", "regroups")
Test: Walk into a Quest Team battle with your own wizard. They should all flee, gather, and start the fight again without you.

Fixed: Quest Team blades the right wizards
Teammates now only blade the team's hitters, Storm first and then Death. They used to blade whoever was in the first circle.
Where: in a Quest Team battle
Test: Watch a Quest Team fight. Life and Ice should put blades on Storm (or Death), never on Ice.

New: Quest Team issues say what a player would expect
Each issue on Team Issues now says what a player would do and see at that step ("Expected, as a player: ..."), and where each of the four wizards was, what level, and which step of the quest it was on. This lets you check the step yourself as a player.
Where: Quest Team > Team Issues
Test: When an issue shows, open it. Under the problem you should see the "Expected, as a player" line and one line per wizard.

New: The Quest Team stays on the same quest together
All four always work on the same quest and the same step. If one of them is on a different step (or finished the quest ahead of the others) for more than two minutes, an issue names that wizard, the quest, and where it is versus the others. They also turn down quests that are not the team's story (side quests and quests of other worlds), so nobody gets ahead.
Where: Quest Team > Team Issues, Team Logs ("leaves it (not the team's story)")
Test: Let the team play a while. Their quest and step on the Quest Team page should always match.
