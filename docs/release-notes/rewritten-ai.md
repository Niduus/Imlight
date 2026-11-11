New: Rewritten AI (off unless you turn it on)
A new switch makes creatures fight with reason. They go after the most threatening wizard: big blade stacks and lots of pips count the most, then the damage a wizard did, healing, blades they just used up (all of it if that hit defeated a creature), low health, and school matchup (a fire creature wants the ice wizard). They only cast cards that make sense: no shield of a school nobody in the fight uses, no blade of the wrong school, and they pass instead of casting nonsense. They finish wizards off, heal and shield when in danger, weaken a wizard with a big blade stack, set up blades and traps that match the hit they plan, and hit around shields. Creatures on the same side plan together: they see each other's setup, don't waste hits on a wizard a teammate is already finishing, feed their leader, and notice when several wizards blade one wizard. A pet's May Cast or Will Cast counts for its owner. Off: creatures fight exactly as before.
Where: Game Rules > Battles and World
Test: Turn on Rewritten AI, fight a group of creatures with a friend, and both blade the same wizard. The creatures should go after that wizard. The server log explains every choice in lines that start with "Rewritten AI".

New: Fine-tune the Rewritten AI
Sliders under the switch set how much blades and pips, damage, healing, low health and school matchup count, how fast threat fades, how stubborn creatures are about their target, how much threat used-up blades leave, the gang-up chance, the most setup rounds and what counts as a big enough hit. They apply from the next round.
Where: Game Rules > Battles and World
Test: Open "Rewritten AI: fine-tuning", set "How much school matchup counts" to 300%, and fight a fire creature with an ice and a fire wizard: it should go for the ice wizard.

New: Give each creature its own Rewritten AI
Every creature and boss now has a Rewritten AI part in its stats: turn it on or off just for that creature, pick its part in the team (Leader, Protector, Healer, Striker, Support or Free), give it its own numbers, tick what it never does (shield or heal teammates, traps, setting up, ganging up, holding back), and list cards it never casts.
Where: Database > Bosses
Test: Find a boss that fights with other creatures, set Rewritten AI to Always on and its part to Leader, save, and fight it: its teammates should blade it.
