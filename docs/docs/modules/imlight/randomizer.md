# Randomizer Mode

Randomizer mode shuffles the game so every playthrough is different. It is off by default and is switched on in the `[Randomizer]` section of `Imlight.ini`.

```ini
[Randomizer]
Enabled = true
```

## What gets shuffled

| Setting | Default | What it does |
| ------- | ------- | ------------ |
| `RandomizeSchool` | `true` | New characters get a random school instead of the one they picked. |
| `RandomizeSpells` | `true` | Spells learned from trainers, quests and drops are swapped. Trainers show the swapped spell, including its prerequisite. |
| `RandomizeDrops` | `true` | Gear and treasure cards from drops and quest rewards are swapped. Quest items, keys and housing are never swapped. |
| `RandomizeCreatures` | `false` | Creatures in the world are swapped for others of a similar level. |
| `CreaturesChangeEverySpawn` | `true` | With `RandomizeCreatures` on, every spawn rolls a new creature. If false, each creature always swaps to the same one, for every player. |

## Battles

| Setting | Default | What it does |
| ------- | ------- | ------------ |
| `RandomizeCreatureDecks` | `true` | Each creature's deck is rolled fresh every spawn: every spell becomes a random spell of the same type and pip cost, from any school. |
| `RandomizeItemCards` | `true` | Cards your gear gives are rolled fresh every battle, the same way. |
| `RandomizePips` | `true` | Each round gives a random number of pips: 1 about half the time, otherwise 0, 2 or (rarely) 3, but never 0 when you have no pips. Creatures too. |
| `StartingSpells` | `6` | New characters start with this many random spells costing up to 3 pips, from any school, already in the starter deck. The tutorial still teaches its spell too. |
| `SpellAfterBattleChance` | `100` | The chance (0 to 100) that a win teaches a random spell. Any battle spell in the game files can come up, including ones players normally never get. |

These rolls are truly random and differ every time. They use every battle spell in the game files (treasure cards, gardening, fishing and cantrips are left out), not just trainer spells.

:::warning
With `RandomizeCreatures` on, a swapped creature still counts for quests as the creature it replaced: if a quest asks for Rotting Fodder and a Fire Elf stands where the Fodder would be, defeating that Fire Elf counts. Creatures that a quest spawns on its own (some bosses and dungeon fights) are not swapped.

`Tame` (default `false`) is the questing mode: with it on, creatures, creature decks, pips and shadow pips all stay normal whatever their own switches say, so every quest can be finished and fights are as hard as usual. Schools, spells, drops, starting spells and the spell you win after a battle still shuffle. In ImlightManager it is the "Randomizer: tame mode" switch.
:::

## How it stays balanced

Things are only ever swapped for something in the same group:

- **Spells** keep their type (damage, healing, charm, ward, ...) and pip cost. Only spells some trainer teaches are used, so every swap is a spell players can normally learn.
- **Gear** keeps its slot, its school and its level range.
- **Treasure cards** keep their type and pip cost.
- **Creatures** keep their level range, and bosses only swap with bosses.

Within a group the swap is a shuffle, so two different spells never turn into the same spell. For a given character the same spell or drop always becomes the same thing. The battle rolls above are the exception: they are random every time.

## Seeds

| Setting | Default | What it does |
| ------- | ------- | ------------ |
| `Seed` | `imlight` | Any text. Changing it reshuffles everything for everyone, so set it before characters are made. |
| `SameForEveryone` | `false` | If true, every character gets the same shuffle, which suits races. If false, each character's shuffle is their own. |
| `LevelRange` | `10` | How many levels wide each gear and creature level range is. |

The admin commands `.sb learn` and `.mod additem` are never shuffled; they always give exactly the ID you type. Use `.lookup` to find IDs.

## Spell trainers and training points

The `[Trainers]` section of `Imlight.ini` works with or without randomizer mode. The app has a switch for each one on the Server tab.

| Setting | Default | What it does |
| ------- | ------- | ------------ |
| `RandomizeSpellRequirements` | `false` | Each spell that needs another spell first needs a different, random one instead, picked from the same trainer's lower level spells, so every spell can still be reached. |
| `NoSpellRequirements` | `false` | No spell needs another spell first. Overrides `RandomizeSpellRequirements`. |
| `NoLevelRequirements` | `false` | Any level can train any spell. |
| `FreeTraining` | `false` | Training costs no training points, even for other schools' spells. |
| `TrainingPointPerLevel` | `false` | Every level gained also gives training points. |
| `TrainingPointsPerLevel` | `1` | How many training points each level gives while `TrainingPointPerLevel` is on (0 to 100). |
| `PlayersCanGainTrainingPoints` | `true` | Any player can type `.gaintp <amount>` (or `.trainingpoints <amount>`) to give themselves 1 to 100 training points. |
| `RespecAtTrainers` | `true` | The game's spell trainers offer to reset training points, for free. Every spell bought with training points leaves the spellbook and decks and the points come back; spells of the wizard's own school, free spells and quest spells stay. `.respec` shows what a reset would do and `.respec yes` does it, for any player. |

## Battle

The `[Battle]` section works with or without randomizer mode, with switches on the app's Server tab.

| Setting | Default | What it does |
| ------- | ------- | ------------ |
| `ManaOnFizzle` | `false` | A spell that fizzles still costs its mana, the same as if it had worked. |
| `UnlimitedMana` | `false` | Spells never use up mana, and arriving in the Commons fills mana back to full. Otherwise spells cost mana equal to their pips and need that much to cast. |
| `FullPipConserve` | `false` | A power pip only half used on a spell (a 3 pip spell paid with 2 power pips) always gives the spare pip back as a normal pip, like 100% pip conserve. |
| `SchoolPips` | `true` | Once every pip slot is full, each round turns one power pip into a school pip of the wizard's own school. School pips pay for the school pip cost some higher level spells have. |

## World

| Setting | Default | What it does |
| ------- | ------- | ------------ |
| `OpenAllDoors` | `false` | Every door and teleport works without the quest, level or event it normally needs, such as the Nightside door in the Commons. |

More school pip settings, also in `[Battle]`:

| Setting | Default | What it does |
| ------- | ------- | ------------ |
| `GainSchoolPips` | `false` | A wizard's school pip gauge fills each round; once full, their next pip is a school pip. No stats needed. |
| `SchoolPipChance` | `25` | How much the school pip gauge fills each round (0 to 100; 25 is a school pip every 4 rounds). |
| `RandomSchoolPips` | `false` | School pips come in a random school instead of the wizard's own. |

Shadow pips, also in `[Battle]` (`NaturalShadowPips` gives wizards shadow pips without the shadow pip rating stat, the gauge fills 25 each round, or `ShadowPipChance` if set):

| Setting | Default | What it does |
| ------- | ------- | ------------ |
| `ShadowPips` | `true` | Wizards and creatures can get shadow pips, which shadow spells cost. They have their own slots, and wizards get shadow magic unlocked so the slots and shadow gauge show. |
| `PlayerShadowPipLimit` | `0` | The most shadow pips a wizard can hold. 0 means no limit. |
| `PlayerShadowPipMin` | `0` | The fewest shadow pips a wizard holds; they top back up to this each round. |
| `CreatureShadowPipLimit` | `0` | The most shadow pips a creature can hold. 0 means no limit. |
| `CreatureShadowPips` | `true` | Creatures can get shadow pips at all. |
| `RandomCreatureShadowPips` | `false` | Each creature gets shadow magic only by chance, rolled every battle. |
| `CreatureShadowMagicChance` | `50` | That chance, from 0 to 100. |
| `ShadowPipChance` | `0` | How much the shadow gauge fills each round (0 to 100); a full gauge gives a shadow pip. 0 uses each one's own shadow pip rating from level and gear. |

## Enchantments and test shops

Enchantment cards (Epic, Sharpen Blade, Potent Trap and the rest) can be played onto another card in your hand in battle. The enchanted card keeps the change until it is cast. For testing, Abner K. Doodle in the Commons sells every enchantment treasure card, each for 1 to 200 gold, and they never run out. In Ravenwood, Mr. Lincoln teaches every enchantment spell for free, Monstrologist Burke every Extract spell (Extract Undead and the rest), Mellori the Shadow spells (Shadow Shrike, Sentinel, Seraph and Trickster, Dark Fiend, Dark Nova, Dark Shepherd, Shadow Shield, Shadow Trap, Shadowblade, Shadowspear, Justiceblade and every other Shadow spell that is not a monster's), and Merle Ambrose the spells only monsters and bosses cast. Each card name shows once, and no two of these trainers teach the same card. A trainer shows a section of up to 50 spells you don't know yet (about six pages); walk away and come back to see the next section. Daybreaker and Nightbringer delay the enchanted spell to your next turn (its start or its end); you pay for it when you play it.

Won battles (not fled ones) give gold from every monster and drop a health, a mana and a gold wisp where the battle was (`[Battle] BattleWisps`); wisps nobody picks up vanish after 2 minutes. `.intothedarkness` gives your character shadow magic of its own, even with `[Battle] ShadowPips` off. `[Randomizer] RandomizeShadowPips` rolls each wizard's shadow pip limit (1 to 5) from the seed and gives creatures shadow magic by chance. `[Battle] NoPipsFromDaybreakerNightbringer` keeps those enchantments' damage but drops their extra pip. Every wizard has at least `[Character] StartingPotions` potion bottles (default 1), which potion vendors refill. `.givemeallbadges` gives every badge, which quests and doors that need a badge then count. With `[Battle] GainSchoolPips` on, the school pip gauge fills toward the school picked in your deck, or your own school if none is picked.

The Commons button by the compass always takes you to the Commons. `.givemetpstyle` unlocks every teleport effect (log out and back in to see them).
