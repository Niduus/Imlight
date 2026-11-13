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

Fixed: A Quest Team wizard passed round after round with lots of pips
To get more of the creatures it needs into a fight, the team held its hits for up to four rounds whenever such a creature was anywhere close, even one only walking by. So a wizard hit once and then passed again and again while its pips piled up. Now it holds for two rounds at most, only while a creature is really close, and while it waits it plays a blade or a trap instead of passing.
Where: in battle; Team Logs ("... while more creatures walk in")
Test: Watch Ice fight Lost Souls on Unicorn Way. It should hit every round from round 3 on, and play blades or traps (not passes) in the first rounds.

Fixed: The Quest Team was never in a group
A game asks for its friends list right after it logs in, and only then does the server count that wizard for groups. The team's wizards never asked, so their group invites went nowhere, and going to a teammate always failed with "Your friend is busy right now. They are not in your group anymore." Now each wizard asks like a game does, so the four group up and can go to each other.
Where: in the game (the four show as a group); Team Logs
Test: Press Restart. After they log in, the four should be in one group, and "not in your group anymore" should no longer show in Team Logs.

New: Team Wizards page
Pick Ice, Life, Death or Storm to see that wizard's stats (health, mana, damage, accuracy, pierce, critical, block, power pip chance, healing, resists and more), the gear it wears, the cards in its deck right now, its trained spells, and the quest and step it is on. Updated every 3 seconds. Ultimus uses the same view.
Where: Quest Team > Team Wizards
Test: With the Quest Team playing, open Team Wizards and click each of the four buttons.

New: A shout-out for the bot idea
"Shout out to Discord user gese11 for sharing the bot concept" is now on the Quest Team page and in Credits.
Where: Quest Team > Quest Team; Credits
Test: Look at the Quest Team page text and the Credits page.

New: Critical Quest Team issues pop up in Ahziael
When the Quest Team hits a critical issue (red: the story is stopped, like "No progress for 8 minutes"), a window pops up over Ahziael with the issue, an OK button, and "Open Team Issues". The banner at the top still shows too.
Where: over Ahziael
Test: When a red issue shows in Team Logs, the window should pop up. Click OK to close it.

Fixed: The team stood still on "Go to Wizard City, Unicorn Way" in "Finding Out Why"
Those four steps mean opening the four fairy cages on Unicorn Way, but the team only walked to each cage and waited. Steps like this are now clicked, as a player clicks the cage.
Where: Wizard City, Unicorn Way, "Finding Out Why"
Test: Let the team reach "Finding Out Why". Team Logs should show "uses something for ..." at each cage, and the quest should move on.

Changed: The Quest Team leaves Ultimus's creatures alone
When the team looks for a creature to fight, it skips any creature Ultimus is fighting or walking to, so the two never chase the same one.
Where: in the game
Test: With both on, on the same street, the team and Ultimus should fight different creatures.

New: The Quest Team talks in Menu Chat
The four now speak, but only in Menu Chat (the game's own phrase menu, no typed text), and only now and then: at most one line every 40 seconds, and most moments pass in silence. They say things like "Let's go" when they head for the next step, "Ready" before a fight, "Good job" after a win, and "Wait for me" when someone has to rest. When a player near them uses Menu Chat, they may answer: a hello back, "you're welcome", or a goodbye. They turn down anything a player asks ("No thanks") and never let it change what they do; they take orders only from each other. Every line they say, and every player line they hear, is in Team Logs.
Where: in the game, over their heads and in chat; Quest Team > Team Logs ("says in Menu Chat")
Test: Watch the team for a few minutes; a line should show now and then. Stand next to them and use Menu Chat to say Hello, then ask them to follow you. They should answer hello, then say no and carry on.

Fixed: The team stood still for ever by the Teleporter Hub sign
At a street teleporter the leader waited for all four to be close to the stone, but the others stood in a line behind it, and the last one was always too far away, so nobody moved again. Now all four walk up to the stone before they use it.
Where: any street teleporter (for example Wizard City, by the "To Teleporter Hub" sign)
Test: Let the team reach a street teleporter. All four should walk up to it and use it together.

New: The Quest Team and Ultimus free themselves when they get stuck
When a wizard should be getting somewhere but has stood in one place for over a minute (not in a battle, a dialog, a rest or a pause), it steps aside and finds its way again. The second time, a teammate goes straight to the leader, and the leader goes further off and tries every click again. The third time, it is written up as a pink (ABNORMAL) issue with where it was and what it was doing. Every try is in the log in pink.
Where: in the game; Quest Team > Team Logs and Ultimus's log (pink lines "has stood still ... steps aside")
Test: Watch the team for a while. If anyone stands still for over a minute for no reason, a pink line should show and they should start moving again.

Changed: A short pause after every area change
After a door, a teleporter or a Spiral Door, the Quest Team and Ultimus now stand for about 2.5 seconds while the server reads the new area's walls, ground and paths, before taking a step, as a player waits out the loading screen. A walk planned in the old area is no longer walked in the new one.
Where: in the game, after every area change
Test: Follow the team through a door. They should stand a moment after they arrive, then walk on the paths.

Changed: Quest Team and Ultimus alerts on the game are now the "Bot Alerts" add-on
The alert on the game now stays inside the window, with its words wrapped so you can read all of it, at the top in the middle, with an Acknowledge button that takes it away. It stays until you click Acknowledge. It is now an add-on for both the Quest Team and Ultimus, so it only shows on a game if you add it. Without it, alerts still show in Ahziael (the pop-up and the Issues pages). You can change its colours, size and how far down it sits, like any text.
Where: Play > Overlay > Add-ons > Niduus's add-ons > Bot Alerts > Download, then add it to your overlay
Test: Add Bot Alerts to your overlay. When the next Quest Team or Ultimus alert comes, it should show at the top in the middle, fully inside the window, and go away when you click Acknowledge.

New: Quest Team issues say why a step can't be done
Every Quest Team issue about a quest now runs the same quest check Ultimus uses. It looks at the quest's data, the wizard, and whether the NPC, object or creature is really there and clickable. The issue then says the most likely cause, how sure it is (High, Medium or Low), and whether the step can be done at all. Every check it made is listed under what the team saw. A cause it is sure of also shows in pink in Team Logs.
Where: Quest Team > Team Issues; Team Logs ("DIAGNOSIS: ...")
Test: When the next quest issue comes up, open it in Team Issues. It should start with "Likely cause" and "Can the step be done", followed by the "Check OK/PROBLEM/INFO" lines.

Fixed: The Quest Team and Ultimus walked slowly and choppily
They walked at about two thirds of a player's running pace, and sent where they were only four times a second, so on your screen they looked slow and jumpy. They now run at a player's pace (400, the RunSpeed setting under [Bots], changes live), send where they are ten times a second, and take corners without stopping on them.
Where: in the game, wherever they walk
Test: Run next to the team or Ultimus. They should keep up with you and glide smoothly, not hop.

Fixed: The Quest Team and Ultimus walked through walls
Their walks already went around walls, but three things let them pass through one. When no way around was found, they walked straight to the spot. A straight stretch could slip between two wall corners that touch. And a spot inside a wall was walked into. Now, when there is no way all the way, they walk as far as the walls let them (a door or exit just past its wall still counts). A straight stretch never slips between touching corners. And a spot deep in a wall ends on the nearest floor. If they then can't go on, the stuck check frees them.
Where: in the game, wherever they walk
Test: Watch them walk past buildings, fences and walls. They should go around them, not through them.

Changed: Item and Treasure cards on the Team Wizards and Ultimus pages
Under Deck, the page now lists the deck's spells, then "Item Cards" (the cards their gear gives, without the ones crossed out for that deck), then "Treasure Cards", each with its own count, as the game's deck window sorts them.
Where: Ahziael > Quest Team > Team Wizards; Ahziael > Ultimus (its wizard page)
Test: Open a wizard's page. Under Deck you should see Item Cards and Treasure Cards headings with their cards.

Changed: Collecting quest objects one after another, and around creatures
When a step asks the Quest Team or Ultimus to use or collect several things (like the Bone Cages), each wizard now remembers what it has already used for that step. After using one, it goes on to the next one it hasn't done, and it never goes back to one it already used. Spots with no creatures nearby come first. On the way, its path bends around creatures, so it stays out of battles as much as it can. Ultimus also now clicks "use" steps that the quest writes as a "go to" (like the fairy cages), the same way the Quest Team does since 0.10.828.
Where: in the game, on steps that ask to use or collect several things; Team Logs ("already used what is here ... on to the next one")
Test: Let them reach a collect step like the Bone Cages. Each should use one, walk on to the next (around the creatures), and not come back to the ones they did.

Fixed: A Quest Team issue on a door or exit could not skip the broken step
Some issues come from walking a way out of an area, which has no quest of its own. Those issues could not skip the step, so the team could loop. They now use the quest the team is on, and the step gets skipped as with any other quest issue.
Where: Quest Team > Team Issues
Test: Nothing to do by hand. If the team loops at a door, the issue should say "Ahziael finished the step for them".

Fixed: The Quest Team and Ultimus still walked through some walls
The server's walking map for them is made from the area's collision file, and it was missing many solid things. A wall standing straight up has no width when seen from above, so it was never marked. A box turned on its side lost its length. Round posts, trees and pillars were left out, and so were the sides of solid shapes like buildings. All of these are now marked as walls on their walking map. Small steps like stairs and kerbs still aren't walls. The Quest Tracker's line on the minimap uses the same map, so it now goes round these too.
Where: in the game, wherever they walk
Test: Watch them walk past buildings, fences, posts and trees. They should go around them.

Fixed: The Badges page was empty (one group showing 0/0)
The Ahmarra game update changed how the game reads the badge list. It now expects only each badge's id and progress, and it takes the names and pictures from its own files. The server still sent the names too, so the game read a name as the next badge and threw the whole list away. The list now goes out the way the new game reads it. Older games still get everything.
Where: in the game, Character > Badges
Test: Log in on the wizard you ran .givemeallbadges on and open Badges. You should see all the groups with their counts, and the badges inside them.

New: The deck on the Quest Team and Ultimus wizard pages looks like the game's deck window
The deck now shows as card pictures in slots, 8 to a row, the way the game's deck window shows it. Empty slots fill up to what the deck holds. The item cards from gear sit in their own rows underneath, and the treasure cards are below them with how many it has. Point at any card to see it big.
Where: Ahziael > Quest Team > The team's wizards, and the Ultimus page
Test: Open a wizard there. Its deck should look like the in-game deck window.

New: The Quest Team and Ultimus turn off item cards they never play
Like a player clicking item cards in the deck window, they now turn off the item cards from their gear that they would never cast. That means enchantments, pick cards, cards from developers' gear, and (for the hitter) plain heals and shields. This keeps those cards out of their hand. If a card becomes useful to them again, they turn it back on. Ahziael shows the turned-off ones with a red X as soon as they change, and the team log says which cards and why.
Where: Ahziael wizard pages (item card rows); Team Logs ("turns item cards off: ...")
Test: Give a team wizard gear with an enchantment item card (or give the Storm hitter gear with a heal item card). Within a few seconds the card should show a red X on its page, and in the game's deck window if you log in as that wizard.

New: Game icons for gear and stats on the Quest Team and Ultimus wizard pages
Gear now shows one line per slot (hat, robe, boots, wand, athame, amulet, ring, pet, mount, deck). Each line has the slot's round icon from the game's backpack tabs, with the item's name and level next to it. Stats now look like the game's stat tooltip: yellow numbers on a dark blue panel, with the game's icons for health, mana, power pip chance, damage, accuracy, pierce, critical, block, healing, pip conversion, shadow pips, archmastery, stun resist and each school's resist. The pictures are read from your game files and nothing in them is changed. Until they're read, or if one isn't found, a drawn icon shows in its place.
Where: Ahziael > Quest Team > The team's wizards, and the Ultimus page
Test: Open a wizard there. Gear should show the slot icons with the item names, and stats should show the game's icons beside the numbers.

Changed: Wizard pages show only the stats a wizard has
The Stats panel on the Quest Team and Ultimus pages now leaves out stats at 0 (like 0 critical rating or 0% Fire resist). Health and mana always show.
Where: Ahziael > Quest Team > The team's wizards, and the Ultimus page
Test: Open a wizard. Every stat line should be 1 or more, apart from health and mana.

Fixed: Gear slot icons showed four small icons instead of one
The game keeps each backpack tab's four looks (normal, pointed at, pressed, greyed) in one picture, and the wizard pages showed all four. Now only the top-left one shows, for every slot.
Where: Ahziael > Quest Team > The team's wizards, and the Ultimus page (Gear)
Test: Each gear line should have one round slot icon, then the item's name.

Changed: The Quest Team and Ultimus always show the Essential badge
Their title under their names is now always the Essential badge, instead of their newest story badge. They're given the badge if they don't have it, and they pick it the way a player does. If anything changes their title, they pick Essential again within about a minute.
Where: in the game, under their names
Test: Look at a Quest Team wizard or Ultimus in the game. Their title should be the Essential badge's.

Changed: Health, mana and energy use the game's globes
The Stats panel (and other stat icons in Ahziael) now shows health as the game's red globe and mana as the blue globe. Energy has its own line with the green globe.
Where: Ahziael > Quest Team > The team's wizards, and the Ultimus page (Stats)
Test: Open a wizard. Health should show a red globe, mana a blue globe, and a new Energy line a green globe.

Changed: The Quest Team and Ultimus only play the Story Main line
Some main quests can't be turned down. The bots now take those, but ignore them unless a Story Main quest needs them done first. They still turn down side quests, unless the story needs one.
Where: in the game, and the Quest Team and Ultimus logs
Test: When a bot is offered a main quest that isn't part of the story, the log should say "accepts it but ignores it". The team should keep following the Story Main line.
