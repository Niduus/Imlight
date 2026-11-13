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

Changed: The Quest Team battles like a person
Each Quest Team wizard now plays its battle turn through the same clicks a game sends: it takes a moment to read its hand, discards one card at a time, then picks its card and target (or passes or flees). A wizard on a lower circle can still pick before them. The same click path is ready for the street bots.
Where: in a Quest Team battle; Quest Team > Team Logs
Test: Watch a Quest Team battle. The cards should go in a few seconds apart, not all at once.

New: The Quest Team waits for more creatures to join
In the first rounds, while there is room on the creatures' side and more of the creatures they need are close by, the team holds its hits (blades, shields and heals still go) so those creatures can walk into the battle. Once their side is full, or nothing more is coming, they attack.
Where: in a Quest Team battle; Team Logs ("waits for more creatures to join before hitting")
Test: Let the team start a fight near several of the same creature. They should hold their hits for a round or two while the others join.

New: Pause and Stop buttons for the Quest Team
"Pause or resume" makes the four stand still where they are without logging out, so you can walk over and look. A battle they are in still finishes. "Stop and save" logs them out cleanly at the step they are on, and turning the Quest Team on again picks up from there.
Where: Quest Team > Quest Team
Test: Click "Pause or resume" while they walk. They should stop, and Team log should say "is paused". Click it again and they go on.

Changed: Quest Team plays steadier and reports more
- They never walk into a fight weak. Anyone low on health or mana drinks a potion, or they rest for up to a minute first.
- Losing the same fight twice is now an issue (it was three). Note: if your Quest Team settings already say 3, change "Lost fights before it is an issue" to 2.
- New setting "Time limit for one quest (minutes)", 45 by default. A quest that takes longer than that is reported.
- A broken step no longer stops the run. After the tries that raise the issue (or one more lost fight than the issue limit), Ahziael finishes that step for them, the issue says so, and the quest log marks it SKIPPED.
- Each bug is reported once, not again every half hour.
- If they run short of gold, Ahziael gives them some, and Team log says how much.
- They ride their school's Ghulture and walk faster on it.
- The quest log bookmarks each world they finish with the date and time ("WORLD DONE").
- The quest log notes how many battles each fighting step took, and a step that takes more than 12 is reported as a possibly broken drop.
Where: Quest Team > Quest Team, Team Issues, Team Logs
Test: Let the team play for a while, then check Team Logs for "drinks a potion before the fight", "rides"/"puts on its Ghulture" and the battle counts in the quest log.

New: Colored Team log
Each line in Team log now has a color, and a word at the start that says the same thing, so a copied log keeps it:
- White: normal.
- Yellow (MINOR): something is off but the quest still goes on, like a lost fight or a step that took too many battles.
- Orange (NOTABLE): something that could stop a quest from finishing, like a dialog that never ends or a wizard on a different step.
- Red (CRITICAL): the story is stopped or was stopped, like a broken step or no quest left to get. Fix these first.
- Cyan (COMMAND): something Ahziael did for them, with the reason: XP to catch up a level, gold, a Ghulture, a skipped step, a save.
Where: Quest Team > Team Logs (the colors are listed above the log)
Test: Let the team play a while and open Team Logs. Normal lines are white; any "COMMAND:" line is cyan.

New: The Quest Team keeps going on its own
- Under-leveled: when the next story quest needs a higher level, Ahziael gives each wizard below it just enough XP to reach it. Team log says how much XP and which level (cyan).
- They never join a battle that already has another player or street bot in it. They walk past and pick another creature.
- Before each new world, Ahziael saves the team's accounts and wizards to logs\quest-team\Snapshots. To undo a broken world, load the file in Account Transfer.
- The four are friends with each other. One left alone in a dungeon tries to go to each teammate. If they are all busy in their own copy of the same place, it treats it as a solo dungeon and makes its deck ready to fight alone.
- When the backpack is nearly full, they sell gear they will never wear, and Team log lists what went and for how much.
- Training follows the team role: each also learns a heal and a shield of its own so it can fight alone. They now train at any trainer in the area they are in, not only in Ravenwood, while they have the training points and level for it.
- They use the game's street teleporters only when one brings them much closer to a story quest goal. All four wait at the stone and go together. They do no other teleporting.
- They wear their newest story badge as their title.
- After the last world the server's data has, they stop (paused). Team log says "MAIN STORY COMPLETE".
- After a server restart or crash they pick up the same step, so a battle that was cut off is fought again.
- Their gear is checked all the time, so it is also checked after every quest and in each new world.
Where: Quest Team > Quest Team, Team Logs, logs\quest-team\Snapshots
Test: Watch Team log for "gains ... XP", "SNAPSHOT before", "sells", "uses the street teleporter", and "wears its newest story badge".

New: Daily summary and alerts over the game
- The Quest Team page shows "Today so far": quests finished, battles won and lost, issues, steps skipped, XP and gold given, things sold. At midnight the day's summary goes into Team log and the quest log.
- A new Quest Team issue also shows for two minutes over the games on this PC, in the overlay.
- A dialog that keeps going for over a minute without ending is reported (orange).
Where: Quest Team > Quest Team; the game overlay; Team Issues
Test: Look at "Today so far" on the Quest Team page. With the overlay on, a new team issue should show over your game.

Fixed: Restart in the Quest Team page failed
Pressing "Restart with four new wizards" could end with "The Quest Team's wizards could not be made or loaded". The database still listed the old accounts for a moment after they were deleted, so the new ones were never made. Now the team waits until the old accounts are really gone (and the new ones are saved) before going on.
Where: Quest Team > Quest Team > Restart with four new wizards
Test: Press Restart. Within about half a minute Team log should say "Restart: four new level 1 wizards were made", and the four should log in.

Fixed: Team log said "next: Defeat ..." every second
A creature that walks about made the team announce the same step again and again. Now each step is said once.
Where: Quest Team > Team Logs
Test: Watch a fighting step. "next: Defeat ..." should show once.

New: A way that loops is reported
If the leader goes in and out of areas 8 times in 90 seconds without reaching the step (like walking between two areas with the same name), it is reported as an issue and the step is skipped so the run goes on.
Where: Quest Team > Team Issues
Test: Nothing to set up; if it happens, Team Issues shows "goes round in a loop".

New: OK button on Quest Team alerts over the game
The Quest Team alert that shows over your game now has an OK button under it. Click it to take the alert away.
Where: over the game, in the overlay
Test: When a Quest Team alert shows over the game, click OK. It should go away.

Fixed: Three of the wizards stayed with Merle at the start
When Ice took Merle Ambrose's first quest, the game sent Ice on to Unicorn Way, but the other three never talked to Merle, so they stood where Ice had been. Now each teammate talks to the same person the leader talked to (and takes the same quest), then follows the leader the same way.
Where: Quest Team > Team Logs
Test: Press Restart. All four should talk to Merle and all four should end up on Unicorn Way together.

Fixed: No title under the Quest Team wizards' names
The team looked for its newest story badge with the world names written with spaces ("Wizard City"), but the badge list writes some of them without ("WizardCity"), so none was found. Both spellings count now. A new wizard with no story badge yet wears the newest badge it did earn, so a title shows as soon as it has one.
Where: in the game, under each Quest Team wizard's name; Team Logs ("wears its newest story badge")
Test: Let the team win a few fights. A title should show under their names.

Fixed: The Badges page was empty for a wizard with many badges
A wizard with hundreds of badges (like after .givemeallbadges) got them sent in cut pieces, and the game showed an empty Badges page. Now the list goes out as several whole lists, so every badge shows.
Where: in the game, Character > Badges
Test: Log in on the wizard you ran .givemeallbadges on and open Badges. They should all be there.

Changed: The Quest Team no longer stands in the road between fights
When the team rests after a battle, or has nothing to walk to for a moment, the leader now walks to the closest path spot well away from the creatures, and the others follow. When the next creature it needs is ready, it goes straight for it.
Where: in the game; Team Logs
Test: Watch the team after a battle on Unicorn Way. They should step off to the side, away from the creatures, and not stand among them.

New: Pink lines for abnormal things the team notices
Team Logs has a sixth color. Pink means the team noticed something abnormal, not always a broken quest: a step that took far too many battles to drop what it needs, or a battle that has gone on for over 10 minutes.
Where: Quest Team > Team Logs (the color key lists Pink)
Test: Look at the color key in Team Logs. Pink lines show "ABNORMAL:".
